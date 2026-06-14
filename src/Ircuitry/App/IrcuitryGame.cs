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

    public IrcuitryGame(string[] args)
    {
        _args = args;
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
        Exiting += (_, _) => { if (!_demo && _shotPath == null) _app.Save(); };
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

        UpdateViewport();
        if (_demo) StartDemo();
        var ms = _screen as MainScreen;

        // interactive runs: start maximized, and make the window's X prompt (exit / minimise) instead of quitting
        if (_shotPath == null && ms != null)
        {
            ms.OnExitRequested = Exit;
            // minimise → vanish to the tray if a tray icon is up, else a normal taskbar minimise
            ms.OnMinimizeRequested = () =>
            {
                if (TrayIcon.Available) Ircuitry.Core.Sdl.Hide(Window.Handle);
                else Ircuitry.Core.Sdl.Minimize(Window.Handle);
            };
            Ircuitry.Core.Sdl.InterceptClose();
            Ircuitry.Core.Sdl.Maximize(Window.Handle);
            TrayIcon.Start();
        }

        // flashy startup splash in interactive runs; suppressed for screenshots unless --splash is passed
        if (_shotPath == null || Array.IndexOf(_args, "--splash") >= 0) _splash = new Splash();

        // first-run onboarding (skipped in demo/screenshot modes); --tutorial forces it for capture
        if (_shotPath == null && !_demo) ms?.MaybeAutostartTutorial();
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
        if (Array.IndexOf(_args, "--showlabels") >= 0) ms?.DebugShowLabels();
        if (Array.IndexOf(_args, "--showschedule") >= 0) ms?.DebugSpawnSelect("event.schedule");
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
        string title = $"ircuitry - {bot.Name}{(_app.Dirty ? " ●" : "")}  ·  {status}  ·  {bot.Graph.Nodes.Count} nodes / {bot.Graph.Connections.Count} wires"
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
        if (Ircuitry.Core.Sdl.CloseRequested) { Ircuitry.Core.Sdl.CloseRequested = false; (_screen as MainScreen)?.RequestClosePrompt(); }
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

    protected override void Draw(GameTime gameTime)
    {
        UpdateViewport();
        GraphicsDevice.Clear(Theme.Void);
        _screen.Draw(_r, _clock);
        _splash?.Draw(_r, _clock);
        base.Draw(gameTime);

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
            // final autosave - Dispose runs when the loop ends, including window-close
            if (!_demo && _shotPath == null) { try { _app?.Save(announce: false); } catch { } }
            _mock?.Dispose();
            _fonts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
