using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Ircuitry.Core;
using Ircuitry.Input;
using Ircuitry.Irc;
using Ircuitry.Render;
using Ircuitry.Screens;

namespace Ircuitry.App;

/// <summary>
/// The MonoGame host. Owns the device, renderer, fonts, input and the active
/// screen. Supports a headless-ish "--shot" mode that renders a few frames then
/// dumps the backbuffer to PNG, which is how we iterate on visuals.
/// </summary>
public sealed class IrcuitryGame : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private Fonts _fonts = null!;
    private Renderer _r = null!;
    private readonly InputState _input = new();
    private readonly Clock _clock = new();
    private AppModel _app = null!;
    private IScreen _screen = null!;

    private bool _resizing;
    private int _screenshotSeq;
    private Splash? _splash;

    // shot mode
    private readonly string? _shotPath;
    private readonly int _shotAfterFrames;
    private float _shotAfterSeconds;
    private bool _shotTaken;

    // offline demo (runs the bot against an embedded mock server)
    private readonly bool _demo;
    private MockIrcServer? _mock;
    private int _autoExit;   // test hook: clean-Exit after N frames (exercises the autosave path)
    private readonly string[] _args;
    private readonly System.Collections.Concurrent.ConcurrentQueue<(Vector2 pos, string path)> _drops = new();
    private FileSystemWatcher? _wsWatcher;
    private volatile bool _reloadRequested;

    // deep-link (ircuitry://) install: an inbox DIRECTORY other instances drop one file per link into, plus our own launch link
    private readonly string? _inboxDir;
    private readonly string? _initialDeepLink;
    private FileSystemWatcher? _inboxWatcher;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _deepLinks = new();
    private readonly System.Collections.Generic.Dictionary<string, DateTime> _recentLinks = new();   // collapse rapid duplicate clicks

    public IrcuitryGame(string[] args, string? inboxDir = null, string? initialDeepLink = null)
    {
        _args = args;
        _inboxDir = inboxDir;
        _initialDeepLink = initialDeepLink;
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1600,
            PreferredBackBufferHeight = 952,
            PreferMultiSampling = false,       // required: this GL driver rejects MSAA visuals
            GraphicsProfile = GraphicsProfile.Reach,
            SynchronizeWithVerticalRetrace = true,
        };
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        IsFixedTimeStep = false;
        Window.Title = "ircuitry ~ * ~ IRCv3 Bot Bakery";
        Window.AllowUserResizing = true;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--shot" && i + 1 < args.Length) _shotPath = args[i + 1];
            if (args[i] == "--frames" && i + 1 < args.Length && int.TryParse(args[i + 1], out var f)) _shotAfterFrames = f;
            if (args[i] == "--shotsec" && i + 1 < args.Length && float.TryParse(args[i + 1], out var s)) _shotAfterSeconds = s;
            if (args[i] == "--demo") _demo = true;
            if (args[i] == "--autoexit" && i + 1 < args.Length && int.TryParse(args[i + 1], out var ae)) _autoExit = ae;
        }
        if (_shotAfterFrames <= 0) _shotAfterFrames = 18;
    }

    protected override void Initialize()
    {
        Window.ClientSizeChanged += OnResize;
        base.Initialize();
    }

    protected override void LoadContent()
    {
        var fontDir = Path.Combine(AppContext.BaseDirectory, "assets", "fonts");
        _fonts = new Fonts(fontDir);
        _r = new Renderer(GraphicsDevice, _fonts);
        _app = new AppModel();
        _screen = new MainScreen(_app);
        // autosave on close - but never in demo/screenshot modes (they'd clobber the real workspace)
        Exiting += (_, _) => { if (!_demo && _shotPath == null) { _app.Save(); Ircuitry.Core.Achievements.Save(); } try { Ircuitry.Net.ContainerEngine.StopAll(); } catch { } };
        Window.TextInput += (_, e) => OnText(e.Character);
        // FileDrop fires on the OS event thread; queue the files and apply them on the game thread
        // (Update) so we never mutate the graph/bot list while Update/Draw are iterating it.
        Window.FileDrop += (_, e) =>
        {
            var ms = Mouse.GetState();
            var pos = new Vector2(ms.X, ms.Y);
            foreach (var f in e.Files) _drops.Enqueue((pos, f));
        };
        // watch the workspace file: an external edit (another editor, git, this assistant) auto-reloads it
        if (!_demo && _shotPath == null) StartWorkspaceWatcher();
        // a loopback endpoint so the ircuitry website can tailor its gallery to this app (read-only, origin-locked)
        if (!_demo && _shotPath == null) Ircuitry.App.CapabilityServer.Start();
        if (_inboxDir != null) StartInboxWatcher();
        if (_initialDeepLink != null) _deepLinks.Enqueue(_initialDeepLink);

        UpdateViewport();
        if (_demo) StartDemo();
        var ms = _screen as MainScreen;

        // custom client-side title bar: drop the OS frame and draw our own (Linux/Windows). macOS keeps its
        // native traffic-light frame, so we leave it bordered there and simply don't draw window controls.
        if (ms != null)
        {
            ms.WindowHandle = Window.Handle;
            if (!OperatingSystem.IsMacOS()) Ircuitry.Core.Sdl.EnableCustomChrome(Window.Handle);
        }

        // appearance: re-apply the non-colour parts of a theme (fonts; window opacity) whenever it changes.
        // Colours are read live by Theme, so they need no hook. Then restore the user's saved theme.
        Ircuitry.Core.Themes.Changed += ApplyAppearance;
        Ircuitry.Core.Themes.LoadActive();
        // --theme <file>: apply a theme up front (for demos/screenshots); not persisted
        for (int i = 0; i < _args.Length - 1; i++)
            if (_args[i] == "--theme")
                try { Ircuitry.Core.Themes.Apply(Ircuitry.Core.ThemeData.FromJson(File.ReadAllText(_args[i + 1])), persist: false); } catch { }

        // interactive runs: start maximized, and make the window's X prompt (exit / minimise) instead of quitting
        if (_shotPath == null && ms != null)
        {
            ms.OnExitRequested = Exit;
            // minimise -> vanish to the tray if a tray icon is up, else a normal taskbar minimise
            ms.OnMinimizeRequested = () =>
            {
                if (TrayIcon.Available) Ircuitry.Core.Sdl.Hide(Window.Handle);
                else Ircuitry.Core.Sdl.Minimize(Window.Handle);
            };
            Ircuitry.Core.Sdl.InterceptClose();
            Ircuitry.Core.Sdl.Maximize(Window.Handle);
            RefreshTrayModel();   // populate BEFORE registering, so a host that fetches the menu once at startup already sees the bots/servers
            TrayIcon.Start();
        }

        // flashy startup splash in interactive runs; suppressed for screenshots unless --splash is passed
        if (_shotPath == null || Array.IndexOf(_args, "--splash") >= 0) _splash = new Splash();

        // first-run onboarding (skipped in demo/screenshot modes); --tutorial forces it for capture
        if (_shotPath == null && !_demo) ms?.MaybeAutostartTutorial();
        if (_shotPath == null && !_demo) ms?.StartUpdateCheck();   // quietly check GitHub for a newer release
        // bring any "connect on app startup" servers online right away
        if (_shotPath == null && !_demo)
            foreach (var b in _app.Bots)
                foreach (var sv in b.Servers)
                    if (sv.ConnectOnStartup && sv.Host.Length > 0) b.Runtime.ConnectServer(b.Graph, sv);
        if (Array.IndexOf(_args, "--tutorial") >= 0) ms?.ForceStartTutorial();
        for (int i = 0; i < _args.Length - 1; i++)
            if (_args[i] == "--tutstep" && int.TryParse(_args[i + 1], out var ts)) ms?.DebugTutorialStep(ts);

        if (Array.IndexOf(_args, "--inspect") >= 0) ms?.DebugSelectFirst();
        if (Array.IndexOf(_args, "--showhistory") >= 0 && ms != null) ms.DebugAutoHistory = true;
        if (Array.IndexOf(_args, "--showquick") >= 0 && ms != null) ms.DebugAutoQuick = true;
        if (Array.IndexOf(_args, "--showtemplate") >= 0) ms?.DebugOpenTemplate();
        if (Array.IndexOf(_args, "--showsecrets") >= 0) ms?.DebugOpenSecrets();
        if (Array.IndexOf(_args, "--showtest") >= 0) ms?.DebugOpenTest();
        if (Array.IndexOf(_args, "--showctxnode") >= 0) ms?.DebugOpenContextMenu(true);
        if (Array.IndexOf(_args, "--showctxcanvas") >= 0) ms?.DebugOpenContextMenu(false);
        if (Array.IndexOf(_args, "--showsavenode") >= 0) ms?.DebugOpenSaveNode();
        if (Array.IndexOf(_args, "--showgh") >= 0) ms?.DebugShowGh();
        if (Array.IndexOf(_args, "--showinstall") >= 0) ms?.DebugOpenInstall();
        if (Array.IndexOf(_args, "--showinstallclip") >= 0) ms?.DebugInstallClip();
        if (Array.IndexOf(_args, "--showuninstall") >= 0) ms?.DebugOpenUninstall();
        if (Array.IndexOf(_args, "--showircwindow") >= 0) ms?.DebugOpenIrcWindow();
        if (Array.IndexOf(_args, "--showircchannel") >= 0 && ms != null) { ms.DebugOpenIrcWindow(); ms.DebugAutoIrcChannel = true; }
        if (Array.IndexOf(_args, "--shownodemgr") >= 0) ms?.DebugOpenNodeManager();
        if (Array.IndexOf(_args, "--showupdate") >= 0) ms?.DebugShowUpdate();
        if (Array.IndexOf(_args, "--showupgrade") >= 0) ms?.DebugShowUpgrade();
        if (Array.IndexOf(_args, "--demoshot") >= 0) ms?.DebugDemoShot();
        if (Array.IndexOf(_args, "--showsecretpick") >= 0) ms?.DebugOpenSecretPick();
        if (Array.IndexOf(_args, "--showservers") >= 0) ms?.DebugShowServers();
        if (Array.IndexOf(_args, "--shownetwork") >= 0) ms?.DebugShowNetwork();
        if (Array.IndexOf(_args, "--showfleet") >= 0) ms?.DebugShowFleet();
        if (Array.IndexOf(_args, "--showeval") >= 0) ms?.DebugShowEval();
        if (Array.IndexOf(_args, "--showtimeline") >= 0) ms?.DebugShowTimeline();
        if (Array.IndexOf(_args, "--showach") >= 0) ms?.DebugShowAchievements();
        if (Array.IndexOf(_args, "--showircv3") >= 0) ms?.DebugOpenIrcv3Cat();
        if (Array.IndexOf(_args, "--showfilemenu") >= 0) ms?.DebugOpenFileMenu();
        if (Array.IndexOf(_args, "--showmultiserver") >= 0) ms?.DebugMultiServer();
        if (Array.IndexOf(_args, "--shownotifs") >= 0) ms?.DebugNotifications();
        if (Array.IndexOf(_args, "--showpalette") >= 0) ms?.DebugCommandPalette();
        if (Array.IndexOf(_args, "--showlibprefs") >= 0) ms?.DebugLibraryPrefs();
        if (Array.IndexOf(_args, "--shownodebuilder") >= 0) ms?.DebugOpenNodeBuilder();
        if (Array.IndexOf(_args, "--showcomposite") >= 0) ms?.DebugOpenComposite();
        if (Array.IndexOf(_args, "--showmaxbuilder") >= 0) ms?.DebugOpenMaxBuilder();
        if (Array.IndexOf(_args, "--showwfinstall") >= 0) ms?.DebugWorkflowInstall();
        if (Array.IndexOf(_args, "--showbake") >= 0) ms?.DebugOpenBake();
        if (Array.IndexOf(_args, "--showbakery") >= 0) ms?.DebugOpenBakery();
        if (Array.IndexOf(_args, "--showbakeanim") >= 0) ms?.DebugBakeAnim();
        if (Array.IndexOf(_args, "--showappearance") >= 0) ms?.DebugOpenAppearance();
        if (Array.IndexOf(_args, "--showremote") >= 0) ms?.DebugOpenRemote();
        if (Array.IndexOf(_args, "--showremoteedit") >= 0) ms?.DebugOpenRemoteEdit();
        if (Array.IndexOf(_args, "--showthemeinstall") >= 0) ms?.DebugThemeInstall();
        for (int i = 0; i < _args.Length - 1; i++)
            if (_args[i] == "--showdeeplink") ms?.HandleDeepLink(_args[i + 1]);
        if (Array.IndexOf(_args, "--showlabels") >= 0) ms?.DebugShowLabels();
        if (Array.IndexOf(_args, "--showschedule") >= 0) ms?.DebugSpawnSelect("event.schedule");
        for (int i = 0; i < _args.Length - 1; i++)
            if (_args[i] == "--spawn") ms?.DebugSpawnSelect(_args[i + 1]);
        base.LoadContent();
    }

    private void StartDemo()
    {
        var script = new (int, string)[]
        {
            (90, ":alice!a@h PRIVMSG #ircuitry-test :!ping"),
            (1100, ":bob!b@h PRIVMSG #ircuitry-test :hey ircuitry, you around?"),
            (1200, ":carol!c@h PRIVMSG #ircuitry-test :lol nice bot"),
            (1100, ":dave!d@h PRIVMSG #ircuitry-test :!ping"),
            (1000, ":erin!e@h JOIN #ircuitry-test"),
            (900, ":erin!e@h PRIVMSG #ircuitry-test :ircuitry rules"),
        };
        _mock = new MockIrcServer(script);
        Console.WriteLine($"ircuitry_DEMO mock server on 127.0.0.1:{_mock.Port}");
        var bot = _app.ActiveBot;
        bot.Settings.Host = "127.0.0.1";
        bot.Settings.Port = _mock.Port;
        bot.Settings.UseTls = false;
        bot.Settings.Nick = "ircuitry";
        bot.Settings.Channels = "#ircuitry-test";
        bot.Runtime.Start(bot.Graph, bot.Settings);
    }

    private int _trayTick;
    private string _traySig = "";   // sentinel: forces a MenuChanged on the first real build

    // Snapshot the current bots/servers into the tray model; returns a content signature so callers can tell
    // when it changed. Built from the workspace bots (present even before anything connects), so the menu is
    // never empty - vital because some tray hosts (GNOME AppIndicator) fetch the menu once and cache it.
    private string RefreshTrayModel()
    {
        var model = new TrayMenuModel();
        var sig = new System.Text.StringBuilder();
        foreach (var b in _app.Bots)
        {
            var bi = new TrayBotInfo { Name = b.Name };
            sig.Append(b.Name).Append('{');
            foreach (var sv in b.Servers)
            {
                bool on = b.Runtime.FindConn(sv.DisplayName)?.Running == true;
                bi.Servers.Add(new TrayServerInfo { Label = sv.DisplayName, Online = on });
                sig.Append(sv.DisplayName).Append(on ? '+' : '-').Append(',');
            }
            sig.Append('}');
            model.Bots.Add(bi);
        }
        TrayIcon.Model = model;
        return sig.ToString();
    }

    // keep the tray menu's model current and run whatever the user picked from it (on the game thread)
    private void PumpTray()
    {
        if (!TrayIcon.Available) return;
        if (_trayTick++ % 20 == 0)   // refresh a few times a second
        {
            string s = RefreshTrayModel();
            // nudge the host to re-fetch only when the menu's content changed (connect/disconnect, bot add/remove)
            if (s != _traySig) { _traySig = s; TrayIcon.MenuChanged(); }
            var (cr, cg, cb) = TrayStatusColor();      // the living orb tracks bot status (and re-themes too)
            TrayIcon.SetStatus(cr, cg, cb);            // SetStatus is a no-op unless the colour changed
        }
        while (TrayIcon.Commands.TryDequeue(out var cmd)) HandleTray(cmd);
    }

    // idle (nothing running) -> calm taupe; running but a server is still offline -> honey; all live -> leaf green.
    private (int, int, int) TrayStatusColor()
    {
        bool anyRunning = false, anyOffline = false;
        foreach (var b in _app.Bots)
        {
            if (!b.Runtime.Running) continue;
            anyRunning = true;
            foreach (var sv in b.Servers)
            {
                if (sv.Host.Length == 0) continue;
                if (b.Runtime.FindConn(sv.DisplayName)?.Running != true) anyOffline = true;
            }
        }
        var c = !anyRunning ? Theme.Idle : anyOffline ? Theme.Warn : Theme.Ok;
        return (c.R, c.G, c.B);
    }

    private void HandleTray(TrayCommand cmd)
    {
        switch (cmd.Kind)
        {
            case "open":
                Ircuitry.Core.Sdl.BringToFront(Window.Handle);   // surface + focus, keep maximised state
                break;
            case "exit":
            {
                var m = _screen as MainScreen;
                m?.RequestClosePrompt();                          // asks only if bots are live, else just quits
                if (m?.ClosePromptOpen == true) Ircuitry.Core.Sdl.BringToFront(Window.Handle);   // bring the prompt to the front
                break;
            }
            case "disconnect":
                foreach (var b in _app.Bots)
                {
                    if (cmd.Bot != "*" && !b.Name.Equals(cmd.Bot, StringComparison.OrdinalIgnoreCase)) continue;
                    if (cmd.Server == "*") b.Runtime.Stop();
                    else b.Runtime.DisconnectServer(cmd.Server);
                }
                break;
            case "reconnect":
                foreach (var b in _app.Bots)
                {
                    if (cmd.Bot != "*" && !b.Name.Equals(cmd.Bot, StringComparison.OrdinalIgnoreCase)) continue;
                    if (cmd.Server == "*")
                    { foreach (var sv in b.Servers) if (sv.Host.Length > 0) b.Runtime.ConnectServer(b.Graph, sv); }
                    else
                    { var sv = System.Linq.Enumerable.FirstOrDefault(b.Servers, s => s.DisplayName == cmd.Server); if (sv != null) b.Runtime.ConnectServer(b.Graph, sv); }
                }
                break;
        }
    }

    private void OnText(char c)
    {
        // forward printable characters; editing keys are handled via InputState edges
        if (c == '\r' || c == '\n' || c == '\t' || c == '\b') return;
        if (c < 32 && c != 0) return;
        _input.PushChar(c);
    }

    private void OnResize(object? sender, EventArgs e)
    {
        if (_resizing) return;
        _resizing = true;
        try
        {
            _gdm.PreferredBackBufferWidth = Math.Max(960, Window.ClientBounds.Width);
            _gdm.PreferredBackBufferHeight = Math.Max(600, Window.ClientBounds.Height);
            _gdm.ApplyChanges();
            UpdateViewport();
        }
        finally { _resizing = false; }   // never wedge the flag if ApplyChanges throws
    }

    private void StartWorkspaceWatcher()
    {
        try
        {
            Directory.CreateDirectory(AppModel.WorkspaceDir);
            _wsWatcher = new FileSystemWatcher(AppModel.WorkspaceDir, "workspace.ircuitry")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            // events fire on a thread-pool thread - just flag; the reload happens on the game thread in Update
            _wsWatcher.Changed += (_, _) => _reloadRequested = true;
            _wsWatcher.Created += (_, _) => _reloadRequested = true;
            _wsWatcher.Renamed += (_, _) => _reloadRequested = true;
        }
        catch { /* watching is best-effort */ }
    }

    // watch the deep-link inbox dir: each ircuitry:// click drops a *.link file; we pick them up
    private void StartInboxWatcher()
    {
        try
        {
            Directory.CreateDirectory(_inboxDir!);
            _inboxWatcher = new FileSystemWatcher(_inboxDir!, "*.link")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            _inboxWatcher.Created += (_, _) => DrainInbox();
            _inboxWatcher.Changed += (_, _) => DrainInbox();
        }
        catch { _inboxWatcher = null; }
    }

    private void DrainInbox()
    {
        if (_inboxDir == null) return;
        try
        {
            if (!Directory.Exists(_inboxDir)) return;
            foreach (var file in Directory.GetFiles(_inboxDir, "*.link"))
            {
                string link;
                try { link = File.ReadAllText(file).Trim(); }
                catch { continue; }                 // locked/mid-write; a later event/poll retries (don't delete)
                if (link.Length == 0) continue;     // not fully written yet; leave it for a retry
                try { File.Delete(file); } catch { }
                // collapse rapid duplicate clicks of the same link (mashing the install button)
                // collapse rapid duplicate clicks of the same link (mashing the install button)
                var now = DateTime.UtcNow;
                if (_recentLinks.TryGetValue(link, out var t) && (now - t).TotalSeconds < 10) continue;
                _recentLinks[link] = now;
                _deepLinks.Enqueue(link);
            }
            if (_recentLinks.Count > 64)   // prune old dedup entries
                foreach (var k in new System.Collections.Generic.List<string>(_recentLinks.Keys))
                    if ((DateTime.UtcNow - _recentLinks[k]).TotalSeconds > 30) _recentLinks.Remove(k);
        }
        catch { /* transient; a later event retries */ }
    }

    private void UpdateViewport() =>
        _r.SetViewport(GraphicsDevice.PresentationParameters.BackBufferWidth, GraphicsDevice.PresentationParameters.BackBufferHeight);

    // apply queued file drops on the game thread (see Window.FileDrop)
    private void DrainFileDrops()
    {
        while (_drops.TryDequeue(out var d))
        {
            if (d.path.EndsWith(".ircbot", StringComparison.OrdinalIgnoreCase)) (_screen as MainScreen)?.OnIrcbotDrop(d.pos, d.path);
            else if (d.path.EndsWith(".ircnode", StringComparison.OrdinalIgnoreCase)) (_screen as MainScreen)?.OnNodeDrop(d.pos, d.path);
            else if (d.path.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) || Directory.Exists(d.path))
                (_screen as MainScreen)?.OnCalendarDrop(d.pos, d.path);
        }
    }

    private string _lastTitle = "";
    private void UpdateWindowTitle()
    {
        var bot = _app.ActiveBot;
        var rt = bot.Runtime;
        string status = rt.State switch
        {
            IrcState.Connected => "LIVE " + rt.CurrentNick,
            IrcState.Connecting => "CONNECTING",
            IrcState.Registering => "REGISTERING",
            IrcState.Error => "ERROR",
            _ => rt.Running ? "STARTING" : "OFFLINE",
        };
        string title = $"ircuitry - {bot.Name}{(_app.Dirty ? " *" : "")}  ·  {status}  ·  {bot.Graph.Nodes.Count} nodes / {bot.Graph.Connections.Count} wires"
                     + $"  ·  {_app.Bots.Count} bot{(_app.Bots.Count == 1 ? "" : "s")}";
        if (title != _lastTitle) { Window.Title = title; _lastTitle = title; }
    }

    protected override void Update(GameTime gameTime)
    {
        _clock.Tick(gameTime);
        _input.Active = IsActive;   // inputs read inert while the window isn't focused
        _input.Update();

        // F11 fullscreen toggle, F12 screenshot - game-console conveniences
        if (_input.KeyPressed(Keys.F11)) _gdm.ToggleFullScreen();
        if (_input.KeyPressed(Keys.F12)) SaveScreenshot(Path.Combine(AppContext.BaseDirectory, $"ircuitry-{++_screenshotSeq:000}.png"));

        if (TrayIcon.RestoreRequested) { TrayIcon.RestoreRequested = false; Ircuitry.Core.Sdl.Show(Window.Handle); }
        PumpTray();

        // while the splash is up it swallows input - a key/click skips it, and the editor underneath
        // is fed inert input so nothing registers. The screen still updates (keeps its layout/state live).
        bool splashUp = _splash is { Active: true };
        if (splashUp && IsActive && (_input.LeftPressed || _input.EnterPressed
            || _input.KeyPressed(Keys.Escape) || _input.KeyPressed(Keys.Space)))
            _splash!.Dismiss(_clock.Time);
        if (splashUp) _input.Active = false;

        if (IsActive) _screen.Update(_input, _clock);

        DrainFileDrops();
        if (_reloadRequested) { _reloadRequested = false; _app.ReloadIfChangedOnDisk(); }
        // poll the deep-link inbox every frame (a cheap File.Exists): inotify/FileSystemWatcher drops
        // events under rapid open-link clicks, which made links need many tries. Polling never misses.
        if (_inboxDir != null) DrainInbox();
        // process one deep-link install per frame. Its confirm modal stacks over any open modal, and we
        // just bring the window to the front (no unmaximise/resize - Restore() used to shrink it).
        if (!_deepLinks.IsEmpty && _screen is MainScreen dlScreen && _deepLinks.TryDequeue(out var link))
        {
            Ircuitry.Core.Sdl.BringToFront(Window.Handle);   // surface + focus, keep maximised state
            dlScreen.HandleDeepLink(link);
        }
        if (Ircuitry.Core.Sdl.CloseRequested)
        {
            Ircuitry.Core.Sdl.CloseRequested = false;
            var m = _screen as MainScreen;
            m?.RequestClosePrompt();
            if (m?.ClosePromptOpen == true) Ircuitry.Core.Sdl.BringToFront(Window.Handle);   // show the "are you sure?" if we're in the background
        }
        UpdateWindowTitle();

        // autosave on blur (not mid-typing): save when there are changes and no text field is being edited
        if (!_demo && _shotPath == null && _app.Dirty)
        {
            bool editing = _screen.SuppressAutosave;
            bool blurred = _wasEditing && !editing;
            if (!editing && (blurred || _clock.Time - _lastSave > 2f)) { _app.Save(announce: false); _lastSave = _clock.Time; }
            _wasEditing = editing;
        }

        if (_autoExit > 0 && _clock.Frame >= _autoExit) Exit();

        base.Update(gameTime);
    }

    private bool _wasEditing;

    private float _lastSave;
    private string? _pendingShot;     // window-menu screenshot, captured one frame after the menu closes
    private long _pendingShotFrame;

    /// <summary>Re-apply a theme's non-colour parts (colours are read live by <see cref="Theme"/>): swap the UI/display
    /// fonts and set whole-window translucency. Driven by <see cref="Ircuitry.Core.Themes.Changed"/>.</summary>
    private void ApplyAppearance()
    {
        try { _fonts.SetUiFont(Theme.Active.UiFont); _fonts.SetDisplayFont(Theme.Active.DisplayFont); } catch { }
        if (_shotPath == null) Ircuitry.Core.Sdl.SetOpacity(Window.Handle, Theme.WindowOpacity);   // offscreen runs stay crisp
    }

    /// <summary>A soft glassy treatment painted over the whole window when the theme's frosted glass is on:
    /// a top-down sheen and a faint inner highlight, so it reads like a glass pane even where the compositor
    /// can't blur the desktop behind a translucent window.</summary>
    private void DrawGlassSheen()
    {
        _r.Begin();
        int w = _r.ViewW, h = _r.ViewH;
        float topH = h * 0.34f;
        for (int i = 0; i < 6; i++)
        {
            float a = 0.05f * (1f - i / 6f);
            _r.Fill(new RectF(0, topH * i / 6f, w, topH / 6f + 1), Theme.WithAlpha(Color.White, a));
        }
        _r.RectOutline(new RectF(1, 1, w - 2, h - 2), Theme.WithAlpha(Color.White, 0.06f), 1.5f);
        _r.End();
    }

    protected override void Draw(GameTime gameTime)
    {
        UpdateViewport();
        GraphicsDevice.Clear(Theme.Void);
        _screen.Draw(_r, _clock);
        if (Theme.Glass) DrawGlassSheen();
        _splash?.Draw(_r, _clock);
        base.Draw(gameTime);

        // "Screenshot this window" from the window menu: wait one extra frame so the now-closed menu is gone
        // from the back buffer before we grab it (the click happened on the frame the menu was still drawn).
        if (_screen is MainScreen sms)
        {
            if (sms.ScreenshotRequested) { sms.ScreenshotRequested = false; _pendingShot = sms.ScreenshotPath; _pendingShotFrame = _clock.Frame + 1; }
            if (_pendingShot != null && _clock.Frame >= _pendingShotFrame)
            {
                SaveScreenshot(_pendingShot);
                sms.NotifyExternal(Ircuitry.Core.Icons.Glyph("camera") + " Saved to shots/" + Path.GetFileName(_pendingShot));   // toast next frame, not in the shot
                _pendingShot = null;
            }
        }

        if (_shotPath != null && !_shotTaken && _clock.Frame >= _shotAfterFrames && _clock.Time >= _shotAfterSeconds)
        {
            _shotTaken = true;
            SaveScreenshot(_shotPath);
            Exit();
        }

        _input.EndFrame();   // clear typed-char buffer after UI (drawn this frame) consumed it
    }

    private void SaveScreenshot(string path)
    {
        try
        {
            int w = GraphicsDevice.PresentationParameters.BackBufferWidth;
            int h = GraphicsDevice.PresentationParameters.BackBufferHeight;
            var data = new Color[w * h];
            GraphicsDevice.GetBackBufferData(data);
            using var tex = new Texture2D(GraphicsDevice, w, h);
            tex.SetData(data);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var fs = File.Create(path);
            tex.SaveAsPng(fs, w, h);
            Console.WriteLine($"ircuitry_SHOT {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("ircuitry_SHOT_FAIL " + ex);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _wsWatcher?.Dispose();
            _inboxWatcher?.Dispose();
            // final autosave - Dispose runs when the loop ends, including window-close
            if (!_demo && _shotPath == null) { try { _app?.Save(announce: false); } catch { } }
            _mock?.Dispose();
            _fonts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
