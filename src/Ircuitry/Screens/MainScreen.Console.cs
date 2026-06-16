using System;
using System.Collections.Generic;
using System.Text;
using FontStashSharp;
using Microsoft.Xna.Framework;
using Ircuitry.Core;
using Ircuitry.Irc;
using Ircuitry.Render;
using Ircuitry.Runtime;

namespace Ircuitry.Screens;

// The Event Console: a cozy PictoChat-styled feed that fully parses every IRC line - tags, source
// nick/user/host, command (or RPL_ numeric name), subcommands and params - and is scrollable through
// the whole history and draggable-resizable by its top edge.
public sealed partial class MainScreen
{
    private long _consoleCacheRev = -1;
    private float _consoleCacheW = -1;
    private readonly List<ConsoleRow> _consoleRows = new();
    private float _consoleTotalH;

    private struct ConsoleRow { public LogEntry E; public IrcMessage? M; public float H; }

    private const float RowPadV = 5f;
    private const float MainLineH = 17f;
    private const float TagsLineH = 14f;

    private void ConsoleHeaderStats(Renderer r, RectF p)
    {
        // drawn inside the panel-chrome batch (no Begin/End here)
        var f = r.Fonts.Get(FontKind.Mono, 12);
        r.TextRight(f, $"MSG {Bot.Runtime.MessagesSeen}   ACT {Bot.Runtime.ActionsFired}", p.Right - 132, p.Y + 12, Theme.TextFaint);
    }

    private void DrawConsole(Renderer r)
    {
        var p = _l.Console;
        ConsoleResizeHandle(r, p);

        var content = new RectF(p.X + 8, p.Y + Hud.HeaderH + 2, p.W - 16, p.H - Hud.HeaderH - 8);
        RebuildConsoleRows(r, content.W);

        float maxScroll = MathF.Max(0, _consoleTotalH - content.H);
        if (!Modal && content.Contains(In.Mouse) && In.ScrollDelta != 0)
            _consoleScroll += In.ScrollDelta * 0.5f;     // wheel up reveals older history
        _consoleScroll = Math.Clamp(_consoleScroll, 0, maxScroll);

        r.Begin(BlendMode.Alpha, content.ToRectangle());
        r.RoundFill(content, Theme.PanelHi, 10f);
        ConsolePaperLines(r, content);

        // rows are bottom-anchored (newest at the bottom, terminal-style); scroll lifts the view into history
        float y = content.Bottom - _consoleTotalH + _consoleScroll;
        foreach (var row in _consoleRows)
        {
            if (y + row.H >= content.Y - 2 && y <= content.Bottom + 2)
                DrawLogRow(r, row, content.X + 3, y, content.W - 10);
            y += row.H;
        }
        r.End();

        if (maxScroll > 1) ConsoleScrollbar(r, content, maxScroll);
    }

    // notebook-paper hairlines behind the (translucent) message cards
    private void ConsolePaperLines(Renderer r, RectF c)
    {
        var dot = Theme.WithAlpha(Theme.Hairline, 0.45f);
        for (float gy = c.Y + 22; gy < c.Bottom - 4; gy += 26)
            r.HLine(c.X + 8, c.Right - 8, MathF.Round(gy), dot, 1f);
    }

    private void RebuildConsoleRows(Renderer r, float w)
    {
        long rev = Bot.Log.Revision;
        if (rev == _consoleCacheRev && Math.Abs(w - _consoleCacheW) < 1f) return;
        _consoleCacheRev = rev; _consoleCacheW = w;

        _consoleRows.Clear();
        float total = 0;
        foreach (var e in Bot.Log.Tail(1000))
        {
            IrcMessage? m = null;
            if ((e.Level == LogLevel.In || e.Level == LogLevel.Out) && LooksLikeIrc(e.Text))
            {
                try { m = IrcParser.Parse(e.Text); } catch { m = null; }
                if (m != null && m.Command.Length == 0) m = null;
            }
            float h = RowPadV * 2 + MainLineH + (m != null && m.Tags.Count > 0 ? TagsLineH : 0);
            _consoleRows.Add(new ConsoleRow { E = e, M = m, H = h });
            total += h;
        }
        _consoleTotalH = total;
    }

    private static bool LooksLikeIrc(string s)
    {
        if (s.Length == 0) return false;
        if (s[0] == '@' || s[0] == ':') return true;
        int sp = s.IndexOf(' ');
        string cmd = sp < 0 ? s : s[..sp];
        return cmd.Length > 0 && (char.IsUpper(cmd[0]) || char.IsDigit(cmd[0]));
    }

