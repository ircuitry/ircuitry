using System;
using System.IO;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Ircuitry.Core;
using Ircuitry.Input;
using Ircuitry.Render;

namespace Ircuitry.UiKit;

/// <summary>
/// A window-host render process: one real OS window that paints a <see cref="UiScene"/> with ircuitry's own
/// renderer. The host (a node graph) streams a full scene as one JSON line on stdin per update; this process
/// swaps it in atomically and keeps advancing tweens. `--scene &lt;file&gt;` loads an initial scene; with no scene
/// it shows a built-in demo. `--shot &lt;png&gt; --frames N` grabs a frame and exits (used to prove the keystone).
/// </summary>
public sealed class UiWindowGame : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private Fonts _fonts = null!;
    private Renderer _r = null!;
    private readonly Clock _clock = new();
    private readonly InputState _input = new();
    private UiWindowScreen _screen = null!;

    private string? _scenePath, _shotPath;
    private int _shotFrames = 18;
    private bool _shotTaken, _demoMode, _demo3d;
    private volatile UiScene? _incoming;

    public static int Run(string[] args)
    {
        try { using var g = new UiWindowGame(args); g.Run(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine("ui-window failed: " + ex.Message); return 1; }
    }

    private UiWindowGame(string[] args)
    {
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 800,
            PreferredBackBufferHeight = 600,
            PreferMultiSampling = false,
            GraphicsProfile = GraphicsProfile.Reach,
            PreferredDepthStencilFormat = DepthFormat.Depth24Stencil8,   // for the 3D surface
            SynchronizeWithVerticalRetrace = true,
        };
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        IsFixedTimeStep = false;
        Window.Title = "ircuitry ui";
        Window.AllowUserResizing = true;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--scene" && i + 1 < args.Length) _scenePath = args[i + 1];
            if (args[i] == "--shot" && i + 1 < args.Length) _shotPath = args[i + 1];
            if (args[i] == "--frames" && i + 1 < args.Length && int.TryParse(args[i + 1], out var f)) _shotFrames = f;
            if (args[i] == "--demo3d") _demo3d = true;
        }
    }

    protected override void LoadContent()
    {
        _fonts = new Fonts(Path.Combine(AppContext.BaseDirectory, "assets", "fonts"));
        _r = new Renderer(GraphicsDevice, _fonts);
        _screen = new UiWindowScreen(GraphicsDevice);
        _demoMode = _scenePath == null;
        var scene = !_demoMode && File.Exists(_scenePath!) ? UiScene.FromJson(File.ReadAllText(_scenePath!))
                  : _demo3d ? Demo3DScene() : DemoScene();
        ApplyScene(scene);
        Window.TextInput += (_, e) => _input.PushChar(e.Character);   // SDL layout-correct typed chars for inputs
        new Thread(StdinLoop) { IsBackground = true, Name = "ui-stdin" }.Start();
    }

    private void ApplyScene(UiScene s)
    {
        _screen.Scene = s;
        _gdm.PreferredBackBufferWidth = Math.Clamp(s.Width, 160, 4096);
        _gdm.PreferredBackBufferHeight = Math.Clamp(s.Height, 120, 4096);
        try { _gdm.ApplyChanges(); } catch { }
        Window.Title = s.Title;
    }

    // each stdin line is one full scene (JSON). Swap it in on the game thread next Update.
    private void StdinLoop()
    {
        try { string? line; while ((line = Console.In.ReadLine()) != null) if (line.Length > 0) _incoming = UiScene.FromJson(line); }
        catch { }
    }

    protected override void Update(GameTime gt)
    {
        _clock.Tick(gt);
        var inc = _incoming;
        if (inc != null) { _incoming = null; ApplyScene(inc); }
        if (IsActive) _input.Update();
        _screen.Update(_input, _clock);
        foreach (var ev in _screen.DrainEvents())
        {
            // stream the interaction to the host (the node graph) as one line; the host fires a UI-event trigger
            Console.Out.WriteLine("@UIEVENT " + UiScene.EventJson(ev));
            try { Console.Out.Flush(); } catch { }
            if (_demoMode) DemoReact(ev);
        }
        _input.EndFrame();
        base.Update(gt);
    }

    // until the node host consumes events, the built-in demo reacts locally so a click is visibly alive: the
    // button pops + relabels, the logo bounces.
    private void DemoReact(UiEvent ev)
    {
        if (ev.Type != "click" || ev.Id != "go") return;
        var s = _screen.Scene;
        if (s.Find("go") is { } b) { b.Text = "Clicked!"; b.Tweens.Add(new Tween { Prop = "scale", From = 1.18f, To = 1f, Duration = 0.35f, Ease = "back" }); }
        s.Find("logo")?.Tweens.Add(new Tween { Prop = "scale", From = 1.3f, To = 1f, Duration = 0.55f, Ease = "bounce" });
    }

    protected override void Draw(GameTime gt)
    {
        GraphicsDevice.Clear(UiWindowScreen.Rgba(_screen.Scene.Bg));
        _screen.Draw(_r, _clock);
        base.Draw(gt);
        if (_shotPath != null && !_shotTaken && _clock.Frame >= _shotFrames)
        {
            _shotTaken = true;
            SaveScreenshot(_shotPath);
            Exit();
        }
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
            Console.WriteLine("ircuitry_SHOT " + path);
        }
        catch (Exception ex) { Console.WriteLine("ircuitry_SHOT_FAIL " + ex.Message); }
    }

    // A built-in scene that exercises panel + text + image + rect + button, with two live tweens, so a single
    // screenshot proves: real OS window + ircuitry's renderer + a node-style scene + media + animation.
    private static UiScene DemoScene()
    {
        string icon = Path.Combine(AppContext.BaseDirectory, "assets", "icons", "icon-64.png");
        var s = new UiScene { Title = "ircuitry ui ~ nodes drawing windows", Width = 760, Height = 460, Bg = 0x141018FF };
        s.Elements.Add(new UiElement { Id = "card", Kind = UiKind.Panel, X = 40, Y = 40, W = 680, H = 380, Color = 0x1E1B26FF, Radius = 20f });
        s.Elements.Add(new UiElement { Id = "title", Kind = UiKind.Text, Parent = "card", X = 32, Y = 28, Text = "ircuitry, drawn by nodes", Font = "display", FontSize = 30, Color = 0xF2EEF7FF });
        s.Elements.Add(new UiElement { Id = "sub", Kind = UiKind.Text, Parent = "card", X = 32, Y = 74, Text = "a real OS window, ircuitry's own renderer, media + tweens", Font = "sans", FontSize = 15, Color = 0x9C95AEFF });
        // a slab that breathes (alpha pingpong) + slides (x pingpong)
        var slab = new UiElement { Id = "slab", Kind = UiKind.Rect, Parent = "card", X = 32, Y = 130, W = 180, H = 120, Color = 0xFF6FB5FF, Radius = 16f };
        slab.Tweens.Add(new Tween { Prop = "x", From = 32, To = 220, Duration = 1.4f, Ease = "easeInOut", PingPong = true });
        slab.Tweens.Add(new Tween { Prop = "alpha", From = 0.45f, To = 1f, Duration = 0.9f, Ease = "easeInOut", PingPong = true });
        s.Elements.Add(slab);
        // the app icon, sliding the other way
        var img = new UiElement { Id = "logo", Kind = UiKind.Image, Parent = "card", X = 470, Y = 140, W = 96, H = 96, Src = icon };
        img.Tweens.Add(new Tween { Prop = "y", From = 140, To = 200, Duration = 1.1f, Ease = "easeInOut", PingPong = true });
        s.Elements.Add(img);
        s.Elements.Add(new UiElement { Id = "go", Kind = UiKind.Button, Parent = "card", X = 32, Y = 300, W = 160, H = 48, Text = "Click me", Color = 0x6C5CE7FF, TextColor = 0xFFFFFFFF, Radius = 14f, FontSize = 17 });
        s.Elements.Add(new UiElement { Id = "say", Kind = UiKind.Input, Parent = "card", X = 210, Y = 300, W = 280, H = 48, Text = "", Color = 0x554F66FF, TextColor = 0xF2EEF7FF, FontSize = 16 });
        return s;
    }

    // A 3D world (lit primitives with live tweens) composited under a 2D HUD - proves the 3D surface + the
    // game-with-overlay model in a single screenshot.
    private static UiScene Demo3DScene()
    {
        var s = new UiScene { Title = "ircuitry 3D ~ nodes drawing a world", Width = 760, Height = 460, Bg = 0x0E0B14FF };
        var w = new Scene3D { Cam = new Camera { Px = 0f, Py = 3.6f, Pz = 8f, Tx = 0f, Ty = 0.4f, Tz = 0f, Fov = 50f } };
        w.Objects.Add(new Obj3D { Id = "ground", Mesh = Mesh3D.Plane, Px = 0, Py = -1f, Pz = 0, Sx = 16, Sy = 1, Sz = 16, Color = 0x2A2740FF });
        var cube = new Obj3D { Id = "cube", Mesh = Mesh3D.Box, Px = -2.4f, Py = 0, Pz = 0, Sx = 1.6f, Sy = 1.6f, Sz = 1.6f, Color = 0xFF6FB5FF };
        cube.Tweens.Add(new Tween { Prop = "ry", From = 0, To = 360, Duration = 4f, Ease = "linear", Loop = true });
        w.Objects.Add(cube);
        var ball = new Obj3D { Id = "ball", Mesh = Mesh3D.Sphere, Px = 0.5f, Py = 0.2f, Pz = 0.4f, Sx = 2f, Sy = 2f, Sz = 2f, Color = 0x6C5CE7FF };
        ball.Tweens.Add(new Tween { Prop = "py", From = 0.2f, To = 1.7f, Duration = 1.3f, Ease = "easeInOut", PingPong = true });
        w.Objects.Add(ball);
        var cyl = new Obj3D { Id = "cyl", Mesh = Mesh3D.Cylinder, Px = 2.7f, Py = 0, Pz = -0.4f, Sx = 1.1f, Sy = 2.2f, Sz = 1.1f, Color = 0x4EC4B2FF };
        cyl.Tweens.Add(new Tween { Prop = "rx", From = 0, To = 360, Duration = 6f, Ease = "linear", Loop = true });
        w.Objects.Add(cyl);
        s.World = w;
        s.Elements.Add(new UiElement { Id = "hud", Kind = UiKind.Text, X = 24, Y = 20, Text = "3D world + 2D HUD, all from nodes", Font = "display", FontSize = 24, Color = 0xF2EEF7FF });
        s.Elements.Add(new UiElement { Id = "badge", Kind = UiKind.Panel, X = 24, Y = 408, W = 230, H = 34, Color = 0x1E1B26FF, Radius = 10 });
        s.Elements.Add(new UiElement { Id = "badgetxt", Kind = UiKind.Text, Parent = "badge", X = 12, Y = 8, Text = "MonoGame 3D pipeline", Font = "mono", FontSize = 14, Color = 0x9C95AEFF });
        return s;
    }
}
