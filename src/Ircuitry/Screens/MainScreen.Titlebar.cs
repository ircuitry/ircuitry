using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Ircuitry.Core;
using Ircuitry.Render;

namespace Ircuitry.Screens;

// The custom client-side title bar: a single glossy DS-style bar holding the app icon, the bot tabs (cute
// game-cartridge tabs in a scrollable gutter), and an icon-only action cluster on the right
// (play/stop, test, history, bell, file, more) followed by the window controls. The OS still does the real
// dragging/resizing via SDL's hit-test; we publish which rects must stay clickable.
public sealed partial class MainScreen
{
    public IntPtr WindowHandle;
    private float _tabScroll;
    private RectF _testBtnRect;   // where the Test icon landed this frame (for the tutorial highlight)

    // Title-bar palette: a soft, glossy pastel teal (the brand colour, gentled so it isn't "too powerful").
    // Paired with a neutral cream brand mark (below) so nothing clashes.
    private static readonly Color BarTop = Theme.Mix(Theme.CyanBright, Color.White, 0.52f);  // glossy highlight band
    private static readonly Color BarBot = Theme.Mix(Theme.Cyan, Color.White, 0.10f);        // soft bottom
    private static readonly Color BarDim = Theme.Mix(Theme.Cyan, Theme.Text, 0.32f);         // shadows / lips
    private static readonly Color BarPad = Theme.Mix(Theme.PanelHi, Theme.Cyan, 0.12f);      // resting key-pad fill
    private static readonly Color BarAccent = Theme.Cyan;

    private void DrawTitlebar(Renderer r, Clock clock)
    {
        var bar = _l.Titlebar;
        r.Begin();
        GlossyBar(r, bar);

        var noDrag = new List<float>();
        void NoDrag(RectF q) { noDrag.Add(q.X); noDrag.Add(q.Y); noDrag.Add(q.W); noDrag.Add(q.H); }

        // cute cream chat-bubble brand mark (neutral, so it never clashes with the bar colour)
        float x = DrawBrandMark(r, bar) + 10;

        // right cluster, laid out from the right edge inward
        float rx = bar.Right;
        if (Sdl.CustomChrome && WindowHandle != IntPtr.Zero) rx = DrawWindowControls(r, bar, NoDrag);

        float bh = 32, by = bar.Y + (bar.H - bh) / 2f;
        RectF Btn(float w, float gapBefore = 0) { rx -= gapBefore; var rr = new RectF(rx - w, by, w, bh); rx -= w; NoDrag(rr); return rr; }

        var moreR = Btn(38, 6);
        bool moreClick = IconPad(r, moreR, out var morePad);   // "⋯" doesn't render in this font - draw a hamburger
        for (int i = -1; i <= 1; i++) r.HLine(morePad.Center.X - 7, morePad.Center.X + 7, MathF.Round(morePad.Center.Y + i * 5), Theme.Text, 1.8f);
        if (moreClick) OpenMoreMenu(new Vector2(moreR.X - 80, moreR.Bottom + 3));
        var fileR = Btn(38);
        if (IconBtn(r, fileR, "📁", 16, _app.Dirty ? Theme.Amber : (Color?)null)) OpenFileMenu(new Vector2(fileR.X - 60, fileR.Bottom + 3));

        var bellR = Btn(38, 14);
        if (IconBtn(r, bellR, "🔔", 16)) { _notifOpen = !_notifOpen; _notifJustOpened = true; _notifUnread = 0; _notifScroll = 0; }
        if (_notifUnread > 0)
        {
            var c = new Vector2(bellR.Right - 10, bellR.Y + 8);
            r.Disc(c, 7f, Theme.Alert);
            var nf = r.Fonts.Get(FontKind.SansBold, 9);
            string n = _notifUnread > 9 ? "9+" : _notifUnread.ToString();
            r.Text(nf, n, new Vector2(c.X - nf.MeasureString(n).X / 2f, c.Y - nf.MeasureString(n).Y / 2f), Theme.TextInk);
        }

        var histR = Btn(38, 14);
        if (IconBtn(r, histR, "📜", 16)) OpenHistory();
        var testR = Btn(38);
        _testBtnRect = testR;   // the tutorial highlights this
        if (IconBtn(r, testR, "🧪", 16)) { _testOpen = true; _testJustOpened = true; RunTest(); }
        var playR = Btn(50, 4);
        DrawPlayStop(r, playR);
        NoDrag(playR);
        r.End();

        // tabs fill the gutter between the icon and the action cluster (manages its own scissor batches)
        DrawCartridgeTabs(r, bar, new RectF(x, bar.Y, rx - 12 - x, bar.H), clock, NoDrag);

        // right-click the icon or any empty (draggable) part of the bar -> the window menu, like a real title bar
        if (!Modal && In.RightPressed && bar.Contains(In.Mouse))
        {
            bool overBtn = false;
            for (int i = 0; i + 3 < noDrag.Count; i += 4)
                if (In.Mouse.X >= noDrag[i] && In.Mouse.X < noDrag[i] + noDrag[i + 2] &&
                    In.Mouse.Y >= noDrag[i + 1] && In.Mouse.Y < noDrag[i + 1] + noDrag[i + 3]) { overBtn = true; break; }
            if (!overBtn) OpenWindowMenu(In.Mouse);
        }

        Sdl.PublishTitlebar((int)bar.Bottom, _vw, _vh, Sdl.IsMaximized(WindowHandle), noDrag.ToArray());
    }

