using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
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

    private string? _hoverId, _pressId, _focusId;
    private readonly object _evLock = new();
    private readonly List<UiEvent> _events = new();

    public UiWindowScreen(GraphicsDevice gd) { _gd = gd; }

    public bool SuppressAutosave => _focusId != null;   // a focused text field is mid-edit
    public UiScene Scene { get => _scene; set { _scene = value ?? new(); _focusId = _pressId = _hoverId = null; } }

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

    public void Update(InputState input, Clock clock)
    {
        var s = _scene;
        s.Advance(clock.Dt);

        // topmost interactive element under the cursor
        var m = input.Mouse;
        string? over = null;
        for (int i = s.Elements.Count - 1; i >= 0; i--)
        {
            var e = s.Elements[i];
            if (e.Visible && (e.Kind == UiKind.Button || e.Kind == UiKind.Input) && Hit(Bounds(s, e), m)) { over = e.Id; break; }
        }
        _hoverId = over;

        if (input.LeftPressed)
        {
            _pressId = over;
            _focusId = over != null && s.Find(over)?.Kind == UiKind.Input ? over : null;   // focus an input, blur otherwise
        }
        if (input.LeftReleased)
        {
            if (_pressId != null && _pressId == over && s.Find(_pressId)?.Kind == UiKind.Button)
                Emit(new UiEvent { Type = "click", Id = _pressId });
            _pressId = null;
        }

        // text editing on the focused input
        if (_focusId != null && s.Find(_focusId) is { Kind: UiKind.Input } box)
        {
            foreach (var c in input.Typed) if (!char.IsControl(c)) box.Text += c;
            if (input.BackspacePressed && box.Text.Length > 0) box.Text = box.Text[..^1];
            if (input.EnterPressed) Emit(new UiEvent { Type = "submit", Id = _focusId, Value = box.Text });
        }
    }

    public void Draw(Renderer r, Clock clock)
    {
        var scene = _scene;
        r.Begin();
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

    private static RectF Bounds(UiScene s, UiElement e)
    {
        var (ax, ay) = Abs(s, e);
        return new RectF(ax, ay, e.W * e.Scale, e.H * e.Scale);
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
                r.RoundFill(rect, Rgba(0x000000FF, 0.25f * e.Alpha), 8f);
                r.RoundOutline(rect, focused ? Rgba(0xFFFFFFFF, e.Alpha) : col, 8f);
                var font = r.Fonts.Get(FK(e.Font), e.FontSize);
                float tx = rect.X + 8f, ty = rect.Y + (rect.H - e.FontSize) / 2f;
                r.Text(font, e.Text, new Vector2(tx, ty), Rgba(e.TextColor, e.Alpha));
                if (focused && clock.Pulse(1f) > 0.5f)   // blinking caret
                {
                    float cx = tx + font.MeasureString(e.Text).X + 1f;
                    r.VLine(cx, ty + 1f, ty + e.FontSize, Rgba(0xFFFFFFFF, e.Alpha));
                }
                break;

            case UiKind.Image:
                var tex = LoadImage(e.Src);
                if (tex != null) r.Image(tex, rect, Rgba(0xFFFFFFFF, e.Alpha));
                else r.RoundFill(rect, Rgba(0x2A2730FF, e.Alpha), e.Radius);   // placeholder until the file loads
                break;
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
