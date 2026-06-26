using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FontStashSharp;
using Microsoft.Xna.Framework;
using Ircuitry.Core;
using Ircuitry.Render;
using Ircuitry.Runtime;

namespace Ircuitry.Screens;

// "What ircuitry has seen": a read-only, state-accurate IRC client in cosy Animal-Crossing / DS dress.
// Left: the channels we're in (+ a Status view). Centre: the selected channel's topic + message replay,
// or a human-language Status narration. Right: the channel's members. All from the live IrcSessionState,
// so it shows exactly what the bot is tracking. Draggable by the title bar, resizable from the corner.
public sealed partial class MainScreen
{
    private bool _ircWinOpen, _ircWinJustOpened;
    private RectF _ircWin = new(120, 80, 720, 540);
    private bool _ircWinDragging, _ircWinResizing;
    private Vector2 _ircWinDragOff;

    private const string StatusTab = "status";   // sentinel for the Status (narration) view
    private string _ircSel = StatusTab;
    private float _ircMsgScroll, _ircMemScroll, _ircNarrScroll;

    private struct IrcBubble { public RecentMsg M; public string[] Lines; public float H; public RunRecord? Run; }
    private readonly List<IrcBubble> _ircBubbles = new();
    private float _ircMsgTotalH;
    private string _ircCacheSig = "";

    // For a server-hosted bot the IRC connection runs on the server, not in this desktop's runtime, so the viewer
    // polls a snapshot of its session over the control link. A unified IrcView lets the panes render identically
    // whether the bot is local or remote.
    private Ircuitry.App.Server.ControlClient.RemoteIrc? _remoteIrc;
    private DateTime _remoteIrcNext;

    private sealed class IrcView
    {
        public bool Live;
        public string Nick = "", Network = "", ChanTypes = "#&";
        public List<string> Caps = new();
        public sealed class VChan { public string Name = "", Topic = ""; public List<(string nick, string prefix)> Members = new(); }
        public List<VChan> Channels = new();
        public List<(DateTime at, string text)> Notes = new();
        public List<RecentMsg> Messages = new();
        public bool IsChannel(string t) => t.Length > 0 && ChanTypes.IndexOf(t[0]) >= 0;
        public VChan? Find(string ch) => Channels.FirstOrDefault(c => c.Name.Equals(ch, StringComparison.OrdinalIgnoreCase));
        public List<string> ChannelNames() => Channels.Select(c => c.Name).ToList();
        public string Topic(string ch) => Find(ch)?.Topic ?? "";
        public int MemberCount(string ch) => Find(ch)?.Members.Count ?? 0;
        public List<(string nick, string prefix)> Members(string ch) => Find(ch)?.Members ?? new();
    }

    private void PollRemoteIrc()
    {
        if (!Bot.IsRemote || Bot.Remote is not { } rc) return;
        if (DateTime.UtcNow < _remoteIrcNext) return;
        _remoteIrcNext = DateTime.UtcNow.AddSeconds(1.5);
        rc.GetIrcState(Bot.Name, s => _remoteIrc = s);    // callback runs on the UI thread via the remote Pump
    }

    private IrcView BuildIrcView()
    {
        var v = new IrcView();
        if (Bot.IsRemote)
        {
            if (_remoteIrc is { } s)
            {
                v.Live = s.Connected; v.Nick = s.Nick; v.Network = s.Network; v.ChanTypes = s.ChanTypes; v.Caps = s.Caps;
                foreach (var ch in s.Channels) v.Channels.Add(new IrcView.VChan { Name = ch.Name, Topic = ch.Topic, Members = ch.Members });
                v.Notes = s.Notes; v.Messages = s.Messages;
            }
        }
        else if (Bot.Runtime.PrimaryConn is { } conn)
        {
            var sess = conn.Session;
            v.Live = conn.Running && conn.State == Ircuitry.Irc.IrcState.Connected;
            v.Nick = conn.CurrentNick; v.Network = sess.Network; v.ChanTypes = sess.Isupport.ChanTypes;
            v.Caps = conn.EnabledCaps.ToList();
            foreach (var ch in sess.Channels()) v.Channels.Add(new IrcView.VChan { Name = ch, Topic = sess.Topic(ch), Members = sess.Members(ch) });
            v.Notes = sess.RecentNotes(60).Select(n => (n.At, n.Text)).ToList();
            v.Messages = Bot.Runtime.RecentMessages(200).ToList();
        }
        return v;
    }

