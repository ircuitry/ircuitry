using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Ircuitry.Core;
using Ircuitry.Render;

namespace Ircuitry.Screens;

// The custom client-side title bar (VSCode/Firefox style): the OS frame is gone and we draw our own.
// Layout: app icon · DS-cartridge bot tabs · (right) File / More / window controls. The OS still does the
// real dragging/resizing via SDL's hit-test (see Core.Sdl); we just publish which rects stay clickable.
public sealed partial class MainScreen
{
    public IntPtr WindowHandle;   // set by the host so the window controls can drive SDL

    private void DrawTitlebar(Renderer r, Clock clock)
    {
        var bar = _l.Titlebar;
        r.Fill(bar, Theme.PanelHi);
        r.HLine(0, bar.W, bar.Bottom, Theme.WithAlpha(Theme.Cyan, 0.5f), 1.4f);
        r.HLine(0, bar.W, bar.Bottom + 1.4f, Theme.WithAlpha(Theme.Cyan, 0.10f), 3f);

        var noDrag = new List<float>();
        void NoDrag(RectF q) { noDrag.Add(q.X); noDrag.Add(q.Y); noDrag.Add(q.W); noDrag.Add(q.H); }

        // app icon (left)
        float x = 12;
        if (r.Brand != null) { float isz = 26; r.Image(r.Brand, new RectF(x, (bar.H - isz) / 2f, isz, isz)); x += isz + 12; }

        // right cluster: window controls (custom chrome only), then More / File
        float rx = bar.Right;
        if (Sdl.CustomChrome && WindowHandle != IntPtr.Zero) rx = DrawWindowControls(r, bar, NoDrag);

        RectF Slot(float ww) { var rr = new RectF(rx - ww, bar.Y + 8, ww, bar.H - 16); rx -= ww + 8; NoDrag(rr); return rr; }
        var moreR = Slot(80);
        if (_ui.Button("tb.more", moreR, "⋯ More", Theme.Violet)) OpenMoreMenu(new Vector2(moreR.X, moreR.Bottom + 3));
        var fileR = Slot(86);
        if (_ui.Button("tb.file", fileR, _app.Dirty ? "📁 File ●" : "📁 File", _app.Dirty ? Theme.Amber : Theme.Cyan))
            OpenFileMenu(new Vector2(fileR.X, fileR.Bottom + 3));

        // bot tabs fill the space between the icon and the right cluster
        DrawCartridgeTabs(r, bar, x, rx - 12, clock, NoDrag);

        Sdl.PublishTitlebar((int)bar.Bottom, _vw, _vh, Sdl.IsMaximized(WindowHandle), noDrag.ToArray());
    }

    // The –, ▢/❐, ✕ window buttons, right-aligned. Returns the left edge of the cluster.
    private float DrawWindowControls(Renderer r, RectF bar, Action<RectF> noDrag)
    {
        float w = 42, h = bar.H;
        float cx = bar.Right;
        var f = r.Fonts.Get(FontKind.SansBold, 13);

        // close
        var closeR = new RectF(cx - w, 0, w, h);
        bool ch = closeR.Contains(In.Mouse);
        if (ch) r.Fill(closeR, Theme.Alert);
        DrawGlyphX(r, closeR.Center, 4.5f, ch ? Theme.TextInk : Theme.TextDim);
        noDrag(closeR);
        if (In.LeftPressed && ch) Sdl.CloseRequested = true;
        cx -= w;

        // maximize / restore
        var maxR = new RectF(cx - w, 0, w, h);
        bool mh = maxR.Contains(In.Mouse);
        if (mh) r.Fill(maxR, Theme.WithAlpha(Theme.Edge, 0.35f));
        bool maxed = Sdl.IsMaximized(WindowHandle);
        var mc = mh ? Theme.Text : Theme.TextDim;
        if (maxed)   // restore glyph: two overlapped squares
        {
            r.RectOutline(new RectF(maxR.Center.X - 3, maxR.Center.Y - 5, 8, 8), mc, 1.4f);
            r.Fill(new RectF(maxR.Center.X - 6, maxR.Center.Y - 2, 8, 8), mh ? Theme.WithAlpha(Theme.Edge, 0.35f) : Theme.PanelHi);
            r.RectOutline(new RectF(maxR.Center.X - 6, maxR.Center.Y - 2, 8, 8), mc, 1.4f);
        }
        else r.RectOutline(new RectF(maxR.Center.X - 5, maxR.Center.Y - 5, 10, 10), mc, 1.4f);
        noDrag(maxR);
        if (In.LeftPressed && mh) Sdl.ToggleMaximize(WindowHandle);
        cx -= w;

        // minimize
        var minR = new RectF(cx - w, 0, w, h);
        bool nh = minR.Contains(In.Mouse);
        if (nh) r.Fill(minR, Theme.WithAlpha(Theme.Edge, 0.35f));
        r.HLine(minR.Center.X - 6, minR.Center.X + 6, minR.Center.Y + 4, nh ? Theme.Text : Theme.TextDim, 1.6f);
        noDrag(minR);
        if (In.LeftPressed && nh) Sdl.Minimize(WindowHandle);
        cx -= w;

        return cx;
    }