    private static bool IsSubcmdCarrier(string cmd) =>
        cmd.Equals("CAP", StringComparison.OrdinalIgnoreCase) ||
        cmd.Equals("BATCH", StringComparison.OrdinalIgnoreCase) ||
        cmd.Equals("AUTHENTICATE", StringComparison.OrdinalIgnoreCase) ||
        cmd.Equals("CHATHISTORY", StringComparison.OrdinalIgnoreCase) ||
        cmd.Equals("METADATA", StringComparison.OrdinalIgnoreCase);

    private static Color IrcAccent(IrcMessage m)
    {
        if (m.IsNumeric(out int n)) return IrcNumerics.IsError(n) ? Theme.Alert : Theme.Amber;
        return m.Command.ToUpperInvariant() switch
        {
            "PRIVMSG" => Theme.Teal,
            "NOTICE" => Theme.Sky,
            "JOIN" or "PART" or "QUIT" or "NICK" => Theme.Lime,
            "MODE" or "TOPIC" or "KICK" => Theme.Violet,
            "CAP" or "AUTHENTICATE" or "BATCH" or "TAGMSG" => Theme.Berry,
            "PING" or "PONG" => Theme.Idle,
            _ => Theme.Cyan,
        };
    }

    private void DrawLogRow(Renderer r, ConsoleRow row, float x, float y, float w)
    {
        var e = row.E; var m = row.M;
        Color accent = m != null ? IrcAccent(m) : LogColors.Of(e.Level);
        var card = new RectF(x, y + 2, w, row.H - 4);
        r.RoundFill(card, Theme.WithAlpha(accent, 0.08f), 7f);
        r.RoundFill(new RectF(card.X, card.Y + 2, 3f, card.H - 4), accent, 1.5f);

        var time = r.Fonts.Get(FontKind.Mono, 10);
        var mono = r.Fonts.Get(FontKind.Mono, 12);
        var monoB = r.Fonts.Get(FontKind.MonoBold, 12);
        float ty = card.Y + RowPadV;
        float cx = card.X + 10;
        float maxX = card.Right - 8;

        r.Text(time, e.Time.ToString("HH:mm:ss"), new Vector2(cx, ty + 2), Theme.TextFaint);
        cx += 54;
        if (e.Server.Length > 0) cx = Chip(r, time, e.Server, cx, ty, Theme.Sky);

        if (m != null) DrawIrcMain(r, m, e.Level, ref cx, ty, maxX, mono, monoB);
        else
        {
            cx = Chip(r, r.Fonts.Get(FontKind.SansBold, 10), LogColors.Tag(e.Level), cx, ty, accent);
            string txt = r.Ellipsize(mono, e.Text, MathF.Max(10, maxX - cx));
            r.Text(mono, txt, new Vector2(cx, ty), e.Level == LogLevel.In ? Theme.TextDim : accent);
        }

        if (m != null && m.Tags.Count > 0)
            DrawTagsLine(r, m, card.X + 14, card.Y + RowPadV + MainLineH, card.Right - 10);
    }

    private void DrawIrcMain(Renderer r, IrcMessage m, LogLevel lvl, ref float cx, float ty, float maxX,
        DynamicSpriteFont mono, DynamicSpriteFont monoB)
    {
        // a tiny direction dot (cyan in / honey out) where the source nick/server starts
        r.Disc(new Vector2(cx + 2, ty + MainLineH / 2 - 2), 2.4f, lvl == LogLevel.Out ? Theme.Amber : Theme.Cyan);
        cx += 9;

        if (!string.IsNullOrEmpty(m.Nick))
        {
            cx = Seg(r, monoB, m.Nick!, cx, ty, Theme.Cyan, maxX);
            string uh = (m.User ?? "") + (string.IsNullOrEmpty(m.Host) ? "" : "@" + m.Host);
            if (uh.Length > 0) cx = Seg(r, mono, uh, cx, ty, Theme.TextFaint, maxX);
        }
        else if (!string.IsNullOrEmpty(m.Source))
            cx = Seg(r, mono, m.Source!, cx, ty, Theme.TextFaint, maxX);

        bool numeric = m.IsNumeric(out int n);
        if (numeric)
        {
            cx = Seg(r, mono, n.ToString("000"), cx, ty, Theme.TextFaint, maxX);
            string name = IrcNumerics.Name(n) ?? "";
            if (name.Length > 0) cx = Seg(r, monoB, name, cx, ty, IrcNumerics.IsError(n) ? Theme.Alert : Theme.AmberDim, maxX);
        }
        else
        {
            Color cc = lvl == LogLevel.Out ? Theme.CyanBright : Theme.Teal;
            cx = Seg(r, monoB, m.Command.ToUpperInvariant(), cx, ty, cc, maxX);
            if (IsSubcmdCarrier(m.Command) && m.Params.Count > 0)
                cx = Seg(r, monoB, m.P(0), cx, ty, Theme.Violet, maxX);
        }

        int start = (!numeric && IsSubcmdCarrier(m.Command)) ? 1 : 0;
        for (int i = start; i < m.Params.Count && cx < maxX; i++)
        {
            string par = m.P(i);
            bool last = i == m.Params.Count - 1;
            Color pc = (par.StartsWith("#") || par.StartsWith("&")) ? Theme.Lime : (last ? Theme.Text : Theme.TextDim);
            cx = Seg(r, mono, par, cx, ty, pc, maxX, ellipsize: last);
        }
    }