    private static readonly Color[] NickPalette =
        { Theme.Cyan, Theme.Amber, Theme.Magenta, Theme.Violet, Theme.Lime, Theme.Berry, Theme.Sky, Theme.Teal };

    private static Color NickColor(string nick)
    {
        int h = 0; foreach (char c in nick) h = h * 31 + c;
        return NickPalette[((h % NickPalette.Length) + NickPalette.Length) % NickPalette.Length];
    }

    public void OpenIrcWindow()
    {
        float w = MathF.Min(760, _vw - 60), h = MathF.Min(560, _vh - 100);
        _ircWin = new RectF((_vw - w) / 2f, (_vh - h) / 2f, w, h);
        _ircSel = StatusTab; _ircMsgScroll = _ircMemScroll = _ircNarrScroll = 0; _ircCacheSig = "";
        _remoteIrc = null; _remoteIrcNext = DateTime.MinValue;   // refetch the remote session immediately
        _ircWinOpen = true; _ircWinJustOpened = true;
    }

    public void DebugOpenIrcWindow() { _l = DockLayout(); OpenIrcWindow(); }
    public bool DebugAutoIrcChannel;   // screenshot helper: auto-select the first channel once it exists

    private void ConsoleViewButton(Renderer r)
    {
        var p = _l.Console;
        var btn = new RectF(p.Right - 156, p.Y + 7, 144, 24);
        if (_ui.Button("console.ircview", btn, Ircuitry.Core.Icons.Glyph("television") + "  Bot's-eye view", Theme.Teal)) OpenIrcWindow();
    }

