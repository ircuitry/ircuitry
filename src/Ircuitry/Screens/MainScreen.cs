using System;
using System.Collections.Generic;
using System.Linq;
using FontStashSharp;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Ircuitry.App;
using Ircuitry.Core;
using Ircuitry.Editor;
using Ircuitry.Graph;
using Ircuitry.Gui;
using Ircuitry.Input;
using Ircuitry.Irc;
using Ircuitry.Render;
using Ircuitry.Runtime;

namespace Ircuitry.Screens;

/// <summary>Root screen - composes the command-console dock around the live graph editor.</summary>
public sealed partial class MainScreen : IScreen
{
    private readonly AppModel _app;
    private readonly GraphEditor _editor;
    private readonly Ui _ui = new();
    private int _testRunSeq;   // bumped each RunTest() - the tutorial watches it

    private Layout _l;
    private int _vw = 1600, _vh = 952;
    private InputState _input = null!;

    // palette drag-to-spawn
    private NodeDef? _dragDef;
    private Vector2 _dragStart;
    private bool _dragging;
    private float _paletteScroll;
    private string _paletteSearch = "";
    private NodeCategory? _openCat;   // palette accordion: at most one category expanded (null = all collapsed)

    // import modal + graph-change tracking
    private bool _importOpen;
    private bool _importJustOpened;     // suppress the opening click from closing the modal
    private string[] _importFiles = Array.Empty<string>();
    private NodeGraph? _lastGraph;

    // delete-bot confirmation
    private Bot? _confirmDeleteBot;
    private bool _confirmJustOpened;

    // close prompt (window X → exit / minimise)
    private bool _closePromptOpen, _closeJustOpened;
    public Action? OnExitRequested;
    public Action? OnMinimizeRequested;
    public void RequestClosePrompt() { _closePromptOpen = true; _closeJustOpened = true; }

    // inline tab rename (double-click a tab)
    private Bot? _renamingBot;
    private float _tabClickTime;
    private Bot? _tabClickBot;

    // run-history viewer
    private bool _historyOpen, _historyJustOpened;
    private List<RunRecord> _historyRuns = new();
    private int _historySel = -1;
    private float _historyListScroll, _historyDetailScroll;

    // secrets vault editor
    private bool _secretsOpen, _secretsJustOpened;
    private string _secretName = "", _secretValue = "";

    // "Obby" - advanced bot-tools controls, collapsed by default in the inspector
    private bool _obbyConn, _obbyNode;

    // test bench (dry-run without IRC)
    private bool _testOpen, _testJustOpened;
    private string _testMsg = "!ping", _testNick = "alice", _testChan = "#test";
    private readonly List<(string kind, string text)> _testSent = new();
    private RunRecord? _testRec;
    private float _testScroll;

    // save-selection-as-reusable-node
    private bool _saveNodeOpen, _saveNodeJustOpened;
    private string _saveNodeName = "My Node";

    // confirm installing a dropped community .ircnode (it runs code) before it's installed
    private bool _installOpen, _installJustOpened;
    private string _installPath = "", _installPreview = "";
    private string? _installText;   // set when installing from clipboard (write text) instead of a dropped file (copy)
    private Vector2 _installScreen;
    private NodeDef? _installDef;
    private bool _uninstallOpen, _uninstallJustOpened;
    private NodeDef? _uninstallDef;

    // community node manager
    private bool _nodeMgrOpen, _nodeMgrJustOpened;
    private float _nodeMgrScroll;
    private readonly HashSet<string> _nodeMgrSel = new();
    private volatile bool _nodeMgrChecking;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _nodeMgrUpdates = new();   // typeId -> changelog note
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _nodeMgrInLibrary = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _nodeMgrLatest = new();    // typeId -> latest manifest json

    // new-bot template picker
    private bool _templateOpen, _templateJustOpened;

    // quick-add (double-click canvas)
    private bool _quickOpen, _quickJustOpened;
    private Vector2 _quickWorld, _quickScreen;
    private string _quickSearch = "";
    private float _quickScroll;
    private float _lastClickTime;
    private Vector2 _lastClickPos;

    private bool Modal => _importOpen || _confirmDeleteBot != null || _historyOpen || _quickOpen || _templateOpen || _closePromptOpen || _secretsOpen || _testOpen || _ctxOpen || _saveNodeOpen || _installOpen || _uninstallOpen || _nodeMgrOpen;

    public MainScreen(AppModel app)
    {
        _app = app;
        _editor = new GraphEditor(_app.ActiveBot.Graph)
        {
            FireGlow = id => _app.ActiveBot.Runtime.FireGlow(id),
            FireCount = id => _app.ActiveBot.Runtime.FireCount(id),
        };
    }

    private Bot Bot => _app.ActiveBot;
    private InputState In => _input;

    public bool SuppressAutosave => _ui.AnyFieldFocused;

    /// <summary>Dropping an .ircbot file loads its nodes into the current workflow at the drop point.</summary>
    public void OnIrcbotDrop(Vector2 screen, string path)
    {
        try
        {
            var (g, _) = Ircuitry.Graph.GraphSerializer.Load(System.IO.File.ReadAllText(path));
            if (g.Nodes.Count == 0) { Bot.Log.Add(LogLevel.System, "nothing to load from " + System.IO.Path.GetFileName(path)); return; }
            _editor.InsertGraphAt(g, screen);
            _app.MarkDirty();
            Bot.Log.Add(LogLevel.System, $"loaded {g.Nodes.Count} node(s) from {System.IO.Path.GetFileName(path)}");
        }
        catch (Exception ex) { Bot.Log.Add(LogLevel.Error, "load failed: " + ex.Message); }
    }

    /// <summary>Dropping a .ircnode stages a community node for install behind a confirm dialog (it runs code).</summary>
    public void OnNodeDrop(Vector2 screen, string path)
    {
        try
        {
            var text = System.IO.File.ReadAllText(path);
            var def = Ircuitry.Graph.CustomNode.Load(text);
            if (def == null) { Bot.Log.Add(LogLevel.Error, "not a valid .ircnode: " + System.IO.Path.GetFileName(path)); return; }
            _installPath = path; _installText = null; _installScreen = screen; _installDef = def;
            _installPreview = NodePreview(text);
            _installOpen = true; _installJustOpened = true;
        }
        catch (Exception ex) { Bot.Log.Add(LogLevel.Error, "could not read .ircnode: " + ex.Message); }
    }

    /// <summary>
    /// Install a community node straight from the clipboard (a copied .ircnode manifest), behind the
    /// same confirm dialog as a dropped file. This is how the website's Copy button lands in the app.
    /// </summary>
    public void InstallFromClipboard()
    {
        if (_installOpen) return;
        var text = (Ircuitry.Core.Clipboard.GetText() ?? "").Trim();
        if (text.Length == 0) { Bot.Log.Add(LogLevel.Error, "clipboard is empty - copy a node from ircuitry.github.io/nodes first"); return; }
        StageInstall(text, "clipboard");
    }

    /// <summary>True when a deep-link install can be shown right now (nothing modal is open).</summary>
    public bool CanAcceptDeepLink => !Modal;

    /// <summary>
    /// Handle an <c>ircuitry://</c> link: download the named community node/workflow and stage it for a
    /// one-click confirm. Two clicks total: one on the website, one here.
    /// </summary>
    public void HandleDeepLink(string link)
    {
        if (!Ircuitry.App.DeepLink.TryParse(link, out var action, out var url))
        { Bot.Log.Add(LogLevel.Error, "unrecognised link: " + link); return; }
        if (!Ircuitry.App.DeepLink.IsAllowedUrl(url))
        { Bot.Log.Add(LogLevel.Error, "blocked link (only ircuitry community URLs are allowed): " + url); return; }

        Bot.Log.Add(LogLevel.System, "fetching " + url);
        string text;
        try
        {
            var (status, body) = Ircuitry.Net.Http.Send("GET", url, System.Array.Empty<(string, string)>(), null);
            if (status < 200 || status >= 300) { Bot.Log.Add(LogLevel.Error, $"download failed (HTTP {status})"); return; }
            text = body;
        }
        catch (Exception ex) { Bot.Log.Add(LogLevel.Error, "download failed: " + ex.Message); return; }

        if (action == "install-bot")
        {
            var bot = _app.ImportText(text);
            if (bot != null) Bot.Log.Add(LogLevel.System, $"imported workflow “{bot.Name}” - set your server/nick/channels, then RUN BOT");
            return;
        }
        StageInstall(text, "link");   // default action: install-node
    }

    // Stage a community node (from clipboard or a deep link) in the install confirm dialog.
    private void StageInstall(string text, string source)
    {
        NodeDef? def;
        try { def = Ircuitry.Graph.CustomNode.Load(text); }
        catch (Exception ex) { Bot.Log.Add(LogLevel.Error, source + " is not a valid .ircnode: " + ex.Message); return; }
        if (def == null) { Bot.Log.Add(LogLevel.Error, source + " is not a node manifest (needs a typeId and code or subgraph)"); return; }
        _installText = text; _installPath = "";
        _installScreen = new Vector2((_vw > 0 ? _vw : 1280) / 2f, (_vh > 0 ? _vh : 800) / 2f);
        _installDef = def;
        _installPreview = NodePreview(text);
        _installOpen = true; _installJustOpened = true;
    }

    // A short preview of what a dropped node will run, shown in the install confirm dialog.
    private static string NodePreview(string manifestJson)
    {
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(manifestJson);
            var r = d.RootElement;
            if (r.TryGetProperty("subgraph", out var sg) && sg.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                int n = sg.TryGetProperty("nodes", out var ns) && ns.ValueKind == System.Text.Json.JsonValueKind.Array ? ns.GetArrayLength() : 0;
                return $"[subflow - {n} node(s), no code]";
            }
            if (r.TryGetProperty("code", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var lang = r.TryGetProperty("language", out var l) ? l.GetString() : "python";
                var code = c.GetString() ?? "";
                if (code.Length > 800) code = code[..800] + "\n… (truncated)";
                return $"language: {lang}\n\n{code}";
            }
        }
        catch { }
        return "(no script)";
    }