    private static void DrawGlyphX(Renderer r, Vector2 c, float s, Color col)
    {
        r.Line(new Vector2(c.X - s, c.Y - s), new Vector2(c.X + s, c.Y + s), col, 1.7f);
        r.Line(new Vector2(c.X - s, c.Y + s), new Vector2(c.X + s, c.Y - s), col, 1.7f);
    }

    // DS game-cartridge tabs: each bot is a chunky rounded cartridge with a colored label strip + status
    // dot; the active one lifts up, brightens and glows. Double-click renames, the × closes, "+" adds a bot.
    private void DrawCartridgeTabs(Renderer r, RectF bar, float startX, float endX, Clock clock, Action<RectF> noDrag)
    {
        var tf = r.Fonts.Get(FontKind.Display, 13);
        float x = startX;
        float top = bar.Y + 7, tabH = bar.H - 9;   // sits on the title bar, a few px of breathing room on top

        for (int i = 0; i < _app.Bots.Count && x < endX - 40; i++)
        {
            var bot = _app.Bots[i];
            bool active = i == _app.Active;
            bool renaming = _renamingBot == bot;
            float w = renaming ? 210 : Math.Min(196, tf.MeasureString(bot.Name).X + 58);
            w = Math.Min(w, endX - x);
            var col = StatusColor(bot.Runtime);

            float ty = active ? top - 2 : top + 2;       // active lifts up, inactive recedes
            float th = active ? tabH + 2 : tabH - 2;
            var tab = new RectF(x, ty, w, th);
            noDrag(tab);

            if (active)
            {
                r.RoundFill(tab.Inflate(1.5f, 1.5f), Theme.WithAlpha(col, 0.18f), 11f);   // soft glow halo
                r.RoundFill(tab, Theme.PanelHi, 9f);
                r.RoundOutline(tab, Theme.WithAlpha(col, 0.85f), 9f);
            }
            else
            {
                r.RoundFill(tab, Theme.PanelLo, 9f);
                r.RoundOutline(tab, Theme.Hairline, 9f);
            }
            // cartridge "label" strip across the top
            r.RoundFill(new RectF(tab.X + 6, tab.Y + 4, tab.W - 12, 4), Theme.WithAlpha(col, active ? 0.9f : 0.4f), 2f);
            // status dot
            if (active) Hud.SoftDot(r, new Vector2(tab.X + 16, tab.Center.Y + 3), 3.4f, col);
            else r.Disc(new Vector2(tab.X + 16, tab.Center.Y + 3), 3f, Theme.WithAlpha(col, 0.8f));

            if (renaming)
            {
                var nm = _ui.TextField("tab.rename", new RectF(tab.X + 26, tab.Center.Y - 9, tab.W - 36, 22), bot.Name, "bot name");
                if (nm != bot.Name) { bot.Name = string.IsNullOrWhiteSpace(nm) ? bot.Name : nm; _app.MarkDirty(); }
                if (_ui.Focus != "tab.rename") _renamingBot = null;
                x += w + 7;
                continue;
            }

            r.Text(tf, r.Ellipsize(tf, bot.Name, w - 52), new Vector2(tab.X + 28, tab.Center.Y - tf.MeasureString(bot.Name).Y / 2f + 3), active ? Theme.Text : Theme.TextDim);

            // close × (only with >1 bot)
            bool canClose = _app.Bots.Count > 1;
            var xc = new Vector2(tab.Right - 14, tab.Center.Y + 3);
            var xhit = new RectF(xc.X - 10, tab.Y, 20, tab.H);
            bool xHover = canClose && xhit.Contains(In.Mouse);
            if (canClose)
            {
                if (xHover) r.Disc(xc, 9f, Theme.WithAlpha(Theme.Alert, 0.22f));
                DrawGlyphX(r, xc, 3.4f, xHover ? Theme.Alert : active ? Theme.TextDim : Theme.TextFaint);
            }

            if (!Modal && In.LeftPressed && tab.Contains(In.Mouse))
            {
                if (xHover) { _confirmDeleteBot = bot; _confirmJustOpened = true; }
                else
                {
                    bool dbl = _tabClickBot == bot && clock.Time - _tabClickTime < 0.35f;
                    _tabClickBot = bot; _tabClickTime = clock.Time;
                    if (!active) { _app.SetActive(i); _editor.Selection.Clear(); }
                    if (dbl) { _renamingBot = bot; _ui.Focus = "tab.rename"; }
                }
                return;
            }
            x += w + 7;
        }

        // the + add-bot cartridge
        var addR = new RectF(x, top + 1, 34, tabH - 2);
        if (addR.Right <= endX) { noDrag(addR); if (_ui.Button("tab.add", addR, "+", Theme.Cyan)) { _templateOpen = true; _templateJustOpened = true; } }
    }