    private void DrawIrcWindow(Renderer r, Clock clock)
    {
        HandleIrcWindowInput();
        var win = _ircWin;
        PollRemoteIrc();
        var v = BuildIrcView();
        if (DebugAutoIrcChannel && _ircSel == StatusTab && v.ChannelNames().FirstOrDefault() is { } dc) _ircSel = dc;

        // ---- shell ----
        r.Begin();
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.32f));
        r.RoundFill(win.Offset(0, 6), Theme.WithAlpha(Color.Black, 0.20f), 20f);
        r.RoundFill(win, Theme.Void, 20f);
        r.RoundOutline(win, Theme.Edge, 20f);
        r.End();

        DrawIrcHeader(r, win, v);

        // ---- three panes ----
        float bodyTop = win.Y + 70, bodyBot = win.Bottom - 30;
        float leftW = 156, rightW = 138;
        var left = new RectF(win.X + 12, bodyTop, leftW, bodyBot - bodyTop);
        var right = new RectF(win.Right - 12 - rightW, bodyTop, rightW, bodyBot - bodyTop);
        var center = new RectF(left.Right + 8, bodyTop, right.Left - 8 - (left.Right + 8), bodyBot - bodyTop);

        DrawIrcChannelList(r, left, v);
        bool status = _ircSel == StatusTab;
        if (status) DrawIrcStatusView(r, center, v);
        else DrawIrcChannelView(r, center, v);
        DrawIrcMembers(r, right, v, status);

        DrawIrcFooter(r, win, v);
        DrawIrcResizeGrip(r, win);

        _ircWinJustOpened = false;
    }

    private void DrawIrcHeader(Renderer r, RectF win, IrcView v)
    {
        r.Begin();
        r.Text(r.Fonts.Get(FontKind.Display, 18), Ircuitry.Core.Icons.Glyph("television") + "  What ircuitry has seen", new Vector2(win.X + 18, win.Y + 11), Theme.Text);

        bool live = v.Live;
        string nick = v.Nick;
        string net = v.Network;
        string status = nick.Length == 0 ? "not connected yet"
            : "I'm " + nick + (net.Length > 0 ? "  ·  on " + net : (live ? "  ·  connected" : "  ·  offline"));
        var sf = r.Fonts.Get(FontKind.Mono, 12);
        if (live) r.Disc(new Vector2(win.X + 22, win.Y + 44), 4.2f, Theme.Ok);
        else r.Disc(new Vector2(win.X + 22, win.Y + 44), 4.2f, Theme.Idle);
        r.Text(sf, status, new Vector2(win.X + 32, win.Y + 38), Theme.TextDim);

        var closeR = new RectF(win.Right - 40, win.Y + 12, 26, 24);
        bool hot = closeR.Contains(In.Mouse);
        r.RoundFill(closeR, hot ? Theme.Alert : Theme.PanelLo, 7f);
        r.Text(r.Fonts.Get(FontKind.SansBold, 14), "×", new Vector2(closeR.X + 8, closeR.Y + 3), hot ? Theme.TextInk : Theme.TextDim);
        r.End();
        if (In.LeftPressed && hot && !_ircWinJustOpened) _ircWinOpen = false;
    }

    private void DrawIrcChannelList(Renderer r, RectF box, IrcView v)
    {
        r.Begin();
        r.RoundFill(box, Theme.PanelLo, 12f);
        r.RoundOutline(box, Theme.Hairline, 12f);
        r.Text(r.Fonts.Get(FontKind.SansBold, 11), "WINDOWS", new Vector2(box.X + 12, box.Y + 8), Theme.TextFaint);
        r.End();

        var f = r.Fonts.Get(FontKind.Sans, 13);
        float y = box.Y + 28;
        var items = new List<(string id, string label, string sub, bool pm)> { (StatusTab, Ircuitry.Core.Icons.Glyph("clipboard") + "  Status", "", false) };
        items.AddRange(IrcWindows(v));

        r.Begin(BlendMode.Alpha, new RectF(box.X, box.Y + 24, box.W, box.H - 28).ToRectangle());
        foreach (var (id, label, sub, _) in items)
        {
            var row = new RectF(box.X + 6, y, box.W - 12, 26);
            bool sel = id == _ircSel;
            bool hot = row.Contains(In.Mouse);
            if (sel) r.RoundFill(row, Theme.WithAlpha(Theme.Teal, 0.30f), 7f);
            else if (hot) r.RoundFill(row, Theme.WithAlpha(Theme.Edge, 0.20f), 7f);
            r.Text(f, r.Ellipsize(f, label, row.W - (sub.Length > 0 ? 34 : 12)), new Vector2(row.X + 8, row.Y + 5), sel ? Theme.Text : Theme.TextDim);
            if (sub.Length > 0) r.TextRight(r.Fonts.Get(FontKind.Mono, 10), sub, row.Right - 8, row.Y + 7, Theme.TextFaint);
            if (In.LeftPressed && hot && !_ircWinJustOpened) { _ircSel = id; _ircMsgScroll = _ircMemScroll = 0; }
            y += 28;
        }
        r.End();
    }

    // Every window worth showing: the channels we're in, plus any other target we've actually seen a message
    // from. That second part is what surfaces private-message queries (a PM's "channel" is the sender's nick)
    // and any one-off window - so the viewer shows everywhere something happened, not just joined channels.
    private List<(string id, string label, string sub, bool pm)> IrcWindows(IrcView v)
    {
        var list = new List<(string id, string label, string sub, bool pm)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ch in v.ChannelNames())
            if (seen.Add(ch)) list.Add((ch, Ircuitry.Core.Icons.Glyph("hash") + "  " + ch, v.MemberCount(ch).ToString(), false));
        foreach (var m in v.Messages)
        {
            string w = m.Channel;
            if (w.Length == 0 || !seen.Add(w)) continue;
            bool pm = !v.IsChannel(w);
            list.Add((w, (pm ? Ircuitry.Core.Icons.Glyph("chat-circle") : Ircuitry.Core.Icons.Glyph("hash")) + "  " + w, pm ? "dm" : "", pm));
        }
        return list;
    }

    private void DrawIrcChannelView(Renderer r, RectF box, IrcView v)
    {
        string ch = _ircSel;
        bool pm = !v.IsChannel(ch);
        string topic = v.Topic(ch);
        // topic bar
        var topicBar = new RectF(box.X, box.Y, box.W, 34);
        r.Begin();
        r.RoundFill(topicBar, Theme.WithAlpha(Theme.Teal, 0.14f), 10f);
        r.Text(r.Fonts.Get(FontKind.SansBold, 13), ch, new Vector2(topicBar.X + 12, topicBar.Y + 4), Theme.Mix(Theme.Teal, Theme.Text, 0.5f));
        r.Text(r.Fonts.Get(FontKind.Sans, 12), r.Ellipsize(r.Fonts.Get(FontKind.Sans, 12), topic.Length > 0 ? topic : (pm ? "private messages" : "(no topic set)"), box.W - 24), new Vector2(topicBar.X + 12, topicBar.Y + 18), Theme.TextDim);
        r.End();

        var content = new RectF(box.X, box.Y + 40, box.W, box.H - 40);
        RebuildIrcBubbles(r, content.W - 22, ch, v);

        float maxScroll = MathF.Max(0, _ircMsgTotalH - content.H);
        if (content.Contains(In.Mouse) && In.ScrollDelta != 0) _ircMsgScroll += In.ScrollDelta * 0.5f;
        _ircMsgScroll = Math.Clamp(_ircMsgScroll, 0, maxScroll);

        r.Begin(BlendMode.Alpha, content.ToRectangle());
        if (_ircBubbles.Count == 0)
            r.Text(r.Fonts.Get(FontKind.Sans, 13), "No messages seen here yet.", new Vector2(content.X + 4, content.Y + 8), Theme.TextFaint);
        float y = content.Bottom - _ircMsgTotalH + _ircMsgScroll;
        var jumpHit = new List<(RectF box, string msgid)>();
        foreach (var b in _ircBubbles)
        {
            if (y + b.H >= content.Y - 4 && y <= content.Bottom + 4) DrawBubble(r, b, content.X, y, content.W, jumpHit);
            y += b.H;
        }
        r.End();

        if (In.LeftPressed && !_ircWinJustOpened)
            foreach (var (jb, msgid) in jumpHit)
                if (jb.Contains(In.Mouse)) { _ircWinOpen = false; OpenHistoryForMsg(msgid); break; }
    }

    private void DrawIrcStatusView(Renderer r, RectF box, IrcView v)
    {
        var headBar = new RectF(box.X, box.Y, box.W, 34);
        r.Begin();
        r.RoundFill(headBar, Theme.WithAlpha(Theme.Amber, 0.14f), 10f);
        r.Text(r.Fonts.Get(FontKind.SansBold, 13), "What's happening", new Vector2(headBar.X + 12, headBar.Y + 8), Theme.Mix(Theme.Amber, Theme.Text, 0.5f));
        r.End();

        // assemble human-language lines: summary first, then the narration feed (newest last)
        var lines = new List<(string text, bool strong)>();
        string nick = v.Nick;
        lines.Add(("My name is " + (nick.Length > 0 ? nick : "(not set yet)"), true));
        string net = v.Network;
        if (net.Length > 0) lines.Add(("I'm connected to " + net, true));
        else if (v.Live) lines.Add(("I'm connected", true));
        else lines.Add(("I'm not connected right now", true));
        var chans = v.ChannelNames();
        if (chans.Count > 0) lines.Add(("I'm in " + string.Join(", ", chans), true));
        if (v.Caps.Count > 0) lines.Add(("I can do: " + string.Join(", ", v.Caps), false));
        lines.Add(("", false));
        foreach (var n in v.Notes) lines.Add((n.at.ToString("HH:mm") + "   " + n.text, false));

        var content = new RectF(box.X, box.Y + 40, box.W, box.H - 40);
        var bodyF = r.Fonts.Get(FontKind.Sans, 13);
        var strongF = r.Fonts.Get(FontKind.SansBold, 13);
        float lineH = 24f;
        float total = lines.Count * lineH;
        float maxScroll = MathF.Max(0, total - content.H);
        if (content.Contains(In.Mouse) && In.ScrollDelta != 0) _ircNarrScroll -= In.ScrollDelta * 0.5f;
        _ircNarrScroll = Math.Clamp(_ircNarrScroll, 0, maxScroll);

        r.Begin(BlendMode.Alpha, content.ToRectangle());
        float y = content.Y - _ircNarrScroll;   // top-anchored: summary first, narration below
        foreach (var (text, strong) in lines)
        {
            if (text.Length > 0 && y + lineH >= content.Y - 2 && y <= content.Bottom + 2)
            {
                if (strong) r.Disc(new Vector2(content.X + 6, y + 10), 3f, Theme.Amber);
                r.Text(strong ? strongF : bodyF, r.Ellipsize(strong ? strongF : bodyF, text, content.W - 18),
                    new Vector2(content.X + 14, y + 2), strong ? Theme.Text : Theme.TextDim);
            }
            y += lineH;
        }
        r.End();
    }

    private void DrawIrcMembers(Renderer r, RectF box, IrcView v, bool status)
    {
        r.Begin();
        r.RoundFill(box, Theme.PanelLo, 12f);
        r.RoundOutline(box, Theme.Hairline, 12f);
        var members = !status ? v.Members(_ircSel) : new();
        r.Text(r.Fonts.Get(FontKind.SansBold, 11), status ? "MEMBERS" : $"MEMBERS · {members.Count}", new Vector2(box.X + 12, box.Y + 8), Theme.TextFaint);
        r.End();

        var content = new RectF(box.X, box.Y + 24, box.W, box.H - 28);
        float rowH = 22f;
        float total = members.Count * rowH;
        float maxScroll = MathF.Max(0, total - content.H);
        if (content.Contains(In.Mouse) && In.ScrollDelta != 0) _ircMemScroll += In.ScrollDelta * 0.5f;
        _ircMemScroll = Math.Clamp(_ircMemScroll, 0, maxScroll);

        var f = r.Fonts.Get(FontKind.Sans, 12);
        r.Begin(BlendMode.Alpha, content.ToRectangle());
        float y = content.Y + 4 - _ircMemScroll;
        foreach (var (mnick, prefix) in members)
        {
            if (y + rowH >= content.Y - 2 && y <= content.Bottom + 2)
            {
                Color pc = prefix.Contains('~') || prefix.Contains('&') || prefix.Contains('@') ? Theme.Amber
                         : prefix.Contains('%') || prefix.Contains('+') ? Theme.Lime : Theme.Idle;
                if (prefix.Length > 0) r.Text(r.Fonts.Get(FontKind.MonoBold, 12), prefix[..1], new Vector2(box.X + 10, y), pc);
                r.Disc(new Vector2(box.X + 22, y + 7), 2.6f, NickColor(mnick));
                r.Text(f, r.Ellipsize(f, mnick, box.W - 40), new Vector2(box.X + 30, y), Theme.TextDim);
            }
            y += rowH;
        }
        r.End();
    }

    private void DrawBubble(Renderer r, IrcBubble b, float x, float y, float w, List<(RectF, string)> jumpHit)
    {
        var nickF = r.Fonts.Get(FontKind.SansBold, 12);
        var bodyF = r.Fonts.Get(FontKind.Sans, 13);
        Color nc = NickColor(b.M.Nick.Length > 0 ? b.M.Nick : "server");
        float pad = 9f, headY = y + 4;

        string nick = b.M.Nick.Length > 0 ? b.M.Nick : "·";
        float nw = nickF.MeasureString(Ircuitry.Render.Renderer.SafeText(nick)).X;
        var tag = new RectF(x + 2, headY, nw + 16, 17);
        r.RoundFill(tag, Theme.WithAlpha(nc, 0.22f), 7f);
        r.Disc(new Vector2(tag.X + 8, tag.Center.Y), 3f, nc);
        r.Text(nickF, nick, new Vector2(tag.X + 15, headY + 1), Theme.Mix(nc, Theme.Text, 0.45f));
        if (b.M.At != default) r.TextRight(r.Fonts.Get(FontKind.Mono, 10), b.M.At.ToString("HH:mm"), x + w - 4, headY + 3, Theme.TextFaint);

        float bubTop = headY + 20;
        float lineH = bodyF.MeasureString(Ircuitry.Render.Renderer.SafeText("Mg")).Y + 2f;
        var bub = new RectF(x + 6, bubTop, w - 12, b.Lines.Length * lineH + pad * 2);
        r.RoundFill(bub, Theme.WithAlpha(nc, 0.10f), 10f);
        r.RoundOutline(bub, Theme.WithAlpha(nc, 0.30f), 10f);
        r.Disc(new Vector2(bub.X + 14, bub.Y - 1), 3.2f, Theme.Mix(Theme.PanelHi, nc, 0.10f));
        float ly = bubTop + pad;
        foreach (var line in b.Lines) { r.Text(bodyF, line, new Vector2(bub.X + pad + 2, ly), Theme.Text); ly += lineH; }

        if (b.Run != null)
        {
            string label = Ircuitry.Core.Icons.Glyph(b.Run.Icon) + " ran " + b.Run.Trigger;
            var cf = r.Fonts.Get(FontKind.SansBold, 11);
            var chip = new RectF(x + 8, bub.Bottom + 4, MathF.Min(cf.MeasureString(Ircuitry.Render.Renderer.SafeText(label)).X + 18, w - 16), 18);
            bool hot = chip.Contains(In.Mouse);
            r.RoundFill(chip, hot ? Theme.Lime : Theme.WithAlpha(Theme.Lime, 0.30f), 8f);
            r.Text(cf, r.Ellipsize(cf, label, chip.W - 14), new Vector2(chip.X + 8, chip.Y + 2), hot ? Theme.TextInk : Theme.Mix(Theme.Lime, Theme.Text, 0.5f));
            jumpHit.Add((chip, b.M.Msgid));
        }
    }

    private void DrawIrcFooter(Renderer r, RectF win, IrcView v)
    {
        r.Begin();
        var f = r.Fonts.Get(FontKind.Mono, 11);
        int wins = IrcWindows(v).Count;
        string foot = !v.Live && wins == 0 ? "read-only · start the bot to watch it live"
            : $"read-only · {wins} window(s) · scroll to replay";
        r.Text(f, foot, new Vector2(win.X + 18, win.Bottom - 24), Theme.TextFaint);
        r.End();
    }

    private void DrawIrcResizeGrip(Renderer r, RectF win)
    {
        var grip = new RectF(win.Right - 22, win.Bottom - 22, 16, 16);
        r.Begin();
        for (int i = 0; i < 3; i++)
            r.Line(new Vector2(grip.Right - i * 4, grip.Bottom), new Vector2(grip.Right, grip.Bottom - i * 4), Theme.WithAlpha(Theme.Edge, 0.8f), 1.5f);
        r.End();
    }

    private void HandleIrcWindowInput()
    {
        var win = _ircWin;
        var head = new RectF(win.X, win.Y, win.W - 46, 30);
        var grip = new RectF(win.Right - 24, win.Bottom - 24, 22, 22);

        if (In.LeftPressed && !_ircWinJustOpened)
        {
            if (grip.Contains(In.Mouse)) _ircWinResizing = true;
            else if (head.Contains(In.Mouse)) { _ircWinDragging = true; _ircWinDragOff = In.Mouse - new Vector2(win.X, win.Y); }
            else if (!win.Contains(In.Mouse)) { _ircWinOpen = false; return; }
        }

        if (_ircWinDragging)
        {
            if (In.LeftDown)
                _ircWin = new RectF(Math.Clamp(In.Mouse.X - _ircWinDragOff.X, 0, _vw - win.W),
                    Math.Clamp(In.Mouse.Y - _ircWinDragOff.Y, Layout.TitlebarH, _vh - 44), win.W, win.H);
            else _ircWinDragging = false;
        }
        else if (_ircWinResizing)
        {
            if (In.LeftDown)
                _ircWin = new RectF(win.X, win.Y, Math.Clamp(In.Mouse.X - win.X + 12, 520, _vw - win.X - 8),
                    Math.Clamp(In.Mouse.Y - win.Y + 12, 340, _vh - win.Y - 8));
            else _ircWinResizing = false;
        }
    }

    // Lays out the message bubbles for the selected channel; cached by ring/history/width/channel signature.
    private void RebuildIrcBubbles(Renderer r, float bubbleW, string channel, IrcView v)
    {
        var ring = v.Messages;
        long histRev = Bot.Runtime.HistoryRevision;
        string sig = $"{ring.Count}|{(ring.Count > 0 ? ring[^1].Msgid + ring[^1].Text.Length : "")}|{(int)bubbleW}|{histRev}|{channel}";
        if (sig == _ircCacheSig) return;
        _ircCacheSig = sig;

        var byMsg = new Dictionary<string, RunRecord>();
        foreach (var run in Bot.Runtime.History())
            if (run.Msgid.Length > 0) byMsg[run.Msgid] = run;

        var bodyF = r.Fonts.Get(FontKind.Sans, 13);
        float lineH = bodyF.MeasureString(Ircuitry.Render.Renderer.SafeText("Mg")).Y + 2f;
        _ircBubbles.Clear();
        float total = 0;
        foreach (var m in ring)
        {
            if (!m.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase)) continue;
            var lines = WrapText(bodyF, m.Text.Length > 0 ? m.Text : "(empty)", bubbleW).ToArray();
            RunRecord? run = m.Msgid.Length > 0 && byMsg.TryGetValue(m.Msgid, out var rr) ? rr : null;
            float h = 24 + lines.Length * lineH + 18 + 6 + (run != null ? 22 : 0);
            _ircBubbles.Add(new IrcBubble { M = m, Lines = lines, H = h, Run = run });
            total += h;
        }
        _ircMsgTotalH = total;
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
                if (cur.Length > 0 && f.MeasureString(Ircuitry.Render.Renderer.SafeText(trial)).X > maxW) { lines.Add(cur.ToString()); cur.Clear(); cur.Append(word); }
                else { if (cur.Length > 0) cur.Append(' '); cur.Append(word); }
            }
            lines.Add(cur.ToString());
        }
        return lines;
    }

    private void OpenHistoryForMsg(string msgid)
    {
        OpenHistory();
        if (msgid.Length == 0) return;
        for (int i = 0; i < _historyRuns.Count; i++)
            if (_historyRuns[i].Msgid == msgid) { _historySel = i; _historyDetailScroll = 0; break; }
    }
}