    /// <summary>Dropping an .ics file/folder sets the source on the calendar node under the cursor, or spawns one there.</summary>
    public void OnCalendarDrop(Vector2 screen, string path)
    {
        if (!(path.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) || System.IO.Directory.Exists(path))) return;
        var node = _editor.NodeAt(screen);
        if (node != null && (node.TypeId is "file.ical" or "cal.search")) { node.SetParam("source", path); _editor.Selection.Clear(); _editor.Selection.Add(node.Id); }
        else if (node != null && node.TypeId == "cal.add") { node.SetParam("path", path); _editor.Selection.Clear(); _editor.Selection.Add(node.Id); }
        else { var n = _editor.Spawn(NodeCatalog.Get("file.ical"), _editor.Cam.ScreenToWorld(screen)); n.SetParam("source", path); }
        _app.MarkDirty();
        Bot.Log.Add(LogLevel.System, "calendar source set → " + System.IO.Path.GetFileName(path.TrimEnd('/', '\\')));
    }

    /// <summary>Debug hook (--inspect): select the first node with a multi-line param to preview the inspector.</summary>
    public void DebugSelectFirst()
    {
        _lastGraph = Bot.Graph; // prevent the first-frame graph-change reset from clearing this
        foreach (var n in Bot.Graph.Nodes)
            if (Array.Exists(n.Def.Params, p => p.Type == ParamType.Multiline))
            { _editor.Selection.Clear(); _editor.Selection.Add(n.Id); return; }
    }

    // debug hooks for headless screenshots (set via CLI flags) ---------------
    public bool DebugAutoHistory, DebugAutoQuick;
    public void DebugOpenTemplate() { _templateOpen = true; _templateJustOpened = true; }
    public void DebugOpenSecrets() { _secretsOpen = true; _secretsJustOpened = true; }
    public void DebugOpenTest() { _testOpen = true; _testJustOpened = true; RunTest(); }
    public void DebugOpenSaveNode() { _saveNodeName = "Greeting Macro"; _saveNodeOpen = true; _saveNodeJustOpened = true; }
    public void DebugShowGh()
    {
        _l = Layout.Compute(_vw, _vh);
        _openCat = NodeCategory.Action;
        if (NodeCatalog.TryGet("gh.run", out var def))
        {
            var n = _editor.Spawn(def, Vector2.Zero);
            _editor.Selection.Clear(); _editor.Selection.Add(n.Id);
            _editor.FocusContent(_l.Canvas);
        }
        _lastGraph = Bot.Graph;
    }
    public void DebugOpenInstall()
    {
        _l = Layout.Compute(_vw, _vh);
        var p = System.IO.Path.Combine(NodeCatalog.CustomDir, "wordcount.ircnode");
        if (System.IO.File.Exists(p)) OnNodeDrop(_l.Canvas.Center, p);
    }

    public void DebugInstallClip() { _l = Layout.Compute(_vw, _vh); InstallFromClipboard(); }

    public void DebugOpenUninstall()
    {
        _l = Layout.Compute(_vw, _vh);
        var d = NodeCatalog.Custom.Count > 0 ? NodeCatalog.Custom[0] : null;
        if (d != null) { _uninstallDef = d; _uninstallOpen = true; _uninstallJustOpened = true; }
    }

    public void DebugOpenNodeManager() => OpenNodeManager();
    public void DebugSpawnSelect(string typeId)
    {
        if (!NodeCatalog.TryGet(typeId, out _)) return;
        var n = _editor.Spawn(NodeCatalog.Get(typeId), Vector2.Zero);
        _editor.Selection.Clear(); _editor.Selection.Add(n.Id); _lastGraph = Bot.Graph;
    }
    public void DebugShowLabels()
    {
        var n = Bot.Graph.Nodes.FirstOrDefault();
        if (n != null) { n.Title = "✨ my greeting"; _editor.Selection.Clear(); _editor.Selection.Add(n.Id); _lastGraph = Bot.Graph; }
        _renamingBot = Bot; _ui.Focus = "tab.rename";
    }

    public void Update(InputState input, Clock clock)
    {
        _input = input;
        _editor.Graph = Bot.Graph;
        _l = Layout.Compute(_vw, _vh);

        if (DebugAutoHistory && Bot.Runtime.HistoryCount > 0 && (!_historyOpen || _historyRuns.Count != Bot.Runtime.HistoryCount)) OpenHistory();
        if (DebugAutoQuick && !Modal) { OpenQuickAdd(_l.Canvas.Center); DebugAutoQuick = false; }

        if (Modal)
        {
            if (input.KeyPressed(Keys.Escape)) { _importOpen = false; _confirmDeleteBot = null; _historyOpen = false; _quickOpen = false; _templateOpen = false; _closePromptOpen = false; _secretsOpen = false; _testOpen = false; _ctxOpen = false; _saveNodeOpen = false; _installOpen = false; _uninstallOpen = false; _nodeMgrOpen = false; }
        }
        else if (_renamingBot != null)
        {
            // renaming a tab - keep the inline editor focused; Esc cancels
            if (input.KeyPressed(Keys.Escape)) { _renamingBot = null; _ui.Focus = null; }
        }
        else if (!_tut.Active)   // tutorial owns the canvas while it runs (it places/wires for you)
        {
            // right-click anywhere on the canvas → context menu
            if (In.RightPressed && _l.Canvas.Contains(input.Mouse) && !_ui.AnyFieldFocused)
            {
                // right-clicking a node that isn't already selected makes it the target
                var hit = _editor.NodeAt(input.Mouse);
                if (hit != null && !_editor.Selection.Contains(hit.Id)) { _editor.Selection.Clear(); _editor.Selection.Add(hit.Id); }
                OpenContextMenu(input.Mouse, hit != null);
            }

            // double-click empty canvas → quick-add menu
            if (In.LeftPressed && _l.Canvas.Contains(input.Mouse) && _editor.IsEmptyAt(input.Mouse))
            {
                if (clock.Time - _lastClickTime < 0.35f && Vector2.Distance(input.Mouse, _lastClickPos) < 6f)
                { OpenQuickAdd(input.Mouse); _lastClickTime = 0; }
                else { _lastClickTime = clock.Time; _lastClickPos = input.Mouse; }
            }

            if (!Modal)
            {
                _editor.Update(input, _l.Canvas, _ui.AnyFieldFocused);
                if (input.Ctrl && input.KeyPressed(Keys.S)) _app.Save();
                if (input.Ctrl && input.KeyPressed(Keys.R)) ToggleRun();
                if (input.Ctrl && input.KeyPressed(Keys.E)) _app.ExportActive();
                if (input.Ctrl && input.KeyPressed(Keys.H)) OpenHistory();
                if (input.Ctrl && input.KeyPressed(Keys.L)) { _editor.AutoLayout(); _editor.FocusContent(_l.Canvas); _app.MarkDirty(); }
                if (input.Ctrl && input.Shift && input.KeyPressed(Keys.V)) InstallFromClipboard();
            }
        }
    }

    private void OpenHistory()
    {
        _historyRuns = Bot.Runtime.History();
        _historyRuns.Reverse();                 // newest first
        _historySel = _historyRuns.Count > 0 ? 0 : -1;
        _historyListScroll = _historyDetailScroll = 0;
        _historyOpen = true; _historyJustOpened = true;
    }

    private void OpenQuickAdd(Vector2 screen)
    {
        _quickOpen = true; _quickJustOpened = true;
        _quickScreen = screen;
        _quickWorld = _editor.Cam.ScreenToWorld(screen);
        _quickSearch = "";
        _quickScroll = 0;
        _ui.Focus = "quick.search";             // auto-focus the search box
    }

    public void Draw(Renderer r, Clock clock)
    {
        _vw = r.ViewW; _vh = r.ViewH;
        _l = Layout.Compute(_vw, _vh);
        _editor.Graph = Bot.Graph;
        _editor.Running = Bot.Runtime.Running;
        if (!ReferenceEquals(_lastGraph, Bot.Graph)) { _editor.Selection.Clear(); _lastGraph = Bot.Graph; }
        _ui.Begin(r, In, clock);
        _ui.Enabled = !Modal;   // a modal blocks the widgets underneath it

        // ---------- canvas ----------
        r.Begin(BlendMode.Alpha, _l.Canvas.ToRectangle());
        r.RoundFill(_l.Canvas, Theme.Backdrop, Hud.PanelRadius);
        r.End();
        _editor.Draw(r, _l.Canvas, In, clock);
        r.Begin();
        if (Bot.Graph.Nodes.Count == 0) EmptyHint(r, _l.Canvas, clock);
        CanvasFrame(r, _l.Canvas);
        r.End();

        // ---------- panel chromes ----------
        r.Begin();
        Hud.Panel(r, _l.Palette, "Node Library", Theme.Cyan);
        Hud.Panel(r, _l.Inspector, "Inspector", Theme.Amber);
        Hud.Panel(r, _l.Console, "Event Console", Theme.Lime);
        ConsoleHeaderStats(r, _l.Console);
        TopBar(r, _l.TopBar, clock);
        StatusBar(r, _l.StatusBar, clock);
        DrawTabs(r, clock);
        r.End();

        // ---------- panel contents (scissored) ----------
        DrawPalette(r);
        DrawInspector(r);
        DrawConsole(r);

        // ---------- run button + tab buttons (over chrome) ----------
        r.Begin();
        RunButton(r, clock);
        HistoryButton(r);
        ApplyButton(r);
        r.End();

        // ---------- palette drag ghost ----------
        UpdatePaletteDrag(r);

        // ---------- overlay ----------
        r.Begin();
        Hud.Overlay(r, _vw, _vh);
        r.End();

        // ---------- modals (on top, capture input) ----------
        if (_importOpen)
        {
            _ui.Enabled = true;
            r.Begin();
            DrawImportModal(r);
            r.End();
        }
        else if (_confirmDeleteBot != null)
        {
            _ui.Enabled = true;
            r.Begin();
            DrawConfirmModal(r);
            r.End();
        }
        else if (_historyOpen)
        {
            _ui.Enabled = true;
            DrawHistoryModal(r, clock);
        }
        else if (_quickOpen)
        {
            _ui.Enabled = true;
            DrawQuickAdd(r);
        }
        else if (_templateOpen)
        {
            _ui.Enabled = true;
            DrawTemplateModal(r);
        }
        else if (_closePromptOpen)
        {
            _ui.Enabled = true;
            r.Begin();
            DrawCloseModal(r);
            r.End();
        }
        else if (_secretsOpen)
        {
            _ui.Enabled = true;
            DrawSecretsModal(r);
        }
        else if (_testOpen)
        {
            _ui.Enabled = true;
            DrawTestModal(r);
        }
        else if (_ctxOpen)
        {
            _ui.Enabled = true;
            r.Begin();
            DrawContextMenu(r);
            r.End();
        }
        else if (_saveNodeOpen)
        {
            _ui.Enabled = true;
            r.Begin();
            DrawSaveNodeModal(r);
            r.End();
        }
        else if (_installOpen)
        {
            _ui.Enabled = true;
            r.Begin();
            DrawInstallModal(r);
            r.End();
        }
        else if (_uninstallOpen)
        {
            _ui.Enabled = true;
            r.Begin();
            DrawUninstallModal(r);
            r.End();
        }
        else if (_nodeMgrOpen)
        {
            _ui.Enabled = true;
            DrawNodeManager(r);
        }

        // ---------- gamified tutorial overlay (on top of everything but app modals) ----------
        DrawTutorial(r, clock);

        _ui.EndFrame(); // blur stale focus (e.g. after switching node/bot) so canvas shortcuts keep working
    }

    private void DrawSaveNodeModal(Renderer r)
    {
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.45f));
        float pw = 480, ph = 236;
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        Hud.Panel(r, panel, "Save selection as a node", Theme.Violet);

        float x = panel.X + 22, w = panel.W - 44, y = panel.Y + Hud.HeaderH + 16;
        foreach (var line in Wrap(r.Fonts.Get(FontKind.Sans, 13), "Bundles the selected nodes into one reusable node. Use a Subflow Start as the entry, and Subflow Input/Output nodes to define its pins. Installs into ~/ircuitry/nodes.", w))
        { r.Text(r.Fonts.Get(FontKind.Sans, 13), line, new Vector2(x, y), Theme.TextDim); y += 18; }
        y += 10;
        r.Text(r.Fonts.Get(FontKind.SansBold, 11), "NODE NAME", new Vector2(x, y), Theme.TextDim); y += 18;
        _saveNodeName = _ui.TextField("savenode.name", new RectF(x, y, w, 30), _saveNodeName, "My Node");

        var saveR = new RectF(panel.Right - 22 - 132, panel.Bottom - 50, 132, 34);
        var cancelR = new RectF(saveR.X - 12 - 110, panel.Bottom - 50, 110, 34);
        if (_ui.Button("savenode.cancel", cancelR, "CANCEL", Theme.Idle)) _saveNodeOpen = false;
        if (_ui.Button("savenode.save", saveR, "SAVE NODE", Theme.Violet, primary: true))
        {
            var name = _saveNodeName.Trim(); if (name.Length == 0) name = "My Node";
            var manifest = _editor.SaveSelectionAsNode(name);
            if (manifest == null) Bot.Log.Add(LogLevel.Error, "Add a 'Subflow Start' node to your selection first.");
            else
            {
                try
                {
                    var def = Ircuitry.Graph.CustomNode.Load(manifest) ?? throw new Exception("invalid manifest");
                    System.IO.Directory.CreateDirectory(NodeCatalog.CustomDir);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(NodeCatalog.CustomDir, def.TypeId + ".ircnode"), manifest);
                    NodeCatalog.LoadCustom();
                    Bot.Log.Add(LogLevel.System, $"saved reusable node “{name}” → Node Library ▸ Logic & Flow");
                }
                catch (Exception ex) { Bot.Log.Add(LogLevel.Error, "save node failed: " + ex.Message); }
            }
            _saveNodeOpen = false;
        }
        if (In.LeftPressed && !panel.Contains(In.Mouse) && !_saveNodeJustOpened) _saveNodeOpen = false;
        _saveNodeJustOpened = false;
    }

    private void DrawInstallModal(Renderer r)
    {
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.45f));
        float pw = 560, ph = 430;
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        Hud.Panel(r, panel, "Install community node?", Theme.Amber);

        float x = panel.X + 22, w = panel.W - 44, y = panel.Y + Hud.HeaderH + 16;
        var d = _installDef;
        if (d != null)
        {
            r.Text(r.Fonts.Get(FontKind.SansBold, 15), d.Icon + "  " + d.Title, new Vector2(x, y), Theme.Text); y += 24;
            r.Text(r.Fonts.Get(FontKind.Mono, 11), $"{d.TypeId} · {d.Category} · {d.Inputs.Length}in/{d.Outputs.Length}out", new Vector2(x, y), Theme.TextDim); y += 22;
        }
        r.Text(r.Fonts.Get(FontKind.Sans, 12), "⚠  This runs code on your machine. Review before installing:", new Vector2(x, y), Theme.Alert); y += 22;

        var box = new RectF(x, y, w, panel.Bottom - 50 - y - 12);
        r.RoundFill(box, Theme.PanelLo, 7f);
        var mf = r.Fonts.Get(FontKind.Mono, 11);
        float ly = box.Y + 6f;
        foreach (var raw in _installPreview.Split('\n'))
        {
            if (ly + 14f > box.Bottom - 4f) { r.Text(mf, "…", new Vector2(box.X + 8, ly), Theme.TextFaint); break; }
            r.Text(mf, r.Ellipsize(mf, raw, box.W - 16), new Vector2(box.X + 8, ly), Theme.Text);
            ly += 14f;
        }

        var goR = new RectF(panel.Right - 22 - 120, panel.Bottom - 50, 120, 34);
        var cancelR = new RectF(goR.X - 12 - 110, panel.Bottom - 50, 110, 34);
        if (_ui.Button("install.cancel", cancelR, "CANCEL", Theme.Idle)) _installOpen = false;
        if (_ui.Button("install.go", goR, "INSTALL", Theme.Amber, primary: true))
        {
            try
            {
                System.IO.Directory.CreateDirectory(NodeCatalog.CustomDir);
                if (_installText != null && d != null)
                    System.IO.File.WriteAllText(System.IO.Path.Combine(NodeCatalog.CustomDir, d.TypeId + ".ircnode"), _installText);
                else
                    System.IO.File.Copy(_installPath, System.IO.Path.Combine(NodeCatalog.CustomDir, System.IO.Path.GetFileName(_installPath)), overwrite: true);
                NodeCatalog.LoadCustom();
                if (d != null && NodeCatalog.TryGet(d.TypeId, out var inst)) { _editor.Spawn(inst, _editor.Cam.ScreenToWorld(_installScreen)); _app.MarkDirty(); }
                Bot.Log.Add(LogLevel.System, $"installed “{d?.Title}” ({d?.TypeId})");
            }
            catch (Exception ex) { Bot.Log.Add(LogLevel.Error, "install failed: " + ex.Message); }
            _installOpen = false;
        }
        if (In.LeftPressed && !panel.Contains(In.Mouse) && !_installJustOpened) _installOpen = false;
        _installJustOpened = false;
    }

    private void DrawCloseModal(Renderer r)
    {
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.45f));
        float pw = 480, ph = 188;
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        Hud.Panel(r, panel, "Close ircuitry?", Theme.Cyan);

        float x = panel.X + 22, w = panel.W - 44, y = panel.Y + Hud.HeaderH + 18;
        foreach (var line in Wrap(r.Fonts.Get(FontKind.Sans, 13), "Minimise keeps your bots running in the background. Exit stops every bot and closes the app.", w))
        { r.Text(r.Fonts.Get(FontKind.Sans, 13), line, new Vector2(x, y), Theme.TextDim); y += 18; }

        var exitR = new RectF(panel.Right - 22 - 120, panel.Bottom - 50, 120, 34);
        var minR = new RectF(exitR.X - 12 - 130, panel.Bottom - 50, 130, 34);
        var cancelR = new RectF(panel.X + 22, panel.Bottom - 50, 100, 34);
        if (_ui.Button("close.cancel", cancelR, "CANCEL", Theme.Idle)) _closePromptOpen = false;
        if (_ui.Button("close.min", minR, "MINIMISE", Theme.Cyan)) { _closePromptOpen = false; OnMinimizeRequested?.Invoke(); }
        if (_ui.Button("close.exit", exitR, "EXIT", Theme.Alert, primary: true)) { _closePromptOpen = false; OnExitRequested?.Invoke(); }

        if (In.LeftPressed && !panel.Contains(In.Mouse) && !_closeJustOpened) _closePromptOpen = false;
        _closeJustOpened = false;
    }

    private void DrawConfirmModal(Renderer r)
    {
        var bot = _confirmDeleteBot;
        if (bot == null) return;
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.45f));

        float pw = 480, ph = 190;
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        Hud.Panel(r, panel, "Delete bot?", Theme.Alert);

        float x = panel.X + 22, w = panel.W - 44, y = panel.Y + Hud.HeaderH + 18;
        r.Text(r.Fonts.Get(FontKind.SansBold, 16), $"Delete “{bot.Name}”?", new Vector2(x, y), Theme.Text);
        y += 26;
        foreach (var line in Wrap(r.Fonts.Get(FontKind.Sans, 13), "This removes the bot's workflow, connection and variables. This can't be undone.", w))
        { r.Text(r.Fonts.Get(FontKind.Sans, 13), line, new Vector2(x, y), Theme.TextDim); y += 17; }

        var delRect = new RectF(panel.Right - 22 - 130, panel.Bottom - 50, 130, 34);
        var cancelRect = new RectF(delRect.X - 12 - 110, panel.Bottom - 50, 110, 34);
        if (_ui.Button("confirm.cancel", cancelRect, "CANCEL", Theme.Idle)) _confirmDeleteBot = null;
        if (_ui.Button("confirm.delete", delRect, "DELETE", Theme.Alert, primary: true))
        {
            int idx = _app.Bots.IndexOf(bot);
            if (idx >= 0) { _app.RemoveBot(idx); _editor.Selection.Clear(); }
            _confirmDeleteBot = null;
        }

        if (!_confirmJustOpened && In.LeftPressed && !panel.Contains(In.Mouse)) _confirmDeleteBot = null;
        _confirmJustOpened = false;
    }

    private void DrawImportModal(Renderer r)
    {
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.45f));

        float pw = 560, ph = Math.Min(460, 150 + _importFiles.Length * 44 + 60);
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        Hud.Panel(r, panel, "Import .ircbot", Theme.Lime);

        float x = panel.X + 20, w = panel.W - 40, y = panel.Y + Hud.HeaderH + 16;
        r.Text(r.Fonts.Get(FontKind.Sans, 14), "Drag a .ircbot file onto the window, or pick one from " + AppModel.WorkspaceDir + ":",
            new Vector2(x, y), Theme.TextDim);
        y += 28;

        if (_importFiles.Length == 0)
            r.Text(r.Fonts.Get(FontKind.Sans, 14), "No .ircbot files found - export one first.", new Vector2(x, y + 8), Theme.TextFaint);

        foreach (var file in _importFiles)
        {
            if (y + 36 > panel.Bottom - 52) break;
            if (_ui.Button("imp." + file, new RectF(x, y, w, 34), "📦  " + System.IO.Path.GetFileName(file), Theme.Cyan))
            { _app.ImportFile(file); _importOpen = false; }
            y += 40;
        }

        if (_ui.Button("imp.cancel", new RectF(panel.Right - 130, panel.Bottom - 46, 110, 32), "CANCEL", Theme.Idle))
            _importOpen = false;

        // click outside the panel closes - but ignore the very click that opened it
        if (!_importJustOpened && In.LeftPressed && !panel.Contains(In.Mouse)) _importOpen = false;
        _importJustOpened = false;
    }

    // ===================================================================
    private void CanvasFrame(Renderer r, RectF c)
    {
        r.RoundOutline(c, Theme.Edge, Hud.PanelRadius);
        Hud.CornerBrackets(r, c, Theme.WithAlpha(Theme.Cyan, 0.5f), 18f, 2f);
        var f = r.Fonts.Get(FontKind.SansBold, 12);
        var box = new RectF(c.X + 12, c.Y + 12, 250, 26);
        r.RoundFill(box, Theme.WithAlpha(Theme.PanelHi, 0.92f), 7f);
        r.RoundOutline(box, Theme.Hairline, 7f);
        r.Fill(new RectF(box.X + 9, box.Y + 7, 3, 12), Theme.Cyan);
        r.Text(f, $"{Bot.Name}  ·  {Bot.Graph.Nodes.Count} nodes · {Bot.Graph.Connections.Count} wires", new Vector2(box.X + 18, box.Y + 7), Theme.TextDim);
        r.TextRight(r.Fonts.Get(FontKind.SansBold, 12), $"{(int)Math.Round(_editor.Cam.Zoom * 100)}%", c.Right - 14, c.Bottom - 24, Theme.TextFaint);
    }

    private void EmptyHint(Renderer r, RectF c, Clock clock)
    {
        var f = r.Fonts.Get(FontKind.Mono, 16);
        float a = 0.25f + 0.1f * clock.Sin01(2.4f);
        r.TextCenteredX(f, "▣  drag a node from the Node Library to begin", c.Center.X, c.Center.Y - 10, Theme.WithAlpha(Theme.TextDim, a));
    }

    // ===================================================================
    private void DrawTabs(Renderer r, Clock clock)
    {
        var bar = _l.Tabs;
        var tf = r.Fonts.Get(FontKind.Display, 13);
        float x = bar.X;

        for (int i = 0; i < _app.Bots.Count; i++)
        {
            var bot = _app.Bots[i];
            bool active = i == _app.Active;
            bool renaming = _renamingBot == bot;
            float w = renaming ? 220 : Math.Min(220, tf.MeasureString(bot.Name).X + 58);
            var tab = new RectF(x, bar.Y, w, bar.H);
            var col = StatusColor(bot.Runtime);

            r.RoundFill(tab, active ? Theme.PanelHi : Theme.PanelLo, 8f);
            r.RoundOutline(tab, renaming ? Theme.Amber : active ? Theme.WithAlpha(Theme.Cyan, 0.9f) : Theme.Hairline, 8f);
            if (active) r.Fill(new RectF(tab.X + 8, tab.Bottom - 3, tab.W - 16, 2), Theme.Cyan);
            Hud.SoftDot(r, new Vector2(tab.X + 15, tab.Center.Y), 3.2f, col);

            if (renaming)
            {
                // inline editor - Enter or click-away commits; Esc cancels (handled in Update)
                var nm = _ui.TextField("tab.rename", new RectF(tab.X + 24, tab.Y + 4, tab.W - 32, tab.H - 8), bot.Name, "bot name");
                if (nm != bot.Name) { bot.Name = string.IsNullOrWhiteSpace(nm) ? bot.Name : nm; _app.MarkDirty(); }
                if (_ui.Focus != "tab.rename") _renamingBot = null;   // committed / blurred
                x += w + 6;
                continue;
            }

            r.Text(tf, r.Ellipsize(tf, bot.Name, w - 50), new Vector2(tab.X + 26, tab.Center.Y - tf.MeasureString(bot.Name).Y / 2f), active ? Theme.Text : Theme.TextDim);

            // close button - drawn procedurally (the ✕ glyph isn't in the UI font), only when >1 bot
            bool canClose = _app.Bots.Count > 1;
            var xc = new Vector2(tab.Right - 14, tab.Center.Y);
            var xhit = new RectF(xc.X - 10, tab.Y, 20, tab.H);
            bool xHover = canClose && xhit.Contains(In.Mouse);
            if (canClose)
            {
                if (xHover) r.Disc(xc, 9f, Theme.WithAlpha(Theme.Alert, 0.2f));
                var xcol = xHover ? Theme.Alert : active ? Theme.TextDim : Theme.TextFaint;
                const float s = 3.5f;
                r.Line(new Vector2(xc.X - s, xc.Y - s), new Vector2(xc.X + s, xc.Y + s), xcol, 1.8f);
                r.Line(new Vector2(xc.X - s, xc.Y + s), new Vector2(xc.X + s, xc.Y - s), xcol, 1.8f);
            }

            if (!Modal && In.LeftPressed && tab.Contains(In.Mouse))
            {
                if (xHover) { _confirmDeleteBot = bot; _confirmJustOpened = true; }   // confirm before deleting
                else
                {
                    bool dbl = _tabClickBot == bot && clock.Time - _tabClickTime < 0.35f;
                    _tabClickBot = bot; _tabClickTime = clock.Time;
                    if (!active) { _app.SetActive(i); _editor.Selection.Clear(); }
                    if (dbl) { _renamingBot = bot; _ui.Focus = "tab.rename"; }   // double-click → rename
                }
                return; // collection/active may change - bail this frame
            }
            x += w + 6;
        }

        if (_ui.Button("tab.add", new RectF(x, bar.Y, 36, bar.H), "+", Theme.Cyan))
        { _templateOpen = true; _templateJustOpened = true; }

        // right cluster: import / export / save
        float rx = bar.Right;
        RectF Slot(float ww) { var rr = new RectF(rx - ww, bar.Y, ww, bar.H); rx -= ww + 6; return rr; }
        if (_ui.Button("tab.tidy", Slot(80), "⤢ TIDY", Theme.Lime)) { _editor.AutoLayout(); _editor.FocusContent(_l.Canvas); _app.MarkDirty(); }
        if (_ui.Button("tab.test", Slot(86), "▶ TEST", Theme.Cyan)) { _testOpen = true; _testJustOpened = true; RunTest(); }
        if (_ui.Button("tab.secrets", Slot(86), "🔑 KEYS", Theme.Violet)) { _secretsOpen = true; _secretsJustOpened = true; }
        if (_ui.Button("tab.save", Slot(92), _app.Dirty ? "● SAVE" : "SAVE", _app.Dirty ? Theme.Amber : Theme.Ok)) _app.Save();
        if (_ui.Button("tab.export", Slot(96), "EXPORT", Theme.Cyan)) _app.ExportActive();
        if (_ui.Button("tab.import", Slot(96), "IMPORT", Theme.Cyan)) { _importFiles = _app.Importable().ToArray(); _importOpen = true; _importJustOpened = true; }
        if (_ui.Button("tab.help", Slot(40), "?", Theme.Amber)) ForceStartTutorial();
    }

    // ===================================================================
    private static string CategoryName(NodeCategory c) => c switch
    {
        NodeCategory.Event => "Events",
        NodeCategory.Filter => "Conditions",
        NodeCategory.Logic => "Logic & Flow",
        NodeCategory.Data => "Text & Values",
        NodeCategory.Ai => "AI",
        NodeCategory.Storage => "Files & Database",
        NodeCategory.Action => "Actions",
        _ => c.ToString(),
    };

    private static string CategoryIcon(NodeCategory c) => c switch
    {
        NodeCategory.Event => "⚡",
        NodeCategory.Filter => "❓",
        NodeCategory.Logic => "🔀",
        NodeCategory.Data => "🔢",
        NodeCategory.Ai => "🤖",
        NodeCategory.Storage => "💾",
        NodeCategory.Action => "💬",
        _ => "🧩",
    };

    private void DrawPalette(Renderer r)
    {
        var p = _l.Palette;
        var content = new RectF(p.X + 6, p.Y + Hud.HeaderH + 2, p.W - 12, p.H - Hud.HeaderH - 8);
        if (content.Contains(In.Mouse)) _paletteScroll = Math.Max(0, _paletteScroll - In.ScrollDelta / 4f);

        // search field + a single tidy entry point for community nodes (install / update / remove)
        float x = content.X + 8, w = content.W - 16;
        var searchRect = new RectF(x, content.Y + 8, w, 30);
        var mgrRect = new RectF(x, searchRect.Bottom + 8, w, 30);
        r.Begin(BlendMode.Alpha, content.ToRectangle());
        _paletteSearch = _ui.TextField("palette.search", searchRect, _paletteSearch, "⌕  search nodes…");
        int customCount = NodeCatalog.Custom.Count;
        if (_ui.Button("palette.manage", mgrRect, customCount > 0 ? $"🧩  Community nodes · {customCount}" : "🧩  Community nodes", Theme.Berry))
            OpenNodeManager();
        r.End();
        string q = _paletteSearch.Trim();
        bool searching = q.Length > 0;

        var listClip = new RectF(content.X, mgrRect.Bottom + 8, content.W, content.Bottom - mgrRect.Bottom - 10);
        if (listClip.Contains(In.Mouse)) { /* scroll handled above on content */ }
        r.Begin(BlendMode.Alpha, listClip.ToRectangle());
        float y = listClip.Y - _paletteScroll;

        foreach (var group in NodeCatalog.ByCategory())
        {
            var matches = searching
                ? group.Where(d => Has(d.Title, q) || Has(d.Subtitle, q) || Has(d.TypeId, q)).ToList()
                : group.ToList();
            if (matches.Count == 0) continue;

            var col = Theme.Category(group.Key);
            bool collapsed = !searching && _openCat != group.Key;

            // cosy pill header: tinted rounded background, colored icon tile, readable name, count badge, chevron
            const float hh = 32f;
            var hdr = new RectF(x, y, w, hh);
            bool hHover = hdr.Contains(In.Mouse) && listClip.Contains(In.Mouse);
            r.RoundFill(hdr, Theme.Mix(Theme.PanelHi, col, hHover ? 0.28f : 0.16f), 10f);
            r.RoundOutline(hdr, Theme.WithAlpha(col, 0.35f), 10f);
            var tile = new RectF(hdr.X + 5, hdr.Y + 5, hh - 10, hh - 10);
            r.RoundFill(tile, col, 7f);
            var icf = r.Fonts.Get(FontKind.Display, 14);
            string ci = CategoryIcon(group.Key);
            r.Text(icf, ci, new Vector2(tile.Center.X - icf.MeasureString(ci).X / 2f, tile.Center.Y - icf.MeasureString(ci).Y / 2f), Theme.TextInk);
            var nf = r.Fonts.Get(FontKind.SansBold, 13);
            r.Text(nf, CategoryName(group.Key), new Vector2(tile.Right + 9, hdr.Center.Y - nf.MeasureString("M").Y / 2f - 1), Theme.Text);
            var cf = r.Fonts.Get(FontKind.SansBold, 11);
            string cnt = matches.Count.ToString();
            float cw = cf.MeasureString(cnt).X + 14;
            var badge = new RectF(hdr.Right - 12 - 16 - cw, hdr.Center.Y - 9, cw, 18);
            r.RoundFill(badge, Theme.WithAlpha(col, 0.92f), 9f);
            r.Text(cf, cnt, new Vector2(badge.Center.X - cf.MeasureString(cnt).X / 2f, badge.Center.Y - cf.MeasureString(cnt).Y / 2f), Theme.TextInk);
            var chf = r.Fonts.Get(FontKind.SansBold, 12);
            r.Text(chf, collapsed ? "▸" : "▾", new Vector2(hdr.Right - 18, hdr.Center.Y - chf.MeasureString("M").Y / 2f - 1), Theme.WithAlpha(Theme.Text, 0.55f));

            if (!searching && !Modal && In.LeftPressed && hdr.Contains(In.Mouse))
                _openCat = _openCat == group.Key ? (NodeCategory?)null : group.Key;
            y += hh + 7;

            if (collapsed) continue;
            foreach (var def in matches)
            {
                var chip = new RectF(x + 6, y, w - 6, 40);
                if (chip.Bottom > listClip.Y && chip.Y < listClip.Bottom) DrawPaletteChip(r, chip, def, col, listClip);
                y += 46;
            }
            y += 10;
        }
        r.End();

        float total = y + _paletteScroll - listClip.Y;
        _paletteScroll = Math.Clamp(_paletteScroll, 0, Math.Max(0, total - listClip.H));
    }

    private static bool Has(string s, string q) => s.Contains(q, StringComparison.OrdinalIgnoreCase);

    private void DrawPaletteChip(Renderer r, RectF chip, NodeDef def, Color col, RectF content)
    {
        bool hover = chip.Contains(In.Mouse) && content.Contains(In.Mouse) && _dragDef == null;
        r.RoundFill(chip, hover ? Theme.Mix(Theme.PanelHi, col, 0.14f) : Theme.PanelHi, 9f);
        r.RoundOutline(chip, Theme.WithAlpha(col, hover ? 0.65f : 0.30f), 9f);
        r.Fill(new RectF(chip.X, chip.Y + 7, 3, chip.H - 14), col);
        var iconImg = def.IconImage != null ? r.IconTexture(def.TypeId, def.IconImage) : null;
        if (iconImg != null)
            r.Image(iconImg, new RectF(chip.X + 12, chip.Center.Y - 10, 20, 20));
        else
        {
            var iconF = r.Fonts.Get(FontKind.Display, 17);
            r.Text(iconF, def.Icon, new Vector2(chip.X + 13, chip.Center.Y - iconF.MeasureString(def.Icon).Y / 2f), col);
        }
        r.Text(r.Fonts.Get(FontKind.SansBold, 13), def.Title, new Vector2(chip.X + 42, chip.Y + 6), Theme.Text);
        r.Text(r.Fonts.Get(FontKind.Sans, 10), def.Subtitle, new Vector2(chip.X + 42, chip.Y + 23), Theme.TextFaint);

        if (hover && In.LeftPressed && !Modal) { _dragDef = def; _dragStart = In.Mouse; _dragging = false; }
    }

    private void DrawUninstallModal(Renderer r)
    {
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.45f));
        float pw = 460, ph = 196;
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        Hud.Panel(r, panel, "Uninstall community node?", Theme.Alert);
        var d = _uninstallDef;
        float x = panel.X + 22, y = panel.Y + Hud.HeaderH + 18;
        if (d != null)
        {
            r.Text(r.Fonts.Get(FontKind.SansBold, 15), d.Icon + "  " + d.Title, new Vector2(x, y), Theme.Text); y += 24;
            r.Text(r.Fonts.Get(FontKind.Mono, 11), d.TypeId, new Vector2(x, y), Theme.TextDim); y += 24;
        }
        r.Text(r.Fonts.Get(FontKind.Sans, 12), "Removes it from your Node Library. You can re-add it any time.", new Vector2(x, y), Theme.TextDim);

        var goR = new RectF(panel.Right - 22 - 120, panel.Bottom - 50, 120, 34);
        var cancelR = new RectF(goR.X - 12 - 110, panel.Bottom - 50, 110, 34);
        if (_ui.Button("uninstall.cancel", cancelR, "CANCEL", Theme.Idle)) _uninstallOpen = false;
        if (_ui.Button("uninstall.go", goR, "UNINSTALL", Theme.Alert, primary: true))
        {
            if (d != null && NodeCatalog.Uninstall(d.TypeId)) Bot.Log.Add(LogLevel.System, $"uninstalled “{d.Title}” ({d.TypeId})");
            else Bot.Log.Add(LogLevel.Error, "could not uninstall " + (d?.TypeId ?? "node"));
            _uninstallOpen = false;
        }
        if (In.LeftPressed && !panel.Contains(In.Mouse) && !_uninstallJustOpened) _uninstallOpen = false;
        _uninstallJustOpened = false;
    }

    // ====================== community node manager ======================
    private const string NodeLibraryUrl = "https://ircuitry.github.io/nodes";

    private void OpenNodeManager()
    {
        if (Modal) return;
        _l = Layout.Compute(_vw, _vh);
        _nodeMgrOpen = true; _nodeMgrJustOpened = true; _nodeMgrScroll = 0; _nodeMgrSel.Clear();
        StartNodeUpdateCheck();
    }

    // Fetch the live library index on a background thread and flag installed nodes whose code differs.
    private void StartNodeUpdateCheck()
    {
        _nodeMgrChecking = true;
        _nodeMgrUpdates.Clear(); _nodeMgrInLibrary.Clear(); _nodeMgrLatest.Clear();
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var (status, body) = Ircuitry.Net.Http.Send("GET",
                    "https://raw.githubusercontent.com/ircuitry/community-nodes/main/index.json",
                    System.Array.Empty<(string, string)>(), null);
                if (status < 200 || status >= 300) return;

                var installed = new Dictionary<string, (string code, string sub)>();
                try
                {
                    foreach (var f in System.IO.Directory.GetFiles(NodeCatalog.CustomDir, "*.ircnode"))
                        try
                        {
                            using var d = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(f));
                            var root = d.RootElement;
                            string tid = root.TryGetProperty("typeId", out var t) ? t.GetString() ?? "" : "";
                            if (tid.Length == 0) continue;
                            string code = root.TryGetProperty("code", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String ? c.GetString() ?? "" : "";
                            string sub = root.TryGetProperty("subgraph", out var s) ? s.GetRawText() : "";
                            installed[tid] = (code, sub);
                        }
                        catch { }
                }
                catch { }

                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("nodes", out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array) return;
                foreach (var n in arr.EnumerateArray())
                {
                    string tid = n.TryGetProperty("typeId", out var t) ? t.GetString() ?? "" : "";
                    if (tid.Length == 0) continue;
                    _nodeMgrInLibrary[tid] = 1;
                    if (!n.TryGetProperty("manifest", out var man) || man.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    _nodeMgrLatest[tid] = man.GetRawText();
                    if (installed.TryGetValue(tid, out var inst))
                    {
                        string idxCode = man.TryGetProperty("code", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String ? c.GetString() ?? "" : "";
                        string idxSub = man.TryGetProperty("subgraph", out var s) ? s.GetRawText() : "";
                        if (idxCode != inst.code || idxSub != inst.sub)
                        {
                            string note = n.TryGetProperty("note", out var nn) ? nn.GetString() ?? "" : "";
                            _nodeMgrUpdates[tid] = note.Length > 0 ? note : "updated in the library";
                        }
                    }
                }
            }
            catch { }
            finally { _nodeMgrChecking = false; }
        });
    }

    private void UpdateNode(string tid)
    {
        if (!_nodeMgrLatest.TryGetValue(tid, out var manifest)) return;
        try
        {
            System.IO.Directory.CreateDirectory(NodeCatalog.CustomDir);
            string target = System.IO.Path.Combine(NodeCatalog.CustomDir, tid + ".ircnode");
            foreach (var f in System.IO.Directory.GetFiles(NodeCatalog.CustomDir, "*.ircnode"))
                try
                {
                    using var d = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(f));
                    if ((d.RootElement.TryGetProperty("typeId", out var t) ? t.GetString() : "") == tid) { target = f; break; }
                }
                catch { }
            System.IO.File.WriteAllText(target, manifest);
            NodeCatalog.LoadCustom();
            _nodeMgrUpdates.TryRemove(tid, out _);
            Bot.Log.Add(LogLevel.System, $"updated node {tid}");
        }
        catch (Exception ex) { Bot.Log.Add(LogLevel.Error, "update failed: " + ex.Message); }
    }

    private void RemoveSelectedNodes()
    {
        int n = 0;
        foreach (var tid in _nodeMgrSel.ToList())
            if (NodeCatalog.Uninstall(tid)) { n++; _nodeMgrUpdates.TryRemove(tid, out _); }
        _nodeMgrSel.Clear();
        if (n > 0) Bot.Log.Add(LogLevel.System, $"uninstalled {n} community node(s)");
    }

    private void DrawNodeManager(Renderer r)
    {
        r.Begin();
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.5f));
        float pw = MathF.Min(780, _vw * 0.92f), ph = MathF.Min(620, _vh * 0.9f);
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        Hud.Panel(r, panel, "Community nodes", Theme.Berry);

        var custom = NodeCatalog.Custom;
        int updates = _nodeMgrUpdates.Count;
        float x = panel.X + 20, top = panel.Y + Hud.HeaderH + 14;
        r.Text(r.Fonts.Get(FontKind.SansBold, 14), custom.Count == 0 ? "No community nodes installed" : $"{custom.Count} installed", new Vector2(x, top), Theme.Text);
        string status = _nodeMgrChecking ? "checking the library for updates…" : updates > 0 ? $"{updates} update{(updates == 1 ? "" : "s")} available" : "all up to date";
        r.Text(r.Fonts.Get(FontKind.Sans, 12), status, new Vector2(x, top + 20), updates > 0 ? Theme.Warn : Theme.TextDim);
        r.End();

        r.Begin();
        float bh = 30, by = top - 2, gap = 10, bx = panel.Right - 20;
        bx -= 138; if (_ui.Button("nm.browse", new RectF(bx, by, 138, bh), "🌐 Browse library ↗", Theme.Lime)) Ircuitry.App.DeepLink.OpenUrl(NodeLibraryUrl);
        bx -= gap + 176; if (_ui.Button("nm.paste", new RectF(bx, by, 176, bh), "⎘  Install from clipboard", Theme.Cyan)) { _nodeMgrOpen = false; InstallFromClipboard(); }
        if (updates > 0) { bx -= gap + 138; if (_ui.Button("nm.updateall", new RectF(bx, by, 138, bh), $"⤓ Update all ({updates})", Theme.Amber, primary: true)) foreach (var tid in _nodeMgrUpdates.Keys.ToList()) UpdateNode(tid); }
        r.End();

        float listTop = top + 44;
        float footerY = panel.Bottom - 52;
        var listRect = new RectF(panel.X + 16, listTop, panel.W - 32, footerY - listTop - 10);
        r.Begin(); r.RoundFill(listRect, Theme.PanelLo, 8); r.RoundOutline(listRect, Theme.Edge, 8); r.End();

        if (custom.Count == 0)
        {
            r.Begin();
            r.TextCenteredX(r.Fonts.Get(FontKind.SansBold, 15), "Nothing installed yet", listRect.Center.X, listRect.Center.Y - 16, Theme.TextDim);
            r.TextCenteredX(r.Fonts.Get(FontKind.Sans, 12), "Copy a node from the library and use Install from clipboard.", listRect.Center.X, listRect.Center.Y + 8, Theme.TextFaint);
            r.End();
        }
        else DrawNodeManagerList(r, listRect);

        r.Begin();
        int sel = _nodeMgrSel.Count;
        var allR = new RectF(panel.X + 16, footerY, 116, 34);
        var closeR = new RectF(panel.Right - 16 - 100, footerY, 100, 34);
        var rmR = new RectF(closeR.X - 12 - 184, footerY, 184, 34);
        if (_ui.Button("nm.all", allR, sel == custom.Count && custom.Count > 0 ? "Select none" : "Select all", Theme.Idle))
        {
            if (sel == custom.Count) _nodeMgrSel.Clear();
            else { _nodeMgrSel.Clear(); foreach (var d in custom) _nodeMgrSel.Add(d.TypeId); }
        }
        bool canRemove = sel > 0;
        if (_ui.Button("nm.remove", rmR, canRemove ? $"🗑  Remove selected ({sel})" : "Remove selected", canRemove ? Theme.Alert : Theme.Idle, primary: canRemove) && canRemove)
            RemoveSelectedNodes();
        if (_ui.Button("nm.close", closeR, "CLOSE", Theme.Cyan, primary: true)) _nodeMgrOpen = false;
        r.End();

        if (In.LeftPressed && !panel.Contains(In.Mouse) && !_nodeMgrJustOpened) _nodeMgrOpen = false;
        _nodeMgrJustOpened = false;
    }

    private void DrawNodeManagerList(Renderer r, RectF rect)
    {
        var custom = NodeCatalog.Custom;
        const float rowH = 60f;
        float total = custom.Count * rowH;
        if (rect.Contains(In.Mouse) && In.ScrollDelta != 0) _nodeMgrScroll -= In.ScrollDelta;
        _nodeMgrScroll = Math.Clamp(_nodeMgrScroll, 0, MathF.Max(0, total - rect.H));

        r.Begin(BlendMode.Alpha, rect.ToRectangle());
        float y = rect.Y - _nodeMgrScroll;
        var tf = r.Fonts.Get(FontKind.SansBold, 13);
        var mf = r.Fonts.Get(FontKind.Mono, 10);
        var nf = r.Fonts.Get(FontKind.Sans, 11);
        var icf = r.Fonts.Get(FontKind.Display, 18);
        foreach (var def in custom)
        {
            var row = new RectF(rect.X + 4, y + 3, rect.W - 8, rowH - 6);
            if (row.Bottom >= rect.Y && row.Y <= rect.Bottom)
            {
                bool selected = _nodeMgrSel.Contains(def.TypeId);
                bool hover = row.Contains(In.Mouse) && rect.Contains(In.Mouse);
                var col = Theme.Category(def.Category);
                r.RoundFill(row, selected ? Theme.Mix(Theme.PanelHi, Theme.Cyan, 0.20f) : hover ? Theme.PanelHi : Theme.Panel, 8);
                r.RoundOutline(row, selected ? Theme.Cyan : Theme.WithAlpha(col, 0.3f), 8);

                var cb = new RectF(row.X + 12, row.Center.Y - 9, 18, 18);
                r.RoundFill(cb, selected ? Theme.Cyan : Theme.PanelLo, 5);
                r.RoundOutline(cb, selected ? Theme.Cyan : Theme.Edge, 5);
                if (selected) r.Text(tf, "✓", new Vector2(cb.Center.X - tf.MeasureString("✓").X / 2f, cb.Center.Y - tf.MeasureString("✓").Y / 2f), Theme.TextInk);

                var iconImg = def.IconImage != null ? r.IconTexture(def.TypeId, def.IconImage) : null;
                if (iconImg != null) r.Image(iconImg, new RectF(row.X + 40, row.Center.Y - 11, 22, 22));
                else r.Text(icf, def.Icon, new Vector2(row.X + 40, row.Center.Y - icf.MeasureString(def.Icon).Y / 2f), col);

                bool inLib = _nodeMgrInLibrary.ContainsKey(def.TypeId);
                bool hasUpdate = _nodeMgrUpdates.TryGetValue(def.TypeId, out var note);
                r.Text(tf, def.Title, new Vector2(row.X + 72, row.Y + 9), Theme.Text);
                r.Text(mf, def.TypeId, new Vector2(row.X + 72, row.Y + 27), Theme.TextDim);
                string meta = hasUpdate ? "update available · " + note
                    : _nodeMgrChecking ? CategoryName(def.Category)
                    : inLib ? CategoryName(def.Category) + " · up to date"
                    : CategoryName(def.Category) + " · local";
                r.Text(nf, r.Ellipsize(nf, meta, row.W - 230), new Vector2(row.X + 72, row.Y + 42), hasUpdate ? Theme.Warn : Theme.TextFaint);

                var upR = new RectF(row.Right - 12 - 96, row.Center.Y - 14, 96, 28);
                if (hasUpdate)
                {
                    bool up = _ui.Button("nm.up." + def.TypeId, upR, "⤓ Update", Theme.Amber);
                    if (up && rect.Contains(In.Mouse)) UpdateNode(def.TypeId);
                }

                if (hover && In.LeftPressed && !_nodeMgrJustOpened && !(hasUpdate && upR.Contains(In.Mouse)))
                {
                    if (selected) _nodeMgrSel.Remove(def.TypeId); else _nodeMgrSel.Add(def.TypeId);
                }
            }
            y += rowH;
        }
        r.End();
    }

    private void UpdatePaletteDrag(Renderer r)
    {
        if (_dragDef == null) return;
        if (!In.Active || Modal) { _dragDef = null; _dragging = false; return; } // cancel on focus loss / modal - never spawn a phantom node
        if (Vector2.Distance(In.Mouse, _dragStart) > 6) _dragging = true;

        if (!In.LeftDown)
        {
            bool spawned = false;
            if (_l.Canvas.Contains(In.Mouse)) { _editor.Spawn(_dragDef, _editor.Cam.ScreenToWorld(In.Mouse)); spawned = true; }
            else if (!_dragging) { _editor.Spawn(_dragDef, _editor.Cam.ScreenToWorld(_l.Canvas.Center)); spawned = true; }
            if (spawned) _app.MarkDirty();
            _dragDef = null; _dragging = false;
            return;
        }

        if (_dragging)
        {
            r.Begin();
            var col = Theme.Category(_dragDef.Category);
            var ghost = new RectF(In.Mouse.X - 90, In.Mouse.Y - 16, 180, 38);
            r.RoundFill(ghost, Theme.WithAlpha(Theme.PanelHi, 0.9f), 9f);
            r.RoundOutline(ghost, col, 9f);
            r.Fill(new RectF(ghost.X, ghost.Y + 6, 3, ghost.H - 12), col);
            r.Text(r.Fonts.Get(FontKind.SansBold, 13), _dragDef.Title, new Vector2(ghost.X + 14, ghost.Center.Y - 8), Theme.Text);
            r.End();
        }
    }

    // ===================================================================
    private void DrawInspector(Renderer r)
    {
        var p = _l.Inspector;
        var content = new RectF(p.X + 4, p.Y + Hud.HeaderH + 2, p.W - 8, p.H - Hud.HeaderH - 6);
        r.Begin(BlendMode.Alpha, content.ToRectangle());
        var sel = _editor.SelectedSingle;
        if (sel != null) DrawNodeInspector(r, content, sel);
        else DrawConnectionInspector(r, content);
        r.End();
    }

    /// <summary>A clickable "▸ Obby · advanced" header; returns the new expanded state. Advances y.</summary>
    private bool ObbyHeader(Renderer r, ref float y, float x, float w, bool expanded)
    {
        var hdr = new RectF(x, y, w, 24);
        bool hover = !Modal && hdr.Contains(In.Mouse);
        r.Text(r.Fonts.Get(FontKind.SansBold, 12), (expanded ? "▾  " : "▸  ") + "OBBY · ADVANCED (IRCv3 bot-tools)",
            new Vector2(x, y + 4), hover ? Theme.Text : Theme.TextFaint);
        y += 26;
        return hover && In.LeftPressed ? !expanded : expanded;
    }

    private void DrawNodeInspector(Renderer r, RectF c, Node n)
    {
        float x = c.X + 14, w = c.W - 28, y = c.Y + 14;
        var cat = Theme.Category(n.Def.Category);

        r.Disc(new Vector2(x + 5, y + 11), 5f, cat);
        r.Text(r.Fonts.Get(FontKind.SansBold, 18), r.Ellipsize(r.Fonts.Get(FontKind.SansBold, 18), n.DisplayTitle, w - 18), new Vector2(x + 18, y), Theme.Text);
        y += 26;
        // always show the underlying node type + category, even after a custom rename
        r.Text(r.Fonts.Get(FontKind.Mono, 11), n.Def.Title + " · " + n.Def.Category.ToString().ToLowerInvariant(), new Vector2(x + 18, y), Theme.WithAlpha(cat, 0.85f));
        y += 22;

        foreach (var line in Wrap(r.Fonts.Get(FontKind.Mono, 12), n.Def.Description, w))
        { r.Text(r.Fonts.Get(FontKind.Mono, 12), line, new Vector2(x, y), Theme.TextDim); y += 16; }
        y += 8;

        // custom label (blank = the catalog title above)
        r.Text(r.Fonts.Get(FontKind.SansBold, 12), "LABEL", new Vector2(x, y), Theme.TextDim); y += 18;
        var nt = _ui.TextField($"insp.title.{n.Id}", new RectF(x, y, w, 30), n.Title, n.Def.Title);
        if (nt != n.Title) { n.Title = nt; _app.MarkDirty(); }
        y += 38;

        // enable/disable (mute) - disabled nodes are skipped when the bot runs
        bool enabled = !n.Muted;
        bool ne = _ui.Toggle($"insp.mute.{n.Id}", new RectF(x, y, w, 26), enabled, enabled ? "node enabled" : "node muted (skipped)");
        if (ne != enabled) { _editor.PushUndo(); n.Muted = !ne; _app.MarkDirty(); }
        y += 32;

        // stream this node as an IRCv3 bot-tools workflow step (advanced; collapsed by default)
        _obbyNode = ObbyHeader(r, ref y, x, w, _obbyNode);
        if (_obbyNode)
        {
            bool st = _ui.Toggle($"insp.stream.{n.Id}", new RectF(x, y, w, 26), n.StreamAsTool, "stream as tool step");
            if (st != n.StreamAsTool) { n.StreamAsTool = st; _app.MarkDirty(); }
            y += 30;
        }
        y += 4;

        r.HLine(x, c.Right - 14, y, Theme.Hairline, 1f); y += 14;

        var lf = r.Fonts.Get(FontKind.SansBold, 12);
        foreach (var pdef in n.Def.Params)
        {
            if (pdef.VisibleWhen != null && !pdef.VisibleWhen(n)) continue;   // hide fields that don't apply (e.g. schedule mode)
            r.Text(lf, pdef.Label.ToUpperInvariant(), new Vector2(x, y), Theme.TextDim);
            y += 18;
            string cur = n.GetParam(pdef.Key);
            string id = $"p.{n.Id}.{pdef.Key}";
            string next = cur;
            switch (pdef.Type)
            {
                case ParamType.Multiline:
                    next = _ui.TextArea(id, new RectF(x, y, w, 76), cur, pdef.Placeholder); y += 86; break;
                case ParamType.Int:
                    next = _ui.IntField(id, new RectF(x, y, 110, 30), int.TryParse(cur, out var iv) ? iv : 0, 0, 1000000).ToString(); y += 40; break;
                case ParamType.Bool:
                    bool b = cur is "true" or "1" or "yes";
                    next = _ui.Toggle(id, new RectF(x, y, w, 26), b, b ? "enabled" : "disabled") ? "true" : "false"; y += 36; break;
                case ParamType.Choice:
                    next = _ui.Choice(id, new RectF(x, y, w, 30), pdef.Choices ?? Array.Empty<string>(), cur); y += 40; break;
                default:
                    next = _ui.TextField(id, new RectF(x, y, w, 30), cur, pdef.Placeholder); y += 40; break;
            }
            if (next != cur) { n.SetParam(pdef.Key, next); _app.MarkDirty(); }
        }

        y += 8;
        if (!_tut.Active && _ui.Button("insp.del", new RectF(x, y, w, 32), "✕  DELETE NODE", Theme.Alert))
        { Bot.Graph.Remove(n); _editor.Selection.Clear(); _app.MarkDirty(); }
    }

    private void DrawConnectionInspector(Renderer r, RectF c)
    {
        var s = Bot.Settings;
        float x = c.X + 14, w = c.W - 28, y = c.Y + 14;

        y = Labeled(r, "BOT NAME", x, y);
        var nm = _ui.TextField("c.name", new RectF(x, y, w, 30), Bot.Name, "bot name");
        if (nm != Bot.Name) { Bot.Name = string.IsNullOrWhiteSpace(nm) ? Bot.Name : nm; _app.MarkDirty(); }
        y += 42;

        r.Text(r.Fonts.Get(FontKind.SansBold, 16), "IRC Connection", new Vector2(x, y), Theme.Text); y += 24;
        var (slabel, scol, _) = StatusInfo();
        Hud.SoftDot(r, new Vector2(x + 5, y + 7), 4f, scol);
        r.Text(r.Fonts.Get(FontKind.Mono, 12), slabel, new Vector2(x + 16, y), scol); y += 22;
        if (Bot.Runtime.EnabledCaps.Count > 0)
            foreach (var line in Wrap(r.Fonts.Get(FontKind.Mono, 10), "caps: " + string.Join(' ', Bot.Runtime.EnabledCaps), w))
            { r.Text(r.Fonts.Get(FontKind.Mono, 10), line, new Vector2(x, y), Theme.TextFaint); y += 13; }
        y += 6;
        r.HLine(x, c.Right - 14, y, Theme.Hairline, 1f); y += 12;

        y = Labeled(r, "SERVER", x, y);
        var host = _ui.TextField("c.host", new RectF(x, y, w - 78, 30), s.Host, "irc.libera.chat");
        var port = _ui.IntField("c.port", new RectF(x + w - 70, y, 70, 30), s.Port, 1, 65535);
        if (host != s.Host) { s.Host = host; _app.MarkDirty(); }
        if (port != s.Port) { s.Port = port; _app.MarkDirty(); }
        y += 40;

        r.Text(r.Fonts.Get(FontKind.SansBold, 12), "SECURITY", new Vector2(x, y), Theme.TextDim); y += 18;
        var tls = _ui.Toggle("c.tls", new RectF(x, y, w, 24), s.UseTls, "Use TLS"); if (tls != s.UseTls) { s.UseTls = tls; _app.MarkDirty(); } y += 28;
        var cert = _ui.Toggle("c.cert", new RectF(x, y, w, 24), s.AcceptInvalidCerts, "Accept self-signed"); if (cert != s.AcceptInvalidCerts) { s.AcceptInvalidCerts = cert; _app.MarkDirty(); } y += 28;
        var recon = _ui.Toggle("c.recon", new RectF(x, y, w, 24), s.AutoReconnect, "Auto-reconnect"); if (recon != s.AutoReconnect) { s.AutoReconnect = recon; _app.MarkDirty(); } y += 34;

        y = Labeled(r, "NICK", x, y); s.Nick = Edit("c.nick", new RectF(x, y, w, 30), s.Nick, "ircuitry-bot"); y += 40;
        y = Labeled(r, "CHANNELS", x, y); s.Channels = Edit("c.chan", new RectF(x, y, w, 30), s.Channels, "#chan1 #chan2"); y += 40;
        y = Labeled(r, "SASL ACCOUNT (optional)", x, y); s.SaslUser = Edit("c.sasluser", new RectF(x, y, w, 30), s.SaslUser, "account"); y += 40;
        y = Labeled(r, "SASL PASSWORD", x, y); s.SaslPass = Edit("c.saslpass", new RectF(x, y, w, 30), s.SaslPass, "", password: true); y += 44;

        _obbyConn = ObbyHeader(r, ref y, x, w, _obbyConn);
        if (_obbyConn)
        {
            var bm = _ui.Toggle("c.botmode", new RectF(x, y, w, 24), s.BotMode, "Bot mode +B"); if (bm != s.BotMode) { s.BotMode = bm; _app.MarkDirty(); } y += 28;
            var adv = _ui.Toggle("c.adv", new RectF(x, y, w, 24), s.AdvertiseCommands, "Advertise slash commands"); if (adv != s.AdvertiseCommands) { s.AdvertiseCommands = adv; _app.MarkDirty(); } y += 28;
            var sw = _ui.Toggle("c.sw", new RectF(x, y, w, 24), s.StreamWorkflows, "Stream tool workflows"); if (sw != s.StreamWorkflows) { s.StreamWorkflows = sw; _app.MarkDirty(); } y += 30;
        }
        y += 6;

        if (_ui.Button("c.run", new RectF(x, y, w, 34), Bot.Runtime.Running ? "■  STOP BOT" : "▶  RUN BOT", Bot.Runtime.Running ? Theme.Alert : Theme.Cyan, primary: true))
            ToggleRun();
    }

    private string Edit(string id, RectF rect, string value, string ph, bool password = false)
    {
        var next = _ui.TextField(id, rect, value, ph, password: password);
        if (next != value) _app.MarkDirty();
        return next;
    }

    private float Labeled(Renderer r, string label, float x, float y)
    { r.Text(r.Fonts.Get(FontKind.SansBold, 12), label, new Vector2(x, y), Theme.TextDim); return y + 18; }

    // ===================================================================
    private void DrawConsole(Renderer r)
    {
        var p = _l.Console;
        var content = new RectF(p.X + 10, p.Y + Hud.HeaderH + 6, p.W - 20, p.H - Hud.HeaderH - 12);
        r.Begin(BlendMode.Alpha, content.ToRectangle());
        var mono = r.Fonts.Get(FontKind.Mono, 13);
        float lineH = mono.MeasureString("M").Y + 4f;
        int fit = Math.Max(1, (int)(content.H / lineH));
        var lines = Bot.Log.Tail(fit);
        float y = content.Bottom - lines.Count * lineH;
        foreach (var e in lines)
        {
            var col = LogColors.Of(e.Level);
            r.Text(mono, e.Time.ToString("HH:mm:ss"), new Vector2(content.X, y), Theme.TextFaint);
            r.Text(r.Fonts.Get(FontKind.SansBold, 12), LogColors.Tag(e.Level), new Vector2(content.X + 76, y + 1), col);
            string text = r.Ellipsize(mono, e.Text, content.W - 130);
            r.Text(mono, text, new Vector2(content.X + 118, y), e.Level == LogLevel.In ? Theme.TextDim : col);
            y += lineH;
        }
        r.End();
    }

    private void ConsoleHeaderStats(Renderer r, RectF p)
    {
        var f = r.Fonts.Get(FontKind.Mono, 12);
        r.TextRight(f, $"MSG {Bot.Runtime.MessagesSeen}   ACT {Bot.Runtime.ActionsFired}", p.Right - 14, p.Y + 11, Theme.TextFaint);
    }

    // ===================================================================
    private void TopBar(Renderer r, RectF bar, Clock clock)
    {
        r.Fill(bar, Theme.PanelHi);
        r.HLine(0, bar.W, bar.Bottom, Theme.WithAlpha(Theme.Cyan, 0.6f), 1.5f);
        r.HLine(0, bar.W, bar.Bottom + 1.5f, Theme.WithAlpha(Theme.Cyan, 0.12f), 3f);

        float bx = 20;
        if (r.Brand != null)
        {
            float isz = MathF.Min(36f, bar.H - 12f);
            r.Image(r.Brand, new RectF(bx, (bar.H - isz) / 2f, isz, isz));
            bx += isz + 10;
        }
        var brand = r.Fonts.Get(FontKind.Display, 30);
        var bsz = brand.MeasureString("ircuitry");
        r.Text(brand, "ircuitry", new Vector2(bx, (bar.H - bsz.Y) / 2f - 1), Theme.Mix(Theme.Cyan, Theme.Text, 0.3f));
        r.Text(r.Fonts.Get(FontKind.Sans, 13), "· IRCv3 Bot Bakery", new Vector2(bx + bsz.X + 14, bar.H / 2f - 8), Theme.Mix(Theme.Amber, Theme.Text, 0.25f));

        var pf = r.Fonts.Get(FontKind.SansBold, 14);
        r.TextCenteredX(pf, _app.ProjectName + (_app.Dirty ? " *" : ""), bar.W / 2f, bar.H / 2f - 8, Theme.Text);

        var time = DateTime.Now.ToString("HH:mm:ss");
        var tf = r.Fonts.Get(FontKind.SansBold, 16);
        r.TextRight(tf, time, bar.W - 22, bar.H / 2f - 9, Theme.TextDim);

        var (label, col, pulse) = StatusInfo();
        float clockW = tf.MeasureString(time).X;
        var pillRect = new RectF(bar.W - 22 - clockW - 16 - 150 - 12 - 150, 14, 150, 28);
        Hud.Pill(r, pillRect, label, col, clock, pulse);
    }

    private void RunButton(Renderer r, Clock clock)
    {
        var bar = _l.TopBar;
        var tf = r.Fonts.Get(FontKind.SansBold, 16);
        float clockW = tf.MeasureString(DateTime.Now.ToString("HH:mm:ss")).X;
        var rect = new RectF(bar.W - 22 - clockW - 16 - 150, 12, 150, 32);
        bool running = Bot.Runtime.Running;
        if (_ui.Button("top.run", rect, running ? "■ STOP BOT" : "▶ RUN BOT", running ? Theme.Alert : Theme.Cyan, primary: true))
            ToggleRun();
    }

    private void ApplyButton(Renderer r)
    {
        if (!Bot.Runtime.Running) return;   // only meaningful on a live bot
        var bar = _l.TopBar;
        var tf = r.Fonts.Get(FontKind.SansBold, 16);
        float clockW = tf.MeasureString(DateTime.Now.ToString("HH:mm:ss")).X;
        float runX = bar.W - 22 - clockW - 16 - 150;
        float histX = runX - 12 - 128;
        var rect = new RectF(histX - 12 - 116, 12, 116, 32);
        if (_ui.Button("top.apply", rect, "⟲ APPLY", Theme.Ok, primary: true))
            Bot.Runtime.ApplyGraph(Bot.Graph);
    }

    private void HistoryButton(Renderer r)
    {
        var bar = _l.TopBar;
        var tf = r.Fonts.Get(FontKind.SansBold, 16);
        float clockW = tf.MeasureString(DateTime.Now.ToString("HH:mm:ss")).X;
        float runX = bar.W - 22 - clockW - 16 - 150;
        int count = Bot.Runtime.HistoryCount;
        var rect = new RectF(runX - 12 - 128, 12, 128, 32);
        if (_ui.Button("top.hist", rect, count > 0 ? $"⟲ HISTORY {count}" : "⟲ HISTORY", Theme.Amber))
            OpenHistory();
    }

    private void StatusBar(Renderer r, RectF bar, Clock clock)
    {
        r.Fill(bar, Theme.PanelLo);
        r.HLine(0, bar.W, bar.Y, Theme.Hairline, 1f);
        var f = r.Fonts.Get(FontKind.SansBold, 12);
        float y = bar.Y + (bar.H - f.MeasureString("R").Y) / 2f;

        var (slabel, scol, _) = StatusInfo();
        Hud.SoftDot(r, new Vector2(16, bar.Center.Y), 3.5f, scol);
        r.Text(f, slabel, new Vector2(28, y), scol);
        r.Text(f, $"BOTS {_app.Bots.Count}", new Vector2(210, y), Theme.TextDim);
        r.Text(f, $"NODES {Bot.Graph.Nodes.Count}", new Vector2(300, y), Theme.TextDim);
        r.Text(f, $"WIRES {Bot.Graph.Connections.Count}", new Vector2(400, y), Theme.TextDim);
        r.TextRight(f, "dbl-click=add · drag ports=wire · Ctrl+Z undo · Ctrl+C/V/D copy · M mute · Ctrl+H history · MMB/Space=pan", bar.W - 16, y, Theme.TextFaint);
    }

    // ===================================================================
    private (string label, Color color, bool pulse) StatusInfo()
    {
        var rt = Bot.Runtime;
        return rt.State switch
        {
            IrcState.Connecting => ("CONNECTING", Theme.Warn, true),
            IrcState.Registering => ("REGISTERING", Theme.Warn, true),
            IrcState.Connected => ("LIVE ▸ " + rt.CurrentNick, Theme.Ok, true),
            IrcState.Error => ("ERROR", Theme.Alert, false),
            _ => rt.Running ? ("STARTING", Theme.Warn, true) : ("OFFLINE", Theme.Idle, false),
        };
    }

    private static Color StatusColor(BotRuntime rt) => rt.State switch
    {
        IrcState.Connected => Theme.Ok,
        IrcState.Connecting or IrcState.Registering => Theme.Warn,
        IrcState.Error => Theme.Alert,
        _ => rt.Running ? Theme.Warn : Theme.Idle,
    };

    private void ToggleRun()
    {
        if (Bot.Runtime.Running) Bot.Runtime.Stop();
        else Bot.Runtime.Start(Bot.Graph, Bot.Settings);
    }

    private static List<string> Wrap(DynamicSpriteFont f, string text, float maxW)
    {
        var lines = new List<string>();
        var cur = "";
        foreach (var word in text.Split(' '))
        {
            string test = cur.Length == 0 ? word : cur + " " + word;
            if (f.MeasureString(test).X <= maxW) cur = test;
            else { if (cur.Length > 0) lines.Add(cur); cur = word; }
        }
        if (cur.Length > 0) lines.Add(cur);
        return lines;
    }

    // ===================================================================
    //  Run History viewer
    // ===================================================================
    private void DrawHistoryModal(Renderer r, Clock clock)
    {
        r.Begin();
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.5f));
        float pw = MathF.Min(1120, _vw * 0.9f), ph = MathF.Min(760, _vh * 0.88f);
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        Hud.Panel(r, panel, "Run History", Theme.Amber);
        r.TextRight(r.Fonts.Get(FontKind.Mono, 12), $"{_historyRuns.Count} runs captured (max 1000)", panel.Right - 18, panel.Y + 13, Theme.TextFaint);
        r.End();

        float top = panel.Y + Hud.HeaderH + 12;
        float btnY = panel.Bottom - 46;
        var listRect = new RectF(panel.X + 16, top, 332, btnY - top - 10);
        var detailRect = new RectF(listRect.Right + 14, top, panel.Right - 16 - (listRect.Right + 14), btnY - top - 10);

        r.Begin();
        r.RoundFill(listRect, Theme.PanelLo, 8); r.RoundOutline(listRect, Theme.Edge, 8);
        r.RoundFill(detailRect, Theme.Panel, 8); r.RoundOutline(detailRect, Theme.Edge, 8);
        r.End();

        if (_historyRuns.Count == 0)
        {
            r.Begin();
            r.TextCenteredX(r.Fonts.Get(FontKind.SansBold, 16), "No runs recorded yet", panel.Center.X, panel.Center.Y - 18, Theme.TextDim);
            r.TextCenteredX(r.Fonts.Get(FontKind.Sans, 13), "Run the bot and trigger an event - every fire is captured here with its data.", panel.Center.X, panel.Center.Y + 8, Theme.TextFaint);
            r.End();
        }
        else { DrawHistoryList(r, listRect); DrawHistoryDetail(r, detailRect); }

        r.Begin();
        var closeR = new RectF(panel.Right - 16 - 110, btnY, 110, 34);
        var clearR = new RectF(closeR.X - 12 - 110, btnY, 110, 34);
        if (_ui.Button("hist.close", closeR, "CLOSE", Theme.Cyan, primary: true)) _historyOpen = false;
        if (_ui.Button("hist.clear", clearR, "CLEAR", Theme.Idle)) { Bot.Runtime.ClearHistory(); _historyRuns.Clear(); _historySel = -1; }
        r.End();

        if (In.LeftPressed && !panel.Contains(In.Mouse) && !_historyJustOpened) _historyOpen = false;
        _historyJustOpened = false;
    }

    private void DrawHistoryList(Renderer r, RectF rect)
    {
        const float rowH = 50f;
        float total = _historyRuns.Count * rowH;
        if (rect.Contains(In.Mouse) && In.ScrollDelta != 0) _historyListScroll -= In.ScrollDelta;
        _historyListScroll = Math.Clamp(_historyListScroll, 0, MathF.Max(0, total - rect.H));

        r.Begin(BlendMode.Alpha, rect.ToRectangle());
        var tf = r.Fonts.Get(FontKind.SansBold, 13);
        var sf = r.Fonts.Get(FontKind.Mono, 11);
        var icf = r.Fonts.Get(FontKind.Display, 16);
        float y = rect.Y - _historyListScroll;
        for (int i = 0; i < _historyRuns.Count; i++)
        {
            var run = _historyRuns[i];
            var row = new RectF(rect.X + 4, y + 3, rect.W - 8, rowH - 6);
            if (row.Bottom >= rect.Y && row.Y <= rect.Bottom)
            {
                bool seld = i == _historySel;
                bool hover = row.Contains(In.Mouse) && rect.Contains(In.Mouse);
                r.RoundFill(row, seld ? Theme.Mix(Theme.Panel, Theme.Amber, 0.28f) : hover ? Theme.PanelHi : Theme.Panel, 6);
                if (seld) r.RoundOutline(row, Theme.WithAlpha(Theme.Amber, 0.8f), 6);
                r.Text(icf, run.Icon, new Vector2(row.X + 8, row.Y + 6), Theme.Amber);
                r.Text(tf, r.Ellipsize(tf, run.Trigger, row.W - 90), new Vector2(row.X + 34, row.Y + 6), Theme.Text);
                string sum = run.Summary.Length == 0 ? "-" : run.Summary.Replace("\n", " ");
                r.Text(sf, r.Ellipsize(sf, sum, row.W - 44), new Vector2(row.X + 34, row.Y + 26), Theme.TextDim);
                r.TextRight(sf, run.Time.ToString("HH:mm:ss"), row.Right - 8, row.Y + 6, Theme.TextFaint);
                if (run.Actions > 0) r.TextRight(sf, run.Actions + "⚡", row.Right - 8, row.Y + 26, Theme.WithAlpha(Theme.Ok, 0.95f));
                if (hover && In.LeftPressed) { _historySel = i; _historyDetailScroll = 0; }
            }
            y += rowH;
        }
        r.End();
    }

    private void DrawHistoryDetail(Renderer r, RectF rect)
    {
        if (_historySel < 0 || _historySel >= _historyRuns.Count) return;
        DrawRunDetail(r, rect, _historyRuns[_historySel], ref _historyDetailScroll);
    }

    /// <summary>Render a RunRecord's node-by-node I/O (shared by Run History and the Test Bench).</summary>
    private void DrawRunDetail(Renderer r, RectF rect, RunRecord run, ref float scroll)
    {
        if (rect.Contains(In.Mouse) && In.ScrollDelta != 0) scroll -= In.ScrollDelta;

        r.Begin(BlendMode.Alpha, rect.ToRectangle());
        float pad = 14, x = rect.X + pad, w = rect.W - pad * 2;
        float y = rect.Y + pad - scroll;
        var lf = r.Fonts.Get(FontKind.Mono, 12);
        var nf = r.Fonts.Get(FontKind.SansBold, 13);

        r.Text(r.Fonts.Get(FontKind.Display, 20), run.Icon, new Vector2(x, y), Theme.Amber);
        r.Text(r.Fonts.Get(FontKind.SansBold, 16), run.Trigger, new Vector2(x + 30, y + 2), Theme.Text);
        if (run.Summary.Length > 0)
            r.Text(lf, r.Ellipsize(lf, run.Summary.Replace("\n", " "), w - 30), new Vector2(x + 30, y + 24), Theme.TextDim);
        r.Text(lf, run.Nodes.Count + " nodes   ·   " + run.Actions + " actions", new Vector2(x, y + 44), Theme.TextFaint);
        y += 66;
        r.HLine(x, rect.Right - pad, y, Theme.Hairline, 1f); y += 12;

        foreach (var nt in run.Nodes)
        {
            float ch = 28 + nt.Inputs.Count * 16 + nt.Outputs.Count * 16 + (nt.Pulsed.Count > 0 ? 18 : 0) + 8;
            var card = new RectF(x, y, w, ch);
            if (card.Bottom >= rect.Y && card.Y <= rect.Bottom)
            {
                r.RoundFill(card, Theme.PanelLo, 7);
                float cy = y + 6;
                r.Text(r.Fonts.Get(FontKind.Display, 14), nt.Icon, new Vector2(x + 8, cy), Theme.Cyan);
                r.Text(nf, nt.Title, new Vector2(x + 30, cy + 1), Theme.Text);
                cy += 22;
                if (nt.Pulsed.Count > 0) { r.Text(lf, "▸ " + string.Join(", ", nt.Pulsed), new Vector2(x + 12, cy), Theme.WithAlpha(Theme.Ok, 0.95f)); cy += 18; }
                foreach (var (pin, val) in nt.Inputs) { DrawIOLine(r, lf, x + 12, cy, w - 24, "in ▸ " + pin, val, Theme.CyanDim); cy += 16; }
                foreach (var (pin, val) in nt.Outputs) { DrawIOLine(r, lf, x + 12, cy, w - 24, "out ◂ " + pin, val, Theme.AmberDim); cy += 16; }
            }
            y += ch + 8;
        }
        float contentH = (y + scroll) - rect.Y + 8;
        r.End();
        scroll = Math.Clamp(scroll, 0, MathF.Max(0, contentH - rect.H));
    }

    private void DrawIOLine(Renderer r, DynamicSpriteFont f, float x, float y, float w, string label, string val, Color lc)
    {
        r.Text(f, label, new Vector2(x, y), lc);
        string v = val.Length == 0 ? "∅" : val.Replace("\n", " ⏎ ");
        r.Text(f, r.Ellipsize(f, v, w - 96), new Vector2(x + 92, y), Theme.Text);
    }

    // ===================================================================
    //  Quick-add (double-click canvas)
    // ===================================================================
    private void DrawQuickAdd(Renderer r)
    {
        const float pw = 304, ph = 348;
        float px = Math.Clamp(_quickScreen.X, 8, _vw - pw - 8);
        float py = Math.Clamp(_quickScreen.Y, 8, _vh - ph - 8);
        var panel = new RectF(px, py, pw, ph);

        r.Begin();
        r.RoundFill(panel.Offset(0, 4), Theme.WithAlpha(Color.Black, 0.18f), 10);
        r.RoundFill(panel, Theme.Panel, 10);
        r.RoundOutline(panel, Theme.WithAlpha(Theme.Cyan, 0.85f), 10);
        r.Text(r.Fonts.Get(FontKind.SansBold, 13), "✚  Add node", new Vector2(panel.X + 14, panel.Y + 10), Theme.Text);
        r.End();

        float x = panel.X + 12, w = panel.W - 24, y = panel.Y + 32;
        r.Begin();
        _quickSearch = _ui.TextField("quick.search", new RectF(x, y, w, 30), _quickSearch, "⌕  search nodes…");
        r.End();
        y += 38;

        var listRect = new RectF(x, y, w, panel.Bottom - 12 - y);
        var matches = NodeCatalog.All.Where(d => QuickMatch(d, _quickSearch)).ToList();
        const float rowH = 30f;
        float total = matches.Count * rowH;
        if (listRect.Contains(In.Mouse) && In.ScrollDelta != 0) _quickScroll -= In.ScrollDelta;
        _quickScroll = Math.Clamp(_quickScroll, 0, MathF.Max(0, total - listRect.H));

        r.Begin(BlendMode.Alpha, listRect.ToRectangle());
        var tf = r.Fonts.Get(FontKind.Sans, 13);
        var icf = r.Fonts.Get(FontKind.Display, 15);
        var cf = r.Fonts.Get(FontKind.Mono, 10);
        float ry = listRect.Y - _quickScroll;
        foreach (var def in matches)
        {
            var row = new RectF(listRect.X, ry, listRect.W, rowH - 2);
            if (row.Bottom >= listRect.Y && row.Y <= listRect.Bottom)
            {
                bool hover = row.Contains(In.Mouse) && listRect.Contains(In.Mouse);
                var col = Theme.Category(def.Category);
                if (hover) r.RoundFill(row, Theme.PanelHi, 6);
                r.Text(icf, def.Icon, new Vector2(row.X + 6, row.Y + 4), col);
                r.Text(tf, r.Ellipsize(tf, def.Title, row.W - 84), new Vector2(row.X + 30, row.Y + 6), Theme.Text);
                r.TextRight(cf, def.Category.ToString().ToLowerInvariant(), row.Right - 8, row.Y + 8, Theme.WithAlpha(col, 0.9f));
                if (hover && In.LeftPressed) { _editor.Spawn(def, _quickWorld); _app.MarkDirty(); _quickOpen = false; }
            }
            ry += rowH;
        }
        r.End();

        if (In.LeftPressed && !panel.Contains(In.Mouse) && !_quickJustOpened) _quickOpen = false;
        _quickJustOpened = false;
    }

    // ===================================================================
    //  Test bench - fire the current graph against a fake event, with no IRC
    // ===================================================================
    private void RunTest()
    {
        _testRunSeq++;   // tutorial watches this to know the user actually ran a test
        _testSent.Clear(); _testRec = null; _testScroll = 0;
        var graph = Bot.Graph;
        var sink = new TestSink(new Dictionary<string, string>(Bot.State));   // throwaway state copy
        var baseVars = new Dictionary<string, string>
        {
            ["botnick"] = Bot.Settings.Nick, ["nick"] = _testNick, ["user"] = "tester", ["host"] = "test.host",
            ["channel"] = _testChan, ["target"] = _testChan, ["message"] = _testMsg, ["replyto"] = _testChan,
            ["args"] = "", ["command"] = "", ["account"] = "", ["isbot"] = "false",
            ["msgid"] = "test-msg", ["__reply"] = "test-msg",
        };
        RunRecord? fired = null;
        foreach (var node in graph.Nodes)
        {
            if (node.Def.TriggerEvent != "message") continue;
            var rec = new RunRecord { Time = DateTime.Now, Trigger = node.DisplayTitle, Icon = node.Def.Icon, Summary = _testMsg };
            GraphExecutor.Fire(graph, sink, node, new Dictionary<string, string>(baseVars), rec);
            rec.Fired = rec.Nodes.Count > 0 && rec.Nodes[0].Pulsed.Count > 0;
            rec.Actions = sink.Sent.Count;
            if (rec.Fired && fired == null) fired = rec;
        }
        _testRec = fired;
        _testSent.AddRange(sink.Sent);
    }

    private void DrawTestModal(Renderer r)
    {
        r.Begin();
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.5f));
        float pw = MathF.Min(1040, _vw * 0.9f), ph = MathF.Min(660, _vh * 0.9f);
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        Hud.Panel(r, panel, "Test Bench - dry run, no IRC", Theme.Cyan);
        r.End();

        float top = panel.Y + Hud.HeaderH + 14, btnY = panel.Bottom - 46;
        float fx = panel.X + 18, fw = 290;
        var formRect = new RectF(fx, top, fw, btnY - top - 10);
        var rightX = formRect.Right + 16;
        var sentRect = new RectF(rightX, top, panel.Right - 18 - rightX, 150);
        var traceRect = new RectF(rightX, sentRect.Bottom + 10, sentRect.W, btnY - 10 - (sentRect.Bottom + 10));

        // ---- left form ----
        r.Begin();
        float y = formRect.Y;
        r.Text(r.Fonts.Get(FontKind.SansBold, 12), "MESSAGE", new Vector2(fx, y), Theme.TextDim); y += 18;
        _testMsg = _ui.TextField("test.msg", new RectF(fx, y, fw, 30), _testMsg, "!ping"); y += 40;
        r.Text(r.Fonts.Get(FontKind.SansBold, 12), "FROM NICK", new Vector2(fx, y), Theme.TextDim); y += 18;
        _testNick = _ui.TextField("test.nick", new RectF(fx, y, fw, 30), _testNick, "alice"); y += 40;
        r.Text(r.Fonts.Get(FontKind.SansBold, 12), "CHANNEL", new Vector2(fx, y), Theme.TextDim); y += 18;
        _testChan = _ui.TextField("test.chan", new RectF(fx, y, fw, 30), _testChan, "#test"); y += 42;
        if (_ui.Button("test.run", new RectF(fx, y, fw, 36), "▶  RUN", Theme.Ok, primary: true)) RunTest();
        y += 46;
        foreach (var line in Wrap(r.Fonts.Get(FontKind.Sans, 12), "Fires every On Message / On Command node against this fake event. Nothing is sent and your variables aren't touched.", fw))
        { r.Text(r.Fonts.Get(FontKind.Sans, 12), line, new Vector2(fx, y), Theme.TextFaint); y += 16; }
        r.End();

        // ---- right: what it would send ----
        r.Begin();
        r.RoundFill(sentRect, Theme.PanelLo, 8); r.RoundOutline(sentRect, Theme.Edge, 8);
        r.Text(r.Fonts.Get(FontKind.SansBold, 12), $"WOULD SEND  ({_testSent.Count})", new Vector2(sentRect.X + 12, sentRect.Y + 8), Theme.TextDim);
        r.End();
        r.Begin(BlendMode.Alpha, new RectF(sentRect.X, sentRect.Y + 28, sentRect.W, sentRect.H - 34).ToRectangle());
        var mono = r.Fonts.Get(FontKind.Mono, 12);
        float sy = sentRect.Y + 30;
        if (_testSent.Count == 0) r.Text(r.Fonts.Get(FontKind.Sans, 13), "nothing sent", new Vector2(sentRect.X + 14, sy + 2), Theme.TextFaint);
        foreach (var (kind, text) in _testSent)
        {
            r.Text(r.Fonts.Get(FontKind.SansBold, 11), kind, new Vector2(sentRect.X + 12, sy + 1), Theme.WithAlpha(Theme.Cyan, 0.95f));
            r.Text(mono, r.Ellipsize(mono, text, sentRect.W - 92), new Vector2(sentRect.X + 80, sy), Theme.Text);
            sy += 18;
        }
        r.End();

        // ---- right: node-by-node trace ----
        r.Begin();
        r.RoundFill(traceRect, Theme.Panel, 8); r.RoundOutline(traceRect, Theme.Edge, 8);
        r.End();
        if (_testRec != null) DrawRunDetail(r, traceRect, _testRec, ref _testScroll);
        else { r.Begin(); r.TextCenteredX(r.Fonts.Get(FontKind.Sans, 13), "No command/message node fired for that input.", traceRect.Center.X, traceRect.Center.Y - 8, Theme.TextFaint); r.End(); }

        r.Begin();
        if (_ui.Button("test.close", new RectF(panel.Right - 18 - 100, btnY, 100, 34), "CLOSE", Theme.Cyan, primary: true)) _testOpen = false;
        r.End();

        if (In.LeftPressed && !panel.Contains(In.Mouse) && !_testJustOpened) _testOpen = false;
        _testJustOpened = false;
    }

    // ===================================================================
    //  Secrets vault editor - keys kept out of the workspace, exports and clipboard
    // ===================================================================
    private void DrawSecretsModal(Renderer r)
    {
        var names = Ircuitry.Core.Secrets.Names();
        float pw = 560, ph = 196 + Math.Min(names.Count, 6) * 34 + 60;
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);

        r.Begin();   // one batch: panel chrome, text, and all _ui widgets draw into it
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.5f));
        Hud.Panel(r, panel, "Secrets", Theme.Violet);

        float x = panel.X + 22, w = panel.W - 44, y = panel.Y + Hud.HeaderH + 14;
        foreach (var line in Wrap(r.Fonts.Get(FontKind.Sans, 12), "Reference these anywhere as {{secret.name}} - e.g. an Ask AI key or SASL password. They live in ~/ircuitry/secrets.json and never go into the workspace, exports, or copied nodes.", w))
        { r.Text(r.Fonts.Get(FontKind.Sans, 12), line, new Vector2(x, y), Theme.TextDim); y += 16; }
        y += 8;

        var mono = r.Fonts.Get(FontKind.Mono, 13);
        foreach (var name in names)
        {
            var row = new RectF(x, y, w, 30);
            r.RoundFill(row, Theme.PanelLo, 6);
            r.Text(r.Fonts.Get(FontKind.SansBold, 13), name, new Vector2(row.X + 10, row.Y + 7), Theme.Text);
            int len = Ircuitry.Core.Secrets.Get(name).Length;
            r.Text(mono, new string('•', Math.Clamp(len, 4, 12)) + "  ({{secret." + name + "}})", new Vector2(row.X + 150, row.Y + 7), Theme.TextFaint);
            if (_ui.Button("sec.del." + name, new RectF(row.Right - 70, row.Y + 3, 64, 24), "DELETE", Theme.Alert))
                Ircuitry.Core.Secrets.Delete(name);
            y += 34;
        }
        if (names.Count == 0) { r.Text(r.Fonts.Get(FontKind.Sans, 13), "No secrets yet.", new Vector2(x, y), Theme.TextFaint); y += 26; }

        // add row
        y += 8;
        _secretName = _ui.TextField("sec.name", new RectF(x, y, 160, 30), _secretName, "name");
        _secretValue = _ui.TextField("sec.val", new RectF(x + 168, y, w - 168 - 80, 30), _secretValue, "value", password: true);
        if (_ui.Button("sec.add", new RectF(panel.Right - 22 - 72, y, 72, 30), "ADD", Theme.Ok, primary: true) && _secretName.Trim().Length > 0)
        {
            Ircuitry.Core.Secrets.Set(_secretName.Trim(), _secretValue);
            _secretName = ""; _secretValue = "";
        }

        if (_ui.Button("sec.close", new RectF(panel.Right - 22 - 100, panel.Bottom - 46, 100, 32), "CLOSE", Theme.Cyan, primary: true)) _secretsOpen = false;
        r.End();

        if (In.LeftPressed && !panel.Contains(In.Mouse) && !_secretsJustOpened) _secretsOpen = false;
        _secretsJustOpened = false;
    }

    private static bool QuickMatch(NodeDef d, string q)
    {
        if (q.Length == 0) return true;
        q = q.ToLowerInvariant();
        return d.Title.ToLowerInvariant().Contains(q)
            || d.Subtitle.ToLowerInvariant().Contains(q)
            || d.Category.ToString().ToLowerInvariant().Contains(q);
    }

    // ===================================================================
    //  New-bot template picker
    // ===================================================================
    private void DrawTemplateModal(Renderer r)
    {
        r.Begin();
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.45f));
        var tpl = AppModel.Templates;
        float pw = 560, ph = 150 + tpl.Length * 66;
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        Hud.Panel(r, panel, "New bot - pick a starter", Theme.Cyan);
        r.End();

        float x = panel.X + 22, w = panel.W - 44, y = panel.Y + Hud.HeaderH + 16;
        var tf = r.Fonts.Get(FontKind.SansBold, 15);
        var bf = r.Fonts.Get(FontKind.Sans, 12);
        var icf = r.Fonts.Get(FontKind.Display, 24);

        foreach (var t in tpl)
        {
            var row = new RectF(x, y, w, 58);
            bool hover = row.Contains(In.Mouse);
            r.Begin();
            r.RoundFill(row, hover ? Theme.Mix(Theme.Panel, Theme.Cyan, 0.18f) : Theme.PanelLo, 9);
            r.RoundOutline(row, hover ? Theme.WithAlpha(Theme.Cyan, 0.8f) : Theme.Edge, 9);
            r.Text(icf, t.Icon, new Vector2(row.X + 14, row.Y + 14), Theme.Cyan);
            r.Text(tf, t.Label, new Vector2(row.X + 58, row.Y + 10), Theme.Text);
            r.Text(bf, t.Blurb, new Vector2(row.X + 58, row.Y + 32), Theme.TextDim);
            r.End();
            if (hover && In.LeftPressed && !_templateJustOpened)
            {
                _app.AddBot(t.Key);
                _editor.Selection.Clear();
                _templateOpen = false;
            }
            y += 66;
        }

        r.Begin();
        if (_ui.Button("tpl.cancel", new RectF(panel.Right - 22 - 110, panel.Bottom - 46, 110, 32), "CANCEL", Theme.Idle))
            _templateOpen = false;
        r.End();

        if (In.LeftPressed && !panel.Contains(In.Mouse) && !_templateJustOpened) _templateOpen = false;
        _templateJustOpened = false;
    }
}