    // The toolbar row under the title bar: project name (left), live clock + status pill (right). The
    // Run / History / Test / Apply buttons are drawn over this by their own methods.
    private void DrawToolbar(Renderer r, Clock clock)
    {
        var bar = _l.TopBar;
        r.Fill(bar, Theme.Panel);
        r.HLine(0, bar.W, bar.Bottom, Theme.Hairline, 1f);

        var nameF = r.Fonts.Get(FontKind.Display, 17);
        r.Text(nameF, _app.ProjectName + (_app.Dirty ? " *" : ""), new Vector2(20, bar.Y + (bar.H - nameF.MeasureString("X").Y) / 2f - 1), Theme.Mix(Theme.Cyan, Theme.Text, 0.35f));
        r.Text(r.Fonts.Get(FontKind.Sans, 11), "v" + Ircuitry.App.AppInfo.Version, new Vector2(20, bar.Y + bar.H - 16), Theme.TextFaint);

        var tf = r.Fonts.Get(FontKind.SansBold, 16);
        var time = DateTime.Now.ToString("HH:mm:ss");
        r.TextRight(tf, time, bar.W - 22, bar.Y + bar.H / 2f - 9, Theme.TextDim);

        // place the status pill just left of the whole action-button cluster (run/history/test/apply/bell)
        var (label, col, pulse) = StatusInfo();
        float clockW = tf.MeasureString(time).X;
        float runX = bar.W - 22 - clockW - 16 - 150;
        float histX = runX - 12 - 128;
        float bellX = histX - 12 - 94 - 12 - 110 - 12 - 40;
        var pillRect = new RectF(bellX - 12 - 150, bar.Y + (bar.H - 28) / 2f, 150, 28);
        Hud.Pill(r, pillRect, label, col, clock, pulse);
    }
}
