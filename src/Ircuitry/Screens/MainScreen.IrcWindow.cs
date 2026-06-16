using System;
using System.Collections.Generic;
using System.Text;
using FontStashSharp;
using Microsoft.Xna.Framework;
using Ircuitry.Core;
using Ircuitry.Render;
using Ircuitry.Runtime;

namespace Ircuitry.Screens;

// "What ircuitry has seen": a cosy, read-only Animal-Crossing / DS-styled IRC window. It replays the
// recent-message ring as speech bubbles (who said what, where, when) and links any message that triggered
// a workflow to that run in the History modal. Draggable by its title bar, resizable from the corner.
public sealed partial class MainScreen
{
    private bool _ircWinOpen, _ircWinJustOpened;
    private RectF _ircWin = new(120, 90, 620, 520);
    private float _ircWinScroll;
    private bool _ircWinDragging, _ircWinResizing;
    private Vector2 _ircWinDragOff;

    private struct IrcBubble { public RecentMsg M; public string[] Lines; public float H; public RunRecord? Run; }
    private readonly List<IrcBubble> _ircBubbles = new();
    private float _ircTotalH;
    private string _ircCacheSig = "";

    private static readonly Color[] NickPalette =
        { Theme.Cyan, Theme.Amber, Theme.Magenta, Theme.Violet, Theme.Lime, Theme.Berry, Theme.Sky, Theme.Teal };

    private static Color NickColor(string nick)
    {
        int h = 0; foreach (char c in nick) h = h * 31 + c;
        return NickPalette[((h % NickPalette.Length) + NickPalette.Length) % NickPalette.Length];
    }

    public void OpenIrcWindow()
    {
        float w = MathF.Min(640, _vw - 80), h = MathF.Min(540, _vh - 110);
        _ircWin = new RectF((_vw - w) / 2f, (_vh - h) / 2f, w, h);
        _ircWinScroll = 0; _ircCacheSig = "";
        _ircWinOpen = true; _ircWinJustOpened = true;
    }

    public void DebugOpenIrcWindow() { _l = Layout.Compute(_vw, _vh, _consoleH); OpenIrcWindow(); }

    // The little header button on the Event Console that opens the read-only IRC view.
    private void ConsoleViewButton(Renderer r)
    {
        var p = _l.Console;
        var btn = new RectF(p.Right - 156, p.Y + 7, 144, 24);
        if (_ui.Button("console.ircview", btn, "📺  Bot's-eye view", Theme.Teal)) OpenIrcWindow();
    }

