using System;
using Microsoft.Xna.Framework;
using Ircuitry.Core;
using Ircuitry.Graph;
using Ircuitry.Render;

namespace Ircuitry.Screens;

// The gamified onboarding flow. Drives a fixed sequence that ends with the user having built a
// working "!hello" command, then runs the test bench so they see it reply. The Tutorial class owns
// the look (dim/spotlight/card/confetti); this partial owns the step logic and app mutations.
public sealed partial class MainScreen
{
    private readonly Tutorial _tut = new();
    private int _tutLastStep = -999;
    private int _tutTestBaseline;
    private string? _tutCmdId, _tutReplyId;   // the nodes THIS run created (so a populated graph isn't hijacked)

    private void ResetTut() { _tutCmdId = _tutReplyId = null; _tutLastStep = -999; }

    /// <summary>Called once by the host on first launch - runs only for users who've never seen it.</summary>
    public void MaybeAutostartTutorial() { if (!Tutorial.DoneOnDisk) { ResetTut(); _tut.Begin(); } }
    public void ForceStartTutorial() { ResetTut(); _tut.Begin(); }

    /// <summary>Debug/screenshot hook: jump to a step with the prerequisite nodes already built.</summary>
    public void DebugTutorialStep(int step)
    {
        _l = Layout.Compute(_vw, _vh, _consoleH);
        ResetTut();
        _tut.Begin();
        if (step >= 2) { var c = _editor.Spawn(NodeCatalog.Get("event.command"), new Vector2(-160, 0)); c.SetParam("command", step >= 5 ? "hello" : ""); _tutCmdId = c.Id; }
        if (step >= 3) { var rp = _editor.Spawn(NodeCatalog.Get("action.reply"), new Vector2(180, 0)); rp.SetParam("message", step >= 6 ? "hi there {nick} ✨" : ""); _tutReplyId = rp.Id; }
        if (step >= 4 && _tutCmdId != null && _tutReplyId != null) Bot.Graph.Connect(_tutCmdId, 0, _tutReplyId, 0);
        _editor.FocusContent(_l.Canvas);
        _tut.GoTo(step);
    }

    // prefer the node this tutorial run placed; fall back to any of that type (e.g. an empty first-run graph)
    private Node? CmdNode()
    {
        if (_tutCmdId != null) { var t = Bot.Graph.Find(_tutCmdId); if (t != null) return t; }
        foreach (var n in Bot.Graph.Nodes) if (n.TypeId == "event.command") return n;
        return null;
    }
    private Node? ReplyNode()
    {
        if (_tutReplyId != null) { var t = Bot.Graph.Find(_tutReplyId); if (t != null) return t; }
        foreach (var n in Bot.Graph.Nodes) if (n.TypeId == "action.reply") return n;
        return null;
    }

    private bool Wired(Node a, Node b)
    {
        foreach (var c in Bot.Graph.Connections) if (c.FromNode == a.Id && c.ToNode == b.Id) return true;
        return false;
    }

    private void TutSelect(Node? n) { if (n == null) return; _editor.Selection.Clear(); _editor.Selection.Add(n.Id); }

    private void TutPlace(string typeId, Vector2 screenPoint, string clearParam)
    {
        var n = _editor.Spawn(NodeCatalog.Get(typeId), _editor.Cam.ScreenToWorld(screenPoint));
        n.SetParam(clearParam, "");   // start blank so the user fills it in a later step
        if (typeId == "event.command") _tutCmdId = n.Id;
        else if (typeId == "action.reply") _tutReplyId = n.Id;
        _app.MarkDirty();
    }

    private void TutWire()
    {
        var c = CmdNode(); var rp = ReplyNode();
        if (c == null || rp == null || Wired(c, rp)) return;
        _editor.PushUndo();
        Bot.Graph.Connect(c.Id, 0, rp.Id, 0);   // 'then' exec → reply exec
        _app.MarkDirty();
    }

    private static RectF Union(RectF a, RectF b)
    {
        float x = MathF.Min(a.X, b.X), y = MathF.Min(a.Y, b.Y);
        return new RectF(x, y, MathF.Max(a.Right, b.Right) - x, MathF.Max(a.Bottom, b.Bottom) - y);
    }

    // TEST button lives in the top tab bar's right cluster: [TIDY 80][TEST 86] from the right edge.
    private RectF TestButtonRect() => new(_l.Tabs.Right - 172, _l.Tabs.Y, 86, _l.Tabs.H);

    private void OnTutEnter(int step)
    {
        if (step == 4) TutSelect(CmdNode());
        else if (step == 5) TutSelect(ReplyNode());
        else if (step == 6)
        {
            _tutTestBaseline = _testRunSeq;
            var c = CmdNode();
            if (c != null)
            {
                var pfx = c.GetParam("prefix"); if (pfx.Length == 0) pfx = "!";
                var word = c.GetParam("command"); if (word.Length == 0) word = "hello";
                _testMsg = pfx + word;
            }
        }
    }

