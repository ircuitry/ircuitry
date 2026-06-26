using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Ircuitry.App;
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
    // these MUST be live (properties) so the bar re-themes with the active theme - as a static readonly they
    // froze at the default cozy cyan and the title bar never changed colour (and its glyphs went invisible).
    private static Color BarTop => Theme.Mix(Theme.CyanBright, Color.White, 0.52f);  // glossy highlight band
    private static Color BarBot => Theme.Mix(Theme.Cyan, Color.White, 0.10f);        // soft bottom
    private static Color BarDim => Theme.Mix(Theme.Cyan, Theme.Text, 0.32f);         // shadows / lips
    private static Color BarPad => Theme.Mix(Theme.PanelHi, Theme.Cyan, 0.12f);      // resting key-pad fill
    private static Color BarAccent => Theme.Cyan;

    private static float Lum(Color c) => (0.299f * c.R + 0.587f * c.G + 0.114f * c.B) / 255f;
    // the colour glyphs actually sit on: the sheened top of the gloss bar
    private static Color BarFace => Theme.Mix(BarTop, Color.White, 0.22f);
    // a glyph/ink colour guaranteed to contrast with the bar under ANY theme (derived from the bar, not from
    // the theme's text colours, which flip light/dark and don't track the bar's own lightness).
    private static Color BarInk => Lum(BarFace) > 0.5f ? Theme.Mix(BarFace, Color.Black, 0.72f) : Theme.Mix(BarFace, Color.White, 0.9f);
    // the key-pad surface tracks the (light) bar, not the theme's panel - so it stays light and BarInk reads on
    // it in every theme (panel-coloured pads went dark in dark themes and swallowed the icons).
    private static Color BarKey => Theme.Mix(BarTop, Color.White, 0.42f);
    private static Color BarKeyHot => Theme.Mix(BarTop, Color.White, 0.66f);

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
        bool moreClick = IconPad(r, moreR, out var morePad);   // a dots glyph doesn't render in this font - draw a hamburger
        for (int i = -1; i <= 1; i++) r.HLine(morePad.Center.X - 7, morePad.Center.X + 7, MathF.Round(morePad.Center.Y + i * 5), BarInk, 1.8f);
        if (moreClick) OpenMoreMenu(new Vector2(moreR.X - 80, moreR.Bottom + 3));
        var fileR = Btn(38);
        if (IconBtn(r, fileR, Ircuitry.Core.Icons.Glyph("folder"), 16, _app.Dirty ? Theme.Amber : (Color?)null)) OpenFileMenu(new Vector2(fileR.X - 60, fileR.Bottom + 3));

        var bellR = Btn(38, 14);
        if (IconBtn(r, bellR, Ircuitry.Core.Icons.Glyph("bell"), 16)) { _notifOpen = !_notifOpen; _notifJustOpened = true; _notifUnread = 0; _notifScroll = 0; }
        if (_notifUnread > 0)
        {
            var c = new Vector2(bellR.Right - 10, bellR.Y + 8);
            r.Disc(c, 7f, Theme.Alert);
            var nf = r.Fonts.Get(FontKind.SansBold, 9);
            string n = _notifUnread > 9 ? "9+" : _notifUnread.ToString();
            r.Text(nf, n, new Vector2(c.X - nf.MeasureString(Ircuitry.Render.Renderer.SafeText(n)).X / 2f, c.Y - nf.MeasureString(Ircuitry.Render.Renderer.SafeText(n)).Y / 2f), Theme.TextInk);
        }

        var fleetR = Btn(38, 14);   // fleet health board (every bot's live status); reddens when any local bot has errors
        if (IconBtn(r, fleetR, Ircuitry.Core.Icons.Glyph("heartbeat"), 16, _app.Bots.Any(b => !b.IsRemote && b.Runtime.ErrorCount > 0) ? Theme.Alert : (Color?)null)) { _fleetOpen = true; _fleetJustOpened = true; _fleetScroll = 0; }

        var netR = Btn(38, 14);   // app-global network map (every bot + server + channel)
        if (IconBtn(r, netR, Ircuitry.Core.Icons.Glyph("map-trifold"), 16)) { _networkOpen = true; _networkJustOpened = true; }
        var histR = Btn(38);
        if (IconBtn(r, histR, Ircuitry.Core.Icons.Glyph("scroll"), 16)) OpenHistory();
        var testR = Btn(38);
        _testBtnRect = testR;   // the tutorial highlights this
        if (IconBtn(r, testR, Ircuitry.Core.Icons.Glyph("test-tube"), 16)) { _testOpen = true; _testJustOpened = true; RunTest(); }
        var playR = Btn(50, 4);
        DrawPlayStop(r, playR);
        NoDrag(playR);
        foreach (var c in _plugins.Contributions("toolbar"))   // plugin toolbar buttons, to the left of Run
        {
            var cap = c; var pr = Btn(34, 8);
            if (IconBtn(r, pr, Ircuitry.Core.Icons.Glyph(string.IsNullOrEmpty(cap.Icon) ? "puzzle-piece" : cap.Icon), 16)) _plugins.Activate(cap.PluginId, "toolbar", cap.Id);
        }
        r.End();

        // tabs fill the gutter between the icon and the action cluster (manages its own scissor batches)
        DrawCartridgeTabs(r, bar, new RectF(x, bar.Y, rx - 12 - x, bar.H), clock, NoDrag);

        bool OverNoDrag()
        {
            for (int i = 0; i + 3 < noDrag.Count; i += 4)
                if (In.Mouse.X >= noDrag[i] && In.Mouse.X < noDrag[i] + noDrag[i + 2] &&
                    In.Mouse.Y >= noDrag[i + 1] && In.Mouse.Y < noDrag[i + 1] + noDrag[i + 3]) return true;
            return false;
        }
        // right-click the icon or any empty (draggable) part of the bar -> the window menu, like a real title bar
        if (!Modal && In.RightPressed && bar.Contains(In.Mouse) && !OverNoDrag()) OpenWindowMenu(In.Mouse);
        // double-click an empty part of the bar -> maximize/restore, like a real title bar
        if (!Modal && In.LeftPressed && bar.Contains(In.Mouse) && Sdl.CustomChrome && WindowHandle != IntPtr.Zero && !OverNoDrag() && !_tabDragging)
        {
            if (clock.Time - _titleClickTime < 0.35f) { Sdl.ToggleMaximize(WindowHandle); _titleClickTime = -1; }
            else _titleClickTime = clock.Time;
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
        _ctxItems.Add(new CtxItem { Icon = Ircuitry.Core.Icons.Glyph("minus"), Label = "Minimize", Shortcut = "", Enabled = true, Do = () => Sdl.Minimize(WindowHandle) });
        _ctxItems.Add(new CtxItem { Icon = maxed ? Ircuitry.Core.Icons.Glyph("cards") : Ircuitry.Core.Icons.Glyph("square"), Label = maxed ? "Restore" : "Maximize", Shortcut = "", Enabled = true, Do = () => Sdl.ToggleMaximize(WindowHandle) });
        _ctxItems.Add(new CtxItem { Icon = Sdl.AlwaysOnTop ? Ircuitry.Core.Icons.Glyph("check") : Ircuitry.Core.Icons.Glyph("square"), Label = "Always on top", Shortcut = "", Enabled = true, Do = () => Sdl.ToggleAlwaysOnTop(WindowHandle) });
        _ctxItems.Add(new CtxItem { Sep = true });
        _ctxItems.Add(new CtxItem { Icon = Ircuitry.Core.Icons.Glyph("camera"), Label = "Screenshot this window", Shortcut = "", Enabled = true, Do = RequestScreenshot });
        _ctxItems.Add(new CtxItem { Sep = true });
        _ctxItems.Add(new CtxItem { Icon = Ircuitry.Core.Icons.Glyph("x"), Label = "Close", Shortcut = "", Enabled = true, Do = () => Sdl.CloseRequested = true });
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
        r.RoundFill(pad, hot ? BarKeyHot : BarKey, 9f);
        r.RoundFill(new RectF(pad.X + 2, pad.Y + 2, pad.W - 4, pad.H * 0.42f), Theme.WithAlpha(Color.White, 0.5f), 6f);  // gloss
        return hot && In.LeftPressed && !_ircWinJustOpened;
    }

    // An icon-only title-bar button: a key pad with a (monochrome) glyph. Emoji read as clean line-art here.
    private bool IconBtn(Renderer r, RectF rect, string glyph, int size, Color? tint = null)
    {
        bool c = IconPad(r, rect, out var pad);
        r.TextCentered(r.Fonts.Get(FontKind.Sans, size), glyph, pad, tint ?? BarInk);
        return c;
    }

    // The primary run control: a matte, pastel play (start, soft green) / stop (soft red) button - the same
    // flat, chunky look as the inspector's RUN BOT, not a glossy candy pill.
    private void DrawPlayStop(Renderer r, RectF rect)
    {
        bool running = RunningOf(Bot);
        bool hot = !Modal && rect.Contains(In.Mouse);
        Color baseCol = running ? Theme.Mix(Theme.Alert, Color.White, 0.10f) : Theme.Mix(Theme.Ok, Color.White, 0.28f);
        Color fill = hot ? Theme.Mix(baseCol, Color.White, 0.12f) : baseCol;
        Color glyph = Theme.Mix(baseCol, Color.Black, 0.62f);   // always a dark icon, readable on the green/red pill in any theme
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
        DrawGlyphX(r, closeR.Center, 4.5f, ch ? Theme.TextInk : Theme.WithAlpha(BarInk, 0.85f));
        noDrag(closeR); if (In.LeftPressed && ch) Sdl.CloseRequested = true; cx -= w;

        var maxR = new RectF(cx - w, 0, w, h);
        bool mh = maxR.Contains(In.Mouse);
        if (mh) r.Fill(maxR, Theme.WithAlpha(Color.White, 0.30f));
        var mc = Theme.WithAlpha(BarInk, mh ? 1f : 0.85f);
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
        r.HLine(minR.Center.X - 6, minR.Center.X + 6, minR.Center.Y + 4, Theme.WithAlpha(BarInk, nh ? 1f : 0.85f), 1.7f);
        noDrag(minR); if (In.LeftPressed && nh) Sdl.Minimize(WindowHandle); cx -= w;

        return cx;
    }

    private static void DrawGlyphX(Renderer r, Vector2 c, float s, Color col)
    {
        r.Line(new Vector2(c.X - s, c.Y - s), new Vector2(c.X + s, c.Y + s), col, 1.7f);
        r.Line(new Vector2(c.X - s, c.Y + s), new Vector2(c.X + s, c.Y - s), col, 1.7f);
    }

    // DS game-cartridge tabs in a scrollable gutter. Each is a chunky rounded cartridge with a colour "label"
    // strip + status dot; the active one is a cream cartridge that pops off the glossy bar. A caret button (and
    // mouse wheel) scrolls when the tabs overflow.
    private void DrawCartridgeTabs(Renderer r, RectF bar, RectF gutter, Clock clock, Action<RectF> noDrag)
    {
        var tf = r.Fonts.Get(FontKind.Display, 16);
        var gf = r.Fonts.Get(FontKind.SansBold, 12);
        float top = bar.Y + 7, tabH = bar.H - 12, pad = 7f;
        var bots = _app.Bots;

        // ---- render plan: a flat ordered list of elements. kind 0 = a bot tab, kind 1 = a group header. a
        // collapsed group hides its member tabs (except the active bot, which always stays visible). ----
        var elems = new List<(int kind, int bot, TabGroup? g)>();
        for (int i = 0; i < bots.Count;)
        {
            var g = _app.GroupOf(bots[i]);
            if (g == null) { elems.Add((0, i, null)); i++; continue; }
            elems.Add((1, i, g));
            int j = i;
            while (j < bots.Count && bots[j].GroupId == g.Id) { if (!g.Collapsed || j == _app.Active) elems.Add((0, j, g)); j++; }
            i = j;
        }

        float ElemW(int k)
        {
            var e = elems[k];
            if (e.kind == 1) return GroupHeaderWidth(gf, e.g!);
            var b = bots[e.bot];
            return _renamingBot == b ? 210 : Math.Clamp(tf.MeasureString(Ircuitry.Render.Renderer.SafeText(b.Name)).X + 58, 86, 150);
        }
        var ws = new float[elems.Count];
        float total = 0;
        for (int k = 0; k < elems.Count; k++) { ws[k] = ElemW(k); total += ws[k] + pad; }
        total += 36;   // the + button

        bool overflow = total > gutter.W;
        float scrollBtnW = overflow ? 26 : 0;
        float viewW = gutter.W - scrollBtnW;
        float maxScroll = MathF.Max(0, total - viewW);
        if (!Modal && gutter.Contains(In.Mouse) && In.ScrollDelta != 0) _tabScroll -= In.ScrollDelta * 0.6f;
        _tabScroll = Math.Clamp(_tabScroll, 0, maxScroll);

        var ex = new float[elems.Count];
        { float x = gutter.X - _tabScroll; for (int k = 0; k < elems.Count; k++) { ex[k] = x; x += ws[k] + pad; } }
        float addX = elems.Count > 0 ? ex[^1] + ws[^1] + pad : gutter.X - _tabScroll;

        r.Begin(BlendMode.Alpha, new RectF(gutter.X, gutter.Y, viewW, gutter.H).ToRectangle());

        // ---- coloured group containers (behind each group's header + member tabs) ----
        _groupRects.Clear();
        for (int k = 0; k < elems.Count; k++)
        {
            if (elems[k].kind != 1) continue;
            var g = elems[k].g!;
            int last = k;
            for (int m = k + 1; m < elems.Count && elems[m].g == g; m++) last = m;
            var cont = new RectF(ex[k] - 3, top - 1, (ex[last] + ws[last]) - ex[k] + 6, tabH + 2);
            _groupRects.Add((g, cont));
            r.RoundFill(cont, Theme.WithAlpha(g.Color, 0.17f), 11f);
            r.RoundOutline(cont, Theme.WithAlpha(g.Color, 0.55f), 11f);
        }

        // ---- elements: while a tab is being dragged, neighbours slide toward their slots and the dragged tab
        // rides the cursor (lifted), drawn last so it stays on top - the real-browser feel ----
        bool anim = _tabDragging && _tabDragBot != null;
        float DrawX(int k)
        {
            if (elems[k].kind != 0) return ex[k];                                  // headers snap to their slot
            var b = bots[elems[k].bot];
            if (anim && b == _tabDragBot) return Math.Clamp(In.Mouse.X - _tabDragGrabDx, gutter.X - ws[k] + 24, gutter.X + viewW - 24);
            if (!anim) { _tabDrawX[b] = ex[k]; return ex[k]; }
            float cur = _tabDrawX.TryGetValue(b, out var v) ? v : ex[k];
            cur += (ex[k] - cur) * 0.35f;
            if (MathF.Abs(ex[k] - cur) < 0.5f) cur = ex[k];
            return _tabDrawX[b] = cur;
        }

        int n0 = bots.Count;   // closing a tab (the ×) mutates Bots mid-draw - bail before our stale indices bite
        int dragK = -1;
        for (int k = 0; k < elems.Count; k++)
        {
            if (anim && elems[k].kind == 0 && bots[elems[k].bot] == _tabDragBot) { dragK = k; continue; }   // deferred - drawn on top below
            var slot = new RectF(DrawX(k), top, ws[k], tabH);
            if (slot.Right < gutter.X - 2 || slot.X > gutter.X + viewW + 2) continue;
            if (elems[k].kind == 1) DrawGroupHeader(r, elems[k].g!, slot, gf, clock, gutter, viewW, noDrag);
            else DrawOneTab(r, elems[k].bot, slot, tf, clock, gutter, viewW, noDrag);
            if (bots.Count != n0) break;   // a tab just closed itself; the rest of the plan is stale
        }
        if (bots.Count != n0) { r.End(); _tabDragBot = null; _tabDragging = false; return; }
        if (dragK >= 0)
            DrawOneTab(r, elems[dragK].bot, new RectF(DrawX(dragK), top, ws[dragK], tabH), tf, clock, gutter, viewW, noDrag, lifted: true);

        // + add a bot
        var addR = new RectF(addX, top + 1, 32, tabH - 2);
        if (addR.X <= gutter.X + viewW)
        {
            bool ah = !Modal && addR.Contains(In.Mouse);
            r.RoundFill(addR, ah ? Theme.WithAlpha(Color.White, 0.5f) : Theme.WithAlpha(Color.White, 0.28f), 8f);
            r.TextCentered(r.Fonts.Get(FontKind.SansBold, 18), "+", addR, BarInk);
            var addClip = addR.Intersect(new RectF(gutter.X, gutter.Y, viewW, gutter.H));
            if (addClip.W > 4) noDrag(addClip);
            if (ah && In.LeftPressed) { _templateOpen = true; _templateJustOpened = true; }
        }
        r.End();

        // per-bot visible x-centre (hidden tabs = -1), so drag targets and group hit-tests use what's on screen
        var botCx = new float[bots.Count];
        for (int b = 0; b < bots.Count; b++) botCx[b] = -1;
        for (int k = 0; k < elems.Count; k++) if (elems[k].kind == 0) botCx[elems[k].bot] = ex[k] + ws[k] / 2f;
        int TargetIndex() { int t = 0; for (int b = 0; b < bots.Count; b++) if (botCx[b] >= 0 && In.Mouse.X > botCx[b]) t = b + 1; return Math.Clamp(t, 0, bots.Count - 1); }
        TabGroup? HoverGroup() { foreach (var (g, rect) in _groupRects) if (rect.Contains(In.Mouse)) return g; return null; }

        DragTabReorder(TargetIndex, HoverGroup);
        DragGroup(botCx);

        // caret scroll button to page the gutter right (wraps back to start at the end)
        if (overflow)
        {
            r.Begin();
            var sr = new RectF(gutter.Right - scrollBtnW, top + 2, scrollBtnW - 2, tabH - 4);
            bool sh = !Modal && sr.Contains(In.Mouse);
            r.RoundFill(sr, sh ? Theme.WithAlpha(Color.White, 0.5f) : Theme.WithAlpha(Color.White, 0.3f), 7f);
            r.TextCentered(r.Fonts.Get(FontKind.SansBold, 15), Ircuitry.Core.Icons.Glyph("caret-right"), sr, BarInk);
            noDrag(sr);
            if (sh && In.LeftPressed) _tabScroll = _tabScroll >= maxScroll - 1 ? 0 : Math.Min(maxScroll, _tabScroll + viewW * 0.8f);
            r.End();
        }
    }

    private float GroupHeaderWidth(FontStashSharp.DynamicSpriteFont gf, TabGroup g)
    {
        if (_renamingGroup == g) return 150;
        return Math.Clamp(gf.MeasureString(Ircuitry.Render.Renderer.SafeText(g.Name)).X + 32, 56, 150) + (g.Collapsed ? 20 : 0);   // + room for the count badge
    }

    private void DrawGroupHeader(Renderer r, TabGroup g, RectF slot, FontStashSharp.DynamicSpriteFont gf, Clock clock, RectF gutter, float viewW, Action<RectF> noDrag)
    {
        var col = g.Color;
        var chip = new RectF(slot.X, slot.Y + 1, slot.W, slot.H - 2);
        var clip = chip.Intersect(new RectF(gutter.X, gutter.Y, viewW, gutter.H));
        if (clip.W > 4) noDrag(clip);
        bool hot = !Modal && chip.Contains(In.Mouse);
        r.RoundFill(chip, Theme.WithAlpha(col, hot ? 0.96f : 0.85f), 8f);
        r.RoundOutline(chip, Theme.WithAlpha(Theme.Mix(col, Theme.Text, 0.35f), 0.85f), 8f);

        if (_renamingGroup == g)
        {
            if (_pendingRenameFocus == "group.rename") { _ui.Focus = "group.rename"; _pendingRenameFocus = null; }   // focus the field on its first drawn frame (so EndFrame doesn't blur it)
            var nm = _ui.TextField("group.rename", new RectF(chip.X + 6, chip.Center.Y - 9, chip.W - 12, 22), g.Name, "group");
            if (nm != g.Name) { g.Name = string.IsNullOrWhiteSpace(nm) ? g.Name : nm; _app.MarkDirty(); }
            if (_ui.Focus != "group.rename") _renamingGroup = null;
            return;
        }

        float tx = chip.X + 8;
        var cf = r.Fonts.Get(FontKind.SansBold, 11);
        string caret = Ircuitry.Core.Icons.Glyph(g.Collapsed ? "caret-right" : "caret-down");
        r.Text(cf, caret, new Vector2(tx, chip.Center.Y - cf.MeasureString(Ircuitry.Render.Renderer.SafeText(caret)).Y / 2f), Theme.TextInk);
        tx += 13;
        float nameMax = chip.Right - tx - 8 - (g.Collapsed ? 16 : 0);
        r.Text(gf, r.Ellipsize(gf, g.Name, nameMax), new Vector2(tx, chip.Center.Y - gf.MeasureString(Ircuitry.Render.Renderer.SafeText(g.Name)).Y / 2f - 1), Theme.TextInk);
        if (g.Collapsed)
            r.TextRight(cf, _app.GroupCount(g).ToString(), chip.Right - 8, chip.Center.Y - cf.MeasureString(Ircuitry.Render.Renderer.SafeText("0")).Y / 2f, Theme.WithAlpha(Theme.TextInk, 0.85f));

        if (!Modal && gutter.Contains(In.Mouse) && chip.Contains(In.Mouse))
        {
            if (In.RightPressed) OpenGroupMenu(g, In.Mouse);
            else if (In.LeftPressed) { _groupDragG = g; _groupDragDownX = In.Mouse.X; _groupDragging = false; }   // click=collapse, drag=move
        }
    }

    private void DrawOneTab(Renderer r, int i, RectF slot, FontStashSharp.DynamicSpriteFont tf, Clock clock, RectF gutter, float viewW, Action<RectF> noDrag, bool lifted = false)
    {
        var bot = _app.Bots[i];
        bool active = i == _app.Active;
        bool renaming = _renamingBot == bot;
        var col = StatusColor(bot.Runtime);

        var tab = lifted ? new RectF(slot.X, slot.Y - 4, slot.W, slot.H + 2)
                : active ? new RectF(slot.X, slot.Y - 2, slot.W, slot.H + 2)
                : new RectF(slot.X, slot.Y + 2, slot.W, slot.H - 2);
        var clip = tab.Intersect(new RectF(gutter.X, gutter.Y, viewW, gutter.H));
        if (clip.W > 4) noDrag(clip);

        if (lifted) r.RoundFill(tab.Offset(0, 5), Theme.WithAlpha(Color.Black, 0.28f), 10f);   // a real shadow under the picked-up tab
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
        if (bot.IsRemote)   // a little cloud where the status dot would be: this bot lives on a server
        {
            var cf = r.Fonts.Get(FontKind.Sans, 13);
            // glyph + tint reflect the live connection: connected = cloud, reconnecting = amber cloud, dropped = slash
            var st = bot.Remote!.State;
            bool live = st == Ircuitry.App.Server.ControlClient.Conn.Connected;
            bool busy = st is Ircuitry.App.Server.ControlClient.Conn.Reconnecting or Ircuitry.App.Server.ControlClient.Conn.Connecting;
            string cg = Ircuitry.Core.Icons.Glyph(live ? "cloud" : busy ? "cloud-arrow-up" : "cloud-slash");
            var cc = live ? col : busy ? Theme.Amber : Theme.Alert;
            var cm = cf.MeasureString(Ircuitry.Render.Renderer.SafeText(cg));
            r.Text(cf, cg, new Vector2(tab.X + 16 - cm.X / 2f, tab.Center.Y + 3 - cm.Y / 2f), Theme.WithAlpha(cc, active ? 1f : 0.8f));
        }
        else if (active) Hud.SoftDot(r, new Vector2(tab.X + 16, tab.Center.Y + 3), 3.4f, col);
        else r.Disc(new Vector2(tab.X + 16, tab.Center.Y + 3), 3f, col);

        if (renaming)
        {
            if (_pendingRenameFocus == "tab.rename") { _ui.Focus = "tab.rename"; _pendingRenameFocus = null; }
            var nm = _ui.TextField("tab.rename", new RectF(tab.X + 26, tab.Center.Y - 9, tab.W - 36, 22), bot.Name, "bot name");
            if (nm != bot.Name) { bot.Name = string.IsNullOrWhiteSpace(nm) ? bot.Name : nm; _app.MarkDirty(); }
            if (_ui.Focus != "tab.rename") _renamingBot = null;
            return;
        }

        Color txt = active ? Theme.Text : Theme.TextDim;
        r.Text(tf, r.Ellipsize(tf, bot.Name, slot.W - 52), new Vector2(tab.X + 28, tab.Center.Y - tf.MeasureString(Ircuitry.Render.Renderer.SafeText(bot.Name)).Y / 2f + 3), txt);

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
            // a remote tab is just a live view of a server bot - closing it detaches the view, deletes nothing,
            // so skip the destructive confirm. Local bots still prompt (their data is only here).
            if (xHover && bot.IsRemote) { int bi = _app.Bots.IndexOf(bot); if (bi >= 0) { _app.RemoveBot(bi); _editor.Selection.Clear(); } }
            else if (xHover) { _confirmDeleteBot = bot; _confirmJustOpened = true; }
            else
            {
                bool dbl = _tabClickBot == bot && clock.Time - _tabClickTime < 0.35f;
                _tabClickBot = bot; _tabClickTime = clock.Time;
                if (!active) { _app.SetActive(i); _editor.Selection.Clear(); }
                if (dbl) { _renamingBot = bot; _pendingRenameFocus = "tab.rename"; }
                else { _tabDragBot = bot; _tabDragDownX = In.Mouse.X; _tabDragGrabDx = In.Mouse.X - tab.X; _tabDragging = false; }   // begin a potential drag-reorder
            }
        }
        else if (!Modal && In.RightPressed && tab.Contains(In.Mouse) && gutter.Contains(In.Mouse))
            OpenTabMenu(bot, In.Mouse);   // group options for this tab
    }

    // ---- tab-group drag + menus ----
    private void DragTabReorder(Func<int> target, Func<TabGroup?> hoverGroup)
    {
        if (_tabDragBot != null && !_app.Bots.Contains(_tabDragBot)) { _tabDragBot = null; _tabDragging = false; }
        if (_tabDragBot == null) return;
        if (!In.LeftDown)
        {
            // drop: the tab joins the group whose coloured band the cursor is over (or leaves its group), then re-contiguify
            if (_tabDragging) { _tabDragBot.GroupId = hoverGroup()?.Id; _app.NormalizeGroups(); _app.MarkDirty(); }
            _tabDragBot = null; _tabDragging = false; _tabDrawX.Clear(); return;
        }
        if (!_tabDragging && MathF.Abs(In.Mouse.X - _tabDragDownX) > 6) _tabDragging = true;
        if (!_tabDragging) return;
        // live: membership follows the band under the cursor; position follows the target slot (no mid-drag normalize)
        _tabDragBot.GroupId = hoverGroup()?.Id;
        int tgt = target(), cur = _app.Bots.IndexOf(_tabDragBot);
        if (cur >= 0 && tgt != cur)
        {
            var activeBot = _app.ActiveBot;
            _app.Bots.RemoveAt(cur); _app.Bots.Insert(tgt, _tabDragBot);
            _app.Active = Math.Max(0, _app.Bots.IndexOf(activeBot));
            _app.MarkDirty();
        }
    }

    private void DragGroup(float[] botCx)
    {
        if (_groupDragG != null && !_app.Groups.Contains(_groupDragG)) { _groupDragG = null; _groupDragging = false; }
        if (_groupDragG == null) return;
        if (!In.LeftDown)
        {
            if (!_groupDragging) { _groupDragG.Collapsed = !_groupDragG.Collapsed; _app.MarkDirty(); }   // a plain click toggles collapse
            _groupDragG = null; _groupDragging = false; return;
        }
        if (!_groupDragging && MathF.Abs(In.Mouse.X - _groupDragDownX) > 6) _groupDragging = true;
        if (!_groupDragging) return;
        // move the whole group: drop the block before the first non-member tab whose centre is right of the cursor
        Bot? before = null;
        for (int b = 0; b < _app.Bots.Count; b++)
            if (_app.Bots[b].GroupId != _groupDragG.Id && botCx[b] >= 0 && botCx[b] > In.Mouse.X) { before = _app.Bots[b]; break; }
        _app.MoveGroupBlock(_groupDragG, before);
    }

    private void OpenTabMenu(Bot b, Vector2 anchor)
    {
        _ctxAnchor = anchor; _ctxItems.Clear();
        var g = _app.GroupOf(b);
        _ctxItems.Add(new CtxItem { Icon = Ircuitry.Core.Icons.Glyph("plus"), Label = "Add to new group", Enabled = true, Do = () => { var ng = _app.NewGroup(b); _renamingGroup = ng; _pendingRenameFocus = "group.rename"; } });
        foreach (var other in _app.Groups)
        {
            if (other == g) continue;
            var o = other;
            _ctxItems.Add(new CtxItem { Icon = "circle", Label = "Add to " + (o.Name.Length > 0 ? o.Name : "group"), Enabled = true, Tint = o.Color, Do = () => _app.AddToGroup(b, o) });
        }
        if (g != null)
        {
            _ctxItems.Add(new CtxItem { Sep = true });
            _ctxItems.Add(new CtxItem { Icon = Ircuitry.Core.Icons.Glyph("minus-circle"), Label = "Remove from group", Enabled = true, Do = () => _app.RemoveFromGroup(b) });
        }
        _ctxOpen = true; _ctxJustOpened = true;
    }

    private void OpenGroupMenu(TabGroup g, Vector2 anchor)
    {
        _ctxAnchor = anchor; _ctxItems.Clear();
        _ctxItems.Add(new CtxItem { Icon = Ircuitry.Core.Icons.Glyph(g.Collapsed ? "caret-right" : "caret-down"), Label = g.Collapsed ? "Expand group" : "Collapse group", Enabled = true, Do = () => { g.Collapsed = !g.Collapsed; _app.MarkDirty(); } });
        _ctxItems.Add(new CtxItem { Icon = Ircuitry.Core.Icons.Glyph("pencil"), Label = "Rename group", Enabled = true, Do = () => { _renamingGroup = g; _pendingRenameFocus = "group.rename"; } });
        _ctxItems.Add(new CtxItem { Sep = true });
        for (int c = 0; c < TabGroup.PaletteCount; c++)
        {
            int ci = c;
            _ctxItems.Add(new CtxItem { Icon = "circle", Label = TabGroup.PaletteName(ci), Enabled = true, Tint = TabGroup.Palette(ci), Do = () => { g.ColorIndex = ci; _app.MarkDirty(); } });
        }
        _ctxItems.Add(new CtxItem { Sep = true });
        _ctxItems.Add(new CtxItem { Icon = Ircuitry.Core.Icons.Glyph("x"), Label = "Ungroup", Enabled = true, Do = () => _app.Ungroup(g) });
        _ctxOpen = true; _ctxJustOpened = true;
    }

    // A floppy "apply" button that hangs in the canvas's top-right ONLY while the live bot has edits it hasn't
    // applied yet (keyed on the behaviour signature, not the autosaved disk-dirty flag - so it doesn't vanish
    // when you click away). Clicking it applies the edits to the running bot (and saves).
    private void DrawCanvasSave(Renderer r)
    {
        // local: the runtime has edits it hasn't applied. remote: the server says the running bot's stored graph
        // differs from what its live runtime is executing. Either way, clicking hot-applies (no restart).
        bool remote = Bot.IsRemote;
        bool unapplied = remote
            ? (Bot.Remote?.Connected == true && Bot.Remote.BotUnapplied(Bot.RemoteName))
            : Bot.Runtime.HasUnapplied(Bot.Graph);
        if (!unapplied) return;
        var c = _l.Canvas;
        var rect = new RectF(c.Right - 54, c.Y + 14, 40, 40);
        bool hot = !Modal && rect.Contains(In.Mouse);
        var col = Theme.Cyan;
        r.Begin();
        r.RoundFill(rect.Offset(0, 2), Theme.WithAlpha(Color.Black, 0.10f), 11f);
        r.RoundFill(rect, hot ? Theme.Mix(col, Color.White, 0.14f) : col, 11f);
        r.RoundOutline(rect, Theme.WithAlpha(Theme.Mix(col, Theme.Text, 0.25f), hot ? 1f : 0.7f), 11f);
        r.TextCentered(r.Fonts.Get(FontKind.Sans, 19), Ircuitry.Core.Icons.Glyph("floppy-disk"), rect, Theme.TextInk);
        if (hot)   // a tiny "apply to live bot" hint on hover
            r.Text(r.Fonts.Get(FontKind.SansBold, 11), "apply", new Vector2(rect.X - 4, rect.Bottom + 2), Theme.Mix(col, Theme.Text, 0.4f));
        r.End();
        if (hot && In.LeftPressed)
        {
            if (remote) ApplyRemote(Bot);
            else
            {
                Bot.Runtime.ApplyGraph(Bot.Graph);
                _app.Save();
                Notify(Ircuitry.Core.Icons.Glyph("arrows-clockwise") + " Applied changes to the live bot");
            }
        }
    }
}
