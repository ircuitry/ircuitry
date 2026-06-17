using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Ircuitry.Core;
using Ircuitry.Render;

namespace Ircuitry.App;

/// <summary>
/// A short, flashy startup splash overlaid on the app: a spinning circuit-comet ring with an
/// orbiting trio of pastel nodes, the brand mark popping in with a gentle bob, and the wordmark
/// rising up - then the whole thing fades to reveal the editor. Auto-dismisses (~2.3s); a key or
/// click skips straight to the fade-out. Purely decorative - owns no app state.
/// </summary>
public sealed class Splash
{
    private const float In = 0.55f;                 // logo pop-in
    private const float Hold = 1.25f;               // dwell
    private const float Out = 0.5f;                 // fade-out
    private const float Total = In + Hold + Out;    // ~2.3s

    private float _start = -1f;
    private bool _dismissing;
    private float _dismissAt;
    public bool Active { get; private set; } = true;

    /// <summary>Skip straight to the fade-out (on key/click). No-op once already fading.</summary>
    public void Dismiss(float now) { if (!_dismissing) { _dismissing = true; _dismissAt = now; } }

    public void Draw(Renderer r, Clock clock)
    {
        if (!Active) return;
        float now = clock.Time;
        if (_start < 0f) _start = now;
        float t = now - _start;

        // overall opacity: 1 until the fade window, then ramp down (a manual dismiss starts it early)
        float fadeStart = _dismissing ? _dismissAt - _start : Total - Out;
        float a = t < fadeStart ? 1f : 1f - (t - fadeStart) / Out;
        if (a <= 0f) { Active = false; return; }
        a = Clamp01(a);

        float cx = r.ViewW / 2f, cy = r.ViewH / 2f - 26f;
        var center = new Vector2(cx, cy);

        float p = Clamp01(t / In);
        float pop = EaseOutBack(p);                  // pop-in with a little overshoot
        float bob = MathF.Sin(t * 2.4f) * 4f;
        float breath = 0.5f + 0.5f * MathF.Sin(t * 2.2f);
        float ringR = 86f;
        float head = t * 3.4f;                        // sweep angle of the comet head
        var cols = new[] { Theme.Cyan, Theme.Amber, Theme.Lime };

        // ---- opaque cover ----
        r.Begin(BlendMode.Alpha);
        r.Fill(new RectF(0, 0, r.ViewW, r.ViewH), Theme.WithAlpha(Theme.Void, a));
        r.End();

        // ---- warm bloom behind the mark ----
        r.Begin(BlendMode.Add);
        r.Glow(center, 240f, Theme.WithAlpha(Theme.CyanDeep, 0.30f * a));
        r.Glow(center, 150f, Theme.WithAlpha(Theme.Amber, (0.10f + 0.06f * breath) * a));
        r.End();

        // ---- spinner comet + orbiting nodes (solids) ----
        r.Begin(BlendMode.Alpha);
        const int N = 14;
        for (int i = 0; i < N; i++)
        {
            float frac = i / (float)N;
            float ang = head - i * (MathF.PI * 2f / N);
            var pos = center + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * ringR * pop;
            float tail = 1f - frac;                  // bright head -> faint tail
            float sz = 1.6f + 3.2f * tail;
            r.Disc(pos, sz * pop, Theme.WithAlpha(Theme.Cyan, (0.12f + 0.7f * tail * tail) * a * pop));
        }
        for (int i = 0; i < 3; i++)
        {
            float ang = -t * 1.5f + i * (MathF.PI * 2f / 3f);
            var pos = center + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * (ringR + 26f) * pop;
            r.Disc(pos, 5.5f * pop, Theme.WithAlpha(cols[i], a));
        }
        r.End();

        // ---- their glows ----
        r.Begin(BlendMode.Add);
        var headPos = center + new Vector2(MathF.Cos(head), MathF.Sin(head)) * ringR * pop;
        r.Glow(headPos, 26f, Theme.WithAlpha(Theme.CyanBright, 0.6f * a * pop));
        for (int i = 0; i < 3; i++)
        {
            float ang = -t * 1.5f + i * (MathF.PI * 2f / 3f);
            var pos = center + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * (ringR + 26f) * pop;
            r.Glow(pos, 22f, Theme.WithAlpha(cols[i], 0.5f * a * pop));
        }
        r.Glow(center + new Vector2(0, bob), 96f, Theme.WithAlpha(Theme.AmberBright, 0.18f * a * pop));
        r.End();

        // ---- brand mark, on top of the bloom ----
        r.Begin(BlendMode.Alpha);
        float logo = 116f * pop;
        var lp = center + new Vector2(0, bob);
        if (r.Brand != null)
        {
            float sc = logo / r.Brand.Width;
            r.Sb.Draw(r.Brand, lp, null, Color.White * a, 0f,
                new Vector2(r.Brand.Width / 2f, r.Brand.Height / 2f), new Vector2(sc), SpriteEffects.None, 0f);
        }
        else r.Disc(lp, logo * 0.42f, Theme.WithAlpha(Theme.Cyan, a));
        r.End();

        // ---- wordmark, tagline, loader ----
        r.Begin(BlendMode.Alpha);
        float textP = Clamp01((t - 0.28f) / 0.45f);
        float ty = cy + 132f - (1f - EaseOut(textP)) * 12f;
        r.TextCenteredX(r.Fonts.Get(FontKind.Display, 46), "ircuitry", cx, ty, Theme.WithAlpha(Theme.Text, a * textP));
        float tagP = Clamp01((t - 0.55f) / 0.45f);
        r.TextCenteredX(r.Fonts.Get(FontKind.Sans, 15), "·  IRCv3 Bot Bakery  ·", cx, ty + 58f, Theme.WithAlpha(Theme.TextDim, a * tagP));

        float barW = 220f, barH = 5f;
        var track = new RectF(cx - barW / 2f, cy + 212f, barW, barH);
        r.RoundFill(track, Theme.WithAlpha(Theme.PanelLo, a * 0.9f), barH / 2f);
        float seg = 70f;
        float sx = track.X + (MathF.Sin(t * 2.0f) * 0.5f + 0.5f) * (barW - seg);
        r.RoundFill(new RectF(sx, track.Y, seg, barH), Theme.WithAlpha(Theme.Cyan, a), barH / 2f);
        r.End();
    }

    private static float Clamp01(float x) => x < 0 ? 0 : x > 1 ? 1 : x;
    private static float EaseOut(float x) => 1f - (1f - x) * (1f - x);
    private static float EaseOutBack(float x)
    {
        const float c1 = 1.70158f, c3 = c1 + 1f;
        float xm = x - 1f;
        return 1f + c3 * xm * xm * xm + c1 * xm * xm;
    }
}