    private void DrawTutorial(Renderer r, Clock clock)
    {
        if (!_tut.Active) return;
        if (Modal) return;   // pause behind app modals (e.g. while the test bench is open)

        if (_tut.Step != _tutLastStep) { OnTutEnter(_tut.Step); _tutLastStep = _tut.Step; }

        var cmd = CmdNode();
        var reply = ReplyNode();
        var canvas = _l.Canvas;

        var accent = Theme.Cyan;
        RectF? spot = null;
        string title, body, prim;
        string? sec = null;
        bool showSkip = true;
        bool confetti = false;

        // default card: lower-centre over the canvas
        RectF card = new(canvas.Center.X - 260, canvas.Bottom - 252, 520, 212);
        RectF cardCenter = new(_vw / 2f - 270, _vh / 2f - 130, 540, 232);
        RectF cardLeft = new(canvas.X + 30, canvas.Center.Y - 110, 480, 214);

        switch (_tut.Step)
        {
            case 1:   // place trigger
                accent = Theme.Cyan; spot = canvas;
                title = "Add a trigger";
                body = "Every command begins with a trigger. \"On Command\" fires when someone types a prefix and a word - like !hello. Pop one onto the canvas.";
                prim = cmd == null ? "➕  Place 'On Command'" : "Next  ▶";
                break;

            case 2:   // place reply
                accent = Theme.Lime; spot = canvas;
                title = "Add an action";
                body = "Now the fun part - what the bot does. \"Send Reply\" answers right back in the channel. Add one next to your trigger.";
                prim = reply == null ? "➕  Place 'Send Reply'" : "Next  ▶";
                break;

            case 3:   // wire
                accent = Theme.Cyan;
                if (cmd != null && reply != null)
                    spot = Union(_editor.NodeScreenRect(cmd), _editor.NodeScreenRect(reply)).Inflate(10, 10).Intersect(canvas);
                else spot = canvas;
                bool wired = cmd != null && reply != null && Wired(cmd, reply);
                title = "Connect the wire";
                body = "Wires carry the signal. Link the trigger's → output to the reply so that firing the command runs the reply.";
                prim = wired ? "Next  ▶" : "🔗  Wire them together";
                break;

            case 4:   // name the command
                accent = Theme.Amber; spot = _l.Inspector; card = cardLeft;
                TutSelect(cmd);
                bool named = cmd != null && cmd.GetParam("command").Trim().Length > 0;
                title = "Name your command";
                body = "Look right → the Inspector shows your trigger. Type a word in the \"Command\" field - try hello. (No prefix, just the word.)";
                prim = named ? "Next  ▶" : "Type a word first…";
                sec = "Use 'hello'";
                break;

            case 5:   // write the reply
                accent = Theme.Lime; spot = _l.Inspector; card = cardLeft;
                TutSelect(reply);
                bool wrote = reply != null && reply.GetParam("message").Trim().Length > 0;
                title = "Write the reply";
                body = "Pick the reply node and fill its \"Message\" field with what the bot should say. Try: hi there {nick} ✨  ({nick} becomes who asked!)";
                prim = wrote ? "Next  ▶" : "Write a message first…";
                sec = "Use a friendly one";
                break;

            case 6:   // test it
                accent = Theme.Cyan; spot = TestButtonRect(); card = cardCenter;
                title = "Try it out!";
                body = "Moment of truth. Click ▶ TEST up top - the message is already set to your command - then hit RUN and watch your bot reply. 🎉";
                prim = "";   // they must click the real TEST button
                if (_testRunSeq > _tutTestBaseline) { _tut.GoTo(Tutorial.Done); return; }
                break;

            case Tutorial.Done:   // celebrate
                accent = Theme.Lime; spot = null; card = cardCenter; confetti = true; showSkip = false;
                title = "🎉  You built a bot command!";
                body = "When someone types your command, your bot replies - all wired up by you. Mix in AI, timers, files and more from the Node Library. Happy baking!";
                prim = "Finish  🎈";
                break;

            default:  // welcome (Step 0)
                accent = Theme.Cyan; spot = null; card = cardCenter;
                title = "Welcome to ircuitry!";
                body = "Let's build your first bot command together - a friendly !hello that answers in chat. Takes about a minute, and you can skip anytime.";
                prim = "Let's go!  ▶";
                break;
        }

        r.Begin(BlendMode.Alpha);
        _tut.Spotlight(r, clock, _vw, _vh, spot, accent);
        if (confetti) _tut.Confetti(r, clock, _vw, _vh);
        var res = _tut.Card(r, _ui, clock, card, accent, _tut.Step, title, body,
            string.IsNullOrEmpty(prim) ? null : prim, sec, showSkip);
        r.End();

        if (res == TutResult.Skip) { _tut.Quit(); return; }
        HandleTutResult(res, cmd, reply, canvas);
    }

    private void HandleTutResult(TutResult res, Node? cmd, Node? reply, RectF canvas)
    {
        var cmdPt = new Vector2(canvas.Center.X - 150, canvas.Center.Y - 30);
        var replyPt = new Vector2(canvas.Center.X + 175, canvas.Center.Y - 30);

        switch (_tut.Step)
        {
            case 0:
                if (res == TutResult.Primary) _tut.GoTo(1);
                break;
            case 1:
                if (res == TutResult.Primary)
                {
                    if (cmd == null) TutPlace("event.command", cmdPt, "command");
                    else _tut.GoTo(2);
                }
                break;
            case 2:
                if (res == TutResult.Primary)
                {
                    if (reply == null) TutPlace("action.reply", replyPt, "message");
                    else _tut.GoTo(3);
                }
                break;
            case 3:
                if (res == TutResult.Primary)
                {
                    bool wired = cmd != null && reply != null && Wired(cmd, reply);
                    if (!wired) TutWire(); else _tut.GoTo(4);
                }
                break;
            case 4:
                if (res == TutResult.Secondary && cmd != null) { cmd.SetParam("command", "hello"); _app.MarkDirty(); }
                if (res == TutResult.Primary && cmd != null && cmd.GetParam("command").Trim().Length > 0) _tut.GoTo(5);
                break;
            case 5:
                if (res == TutResult.Secondary && reply != null) { reply.SetParam("message", "hi there {nick} ✨"); _app.MarkDirty(); }
                if (res == TutResult.Primary && reply != null && reply.GetParam("message").Trim().Length > 0) _tut.GoTo(6);
                break;
            case Tutorial.Done:
                if (res == TutResult.Primary) _tut.Finish();
                break;
        }
    }
}
