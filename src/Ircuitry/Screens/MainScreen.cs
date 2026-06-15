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
    private float _inspScroll;        // inspector panel scroll (the connection panel can run long)
    private string _inspKey = "";     // what the inspector is showing, to reset scroll on change
    private string _nodeTestId = "", _nodeTestResult = "";   // last "test this node" result
    private string _paletteSearch = "";
    private float _clipCheckAt = -1f;     // throttle clipboard polling
    private string? _clipNodeTitle;        // title of an installable .ircnode currently in the clipboard, or null
    private NodeCategory? _openCat;   // palette accordion: at most one category expanded (null = all collapsed)

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

    // close prompt (window X → exit / minimise)
    private bool _closePromptOpen, _closeJustOpened;
    public Action? OnExitRequested;
    public Action? OnMinimizeRequested;
    public void RequestClosePrompt()
    {
        // only stop to ask (offer to minimise instead) when bots are actually live; otherwise just quit
        if (_app.Bots.Any(b => b.Runtime.Running)) { _closePromptOpen = true; _closeJustOpened = true; }
        else OnExitRequested?.Invoke();
    }

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
    private bool _secretPickOpen, _secretPickJustOpened;
    private Action<string>? _secretPickApply;
    private string _secretPickTitle = "", _secretPickName = "", _secretPickNewVal = "";
    private float _secretPickScroll;
    private bool _serversOpen, _serversJustOpened;
    private string _serverSaveName = "";
    private float _serversScroll;
    // irc:// / ircs:// link → "save this server" (prompt only when one already exists)
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
    private string _quickSearch = "";
    private float _quickScroll;
    private float _lastClickTime;
    private Vector2 _lastClickPos;

    private bool Modal => _importOpen || _confirmDeleteBot != null || _historyOpen || _quickOpen || _templateOpen || _closePromptOpen || _secretsOpen || _testOpen || _ctxOpen || _saveNodeOpen || _installOpen || _uninstallOpen || _nodeMgrOpen || _upPromptOpen || _secretPickOpen || _serversOpen || _networkOpen || _achOpen || _snapOpen || _serverLinkOpen || _cmdkOpen
        || _upState == UpState.Downloading || _upState == UpState.Applying;

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
        if (Ircuitry.App.DeepLink.IsServerLink(link)) { HandleServerLink(link); return; }
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
        Notify($"📡 saved server {host}:{port}" + (channels.Length > 0 ? "  ·  " + channels : ""));
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
            Notify($"📡 saved server {p.Host}:{p.Port}");
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
    public void DebugOpenSecretPick() { _l = Layout.Compute(_vw, _vh); OpenSecretPicker("", "API key", _ => { }); }
    public void DebugShowServers() { _l = Layout.Compute(_vw, _vh); _serversOpen = true; _serversJustOpened = true; _serverSaveName = "my-network"; }
    public void DebugShowAchievements() { _l = Layout.Compute(_vw, _vh); _achOpen = true; _achJustOpened = true; _achScroll = 0; }
    public void DebugOpenIrcv3Cat() { _openCat = NodeCategory.Ircv3; }
    public void DebugOpenFileMenu() { _l = Layout.Compute(_vw, _vh); OpenFileMenu(new Vector2(_vw - 360, _l.Tabs.Bottom + 3)); }
    public void DebugCommandPalette() { OpenCommandPalette(); _cmdkQuery = "se"; _cmdkJustOpened = true; }
    public void DebugLibraryPrefs()
    {
        foreach (var t in new[] { "action.reply", "event.command" }) if (!Ircuitry.Core.NodePrefs.IsFavorite(t)) Ircuitry.Core.NodePrefs.ToggleFavorite(t);
        foreach (var t in new[] { "filter.contains", "ai.reply", "logic.forEach" }) Ircuitry.Core.NodePrefs.RecordUse(t);
        _openCat = null;
    }
    public void DebugNotifications()
    {
        _l = Layout.Compute(_vw, _vh);
        PushToast("💾 Workspace saved");
        _notifLog.Insert(0, (DateTime.Now.AddMinutes(-1), "📤 Exported welcomer"));
        _notifLog.Insert(0, (DateTime.Now.AddMinutes(-3), "📡 saved server irc.libera.chat:6697"));
        _notifLog.Insert(0, (DateTime.Now.AddMinutes(-8), "↩ Snapshot restored"));
        _notifOpen = true; _notifJustOpened = true; _notifUnread = 0;
    }
    public void DebugMultiServer()
    {
        _l = Layout.Compute(_vw, _vh);
        var b = Bot;
        b.Servers.Clear();
        b.Servers.Add(new Ircuitry.Irc.IrcSettings { Label = "Libera", Host = "irc.libera.chat", Channels = "#ircuitry", ConnectOnStartup = true });
        b.Servers.Add(new Ircuitry.Irc.IrcSettings { Label = "OFTC", Host = "irc.oftc.net", Channels = "#bots" });
        b.Servers.Add(new Ircuitry.Irc.IrcSettings { Label = "Libera (test)", Host = "irc.libera.chat", Channels = "#test" });
        b.SelectedServer = 0;
        _editor.Selection.Clear();   // no node selected → the connection inspector shows
    }
    public void DebugShowNetwork()
    {
        _l = Layout.Compute(_vw, _vh);
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
        var welcome = Add("action.reply", -30, 40); welcome.SetParam("message", "welcome to {channel}, {nick}! 🎉");
        g.Connect(join.Id, 0, welcome.Id, 0);
        var timer = Add("event.timer", -360, 210); timer.SetParam("seconds", "3600");
        var say = Add("action.say", -30, 210); say.SetParam("channel", "#ircuitry"); say.SetParam("message", "still here and cosy ☕");
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
        ClipboardPoll(clock);
        AchievementsTick(clock);

        if (DebugAutoHistory && Bot.Runtime.HistoryCount > 0 && (!_historyOpen || _historyRuns.Count != Bot.Runtime.HistoryCount)) OpenHistory();
        if (DebugAutoQuick && !Modal) { OpenQuickAdd(_l.Canvas.Center); DebugAutoQuick = false; }

        if (Modal)
        {
            if (input.KeyPressed(Keys.Escape)) { _importOpen = false; _confirmDeleteBot = null; _historyOpen = false; _quickOpen = false; _templateOpen = false; _closePromptOpen = false; _secretsOpen = false; _testOpen = false; _ctxOpen = false; _saveNodeOpen = false; _installOpen = false; _uninstallOpen = false; _nodeMgrOpen = false; _secretPickOpen = false; _serversOpen = false; _networkOpen = false; _achOpen = false; _snapOpen = false; _serverLinkOpen = false; _cmdkOpen = false; if (_upState != UpState.Downloading && _upState != UpState.Applying) _upPromptOpen = false; }
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
                if (input.Ctrl && input.KeyPressed(Keys.K)) OpenCommandPalette();
                if (input.Ctrl && input.KeyPressed(Keys.S)) { _app.Save(); Notify("💾 Workspace saved"); }
                if (input.Ctrl && input.KeyPressed(Keys.R)) ToggleRun();
                if (input.Ctrl && input.KeyPressed(Keys.E)) { _app.ExportActive(); Notify($"📤 Exported {Bot.Name}"); }
                if (input.Ctrl && input.KeyPressed(Keys.H)) OpenHistory();
                if (input.Ctrl && input.KeyPressed(Keys.L)) { _editor.AutoLayout(); _editor.FocusContent(_l.Canvas); _app.MarkDirty(); }
                if (input.Ctrl && input.Shift && input.KeyPressed(Keys.V)) InstallFromClipboard();
            }
        }

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
        if (_demoShotFit && _vw > 0) { _demoShotFit = false; _editor.FocusContent(_l.Canvas); }   // frame the demo graph for screenshots
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
        TestButton(r);
        ApplyButton(r);
        BellButton(r);
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

        // ---------- gamified tutorial overlay (on top of everything but app modals) ----------
        DrawTutorial(r, clock);

        // ---------- update overlay (on top of absolutely everything) ----------
        if (_upState == UpState.Downloading || _upState == UpState.Applying) DrawUpgradeOverlay(r, clock);

        // ---------- achievement toast + unified notifications ----------
        DrawAchToast(r, clock);
        DrawToast(r, clock);
        DrawNotifPopover(r);

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
            r.Text(r.Fonts.Get(FontKind.Sans, 14), "No snapshots yet - use File ▸ Save a snapshot.", new Vector2(x, y + 8), Theme.TextFaint);
        foreach (var f in _snapFiles)
        {
            if (y + 36 > panel.Bottom - 52) break;
            string label = "📸  " + System.IO.Path.GetFileNameWithoutExtension(f).Replace("workspace-", "");
            if (_ui.Button("snap." + f, new RectF(x, y, w, 34), label, Theme.Cyan)) { _app.RestoreSnapshot(f); _snapOpen = false; Notify("↩ Snapshot restored"); }
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
            if (_ui.Button("imp." + file, new RectF(x, y, w, 34), "📦  " + System.IO.Path.GetFileName(file), Theme.Cyan))
            { _app.ImportFile(file); _importOpen = false; }
            y += 40;
        }

        if (_ui.Button("imp.browse", new RectF(x, panel.Bottom - 46, 254, 32), "🌐  Browse community workflows ↗", Theme.Lime))
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

        // right cluster: two cozy dropdowns instead of a row of big buttons
        float rx = bar.Right;
        RectF Slot(float ww) { var rr = new RectF(rx - ww, bar.Y, ww, bar.H); rx -= ww + 6; return rr; }
        var moreR = Slot(78);
        if (_ui.Button("tab.more", moreR, "⋯ More", Theme.Violet)) OpenMoreMenu(new Vector2(moreR.X, moreR.Bottom + 3));
        var fileR = Slot(84);
        if (_ui.Button("tab.file", fileR, _app.Dirty ? "📁 File ●" : "📁 File", _app.Dirty ? Theme.Amber : Theme.Cyan))
            OpenFileMenu(new Vector2(fileR.X, fileR.Bottom + 3));
    }

    // The File dropdown: workspace save/snapshots + per-bot export/import. Reuses the context-menu popover.
    private void OpenFileMenu(Vector2 anchor)
    {
        _ctxAnchor = anchor;
        _ctxItems.Clear();
        void Item(string icon, string label, string sc, bool en, Action a) => _ctxItems.Add(new CtxItem { Icon = icon, Label = label, Shortcut = sc, Enabled = en, Do = a });
        void Sep() => _ctxItems.Add(new CtxItem { Sep = true });
        Item("💾", _app.Dirty ? "Save" : "Save (up to date)", "Ctrl+S", true, () => { _app.Save(); Notify("💾 Workspace saved"); });
        Item("📸", "Save a snapshot", "", true, () => { _app.SaveSnapshot(); Notify("📸 Snapshot saved"); });
        Item("↩", "Restore a snapshot…", "", _app.Snapshots().Length > 0, () => { _snapFiles = _app.Snapshots(); _snapOpen = true; _snapJustOpened = true; });
        Sep();
        Item("📤", "Export this bot…", "Ctrl+E", true, () => { _app.ExportActive(); Notify($"📤 Exported {Bot.Name}"); });
        Item("📥", "Import a bot…", "", true, () => { _importFiles = _app.Importable().ToArray(); _importOpen = true; _importJustOpened = true; });
        Sep();
        Item("📂", "Show files", "", true, () => Ircuitry.App.DeepLink.OpenUrl(AppModel.WorkspaceDir));
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
        Item("🔑", "Secret keys…", "", true, () => { _secretsOpen = true; _secretsJustOpened = true; });
        Item("🏆", "Achievements", "", true, () => { _achOpen = true; _achJustOpened = true; _achScroll = 0; });
        Item("🧩", "Community nodes…", "", true, OpenNodeManager);
        Sep();
        Item("📐", "Tidy layout", "Ctrl+L", hasNodes, () => { _editor.AutoLayout(); _editor.FocusContent(_l.Canvas); _app.MarkDirty(); });
        Item("🔍", "Fit to view", "", hasNodes, () => _editor.FocusContent(_l.Canvas));
        Item("🎯", "Frame selection", "F", hasNodes, () => _editor.FrameSelection(_l.Canvas));
        Item(_editor.SnapToGrid ? "⊞" : "⊡", _editor.SnapToGrid ? "Snap to grid: on" : "Snap to grid: off", "", true, () => _editor.SnapToGrid = !_editor.SnapToGrid);
        Sep();
        Item("🎓", "Tutorial", "", true, ForceStartTutorial);
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
        NodeCategory.Action => "Actions",
        NodeCategory.Ircv3 => "IRCv3",
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
        NodeCategory.Ircv3 => "📡",
        _ => "🧩",
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
        _paletteSearch = _ui.TextField("palette.search", searchRect, _paletteSearch, "⌕  search nodes…");
        // contextual: only appears when a node is sitting in the clipboard, and names it
        float listTopY = searchRect.Bottom + 8;
        if (_clipNodeTitle != null)
        {
            string label = _clipNodeTitle.Length > 20 ? _clipNodeTitle[..19] + "…" : _clipNodeTitle;
            var clipRect = new RectF(x, searchRect.Bottom + 8, w, 32);
            if (_ui.Button("palette.clip", clipRect, "⎘  Install \"" + label + "\"", Theme.Amber, primary: true)) InstallFromClipboard();
            r.Text(r.Fonts.Get(FontKind.Sans, 9), "found in your clipboard", new Vector2(x + 6, clipRect.Bottom), Theme.TextFaint);
            listTopY = clipRect.Bottom + 16;
        }
        r.End();
        string q = _paletteSearch.Trim();
        bool searching = q.Length > 0;

        // two community links pinned to the bottom of the panel (nodes open the in-app manager; workflows open the gallery)
        int customCount = NodeCatalog.Custom.Count;
        float footY = content.Bottom - 68;
        r.Begin(BlendMode.Alpha, content.ToRectangle());
        if (_ui.Button("palette.manage", new RectF(x, footY, w, 30), customCount > 0 ? $"🧩  Community nodes · {customCount}" : "🧩  Community nodes", Theme.Berry))
            OpenNodeManager();
        if (_ui.Button("palette.workflows", new RectF(x, footY + 34, w, 30), "🤖  Community workflows ↗", Theme.Sky))
            Ircuitry.App.DeepLink.OpenUrl(WorkflowsUrl);
        r.End();

        var listClip = new RectF(content.X, listTopY, content.W, footY - 8 - listTopY);
        r.Begin(BlendMode.Alpha, listClip.ToRectangle());
        float y = listClip.Y - _paletteScroll;

        // a simple pinned/recent section (no collapse), drawn above the categories when not searching
        void Section(string title, string icon, Color col, IEnumerable<NodeDef> defs)
        {
            var items = defs.ToList();
            if (items.Count == 0) return;
            const float hh = 30f;
            var hdr = new RectF(x, y, w, hh);
            r.RoundFill(hdr, Theme.Mix(Theme.PanelHi, col, 0.16f), 10f);
            r.RoundOutline(hdr, Theme.WithAlpha(col, 0.35f), 10f);
            var icf = r.Fonts.Get(FontKind.Display, 14);
            r.Text(icf, icon, new Vector2(hdr.X + 10, hdr.Center.Y - icf.MeasureString(icon).Y / 2f), col);
            var nf = r.Fonts.Get(FontKind.SansBold, 13);
            r.Text(nf, title, new Vector2(hdr.X + 34, hdr.Center.Y - nf.MeasureString("M").Y / 2f - 1), Theme.Text);
            y += hh + 7;
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
            Section("Favorites", "★", Theme.Amber, favs);
            Section("Recent", "🕘", Theme.Sky, recents);
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
            r.Text(iconF, def.Icon, new Vector2(chip.X + 13, chip.Center.Y - iconF.MeasureString(def.Icon).Y / 2f), col);
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
            string star = fav ? "★" : "☆";
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
    private const string NodeLibraryUrl = "https://ircuitry.github.io/nodes.html";
    private const string WorkflowsUrl = "https://ircuitry.github.io/workflows.html";

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
        // re-check GitHub every 6 hours so a long-running instance still notices new releases
        if (_upCheckAt < 0) _upCheckAt = clock.Time;
        else if (clock.Time - _upCheckAt > 6 * 3600f && _upState != UpState.Downloading && _upState != UpState.Applying)
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
        if (_ui.Button("up.later", laterR, "LATER", Theme.Idle)) _upPromptOpen = false;
        if (_ui.Button("up.go", goR, UpAuto ? "⤓  Update now" : "⤓  Download", Theme.Lime, primary: true)) StartUpdateDownload();
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
        r.TextCenteredX(r.Fonts.Get(FontKind.SansBold, 15), "v" + Ircuitry.App.AppInfo.Version + "   →   v" + _upVer, cx, cy + 126f, Theme.CyanDim);

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
            bool spawned = false;
            if (_l.Canvas.Contains(In.Mouse)) { SpawnNode(_dragDef, _editor.Cam.ScreenToWorld(In.Mouse)); spawned = true; }
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
        // reset scroll when the inspected thing changes (node id, or which server is selected)
        string key = (_editor.SelectedSingle?.Id ?? "conn") + ":" + Bot.SelectedServer;
        if (key != _inspKey) { _inspKey = key; _inspScroll = 0; }
        if (!Modal) _inspScroll = Wheel("insp", _inspScroll, content);

        r.Begin(BlendMode.Alpha, content.ToRectangle());
        var scrolled = content.Offset(0, -_inspScroll);   // shift content up by the scroll amount; the scissor still clips to the panel
        var sel = _editor.SelectedSingle;
        float bottom = sel != null ? DrawNodeInspector(r, scrolled, sel) : DrawConnectionInspector(r, scrolled);
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
            if (next != cur) { n.SetParam(pdef.Key, next); _app.MarkDirty(); }

            string hint = ParamHint(pdef.Key, next);
            if (hint.Length > 0)
                foreach (var line in Wrap(r.Fonts.Get(FontKind.Sans, 10), "⚠ " + hint, w))
                { r.Text(r.Fonts.Get(FontKind.Sans, 10), line, new Vector2(x, y - 2), Theme.Amber); y += 13; }
        }

        y += 8;
        // dry-run just this node and report whether it ran (and its output)
        if (_ui.Button("insp.testnode." + n.Id, new RectF(x, y, w, 30), "🧪 Test this node", Theme.Cyan))
        { _nodeTestId = n.Id; _nodeTestResult = TestNode(n); }
        y += 36;
        if (_nodeTestId == n.Id && _nodeTestResult.Length > 0)
        {
            foreach (var line in Wrap(r.Fonts.Get(FontKind.Sans, 11), _nodeTestResult, w))
            { r.Text(r.Fonts.Get(FontKind.Sans, 11), line, new Vector2(x, y), Theme.TextDim); y += 14; }
            y += 6;
        }

        if (!_tut.Active && _ui.Button("insp.del", new RectF(x, y, w, 32), "✕  DELETE NODE", Theme.Alert))
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
            return $"✗ didn't run for a test message “{_testMsg}”. Open 🧪 TEST to change the event, or check the wires/filters above it.";
        string outs = found.Outputs.Count > 0 ? "  ·  out: " + string.Join(", ", found.Outputs.Select(o => $"{o.pin}={Trunc(o.value, 40)}")) : "";
        string fired = found.Pulsed.Count > 0 ? "  ·  → " + string.Join(", ", found.Pulsed) : "";
        return $"✓ ran{outs}{fired}";
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
            if (_ui.Button($"{idBase}.x{i}", new RectF(btnX, y, 26, 28), "✕", Theme.Idle)) remove = i;
            y += 34;
        }
        if (remove >= 0 && remove < rows.Count) rows.RemoveAt(remove);
        if (_ui.Button($"{idBase}.add", new RectF(x, y, w, 26), "＋  " + addLabel, Theme.Cyan)) rows.Add(pair ? new[] { "", "" } : new[] { "" });
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
        // reusable servers + a live map of bots ↔ servers
        float half = (w - 8) / 2f;
        if (_ui.Button("c.servers", new RectF(x, y, half, 28), "📡 Servers", Theme.Sky)) { _serversOpen = true; _serversJustOpened = true; _serverSaveName = Bot.Name; }
        if (_ui.Button("c.network", new RectF(x + half + 8, y, half, 28), "🗺 Network", Theme.Berry)) { _networkOpen = true; _networkJustOpened = true; }
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
        if (_ui.Button("c.sv.add", new RectF(cx, cy, 30, chipH), "＋", Theme.Lime))
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
        if (_ui.Button("c.nick.gen", new RectF(x + w - 34, y, 34, 30), "🎲", Theme.Berry)) { s.Nick = Ircuitry.Core.BakeryNames.Random(); _app.MarkDirty(); }
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
            if (_ui.Button("c.svrun", new RectF(x, y, bw, 30), thisOn ? "■ Disconnect this" : "▸ Connect this", thisOn ? Theme.Alert : Theme.Sky))
            {
                if (thisOn) Bot.Runtime.DisconnectServer(s.DisplayName);
                else Bot.Runtime.ConnectServer(Bot.Graph, s);
            }
            if (_ui.Button("c.svdel", new RectF(x + bw + 8, y, bw, 30), "🗑 Remove server", Theme.Idle))
            {
                Bot.Runtime.DisconnectServer(s.DisplayName);
                Bot.Servers.RemoveAt(Bot.SelectedServer);
                Bot.SelectedServer = Math.Clamp(Bot.SelectedServer, 0, Bot.Servers.Count - 1);
                _app.MarkDirty();
            }
            y += 38;
        }

        if (_ui.Button("c.run", new RectF(x, y, w, 34),
                Bot.Runtime.Running ? "■  STOP BOT" : (Bot.Servers.Count > 1 ? "▶  RUN ALL SERVERS" : "▶  RUN BOT"),
                Bot.Runtime.Running ? Theme.Alert : Theme.Cyan, primary: true))
            ToggleRun();
        return y + 42;
    }

    /// <summary>Status label+colour for one server connection (or offline when it isn't live).</summary>
    private static (string label, Color color) ServerStatus(Ircuitry.Runtime.ServerConn? c) => c?.State switch
    {
        IrcState.Connecting => ("CONNECTING", Theme.Warn),
        IrcState.Registering => ("REGISTERING", Theme.Warn),
        IrcState.Connected => ("LIVE ▸ " + c.CurrentNick, Theme.Ok),
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
        string disp = m.Success ? "🔑  " + m.Groups[1].Value
            : string.IsNullOrEmpty(cur) ? "🔑  Choose a key…"
            : "🔑  •••• (tap to secure)";
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
                    if (_ui.Button("sp.k." + name, new RectF(listRect.X, ly, w, 30), "🔑  " + name, Theme.Lime))
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
        if (_ui.Button("sp.add", new RectF(x, y, w, 30), "＋  Add & use this key", canAdd ? Theme.Lime : Theme.Idle, primary: canAdd) && canAdd)
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
                if (_ui.Button("sv.del." + p.Name, new RectF(row.Right - 30, row.Y + 9, 28, 28), "✕", Theme.Idle) && listRect.Contains(In.Mouse))
                { Ircuitry.Core.Servers.Delete(p.Name); }
            }
            ly += 50;
        }
        r.End();

        r.Begin();
        r.Text(r.Fonts.Get(FontKind.SansBold, 11), "SAVE THIS BOT'S CONNECTION", new Vector2(x, saveY), Theme.TextFaint);
        _serverSaveName = _ui.TextField("sv.name", new RectF(x, saveY + 18, w - 120, 30), _serverSaveName, "server name");
        bool canSave = _serverSaveName.Trim().Length > 0 && Bot.Settings.Host.Length > 0;
        if (_ui.Button("sv.save", new RectF(x + w - 112, saveY + 18, 112, 30), "💾 Save", canSave ? Theme.Sky : Theme.Idle, primary: canSave) && canSave)
            SaveCurrentAsServer(_serverSaveName);
        if (_ui.Button("sv.close", new RectF(panel.Right - 20 - 100, panel.Bottom - 44, 100, 32), "CLOSE", Theme.Cyan, primary: true)) _serversOpen = false;
        if (In.LeftPressed && !panel.Contains(In.Mouse) && !_serversJustOpened) _serversOpen = false;
        _serversJustOpened = false;
        r.End();
    }

    private void DrawNetworkModal(Renderer r)
    {
        r.Begin();
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.5f));
        float pw = MathF.Min(820, _vw * 0.92f), ph = MathF.Min(620, _vh * 0.9f);
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        Hud.Panel(r, panel, "Network · bots & servers", Theme.Berry);

        // group every bot/server pairing by server host (a multi-server bot appears under each of its servers)
        var groups = new List<(string host, int port, bool tls, List<(Bot bot, IrcSettings sv)> rows)>();
        foreach (var b in _app.Bots)
            foreach (var sv in b.Servers)
            {
                string host = sv.Host.Length > 0 ? sv.Host : "(no server set)";
                var g = groups.FirstOrDefault(x => x.host == host && x.port == sv.Port);
                if (g.rows == null) { g = (host, sv.Port, sv.UseTls, new List<(Bot, IrcSettings)>()); groups.Add(g); }
                g.rows.Add((b, sv));
            }
        int online = _app.Bots.Count(b => b.Runtime.Running);
        r.TextRight(r.Fonts.Get(FontKind.Mono, 11), $"{_app.Bots.Count} bots · {online} online · {groups.Count} servers", panel.Right - 18, panel.Y + 14, Theme.TextFaint);
        r.End();

        var area = new RectF(panel.X + 16, panel.Y + Hud.HeaderH + 12, panel.W - 32, panel.Bottom - (panel.Y + Hud.HeaderH) - 24);
        r.Begin(BlendMode.Alpha, area.ToRectangle());
        float gx = area.X, gy = area.Y - _networkScroll;
        float maxBottom = gy;
        foreach (var grp in groups)
        {
            float cardH = 64 + grp.rows.Count * 30 + 10;
            var card = new RectF(gx, gy, area.W, cardH);
            if (card.Bottom >= area.Y && card.Y <= area.Bottom)
            {
                r.RoundFill(card, Theme.PanelLo, 10); r.RoundOutline(card, Theme.WithAlpha(Theme.Sky, 0.5f), 10);
                bool anyOn = grp.rows.Exists(t => t.bot.Runtime.FindConn(t.sv.DisplayName)?.Running == true);
                Hud.SoftDot(r, new Vector2(card.X + 20, card.Y + 24), 6f, anyOn ? Theme.Ok : Theme.Idle);
                r.Text(r.Fonts.Get(FontKind.SansBold, 16), grp.host, new Vector2(card.X + 36, card.Y + 14), Theme.Text);
                r.Text(r.Fonts.Get(FontKind.Mono, 11), grp.host == "(no server set)" ? "fill in a server to connect" : $":{grp.port}{(grp.tls ? "  ·  TLS" : "")}", new Vector2(card.X + 36, card.Y + 36), Theme.TextDim);
                float by = card.Y + 60;
                foreach (var (b, sv) in grp.rows)
                {
                    var conn = b.Runtime.FindConn(sv.DisplayName);
                    var col = conn != null ? StatusColor(conn) : Theme.Idle;
                    Hud.SoftDot(r, new Vector2(card.X + 30, by + 9), 4f, col);
                    r.Text(r.Fonts.Get(FontKind.SansBold, 12), b.Name, new Vector2(card.X + 42, by + 2), Theme.Text);
                    string chans = sv.Channels.Length > 0 ? sv.Channels : "no channels";
                    r.Text(r.Fonts.Get(FontKind.Mono, 10), (conn?.Running == true ? "online" : "offline") + "  ·  " + chans, new Vector2(card.X + 42 + 120, by + 3), Theme.TextDim);
                    by += 30;
                }
            }
            gy += cardH + 12;
            maxBottom = gy;
        }
        r.End();
        float total = maxBottom + _networkScroll - area.Y;
        _networkScroll = ClampScroll("networkScroll", Wheel("networkScroll", _networkScroll, area), total, area.H);

        r.Begin();
        if (_ui.Button("nw.close", new RectF(panel.Right - 16 - 100, panel.Bottom - 44, 100, 32), "CLOSE", Theme.Cyan, primary: true)) _networkOpen = false;
        if (In.LeftPressed && !panel.Contains(In.Mouse) && !_networkJustOpened) _networkOpen = false;
        _networkJustOpened = false;
        r.End();
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
        r.Text(icf, _achCur.Icon, new Vector2(tile.Center.X - icf.MeasureString(_achCur.Icon).X / 2f, tile.Center.Y - icf.MeasureString(_achCur.Icon).Y / 2f), Theme.Text);
        r.Text(r.Fonts.Get(FontKind.SansBold, 11), "🏆 ACHIEVEMENT UNLOCKED", new Vector2(tile.Right + 12, panel.Y + 14), Theme.AmberDim);
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

    private void BellButton(Renderer r)
    {
        var bar = _l.TopBar;
        var tf = r.Fonts.Get(FontKind.SansBold, 16);
        float clockW = tf.MeasureString(DateTime.Now.ToString("HH:mm:ss")).X;
        float runX = bar.W - 22 - clockW - 16 - 150;
        float histX = runX - 12 - 128;
        float applyX = histX - 12 - 94 - 12 - 110;   // stable slot whether or not APPLY is shown
        var rect = new RectF(applyX - 12 - 40, 12, 40, 32);
        if (_ui.Button("top.bell", rect, "🔔", _notifUnread > 0 ? Theme.Amber : Theme.Idle))
        { _notifOpen = !_notifOpen; _notifJustOpened = true; _notifUnread = 0; _notifScroll = 0; }
        if (_notifUnread > 0)
        {
            var c = new Vector2(rect.Right - 8, rect.Y + 8);
            r.Disc(c, 8f, Theme.Alert);
            string n = _notifUnread > 9 ? "9+" : _notifUnread.ToString();
            var nf = r.Fonts.Get(FontKind.SansBold, 9);
            r.Text(nf, n, new Vector2(c.X - nf.MeasureString(n).X / 2f, c.Y - nf.MeasureString(n).Y / 2f), Theme.Text);
        }
    }

    private void DrawNotifPopover(Renderer r)
    {
        if (!_notifOpen) return;
        float pw = 340, ph = MathF.Min(380, 70 + _notifLog.Count * 34f + 12);
        var panel = new RectF(_vw - pw - 18, _l.TopBar.Bottom + 6, pw, MathF.Max(110, ph));
        r.Begin();
        Hud.Panel(r, panel, "🔔 Notifications", Theme.Amber);
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
        _l = Layout.Compute(_vw, _vh);
        _cmdkOpen = true; _cmdkJustOpened = true; _cmdkQuery = ""; _cmdkSel = 0; _cmdkScroll = 0;
        _ui.Focus = "cmdk.query";
    }

    private List<Cmd> BuildCommands()
    {
        var list = new List<Cmd>();
        void A(string icon, string label, string hint, Action act) => list.Add(new Cmd { Icon = icon, Label = label, Hint = hint, Do = act });
        bool running = Bot.Runtime.Running, hasNodes = Bot.Graph.Nodes.Count > 0;

        A("💾", "Save workspace", "Ctrl+S", () => { _app.Save(); Notify("💾 Workspace saved"); });
        A(running ? "■" : "▶", running ? "Stop bot" : "Run bot", "Ctrl+R", ToggleRun);
        if (running) A("⟲", "Apply changes to the live bot", "", () => Bot.Runtime.ApplyGraph(Bot.Graph));
        A("🧪", "Test (dry run)", "", () => { _testOpen = true; _testJustOpened = true; RunTest(); });
        A("📐", "Tidy layout", "Ctrl+L", () => { if (hasNodes) { _editor.AutoLayout(); _editor.FocusContent(_l.Canvas); _app.MarkDirty(); } });
        A("🔍", "Fit to view", "", () => _editor.FocusContent(_l.Canvas));
        A("⟲", "Run history", "Ctrl+H", OpenHistory);
        A("🔔", "Notifications", "", () => { _notifOpen = true; _notifJustOpened = true; _notifUnread = 0; });
        A("🏆", "Achievements", "", () => { _achOpen = true; _achJustOpened = true; _achScroll = 0; });
        A("🔑", "Secret keys", "", () => { _secretsOpen = true; _secretsJustOpened = true; });
        A("🧩", "Community nodes", "", OpenNodeManager);
        A("📡", "Saved servers", "", () => { _serversOpen = true; _serversJustOpened = true; _serverSaveName = Bot.Name; });
        A("🗺", "Network map", "", () => { _networkOpen = true; _networkJustOpened = true; });
        A("📸", "Save a snapshot", "", () => { _app.SaveSnapshot(); Notify("📸 Snapshot saved"); });
        if (_app.Snapshots().Length > 0) A("↩", "Restore a snapshot", "", () => { _snapFiles = _app.Snapshots(); _snapOpen = true; _snapJustOpened = true; });
        A("📤", "Export this bot", "Ctrl+E", () => { _app.ExportActive(); Notify($"📤 Exported {Bot.Name}"); });
        A("📥", "Import a bot", "", () => { _importFiles = _app.Importable().ToArray(); _importOpen = true; _importJustOpened = true; });
        A("📂", "Show files", "", () => Ircuitry.App.DeepLink.OpenUrl(AppModel.WorkspaceDir));
        A("🎓", "Tutorial", "", ForceStartTutorial);

        // every node: "Add <Title>", spawned at the centre of the canvas
        foreach (var def in NodeCatalog.All)
        {
            var d = def;
            A(d.Icon, "Add: " + d.Title, d.Category.ToString().ToLowerInvariant(), () =>
            {
                var world = _editor.Cam.ScreenToWorld(new Vector2(_l.Canvas.Center.X, _l.Canvas.Center.Y));
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
        r.Text(r.Fonts.Get(FontKind.Display, 16), "⌘", new Vector2(x, y + 3), Theme.Violet);
        r.End();
        r.Begin();
        var prev = _cmdkQuery;
        _cmdkQuery = _ui.TextField("cmdk.query", new RectF(x + 26, y, w - 26, 32), _cmdkQuery, "Type a command or node…  (↑ ↓ Enter)");
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
                r.Text(icf, c.Icon, new Vector2(row.X + 8, row.Y + 6), Theme.Text);
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
                r.Text(icf, d.Icon, new Vector2(tile.Center.X - icf.MeasureString(d.Icon).X / 2f, tile.Center.Y - icf.MeasureString(d.Icon).Y / 2f), d.Unlocked ? Theme.Text : Theme.TextFaint);
                r.Text(bf, d.Title, new Vector2(row.X + 50, row.Y + 6), d.Unlocked ? Theme.Text : Theme.TextDim);
                r.Text(r.Fonts.Get(FontKind.Sans, 10), r.Ellipsize(r.Fonts.Get(FontKind.Sans, 10), d.Desc, row.W - 150), new Vector2(row.X + 50, row.Y + 26), Theme.TextFaint);
                // progress / status on the right
                if (d.Unlocked)
                    r.TextRight(bf, "✓", row.Right - 14, row.Center.Y - 8, Theme.Amber);
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
        float tagX = bx + bsz.X + 14;
        r.Text(r.Fonts.Get(FontKind.Sans, 13), "· IRCv3 Bot Bakery", new Vector2(tagX, bar.H / 2f - 9), Theme.Mix(Theme.Amber, Theme.Text, 0.25f));
        r.Text(r.Fonts.Get(FontKind.Sans, 11), "v" + Ircuitry.App.AppInfo.Version, new Vector2(tagX, bar.H / 2f + 8), Theme.TextFaint);

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

    private void TestButton(Renderer r)
    {
        var bar = _l.TopBar;
        var tf = r.Fonts.Get(FontKind.SansBold, 16);
        float clockW = tf.MeasureString(DateTime.Now.ToString("HH:mm:ss")).X;
        float runX = bar.W - 22 - clockW - 16 - 150;
        float histX = runX - 12 - 128;
        var rect = new RectF(histX - 12 - 94, 12, 94, 32);   // dry-run test, sat next to RUN BOT
        if (_ui.Button("top.test", rect, "🧪 TEST", Theme.Cyan)) { _testOpen = true; _testJustOpened = true; RunTest(); }
    }

    private void ApplyButton(Renderer r)
    {
        if (!Bot.Runtime.Running) return;   // only meaningful on a live bot
        var bar = _l.TopBar;
        var tf = r.Fonts.Get(FontKind.SansBold, 16);
        float clockW = tf.MeasureString(DateTime.Now.ToString("HH:mm:ss")).X;
        float runX = bar.W - 22 - clockW - 16 - 150;
        float histX = runX - 12 - 128;
        var rect = new RectF(histX - 12 - 94 - 12 - 110, 12, 110, 32);
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

    private static Color StatusColor(Ircuitry.Runtime.ServerConn c) => c.State switch
    {
        IrcState.Connected => Theme.Ok,
        IrcState.Connecting or IrcState.Registering => Theme.Warn,
        IrcState.Error => Theme.Alert,
        _ => c.Running ? Theme.Warn : Theme.Idle,
    };

    private void ToggleRun()
    {
        if (Bot.Runtime.Running) Bot.Runtime.Stop();
        else Bot.Runtime.Start(Bot.Graph, Bot.Servers);
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
        scroll = ClampScroll(id, scroll, contentH, rect.H);
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
                r.Text(icf, def.Icon, new Vector2(row.X + 6, row.Y + 4), col);
                r.Text(tf, r.Ellipsize(tf, def.Title, row.W - 84), new Vector2(row.X + 30, row.Y + 6), Theme.Text);
                r.TextRight(cf, def.Category.ToString().ToLowerInvariant(), row.Right - 8, row.Y + 8, Theme.WithAlpha(col, 0.9f));
                if (hover && In.LeftPressed) { SpawnNode(def, _quickWorld); _app.MarkDirty(); _quickOpen = false; }
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