    private void DrawIrcWindow(Renderer r, Clock clock)
    {
        HandleIrcWindowInput();
        var win = _ircWin;

        r.Begin();
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.32f));
        // window body - rounded handheld shell
        r.RoundFill(win.Offset(0, 6), Theme.WithAlpha(Color.Black, 0.20f), 20f);
        r.RoundFill(win, Theme.Void, 20f);
        r.RoundOutline(win, Theme.Edge, 20f);
        // inner screen bezel
        var screen = new RectF(win.X + 12, win.Y + 50, win.W - 24, win.H - 50 - 40);
        r.RoundFill(screen, Theme.PanelHi, 12f);
        r.RoundOutline(screen, Theme.Hairline, 12f);
        r.End();

        DrawIrcWindowHeader(r, win, clock);
        DrawIrcWindowBody(r, screen, clock);
        DrawIrcWindowFooter(r, win);
        DrawIrcWindowResizeGrip(r, win);

        _ircWinJustOpened = false;
    }

    private void DrawIrcWindowHeader(Renderer r, RectF win, Clock clock)
    {
        var head = new RectF(win.X, win.Y, win.W, 46);
        r.Begin();
        // title
        r.Text(r.Fonts.Get(FontKind.Display, 19), "📺  What ircuitry has seen", new Vector2(win.X + 18, win.Y + 12), Theme.Text);
        // live/offline dot + label
        bool live = Bot.Runtime.Running;
        float dotX = win.Right - 150;
        if (live) r.Disc(new Vector2(dotX, win.Y + 23), 7f, Theme.WithAlpha(Theme.Ok, 0.30f));
        r.Disc(new Vector2(dotX, win.Y + 23), 4.5f, live ? Theme.Ok : Theme.Idle);
        r.Text(r.Fonts.Get(FontKind.Mono, 11), live ? "live · read-only" : "offline · read-only",
            new Vector2(dotX + 10, win.Y + 17), Theme.TextDim);
        r.End();

        // close button
        var closeR = new RectF(win.Right - 40, win.Y + 12, 26, 24);
        bool closeHot = closeR.Contains(In.Mouse);
        r.Begin();
        r.RoundFill(closeR, closeHot ? Theme.Alert : Theme.PanelLo, 7f);
        r.Text(r.Fonts.Get(FontKind.SansBold, 14), "✕", new Vector2(closeR.X + 8, closeR.Y + 3), closeHot ? Theme.TextInk : Theme.TextDim);
        r.End();
        if (In.LeftPressed && closeHot) { _ircWinOpen = false; return; }
    }

    private void DrawIrcWindowBody(Renderer r, RectF screen, Clock clock)
    {
        RebuildIrcBubbles(r, screen.W - 28);
        var content = screen.Shrink(10);

        float maxScroll = MathF.Max(0, _ircTotalH - content.H);
        if (content.Contains(In.Mouse) && In.ScrollDelta != 0) _ircWinScroll += In.ScrollDelta * 0.5f;
        _ircWinScroll = Math.Clamp(_ircWinScroll, 0, maxScroll);

        r.Begin(BlendMode.Alpha, content.ToRectangle());
        if (_ircBubbles.Count == 0)
        {
            r.Text(r.Fonts.Get(FontKind.Sans, 14), "Nothing seen yet - when your bot is connected, every",
                new Vector2(content.X + 6, content.Y + 12), Theme.TextDim);
            r.Text(r.Fonts.Get(FontKind.Sans, 14), "message it sees will replay here. ✨",
                new Vector2(content.X + 6, content.Y + 34), Theme.TextDim);
        }

        float y = content.Bottom - _ircTotalH + _ircWinScroll;
        var jumpHit = new List<(RectF box, string msgid)>();
        foreach (var b in _ircBubbles)
        {
            if (y + b.H >= content.Y - 4 && y <= content.Bottom + 4)
                DrawBubble(r, b, content.X, y, content.W, jumpHit);
            y += b.H;
        }
        r.End();

        if (maxScroll > 1)
        {
            r.Begin();
            float frac = content.H / _ircTotalH;
            float th = MathF.Max(28, content.H * frac);
            float t = maxScroll <= 0 ? 1f : 1f - _ircWinScroll / maxScroll;
            r.RoundFill(new RectF(content.Right - 2, content.Y + (content.H - th) * t, 4f, th), Theme.WithAlpha(Theme.Edge, 0.7f), 2f);
            r.End();
        }

        // jump-to-workflow clicks (drawn during DrawBubble, hit-tested here)
        if (In.LeftPressed && !_ircWinJustOpened)
            foreach (var (box, msgid) in jumpHit)
                if (box.Contains(In.Mouse)) { _ircWinOpen = false; OpenHistoryForMsg(msgid); break; }
    }

    private void DrawBubble(Renderer r, IrcBubble b, float x, float y, float w, List<(RectF, string)> jumpHit)
    {
        var nickF = r.Fonts.Get(FontKind.SansBold, 12);
        var metaF = r.Fonts.Get(FontKind.Mono, 10);
        var bodyF = r.Fonts.Get(FontKind.Sans, 13);
        Color nc = NickColor(b.M.Nick.Length > 0 ? b.M.Nick : "server");

        float pad = 9f;
        float headY = y + 4;
        // name tag pill
        string nick = b.M.Nick.Length > 0 ? b.M.Nick : "·";
        float nw = nickF.MeasureString(nick).X;
        var tag = new RectF(x + 2, headY, nw + 16, 17);
        r.RoundFill(tag, Theme.WithAlpha(nc, 0.22f), 7f);
        r.Disc(new Vector2(tag.X + 8, tag.Center.Y), 3f, nc);
        r.Text(nickF, nick, new Vector2(tag.X + 15, headY + 1), Theme.Mix(nc, Theme.Text, 0.45f));

        // channel + time on the right
        string meta = b.M.Channel + (b.M.At == default ? "" : "  " + b.M.At.ToString("HH:mm"));
        r.TextRight(metaF, meta, x + w - 4, headY + 3, Theme.TextFaint);

        // bubble body
        float bubTop = headY + 20;
        float lineH = bodyF.MeasureString("Mg").Y + 2f;
        float bodyH = b.Lines.Length * lineH + pad * 2;
        var bub = new RectF(x + 6, bubTop, w - 12, bodyH);
        r.RoundFill(bub, Theme.WithAlpha(nc, 0.10f), 10f);
        r.RoundOutline(bub, Theme.WithAlpha(nc, 0.30f), 10f);
        // little tail
        r.Disc(new Vector2(bub.X + 14, bub.Y - 1), 3.2f, Theme.Mix(Theme.PanelHi, nc, 0.10f));
        float ly = bubTop + pad;
        foreach (var line in b.Lines)
        {
            r.Text(bodyF, line, new Vector2(bub.X + pad + 2, ly), Theme.Text);
            ly += lineH;
        }

        // "ran a workflow" chip
        if (b.Run != null)
        {
            string label = b.Run.Icon + " ran " + b.Run.Trigger;
            var cf = r.Fonts.Get(FontKind.SansBold, 11);
            float cw = cf.MeasureString(label).X + 18;
            var chip = new RectF(x + 8, bub.Bottom + 4, MathF.Min(cw, w - 16), 18);
            bool hot = chip.Contains(In.Mouse);
            r.RoundFill(chip, hot ? Theme.Lime : Theme.WithAlpha(Theme.Lime, 0.30f), 8f);
            r.Text(cf, r.Ellipsize(cf, label, chip.W - 14), new Vector2(chip.X + 8, chip.Y + 2), hot ? Theme.TextInk : Theme.Mix(Theme.Lime, Theme.Text, 0.5f));
            jumpHit.Add((chip, b.M.Msgid));
        }
    }

    private void DrawIrcWindowFooter(Renderer r, RectF win)
    {
        r.Begin();
        var f = r.Fonts.Get(FontKind.Mono, 11);
        r.Text(f, $"{_ircBubbles.Count} message(s) in view · scroll to replay history",
            new Vector2(win.X + 18, win.Bottom - 28), Theme.TextFaint);
        r.End();
    }

    private void DrawIrcWindowResizeGrip(Renderer r, RectF win)
    {
        var grip = new RectF(win.Right - 22, win.Bottom - 22, 16, 16);
        r.Begin();
        for (int i = 0; i < 3; i++)
            r.Line(new Vector2(grip.Right - i * 4, grip.Bottom), new Vector2(grip.Right, grip.Bottom - i * 4),
                Theme.WithAlpha(Theme.Edge, 0.8f), 1.5f);
        r.End();
    }

    private void HandleIrcWindowInput()
    {
        var win = _ircWin;
        var head = new RectF(win.X, win.Y, win.W - 46, 46);          // title bar (minus close button)
        var grip = new RectF(win.Right - 24, win.Bottom - 24, 22, 22);

        if (In.LeftPressed && !_ircWinJustOpened)
        {
            if (grip.Contains(In.Mouse)) _ircWinResizing = true;
            else if (head.Contains(In.Mouse)) { _ircWinDragging = true; _ircWinDragOff = In.Mouse - new Vector2(win.X, win.Y); }
            else if (!win.Contains(In.Mouse)) { _ircWinOpen = false; return; }   // click-away closes
        }

        if (_ircWinDragging)
        {
            if (In.LeftDown)
            {
                float nx = Math.Clamp(In.Mouse.X - _ircWinDragOff.X, 0, _vw - win.W);
                float ny = Math.Clamp(In.Mouse.Y - _ircWinDragOff.Y, Layout.TopH, _vh - 44);
                _ircWin = new RectF(nx, ny, win.W, win.H);
            }
            else _ircWinDragging = false;
        }
        else if (_ircWinResizing)
        {
            if (In.LeftDown)
            {
                float nw = Math.Clamp(In.Mouse.X - win.X + 12, 380, _vw - win.X - 8);
                float nh = Math.Clamp(In.Mouse.Y - win.Y + 12, 300, _vh - win.Y - 8);
                _ircWin = new RectF(win.X, win.Y, nw, nh);
            }
            else _ircWinResizing = false;
        }
    }

    // Rebuilds the laid-out bubble list when the ring / history / width changes (cheap signature check).
    private void RebuildIrcBubbles(Renderer r, float bubbleW)
    {
        var ring = Bot.Runtime.RecentMessages(200);
        long histRev = Bot.Runtime.HistoryRevision;
        string sig = $"{ring.Count}|{(ring.Count > 0 ? ring[^1].Msgid + ring[^1].Text.Length : "")}|{(int)bubbleW}|{histRev}";
        if (sig == _ircCacheSig) return;
        _ircCacheSig = sig;

        // index runs by the message id that triggered them (newest wins)
        var byMsg = new Dictionary<string, RunRecord>();
        foreach (var run in Bot.Runtime.History())
            if (run.Msgid.Length > 0) byMsg[run.Msgid] = run;

        var bodyF = r.Fonts.Get(FontKind.Sans, 13);
        float lineH = bodyF.MeasureString("Mg").Y + 2f;
        _ircBubbles.Clear();
        float total = 0;
        foreach (var m in ring)
        {
            var lines = WrapText(bodyF, m.Text.Length > 0 ? m.Text : "(empty)", bubbleW).ToArray();
            RunRecord? run = m.Msgid.Length > 0 && byMsg.TryGetValue(m.Msgid, out var rr) ? rr : null;
            float h = 24 + lines.Length * lineH + 18 + 6 + (run != null ? 22 : 0);
            _ircBubbles.Add(new IrcBubble { M = m, Lines = lines, H = h, Run = run });
            total += h;
        }
        _ircTotalH = total;
    }

    private static List<string> WrapText(DynamicSpriteFont f, string text, float maxW)
    {
        var lines = new List<string>();
        foreach (var hard in text.Split('\n'))
        {
            var cur = new StringBuilder();
            foreach (var word in hard.Split(' '))
            {
                string trial = cur.Length == 0 ? word : cur + " " + word;
                if (cur.Length > 0 && f.MeasureString(trial).X > maxW)
                { lines.Add(cur.ToString()); cur.Clear(); cur.Append(word); }
                else { if (cur.Length > 0) cur.Append(' '); cur.Append(word); }
            }
            lines.Add(cur.ToString());
        }
        return lines;
    }

    // Opens the History modal pre-selected to the run a given message triggered.
    private void OpenHistoryForMsg(string msgid)
    {
        OpenHistory();
        if (msgid.Length == 0) return;
        for (int i = 0; i < _historyRuns.Count; i++)
            if (_historyRuns[i].Msgid == msgid) { _historySel = i; _historyDetailScroll = 0; break; }
    }
}