    // The title-bar window menu. SDL doesn't surface the OS's native window menu on a borderless window, so we
    // present our own - with the standard actions plus two genuinely-useful extras (always-on-top, screenshot).
    public bool ScreenshotRequested;   // host captures the frame after the menu closes
    public string ScreenshotPath = "";

    private void OpenWindowMenu(Vector2 anchor)
    {
        _ctxAnchor = anchor;
        _ctxItems.Clear();
        bool maxed = Sdl.IsMaximized(WindowHandle);
        _ctxItems.Add(new CtxItem { Icon = "—", Label = "Minimize", Shortcut = "", Enabled = true, Do = () => Sdl.Minimize(WindowHandle) });
        _ctxItems.Add(new CtxItem { Icon = maxed ? "❐" : "□", Label = maxed ? "Restore" : "Maximize", Shortcut = "", Enabled = true, Do = () => Sdl.ToggleMaximize(WindowHandle) });
        _ctxItems.Add(new CtxItem { Icon = Sdl.AlwaysOnTop ? "✔" : "▢", Label = "Always on top", Shortcut = "", Enabled = true, Do = () => Sdl.ToggleAlwaysOnTop(WindowHandle) });
        _ctxItems.Add(new CtxItem { Sep = true });
        _ctxItems.Add(new CtxItem { Icon = "📸", Label = "Screenshot this window", Shortcut = "", Enabled = true, Do = RequestScreenshot });
        _ctxItems.Add(new CtxItem { Sep = true });
        _ctxItems.Add(new CtxItem { Icon = "×", Label = "Close", Shortcut = "", Enabled = true, Do = () => Sdl.CloseRequested = true });
        _ctxOpen = true; _ctxJustOpened = true;
    }

    public void NotifyExternal(string msg) => Notify(msg);   // host -> toast (used after the deferred screenshot)