    private void DrawTagsLine(Renderer r, IrcMessage m, float x, float y, float maxX)
    {
        var f = r.Fonts.Get(FontKind.Mono, 10);
        r.Text(r.Fonts.Get(FontKind.Sans, 10), "🏷", new Vector2(x, y), Theme.AmberDim);
        float cx = x + 16;
        bool first = true;
        foreach (var kv in m.Tags)
        {
            if (cx >= maxX) break;
            if (!first) cx = Seg(r, f, "·", cx, y, Theme.TextFaint, maxX);
            first = false;
            cx = Seg(r, f, kv.Key, cx, y, Theme.AmberDim, maxX, gap: kv.Value.Length > 0 ? 1f : 7f);
            if (kv.Value.Length > 0)
            {
                cx = Seg(r, f, "=", cx, y, Theme.TextFaint, maxX, gap: 1f);
                cx = Seg(r, f, kv.Value, cx, y, Theme.TextDim, maxX);
            }
        }
    }

    // Draws a text segment at cx, returns the advanced cursor. Ellipsizes to fit when ellipsize=true.
    private float Seg(Renderer r, DynamicSpriteFont f, string s, float cx, float y, Color col, float maxX,
        bool ellipsize = false, float gap = 7f)
    {
        if (string.IsNullOrEmpty(s) || cx >= maxX) return cx;
        string draw = ellipsize ? r.Ellipsize(f, s, MathF.Max(8, maxX - cx)) : s;
        r.Text(f, draw, new Vector2(MathF.Round(cx), MathF.Round(y)), col);
        return cx + f.MeasureString(draw).X + gap;
    }

    private float Chip(Renderer r, DynamicSpriteFont f, string s, float cx, float y, Color col)
    {
        float tw = f.MeasureString(s).X;
        var box = new RectF(cx, y, tw + 10, 15);
        r.RoundFill(box, Theme.WithAlpha(col, 0.20f), 4f);
        r.Text(f, s, new Vector2(cx + 5, y + 1), Theme.Mix(col, Theme.Text, 0.4f));
        return cx + box.W + 6;
    }

    private void ConsoleScrollbar(Renderer r, RectF content, float maxScroll)
    {
        r.Begin();
        float trackX = content.Right - 4;
        float frac = content.H / _consoleTotalH;                          // visible fraction
        float thumbH = MathF.Max(24, content.H * frac);
        float t = maxScroll <= 0 ? 1f : 1f - _consoleScroll / maxScroll;   // 1 = bottom
        float thumbY = content.Y + (content.H - thumbH) * t;
        r.RoundFill(new RectF(trackX, thumbY, 3.5f, thumbH), Theme.WithAlpha(Theme.Edge, 0.7f), 2f);
        r.End();
    }

    private void ConsoleResizeHandle(Renderer r, RectF p)
    {
        float cx = p.X + p.W / 2f;
        var grab = new RectF(cx - 28, p.Y - 5, 56, 12);
        var hit = grab.Inflate(8, 8);
        bool hot = _consoleResizing || (!Modal && hit.Contains(In.Mouse));

        if (!Modal)
        {
            if (In.LeftPressed && hit.Contains(In.Mouse))
            { _consoleResizing = true; _consoleResizeStartH = _l.Console.H; _consoleResizeStartY = In.Mouse.Y; }
            if (_consoleResizing)
            {
                if (In.LeftDown)
                    _consoleH = Math.Clamp(_consoleResizeStartH + (_consoleResizeStartY - In.Mouse.Y), 120f, _vh * 0.72f);
                else _consoleResizing = false;
            }
        }

        r.Begin();
        r.RoundFill(grab, hot ? Theme.Lime : Theme.Edge, 6f);
        for (int i = -1; i <= 1; i++) r.Disc(new Vector2(cx + i * 8, p.Y + 1), 1.7f, Theme.PanelHi);
        r.End();
    }
}
