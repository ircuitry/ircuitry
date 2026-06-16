using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Ircuitry.Core;
using Ircuitry.Graph;
using Ircuitry.Render;

namespace Ircuitry.Screens;

// The "Bot Bakery": pick two or more of your bots and merge them into a new one, resolving any command
// clashes by hand, then watch them bake. A pure-UI wrapper over BotMerge.
public partial class MainScreen
{
    private bool _bakeryOpen, _bakeryJustOpened;
    private readonly HashSet<int> _bakeSel = new();     // selected bot indices (into _app.Bots)
    private List<int> _bakeOrder = new();               // selection in stable order -> merge source order
    private List<BotMerge.Conflict> _bakeConf = new();
    private string _bakeSelKey = "";                    // recompute conflicts when the selection set changes
    private int _bakeConn;                              // which selected bot's connection to keep (order index)
    private string _bakeName = "";

    // bake animation
    private float _bakeStart = -1f;                     // <0 = idle
    private Action? _bakeFinish;                        // creates the merged bot at the end of the animation
    private string _bakeAnimName = "";

    public void OpenBakery()
    {
        _l = Layout.Compute(_vw, _vh, _consoleH);
        _bakeryOpen = true; _bakeryJustOpened = true;
        _bakeSel.Clear(); _bakeConf.Clear(); _bakeSelKey = ""; _bakeConn = 0; _bakeName = "";
    }
    public void DebugOpenBakery()
    {
        while (_app.Bots.Count < 3) _app.AddBot("pingpong");   // two+ bots that both bind !ping -> a clash to resolve
        _app.Bots[1].Name = "Greeter"; _app.Bots[2].Name = "Trivia";
        OpenBakery();
        _bakeSel.Add(1); _bakeSel.Add(2);
        RefreshBakeConflicts();
    }
    private bool _bakeDebugPending;
    public void DebugBakeAnim() => _bakeDebugPending = true;   // started on the next Draw, when the clock is live

    private void RefreshBakeConflicts()
    {
        _bakeOrder = _bakeSel.OrderBy(i => i).ToList();
        string key = string.Join(",", _bakeOrder);
        if (key == _bakeSelKey) return;
        _bakeSelKey = key;
        if (_bakeOrder.Count >= 2)
        {
            var graphs = _bakeOrder.Select(i => _app.Bots[i].Graph).ToList();
            _bakeConf = BotMerge.Detect(graphs);
            _bakeConn = 0;
            _bakeName = string.Join(" + ", _bakeOrder.Take(3).Select(i => _app.Bots[i].Name));
            if (_bakeOrder.Count > 3) _bakeName += " +";
        }
        else { _bakeConf.Clear(); }
    }