    private void RequestScreenshot()
    {
        string dir = System.IO.Path.Combine(Ircuitry.App.AppModel.WorkspaceDir, "shots");
        ScreenshotPath = System.IO.Path.Combine(dir, $"ircuitry-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        ScreenshotRequested = true;   // the host saves it once the menu + this toast are off-screen, then notifies
    }

    // The app logo (assets/icons/icon-256.png), drawn at the left of the title bar. Returns its right edge.
    private float DrawBrandMark(Renderer r, RectF bar)
    {
        float s = 36, y = (bar.H - s) / 2f;
        var ic = new RectF(10, y, s, s);
        if (r.Brand != null) r.Image(r.Brand, ic);
        return ic.Right;
    }

    // A glossy, slightly-domed coloured bar (think DS plastic): vertical gradient + a bright sheen up top and
    // a soft shadow lip at the bottom so it reads as raised on the z axis.
    private void GlossyBar(Renderer r, RectF bar)
    {
        const int bands = 16;
        for (int i = 0; i < bands; i++)
        {
            float t = i / (float)bands;
            r.Fill(new RectF(bar.X, bar.Y + bar.H * t, bar.W, bar.H / bands + 1f), Theme.Mix(BarTop, BarBot, t));
        }
        // glossy sheen across the top ~44%
        r.Fill(new RectF(bar.X, bar.Y, bar.W, bar.H * 0.44f), Theme.WithAlpha(Color.White, 0.22f));
        r.HLine(bar.X, bar.Right, bar.Y + 1f, Theme.WithAlpha(Color.White, 0.55f), 1.2f);          // top highlight
        r.HLine(bar.X, bar.Right, bar.Bottom - 1f, Theme.WithAlpha(BarDim, 0.55f), 1.4f);          // bottom lip
        r.HLine(bar.X, bar.Right, bar.Bottom + 1f, Theme.WithAlpha(BarAccent, 0.30f), 2.5f);       // soft glow below
    }

    // A glossy DS "key" pad. Returns whether it was clicked; hands back the inner pad rect to draw onto.
    private bool IconPad(Renderer r, RectF rect, out RectF pad)
    {
        bool hot = !Modal && rect.Contains(In.Mouse);
        pad = rect.Inflate(-3, -1);
        r.RoundFill(pad.Offset(0, 1), Theme.WithAlpha(BarDim, 0.35f), 9f);                  // tiny drop shadow
        r.RoundFill(pad, hot ? Theme.PanelHi : BarPad, 9f);
        r.RoundFill(new RectF(pad.X + 2, pad.Y + 2, pad.W - 4, pad.H * 0.42f), Theme.WithAlpha(Color.White, 0.5f), 6f);  // gloss
        return hot && In.LeftPressed && !_ircWinJustOpened;
    }

    // An icon-only title-bar button: a key pad with a (monochrome) glyph. Emoji read as clean line-art here.
    private bool IconBtn(Renderer r, RectF rect, string glyph, int size, Color? tint = null)
    {
        bool c = IconPad(r, rect, out var pad);
        r.TextCentered(r.Fonts.Get(FontKind.Sans, size), glyph, pad, tint ?? Theme.Text);
        return c;
    }

    // The primary run control: a matte, pastel ▶ (start, soft green) / ■ (stop, soft red) button - the same
    // flat, chunky look as the inspector's RUN BOT, not a glossy candy pill.
    private void DrawPlayStop(Renderer r, RectF rect)
    {
        bool running = Bot.Runtime.Running;
        bool hot = !Modal && rect.Contains(In.Mouse);
        Color baseCol = running ? Theme.Mix(Theme.Alert, Color.White, 0.10f) : Theme.Mix(Theme.Ok, Color.White, 0.28f);
        Color fill = hot ? Theme.Mix(baseCol, Color.White, 0.12f) : baseCol;
        Color glyph = running ? Theme.Mix(Theme.Alert, Theme.Text, 0.30f) : Theme.Mix(Theme.Ok, Theme.Text, 0.42f);
        r.RoundFill(new RectF(rect.X, rect.Y + 2f, rect.W, rect.H), Theme.WithAlpha(Color.Black, 0.07f), 10f);  // soft shadow
        r.RoundFill(rect, fill, 10f);                                                                            // matte fill (no gloss)
        r.RoundOutline(rect, Theme.WithAlpha(glyph, hot ? 0.9f : 0.6f), 10f);
        var c = rect.Center;
        if (running) r.RoundFill(new RectF(c.X - 5.5f, c.Y - 5.5f, 11, 11), glyph, 2f);                          // stop square
        else for (int i = 0; i <= 11; i++)                                                                       // play triangle
            { float t = i / 11f, hh = 6.5f * (1 - t); r.VLine(c.X - 5 + i, c.Y - hh, c.Y + hh, glyph, 1.3f); }
        if (hot && In.LeftPressed && !_ircWinJustOpened) ToggleRun();
    }

    private float DrawWindowControls(Renderer r, RectF bar, Action<RectF> noDrag)
    {
        float w = 44, h = bar.H, cx = bar.Right;

        var closeR = new RectF(cx - w, 0, w, h);
        bool ch = closeR.Contains(In.Mouse);
        if (ch) r.Fill(closeR, Theme.Alert);
        DrawGlyphX(r, closeR.Center, 4.5f, ch ? Theme.TextInk : Theme.WithAlpha(Theme.Text, 0.8f));
        noDrag(closeR); if (In.LeftPressed && ch) Sdl.CloseRequested = true; cx -= w;

        var maxR = new RectF(cx - w, 0, w, h);
        bool mh = maxR.Contains(In.Mouse);
        if (mh) r.Fill(maxR, Theme.WithAlpha(Color.White, 0.30f));
        var mc = Theme.WithAlpha(Theme.Text, mh ? 1f : 0.8f);
        if (Sdl.IsMaximized(WindowHandle))
        {
            r.RectOutline(new RectF(maxR.Center.X - 3, maxR.Center.Y - 5, 8, 8), mc, 1.4f);
            r.RectOutline(new RectF(maxR.Center.X - 6, maxR.Center.Y - 2, 8, 8), mc, 1.4f);
        }
        else r.RectOutline(new RectF(maxR.Center.X - 5, maxR.Center.Y - 5, 10, 10), mc, 1.4f);
        noDrag(maxR); if (In.LeftPressed && mh) Sdl.ToggleMaximize(WindowHandle); cx -= w;

        var minR = new RectF(cx - w, 0, w, h);
        bool nh = minR.Contains(In.Mouse);
        if (nh) r.Fill(minR, Theme.WithAlpha(Color.White, 0.30f));
        r.HLine(minR.Center.X - 6, minR.Center.X + 6, minR.Center.Y + 4, Theme.WithAlpha(Theme.Text, nh ? 1f : 0.8f), 1.7f);
        noDrag(minR); if (In.LeftPressed && nh) Sdl.Minimize(WindowHandle); cx -= w;

        return cx;
    }

    private static void DrawGlyphX(Renderer r, Vector2 c, float s, Color col)
    {
        r.Line(new Vector2(c.X - s, c.Y - s), new Vector2(c.X + s, c.Y + s), col, 1.7f);
        r.Line(new Vector2(c.X - s, c.Y + s), new Vector2(c.X + s, c.Y - s), col, 1.7f);
    }

    // DS game-cartridge tabs in a scrollable gutter. Each is a chunky rounded cartridge with a colour "label"
    // strip + status dot; the active one is a cream cartridge that pops off the glossy bar. A "▸" button (and
    // mouse wheel) scrolls when the tabs overflow.
    private void DrawCartridgeTabs(Renderer r, RectF bar, RectF gutter, Clock clock, Action<RectF> noDrag)
    {
        var tf = r.Fonts.Get(FontKind.Display, 16);
        float top = bar.Y + 7, tabH = bar.H - 12, pad = 7f;

        // measure total width
        var widths = new float[_app.Bots.Count];
        float total = 0;
        for (int i = 0; i < _app.Bots.Count; i++)
        {
            var b = _app.Bots[i];
            widths[i] = _renamingBot == b ? 210 : Math.Min(196, tf.MeasureString(b.Name).X + 58);
            total += widths[i] + pad;
        }
        total += 36;   // the + button

        bool overflow = total > gutter.W;
        float scrollBtnW = overflow ? 26 : 0;
        float viewW = gutter.W - scrollBtnW;
        float maxScroll = MathF.Max(0, total - viewW);

        if (!Modal && gutter.Contains(In.Mouse) && In.ScrollDelta != 0) _tabScroll -= In.ScrollDelta * 0.6f;
        _tabScroll = Math.Clamp(_tabScroll, 0, maxScroll);

        r.Begin(BlendMode.Alpha, new RectF(gutter.X, gutter.Y, viewW, gutter.H).ToRectangle());
        float x = gutter.X - _tabScroll;
        for (int i = 0; i < _app.Bots.Count; i++)
        {
            float w = widths[i];
            var slot = new RectF(x, top, w, tabH);
            if (slot.Right >= gutter.X - 2 && slot.X <= gutter.X + viewW + 2)
                DrawOneTab(r, i, slot, tf, clock, gutter, viewW, noDrag);
            x += w + pad;
        }
        // + add a bot
        var addR = new RectF(x, top + 1, 32, tabH - 2);
        if (addR.X <= gutter.X + viewW)
        {
            bool ah = !Modal && addR.Contains(In.Mouse);
            r.RoundFill(addR, ah ? Theme.WithAlpha(Color.White, 0.5f) : Theme.WithAlpha(Color.White, 0.28f), 8f);
            r.TextCentered(r.Fonts.Get(FontKind.SansBold, 18), "+", addR, Theme.Mix(Theme.Cyan, Theme.Text, 0.4f));
            var addClip = addR.Intersect(new RectF(gutter.X, gutter.Y, viewW, gutter.H));
            if (addClip.W > 4) noDrag(addClip);
            if (ah && In.LeftPressed) { _templateOpen = true; _templateJustOpened = true; }
        }
        r.End();

        // "▸" scroll button to page the gutter right (wraps back to start at the end)
        if (overflow)
        {
            r.Begin();
            var sr = new RectF(gutter.Right - scrollBtnW, top + 2, scrollBtnW - 2, tabH - 4);
            bool sh = !Modal && sr.Contains(In.Mouse);
            r.RoundFill(sr, sh ? Theme.WithAlpha(Color.White, 0.5f) : Theme.WithAlpha(Color.White, 0.3f), 7f);
            r.TextCentered(r.Fonts.Get(FontKind.SansBold, 15), "▸", sr, Theme.Mix(Theme.Cyan, Theme.Text, 0.4f));
            noDrag(sr);
            if (sh && In.LeftPressed) _tabScroll = _tabScroll >= maxScroll - 1 ? 0 : Math.Min(maxScroll, _tabScroll + viewW * 0.8f);
            r.End();
        }
    }

    private void DrawOneTab(Renderer r, int i, RectF slot, FontStashSharp.DynamicSpriteFont tf, Clock clock, RectF gutter, float viewW, Action<RectF> noDrag)
    {
        var bot = _app.Bots[i];
        bool active = i == _app.Active;
        bool renaming = _renamingBot == bot;
        var col = StatusColor(bot.Runtime);

        var tab = active ? new RectF(slot.X, slot.Y - 2, slot.W, slot.H + 2) : new RectF(slot.X, slot.Y + 2, slot.W, slot.H - 2);
        var clip = tab.Intersect(new RectF(gutter.X, gutter.Y, viewW, gutter.H));
        if (clip.W > 4) noDrag(clip);

        r.RoundFill(tab.Offset(0, 1), Theme.WithAlpha(BarDim, 0.35f), 9f);   // drop shadow so it reads on the bar
        if (active)
        {
            r.RoundFill(tab.Inflate(1.5f, 1.5f), Theme.WithAlpha(col, 0.30f), 11f);
            r.RoundFill(tab, Theme.PanelHi, 9f);
            r.RoundOutline(tab, Theme.WithAlpha(col, 0.9f), 9f);
        }
        else
        {
            r.RoundFill(tab, Theme.PanelLo, 9f);                    // solid (not see-through), dimmer than active
            r.RoundOutline(tab, Theme.Edge, 9f);
        }
        r.RoundFill(new RectF(tab.X + 6, tab.Y + 4, tab.W - 12, 4), Theme.WithAlpha(col, active ? 0.95f : 0.7f), 2f);
        if (active) Hud.SoftDot(r, new Vector2(tab.X + 16, tab.Center.Y + 3), 3.4f, col);
        else r.Disc(new Vector2(tab.X + 16, tab.Center.Y + 3), 3f, col);

        if (renaming)
        {
            var nm = _ui.TextField("tab.rename", new RectF(tab.X + 26, tab.Center.Y - 9, tab.W - 36, 22), bot.Name, "bot name");
            if (nm != bot.Name) { bot.Name = string.IsNullOrWhiteSpace(nm) ? bot.Name : nm; _app.MarkDirty(); }
            if (_ui.Focus != "tab.rename") _renamingBot = null;
            return;
        }

        Color txt = active ? Theme.Text : Theme.TextDim;
        r.Text(tf, r.Ellipsize(tf, bot.Name, slot.W - 52), new Vector2(tab.X + 28, tab.Center.Y - tf.MeasureString(bot.Name).Y / 2f + 3), txt);

        bool canClose = _app.Bots.Count > 1;
        var xc = new Vector2(tab.Right - 14, tab.Center.Y + 3);
        var xhit = new RectF(xc.X - 10, tab.Y, 20, tab.H);
        bool xHover = canClose && xhit.Contains(In.Mouse);
        if (canClose)
        {
            if (xHover) r.Disc(xc, 9f, Theme.WithAlpha(Theme.Alert, 0.25f));
            DrawGlyphX(r, xc, 3.4f, xHover ? Theme.Alert : active ? Theme.TextDim : Theme.TextFaint);
        }

        if (!Modal && In.LeftPressed && tab.Contains(In.Mouse) && gutter.Contains(In.Mouse))
        {
            if (xHover) { _confirmDeleteBot = bot; _confirmJustOpened = true; }
            else
            {
                bool dbl = _tabClickBot == bot && clock.Time - _tabClickTime < 0.35f;
                _tabClickBot = bot; _tabClickTime = clock.Time;
                if (!active) { _app.SetActive(i); _editor.Selection.Clear(); }
                if (dbl) { _renamingBot = bot; _ui.Focus = "tab.rename"; }
            }
        }
    }

    // A floppy "apply" button that hangs in the canvas's top-right ONLY while the live bot has edits it hasn't
    // applied yet (keyed on the behaviour signature, not the autosaved disk-dirty flag - so it doesn't vanish
    // when you click away). Clicking it applies the edits to the running bot (and saves).
    private void DrawCanvasSave(Renderer r)
    {
        if (!Bot.Runtime.HasUnapplied(Bot.Graph)) return;
        var c = _l.Canvas;
        var rect = new RectF(c.Right - 54, c.Y + 14, 40, 40);
        bool hot = !Modal && rect.Contains(In.Mouse);
        var col = Theme.Cyan;
        r.Begin();
        r.RoundFill(rect.Offset(0, 2), Theme.WithAlpha(Color.Black, 0.10f), 11f);
        r.RoundFill(rect, hot ? Theme.Mix(col, Color.White, 0.14f) : col, 11f);
        r.RoundOutline(rect, Theme.WithAlpha(Theme.Mix(col, Theme.Text, 0.25f), hot ? 1f : 0.7f), 11f);
        r.TextCentered(r.Fonts.Get(FontKind.Sans, 19), "💾", rect, Theme.TextInk);
        if (hot)   // a tiny "apply to live bot" hint on hover
            r.Text(r.Fonts.Get(FontKind.SansBold, 11), "apply", new Vector2(rect.X - 4, rect.Bottom + 2), Theme.Mix(col, Theme.Text, 0.4f));
        r.End();
        if (hot && In.LeftPressed)
        {
            Bot.Runtime.ApplyGraph(Bot.Graph);
            _app.Save();
            Notify("↻ Applied changes to the live bot");
        }
    }
}
