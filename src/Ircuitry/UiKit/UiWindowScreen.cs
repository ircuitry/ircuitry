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
/// atomically from the host between frames; tweens advance every Update.
/// </summary>
public sealed class UiWindowScreen : IScreen
{
    private UiScene _scene = new();
    private readonly GraphicsDevice _gd;
    private readonly Dictionary<string, Texture2D?> _images = new();

    public UiWindowScreen(GraphicsDevice gd) { _gd = gd; }

    public bool SuppressAutosave => false;
    public UiScene Scene { get => _scene; set => _scene = value ?? new(); }

    public void Update(InputState input, Clock clock) => _scene.Advance(clock.Dt);

    public void Draw(Renderer r, Clock clock)
    {
        var scene = _scene;
        r.Begin();
        foreach (var e in scene.Elements)
            if (e.Visible) DrawElement(r, scene, e);
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

    private void DrawElement(Renderer r, UiScene s, UiElement e)
    {
        var (ax, ay) = Abs(s, e);
        float w = e.W * e.Scale, h = e.H * e.Scale;
        var rect = new RectF(ax, ay, w, h);
        var col = Rgba(e.Color, e.Alpha);
        switch (e.Kind)
        {
            case UiKind.Panel:
            case UiKind.Button:
                if (e.Filled) r.RoundFill(rect, col, e.Radius); else r.RoundOutline(rect, col, e.Radius);
                if (e.Text.Length > 0) r.TextCentered(r.Fonts.Get(FK(e.Font), e.FontSize), e.Text, rect, Rgba(e.TextColor, e.Alpha));
                break;

            case UiKind.Rect:
                if (e.Filled) r.Fill(rect, col); else r.RectOutline(rect, col);
                break;

            case UiKind.Text:
                r.Text(r.Fonts.Get(FK(e.Font), e.FontSize), e.Text, new Vector2(ax, ay), col);
                break;

            case UiKind.Input:
                r.RoundFill(rect, Rgba(0x000000FF, 0.25f * e.Alpha), 8f);
                r.RoundOutline(rect, col, 8f);
                r.Text(r.Fonts.Get(FK(e.Font), e.FontSize), e.Text, new Vector2(ax + 8f, ay + (h - e.FontSize) / 2f), Rgba(e.TextColor, e.Alpha));
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
}