    private void DrawBakeryModal(Renderer r, Clock clock)
    {
        r.Begin();
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.5f));
        float pw = Math.Min(760, _vw - 60), ph = Math.Min(640, _vh - 60);
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        Hud.Panel(r, panel, "🧁  Bot Bakery", Theme.Berry);

        float pad = 22, cx = panel.X + pad, cw = panel.W - 2 * pad;
        float y = panel.Y + Hud.HeaderH + 12;
        var sans = r.Fonts.Get(FontKind.Sans, 12);
        var lbl = r.Fonts.Get(FontKind.SansBold, 10);
        void Label(string t, float ly) => r.Text(lbl, t, new Vector2(cx, ly), Theme.TextDim);

        r.Text(sans, "Pick two or more bots to merge into a brand-new one. Originals are left untouched.", new Vector2(cx, y), Theme.TextDim);
        y += 22;

        RefreshBakeConflicts();

        // ---- bot picker (two columns) ----
        Label("YOUR BOTS", y); y += 16;
        float colW = (cw - 12) / 2f, rowH = 32;
        int rows = (_app.Bots.Count + 1) / 2;
        for (int i = 0; i < _app.Bots.Count; i++)
        {
            int col = i % 2, rw = i / 2;
            var rect = new RectF(cx + col * (colW + 12), y + rw * (rowH + 6), colW, rowH);
            bool sel = _bakeSel.Contains(i);
            bool hover = rect.Contains(In.Mouse);
            r.RoundFill(rect, sel ? Theme.Mix(Theme.PanelHi, Theme.Berry, 0.20f) : (hover ? Theme.PanelHi : Theme.PanelLo), 8f);
            r.RoundOutline(rect, sel ? Theme.Berry : Theme.Hairline, 8f);
            var box = new RectF(rect.X + 8, rect.Center.Y - 8, 16, 16);
            r.RoundFill(box, sel ? Theme.Berry : Theme.Panel, 4f);
            r.RoundOutline(box, sel ? Theme.Berry : Theme.Edge, 4f);
            if (sel) r.Text(r.Fonts.Get(FontKind.SansBold, 12), "✓", new Vector2(box.X + 3, box.Y), Theme.TextInk);
            r.Text(r.Fonts.Get(FontKind.SansBold, 12), r.Ellipsize(r.Fonts.Get(FontKind.SansBold, 12), _app.Bots[i].Name, colW - 90), new Vector2(rect.X + 32, rect.Center.Y - 9), Theme.Text);
            r.TextRight(r.Fonts.Get(FontKind.Sans, 10), _app.Bots[i].Graph.Nodes.Count + " nodes", rect.Right - 8, rect.Center.Y - 7, Theme.TextFaint);
            if (hover && In.LeftPressed) { if (sel) _bakeSel.Remove(i); else _bakeSel.Add(i); }
        }
        y += rows * (rowH + 6) + 10;

        // ---- conflicts ----
        if (_bakeOrder.Count >= 2)
        {
            if (_bakeConf.Count == 0)
            {
                r.RoundFill(new RectF(cx, y, cw, 30), Theme.WithAlpha(Theme.Ok, 0.14f), 8f);
                r.Text(sans, "✓ No command clashes - these bots merge cleanly.", new Vector2(cx + 10, y + 8), Theme.Text);
                y += 40;
            }
            else
            {
                Label($"RESOLVE {_bakeConf.Count} CLASH" + (_bakeConf.Count == 1 ? "" : "ES") + "  (both bots bind the same command)", y); y += 18;
                foreach (var c in _bakeConf)
                {
                    if (y > panel.Bottom - 150) { r.Text(sans, "…", new Vector2(cx, y), Theme.TextFaint); break; }
                    var who = string.Join(", ", c.Bots.Select(b => _app.Bots[_bakeOrder[b]].Name));
                    r.Text(r.Fonts.Get(FontKind.MonoBold, 12), (c.Prefix.Length > 0 ? c.Prefix : "") + c.Command, new Vector2(cx, y + 3), Theme.Berry);
                    r.Text(r.Fonts.Get(FontKind.Sans, 10), "in " + who, new Vector2(cx + 120, y + 4), Theme.TextFaint);
                    float bx = cx; float by = y + 20;
                    // one "keep" button per source bot, then run/rename/combine
                    foreach (var b in c.Bots)
                    {
                        bool on = c.Resolution == BotMerge.Mode.Keep && c.KeepBot == b;
                        string name = _app.Bots[_bakeOrder[b]].Name;
                        bx = ResBtn(r, $"bk.{c.Key}.k{b}", ref by, bx, "keep " + Trunc(name, 8), on, () => { c.Resolution = BotMerge.Mode.Keep; c.KeepBot = b; });
                    }
                    bx = ResBtn(r, $"bk.{c.Key}.all", ref by, bx, "run both", c.Resolution == BotMerge.Mode.RunAll, () => c.Resolution = BotMerge.Mode.RunAll);
                    bx = ResBtn(r, $"bk.{c.Key}.ren", ref by, bx, "keep both", c.Resolution == BotMerge.Mode.Rename, () => c.Resolution = BotMerge.Mode.Rename);
                    bx = ResBtn(r, $"bk.{c.Key}.cmb", ref by, bx, "combine", c.Resolution == BotMerge.Mode.Combine, () => c.Resolution = BotMerge.Mode.Combine);
                    y = by + 34;
                }
            }

            // ---- connection + name ----
            Label("CONNECTION FROM", y); y += 16;
            float chx = cx;
            for (int oi = 0; oi < _bakeOrder.Count; oi++)
            {
                bool on = _bakeConn == oi;
                chx = ResBtn(r, $"bk.conn{oi}", ref y, chx, Trunc(_app.Bots[_bakeOrder[oi]].Name, 12), on, () => _bakeConn = oi, asRow: false);
            }
            y += 34;
            Label("NEW BOT NAME", y); y += 16;
            _bakeName = _ui.TextField("bk.name", new RectF(cx, y, cw - 170, 28), _bakeName, "Merged Bot");
        }

        // ---- footer ----
        float bh = 36, by2 = panel.Bottom - bh - 10;
        bool ready = _bakeOrder.Count >= 2;
        if (_ui.Button("bk.bake", new RectF(panel.Right - pad - 160, by2, 160, bh), "🧁  Bake!", Theme.Berry, primary: true, enabled: ready))
            StartBake(clock);
        if (_ui.Button("bk.close", new RectF(cx, by2, 90, bh), "CLOSE", Theme.Idle)) _bakeryOpen = false;
        if (!ready) r.Text(sans, "Select at least two bots.", new Vector2(cx + 104, by2 + 11), Theme.TextFaint);

        if (In.LeftPressed && !panel.Contains(In.Mouse) && !_bakeryJustOpened) _bakeryOpen = false;
        _bakeryJustOpened = false;
        r.End();
    }

    // a small pill toggle button; advances bx and returns it (wraps in DrawBakeryModal's layout space)
    private float ResBtn(Renderer r, string id, ref float by, float bx, string label, bool on, Action click, bool asRow = true)
    {
        var f = r.Fonts.Get(FontKind.SansBold, 11);
        float w = f.MeasureString(label).X + 22, h = 24;
        if (_ui.Button(id, new RectF(bx, by, w, h), label, on ? Theme.Berry : Theme.Idle, primary: on)) click();
        return bx + w + 6;
    }

    private void StartBake(Clock clock)
    {
        var graphs = _bakeOrder.Select(i => _app.Bots[i].Graph).ToList();
        var merged = BotMerge.Merge(graphs, _bakeConf);
        var servers = _app.Bots[_bakeOrder[Math.Clamp(_bakeConn, 0, _bakeOrder.Count - 1)]].Servers;
        string name = _bakeName.Trim().Length > 0 ? _bakeName.Trim() : "Merged Bot";
        _bakeAnimName = name;
        _bakeFinish = () =>
        {
            _app.AddBotFrom(name, merged, servers);
            _editor.FocusContent(_l.Canvas);
            Notify($"🧁 {name} is out of the oven - {merged.Nodes.Count} nodes");
        };
        _bakeryOpen = false;
        _bakeStart = clock.Time;
    }

    private bool BakeAnimActive => _bakeStart >= 0f;

    // ---- the bake animation: an oven + an orbiting clock for ~2s, then the bot pops out, DS-cute ----
    private const float BakeBake = 2.0f;   // oven + clock
    private const float BakePop = 0.95f;   // bot rises out
    private const float BakeHold = 1.1f;   // dwell so the ripple is enjoyed
    private const float BakeTotal = BakeBake + BakePop + BakeHold;

    private void DrawBakeAnim(Renderer r, Clock clock)
    {
        if (_bakeStart < 0f) return;
        float t = clock.Time - _bakeStart;
        if (t >= BakeTotal) { var f = _bakeFinish; _bakeStart = -1f; _bakeFinish = null; f?.Invoke(); return; }

        float fade = t < BakeTotal - 0.4f ? 1f : 1f - (t - (BakeTotal - 0.4f)) / 0.4f;
        float a = Clamp01(fade);
        float cx = _vw / 2f, cy = _vh / 2f - 10f;
        var center = new Vector2(cx, cy);
        float breath = 0.5f + 0.5f * MathF.Sin(t * 5f);

        // cover + warm bloom
        r.Begin(BlendMode.Alpha);
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Theme.Void, a));
        r.End();
        r.Begin(BlendMode.Add);
        r.Glow(center, 260f, Theme.WithAlpha(Theme.AmberBright, (0.10f + 0.05f * breath) * a));
        r.End();

        // bot emergence progress
        float popT = Clamp01((t - BakeBake) / BakePop);

        // ---- the oven: rumbles while baking, then zooms + fades out (and stops shaking) as the bot appears ----
        float ovenScale = 1f + 0.5f * popT;
        float ovenA = a * Clamp01(1f - popT * 1.5f);                                       // faded out by popT ~0.67
        float rumble = (0.55f + 0.45f * MathF.Sin(t * 3f)) * Clamp01(1f - popT * 3f);      // shaking stops fast
        var ovenShake = new Vector2(MathF.Sin(t * 26f) + 0.4f * MathF.Sin(t * 38f), MathF.Sin(t * 31f + 1f)) * 2.6f * rumble;
        var oc = new Vector2(cx, cy);
        RectF Z(RectF rr) => new RectF(oc.X + (rr.X - oc.X) * ovenScale, oc.Y + (rr.Y - oc.Y) * ovenScale, rr.W * ovenScale, rr.H * ovenScale);
        Vector2 Zp(Vector2 pp) => oc + (pp - oc) * ovenScale;
        float Zr(float rad) => rad * ovenScale;

        if (ovenA > 0.003f)
        {
            r.Begin(BlendMode.Alpha);
            var body = new RectF(cx - 92 + ovenShake.X, cy - 64 + ovenShake.Y, 184, 150);
            r.RoundFill(Z(new RectF(body.X, body.Y + 4, body.W, body.H)), Theme.WithAlpha(Color.Black, 0.10f * ovenA), Zr(16f));  // shadow
            r.RoundFill(Z(body), Theme.WithAlpha(Theme.Mix(Theme.PanelHi, Theme.Amber, 0.18f), ovenA), Zr(16f));
            r.RoundOutline(Z(body), Theme.WithAlpha(Theme.Edge, ovenA), Zr(16f));
            var strip = new RectF(body.X + 12, body.Y + 12, body.W - 24, 22);
            r.RoundFill(Z(strip), Theme.WithAlpha(Theme.PanelLo, ovenA), Zr(8f));
            for (int i = 0; i < 3; i++)
            {
                var kp = Zp(new Vector2(strip.X + 16 + i * 24, strip.Center.Y));
                r.Disc(kp, Zr(6f), Theme.WithAlpha(Theme.Panel, ovenA));
                r.Disc(kp, Zr(6f), Theme.WithAlpha(Theme.Edge, ovenA * 0.5f));
                float kn = t * (2.2f + i);
                r.Line(kp, kp + new Vector2(MathF.Cos(kn), MathF.Sin(kn)) * Zr(4.5f), Theme.WithAlpha(Theme.TextDim, ovenA), 1.6f);
            }
            var door = new RectF(body.X + 14, body.Y + 44, body.W - 28, body.H - 58);
            r.RoundFill(Z(door), Theme.WithAlpha(Theme.Mix(Theme.PanelLo, Theme.Edge, 0.3f), ovenA), Zr(12f));
            var win = new RectF(door.X + 14, door.Y + 12, door.W - 28, door.H - 34);
            r.RoundFill(Z(win), Theme.WithAlpha(Theme.Mix(Theme.Amber, Theme.AmberBright, breath), 0.85f * ovenA), Zr(10f));
            r.RoundOutline(Z(win), Theme.WithAlpha(Theme.AmberDim, ovenA), Zr(10f));
            r.RoundFill(Z(new RectF(door.X + 10, door.Bottom - 14, door.W - 20, 6)), Theme.WithAlpha(Theme.Edge, ovenA), Zr(3f));
            r.End();
            r.Begin(BlendMode.Add);
            r.Glow(Zp(win.Center), Zr(70f), Theme.WithAlpha(Theme.AmberBright, (0.25f + 0.18f * breath) * ovenA));
            r.End();
        }

        // ---- a clock orbiting the oven; it pops like a bubble the moment the bot appears ----
        float clockPop = Clamp01(popT / 0.16f);             // 0..1 over the first ~0.15s of the pop
        float clockA = a * (1f - clockPop);
        if (clockA > 0.003f)
        {
            float orbit = t * 1.7f;
            var clockPos = center + new Vector2(MathF.Cos(orbit), MathF.Sin(orbit) * 0.62f) * 122f + new Vector2(0, -16);
            float cs = 1f + 0.9f * (1f - (1f - clockPop) * (1f - clockPop));   // balloon out as it bursts
            float cr = 22f * cs;
            r.Begin(BlendMode.Add);
            r.Glow(clockPos, 30f * cs, Theme.WithAlpha(Theme.CyanBright, 0.35f * clockA));
            r.End();
            r.Begin(BlendMode.Alpha);
            r.Disc(clockPos, cr, Theme.WithAlpha(Theme.PanelHi, clockA));
            r.Ring(clockPos, cr, Theme.WithAlpha(Theme.CyanDim, clockA));
            for (int i = 0; i < 12; i++) { float ang = i * MathF.PI / 6f; r.Disc(clockPos + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * (cr - 4f * cs), 1.1f * cs, Theme.WithAlpha(Theme.TextDim, clockA)); }
            r.Line(clockPos, clockPos + new Vector2(MathF.Cos(t * 3.2f - MathF.PI / 2), MathF.Sin(t * 3.2f - MathF.PI / 2)) * (cr - 8f * cs), Theme.WithAlpha(Theme.Text, clockA), 2.2f);
            r.Line(clockPos, clockPos + new Vector2(MathF.Cos(t * 0.8f - MathF.PI / 2), MathF.Sin(t * 0.8f - MathF.PI / 2)) * (cr - 12f * cs), Theme.WithAlpha(Theme.Text, clockA), 2.6f);
            r.Disc(clockPos, 2.2f, Theme.WithAlpha(Theme.Berry, clockA));
            // bubble film + droplets flicking off as it bursts
            if (clockPop > 0f)
            {
                r.Ring(clockPos, cr + 7f * clockPop, Theme.WithAlpha(Theme.CyanBright, clockA * 0.7f));
                for (int i = 0; i < 6; i++)
                {
                    float ang = i * (MathF.PI * 2f / 6f) + orbit;
                    var dp = clockPos + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * (cr + 8f + 20f * clockPop);
                    r.Disc(dp, 2.2f * (1f - clockPop), Theme.WithAlpha(Theme.CyanBright, clockA));
                }
            }
            r.End();
        }

        // ---- the bot hatches out of the oven in a lavish golden ripple (Pokemon-egg-hatch energy) ----
        if (popT > 0f)
        {
            float pop = EaseOutBack(popT);
            float rise = (1f - popT) * 28f;
            var bp = new Vector2(cx, cy + 20f - 6f - 72f * pop + rise);   // emerges from the window, rises up

            float botR = 52f;   // the rings stay OUTSIDE this, so they ring the bot instead of covering it
            r.Begin(BlendMode.Add);
            // a soft golden backlight behind the bot (gentle, doesn't wash it out)
            r.Glow(bp, 150f * pop, Theme.WithAlpha(Theme.AmberBright, (0.12f + 0.05f * MathF.Sin(t * 5f)) * a));
            // the hatch FLASH - a quick bright bloom the instant it emerges, then gone
            float flash = MathF.Max(0f, 1f - popT * 2.6f);
            r.Glow(bp, 190f, Theme.WithAlpha(Color.White, 0.42f * flash * a));
            // the RIPPLE: slow, graceful expanding shockwave rings - round, golden, fluid, with colour accents.
            // each ring is born at the bot's edge and drifts outward.
            var rip = new[] { Theme.AmberBright, Theme.Amber, Color.White, Theme.CyanBright, Theme.Magenta };
            for (int i = 0; i < rip.Length; i++)
            {
                float ph = ((t * 0.42f) + i / (float)rip.Length) % 1f;   // ~2.4s per ring - slow + fluid
                float rad = botR + ph * 175f;                            // starts at the bot's edge, never over it
                float ease = 1f - ph;
                float ra = ease * ease * 0.95f * a * pop;
                var col = rip[i];
                r.Ring(bp, rad, Theme.WithAlpha(col, ra));
                r.Ring(bp, rad + 2.5f, Theme.WithAlpha(col, ra * 0.6f));
                r.Ring(bp, rad - 2.5f, Theme.WithAlpha(col, ra * 0.45f));
            }
            // a slow rotating glint star - shiny white-gold rays that start beyond the bot
            float gl = 118f * pop * (0.9f + 0.1f * MathF.Sin(t * 4f));
            for (int k = 0; k < 4; k++)
            {
                float ang = t * 0.5f + k * (MathF.PI / 2f);
                var d = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
                var a0 = bp + d * (botR + 6f); var a1 = bp + d * gl;       // ray from the bot's edge outward
                r.Line(a0, a1, Theme.WithAlpha(Theme.AmberBright, 0.16f * a * pop), 5.5f);
                r.Line(a0, a1, Theme.WithAlpha(Color.White, 0.20f * a * pop), 2.2f);
                r.Line(bp - d * gl, bp - d * (botR + 6f), Theme.WithAlpha(Color.White, 0.20f * a * pop), 2.2f);
            }
            // golden sparkle motes drifting outward on the ripple
            for (int i = 0; i < 11; i++)
            {
                float ang = -t * 0.9f + i * (MathF.PI * 2f / 11f);
                float orad = (botR + 18f + 26f * MathF.Sin(t * 1.6f + i)) * pop;
                var sp = bp + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * orad;
                float tw = 0.5f + 0.5f * MathF.Sin(t * 5f + i * 1.6f);
                r.Disc(sp, 2.6f * tw, Theme.WithAlpha(i % 3 == 0 ? Theme.CyanBright : Theme.AmberBright, 0.9f * a * pop * tw));
            }
            r.End();

            // the bot itself, riding up out of the light
            r.Begin(BlendMode.Alpha);
            float bob = MathF.Sin(t * 6f) * 2.5f * popT;
            float logo = 98f * pop;
            if (r.Brand != null)
            {
                float sc = logo / r.Brand.Width;
                r.Sb.Draw(r.Brand, bp + new Vector2(0, bob), null, Color.White * a, 0f,
                    new Vector2(r.Brand.Width / 2f, r.Brand.Height / 2f), new Vector2(sc), SpriteEffects.None, 0f);
            }
            else r.Disc(bp, logo * 0.4f, Theme.WithAlpha(Theme.Cyan, a));
            r.End();
        }

        // ---- caption ----
        r.Begin(BlendMode.Alpha);
        string cap = popT > 0.5f ? $"{_bakeAnimName} is ready!" : "Baking your bot…";
        r.TextCenteredX(r.Fonts.Get(FontKind.Display, 30), cap, cx, cy + 104f, Theme.WithAlpha(Theme.Text, a));
        // progress bar
        float barW = 220f, barH = 5f;
        var track = new RectF(cx - barW / 2f, cy + 150f, barW, barH);
        r.RoundFill(track, Theme.WithAlpha(Theme.PanelLo, a * 0.9f), barH / 2f);
        r.RoundFill(new RectF(track.X, track.Y, barW * Clamp01(t / BakeTotal), barH), Theme.WithAlpha(Theme.Berry, a), barH / 2f);
        r.End();
    }

    private static float Clamp01(float x) => x < 0 ? 0 : x > 1 ? 1 : x;
    private static float EaseOutBack(float x)
    {
        const float c1 = 1.70158f, c3 = c1 + 1f;
        float xm = x - 1f;
        return 1f + c3 * xm * xm * xm + c1 * xm * xm;
    }
}
