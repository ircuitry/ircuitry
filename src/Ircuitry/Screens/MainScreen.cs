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
    // event console: scroll position. Sizing + resizing are owned by the dock system (drag its top border).
    private readonly DockManager _dock = new();
    private float _consoleScroll;
    private float _inspScroll;        // inspector panel scroll (the connection panel can run long)
    private string _inspKey = "";     // what the inspector is showing, to reset scroll on change
    private string _nodeTestId = "", _nodeTestResult = "";   // last "test this node" result
    private string _paletteSearch = "";
    private float _clipCheckAt = -1f;     // throttle clipboard polling
    private string? _clipNodeTitle;        // title of an installable .ircnode currently in the clipboard, or null
    private NodeCategory? _openCat;   // palette accordion: at most one category expanded (null = all collapsed)
    private readonly System.Collections.Generic.HashSet<string> _collapsedSections = new() { "Recent" };   // pinned/recent sections collapsed by title; Recent starts collapsed

    // import modal + graph-change tracking
    private bool _importOpen;
    private bool _importJustOpened;     // suppress the opening click from closing the modal
    private string[] _importFiles = Array.Empty<string>();
    private bool _snapOpen, _snapJustOpened;
    private string[] _snapFiles = Array.Empty<string>();
    private NodeGraph? _lastGraph;

    // delete-bot confirmation
    private Bot? _confirmDeleteBot;
    private bool _confirmJustOpened;

    // close prompt (window X -> exit / minimise)
    private bool _closePromptOpen, _closeJustOpened;
    public Action? OnExitRequested;
    public Action? OnMinimizeRequested;
    public void RequestClosePrompt()
    {
        // only stop to ask (offer to minimise instead) when bots are actually live; otherwise just quit
        if (_app.Bots.Any(b => b.Runtime.Running)) { _closePromptOpen = true; _closeJustOpened = true; }
        else OnExitRequested?.Invoke();
    }

    /// <summary>True while the "are you sure you want to quit?" prompt is showing (the host raises the window for it).</summary>
    public bool ClosePromptOpen => _closePromptOpen;

    public void DebugWorkflowInstall() { _l = DockLayout(); StageWorkflowInstall("{\"format\":\"ircuitry.workflow.v1\",\"name\":\"Greeter Bot\",\"description\":\"Welcomes people when they join your channel and answers a friendly !hi command.\",\"nodes\":[{\"id\":\"a\",\"type\":\"event.join\"},{\"id\":\"b\",\"type\":\"action.say\"}],\"connections\":[]}"); }

    public void DebugOpenBake()
    {
        _l = DockLayout();
        _editor.Selection.Clear();
        foreach (var n in _app.ActiveBot.Graph.Nodes.Where(n => !n.Def.IsTrigger).Take(2)) _editor.Selection.Add(n.Id);
        _saveNodeName = "Greeting Macro"; _saveNodeIcon = "puzzle-piece"; _saveNodeCat = "Action"; _saveNodeDesc = "";
        _saveNodeOpen = true; _saveNodeJustOpened = true;
    }

    // inline tab rename (double-click a tab)
    private Bot? _renamingBot;
    private float _tabClickTime;
    private Bot? _tabClickBot;
    private Bot? _tabDragBot; private bool _tabDragging; private float _tabDragDownX; private double _titleClickTime = -1;
    private float _tabDragGrabDx;                                  // where on the tab the cursor grabbed it (so it rides true)
    private readonly Dictionary<Bot, float> _tabDrawX = new();     // animated draw-x per tab, so neighbours slide while dragging
    private string? _pendingRenameFocus;                           // arm a rename field to grab focus on the frame it first draws
    // tab groups: inline-rename target, whole-group drag, and the group container rects laid out this frame (so a
    // tab dropped onto a group's coloured band joins it)
    private TabGroup? _renamingGroup;
    private TabGroup? _groupDragG; private bool _groupDragging; private float _groupDragDownX;
    private readonly List<(TabGroup g, RectF rect)> _groupRects = new();

    // run-history viewer
    private bool _historyOpen, _historyJustOpened;
    private List<RunRecord> _historyRuns = new();
    private int _historySel = -1;
    private float _historyListScroll, _historyDetailScroll;

    // secrets vault editor
    private bool _secretsOpen, _secretsJustOpened;
    private bool _secretPickOpen, _secretPickJustOpened;
    private Action<string>? _secretPickApply;
    private string _secretPickTitle = "", _secretPickName = "", _secretPickNewVal = "";
    private float _secretPickScroll;
    private bool _serversOpen, _serversJustOpened;
    private string _serverSaveName = "";
    private float _serversScroll;
    // irc:// / ircs:// link -> "save this server" (prompt only when one already exists)
    private bool _serverLinkOpen, _serverLinkJustOpened;
    private Ircuitry.Core.ServerProfile? _serverLinkProfile;
    private string _serverLinkExisting = "";
    private bool _networkOpen, _networkJustOpened;
    private float _networkScroll;
    // achievements
    private float _achLastTick = -1f, _achEvalAt = -1f;
    private readonly Queue<Ircuitry.Core.AchDef> _achToasts = new();
    // unified notifications: a cozy slide-in toast + a history dropdown of recent ones
    private readonly Queue<string> _toasts = new();
    private string? _toastCur;
    private float _toastUntil;
    private readonly List<(DateTime time, string text)> _notifLog = new();
    private bool _notifOpen, _notifJustOpened;
    private int _notifUnread;        // toasts shown since the history was last opened (for the bell badge)
    private float _notifScroll;
    // snippet shelf: reusable graph fragments (saved selections you drop back in)
    private bool _shelfOpen, _shelfDirty = true;
    private List<(string name, string path)> _shelfSnips = new();

    // error tray (#15): node-attributed runtime errors, jump-to-offender
    private bool _errTrayOpen;
    private float _errTrayScroll;

    // find-in-graph (Ctrl+F): live node search + jump-to-hit
    private bool _findOpen, _findArm;
    private string _findQuery = "";
    private readonly List<string> _findHits = new();
    private int _findIdx;

    // command palette (Ctrl+K): run any action or add any node by typing
    private bool _cmdkOpen, _cmdkJustOpened;
    private string _cmdkQuery = "";
    private int _cmdkSel;
    private float _cmdkScroll;
    private Ircuitry.Core.AchDef? _achCur;
    private float _achCurUntil;
    private bool _achOpen, _achJustOpened;
    private float _achScroll;
    private string _secretName = "", _secretValue = "";

    // "Obby" - advanced bot-tools controls, collapsed by default in the inspector
    private bool _obbyConn, _obbyNode;

    // test bench (dry-run without IRC)
    private bool _testOpen, _testJustOpened;
    private string _testMsg = "!ping", _testNick = "alice", _testChan = "#test";
    private readonly List<(string kind, string text)> _testSent = new();
    private RunRecord? _testRec;
    private float _testScroll;
    private string _testReplayNote = "";   // #17: when set, the test bench is showing a replayed recorded event

    // save-selection-as-reusable-node
    private bool _saveNodeOpen, _saveNodeJustOpened, _saveNodeAsTool;
    private string _saveNodeName = "My Node";
    private string _saveNodeIcon = "puzzle-piece", _saveNodeCat = "Logic", _saveNodeDesc = "";
    private static readonly string[] _bakeCats = { "Action", "Data", "Logic", "Ai", "Filter", "Storage" };

    // confirm installing a dropped community .ircnode (it runs code) before it's installed
    private bool _installOpen, _installJustOpened;
    private string _installPath = "", _installPreview = "";
    private string? _installText;   // set when installing from clipboard (write text) instead of a dropped file (copy)
    private Vector2 _installScreen;
    private NodeDef? _installDef;
    private bool _uninstallOpen, _uninstallJustOpened;
    private NodeDef? _uninstallDef;

    // confirm installing a community workflow (it becomes a new bot tab) before importing it
    private bool _wfInstallOpen, _wfInstallJustOpened;
    private string _wfInstallText = "", _wfInstallName = "", _wfInstallDesc = "";
    private int _wfInstallNodes;

    // community node manager
    private bool _nodeMgrOpen, _nodeMgrJustOpened;
    private float _nodeMgrScroll;
    private readonly HashSet<string> _nodeMgrSel = new();
    private volatile bool _nodeMgrChecking;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _nodeMgrUpdates = new();   // typeId -> changelog note
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _nodeMgrInLibrary = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _nodeMgrLatest = new();    // typeId -> latest manifest json

    // in-app updater
    private enum UpState { None, Available, Downloading, Applying, Failed }
    private enum InstallKind { AppImage, Deb, WinExe, Portable }   // how this build was installed -> how it updates
    private volatile UpState _upState = UpState.None;
    private InstallKind _upKind = InstallKind.Portable;
    private string _upVer = "", _upBody = "", _upAssetUrl = "", _upAssetName = "";
    private volatile string _upRelaunchPath = "";   // set by the worker; the game thread launches it then exits
    private bool _upPromptOpen, _upPromptJustOpened, _upPrompted;
    private volatile float _upProgress;
    private volatile string _upStatus = "";
    private volatile bool _upReady;     // worker finished install; game thread should relaunch + exit
    private float _upBodyScroll;
    private float _upCheckAt = -1f;     // last update check (re-checks every 6h)
    private string _upSeenVer = "";     // newest version we have already prompted for
    private bool UpAuto => _upKind is InstallKind.AppImage or InstallKind.Deb or InstallKind.WinExe;   // installs itself

    // new-bot template picker
    private bool _templateOpen, _templateJustOpened;

    // quick-add (double-click canvas)
    private bool _quickOpen, _quickJustOpened;
    private Vector2 _quickWorld, _quickScreen;
    private (string node, int pin)? _ghostWireFrom;   // when a quick-add was opened from a "+" ghost, auto-wire the result
    private string _quickSearch = "";
    private float _quickScroll;
    private float _lastClickTime;
    private Vector2 _lastClickPos;

    private bool Modal => _importOpen || _confirmDeleteBot != null || _historyOpen || _quickOpen || _templateOpen || _closePromptOpen || _secretsOpen || _testOpen || _ctxOpen || _saveNodeOpen || _installOpen || _wfInstallOpen || _uninstallOpen || _nodeMgrOpen || _upPromptOpen || _secretPickOpen || _serversOpen || _networkOpen || _achOpen || _snapOpen || _serverLinkOpen || _cmdkOpen || _nbOpen || _ircWinOpen || _bakeryOpen || _appearanceOpen || _themeInstallOpen || _remoteOpen || _staleRemote != null || _secretCopy.Count > 0 || BakeAnimActive
        || _upState == UpState.Downloading || _upState == UpState.Applying;

    public MainScreen(AppModel app)
    {
        _app = app;
        _editor = new GraphEditor(_app.ActiveBot.Graph)
        {
            FireGlow = id => { var b = _app.ActiveBot; return b.IsRemote ? b.Remote!.FireGlow(b.RemoteName, id) : b.Runtime.FireGlow(id); },
            FireCount = id => _app.ActiveBot.Runtime.FireCount(id),
            LastTrace = id => _app.ActiveBot.IsRemote ? null : _app.ActiveBot.Runtime.LastTraceFor(id),
            LastRun = () => _app.ActiveBot.IsRemote ? null : _app.ActiveBot.Runtime.LastRun,
            Notify = PushToast,
        };
        // dockable panels over a full-bleed map: Library left, Inspector right, Console bottom (hidden by default)
        _dock.Add(new DockManager.Panel { Id = "library", Dock = DockManager.Edge.Left, Size = Layout.PaletteW });
        _dock.Add(new DockManager.Panel { Id = "inspector", Dock = DockManager.Edge.Right, Size = Layout.InspectorW });
        _dock.Add(new DockManager.Panel { Id = "console", Dock = DockManager.Edge.Bottom, Size = Layout.ConsoleH, Visible = false });
        LoadDockLayout();
    }

    private string DockFile => System.IO.Path.Combine(AppModel.WorkspaceDir, "dock-layout.txt");
    private void LoadDockLayout()
    {
        try { if (System.IO.File.Exists(DockFile)) _dock.Deserialize(System.IO.File.ReadAllText(DockFile)); } catch { }
        // Library + Inspector are always-on (you dock/float them, never hide them); only the console toggles.
        // This also heals layouts saved while the old close-x had hidden a panel, which left it stuck off-screen.
        _dock.Get("library")!.Visible = true;
        _dock.Get("inspector")!.Visible = true;
    }
    private void SaveDockLayout() { try { System.IO.Directory.CreateDirectory(AppModel.WorkspaceDir); System.IO.File.WriteAllText(DockFile, _dock.Serialize()); } catch { } }

    /// <summary>Build the frame layout from the dock: a full-bleed map with panels overlaying it.</summary>
    private Layout DockLayout()
    {
        var l = new Layout
        {
            Titlebar = new RectF(0, 0, _vw, Layout.TitlebarH),
            StatusBar = new RectF(0, _vh - Layout.StatusH, _vw, Layout.StatusH),
        };
        var work = new RectF(0, Layout.TitlebarH, _vw, MathF.Max(10, _vh - Layout.TitlebarH - Layout.StatusH));
        _dock.Layout(work);
        l.Canvas = work;   // the map is always the full work area; panels overlay it
        l.Palette = _dock.Get("library")!.Content;
        l.Inspector = _dock.Get("inspector")!.Content;
        l.Console = _dock.Get("console")!.Content;
        return l;
    }

    /// <summary>Pick a resize cursor when the pointer is over a panel's resize border (its inner edge facing the
    /// map - e.g. the top of a bottom-docked pane - or a floating pane's bottom-right corner), or while a resize
    /// is in progress. Returns false when no border is involved.</summary>
    private bool DockResizeCursor(out MouseCursor pick)
    {
        pick = MouseCursor.Arrow;
        var target = _dock.ResizingPanel;
        if (target == null)
        {
            if (Modal || _dock.Dragging) return false;   // a move-drag isn't a resize
            foreach (var p in _dock.Panels)
                if (p.Visible && ResizeBorder(p).Contains(In.Mouse)) { target = p; break; }
        }
        if (target == null) return false;
        pick = target.Dock switch
        {
            DockManager.Edge.Left or DockManager.Edge.Right => MouseCursor.SizeWE,
            DockManager.Edge.Top or DockManager.Edge.Bottom => MouseCursor.SizeNS,
            _ => MouseCursor.SizeNWSE,
        };
        return true;
    }

    private static RectF ResizeBorder(DockManager.Panel p)
    {
        const float t = 6f;
        return p.Dock switch
        {
            DockManager.Edge.Left => new RectF(p.Rect.Right - t, p.Rect.Y, t, p.Rect.H),
            DockManager.Edge.Right => new RectF(p.Rect.X, p.Rect.Y, t, p.Rect.H),
            DockManager.Edge.Top => new RectF(p.Rect.X, p.Rect.Bottom - t, p.Rect.W, t),
            DockManager.Edge.Bottom => new RectF(p.Rect.X, p.Rect.Y, p.Rect.W, t),
            _ => new RectF(p.Rect.Right - 16, p.Rect.Bottom - 16, 16, 16),
        };
    }

    /// <summary>Pick up a panel by its header to move/dock it, grab its inner border to resize, or hit its
    /// header × to hide it.</summary>
    private void DockInputTick(InputState input)
    {
        if (Modal) return;
        var m = input.Mouse;
        if (_dock.Dragging) { _dock.Tick(m, input.LeftDown); if (!input.LeftDown) SaveDockLayout(); return; }
        if (!input.LeftPressed) return;
        const float dragH = 30f;   // the Hud.Panel title bar doubles as the drag grip
        foreach (var p in _dock.Panels)
        {
            if (!p.Visible) continue;
            if (ResizeBorder(p).Contains(m)) { _dock.BeginResize(p, m); return; }
            var hdr = new RectF(p.Rect.X, p.Rect.Y, p.Rect.W, dragH);
            if (!hdr.Contains(m)) continue;
            _dock.BeginDrag(p, m); return;
        }
    }

    private const float MapCornerBtnReserve = 44f;   // vertical band the corner buttons own at the bottom of the visible map

    /// <summary>Console toggle + Bot's-eye buttons, locked to the bottom-right of the VISIBLE map: they ride on
    /// the map but slide left/up to dodge any right/bottom-docked panel that would otherwise cover them.</summary>
    private void DrawMapCornerButtons(Renderer r)
    {
        if (Modal) return;
        var corner = _dock.VisibleMapCorner();
        var con = _dock.Get("console")!;
        r.Begin();
        var bf = r.Fonts.Get(FontKind.SansBold, 13);
        float h = 30, y = corner.Y - 10 - h, rx = corner.X - 10;
        string eyeLbl = Ircuitry.Core.Icons.Glyph("television") + "  Bot's-eye";
        float ew = bf.MeasureString(eyeLbl).X + 26;
        if (_ui.Button("map.eye", new RectF(rx - ew, y, ew, h), eyeLbl, Theme.Teal)) OpenIrcWindow();
        rx -= ew + 8;
        string cLbl = Ircuitry.Core.Icons.Glyph("terminal-window") + (con.Visible ? "  Hide console" : "  Console");
        float cw = bf.MeasureString(cLbl).X + 26;
        if (_ui.Button("map.console", new RectF(rx - cw, y, cw, h), cLbl, con.Visible ? Theme.Lime : Theme.Idle))
        { con.Visible = !con.Visible; SaveDockLayout(); }
        rx -= cw + 8;
        string sLbl = Ircuitry.Core.Icons.Glyph("bookmarks-simple") + "  Snippets";
        float sw = bf.MeasureString(sLbl).X + 26;
        if (_ui.Button("map.snippets", new RectF(rx - sw, y, sw, h), sLbl, _shelfOpen ? Theme.Berry : Theme.Idle))
        { _shelfOpen = !_shelfOpen; _shelfDirty = true; }
        r.End();
    }

    /// <summary>Draw each panel's drag grip (over its Hud title bar), the drop-edge highlight while dragging, and
    /// the floating ghost of the panel being moved. Runs over the top of the panels.</summary>
    private void DrawDockChrome(Renderer r)
    {
        r.Begin();
        foreach (var p in _dock.Panels)
        {
            if (!p.Visible) continue;
            // a grip handle centered on the title bar, so it's clear you can drag the panel here
            var hdr = new RectF(p.Rect.X, p.Rect.Y, p.Rect.W, 30f);
            bool hot = !Modal && hdr.Contains(In.Mouse) && !_dock.Dragging;
            var grip = Theme.WithAlpha(Theme.TextDim, hot ? 0.9f : 0.5f);
            float gx = hdr.Center.X, gy = hdr.Y + 6;
            for (int row = 0; row < 2; row++)
                for (int col = -2; col <= 2; col++)
                    r.Disc(new Vector2(gx + col * 5, gy + row * 4), 1.15f, grip);
        }
        // the dragged panel rides live at its real drop target (positioned in Layout) - no separate landing ghost.
        // just give it a "lifted" accent border so it reads as the one being moved.
        var dp = _dock.DraggingPanel;
        if (dp != null) r.RoundOutline(dp.Rect, Theme.WithAlpha(Theme.Sky, 0.9f), Hud.PanelRadius);
        r.End();
    }

    private Bot Bot => _app.ActiveBot;
    private InputState In => _input;

    public bool SuppressAutosave => _ui.AnyFieldFocused;

    /// <summary>Dropping an .ircbot file loads its nodes into the current workflow at the drop point.</summary>
    public void OnIrcbotDrop(Vector2 screen, string path)
    {
        try
        {
            var (g, _) = Ircuitry.Graph.GraphSerializer.Load(System.IO.File.ReadAllText(path), out var skipped);
            if (g.Nodes.Count == 0 && skipped.Count == 0) { Bot.Log.Add(LogLevel.System, "nothing to load from " + System.IO.Path.GetFileName(path)); return; }
            _editor.InsertGraphAt(g, screen);
            _app.MarkDirty();
            Bot.Log.Add(LogLevel.System, $"loaded {g.Nodes.Count} node(s) from {System.IO.Path.GetFileName(path)}");
            if (skipped.Count > 0)
            {
                var w = Ircuitry.Graph.GraphSerializer.SkippedWarning(skipped);
                Bot.Log.Add(LogLevel.Warn, Ircuitry.Core.Icons.Glyph("warning") + " " + w);
                PushToast(Ircuitry.Core.Icons.Glyph("warning") + " " + w);
            }
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
        if (Ircuitry.App.DeepLink.IsServerLink(link)) { HandleServerLink(link); return; }
        if (Ircuitry.App.DeepLink.IsConnectLink(link)) { HandleConnectLink(link); return; }   // cockpit "open in app"
        if (!Ircuitry.App.DeepLink.TryParse(link, out var action, out var url, out var data))
        { Bot.Log.Add(LogLevel.Error, "unrecognised link: " + link); return; }

        string text;
        if (data.Length > 0)   // an inline workflow (e.g. a bot merged in the browser) - no fetch, still confirmed
        {
            text = Ircuitry.App.DeepLink.DecodeData(data);
            if (text.Length == 0) { Bot.Log.Add(LogLevel.Error, "couldn't decode the inline workflow link"); return; }
        }
        else
        {
            if (!Ircuitry.App.DeepLink.IsAllowedUrl(url))
            { Bot.Log.Add(LogLevel.Error, "blocked link (only ircuitry community URLs are allowed): " + url); return; }
            Bot.Log.Add(LogLevel.System, "fetching " + url);
            try
            {
                var (status, body) = Ircuitry.Net.Http.Send("GET", url, System.Array.Empty<(string, string)>(), null);
                if (status < 200 || status >= 300) { Bot.Log.Add(LogLevel.Error, $"download failed (HTTP {status})"); return; }
                text = body;
            }
            catch (Exception ex) { Bot.Log.Add(LogLevel.Error, "download failed: " + ex.Message); return; }
        }

        if (action == "install-bot")
        {
            StageWorkflowInstall(text);   // confirm first, like nodes - it becomes a new bot tab
            return;
        }
        if (action == "install-theme")
        {
            StageThemeInstall(text);      // live-previews the theme so you can try before keeping
            return;
        }
        StageInstall(text, "link");   // default action: install-node
    }

    /// <summary>An irc:// / ircs:// link adds a reusable server to the saved list (it never auto-connects).
    /// If a server with the same host:port is already saved, we ask before adding another copy.</summary>
    private void HandleServerLink(string link)
    {
        if (!Ircuitry.App.DeepLink.TryParseServer(link, out var host, out var port, out var tls, out var channels))
        { Bot.Log.Add(LogLevel.Error, "unrecognised server link: " + link); return; }

        var profile = new Ircuitry.Core.ServerProfile
        {
            Name = host, Host = host, Port = port, UseTls = tls,
            AcceptInvalidCerts = tls, AutoReconnect = true, Channels = channels,
        };
        var existing = Ircuitry.Core.Servers.All().FirstOrDefault(s => s.Host == host && s.Port == port);
        if (existing != null)
        {
            profile.Name = UniqueServerName(host);
            _serverLinkProfile = profile;
            _serverLinkExisting = existing.Name;
            _serverLinkOpen = true; _serverLinkJustOpened = true;
            return;
        }
        profile.Name = UniqueServerName(host);
        Ircuitry.Core.Servers.Save(profile);
        Notify(Ircuitry.Core.Icons.Glyph("broadcast") + $" saved server {host}:{port}" + (channels.Length > 0 ? "  ·  " + channels : ""));
        Bot.Log.Add(LogLevel.System, $"saved server '{profile.Name}' ({host}:{port}) from a link");
    }

    // a brief on-theme notification (fleshed out into toasts + history below)
    private void Notify(string msg) => PushToast(msg);

    private static string UniqueServerName(string baseName)
    {
        var names = Ircuitry.Core.Servers.All().Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName)) return baseName;
        for (int i = 2; ; i++) { var n = $"{baseName} ({i})"; if (!names.Contains(n)) return n; }
    }

    private void DrawServerLinkModal(Renderer r)
    {
        var p = _serverLinkProfile;
        if (p == null) return;
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.45f));

        float pw = 500, ph = 210;
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        Hud.Panel(r, panel, "Save this server?", Theme.Sky);

        float x = panel.X + 22, w = panel.W - 44, y = panel.Y + Hud.HeaderH + 18;
        r.Text(r.Fonts.Get(FontKind.SansBold, 16), $"{p.Host}:{p.Port}" + (p.UseTls ? "  · TLS" : ""), new Vector2(x, y), Theme.Text);
        y += 26;
        foreach (var line in Wrap(r.Fonts.Get(FontKind.Sans, 13), $"You already have this server saved as “{_serverLinkExisting}”. Add another copy? It is only saved to your servers list - nothing connects.", w))
        { r.Text(r.Fonts.Get(FontKind.Sans, 13), line, new Vector2(x, y), Theme.TextDim); y += 17; }

        var addRect = new RectF(panel.Right - 22 - 150, panel.Bottom - 50, 150, 34);
        var cancelRect = new RectF(addRect.X - 12 - 110, panel.Bottom - 50, 110, 34);
        if (_ui.Button("svlink.cancel", cancelRect, "CANCEL", Theme.Idle)) { _serverLinkOpen = false; _serverLinkProfile = null; }
        if (_ui.Button("svlink.add", addRect, "ADD ANYWAY", Theme.Sky, primary: true))
        {
            Ircuitry.Core.Servers.Save(p);
            Notify(Ircuitry.Core.Icons.Glyph("broadcast") + $" saved server {p.Host}:{p.Port}");
            Bot.Log.Add(LogLevel.System, $"saved server '{p.Name}' ({p.Host}:{p.Port}) from a link");
            _serverLinkOpen = false; _serverLinkProfile = null;
        }

        if (!_serverLinkJustOpened && In.LeftPressed && !panel.Contains(In.Mouse)) { _serverLinkOpen = false; _serverLinkProfile = null; }
        _serverLinkJustOpened = false;
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

    // Stage a community workflow for a confirm (mirrors the node install): it becomes a new bot tab.
    private void StageWorkflowInstall(string text)
    {
        string name = "workflow", desc = ""; int nodes = 0;
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(text);
            var r = d.RootElement;
            if (r.ValueKind != System.Text.Json.JsonValueKind.Object || !r.TryGetProperty("nodes", out var ns) || ns.ValueKind != System.Text.Json.JsonValueKind.Array)
            { Bot.Log.Add(LogLevel.Error, "link is not a workflow (.ircbot)"); return; }
            nodes = ns.GetArrayLength();
            if (r.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String) name = n.GetString() ?? name;
            if (r.TryGetProperty("description", out var dd) && dd.ValueKind == System.Text.Json.JsonValueKind.String) desc = dd.GetString() ?? "";
        }
        catch (Exception ex) { Bot.Log.Add(LogLevel.Error, "invalid workflow: " + ex.Message); return; }
        _wfInstallText = text; _wfInstallName = name; _wfInstallDesc = desc; _wfInstallNodes = nodes;
        _wfInstallOpen = true; _wfInstallJustOpened = true;
    }

    private void DrawWorkflowInstallModal(Renderer r)
    {
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.45f));
        float pw = 560, ph = 300;
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        Hud.Panel(r, panel, "Install community workflow?", Theme.Sky);

        float x = panel.X + 22, w = panel.W - 44, y = panel.Y + Hud.HeaderH + 16;
        r.Text(r.Fonts.Get(FontKind.SansBold, 16), Ircuitry.Core.Icons.Glyph("robot") + "  " + _wfInstallName, new Vector2(x, y), Theme.Text); y += 26;
        r.Text(r.Fonts.Get(FontKind.Mono, 11), $"{_wfInstallNodes} node(s) · adds a new bot tab", new Vector2(x, y), Theme.TextDim); y += 24;
        if (_wfInstallDesc.Length > 0)
            foreach (var line in Wrap(r.Fonts.Get(FontKind.Sans, 12), _wfInstallDesc, w))
            { r.Text(r.Fonts.Get(FontKind.Sans, 12), line, new Vector2(x, y), Theme.TextDim); y += 17; }
        y += 8;
        foreach (var line in Wrap(r.Fonts.Get(FontKind.Sans, 12), Ircuitry.Core.Icons.Glyph("warning") + "  A workflow can include Code nodes that run on your machine. Review it before you press RUN BOT.", w))
        { r.Text(r.Fonts.Get(FontKind.Sans, 12), line, new Vector2(x, y), Theme.Alert); y += 17; }

        var goR = new RectF(panel.Right - 22 - 130, panel.Bottom - 50, 130, 34);
        var cancelR = new RectF(goR.X - 12 - 110, panel.Bottom - 50, 110, 34);
        if (_ui.Button("wfinstall.cancel", cancelR, "CANCEL", Theme.Idle)) _wfInstallOpen = false;
        if (_ui.Button("wfinstall.go", goR, "INSTALL", Theme.Sky, primary: true))
        {
            var bot = _app.ImportText(_wfInstallText);
            if (bot != null) { Bot.Log.Add(LogLevel.System, $"imported workflow “{bot.Name}” - set your server/nick/channels, then RUN BOT"); PushToast(Ircuitry.Core.Icons.Glyph("check") + $" {bot.Name} added as a new bot tab"); }
            _wfInstallOpen = false;
        }
        if (In.LeftPressed && !panel.Contains(In.Mouse) && !_wfInstallJustOpened) _wfInstallOpen = false;
        _wfInstallJustOpened = false;
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
        Bot.Log.Add(LogLevel.System, "calendar source set " + Ircuitry.Core.Icons.Glyph("arrow-right") + " " + System.IO.Path.GetFileName(path.TrimEnd('/', '\\')));
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
        _l = DockLayout();
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
        _l = DockLayout();
        var p = System.IO.Path.Combine(NodeCatalog.CustomDir, "wordcount.ircnode");
        if (System.IO.File.Exists(p)) OnNodeDrop(_l.Canvas.Center, p);
    }

    public void DebugInstallClip() { _l = DockLayout(); InstallFromClipboard(); }

    public void DebugOpenUninstall()
    {
        _l = DockLayout();
        var d = NodeCatalog.Custom.Count > 0 ? NodeCatalog.Custom[0] : null;
        if (d != null) { _uninstallDef = d; _uninstallOpen = true; _uninstallJustOpened = true; }
    }

    public void DebugOpenNodeManager() => OpenNodeManager();
    public void DebugOpenSecretPick() { _l = DockLayout(); OpenSecretPicker("", "API key", _ => { }); }
    public void DebugShowServers() { _l = DockLayout(); _serversOpen = true; _serversJustOpened = true; _serverSaveName = "my-network"; }
    public void DebugShowAchievements() { _l = DockLayout(); _achOpen = true; _achJustOpened = true; _achScroll = 0; }
    public void DebugOpenIrcv3Cat() { _openCat = NodeCategory.Ircv3; }
    public void DebugOpenFileMenu() { _l = DockLayout(); OpenFileMenu(new Vector2(_vw - 360, _l.Titlebar.Bottom + 3)); }
    public void DebugCommandPalette() { OpenCommandPalette(); _cmdkQuery = "se"; _cmdkJustOpened = true; }
    public void DebugLibraryPrefs()
    {
        foreach (var t in new[] { "action.reply", "event.command" }) if (!Ircuitry.Core.NodePrefs.IsFavorite(t)) Ircuitry.Core.NodePrefs.ToggleFavorite(t);
        foreach (var t in new[] { "filter.contains", "ai.reply", "logic.forEach" }) Ircuitry.Core.NodePrefs.RecordUse(t);
        _openCat = null;
    }
    public void DebugNotifications()
    {
        _l = DockLayout();
        PushToast(Ircuitry.Core.Icons.Glyph("floppy-disk") + " Workspace saved");
        _notifLog.Insert(0, (DateTime.Now.AddMinutes(-1), Ircuitry.Core.Icons.Glyph("export") + " Exported welcomer"));
        _notifLog.Insert(0, (DateTime.Now.AddMinutes(-3), Ircuitry.Core.Icons.Glyph("broadcast") + " saved server irc.libera.chat:6697"));
        _notifLog.Insert(0, (DateTime.Now.AddMinutes(-8), Ircuitry.Core.Icons.Glyph("arrow-bend-up-left") + " Snapshot restored"));
        _notifOpen = true; _notifJustOpened = true; _notifUnread = 0;
    }
    public void DebugMultiServer()
    {
        _l = DockLayout();
        var b = Bot;
        b.Servers.Clear();
        b.Servers.Add(new Ircuitry.Irc.IrcSettings { Label = "Libera", Host = "irc.libera.chat", Channels = "#ircuitry", ConnectOnStartup = true });
        b.Servers.Add(new Ircuitry.Irc.IrcSettings { Label = "OFTC", Host = "irc.oftc.net", Channels = "#bots" });
        b.Servers.Add(new Ircuitry.Irc.IrcSettings { Label = "Libera (test)", Host = "irc.libera.chat", Channels = "#test" });
        b.SelectedServer = 0;
        _editor.Selection.Clear();   // no node selected -> the connection inspector shows
    }
    public void DebugShowNetwork()
    {
        _l = DockLayout();
        DebugDemoShot();   // bot 1 -> libera
        var b2 = _app.AddBot("greeter"); b2.Name = "welcomer"; b2.Settings.Host = "irc.libera.chat"; b2.Settings.Channels = "#cozy";
        var b3 = _app.AddBot("pingpong"); b3.Name = "pong-bot"; b3.Settings.Host = "irc.oftc.net"; b3.Settings.Channels = "#bots";
        _networkOpen = true; _networkJustOpened = true;
    }

    private bool _demoShotFit;
    // Build a clean, credential-free showcase graph for marketing screenshots.
    public void DebugDemoShot()
    {
        var b = Bot;
        var g = b.Graph;
        g.Nodes.Clear(); g.Connections.Clear();
        Node Add(string type, float x, float y) => g.Add(NodeCatalog.Get(type), new Vector2(x, y));
        var cmd = Add("event.command", -360, -150); cmd.SetParam("command", "ask");
        var ai = Add("ai.reply", -30, -150); ai.SetParam("prompt", "{args}");
        var rep = Add("action.reply", 300, -150);
        g.Connect(cmd.Id, 0, ai.Id, 0); g.Connect(ai.Id, 0, rep.Id, 0); g.Connect(ai.Id, 1, rep.Id, 1);
        var join = Add("event.join", -360, 40);
        var welcome = Add("action.reply", -30, 40); welcome.SetParam("message", "welcome to {channel}, {nick}! \U0001F389");   // intentional unicode (party popper)
        g.Connect(join.Id, 0, welcome.Id, 0);
        var timer = Add("event.timer", -360, 210); timer.SetParam("seconds", "3600");
        var say = Add("action.say", -30, 210); say.SetParam("channel", "#ircuitry"); say.SetParam("message", "still here and cosy \u2615");   // intentional unicode (hot beverage)
        g.Connect(timer.Id, 0, say.Id, 0);
        b.Name = "demo";
        b.Settings.Host = "irc.libera.chat"; b.Settings.Port = 6697; b.Settings.UseTls = true;
        b.Settings.Nick = "ircuitry-bot"; b.Settings.Channels = "#ircuitry";
        _editor.Graph = g;
        _demoShotFit = true;
    }
    /// <summary>Add a node the user chose (drag/quick-add/palette/install) and remember it as recently used.</summary>
    private Node SpawnNode(NodeDef def, Vector2 world)
    {
        var n = _editor.Spawn(def, world);
        Ircuitry.Core.NodePrefs.RecordUse(def.TypeId);
        return n;
    }

    /// <summary>If the just-spawned node came from a "+" ghost, wire the source exec output into its first exec input.</summary>
    private void GhostWire(Node target)
    {
        if (_ghostWireFrom is not { } from) return;
        _ghostWireFrom = null;
        // wire the source output into the first pin-kind-compatible input (Connect enforces the rules)
        for (int i = 0; i < target.Inputs.Length; i++)
            if (Bot.Graph.Connect(from.node, from.pin, target.Id, i)) return;
    }

    public void DebugSpawnSelect(string typeId)
    {
        if (!NodeCatalog.TryGet(typeId, out _)) return;
        var n = _editor.Spawn(NodeCatalog.Get(typeId), Vector2.Zero);
        _editor.Selection.Clear(); _editor.Selection.Add(n.Id); _lastGraph = Bot.Graph;
    }
    public void DebugShowLabels()
    {
        var n = Bot.Graph.Nodes.FirstOrDefault();
        if (n != null) { n.Title = Ircuitry.Core.Icons.Glyph("sparkle") + " my greeting"; _editor.Selection.Clear(); _editor.Selection.Add(n.Id); _lastGraph = Bot.Graph; }
        _renamingBot = Bot; _ui.Focus = "tab.rename";
    }

    public void Update(InputState input, Clock clock)
    {
        _input = input;
        _editor.Graph = Bot.Graph;
        _l = DockLayout();
        ClipboardPoll(clock);
        AchievementsTick(clock);
        RemotePump();        // keep any remote-server session live (drains its callbacks/events)
        RemoteEditTick(clock);   // debounce-push edits of a remote bot tab to its server
        RemoteCursorTick();      // report my cursor + soft lock to co-editors of a remote bot
        foreach (var b in _app.Bots) b.Runtime.PlaybackStep(clock.Time);   // reveal queued node glows (slow-mo); instant + drains when off

        if (DebugAutoHistory && Bot.Runtime.HistoryCount > 0 && (!_historyOpen || _historyRuns.Count != Bot.Runtime.HistoryCount)) OpenHistory();
        if (DebugAutoQuick && !Modal) { OpenQuickAdd(_l.Canvas.Center); DebugAutoQuick = false; }

        // F1 opens the online documentation from anywhere (even over a modal)
        if (input.KeyPressed(Keys.F1)) Ircuitry.App.DeepLink.OpenUrl(DocsUrl);

        if (Modal)
        {
            if (input.KeyPressed(Keys.Escape))
            {
                if (_appearanceOpen) CloseAppearance();
                else if (_themeInstallOpen) CancelThemeInstall();
                else { _importOpen = false; _confirmDeleteBot = null; _historyOpen = false; _quickOpen = false; _templateOpen = false; _closePromptOpen = false; _secretsOpen = false; _testOpen = false; _ctxOpen = false; _saveNodeOpen = false; _installOpen = false; _wfInstallOpen = false; _uninstallOpen = false; _nodeMgrOpen = false; _secretPickOpen = false; _serversOpen = false; _networkOpen = false; _achOpen = false; _snapOpen = false; _serverLinkOpen = false; _nbOpen = false; _cmdkOpen = false; _ircWinOpen = false; _remoteOpen = false; if (_upState != UpState.Downloading && _upState != UpState.Applying) _upPromptOpen = false; }
            }
        }
        else if (_renamingBot != null)
        {
            // renaming a tab - keep the inline editor focused; Esc cancels
            if (input.KeyPressed(Keys.Escape)) { _renamingBot = null; _ui.Focus = null; }
        }
        else if (!_tut.Active)   // tutorial owns the canvas while it runs (it places/wires for you)
        {
            bool overBar = PlaybackBarRect().Contains(input.Mouse);   // the on-canvas playback bar swallows canvas input
            DockInputTick(input);                                     // drag panels to dock/float + resize them
            bool overPanel = _dock.OverPanel(input.Mouse) || _dock.Dragging;   // panels overlay the map - they eat canvas input
            overBar = overBar || overPanel;

            // right-click anywhere on the canvas -> context menu
            if (In.RightPressed && _l.Canvas.Contains(input.Mouse) && !_ui.AnyFieldFocused && !overBar)
            {
                // right-clicking a node that isn't already selected makes it the target
                var hit = _editor.NodeAt(input.Mouse);
                if (hit != null && !_editor.Selection.Contains(hit.Id)) { _editor.Selection.Clear(); _editor.Selection.Add(hit.Id); }
                OpenContextMenu(input.Mouse, hit != null);
            }

            // double-click empty canvas -> quick-add menu
            if (In.LeftPressed && _l.Canvas.Contains(input.Mouse) && _editor.IsEmptyAt(input.Mouse) && !overBar)
            {
                if (clock.Time - _lastClickTime < 0.35f && Vector2.Distance(input.Mouse, _lastClickPos) < 6f)
                { OpenQuickAdd(input.Mouse); _lastClickTime = 0; }
                else { _lastClickTime = clock.Time; _lastClickPos = input.Mouse; }
            }

            if (!Modal)
            {
                _editor.Update(input, _l.Canvas, _ui.AnyFieldFocused || overBar);
                if (_editor.GhostAdd is { } g)   // a "+" ghost was clicked: quick-add a node wired from that exec pin
                {
                    OpenQuickAdd(_editor.Cam.WorldToScreen(g.world));
                    _quickWorld = g.world; _ghostWireFrom = (g.node, g.pin);
                    _editor.GhostAdd = null;
                }
                if (input.Ctrl && input.KeyPressed(Keys.K)) OpenCommandPalette();
                if (input.Ctrl && input.KeyPressed(Keys.F)) { _findOpen = !_findOpen; _findArm = _findOpen; if (!_findOpen && _ui.Focus == "find") _ui.Focus = null; }
                if (input.Ctrl && input.KeyPressed(Keys.S)) { _app.Save(); Notify(Ircuitry.Core.Icons.Glyph("floppy-disk") + " Workspace saved"); }
                if (input.Ctrl && input.KeyPressed(Keys.R)) ToggleRun();
                if (input.Ctrl && input.KeyPressed(Keys.E)) { _app.ExportActive(); Notify(Ircuitry.Core.Icons.Glyph("export") + $" Exported {Bot.Name}"); }
                if (input.Ctrl && input.KeyPressed(Keys.H)) OpenHistory();
                if (input.Ctrl && input.KeyPressed(Keys.L)) { _editor.AutoLayout(); _editor.FocusContent(_l.Canvas); _app.MarkDirty(); }
                if (input.Ctrl && input.Shift && input.KeyPressed(Keys.V)) InstallFromClipboard();
            }
        }

        if (_followCam && !Modal && !_tut.Active) UpdateFollowCam();

        UpdateTick(clock);
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
        _ghostWireFrom = null;                   // a plain quick-add doesn't auto-wire (the ghost handler re-sets this)
        _quickScreen = screen;
        _quickWorld = _editor.Cam.ScreenToWorld(screen);
        _quickSearch = "";
        _quickScroll = 0;
        _ui.Focus = "quick.search";             // auto-focus the search box
    }

    public void Draw(Renderer r, Clock clock)
    {
        _vw = r.ViewW; _vh = r.ViewH;
        _l = DockLayout();
        if (_demoShotFit && _vw > 0) { _demoShotFit = false; _editor.FocusContent(_l.Canvas); }   // frame the demo graph for screenshots
        _editor.Graph = Bot.Graph;
        _editor.Running = RunningOf(Bot);
        if (!ReferenceEquals(_lastGraph, Bot.Graph)) { _editor.Selection.Clear(); _lastGraph = Bot.Graph; }
        _ui.Begin(r, In, clock);
        _ui.Enabled = !Modal;   // a modal blocks the widgets underneath it

        // ---------- canvas ----------
        // keep the bottom-right minimap on the VISIBLE map: left of any right-docked pane, and above the
        // on-map corner buttons - so it's never hidden behind a panel or the Console/Bot's-eye buttons
        var mmVis = _dock.VisibleMapRect();
        _editor.SetMinimapArea(new RectF(mmVis.X, mmVis.Y, mmVis.W, MathF.Max(0, mmVis.H - MapCornerBtnReserve)));
        r.Begin(BlendMode.Alpha, _l.Canvas.ToRectangle());
        r.RoundFill(_l.Canvas, Theme.Backdrop, Hud.PanelRadius);
        r.End();
        _editor.Draw(r, _l.Canvas, In, clock);
        DrawRemotePeers(r);   // co-editors' live cursors + soft locks, over the canvas
        r.Begin();
        if (Bot.Graph.Nodes.Count == 0) EmptyHint(r, _dock.VisibleMapRect(), clock);
        CanvasFrame(r, _l.Canvas);
        r.End();
        DrawPlaybackBar(r);   // slow-motion run playback control (over the canvas)
        DrawFindBar(r);       // find-in-graph (Ctrl+F)
        DrawShelf(r);         // snippet shelf
        DrawErrorTray(r);     // node-attributed runtime errors (#15)

        // ---------- panel chromes ----------
        bool consoleOn = _dock.Get("console")!.Visible;
        r.Begin();
        Hud.Panel(r, _l.Palette, "Node Library", Theme.Cyan);
        Hud.Panel(r, _l.Inspector, "Inspector", Theme.Amber);
        if (consoleOn) { Hud.Panel(r, _l.Console, "Event Console", Theme.Lime); ConsoleHeaderStats(r, _l.Console); }
        StatusBar(r, _l.StatusBar, clock);
        r.End();
        DrawTitlebar(r, clock);   // manages its own batches (gloss + scissored tab gutter)

        // ---------- panel contents (scissored) ----------
        DrawPalette(r);
        DrawInspector(r);
        if (consoleOn) DrawConsole(r);
        DrawDockChrome(r);   // panel close buttons + drop highlight + drag ghost, over the panels

        // ---------- canvas save floppy + map corner buttons (console + bot's-eye) ----------
        DrawCanvasSave(r);
        DrawMapCornerButtons(r);

        // ---------- palette drag ghost ----------
        UpdatePaletteDrag(r);

        // ---------- overlay ----------
        r.Begin();
        Hud.Overlay(r, _vw, _vh);
        r.End();

        // ---------- modals (on top, capture input) ----------
        // install confirms take priority so a deep-link install overlays whatever modal is already open
        if (_installOpen)
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
        else if (_wfInstallOpen)
        {
            _ui.Enabled = true;
            r.Begin();
            DrawWorkflowInstallModal(r);
            r.End();
        }
        else if (_importOpen)
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
        else if (_snapOpen)
        {
            _ui.Enabled = true;
            r.Begin();
            DrawSnapshotModal(r);
            r.End();
        }
        else if (_historyOpen)
        {
            _ui.Enabled = true;
            DrawHistoryModal(r, clock);
        }
        else if (_ircWinOpen)
        {
            _ui.Enabled = true;
            DrawIrcWindow(r, clock);
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
        else if (_nbOpen)
        {
            _ui.Enabled = true;
            DrawNodeBuilder(r, clock);
        }
        else if (_nodeMgrOpen)
        {
            _ui.Enabled = true;
            DrawNodeManager(r);
        }
        else if (_upPromptOpen)
        {
            _ui.Enabled = true;
            DrawUpdatePrompt(r);
        }
        else if (_secretPickOpen)
        {
            _ui.Enabled = true;
            DrawSecretPicker(r);
        }
        else if (_serversOpen)
        {
            _ui.Enabled = true;
            DrawServersModal(r);
        }
        else if (_networkOpen)
        {
            _ui.Enabled = true;
            DrawNetworkModal(r);
        }
        else if (_achOpen)
        {
            _ui.Enabled = true;
            DrawAchievementsModal(r);
        }
        else if (_serverLinkOpen)
        {
            _ui.Enabled = true;
            r.Begin();
            DrawServerLinkModal(r);
            r.End();
        }
        else if (_cmdkOpen)
        {
            _ui.Enabled = true;
            DrawCommandPalette(r);
        }
        else if (_bakeryOpen)
        {
            _ui.Enabled = true;
            DrawBakeryModal(r, clock);
        }
        else if (_appearanceOpen)
        {
            _ui.Enabled = true;
            DrawAppearanceModal(r);
        }
        else if (_themeInstallOpen)
        {
            _ui.Enabled = true;
            DrawThemeInstallModal(r);
        }
        else if (_remoteOpen)
        {
            _ui.Enabled = true;
            DrawRemoteModal(r);
        }
        else if (_staleRemote != null)
        {
            _ui.Enabled = true;
            DrawStaleModal(r);
        }
        else if (_secretCopy.Count > 0)
        {
            _ui.Enabled = true;
            DrawSecretCopyModal(r);
        }

        // ---------- gamified tutorial overlay (on top of everything but app modals) ----------
        DrawTutorial(r, clock);

        // ---------- update overlay (on top of absolutely everything) ----------
        if (_upState == UpState.Downloading || _upState == UpState.Applying) DrawUpgradeOverlay(r, clock);

        // ---------- bake animation (oven + clock -> the bot pops out) ----------
        if (_bakeDebugPending && !BakeAnimActive) { _bakeDebugPending = false; DebugOpenBakery(); StartBake(clock); }
        if (BakeAnimActive) DrawBakeAnim(r, clock);

        // ---------- achievement toast + unified notifications ----------
        DrawAchToast(r, clock);
        DrawToast(r, clock);
        DrawNotifPopover(r);

        SetContextCursor(r);   // pick the right custom pointer for whatever is under the mouse

        _ui.EndFrame(); // blur stale focus (e.g. after switching node/bot) so canvas shortcuts keep working
    }

    /// <summary>
    /// Choose the custom cursor for the current context: vertical-resize over the console handle, a grabby hand
    /// while panning or dragging nodes, a crosshair when Shift is held over the editor (box-select), a plain hand
    /// hovering the editor canvas, and the normal pointer everywhere else (panels, titlebar, modals).
    /// </summary>
    private void SetContextCursor(Renderer r)
    {
        var c = r.Cursors;
        MouseCursor pick;
        // window resize edges win over everything: the OS will resize there (matching Sdl's hit-test), so the
        // cursor must say so even when the pointer is over a panel at the very edge/corner.
        int re = Ircuitry.Core.Sdl.ResizeEdge((int)In.Mouse.X, (int)In.Mouse.Y);
        if (re != 0)
            pick = re switch { 2 or 6 => MouseCursor.SizeNWSE, 4 or 8 => MouseCursor.SizeNESW, 3 or 7 => MouseCursor.SizeNS, _ => MouseCursor.SizeWE };
        else if (Modal) pick = c.Pointer;
        else if (DockResizeCursor(out var dc)) pick = dc;                  // hovering (or dragging) a panel's resize border
        else if (_editor.IsGrabbing) pick = c.Grab;                       // keep grabbing even if the pointer strays off-canvas
        else if (_l.Canvas.Contains(In.Mouse)) pick = In.Shift ? c.Crosshair : c.Hand;
        else pick = c.Pointer;
        try { Mouse.SetCursor(pick); } catch { }
    }

    private void DrawSaveNodeModal(Renderer r)
    {
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.45f));
        float pw = 520, ph = 366;
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        int count = _editor.Selection.Count;
        Hud.Panel(r, panel, "Bake nodes into one", Theme.Violet);

        float x = panel.X + 22, w = panel.W - 44, y = panel.Y + Hud.HeaderH + 14;
        foreach (var line in Wrap(r.Fonts.Get(FontKind.Sans, 12), $"Bundles your {count} selected node(s) into one reusable node. Its inputs and outputs are worked out from the wires crossing the selection - no extra setup. It joins your Node Library.", w))
        { r.Text(r.Fonts.Get(FontKind.Sans, 12), line, new Vector2(x, y), Theme.TextDim); y += 17; }
        y += 8;

        float catW = 130, iconW = 50, gap = 10, nameW = w - catW - iconW - 2 * gap;
        r.Text(r.Fonts.Get(FontKind.SansBold, 10), "NAME", new Vector2(x, y), Theme.TextDim);
        r.Text(r.Fonts.Get(FontKind.SansBold, 10), "ICON", new Vector2(x + nameW + gap, y), Theme.TextDim);
        r.Text(r.Fonts.Get(FontKind.SansBold, 10), "CATEGORY", new Vector2(x + nameW + gap + iconW + gap, y), Theme.TextDim);
        y += 15;
        _saveNodeName = _ui.TextField("savenode.name", new RectF(x, y, nameW, 28), _saveNodeName, "My Node");
        _saveNodeIcon = _ui.TextField("savenode.icon", new RectF(x + nameW + gap, y, iconW, 28), _saveNodeIcon, "puzzle-piece");
        _saveNodeCat = _ui.Choice("savenode.cat", new RectF(x + nameW + gap + iconW + gap, y, catW, 28), _bakeCats, _saveNodeCat);
        y += 28 + 12;
        r.Text(r.Fonts.Get(FontKind.SansBold, 10), "DESCRIPTION", new Vector2(x, y), Theme.TextDim); y += 15;
        _saveNodeDesc = _ui.TextField("savenode.desc", new RectF(x, y, w, 28), _saveNodeDesc, "what this node does"); y += 28 + 10;
        // makes the baked node wireable into Ask AI: its input pins become the model's arguments, its first
        // data output the result (or, if it contains an AI Tool node, that tool is used directly)
        _saveNodeAsTool = _ui.Toggle("savenode.astool", new RectF(x, y, w, 24), _saveNodeAsTool, Ircuitry.Core.Icons.Glyph("toolbox") + " Usable as an AI tool (wire into Ask AI)");

        var saveR = new RectF(panel.Right - 22 - 132, panel.Bottom - 50, 132, 34);
        var cancelR = new RectF(saveR.X - 12 - 110, panel.Bottom - 50, 110, 34);
        if (_ui.Button("savenode.cancel", cancelR, "CANCEL", Theme.Idle)) _saveNodeOpen = false;
        if (_ui.Button("savenode.save", saveR, Ircuitry.Core.Icons.Glyph("cake") + "  BAKE", Theme.Violet, primary: true))
        {
            var name = _saveNodeName.Trim(); if (name.Length == 0) name = "My Node";
            // an explicit Subflow Start means the author defined the pins by hand; otherwise auto-wrap them
            string? manifest = _editor.SelectionIsSubflow
                ? _editor.SaveSelectionAsNode(name, _saveNodeAsTool)
                : _editor.BuildCompositeFromSelection(name, _saveNodeIcon, _saveNodeCat, _saveNodeDesc, _saveNodeAsTool, out _);
            if (manifest == null) { Bot.Log.Add(LogLevel.Error, "Select a couple of wired-up nodes to bake first."); PushToast(Ircuitry.Core.Icons.Glyph("warning") + " nothing to bake - select some nodes"); }
            else
            {
                try
                {
                    var def = Ircuitry.Graph.CustomNode.Load(manifest) ?? throw new Exception("invalid manifest");
                    System.IO.Directory.CreateDirectory(NodeCatalog.CustomDir);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(NodeCatalog.CustomDir, def.TypeId + ".ircnode"), manifest);
                    NodeCatalog.LoadCustom();
                    Bot.Log.Add(LogLevel.System, $"baked node “{name}” " + Ircuitry.Core.Icons.Glyph("arrow-right") + " Node Library " + Ircuitry.Core.Icons.Glyph("caret-right") + $" {def.Category}");
                    PushToast(Ircuitry.Core.Icons.Glyph("cake") + $" {name} baked into your library");
                    _saveNodeOpen = false;
                }
                catch (Exception ex) { Bot.Log.Add(LogLevel.Error, "bake failed: " + ex.Message); PushToast(Ircuitry.Core.Icons.Glyph("warning") + " bake failed: " + ex.Message); }
            }
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
            r.Text(r.Fonts.Get(FontKind.SansBold, 15), Ircuitry.Core.Icons.Glyph(d.Icon) + "  " + d.Title, new Vector2(x, y), Theme.Text); y += 24;
            r.Text(r.Fonts.Get(FontKind.Mono, 11), $"{d.TypeId} · {d.Category} · {d.Inputs.Length}in/{d.Outputs.Length}out", new Vector2(x, y), Theme.TextDim); y += 22;
        }
        r.Text(r.Fonts.Get(FontKind.Sans, 12), Ircuitry.Core.Icons.Glyph("warning") + "  This runs code on your machine. Review before installing:", new Vector2(x, y), Theme.Alert); y += 22;

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
                if (d != null && NodeCatalog.TryGet(d.TypeId, out var inst)) { SpawnNode(inst, _editor.Cam.ScreenToWorld(_installScreen)); _app.MarkDirty(); }
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

        int live = _app.Bots.Count(b => b.Runtime.Running);
        string msg = $"{live} bot{(live == 1 ? " is" : "s are")} still connected. Minimise to keep "
            + (live == 1 ? "it" : "them") + " online in the background, or Exit to disconnect and close.";
        float x = panel.X + 22, w = panel.W - 44, y = panel.Y + Hud.HeaderH + 18;
        foreach (var line in Wrap(r.Fonts.Get(FontKind.Sans, 13), msg, w))
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

    private void DrawSnapshotModal(Renderer r)
    {
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.45f));
        float pw = 540, ph = Math.Min(480, 150 + _snapFiles.Length * 44 + 60);
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        Hud.Panel(r, panel, "Restore a snapshot", Theme.Lime);
        float x = panel.X + 20, w = panel.W - 40, y = panel.Y + Hud.HeaderH + 16;
        r.Text(r.Fonts.Get(FontKind.Sans, 13), "Load a saved snapshot of the whole workspace. Your current state is saved first.", new Vector2(x, y), Theme.TextDim);
        y += 28;
        if (_snapFiles.Length == 0)
            r.Text(r.Fonts.Get(FontKind.Sans, 14), "No snapshots yet - use File " + Ircuitry.Core.Icons.Glyph("caret-right") + " Save a snapshot.", new Vector2(x, y + 8), Theme.TextFaint);
        foreach (var f in _snapFiles)
        {
            if (y + 36 > panel.Bottom - 52) break;
            string label = Ircuitry.Core.Icons.Glyph("camera") + "  " + System.IO.Path.GetFileNameWithoutExtension(f).Replace("workspace-", "");
            if (_ui.Button("snap." + f, new RectF(x, y, w, 34), label, Theme.Cyan)) { _app.RestoreSnapshot(f); _snapOpen = false; Notify(Ircuitry.Core.Icons.Glyph("arrow-bend-up-left") + " Snapshot restored"); }
            y += 40;
        }
        if (_ui.Button("snap.cancel", new RectF(panel.Right - 130, panel.Bottom - 46, 110, 32), "CANCEL", Theme.Idle)) _snapOpen = false;
        if (!_snapJustOpened && In.LeftPressed && !panel.Contains(In.Mouse)) _snapOpen = false;
        _snapJustOpened = false;
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
            if (_ui.Button("imp." + file, new RectF(x, y, w, 34), Ircuitry.Core.Icons.Glyph("package") + "  " + System.IO.Path.GetFileName(file), Theme.Cyan))
            { _app.ImportFile(file); _importOpen = false; }
            y += 40;
        }

        if (_ui.Button("imp.browse", new RectF(x, panel.Bottom - 46, 254, 32), Ircuitry.Core.Icons.Glyph("globe") + "  Browse community workflows " + Ircuitry.Core.Icons.Glyph("arrow-up-right"), Theme.Lime))
            Ircuitry.App.DeepLink.OpenUrl(WorkflowsUrl);
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
        // panels overlay the full-bleed map, so the on-map readouts ride the visible region (dodging docked panels)
        var vis = _dock.VisibleMapRect();
        var f = r.Fonts.Get(FontKind.SansBold, 12);
        var box = new RectF(vis.X + 12, vis.Y + 12, 250, 26);
        r.RoundFill(box, Theme.WithAlpha(Theme.PanelHi, 0.92f), 7f);
        r.RoundOutline(box, Theme.Hairline, 7f);
        r.Fill(new RectF(box.X + 9, box.Y + 7, 3, 12), Theme.Cyan);
        r.Text(f, $"{Bot.Name}  ·  {Bot.Graph.Nodes.Count} nodes · {Bot.Graph.Connections.Count} wires", new Vector2(box.X + 18, box.Y + 7), Theme.TextDim);
        r.Text(r.Fonts.Get(FontKind.SansBold, 12), $"{(int)Math.Round(_editor.Cam.Zoom * 100)}%", new Vector2(vis.X + 14, vis.Bottom - 24), Theme.TextFaint);
    }

    private void EmptyHint(Renderer r, RectF c, Clock clock)
    {
        var f = r.Fonts.Get(FontKind.Mono, 16);
        float a = 0.25f + 0.1f * clock.Sin01(2.4f);
        r.TextCenteredX(f, Ircuitry.Core.Icons.Glyph("square") + "  drag a node from the Node Library to begin", c.Center.X, c.Center.Y - 10, Theme.WithAlpha(Theme.TextDim, a));
    }

    // ===================================================================

    // The File dropdown: workspace save/snapshots + per-bot export/import. Reuses the context-menu popover.
    private void OpenFileMenu(Vector2 anchor)
    {
        _ctxAnchor = anchor;
        _ctxItems.Clear();
        void Item(string icon, string label, string sc, bool en, Action a) => _ctxItems.Add(new CtxItem { Icon = icon, Label = label, Shortcut = sc, Enabled = en, Do = a });
        void Sep() => _ctxItems.Add(new CtxItem { Sep = true });
        Item("floppy-disk", _app.Dirty ? "Save" : "Save (up to date)", "Ctrl+S", true, () => { _app.Save(); Notify(Ircuitry.Core.Icons.Glyph("floppy-disk") + " Workspace saved"); });
        Item("camera", "Save a snapshot", "", true, () => { _app.SaveSnapshot(); Notify(Ircuitry.Core.Icons.Glyph("camera") + " Snapshot saved"); });
        Item("arrow-bend-up-left", "Restore a snapshot…", "", _app.Snapshots().Length > 0, () => { _snapFiles = _app.Snapshots(); _snapOpen = true; _snapJustOpened = true; });
        Sep();
        Item("export", "Export this bot…", "Ctrl+E", true, () => { _app.ExportActive(); Notify(Ircuitry.Core.Icons.Glyph("export") + $" Exported {Bot.Name}"); });
        Item("tray", "Import a bot…", "", true, () => { _importFiles = _app.Importable().ToArray(); _importOpen = true; _importJustOpened = true; });
        Sep();
        Item("cake", "Bot Bakery (merge bots)…", "", _app.Bots.Count >= 2, OpenBakery);
        Sep();
        Item("folder-open", "Show files", "", true, () => Ircuitry.App.DeepLink.OpenUrl(AppModel.WorkspaceDir));
        _ctxOpen = true; _ctxJustOpened = true;
    }

    // The More dropdown: secrets, achievements and canvas helpers.
    private void OpenMoreMenu(Vector2 anchor)
    {
        _ctxAnchor = anchor;
        _ctxItems.Clear();
        void Item(string icon, string label, string sc, bool en, Action a) => _ctxItems.Add(new CtxItem { Icon = icon, Label = label, Shortcut = sc, Enabled = en, Do = a });
        void Sep() => _ctxItems.Add(new CtxItem { Sep = true });
        bool hasNodes = Bot.Graph.Nodes.Count > 0;
        Item("key", "Secret keys…", "", true, () => { _secretsOpen = true; _secretsJustOpened = true; });
        Item("trophy", "Achievements", "", true, () => { _achOpen = true; _achJustOpened = true; _achScroll = 0; });
        Item("puzzle-piece", "Community nodes…", "", true, OpenNodeManager);
        Item("cloud", "Connect to server…", "", true, OpenRemote);
        Item("palette", "Appearance…", "", true, OpenAppearance);
        Sep();
        Item("ruler", "Tidy layout", "Ctrl+L", hasNodes, () => { _editor.AutoLayout(); _editor.FocusContent(_l.Canvas); _app.MarkDirty(); });
        Item("magnifying-glass", "Fit to view", "", hasNodes, () => _editor.FocusContent(_l.Canvas));
        Item("target", "Frame selection", "F", hasNodes, () => _editor.FrameSelection(_l.Canvas));
        Item("grid-four", _editor.SnapToGrid ? "Snap to grid: on" : "Snap to grid: off", "", true, () => _editor.SnapToGrid = !_editor.SnapToGrid);
        Item("video-camera", _followCam ? "Follow the action: on" : "Follow the action: off", "", true, () => _followCam = !_followCam);
        Sep();
        Item("graduation-cap", "Tutorial", "", true, ForceStartTutorial);
        Item("book-open", "Documentation", "F1", true, () => Ircuitry.App.DeepLink.OpenUrl(DocsUrl));
        _ctxOpen = true; _ctxJustOpened = true;
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
        NodeCategory.Code => "Code",
        NodeCategory.Action => "Actions",
        NodeCategory.Ircv3 => "IRCv3",
        _ => c.ToString(),
    };

    private static string CategoryIcon(NodeCategory c) => c switch
    {
        NodeCategory.Event => Ircuitry.Core.Icons.Glyph("lightning"),
        NodeCategory.Filter => Ircuitry.Core.Icons.Glyph("question"),
        NodeCategory.Logic => Ircuitry.Core.Icons.Glyph("shuffle"),
        NodeCategory.Data => Ircuitry.Core.Icons.Glyph("hash"),
        NodeCategory.Ai => Ircuitry.Core.Icons.Glyph("robot"),
        NodeCategory.Storage => Ircuitry.Core.Icons.Glyph("floppy-disk"),
        NodeCategory.Code => Ircuitry.Core.Icons.Glyph("laptop"),
        NodeCategory.Action => Ircuitry.Core.Icons.Glyph("chat-circle"),
        NodeCategory.Ircv3 => Ircuitry.Core.Icons.Glyph("broadcast"),
        _ => Ircuitry.Core.Icons.Glyph("puzzle-piece"),
    };

    private void DrawPalette(Renderer r)
    {
        var p = _l.Palette;
        var content = new RectF(p.X + 6, p.Y + Hud.HeaderH + 2, p.W - 12, p.H - Hud.HeaderH - 8);
        _paletteScroll = Wheel("palette", _paletteScroll, content);

        // search field at the top; the community node/workflow links live at the BOTTOM of the panel
        float x = content.X + 8, w = content.W - 16;
        var searchRect = new RectF(x, content.Y + 8, w, 30);
        r.Begin(BlendMode.Alpha, content.ToRectangle());
        _paletteSearch = _ui.TextField("palette.search", searchRect, _paletteSearch, Ircuitry.Core.Icons.Glyph("magnifying-glass") + "  search nodes…");
        // contextual: only appears when a node is sitting in the clipboard, and names it
        float listTopY = searchRect.Bottom + 8;
        if (_clipNodeTitle != null)
        {
            string label = _clipNodeTitle.Length > 20 ? _clipNodeTitle[..19] + "…" : _clipNodeTitle;
            var clipRect = new RectF(x, searchRect.Bottom + 8, w, 32);
            if (_ui.Button("palette.clip", clipRect, Ircuitry.Core.Icons.Glyph("copy") + "  Install \"" + label + "\"", Theme.Amber, primary: true)) InstallFromClipboard();
            r.Text(r.Fonts.Get(FontKind.Sans, 9), "found in your clipboard", new Vector2(x + 6, clipRect.Bottom), Theme.TextFaint);
            listTopY = clipRect.Bottom + 16;
        }
        r.End();
        string q = _paletteSearch.Trim();
        bool searching = q.Length > 0;

        // two community links pinned to the bottom of the panel (nodes open the in-app manager; workflows open the gallery)
        int customCount = NodeCatalog.Custom.Count;
        float footY = content.Bottom - 102;
        r.Begin(BlendMode.Alpha, content.ToRectangle());
        if (_ui.Button("palette.build", new RectF(x, footY, w, 30), Ircuitry.Core.Icons.Glyph("cake") + "  Bake a node…", Theme.Violet, primary: true))
            OpenNodeBuilder();
        if (_ui.Button("palette.manage", new RectF(x, footY + 34, w, 30), customCount > 0 ? Ircuitry.Core.Icons.Glyph("puzzle-piece") + $"  Community nodes · {customCount}" : Ircuitry.Core.Icons.Glyph("puzzle-piece") + "  Community nodes", Theme.Berry))
            OpenNodeManager();
        if (_ui.Button("palette.workflows", new RectF(x, footY + 68, w, 30), Ircuitry.Core.Icons.Glyph("robot") + "  Community workflows " + Ircuitry.Core.Icons.Glyph("arrow-up-right"), Theme.Sky))
            Ircuitry.App.DeepLink.OpenUrl(WorkflowsUrl);
        r.End();

        var listClip = new RectF(content.X, listTopY, content.W, footY - 8 - listTopY);
        r.Begin(BlendMode.Alpha, listClip.ToRectangle());
        float y = listClip.Y - _paletteScroll;

        // a pinned/recent section drawn above the categories when not searching; click the header to collapse it
        void Section(string title, string icon, Color col, IEnumerable<NodeDef> defs)
        {
            var items = defs.ToList();
            if (items.Count == 0) return;
            bool collapsed = _collapsedSections.Contains(title);
            const float hh = 30f;
            var hdr = new RectF(x, y, w, hh);
            bool hHover = hdr.Contains(In.Mouse) && listClip.Contains(In.Mouse);
            r.RoundFill(hdr, Theme.Mix(Theme.PanelHi, col, hHover ? 0.28f : 0.16f), 10f);
            r.RoundOutline(hdr, Theme.WithAlpha(col, 0.35f), 10f);
            var icf = r.Fonts.Get(FontKind.Display, 14);
            r.Text(icf, icon, new Vector2(hdr.X + 10, hdr.Center.Y - icf.MeasureString(icon).Y / 2f), col);
            var nf = r.Fonts.Get(FontKind.SansBold, 13);
            r.Text(nf, title, new Vector2(hdr.X + 34, hdr.Center.Y - nf.MeasureString("M").Y / 2f - 1), Theme.Text);
            var chf = r.Fonts.Get(FontKind.SansBold, 12);
            r.Text(chf, collapsed ? Ircuitry.Core.Icons.Glyph("caret-right") : Ircuitry.Core.Icons.Glyph("caret-down"), new Vector2(hdr.Right - 18, hdr.Center.Y - chf.MeasureString("M").Y / 2f - 1), Theme.WithAlpha(Theme.Text, 0.55f));
            if (!Modal && In.LeftPressed && hHover) { if (!_collapsedSections.Remove(title)) _collapsedSections.Add(title); }
            y += hh + 7;
            if (collapsed) return;
            foreach (var def in items)
            {
                var chip = new RectF(x + 6, y, w - 6, 40);
                if (chip.Bottom > listClip.Y && chip.Y < listClip.Bottom) DrawPaletteChip(r, chip, def, Theme.Category(def.Category), listClip);
                y += 46;
            }
            y += 10;
        }

        if (!searching)
        {
            var favs = Ircuitry.Core.NodePrefs.Favorites.Select(t => NodeCatalog.TryGet(t, out var d) ? d : null).Where(d => d != null).Cast<NodeDef>();
            var favSet = Ircuitry.Core.NodePrefs.Favorites.ToHashSet();
            var recents = Ircuitry.Core.NodePrefs.Recents.Where(t => !favSet.Contains(t)).Take(6)
                .Select(t => NodeCatalog.TryGet(t, out var d) ? d : null).Where(d => d != null).Cast<NodeDef>();
            Section("Favorites", Ircuitry.Core.Icons.Glyph("star"), Theme.Amber, favs);
            Section("Recent", Ircuitry.Core.Icons.Glyph("clock"), Theme.Sky, recents);
        }

        foreach (var group in NodeCatalog.ByCategory())
        {
            var matches = searching
                ? group.Where(d => Has(d.Title, q) || Has(d.Subtitle, q) || Has(d.TypeId, q) || Has(d.Description, q)).ToList()
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
            r.Text(chf, collapsed ? Ircuitry.Core.Icons.Glyph("caret-right") : Ircuitry.Core.Icons.Glyph("caret-down"), new Vector2(hdr.Right - 18, hdr.Center.Y - chf.MeasureString("M").Y / 2f - 1), Theme.WithAlpha(Theme.Text, 0.55f));

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
        _paletteScroll = ClampScroll("palette", _paletteScroll, total, listClip.H);
    }

    private static bool Has(string s, string q) => s.Contains(q, StringComparison.OrdinalIgnoreCase);

    // ---- scrolling helpers (no over-scroll stutter) ----
    // The classic immediate-mode bug is clamping a scroll value only AFTER drawing, so a wheel notch at the
    // bottom draws one over-scrolled (jumpy) frame before snapping back. We clamp the wheel at input time
    // against the previous frame's measured max, so the value never exceeds the content - it just sits still.
    private readonly Dictionary<string, float> _scrollMax = new();
    private float Wheel(string id, float cur, RectF area)
    {
        if (area.Contains(In.Mouse) && In.ScrollDelta != 0)
            cur = Math.Clamp(cur - In.ScrollDelta * 0.4f, 0, _scrollMax.TryGetValue(id, out var m) ? m : cur);
        return cur;
    }
    private float ClampScroll(string id, float cur, float total, float viewH)
    {
        float max = MathF.Max(0, total - viewH);
        _scrollMax[id] = max;
        return Math.Clamp(cur, 0, max);
    }

    // Peek at the clipboard a couple of times a second so the palette can offer a one-click install
    // the moment a node is copied. Cheap: only parses when the text actually looks like a node manifest.
    private void ClipboardPoll(Clock clock)
    {
        if (clock.Time - _clipCheckAt < 0.5f) return;
        _clipCheckAt = clock.Time;
        _clipNodeTitle = null;
        try
        {
            var txt = (Ircuitry.Core.Clipboard.GetText() ?? "").Trim();
            if (txt.StartsWith("{") && txt.Length < 200000 && txt.Contains("\"typeId\""))
            {
                var d = Ircuitry.Graph.CustomNode.Load(txt);
                if (d != null) _clipNodeTitle = d.Title;
            }
        }
        catch { /* not a node - ignore */ }
    }

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
            r.Text(iconF, Ircuitry.Core.Icons.Glyph(def.Icon), new Vector2(chip.X + 13, chip.Center.Y - iconF.MeasureString(Ircuitry.Core.Icons.Glyph(def.Icon)).Y / 2f), col);
        }
        r.Text(r.Fonts.Get(FontKind.SansBold, 13), r.Ellipsize(r.Fonts.Get(FontKind.SansBold, 13), def.Title, chip.W - 70), new Vector2(chip.X + 42, chip.Y + 6), Theme.Text);
        r.Text(r.Fonts.Get(FontKind.Sans, 10), def.Subtitle, new Vector2(chip.X + 42, chip.Y + 23), Theme.TextFaint);

        // pin/unpin: a star at the right edge (shown on hover, or always when favourited)
        bool fav = Ircuitry.Core.NodePrefs.IsFavorite(def.TypeId);
        var starHit = new RectF(chip.Right - 30, chip.Y, 30, chip.H);
        bool starHover = starHit.Contains(In.Mouse) && content.Contains(In.Mouse);
        if (fav || hover)
        {
            var sf = r.Fonts.Get(FontKind.Display, 15);
            string star = Ircuitry.Core.Icons.Glyph("star");   // colour (amber vs faint) distinguishes pinned from not
            r.Text(sf, star, new Vector2(chip.Right - 24, chip.Center.Y - sf.MeasureString(star).Y / 2f), fav ? Theme.Amber : Theme.WithAlpha(Theme.Text, starHover ? 0.8f : 0.4f));
        }

        if (starHover && In.LeftPressed && !Modal) { Ircuitry.Core.NodePrefs.ToggleFavorite(def.TypeId); return; }   // pin toggles, never starts a drag
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
            r.Text(r.Fonts.Get(FontKind.SansBold, 15), Ircuitry.Core.Icons.Glyph(d.Icon) + "  " + d.Title, new Vector2(x, y), Theme.Text); y += 24;
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
    private const string NodeLibraryUrl = "https://ircuitry.github.io/nodes.html";
    private const string WorkflowsUrl = "https://ircuitry.github.io/workflows.html";
    private const string DocsUrl = "https://ircuitry.github.io/docs.html";

    private void OpenNodeManager()
    {
        if (Modal) return;
        _l = DockLayout();
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
        bx -= 138; if (_ui.Button("nm.browse", new RectF(bx, by, 138, bh), Ircuitry.Core.Icons.Glyph("globe") + " Browse library " + Ircuitry.Core.Icons.Glyph("arrow-up-right"), Theme.Lime)) Ircuitry.App.DeepLink.OpenUrl(NodeLibraryUrl);
        // only offer clipboard install when an actual .ircnode manifest is sitting in the clipboard, and name it
        if (_clipNodeTitle != null)
        {
            string cn = _clipNodeTitle.Length > 16 ? _clipNodeTitle[..15] + "…" : _clipNodeTitle;
            string clabel = Ircuitry.Core.Icons.Glyph("copy") + $"  Install {cn} (from clipboard)";
            float cw = r.Fonts.Get(FontKind.SansBold, 13).MeasureString(clabel).X + 30;
            bx -= gap + cw; if (_ui.Button("nm.paste", new RectF(bx, by, cw, bh), clabel, Theme.Cyan, primary: true)) { _nodeMgrOpen = false; InstallFromClipboard(); }
        }
        if (updates > 0) { bx -= gap + 138; if (_ui.Button("nm.updateall", new RectF(bx, by, 138, bh), Ircuitry.Core.Icons.Glyph("download-simple") + $" Update all ({updates})", Theme.Amber, primary: true)) foreach (var tid in _nodeMgrUpdates.Keys.ToList()) UpdateNode(tid); }
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
        if (_ui.Button("nm.remove", rmR, canRemove ? Ircuitry.Core.Icons.Glyph("trash") + $"  Remove selected ({sel})" : "Remove selected", canRemove ? Theme.Alert : Theme.Idle, primary: canRemove) && canRemove)
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
        _nodeMgrScroll = ClampScroll("nodeMgrScroll", Wheel("nodeMgrScroll", _nodeMgrScroll, rect), total, rect.H);

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
                if (selected) r.Text(tf, Ircuitry.Core.Icons.Glyph("check"), new Vector2(cb.Center.X - tf.MeasureString(Ircuitry.Core.Icons.Glyph("check")).X / 2f, cb.Center.Y - tf.MeasureString(Ircuitry.Core.Icons.Glyph("check")).Y / 2f), Theme.TextInk);

                var iconImg = def.IconImage != null ? r.IconTexture(def.TypeId, def.IconImage) : null;
                if (iconImg != null) r.Image(iconImg, new RectF(row.X + 40, row.Center.Y - 11, 22, 22));
                else r.Text(icf, Ircuitry.Core.Icons.Glyph(def.Icon), new Vector2(row.X + 40, row.Center.Y - icf.MeasureString(Ircuitry.Core.Icons.Glyph(def.Icon)).Y / 2f), col);

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
                    bool up = _ui.Button("nm.up." + def.TypeId, upR, Ircuitry.Core.Icons.Glyph("download-simple") + " Update", Theme.Amber);
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

    // ====================== in-app updater ======================
    public void StartUpdateCheck()
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var (status, body) = Ircuitry.Net.Http.Send("GET",
                    $"https://api.github.com/repos/{Ircuitry.App.AppInfo.Repo}/releases/latest",
                    System.Array.Empty<(string, string)>(), null);
                if (status < 200 || status >= 300) return;
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var root = doc.RootElement;
                string ver = (root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "").TrimStart('v', 'V');
                if (ver.Length == 0 || !IsNewer(ver, Ircuitry.App.AppInfo.Version)) return;
                string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                var kind = DetectInstall();
                string url = "", name = "";
                if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var a in arr.EnumerateArray())
                    {
                        string an = a.TryGetProperty("name", out var nn) ? nn.GetString() ?? "" : "";
                        if (!WantAsset(an, kind)) continue;
                        url = a.TryGetProperty("browser_download_url", out var uu) ? uu.GetString() ?? "" : "";
                        name = an; break;
                    }
                if (url.Length == 0) return;
                _upVer = ver; _upBody = notes; _upAssetUrl = url; _upAssetName = name; _upKind = kind;
                if (ver != _upSeenVer) { _upSeenVer = ver; _upPrompted = false; }   // a genuinely new version may prompt again
                _upState = UpState.Available;
            }
            catch { /* offline / rate-limited - just skip the check */ }
        });
    }

    // How this build was installed determines how it updates itself.
    private static InstallKind DetectInstall()
    {
        if ((Environment.GetEnvironmentVariable("APPIMAGE") ?? "").Length > 0) return InstallKind.AppImage;
        if (OperatingSystem.IsWindows()) return InstallKind.WinExe;
        string proc = Environment.ProcessPath ?? "";
        if (OperatingSystem.IsLinux() && (proc.StartsWith("/opt/ircuitry") || proc.StartsWith("/usr/"))) return InstallKind.Deb;
        return InstallKind.Portable;
    }

    private static bool WantAsset(string name, InstallKind kind) => kind switch
    {
        InstallKind.AppImage => name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase),
        InstallKind.Deb => name.EndsWith(".deb", StringComparison.OrdinalIgnoreCase),
        InstallKind.WinExe => name == "ircuitry-win-x64.exe",
        // portable: linux gets the runnable AppImage; mac gets its zip
        _ => OperatingSystem.IsMacOS()
            ? name == (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "ircuitry-osx-arm64.zip" : "ircuitry-osx-x64.zip")
            : name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase),
    };

    private static bool IsNewer(string a, string b)
    {
        var pa = ParseVer(a); var pb = ParseVer(b);
        for (int i = 0; i < 3; i++) if (pa[i] != pb[i]) return pa[i] > pb[i];
        return false;
    }
    private static int[] ParseVer(string v)
    {
        var p = new int[3]; var s = v.Split('.');
        for (int i = 0; i < 3 && i < s.Length; i++) int.TryParse(new string(s[i].TakeWhile(char.IsDigit).ToArray()), out p[i]);
        return p;
    }

    private static int RunQuiet(string file, string args)
    {
        try
        {
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, args)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p == null) return -1;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch { return -1; }
    }
    private static string EnsureDir(string d) { try { System.IO.Directory.CreateDirectory(d); } catch { } return d; }

    // runs every frame: re-check periodically, auto-ask once, and hand a finished download to the game thread
    private void UpdateTick(Clock clock)
    {
        // re-check GitHub every hour so a long-running instance still notices new releases
        if (_upCheckAt < 0) _upCheckAt = clock.Time;
        else if (clock.Time - _upCheckAt > 3600f && _upState != UpState.Downloading && _upState != UpState.Applying)
        { _upCheckAt = clock.Time; StartUpdateCheck(); }

        if (_upState == UpState.Available && !_upPrompted && !Modal && !_tut.Active)
        { _upPrompted = true; _upPromptOpen = true; _upPromptJustOpened = true; _upBodyScroll = 0; }

        // the worker set a relaunch path -> swap to the new build on the game thread (clean exit autosaves)
        if (_upReady && _upRelaunchPath.Length > 0)
        {
            _upReady = false;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(_upRelaunchPath) { UseShellExecute = false };
                psi.ArgumentList.Add("--relaunch");   // the new instance waits for our single-instance lock, then takes over
                System.Diagnostics.Process.Start(psi);
                OnExitRequested?.Invoke();
            }
            catch (Exception ex) { _upStatus = "Restart failed: " + ex.Message; _upState = UpState.Failed; }
        }
    }

    private void StartUpdateDownload()
    {
        if (_upState == UpState.Downloading || _upState == UpState.Applying) return;
        _upPromptOpen = false;
        _upState = UpState.Downloading; _upProgress = 0f; _upReady = false;
        _upStatus = "Downloading " + _upAssetName + " …";
        string url = _upAssetUrl, name = _upAssetName;
        var kind = _upKind;
        string appimage = Environment.GetEnvironmentVariable("APPIMAGE") ?? "";
        string procPath = Environment.ProcessPath ?? "";
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                string updir = System.IO.Path.Combine(home, "ircuitry", ".update");
                try { System.IO.Directory.CreateDirectory(updir); } catch { }
                bool auto = kind is InstallKind.AppImage or InstallKind.Deb or InstallKind.WinExe;
                string dest = auto ? System.IO.Path.Combine(updir, name)
                    : System.IO.Path.Combine(EnsureDir(System.IO.Path.Combine(home, "Downloads")), name);

                bool ok = Ircuitry.Net.Http.DownloadFile(url, dest, p => _upProgress = p);
                if (!ok) { _upStatus = "Download failed - check your connection"; _upState = UpState.Failed; return; }

                _upState = UpState.Applying; _upProgress = 1f;
                switch (kind)
                {
                    case InstallKind.AppImage:
                        _upStatus = "Installing and restarting…";
                        RunQuiet("chmod", "+x \"" + dest + "\"");
                        System.IO.File.Move(dest, appimage, true);
                        _upRelaunchPath = appimage; _upReady = true;
                        break;

                    case InstallKind.Deb:
                        _upStatus = "Installing (you may be asked for your password)…";
                        int code = RunQuiet("pkexec", "dpkg -i \"" + dest + "\"");
                        if (code != 0) { _upStatus = "Install was cancelled or failed"; _upState = UpState.Failed; break; }
                        _upRelaunchPath = System.IO.File.Exists("/usr/bin/ircuitry") ? "/usr/bin/ircuitry" : procPath;
                        _upReady = true;
                        break;

                    case InstallKind.WinExe:
                        _upStatus = "Installing and restarting…";
                        if (procPath.Length > 0)
                        {
                            try { System.IO.File.Delete(procPath + ".old"); } catch { }
                            System.IO.File.Move(procPath, procPath + ".old");   // a running .exe can be renamed on Windows
                            System.IO.File.Move(dest, procPath);
                            _upRelaunchPath = procPath; _upReady = true;
                        }
                        else { _upStatus = "Could not locate the app to replace"; _upState = UpState.Failed; }
                        break;

                    default:   // Portable / macOS: downloaded to Downloads, point the user at it
                        _upState = UpState.None;
                        Bot.Log.Add(LogLevel.System, "downloaded " + name + " to your Downloads folder - open it to finish updating");
                        Ircuitry.App.DeepLink.OpenUrl($"https://github.com/{Ircuitry.App.AppInfo.Repo}/releases/latest");
                        break;
                }
            }
            catch (Exception ex) { _upStatus = "Update failed: " + ex.Message; _upState = UpState.Failed; }
        });
    }

    private void DrawUpdatePrompt(Renderer r)
    {
        r.Begin();
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.5f));
        float pw = 560, ph = MathF.Min(480, _vh * 0.86f);
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        Hud.Panel(r, panel, "Update available", Theme.Lime);
        float x = panel.X + 22, w = panel.W - 44, y = panel.Y + Hud.HeaderH + 16;
        r.Text(r.Fonts.Get(FontKind.SansBold, 17), "ircuitry v" + _upVer + " is ready", new Vector2(x, y), Theme.Text); y += 26;
        r.Text(r.Fonts.Get(FontKind.Sans, 12), "You have v" + Ircuitry.App.AppInfo.Version + (UpAuto ? "  ·  installs automatically" : "  ·  downloads to your Downloads folder"), new Vector2(x, y), Theme.TextDim); y += 24;
        r.Text(r.Fonts.Get(FontKind.SansBold, 11), "WHAT'S NEW", new Vector2(x, y), Theme.TextFaint); y += 18;
        var box = new RectF(x, y, w, panel.Bottom - 56 - y);
        r.RoundFill(box, Theme.PanelLo, 7);
        r.End();

        var lines = new List<string>();
        foreach (var raw in _upBody.Replace("\r", "").Split('\n'))
        {
            var s = raw.TrimEnd();
            if (s.Trim().Length == 0) { lines.Add(""); continue; }
            foreach (var wl in Wrap(r.Fonts.Get(FontKind.Sans, 12), s, box.W - 22)) lines.Add(wl);
        }
        const float lh = 17f;
        float totalH = lines.Count * lh;
        _upBodyScroll = ClampScroll("upBodyScroll", Wheel("upBodyScroll", _upBodyScroll, box), totalH, box.H - 14);

        r.Begin(BlendMode.Alpha, box.ToRectangle());
        var bf = r.Fonts.Get(FontKind.Sans, 12);
        float ty = box.Y + 8 - _upBodyScroll;
        foreach (var line in lines) { if (ty + lh > box.Y && ty < box.Bottom && line.Length > 0) r.Text(bf, line, new Vector2(box.X + 11, ty), Theme.Text); ty += lh; }
        r.End();

        r.Begin();
        var goR = new RectF(panel.Right - 22 - 150, panel.Bottom - 48, 150, 34);
        var laterR = new RectF(goR.X - 12 - 110, panel.Bottom - 48, 110, 34);
        if (_ui.Button("up.changelog", new RectF(x, panel.Bottom - 48, 168, 34), Ircuitry.Core.Icons.Glyph("clipboard") + " Full changelog " + Ircuitry.Core.Icons.Glyph("arrow-up-right"), Theme.Idle))
            Ircuitry.App.DeepLink.OpenUrl($"https://github.com/{Ircuitry.App.AppInfo.Repo}/releases/tag/v{_upVer}");
        if (_ui.Button("up.later", laterR, "LATER", Theme.Idle)) _upPromptOpen = false;
        if (_ui.Button("up.go", goR, UpAuto ? Ircuitry.Core.Icons.Glyph("download-simple") + "  Update now" : Ircuitry.Core.Icons.Glyph("download-simple") + "  Download", Theme.Lime, primary: true)) StartUpdateDownload();
        if (In.LeftPressed && !panel.Contains(In.Mouse) && !_upPromptJustOpened) _upPromptOpen = false;
        _upPromptJustOpened = false;
        r.End();
    }

    private void DrawUpgradeOverlay(Renderer r, Clock clock)
    {
        float t = clock.Time;
        float cx = _vw / 2f, cy = _vh / 2f - 44f;
        var center = new Vector2(cx, cy);

        r.Begin(BlendMode.Alpha);
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Theme.Void, 0.98f));
        r.End();

        r.Begin(BlendMode.Add);
        r.Glow(center, 240f, Theme.WithAlpha(Theme.CyanDeep, 0.30f));
        r.Glow(center, 150f, Theme.WithAlpha(Theme.Amber, 0.10f));
        r.End();

        r.Begin(BlendMode.Alpha);
        const int N = 14; float head = t * 3.4f; const float ringR = 72f;
        for (int i = 0; i < N; i++)
        {
            float frac = i / (float)N; float ang = head - i * (MathF.PI * 2f / N);
            var pos = center + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * ringR;
            float tail = 1f - frac;
            r.Disc(pos, 1.5f + 3f * tail, Theme.WithAlpha(Theme.Cyan, 0.12f + 0.7f * tail * tail));
        }
        var cols = new[] { Theme.Cyan, Theme.Amber, Theme.Lime };
        for (int i = 0; i < 3; i++)
        {
            float ang = -t * 1.5f + i * (MathF.PI * 2f / 3f);
            r.Disc(center + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * (ringR + 24f), 5f, cols[i]);
        }
        if (r.Brand != null)
        {
            float sc = 88f / r.Brand.Width;
            r.Sb.Draw(r.Brand, center, null, Color.White, 0f, new Vector2(r.Brand.Width / 2f, r.Brand.Height / 2f), new Vector2(sc), Microsoft.Xna.Framework.Graphics.SpriteEffects.None, 0f);
        }
        r.End();

        r.Begin(BlendMode.Alpha);
        r.TextCenteredX(r.Fonts.Get(FontKind.Display, 30), "Updating ircuitry", cx, cy + 84f, Theme.Text);
        r.TextCenteredX(r.Fonts.Get(FontKind.SansBold, 15), "v" + Ircuitry.App.AppInfo.Version + "   " + Ircuitry.Core.Icons.Glyph("arrow-right") + "   v" + _upVer, cx, cy + 126f, Theme.CyanDim);

        float barW = 360f, barH = 10f;
        var track = new RectF(cx - barW / 2f, cy + 158f, barW, barH);
        r.RoundFill(track, Theme.PanelLo, barH / 2f);
        float pr = Math.Clamp(_upProgress, 0f, 1f);
        if (pr > 0.001f) r.RoundFill(new RectF(track.X, track.Y, MathF.Max(barH, barW * pr), barH), Theme.Cyan, barH / 2f);
        r.TextRight(r.Fonts.Get(FontKind.Mono, 11), (int)(pr * 100) + "%", track.Right, cy + 140f, Theme.TextFaint);
        r.TextCenteredX(r.Fonts.Get(FontKind.Sans, 12), _upStatus.Length > 0 ? _upStatus : "Working…", cx, cy + 178f, Theme.TextDim);

        if (_upBody.Length > 0)
        {
            float ly = cy + 212f;
            r.TextCenteredX(r.Fonts.Get(FontKind.SansBold, 12), "What's new", cx, ly, Theme.TextDim); ly += 22;
            var lf = r.Fonts.Get(FontKind.Sans, 12);
            int shown = 0;
            foreach (var raw in _upBody.Replace("\r", "").Split('\n'))
            {
                var line = raw.Trim().TrimStart('-', '*', ' ');
                if (line.Length == 0) continue;
                if (shown++ >= 5) break;
                r.TextCenteredX(lf, r.Ellipsize(lf, "•  " + line, 540f), cx, ly, Theme.TextFaint); ly += 18;
            }
        }
        r.End();
    }

    public void DebugShowUpdate()
    {
        _upVer = "9.9.9"; _upKind = InstallKind.AppImage;
        _upBody = "- Cosier Node Library with proper category headers\n- New community node manager with bulk uninstall and updates\n- One-click installs from the website via ircuitry://\n- Auto-update with this lovely overlay";
        _upState = UpState.Available; _upPrompted = true; _upPromptOpen = true; _upPromptJustOpened = true; _upBodyScroll = 0;
    }

    public void DebugShowUpgrade()
    {
        _upVer = "9.9.9"; _upKind = InstallKind.AppImage;
        _upBody = "- Cosier Node Library\n- Community node manager\n- Auto-update overlay";
        _upStatus = "Downloading ircuitry-9.9.9-x86_64.AppImage …"; _upProgress = 0.46f; _upState = UpState.Downloading;
    }

    private void UpdatePaletteDrag(Renderer r)
    {
        if (_dragDef == null) return;
        if (!In.Active || Modal) { _dragDef = null; _dragging = false; return; } // cancel on focus loss / modal - never spawn a phantom node
        if (Vector2.Distance(In.Mouse, _dragStart) > 6) _dragging = true;

        if (!In.LeftDown)
        {
            // the map is full-bleed under the panels now, so "on the canvas" must exclude the panels - dropping
            // on a panel (or just clicking a library entry) should land the node in the centre of the VISIBLE map
            bool overMap = _l.Canvas.Contains(In.Mouse) && !_dock.OverPanel(In.Mouse);
            bool spawned = false;
            if (overMap)
            {
                var wire = _editor.WireUnder(In.Mouse);   // dropping a node onto a wire splices it inline
                if (wire != null) _editor.SpliceOnWire(wire, _dragDef, _editor.Cam.ScreenToWorld(In.Mouse));
                else SpawnNode(_dragDef, _editor.Cam.ScreenToWorld(In.Mouse));
                spawned = true;
            }
            else if (!_dragging) { _editor.Spawn(_dragDef, _editor.Cam.ScreenToWorld(_dock.VisibleMapRect().Center)); spawned = true; }
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
    // The on-canvas slow-motion playback control: a compact pill BELOW the graph-title card (so it never covers
    // it) holding the toggle, a collapsed "speed" button, and a live / jump-to-now indicator. Clicking the speed
    // button drops a vertical slider; clicking away from it collapses again.
    private bool _pbSliderOpen;

    private void PlaybackLayout(out RectF pill, out RectF iconP, out RectF toggleR, out RectF delayBtn, out RectF slot, out RectF panel)
    {
        var c = _dock.VisibleMapRect();   // ride the visible map so a left-docked Library never covers the pill
        bool on = Ircuitry.Core.Playback.SlowMo;
        float slotW = on ? (_app.ActiveBot.Runtime.PlaybackPending >= 4 ? 116f : 56f) : 0f;
        const float h = 30, iconW = 22, toggleW = 100, delayW = 66, gap = 8;
        float w = 12 + iconW + toggleW + gap + delayW + (on ? gap + slotW : 0) + 12;
        float by = c.Y + 12 + 26 + 8;   // just below the title card
        pill = new RectF(c.X + 12, by, w, h);
        float x = c.X + 12 + 12;
        iconP = new RectF(x, by, iconW, h); x += iconW;
        toggleR = new RectF(x, by, toggleW, h); x += toggleW + gap;
        delayBtn = new RectF(x, by + 3, delayW, h - 6); x += delayW + gap;
        slot = new RectF(x, by + 4, slotW, h - 8);
        panel = new RectF(delayBtn.X - 1, pill.Bottom + 6, 60, 160);
    }

    private RectF PlaybackBarRect()
    {
        PlaybackLayout(out var pill, out _, out _, out _, out _, out var panel);
        if (!_pbSliderOpen) return pill;
        float minX = Math.Min(pill.X, panel.X), minY = Math.Min(pill.Y, panel.Y);
        return new RectF(minX, minY, Math.Max(pill.Right, panel.Right) - minX, Math.Max(pill.Bottom, panel.Bottom) - minY);
    }

    private void DrawPlaybackBar(Renderer r)
    {
        PlaybackLayout(out var pill, out var iconP, out var toggleR, out var delayBtn, out var slot, out var panel);
        r.Begin();
        r.RoundFill(new RectF(pill.X, pill.Y + 2, pill.W, pill.H), Theme.WithAlpha(Color.Black, 0.10f), 12f);
        r.RoundFill(pill, Theme.WithAlpha(Theme.PanelHi, 0.94f), 12f);
        r.RoundOutline(pill, Theme.Edge, 12f);
        float cy = pill.Center.Y;
        r.Text(r.Fonts.Get(FontKind.Display, 14), Ircuitry.Core.Icons.Glyph("film-strip"), new Vector2(iconP.X, cy - 10), Theme.Berry);

        Ircuitry.Core.Playback.SlowMo = _ui.Toggle("pb.slow", toggleR, Ircuitry.Core.Playback.SlowMo, "Slow-mo");

        // collapsed speed control: a button showing the current delay + a caret; click to drop the vertical slider
        bool dh = _ui.Enabled && delayBtn.Contains(In.Mouse);
        r.RoundFill(delayBtn, dh || _pbSliderOpen ? Theme.Mix(Theme.PanelHi, Theme.Cyan, 0.18f) : Theme.PanelLo, 8f);
        r.RoundOutline(delayBtn, _pbSliderOpen ? Theme.Cyan : Theme.Hairline, 8f);
        r.Text(r.Fonts.Get(FontKind.Mono, 12), Ircuitry.Core.Playback.Delay.ToString("0.00") + "s", new Vector2(delayBtn.X + 8, cy - 7), Theme.Text);
        r.Text(r.Fonts.Get(FontKind.SansBold, 11), Ircuitry.Core.Icons.Glyph(_pbSliderOpen ? "caret-up" : "caret-down"), new Vector2(delayBtn.Right - 15, cy - 7), Theme.TextDim);
        if (dh && In.LeftPressed) _pbSliderOpen = !_pbSliderOpen;

        if (Ircuitry.Core.Playback.SlowMo)
        {
            int pend = _app.ActiveBot.Runtime.PlaybackPending;
            if (pend >= 4)
            {
                if (_ui.Button("pb.jump", slot, Ircuitry.Core.Icons.Glyph("fast-forward") + " now (" + pend + ")", Theme.Amber, primary: true))
                    _app.ActiveBot.Runtime.PlaybackJumpToNow();
            }
            else
            {
                Hud.SoftDot(r, new Vector2(slot.Right - 12, cy), 4f, Theme.Ok);
                r.TextRight(r.Fonts.Get(FontKind.SansBold, 12), "live", slot.Right - 22, cy - 7, Theme.Mix(Theme.Text, Theme.Ok, 0.35f));
            }
        }
        r.End();

        if (_pbSliderOpen)
        {
            r.Begin();
            r.RoundFill(new RectF(panel.X, panel.Y + 2, panel.W, panel.H), Theme.WithAlpha(Color.Black, 0.12f), 11f);
            r.RoundFill(panel, Theme.WithAlpha(Theme.PanelHi, 0.97f), 11f);
            r.RoundOutline(panel, Theme.Edge, 11f);
            r.TextCenteredX(r.Fonts.Get(FontKind.Sans, 10), "slow", panel.Center.X, panel.Y + 8, Theme.TextFaint);
            var sl = new RectF(panel.X, panel.Y + 24, panel.W, panel.H - 56);
            Ircuitry.Core.Playback.Delay = _ui.SliderV("pb.delayv", sl, Ircuitry.Core.Playback.Delay, Ircuitry.Core.Playback.MinDelay, Ircuitry.Core.Playback.MaxDelay);
            r.TextCenteredX(r.Fonts.Get(FontKind.Sans, 10), "fast", panel.Center.X, panel.Bottom - 28, Theme.TextFaint);
            r.TextCenteredX(r.Fonts.Get(FontKind.MonoBold, 11), Ircuitry.Core.Playback.Delay.ToString("0.00") + "s", panel.Center.X, panel.Bottom - 15, Theme.Text);
            r.End();
            // click anywhere outside the pill + panel collapses it
            if (In.LeftPressed && !pill.Contains(In.Mouse) && !panel.Contains(In.Mouse)) _pbSliderOpen = false;
        }
    }

    // ---- snippet shelf: save a selection as a reusable fragment; click a chip to drop a fresh copy ----
    private void SaveSelectionSnippet()
    {
        var json = _editor.SerializeSelection();
        if (json == null) return;
        var first = _editor.Selection.Select(id => Bot.Graph.Find(id)).FirstOrDefault(x => x != null);
        int cnt = _editor.Selection.Count;
        string name = (first?.DisplayTitle ?? "snippet") + (cnt > 1 ? " +" + (cnt - 1) : "");
        var saved = Ircuitry.App.SnippetStore.Save(json, name);
        _shelfDirty = true;
        Notify(Ircuitry.Core.Icons.Glyph("bookmarks-simple") + " Saved snippet: " + saved);
    }

    private void DrawShelf(Renderer r)
    {
        if (!_shelfOpen || Modal) return;
        if (_shelfDirty) { _shelfSnips = Ircuitry.App.SnippetStore.List(); _shelfDirty = false; }
        var vis = _dock.VisibleMapRect();
        const float w = 210, rowH = 32;
        float h = Math.Min(vis.H - 70, 44 + Math.Max(1, _shelfSnips.Count) * (rowH + 4) + 8);
        var panel = new RectF(vis.X + 12, vis.Y + 56, w, h);
        r.Begin();
        r.RoundFill(panel.Offset(0, 3), Theme.WithAlpha(Color.Black, 0.14f), 11f);
        r.RoundFill(panel, Theme.WithAlpha(Theme.PanelHi, 0.97f), 11f);
        r.RoundOutline(panel, Theme.Edge, 11f);
        r.Text(r.Fonts.Get(FontKind.SansBold, 13), Ircuitry.Core.Icons.Glyph("bookmarks-simple") + "  Snippets", new Vector2(panel.X + 12, panel.Y + 11), Theme.Text);
        float ty = panel.Y + 40;
        if (_shelfSnips.Count == 0)
            foreach (var line in Wrap(r.Fonts.Get(FontKind.Sans, 12), "Select nodes, right-click -> Save as snippet.", w - 24))
            { r.Text(r.Fonts.Get(FontKind.Sans, 12), line, new Vector2(panel.X + 12, ty), Theme.TextFaint); ty += 16; }
        foreach (var (name, path) in _shelfSnips)
        {
            var row = new RectF(panel.X + 6, ty, w - 12, rowH);
            if (row.Bottom > panel.Bottom - 4) break;
            bool hot = !Modal && row.Contains(In.Mouse);
            var del = new RectF(row.Right - 24, row.Y + 5, 20, rowH - 10);
            bool delHot = del.Contains(In.Mouse);
            r.RoundFill(row, hot ? Theme.WithAlpha(Theme.Berry, 0.16f) : Theme.PanelLo, 8f);
            var lf = r.Fonts.Get(FontKind.SansBold, 12);
            r.Text(lf, r.Ellipsize(lf, name, w - 50), new Vector2(row.X + 10, row.Center.Y - 7), Theme.Text);
            r.Text(lf, Ircuitry.Core.Icons.Glyph("x"), new Vector2(del.X + 5, del.Center.Y - 7), delHot ? Theme.Alert : Theme.TextFaint);
            if (hot && In.LeftPressed)
            {
                if (delHot) { Ircuitry.App.SnippetStore.Delete(path); _shelfDirty = true; }
                else { _editor.InsertSnippet(Ircuitry.App.SnippetStore.Read(path), _editor.Cam.ScreenToWorld(vis.Center)); _app.MarkDirty(); }
            }
            ty += rowH + 4;
        }
        r.End();
    }

    // ---- error tray (#15): runtime errors attributed to the node that threw, click a row to jump there ----
    private void DrawErrorTray(Renderer r)
    {
        if (!_errTrayOpen || Modal) return;
        if (Bot.IsRemote) { _errTrayOpen = false; return; }
        var errs = Bot.Runtime.Errors();
        var vis = _dock.VisibleMapRect();
        const float w = 340, rowH = 50, head = 38;
        float bodyH = Math.Min(vis.H - 120, head + Math.Max(1, errs.Count) * (rowH + 4) + 10);
        var panel = new RectF(vis.Right - w - 14, vis.Bottom - bodyH - 14, w, bodyH);
        r.Begin();
        r.RoundFill(panel.Offset(0, 3), Theme.WithAlpha(Color.Black, 0.16f), 11f);
        r.RoundFill(panel, Theme.WithAlpha(Theme.PanelHi, 0.98f), 11f);
        r.RoundOutline(panel, Theme.WithAlpha(Theme.Alert, 0.6f), 11f);
        r.Text(r.Fonts.Get(FontKind.SansBold, 13), Ircuitry.Core.Icons.Glyph("warning-octagon") + "  Errors", new Vector2(panel.X + 12, panel.Y + 11), Theme.Text);
        var clr = new RectF(panel.Right - 64, panel.Y + 7, 56, 22);
        if (errs.Count > 0 && _ui.Button("errtray.clear", clr, "Clear", Theme.Idle)) { Bot.Runtime.ClearErrors(); _errTrayOpen = false; }

        var content = new RectF(panel.X + 6, panel.Y + head, w - 12, bodyH - head - 6);
        if (errs.Count == 0)
        {
            r.Text(r.Fonts.Get(FontKind.Sans, 12), "No errors. A node that throws will show up here.", new Vector2(panel.X + 12, content.Y + 4), Theme.TextFaint);
            r.End();
            return;
        }
        if (!Modal) _errTrayScroll = Wheel("errtray", _errTrayScroll, content);
        float total = errs.Count * (rowH + 4);
        _errTrayScroll = ClampScroll("errtray", _errTrayScroll, total, content.H);
        r.End();

        r.Begin(BlendMode.Alpha, content.ToRectangle());
        float ty = content.Y - _errTrayScroll;
        var tf = r.Fonts.Get(FontKind.SansBold, 12);
        var mf = r.Fonts.Get(FontKind.Sans, 11);
        var sf = r.Fonts.Get(FontKind.Mono, 10);
        foreach (var e in errs)
        {
            var row = new RectF(content.X, ty, content.W, rowH);
            if (row.Bottom >= content.Y && row.Y <= content.Bottom)
            {
                bool hot = !Modal && row.Contains(In.Mouse) && content.Contains(In.Mouse);
                r.RoundFill(row, hot ? Theme.WithAlpha(Theme.Alert, 0.14f) : Theme.PanelLo, 8f);
                r.RoundFill(new RectF(row.X, row.Y + 6, 3, rowH - 12), Theme.Alert, 1.5f);
                r.Text(tf, r.Ellipsize(tf, e.Title, w - 64), new Vector2(row.X + 12, row.Y + 7), Theme.Text);
                if (e.Count > 1)
                {
                    string cnt = "x" + e.Count;
                    var cb = new RectF(row.Right - 8 - (sf.MeasureString(cnt).X + 12), row.Y + 7, sf.MeasureString(cnt).X + 12, 15);
                    r.RoundFill(cb, Theme.WithAlpha(Theme.Alert, 0.22f), 7f);
                    r.Text(sf, cnt, new Vector2(cb.X + 6, cb.Y + 2), Theme.Alert);
                }
                r.Text(mf, r.Ellipsize(mf, e.Message, w - 28), new Vector2(row.X + 12, row.Y + 26), Theme.TextDim);
                if (hot && In.LeftPressed)
                {
                    _editor.PulseNode(e.NodeId, _l.Canvas);
                    _editor.Selection.Clear();
                    if (Bot.Graph.Nodes.Any(n => n.Id == e.NodeId)) _editor.Selection.Add(e.NodeId);
                }
            }
            ty += rowH + 4;
        }
        r.End();
    }

    // ---- find-in-graph (#6): a floating search bar that jumps the camera to matching nodes ----
    private void RecomputeFind()
    {
        _findHits.Clear();
        string q = _findQuery.Trim().ToLowerInvariant();
        if (q.Length == 0) { _findIdx = 0; return; }
        foreach (var n in Bot.Graph.Nodes)
        {
            bool hit = n.DisplayTitle.ToLowerInvariant().Contains(q)
                || n.TypeId.ToLowerInvariant().Contains(q)
                || n.Def.Title.ToLowerInvariant().Contains(q)
                || n.Params.Any(kv => kv.Value.ToLowerInvariant().Contains(q) || kv.Key.ToLowerInvariant().Contains(q));
            if (hit) _findHits.Add(n.Id);
        }
        _findIdx = Math.Clamp(_findIdx, 0, Math.Max(0, _findHits.Count - 1));
    }

    private void FindJump(int dir)
    {
        if (_findHits.Count == 0) return;
        _findIdx = ((_findIdx + dir) % _findHits.Count + _findHits.Count) % _findHits.Count;
        _editor.Reveal(_findHits[_findIdx], _l.Canvas);
    }

    private void DrawFindBar(Renderer r)
    {
        if (!_findOpen || Modal) return;
        var vis = _dock.VisibleMapRect();
        const float w = 330, h = 34;
        var bar = new RectF(vis.Center.X - w / 2f, vis.Y + 12, w, h);
        r.Begin();
        r.RoundFill(bar.Offset(0, 2), Theme.WithAlpha(Color.Black, 0.12f), 10f);
        r.RoundFill(bar, Theme.WithAlpha(Theme.PanelHi, 0.97f), 10f);
        r.RoundOutline(bar, Theme.Edge, 10f);
        r.Text(r.Fonts.Get(FontKind.Sans, 15), Ircuitry.Core.Icons.Glyph("magnifying-glass"), new Vector2(bar.X + 10, bar.Center.Y - 9), Theme.TextDim);
        if (_findArm) { _ui.Focus = "find"; _findArm = false; }
        var q = _ui.TextField("find", new RectF(bar.X + 32, bar.Center.Y - 11, w - 150, 22), _findQuery, "find nodes by name, type or value");
        if (q != _findQuery) { _findQuery = q; RecomputeFind(); }
        var cf = r.Fonts.Get(FontKind.Mono, 12);
        string cnt = _findQuery.Trim().Length == 0 ? "" : _findHits.Count == 0 ? "0" : (_findIdx + 1) + "/" + _findHits.Count;
        r.TextRight(cf, cnt, bar.Right - 86, bar.Center.Y - 7, _findHits.Count == 0 && _findQuery.Trim().Length > 0 ? Theme.Alert : Theme.TextDim);
        if (_ui.Button("find.prev", new RectF(bar.Right - 76, bar.Y + 5, 22, h - 10), Ircuitry.Core.Icons.Glyph("caret-up"), Theme.Idle)) FindJump(-1);
        if (_ui.Button("find.next", new RectF(bar.Right - 52, bar.Y + 5, 22, h - 10), Ircuitry.Core.Icons.Glyph("caret-down"), Theme.Idle)) FindJump(1);
        if (_ui.Button("find.close", new RectF(bar.Right - 28, bar.Y + 5, 22, h - 10), Ircuitry.Core.Icons.Glyph("x"), Theme.Idle)) { _findOpen = false; if (_ui.Focus == "find") _ui.Focus = null; }
        r.End();
        if (In.EnterPressed) { FindJump(In.Shift ? -1 : 1); _findArm = true; }   // Enter cycles + keeps the box focused
        if (In.KeyPressed(Keys.F3)) FindJump(In.Shift ? -1 : 1);
        if (In.KeyPressed(Keys.Escape)) { _findOpen = false; if (_ui.Focus == "find") _ui.Focus = null; }
    }

    private void DrawInspector(Renderer r)
    {
        var p = _l.Inspector;
        var content = new RectF(p.X + 4, p.Y + Hud.HeaderH + 2, p.W - 8, p.H - Hud.HeaderH - 6);
        // reset scroll when the inspected thing changes (node id, or which server is selected)
        string key = (_editor.SelectedFrame?.Id ?? _editor.SelectedSingle?.Id ?? "conn") + ":" + Bot.SelectedServer;
        if (key != _inspKey) { _inspKey = key; _inspScroll = 0; }
        if (!Modal) _inspScroll = Wheel("insp", _inspScroll, content);

        r.Begin(BlendMode.Alpha, content.ToRectangle());
        var scrolled = content.Offset(0, -_inspScroll);   // shift content up by the scroll amount; the scissor still clips to the panel
        var sel = _editor.SelectedSingle;
        float bottom = _editor.SelectedFrame is { } fr ? DrawFrameInspector(r, scrolled, fr)
                     : sel != null ? DrawNodeInspector(r, scrolled, sel)
                     : DrawConnectionInspector(r, scrolled);
        r.End();

        // clamp scroll to the content height we just measured
        float total = bottom - scrolled.Y;
        _inspScroll = ClampScroll("insp", _inspScroll, total, content.H);

        // a slim scrollbar when there's overflow
        if (total > content.H)
        {
            float track = content.H, thumb = MathF.Max(28f, track * content.H / total);
            float ty = content.Y + (_inspScroll / (total - content.H)) * (track - thumb);
            r.Begin();
            r.RoundFill(new RectF(content.Right - 4, ty, 3, thumb), Theme.WithAlpha(Theme.Text, 0.28f), 1.5f);
            r.End();
        }
    }

    /// <summary>A clickable "Obby · advanced" header; returns the new expanded state. Advances y.</summary>
    private bool ObbyHeader(Renderer r, ref float y, float x, float w, bool expanded)
    {
        var hdr = new RectF(x, y, w, 24);
        bool hover = !Modal && hdr.Contains(In.Mouse);
        r.Text(r.Fonts.Get(FontKind.SansBold, 12), (expanded ? Ircuitry.Core.Icons.Glyph("caret-down") + "  " : Ircuitry.Core.Icons.Glyph("caret-right") + "  ") + "OBBY · ADVANCED (IRCv3 bot-tools)",
            new Vector2(x, y + 4), hover ? Theme.Text : Theme.TextFaint);
        y += 26;
        return hover && In.LeftPressed ? !expanded : expanded;
    }

    private float DrawFrameInspector(Renderer r, RectF c, Ircuitry.Graph.Frame f)
    {
        float x = c.X + 14, w = c.W - 28, y = c.Y + 14;
        r.Disc(new Vector2(x + 5, y + 11), 5f, Theme.Tag(f.ColorIndex));
        r.Text(r.Fonts.Get(FontKind.SansBold, 18), "Sticky note", new Vector2(x + 18, y), Theme.Text); y += 26;
        r.Text(r.Fonts.Get(FontKind.Mono, 11), "annotation · does not run", new Vector2(x + 18, y), Theme.TextFaint); y += 24;

        r.Text(r.Fonts.Get(FontKind.SansBold, 12), "TITLE", new Vector2(x, y), Theme.TextDim); y += 18;
        var nt = _ui.TextField("frame.title", new RectF(x, y, w, 30), f.Title, "Note");
        if (nt != f.Title) { f.Title = nt; _app.MarkDirty(); }
        y += 38;

        r.Text(r.Fonts.Get(FontKind.SansBold, 12), "TEXT", new Vector2(x, y), Theme.TextDim); y += 18;
        var nb = _ui.TextArea("frame.body", new RectF(x, y, w, 120), f.Body, "what this part of the workflow does…");
        if (nb != f.Body) { f.Body = nb; _app.MarkDirty(); }
        y += 130;

        r.Text(r.Fonts.Get(FontKind.SansBold, 12), "COLOUR", new Vector2(x, y), Theme.TextDim); y += 18;
        { float sx = x; for (int t = 0; t < Theme.TagCount; t++) { var sr = new RectF(sx, y, 22, 22); sx += 28; r.RoundFill(sr, Theme.Tag(t), 6f); if (f.ColorIndex == t) r.RoundOutline(sr.Inflate(2, 2), Theme.Text, 8f); if (!Modal && sr.Contains(In.Mouse) && In.LeftPressed && f.ColorIndex != t) { f.ColorIndex = t; _app.MarkDirty(); } } y += 34; }

        if (_ui.Button("frame.collapse", new RectF(x, y, w / 2 - 4, 32), Ircuitry.Core.Icons.Glyph(f.Collapsed ? "caret-down" : "caret-up") + (f.Collapsed ? "  Expand" : "  Collapse"), Theme.Idle)) { f.Collapsed = !f.Collapsed; _app.MarkDirty(); }
        if (_ui.Button("frame.delete", new RectF(x + w / 2 + 4, y, w / 2 - 4, 32), Ircuitry.Core.Icons.Glyph("trash") + "  Delete", Theme.Alert)) { _editor.DeleteFrame(f.Id); _app.MarkDirty(); }
        y += 42;
        return y;
    }

    private float DrawNodeInspector(Renderer r, RectF c, Node n)
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

        // colour tag - group a subsystem at a glance (rendered as a stripe on the card + minimap)
        r.Text(r.Fonts.Get(FontKind.SansBold, 12), "TAG", new Vector2(x, y), Theme.TextDim); y += 18;
        {
            const float sw = 22, gap = 6;
            float sx = x;
            var noneR = new RectF(sx, y, sw, sw);
            r.RoundOutline(noneR, n.ColorTag < 0 ? Theme.Text : Theme.Edge, 6f);
            r.TextCentered(r.Fonts.Get(FontKind.SansBold, 11), Ircuitry.Core.Icons.Glyph("x"), noneR, Theme.TextDim);
            if (!Modal && noneR.Contains(In.Mouse) && In.LeftPressed && n.ColorTag != -1) { n.ColorTag = -1; _app.MarkDirty(); }
            sx += sw + gap + 6;
            for (int t = 0; t < Theme.TagCount; t++)
            {
                var sr = new RectF(sx, y, sw, sw); sx += sw + gap;
                r.RoundFill(sr, Theme.Tag(t), 6f);
                if (n.ColorTag == t) r.RoundOutline(sr.Inflate(2, 2), Theme.Text, 8f);
                if (!Modal && sr.Contains(In.Mouse) && In.LeftPressed && n.ColorTag != t) { n.ColorTag = t; _app.MarkDirty(); }
            }
            y += sw + 12;
        }

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
            if (pdef.Key == "server" && Bot.Servers.Count <= 1) continue;     // the route override only matters with several servers
            r.Text(lf, pdef.Label.ToUpperInvariant(), new Vector2(x, y), Theme.TextDim);
            y += 18;
            string cur = n.GetParam(pdef.Key);
            string id = $"p.{n.Id}.{pdef.Key}";
            if (pdef.Secret)   // credentials are picked, never typed
            {
                SecretButton(r, id, ref y, x, w, cur, pdef.Label, v => { n.SetParam(pdef.Key, v); _app.MarkDirty(); });
                continue;
            }
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
                case ParamType.List:
                    next = DrawListParam(r, id, ref y, x, w, cur, pdef.Pair, pdef.AddLabel); break;
                default:
                    next = _ui.TextField(id, new RectF(x, y, w, 30), cur, pdef.Placeholder); y += 40; break;
            }
            if (next != cur)
            {
                n.SetParam(pdef.Key, next);
                // a param can change a node's dynamic pins (e.g. Switch cases); drop any wire left dangling
                if (n.Def.DynOutputs != null || n.Def.DynInputs != null) Bot.Graph.PruneDeadWires();
                _app.MarkDirty();
            }

            string hint = ParamHint(pdef.Key, next);
            if (hint.Length > 0)
                foreach (var line in Wrap(r.Fonts.Get(FontKind.Sans, 10), Ircuitry.Core.Icons.Glyph("warning") + " " + hint, w))
                { r.Text(r.Fonts.Get(FontKind.Sans, 10), line, new Vector2(x, y - 2), Theme.Amber); y += 13; }
        }

        y += 8;
        // dry-run just this node and report whether it ran (and its output)
        if (_ui.Button("insp.testnode." + n.Id, new RectF(x, y, w, 30), Ircuitry.Core.Icons.Glyph("test-tube") + " Test this node", Theme.Cyan))
        { _nodeTestId = n.Id; _nodeTestResult = TestNode(n); }
        y += 36;
        if (_nodeTestId == n.Id && _nodeTestResult.Length > 0)
        {
            foreach (var line in Wrap(r.Fonts.Get(FontKind.Sans, 11), _nodeTestResult, w))
            { r.Text(r.Fonts.Get(FontKind.Sans, 11), line, new Vector2(x, y), Theme.TextDim); y += 14; }
            y += 6;
        }

        if (!_tut.Active && _ui.Button("insp.del", new RectF(x, y, w, 32), "×  DELETE NODE", Theme.Alert))
        { Bot.Graph.Remove(n); _editor.Selection.Clear(); _app.MarkDirty(); }
        return y + 40;
    }

    // A gentle, non-blocking hint for a param value that looks off (never prevents anything).
    private string ParamHint(string key, string val)
    {
        val = (val ?? "").Trim();
        if (val.Length == 0 || val.StartsWith("{")) return "";   // blank or a {token}/template - don't second-guess
        if (key is "channel" or "channels")
        {
            var toks = val.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (toks.Length > 0 && toks.All(t => !t.StartsWith("#") && !t.StartsWith("&") && !t.StartsWith("{")))
                return "channels usually start with #";
        }
        if (key == "server" && Bot.Servers.Count > 1)
        {
            bool known = Bot.Servers.Any(s => string.Equals(s.DisplayName, val, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.Label, val, StringComparison.OrdinalIgnoreCase) || string.Equals(s.Host, val, StringComparison.OrdinalIgnoreCase));
            if (!known) return $"no server “{val}” on this bot (blank = reply on the origin server)";
        }
        return "";
    }

    /// <summary>Dry-run the graph from each trigger with the test-bench event and report whether this node ran.</summary>
    private string TestNode(Node n)
    {
        var graph = Bot.Graph;
        var sink = new TestSink(new Dictionary<string, string>(Bot.State));
        var baseVars = new Dictionary<string, string>
        {
            ["botnick"] = Bot.Settings.Nick, ["nick"] = _testNick, ["user"] = "tester", ["host"] = "test.host",
            ["channel"] = _testChan, ["target"] = _testChan, ["message"] = _testMsg, ["replyto"] = _testChan,
            ["args"] = "", ["command"] = "", ["account"] = "", ["isbot"] = "false", ["msgid"] = "test-msg", ["__reply"] = "test-msg",
        };
        NodeTrace? found = null;
        foreach (var trig in graph.Nodes.Where(t => t.Def.IsTrigger))
        {
            var rec = new RunRecord { Time = DateTime.Now, Trigger = trig.DisplayTitle, Icon = trig.Def.Icon };
            try { GraphExecutor.Fire(graph, sink, trig, new Dictionary<string, string>(baseVars), rec); } catch { }
            found = rec.Nodes.FirstOrDefault(x => x.NodeId == n.Id);
            if (found != null) break;
        }
        if (found == null)
            return Ircuitry.Core.Icons.Glyph("x") + $" didn't run for a test message “{_testMsg}”. Open " + Ircuitry.Core.Icons.Glyph("test-tube") + " TEST to change the event, or check the wires/filters above it.";
        string outs = found.Outputs.Count > 0 ? "  ·  out: " + string.Join(", ", found.Outputs.Select(o => $"{o.pin}={Trunc(o.value, 40)}")) : "";
        string fired = found.Pulsed.Count > 0 ? "  ·  " + Ircuitry.Core.Icons.Glyph("arrow-right") + " " + string.Join(", ", found.Pulsed) : "";
        return Ircuitry.Core.Icons.Glyph("check") + $" ran{outs}{fired}";
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    // A growable list param: one row to start, an "Add" button to add more, and a per-row remove.
    private string DrawListParam(Renderer r, string idBase, ref float y, float x, float w, string cur, bool pair, string addLabel)
    {
        var rows = Ircuitry.Core.ParamList.Parse(cur);
        if (rows.Count == 0) rows.Add(pair ? new[] { "", "" } : new[] { "" });
        int remove = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            float rx = x;
            float btnX = x + w - 26;
            if (pair)
            {
                float kw = (btnX - x - 6) * 0.42f, vw = (btnX - x - 6) - kw - 6;
                string k = _ui.TextField($"{idBase}.k{i}", new RectF(rx, y, kw, 28), rows[i].ElementAtOrDefault(0) ?? "", "key");
                string v = _ui.TextField($"{idBase}.v{i}", new RectF(rx + kw + 6, y, vw, 28), rows[i].ElementAtOrDefault(1) ?? "", "value");
                rows[i] = new[] { k, v };
            }
            else
            {
                string v = _ui.TextField($"{idBase}.v{i}", new RectF(rx, y, btnX - x - 6, 28), rows[i].ElementAtOrDefault(0) ?? "", "value");
                rows[i] = new[] { v };
            }
            if (_ui.Button($"{idBase}.x{i}", new RectF(btnX, y, 26, 28), "×", Theme.Idle)) remove = i;
            y += 34;
        }
        if (remove >= 0 && remove < rows.Count) rows.RemoveAt(remove);
        if (_ui.Button($"{idBase}.add", new RectF(x, y, w, 26), "+  " + addLabel, Theme.Cyan)) rows.Add(pair ? new[] { "", "" } : new[] { "" });
        y += 36;
        return Ircuitry.Core.ParamList.Encode(rows, pair);
    }

    private float DrawConnectionInspector(Renderer r, RectF c)
    {
        var s = Bot.Settings;
        float x = c.X + 14, w = c.W - 28, y = c.Y + 14;

        y = Labeled(r, "BOT NAME", x, y);
        var nm = _ui.TextField("c.name", new RectF(x, y, w, 30), Bot.Name, "bot name");
        if (nm != Bot.Name) { Bot.Name = string.IsNullOrWhiteSpace(nm) ? Bot.Name : nm; _app.MarkDirty(); }
        y += 38;
        // reusable saved servers (the bots/servers/channels map lives globally in the title bar now)
        if (_ui.Button("c.servers", new RectF(x, y, w, 28), Ircuitry.Core.Icons.Glyph("broadcast") + " Servers", Theme.Sky)) { _serversOpen = true; _serversJustOpened = true; _serverSaveName = Bot.Name; }
        y += 40;

        // ---- server selector: a bot can hold several servers; pick which one to edit ----
        r.Text(r.Fonts.Get(FontKind.SansBold, 12), "SERVERS  ·  the graph reacts to all of them", new Vector2(x, y), Theme.TextDim); y += 18;
        var chipFont = r.Fonts.Get(FontKind.Sans, 12);
        float chipH = 26, cx = x, cy = y;
        for (int i = 0; i < Bot.Servers.Count; i++)
        {
            var sv = Bot.Servers[i];
            string lbl = sv.DisplayName;
            float cw = MathF.Min(w, chipFont.MeasureString(lbl).X + 30);
            if (cx + cw > x + w + 0.5f) { cx = x; cy += chipH + 6; }
            var chip = new RectF(cx, cy, cw, chipH);
            bool selected = i == Bot.SelectedServer;
            if (_ui.Button($"c.sv.{i}", chip, lbl, selected ? Theme.Cyan : Theme.Idle, primary: selected)) Bot.SelectedServer = i;
            var conn = Bot.Runtime.FindConn(sv.DisplayName);
            Hud.SoftDot(r, new Vector2(chip.X + 7, chip.Y + 7), 3.5f, conn != null ? StatusColor(conn) : Theme.Idle);
            cx += cw + 6;
        }
        if (cx + 30 > x + w + 0.5f) { cx = x; cy += chipH + 6; }
        if (_ui.Button("c.sv.add", new RectF(cx, cy, 30, chipH), "+", Theme.Lime))
        { Bot.Servers.Add(new IrcSettings { Nick = Ircuitry.Core.BakeryNames.Random() }); Bot.SelectedServer = Bot.Servers.Count - 1; _app.MarkDirty(); }
        y = cy + chipH + 12;

        r.Text(r.Fonts.Get(FontKind.SansBold, 16), "IRC Connection", new Vector2(x, y), Theme.Text); y += 24;
        var selConn = Bot.Runtime.FindConn(s.DisplayName);
        var (slabel, scol) = ServerStatus(selConn);
        Hud.SoftDot(r, new Vector2(x + 5, y + 7), 4f, scol);
        r.Text(r.Fonts.Get(FontKind.Mono, 12), slabel, new Vector2(x + 16, y), scol); y += 22;
        var selCaps = selConn?.EnabledCaps;
        if (selCaps is { Count: > 0 })
            foreach (var line in Wrap(r.Fonts.Get(FontKind.Mono, 10), "caps: " + string.Join(' ', selCaps), w))
            { r.Text(r.Fonts.Get(FontKind.Mono, 10), line, new Vector2(x, y), Theme.TextFaint); y += 13; }
        y += 6;
        r.HLine(x, c.Right - 14, y, Theme.Hairline, 1f); y += 12;

        y = Labeled(r, "LABEL (optional)", x, y);
        s.Label = Edit("c.label", new RectF(x, y, w, 30), s.Label, s.Host.Length > 0 ? s.Host : "home, work, …"); y += 40;

        y = Labeled(r, "SERVER", x, y);
        var host = _ui.TextField("c.host", new RectF(x, y, w - 78, 30), s.Host, "irc.libera.chat");
        var port = _ui.IntField("c.port", new RectF(x + w - 70, y, 70, 30), s.Port, 1, 65535);
        if (host != s.Host) { s.Host = host; _app.MarkDirty(); }
        if (port != s.Port) { s.Port = port; _app.MarkDirty(); }
        y += 40;

        r.Text(r.Fonts.Get(FontKind.SansBold, 12), "SECURITY", new Vector2(x, y), Theme.TextDim); y += 18;
        var tls = _ui.Toggle("c.tls", new RectF(x, y, w, 24), s.UseTls, "Use TLS"); if (tls != s.UseTls) { s.UseTls = tls; _app.MarkDirty(); } y += 28;
        var cert = _ui.Toggle("c.cert", new RectF(x, y, w, 24), s.AcceptInvalidCerts, "Accept self-signed"); if (cert != s.AcceptInvalidCerts) { s.AcceptInvalidCerts = cert; _app.MarkDirty(); } y += 28;
        var recon = _ui.Toggle("c.recon", new RectF(x, y, w, 24), s.AutoReconnect, "Auto-reconnect"); if (recon != s.AutoReconnect) { s.AutoReconnect = recon; _app.MarkDirty(); } y += 28;
        var onstart = _ui.Toggle("c.onstart", new RectF(x, y, w, 24), s.ConnectOnStartup, "Connect on app startup"); if (onstart != s.ConnectOnStartup) { s.ConnectOnStartup = onstart; _app.MarkDirty(); } y += 34;

        y = Labeled(r, "NICK", x, y);
        s.Nick = Edit("c.nick", new RectF(x, y, w - 38, 30), s.Nick, "BananaBread66");
        if (_ui.Button("c.nick.gen", new RectF(x + w - 34, y, 34, 30), Ircuitry.Core.Icons.Glyph("dice-five"), Theme.Berry)) { s.Nick = Ircuitry.Core.BakeryNames.Random(); _app.MarkDirty(); }
        y += 40;
        float idW = (w - 8) / 2f;
        Labeled(r, "IDENT", x, y); Labeled(r, "REAL NAME", x + idW + 8, y); y += 18;
        s.User = Edit("c.ident", new RectF(x, y, idW, 30), s.User, "ircuitry"); s.RealName = Edit("c.real", new RectF(x + idW + 8, y, idW, 30), s.RealName, "ircuitry bot"); y += 40;
        y = Labeled(r, "CHANNELS", x, y); s.Channels = Edit("c.chan", new RectF(x, y, w, 30), s.Channels, "#chan1 #chan2"); y += 40;
        y = Labeled(r, "SASL ACCOUNT (optional)", x, y); s.SaslUser = Edit("c.sasluser", new RectF(x, y, w, 30), s.SaslUser, "account"); y += 40;
        y = Labeled(r, "SASL PASSWORD", x, y); SecretButton(r, "c.saslpass", ref y, x, w, s.SaslPass, "SASL password", v => { s.SaslPass = v; _app.MarkDirty(); }); y += 4;

        _obbyConn = ObbyHeader(r, ref y, x, w, _obbyConn);
        if (_obbyConn)
        {
            var bm = _ui.Toggle("c.botmode", new RectF(x, y, w, 24), s.BotMode, "Bot mode +B"); if (bm != s.BotMode) { s.BotMode = bm; _app.MarkDirty(); } y += 28;
            var adv = _ui.Toggle("c.adv", new RectF(x, y, w, 24), s.AdvertiseCommands, "Advertise slash commands"); if (adv != s.AdvertiseCommands) { s.AdvertiseCommands = adv; _app.MarkDirty(); } y += 28;
            var sw = _ui.Toggle("c.sw", new RectF(x, y, w, 24), s.StreamWorkflows, "Stream tool workflows"); if (sw != s.StreamWorkflows) { s.StreamWorkflows = sw; _app.MarkDirty(); } y += 30;
        }
        y += 6;

        // per-server connect/disconnect (this server only) + remove, when a bot holds several
        if (Bot.Servers.Count > 1)
        {
            bool thisOn = selConn?.Running == true;
            float bw = (w - 8) / 2f;
            if (_ui.Button("c.svrun", new RectF(x, y, bw, 30), thisOn ? Ircuitry.Core.Icons.Glyph("square") + " Disconnect this" : Ircuitry.Core.Icons.Glyph("caret-right") + " Connect this", thisOn ? Theme.Alert : Theme.Sky))
            {
                if (thisOn) Bot.Runtime.DisconnectServer(s.DisplayName);
                else Bot.Runtime.ConnectServer(Bot.Graph, s);
            }
            if (_ui.Button("c.svdel", new RectF(x + bw + 8, y, bw, 30), Ircuitry.Core.Icons.Glyph("trash") + " Remove server", Theme.Idle))
            {
                Bot.Runtime.DisconnectServer(s.DisplayName);
                Bot.Servers.RemoveAt(Bot.SelectedServer);
                Bot.SelectedServer = Math.Clamp(Bot.SelectedServer, 0, Bot.Servers.Count - 1);
                _app.MarkDirty();
            }
            y += 38;
        }

        bool runningNow = RunningOf(Bot);
        if (_ui.Button("c.run", new RectF(x, y, w, 34),
                runningNow ? Ircuitry.Core.Icons.Glyph("square") + "  STOP BOT" : (Bot.IsRemote ? Ircuitry.Core.Icons.Glyph("play") + "  RUN ON SERVER" : Bot.Servers.Count > 1 ? Ircuitry.Core.Icons.Glyph("play") + "  RUN ALL SERVERS" : Ircuitry.Core.Icons.Glyph("play") + "  RUN BOT"),
                runningNow ? Theme.Alert : Theme.Cyan, primary: true))
            ToggleRun();
        return y + 42;
    }

    /// <summary>Status label+colour for one server connection (or offline when it isn't live).</summary>
    private static (string label, Color color) ServerStatus(Ircuitry.Runtime.ServerConn? c) => c?.State switch
    {
        IrcState.Connecting => ("CONNECTING", Theme.Warn),
        IrcState.Registering => ("REGISTERING", Theme.Warn),
        IrcState.Connected => ("LIVE " + Ircuitry.Core.Icons.Glyph("caret-right") + " " + c.CurrentNick, Theme.Ok),
        IrcState.Error => ("ERROR", Theme.Alert),
        _ => c?.Running == true ? ("STARTING", Theme.Warn) : ("OFFLINE", Theme.Idle),
    };

    private string Edit(string id, RectF rect, string value, string ph, bool password = false)
    {
        var next = _ui.TextField(id, rect, value, ph, password: password);
        if (next != value) _app.MarkDirty();
        return next;
    }

    // ====================== secret picker (credentials) ======================
    private static readonly System.Text.RegularExpressions.Regex SecretRef =
        new(@"^\{\{\s*secret\.([^}\s]+)\s*\}\}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    // A password field is never typed in the clear: it shows the chosen key and opens the picker.
    private void SecretButton(Renderer r, string id, ref float y, float x, float w, string cur, string label, Action<string> apply)
    {
        var m = SecretRef.Match(cur ?? "");
        string disp = m.Success ? Ircuitry.Core.Icons.Glyph("key") + "  " + m.Groups[1].Value
            : string.IsNullOrEmpty(cur) ? Ircuitry.Core.Icons.Glyph("key") + "  Choose a key…"
            : Ircuitry.Core.Icons.Glyph("key") + "  •••• (tap to secure)";
        if (_ui.Button(id + ".sec", new RectF(x, y, w, 30), disp, m.Success ? Theme.Lime : Theme.Idle))
            OpenSecretPicker(cur ?? "", label, apply);
        y += 40;
    }

    private void OpenSecretPicker(string current, string title, Action<string> apply)
    {
        _secretPickApply = apply; _secretPickTitle = title;
        _secretPickOpen = true; _secretPickJustOpened = true;
        _secretPickName = ""; _secretPickNewVal = ""; _secretPickScroll = 0;
    }

    private void DrawSecretPicker(Renderer r)
    {
        r.Begin();
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.5f));
        float pw = 480, ph = MathF.Min(560, _vh * 0.9f);
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        Hud.Panel(r, panel, "Choose a secret · " + _secretPickTitle, Theme.Violet);
        float x = panel.X + 20, w = panel.W - 40, y = panel.Y + Hud.HeaderH + 14;
        foreach (var line in Wrap(r.Fonts.Get(FontKind.Sans, 12), "Credentials live in your separate secrets file, never in the workspace or in shared flows. Pick an existing key or add one now.", w))
        { r.Text(r.Fonts.Get(FontKind.Sans, 12), line, new Vector2(x, y), Theme.TextDim); y += 16; }
        y += 6;

        var names = Ircuitry.Core.Secrets.Names();
        r.Text(r.Fonts.Get(FontKind.SansBold, 11), names.Count > 0 ? "YOUR KEYS" : "NO KEYS YET", new Vector2(x, y), Theme.TextFaint); y += 20;
        float listH = MathF.Min(names.Count * 34f, 170f);
        var listRect = new RectF(x, y, w, MathF.Max(0, listH));
        r.End();
        if (listH > 0)
        {
            _secretPickScroll = ClampScroll("secretPickScroll", Wheel("secretPickScroll", _secretPickScroll, listRect), names.Count * 34f, listH);
            r.Begin(BlendMode.Alpha, listRect.ToRectangle());
            float ly = listRect.Y - _secretPickScroll;
            foreach (var name in names)
            {
                if (ly + 30 >= listRect.Y && ly <= listRect.Bottom)
                    if (_ui.Button("sp.k." + name, new RectF(listRect.X, ly, w, 30), Ircuitry.Core.Icons.Glyph("key") + "  " + name, Theme.Lime))
                    { _secretPickApply?.Invoke("{{secret." + name + "}}"); _secretPickOpen = false; }
                ly += 34;
            }
            r.End();
        }
        y = listRect.Bottom + 12;

        r.Begin();
        r.HLine(x, panel.Right - 20, y, Theme.Hairline, 1f); y += 12;
        r.Text(r.Fonts.Get(FontKind.SansBold, 11), "ADD A NEW KEY", new Vector2(x, y), Theme.TextFaint); y += 20;
        _secretPickName = _ui.TextField("sp.name", new RectF(x, y, w, 28), _secretPickName, "name e.g. openai"); y += 34;
        _secretPickNewVal = _ui.TextField("sp.val", new RectF(x, y, w, 28), _secretPickNewVal, "value (key/password)", password: true); y += 36;
        bool canAdd = _secretPickName.Trim().Length > 0;
        if (_ui.Button("sp.add", new RectF(x, y, w, 30), "+  Add & use this key", canAdd ? Theme.Lime : Theme.Idle, primary: canAdd) && canAdd)
        {
            var nm = _secretPickName.Trim();
            Ircuitry.Core.Secrets.Set(nm, _secretPickNewVal);
            _secretPickApply?.Invoke("{{secret." + nm + "}}");
            _secretPickOpen = false;
        }
        y += 38;

        var clearR = new RectF(x, panel.Bottom - 46, 110, 32);
        var cancelR = new RectF(panel.Right - 20 - 100, panel.Bottom - 46, 100, 32);
        if (_ui.Button("sp.clear", clearR, "Use none", Theme.Idle)) { _secretPickApply?.Invoke(""); _secretPickOpen = false; }
        if (_ui.Button("sp.cancel", cancelR, "CANCEL", Theme.Cyan)) _secretPickOpen = false;
        if (In.LeftPressed && !panel.Contains(In.Mouse) && !_secretPickJustOpened) _secretPickOpen = false;
        _secretPickJustOpened = false;
        r.End();
    }

    // ====================== saved servers + network map ======================
    private void ApplyServer(Ircuitry.Core.ServerProfile p)
    {
        var s = Bot.Settings;
        s.Host = p.Host; s.Port = p.Port; s.UseTls = p.UseTls;
        s.AcceptInvalidCerts = p.AcceptInvalidCerts; s.AutoReconnect = p.AutoReconnect;
        if (p.Nick.Length > 0) s.Nick = p.Nick;
        if (p.Channels.Length > 0) s.Channels = p.Channels;
        s.SaslUser = p.SaslUser; s.SaslPass = p.SaslPass;
        _app.MarkDirty();
        Bot.Log.Add(LogLevel.System, $"loaded server '{p.Name}' ({p.Host}:{p.Port})");
    }

    private void SaveCurrentAsServer(string name)
    {
        var s = Bot.Settings;
        Ircuitry.Core.Servers.Save(new Ircuitry.Core.ServerProfile
        {
            Name = name.Trim(), Host = s.Host, Port = s.Port, UseTls = s.UseTls,
            AcceptInvalidCerts = s.AcceptInvalidCerts, AutoReconnect = s.AutoReconnect,
            Nick = s.Nick, Channels = s.Channels, SaslUser = s.SaslUser, SaslPass = s.SaslPass,
        });
        Bot.Log.Add(LogLevel.System, $"saved server '{name.Trim()}'");
    }

    private void DrawServersModal(Renderer r)
    {
        r.Begin();
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.5f));
        float pw = 540, ph = MathF.Min(560, _vh * 0.9f);
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        Hud.Panel(r, panel, "Saved servers", Theme.Sky);
        float x = panel.X + 20, w = panel.W - 40, y = panel.Y + Hud.HeaderH + 14;
        var list = Ircuitry.Core.Servers.All();
        r.Text(r.Fonts.Get(FontKind.Sans, 12), list.Count == 0 ? "No saved servers yet. Save this bot's connection below to reuse it." : "Reuse a server across bots. Passwords stay in your secrets file.", new Vector2(x, y), Theme.TextDim);
        y += 24;

        float saveY = panel.Bottom - 92;
        var listRect = new RectF(x, y, w, saveY - y - 10);
        r.RoundFill(listRect, Theme.PanelLo, 8); r.RoundOutline(listRect, Theme.Edge, 8);
        r.End();

        _serversScroll = ClampScroll("serversScroll", Wheel("serversScroll", _serversScroll, listRect), list.Count * 50f, listRect.H);
        r.Begin(BlendMode.Alpha, listRect.ToRectangle());
        float ly = listRect.Y + 4 - _serversScroll;
        foreach (var p in list)
        {
            var row = new RectF(listRect.X + 4, ly, listRect.W - 8, 46);
            if (row.Bottom >= listRect.Y && row.Y <= listRect.Bottom)
            {
                r.RoundFill(row, Theme.Panel, 7); r.RoundOutline(row, Theme.Hairline, 7);
                r.Text(r.Fonts.Get(FontKind.SansBold, 13), p.Name, new Vector2(row.X + 12, row.Y + 6), Theme.Text);
                r.Text(r.Fonts.Get(FontKind.Mono, 10), $"{p.Host}:{p.Port}{(p.UseTls ? " · TLS" : "")}{(p.Channels.Length > 0 ? " · " + p.Channels : "")}", new Vector2(row.X + 12, row.Y + 25), Theme.TextDim);
                if (_ui.Button("sv.use." + p.Name, new RectF(row.Right - 70 - 34, row.Y + 9, 70, 28), "Use", Theme.Lime) && listRect.Contains(In.Mouse))
                { ApplyServer(p); _serversOpen = false; }
                if (_ui.Button("sv.del." + p.Name, new RectF(row.Right - 30, row.Y + 9, 28, 28), "×", Theme.Idle) && listRect.Contains(In.Mouse))
                { Ircuitry.Core.Servers.Delete(p.Name); }
            }
            ly += 50;
        }
        r.End();

        r.Begin();
        r.Text(r.Fonts.Get(FontKind.SansBold, 11), "SAVE THIS BOT'S CONNECTION", new Vector2(x, saveY), Theme.TextFaint);
        _serverSaveName = _ui.TextField("sv.name", new RectF(x, saveY + 18, w - 120, 30), _serverSaveName, "server name");
        bool canSave = _serverSaveName.Trim().Length > 0 && Bot.Settings.Host.Length > 0;
        if (_ui.Button("sv.save", new RectF(x + w - 112, saveY + 18, 112, 30), Ircuitry.Core.Icons.Glyph("floppy-disk") + " Save", canSave ? Theme.Sky : Theme.Idle, primary: canSave) && canSave)
            SaveCurrentAsServer(_serverSaveName);
        if (_ui.Button("sv.close", new RectF(panel.Right - 20 - 100, panel.Bottom - 44, 100, 32), "CLOSE", Theme.Cyan, primary: true)) _serversOpen = false;
        if (In.LeftPressed && !panel.Contains(In.Mouse) && !_serversJustOpened) _serversOpen = false;
        _serversJustOpened = false;
        r.End();
    }

    // A live node-link graph of the whole setup: bot nodes -> server nodes -> channel nodes, wired by
    // connection (status-coloured) and membership. One node per bot, per unique server (host:port), per
    // channel-on-a-server, so shared servers read as hubs. Cozy node cards in the app's own style.
    private void DrawNetworkModal(Renderer r)
    {
        r.Begin();
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.5f));
        float pw = MathF.Min(980, _vw * 0.95f), ph = MathF.Min(700, _vh * 0.92f);
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        Hud.Panel(r, panel, "Network · bots, servers & channels", Theme.Berry);

        // ---- build the model ----
        var servers = new List<(string key, string host, int port, bool tls)>();
        int ServerIdx(IrcSettings sv)
        {
            string host = sv.Host.Length > 0 ? sv.Host : "(no server)";
            string key = host + "|" + sv.Port;
            int idx = servers.FindIndex(s => s.key == key);
            if (idx < 0) { servers.Add((key, host, sv.Port, sv.UseTls)); idx = servers.Count - 1; }
            return idx;
        }
        var channels = new List<(int server, string name)>();
        int ChannelIdx(int server, string name)
        {
            int idx = channels.FindIndex(c => c.server == server && c.name == name);
            if (idx < 0) { channels.Add((server, name)); idx = channels.Count - 1; }
            return idx;
        }
        var botServer = new List<(int bot, int server, Color col)>();
        var serverChan = new HashSet<(int server, int chan)>();
        var serverOn = new HashSet<int>();
        for (int bi = 0; bi < _app.Bots.Count; bi++)
            foreach (var sv in _app.Bots[bi].Servers)
            {
                int si = ServerIdx(sv);
                var conn = _app.Bots[bi].Runtime.FindConn(sv.DisplayName);
                botServer.Add((bi, si, conn != null ? StatusColor(conn) : Theme.Idle));
                if (conn?.Running == true) serverOn.Add(si);
                foreach (var ch in sv.Channels.Split(new[] { ' ', ',', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    serverChan.Add((si, ChannelIdx(si, ch)));
            }

        int online = _app.Bots.Count(b => b.Runtime.Running);
        r.TextRight(r.Fonts.Get(FontKind.Mono, 11), $"{_app.Bots.Count} bots · {online} online · {servers.Count} servers · {channels.Count} channels", panel.Right - 18, panel.Y + 14, Theme.TextFaint);
        r.End();

        // ---- geometry ----
        float labelY = panel.Y + Hud.HeaderH + 12;
        var area = new RectF(panel.X + 18, labelY + 22, panel.W - 36, panel.Bottom - labelY - 22 - 52);
        const float botW = 160, srvW = 178, chanW = 150, botH = 44, srvH = 48, chanH = 30, gap = 16;
        float botX = area.X, srvX = area.X + (area.W - srvW) / 2f, chanX = area.Right - chanW;

        if (servers.Count == 0)
        {
            r.Begin();
            r.Text(r.Fonts.Get(FontKind.SansBold, 15), "No servers yet - add one from a bot's connection settings.", new Vector2(area.X, area.Y + 30), Theme.TextDim);
            if (_ui.Button("nw.close", new RectF(panel.Right - 16 - 100, panel.Bottom - 44, 100, 32), "CLOSE", Theme.Cyan, primary: true)) _networkOpen = false;
            if (In.LeftPressed && !panel.Contains(In.Mouse) && !_networkJustOpened) _networkOpen = false;
            _networkJustOpened = false;
            r.End();
            return;
        }

        // order to keep wires from tangling: bots near their first server, channels grouped by server
        var botOrder = Enumerable.Range(0, _app.Bots.Count).Where(bi => botServer.Any(e => e.bot == bi))
            .OrderBy(bi => botServer.Where(e => e.bot == bi).Min(e => e.server)).ThenBy(bi => bi).ToList();
        var chanOrder = Enumerable.Range(0, channels.Count).OrderBy(ci => channels[ci].server).ThenBy(ci => channels[ci].name).ToList();

        float botColH = botOrder.Count * botH + Math.Max(0, botOrder.Count - 1) * gap;
        float srvColH = servers.Count * srvH + Math.Max(0, servers.Count - 1) * gap;
        float chanColH = chanOrder.Count * chanH + Math.Max(0, chanOrder.Count - 1) * gap;
        float graphH = Math.Max(botColH, Math.Max(srvColH, chanColH));
        float top = area.Y + Math.Max(0, (area.H - graphH) / 2f) - _networkScroll;

        var botY = new float[_app.Bots.Count];
        var srvY = new float[servers.Count];
        var chanY = new float[channels.Count];
        for (int i = 0; i < botOrder.Count; i++) botY[botOrder[i]] = top + (graphH - botColH) / 2f + i * (botH + gap);
        for (int j = 0; j < servers.Count; j++) srvY[j] = top + (graphH - srvColH) / 2f + j * (srvH + gap);
        for (int k = 0; k < chanOrder.Count; k++) chanY[chanOrder[k]] = top + (graphH - chanColH) / 2f + k * (chanH + gap);

        r.Begin();
        var lf = r.Fonts.Get(FontKind.SansBold, 11);
        r.Text(lf, "BOTS", new Vector2(botX + 2, labelY), Theme.TextDim);
        r.Text(lf, "SERVERS", new Vector2(srvX + 2, labelY), Theme.TextDim);
        r.Text(lf, "CHANNELS", new Vector2(chanX + 2, labelY), Theme.TextDim);
        r.End();

        // ---- wires (under nodes), then node cards, clipped to the scroll area ----
        r.Begin(BlendMode.Alpha, area.ToRectangle());
        foreach (var (bi, si, col) in botServer)
            r.BezierLine(new Vector2(botX + botW, botY[bi] + botH / 2f), new Vector2(srvX, srvY[si] + srvH / 2f), Theme.WithAlpha(col, 0.85f), 2.6f);
        foreach (var (si, ci) in serverChan)
            r.BezierLine(new Vector2(srvX + srvW, srvY[si] + srvH / 2f), new Vector2(chanX, chanY[ci] + chanH / 2f), Theme.WithAlpha(Theme.Violet, 0.5f), 2.2f);

        string Fit(DynamicSpriteFont f, string s, float maxW)
        {
            if (f.MeasureString(s).X <= maxW) return s;
            while (s.Length > 1 && f.MeasureString(s + "…").X > maxW) s = s.Substring(0, s.Length - 1);
            return s + "…";
        }
        void Card(RectF box, Color accent, string icon, string title, string sub, Color? dot)
        {
            r.RoundFill(new RectF(box.X, box.Y + 3, box.W, box.H), Theme.WithAlpha(Color.Black, 0.10f), 12);
            r.RoundFill(box, Color.Lerp(Theme.Panel, accent, 0.10f), 12);
            r.RoundOutline(box, Theme.WithAlpha(accent, 0.7f), 12);
            float ix = box.X + 12, midY = box.Y + box.H / 2f;
            if (dot.HasValue) { Hud.SoftDot(r, new Vector2(ix, midY), 4.5f, dot.Value); ix += 15; }
            if (icon.Length > 0) { r.Text(r.Fonts.Get(FontKind.Display, 15), icon, new Vector2(ix, midY - 10), accent); ix += 23; }
            float tw = box.Right - 10 - ix;
            if (sub.Length > 0)
            {
                r.Text(r.Fonts.Get(FontKind.SansBold, 13), Fit(r.Fonts.Get(FontKind.SansBold, 13), title, tw), new Vector2(ix, box.Y + 6), Theme.Text);
                r.Text(r.Fonts.Get(FontKind.Mono, 10), Fit(r.Fonts.Get(FontKind.Mono, 10), sub, tw), new Vector2(ix, box.Y + box.H - 16), Theme.TextDim);
            }
            else r.Text(r.Fonts.Get(FontKind.SansBold, 13), Fit(r.Fonts.Get(FontKind.SansBold, 13), title, tw), new Vector2(ix, midY - 8), Theme.Text);
        }
        bool Vis(float y, float h) => y + h >= area.Y && y <= area.Bottom;
        for (int j = 0; j < servers.Count; j++)
            if (Vis(srvY[j], srvH))
                Card(new RectF(srvX, srvY[j], srvW, srvH), Theme.Sky, Ircuitry.Core.Icons.Glyph("broadcast"), servers[j].host,
                    servers[j].host == "(no server)" ? "set a server" : $":{servers[j].port}{(servers[j].tls ? "  ·  TLS" : "")}", serverOn.Contains(j) ? Theme.Ok : Theme.Idle);
        foreach (int bi in botOrder)
            if (Vis(botY[bi], botH))
                Card(new RectF(botX, botY[bi], botW, botH), Theme.Lime, Ircuitry.Core.Icons.Glyph("robot"), _app.Bots[bi].Name, _app.Bots[bi].Runtime.Running ? "online" : "offline", StatusColor(_app.Bots[bi].Runtime));
        foreach (int ci in chanOrder)
            if (Vis(chanY[ci], chanH))
                Card(new RectF(chanX, chanY[ci], chanW, chanH), Theme.Violet, "", channels[ci].name, "", null);
        r.End();

        // ---- living speech bubbles: the latest line in each channel pops above its node and fades ----
        var now = DateTime.Now;
        var latest = new Dictionary<string, RecentMsg>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in _app.Bots)
            foreach (var m in b.Runtime.RecentMessages(40))
            {
                if (m.At == default || m.Channel.Length == 0 || !m.Channel.StartsWith('#')) continue;
                if (!latest.TryGetValue(m.Channel, out var cur) || m.At > cur.At) latest[m.Channel] = m;
            }
        r.Begin(BlendMode.Alpha, area.ToRectangle());
        foreach (int ci in chanOrder)
        {
            if (!Vis(chanY[ci], chanH) || !latest.TryGetValue(channels[ci].name, out var m)) continue;
            float age = (float)(now - m.At).TotalSeconds;
            if (age > 7f) continue;
            float a = Math.Clamp(1.5f - age / 5f, 0f, 1f);   // hold bright, then fade out
            DrawSpeechBubble(r, new RectF(chanX, chanY[ci], chanW, chanH), m.Nick, m.Text, a);
        }
        r.End();

        _networkScroll = ClampScroll("networkScroll", Wheel("networkScroll", _networkScroll, area), graphH + 12, area.H);

        r.Begin();
        if (_ui.Button("nw.close", new RectF(panel.Right - 16 - 100, panel.Bottom - 44, 100, 32), "CLOSE", Theme.Cyan, primary: true)) _networkOpen = false;
        if (In.LeftPressed && !panel.Contains(In.Mouse) && !_networkJustOpened) _networkOpen = false;
        _networkJustOpened = false;
        r.End();
    }

    /// <summary>A cozy thought-bubble above a channel card showing the latest line, fading by recency.</summary>
    private void DrawSpeechBubble(Renderer r, RectF card, string nick, string text, float alpha)
    {
        var fn = r.Fonts.Get(FontKind.SansBold, 11);
        var fb = r.Fonts.Get(FontKind.Sans, 11);
        string label = (nick.Length > 0 ? nick : "*") + "  ";
        string body = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        const float maxW = 244;
        float nameW = fn.MeasureString(label).X;
        string shown = r.Ellipsize(fb, body, maxW - nameW - 18);
        float bw = Math.Min(maxW, nameW + fb.MeasureString(shown).X + 18);
        float bh = 24;
        float bx = Math.Clamp(card.Center.X - bw / 2f, card.X - 34, card.Right - bw + 34);
        var box = new RectF(bx, card.Y - bh - 10, bw, bh);

        // thought-bubble trail (two shrinking dots from the card up to the bubble)
        r.Disc(new Vector2(card.Center.X, card.Y - 3), 3.4f, Theme.WithAlpha(Theme.PanelHi, alpha));
        r.Ring(new Vector2(card.Center.X, card.Y - 3), 3.4f, Theme.WithAlpha(Theme.Violet, 0.55f * alpha));
        r.Disc(new Vector2(card.Center.X - 2, box.Bottom + 2), 2.1f, Theme.WithAlpha(Theme.PanelHi, alpha));

        r.RoundFill(new RectF(box.X, box.Y + 2, box.W, box.H), Theme.WithAlpha(Color.Black, 0.10f * alpha), 9f);
        r.RoundFill(box, Theme.WithAlpha(Theme.PanelHi, alpha), 9f);
        r.RoundOutline(box, Theme.WithAlpha(Theme.Violet, 0.7f * alpha), 9f);
        float tx = box.X + 9, ty = box.Center.Y - 7;
        r.Text(fn, label, new Vector2(tx, ty), Theme.WithAlpha(Theme.Mix(Theme.Text, Theme.Violet, 0.45f), alpha));
        r.Text(fb, shown, new Vector2(tx + nameW, ty), Theme.WithAlpha(Theme.Text, alpha));
    }

    // ====================== achievements ======================
    private void AchievementsTick(Clock clock)
    {
        double dt = _achLastTick < 0 ? 0 : Math.Max(0, clock.Time - _achLastTick);
        _achLastTick = clock.Time;
        if (dt > 0 && dt < 5)   // ignore big gaps (the app was paused/asleep)
            foreach (var b in _app.Bots)
                if (b.Runtime.Running)
                {
                    Ircuitry.Core.Achievements.AddOnline(b.Name, dt);
                    if (b.Runtime.EnabledCaps.Count > 0) Ircuitry.Core.Achievements.AddCaps(b.Runtime.EnabledCaps);   // unlock cap specs
                }

        if (clock.Time - _achEvalAt < 2f) return;
        _achEvalAt = clock.Time;
        foreach (var d in Ircuitry.Core.Achievements.Evaluate()) _achToasts.Enqueue(d);
        Ircuitry.Core.Achievements.Save();
    }

    private void DrawAchToast(Renderer r, Clock clock)
    {
        if (_achCur == null && _achToasts.Count > 0) { _achCur = _achToasts.Dequeue(); _achCurUntil = clock.Time + 4.6f; }
        if (_achCur == null) return;
        float left = _achCurUntil - clock.Time;
        if (left <= 0) { _achCur = null; return; }

        float pw = 320, ph = 78;
        float appear = Math.Min(1f, (4.6f - left) / 0.3f);     // slide in
        float leave = Math.Min(1f, left / 0.3f);               // slide out
        float slide = (1f - Math.Min(appear, leave)) * (pw + 30);
        var panel = new RectF(_vw - pw - 18 + slide, 70, pw, ph);

        r.Begin(BlendMode.Add);
        r.Glow(new Vector2(panel.X + 38, panel.Center.Y), 70f, Theme.WithAlpha(Theme.Amber, 0.25f));
        r.End();
        r.Begin();
        r.RoundFill(panel, Theme.PanelHi, 14); r.RoundOutline(panel, Theme.Amber, 14);
        r.Fill(new RectF(panel.X, panel.Y + 12, 4, panel.H - 24), Theme.Amber);
        var tile = new RectF(panel.X + 14, panel.Center.Y - 22, 44, 44);
        r.RoundFill(tile, Theme.WithAlpha(Theme.Amber, 0.22f), 11);
        var icf = r.Fonts.Get(FontKind.Display, 24);
        r.Text(icf, Ircuitry.Core.Icons.Glyph(_achCur.Icon), new Vector2(tile.Center.X - icf.MeasureString(Ircuitry.Core.Icons.Glyph(_achCur.Icon)).X / 2f, tile.Center.Y - icf.MeasureString(Ircuitry.Core.Icons.Glyph(_achCur.Icon)).Y / 2f), Theme.Text);
        r.Text(r.Fonts.Get(FontKind.SansBold, 11), Ircuitry.Core.Icons.Glyph("trophy") + " ACHIEVEMENT UNLOCKED", new Vector2(tile.Right + 12, panel.Y + 14), Theme.AmberDim);
        r.Text(r.Fonts.Get(FontKind.SansBold, 16), r.Ellipsize(r.Fonts.Get(FontKind.SansBold, 16), _achCur.Title, pw - 80), new Vector2(tile.Right + 12, panel.Y + 32), Theme.Text);
        r.Text(r.Fonts.Get(FontKind.Sans, 11), _achCur.Category, new Vector2(tile.Right + 12, panel.Y + 54), Theme.TextDim);
        r.End();
    }

    // ---- unified notifications (cozy toasts + a bell history dropdown) ----
    /// <summary>Show a cozy toast and record it in the notification history.</summary>
    public void PushToast(string msg)
    {
        if (string.IsNullOrWhiteSpace(msg)) return;
        _toasts.Enqueue(msg);
        _notifLog.Insert(0, (DateTime.Now, msg));
        while (_notifLog.Count > 100) _notifLog.RemoveAt(_notifLog.Count - 1);
        _notifUnread++;
    }

    private void DrawToast(Renderer r, Clock clock)
    {
        if (_toastCur == null && _toasts.Count > 0) { _toastCur = _toasts.Dequeue(); _toastUntil = clock.Time + 3.8f; }
        if (_toastCur == null) return;
        float life = 3.8f, leftT = _toastUntil - clock.Time;
        if (leftT <= 0) { _toastCur = null; return; }

        float pw = 326;
        var bodyF = r.Fonts.Get(FontKind.Sans, 13);
        var lines = Wrap(bodyF, _toastCur, pw - 30);
        if (lines.Count > 2) { lines = lines.GetRange(0, 2); lines[1] = r.Ellipsize(bodyF, lines[1] + "…", pw - 30); }
        float ph = 22 + lines.Count * 17;
        float appear = Math.Min(1f, (life - leftT) / 0.3f), leave = Math.Min(1f, leftT / 0.3f);
        float slide = (1f - Math.Min(appear, leave)) * (pw + 30);
        float top = 70 + (_achCur != null ? 92 : 0);   // stack below an achievement toast
        var panel = new RectF(_vw - pw - 18 + slide, top, pw, ph);

        r.Begin();
        r.RoundFill(panel, Theme.PanelHi, 13); r.RoundOutline(panel, Theme.Sky, 13);
        r.Fill(new RectF(panel.X, panel.Y + 9, 4, panel.H - 18), Theme.Sky);
        float ty = panel.Y + 11;
        foreach (var ln in lines) { r.Text(bodyF, ln, new Vector2(panel.X + 16, ty), Theme.Text); ty += 17; }
        r.End();
    }

    private void DrawNotifPopover(Renderer r)
    {
        if (!_notifOpen) return;
        float pw = 340, ph = MathF.Min(380, 70 + _notifLog.Count * 34f + 12);
        var panel = new RectF(_vw - pw - 18, _l.Titlebar.Bottom + 6, pw, MathF.Max(110, ph));
        r.Begin();
        Hud.Panel(r, panel, Ircuitry.Core.Icons.Glyph("bell") + " Notifications", Theme.Amber);
        if (_notifLog.Count == 0)
            r.Text(r.Fonts.Get(FontKind.Sans, 13), "Nothing yet - saves, runs and links show up here.", new Vector2(panel.X + 18, panel.Y + Hud.HeaderH + 14), Theme.TextDim);
        r.End();

        var area = new RectF(panel.X + 12, panel.Y + Hud.HeaderH + 10, panel.W - 24, panel.Bottom - (panel.Y + Hud.HeaderH) - 16);
        if (_notifLog.Count > 0)
        {
            _notifScroll = ClampScroll("notifScroll", Wheel("notifScroll", _notifScroll, area), _notifLog.Count * 34f, area.H);
            r.Begin(BlendMode.Alpha, area.ToRectangle());
            float ny = area.Y - _notifScroll;
            var tf = r.Fonts.Get(FontKind.Mono, 10);
            var bf = r.Fonts.Get(FontKind.Sans, 12);
            foreach (var (time, text) in _notifLog)
            {
                if (ny + 30 >= area.Y && ny <= area.Bottom)
                {
                    r.Text(tf, time.ToString("HH:mm"), new Vector2(area.X, ny + 8), Theme.TextFaint);
                    r.Text(bf, r.Ellipsize(bf, text, area.W - 48), new Vector2(area.X + 42, ny + 6), Theme.Text);
                }
                ny += 34;
            }
            r.End();
        }

        if (!_notifJustOpened && In.LeftPressed && !panel.Contains(In.Mouse)) _notifOpen = false;
        _notifJustOpened = false;
    }

    // ===================================================================
    //  Command palette (Ctrl+K): one search box over every action and every node
    // ===================================================================
    private sealed class Cmd { public string Icon = ""; public string Label = ""; public string Hint = ""; public Action Do = () => { }; }

    public void OpenCommandPalette()
    {
        _l = DockLayout();
        _cmdkOpen = true; _cmdkJustOpened = true; _cmdkQuery = ""; _cmdkSel = 0; _cmdkScroll = 0;
        _ui.Focus = "cmdk.query";
    }

    private List<Cmd> BuildCommands()
    {
        var list = new List<Cmd>();
        void A(string icon, string label, string hint, Action act) => list.Add(new Cmd { Icon = icon, Label = label, Hint = hint, Do = act });
        bool running = Bot.Runtime.Running, hasNodes = Bot.Graph.Nodes.Count > 0;

        A("floppy-disk", "Save workspace", "Ctrl+S", () => { _app.Save(); Notify(Ircuitry.Core.Icons.Glyph("floppy-disk") + " Workspace saved"); });
        A(running ? "square" : "play", running ? "Stop bot" : "Run bot", "Ctrl+R", ToggleRun);
        bool remoteRunning = Bot.IsRemote && Bot.Remote?.Connected == true && Bot.Remote.BotRunning(Bot.RemoteName);
        if (running) A("arrows-clockwise", "Apply changes to the live bot", "", () => Bot.Runtime.ApplyGraph(Bot.Graph));
        else if (remoteRunning) A("arrows-clockwise", "Apply changes to the live bot", "", () => ApplyRemote(Bot));
        A("test-tube", "Test (dry run)", "", () => { _testOpen = true; _testJustOpened = true; RunTest(); });
        A("ruler", "Tidy layout", "Ctrl+L", () => { if (hasNodes) { _editor.AutoLayout(); _editor.FocusContent(_l.Canvas); _app.MarkDirty(); } });
        A("magnifying-glass", "Fit to view", "", () => _editor.FocusContent(_l.Canvas));
        A("arrows-clockwise", "Run history", "Ctrl+H", OpenHistory);
        A("bell", "Notifications", "", () => { _notifOpen = true; _notifJustOpened = true; _notifUnread = 0; });
        A("trophy", "Achievements", "", () => { _achOpen = true; _achJustOpened = true; _achScroll = 0; });
        A("key", "Secret keys", "", () => { _secretsOpen = true; _secretsJustOpened = true; });
        A("puzzle-piece", "Community nodes", "", OpenNodeManager);
        A("broadcast", "Saved servers", "", () => { _serversOpen = true; _serversJustOpened = true; _serverSaveName = Bot.Name; });
        A("map-trifold", "Network map", "", () => { _networkOpen = true; _networkJustOpened = true; });
        A("camera", "Save a snapshot", "", () => { _app.SaveSnapshot(); Notify(Ircuitry.Core.Icons.Glyph("camera") + " Snapshot saved"); });
        if (_app.Snapshots().Length > 0) A("arrow-bend-up-left", "Restore a snapshot", "", () => { _snapFiles = _app.Snapshots(); _snapOpen = true; _snapJustOpened = true; });
        A("export", "Export this bot", "Ctrl+E", () => { _app.ExportActive(); Notify(Ircuitry.Core.Icons.Glyph("export") + $" Exported {Bot.Name}"); });
        A("tray", "Import a bot", "", () => { _importFiles = _app.Importable().ToArray(); _importOpen = true; _importJustOpened = true; });
        A("folder-open", "Show files", "", () => Ircuitry.App.DeepLink.OpenUrl(AppModel.WorkspaceDir));
        A("graduation-cap", "Tutorial", "", ForceStartTutorial);

        // every node: "Add <Title>", spawned at the centre of the visible map (not under a docked panel)
        foreach (var def in NodeCatalog.All)
        {
            var d = def;
            A(Ircuitry.Core.Icons.Glyph(d.Icon), "Add: " + d.Title, d.Category.ToString().ToLowerInvariant(), () =>
            {
                var world = _editor.Cam.ScreenToWorld(_dock.VisibleMapRect().Center);
                var n = SpawnNode(d, world);
                _editor.Selection.Clear(); _editor.Selection.Add(n.Id); _app.MarkDirty();
            });
        }
        return list;
    }

    // subsequence/contains fuzzy score; -1 = no match. Higher is better.
    private static int FuzzyScore(string label, string q)
    {
        if (q.Length == 0) return 1;
        string L = label.ToLowerInvariant();
        int idx = L.IndexOf(q, StringComparison.Ordinal);
        if (idx == 0) return 1000;
        if (idx > 0) return (L[idx - 1] is ' ' or ':' or '-' ? 800 : 400) - idx;
        int qi = 0;
        foreach (char c in L) { if (qi < q.Length && c == q[qi]) qi++; }
        return qi == q.Length ? 90 - L.Length / 8 : -1;
    }

    private List<Cmd> FilterCommands()
    {
        string q = _cmdkQuery.Trim().ToLowerInvariant();
        var all = BuildCommands();
        var scored = new List<(int score, int order, Cmd cmd)>();
        for (int i = 0; i < all.Count; i++)
        {
            int s = FuzzyScore(all[i].Label, q);
            if (s >= 0) scored.Add((s, i, all[i]));
        }
        scored.Sort((a, b) => a.score != b.score ? b.score.CompareTo(a.score) : a.order.CompareTo(b.order));
        var res = new List<Cmd>(scored.Count);
        foreach (var s in scored) res.Add(s.cmd);
        return res;
    }

    private void DrawCommandPalette(Renderer r)
    {
        r.Begin();
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.45f));
        float pw = MathF.Min(560, _vw * 0.86f), ph = MathF.Min(460, _vh * 0.8f);
        var panel = new RectF((_vw - pw) / 2f, MathF.Max(60, _vh * 0.16f), pw, ph);
        r.RoundFill(panel.Offset(0, 5), Theme.WithAlpha(Color.Black, 0.22f), 14);
        r.RoundFill(panel, Theme.Panel, 14);
        r.RoundOutline(panel, Theme.WithAlpha(Theme.Violet, 0.9f), 14);
        r.End();

        float x = panel.X + 16, w = panel.W - 32, y = panel.Y + 14;
        r.Begin();
        r.Text(r.Fonts.Get(FontKind.Display, 16), Ircuitry.Core.Icons.Glyph("command"), new Vector2(x, y + 3), Theme.Violet);
        r.End();
        r.Begin();
        var prev = _cmdkQuery;
        _cmdkQuery = _ui.TextField("cmdk.query", new RectF(x + 26, y, w - 26, 32), _cmdkQuery, "Type a command or node…  (" + Ircuitry.Core.Icons.Glyph("arrow-up") + " " + Ircuitry.Core.Icons.Glyph("arrow-down") + " Enter)");
        if (_cmdkQuery != prev) { _cmdkSel = 0; _cmdkScroll = 0; }
        r.End();
        y += 42;

        var results = FilterCommands();
        const float rowH = 34f;
        var listRect = new RectF(x, y, w, panel.Bottom - 14 - y);

        // keyboard navigation (edges are stable through the frame)
        if (results.Count > 0)
        {
            if (In.KeyPressed(Keys.Down)) _cmdkSel = (_cmdkSel + 1) % results.Count;
            if (In.KeyPressed(Keys.Up)) _cmdkSel = (_cmdkSel - 1 + results.Count) % results.Count;
            _cmdkSel = Math.Clamp(_cmdkSel, 0, results.Count - 1);
            if (In.EnterPressed) { var c = results[_cmdkSel]; _cmdkOpen = false; c.Do(); r.Begin(); r.End(); return; }
            // keep the selection in view
            float selTop = _cmdkSel * rowH, selBot = selTop + rowH;
            if (selTop < _cmdkScroll) _cmdkScroll = selTop;
            if (selBot > _cmdkScroll + listRect.H) _cmdkScroll = selBot - listRect.H;
        }
        float total = results.Count * rowH;
        _cmdkScroll = ClampScroll("cmdkScroll", Wheel("cmdkScroll", _cmdkScroll, listRect), total, listRect.H);

        r.Begin(BlendMode.Alpha, listRect.ToRectangle());
        var tf = r.Fonts.Get(FontKind.Sans, 13);
        var icf = r.Fonts.Get(FontKind.Display, 15);
        var hf = r.Fonts.Get(FontKind.Mono, 10);
        float ry = listRect.Y - _cmdkScroll;
        for (int i = 0; i < results.Count; i++)
        {
            var c = results[i];
            var row = new RectF(listRect.X, ry, listRect.W, rowH - 2);
            if (row.Bottom >= listRect.Y && row.Y <= listRect.Bottom)
            {
                bool hover = row.Contains(In.Mouse) && listRect.Contains(In.Mouse);
                if (hover) _cmdkSel = i;
                if (i == _cmdkSel) { r.RoundFill(row, Theme.PanelHi, 7); r.Fill(new RectF(row.X, row.Y + 5, 3, row.H - 10), Theme.Violet); }
                r.Text(icf, Ircuitry.Core.Icons.Glyph(c.Icon), new Vector2(row.X + 8, row.Y + 6), Theme.Text);
                r.Text(tf, r.Ellipsize(tf, c.Label, row.W - 130), new Vector2(row.X + 34, row.Y + 8), Theme.Text);
                if (c.Hint.Length > 0) r.TextRight(hf, c.Hint, row.Right - 10, row.Y + 10, Theme.TextFaint);
                if (hover && In.LeftPressed) { _cmdkOpen = false; c.Do(); break; }
            }
            ry += rowH;
        }
        r.End();

        if (!_cmdkJustOpened && In.LeftPressed && !panel.Contains(In.Mouse)) _cmdkOpen = false;
        _cmdkJustOpened = false;
    }

    private void DrawAchievementsModal(Renderer r)
    {
        r.Begin();
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.5f));
        float pw = MathF.Min(640, _vw * 0.92f), ph = MathF.Min(620, _vh * 0.9f);
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        Hud.Panel(r, panel, "Achievements", Theme.Amber);

        var defs = Ircuitry.Core.Achievements.AllDefs();
        int got = defs.Count(d => d.Unlocked);
        r.TextRight(r.Fonts.Get(FontKind.Mono, 12), $"{got}/{defs.Count} unlocked", panel.Right - 18, panel.Y + 14, Theme.TextFaint);
        r.End();

        var area = new RectF(panel.X + 16, panel.Y + Hud.HeaderH + 12, panel.W - 32, panel.Bottom - (panel.Y + Hud.HeaderH) - 24);
        r.Begin(BlendMode.Alpha, area.ToRectangle());
        float y = area.Y - _achScroll;
        string cat = "";
        var bf = r.Fonts.Get(FontKind.SansBold, 13);
        float rowH = 56;
        foreach (var d in defs)
        {
            if (d.Category != cat)
            {
                cat = d.Category;
                if (y + 22 >= area.Y && y <= area.Bottom) r.Text(r.Fonts.Get(FontKind.SansBold, 12), cat.ToUpperInvariant(), new Vector2(area.X + 2, y + 4), Theme.TextDim);
                y += 26;
            }
            var row = new RectF(area.X, y, area.W, rowH - 8);
            if (row.Bottom >= area.Y && row.Y <= area.Bottom)
            {
                var col = d.Unlocked ? Theme.Amber : Theme.Idle;
                r.RoundFill(row, d.Unlocked ? Theme.Mix(Theme.PanelHi, Theme.Amber, 0.14f) : Theme.Panel, 10);
                r.RoundOutline(row, Theme.WithAlpha(col, d.Unlocked ? 0.7f : 0.3f), 10);
                var tile = new RectF(row.X + 8, row.Center.Y - 17, 34, 34);
                r.RoundFill(tile, d.Unlocked ? Theme.WithAlpha(Theme.Amber, 0.25f) : Theme.PanelLo, 9);
                var icf = r.Fonts.Get(FontKind.Display, 18);
                r.Text(icf, Ircuitry.Core.Icons.Glyph(d.Icon), new Vector2(tile.Center.X - icf.MeasureString(Ircuitry.Core.Icons.Glyph(d.Icon)).X / 2f, tile.Center.Y - icf.MeasureString(Ircuitry.Core.Icons.Glyph(d.Icon)).Y / 2f), d.Unlocked ? Theme.Text : Theme.TextFaint);
                r.Text(bf, d.Title, new Vector2(row.X + 50, row.Y + 6), d.Unlocked ? Theme.Text : Theme.TextDim);
                r.Text(r.Fonts.Get(FontKind.Sans, 10), r.Ellipsize(r.Fonts.Get(FontKind.Sans, 10), d.Desc, row.W - 150), new Vector2(row.X + 50, row.Y + 26), Theme.TextFaint);
                // progress / status on the right
                if (d.Unlocked)
                    r.TextRight(bf, Ircuitry.Core.Icons.Glyph("check"), row.Right - 14, row.Center.Y - 8, Theme.Amber);
                else
                {
                    float bw = 88;
                    var track = new RectF(row.Right - 14 - bw, row.Center.Y + 4, bw, 6);
                    r.RoundFill(track, Theme.PanelLo, 3);
                    if (d.Progress > 0.001f) r.RoundFill(new RectF(track.X, track.Y, MathF.Max(6, bw * d.Progress), 6), Theme.Cyan, 3);
                    r.TextRight(r.Fonts.Get(FontKind.Mono, 9), d.Detail, row.Right - 14, row.Center.Y - 13, Theme.TextFaint);
                }
            }
            y += rowH;
        }
        r.End();
        float total = y + _achScroll - area.Y;
        _achScroll = ClampScroll("achScroll", Wheel("achScroll", _achScroll, area), total, area.H);

        r.Begin();
        if (_ui.Button("ach.close", new RectF(panel.Right - 16 - 100, panel.Bottom - 44, 100, 32), "CLOSE", Theme.Cyan, primary: true)) _achOpen = false;
        if (In.LeftPressed && !panel.Contains(In.Mouse) && !_achJustOpened) _achOpen = false;
        _achJustOpened = false;
        r.End();
    }

    private float Labeled(Renderer r, string label, float x, float y)
    { r.Text(r.Fonts.Get(FontKind.SansBold, 12), label, new Vector2(x, y), Theme.TextDim); return y + 18; }

    // ===================================================================

    // ===================================================================

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
        r.Text(f, $"NODES {Bot.Graph.Nodes.Count}", new Vector2(294, y), Theme.TextDim);
        r.Text(f, $"WIRES {Bot.Graph.Connections.Count}", new Vector2(384, y), Theme.TextDim);
        r.Text(f, "ircuitry v" + Ircuitry.App.AppInfo.Version, new Vector2(478, y), Theme.TextFaint);

        // flood send-queue gauge: warms green->red as the outgoing backlog grows toward an Excess Flood kill
        if (Bot.Runtime.Running)
        {
            int depth = Bot.Runtime.OutQueueDepth;
            float p = Math.Clamp(depth / 12f, 0f, 1f);
            var fill = Theme.Mix(Theme.Ok, Theme.Alert, p);
            r.Text(r.Fonts.Get(FontKind.Mono, 12), Ircuitry.Core.Icons.Glyph("paper-plane-tilt"), new Vector2(600, y), depth > 0 ? fill : Theme.TextFaint);
            var gx = new RectF(620, bar.Center.Y - 5, 64, 10);
            r.RoundFill(gx, Theme.PanelHi, 5f);
            float tw = depth > 8 ? 0.5f + 0.5f * clock.Sin01(3f) : 1f;   // twinkle a warning when it's near the limit
            r.RoundFill(new RectF(gx.X, gx.Y, gx.W * Math.Max(0.05f, p), gx.H), Theme.WithAlpha(fill, tw), 5f);
            r.RoundOutline(gx, Theme.Hairline, 5f);
        }
        // error badge: when a node has thrown, the hint gives way to a clickable tally that opens the error tray (#15)
        int errN = Bot.IsRemote ? 0 : Bot.Runtime.ErrorCount;
        if (errN > 0)
        {
            string elbl = Ircuitry.Core.Icons.Glyph("warning-octagon") + "  " + errN + (errN == 1 ? " error" : " errors");
            float ew = r.Fonts.Get(FontKind.SansBold, 12).MeasureString(elbl).X + 24;
            var er = new RectF(bar.W - 16 - ew, bar.Center.Y - 11, ew, 22);
            var ec = _errTrayOpen ? Theme.Alert : Theme.Mix(Theme.Alert, Theme.PanelHi, 0.45f);
            if (_ui.Button("status.errtray", er, elbl, ec)) { _errTrayOpen = !_errTrayOpen; }
        }
        else
            r.TextRight(f, "Double-click empty space to add a node · drag a port to wire · drag to pan, Shift-drag to box-select · Ctrl+Z undo · Ctrl+D duplicate",
                bar.W - 16, y, Theme.TextFaint);
    }

    // ===================================================================
    private (string label, Color color, bool pulse) StatusInfo()
    {
        var rt = Bot.Runtime;
        return rt.State switch
        {
            IrcState.Connecting => ("CONNECTING", Theme.Warn, true),
            IrcState.Registering => ("REGISTERING", Theme.Warn, true),
            IrcState.Connected => ("LIVE " + Ircuitry.Core.Icons.Glyph("caret-right") + " " + rt.CurrentNick, Theme.Ok, true),
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

    private static Color StatusColor(Ircuitry.Runtime.ServerConn c) => c.State switch
    {
        IrcState.Connected => Theme.Ok,
        IrcState.Connecting or IrcState.Registering => Theme.Warn,
        IrcState.Error => Theme.Alert,
        _ => c.Running ? Theme.Warn : Theme.Idle,
    };

    private void ToggleRun()
    {
        var b = Bot;
        if (b.IsRemote) { b.Remote!.StartStop(b.RemoteName, !b.Remote.BotRunning(b.RemoteName)); return; }   // run/stop on the server
        if (b.Runtime.Running) b.Runtime.Stop();
        else b.Runtime.Start(b.Graph, b.Servers);
    }

    /// <summary>Is this bot running? For a remote-linked tab that's the server's state, else the local runtime.</summary>
    private static bool RunningOf(Bot b) => b.IsRemote ? (b.Remote?.BotRunning(b.RemoteName) ?? false) : b.Runtime.Running;

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
        var replayR = new RectF(clearR.X - 12 - 150, btnY, 150, 34);
        if (_ui.Button("hist.close", closeR, "CLOSE", Theme.Cyan, primary: true)) _historyOpen = false;
        if (_ui.Button("hist.clear", clearR, "CLEAR", Theme.Idle)) { Bot.Runtime.ClearHistory(); _historyRuns.Clear(); _historySel = -1; }
        // replay (#17): re-run the selected recorded event through the current graph as a dry run
        var selRun = _historySel >= 0 && _historySel < _historyRuns.Count ? _historyRuns[_historySel] : null;
        bool canReplay = selRun?.Envelope != null;
        if (_ui.Button("hist.replay", replayR, Ircuitry.Core.Icons.Glyph("arrow-counter-clockwise") + "  REPLAY", canReplay ? Theme.Ok : Theme.Idle, enabled: canReplay) && selRun != null)
            ReplayRun(selRun);
        r.End();

        if (In.LeftPressed && !panel.Contains(In.Mouse) && !_historyJustOpened) _historyOpen = false;
        _historyJustOpened = false;
    }

    private void DrawHistoryList(Renderer r, RectF rect)
    {
        const float rowH = 50f;
        float total = _historyRuns.Count * rowH;
        _historyListScroll = ClampScroll("historyListScroll", Wheel("historyListScroll", _historyListScroll, rect), total, rect.H);

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
                r.Text(icf, Ircuitry.Core.Icons.Glyph(run.Icon), new Vector2(row.X + 8, row.Y + 6), Theme.Amber);
                r.Text(tf, r.Ellipsize(tf, run.Trigger, row.W - 90), new Vector2(row.X + 34, row.Y + 6), Theme.Text);
                string sum = run.Summary.Length == 0 ? "-" : run.Summary.Replace("\n", " ");
                r.Text(sf, r.Ellipsize(sf, sum, row.W - 44), new Vector2(row.X + 34, row.Y + 26), Theme.TextDim);
                r.TextRight(sf, run.Time.ToString("HH:mm:ss"), row.Right - 8, row.Y + 6, Theme.TextFaint);
                if (run.Actions > 0) r.TextRight(sf, run.Actions + Ircuitry.Core.Icons.Glyph("lightning"), row.Right - 8, row.Y + 26, Theme.WithAlpha(Theme.Ok, 0.95f));
                if (hover && In.LeftPressed) { _historySel = i; _historyDetailScroll = 0; }
            }
            y += rowH;
        }
        r.End();
    }

    private void DrawHistoryDetail(Renderer r, RectF rect)
    {
        if (_historySel < 0 || _historySel >= _historyRuns.Count) return;
        DrawRunDetail(r, rect, _historyRuns[_historySel], ref _historyDetailScroll, "histDetail");
    }

    /// <summary>Render a RunRecord's node-by-node I/O (shared by Run History and the Test Bench).</summary>
    private void DrawRunDetail(Renderer r, RectF rect, RunRecord run, ref float scroll, string id = "runDetail")
    {
        scroll = Wheel(id, scroll, rect);

        r.Begin(BlendMode.Alpha, rect.ToRectangle());
        float pad = 14, x = rect.X + pad, w = rect.W - pad * 2;
        float y = rect.Y + pad - scroll;
        var lf = r.Fonts.Get(FontKind.Mono, 12);
        var nf = r.Fonts.Get(FontKind.SansBold, 13);

        r.Text(r.Fonts.Get(FontKind.Display, 20), Ircuitry.Core.Icons.Glyph(run.Icon), new Vector2(x, y), Theme.Amber);
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
                r.Text(r.Fonts.Get(FontKind.Display, 14), Ircuitry.Core.Icons.Glyph(nt.Icon), new Vector2(x + 8, cy), Theme.Cyan);
                r.Text(nf, nt.Title, new Vector2(x + 30, cy + 1), Theme.Text);
                cy += 22;
                if (nt.Pulsed.Count > 0) { r.Text(lf, Ircuitry.Core.Icons.Glyph("caret-right") + " " + string.Join(", ", nt.Pulsed), new Vector2(x + 12, cy), Theme.WithAlpha(Theme.Ok, 0.95f)); cy += 18; }
                foreach (var (pin, val) in nt.Inputs) { DrawIOLine(r, lf, x + 12, cy, w - 24, "in " + Ircuitry.Core.Icons.Glyph("caret-right") + " " + pin, val, Theme.CyanDim); cy += 16; }
                foreach (var (pin, val) in nt.Outputs) { DrawIOLine(r, lf, x + 12, cy, w - 24, "out " + Ircuitry.Core.Icons.Glyph("caret-left") + " " + pin, val, Theme.AmberDim); cy += 16; }
            }
            y += ch + 8;
        }
        float contentH = (y + scroll) - rect.Y + 8;
        r.End();
        scroll = ClampScroll(id, scroll, contentH, rect.H);
    }

    private void DrawIOLine(Renderer r, DynamicSpriteFont f, float x, float y, float w, string label, string val, Color lc)
    {
        r.Text(f, label, new Vector2(x, y), lc);
        string v = val.Length == 0 ? "∅" : val.Replace("\n", " " + Ircuitry.Core.Icons.Glyph("arrow-bend-down-left") + " ");
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
        r.Text(r.Fonts.Get(FontKind.SansBold, 13), "+  Add node", new Vector2(panel.X + 14, panel.Y + 10), Theme.Text);
        r.End();

        float x = panel.X + 12, w = panel.W - 24, y = panel.Y + 32;
        r.Begin();
        _quickSearch = _ui.TextField("quick.search", new RectF(x, y, w, 30), _quickSearch, Ircuitry.Core.Icons.Glyph("magnifying-glass") + "  search nodes…");
        r.End();
        y += 38;

        var listRect = new RectF(x, y, w, panel.Bottom - 12 - y);
        var matches = NodeCatalog.All.Where(d => QuickMatch(d, _quickSearch)).ToList();
        const float rowH = 30f;
        float total = matches.Count * rowH;
        _quickScroll = ClampScroll("quickScroll", Wheel("quickScroll", _quickScroll, listRect), total, listRect.H);

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
                r.Text(icf, Ircuitry.Core.Icons.Glyph(def.Icon), new Vector2(row.X + 6, row.Y + 4), col);
                r.Text(tf, r.Ellipsize(tf, def.Title, row.W - 84), new Vector2(row.X + 30, row.Y + 6), Theme.Text);
                r.TextRight(cf, def.Category.ToString().ToLowerInvariant(), row.Right - 8, row.Y + 8, Theme.WithAlpha(col, 0.9f));
                if (hover && In.LeftPressed) { var sn = SpawnNode(def, _quickWorld); GhostWire(sn); _app.MarkDirty(); _quickOpen = false; }
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
        _testSent.Clear(); _testRec = null; _testScroll = 0; _testReplayNote = "";
        // a remote tab tests on the SERVER (its real state + secrets), not the local copy
        if (Bot.IsRemote && Bot.Remote?.Connected == true)
        {
            var b = Bot;
            b.Remote!.TestCommand(b.RemoteName, _testMsg, _testNick, _testChan, res =>
            {
                _testSent.Clear();
                if (res.TryGetProperty("sent", out var sa) && sa.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var s in sa.EnumerateArray())
                        _testSent.Add((s.TryGetProperty("kind", out var k) && k.ValueKind == System.Text.Json.JsonValueKind.String ? k.GetString() ?? "" : "",
                                       s.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : ""));
                bool fired = res.TryGetProperty("fired", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.True;
                _testRec = new RunRecord { Time = DateTime.Now, Trigger = fired ? "fired on the server" : "no match on the server", Summary = _testMsg, Fired = fired, Actions = _testSent.Count };
            });
            return;
        }
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

    /// <summary>Replay a recorded real event (#17): re-inject its captured variables through the *current* graph
    /// as a dry run (TestSink, throwaway state copy - no IRC, your live variables untouched), then show the
    /// node-by-node trace in the Test Bench so you can see how today's graph would handle that same event.</summary>
    private void ReplayRun(RunRecord src)
    {
        if (src.Envelope == null) return;
        var graph = Bot.Graph;
        // find a live trigger to fire: prefer one of the exact same node type, else any node of the same event family
        var def = NodeCatalog.All.FirstOrDefault(d => d.TypeId == src.TriggerType);
        string fam = def?.TriggerEvent ?? "";
        var trig = graph.Nodes.FirstOrDefault(n => n.Def.IsTrigger && n.TypeId == src.TriggerType)
                ?? graph.Nodes.FirstOrDefault(n => n.Def.IsTrigger && fam.Length > 0 && n.Def.TriggerEvent == fam);

        _testSent.Clear(); _testScroll = 0;
        if (trig == null)
        {
            _testRec = null;
            _testReplayNote = "This graph no longer has a " + (def?.Title ?? src.Trigger) + " trigger to replay into.";
            _testOpen = true; _testJustOpened = true; _historyOpen = false;
            return;
        }

        var sink = new TestSink(new Dictionary<string, string>(Bot.State));   // throwaway state copy
        var rec = new RunRecord { Time = DateTime.Now, Trigger = trig.DisplayTitle, Icon = trig.Def.Icon, Summary = src.Summary };
        GraphExecutor.Fire(graph, sink, trig, new Dictionary<string, string>(src.Envelope), rec);
        rec.Fired = rec.Nodes.Count > 0 && rec.Nodes[0].Pulsed.Count > 0;
        rec.Actions = sink.Sent.Count;
        _testRec = rec.Nodes.Count > 0 ? rec : null;
        _testSent.AddRange(sink.Sent);
        _testReplayNote = "Replayed a recorded " + (def?.Title ?? "event") + " from " + src.Time.ToString("HH:mm:ss")
            + (rec.Fired ? "" : " - it did not fire through the current graph.");
        _testOpen = true; _testJustOpened = true; _historyOpen = false;
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
        if (_ui.Button("test.run", new RectF(fx, y, fw, 36), Ircuitry.Core.Icons.Glyph("play") + "  RUN", Theme.Ok, primary: true)) RunTest();
        y += 46;
        if (_testReplayNote.Length > 0)
        {
            foreach (var line in Wrap(r.Fonts.Get(FontKind.SansBold, 12), Ircuitry.Core.Icons.Glyph("arrow-counter-clockwise") + "  " + _testReplayNote, fw))
            { r.Text(r.Fonts.Get(FontKind.SansBold, 12), line, new Vector2(fx, y), Theme.Cyan); y += 16; }
            y += 8;
        }
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
        if (_testRec != null) DrawRunDetail(r, traceRect, _testRec, ref _testScroll, "testDetail");
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
            r.Text(icf, Ircuitry.Core.Icons.Glyph(t.Icon), new Vector2(row.X + 14, row.Y + 14), Theme.Cyan);
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
