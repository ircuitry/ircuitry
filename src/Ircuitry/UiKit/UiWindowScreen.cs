using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Ircuitry.App;
using Ircuitry.Core;
using Ircuitry.Input;
using Ircuitry.Render;

namespace Ircuitry.UiKit;

/// <summary>
/// Paints a <see cref="UiScene"/> with ircuitry's own immediate-mode <see cref="Renderer"/> - the exact renderer
/// that draws the editor, which is what makes "build ircuitry in ircuitry" literal. The scene can be swapped
/// atomically from the host between frames; tweens advance every Update. Buttons/inputs are hit-tested for
/// hover/press feedback and produce click/submit events the host streams back into the graph.
/// </summary>
public sealed class UiWindowScreen : IScreen
{
    private UiScene _scene = new();
    private readonly GraphicsDevice _gd;
    private readonly Dictionary<string, Texture2D?> _images = new();

    private string? _hoverId, _pressId, _focusId, _dragId;
    private readonly object _evLock = new();
    private readonly List<UiEvent> _events = new();
    private Scene3DRenderer? _r3d;

    // When this screen paints an in-app dock panel instead of owning a whole OS window, the host sets an Origin
    // (the panel's content top-left, so a scene authored at 0,0 lands inside the panel) and a Clip (the panel rect,
    // so drawing is scissored and only mouse-in-panel interacts). Both default off -> normal full-window behaviour.
    public Microsoft.Xna.Framework.Vector2 Origin;
    public RectF? Clip;

    // first-person controller state (Controls == "fps")
    private bool _fpsInit, _gunBaseSet;
    private float _yaw, _pitch, _ex, _ey, _ez, _bob, _gunBaseY;

    public UiWindowScreen(GraphicsDevice gd) { _gd = gd; }

    public bool SuppressAutosave => _focusId != null;   // a focused text field is mid-edit
    public UiScene Scene
    {
        get => _scene;
        set
        {
            var v = value ?? new();
            // in FPS mode the WINDOW owns the camera - carry it across a re-stream so it doesn't snap back
            if (_fpsInit && v.Controls == "fps" && v.World != null) { v.World.Cam.Px = _ex; v.World.Cam.Py = _ey; v.World.Cam.Pz = _ez; }
            if (v.Controls != "fps") _fpsInit = false;
            _gunBaseSet = false;
            _scene = v; _focusId = _pressId = _hoverId = _dragId = null;
        }
    }

    /// <summary>Take and clear the events queued since the last drain (called by the host each frame).</summary>
    public List<UiEvent> DrainEvents()
    {
        lock (_evLock)
        {
            if (_events.Count == 0) return new();
            var c = new List<UiEvent>(_events); _events.Clear(); return c;
        }
    }

    private void Emit(UiEvent e) { lock (_evLock) _events.Add(e); }

    // a snapshot of every text field's current value, so a button-click (or submit) event can carry a whole form
    private static Dictionary<string, string> SnapshotFields(UiScene s)
    {
        var d = new Dictionary<string, string>();
        foreach (var e in s.Elements)
        {
            if (e.Kind == UiKind.Input) d[e.Id] = e.Text;
            else if (e.Kind == UiKind.Slider) d[e.Id] = FormatValue(e.Value);
        }
        return d;
    }

