using Microsoft.Xna.Framework;
using Ircuitry.Core;
using Ircuitry.Render;

namespace Ircuitry.Screens;

/// <summary>Cozy UI chrome - soft rounded panels with drop shadows, pastel headers, cute dots.</summary>
public static class Hud
{
    public const float PanelRadius = 16f;
    public const float HeaderH = 38f;

    /// <summary>A framed panel: soft shadow, creamy body, pastel header with a cute dot + title.</summary>
    public static void Panel(Renderer r, RectF rect, string title, Color accent)
    {
        // soft drop shadow
        r.RoundFill(new RectF(rect.X, rect.Y + 5, rect.W, rect.H), Theme.WithAlpha(Color.Black, 0.07f), PanelRadius);

        r.RoundFill(rect, Theme.Panel, PanelRadius);

        // pastel header band (rounded top, square bottom)
        var headTint = Theme.Mix(Theme.PanelHi, accent, 0.20f);
        r.RoundFill(new RectF(rect.X, rect.Y, rect.W, HeaderH + PanelRadius), headTint, PanelRadius);
        r.Fill(new RectF(rect.X, rect.Y + HeaderH, rect.W, PanelRadius), Theme.Panel);
        r.HLine(rect.X + 2, rect.Right - 2, rect.Y + HeaderH, Theme.WithAlpha(accent, 0.5f), 1.5f);

        SoftDot(r, new Vector2(rect.X + 18, rect.Y + HeaderH / 2f), 4.5f, accent);
        var f = r.Fonts.Get(FontKind.Display, 15);
        var sz = f.MeasureString(title);
        r.Text(f, title, new Vector2(rect.X + 32, rect.Y + (HeaderH - sz.Y) / 2f - 1), Theme.Text);

        r.RoundOutline(rect, Theme.Edge, PanelRadius);
    }

    /// <summary>(Retained for compatibility; the cozy theme doesn't draw HUD brackets.)</summary>
    public static void CornerBrackets(Renderer r, RectF rect, Color c, float len, float t = 2f) { }

    /// <summary>A status pill: rounded chip with a (optionally pulsing) dot and label.</summary>
    public static void Pill(Renderer r, RectF rect, string label, Color color, Clock clock, bool pulse = false)
    {
        r.RoundFill(rect, Theme.Mix(Theme.PanelHi, color, 0.18f), rect.H / 2f);
        r.RoundOutline(rect, Theme.WithAlpha(color, 0.8f), rect.H / 2f);
        float dotA = pulse ? 0.5f + 0.5f * clock.Sin01(1.1f) : 1f;
        var dot = new Vector2(rect.X + rect.H * 0.62f, rect.Center.Y);
        SoftDot(r, dot, 4.5f, Theme.WithAlpha(color, dotA));
        var f = r.Fonts.Get(FontKind.SansBold, 13);
        r.Text(f, label, new Vector2(dot.X + 12, rect.Center.Y - f.MeasureString(label).Y / 2f - 1), Theme.Mix(Theme.Text, color, 0.35f));
    }

    /// <summary>A glowing dot faked in alpha mode (no batch switch) via stacked discs.</summary>
    public static void SoftDot(Renderer r, Vector2 c, float radius, Color color)
    {
        r.Disc(c, radius * 2.4f, Theme.WithAlpha(color, 0.18f));
        r.Disc(c, radius * 1.6f, Theme.WithAlpha(color, 0.30f));
        r.Disc(c, radius, color);
    }

    /// <summary>Cozy theme: no CRT scanlines/vignette - keep it clean and warm.</summary>
    public static void Overlay(Renderer r, int w, int h) { }
}
