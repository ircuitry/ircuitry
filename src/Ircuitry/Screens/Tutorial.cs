using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Ircuitry.App;
using Ircuitry.Core;
using Ircuitry.Gui;
using Ircuitry.Render;

namespace Ircuitry.Screens;

public enum TutResult { None, Primary, Secondary, Skip }

/// <summary>
/// The gamified onboarding overlay: a dim backdrop with a spotlight cutout over the element in
/// focus, a coaching card with a step counter / title / body and up to two action buttons plus a
/// "Skip tutorial" escape, and a confetti celebration at the end. Pure presentation + step state;
/// the step orchestration (what each step requires, what its buttons do) lives in MainScreen.
/// </summary>
public sealed class Tutorial
{
    public bool Active { get; private set; }
    public int Step { get; private set; }
    private float _stepStart = -1f;

    public const int Total = 6;          // numbered "build a command" steps (welcome + finish are extra)
    public const int Welcome = 0;
    public const int Done = 7;

    // ---- one-time "seen" marker, so it auto-runs only for newcomers ----
    private static string DonePath => Path.Combine(AppModel.WorkspaceDir, "tutorial-done");
    public static bool DoneOnDisk => File.Exists(DonePath);
    private static void MarkDone()
    {
        try { Directory.CreateDirectory(AppModel.WorkspaceDir); File.WriteAllText(DonePath, "done"); } catch { }
    }

    public void Begin() { Active = true; Step = Welcome; _stepStart = -1f; }
    public void GoTo(int step) { Step = step; _stepStart = -1f; }
    public void Finish() { Active = false; MarkDone(); }
    public void Quit() { Active = false; MarkDone(); }

    public float StepAge(Clock clock) { if (_stepStart < 0f) _stepStart = clock.Time; return clock.Time - _stepStart; }

    /// <summary>Dim the screen with a clear hole around <paramref name="spot"/> and pulse a ring on it.</summary>
    public void Spotlight(Renderer r, Clock clock, int vw, int vh, RectF? spot, Color accent)
    {
        var dim = Theme.WithAlpha(Color.Black, 0.48f);
        if (spot is not RectF s) { r.Fill(new RectF(0, 0, vw, vh), dim); return; }
        r.Fill(new RectF(0, 0, vw, s.Y), dim);                              // top
        r.Fill(new RectF(0, s.Bottom, vw, vh - s.Bottom), dim);            // bottom
        r.Fill(new RectF(0, s.Y, s.X, s.H), dim);                          // left
        r.Fill(new RectF(s.Right, s.Y, vw - s.Right, s.H), dim);           // right

        float pu = clock.Pulse(1.0f);
        var ring = s.Inflate(6f + 4f * pu, 6f + 4f * pu);
        r.RoundOutline(ring, Theme.WithAlpha(accent, 0.55f + 0.4f * pu), 13f);
        r.RoundOutline(ring.Inflate(3f, 3f), Theme.WithAlpha(accent, 0.22f * pu), 16f);
    }

    /// <summary>Draw the coaching card and its buttons; returns which control the user activated.</summary>
    public TutResult Card(Renderer r, Ui ui, Clock clock, RectF card, Color accent,
        int index, string title, string body, string? primary, string? secondary, bool showSkip)
    {
        // little drop-in animation
        Hud.Panel(r, card, title, accent);

        var small = r.Fonts.Get(FontKind.Mono, 11);
        if (index >= 1 && index <= Total)
            r.TextRight(small, $"STEP {index} / {Total}", card.Right - 18, card.Y + (Hud.HeaderH - small.MeasureString(Ircuitry.Render.Renderer.SafeText("M")).Y) / 2f - 1, Theme.TextDim);
        else
            r.TextRight(small, index == 0 ? "WELCOME" : "DONE", card.Right - 18, card.Y + (Hud.HeaderH - small.MeasureString(Ircuitry.Render.Renderer.SafeText("M")).Y) / 2f - 1, Theme.TextDim);

        float x = card.X + 22, w = card.W - 44, y = card.Y + Hud.HeaderH + 16;
        var bf = r.Fonts.Get(FontKind.Sans, 14);
        foreach (var line in Wrap(r, bf, body, w)) { r.Text(bf, line, new Vector2(x, y), Theme.Text); y += 20f; }

        var res = TutResult.None;
        float by = card.Bottom - 50;
        if (showSkip && ui.Button("tut.skip", new RectF(card.X + 18, by, 124, 34), "Skip tutorial", Theme.Idle)) res = TutResult.Skip;

        float rx = card.Right - 18;
        if (primary != null)
        {
            var pr = new RectF(rx - 178, by, 178, 34); rx -= 178 + 10;
            if (ui.Button("tut.primary", pr, primary, accent, primary: true)) res = TutResult.Primary;
        }
        if (secondary != null)
        {
            var sr = new RectF(rx - 150, by, 150, 34);
            if (ui.Button("tut.secondary", sr, secondary, Theme.Idle)) res = TutResult.Secondary;
        }
        return res;
    }

    /// <summary>Falling confetti for the finale (deterministic - no RNG).</summary>
    public void Confetti(Renderer r, Clock clock, int vw, int vh)
    {
        float t = StepAge(clock);
        var cols = new[] { Theme.Cyan, Theme.Amber, Theme.Lime, Theme.Magenta, Theme.Violet, Theme.Sky };
        const int N = 90;
        for (int i = 0; i < N; i++)
        {
            float seedx = Frac(i * 0.61803398f);
            float speed = 90f + 150f * Frac(i * 2.3994f);
            float sway = MathF.Sin(t * (1.5f + Frac(i * 0.7f) * 2f) + i) * 26f;
            float x = seedx * vw + sway;
            float y = (-40f + (t * speed + Frac(i * 1.77f) * vh)) % (vh + 60f);
            float sz = 3f + 4f * Frac(i * 3.1f);
            var c = cols[i % cols.Length];
            r.Disc(new Vector2(x, y), sz, Theme.WithAlpha(c, 0.92f));
        }
    }

    private static float Frac(float v) => v - MathF.Floor(v);

    private static List<string> Wrap(Renderer r, FontStashSharp.DynamicSpriteFont f, string text, float maxW)
    {
        var lines = new List<string>();
        foreach (var para in text.Split('\n'))
        {
            var words = para.Split(' ');
            string cur = "";
            foreach (var word in words)
            {
                string trial = cur.Length == 0 ? word : cur + " " + word;
                if (r.Measure(f, trial).X > maxW && cur.Length > 0) { lines.Add(cur); cur = word; }
                else cur = trial;
            }
            lines.Add(cur);
        }
        return lines;
    }
}