    private static string FormatValue(float v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    // WASD walk + arrow-key look on the 3D camera, with optional gun-bob + muzzle-flash conventions (elements
    // named "gun" / "flash"). Camera height is held fixed (no flying), the classic shooter feel.
    private void UpdateFps(InputState input, float dt)
    {
        var cam = _scene.World!.Cam;
        if (!_fpsInit)
        {
            _ex = cam.Px; _ey = cam.Py; _ez = cam.Pz;
            float dx = cam.Tx - cam.Px, dy = cam.Ty - cam.Py, dz = cam.Tz - cam.Pz;
            float flat = MathF.Max(0.001f, MathF.Sqrt(dx * dx + dz * dz));
            _yaw = MathF.Atan2(dx, -dz) * 180f / MathF.PI;
            _pitch = MathF.Atan2(dy, flat) * 180f / MathF.PI;
            _fpsInit = true;
        }
        float look = 95f * dt;
        if (input.KeyDown(Keys.Left)) _yaw -= look;
        if (input.KeyDown(Keys.Right)) _yaw += look;
        if (input.KeyDown(Keys.Up)) _pitch += look * 0.7f;
        if (input.KeyDown(Keys.Down)) _pitch -= look * 0.7f;
        _pitch = Math.Clamp(_pitch, -85f, 85f);

        float yr = _yaw * MathF.PI / 180f;
        float fx = MathF.Sin(yr), fz = -MathF.Cos(yr);   // horizontal forward
        float rx = MathF.Cos(yr), rz = MathF.Sin(yr);    // strafe right
        float sp = 5f * dt; bool moving = false;
        if (input.KeyDown(Keys.W)) { _ex += fx * sp; _ez += fz * sp; moving = true; }
        if (input.KeyDown(Keys.S)) { _ex -= fx * sp; _ez -= fz * sp; moving = true; }
        if (input.KeyDown(Keys.A)) { _ex -= rx * sp; _ez -= rz * sp; moving = true; }
        if (input.KeyDown(Keys.D)) { _ex += rx * sp; _ez += rz * sp; moving = true; }

        float pr = _pitch * MathF.PI / 180f, cp = MathF.Cos(pr);
        cam.Px = _ex; cam.Py = _ey; cam.Pz = _ez;
        cam.Tx = _ex + fx * cp; cam.Ty = _ey + MathF.Sin(pr); cam.Tz = _ez + fz * cp;

        var gun = _scene.Find("gun");
        if (gun != null)
        {
            if (!_gunBaseSet) { _gunBaseY = gun.Y; _gunBaseSet = true; }
            _bob += (moving ? 9f : 3f) * dt;
            gun.Y = _gunBaseY + MathF.Sin(_bob) * (moving ? 7f : 2f);
        }
        var flash = _scene.Find("flash");
        if (flash != null)
        {
            if (input.LeftPressed) flash.Alpha = 0.95f;
            else { flash.Alpha *= 0.78f; if (flash.Alpha < 0.02f) flash.Alpha = 0f; }
        }
    }

    public void Update(InputState input, Clock clock)
    {
        var s = _scene;
        s.Advance(clock.Dt);
        if (s.Controls == "fps" && s.World != null) UpdateFps(input, clock.Dt);

        // topmost interactive element under the cursor (in-panel: only when the cursor is over this panel)
        var m = input.Mouse;
        bool inClip = Clip is not { } cr || Hit(cr, m);
        string? over = null;
        if (inClip)
            for (int i = s.Elements.Count - 1; i >= 0; i--)
            {
                var e = s.Elements[i];
                if (e.Visible && (e.Kind == UiKind.Button || e.Kind == UiKind.Input || e.Kind == UiKind.Slider) && Hit(Bounds(s, e), m)) { over = e.Id; break; }
            }
        _hoverId = over;

        if (input.LeftPressed)
        {
            if (!inClip) { _focusId = null; _pressId = null; }   // a click outside this panel blurs its field
            else
            {
                _pressId = over;
                _focusId = over != null && s.Find(over)?.Kind == UiKind.Input ? over : null;   // focus an input, blur otherwise
                _dragId = over != null && s.Find(over)?.Kind == UiKind.Slider ? over : null;    // grab a slider handle
                if (_dragId != null && s.Find(_dragId) is { } sl0) DragSlider(s, sl0, m.X);     // jump straight to the click point
            }
        }
        if (_dragId != null)                                                                // follow the mouse while held
        {
            if (input.LeftDown && s.Find(_dragId) is { Kind: UiKind.Slider } sl) DragSlider(s, sl, m.X);
            else _dragId = null;
        }
        if (input.LeftReleased)
        {
            if (_pressId != null && _pressId == over && s.Find(_pressId)?.Kind == UiKind.Button)
                Emit(new UiEvent { Type = "click", Id = _pressId, Fields = SnapshotFields(s) });
            _pressId = null;
        }

        // text editing on the focused input
        if (_focusId != null && s.Find(_focusId) is { Kind: UiKind.Input } box)
        {
            foreach (var c in input.Typed) if (!char.IsControl(c)) box.Text += c;
            if (input.BackspacePressed && box.Text.Length > 0) box.Text = box.Text[..^1];
            if (input.EnterPressed)
            {
                if (box.Multiline) box.Text += "\n";   // newline in a multiline field; a Save button persists the text
                else Emit(new UiEvent { Type = "submit", Id = _focusId, Value = box.Text, Fields = SnapshotFields(s) });
            }
        }
    }

    // map the mouse x to a slider's value (snapped to Step), within its draggable track. Geometry MUST match the
    // Slider case in DrawElement so the handle sits under the cursor.
    private void DragSlider(UiScene s, UiElement e, float mouseX)
    {
        var r = Bounds(s, e);
        float pad = r.H * 0.5f, valW = 48f;
        float tl = r.X + pad, tr = r.X + r.W - valW - pad;
        if (tr <= tl) { e.Value = e.Min; return; }
        float t = System.Math.Clamp((mouseX - tl) / (tr - tl), 0f, 1f);
        float v = e.Min + t * (e.Max - e.Min);
        if (e.Step > 0f) v = MathF.Round(v / e.Step) * e.Step;
        float lo = MathF.Min(e.Min, e.Max), hi = MathF.Max(e.Min, e.Max);
        string before = FormatValue(e.Value);
        e.Value = System.Math.Clamp(v, lo, hi);
        string after = FormatValue(e.Value);
        if (after != before) Emit(new UiEvent { Type = "change", Id = e.Id, Value = after });   // live: drive things while dragging
    }

    public void Draw(Renderer r, Clock clock)
    {
        var scene = _scene;
        if (scene.World != null && Clip == null) (_r3d ??= new Scene3DRenderer(_gd)).Draw(scene.World);   // 3D world first (with depth); panels are 2D
        Microsoft.Xna.Framework.Rectangle? scissor = Clip is { } c
            ? new Microsoft.Xna.Framework.Rectangle((int)c.X, (int)c.Y, (int)MathF.Ceiling(c.W), (int)MathF.Ceiling(c.H))
            : null;
        r.Begin(scissor: scissor);                                                        // 2D overlay (scissored to the panel when in-app)
        foreach (var e in scene.Elements)
            if (e.Visible) DrawElement(r, scene, e, clock);
        r.End();
    }

    // resolve absolute top-left by summing parent offsets (cheap; guards against cycles)
    private static (float x, float y) Abs(UiScene s, UiElement e)
    {
        float x = e.X, y = e.Y; var p = e.Parent; int guard = 0;
        while (!string.IsNullOrEmpty(p) && guard++ < 64)
        {
            var pe = s.Find(p!); if (pe == null) break;
            x += pe.X; y += pe.Y; p = pe.Parent;
        }
        return (x, y);
    }

    private RectF Bounds(UiScene s, UiElement e)
    {
        var (ax, ay) = Abs(s, e);
        return new RectF(ax + Origin.X, ay + Origin.Y, e.W * e.Scale, e.H * e.Scale);   // Origin shifts a panel scene into its dock rect
    }

    private static bool Hit(RectF r, Vector2 m) => m.X >= r.X && m.X < r.X + r.W && m.Y >= r.Y && m.Y < r.Y + r.H;

    private void DrawElement(Renderer r, UiScene s, UiElement e, Clock clock)
    {
        var rect = Bounds(s, e);
        var col = Rgba(e.Color, e.Alpha);
        switch (e.Kind)
        {
            case UiKind.Panel:
                if (e.Filled) r.RoundFill(rect, col, e.Radius); else r.RoundOutline(rect, col, e.Radius);
                if (e.Text.Length > 0) r.TextCentered(r.Fonts.Get(FK(e.Font), e.FontSize), e.Text, rect, Rgba(e.TextColor, e.Alpha));
                break;

            case UiKind.Button:
                var fill = e.Id == _pressId ? Shade(col, 0.80f) : e.Id == _hoverId ? Shade(col, 1.14f) : col;
                if (e.Filled) r.RoundFill(rect, fill, e.Radius); else r.RoundOutline(rect, fill, e.Radius);
                if (e.Id == _hoverId && e.Filled) r.RoundOutline(rect, Rgba(0xFFFFFFFF, 0.20f * e.Alpha), e.Radius);
                if (e.Text.Length > 0) r.TextCentered(r.Fonts.Get(FK(e.Font), e.FontSize), e.Text, rect, Rgba(e.TextColor, e.Alpha));
                break;

            case UiKind.Rect:
                if (e.Filled) r.Fill(rect, col); else r.RectOutline(rect, col);
                break;

            case UiKind.Text:
                r.Text(r.Fonts.Get(FK(e.Font), e.FontSize), e.Text, new Vector2(rect.X, rect.Y), col);
                break;

            case UiKind.Input:
                bool focused = e.Id == _focusId;
                bool blink = focused && clock.Pulse(1f) > 0.5f;
                r.RoundFill(rect, Rgba(0x000000FF, 0.25f * e.Alpha), 8f);
                r.RoundOutline(rect, focused ? Rgba(0xFFFFFFFF, e.Alpha) : col, 8f);
                if (e.Multiline) { DrawMultiline(r, e, rect, blink); break; }
                var font = r.Fonts.Get(FK(e.Font), e.FontSize);
                float tx = rect.X + 8f, ty = rect.Y + (rect.H - e.FontSize) / 2f;
                r.Text(font, e.Text, new Vector2(tx, ty), Rgba(e.TextColor, e.Alpha));
                if (blink)   // blinking caret
                {
                    float cx = tx + font.MeasureString(e.Text).X + 1f;
                    r.VLine(cx, ty + 1f, ty + e.FontSize, Rgba(0xFFFFFFFF, e.Alpha));
                }
                break;

            case UiKind.Slider:
            {
                float h = rect.H, pad = h * 0.5f, valW = 48f, cy = rect.Y + h * 0.5f;
                float tl = rect.X + pad, tr = rect.X + rect.W - valW - pad;
                if (tr < tl + 8f) tr = tl + 8f;
                float tnorm = e.Max != e.Min ? System.Math.Clamp((e.Value - e.Min) / (e.Max - e.Min), 0f, 1f) : 0f;
                float kx = tl + tnorm * (tr - tl);
                r.RoundFill(new RectF(tl, cy - 3f, tr - tl, 6f), Rgba(0x000000FF, 0.35f * e.Alpha), 3f);   // track
                r.RoundFill(new RectF(tl, cy - 3f, kx - tl, 6f), Rgba(e.Color, e.Alpha), 3f);             // filled portion
                float kr = pad - 3f;
                if (e.Id == _hoverId || e.Id == _dragId)                                                  // soft halo on hover/drag
                    r.RoundFill(new RectF(kx - kr - 3f, cy - kr - 3f, (kr + 3f) * 2f, (kr + 3f) * 2f), Rgba(e.Color, 0.30f * e.Alpha), kr + 3f);
                r.RoundFill(new RectF(kx - kr, cy - kr, kr * 2f, kr * 2f), Rgba(0xF4F0F8FF, e.Alpha), kr); // knob
                var vf = r.Fonts.Get(FK(e.Font), e.FontSize);
                r.TextRight(vf, FormatValue(e.Value), rect.X + rect.W, cy - e.FontSize / 2f, Rgba(e.TextColor, e.Alpha));   // live value
                break;
            }

            case UiKind.Image:
                var tex = LoadImage(e.Src);
                if (tex != null) r.Image(tex, rect, Rgba(0xFFFFFFFF, e.Alpha));
                else r.RoundFill(rect, Rgba(0x2A2730FF, e.Alpha), e.Radius);   // placeholder until the file loads
                break;
        }
    }

    // a wrapped, bottom-anchored multiline text field: word-wraps to the box width (honouring explicit newlines),
    // shows the lines that fit so the end stays visible while typing, and draws the caret after the last line.
    private void DrawMultiline(Renderer r, UiElement e, RectF rect, bool blink)
    {
        var font = r.Fonts.Get(FK(e.Font), e.FontSize);
        float maxW = rect.W - 16f;
        int lineH = e.FontSize + 4;
        int maxLines = System.Math.Max(1, (int)((rect.H - 12f) / lineH));
        var lines = new List<string>();
        foreach (var para in (e.Text ?? "").Split('\n'))
        {
            if (para.Length == 0) { lines.Add(""); continue; }
            string cur = "";
            foreach (var word in para.Split(' '))
            {
                string trial = cur.Length == 0 ? word : cur + " " + word;
                if (cur.Length == 0 || r.Measure(font, trial).X <= maxW) cur = trial;
                else { lines.Add(cur); cur = word; }
            }
            lines.Add(cur);
        }
        int start = System.Math.Max(0, lines.Count - maxLines);
        var col = Rgba(e.TextColor, e.Alpha);
        for (int i = start; i < lines.Count; i++)
            r.Text(font, lines[i], new Vector2(rect.X + 8f, rect.Y + 8f + (i - start) * lineH), col);
        if (blink)
        {
            string last = lines.Count > 0 ? lines[^1] : "";
            float cx = rect.X + 8f + r.Measure(font, last).X + 1f;
            float cy = rect.Y + 8f + (System.Math.Min(lines.Count, maxLines) - 1) * lineH;
            r.VLine(cx, cy + 1f, cy + e.FontSize, Rgba(0xFFFFFFFF, e.Alpha));
        }
    }

    private Texture2D? LoadImage(string src)
    {
        if (string.IsNullOrEmpty(src)) return null;
        if (_images.TryGetValue(src, out var cached)) return cached;
        Texture2D? tex = null;
        try { if (File.Exists(src)) { using var fs = File.OpenRead(src); tex = Texture2D.FromStream(_gd, fs); } } catch { }
        _images[src] = tex;
        return tex;
    }

    private static FontKind FK(string f) => f switch
    {
        "bold" or "sansbold" => FontKind.SansBold,
        "mono" => FontKind.Mono,
        "monobold" => FontKind.MonoBold,
        "display" => FontKind.Display,
        _ => FontKind.Sans,
    };

    /// <summary>0xRRGGBBAA + an alpha multiplier -&gt; XNA Color.</summary>
    public static Color Rgba(uint rgba, float a = 1f)
    {
        byte R = (byte)(rgba >> 24), G = (byte)(rgba >> 16), B = (byte)(rgba >> 8), A = (byte)rgba;
        return new Color(R, G, B, (byte)Math.Clamp(A * a, 0f, 255f));
    }

    private static Color Shade(Color c, float f) => new(
        (byte)Math.Clamp(c.R * f, 0f, 255f), (byte)Math.Clamp(c.G * f, 0f, 255f), (byte)Math.Clamp(c.B * f, 0f, 255f), c.A);
}
