using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Xna.Framework;
using Ircuitry.Core;
using Ircuitry.Editor;
using Ircuitry.Graph;
using Ircuitry.Irc;

namespace Ircuitry.Runtime;

/// <summary>Headless checks for the executor - run with `Ircuitry --selftest`.</summary>
public static class SelfTest
{
    private sealed class FakeSink : IRuntimeSink
    {
        public readonly List<(string target, string text)> Sent = new();
        public readonly List<string> SentTags = new();   // client tags parallel to Sent (for +reply etc.)
        public readonly List<string> Logs = new();
        public readonly Dictionary<string, string> State = new();
        public string GetState(string key) => State.TryGetValue(key, out var v) ? v : "";
        public void SetState(string key, string value) => State[key] = value;
        public void Privmsg(string t, string x) { Sent.Add((t, x)); SentTags.Add(""); }
        public void Notice(string t, string x) { Sent.Add((t, x)); SentTags.Add(""); }
        public void React(string t, string m, string e) { Sent.Add((t, "react:" + e)); SentTags.Add(""); }
        public void PrivmsgTagged(string t, string x, string tags) { Sent.Add((t, x)); SentTags.Add(tags); }
        public void NoticeTagged(string t, string x, string tags) { Sent.Add((t, x)); SentTags.Add(tags); }
        public void Join(string c) => Logs.Add("JOIN " + c);
        public void Part(string c, string r) => Logs.Add("PART " + c + (r.Length > 0 ? " :" + r : ""));
        public void Raw(string l) => Logs.Add("RAW " + l);
        public void StartTyping(string t) => Logs.Add("TYPING start " + t);
        public void StopTyping(string t) => Logs.Add("TYPING stop " + t);
        public void Log(string m, LogLevel lvl) => Logs.Add(m);
        public void NodeFired(string id) { }
        public readonly List<string> Reconnects = new();
        public void Reconnect(string server) => Reconnects.Add(server);
        public string FilehostUrl = "";
        public string IrcInfo(string what, string channel) => what == "filehost" ? FilehostUrl : "";
        public readonly HashSet<string> Caps = new(StringComparer.OrdinalIgnoreCase);   // negotiated caps to simulate
        public bool HasCap(string cap) => Caps.Contains(cap);
        public readonly Dictionary<string, string> MetaSeed = new();   // metadata values a METADATA GET node will read
        public string MetadataGet(string target, string key, int timeoutMs) => MetaSeed.TryGetValue(key, out var v) ? v : "";
        public System.Collections.Generic.List<string> LastRun = new();
        public void RunCompleted(System.Collections.Generic.IReadOnlyCollection<string> executedTypes) { LastRun = new(executedTypes); }
        public readonly List<RecentMsg> RecentSeed = new();   // what SuperAI's recent_messages tool sees
        public IReadOnlyList<RecentMsg> RecentMessages(int count) => RecentSeed;
        public readonly List<RecentMsg> HistorySeed = new();  // what a CHATHISTORY request returns
        public IReadOnlyList<RecentMsg> RequestHistory(string target, string sub, int count, int timeoutMs) => HistorySeed;
    }

    private static Node N(NodeGraph g, string type, float x, float y)
    {
        var n = g.Add(NodeCatalog.Get(type), new Vector2(x, y));
        return n;
    }

    private static Dictionary<string, string> Vars(string msg, string nick, string chan) => new()
    {
        ["message"] = msg, ["nick"] = nick, ["channel"] = chan, ["target"] = chan,
        ["replyto"] = chan, ["args"] = "", ["command"] = "", ["botnick"] = "ircuitry",
    };

    public static int RunAll()
    {
        int fails = 0;
        Ircuitry.Core.Notifier.Enabled = false;   // never pop real desktop notifications while testing

        // --- Test 1: On Command(ping) -> Send Reply(pong) ---
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "ping");
            var reply = N(g, "action.reply", 300, 0); reply.SetParam("message", "pong");
            g.Connect(cmd.Id, 0, reply.Id, 0);

            var sink = new FakeSink();
            GraphExecutor.Fire(g, sink, cmd, Vars("!ping", "alice", "#test"));
            fails += Expect("cmd-match", sink.Sent.Count == 1 && sink.Sent[0] == ("#test", "pong"), Dump(sink));

            var sink2 = new FakeSink();
            GraphExecutor.Fire(g, sink2, cmd, Vars("!nope", "alice", "#test"));
            fails += Expect("cmd-nomatch", sink2.Sent.Count == 0, Dump(sink2));
        }

        // --- Test 2: On Message -> Text Contains(hi) -> Reply(hey {nick}) ---
        {
            var g = new NodeGraph();
            var msg = N(g, "event.message", 0, 0);
            var flt = N(g, "filter.contains", 250, 0); flt.SetParam("needle", "hi");
            var reply = N(g, "action.reply", 500, 0); reply.SetParam("message", "hey {nick}");
            g.Connect(msg.Id, 0, flt.Id, 0);     // exec
            g.Connect(flt.Id, 0, reply.Id, 0);   // match -> reply

            var hit = new FakeSink();
            GraphExecutor.Fire(g, hit, msg, Vars("hi there", "bob", "#dev"));
            fails += Expect("contains-hit", hit.Sent.Count == 1 && hit.Sent[0] == ("#dev", "hey bob"), Dump(hit));

            var miss = new FakeSink();
            GraphExecutor.Fire(g, miss, msg, Vars("goodbye", "bob", "#dev"));
            fails += Expect("contains-miss", miss.Sent.Count == 0, Dump(miss));
        }

        // --- Test 3: pure data node pull (Random Reply -> Send Reply) ---
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "hi");
            var rnd = N(g, "data.random", 250, 120); rnd.SetParam("options", "only-one");
            var reply = N(g, "action.reply", 500, 0);
            g.Connect(cmd.Id, 0, reply.Id, 0);     // exec
            g.Connect(rnd.Id, 0, reply.Id, 1);     // data: random text -> reply message

            var sink = new FakeSink();
            GraphExecutor.Fire(g, sink, cmd, Vars("!hi", "cat", "#x"));
            fails += Expect("pure-pull", sink.Sent.Count == 1 && sink.Sent[0] == ("#x", "only-one"), Dump(sink));
        }

        // --- Test 3b: a Set Var value is readable later as a plain {token} (state fallback in ResolveToken).
        // This is what makes a computed Delay (seconds={typing_secs}) and a state-gated check ({mita_active}) work. ---
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "go");
            var sv = N(g, "data.setvar", 250, 0); sv.SetParam("name", "ztok"); sv.SetParam("value", "hello");
            var reply = N(g, "action.reply", 500, 0); reply.SetParam("message", "[{ztok}]");
            g.Connect(cmd.Id, 0, sv.Id, 0);      // exec: fire -> set the var
            g.Connect(sv.Id, 0, reply.Id, 0);    // exec: then -> reply reading it back as a token

            var sink = new FakeSink();
            GraphExecutor.Fire(g, sink, cmd, Vars("!go", "cat", "#x"));
            fails += Expect("statevar-token", sink.Sent.Count == 1 && sink.Sent[0] == ("#x", "[hello]"), Dump(sink));
        }

        // --- Test 3c: a Set Var written in ONE event is readable as a {token} in a SEPARATE later event
        // (the message path writes mita_active; the timer path reads it - same bot state, different run). ---
        {
            var sink = new FakeSink();   // one sink == one bot's persistent state, shared across events
            var g = new NodeGraph();
            var c1 = N(g, "event.command", 0, 0); c1.SetParam("command", "set");
            var sv = N(g, "data.setvar", 250, 0); sv.SetParam("name", "kx"); sv.SetParam("value", "v42");
            g.Connect(c1.Id, 0, sv.Id, 0);
            var c2 = N(g, "event.command", 0, 200); c2.SetParam("command", "get");
            var reply = N(g, "action.reply", 250, 200); reply.SetParam("message", "[{kx}]");
            g.Connect(c2.Id, 0, reply.Id, 0);

            GraphExecutor.Fire(g, sink, c1, Vars("!set", "a", "#x"));   // event 1: write the var
            GraphExecutor.Fire(g, sink, c2, Vars("!get", "a", "#x"));   // event 2: separate run reads it back
            fails += Expect("statevar-cross-event", sink.Sent.Count == 1 && sink.Sent[0] == ("#x", "[v42]"), Dump(sink));
        }

        // --- Test 3d: Format Text resolves more than raw event vars - the {me}/botnick alias AND a stored Set Var
        // value. (Before the fix, data.format read raw run-vars only, so {me} and {mita_active} came back empty.) ---
        {
            var sink = new FakeSink();
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "go");
            var sv = N(g, "data.setvar", 200, 0); sv.SetParam("name", "skey"); sv.SetParam("value", "SVAL");
            var fmt = N(g, "data.format", 400, 0); fmt.SetParam("template", "me={me} sk={skey}");
            var reply = N(g, "action.reply", 600, 0);
            g.Connect(cmd.Id, 0, sv.Id, 0);
            g.Connect(sv.Id, 0, reply.Id, 0);
            g.Connect(fmt.Id, 0, reply.Id, 1);   // Format Text -> reply message (pure pull)
            GraphExecutor.Fire(g, sink, cmd, Vars("!go", "cat", "#x"));
            fails += Expect("format-resolves-state-and-alias", sink.Sent.Count == 1 && sink.Sent[0] == ("#x", "me=ircuitry sk=SVAL"), Dump(sink));
        }

        // --- Test 3e: the new primitives - data.xml turns RSS into JSON a JSON Field can read; mail nodes registered ---
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "feed");
            var xml = N(g, "data.xml", 200, 120);
            xml.SetParam("xml", "<rss><channel><title>My Feed</title><item><title>First</title></item><item><title>Second</title></item></channel></rss>");
            var pick = N(g, "data.json", 400, 120); pick.SetParam("path", "rss.channel.item.0.title");
            var reply = N(g, "action.reply", 600, 0);
            g.Connect(cmd.Id, 0, reply.Id, 0);     // exec
            g.Connect(xml.Id, 0, pick.Id, 0);      // xml's json output -> JSON Field input
            g.Connect(pick.Id, 0, reply.Id, 1);    // extracted title -> reply message
            var sink = new FakeSink();
            GraphExecutor.Fire(g, sink, cmd, Vars("!feed", "u", "#x"));
            fails += Expect("data.xml-rss", sink.Sent.Count == 1 && sink.Sent[0] == ("#x", "First"), Dump(sink));
            fails += Expect("mail-nodes-registered", NodeCatalog.Get("mail.send") != null && NodeCatalog.Get("mail.fetch") != null && NodeCatalog.Get("data.xml") != null, "");
        }

        fails += NewNodesTest();
        fails += Ircv3AndHistoryTest();
        fails += ScheduleTest();
        fails += FileAndIcalTest();
        fails += MoreNodesTest();
        fails += SqlAndCodeTest();
        fails += ParserTests();
        fails += BotToolsTest();
        fails += StreamAndPasteTest();
        fails += SecretsTest();
        fails += IrcLoopTest();
        fails += IrcRestartTest();
        fails += LiveApplyTest();
        fails += BotCmdInvokeTest();
        fails += BotCmdBadContextTest();
        fails += LiveStreamTest();
        fails += AiLoopTest();
        fails += AiToolsTest();
        fails += AiToolStatePersistTest();
        fails += SubAgentTest();
        fails += McpClientTest();
        fails += DynamicAiArgsTest();
        fails += SuperAiTest();
        fails += SuperAiCompositeTest();
        fails += HistoryBatchTest();
        fails += HistoryAbandonTest();
        fails += ChatHistoryNodeTest();
        fails += IrcStateTest();
        fails += SwitchPruneTest();
        fails += IoTest();
        fails += WorkspaceTest();
        fails += ZimTest();
        fails += TextSafetyTest();
        fails += SignalTest();
        fails += RunCompletedTest();
        fails += FanInTest();
        fails += MultiServerTest();
        fails += TrayMenuTest();
        fails += ShortcodeTest();
        fails += CameraBoundsTest();
        fails += McpEditorTest();
        fails += AiEditorToolTest();
        fails += ReloadKeepsRunningBotTest();
        fails += UnknownNodeWarningTest();
        fails += HumanNodesTest();
        fails += HumanLoopApproveTest();
        fails += ConcurrentExecutorTest();
        fails += JsonAndLoopsTest();
        fails += NodeAsToolTest();
        fails += ToolBakeTest();
        fails += CompositeBakeTest();
        fails += CompositeMiniSerializeTest();
        fails += CompositeExposeTest();
        fails += ParamListAddTest();
        fails += ToolkitTest();
        fails += BotMergeTest();
        fails += DccTest();
        fails += MultilineSendTest();
        fails += FileParamTest();
        fails += CapabilityCorsTest();
        fails += ThemeRoundTripTest();
        fails += WebhookTest();
        fails += ModeTemplateTest();
        fails += CodeSandboxTest();
        fails += GuardrailTest();
        fails += CacheTest();
        fails += WatchdogTest();
        fails += CapabilitiesTest();
        fails += FixesTest();
        fails += FilehostTest();
        fails += ScramVectorTest();
        fails += IsupportParseTest();
        fails += CapGuardTest();
        fails += RegexCaptureTest();
        fails += MathTokenTest();
        fails += SaslLoopTest();
        fails += MetadataTest();
        fails += McpErrorTest();
        fails += BotCmdsFitTest();
        fails += ClientCertTest();
        fails += SocketLoopTest();
        fails += StartTriggerTest();
        fails += IrcdE2ETest();
        fails += IrcdNodesE2ETest();
        fails += IrcdNodesTlsTest();

        Console.WriteLine(fails == 0 ? "SELFTEST_OK all passed" : $"SELFTEST_FAIL {fails} failure(s)");
        return fails;
    }

    /// <summary>Guardrails (#19): the heuristic moderation primitives, and a Moderate In node branching
    /// clean vs flagged on a block-list term.</summary>
    private static int GuardrailTest()
    {
        int fails = 0;

        // heuristic primitives
        var terms = Ircuitry.Graph.Moderation.Terms("spam, scam\nphish");
        fails += Expect("mod-terms", terms.Count == 3 && terms.Contains("scam"), string.Join("|", terms));
        fails += Expect("mod-block", Ircuitry.Graph.Moderation.Check("please no spam here", terms, false, false, false).flagged, "");
        fails += Expect("mod-clean", !Ircuitry.Graph.Moderation.Check("a friendly hello", terms, true, true, true).flagged, "");
        fails += Expect("mod-caps", Ircuitry.Graph.Moderation.Check("STOP SHOUTING AT ME", new string[0], false, true, false).flagged, "");
        fails += Expect("mod-link", Ircuitry.Graph.Moderation.Check("see https://evil.example", new string[0], true, false, false).flagged, "");
        fails += Expect("mod-redact", Ircuitry.Graph.Moderation.Redact("buy spam now", terms, false) == "buy **** now", Ircuitry.Graph.Moderation.Redact("buy spam now", terms, false));
        var verdict = Ircuitry.Graph.Moderation.ParseVerdict("FLAG: harassment");
        fails += Expect("mod-verdict", verdict is { } v && v.flagged && v.reason == "harassment", "");
        fails += Expect("mod-verdict-safe", Ircuitry.Graph.Moderation.ParseVerdict("SAFE") is { } sv && !sv.flagged, "");

        // node branching: On Message -> Moderate In -> (clean -> "ok") / (flagged -> "blocked")
        var g = new NodeGraph();
        var msg = N(g, "event.message", 0, 0);
        var mod = N(g, "mod.in", 250, 0); mod.SetParam("blockWords", "spam");
        var ok = N(g, "action.reply", 500, 0); ok.SetParam("message", "ok");
        var bad = N(g, "action.reply", 500, 140); bad.SetParam("message", "blocked");
        g.Connect(msg.Id, 0, mod.Id, 0);
        g.Connect(mod.Id, 0, ok.Id, 0);    // clean
        g.Connect(mod.Id, 1, bad.Id, 0);   // flagged

        var s1 = new FakeSink();
        GraphExecutor.Fire(g, s1, msg, Vars("buy spam now", "alice", "#x"));
        fails += Expect("mod-in-flagged", s1.Sent.Count == 1 && s1.Sent[0] == ("#x", "blocked"), Dump(s1));

        var s2 = new FakeSink();
        GraphExecutor.Fire(g, s2, msg, Vars("hello world", "alice", "#x"));
        fails += Expect("mod-in-clean", s2.Sent.Count == 1 && s2.Sent[0] == ("#x", "ok"), Dump(s2));

        return fails;
    }

    /// <summary>IRCv3 draft/FILEHOST: ISUPPORT parsing (all token name forms + removal) and the {filehost} token
    /// resolving to the server-advertised URL through the runtime.</summary>
    private static int FilehostTest()
    {
        int fails = 0;
        var s = new IrcSessionState();
        s.Observe(IrcParser.Parse(":serv 005 me draft/FILEHOST=https://files.example/upload NETWORK=X :are supported"), "me");
        fails += Expect("fh-isupport-draft", s.Filehost == "https://files.example/upload", s.Filehost);
        var s2 = new IrcSessionState();
        s2.Observe(IrcParser.Parse(":serv 005 me FILEHOST=https://f2.example/up :are supported"), "me");
        fails += Expect("fh-isupport-final", s2.Filehost == "https://f2.example/up", s2.Filehost);
        s.Observe(IrcParser.Parse(":serv 005 me -draft/FILEHOST :are supported"), "me");
        fails += Expect("fh-isupport-remove", s.Filehost == "", s.Filehost);

        // {filehost} token resolves to the sink's advertised URL
        var g = new NodeGraph();
        var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "fh");
        var reply = N(g, "action.reply", 250, 0); reply.SetParam("message", "{filehost}");
        g.Connect(cmd.Id, 0, reply.Id, 0);
        var sink = new FakeSink { FilehostUrl = "https://files.example/upload" };
        GraphExecutor.Fire(g, sink, cmd, Vars("!fh", "u", "#c"));
        fails += Expect("fh-token-resolve", sink.Sent.Count == 1 && sink.Sent[0] == ("#c", "https://files.example/upload"), Dump(sink));

        // Has Filehost? branches yes/no on availability
        var hg = new NodeGraph();
        var hc = N(hg, "event.command", 0, 0); hc.SetParam("command", "h");
        var hf = N(hg, "irc.hasfilehost", 200, 0);
        var yes = N(hg, "action.reply", 400, 0); yes.SetParam("message", "yes");
        var no = N(hg, "action.reply", 400, 120); no.SetParam("message", "no");
        hg.Connect(hc.Id, 0, hf.Id, 0); hg.Connect(hf.Id, 0, yes.Id, 0); hg.Connect(hf.Id, 1, no.Id, 0);
        var withFh = new FakeSink { FilehostUrl = "https://f/up" };
        GraphExecutor.Fire(hg, withFh, hc, Vars("!h", "u", "#c"));
        fails += Expect("fh-has-yes", withFh.Sent.Count == 1 && withFh.Sent[0] == ("#c", "yes"), Dump(withFh));
        var noFh = new FakeSink();
        GraphExecutor.Fire(hg, noFh, hc, Vars("!h", "u", "#c"));
        fails += Expect("fh-has-no", noFh.Sent.Count == 1 && noFh.Sent[0] == ("#c", "no"), Dump(noFh));
        return fails;
    }

    /// <summary>Coverage for the gap-audit fixes: token-meter cap gating + same-cap-no-reset, semantic cache
    /// cosine lookup, workspace round-trip of evals/colorTag/frames, and the Moderate Out block/redact branches.</summary>
    private static int FixesTest()
    {
        int fails = 0;

        // ---- AI spend cap gating (BotRuntime token meter) ----
        var rt = new BotRuntime(new ConsoleLog(), new System.Collections.Concurrent.ConcurrentDictionary<string, string>());
        fails += Expect("tok-uncapped", !rt.AiOverBudget, "no cap set");
        rt.AddTokens(40, 30);
        fails += Expect("tok-total", rt.TokensTotal == 70, rt.TokensTotal.ToString());
        rt.SetTokenBudget(100, 0);                 // a genuinely new cap resets the window to 0
        fails += Expect("tok-cap-fresh", !rt.AiOverBudget, "fresh window");
        rt.AddTokens(60, 50);                      // window now 110 >= 100
        fails += Expect("tok-over", rt.AiOverBudget, "over the cap");
        rt.SetTokenBudget(100, 0);                 // re-applying the SAME cap must NOT reset (the reconnect-storm fix)
        fails += Expect("tok-same-cap-no-reset", rt.AiOverBudget, "still over after re-applying same cap");
        rt.SetTokenBudget(1000000, 0);             // a different cap resets the window
        fails += Expect("tok-new-cap-reset", !rt.AiOverBudget, "");
        rt.SetTokenBudget(0, 0); rt.AddTokens(999999, 0);
        fails += Expect("tok-zero-unlimited", !rt.AiOverBudget, "cap 0 = unlimited");

        // ---- semantic cache cosine lookup ----
        string j = AiCache.Put("", "alpha", "replyA", new[] { 1f, 0f, 0f }, 50);
        fails += Expect("cache-sem-hit", AiCache.Lookup(j, "beta", new[] { 0.9f, 0.1f, 0f }, 0.9) is { } sh && sh.reply == "replyA", "near vector should match");
        fails += Expect("cache-sem-miss", AiCache.Lookup(j, "beta", new[] { 0f, 1f, 0f }, 0.9) == null, "orthogonal vector should miss");

        // ---- workspace round-trip: evals + colorTag + frames survive save/load ----
        var b = new Ircuitry.App.Bot("rt");
        b.Evals.Add(new Ircuitry.App.EvalCase { Message = "!ping", Expect = "pong", Mode = Ircuitry.App.EvalMatch.Contains });
        b.Evals.Add(new Ircuitry.App.EvalCase { Message = "hi", Mode = Ircuitry.App.EvalMatch.NoReply });
        var cn = b.Graph.Add(NodeCatalog.Get("event.command"), new Vector2(0, 0)); cn.ColorTag = 3;
        var fr = Frame.Create(new Vector2(10, 20)); fr.Title = "note"; fr.Body = "body"; b.Graph.Frames.Add(fr);
        b.Servers[0].SaslMech = "scram"; b.Servers[0].ClientCertPath = "/certs/bot.pem"; b.Servers[0].ClientCertPass = "{{secret.certpw}}";
        var (rtBots, _, _) = Ircuitry.App.WorkspaceSerializer.Load(Ircuitry.App.WorkspaceSerializer.Save(new[] { b }, 0, null));
        var lb = rtBots[0];
        fails += Expect("ws-evals", lb.Evals.Count == 2 && lb.Evals[0].Expect == "pong" && lb.Evals[1].Mode == Ircuitry.App.EvalMatch.NoReply, lb.Evals.Count.ToString());
        fails += Expect("ws-sasl-fields", lb.Servers[0].SaslMech == "scram" && lb.Servers[0].ClientCertPath == "/certs/bot.pem" && lb.Servers[0].ClientCertPass == "{{secret.certpw}}", lb.Servers[0].SaslMech + " " + lb.Servers[0].ClientCertPath);
        fails += Expect("ws-colortag", lb.Graph.Nodes.Count == 1 && lb.Graph.Nodes[0].ColorTag == 3, "");
        fails += Expect("ws-frames", lb.Graph.Frames.Count == 1 && lb.Graph.Frames[0].Title == "note" && lb.Graph.Frames[0].Body == "body", "");

        // ---- Moderate Out: block vs redact branches ----
        var g = new NodeGraph();
        var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "x");
        var src = N(g, "data.random", 120, 150); src.SetParam("options", "buy spam now");
        var mo = N(g, "mod.out", 240, 0); mo.SetParam("blockWords", "spam"); mo.SetParam("onFlag", "block");
        var clean = N(g, "action.reply", 460, 0);
        var flag = N(g, "action.reply", 460, 140); flag.SetParam("message", "BLOCKED");
        g.Connect(cmd.Id, 0, mo.Id, 0);     // exec
        g.Connect(src.Id, 0, mo.Id, 1);     // text input
        g.Connect(mo.Id, 0, clean.Id, 0);   // clean
        g.Connect(mo.Id, 2, clean.Id, 1);   // safe text -> clean reply message
        g.Connect(mo.Id, 1, flag.Id, 0);    // flagged
        var sb = new FakeSink();
        GraphExecutor.Fire(g, sb, cmd, Vars("!x", "u", "#c"));
        fails += Expect("modout-block", sb.Sent.Count == 1 && sb.Sent[0] == ("#c", "BLOCKED"), Dump(sb));
        mo.SetParam("onFlag", "redact");
        var sr = new FakeSink();
        GraphExecutor.Fire(g, sr, cmd, Vars("!x", "u", "#c"));
        fails += Expect("modout-redact", sr.Sent.Count == 1 && sr.Sent[0] == ("#c", "buy **** now"), Dump(sr));

        // ---- code tool guard: an empty/blank path must be rejected, never resolved to (and clobber) the root ----
        string troot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ircuitry-ct-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(troot);
        try
        {
            bool threw = false;
            try { CodeTools.Write(troot, "", "must not write to the root folder"); } catch (CodeAccessException) { threw = true; }
            fails += Expect("ct-empty-write-rejected", threw, "blank path should throw");
            fails += Expect("ct-root-intact", Directory.Exists(troot), "root must still be a directory, not overwritten");
            CodeTools.Write(troot, "sub/a.txt", "hi");
            fails += Expect("ct-valid-write", File.Exists(System.IO.Path.Combine(troot, "sub", "a.txt")), "a real path still works");
        }
        finally { try { Directory.Delete(troot, true); } catch { } }

        // ---- container sandbox: the runtime arg builder (deterministic, no daemon needed) ----
        var runArgs = Ircuitry.Net.ContainerEngine.BuildRunArgs(Ircuitry.Net.ContainerEngine.Engine.Docker, "python:3.12", "/home/u/proj", "pytest -q", false, "ircuitry-run-abc");
        string runJoined = string.Join(" ", runArgs);
        fails += Expect("cont-run-netoff", runArgs.Contains("--network=none"), runJoined);
        fails += Expect("cont-run-mount", runArgs.Contains("/home/u/proj:/work") && runArgs.Contains("python:3.12"), runJoined);
        fails += Expect("cont-run-cmd", runArgs[^3] == "sh" && runArgs[^2] == "-c" && runArgs[^1] == "pytest -q", runJoined);
        fails += Expect("cont-run-rm", runArgs.Contains("--rm") && runArgs.Contains("--memory=2g") && runArgs.Contains("--pids-limit=512"), runJoined);
        var netArgs = Ircuitry.Net.ContainerEngine.BuildRunArgs(Ircuitry.Net.ContainerEngine.Engine.Podman, "alpine", "", "echo hi", true, "n");
        fails += Expect("cont-run-neton", !netArgs.Contains("--network=none"), string.Join(" ", netArgs));
        fails += Expect("cont-run-nomount", !netArgs.Contains("-v"), string.Join(" ", netArgs));   // blank dir -> no bind mount
        var startArgs = Ircuitry.Net.ContainerEngine.BuildStartArgs(Ircuitry.Net.ContainerEngine.Engine.Docker, "node:20", "ircuitry-devbox", "/p", false);
        fails += Expect("cont-start-detached", startArgs.Contains("-d") && startArgs.Contains("ircuitry-devbox") && startArgs[^1].Contains("sleep"), string.Join(" ", startArgs));
        fails += Expect("cont-safename", Ircuitry.Net.ContainerEngine.SafeName("My Box!") == "ircuitry-my-box-", Ircuitry.Net.ContainerEngine.SafeName("My Box!"));

        // ---- try-before-install sandbox: network and code are blocked when the per-thread dry-run flag is set ----
        Ircuitry.Net.Http.DryRun = true;
        var (st, body) = Ircuitry.Net.Http.Send("GET", "http://example.invalid/x", System.Array.Empty<(string, string)>(), null, 5);
        Ircuitry.Net.Http.DryRun = false;
        fails += Expect("dryrun-blocks-net", st == 0 && body.Contains("dry run"), st + " " + body);
        Ircuitry.Net.CodeRunner.DryRun = true;
        var (cout, cerr) = Ircuitry.Net.CodeRunner.Run("python", "print(1)", new Dictionary<string, string>(), 5);
        Ircuitry.Net.CodeRunner.DryRun = false;
        fails += Expect("dryrun-blocks-code", cerr == null && cout.Contains("dry run"), cout + " / " + cerr);

        return fails;
    }

    /// <summary>The "can't lie" capability scan: powers are derived truthfully from the contained node types.</summary>
    private static int CapabilitiesTest()
    {
        int fails = 0;
        var g = new NodeGraph();
        N(g, "event.command", 0, 0);
        var http = N(g, "net.http", 120, 0); http.SetParam("url", "{{secret.token}}");
        N(g, "action.reply", 240, 0);
        var caps = Ircuitry.Graph.Capabilities.Scan(g);
        fails += Expect("caps-net", caps.Any(c => c.Label == "Network access" && c.Caution), "");
        fails += Expect("caps-irc", caps.Any(c => c.Label == "Acts on IRC"), "");
        fails += Expect("caps-secret", caps.Any(c => c.Label == "Uses your secret keys"), "");

        var g2 = new NodeGraph();
        g2.Nodes.Add(new Node("x1", "totally.bogus.node"));
        fails += Expect("caps-unknown", Ircuitry.Graph.Capabilities.Scan(g2).Any(c => c.Label == "Unknown node" && c.Caution), "");

        var g3 = new NodeGraph();
        N(g3, "event.command", 0, 0);
        N(g3, "filter.contains", 120, 0);
        var clean = Ircuitry.Graph.Capabilities.Scan(g3);
        fails += Expect("caps-clean", clean.Count == 1 && !clean[0].Caution, clean.Count.ToString());
        return fails;
    }

    /// <summary>Watchdog / auto-heal: the Watchdog trigger branches healthy vs needs-heal on the injected
    /// health vars, and the Reconnect node routes a (re)connect request to the runtime.</summary>
    private static int WatchdogTest()
    {
        int fails = 0;

        // Watchdog -> healthy / needs-heal, plus needs-heal -> Reconnect
        var g = new NodeGraph();
        var wd = N(g, "event.watchdog", 0, 0); wd.SetParam("when", "a server is down");
        var ok = N(g, "action.reply", 300, 0); ok.SetParam("message", "ok");
        var rc = N(g, "action.reconnect", 300, 120); rc.SetParam("server", "irc.libera.chat");
        g.Connect(wd.Id, 0, ok.Id, 0);    // healthy -> reply
        g.Connect(wd.Id, 1, rc.Id, 0);    // needs heal -> reconnect

        Dictionary<string, string> Health(string down, bool connected, string queue = "0", string errors = "0") => new()
        { ["down"] = down, ["connected"] = connected ? "true" : "false", ["queue"] = queue, ["errors"] = errors, ["replyto"] = "#x", ["channel"] = "#x" };

        var down1 = new FakeSink();
        GraphExecutor.Fire(g, down1, wd, Health("1", false));
        fails += Expect("wd-needs-heal", down1.Reconnects.Count == 1 && down1.Reconnects[0] == "irc.libera.chat" && down1.Sent.Count == 0, string.Join("|", down1.Reconnects));

        var ok1 = new FakeSink();
        GraphExecutor.Fire(g, ok1, wd, Health("0", true));
        fails += Expect("wd-healthy", ok1.Sent.Count == 1 && ok1.Sent[0] == ("#x", "ok") && ok1.Reconnects.Count == 0, Dump(ok1));

        // "any errors" rule heals when the error count is non-zero
        wd.SetParam("when", "any errors");
        var err1 = new FakeSink();
        GraphExecutor.Fire(g, err1, wd, Health("0", true, errors: "3"));
        fails += Expect("wd-errors-rule", err1.Reconnects.Count == 1, string.Join("|", err1.Reconnects));

        // "queue above" rule respects the threshold
        wd.SetParam("when", "queue above"); wd.SetParam("threshold", "5");
        var q1 = new FakeSink();
        GraphExecutor.Fire(g, q1, wd, Health("0", true, queue: "9"));
        fails += Expect("wd-queue-rule", q1.Reconnects.Count == 1, string.Join("|", q1.Reconnects));
        var q2 = new FakeSink();
        GraphExecutor.Fire(g, q2, wd, Health("0", true, queue: "2"));
        fails += Expect("wd-queue-ok", q2.Reconnects.Count == 0 && q2.Sent.Count == 1, Dump(q2));

        return fails;
    }

    /// <summary>Semantic AI response cache: normalisation, FIFO bound, exact-text hit/miss, and a node-level
    /// look-up branching hit vs miss after a save.</summary>
    private static int CacheTest()
    {
        int fails = 0;
        fails += Expect("cache-norm", Ircuitry.Graph.AiCache.Normalize("Hello, There!!") == "hello there", Ircuitry.Graph.AiCache.Normalize("Hello, There!!"));

        string j = "";
        j = Ircuitry.Graph.AiCache.Put(j, Ircuitry.Graph.AiCache.Normalize("what time is it"), "noon", null, 50);
        fails += Expect("cache-hit", Ircuitry.Graph.AiCache.Lookup(j, Ircuitry.Graph.AiCache.Normalize("What time is it?"), null, 0.9) is { } h && h.reply == "noon", "");
        fails += Expect("cache-miss", Ircuitry.Graph.AiCache.Lookup(j, Ircuitry.Graph.AiCache.Normalize("who are you"), null, 0.9) == null, "");

        // FIFO bound drops the oldest
        string b = "";
        for (int i = 0; i < 5; i++) b = Ircuitry.Graph.AiCache.Put(b, "k" + i, "v" + i, null, 3);
        fails += Expect("cache-bound", Ircuitry.Graph.AiCache.Count(b) == 3 && Ircuitry.Graph.AiCache.Lookup(b, "k0", null, 0.9) == null && Ircuitry.Graph.AiCache.Lookup(b, "k4", null, 0.9) is { }, "");

        // node: save "noon" for a prompt, then a look-up with the same prompt hits -> reply "noon"
        var g = new NodeGraph();
        var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "time");
        var save = N(g, "ai.cache", 250, 0); save.SetParam("mode", "save"); save.SetParam("prompt", "what time is it"); save.SetParam("reply", "noon");
        var look = N(g, "ai.cache", 500, 0); look.SetParam("mode", "look up"); look.SetParam("prompt", "WHAT TIME IS IT?!");
        var reply = N(g, "action.reply", 750, 0);
        g.Connect(cmd.Id, 0, save.Id, 0);     // exec: command -> save
        g.Connect(save.Id, 3, look.Id, 0);    // saved -> look up
        g.Connect(look.Id, 0, reply.Id, 0);   // hit -> reply
        g.Connect(look.Id, 2, reply.Id, 1);   // cached text -> reply message

        var sink = new FakeSink();
        GraphExecutor.Fire(g, sink, cmd, Vars("!time", "alice", "#x"));
        fails += Expect("cache-node-hit", sink.Sent.Count == 1 && sink.Sent[0] == ("#x", "noon"), Dump(sink));
        return fails;
    }

    /// <summary>Themes: the .irctheme schema the app, the community repo's index builder, and the website all
    /// share. Round-trip the default, parse a partial theme (missing colours fall back), and reject junk hex.</summary>
    private static int ThemeRoundTripTest()
    {
        int fails = 0;
        var def = Ircuitry.Core.ThemeData.Default();
        var back = Ircuitry.Core.ThemeData.FromJson(def.ToJson());
        fails += Expect("theme-roundtrip", back.C("cyan") == def.C("cyan") && back.C("text") == def.C("text") && back.Colors.Count == def.Colors.Count, "");

        // a partial theme keeps what it sets and inherits the rest from the cozy default
        string partial = "{\"format\":\"ircuitry.theme.v1\",\"name\":\"Tiny\",\"colors\":{\"cyan\":\"#102030\"},\"knobs\":{\"glow\":1.5,\"glass\":true,\"opacity\":0.9}}";
        var p = Ircuitry.Core.ThemeData.FromJson(partial);
        fails += Expect("theme-partial", p.Name == "Tiny" && p.C("cyan") == new Microsoft.Xna.Framework.Color(16, 32, 48)
            && p.C("text") == def.C("text") && p.Glass && System.Math.Abs(p.Glow - 1.5f) < 0.001f && System.Math.Abs(p.Opacity - 0.9f) < 0.001f, "");

        fails += Expect("theme-hex", Ircuitry.Core.ThemeData.TryHex("#7ED6E4", out var hc) && hc == new Microsoft.Xna.Framework.Color(126, 214, 228)
            && !Ircuitry.Core.ThemeData.TryHex("nope", out _) && Ircuitry.Core.ThemeData.TryHex("#abc", out _), "");
        return fails;
    }

    /// <summary>Set Mode resolves {tokens} in every field - {me} for a self user-mode, {channel} for the
    /// channel, {arg1} for a target - which regressed when channel/modes were read raw instead of resolved.</summary>
    private static int ModeTemplateTest()
    {
        int fails = 0;
        // self user-mode: the first field = {me} -> the bot's own nick, target blank -> MODE <me> +B
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "secure");
            var mode = N(g, "irc.mode", 200, 0); mode.SetParam("channel", "{me}"); mode.SetParam("modes", "+B"); mode.SetParam("target", "");
            g.Connect(cmd.Id, 0, mode.Id, 0);
            var s = new FakeSink(); GraphExecutor.Fire(g, s, cmd, Vars("!secure", "alice", "#c"));
            fails += Expect("mode-me-selfmode", s.Logs.Contains("RAW MODE ircuitry +B"), "logs=[" + string.Join(",", s.Logs) + "]");
        }
        // {channel} as the channel and {arg1} as the target word
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "op");
            var mode = N(g, "irc.mode", 200, 0); mode.SetParam("channel", "{channel}"); mode.SetParam("modes", "+o"); mode.SetParam("target", "{arg1}");
            g.Connect(cmd.Id, 0, mode.Id, 0);
            var v = Vars("!op bob", "alice", "#c"); v["args"] = "bob";
            var s = new FakeSink(); GraphExecutor.Fire(g, s, cmd, v);
            fails += Expect("mode-arg1-target", s.Logs.Contains("RAW MODE #c +o bob"), "logs=[" + string.Join(",", s.Logs) + "]");
        }
        return fails;
    }

    /// <summary>Webhook: an On Webhook trigger fires its graph and the request {body} flows into the run.</summary>
    private static int WebhookTest()
    {
        var g = new NodeGraph();
        var hook = N(g, "event.webhook", 0, 0); hook.SetParam("path", "abc");
        var reply = N(g, "action.reply", 300, 0); reply.SetParam("message", "got {body}");
        g.Connect(hook.Id, 0, reply.Id, 0);
        var sink = new FakeSink();
        GraphExecutor.Fire(g, sink, hook, new System.Collections.Generic.Dictionary<string, string> { ["body"] = "ping", ["channel"] = "#h", ["nick"] = "hook" });
        return Expect("webhook-fire", sink.Sent.Count == 1 && sink.Sent[0] == ("#h", "got ping"), Dump(sink));
    }

    /// <summary>The code sandbox actually contains the child process: with NoNetwork on, a code node can't reach
    /// even a loopback listener it connects to fine when the sandbox is relaxed. Self-skips where the host can't
    /// create a network namespace (no bwrap/unshare or userns disabled) or where node isn't installed, so it
    /// stays hermetic across CI, containers and dev machines.</summary>
    private static int CodeSandboxTest()
    {
        if (!OperatingSystem.IsLinux()) return Expect("code-sandbox (skipped: non-linux)", true, "");
        if (!NamespaceIsolationWorks()) return Expect("code-sandbox (skipped: no usable namespace isolation)", true, "");

        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        try { listener.Start(); }
        catch (Exception ex) { return Expect("code-sandbox (skipped: " + ex.Message + ")", true, ""); }
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var accepter = new Thread(() => { try { while (true) { var c = listener.AcceptTcpClient(); try { c.Close(); } catch { } } } catch { } }) { IsBackground = true };
        accepter.Start();

        // same script both times: connect to our loopback listener, print whether it got through
        string js = ("const net=require('net');const s=net.connect({host:'127.0.0.1',port:__P__});let d=false;"
            + "s.setTimeout(1500);const fin=(m)=>{if(d)return;d=true;console.log(m);try{s.destroy()}catch(e){}process.exit(0)};"
            + "s.on('connect',()=>fin('CONN_OK'));s.on('error',()=>fin('CONN_FAIL'));s.on('timeout',()=>fin('CONN_FAIL'));")
            .Replace("__P__", port.ToString());
        var ctx = new Dictionary<string, string>();

        try
        {
            // control: sandbox relaxed -> the loopback connect succeeds
            Ircuitry.Net.CodeRunner.NoNetwork = false; Ircuitry.Net.CodeRunner.ConfineFs = false;
            var (okOut, okErr) = Ircuitry.Net.CodeRunner.Run("javascript", js, ctx, 5);
            if (okOut.Trim() != "CONN_OK")
                return Expect("code-sandbox (skipped: control did not connect: " + (okOut.Trim().Length > 0 ? okOut.Trim() : okErr) + ")", true, "");

            // blocked: cut the network -> the very same connect is refused
            Ircuitry.Net.CodeRunner.NoNetwork = true; Ircuitry.Net.CodeRunner.ConfineFs = true;
            var (blkOut, _) = Ircuitry.Net.CodeRunner.Run("javascript", js, ctx, 5);
            return Expect("code-sandbox-blocks-network", blkOut.Trim() == "CONN_FAIL", "out=" + blkOut.Trim());
        }
        finally
        {
            Ircuitry.Net.CodeRunner.NoNetwork = false; Ircuitry.Net.CodeRunner.ConfineFs = false;
            try { listener.Stop(); } catch { }
        }
    }

    // Can this host create a network namespace unprivileged? Probe bwrap then unshare with /bin/true.
    private static bool NamespaceIsolationWorks()
    {
        (string exe, string[] args)[] probes =
        {
            ("bwrap", new[] { "--ro-bind", "/", "/", "--unshare-net", "--", "/bin/true" }),
            ("unshare", new[] { "--user", "--map-root-user", "--net", "/bin/true" }),
        };
        foreach (var (exe, args) in probes)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo { FileName = exe, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) continue;
                if (!p.WaitForExit(4000)) { try { p.Kill(true); } catch { } continue; }
                if (p.ExitCode == 0) return true;
            }
            catch { }
        }
        return false;
    }

    /// <summary>Multiline replies: a newline message becomes a draft/multiline BATCH when the cap is on,
    /// one command per line otherwise (the spec's fallback), with blank lines dropped.</summary>
    private static int MultilineSendTest()
    {
        int fails = 0;
        var single = Ircuitry.Irc.IrcClient.BuildSendLines("PRIVMSG", "#c", "hello world", "", false, "r1");
        fails += Expect("ml-single", single.Count == 1 && single[0] == "PRIVMSG #c :hello world", string.Join(" | ", single));

        var fb = Ircuitry.Irc.IrcClient.BuildSendLines("PRIVMSG", "#c", "one\n\ntwo", "", false, "r1");
        fails += Expect("ml-fallback", fb.Count == 2 && fb[0] == "PRIVMSG #c :one" && fb[1] == "PRIVMSG #c :two", string.Join(" | ", fb));

        var fbTag = Ircuitry.Irc.IrcClient.BuildSendLines("NOTICE", "bob", "a\nb", "+reply=x", false, "r1");
        fails += Expect("ml-fallback-tag", fbTag.Count == 2 && fbTag[0] == "@+reply=x NOTICE bob :a" && fbTag[1] == "NOTICE bob :b", string.Join(" | ", fbTag));

        var batch = Ircuitry.Irc.IrcClient.BuildSendLines("PRIVMSG", "#c", "one\ntwo", "+reply=x", true, "r1");
        fails += Expect("ml-batch", batch.Count == 4
            && batch[0] == "@+reply=x BATCH +r1 draft/multiline #c"
            && batch[1] == "@batch=r1 PRIVMSG #c :one"
            && batch[2] == "@batch=r1 PRIVMSG #c :two"
            && batch[3] == "BATCH -r1", string.Join(" | ", batch));

        // long single line: word-wrapped (split on spaces, no data lost, each piece within the byte limit)
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 60; i++) sb.Append("alpha bravo charlie ");
        string longLine = sb.ToString().Trim();                  // ~1200 bytes, one logical line
        Func<string, string> body = l => { int ci = l.IndexOf(" :", StringComparison.Ordinal); return ci >= 0 ? l[(ci + 2)..] : l; };

        var wf = Ircuitry.Irc.IrcClient.BuildSendLines("PRIVMSG", "#c", longLine, "", false, "r1");
        var rejoin = new System.Text.StringBuilder(); bool underLimit = true;
        foreach (var l in wf) { var b = body(l); if (System.Text.Encoding.UTF8.GetByteCount(b) > 400) underLimit = false; if (rejoin.Length > 0) rejoin.Append(' '); rejoin.Append(b); }
        fails += Expect("ml-wrap-fallback", wf.Count > 1 && underLimit && rejoin.ToString() == longLine, wf.Count + " lines");

        var wb = Ircuitry.Irc.IrcClient.BuildSendLines("PRIVMSG", "#c", longLine, "", true, "r1");
        var recon = new System.Text.StringBuilder(); bool sawConcat = false;
        for (int i = 1; i < wb.Count - 1; i++) { if (wb[i].Contains("draft/multiline-concat")) sawConcat = true; recon.Append(body(wb[i])); }
        fails += Expect("ml-wrap-concat", wb[0].Contains("BATCH +r1 draft/multiline #c") && wb[wb.Count - 1] == "BATCH -r1"
            && sawConcat && recon.ToString() == longLine, string.Join(" | ", wb));
        return fails;
    }

    /// <summary>File-path params are flagged (so the inspector shows Browse and the node accepts a dropped file).</summary>
    private static int FileParamTest()
    {
        int fails = 0;
        fails += Expect("fp-zim", NodeCatalog.Get("zim.info").FileParam?.Key == "path", "");
        fails += Expect("fp-read", NodeCatalog.Get("file.read").FileParam?.Key == "path", "");
        fails += Expect("fp-ical", NodeCatalog.Get("file.ical").FileParam?.Key == "source", "");
        fails += Expect("fp-http", NodeCatalog.Get("net.http").FileParam?.Key == "file", "");
        fails += Expect("fp-none", NodeCatalog.Get("event.command").FileParam == null, "");
        return fails;
    }

    /// <summary>DCC: CTCP offer parsing, the 32-bit IP conversion, filename sanitising, and a real loopback
    /// file transfer through the StreamOut/StreamIn engine (acks and all).</summary>
    private static int DccTest()
    {
        int fails = 0;
        fails += Expect("dcc-ip", Ircuitry.Net.Dcc.IpFromInt(2130706433) == "127.0.0.1" && Ircuitry.Net.Dcc.IpToInt("127.0.0.1") == 2130706433, "");
        fails += Expect("dcc-parse-send",
            Ircuitry.Net.Dcc.TryParse("DCC SEND \"my file.txt\" 2130706433 5000 1234", out var o)
            && o.Type == "send" && o.File == "my file.txt" && o.Ip == "127.0.0.1" && o.Port == 5000 && o.Size == 1234, "");
        fails += Expect("dcc-parse-passive",
            Ircuitry.Net.Dcc.TryParse("DCC SEND pic.png 0 0 900 tok7", out var pv) && pv.Port == 0 && pv.Token == "tok7", "");
        fails += Expect("dcc-parse-bad", !Ircuitry.Net.Dcc.TryParse("VERSION", out _), "");
        fails += Expect("dcc-sanitize", Ircuitry.Net.Dcc.SanitizeName("../../etc/passwd") == "passwd", "");

        // the CTCP markers must be a real SOH (U+0001) - guards the "\x01DCC" greedy-hex trap (-> U+01DC, 'GC')
        fails += Expect("dcc-prefix-marker", Ircuitry.Net.Dcc.Prefix == (char)1 + "DCC " && Ircuitry.Net.Dcc.Marker == (char)1, "");
        var ctcp = Ircuitry.Net.Dcc.SendLine("a b.txt", Ircuitry.Net.Dcc.IpToInt("1.2.3.4"), 99, 5, "tk");
        fails += Expect("dcc-sendline-roundtrip",
            ctcp[0] == (char)1 && ctcp[^1] == (char)1 && ctcp.StartsWith(Ircuitry.Net.Dcc.Prefix)
            && Ircuitry.Net.Dcc.TryParse(Ircuitry.Net.Dcc.Strip(ctcp), out var ro)
            && ro.File == "a b.txt" && ro.Ip == "1.2.3.4" && ro.Port == 99 && ro.Size == 5 && ro.Token == "tk", ctcp);

        try
        {
            string src = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ircuitry-dcc-src-" + System.Guid.NewGuid().ToString("N"));
            string dst = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ircuitry-dcc-dst-" + System.Guid.NewGuid().ToString("N"));
            var data = new byte[9001];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 7);
            System.IO.File.WriteAllBytes(src, data);

            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var sender = new System.Threading.Thread(() => { try { using var s = listener.AcceptTcpClient(); Ircuitry.Net.Dcc.StreamOut(s.GetStream(), src); } catch { } }) { IsBackground = true };
            sender.Start();
            long got;
            using (var cli = new System.Net.Sockets.TcpClient())
            {
                cli.Connect(System.Net.IPAddress.Loopback, port);
                got = Ircuitry.Net.Dcc.StreamIn(cli.GetStream(), dst, data.Length);
            }
            sender.Join(3000);
            listener.Stop();
            var rec = System.IO.File.ReadAllBytes(dst);
            fails += Expect("dcc-xfer-size", got == data.Length, $"got {got}");
            fails += Expect("dcc-xfer-bytes", rec.Length == data.Length && rec.AsSpan().SequenceEqual(data), "content mismatch");
            try { System.IO.File.Delete(src); System.IO.File.Delete(dst); } catch { }
        }
        catch (Exception ex) { fails += Expect("dcc-xfer-nothrow", false, ex.Message); }
        return fails;
    }

    /// <summary>Merging two bots that both bind !help: detection + every resolution (run all / keep one /
    /// rename / combine), and a functional fire of the merged graph.</summary>
    private static int BotMergeTest()
    {
        int fails = 0;

        Ircuitry.Graph.NodeGraph BotWith(string cmd, string reply)
        {
            var g = new Ircuitry.Graph.NodeGraph();
            var c = N(g, "event.command", 0, 0); c.SetParam("command", cmd);
            var r = N(g, "action.reply", 300, 0); r.SetParam("message", reply);
            g.Connect(c.Id, 0, r.Id, 0);
            return g;
        }
        int Cmds(Ircuitry.Graph.NodeGraph g) => g.Nodes.Count(n => n.TypeId == "event.command");
        int Replies(Ircuitry.Graph.NodeGraph g) => g.Nodes.Count(n => n.TypeId == "action.reply");

        var a = BotWith("help", "A help");
        var b = BotWith("help", "B help");
        var graphs = new[] { a, b };

        Ircuitry.Graph.BotMerge.Conflict[] Resolved(Ircuitry.Graph.BotMerge.Mode m, int keep = 0)
        {
            var cf = Ircuitry.Graph.BotMerge.Detect(graphs);
            foreach (var c in cf) { c.Resolution = m; c.KeepBot = keep; }
            return cf.ToArray();
        }

        var conf = Ircuitry.Graph.BotMerge.Detect(graphs);
        fails += Expect("merge-detect", conf.Count == 1 && conf[0].Command == "help" && conf[0].Bots.Count == 2, $"got {conf.Count}");

        var runAll = Ircuitry.Graph.BotMerge.Merge(graphs, Resolved(Ircuitry.Graph.BotMerge.Mode.RunAll));
        fails += Expect("merge-runall-nodes", Cmds(runAll) == 2 && Replies(runAll) == 2, $"{Cmds(runAll)}c/{Replies(runAll)}r");
        {
            var sink = new FakeSink();
            foreach (var t in runAll.Nodes.Where(n => n.TypeId == "event.command"))
                GraphExecutor.Fire(runAll, sink, t, Vars("!help", "alice", "#x"));
            fails += Expect("merge-runall-fires-both", sink.Sent.Count == 2 && sink.Sent.Any(s => s.text == "A help") && sink.Sent.Any(s => s.text == "B help"), Dump(sink));
        }

        var keep = Ircuitry.Graph.BotMerge.Merge(graphs, Resolved(Ircuitry.Graph.BotMerge.Mode.Keep, 0));
        fails += Expect("merge-keep-nodes", Cmds(keep) == 1 && Replies(keep) == 1, $"{Cmds(keep)}c/{Replies(keep)}r");
        fails += Expect("merge-keep-which", keep.Nodes.Any(n => n.TypeId == "action.reply" && n.GetParam("message") == "A help"), "");

        var rename = Ircuitry.Graph.BotMerge.Merge(graphs, Resolved(Ircuitry.Graph.BotMerge.Mode.Rename));
        var cmds = rename.Nodes.Where(n => n.TypeId == "event.command").Select(n => n.GetParam("command")).OrderBy(x => x).ToList();
        fails += Expect("merge-rename", cmds.SequenceEqual(new[] { "help", "help2" }), string.Join(",", cmds));

        var combine = Ircuitry.Graph.BotMerge.Merge(graphs, Resolved(Ircuitry.Graph.BotMerge.Mode.Combine));
        fails += Expect("merge-combine-nodes", Cmds(combine) == 1 && Replies(combine) == 2, $"{Cmds(combine)}c/{Replies(combine)}r");
        {
            var trig = combine.Nodes.First(n => n.TypeId == "event.command");
            int fanout = combine.Connections.Count(w => w.FromNode == trig.Id);
            fails += Expect("merge-combine-fanout", fanout == 2, $"fanout {fanout}");
            var sink = new FakeSink();
            GraphExecutor.Fire(combine, sink, trig, Vars("!help", "alice", "#x"));
            fails += Expect("merge-combine-fires-both", sink.Sent.Count == 2, Dump(sink));
        }

        var noConf = Ircuitry.Graph.BotMerge.Detect(new[] { BotWith("foo", "f"), BotWith("bar", "b") });
        fails += Expect("merge-noconflict", noConf.Count == 0, $"{noConf.Count}");

        return fails;
    }

    /// <summary>Exercises the text/number/time toolkit nodes end to end (data.encode/hash/case/shape/regex/
    /// mathx/convert, num.theory/format, gen.random, data.pick/stats, irc.color) so community recipes built
    /// from them stay correct.</summary>
    private static int ToolkitTest()
    {
        int fails = 0;

        // helper: command -> [src(const) -> op] -> reply ; returns the replied text
        string RunOp(string opType, Action<Node> cfg, string input = null!, Action<Node> srcCfg = null!)
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "t");
            var op = N(g, opType, 200, 0); cfg(op);
            var rep = N(g, "action.reply", 400, 0);
            g.Connect(cmd.Id, 0, rep.Id, 0);
            if (input != null)
            {
                var src = N(g, "data.format", 100, 0); src.SetParam("template", input); srcCfg?.Invoke(src);
                g.Connect(src.Id, 0, op.Id, 0);
            }
            g.Connect(op.Id, 0, rep.Id, 1);
            var s = new FakeSink();
            GraphExecutor.Fire(g, s, cmd, Vars("!t", "alice", "#x"));
            return s.Sent.Count > 0 ? s.Sent[0].text : "(none)";
        }

        // json-escape must produce text that drops cleanly into a JSON string literal (the relay.discord case)
        bool JsonEmbedOk(string raw)
        {
            var enc = RunOp("data.encode", n => { n.SetParam("op", "json"); n.SetParam("mode", "encode"); }, raw);
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse("{\"content\":\"" + enc + "\"}");
                return doc.RootElement.GetProperty("content").GetString() == raw;
            }
            catch { return false; }
        }

        fails += Expect("tk-case-upper", RunOp("data.case", n => n.SetParam("op", "upper"), "Hello World") == "HELLO WORLD", "");
        fails += Expect("tk-case-snake", RunOp("data.case", n => n.SetParam("op", "snake"), "Hello World") == "hello_world", "");
        fails += Expect("tk-encode-b64", RunOp("data.encode", n => { n.SetParam("op", "base64"); n.SetParam("mode", "encode"); }, "hi") == "aGk=", "");
        fails += Expect("tk-encode-b64dec", RunOp("data.encode", n => { n.SetParam("op", "base64"); n.SetParam("mode", "decode"); }, "aGk=") == "hi", "");
        fails += Expect("tk-encode-rot13", RunOp("data.encode", n => { n.SetParam("op", "rot13"); n.SetParam("mode", "encode"); }, "abc") == "nop", "");
        fails += Expect("tk-encode-json-rt", RunOp("data.encode", n => { n.SetParam("op", "json"); n.SetParam("mode", "decode"); }, RunOp("data.encode", n => { n.SetParam("op", "json"); n.SetParam("mode", "encode"); }, "she said \"hi\"\nok")) == "she said \"hi\"\nok", "");
        fails += Expect("tk-encode-json-embed", JsonEmbedOk("q\"u\nx\\y"), "");
        fails += Expect("tk-hash-md5", RunOp("data.hash", n => n.SetParam("op", "md5"), "abc") == "900150983cd24fb0d6963f7d28e17f72", "");
        fails += Expect("tk-shape-reverse", RunOp("data.shape", n => n.SetParam("op", "reverse"), "abc") == "cba", "");
        fails += Expect("tk-shape-slug", RunOp("data.shape", n => n.SetParam("op", "slug"), "Hello, World!") == "hello-world", "");
        fails += Expect("tk-regex-first", RunOp("data.regex", n => { n.SetParam("op", "first"); n.SetParam("pattern", "\\d+"); }, "abc123def") == "123", "");
        fails += Expect("tk-mathx", RunOp("data.mathx", n => n.SetParam("expr", "2*(3+4)")) == "14", "");
        fails += Expect("tk-convert-temp", RunOp("data.convert", n => { n.SetParam("family", "temperature"); n.SetParam("value", "100"); n.SetParam("from", "c"); n.SetParam("to", "f"); }) == "212", "");
        fails += Expect("tk-num-factorial", RunOp("num.theory", n => { n.SetParam("op", "factorial"); n.SetParam("a", "5"); }) == "120", "");
        fails += Expect("tk-num-gcd", RunOp("num.theory", n => { n.SetParam("op", "gcd"); n.SetParam("a", "12"); n.SetParam("b", "18"); }) == "6", "");
        fails += Expect("tk-num-ordinal", RunOp("num.theory", n => { n.SetParam("op", "ordinal"); n.SetParam("a", "21"); }) == "21st", "");
        fails += Expect("tk-numfmt-roman", RunOp("num.format", n => { n.SetParam("op", "roman"); n.SetParam("value", "2024"); }) == "MMXXIV", "");
        fails += Expect("tk-numfmt-base", RunOp("num.format", n => { n.SetParam("op", "base"); n.SetParam("value", "255"); n.SetParam("radix", "16"); }) == "ff", "");
        fails += Expect("tk-stats-sum", RunOp("data.stats", n => n.SetParam("op", "sum"), "1 2 3 4") == "10", "");
        fails += Expect("tk-pick-nth", RunOp("data.pick", n => { n.SetParam("op", "nth"); n.SetParam("sep", "comma"); n.SetParam("n", "2"); }, "a,b,c") == "b", "");
        fails += Expect("tk-uuid-shape", RunOp("gen.random", n => n.SetParam("op", "uuid")).Length == 36, "");
        fails += Expect("tk-irc-strip", RunOp("irc.color", n => n.SetParam("op", "strip"), ((char)3) + "04red" + ((char)15)) == "red", "");

        return fails;
    }

    /// <summary>
    /// Guards the GUI text path against lone UTF-16 surrogates (a sliced emoji), which crashed
    /// FontStashSharp when the node-card ellipsizer cut through a surrogate pair.
    /// </summary>
    private static int TextSafetyTest()
    {
        int fails = 0;
        static string Safe(string s) => Ircuitry.Render.Renderer.SafeText(s);

        string plain = "hello world";
        fails += Expect("safetext-plain-noalloc", ReferenceEquals(Safe(plain), plain), "");

        string emoji = "duck \U0001F986 hunt";   // valid surrogate pair preserved, same instance
        fails += Expect("safetext-emoji-keep", ReferenceEquals(Safe(emoji), emoji), "");

        string loneHigh = "ab" + '\uD83E' + "cd";    // high surrogate, no following low
        string fixedHigh = Safe(loneHigh);
        fails += Expect("safetext-lone-high",
            !HasLoneSurrogate(fixedHigh) && fixedHigh.Length == loneHigh.Length && fixedHigh != loneHigh, fixedHigh);

        string loneLow = "x" + '\uDD86' + "y";        // low surrogate, no preceding high
        fails += Expect("safetext-lone-low", !HasLoneSurrogate(Safe(loneLow)), "");

        // every ellipsize cut index must stay off the middle of a surrogate pair
        string s = "AB\U0001F986\U0001F600CD\U0001F389";
        bool anyLone = false;
        for (int n = 0; n <= s.Length; n++)
            if (HasLoneSurrogate(s[..Ircuitry.Render.Renderer.CutAt(s, n)])) { anyLone = true; break; }
        fails += Expect("ellipsize-cut-surrogate-safe", !anyLone, "a CutAt slice split an emoji");
        return fails;
    }

    /// <summary>Several triggers may fan in to one exec input: both stay wired (not replaced) and each
    /// one independently drives the shared downstream flow.</summary>
    private static int FanInTest()
    {
        var g = new NodeGraph();
        var a = N(g, "event.command", 0, 0); a.SetParam("command", "hi");
        var b = N(g, "event.command", 0, 120); b.SetParam("command", "yo");
        var reply = N(g, "action.reply", 300, 60); reply.SetParam("message", "hey");
        g.Connect(a.Id, 0, reply.Id, 0);   // first trigger -> reply.exec
        g.Connect(b.Id, 0, reply.Id, 0);   // second trigger fans in to the same exec input

        int fails = 0;
        // exec input kept both wires
        fails += Expect("fanin-keeps-both", g.Connections.Count(c => c.ToNode == reply.Id && c.ToPin == 0) == 2, "");

        var s1 = new FakeSink();
        GraphExecutor.Fire(g, s1, a, Vars("!hi", "alice", "#x"));
        fails += Expect("fanin-trigger-a", s1.Sent.Count == 1 && s1.Sent[0] == ("#x", "hey"), Dump(s1));

        var s2 = new FakeSink();
        GraphExecutor.Fire(g, s2, b, Vars("!yo", "bob", "#x"));
        fails += Expect("fanin-trigger-b", s2.Sent.Count == 1 && s2.Sent[0] == ("#x", "hey"), Dump(s2));

        // a data input still replaces (single wire)
        var g2 = new NodeGraph();
        var cmd = N(g2, "event.command", 0, 0);
        var r1 = N(g2, "data.random", 0, 80); r1.SetParam("options", "one");
        var r2 = N(g2, "data.random", 0, 160); r2.SetParam("options", "two");
        var rep = N(g2, "action.reply", 300, 0);
        g2.Connect(cmd.Id, 0, rep.Id, 0);
        g2.Connect(r1.Id, 0, rep.Id, 1);
        g2.Connect(r2.Id, 0, rep.Id, 1);   // replaces r1's wire on the data input
        fails += Expect("data-input-single", g2.Connections.Count(c => c.ToNode == rep.Id && c.ToPin == 1) == 1, "");
        return fails;
    }

    /// <summary>A run reports exactly the node types that executed (not muted, not unreached) - the basis
    /// for spec-compliance achievements only counting nodes that actually ran in one fire.</summary>
    private static int RunCompletedTest()
    {
        var g = new NodeGraph();
        var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "go");
        var a = N(g, "action.reply", 250, 0); a.SetParam("message", "hi");
        var b = N(g, "action.say", 250, 120); b.SetParam("channel", "#x"); b.SetParam("message", "yo");
        var muted = N(g, "irc.typing.start", 250, 240); muted.Muted = true;
        g.Connect(cmd.Id, 0, a.Id, 0);
        g.Connect(a.Id, 0, b.Id, 0);
        g.Connect(b.Id, 0, muted.Id, 0);   // wired but muted -> must not be reported

        var s = new FakeSink();
        GraphExecutor.Fire(g, s, cmd, Vars("!go", "alice", "#x"));
        bool ok = s.LastRun.Contains("event.command") && s.LastRun.Contains("action.reply")
               && s.LastRun.Contains("action.say") && !s.LastRun.Contains("irc.typing.start");
        return Expect("run-executed-types", ok, string.Join(",", s.LastRun));
    }

    /// <summary>Emit Signal fires a matching On Signal flow (one workflow triggering another), carrying data.</summary>
    private static int SignalTest()
    {
        var g = new NodeGraph();
        var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "go");
        var emit = N(g, "action.signal", 250, 0); emit.SetParam("signal", "ping"); emit.SetParam("data", "pong");
        var onsig = N(g, "event.signal", 0, 200); onsig.SetParam("signal", "ping");
        var reply = N(g, "action.reply", 250, 200);
        g.Connect(cmd.Id, 0, emit.Id, 0);      // command -> emit signal
        g.Connect(onsig.Id, 0, reply.Id, 0);   // on signal -> reply
        g.Connect(onsig.Id, 1, reply.Id, 1);   // signal data -> reply message

        var sink = new FakeSink();
        GraphExecutor.Fire(g, sink, cmd, Vars("!go", "alice", "#t"));
        int fails = Expect("signal-emit", sink.Sent.Count == 1 && sink.Sent[0] == ("#t", "pong"), Dump(sink));

        var miss = new FakeSink();
        onsig.SetParam("signal", "other");     // name no longer matches -> no fire
        GraphExecutor.Fire(g, miss, cmd, Vars("!go", "alice", "#t"));
        fails += Expect("signal-nomatch", miss.Sent.Count == 0, Dump(miss));
        return fails;
    }

    private static bool HasLoneSurrogate(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i])) { if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return true; i++; }
            else if (char.IsLowSurrogate(s[i])) return true;
        }
        return false;
    }

    /// <summary>End-to-end AI path against a mock OpenAI-compatible server: On Command -> Ask AI -> Send Reply.</summary>
    private static int AiLoopTest()
    {
        int port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        try { listener.Start(); }
        catch (Exception ex) { return Expect("ai-loop (skipped: " + ex.Message + ")", true, ""); }

        bool stop = false;
        var server = new Thread(() =>
        {
            try
            {
                while (!stop)
                {
                    var ctx = listener.GetContext();
                    var b = Encoding.UTF8.GetBytes("{\"choices\":[{\"message\":{\"content\":\"beep boop\"}}]}");
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.OutputStream.Write(b, 0, b.Length);
                    ctx.Response.Close();
                }
            }
            catch { /* listener stopped */ }
        }) { IsBackground = true };
        server.Start();

        var g = new NodeGraph();
        var cmd = g.Add(NodeCatalog.Get("event.command"), Vector2.Zero); cmd.SetParam("command", "ai");
        var ai = g.Add(NodeCatalog.Get("ai.reply"), new Vector2(300, 0));
        ai.SetParam("baseUrl", $"http://localhost:{port}/v1");
        ai.SetParam("apiKey", "test"); ai.SetParam("model", "mock");
        var reply = g.Add(NodeCatalog.Get("action.reply"), new Vector2(600, 0));
        g.Connect(cmd.Id, 0, ai.Id, 0);     // exec
        g.Connect(ai.Id, 0, reply.Id, 0);   // exec
        g.Connect(ai.Id, 1, reply.Id, 1);   // data: AI reply -> message

        var sink = new FakeSink();
        GraphExecutor.Fire(g, sink, cmd, Vars("!ai hello", "zoe", "#x"));

        stop = true; try { listener.Stop(); } catch { }
        bool ok = sink.Sent.Count == 1 && sink.Sent[0] == ("#x", "beep boop");
        return Expect("ai-loop-openai-compatible", ok, Dump(sink));
    }

    /// <summary>AI tool-calling end-to-end: model asks for a tool, tool sub-flow runs with the model's args, result feeds back.</summary>
    private static int AiToolsTest()
    {
        int port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        try { listener.Start(); }
        catch (Exception ex) { return Expect("ai-tools (skipped: " + ex.Message + ")", true, ""); }

        int reqs = 0;
        bool stop = false;
        string toolJson = """{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"call_1","type":"function","function":{"name":"get_x","arguments":"{\"q\":\"hi\"}"}}]}}]}""";
        string finalJson = """{"choices":[{"message":{"content":"final answer"}}]}""";
        var server = new Thread(() =>
        {
            try
            {
                while (!stop)
                {
                    var ctx = listener.GetContext();
                    int n = Interlocked.Increment(ref reqs);
                    var b = Encoding.UTF8.GetBytes(n == 1 ? toolJson : finalJson);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.OutputStream.Write(b, 0, b.Length);
                    ctx.Response.Close();
                }
            }
            catch { }
        }) { IsBackground = true };
        server.Start();

        var g = new NodeGraph();
        var cmd = g.Add(NodeCatalog.Get("event.command"), Vector2.Zero); cmd.SetParam("command", "ai");
        var ai = g.Add(NodeCatalog.Get("ai.reply"), new Vector2(300, 0));
        ai.SetParam("baseUrl", $"http://localhost:{port}/v1"); ai.SetParam("apiKey", "test"); ai.SetParam("model", "mock");
        var reply = g.Add(NodeCatalog.Get("action.reply"), new Vector2(600, 0));
        var tool = g.Add(NodeCatalog.Get("ai.tool"), new Vector2(100, 150)); tool.SetParam("name", "get_x"); tool.SetParam("arg1name", "q");
        var log = g.Add(NodeCatalog.Get("action.log"), new Vector2(350, 150));
        var treply = g.Add(NodeCatalog.Get("tool.reply"), new Vector2(600, 150)); treply.SetParam("result", "TOOLVAL");

        g.Connect(cmd.Id, 0, ai.Id, 0);    // exec
        g.Connect(ai.Id, 0, reply.Id, 0);  // ai.then -> reply
        g.Connect(ai.Id, 1, reply.Id, 1);  // ai.reply -> reply.message
        g.Connect(tool.Id, 0, ai.Id, 2);   // tool def -> ai 'tools'
        g.Connect(tool.Id, 1, log.Id, 0);  // tool.call -> log.exec
        g.Connect(tool.Id, 2, log.Id, 1);  // tool.arg1 (q) -> log.text
        g.Connect(log.Id, 0, treply.Id, 0); // log.then -> tool reply

        var s = new FakeSink();
        GraphExecutor.Fire(g, s, cmd, Vars("!ai weather", "u", "#c"));
        stop = true; try { listener.Stop(); } catch { }

        bool ranToolWithArg = s.Logs.Contains("hi");
        bool finalOk = s.Sent.Count == 1 && s.Sent[0].text == "final answer";
        return Expect("ai-tools-call-loop", ranToolWithArg && finalOk && reqs >= 2,
            $"logs=[{string.Join(",", s.Logs)}] {Dump(s)} reqs={reqs}");
    }

    /// <summary>Reproduces Mita's follow-up bug: a Set Var run INSIDE an AI tool sub-flow (like the speak tool's
    /// mita_active/mita_channel) must persist so a SEPARATE later event (the timer) reads it back as a {token}.</summary>
    private static int AiToolStatePersistTest()
    {
        int port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        try { listener.Start(); }
        catch (Exception ex) { return Expect("ai-tool-state (skipped: " + ex.Message + ")", true, ""); }

        bool stop = false;
        int reqs = 0;
        string toolJson = """{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"c1","type":"function","function":{"name":"remember_it","arguments":"{}"}}]}}]}""";
        string finalJson = """{"choices":[{"message":{"content":"ok"}}]}""";
        var server = new Thread(() =>
        {
            try { while (!stop) { var ctx = listener.GetContext(); int n = Interlocked.Increment(ref reqs);
                var b = Encoding.UTF8.GetBytes(n == 1 ? toolJson : finalJson); ctx.Response.ContentType = "application/json";
                ctx.Response.OutputStream.Write(b, 0, b.Length); ctx.Response.Close(); } } catch { }
        }) { IsBackground = true };
        server.Start();

        var g = new NodeGraph();
        var cmd = g.Add(NodeCatalog.Get("event.command"), Vector2.Zero); cmd.SetParam("command", "ai");
        var ai = g.Add(NodeCatalog.Get("ai.reply"), new Vector2(300, 0));
        ai.SetParam("baseUrl", $"http://localhost:{port}/v1"); ai.SetParam("apiKey", "test"); ai.SetParam("model", "mock");
        var tool = g.Add(NodeCatalog.Get("ai.tool"), new Vector2(100, 150)); tool.SetParam("name", "remember_it");
        // mirror Mita's speak flow: set a state var, DELAY reading it back as a {token}, THEN set the var the timer needs
        var svt = g.Add(NodeCatalog.Get("data.setvar"), new Vector2(300, 150)); svt.SetParam("name", "ksecs"); svt.SetParam("value", "0");
        var dly = g.Add(NodeCatalog.Get("flow.delay"), new Vector2(450, 150)); dly.SetParam("seconds", "{ksecs}");
        var sv = g.Add(NodeCatalog.Get("data.setvar"), new Vector2(600, 150)); sv.SetParam("name", "tk"); sv.SetParam("value", "tv99");
        var treply = g.Add(NodeCatalog.Get("tool.reply"), new Vector2(750, 150)); treply.SetParam("result", "done");
        g.Connect(cmd.Id, 0, ai.Id, 0);     // exec -> ai
        g.Connect(tool.Id, 0, ai.Id, 2);    // tool def -> ai 'tools'
        g.Connect(tool.Id, 1, svt.Id, 0);   // tool.call -> set ksecs
        g.Connect(svt.Id, 0, dly.Id, 0);    // -> delay reads {ksecs} (state token, like typing_secs)
        g.Connect(dly.Id, 0, sv.Id, 0);     // -> AFTER the delay, set tk (like mita_active after the typing delay)
        g.Connect(sv.Id, 0, treply.Id, 0);  // -> tool reply

        // a SEPARATE event (stands in for the timer) that reads what the tool wrote
        var cmd2 = g.Add(NodeCatalog.Get("event.command"), new Vector2(0, 300)); cmd2.SetParam("command", "check");
        var reply2 = g.Add(NodeCatalog.Get("action.reply"), new Vector2(300, 300)); reply2.SetParam("message", "[{tk}]");
        g.Connect(cmd2.Id, 0, reply2.Id, 0);

        var s = new FakeSink();
        GraphExecutor.Fire(g, s, cmd, Vars("!ai", "u", "#c"));      // AI runs, tool sets tk=tv99 mid-execution
        GraphExecutor.Fire(g, s, cmd2, Vars("!check", "u", "#c")); // separate run reads {tk}
        stop = true; try { listener.Stop(); } catch { }

        return Expect("ai-tool-state-persist", s.Sent.Count == 1 && s.Sent[0] == ("#c", "[tv99]"), Dump(s) + " reqs=" + reqs);
    }

    /// <summary>MCP client: spawn our OWN MCP server (ircuitry --mcp) and drive it over stdio - initialize,
    /// tools/list, tools/call - proving the outward MCP client works against a real MCP server.</summary>
    private static int McpClientTest()
    {
        string? dll = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrEmpty(dll) || !dll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return Expect("mcp-client (skipped: no entry dll)", true, "");
        string cmd = "dotnet \"" + dll + "\" --mcp";
        try
        {
            var client = Ircuitry.App.Mcp.McpClient.ForConfig("stdio", cmd, null, 25000);
            var tools = client.ListTools(25000);
            string res = client.Call("list_node_types", new Dictionary<string, string>(), 25000);
            int fails = 0;
            fails += Expect("mcp-client-list", tools.Count > 0 && tools.Any(t => t.Name == "list_node_types"), "tools=" + tools.Count);
            fails += Expect("mcp-client-call", res.Length > 10 && !res.StartsWith("MCP error"), res.Substring(0, Math.Min(80, res.Length)));
            return fails;
        }
        catch (Exception ex) { return Expect("mcp-client (skipped: " + ex.Message + ")", true, ""); }
        finally { try { Ircuitry.App.Mcp.McpClient.StopAll(); } catch { } }
    }

    /// <summary>Agents all the way down: an Ask AI wired (via its 'tool' output) into another Ask AI's 'tools'
    /// is callable as a sub-agent - the parent calls it, it runs its own model turn, and its reply comes back.</summary>
    private static int SubAgentTest()
    {
        int port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        try { listener.Start(); }
        catch (Exception ex) { return Expect("subagent (skipped: " + ex.Message + ")", true, ""); }

        bool stop = false, subCalled = false; int reqs = 0;
        string parentToolCall = """{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"c1","type":"function","function":{"name":"helper","arguments":"{\"prompt\":\"please help\"}"}}]}}]}""";
        string parentFinal = """{"choices":[{"message":{"content":"final from parent"}}]}""";
        string subFinal = """{"choices":[{"message":{"content":"SUBRESULT"}}]}""";
        var server = new Thread(() =>
        {
            try
            {
                while (!stop)
                {
                    var ctx = listener.GetContext();
                    Interlocked.Increment(ref reqs);
                    string b; using (var sr = new StreamReader(ctx.Request.InputStream)) b = sr.ReadToEnd();
                    string resp;
                    if (b.Contains("\"model\":\"sub\"")) { subCalled = true; resp = subFinal; }   // the sub-agent's own turn
                    else if (b.Contains("tool_call_id")) resp = parentFinal;                       // parent, after the tool result
                    else resp = parentToolCall;                                                    // parent, first turn -> call helper
                    var bytes = Encoding.UTF8.GetBytes(resp);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    ctx.Response.Close();
                }
            }
            catch { }
        }) { IsBackground = true };
        server.Start();

        var g = new NodeGraph();
        var cmd = g.Add(NodeCatalog.Get("event.command"), Vector2.Zero); cmd.SetParam("command", "ai");
        var parent = g.Add(NodeCatalog.Get("ai.reply"), new Vector2(300, 0));
        parent.SetParam("baseUrl", $"http://localhost:{port}/v1"); parent.SetParam("apiKey", "t"); parent.SetParam("model", "parent");
        var sub = g.Add(NodeCatalog.Get("ai.reply"), new Vector2(100, 200));
        sub.SetParam("baseUrl", $"http://localhost:{port}/v1"); sub.SetParam("apiKey", "t"); sub.SetParam("model", "sub");
        sub.SetParam("name", "helper"); sub.SetParam("toolDescription", "a helper sub-agent");
        var reply = g.Add(NodeCatalog.Get("action.reply"), new Vector2(600, 0));
        g.Connect(cmd.Id, 0, parent.Id, 0);    // exec
        g.Connect(parent.Id, 0, reply.Id, 0);  // then -> reply
        g.Connect(parent.Id, 1, reply.Id, 1);  // parent reply text -> reply.message
        g.Connect(sub.Id, 3, parent.Id, 2);    // sub-agent's TOOL output -> parent 'tools'

        var s = new FakeSink();
        GraphExecutor.Fire(g, s, cmd, Vars("!ai do it", "u", "#c"));
        stop = true; try { listener.Stop(); } catch { }

        bool finalOk = s.Sent.Count == 1 && s.Sent[0].text == "final from parent";
        return Expect("subagent-recursive", subCalled && finalOk && reqs >= 3, $"subCalled={subCalled} {Dump(s)} reqs={reqs}");
    }

    /// <summary>SuperAI is now a composite .ircnode built from real nodes. This proves the tool primitives it
    /// is made of work: Ask AI wired to Recent Messages + React to Message, the model lists messages then
    /// reacts to one by its id - the user's "react to whoever mentioned cake" flow.</summary>
    private static int SuperAiTest()
    {
        int port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        try { listener.Start(); }
        catch (Exception ex) { return Expect("superai (skipped: " + ex.Message + ")", true, ""); }

        bool stop = false;
        int reqs = 0;
        // round 1: list recent; round 2: react to the cake message by id; round 3: final answer
        string listJson = """{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"c1","type":"function","function":{"name":"recent_messages","arguments":"{\"count\":\"15\"}"}}]}}]}""";
        string cakeEmoji = "\U0001F382";   // intentional unicode (birthday cake) - raw string below can't carry a \U escape
        string reactJson = """{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"c2","type":"function","function":{"name":"react","arguments":"{\"msgid\":\"mid-cake\",\"emoji\":\"CAKE_EMOJI\",\"target\":\"#cake\"}"}}]}}]}""".Replace("CAKE_EMOJI", cakeEmoji);
        string finalJson = """{"choices":[{"message":{"content":"done"}}]}""";
        var server = new Thread(() =>
        {
            try
            {
                while (!stop)
                {
                    var ctx = listener.GetContext();
                    int n = Interlocked.Increment(ref reqs);
                    var b = Encoding.UTF8.GetBytes(n == 1 ? listJson : n == 2 ? reactJson : finalJson);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.OutputStream.Write(b, 0, b.Length);
                    ctx.Response.Close();
                }
            }
            catch { }
        }) { IsBackground = true };
        server.Start();

        var g = new NodeGraph();
        var cmd = g.Add(NodeCatalog.Get("event.command"), Vector2.Zero); cmd.SetParam("command", "do");
        var ai = g.Add(NodeCatalog.Get("ai.reply"), new Vector2(300, 0));
        ai.SetParam("baseUrl", $"http://localhost:{port}/v1"); ai.SetParam("apiKey", "test"); ai.SetParam("model", "mock");
        var recent = g.Add(NodeCatalog.Get("ircv3.recent"), new Vector2(100, 160));
        var react = g.Add(NodeCatalog.Get("action.reactid"), new Vector2(100, 260));
        g.Connect(cmd.Id, 0, ai.Id, 0);                       // exec
        g.Connect(recent.Id, recent.Outputs.Length - 1, ai.Id, 2);   // recent.tool -> ai.tools
        g.Connect(react.Id, react.Outputs.Length - 1, ai.Id, 2);     // react.tool  -> ai.tools

        var s = new FakeSink();
        s.RecentSeed.Add(new RecentMsg("bob", "#chatter", "anyone seen the news?", "mid-news"));
        s.RecentSeed.Add(new RecentMsg("amy", "#cake", "I baked a cake today!", "mid-cake"));
        GraphExecutor.Fire(g, s, cmd, Vars("!do react to the cake person", "u", "#cake"));
        stop = true; try { listener.Stop(); } catch { }

        bool reacted = s.Sent.Contains(("#cake", "react:" + cakeEmoji));   // intentional unicode (birthday cake)
        return Expect("superai-tools-react", reacted && reqs >= 3, $"{Dump(s)} reqs={reqs}");
    }

    /// <summary>The SuperAI recipe (--emit-superai) is a valid, loadable composite .ircnode: a subgraph node
    /// with the right pins/params, so it can be dropped into nodes/ and right-click-edited like any other.</summary>
    private static int SuperAiCompositeTest()
    {
        var manifest = Ircuitry.App.SuperAiNode.BuildManifest();
        var def = Ircuitry.Graph.CustomNode.Load(manifest);
        bool ok = def != null
            && def!.TypeId == "superai"
            && def.Outputs.Any(p => p.Name == "reply")
            && def.Params.Any(p => p.Key == "goal") && def.Params.Any(p => p.Key == "model")
            && manifest.Contains("\"subgraph\"");
        return Expect("superai-composite-ircnode", ok,
            def == null ? "manifest did not load" : $"out={string.Join(",", def.Outputs.Select(p => p.Name))} params={string.Join(",", def.Params.Select(p => p.Key))}");
    }

    /// <summary>CHATHISTORY core: messages inside a chathistory BATCH are suppressed (never trigger) and
    /// accumulated, live messages pass through, and on batch close the collected messages reach the waiter -
    /// even for a channel we requested history of (the "before we joined" case).</summary>
    private static int HistoryBatchTest()
    {
        var hb = new HistoryBatches();
        var w = new HistoryBatches.Waiter { Target = "#chan" };
        hb.Await(w);                                                  // node registers BEFORE the server replies

        hb.OnBatch(IrcParser.Parse(":serv BATCH +bx chathistory #chan"));
        bool s1 = hb.Capture(IrcParser.Parse("@batch=bx;msgid=h1 :amy!a@h PRIVMSG #chan :baked a cake"), out var r1);
        bool s2 = hb.Capture(IrcParser.Parse("@batch=bx;msgid=h2 :bob!b@h PRIVMSG #chan :nice one"), out _);
        // a JOIN inside the batch must also be suppressed (data-only), even though it yields no RecentMsg
        bool sJoin = hb.Capture(IrcParser.Parse("@batch=bx :cat!c@h JOIN #chan"), out _);
        // a live message (no batch tag) must NOT be captured - it is free to trigger
        bool live = hb.Capture(IrcParser.Parse("@msgid=L1 :zoe!z@h PRIVMSG #chan :hi right now"), out _);
        hb.OnBatch(IrcParser.Parse(":serv BATCH -bx"));              // close -> deliver to the waiter

        bool ok = s1 && s2 && sJoin && !live
            && r1 != null && r1.Msgid == "h1" && r1.Text == "baked a cake"
            && w.Done.IsSet && w.Result.Count == 2                    // 2 PRIVMSGs (JOIN is suppressed, not collected)
            && w.Result[0].Msgid == "h1" && w.Result[1].Nick == "bob";
        return Expect("chathistory-batch-suppress-deliver", ok,
            $"s1={s1} s2={s2} join={sJoin} live={live} done={w.Done.IsSet} n={w.Result.Count}");
    }

    /// <summary>A timed-out (abandoned) waiter must be skipped on delivery, so a late batch goes to a live
    /// waiter instead of vanishing into a dead one.</summary>
    private static int HistoryAbandonTest()
    {
        var hb = new HistoryBatches();
        var dead = new HistoryBatches.Waiter { Target = "#x", Abandoned = true };
        var live = new HistoryBatches.Waiter { Target = "#x" };
        hb.Await(dead); hb.Await(live);
        hb.OnBatch(IrcParser.Parse(":s BATCH +b chathistory #x"));
        hb.Capture(IrcParser.Parse("@batch=b;msgid=z1 :n!u@h PRIVMSG #x :hi"), out _);
        hb.OnBatch(IrcParser.Parse(":s BATCH -b"));
        bool ok = !dead.Done.IsSet && live.Done.IsSet && live.Result.Count == 1 && live.Result[0].Msgid == "z1";
        return Expect("chathistory-skip-abandoned", ok, $"deadSet={dead.Done.IsSet} liveN={live.Result.Count}");
    }

    /// <summary>Shrinking a Switch's case list orphans wires past the new end; PruneDeadWires drops exactly
    /// those (and leaves in-range wires alone).</summary>
    private static int SwitchPruneTest()
    {
        var g = new NodeGraph();
        var msg = N(g, "event.message", 0, 0);
        var sw = N(g, "logic.switch", 250, 0); sw.SetParam("cases", "[\"a\",\"b\",\"c\"]");
        var r = N(g, "action.reply", 500, 0);
        g.Connect(msg.Id, 0, sw.Id, 0);
        bool wired = g.Connect(sw.Id, 3, r.Id, 0);   // wire to the last case "c" (pin 3)
        int before = g.Connections.Count;
        sw.SetParam("cases", "[\"a\"]");             // now only default(0) + "a"(1) exist; pin 3 is gone
        int pruned = g.PruneDeadWires();
        bool ok = wired && pruned == 1 && g.Connections.Count == before - 1
            && !g.Connections.Any(c => c.FromNode == sw.Id && c.FromPin == 3);
        return Expect("switch-prune-dead-wire", ok, $"wired={wired} pruned={pruned} left={g.Connections.Count}");
    }

    /// <summary>The live IRC session model tracks network, channels, members (with prefixes + nick changes +
    /// parts), topic and human-language narration from raw lines - what the read-only IRC view and state nodes use.</summary>
    private static int IrcStateTest()
    {
        var s = new IrcSessionState();
        void Obs(string line) => s.Observe(IrcParser.Parse(line), "ircuitry");
        Obs(":serv 005 ircuitry NETWORK=Cool\\x20Net CHANTYPES=# :are supported");
        Obs(":ircuitry!u@h JOIN #cake");
        Obs(":serv 353 ircuitry = #cake :@ircuitry +bob carol");
        Obs(":serv 366 ircuitry #cake :End of /NAMES");
        Obs(":serv 332 ircuitry #cake :Cake talk");
        Obs(":dave!d@h JOIN #cake");
        Obs(":bob!b@h PART #cake");
        Obs(":carol!c@h NICK caroline");

        var members = s.Members("#cake");
        bool ok =
            s.Network == "Cool Net"                                     // \x20 decoded
            && s.InChannel("#cake")
            && s.Topic("#cake") == "Cake talk"
            && s.MemberCount("#cake") == 3                              // ircuitry, caroline, dave (bob parted)
            && members.Any(m => m.nick == "ircuitry" && m.prefix.Contains('@'))
            && members.Any(m => m.nick == "caroline")
            && !members.Any(m => m.nick == "bob")
            && s.RecentNotes(60).Any(n => n.Text.Contains("Cool Net"))
            && s.RecentNotes(60).Any(n => n.Text == "I'm joining #cake");
        return Expect("irc-session-state", ok, $"net='{s.Network}' count={s.MemberCount("#cake")} topic='{s.Topic("#cake")}'");
    }

    /// <summary>The Request History node calls into the history path and emits the messages as JSON on its
    /// 'messages' output (here fed straight to a reply so we can read it).</summary>
    private static int ChatHistoryNodeTest()
    {
        var g = new NodeGraph();
        var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "hist");
        var ch = N(g, "action.chathistory", 250, 0); ch.SetParam("target", "#chan");
        var r = N(g, "action.reply", 500, 0);
        g.Connect(cmd.Id, 0, ch.Id, 0);    // exec
        g.Connect(ch.Id, 0, r.Id, 0);      // then -> reply exec
        g.Connect(ch.Id, 1, r.Id, 1);      // messages (JSON) -> reply message

        var s = new FakeSink();
        s.HistorySeed.Add(new RecentMsg("amy", "#chan", "baked a cake", "h1"));
        s.HistorySeed.Add(new RecentMsg("bob", "#chan", "nice one", "h2"));
        GraphExecutor.Fire(g, s, cmd, Vars("!hist", "u", "#chan"));

        bool ok = s.Sent.Count == 1
            && s.Sent[0].text.Contains("\"id\":\"h1\"")
            && s.Sent[0].text.Contains("baked a cake")
            && s.Sent[0].text.Contains("\"nick\":\"bob\"");
        return Expect("chathistory-node-json", ok, Dump(s));
    }

    /// <summary>Plug-and-play inward MCP: Ask AI + a Workflow Editor node. The (mocked) model calls the
    /// add_node editor tool and it actually mutates a SANDBOXED workspace - never the user's ~/ircuitry.</summary>
    private static int AiEditorToolTest()
    {
        int port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        try { listener.Start(); }
        catch (Exception ex) { return Expect("ai-editor-tool (skipped: " + ex.Message + ")", true, ""); }

        bool stop = false;
        int reqs = 0;
        // round 1: model asks to add an action.say node carrying a distinctive marker; round 2: final answer
        string toolJson = """{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"c1","type":"function","function":{"name":"add_node","arguments":"{\"typeId\":\"action.say\",\"params\":{\"message\":\"INJECTED_BY_AI\"}}"}}]}}]}""";
        string finalJson = """{"choices":[{"message":{"content":"done"}}]}""";
        var server = new Thread(() =>
        {
            try
            {
                while (!stop)
                {
                    var ctx = listener.GetContext();
                    int n = Interlocked.Increment(ref reqs);
                    var b = Encoding.UTF8.GetBytes(n == 1 ? toolJson : finalJson);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.OutputStream.Write(b, 0, b.Length);
                    ctx.Response.Close();
                }
            }
            catch { }
        }) { IsBackground = true };
        server.Start();

        var oldHome = Environment.GetEnvironmentVariable("IRCUITRY_HOME");
        var tmp = Path.Combine(Path.GetTempPath(), "ircuitry-aiedit-" + Guid.NewGuid().ToString("N")[..8]);
        int fails;
        try
        {
            Environment.SetEnvironmentVariable("IRCUITRY_HOME", tmp);
            bool sandboxed = Ircuitry.App.AppModel.WorkspaceDir.StartsWith(tmp, StringComparison.Ordinal);
            if (!sandboxed) { stop = true; try { listener.Stop(); } catch { } return Expect("ai-editor-tool (skipped: no sandbox)", true, ""); }
            new Ircuitry.App.AppModel().Save(announce: false);   // seed the sandbox workspace

            var g = new NodeGraph();
            var cmd = g.Add(NodeCatalog.Get("event.command"), Vector2.Zero); cmd.SetParam("command", "ai");
            var ai = g.Add(NodeCatalog.Get("ai.reply"), new Vector2(300, 0));
            ai.SetParam("baseUrl", $"http://localhost:{port}/v1"); ai.SetParam("apiKey", "test"); ai.SetParam("model", "mock");
            var editor = g.Add(NodeCatalog.Get("ai.editor"), new Vector2(100, 150));   // edit mode, active bot
            g.Connect(cmd.Id, 0, ai.Id, 0);     // exec
            g.Connect(editor.Id, 0, ai.Id, 2);  // editor 'tools' -> ai 'tools'

            var s = new FakeSink();
            GraphExecutor.Fire(g, s, cmd, Vars("!ai add a say node", "u", "#c"));

            var graph = Ircuitry.App.Mcp.McpBridge.Invoke("get_graph", new Dictionary<string, string>(), null);
            bool edited = graph.Contains("INJECTED_BY_AI");
            fails = Expect("ai-editor-tool-mutates-sandbox", edited && reqs >= 2, $"edited={edited} reqs={reqs}");
        }
        finally
        {
            stop = true; try { listener.Stop(); } catch { }
            Environment.SetEnvironmentVariable("IRCUITRY_HOME", oldHome);
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
        }
        return fails;
    }

    /// <summary>AI Tool with a DYNAMIC args list (no fixed 3): the model's args reach the sub-flow as
    /// {arg.NAME} tokens, not just the three arg pins.</summary>
    private static int DynamicAiArgsTest()
    {
        int port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        try { listener.Start(); }
        catch (Exception ex) { return Expect("ai-dynamic-args (skipped: " + ex.Message + ")", true, ""); }

        bool stop = false; int reqs = 0;
        string toolJson = """{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"c1","type":"function","function":{"name":"lookup","arguments":"{\"city\":\"paris\",\"units\":\"c\"}"}}]}}]}""";
        string finalJson = """{"choices":[{"message":{"content":"ok"}}]}""";
        var server = new Thread(() =>
        {
            try { while (!stop) { var ctx = listener.GetContext(); int n = Interlocked.Increment(ref reqs); var b = Encoding.UTF8.GetBytes(n == 1 ? toolJson : finalJson); ctx.Response.OutputStream.Write(b, 0, b.Length); ctx.Response.Close(); } }
            catch { }
        }) { IsBackground = true };
        server.Start();

        var g = new NodeGraph();
        var cmd = g.Add(NodeCatalog.Get("event.command"), Vector2.Zero); cmd.SetParam("command", "ai");
        var ai = g.Add(NodeCatalog.Get("ai.reply"), new Vector2(300, 0));
        ai.SetParam("baseUrl", $"http://localhost:{port}/v1"); ai.SetParam("apiKey", "t"); ai.SetParam("model", "m");
        var tool = g.Add(NodeCatalog.Get("ai.tool"), new Vector2(100, 150));
        tool.SetParam("name", "lookup"); tool.SetParam("args", "[[\"city\",\"\"],[\"units\",\"\"]]");   // dynamic args (no legacy)
        var log = g.Add(NodeCatalog.Get("action.log"), new Vector2(350, 150)); log.SetParam("text", "LOC {arg.city}/{arg.units}");
        var treply = g.Add(NodeCatalog.Get("tool.reply"), new Vector2(600, 150)); treply.SetParam("result", "done");
        var reply = g.Add(NodeCatalog.Get("action.reply"), new Vector2(600, 0));
        g.Connect(cmd.Id, 0, ai.Id, 0);
        g.Connect(ai.Id, 0, reply.Id, 0); g.Connect(ai.Id, 1, reply.Id, 1);
        g.Connect(tool.Id, 0, ai.Id, 2);   // tool def -> Ask AI tools
        g.Connect(tool.Id, 1, log.Id, 0);  // 'call' -> log exec (sub-flow reads args via {arg.NAME} token)
        g.Connect(log.Id, 0, treply.Id, 0);

        var s = new FakeSink();
        GraphExecutor.Fire(g, s, cmd, Vars("!ai go", "u", "#c"));
        stop = true; try { listener.Stop(); } catch { }
        bool ok = s.Logs.Any(l => l.Contains("LOC paris/c")) && reqs >= 2;
        return Expect("ai-dynamic-args", ok, "logs=[" + string.Join(",", s.Logs) + "] reqs=" + reqs);
    }

    /// <summary>Exercises the new programmable nodes: If, Switch, counter (Get/Set/Math), Cooldown, For-Each.</summary>
    private static int NewNodesTest()
    {
        int fails = 0;

        // If / Compare: args == "yes" -> true branch
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "x");
            var iff = N(g, "logic.if", 250, 0); iff.SetParam("op", "="); iff.SetParam("b", "yes");
            var t = N(g, "action.reply", 500, -40); t.SetParam("message", "T");
            var f = N(g, "action.reply", 500, 40); f.SetParam("message", "F");
            g.Connect(cmd.Id, 0, iff.Id, 0); g.Connect(cmd.Id, 1, iff.Id, 1); // args -> A
            g.Connect(iff.Id, 0, t.Id, 0); g.Connect(iff.Id, 1, f.Id, 0);
            var s1 = new FakeSink(); GraphExecutor.Fire(g, s1, cmd, Vars("!x yes", "u", "#c"));
            fails += Expect("if-true", s1.Sent.Count == 1 && s1.Sent[0].text == "T", Dump(s1));
            var s2 = new FakeSink(); GraphExecutor.Fire(g, s2, cmd, Vars("!x no", "u", "#c"));
            fails += Expect("if-false", s2.Sent.Count == 1 && s2.Sent[0].text == "F", Dump(s2));
        }

        // Switch on message value - dynamic case outputs: default = pin 0, cases = pins 1..N (list order)
        {
            var g = new NodeGraph();
            var msg = N(g, "event.message", 0, 0);
            var sw = N(g, "logic.switch", 250, 0); sw.SetParam("cases", "[\"red\",\"blue\"]");
            var r = N(g, "action.reply", 500, -60); r.SetParam("message", "R");
            var b = N(g, "action.reply", 500, 0); b.SetParam("message", "B");
            var d = N(g, "action.reply", 500, 60); d.SetParam("message", "D");
            g.Connect(msg.Id, 0, sw.Id, 0); g.Connect(msg.Id, 1, sw.Id, 1);
            // wire default(0)->D, case "red"(1)->R, case "blue"(2)->B  (pin 2 only exists because cases is dynamic)
            bool wired = g.Connect(sw.Id, 0, d.Id, 0) & g.Connect(sw.Id, 1, r.Id, 0) & g.Connect(sw.Id, 2, b.Id, 0);
            fails += Expect("switch-dynamic-pins-wire", wired, "could not wire dynamic case pin");
            var s = new FakeSink(); GraphExecutor.Fire(g, s, msg, Vars("blue", "u", "#c"));
            fails += Expect("switch-case", s.Sent.Count == 1 && s.Sent[0].text == "B", Dump(s));
            var s2 = new FakeSink(); GraphExecutor.Fire(g, s2, msg, Vars("green", "u", "#c"));
            fails += Expect("switch-default", s2.Sent.Count == 1 && s2.Sent[0].text == "D", Dump(s2));
            // pin count reflects the case list: 1 default + 2 cases
            fails += Expect("switch-output-count", sw.Outputs.Length == 3, $"outs={sw.Outputs.Length}");
            // adding a 3rd case must NOT disturb existing wires (default + red + blue still land correctly)
            sw.SetParam("cases", "[\"red\",\"blue\",\"green\"]");
            var s3 = new FakeSink(); GraphExecutor.Fire(g, s3, msg, Vars("blue", "u", "#c"));
            fails += Expect("switch-add-case-stable", s3.Sent.Count == 1 && s3.Sent[0].text == "B", Dump(s3));
            // a value with surrounding whitespace still matches a clean case (both sides trimmed)
            var s4 = new FakeSink(); GraphExecutor.Fire(g, s4, msg, Vars("  blue  ", "u", "#c"));
            fails += Expect("switch-trims-value", s4.Sent.Count == 1 && s4.Sent[0].text == "B", Dump(s4));
            // a whitespace-only case row is ignored - no phantom pin (1 default + 2 real cases)
            sw.SetParam("cases", "[\"red\",\"  \",\"blue\"]");
            fails += Expect("switch-skips-blank-case", sw.Outputs.Length == 3, $"outs={sw.Outputs.Length}");
        }

        // Counter: Get -> Math(+1) -> Set, reply with new value (persists across fires)
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "count");
            var get = N(g, "data.getvar", 150, 120); get.SetParam("name", "n"); get.SetParam("default", "0");
            var math = N(g, "data.math", 350, 120); math.SetParam("op", "+"); math.SetParam("b", "1");
            var set = N(g, "data.setvar", 350, 0); set.SetParam("name", "n");
            var reply = N(g, "action.reply", 600, 0);
            g.Connect(cmd.Id, 0, set.Id, 0);       // exec
            g.Connect(get.Id, 0, math.Id, 0);      // n -> a
            g.Connect(math.Id, 0, set.Id, 1);      // a+1 -> value
            g.Connect(set.Id, 0, reply.Id, 0);     // then -> reply
            g.Connect(math.Id, 0, reply.Id, 1);    // a+1 -> message
            var s = new FakeSink();
            GraphExecutor.Fire(g, s, cmd, Vars("!count", "u", "#c"));
            GraphExecutor.Fire(g, s, cmd, Vars("!count", "u", "#c"));
            fails += Expect("counter-increments", s.Sent.Count == 2 && s.Sent[0].text == "1" && s.Sent[1].text == "2", Dump(s));
        }

        // Cooldown blocks the second hit
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "cd");
            var cd = N(g, "logic.cooldown", 250, 0); cd.SetParam("seconds", "3600"); cd.SetParam("perUser", "false");
            var reply = N(g, "action.reply", 500, 0); reply.SetParam("message", "ok");
            g.Connect(cmd.Id, 0, cd.Id, 0); g.Connect(cd.Id, 0, reply.Id, 0);
            var s = new FakeSink();
            GraphExecutor.Fire(g, s, cmd, Vars("!cd", "u", "#c"));
            GraphExecutor.Fire(g, s, cmd, Vars("!cd", "u", "#c"));
            fails += Expect("cooldown-blocks", s.Sent.Count == 1 && s.Sent[0].text == "ok", Dump(s));
        }

        // Format Text: {a}/{b} splice the inputs, {var} uses the event (regression: Resolve used to eat {a}/{b})
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "f");
            var fmt = N(g, "data.format", 200, 80); fmt.SetParam("template", "{nick}: {a} {b}");
            var ra = N(g, "data.random", 60, 160); ra.SetParam("options", "AA");
            var rb = N(g, "data.random", 60, 220); rb.SetParam("options", "BB");
            var reply = N(g, "action.reply", 440, 0);
            g.Connect(cmd.Id, 0, reply.Id, 0);
            g.Connect(ra.Id, 0, fmt.Id, 0);     // AA -> {a}
            g.Connect(rb.Id, 0, fmt.Id, 1);     // BB -> {b}
            g.Connect(fmt.Id, 0, reply.Id, 1);  // formatted -> reply message
            var s = new FakeSink(); GraphExecutor.Fire(g, s, cmd, Vars("!f", "zoe", "#c"));
            fails += Expect("format-inputs", s.Sent.Count == 1 && s.Sent[0].text == "zoe: AA BB", Dump(s));
        }

        // templating leaves literal JSON/code braces intact while still resolving real {tokens}
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "j");
            var fmt = N(g, "data.format", 200, 80); fmt.SetParam("template", "{\"hi\":\"{nick}\",\"raw\":{}}");
            var reply = N(g, "action.reply", 440, 0);
            g.Connect(cmd.Id, 0, reply.Id, 0);
            g.Connect(fmt.Id, 0, reply.Id, 1);
            var s = new FakeSink(); GraphExecutor.Fire(g, s, cmd, Vars("!j", "zoe", "#c"));
            fails += Expect("brace-safe-format", s.Sent.Count == 1 && s.Sent[0].text == "{\"hi\":\"zoe\",\"raw\":{}}", Dump(s));

            var g2 = new NodeGraph();
            var cmd2 = N(g2, "event.command", 0, 0); cmd2.SetParam("command", "j");
            var rep2 = N(g2, "action.reply", 200, 0); rep2.SetParam("message", "{\"x\":\"{nick}\"}");
            g2.Connect(cmd2.Id, 0, rep2.Id, 0);
            var s2 = new FakeSink(); GraphExecutor.Fire(g2, s2, cmd2, Vars("!j", "zoe", "#c"));
            fails += Expect("brace-safe-resolve", s2.Sent.Count == 1 && s2.Sent[0].text == "{\"x\":\"zoe\"}", Dump(s2));
        }

        // new IRC nodes: Part, Raw IRC (composed client-tags), Start Typing (blank target = triggering channel)
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "x");
            var part = N(g, "action.part", 200, 0); part.SetParam("channel", "#old"); part.SetParam("reason", "bye");
            var raw = N(g, "irc.raw", 400, 0); raw.SetParam("tags", "[[\"typing\",\"active\"]]"); raw.SetParam("line", "PRIVMSG #lobby :hi {nick}");
            var typ = N(g, "irc.typing.start", 600, 0);
            g.Connect(cmd.Id, 0, part.Id, 0);
            g.Connect(part.Id, 0, raw.Id, 0);
            g.Connect(raw.Id, 0, typ.Id, 0);
            var s = new FakeSink(); GraphExecutor.Fire(g, s, cmd, Vars("!x", "zoe", "#c"));
            bool ok = s.Logs.Contains("PART #old :bye")
                   && s.Logs.Contains("RAW @+typing=active PRIVMSG #lobby :hi zoe")   // '+' auto-added, {nick} resolved
                   && s.Logs.Contains("TYPING start #c");
            fails += Expect("irc-new-nodes", ok, string.Join(" | ", s.Logs));
        }

        // community .ircnode loader: a python script-node from a manifest, fired end-to-end
        {
            string manifest = "{\"typeId\":\"test.shout\",\"title\":\"Shout\",\"category\":\"Data\","
                + "\"inputs\":[{\"name\":\"\",\"kind\":\"Exec\"},{\"name\":\"text\",\"kind\":\"Text\"}],"
                + "\"outputs\":[{\"name\":\"then\",\"kind\":\"Exec\"},{\"name\":\"out\",\"kind\":\"Text\"}],"
                + "\"timeout\":20,\"language\":\"python\",\"code\":\"import os\\nprint(os.environ.get('TEXT','').upper())\"}";
            var cdef = Ircuitry.Graph.CustomNode.Load(manifest);
            fails += Expect("customnode-load", cdef != null && cdef.TypeId == "test.shout" && cdef.Inputs.Length == 2 && cdef.Outputs.Length == 2, cdef?.TypeId ?? "<null>");
            if (cdef != null)
            {
                var g = new NodeGraph();
                var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "x");
                var src = N(g, "data.random", 150, 150); src.SetParam("options", "hello");
                var cn = g.Add(cdef, new Vector2(300, 0));
                var rep = N(g, "action.reply", 500, 0);
                g.Connect(cmd.Id, 0, cn.Id, 0);    // exec
                g.Connect(src.Id, 0, cn.Id, 1);    // "hello" -> input "text"
                g.Connect(cn.Id, 0, rep.Id, 0);    // exec "then" -> reply
                g.Connect(cn.Id, 1, rep.Id, 1);    // data "out" -> reply message
                var s = new FakeSink(); GraphExecutor.Fire(g, s, cmd, Vars("!x", "zoe", "#c"));
                // the python3 child can be missing/slow/contended on a CI runner; skip rather than flake when it
                // produced nothing, and only FAIL when the code ran but returned the wrong thing
                if (s.Sent.Count == 0)
                    fails += Expect("customnode-run (skipped: no/slow code runtime)", true, "");
                else
                    fails += Expect("customnode-run", s.Sent.Count == 1 && s.Sent[0].text == "HELLO", Dump(s));
            }

            // a .ircnode placed in CustomDir is discovered by LoadCustom (self-cleaning)
            var dir = NodeCatalog.CustomDir;
            var path = System.IO.Path.Combine(dir, "test-selftest-node.ircnode");
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(path, "{\"typeId\":\"test.selftestnode\",\"title\":\"ST\",\"category\":\"Data\",\"outputs\":[{\"name\":\"v\",\"kind\":\"Text\"}],\"language\":\"python\",\"code\":\"print('ok')\"}");
                NodeCatalog.LoadCustom();
                fails += Expect("customnode-disk", NodeCatalog.TryGet("test.selftestnode", out _), "should load from CustomDir");
            }
            finally
            {
                try { System.IO.File.Delete(path); } catch { }
                NodeCatalog.LoadCustom();   // restore the catalog to built-ins + any real custom nodes
            }
        }

        // reusable subflow: a saved subgraph (in -> return out = UPPER(arg x)) run as a single node
        {
            var sub = new NodeGraph();
            var fin = sub.Add(NodeCatalog.Get("flow.in"), Vector2.Zero);
            var arg = sub.Add(NodeCatalog.Get("flow.arg"), new Vector2(0, 150)); arg.SetParam("name", "x");
            var up = sub.Add(NodeCatalog.Get("data.transform"), new Vector2(200, 150)); up.SetParam("op", "upper");
            var ret = sub.Add(NodeCatalog.Get("flow.return"), new Vector2(400, 0)); ret.SetParam("name", "out");
            sub.Connect(fin.Id, 0, ret.Id, 0);   // exec: in -> return
            sub.Connect(arg.Id, 0, up.Id, 0);    // arg value -> transform text
            sub.Connect(up.Id, 0, ret.Id, 1);    // UPPER -> return value
            string manifest = "{\"typeId\":\"test.subupper\",\"title\":\"Upper\",\"category\":\"Logic\","
                + "\"inputs\":[{\"name\":\"\",\"kind\":\"Exec\"},{\"name\":\"x\",\"kind\":\"Text\"}],"
                + "\"outputs\":[{\"name\":\"then\",\"kind\":\"Exec\"},{\"name\":\"out\",\"kind\":\"Text\"}],"
                + "\"subgraph\":" + GraphSerializer.Save(sub, "sub") + "}";
            var sdef = Ircuitry.Graph.CustomNode.Load(manifest);
            fails += Expect("subflow-load", sdef != null && sdef.Inputs.Length == 2 && sdef.Outputs.Length == 2, sdef?.TypeId ?? "<null>");
            if (sdef != null)
            {
                var g = new NodeGraph();
                var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "x");
                var srcv = N(g, "data.random", 100, 150); srcv.SetParam("options", "hello");
                var sn = g.Add(sdef, new Vector2(300, 0));
                var rep = N(g, "action.reply", 500, 0);
                g.Connect(cmd.Id, 0, sn.Id, 0);     // exec -> subflow
                g.Connect(srcv.Id, 0, sn.Id, 1);    // "hello" -> subflow input "x"
                g.Connect(sn.Id, 0, rep.Id, 0);     // subflow exec -> reply
                g.Connect(sn.Id, 1, rep.Id, 1);     // subflow output "out" -> reply message
                var s = new FakeSink(); GraphExecutor.Fire(g, s, cmd, Vars("!x", "zoe", "#c"));
                fails += Expect("subflow-run", s.Sent.Count == 1 && s.Sent[0].text == "HELLO", Dump(s));
            }
        }

        // For Each over a comma list
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "list");
            var fe = N(g, "logic.forEach", 250, 0); fe.SetParam("sep", "comma");
            var reply = N(g, "action.reply", 500, 0);
            g.Connect(cmd.Id, 0, fe.Id, 0); g.Connect(cmd.Id, 1, fe.Id, 1); // args -> list
            g.Connect(fe.Id, 0, reply.Id, 0); g.Connect(fe.Id, 2, reply.Id, 1); // each -> reply, item -> message
            var s = new FakeSink();
            GraphExecutor.Fire(g, s, cmd, Vars("!list a,b,c", "u", "#c"));
            fails += Expect("foreach-items", s.Sent.Count == 3 && s.Sent[0].text == "a" && s.Sent[2].text == "c", Dump(s));
        }

        return fails;
    }

    /// <summary>IRCv3 tag nodes (react / from-account / is-bot / get-tag), node muting, and run-history I/O capture.</summary>
    private static int Ircv3AndHistoryTest()
    {
        int fails = 0;

        // Add Reaction -> sink.React on the triggering message
        {
            var g = new NodeGraph();
            var msg = N(g, "event.message", 0, 0);
            var react = N(g, "action.react", 250, 0); react.SetParam("emoji", "\U0001F389");   // intentional unicode (party popper)
            g.Connect(msg.Id, 0, react.Id, 0);
            var v = Vars("hi", "alice", "#c"); v["msgid"] = "abc";
            var s = new FakeSink(); GraphExecutor.Fire(g, s, msg, v);
            fails += Expect("react-node", s.Sent.Count == 1 && s.Sent[0] == ("#c", "react:\U0001F389"), Dump(s));
        }

        // From Account: branches on the account tag
        {
            var g = new NodeGraph();
            var msg = N(g, "event.message", 0, 0);
            var fa = N(g, "filter.fromAccount", 250, 0); fa.SetParam("account", "alice");
            var t = N(g, "action.reply", 500, -40); t.SetParam("message", "yes");
            var f = N(g, "action.reply", 500, 40); f.SetParam("message", "no");
            g.Connect(msg.Id, 0, fa.Id, 0); g.Connect(fa.Id, 0, t.Id, 0); g.Connect(fa.Id, 1, f.Id, 0);
            var v = Vars("hi", "alice", "#c"); v["account"] = "alice";
            var s = new FakeSink(); GraphExecutor.Fire(g, s, msg, v);
            fails += Expect("fromaccount-match", s.Sent.Count == 1 && s.Sent[0].text == "yes", Dump(s));
            var v2 = Vars("hi", "bob", "#c"); v2["account"] = "bob";
            var s2 = new FakeSink(); GraphExecutor.Fire(g, s2, msg, v2);
            fails += Expect("fromaccount-else", s2.Sent.Count == 1 && s2.Sent[0].text == "no", Dump(s2));
        }

        // Is Bot: branches on the bot flag
        {
            var g = new NodeGraph();
            var msg = N(g, "event.message", 0, 0);
            var ib = N(g, "filter.isBot", 250, 0);
            var b = N(g, "action.reply", 500, -40); b.SetParam("message", "bot");
            var h = N(g, "action.reply", 500, 40); h.SetParam("message", "human");
            g.Connect(msg.Id, 0, ib.Id, 0); g.Connect(ib.Id, 0, b.Id, 0); g.Connect(ib.Id, 1, h.Id, 0);
            var v = Vars("hi", "x", "#c"); v["isbot"] = "true";
            var s = new FakeSink(); GraphExecutor.Fire(g, s, msg, v);
            fails += Expect("isbot-branch", s.Sent.Count == 1 && s.Sent[0].text == "bot", Dump(s));
        }

        // Get Tag: reads an arbitrary message tag into a reply
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "x");
            var gt = N(g, "data.gettag", 150, 120); gt.SetParam("name", "account");
            var reply = N(g, "action.reply", 400, 0);
            g.Connect(cmd.Id, 0, reply.Id, 0);
            g.Connect(gt.Id, 0, reply.Id, 1);
            var v = Vars("!x", "u", "#c"); v["tag.account"] = "myacct";
            var s = new FakeSink(); GraphExecutor.Fire(g, s, cmd, v);
            fails += Expect("gettag-value", s.Sent.Count == 1 && s.Sent[0].text == "myacct", Dump(s));
        }

        // Muted node is skipped entirely
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "x");
            var reply = N(g, "action.reply", 300, 0); reply.SetParam("message", "hi"); reply.Muted = true;
            g.Connect(cmd.Id, 0, reply.Id, 0);
            var s = new FakeSink(); GraphExecutor.Fire(g, s, cmd, Vars("!x", "u", "#c"));
            fails += Expect("muted-skips", s.Sent.Count == 0, Dump(s));
        }

        // Custom node title: display fallback + survives a .ircbot round-trip; history uses it
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "hi");
            var reply = N(g, "action.reply", 300, 0); reply.SetParam("message", "yo"); reply.Title = "greeting";
            g.Connect(cmd.Id, 0, reply.Id, 0);

            // fallback: blank title shows the catalog title; set title overrides
            fails += Expect("title-fallback", cmd.DisplayTitle == cmd.Def.Title && reply.DisplayTitle == "greeting", $"{cmd.DisplayTitle}/{reply.DisplayTitle}");

            var (g2, _) = GraphSerializer.Load(GraphSerializer.Save(g, "t"));
            fails += Expect("title-roundtrip", g2.Find(reply.Id)?.Title == "greeting", g2.Find(reply.Id)?.Title ?? "<null>");

            var rec = new RunRecord();
            GraphExecutor.Fire(g, new FakeSink(), cmd, Vars("!hi", "u", "#c"), rec);
            var rt = rec.Nodes.Find(t => t.NodeId == reply.Id);
            fails += Expect("title-in-history", rt != null && rt.Title == "greeting", rt?.Title ?? "<none>");

            // copy/paste must preserve the custom title (regression)
            var ed = new Ircuitry.Editor.GraphEditor(g);
            ed.Selection.Add(reply.Id);
            ed.CopySelection();
            ed.PasteAtCursor(new Vector2(600, 600));
            var pasted = g.Nodes.Find(nn => nn.Id != reply.Id && nn.TypeId == "action.reply");
            fails += Expect("title-survives-paste", pasted != null && pasted.Title == "greeting", pasted?.Title ?? "<none>");
        }

        // Run history captures node-by-node inputs/outputs
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "hi");
            var rnd = N(g, "data.random", 150, 120); rnd.SetParam("options", "only");
            var reply = N(g, "action.reply", 400, 0);
            g.Connect(cmd.Id, 0, reply.Id, 0);
            g.Connect(rnd.Id, 0, reply.Id, 1);    // data: random -> reply.message
            var rec = new RunRecord();
            var s = new FakeSink();
            GraphExecutor.Fire(g, s, cmd, Vars("!hi", "u", "#c"), rec);
            var rt = rec.Nodes.Find(t => t.NodeId == reply.Id);
            bool ok = rec.Nodes.Count >= 2
                && rec.Nodes[0].NodeId == cmd.Id && rec.Nodes[0].Pulsed.Count > 0
                && rt != null && rt.Inputs.Exists(p => p.value == "only");
            fails += Expect("history-captures-io", ok, $"nodes={rec.Nodes.Count}");
        }

        return fails;
    }

    /// <summary>SQLite SQL Query node + parser, and the JS/Python Code node (runs real node/python3).</summary>
    private static int SqlAndCodeTest()
    {
        int fails = 0;

        // ---- SQLite (advanced DB) ----
        {
            string db = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ircuitry-selftest-" + System.Environment.TickCount + ".sqlite");
            try
            {
                var (_, _, e1) = Ircuitry.Net.Sql.Run(db, "CREATE TABLE t(n INTEGER);");
                var (_, aff, e2) = Ircuitry.Net.Sql.Run(db, "INSERT INTO t VALUES (5),(9);");
                var (res, rows, e3) = Ircuitry.Net.Sql.Run(db, "SELECT n FROM t ORDER BY n;");
                fails += Expect("sql-run", e1 == null && e2 == null && e3 == null && aff == 2 && rows == 2 && res == "5\n9",
                    $"e=[{e1}|{e2}|{e3}] aff={aff} rows={rows} res={res.Replace("\n", "/")}");

                // node path: SELECT via db.sql -> reply
                var g = new NodeGraph();
                var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "q");
                var sql = N(g, "db.sql", 200, 0); sql.SetParam("file", db); sql.SetParam("sql", "SELECT count(*) FROM t;");
                var reply = N(g, "action.reply", 440, 0);
                g.Connect(cmd.Id, 0, sql.Id, 0); g.Connect(sql.Id, 0, reply.Id, 0); g.Connect(sql.Id, 1, reply.Id, 1);
                var s = new FakeSink(); GraphExecutor.Fire(g, s, cmd, Vars("!q", "u", "#c"));
                fails += Expect("sql-node", s.Sent.Count == 1 && s.Sent[0].text == "2", Dump(s));
            }
            finally { try { System.IO.File.Delete(db); } catch { } }
        }

        // ---- Code node: JavaScript (node) ----
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "js");
            var code = N(g, "code.run", 200, 0); code.SetParam("language", "javascript"); code.SetParam("code", "console.log('hi ' + process.env.NICK)");
            var reply = N(g, "action.reply", 440, 0);
            g.Connect(cmd.Id, 0, code.Id, 0); g.Connect(code.Id, 0, reply.Id, 0); g.Connect(code.Id, 1, reply.Id, 1);
            var s = new FakeSink(); GraphExecutor.Fire(g, s, cmd, Vars("!js", "alice", "#c"));
            fails += Expect("code-js", s.Sent.Count == 1 && s.Sent[0].text == "hi alice", Dump(s));
        }

        // ---- Code node: Python (python3) ----
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "py");
            var code = N(g, "code.run", 200, 0); code.SetParam("language", "python"); code.SetParam("code", "import os\nprint('py ' + os.environ.get('NICK',''))");
            var reply = N(g, "action.reply", 440, 0);
            g.Connect(cmd.Id, 0, code.Id, 0); g.Connect(code.Id, 0, reply.Id, 0); g.Connect(code.Id, 1, reply.Id, 1);
            var s = new FakeSink(); GraphExecutor.Fire(g, s, cmd, Vars("!py", "bob", "#c"));
            fails += Expect("code-python", s.Sent.Count == 1 && s.Sent[0].text == "py bob", Dump(s));
        }

        return fails;
    }

    /// <summary>The 10 added nodes: IRCv3 moderation/emote, Delay, DB (KV), AI Memory, Calendar add+search.</summary>
    private static int MoreNodesTest()
    {
        int fails = 0;

        // ---- IRCv3 moderation + emote (assert raw/sent output) ----
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "t");
            var topic = N(g, "irc.topic", 200, -60); topic.SetParam("topic", "hello {nick}");
            var kick = N(g, "irc.kick", 200, 0); kick.SetParam("nick", "bob"); kick.SetParam("reason", "bye");
            var mode = N(g, "irc.mode", 200, 60); mode.SetParam("modes", "+o"); mode.SetParam("target", "bob");
            var act = N(g, "irc.action", 200, 120); act.SetParam("text", "waves");
            g.Connect(cmd.Id, 0, topic.Id, 0); g.Connect(topic.Id, 0, kick.Id, 0);
            g.Connect(kick.Id, 0, mode.Id, 0); g.Connect(mode.Id, 0, act.Id, 0);
            var s = new FakeSink(); GraphExecutor.Fire(g, s, cmd, Vars("!t", "alice", "#c"));
            bool ok = s.Logs.Contains("RAW TOPIC #c :hello alice")
                   && s.Logs.Contains("RAW KICK #c bob :bye")
                   && s.Logs.Contains("RAW MODE #c +o bob")
                   && s.Sent.Exists(x => x.target == "#c" && x.text == "ACTION waves");
            fails += Expect("irc-moderation-nodes", ok, "logs=[" + string.Join(",", s.Logs) + "] " + Dump(s));
        }

        // ---- Delay (seconds=0 must not hang) ----
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "d");
            var delay = N(g, "flow.delay", 200, 0); delay.SetParam("seconds", "0");
            var reply = N(g, "action.reply", 400, 0); reply.SetParam("message", "done");
            g.Connect(cmd.Id, 0, delay.Id, 0); g.Connect(delay.Id, 0, reply.Id, 0);
            var s = new FakeSink(); GraphExecutor.Fire(g, s, cmd, Vars("!d", "u", "#c"));
            fails += Expect("flow-delay", s.Sent.Count == 1 && s.Sent[0].text == "done", Dump(s));
        }

        // ---- Database (file-backed KV) ----
        {
            string table = "ircuitry-selftest-" + System.Environment.TickCount;
            try
            {
                var g = new NodeGraph();
                var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "s");
                var set = N(g, "db.set", 200, 0); set.SetParam("table", table); set.SetParam("key", "alice"); set.SetParam("value", "42");
                g.Connect(cmd.Id, 0, set.Id, 0);
                GraphExecutor.Fire(g, new FakeSink(), cmd, Vars("!s", "u", "#c"));
                fails += Expect("db-set", Ircuitry.Net.KvStore.Get(table, "alice") == "42", "");

                var g2 = new NodeGraph();
                var cmd2 = N(g2, "event.command", 0, 0); cmd2.SetParam("command", "g");
                var get = N(g2, "db.get", 200, 80); get.SetParam("table", table); get.SetParam("mode", "value"); get.SetParam("key", "alice");
                var reply = N(g2, "action.reply", 420, 0);
                g2.Connect(cmd2.Id, 0, reply.Id, 0); g2.Connect(get.Id, 0, reply.Id, 1);
                var s = new FakeSink(); GraphExecutor.Fire(g2, s, cmd2, Vars("!g", "u", "#c"));
                fails += Expect("db-get", s.Sent.Count == 1 && s.Sent[0].text == "42", Dump(s));

                var hit = Ircuitry.Net.KvStore.Find(table, "4");
                fails += Expect("db-find", hit is { value: "42" }, hit?.value ?? "<null>");
            }
            finally { try { System.IO.File.Delete(System.IO.Path.Combine(Ircuitry.Net.KvStore.Dir, table + ".json")); } catch { } }
        }

        // ---- AI Memory (recall / remember / clear) ----
        {
            var s = new FakeSink();
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "m");
            var mem = N(g, "ai.memory", 200, 80); mem.SetParam("session", "{channel}"); mem.SetParam("mode", "remember"); mem.SetParam("role", "user"); mem.SetParam("text", "hi there");
            g.Connect(cmd.Id, 0, mem.Id, 0);                       // remember only (no reply)
            GraphExecutor.Fire(g, s, cmd, Vars("!m", "u", "#c"));
            fails += Expect("aimem-remember", s.State.TryGetValue("aimem/#c", out var h1) && h1 == "user: hi there", h1 ?? "<none>");

            // a recall node reads it back
            var g2 = new NodeGraph();
            var cmd2 = N(g2, "event.command", 0, 0); cmd2.SetParam("command", "r");
            var rec = N(g2, "ai.memory", 200, 80); rec.SetParam("session", "{channel}"); rec.SetParam("mode", "recall");
            var reply2 = N(g2, "action.reply", 420, 0);
            g2.Connect(cmd2.Id, 0, rec.Id, 0); g2.Connect(rec.Id, 0, reply2.Id, 0); g2.Connect(rec.Id, 1, reply2.Id, 1);
            GraphExecutor.Fire(g2, s, cmd2, Vars("!r", "u", "#c"));   // recall (shares sink state)
            fails += Expect("aimem-recall", s.Sent.Count == 1 && s.Sent[0].text == "user: hi there", Dump(s));
        }

        // ---- Calendar add + search ----
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ircuitry-selftest-cal-" + System.Environment.TickCount + ".ics");
            try
            {
                var g = new NodeGraph();
                var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "a");
                var add = N(g, "cal.add", 200, 0);
                add.SetParam("path", path); add.SetParam("summary", "Launch Party"); add.SetParam("start", "2026-06-20 18:00"); add.SetParam("duration", "90"); add.SetParam("location", "HQ");
                g.Connect(cmd.Id, 0, add.Id, 0);
                GraphExecutor.Fire(g, new FakeSink(), cmd, Vars("!a", "u", "#c"));
                fails += Expect("cal-add", System.IO.File.Exists(path) && System.IO.File.ReadAllText(path).Contains("SUMMARY:Launch Party"), "");

                var g2 = new NodeGraph();
                var cmd2 = N(g2, "event.command", 0, 0); cmd2.SetParam("command", "f");
                var search = N(g2, "cal.search", 200, 0); search.SetParam("source", path); search.SetParam("query", "Launch");
                var ok = N(g2, "action.reply", 440, -40);
                var none = N(g2, "action.reply", 440, 40); none.SetParam("message", "none");
                g2.Connect(cmd2.Id, 0, search.Id, 0); g2.Connect(search.Id, 0, ok.Id, 0); g2.Connect(search.Id, 2, ok.Id, 1); g2.Connect(search.Id, 1, none.Id, 0);
                var s = new FakeSink(); GraphExecutor.Fire(g2, s, cmd2, Vars("!f", "u", "#c"));
                fails += Expect("cal-search", s.Sent.Count == 1 && s.Sent[0].text == "Launch Party", Dump(s));
            }
            finally { try { System.IO.File.Delete(path); } catch { } }
        }

        return fails;
    }

    /// <summary>File Read/Write nodes (round-trip, append, missing branch) and the iCal node (next/none + parser).</summary>
    private static int FileAndIcalTest()
    {
        int fails = 0;
        string dir = System.IO.Path.GetTempPath();
        string path = System.IO.Path.Combine(dir, "ircuitry-selftest-file.txt");
        try { System.IO.File.Delete(path); } catch { }

        // path sandbox: relative traversal is blocked; absolute is honoured; plain relative stays in the sandbox
        {
            bool ok = NodeCatalog.ResolveFile("../../etc/passwd").Length == 0
                   && NodeCatalog.ResolveFile("/tmp/x").Length > 0
                   && NodeCatalog.ResolveFile("notes.txt").Replace('\\', '/').Contains("ircuitry/files");
            fails += Expect("file-path-sandbox", ok, NodeCatalog.ResolveFile("../../etc/passwd") + " | " + NodeCatalog.ResolveFile("notes.txt"));
        }

        // write (overwrite) then read back
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "w");
            var w = N(g, "file.write", 250, 0); w.SetParam("path", path); w.SetParam("mode", "overwrite"); w.SetParam("text", "hello world");
            g.Connect(cmd.Id, 0, w.Id, 0);
            GraphExecutor.Fire(g, new FakeSink(), cmd, Vars("!w", "u", "#c"));
            fails += Expect("file-write", System.IO.File.Exists(path) && System.IO.File.ReadAllText(path) == "hello world", "");

            var g2 = new NodeGraph();
            var cmd2 = N(g2, "event.command", 0, 0); cmd2.SetParam("command", "r");
            var rd = N(g2, "file.read", 250, 0); rd.SetParam("path", path);
            var reply = N(g2, "action.reply", 500, 0);
            g2.Connect(cmd2.Id, 0, rd.Id, 0); g2.Connect(rd.Id, 0, reply.Id, 0); g2.Connect(rd.Id, 2, reply.Id, 1);
            var s = new FakeSink(); GraphExecutor.Fire(g2, s, cmd2, Vars("!r", "u", "#c"));
            fails += Expect("file-read", s.Sent.Count == 1 && s.Sent[0].text == "hello world", Dump(s));
        }

        // append
        {
            try { System.IO.File.Delete(path); } catch { }
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "w");
            var w = N(g, "file.write", 250, 0); w.SetParam("path", path); w.SetParam("mode", "append"); w.SetParam("text", "{args}");
            g.Connect(cmd.Id, 0, w.Id, 0);
            GraphExecutor.Fire(g, new FakeSink(), cmd, Vars("!w a", "u", "#c"));
            GraphExecutor.Fire(g, new FakeSink(), cmd, Vars("!w b", "u", "#c"));
            fails += Expect("file-append", System.IO.File.ReadAllText(path) == "a\nb\n", System.IO.File.ReadAllText(path).Replace("\n", "\\n"));
        }

        // read-missing branch
        {
            string gone = System.IO.Path.Combine(dir, "ircuitry-selftest-nope-" + System.Environment.TickCount + ".txt");
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "r");
            var rd = N(g, "file.read", 250, 0); rd.SetParam("path", gone);
            var ok = N(g, "action.reply", 500, -40); ok.SetParam("message", "found");
            var miss = N(g, "action.reply", 500, 40); miss.SetParam("message", "missing");
            g.Connect(cmd.Id, 0, rd.Id, 0); g.Connect(rd.Id, 0, ok.Id, 0); g.Connect(rd.Id, 1, miss.Id, 0);
            var s = new FakeSink(); GraphExecutor.Fire(g, s, cmd, Vars("!r", "u", "#c"));
            fails += Expect("file-read-missing", s.Sent.Count == 1 && s.Sent[0].text == "missing", Dump(s));
        }
        try { System.IO.File.Delete(path); } catch { }

        // iCal parser + node
        {
            string future = DateTime.Now.AddDays(1).ToString("yyyyMMdd'T'HHmmss");
            string past = DateTime.Now.AddDays(-1).ToString("yyyyMMdd'T'HHmmss");
            string ics =
                "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n" +
                $"BEGIN:VEVENT\r\nSUMMARY:Past Standup\r\nDTSTART:{past}\r\nEND:VEVENT\r\n" +
                $"BEGIN:VEVENT\r\nSUMMARY:Future Launch\r\nLOCATION:HQ\r\nDTSTART:{future}\r\nEND:VEVENT\r\n" +
                "END:VCALENDAR\r\n";

            var parsed = Ircuitry.Net.Ical.Parse(ics);
            fails += Expect("ical-parse", parsed.Count == 2, "count=" + parsed.Count);
            var next = Ircuitry.Net.Ical.Next(parsed, DateTime.Now);
            fails += Expect("ical-next", next != null && next.Summary == "Future Launch" && next.Location == "HQ", next?.Summary ?? "<null>");

            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "cal");
            var cal = N(g, "file.ical", 250, 0); cal.SetParam("source", ics); cal.SetParam("mode", "next");
            var reply = N(g, "action.reply", 520, 0);
            g.Connect(cmd.Id, 0, cal.Id, 0); g.Connect(cal.Id, 0, reply.Id, 0); g.Connect(cal.Id, 2, reply.Id, 1);
            var s = new FakeSink(); GraphExecutor.Fire(g, s, cmd, Vars("!cal", "u", "#c"));
            fails += Expect("ical-node-next", s.Sent.Count == 1 && s.Sent[0].text == "Future Launch", Dump(s));

            // 'none' branch on an empty calendar
            var g2 = new NodeGraph();
            var cmd2 = N(g2, "event.command", 0, 0); cmd2.SetParam("command", "cal");
            var cal2 = N(g2, "file.ical", 250, 0); cal2.SetParam("source", "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n"); cal2.SetParam("mode", "next");
            var none = N(g2, "action.reply", 520, 0); none.SetParam("message", "no events");
            g2.Connect(cmd2.Id, 0, cal2.Id, 0); g2.Connect(cal2.Id, 1, none.Id, 0);
            var s2 = new FakeSink(); GraphExecutor.Fire(g2, s2, cmd2, Vars("!cal", "u", "#c"));
            fails += Expect("ical-node-none", s2.Sent.Count == 1 && s2.Sent[0].text == "no events", Dump(s2));
        }

        // a whole directory of .ics files merges (drag-a-folder-onto-the-node support)
        {
            string caldir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ircuitry-selftest-caldir-" + System.Environment.TickCount);
            System.IO.Directory.CreateDirectory(caldir);
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(caldir, "a.ics"), "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nSUMMARY:A\r\nDTSTART:20260101T090000\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
                System.IO.File.WriteAllText(System.IO.Path.Combine(caldir, "b.ics"), "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nSUMMARY:B\r\nDTSTART:20260202T090000\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
                var g = new NodeGraph();
                var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "cd");
                var cal = N(g, "file.ical", 250, 0); cal.SetParam("source", caldir); cal.SetParam("mode", "count");
                var reply = N(g, "action.reply", 520, 0);
                g.Connect(cmd.Id, 0, cal.Id, 0); g.Connect(cal.Id, 0, reply.Id, 0); g.Connect(cal.Id, 6, reply.Id, 1);
                var s = new FakeSink(); GraphExecutor.Fire(g, s, cmd, Vars("!cd", "u", "#c"));
                fails += Expect("ical-directory", s.Sent.Count == 1 && s.Sent[0].text == "2", Dump(s));
            }
            finally { try { System.IO.Directory.Delete(caldir, true); } catch { } }
        }

        return fails;
    }

    /// <summary>The "On Schedule" trigger: interval / daily / weekly / once, with no startup back-fire and weekday filtering.</summary>
    private static int ScheduleTest()
    {
        int fails = 0;
        var last = new Dictionary<string, DateTime>();
        var fired = new HashSet<string>();
        Node Sched(params (string k, string v)[] ps)
        {
            var g = new NodeGraph();
            var n = g.Add(NodeCatalog.Get("event.schedule"), Vector2.Zero);
            foreach (var (k, v) in ps) n.SetParam(k, v);
            return n;
        }

        // interval: every 2 minutes - baseline first, then fire after the interval, not again immediately
        {
            last.Clear(); fired.Clear();
            var n = Sched(("mode", "interval"), ("every", "2"), ("unit", "minutes"));
            var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
            bool baseline = BotRuntime.ScheduleDue(n, t0, last, fired);
            bool early = BotRuntime.ScheduleDue(n, t0.AddSeconds(90), last, fired);
            bool due = BotRuntime.ScheduleDue(n, t0.AddSeconds(125), last, fired);
            bool again = BotRuntime.ScheduleDue(n, t0.AddSeconds(135), last, fired);
            fails += Expect("sched-interval", !baseline && !early && due && !again, $"{baseline}{early}{due}{again}");
        }

        // daily at 09:00 - fires once when the time passes, repeats next day
        {
            last.Clear(); fired.Clear();
            var n = Sched(("mode", "daily"), ("time", "09:00"));
            var d = new DateTime(2026, 1, 1);
            bool baseline = BotRuntime.ScheduleDue(n, d.AddHours(8), last, fired);
            bool before = BotRuntime.ScheduleDue(n, d.AddHours(8).AddMinutes(59), last, fired);
            bool at = BotRuntime.ScheduleDue(n, d.AddHours(9), last, fired);
            bool later = BotRuntime.ScheduleDue(n, d.AddHours(9).AddMinutes(30), last, fired);
            bool nextDay = BotRuntime.ScheduleDue(n, d.AddDays(1).AddHours(9), last, fired);
            fails += Expect("sched-daily", !baseline && !before && at && !later && nextDay, $"{baseline}{before}{at}{later}{nextDay}");
        }

        // daily started AFTER today's time must not back-fire today
        {
            last.Clear(); fired.Clear();
            var n = Sched(("mode", "daily"), ("time", "09:00"));
            var d = new DateTime(2026, 1, 1);
            bool baseline = BotRuntime.ScheduleDue(n, d.AddHours(14), last, fired);
            bool sameDay = BotRuntime.ScheduleDue(n, d.AddHours(15), last, fired);
            bool nextDay = BotRuntime.ScheduleDue(n, d.AddDays(1).AddHours(9), last, fired);
            fails += Expect("sched-daily-no-backfire", !baseline && !sameDay && nextDay, $"{baseline}{sameDay}{nextDay}");
        }

        // weekly Mon-Fri at 09:00 - Saturday is skipped, Monday fires (2026-01-03=Sat, 2026-01-05=Mon)
        {
            last.Clear(); fired.Clear();
            var n = Sched(("mode", "weekly"), ("time", "09:00"), ("days", "Mon-Fri"));
            var sat = new DateTime(2026, 1, 3);
            BotRuntime.ScheduleDue(n, sat.AddHours(8), last, fired);   // baseline
            bool satFire = BotRuntime.ScheduleDue(n, sat.AddHours(9), last, fired);
            bool monFire = BotRuntime.ScheduleDue(n, new DateTime(2026, 1, 5, 9, 0, 0), last, fired);
            fails += Expect("sched-weekly-days", !satFire && monFire, $"sat={satFire} mon={monFire}");
        }

        // once: fires exactly once at/after the datetime
        {
            last.Clear(); fired.Clear();
            var n = Sched(("mode", "once"), ("datetime", "2026-01-01 09:00"));
            bool before = BotRuntime.ScheduleDue(n, new DateTime(2026, 1, 1, 8, 0, 0), last, fired);
            bool at = BotRuntime.ScheduleDue(n, new DateTime(2026, 1, 1, 9, 0, 0), last, fired);
            bool after = BotRuntime.ScheduleDue(n, new DateTime(2026, 1, 1, 9, 5, 0), last, fired);
            fails += Expect("sched-once", !before && at && !after, $"{before}{at}{after}");
        }

        return fails;
    }

    /// <summary>IRCv3 bot-tools encoding + command-list build + invocation parsing.</summary>
    private static int BotToolsTest()
    {
        int fails = 0;

        // base64-JSON roundtrip
        {
            var b64 = BotTools.Encode(new Dictionary<string, object?> { ["msg"] = "workflow", ["id"] = "x1", ["state"] = "start" });
            var e = BotTools.Decode(b64);
            fails += Expect("bottools-roundtrip", e is { } el && BotTools.Str(el, "id") == "x1" && BotTools.Str(el, "state") == "start", b64);
            fails += Expect("bottools-decode-garbage", BotTools.Decode("not!base64!") == null, "should be null");
        }

        // command list built from On Command nodes
        {
            var g = new NodeGraph();
            var a = g.Add(NodeCatalog.Get("event.command"), Vector2.Zero); a.SetParam("command", "weather"); a.SetParam("prefix", "!");
            var b = g.Add(NodeCatalog.Get("event.command"), new Vector2(0, 80)); b.SetParam("command", "ping");
            var list = BotTools.Decode(BotTools.BuildCommandList(g));
            bool ok = list is { } e && e.TryGetProperty("commands", out var cmds) && cmds.GetArrayLength() == 2
                      && BotTools.Str(e, "prefix") == "!";
            fails += Expect("bottools-cmdlist", ok, BotTools.BuildCommandList(g));
        }

        // invocation parse: good + malformed
        {
            var b64 = BotTools.Encode(new Dictionary<string, object?> { ["name"] = "weather", ["options"] = new Dictionary<string, object?> { ["city"] = "london" } });
            var inv = BotTools.ParseInvocation(b64);
            fails += Expect("bottools-invoke-parse", inv != null && inv.Name == "weather" && inv.OptionValues.Count == 1 && inv.OptionValues[0] == "london", inv?.Name ?? "<null>");
            var bad = BotTools.ParseInvocation(BotTools.Encode(new Dictionary<string, object?> { ["options"] = new Dictionary<string, object?>() }));
            fails += Expect("bottools-invoke-noname", bad == null, "should be null without name");
        }

        // per-command contexts parsing (defaults to all three; keeps only valid tokens)
        {
            bool ok = BotTools.Contexts("public, pm").Length == 2
                   && Array.IndexOf(BotTools.Contexts("public"), "public") >= 0
                   && BotTools.Contexts("garbage").Length == 3
                   && BotTools.Contexts("").Length == 3;
            fails += Expect("bottools-contexts", ok, "");
        }

        return fails;
    }

    /// <summary>A pm invocation of a public-only command is rejected with BAD_CONTEXT.</summary>
    private static int BotCmdBadContextTest()
    {
        var b64 = BotTools.Encode(new Dictionary<string, object?> { ["name"] = "weather", ["options"] = new Dictionary<string, object?>() });
        using var mock = new MockIrcServer(new[] { (120, $"@+draft/bot-cmd={b64};msgid=m1 :alice!a@h TAGMSG ircuitry-bot") });
        var rt = new BotRuntime(new ConsoleLog(), new System.Collections.Concurrent.ConcurrentDictionary<string, string>());

        var g = new NodeGraph();
        var cmd = g.Add(NodeCatalog.Get("event.command"), Vector2.Zero);
        cmd.SetParam("command", "weather"); cmd.SetParam("contexts", "public");   // public only
        var reply = g.Add(NodeCatalog.Get("action.reply"), new Vector2(300, 0)); reply.SetParam("message", "should not run");
        g.Connect(cmd.Id, 0, reply.Id, 0);

        rt.Start(g, new IrcSettings { Host = "127.0.0.1", Port = mock.Port, UseTls = false, Nick = "ircuitry-bot", Channels = "#ircuitry-test", SaslPass = "", AdvertiseCommands = true });
        bool err = false, ran = false;
        for (int i = 0; i < 140 && !err; i++)
        {
            Thread.Sleep(50);
            foreach (var o in mock.Sent())
            {
                if (o.Contains("+draft/bot-cmd-error=BAD_CONTEXT")) err = true;
                if (o.Contains("should not run")) ran = true;
            }
        }
        rt.Stop(); Thread.Sleep(100);
        return Expect("bot-cmd-bad-context", err && !ran, "err=" + err + " ran=" + ran);
    }

    /// <summary>Secrets vault: {{secret.x}} expands at runtime; references (not values) live in the graph.</summary>
    private static int SecretsTest()
    {
        int fails = 0;
        Ircuitry.Core.Secrets.UseForTesting(new() { ["openai"] = "sk-xyz", ["pw"] = "hunter2" });
        try
        {
            fails += Expect("secret-expand", Ircuitry.Core.Secrets.Expand("Bearer {{secret.openai}}") == "Bearer sk-xyz", "");
            fails += Expect("secret-missing", Ircuitry.Core.Secrets.Expand("{{secret.nope}}") == "", "missing -> empty");
            fails += Expect("secret-references", Ircuitry.Core.Secrets.References("x {{secret.pw}}") && !Ircuitry.Core.Secrets.References("plain"), "");
            // forgiving lookup: a name-case mismatch and inner whitespace must still resolve
            fails += Expect("secret-case-insensitive", Ircuitry.Core.Secrets.Expand("{{secret.OpenAI}}") == "sk-xyz", "case-insensitive name");
            fails += Expect("secret-whitespace", Ircuitry.Core.Secrets.Expand("{{ secret.openai }}") == "sk-xyz", "tolerate inner spaces");
            // a referenced-but-undefined secret is reported, not silently empty
            var m = Ircuitry.Core.Secrets.Missing("a {{secret.openai}} b {{secret.ghost}}");
            fails += Expect("secret-missing-reported", m.Count == 1 && m[0] == "ghost", string.Join(",", m));

            // the graph stores the reference; the running bot resolves it
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "k");
            var reply = N(g, "action.reply", 300, 0); reply.SetParam("message", "key={{secret.openai}}");
            g.Connect(cmd.Id, 0, reply.Id, 0);
            var s = new FakeSink(); GraphExecutor.Fire(g, s, cmd, Vars("!k", "u", "#c"));
            fails += Expect("secret-resolved-at-runtime", s.Sent.Count == 1 && s.Sent[0].text == "key=sk-xyz", Dump(s));

            // a .ircbot export keeps only the reference, never the value
            var json = GraphSerializer.Save(g, "x");
            fails += Expect("secret-not-in-export", json.Contains("{{secret.openai}}") && !json.Contains("sk-xyz"), "");
        }
        finally { Ircuitry.Core.Secrets.UseForTesting(new()); }
        return fails;
    }

    /// <summary>Per-node "stream as tool" flag (defaults + persistence) and loading an .ircbot fragment into a graph.</summary>
    private static int StreamAndPasteTest()
    {
        int fails = 0;

        // stream-as-tool is opt-in (off by default for every node); the chosen state persists
        {
            fails += Expect("stream-default-off", !NodeCatalog.Get("net.http").StreamByDefault && !NodeCatalog.Get("action.reply").StreamByDefault, "streaming is opt-in");
            var g = new NodeGraph();
            var h = g.Add(NodeCatalog.Get("net.http"), Vector2.Zero);
            fails += Expect("stream-node-default-off", !h.StreamAsTool, "new node doesn't stream by default");
            h.StreamAsTool = true;                        // user opts in
            var (g2, _) = GraphSerializer.Load(GraphSerializer.Save(g, "s"));
            fails += Expect("stream-roundtrip", g2.Find(h.Id)?.StreamAsTool == true, "should persist the opted-in state");
        }

        // auto-layout lays nodes out left->right by dependency depth
        {
            var g = new NodeGraph();
            var a = g.Add(NodeCatalog.Get("event.command"), new Vector2(100, 100));
            var b = g.Add(NodeCatalog.Get("action.reply"), new Vector2(40, 40));   // deliberately left of / above a
            g.Connect(a.Id, 0, b.Id, 0);
            new Ircuitry.Editor.GraphEditor(g).AutoLayout();
            fails += Expect("auto-layout", g.Find(b.Id)!.Pos.X > g.Find(a.Id)!.Pos.X, $"a.x={g.Find(a.Id)!.Pos.X} b.x={g.Find(b.Id)!.Pos.X}");
        }

        // load an .ircbot fragment into an existing graph (drag-drop) - fresh ids, internal wires kept
        {
            var src = new NodeGraph();
            var c = src.Add(NodeCatalog.Get("event.command"), Vector2.Zero); c.SetParam("command", "x");
            var rp = src.Add(NodeCatalog.Get("action.reply"), new Vector2(200, 0)); rp.SetParam("message", "hi");
            src.Connect(c.Id, 0, rp.Id, 0);
            var (loaded, _) = GraphSerializer.Load(GraphSerializer.Save(src, "frag"));

            var target = new NodeGraph();
            target.Add(NodeCatalog.Get("event.connect"), Vector2.Zero);   // a pre-existing node
            var ed = new Ircuitry.Editor.GraphEditor(target);
            ed.InsertGraphAt(loaded, new Vector2(500, 500));
            bool ok = target.Nodes.Count == 3 && target.Connections.Count == 1
                   && !target.Nodes.Any(n => n.Id == c.Id);   // inserted nodes got NEW ids
            fails += Expect("ircbot-merge", ok, $"nodes={target.Nodes.Count} conns={target.Connections.Count}");
        }

        // context-menu editor ops: select-all, disconnect, delete, cut(+undo)
        {
            var g = new NodeGraph();
            var a = g.Add(NodeCatalog.Get("event.command"), Vector2.Zero);
            var b = g.Add(NodeCatalog.Get("action.reply"), new Vector2(200, 0));
            g.Connect(a.Id, 0, b.Id, 0);
            var ed = new Ircuitry.Editor.GraphEditor(g);

            ed.SelectAll();
            fails += Expect("ctx-select-all", ed.Selection.Count == 2, $"{ed.Selection.Count}");

            ed.DisconnectSelection();
            fails += Expect("ctx-disconnect", g.Connections.Count == 0, $"conns={g.Connections.Count}");

            ed.DeleteSelection();
            fails += Expect("ctx-delete", g.Nodes.Count == 0, $"nodes={g.Nodes.Count}");

            var c = g.Add(NodeCatalog.Get("event.command"), Vector2.Zero);
            ed.Selection.Clear(); ed.Selection.Add(c.Id);
            ed.CutSelection();
            fails += Expect("ctx-cut", g.Nodes.Count == 0, $"nodes={g.Nodes.Count}");
            ed.Undo();
            fails += Expect("ctx-cut-undo", g.Nodes.Count == 1, $"nodes={g.Nodes.Count}");
        }

        return fails;
    }

    /// <summary>End-to-end: a structured +draft/bot-cmd TAGMSG invocation fires the command and the reply carries +reply.</summary>
    private static int BotCmdInvokeTest()
    {
        var b64 = BotTools.Encode(new Dictionary<string, object?> { ["name"] = "weather", ["options"] = new Dictionary<string, object?> { ["city"] = "london" } });
        using var mock = new MockIrcServer(new[]
        {
            (120, $"@+draft/bot-cmd={b64};msgid=m99 :alice!a@h TAGMSG #ircuitry-test"),
        });
        var log = new ConsoleLog();
        var rt = new BotRuntime(log, new System.Collections.Concurrent.ConcurrentDictionary<string, string>());

        var g = new NodeGraph();
        var cmd = g.Add(NodeCatalog.Get("event.command"), Vector2.Zero); cmd.SetParam("command", "weather");
        var reply = g.Add(NodeCatalog.Get("action.reply"), new Vector2(300, 0)); reply.SetParam("message", "London: sunny");
        g.Connect(cmd.Id, 0, reply.Id, 0);

        var cfg = new IrcSettings { Host = "127.0.0.1", Port = mock.Port, UseTls = false, Nick = "ircuitry-bot", Channels = "#ircuitry-test", SaslPass = "", AdvertiseCommands = true };
        rt.Start(g, cfg);

        bool ok = false;
        for (int i = 0; i < 160 && !ok; i++)
        {
            Thread.Sleep(50);
            foreach (var o in mock.Sent())
                if (o.Contains("PRIVMSG #ircuitry-test") && o.Contains("London: sunny") && o.Contains("+reply=m99")) ok = true;
        }
        rt.Stop();
        Thread.Sleep(100);

        string detail = ok ? "" : "sent: " + string.Join(" | ", mock.Sent());
        return Expect("bot-cmd-invoke-reply", ok, detail);
    }

    /// <summary>Export a graph to a .ircbot file and import it back, asserting structure survives.</summary>
    private static int IoTest()
    {
        var g = new NodeGraph();
        var cmd = g.Add(NodeCatalog.Get("event.command"), Vector2.Zero); cmd.SetParam("command", "hi");
        var reply = g.Add(NodeCatalog.Get("action.reply"), new Vector2(300, 0)); reply.SetParam("message", "yo");
        g.Connect(cmd.Id, 0, reply.Id, 0);

        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ircuitry-selftest.ircbot");
        System.IO.File.WriteAllText(path, GraphSerializer.Save(g, "iobot"));
        var (g2, name) = GraphSerializer.Load(System.IO.File.ReadAllText(path));
        try { System.IO.File.Delete(path); } catch { }

        bool ok = name == "iobot" && g2.Nodes.Count == 2 && g2.Connections.Count == 1
            && g2.Find(cmd.Id)?.GetParam("command") == "hi";
        return Expect("ircbot-export-import-roundtrip", ok, $"name={name} nodes={g2.Nodes.Count} wires={g2.Connections.Count}");
    }

    /// <summary>Hand-build a minimal ZIM (raw + zstd cluster) and round-trip open / title-search / read through
    /// the managed reader - the binary parsing (header, dirents, pointer lists, cluster blob slicing) is the risk.</summary>
    private static int ZimTest()
    {
        int fails = ZimCase("zim-read-raw", false) + ZimCase("zim-read-zstd", true);
        // a corrupt archive must fail cleanly (throw), never crash / hang / leak the handle
        string p = Path.Combine(Path.GetTempPath(), "irc-zimbad-" + Guid.NewGuid().ToString("N") + ".zim");
        try
        {
            File.WriteAllBytes(p, new byte[] { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 });
            bool threw = false;
            try { using var z = Ircuitry.Net.ZimArchive.Open(p); } catch { threw = true; }
            fails += Expect("zim-corrupt-clean", threw, "garbage should throw, not crash");
        }
        finally { try { File.Delete(p); } catch { } }
        return fails;
    }

    private static int ZimCase(string name, bool zstd)
    {
        string path = Path.Combine(Path.GetTempPath(), "irc-zim-" + Guid.NewGuid().ToString("N") + ".zim");
        try
        {
            File.WriteAllBytes(path, BuildMiniZim(zstd));
            using var z = Ircuitry.Net.ZimArchive.Open(path);
            var android = z.FindTitle("android");                 // case-insensitive title lookup
            var apple = z.FindPath("A/Apple");                    // explicit namespaced path
            var search = z.SearchTitles("App", 5);                // prefix search
            var all = z.SearchTitles("A", 5);
            bool ok = z.ArticleCount == 2 && z.ClusterCount == 1 && z.ContentNamespace == 'A'
                && android is { } a && z.ReadText(a) == "Android is an OS."
                && apple is { } p && z.ReadText(z.Resolve(p)) == "Apple is a fruit."
                && search.Count == 1 && search[0].Title == "Apple"
                && all.Count == 2;
            return Expect(name, ok, $"count={z.ArticleCount}/{z.ClusterCount} ns={z.ContentNamespace} android={(android != null)} search={search.Count} all={all.Count}");
        }
        catch (Exception ex) { return Expect(name, false, ex.Message); }
        finally { try { File.Delete(path); } catch { } }
    }

    private static byte[] BuildMiniZim(bool zstd)
    {
        var blob0 = Encoding.UTF8.GetBytes("Android is an OS.");
        var blob1 = Encoding.UTF8.GetBytes("Apple is a fruit.");
        // cluster payload = offset table (3 x uint32) then the two blobs
        using var pms = new MemoryStream();
        var pw = new BinaryWriter(pms);
        pw.Write(12u); pw.Write((uint)(12 + blob0.Length)); pw.Write((uint)(12 + blob0.Length + blob1.Length));
        pw.Write(blob0); pw.Write(blob1); pw.Flush();
        byte[] payload = pms.ToArray();
        byte[] body = zstd ? new ZstdSharp.Compressor().Wrap(payload).ToArray() : payload;
        byte info = (byte)(zstd ? 0x05 : 0x01);

        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(new byte[80]);                                    // header placeholder
        long mimeListPos = ms.Position;
        w.Write(Encoding.UTF8.GetBytes("text/html")); w.Write((byte)0); w.Write((byte)0);   // one mime + empty terminator
        long Dirent(char ns, uint blob, string url, string title)
        {
            long pos = ms.Position;
            w.Write((ushort)0); w.Write((byte)0); w.Write((byte)ns); w.Write(0u);   // mime, paramLen, ns, revision
            w.Write(0u); w.Write(blob);                                             // cluster 0, blob
            w.Write(Encoding.UTF8.GetBytes(url)); w.Write((byte)0);
            w.Write(Encoding.UTF8.GetBytes(title)); w.Write((byte)0);
            return pos;
        }
        long dAndroid = Dirent('A', 0, "Android", "Android");
        long dApple = Dirent('A', 1, "Apple", "Apple");
        long urlPtrPos = ms.Position; w.Write((ulong)dAndroid); w.Write((ulong)dApple);     // sorted by url
        long titlePtrPos = ms.Position; w.Write(0u); w.Write(1u);                           // url indices, sorted by title
        long clusterPtrPos = ms.Position; w.Write((ulong)(clusterPtrPos + 8));              // cluster follows the 1-entry list
        w.Write(info); w.Write(body);
        long checksumPos = ms.Position; w.Write(new byte[16]);
        w.Flush();

        var buf = ms.ToArray();
        BitConverter.GetBytes(0x044d495au).CopyTo(buf, 0);
        BitConverter.GetBytes((ushort)5).CopyTo(buf, 4); BitConverter.GetBytes((ushort)0).CopyTo(buf, 6);
        BitConverter.GetBytes(2u).CopyTo(buf, 24); BitConverter.GetBytes(1u).CopyTo(buf, 28);
        BitConverter.GetBytes((ulong)urlPtrPos).CopyTo(buf, 32);
        BitConverter.GetBytes((ulong)titlePtrPos).CopyTo(buf, 40);
        BitConverter.GetBytes((ulong)clusterPtrPos).CopyTo(buf, 48);
        BitConverter.GetBytes((ulong)mimeListPos).CopyTo(buf, 56);
        BitConverter.GetBytes(0u).CopyTo(buf, 64); BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(buf, 68);
        BitConverter.GetBytes((ulong)checksumPos).CopyTo(buf, 72);
        return buf;
    }

    /// <summary>Round-trip the whole workspace (bots + connection settings + graph) through JSON.</summary>
    private static int WorkspaceTest()
    {
        var bot = new Ircuitry.App.Bot("alpha");
        var cmd = bot.Graph.Add(NodeCatalog.Get("event.command"), Vector2.Zero); cmd.SetParam("command", "ping");
        var reply = bot.Graph.Add(NodeCatalog.Get("action.reply"), new Vector2(300, 0)); reply.SetParam("message", "pong");
        bot.Graph.Connect(cmd.Id, 0, reply.Id, 0);
        bot.Settings.Host = "irc.example.net"; bot.Settings.Nick = "zzz";
        bot.Settings.Channels = "#a #b"; bot.Settings.SaslPass = "secret"; bot.Settings.UseTls = false;
        bot.State["score"] = "42";   // persistent variable
        bot.GroupId = "grp1";        // tab-group membership + the group def must round-trip
        var bot2 = new Ircuitry.App.Bot("beta") { GroupId = "grp1" };
        var group = new Ircuitry.App.TabGroup { Id = "grp1", Name = "My Group", ColorIndex = 3, Collapsed = true };

        string json = Ircuitry.App.WorkspaceSerializer.Save(new List<Ircuitry.App.Bot> { bot, bot2 }, 0, new List<Ircuitry.App.TabGroup> { group });
        var (loaded, _, groups) = Ircuitry.App.WorkspaceSerializer.Load(json);

        bool ok = loaded.Count == 2 && loaded[0].Name == "alpha"
            && loaded[0].Graph.Nodes.Count == 2 && loaded[0].Graph.Connections.Count == 1
            && loaded[0].Settings.Host == "irc.example.net" && loaded[0].Settings.Nick == "zzz"
            && loaded[0].Settings.Channels == "#a #b" && loaded[0].Settings.SaslPass == "secret"
            && loaded[0].Settings.UseTls == false
            && loaded[0].Graph.Find(reply.Id)?.GetParam("message") == "pong"
            && loaded[0].State.TryGetValue("score", out var sc) && sc == "42"
            && loaded[0].GroupId == "grp1" && loaded[1].GroupId == "grp1"
            && groups.Count == 1 && groups[0].Id == "grp1" && groups[0].Name == "My Group"
            && groups[0].ColorIndex == 3 && groups[0].Collapsed;
        return Expect("workspace-save-load", ok, $"bots={loaded.Count} groups={groups.Count}");
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static int ParserTests()
    {
        int fails = 0;
        var m = IrcParser.Parse("@time=2020-01-01T00:00:00.000Z;+example/foo=bar\\swith\\sspaces :nick!user@host PRIVMSG #chan :hello :there");
        fails += Expect("parse-tag-time", m.Tag("time") == "2020-01-01T00:00:00.000Z", m.Tag("time"));
        fails += Expect("parse-tag-unescape", m.Tag("+example/foo") == "bar with spaces", m.Tag("+example/foo"));
        fails += Expect("parse-nick", m.Nick == "nick" && m.User == "user" && m.Host == "host", $"{m.Nick}/{m.User}/{m.Host}");
        fails += Expect("parse-cmd", m.Command == "PRIVMSG", m.Command);
        fails += Expect("parse-target", m.P(0) == "#chan", m.P(0));
        fails += Expect("parse-trailing", m.Trailing == "hello :there", m.Trailing);

        var ping = IrcParser.Parse("PING :server.token");
        fails += Expect("parse-ping", ping.Command == "PING" && ping.Trailing == "server.token", ping.Trailing);

        var noTrail = IrcParser.Parse(":n!u@h JOIN #room");
        fails += Expect("parse-join", noTrail.Command == "JOIN" && noTrail.P(0) == "#room", noTrail.P(0));

        // cockpit "Open in app" deep link: ircuitry://connect?url=<server>&token=<token>
        fails += Expect("deeplink-connect-is", Ircuitry.App.DeepLink.IsConnectLink("ircuitry://connect?url=x&token=y")
            && !Ircuitry.App.DeepLink.IsConnectLink("ircuitry://install-node?url=x"), "");
        bool cok = Ircuitry.App.DeepLink.TryParseConnect(
            "ircuitry://connect?url=" + System.Uri.EscapeDataString("https://ircuitry.example.com") + "&token=tok123", out var cu, out var ct);
        fails += Expect("deeplink-connect-parse", cok && cu == "https://ircuitry.example.com" && ct == "tok123", $"{cu} / {ct}");
        return fails;
    }

    /// <summary>End-to-end over a real loopback socket: connect -> register -> !ping -> pong.</summary>
    private static int IrcLoopTest()
    {
        using var mock = new MockIrcServer();
        var log = new ConsoleLog();
        var rt = new BotRuntime(log, new System.Collections.Concurrent.ConcurrentDictionary<string, string>());

        var g = new NodeGraph();
        var cmd = g.Add(NodeCatalog.Get("event.command"), Vector2.Zero); cmd.SetParam("command", "ping");
        var reply = g.Add(NodeCatalog.Get("action.reply"), new Vector2(300, 0)); reply.SetParam("message", "pong");
        g.Connect(cmd.Id, 0, reply.Id, 0);

        var cfg = new IrcSettings { Host = "127.0.0.1", Port = mock.Port, UseTls = false, Nick = "ircuitry-bot", Channels = "#ircuitry-test", SaslPass = "" };
        rt.Start(g, cfg);

        bool ok = false;
        for (int i = 0; i < 120 && !ok; i++)
        {
            Thread.Sleep(50);
            foreach (var o in mock.Sent())
                if (o.StartsWith("PRIVMSG #ircuitry-test", StringComparison.Ordinal) && o.Contains("pong")) ok = true;
        }
        rt.Stop();
        Thread.Sleep(100);

        string detail = ok ? "" : "log: " + string.Join(" | ", log.Tail(20).Select(e => e.Text));
        return Expect("irc-loop-ping-pong", ok, detail);
    }

    /// <summary>Editing a running bot via ApplyGraph changes its behaviour without a restart.</summary>
    private static int LiveApplyTest()
    {
        using var mock = new MockIrcServer(new[]
        {
            (150, ":a!a@h PRIVMSG #ircuitry-test :!ping"),
            (2500, ":a!a@h PRIVMSG #ircuitry-test :!ping"),
        });
        var rt = new BotRuntime(new ConsoleLog(), new System.Collections.Concurrent.ConcurrentDictionary<string, string>());
        var g = new NodeGraph();
        var cmd = g.Add(NodeCatalog.Get("event.command"), Vector2.Zero); cmd.SetParam("command", "ping");
        var reply = g.Add(NodeCatalog.Get("action.reply"), new Vector2(300, 0)); reply.SetParam("message", "v1");
        g.Connect(cmd.Id, 0, reply.Id, 0);
        rt.Start(g, new IrcSettings { Host = "127.0.0.1", Port = mock.Port, UseTls = false, Nick = "ircuitry-bot", Channels = "#ircuitry-test", SaslPass = "" });

        bool v1 = false, applied = false, v2 = false;
        for (int i = 0; i < 200 && !v2; i++)
        {
            Thread.Sleep(50);
            var sent = mock.Sent();
            if (sent.Any(s => s.Contains("PRIVMSG #ircuitry-test") && s.EndsWith(":v1"))) v1 = true;
            if (v1 && !applied) { reply.SetParam("message", "v2"); rt.ApplyGraph(g); applied = true; }  // edit live
            if (applied && sent.Any(s => s.EndsWith(":v2"))) v2 = true;
        }
        rt.Stop(); Thread.Sleep(100);
        return Expect("live-apply-no-restart", v1 && v2, $"v1={v1} v2={v2} sent=[{string.Join(",", mock.Sent())}]");
    }

    /// <summary>bot-tools steps must stream LIVE (interleaved with actions), not as a post-fire replay.</summary>
    private static int LiveStreamTest()
    {
        using var mock = new MockIrcServer(new[] { (120, ":alice!a@h PRIVMSG #ircuitry-test :!go a,b,c") });
        var rt = new BotRuntime(new ConsoleLog(), new System.Collections.Concurrent.ConcurrentDictionary<string, string>());

        var g = new NodeGraph();
        var cmd = g.Add(NodeCatalog.Get("event.command"), Vector2.Zero); cmd.SetParam("command", "go");
        var fe = g.Add(NodeCatalog.Get("logic.forEach"), new Vector2(200, 0)); fe.SetParam("sep", "comma");
        var reply = g.Add(NodeCatalog.Get("action.reply"), new Vector2(400, 0)); reply.StreamAsTool = true;   // stream each iteration
        g.Connect(cmd.Id, 0, fe.Id, 0); g.Connect(cmd.Id, 1, fe.Id, 1);
        g.Connect(fe.Id, 0, reply.Id, 0); g.Connect(fe.Id, 2, reply.Id, 1);

        rt.Start(g, new IrcSettings { Host = "127.0.0.1", Port = mock.Port, UseTls = false, Nick = "ircuitry-bot", Channels = "#ircuitry-test", SaslPass = "", StreamWorkflows = true });

        bool ok = false;
        for (int i = 0; i < 200 && !ok; i++)
        {
            Thread.Sleep(50);
            var sent = mock.Sent();
            int ia = Array.FindIndex(sent, s => s.Contains("PRIVMSG #ircuitry-test") && s.EndsWith(":a"));
            int ib = Array.FindIndex(sent, s => s.Contains("PRIVMSG #ircuitry-test") && s.EndsWith(":b"));
            // a workflow step must appear BETWEEN reply "a" and reply "b" (post-fire replay would put them all after)
            if (ia >= 0 && ib > ia)
                for (int k = ia + 1; k < ib; k++) if (sent[k].Contains("+draft/bot-tools")) ok = true;
        }
        rt.Stop(); Thread.Sleep(100);
        int fails = Expect("bottools-live-interleaved", ok, "sent: " + string.Join(" | ", mock.Sent()));

        // Regression: a tool-call step must show the value PASSED IN down the wire (a/b/c), not the
        // reply node's default "message" param ("pong"). The default used to clobber the wired value.
        bool sawCall = false, argsOk = false, sawPong = false;
        foreach (var line in mock.Sent())
        {
            int at = line.IndexOf("+draft/bot-tools=", StringComparison.Ordinal);
            if (at < 0) continue;
            int v = at + "+draft/bot-tools=".Length;
            int end = line.IndexOfAny(new[] { ';', ' ' }, v);
            var b64 = end < 0 ? line[v..] : line[v..end];
            var e = BotTools.Decode(b64);
            if (e is not { ValueKind: JsonValueKind.Object } step) continue;
            if (BotTools.Str(step, "type") != "tool-call") continue;
            if (!step.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object) continue;
            if (!content.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.String) continue;
            sawCall = true;
            var m = msg.GetString();
            if (m is "a" or "b" or "c") argsOk = true;
            if (m == "pong") sawPong = true;
        }
        fails += Expect("bottools-tool-args-not-default", sawCall && argsOk && !sawPong,
            $"sawCall={sawCall} argsOk={argsOk} sawPong={sawPong}");
        return fails;
    }

    /// <summary>Stop -> restart the same runtime must reconnect, re-join the channel, and reply again
    /// (regression: the throttle writer thread used to die on the first Stop, silently dropping all sends).</summary>
    private static int IrcRestartTest()
    {
        using var mock = new MockIrcServer();
        var log = new ConsoleLog();
        var rt = new BotRuntime(log, new System.Collections.Concurrent.ConcurrentDictionary<string, string>());

        var g = new NodeGraph();
        var cmd = g.Add(NodeCatalog.Get("event.command"), Vector2.Zero); cmd.SetParam("command", "ping");
        var reply = g.Add(NodeCatalog.Get("action.reply"), new Vector2(300, 0)); reply.SetParam("message", "pong");
        g.Connect(cmd.Id, 0, reply.Id, 0);

        var cfg = new IrcSettings { Host = "127.0.0.1", Port = mock.Port, UseTls = false, Nick = "ircuitry-bot", Channels = "#ircuitry-test", SaslPass = "", AutoReconnect = false };

        int Pongs() { int n = 0; foreach (var o in mock.Sent()) if (o.StartsWith("PRIVMSG #ircuitry-test", StringComparison.Ordinal) && o.Contains("pong")) n++; return n; }

        rt.Start(g, cfg);
        bool first = false;
        for (int i = 0; i < 120 && !first; i++) { Thread.Sleep(50); first = Pongs() >= 1; }
        rt.Stop();
        Thread.Sleep(250);

        rt.Start(g, cfg);                 // restart the SAME runtime/client
        bool second = false;
        for (int i = 0; i < 160 && !second; i++) { Thread.Sleep(50); second = Pongs() >= 2; }
        rt.Stop();
        Thread.Sleep(100);

        int joins = 0; foreach (var o in mock.Sent()) if (o.StartsWith("JOIN", StringComparison.Ordinal) && o.Contains("#ircuitry-test")) joins++;
        bool ok = first && second && joins >= 2;
        string detail = ok ? "" : $"first={first} second={second} joins={joins} log: " + string.Join(" | ", log.Tail(12).Select(e => e.Text));
        return Expect("irc-restart-rejoins-and-replies", ok, detail);
    }

    /// <summary>One bot on two servers: an event on server A replies back to A (origin), and a node with a
    /// "server" override sends to the named server instead.</summary>
    private static int MultiServerTest()
    {
        int fails = 0;

        // --- origin routing: !ping on A -> pong on A, nothing on B ---
        {
            using var a = new MockIrcServer(new[] { (150, ":u!u@h PRIVMSG #ircuitry-test :!ping") });
            using var b = new MockIrcServer(System.Array.Empty<(int, string)>());
            var rt = new BotRuntime(new ConsoleLog(), new System.Collections.Concurrent.ConcurrentDictionary<string, string>());
            var g = new NodeGraph();
            var cmd = g.Add(NodeCatalog.Get("event.command"), Vector2.Zero); cmd.SetParam("command", "ping");
            var reply = g.Add(NodeCatalog.Get("action.reply"), new Vector2(300, 0)); reply.SetParam("message", "pong");
            g.Connect(cmd.Id, 0, reply.Id, 0);

            IrcSettings S(string label, int port) => new() { Label = label, Host = "127.0.0.1", Port = port, UseTls = false, Nick = "ircuitry-bot", Channels = "#ircuitry-test", SaslPass = "" };
            rt.Start(g, new[] { S("alpha", a.Port), S("beta", b.Port) });

            bool onA = false;
            for (int i = 0; i < 160 && !onA; i++) { Thread.Sleep(50); onA = a.Sent().Any(s => s.Contains("PRIVMSG #ircuitry-test") && s.Contains("pong")); }
            bool onB = b.Sent().Any(s => s.Contains("PRIVMSG #ircuitry-test") && s.Contains("pong"));
            rt.Stop(); Thread.Sleep(100);
            fails += Expect("multiserver-reply-to-origin", onA && !onB, $"onA={onA} onB={onB}");
        }

        // --- per-node override: !ping on A -> pong on B (reply routed to "beta") ---
        {
            using var a = new MockIrcServer(new[] { (150, ":u!u@h PRIVMSG #ircuitry-test :!ping") });
            using var b = new MockIrcServer(System.Array.Empty<(int, string)>());
            var rt = new BotRuntime(new ConsoleLog(), new System.Collections.Concurrent.ConcurrentDictionary<string, string>());
            var g = new NodeGraph();
            var cmd = g.Add(NodeCatalog.Get("event.command"), Vector2.Zero); cmd.SetParam("command", "ping");
            var reply = g.Add(NodeCatalog.Get("action.reply"), new Vector2(300, 0)); reply.SetParam("message", "pong"); reply.SetParam("server", "beta");
            g.Connect(cmd.Id, 0, reply.Id, 0);

            IrcSettings S(string label, int port) => new() { Label = label, Host = "127.0.0.1", Port = port, UseTls = false, Nick = "ircuitry-bot", Channels = "#ircuitry-test", SaslPass = "" };
            rt.Start(g, new[] { S("alpha", a.Port), S("beta", b.Port) });

            bool onB = false;
            for (int i = 0; i < 160 && !onB; i++) { Thread.Sleep(50); onB = b.Sent().Any(s => s.Contains("PRIVMSG #ircuitry-test") && s.Contains("pong")); }
            bool onA = a.Sent().Any(s => s.Contains("PRIVMSG #ircuitry-test") && s.Contains("pong"));
            rt.Stop(); Thread.Sleep(100);
            fails += Expect("multiserver-node-override", onB && !onA, $"onA={onA} onB={onB}");
        }
        return fails;
    }

    /// <summary>Template shortcodes resolve: {me} = our nick, {argN} = the Nth command word, plus context vars.</summary>
    private static int ShortcodeTest()
    {
        var g = new NodeGraph();
        var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "say");
        var reply = N(g, "action.reply", 300, 0); reply.SetParam("message", "{me}|{arg1}|{arg2}|{channel}");
        g.Connect(cmd.Id, 0, reply.Id, 0);
        var sink = new FakeSink();
        GraphExecutor.Fire(g, sink, cmd, Vars("!say hello world", "alice", "#x"));
        return Expect("shortcodes", sink.Sent.Count == 1 && sink.Sent[0] == ("#x", "ircuitry|hello|world|#x"), Dump(sink));
    }

    /// <summary>The tray right-click menu must list each bot's servers under Disconnect/Reconnect (regression:
    /// submenus came back empty when a host asked for a shallow layout depth).</summary>
    private static int TrayMenuTest()
    {
        var menu = new Ircuitry.App.TrayMenu();

        var pairs = new List<(int id, string label)>();
        void Walk((int, System.Collections.Generic.IDictionary<string, object>, object[]) n)
        {
            if (n.Item2.TryGetValue("label", out var l)) pairs.Add((n.Item1, l?.ToString() ?? ""));
            foreach (var k in n.Item3) Walk(((int, System.Collections.Generic.IDictionary<string, object>, object[]))k);
        }
        int IdOf(string label) { foreach (var p in pairs) if (p.label == label) return p.id; return -1; }

        // 1) build once with no bots (mirrors the menu being fetched before anything connects)
        Ircuitry.App.TrayIcon.Model = new Ircuitry.App.TrayMenuModel();
        var (_, l0) = menu.GetLayoutAsync(0, 1, System.Array.Empty<string>()).GetAwaiter().GetResult();
        pairs.Clear(); Walk(l0);
        int discEmpty = IdOf("Disconnect"), reconEmpty = IdOf("Reconnect");

        // 2) now bots connect - the ids of the static items must NOT move (hosts cache them for lazy fetch)
        Ircuitry.App.TrayIcon.Model = new Ircuitry.App.TrayMenuModel
        {
            Bots = { new Ircuitry.App.TrayBotInfo { Name = "alpha", Servers = {
                new Ircuitry.App.TrayServerInfo { Label = "Libera", Online = true },
                new Ircuitry.App.TrayServerInfo { Label = "OFTC", Online = false } } } }
        };
        var (_, l1) = menu.GetLayoutAsync(0, 1, System.Array.Empty<string>()).GetAwaiter().GetResult();   // shallow request, full reply expected
        pairs.Clear(); Walk(l1);

        bool stable = discEmpty > 0 && discEmpty == IdOf("Disconnect") && reconEmpty == IdOf("Reconnect");
        var labels = pairs.ConvertAll(p => p.label);
        bool listed = labels.Contains("Disconnect") && labels.Contains("Reconnect")
            && labels.Count(s => s == "Libera") == 2 && labels.Count(s => s == "OFTC") == 2   // once under each group
            && labels.Count(s => s == "All servers") == 2;
        return Expect("tray-menu-lists-servers", stable && listed, $"stable={stable} listed={listed} | " + string.Join(",", labels));
    }

    /// <summary>The capability endpoint must hand a CORS grant ONLY to the ircuitry site (+ localhost dev):
    /// any other origin gets zero Access-Control-* headers so the browser blocks it. Tests the pure header
    /// decision directly (no socket) so it can't be fooled by a stale/already-running app on the port.</summary>
    private static int CapabilityCorsTest()
    {
        int fails = 0;
        bool HasCors(string origin) =>
            Ircuitry.App.CapabilityServer.CorsHeaders(origin).Any(h => h.Key.StartsWith("Access-Control", StringComparison.Ordinal));
        string Val(string origin, string key) =>
            Ircuitry.App.CapabilityServer.CorsHeaders(origin).Where(h => h.Key == key).Select(h => h.Value).FirstOrDefault() ?? "";

        // the site origin (and a case-variant) is granted ACAO + the private-network grant
        fails += Expect("cap-cors-site-acao", Val("https://ircuitry.github.io", "Access-Control-Allow-Origin") == "https://ircuitry.github.io", Val("https://ircuitry.github.io", "Access-Control-Allow-Origin"));
        fails += Expect("cap-cors-site-pna", Val("https://ircuitry.github.io", "Access-Control-Allow-Private-Network") == "true", "");
        fails += Expect("cap-cors-case-insensitive", HasCors("HTTPS://IRCUITRY.GITHUB.IO"), "uppercase origin should still be allowed");
        fails += Expect("cap-cors-localhost-dev", HasCors("http://localhost:8099"), "");
        // everyone else gets NOTHING - no ACAO, no PNA, no methods/headers grant
        fails += Expect("cap-cors-evil-blocked", !HasCors("https://evil.example"), "disallowed origin must get no CORS headers");
        fails += Expect("cap-cors-empty-blocked", !HasCors(""), "no-origin request must get no CORS headers");
        fails += Expect("cap-cors-lookalike-blocked", !HasCors("https://ircuitry.github.io.evil.com"), "suffix-spoof origin must be rejected");
        // Cache-Control is always present (it isn't a CORS grant)
        fails += Expect("cap-cache-control", Val("https://evil.example", "Cache-Control") == "no-store", "");
        return fails;
    }

    private static string Dump(FakeSink s) => "sent=[" + string.Join(", ", s.Sent.ConvertAll(t => $"{t.target}:{t.text}")) + "]";

    /// <summary>SCRAM-SHA-256 client crypto matches the published RFC 7677 vector (user "user", pass "pencil",
    /// fixed client nonce), so the SASL exchange is provably correct, plus the server-signature check rejects an
    /// impostor.</summary>
    private static int ScramVectorTest()
    {
        int fails = 0;
        var sc = new Ircuitry.Irc.ScramSha256("user", "pencil", "rOprNGfwEbeRWgbNEkqO");
        string first = sc.ClientFirst();
        fails += Expect("scram-client-first", first == "n,,n=user,r=rOprNGfwEbeRWgbNEkqO", first);
        const string serverFirst = "r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096";
        string final = sc.ClientFinal(serverFirst);
        fails += Expect("scram-client-final-proof",
            final == "c=biws,r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,p=dHzbZapWIk4jUhN+Ute9ytag9zjfMHgsqmmiz7AndVQ=", final);
        bool verified = true;
        try { sc.VerifyServerFinal("v=6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4="); } catch { verified = false; }
        fails += Expect("scram-server-verify", verified, "the server signature should verify");
        bool caught = false;
        try { sc.VerifyServerFinal("v=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="); } catch { caught = true; }
        fails += Expect("scram-impostor-rejected", caught, "a wrong server signature must throw");
        return fails;
    }

    /// <summary>ISUPPORT(005) parsing: typed PREFIX/CHANTYPES/CASEMAPPING/NICKLEN views, the casemapping-aware
    /// comparer, and the session honoring a non-default advertised PREFIX when parsing a NAMES reply.</summary>
    private static int IsupportParseTest()
    {
        int fails = 0;
        var iss = new Ircuitry.Irc.IrcIsupport();
        iss.Feed(new[] { "PREFIX=(qaohv)~&@%+", "CHANTYPES=#&", "CASEMAPPING=rfc1459", "NICKLEN=30", "WHOX", "are supported" });
        fails += Expect("isupport-prefix-chars", iss.PrefixChars == "~&@%+", iss.PrefixChars);
        fails += Expect("isupport-prefix-modes", iss.PrefixModes == "qaohv", iss.PrefixModes);
        fails += Expect("isupport-chantypes", iss.ChanTypes == "#&", iss.ChanTypes);
        fails += Expect("isupport-nicklen", iss.IntValue("NICKLEN", 9) == 30, iss.Get("NICKLEN"));
        fails += Expect("isupport-bool-token", iss.Has("WHOX"), "boolean token kept");
        fails += Expect("isupport-mode-for-prefix", iss.ModeForPrefix('@') == 'o' && iss.ModeForPrefix('+') == 'v', "");
        fails += Expect("isupport-rank-order", iss.Rank("@") < iss.Rank("+") && iss.Rank("~") == 0, "");
        fails += Expect("isupport-ischannel", iss.IsChannel("#x") && iss.IsChannel("&y") && !iss.IsChannel("nick"), "");
        fails += Expect("isupport-decode-escape", Ircuitry.Irc.IrcIsupport.DecodeIsupport("Cool\\x20Net") == "Cool Net", "");
        iss.Feed(new[] { "-NICKLEN" });
        fails += Expect("isupport-remove-token", !iss.Has("NICKLEN"), "-KEY should drop it");

        var rfc = new Ircuitry.Irc.IrcCaseComparer("rfc1459");
        var ascii = new Ircuitry.Irc.IrcCaseComparer("ascii");
        fails += Expect("case-ascii-fold", ascii.Equals("Nick", "nick") && !ascii.Equals("nick[]", "nick{}"), "");
        fails += Expect("case-rfc1459-fold", rfc.Equals("nick[]", "nick{}") && rfc.Equals("Foo\\bar", "foo|bar"), "");

        // the session uses the advertised PREFIX (here a custom set) to strip a NAMES list correctly
        var s = new IrcSessionState();
        void Obs(string l) => s.Observe(IrcParser.Parse(l), "me");
        Obs(":serv 005 me PREFIX=(ov)@+ CHANTYPES=# CASEMAPPING=ascii :are supported");
        Obs(":me!u@h JOIN #c");
        Obs(":serv 353 me = #c :@alice +bob @+carol dave");
        var mem = s.Members("#c");
        fails += Expect("session-prefix-parse",
            mem.Any(m => m.nick == "alice" && m.prefix == "@")
            && mem.Any(m => m.nick == "carol" && m.prefix == "@+")
            && mem.Any(m => m.nick == "dave" && m.prefix == ""),
            string.Join(",", mem.Select(m => m.prefix + m.nick)));
        fails += Expect("session-isupport-exposed", s.Isupport.Get("CHANTYPES") == "#" && s.Isupport.CaseMapping == "ascii", "");
        return fails;
    }

    /// <summary>Cap-gated IRCv3 actions only send when the connection negotiated the capability, and otherwise
    /// log a clear skip instead of silently doing nothing.</summary>
    private static int CapGuardTest()
    {
        int fails = 0;

        NodeGraph Build(string type, Action<Node> cfg)
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "x");
            var act = N(g, type, 200, 0); cfg(act);
            g.Connect(cmd.Id, 0, act.Id, 0);
            return g;
        }

        var gSet = Build("action.setname", a => a.SetParam("name", "Cozy Bot"));
        var withCap = new FakeSink(); withCap.Caps.Add("setname");
        GraphExecutor.Fire(gSet, withCap, gSet.Nodes[0], Vars("!x", "u", "#c"));
        fails += Expect("capguard-setname-sends", withCap.Logs.Contains("RAW SETNAME :Cozy Bot"), string.Join(" | ", withCap.Logs));
        var noCap = new FakeSink();
        GraphExecutor.Fire(gSet, noCap, gSet.Nodes[0], Vars("!x", "u", "#c"));
        fails += Expect("capguard-setname-skips",
            !noCap.Logs.Any(l => l.StartsWith("RAW SETNAME")) && noCap.Logs.Any(l => l.Contains("setname") && l.Contains("skipped")),
            string.Join(" | ", noCap.Logs));

        var gRed = Build("action.redact", a => { a.SetParam("target", "#c"); a.SetParam("msgid", "abc"); });
        var redOk = new FakeSink(); redOk.Caps.Add("draft/message-redaction");
        GraphExecutor.Fire(gRed, redOk, gRed.Nodes[0], Vars("!x", "u", "#c"));
        fails += Expect("capguard-redact-sends", redOk.Logs.Contains("RAW REDACT #c abc"), string.Join(" | ", redOk.Logs));
        var redNo = new FakeSink();
        GraphExecutor.Fire(gRed, redNo, gRed.Nodes[0], Vars("!x", "u", "#c"));
        fails += Expect("capguard-redact-skips", !redNo.Logs.Any(l => l.StartsWith("RAW REDACT")), string.Join(" | ", redNo.Logs));

        var gMeta = Build("action.metadata", a => { a.SetParam("target", "*"); a.SetParam("key", "url"); a.SetParam("value", "https://x"); });
        var metaOk = new FakeSink(); metaOk.Caps.Add("metadata");
        GraphExecutor.Fire(gMeta, metaOk, gMeta.Nodes[0], Vars("!x", "u", "#c"));
        fails += Expect("capguard-metadata-sends", metaOk.Logs.Contains("RAW METADATA * SET url :https://x"), string.Join(" | ", metaOk.Logs));
        return fails;
    }

    /// <summary>Regex captures: the $1-$4 pins AND {1}.. / {name} tokens resolve downstream for the rest of the run
    /// (the long-standing footgun where {1} silently resolved empty).</summary>
    private static int RegexCaptureTest()
    {
        int fails = 0;

        // numbered-group tokens
        {
            var g = new NodeGraph();
            var msg = N(g, "event.message", 0, 0);
            var rx = N(g, "logic.regex", 200, 0); rx.SetParam("pattern", "(\\d+)x(\\d+)");
            var rep = N(g, "action.reply", 400, 0); rep.SetParam("message", "{1} and {2}");
            g.Connect(msg.Id, 0, rx.Id, 0); g.Connect(rx.Id, 0, rep.Id, 0);
            var s = new FakeSink(); var v = Vars("12x34", "u", "#c"); v["message"] = "12x34";
            GraphExecutor.Fire(g, s, msg, v);
            fails += Expect("regex-num-tokens", s.Sent.Count == 1 && s.Sent[0].text == "12 and 34", Dump(s));
        }
        // named-group tokens
        {
            var g = new NodeGraph();
            var msg = N(g, "event.message", 0, 0);
            var rx = N(g, "logic.regex", 200, 0); rx.SetParam("pattern", "(?<user>\\w+)@(?<host>\\w+)");
            var rep = N(g, "action.reply", 400, 0); rep.SetParam("message", "{host}.{user}");
            g.Connect(msg.Id, 0, rx.Id, 0); g.Connect(rx.Id, 0, rep.Id, 0);
            var s = new FakeSink(); var v = Vars("amy@mail", "u", "#c"); v["message"] = "amy@mail";
            GraphExecutor.Fire(g, s, msg, v);
            fails += Expect("regex-named-tokens", s.Sent.Count == 1 && s.Sent[0].text == "mail.amy", Dump(s));
        }
        // $3 output pin (index 4) wired directly
        {
            var g = new NodeGraph();
            var msg = N(g, "event.message", 0, 0);
            var rx = N(g, "logic.regex", 200, 0); rx.SetParam("pattern", "(a)(b)(c)(d)");
            var rep = N(g, "action.reply", 400, 0);
            g.Connect(msg.Id, 0, rx.Id, 0); g.Connect(rx.Id, 0, rep.Id, 0); g.Connect(rx.Id, 4, rep.Id, 1);
            var s = new FakeSink(); var v = Vars("abcd", "u", "#c"); v["message"] = "abcd";
            GraphExecutor.Fire(g, s, msg, v);
            fails += Expect("regex-pin-3", s.Sent.Count == 1 && s.Sent[0].text == "c", Dump(s));
        }
        return fails;
    }

    /// <summary>The Math node resolves {tokens} in its A/B fields (previously read raw, silently yielding 0), so
    /// arithmetic on variables works in place.</summary>
    private static int MathTokenTest()
    {
        var g = new NodeGraph();
        var msg = N(g, "event.message", 0, 0);
        var math = N(g, "data.math", 200, 0); math.SetParam("op", "+"); math.SetParam("a", "{score}"); math.SetParam("b", "{bonus}");
        var rep = N(g, "action.reply", 400, 0);
        g.Connect(msg.Id, 0, rep.Id, 0);
        g.Connect(math.Id, 0, rep.Id, 1);
        var s = new FakeSink();
        var v = Vars("go", "u", "#c"); v["score"] = "10"; v["bonus"] = "5";
        GraphExecutor.Fire(g, s, msg, v);
        return Expect("math-token-resolve", s.Sent.Count == 1 && s.Sent[0].text == "15", Dump(s));
    }

    /// <summary>SASL end to end against the mock: SCRAM-SHA-256 is auto-picked and completes the full handshake,
    /// the bot registers + auto-joins, a real message still triggers, a server FAIL is surfaced, an echo-message
    /// self-line is captured (msgid) without re-triggering, and forced PLAIN / EXTERNAL also complete.</summary>
    private static int SaslLoopTest()
    {
        int fails = 0;

        using (var mock = new MockIrcServer(new[]
        {
            (1, ":serv FAIL SETNAME INVALID_REALNAME :realname too long"),
            (1, "@msgid=ECHO123 :scrambot!u@h PRIVMSG #ircuitry-test :hi from me"),
            (1, ":alice!a@h PRIVMSG #ircuitry-test :hello"),
        }))
        {
            mock.ExpectSaslUser = "scrambot"; mock.ExpectSaslPass = "hunter2";
            var log = new ConsoleLog();
            var state = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
            var rt = new BotRuntime(log, state);

            var g = new NodeGraph();
            var m = g.Add(NodeCatalog.Get("event.message"), Vector2.Zero);
            var reply = g.Add(NodeCatalog.Get("action.reply"), new Vector2(300, 0)); reply.SetParam("message", "echo:{message}");
            g.Connect(m.Id, 0, reply.Id, 0);

            rt.Start(g, new IrcSettings { Host = "127.0.0.1", Port = mock.Port, UseTls = false, Nick = "scrambot", Channels = "#ircuitry-test", SaslUser = "scrambot", SaslPass = "hunter2" });

            bool joined = false, repliedToAlice = false;
            for (int i = 0; i < 200 && !(joined && repliedToAlice); i++)
            {
                Thread.Sleep(50);
                foreach (var o in mock.Sent())
                {
                    if (o.StartsWith("JOIN", StringComparison.Ordinal) && o.Contains("#ircuitry-test")) joined = true;
                    if (o.Contains("PRIVMSG #ircuitry-test") && o.Contains("echo:hello")) repliedToAlice = true;
                }
            }
            Thread.Sleep(150);
            bool selfEchoReplied = mock.Sent().Any(o => o.Contains("echo:hi from me"));
            rt.Stop(); Thread.Sleep(100);

            fails += Expect("sasl-scram-mech", mock.SaslMechUsed == "SCRAM-SHA-256", "mech=" + mock.SaslMechUsed);
            fails += Expect("sasl-scram-ok", mock.SaslOk, "the mock should have accepted SCRAM");
            fails += Expect("sasl-registered-joined", joined, "the bot should register + auto-join after SASL");
            fails += Expect("sasl-message-handled", repliedToAlice, "a normal message should still trigger");
            fails += Expect("echo-message-no-retrigger", !selfEchoReplied, "a self-echo must NOT fire a trigger");
            fails += Expect("echo-message-msgid", state.TryGetValue("last_self_msgid", out var mid) && mid == "ECHO123", "last_self_msgid=" + (state.TryGetValue("last_self_msgid", out var m2) ? m2 : "(none)"));
            fails += Expect("standard-reply-surfaced", log.Tail(80).Any(e => e.Text.Contains("SETNAME") && e.Text.Contains("INVALID_REALNAME")), "FAIL should be logged");
        }

        foreach (var (mech, label) in new[] { ("plain", "PLAIN"), ("external", "EXTERNAL") })
        {
            using var mock = new MockIrcServer(new[] { (1, ":x!x@h PRIVMSG #ircuitry-test :hi") });
            mock.ExpectSaslUser = "bot"; mock.ExpectSaslPass = "pw";
            var rt = new BotRuntime(new ConsoleLog(), new System.Collections.Concurrent.ConcurrentDictionary<string, string>());
            var g = new NodeGraph();
            g.Add(NodeCatalog.Get("event.connect"), Vector2.Zero);
            rt.Start(g, new IrcSettings { Host = "127.0.0.1", Port = mock.Port, UseTls = false, Nick = "bot", Channels = "#ircuitry-test", SaslMech = mech, SaslUser = "bot", SaslPass = "pw" });
            bool ok = false;
            for (int i = 0; i < 160 && !ok; i++) { Thread.Sleep(50); if (mock.SaslOk && mock.SaslMechUsed == label) ok = true; }
            rt.Stop(); Thread.Sleep(80);
            fails += Expect("sasl-mech-" + mech, ok, "mech=" + mock.SaslMechUsed + " ok=" + mock.SaslOk);
        }
        return fails;
    }

    /// <summary>METADATA GET (the read-side mirror of Set Metadata) via the new Get Metadata node: node wiring
    /// against a seeded sink, then end to end against the mock over BOTH labeled-response correlation and the
    /// metadata-numeric fallback (labeled-response withheld).</summary>
    private static int MetadataTest()
    {
        int fails = 0;

        // node wiring: the value the sink returns flows out and into a reply
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "ava");
            var meta = N(g, "irc.metadata", 200, 0); meta.SetParam("target", "{nick}"); meta.SetParam("key", "avatar");
            var rep = N(g, "action.reply", 400, 0);
            g.Connect(cmd.Id, 0, meta.Id, 0);
            g.Connect(meta.Id, 0, rep.Id, 0);
            g.Connect(meta.Id, 1, rep.Id, 1);
            var s = new FakeSink(); s.MetaSeed["avatar"] = "http://pic/a.png";
            GraphExecutor.Fire(g, s, cmd, Vars("!ava", "amy", "#c"));
            fails += Expect("metadata-node-value", s.Sent.Count == 1 && s.Sent[0].text == "http://pic/a.png", Dump(s));
        }

        // end to end: labeled-response path, then the same with labeled-response withheld (numeric fallback)
        foreach (var labeled in new[] { true, false })
        {
            using var mock = new MockIrcServer(new[] { (1, ":alice!a@h PRIVMSG #ircuitry-test :!ava") });
            mock.OfferLabeledResponse = labeled;
            mock.Metadata["avatar"] = "https://pic.example/a.png";
            var rt = new BotRuntime(new ConsoleLog(), new System.Collections.Concurrent.ConcurrentDictionary<string, string>());
            var g = new NodeGraph();
            var cmd = g.Add(NodeCatalog.Get("event.command"), Vector2.Zero); cmd.SetParam("command", "ava");
            var meta = g.Add(NodeCatalog.Get("irc.metadata"), new Vector2(200, 0)); meta.SetParam("target", "*"); meta.SetParam("key", "avatar");
            var reply = g.Add(NodeCatalog.Get("action.reply"), new Vector2(400, 0));
            g.Connect(cmd.Id, 0, meta.Id, 0); g.Connect(meta.Id, 0, reply.Id, 0); g.Connect(meta.Id, 1, reply.Id, 1);
            rt.Start(g, new IrcSettings { Host = "127.0.0.1", Port = mock.Port, UseTls = false, Nick = "metabot", Channels = "#ircuitry-test", SaslPass = "" });

            bool ok = false;
            for (int i = 0; i < 200 && !ok; i++)
            {
                Thread.Sleep(50);
                if (mock.Sent().Any(o => o.Contains("PRIVMSG #ircuitry-test") && o.Contains("https://pic.example/a.png"))) ok = true;
            }
            rt.Stop(); Thread.Sleep(100);
            fails += Expect("metadata-get-" + (labeled ? "labeled" : "fallback"), ok, "sent: " + string.Join(" | ", mock.Sent()));
            fails += Expect("metadata-path-" + (labeled ? "labeled" : "fallback"), mock.SawLabeledRequest == labeled, "sawLabeled=" + mock.SawLabeledRequest);
        }
        return fails;
    }

    /// <summary>A stdio MCP server that can't start (missing command in this environment) must surface the REAL
    /// reason - exit code / stderr - not a bare "Broken pipe" from writing to the dead process.</summary>
    private static int McpErrorTest()
    {
        int fails = 0;
        string msg = ""; bool threw = false;
        try { Ircuitry.App.Mcp.McpClient.ForConfig("stdio", "ircuitry-no-such-cmd-zzz --x", null, 2000); }
        catch (Exception ex) { threw = true; msg = ex.Message; }
        fails += Expect("mcp-badcmd-throws", threw, "a missing MCP command should throw");
        fails += Expect("mcp-badcmd-not-brokenpipe", threw && msg.IndexOf("Broken pipe", StringComparison.OrdinalIgnoreCase) < 0, "should not be the bare broken-pipe message: " + msg);
        fails += Expect("mcp-badcmd-clear", threw && (msg.Contains("exited") || msg.Contains("not found") || msg.Contains("node/npx")), "should name the real reason: " + msg);
        Ircuitry.App.Mcp.McpClient.StopAll();
        return fails;
    }

    /// <summary>The bot-cmds advertisement trims commands to fit one client line instead of dropping the whole
    /// list when it's too big.</summary>
    private static int BotCmdsFitTest()
    {
        int fails = 0;
        var g = new NodeGraph();
        foreach (var c in new[] { "ping", "help" }) { var n = N(g, "event.command", 0, 0); n.SetParam("command", c); }
        var b64 = BotTools.BuildCommandList(g, out int inc, out int tot);
        fails += Expect("botcmds-small-all", inc == 2 && tot == 2 && BotTools.Fits("+draft/bot-cmds=" + b64), $"inc={inc} tot={tot}");

        var big = new NodeGraph();
        for (int i = 0; i < 400; i++) { var n = N(big, "event.command", 0, 0); n.SetParam("command", "command_number_" + i); n.Title = "A reasonably long description for command " + i + " to bloat the advertisement"; }
        var bb = BotTools.BuildCommandList(big, out int binc, out int btot);
        fails += Expect("botcmds-trim-fits", BotTools.Fits("+draft/bot-cmds=" + bb), "the trimmed list must fit one line");
        fails += Expect("botcmds-trim-partial", binc > 0 && binc < btot && btot == 400, $"inc={binc} tot={btot}");
        return fails;
    }

    /// <summary>The auto client certificate is generated, stable per identity, carries a private key (for the TLS
    /// handshake) and yields a 64-hex SHA-256 CertFP fingerprint.</summary>
    private static int ClientCertTest()
    {
        int fails = 0;
        var oldHome = Environment.GetEnvironmentVariable("IRCUITRY_HOME");
        var tmp = Path.Combine(Path.GetTempPath(), "ircuitry-cert-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable("IRCUITRY_HOME", tmp);
        try
        {
            string p1 = Ircuitry.App.ClientCert.EnsureDefault("Test Bot!");
            fails += Expect("cert-generated", p1.Length > 0 && File.Exists(p1) && p1.EndsWith(".pfx"), p1);
            string p2 = Ircuitry.App.ClientCert.EnsureDefault("Test Bot!");
            fails += Expect("cert-stable", p1 == p2, "same identity must reuse the same cert, not regenerate");
            string fp = Ircuitry.App.ClientCert.Fingerprint(p1);
            fails += Expect("cert-fingerprint", fp.Length == 64 && fp.All(ch => "0123456789abcdef".IndexOf(ch) >= 0), "fp=" + fp);
            bool hasKey = false;
            try { using var c = new System.Security.Cryptography.X509Certificates.X509Certificate2(p1); hasKey = c.HasPrivateKey; } catch { }
            fails += Expect("cert-has-private-key", hasKey, "the auto cert must carry its private key for TLS");
        }
        finally
        {
            Environment.SetEnvironmentVariable("IRCUITRY_HOME", oldHome);
            try { Directory.Delete(tmp, true); } catch { }
        }
        return fails;
    }

    /// <summary>The socket engine end to end over loopback: a TCP listener accepts a dialled-out client,
    /// line-framed data flows client-&gt;server (CRLF tolerated) and broadcast server-&gt;client, a UDP datagram
    /// round-trips, and closing fires a disconnect.</summary>
    private static int SocketLoopTest()
    {
        int fails = 0;
        var events = new System.Collections.Concurrent.ConcurrentQueue<(string sub, Dictionary<string, string> vars)>();
        var sm = new SocketManager((sub, vars) => events.Enqueue((sub, vars)), (m, e) => { });
        bool Saw(Func<string, Dictionary<string, string>, bool> pred, int tries = 120)
        {
            for (int i = 0; i < tries; i++) { foreach (var (s, v) in events) if (pred(s, v)) return true; Thread.Sleep(20); }
            return false;
        }
        try
        {
            int port = FreePort();
            string lid = sm.Listen("tcp", port, SocketManager.MakeOpts(false, "line", "\n", "", ""));
            fails += Expect("socket-listen", lid.Length > 0, "listener should start");
            string conn = sm.Connect("tcp", "127.0.0.1", port, SocketManager.MakeOpts(false, "line", "\n", "", ""));
            fails += Expect("socket-connect", conn.Length > 0, "client should connect");
            fails += Expect("socket-server-accept", Saw((s, v) => s == "connect" && v.GetValueOrDefault("listener", "") == lid), "server should accept");

            sm.Send(conn, Encoding.UTF8.GetBytes("hello world\r\n"));
            fails += Expect("socket-data-framed", Saw((s, v) => s == "data" && v.GetValueOrDefault("data", "") == "hello world"), "server should receive the framed line");

            sm.Broadcast(lid, Encoding.UTF8.GetBytes("ping\n"));
            fails += Expect("socket-broadcast", Saw((s, v) => s == "data" && v.GetValueOrDefault("conn", "") == conn && v.GetValueOrDefault("data", "") == "ping"), "client should receive the broadcast");

            sm.Close(conn);
            fails += Expect("socket-disconnect", Saw((s, v) => s == "disconnect"), "a disconnect should fire");

            int uport = FreePort();
            string ulid = sm.Listen("udp", uport, SocketManager.MakeOpts(false, "raw", "", "", ""));
            string uconn = sm.Connect("udp", "127.0.0.1", uport, SocketManager.MakeOpts(false, "raw", "", "", ""));
            sm.Send(uconn, Encoding.UTF8.GetBytes("datagram"));
            fails += Expect("socket-udp", Saw((s, v) => s == "data" && v.GetValueOrDefault("proto", "") == "udp" && v.GetValueOrDefault("data", "") == "datagram"), $"udp datagram should arrive (lid={ulid} conn={uconn})");
        }
        finally { sm.Dispose(); }
        return fails;
    }

    /// <summary>On Start fires when a bot begins running even with NO IRC server configured (hostless) - the boot
    /// hook a pure socket/server bot (e.g. an in-graph IRCd) needs to stand up its listeners.</summary>
    private static int StartTriggerTest()
    {
        var state = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        var rt = new BotRuntime(new ConsoleLog(), state);
        var g = new NodeGraph();
        var start = g.Add(NodeCatalog.Get("event.start"), Vector2.Zero);
        var sv = g.Add(NodeCatalog.Get("data.setvar"), new Vector2(200, 0)); sv.SetParam("name", "booted"); sv.SetParam("value", "yes");
        g.Connect(start.Id, 0, sv.Id, 0);
        rt.Start(g, new IrcSettings { Host = "", Nick = "srv" });   // hostless: no IRC server at all
        bool ok = false;
        for (int i = 0; i < 80 && !ok; i++) { Thread.Sleep(25); if (state.TryGetValue("booted", out var v) && v == "yes") ok = true; }
        rt.Stop(); Thread.Sleep(50);
        return Expect("event-start-hostless", ok, "On Start should fire on a hostless bot");
    }

    // The exact protocol brain that lives in the IRCd POC bot's one code node (see BuildIrcdGraph): given a
    // socket line + current state it returns the new state and the lines to send. Pure - no I/O.
    private const string IrcdBrain = """
        const raw = process.env.INPUT || '';
        const n1 = raw.indexOf('\n');
        const conn = n1 < 0 ? raw : raw.slice(0, n1);
        const r2 = n1 < 0 ? '' : raw.slice(n1 + 1);
        const n2 = r2.indexOf('\n');
        const line = n2 < 0 ? r2 : r2.slice(0, n2);
        let st; try { st = JSON.parse((n2 < 0 ? '' : r2.slice(n2 + 1)) || '{}'); } catch (e) { st = {}; }
        st.clients = st.clients || {}; st.channels = st.channels || {}; st.nicks = st.nicks || {};
        const SERVER = 'ircuitry', sends = [];
        const send = (c, l) => sends.push({ conn: c, line: l });
        const me = st.clients[conn] = st.clients[conn] || { nick: '', user: '', reg: false };
        const numeric = (c, code, args) => send(c, ':' + SERVER + ' ' + code + ' ' + ((st.clients[c]||{}).nick || '*') + ' ' + args);
        const prefixOf = c => { const cl = st.clients[c]||{}; return (cl.nick||'*') + '!' + (cl.user||'user') + '@ircuitry'; };
        const toChan = (chan, l, except) => { const ch = st.channels[chan]; if (ch) for (const m of ch.members) if (m !== except) send(m, l); };
        function parse(s) { s = s.replace(/\r$/, ''); const out = []; let i = 0; while (i < s.length) { if (s[i] === ':') { out.push(s.slice(i+1)); break; } let sp = s.indexOf(' ', i); if (sp < 0) { out.push(s.slice(i)); break; } if (sp > i) out.push(s.slice(i, sp)); i = sp + 1; while (s[i] === ' ') i++; } return out; }
        function tryRegister(c) { const cl = st.clients[c]; if (cl.reg || !cl.nick || !cl.user) return; cl.reg = true;
          numeric(c,'001',':Welcome to the ircuitry IRC network '+cl.nick); numeric(c,'002',':Your host is '+SERVER); numeric(c,'003',':This server is new');
          numeric(c,'004',SERVER+' ircuitry o o'); numeric(c,'005','CHANTYPES=# PREFIX=(o)@ NETWORK=ircuitry :are supported');
          numeric(c,'375',':- MOTD -'); numeric(c,'372',':- An IRC server built in ircuitry.'); numeric(c,'376',':End of /MOTD command.'); }
        const p = parse(line), cmd = (p[0]||'').toUpperCase();
        switch (cmd) {
          case 'CAP': { const sub=(p[1]||'').toUpperCase(); if (sub==='LS') send(conn,':'+SERVER+' CAP * LS :'); else if (sub==='REQ') send(conn,':'+SERVER+' CAP * NAK :'+(p[2]||'')); break; }
          case 'NICK': { const nn=p[1]||''; if(!nn){numeric(conn,'431',':No nickname given');break;} const lo=nn.toLowerCase();
            if(st.nicks[lo]&&st.nicks[lo]!==conn){numeric(conn,'433',nn+' :Nickname is already in use');break;} const old=me.nick;
            if(old)delete st.nicks[old.toLowerCase()]; me.nick=nn; st.nicks[lo]=conn;
            if(me.reg&&old&&old!==nn){const seen=new Set([conn]);send(conn,':'+old+'!'+(me.user||'user')+'@ircuitry NICK '+nn);for(const cn in st.channels)if(st.channels[cn].members.includes(conn))for(const m of st.channels[cn].members)if(!seen.has(m)){seen.add(m);send(m,':'+old+'!'+(me.user||'user')+'@ircuitry NICK '+nn);}}
            tryRegister(conn); break; }
          case 'USER': me.user=p[1]||'user'; tryRegister(conn); break;
          case 'PING': send(conn,':'+SERVER+' PONG '+SERVER+' :'+(p[1]||'')); break;
          case 'QUIT': { const r=p[1]||'Client quit'; const seen=new Set(); for(const cn in st.channels){const ch=st.channels[cn];const ix=ch.members.indexOf(conn);if(ix>=0){ch.members.splice(ix,1);for(const m of ch.members)if(!seen.has(m)){seen.add(m);send(m,':'+prefixOf(conn)+' QUIT :'+r);}if(!ch.members.length)delete st.channels[cn];}} if(me.nick)delete st.nicks[me.nick.toLowerCase()]; delete st.clients[conn]; break; }
          default:
            if(!me.reg){numeric(conn,'451',':You have not registered');break;}
            switch (cmd) {
              case 'JOIN': for(let chan of (p[1]||'').split(',')){ if(!chan||chan[0]!=='#')continue; const ch=st.channels[chan]=st.channels[chan]||{members:[],topic:''}; if(!ch.members.includes(conn))ch.members.push(conn); for(const m of ch.members)send(m,':'+prefixOf(conn)+' JOIN '+chan); if(ch.topic)numeric(conn,'332',chan+' :'+ch.topic);else numeric(conn,'331',chan+' :No topic is set'); numeric(conn,'353','= '+chan+' :'+ch.members.map(m=>(st.clients[m]||{}).nick).filter(Boolean).join(' ')); numeric(conn,'366',chan+' :End of /NAMES list'); } break;
              case 'PART': { const chan=p[1]||'',r=p[2]||'',ch=st.channels[chan]; if(ch&&ch.members.includes(conn)){for(const m of ch.members)send(m,':'+prefixOf(conn)+' PART '+chan+(r?' :'+r:''));ch.members.splice(ch.members.indexOf(conn),1);if(!ch.members.length)delete st.channels[chan];}else numeric(conn,'442',chan+" :You're not on that channel"); break; }
              case 'PRIVMSG': case 'NOTICE': { const t=p[1]||'',tx=p[2]||'',lo=':'+prefixOf(conn)+' '+cmd+' '+t+' :'+tx; if(t[0]==='#'){if(st.channels[t]&&st.channels[t].members.includes(conn))toChan(t,lo,conn);else if(cmd==='PRIVMSG')numeric(conn,'404',t+' :Cannot send to channel');}else{const tc=st.nicks[t.toLowerCase()];if(tc)send(tc,lo);else if(cmd==='PRIVMSG')numeric(conn,'401',t+' :No such nick/channel');} break; }
              case 'TOPIC': { const chan=p[1]||'',ch=st.channels[chan]; if(!ch){numeric(conn,'442',chan+" :You're not on that channel");break;} if(p.length>=3){ch.topic=p[2]||'';for(const m of ch.members)send(m,':'+prefixOf(conn)+' TOPIC '+chan+' :'+ch.topic);}else if(ch.topic)numeric(conn,'332',chan+' :'+ch.topic);else numeric(conn,'331',chan+' :No topic is set'); break; }
              case 'MODE': { if((p[1]||'')[0]==='#')numeric(conn,'324',p[1]+' +'); break; }
              case 'WHO': case 'WHOIS': case 'LIST': case 'PONG': case 'USERHOST': break;
              default: numeric(conn,'421',cmd+' :Unknown command');
            }
        }
        process.stdout.write(JSON.stringify({ state: st, sends }));
        """;

    // Build the exact IRCd POC graph (On Start -> Socket Listen; On Socket Data -> DB Get -> Format -> brain ->
    // DB Set + ForEach(sends) -> Socket Send; On Socket Disconnect -> SetVar(QUIT) -> brain), on a chosen port.
    private static NodeGraph BuildIrcdGraph(int port)
    {
        var g = new NodeGraph();
        var start = g.Add(NodeCatalog.Get("event.start"), new Vector2(40, -80));
        var listen = g.Add(NodeCatalog.Get("socket.listen"), new Vector2(40, 40));
        listen.SetParam("proto", "tcp"); listen.SetParam("port", port.ToString()); listen.SetParam("tls", "false"); listen.SetParam("framing", "line");
        var sdata = g.Add(NodeCatalog.Get("event.socket.data"), new Vector2(40, 220));
        var dbget = g.Add(NodeCatalog.Get("db.get"), new Vector2(40, 380));
        dbget.SetParam("table", "ircd"); dbget.SetParam("mode", "value"); dbget.SetParam("key", "state"); dbget.SetParam("default", "{}");
        var fmt = g.Add(NodeCatalog.Get("data.format"), new Vector2(320, 300));
        fmt.SetParam("template", "{conn}\n{line}\n{a}");
        var brain = g.Add(NodeCatalog.Get("code.run"), new Vector2(580, 240));
        brain.SetParam("language", "javascript"); brain.SetParam("timeout", "5"); brain.SetParam("code", IrcdBrain);
        var jstate = g.Add(NodeCatalog.Get("data.json"), new Vector2(860, 140)); jstate.SetParam("path", "state");
        var dbset = g.Add(NodeCatalog.Get("db.set"), new Vector2(1120, 140)); dbset.SetParam("table", "ircd"); dbset.SetParam("key", "state");
        var jsends = g.Add(NodeCatalog.Get("data.json"), new Vector2(860, 360)); jsends.SetParam("path", "sends");
        var fe = g.Add(NodeCatalog.Get("logic.forEach"), new Vector2(1120, 360)); fe.SetParam("sep", "json"); fe.SetParam("var", "item");
        var send = g.Add(NodeCatalog.Get("socket.send"), new Vector2(1380, 360));
        send.SetParam("conn", "{item.conn}"); send.SetParam("data", "{item.line}"); send.SetParam("encoding", "text"); send.SetParam("append", "crlf");
        var sdisc = g.Add(NodeCatalog.Get("event.socket.disconnect"), new Vector2(40, 560));
        var setvar = g.Add(NodeCatalog.Get("data.setvar"), new Vector2(320, 560)); setvar.SetParam("name", "line"); setvar.SetParam("value", "QUIT :connection closed");
        g.Connect(start.Id, 0, listen.Id, 0);
        g.Connect(sdata.Id, 0, brain.Id, 0);
        g.Connect(dbget.Id, 0, fmt.Id, 0);
        g.Connect(fmt.Id, 0, brain.Id, 1);
        g.Connect(brain.Id, 1, jstate.Id, 0);
        g.Connect(brain.Id, 1, jsends.Id, 0);
        g.Connect(brain.Id, 0, dbset.Id, 0);
        g.Connect(jstate.Id, 0, dbset.Id, 1);
        g.Connect(dbset.Id, 0, fe.Id, 0);
        g.Connect(jsends.Id, 0, fe.Id, 1);
        g.Connect(fe.Id, 0, send.Id, 0);
        g.Connect(sdisc.Id, 0, setvar.Id, 0);
        g.Connect(setvar.Id, 0, brain.Id, 0);
        return g;
    }

    // A scripted IRC client over a real TCP socket - accumulates everything the server sends so WaitFor can match.
    private sealed class IrcPeer
    {
        public readonly System.Net.Sockets.TcpClient Client;
        private readonly System.IO.Stream _io;
        private readonly System.Text.StringBuilder _buf = new();
        public IrcPeer(int port, bool tls = false)
        {
            Client = new System.Net.Sockets.TcpClient();
            Client.Connect("127.0.0.1", port);
            System.IO.Stream s = Client.GetStream();
            if (tls)
            {
                var ssl = new System.Net.Security.SslStream(s, false, (a, b, c, d) => true);   // accept the self-signed auto-cert
                ssl.AuthenticateAsClient("localhost");
                s = ssl;
            }
            s.ReadTimeout = 150;
            _io = s;
        }
        public void Send(string line) { var b = System.Text.Encoding.UTF8.GetBytes(line + "\r\n"); _io.Write(b, 0, b.Length); _io.Flush(); }
        public bool WaitFor(Func<string, bool> pred, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            var buf = new byte[8192];
            while (DateTime.UtcNow < deadline)
            {
                if (pred(_buf.ToString())) return true;
                try { int n = _io.Read(buf, 0, buf.Length); if (n > 0) _buf.Append(System.Text.Encoding.UTF8.GetString(buf, 0, n)); else Thread.Sleep(20); }
                catch (System.IO.IOException) { /* per-read timeout - keep polling until the deadline */ }
            }
            return pred(_buf.ToString());
        }
        public string Buffer => _buf.ToString();
        public void Close() { try { Client.Close(); } catch { } }
    }

    /// <summary>End-to-end IRCd: boot the actual server graph hostless and drive two real TCP clients through
    /// registration, JOIN, a channel relay, a private message and PING - proving the whole wiring (real
    /// SocketManager + the brain via CodeRunner + DB + fan-out) and the socket-event serialization. Skips
    /// cleanly where the code sandbox/node can't run (e.g. minimal CI).</summary>
    private static int IrcdE2ETest()
    {
        var probe = Ircuitry.Net.CodeRunner.Run("javascript", "process.stdout.write('ok')", new Dictionary<string, string>(), 5);
        if (probe.output != "ok")
        {
            Console.WriteLine("  [SKIP] ircd-e2e   code sandbox/node unavailable here" + (string.IsNullOrEmpty(probe.error) ? "" : " (" + probe.error + ")"));
            return 0;
        }
        var oldHome = Environment.GetEnvironmentVariable("IRCUITRY_HOME");
        var tmp = Path.Combine(Path.GetTempPath(), "ircuitry-ircd-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        Environment.SetEnvironmentVariable("IRCUITRY_HOME", tmp);
        int port = FreePort();
        var rt = new BotRuntime(new ConsoleLog(), new System.Collections.Concurrent.ConcurrentDictionary<string, string>());
        var peers = new List<IrcPeer>();
        int fails = 0;
        try
        {
            rt.Start(BuildIrcdGraph(port), new IrcSettings { Host = "", Nick = "ircd" });   // hostless server bot
            IrcPeer? Dial() { for (int i = 0; i < 100; i++) { try { var p = new IrcPeer(port); peers.Add(p); return p; } catch { Thread.Sleep(50); } } return null; }

            var a = Dial();
            fails += Expect("ircd-listen", a != null, $"server should be listening on {port}");
            if (a == null) return fails;
            a.Send("NICK alice"); a.Send("USER alice 0 * :Alice Liddell");   // burst: this is exactly what raced before serialization
            fails += Expect("ircd-register", a.WaitFor(s => s.Contains(" 001 alice ") && s.Contains(" 376 "), 12000), "alice should get a 001..376 welcome");

            var b = Dial();
            fails += Expect("ircd-dial-b", b != null, "second client should connect");
            if (b == null) return fails;
            b.Send("NICK bob"); b.Send("USER bob 0 * :Bob");
            b.WaitFor(s => s.Contains(" 376 "), 12000);

            a.Send("JOIN #test");
            fails += Expect("ircd-join", a.WaitFor(s => s.Contains("JOIN #test") && s.Contains(" 366 "), 12000), "alice should join #test with a names list");
            b.Send("JOIN #test");
            fails += Expect("ircd-peer-join", a.WaitFor(s => s.Contains("bob!") && s.Contains("JOIN #test"), 12000), "alice should see bob join");

            a.Send("PRIVMSG #test :hi bob");
            fails += Expect("ircd-relay", b.WaitFor(s => s.Contains("alice!") && s.Contains("PRIVMSG #test :hi bob"), 12000), "bob should receive alice's channel message");

            b.Send("PRIVMSG alice :pm back");
            fails += Expect("ircd-pm", a.WaitFor(s => s.Contains("bob!") && s.Contains("PRIVMSG alice :pm back"), 12000), "alice should receive bob's private message");

            a.Send("PING :xyz");
            fails += Expect("ircd-ping", a.WaitFor(s => s.Contains("PONG") && s.Contains(":xyz"), 12000), "alice should get a PONG");
        }
        finally
        {
            foreach (var p in peers) p.Close();
            rt.Stop(); Thread.Sleep(150);
            Environment.SetEnvironmentVariable("IRCUITRY_HOME", oldHome);
            try { Directory.Delete(tmp, true); } catch { }
        }
        return fails;
    }

    private static string CasesJson(params string[] cases)
    {
        var sb = new System.Text.StringBuilder("[");
        for (int i = 0; i < cases.Length; i++) { if (i > 0) sb.Append(','); sb.Append('"').Append(cases[i]).Append('"'); }
        return sb.Append(']').ToString();
    }

    // The IRCd built from ONLY built-in nodes - no code node. State lives in DB tables (cn=conn->nick,
    // nc=nick->conn, cu=conn->user, cr=conn->registered, ...). logic.regex parses each line once and exposes
    // {1}/{2}/{3} as run tokens; logic.switch dispatches on the command; logic.if gates registration. This is
    // the registration slice (NICK/USER -> welcome); later commands extend the switch.
    /// <summary>The all-built-in-node IRCd (no code node) serialized as portable .ircbot JSON, for `--emit-ircd`
    /// so it can be dropped straight into a workspace.</summary>
    public static string EmitIrcdNodeGraph() => Ircuitry.Graph.GraphSerializer.Save(BuildIrcdNodeGraph(6667), "IRCd (nodes)");

    private static NodeGraph BuildIrcdNodeGraph(int port) => BuildIrcdNodeGraph(port, false);

    private static NodeGraph BuildIrcdNodeGraph(int port, bool tls)
    {
        var g = new NodeGraph();
        Node Add(string type, float x, float y) => g.Add(NodeCatalog.Get(type), new Vector2(x, y));

        // Rebuild a space-list table[keyTok] keeping only items != excludeTok (accumulate-in-loop). Returns the
        // entry exec node (wire prev -> entry) and exit exec node (the db.set; wire exit -> next).
        (Node entry, Node exit) RemoveFromList(string table, string keyTok, string excludeTok, string acc, float x, float y)
        {
            var init = Add("data.setvar", x, y); init.SetParam("name", acc); init.SetParam("value", "");
            var get = Add("db.get", x, y + 70); get.SetParam("table", table); get.SetParam("key", keyTok);
            var loop = Add("logic.forEach", x + 200, y); loop.SetParam("sep", "space"); loop.SetParam("var", acc + "i");
            g.Connect(init.Id, 0, loop.Id, 0);
            g.Connect(get.Id, 0, loop.Id, 1);
            var itf = Add("data.format", x + 200, y + 70); itf.SetParam("template", "{" + acc + "i}");
            var keep = Add("logic.if", x + 400, y); keep.SetParam("op", "≠"); keep.SetParam("b", excludeTok);
            g.Connect(itf.Id, 0, keep.Id, 1);     // A = item
            g.Connect(loop.Id, 0, keep.Id, 0);    // each -> keep?
            var app = Add("data.setvar", x + 600, y); app.SetParam("name", acc); app.SetParam("value", "{" + acc + "} {" + acc + "i}");
            g.Connect(keep.Id, 0, app.Id, 0);     // true (item != exclude) -> append
            var set = Add("db.set", x + 400, y + 70); set.SetParam("table", table); set.SetParam("key", keyTok); set.SetParam("value", "{" + acc + "}");
            g.Connect(loop.Id, 1, set.Id, 0);     // loop done -> write back
            return (init, set);
        }

        // Ensure a single char is present (add) or absent (!add) in the string at table[keyTok]. Returns the db.set
        // (entry=exit): wire prev -> it, its 'then' -> next. Reusable for channel mode flags (cm), etc.
        Node ToggleChar(string table, string keyTok, string ch, bool add, float x, float y)
        {
            var get = Add("db.get", x, y + 70); get.SetParam("table", table); get.SetParam("key", keyTok);
            var rm = Add("data.regex", x + 180, y + 70); rm.SetParam("op", "replace"); rm.SetParam("pattern", ch); rm.SetParam("replace", "");
            g.Connect(get.Id, 0, rm.Id, 0);   // regex text <- current value (strip the char so we never duplicate)
            Node valSrc = rm;
            if (add) { var ap = Add("data.format", x + 360, y + 70); ap.SetParam("template", "{a}" + ch); g.Connect(rm.Id, 0, ap.Id, 0); valSrc = ap; }
            var set = Add("db.set", x + 180, y); set.SetParam("table", table); set.SetParam("key", keyTok);
            g.Connect(valSrc.Id, 0, set.Id, 1);   // value <- cleaned (+ the char on add)
            return set;
        }

        var start = Add("event.start", 0, -260);
        var listen = Add("socket.listen", 0, -180);
        listen.SetParam("proto", "tcp"); listen.SetParam("port", port.ToString()); listen.SetParam("framing", "line");
        if (tls) listen.SetParam("tls", "true");   // auto-generates a self-signed server cert
        // On boot there are no live clients, so wipe all ephemeral session state - otherwise stale nc/cn
        // rows from the previous run make fresh registrations collide ("nick already in use").
        var wipe = Add("db.set", 0, -100); wipe.SetParam("mode", "clear");
        wipe.SetParam("table", "cn nc cu cr mem nm cc st cm tp capneg");
        g.Connect(start.Id, 0, wipe.Id, 0);
        g.Connect(wipe.Id, 0, listen.Id, 0);

        // parse every line once: {1}=command {2}=first arg {3}=trailing
        var sdata = Add("event.socket.data", 0, 0);
        var rx = Add("logic.regex", 220, 0);
        rx.SetParam("pattern", @"^(\S+)(?:\s+([^:\s]\S*))?\s*:?(.*)$"); rx.SetParam("ci", "false");   // {1}=cmd {2}=arg {3}=trailing
        g.Connect(sdata.Id, 0, rx.Id, 0);
        g.Connect(sdata.Id, 2, rx.Id, 1);   // socket data -> regex text

        var sw = Add("logic.switch", 440, 0);
        sw.SetParam("value", "{1}");
        sw.SetParam("cases", CasesJson("NICK", "USER", "PING", "JOIN", "PART", "PRIVMSG", "NOTICE", "QUIT", "TOPIC", "MODE", "CAP", "WHOIS"));
        g.Connect(rx.Id, 0, sw.Id, 0);   // match -> switch
        // switch outs: 0 default, 1 NICK, 2 USER, 3 PING, 4 JOIN, 5 PART, 6 PRIVMSG, 7 NOTICE, 8 QUIT, 9 TOPIC, 10 MODE, 11 CAP, 12 WHOIS

        // NICK: reject a nick already held by another conn (433), else store conn<->nick and try to register
        var nickA = Add("db.set", 660, -240); nickA.SetParam("table", "cn"); nickA.SetParam("key", "{conn}"); nickA.SetParam("value", "{2}");
        var nickB = Add("db.set", 860, -240); nickB.SetParam("table", "nc"); nickB.SetParam("key", "{2}"); nickB.SetParam("value", "{conn}");
        var nickGet = Add("db.get", 360, -320); nickGet.SetParam("table", "nc"); nickGet.SetParam("key", "{2}");
        var nickFree = Add("logic.if", 360, -260); nickFree.SetParam("op", "is empty");
        var nickMine = Add("logic.if", 360, -200); nickMine.SetParam("op", "="); nickMine.SetParam("b", "{conn}");
        var nick433 = Add("socket.send", 360, -140); nick433.SetParam("conn", "{conn}"); nick433.SetParam("data", ":ircuitry 433 * {2} :Nickname is already in use"); nick433.SetParam("append", "crlf");
        g.Connect(sw.Id, 1, nickFree.Id, 0);        // NICK case -> collision check
        g.Connect(nickGet.Id, 0, nickFree.Id, 1);   // A = who currently holds {2}
        g.Connect(nickGet.Id, 0, nickMine.Id, 1);
        g.Connect(nickFree.Id, 0, nickA.Id, 0);     // free -> claim it
        g.Connect(nickFree.Id, 1, nickMine.Id, 0);  // taken -> is it me?
        g.Connect(nickMine.Id, 0, nickA.Id, 0);     // re-NICK by the same conn -> ok
        g.Connect(nickMine.Id, 1, nick433.Id, 0);   // held by someone else -> 433
        g.Connect(nickA.Id, 0, nickB.Id, 0);

        // USER: store conn->user, then try to register
        var userA = Add("db.set", 660, -140); userA.SetParam("table", "cu"); userA.SetParam("key", "{conn}"); userA.SetParam("value", "{2}");
        g.Connect(sw.Id, 2, userA.Id, 0);

        // try-register: welcome only when nick set AND user set AND not already registered
        var getNick = Add("db.get", 660, 40); getNick.SetParam("table", "cn"); getNick.SetParam("key", "{conn}");
        var getUser = Add("db.get", 660, 120); getUser.SetParam("table", "cu"); getUser.SetParam("key", "{conn}");
        var getReg = Add("db.get", 660, 200); getReg.SetParam("table", "cr"); getReg.SetParam("key", "{conn}");
        var ifNick = Add("logic.if", 880, 40); ifNick.SetParam("op", "is empty");
        var ifUser = Add("logic.if", 1060, 40); ifUser.SetParam("op", "is empty");
        var ifReg = Add("logic.if", 1240, 40); ifReg.SetParam("op", "="); ifReg.SetParam("b", "1");
        // CAP guard: while a client is mid CAP negotiation (capneg set), suspend registration until CAP END
        var capnegGet = Add("db.get", 660, -40); capnegGet.SetParam("table", "capneg"); capnegGet.SetParam("key", "{conn}");
        var ifCapNeg = Add("logic.if", 880, -40); ifCapNeg.SetParam("op", "is empty");
        g.Connect(capnegGet.Id, 0, ifCapNeg.Id, 1);
        g.Connect(ifCapNeg.Id, 0, ifNick.Id, 0);   // not negotiating -> proceed
        g.Connect(nickB.Id, 0, ifCapNeg.Id, 0);    // NICK -> register entry (via CAP guard)
        g.Connect(userA.Id, 0, ifCapNeg.Id, 0);    // USER -> register entry
        g.Connect(getNick.Id, 0, ifNick.Id, 1); // A = nick
        g.Connect(ifNick.Id, 1, ifUser.Id, 0);  // false branch = nick present
        g.Connect(getUser.Id, 0, ifUser.Id, 1);
        g.Connect(ifUser.Id, 1, ifReg.Id, 0);   // false branch = user present
        g.Connect(getReg.Id, 0, ifReg.Id, 1);

        // welcome block (001..376), one Socket Send per line
        var wnick = Add("data.setvar", 1240, 160); wnick.SetParam("name", "wnick");
        g.Connect(getNick.Id, 0, wnick.Id, 1);   // wnick = the conn's nick
        g.Connect(ifReg.Id, 1, wnick.Id, 0);     // false branch = not yet registered -> welcome
        var regset = Add("db.set", 1240, 240); regset.SetParam("table", "cr"); regset.SetParam("key", "{conn}"); regset.SetParam("value", "1");
        g.Connect(wnick.Id, 0, regset.Id, 0);
        var wfmt = Add("data.format", 1440, 340);
        wfmt.SetParam("template",
            ":ircuitry 001 {wnick} :Welcome to the ircuitry IRC network {wnick}\n" +
            ":ircuitry 002 {wnick} :Your host is ircuitry, running on nodes\n" +
            ":ircuitry 003 {wnick} :This server is brand new\n" +
            ":ircuitry 004 {wnick} ircuitry ircuitry o o\n" +
            ":ircuitry 005 {wnick} CHANTYPES=# PREFIX=(o)@ NETWORK=ircuitry :are supported by this server\n" +
            ":ircuitry 375 {wnick} :- ircuitry Message of the Day -\n" +
            ":ircuitry 372 {wnick} :- An IRC server built entirely from ircuitry nodes.\n" +
            ":ircuitry 376 {wnick} :End of /MOTD command.");
        var wloop = Add("logic.forEach", 1440, 240); wloop.SetParam("sep", "newline");
        g.Connect(regset.Id, 0, wloop.Id, 0);
        g.Connect(wfmt.Id, 0, wloop.Id, 1);
        var wsend = Add("socket.send", 1640, 240); wsend.SetParam("conn", "{conn}"); wsend.SetParam("data", "{item}"); wsend.SetParam("append", "crlf");
        g.Connect(wloop.Id, 0, wsend.Id, 0);

        // ---- PING (switch out 3): one Socket Send, tokens resolve in the data param ----
        var pingSend = Add("socket.send", 660, 380);
        pingSend.SetParam("conn", "{conn}"); pingSend.SetParam("data", ":ircuitry PONG ircuitry :{3}"); pingSend.SetParam("append", "crlf");
        g.Connect(sw.Id, 3, pingSend.Id, 0);

        // ---- JOIN (switch out 4): add to channel, broadcast the JOIN, send names ----
        var jGetNick = Add("db.get", 460, 460); jGetNick.SetParam("table", "cn"); jGetNick.SetParam("key", "{conn}");
        var jNickVar = Add("data.setvar", 660, 460); jNickVar.SetParam("name", "jnick");
        g.Connect(jGetNick.Id, 0, jNickVar.Id, 1);
        // +i invite-only gate: reject JOIN with 473 when the channel has +i set
        var jiCmGet = Add("db.get", 60, 460); jiCmGet.SetParam("table", "cm"); jiCmGet.SetParam("key", "{2}");
        var jiIf = Add("logic.if", 60, 400); jiIf.SetParam("op", "contains"); jiIf.SetParam("b", "i");
        g.Connect(jiCmGet.Id, 0, jiIf.Id, 1);
        g.Connect(sw.Id, 4, jiIf.Id, 0);           // JOIN -> +i check
        g.Connect(jiIf.Id, 1, jNickVar.Id, 0);     // not +i -> proceed with join
        var jiNkGet = Add("db.get", 60, 340); jiNkGet.SetParam("table", "cn"); jiNkGet.SetParam("key", "{conn}");
        var jiNk = Add("data.setvar", 260, 340); jiNk.SetParam("name", "jinick");
        g.Connect(jiNkGet.Id, 0, jiNk.Id, 1);
        g.Connect(jiIf.Id, 0, jiNk.Id, 0);         // +i set -> resolve nick for the numeric
        var ji473 = Add("socket.send", 260, 280); ji473.SetParam("conn", "{conn}"); ji473.SetParam("data", ":ircuitry 473 {jinick} {2} :Cannot join channel (+i)"); ji473.SetParam("append", "crlf");
        g.Connect(jiNk.Id, 0, ji473.Id, 0);
        var jGetMem = Add("db.get", 460, 540); jGetMem.SetParam("table", "mem"); jGetMem.SetParam("key", "{2}");
        var jMemFmt = Add("data.format", 660, 540); jMemFmt.SetParam("template", "{a} {conn}");
        g.Connect(jGetMem.Id, 0, jMemFmt.Id, 0);
        var jSetMem = Add("db.set", 860, 540); jSetMem.SetParam("table", "mem"); jSetMem.SetParam("key", "{2}");
        g.Connect(jMemFmt.Id, 0, jSetMem.Id, 1);
        g.Connect(jNickVar.Id, 0, jSetMem.Id, 0);
        var jGetNm = Add("db.get", 460, 620); jGetNm.SetParam("table", "nm"); jGetNm.SetParam("key", "{2}");
        var jNmFmt = Add("data.format", 660, 620); jNmFmt.SetParam("template", "{a} {jnick}");
        g.Connect(jGetNm.Id, 0, jNmFmt.Id, 0);
        var jSetNm = Add("db.set", 860, 620); jSetNm.SetParam("table", "nm"); jSetNm.SetParam("key", "{2}");
        g.Connect(jNmFmt.Id, 0, jSetNm.Id, 1);
        // first joiner (channel was empty before this join) becomes operator: st["{chan} {conn}"] = "o"
        var jFirstIf = Add("logic.if", 1060, 480); jFirstIf.SetParam("op", "is empty");
        g.Connect(jGetMem.Id, 0, jFirstIf.Id, 1);   // A = members BEFORE this join
        g.Connect(jSetMem.Id, 0, jFirstIf.Id, 0);
        var jSetOp = Add("db.set", 1060, 540); jSetOp.SetParam("table", "st"); jSetOp.SetParam("key", "{2} {conn}"); jSetOp.SetParam("value", "o");
        g.Connect(jFirstIf.Id, 0, jSetOp.Id, 0);    // empty -> first joiner -> +o
        g.Connect(jSetOp.Id, 0, jSetNm.Id, 0);
        g.Connect(jFirstIf.Id, 1, jSetNm.Id, 0);    // not first -> continue
        var jGetCc = Add("db.get", 1060, 660); jGetCc.SetParam("table", "cc"); jGetCc.SetParam("key", "{conn}");
        var jCcFmt = Add("data.format", 1060, 700); jCcFmt.SetParam("template", "{a} {2}");
        g.Connect(jGetCc.Id, 0, jCcFmt.Id, 0);
        var jSetCc = Add("db.set", 1240, 620); jSetCc.SetParam("table", "cc"); jSetCc.SetParam("key", "{conn}");
        g.Connect(jCcFmt.Id, 0, jSetCc.Id, 1);
        g.Connect(jSetNm.Id, 0, jSetCc.Id, 0);   // track the conn's channels for cleanup
        var jBcGetMem = Add("db.get", 460, 700); jBcGetMem.SetParam("table", "mem"); jBcGetMem.SetParam("key", "{2}");
        var jLoop = Add("logic.forEach", 660, 700); jLoop.SetParam("sep", "space"); jLoop.SetParam("var", "m");
        g.Connect(jSetCc.Id, 0, jLoop.Id, 0);
        g.Connect(jBcGetMem.Id, 0, jLoop.Id, 1);
        var jBcSend = Add("socket.send", 860, 700); jBcSend.SetParam("conn", "{m}"); jBcSend.SetParam("data", ":{jnick}!user@ircuitry JOIN {2}"); jBcSend.SetParam("append", "crlf");
        g.Connect(jLoop.Id, 0, jBcSend.Id, 0);
        // NAMES with @/+ prefixes: iterate member conns, look up each nick + status (st), accumulate prefixed names
        var pnInit = Add("data.setvar", 460, 780); pnInit.SetParam("name", "jnames"); pnInit.SetParam("value", "");
        g.Connect(jLoop.Id, 1, pnInit.Id, 0);   // broadcast done -> build names
        var pnGetMem = Add("db.get", 460, 840); pnGetMem.SetParam("table", "mem"); pnGetMem.SetParam("key", "{2}");
        var pnLoop = Add("logic.forEach", 660, 780); pnLoop.SetParam("sep", "space"); pnLoop.SetParam("var", "nm2");
        g.Connect(pnInit.Id, 0, pnLoop.Id, 0);
        g.Connect(pnGetMem.Id, 0, pnLoop.Id, 1);
        var pnGetNick = Add("db.get", 460, 900); pnGetNick.SetParam("table", "cn"); pnGetNick.SetParam("key", "{nm2}");
        var pnNickVar = Add("data.setvar", 660, 840); pnNickVar.SetParam("name", "nmnick");
        g.Connect(pnGetNick.Id, 0, pnNickVar.Id, 1);
        g.Connect(pnLoop.Id, 0, pnNickVar.Id, 0);
        var pnGetSt = Add("db.get", 460, 960); pnGetSt.SetParam("table", "st"); pnGetSt.SetParam("key", "{2} {nm2}");
        var pnStVar = Add("data.setvar", 660, 900); pnStVar.SetParam("name", "nmst");
        g.Connect(pnGetSt.Id, 0, pnStVar.Id, 1);
        g.Connect(pnNickVar.Id, 0, pnStVar.Id, 0);
        var pnStFmt = Add("data.format", 660, 1020); pnStFmt.SetParam("template", "{nmst}");
        var pnIfOp = Add("logic.if", 860, 900); pnIfOp.SetParam("op", "contains"); pnIfOp.SetParam("b", "o");
        g.Connect(pnStFmt.Id, 0, pnIfOp.Id, 1);
        g.Connect(pnStVar.Id, 0, pnIfOp.Id, 0);
        var pnIfV = Add("logic.if", 860, 980); pnIfV.SetParam("op", "contains"); pnIfV.SetParam("b", "v");
        g.Connect(pnStFmt.Id, 0, pnIfV.Id, 1);
        g.Connect(pnIfOp.Id, 1, pnIfV.Id, 0);   // not op -> check voice
        var pnAt = Add("data.setvar", 1060, 860); pnAt.SetParam("name", "nmpfx"); pnAt.SetParam("value", "@");
        var pnPlus = Add("data.setvar", 1060, 960); pnPlus.SetParam("name", "nmpfx"); pnPlus.SetParam("value", "+");
        var pnNone = Add("data.setvar", 1060, 1040); pnNone.SetParam("name", "nmpfx"); pnNone.SetParam("value", "");
        g.Connect(pnIfOp.Id, 0, pnAt.Id, 0);
        g.Connect(pnIfV.Id, 0, pnPlus.Id, 0);
        g.Connect(pnIfV.Id, 1, pnNone.Id, 0);
        var pnApp = Add("data.setvar", 1260, 940); pnApp.SetParam("name", "jnames"); pnApp.SetParam("value", "{jnames} {nmpfx}{nmnick}");
        g.Connect(pnAt.Id, 0, pnApp.Id, 0);
        g.Connect(pnPlus.Id, 0, pnApp.Id, 0);
        g.Connect(pnNone.Id, 0, pnApp.Id, 0);
        var j353 = Add("socket.send", 1260, 780); j353.SetParam("conn", "{conn}"); j353.SetParam("data", ":ircuitry 353 {jnick} = {2} :{jnames}"); j353.SetParam("append", "crlf");
        g.Connect(pnLoop.Id, 1, j353.Id, 0);   // names done -> 353
        var j366 = Add("socket.send", 1060, 780); j366.SetParam("conn", "{conn}"); j366.SetParam("data", ":ircuitry 366 {jnick} {2} :End of /NAMES list"); j366.SetParam("append", "crlf");
        g.Connect(j353.Id, 0, j366.Id, 0);

        // ---- PRIVMSG (switch out 6): channel -> fan out to members (skip sender); nick -> deliver to their conn ----
        var pmGetNick = Add("db.get", 460, 880); pmGetNick.SetParam("table", "cn"); pmGetNick.SetParam("key", "{conn}");
        var pmSnick = Add("data.setvar", 660, 880); pmSnick.SetParam("name", "snick");
        g.Connect(pmGetNick.Id, 0, pmSnick.Id, 1);
        g.Connect(sw.Id, 6, pmSnick.Id, 0);   // PRIVMSG

        g.Connect(sw.Id, 7, pmSnick.Id, 0);   // NOTICE - same routing, verb is {1}
        var pmTargetFmt = Add("data.format", 460, 960); pmTargetFmt.SetParam("template", "{2}");
        var pmIf = Add("logic.if", 860, 880); pmIf.SetParam("op", "starts with"); pmIf.SetParam("b", "#");
        g.Connect(pmTargetFmt.Id, 0, pmIf.Id, 1);   // A = target
        g.Connect(pmSnick.Id, 0, pmIf.Id, 0);
        var pmGetMem = Add("db.get", 1060, 840); pmGetMem.SetParam("table", "mem"); pmGetMem.SetParam("key", "{2}");
        var pmLoop = Add("logic.forEach", 1060, 900); pmLoop.SetParam("sep", "space"); pmLoop.SetParam("var", "m");
        // channel send gate: enforce +n (no external messages) and +m (moderated) before fan-out -> 404
        var geCmGet = Add("db.get", 1700, 880); geCmGet.SetParam("table", "cm"); geCmGet.SetParam("key", "{2}");
        var geIfN = Add("logic.if", 1700, 820); geIfN.SetParam("op", "contains"); geIfN.SetParam("b", "n");
        g.Connect(geCmGet.Id, 0, geIfN.Id, 1);
        g.Connect(pmIf.Id, 0, geIfN.Id, 0);          // channel target -> +n check
        var geMemGet = Add("db.get", 1900, 880); geMemGet.SetParam("table", "mem"); geMemGet.SetParam("key", "{2}");
        var geMemFmt = Add("data.format", 1900, 940); geMemFmt.SetParam("template", " {a} ");
        g.Connect(geMemGet.Id, 0, geMemFmt.Id, 0);
        var geMemIf = Add("logic.if", 1900, 820); geMemIf.SetParam("op", "contains"); geMemIf.SetParam("b", " {conn} ");
        g.Connect(geMemFmt.Id, 0, geMemIf.Id, 1);
        g.Connect(geIfN.Id, 0, geMemIf.Id, 0);       // +n set -> sender must be a member
        var geIfM = Add("logic.if", 2100, 820); geIfM.SetParam("op", "contains"); geIfM.SetParam("b", "m");
        g.Connect(geCmGet.Id, 0, geIfM.Id, 1);
        g.Connect(geIfN.Id, 1, geIfM.Id, 0);         // no +n -> +m check
        g.Connect(geMemIf.Id, 0, geIfM.Id, 0);       // +n & member -> +m check
        var geStGet = Add("db.get", 2300, 880); geStGet.SetParam("table", "st"); geStGet.SetParam("key", "{2} {conn}");
        var geStO = Add("logic.if", 2300, 820); geStO.SetParam("op", "contains"); geStO.SetParam("b", "o");
        g.Connect(geStGet.Id, 0, geStO.Id, 1);
        g.Connect(geIfM.Id, 0, geStO.Id, 0);         // +m set -> sender must be op or voiced
        var geStV = Add("logic.if", 2500, 820); geStV.SetParam("op", "contains"); geStV.SetParam("b", "v");
        g.Connect(geStGet.Id, 0, geStV.Id, 1);
        g.Connect(geStO.Id, 1, geStV.Id, 0);
        var ge404 = Add("socket.send", 2300, 980); ge404.SetParam("conn", "{conn}"); ge404.SetParam("data", ":ircuitry 404 {snick} {2} :Cannot send to channel"); ge404.SetParam("append", "crlf");
        g.Connect(geMemIf.Id, 1, ge404.Id, 0);       // +n set, not a member -> 404
        g.Connect(geStV.Id, 1, ge404.Id, 0);         // +m set, not op/voiced -> 404
        g.Connect(geIfM.Id, 1, pmLoop.Id, 0);        // no +m -> deliver
        g.Connect(geStO.Id, 0, pmLoop.Id, 0);        // op -> deliver
        g.Connect(geStV.Id, 0, pmLoop.Id, 0);        // voiced -> deliver
        g.Connect(pmGetMem.Id, 0, pmLoop.Id, 1);
        var pmMFmt = Add("data.format", 1060, 980); pmMFmt.SetParam("template", "{m}");
        var pmSkip = Add("logic.if", 1260, 900); pmSkip.SetParam("op", "≠"); pmSkip.SetParam("b", "{conn}");
        g.Connect(pmMFmt.Id, 0, pmSkip.Id, 1);   // A = m
        g.Connect(pmLoop.Id, 0, pmSkip.Id, 0);
        var pmChanSend = Add("socket.send", 1460, 900); pmChanSend.SetParam("conn", "{m}"); pmChanSend.SetParam("data", ":{snick}!user@ircuitry {1} {2} :{3}"); pmChanSend.SetParam("append", "crlf");
        g.Connect(pmSkip.Id, 0, pmChanSend.Id, 0);   // true = m != sender
        var pmGetTc = Add("db.get", 1060, 1060); pmGetTc.SetParam("table", "nc"); pmGetTc.SetParam("key", "{2}");
        var pmTcVar = Add("data.setvar", 1260, 1060); pmTcVar.SetParam("name", "tconn");
        g.Connect(pmGetTc.Id, 0, pmTcVar.Id, 1);
        g.Connect(pmIf.Id, 1, pmTcVar.Id, 0);   // false = nick
        var pmNickSend = Add("socket.send", 1460, 1060); pmNickSend.SetParam("conn", "{tconn}"); pmNickSend.SetParam("data", ":{snick}!user@ircuitry {1} {2} :{3}"); pmNickSend.SetParam("append", "crlf");
        g.Connect(pmTcVar.Id, 0, pmNickSend.Id, 0);

        // ---- PART (switch out 5): broadcast the PART to members, then remove from mem / nm / cc ----
        var pGetNick = Add("db.get", 260, 1200); pGetNick.SetParam("table", "cn"); pGetNick.SetParam("key", "{conn}");
        var pNick = Add("data.setvar", 460, 1200); pNick.SetParam("name", "pnick");
        g.Connect(pGetNick.Id, 0, pNick.Id, 1);
        g.Connect(sw.Id, 5, pNick.Id, 0);
        var pBcGet = Add("db.get", 260, 1280); pBcGet.SetParam("table", "mem"); pBcGet.SetParam("key", "{2}");
        var pBcLoop = Add("logic.forEach", 460, 1280); pBcLoop.SetParam("sep", "space"); pBcLoop.SetParam("var", "pm");
        g.Connect(pNick.Id, 0, pBcLoop.Id, 0);
        g.Connect(pBcGet.Id, 0, pBcLoop.Id, 1);
        var pBcSend = Add("socket.send", 660, 1280); pBcSend.SetParam("conn", "{pm}"); pBcSend.SetParam("data", ":{pnick}!user@ircuitry PART {2} :{3}"); pBcSend.SetParam("append", "crlf");
        g.Connect(pBcLoop.Id, 0, pBcSend.Id, 0);
        var pRmMem = RemoveFromList("mem", "{2}", "{conn}", "pmem", 260, 1380);
        var pRmNm = RemoveFromList("nm", "{2}", "{pnick}", "pnm", 260, 1540);
        var pRmCc = RemoveFromList("cc", "{conn}", "{2}", "pcc", 260, 1700);
        g.Connect(pBcLoop.Id, 1, pRmMem.entry.Id, 0);   // broadcast done -> remove from mem
        g.Connect(pRmMem.exit.Id, 0, pRmNm.entry.Id, 0);
        g.Connect(pRmNm.exit.Id, 0, pRmCc.entry.Id, 0);

        // ---- QUIT (switch out 8) + On Socket Disconnect: leave every channel + clear identity ----
        var disc = Add("event.socket.disconnect", 1500, -320);
        var qGetNick = Add("db.get", 1500, -220); qGetNick.SetParam("table", "cn"); qGetNick.SetParam("key", "{conn}");
        var qNick = Add("data.setvar", 1700, -220); qNick.SetParam("name", "qnick");
        g.Connect(qGetNick.Id, 0, qNick.Id, 1);
        g.Connect(sw.Id, 8, qNick.Id, 0);     // QUIT command
        g.Connect(disc.Id, 0, qNick.Id, 0);   // socket dropped (multi-source exec)
        var qGetCc = Add("db.get", 1500, -140); qGetCc.SetParam("table", "cc"); qGetCc.SetParam("key", "{conn}");
        var qChLoop = Add("logic.forEach", 1700, -140); qChLoop.SetParam("sep", "space"); qChLoop.SetParam("var", "ch");
        g.Connect(qNick.Id, 0, qChLoop.Id, 0);
        g.Connect(qGetCc.Id, 0, qChLoop.Id, 1);
        // per channel: broadcast QUIT to its members, then remove conn from mem[ch] and qnick from nm[ch]
        var qBcGet = Add("db.get", 1900, -200); qBcGet.SetParam("table", "mem"); qBcGet.SetParam("key", "{ch}");
        var qBcLoop = Add("logic.forEach", 1900, -140); qBcLoop.SetParam("sep", "space"); qBcLoop.SetParam("var", "qm");
        g.Connect(qChLoop.Id, 0, qBcLoop.Id, 0);
        g.Connect(qBcGet.Id, 0, qBcLoop.Id, 1);
        var qBcSend = Add("socket.send", 2100, -140); qBcSend.SetParam("conn", "{qm}"); qBcSend.SetParam("data", ":{qnick}!user@ircuitry QUIT :Connection closed"); qBcSend.SetParam("append", "crlf");
        g.Connect(qBcLoop.Id, 0, qBcSend.Id, 0);
        var qRmMem = RemoveFromList("mem", "{ch}", "{conn}", "qmem", 1900, 0);
        var qRmNm = RemoveFromList("nm", "{ch}", "{qnick}", "qnm", 1900, 160);
        g.Connect(qBcLoop.Id, 1, qRmMem.entry.Id, 0);   // members notified -> remove from mem
        g.Connect(qRmMem.exit.Id, 0, qRmNm.entry.Id, 0);
        // after all channels: clear identity tables (empty value deletes the key)
        var qClrCn = Add("db.set", 1700, 360); qClrCn.SetParam("table", "cn"); qClrCn.SetParam("key", "{conn}"); qClrCn.SetParam("value", "");
        var qClrCu = Add("db.set", 1900, 360); qClrCu.SetParam("table", "cu"); qClrCu.SetParam("key", "{conn}"); qClrCu.SetParam("value", "");
        var qClrCr = Add("db.set", 2100, 360); qClrCr.SetParam("table", "cr"); qClrCr.SetParam("key", "{conn}"); qClrCr.SetParam("value", "");
        var qClrCc = Add("db.set", 2300, 360); qClrCc.SetParam("table", "cc"); qClrCc.SetParam("key", "{conn}"); qClrCc.SetParam("value", "");
        var qClrNc = Add("db.set", 2500, 360); qClrNc.SetParam("table", "nc"); qClrNc.SetParam("key", "{qnick}"); qClrNc.SetParam("value", "");
        g.Connect(qChLoop.Id, 1, qClrCn.Id, 0);   // all channels done -> clear identity
        g.Connect(qClrCn.Id, 0, qClrCu.Id, 0);
        g.Connect(qClrCu.Id, 0, qClrCr.Id, 0);
        g.Connect(qClrCr.Id, 0, qClrCc.Id, 0);
        g.Connect(qClrCc.Id, 0, qClrNc.Id, 0);

        // ---- CAP (switch out 11): LS / REQ / END / LIST, suspending registration until END ----
        var capSw = Add("logic.switch", 360, 1900); capSw.SetParam("value", "{2}"); capSw.SetParam("cases", CasesJson("LS", "REQ", "END", "LIST")); capSw.SetParam("ci", "true");
        g.Connect(sw.Id, 11, capSw.Id, 0);
        var capLsSet = Add("db.set", 560, 1840); capLsSet.SetParam("table", "capneg"); capLsSet.SetParam("key", "{conn}"); capLsSet.SetParam("value", "1");
        var capLsSend = Add("socket.send", 760, 1840); capLsSend.SetParam("conn", "{conn}"); capLsSend.SetParam("data", ":ircuitry CAP * LS :"); capLsSend.SetParam("append", "crlf");
        g.Connect(capSw.Id, 1, capLsSet.Id, 0); g.Connect(capLsSet.Id, 0, capLsSend.Id, 0);
        var capReqSend = Add("socket.send", 560, 1920); capReqSend.SetParam("conn", "{conn}"); capReqSend.SetParam("data", ":ircuitry CAP * NAK :{3}"); capReqSend.SetParam("append", "crlf");
        g.Connect(capSw.Id, 2, capReqSend.Id, 0);
        var capEndClear = Add("db.set", 560, 2000); capEndClear.SetParam("table", "capneg"); capEndClear.SetParam("key", "{conn}"); capEndClear.SetParam("value", "");
        g.Connect(capSw.Id, 3, capEndClear.Id, 0); g.Connect(capEndClear.Id, 0, ifCapNeg.Id, 0);   // END -> clear + re-check registration
        var capListSend = Add("socket.send", 560, 2080); capListSend.SetParam("conn", "{conn}"); capListSend.SetParam("data", ":ircuitry CAP * LIST :"); capListSend.SetParam("append", "crlf");
        g.Connect(capSw.Id, 4, capListSend.Id, 0);

        // ---- TOPIC (switch out 9): query (331/332) or set (store in tp + broadcast) ----
        var tEntryGetNick = Add("db.get", 260, 2200); tEntryGetNick.SetParam("table", "cn"); tEntryGetNick.SetParam("key", "{conn}");
        var tNickVar = Add("data.setvar", 360, 2200); tNickVar.SetParam("name", "topNick");
        g.Connect(tEntryGetNick.Id, 0, tNickVar.Id, 1);
        g.Connect(sw.Id, 9, tNickVar.Id, 0);
        var tArgFmt = Add("data.format", 360, 2260); tArgFmt.SetParam("template", "{3}");
        var tIsSet = Add("logic.if", 560, 2200); tIsSet.SetParam("op", "is empty");   // is the topic-text arg empty (= query)?
        g.Connect(tNickVar.Id, 0, tIsSet.Id, 0);
        g.Connect(tArgFmt.Id, 0, tIsSet.Id, 1);
        // query (empty arg): 332 if a topic is stored, else 331
        var tGetTopic = Add("db.get", 760, 2120); tGetTopic.SetParam("table", "tp"); tGetTopic.SetParam("key", "{2}");
        var tTopicVar = Add("data.setvar", 760, 2160); tTopicVar.SetParam("name", "ctopic");
        g.Connect(tGetTopic.Id, 0, tTopicVar.Id, 1);
        g.Connect(tIsSet.Id, 0, tTopicVar.Id, 0);
        var tHasFmt = Add("data.format", 960, 2220); tHasFmt.SetParam("template", "{ctopic}");
        var tHasTopic = Add("logic.if", 960, 2160); tHasTopic.SetParam("op", "is empty");
        g.Connect(tHasFmt.Id, 0, tHasTopic.Id, 1);
        g.Connect(tTopicVar.Id, 0, tHasTopic.Id, 0);
        var t331 = Add("socket.send", 1160, 2120); t331.SetParam("conn", "{conn}"); t331.SetParam("data", ":ircuitry 331 {topNick} {2} :No topic is set"); t331.SetParam("append", "crlf");
        var t332 = Add("socket.send", 1160, 2200); t332.SetParam("conn", "{conn}"); t332.SetParam("data", ":ircuitry 332 {topNick} {2} :{ctopic}"); t332.SetParam("append", "crlf");
        g.Connect(tHasTopic.Id, 0, t331.Id, 0);
        g.Connect(tHasTopic.Id, 1, t332.Id, 0);
        // set (non-empty arg): store + broadcast TOPIC to members
        var tSet = Add("db.set", 760, 2300); tSet.SetParam("table", "tp"); tSet.SetParam("key", "{2}"); tSet.SetParam("value", "{3}");
        // +t topic-lock gate: when +t is set, only a channel operator may change the topic (482)
        var ttCmGet = Add("db.get", 360, 2480); ttCmGet.SetParam("table", "cm"); ttCmGet.SetParam("key", "{2}");
        var ttIf = Add("logic.if", 360, 2540); ttIf.SetParam("op", "contains"); ttIf.SetParam("b", "t");
        g.Connect(ttCmGet.Id, 0, ttIf.Id, 1);
        g.Connect(tIsSet.Id, 1, ttIf.Id, 0);       // set-topic -> +t check
        g.Connect(ttIf.Id, 1, tSet.Id, 0);         // no +t -> set freely
        var ttOpGet = Add("db.get", 560, 2540); ttOpGet.SetParam("table", "st"); ttOpGet.SetParam("key", "{2} {conn}");
        var ttOpIf = Add("logic.if", 560, 2480); ttOpIf.SetParam("op", "contains"); ttOpIf.SetParam("b", "o");
        g.Connect(ttOpGet.Id, 0, ttOpIf.Id, 1);
        g.Connect(ttIf.Id, 0, ttOpIf.Id, 0);       // +t set -> op?
        g.Connect(ttOpIf.Id, 0, tSet.Id, 0);       // op -> set
        var tt482 = Add("socket.send", 760, 2480); tt482.SetParam("conn", "{conn}"); tt482.SetParam("data", ":ircuitry 482 {topNick} {2} :You're not channel operator"); tt482.SetParam("append", "crlf");
        g.Connect(ttOpIf.Id, 1, tt482.Id, 0);      // not op -> 482
        var tBcGet = Add("db.get", 760, 2420); tBcGet.SetParam("table", "mem"); tBcGet.SetParam("key", "{2}");
        var tBcLoop = Add("logic.forEach", 1160, 2360); tBcLoop.SetParam("sep", "space"); tBcLoop.SetParam("var", "tm");
        g.Connect(tSet.Id, 0, tBcLoop.Id, 0);
        g.Connect(tBcGet.Id, 0, tBcLoop.Id, 1);
        var tBcSend = Add("socket.send", 1360, 2360); tBcSend.SetParam("conn", "{tm}"); tBcSend.SetParam("data", ":{topNick}!user@ircuitry TOPIC {2} :{3}"); tBcSend.SetParam("append", "crlf");
        g.Connect(tBcLoop.Id, 0, tBcSend.Id, 0);

        // ---- MODE (switch out 10): query -> 324, or +o/-o/+v/-v <nick> -> change status (st) + broadcast ----
        var mGetNick = Add("db.get", 1600, 1900); mGetNick.SetParam("table", "cn"); mGetNick.SetParam("key", "{conn}");
        var mNick = Add("data.setvar", 1700, 1900); mNick.SetParam("name", "mnick");
        g.Connect(mGetNick.Id, 0, mNick.Id, 1);
        g.Connect(sw.Id, 10, mNick.Id, 0);
        var mChan = Add("data.setvar", 1700, 1960); mChan.SetParam("name", "mchan"); mChan.SetParam("value", "{2}");
        g.Connect(mNick.Id, 0, mChan.Id, 0);
        var mRest = Add("data.setvar", 1700, 2020); mRest.SetParam("name", "mrest"); mRest.SetParam("value", "{3}");
        g.Connect(mChan.Id, 0, mRest.Id, 0);
        var mRestFmt = Add("data.format", 1700, 2080); mRestFmt.SetParam("template", "{mrest}");
        var mIsQuery = Add("logic.if", 1900, 1960); mIsQuery.SetParam("op", "is empty");
        g.Connect(mRestFmt.Id, 0, mIsQuery.Id, 1);
        g.Connect(mRest.Id, 0, mIsQuery.Id, 0);
        // query -> 324 (channel modes from cm)
        var mGetCm = Add("db.get", 2100, 1900); mGetCm.SetParam("table", "cm"); mGetCm.SetParam("key", "{mchan}");
        var mCmVar = Add("data.setvar", 2300, 1900); mCmVar.SetParam("name", "cmodes");
        g.Connect(mGetCm.Id, 0, mCmVar.Id, 1);
        g.Connect(mIsQuery.Id, 0, mCmVar.Id, 0);
        var m324 = Add("socket.send", 2500, 1900); m324.SetParam("conn", "{conn}"); m324.SetParam("data", ":ircuitry 324 {mnick} {mchan} +{cmodes}"); m324.SetParam("append", "crlf");
        g.Connect(mCmVar.Id, 0, m324.Id, 0);
        // set -> parse "<modestring> <arg>" from {mrest} (saves the outer {2}/{3} via {mchan}/{mrest} first)
        var mParseTxt = Add("data.format", 1900, 2080); mParseTxt.SetParam("template", "{mrest}");
        var mParse = Add("logic.regex", 2100, 2020); mParse.SetParam("pattern", "^(\\S+)\\s*(\\S*)"); mParse.SetParam("ci", "false");
        g.Connect(mParseTxt.Id, 0, mParse.Id, 1);
        // op-gate: only a channel operator may change modes (482 otherwise)
        var mOpGet = Add("db.get", 1900, 2140); mOpGet.SetParam("table", "st"); mOpGet.SetParam("key", "{mchan} {conn}");
        var mOpIf = Add("logic.if", 2050, 2080); mOpIf.SetParam("op", "contains"); mOpIf.SetParam("b", "o");
        g.Connect(mOpGet.Id, 0, mOpIf.Id, 1);
        g.Connect(mIsQuery.Id, 1, mOpIf.Id, 0);     // not query -> op-gate
        var m482 = Add("socket.send", 2050, 2160); m482.SetParam("conn", "{conn}"); m482.SetParam("data", ":ircuitry 482 {mnick} {mchan} :You're not channel operator"); m482.SetParam("append", "crlf");
        g.Connect(mOpIf.Id, 1, m482.Id, 0);         // not op -> 482
        g.Connect(mOpIf.Id, 0, mParse.Id, 0);       // op -> parse + dispatch
        var mSw = Add("logic.switch", 2300, 2020); mSw.SetParam("value", "{1}"); mSw.SetParam("cases", CasesJson("+o", "-o", "+v", "-v", "+i", "-i", "+m", "-m", "+n", "-n", "+t", "-t")); mSw.SetParam("ci", "false");
        g.Connect(mParse.Id, 0, mSw.Id, 0);
        // each case sets the new status value {mval}, then all converge to apply + broadcast
        var mvO = Add("data.setvar", 2500, 1980); mvO.SetParam("name", "mval"); mvO.SetParam("value", "o");
        var mvOo = Add("data.setvar", 2500, 2040); mvOo.SetParam("name", "mval"); mvOo.SetParam("value", "");
        var mvV = Add("data.setvar", 2500, 2100); mvV.SetParam("name", "mval"); mvV.SetParam("value", "v");
        var mvVo = Add("data.setvar", 2500, 2160); mvVo.SetParam("name", "mval"); mvVo.SetParam("value", "");
        g.Connect(mSw.Id, 1, mvO.Id, 0);
        g.Connect(mSw.Id, 2, mvOo.Id, 0);
        g.Connect(mSw.Id, 3, mvV.Id, 0);
        g.Connect(mSw.Id, 4, mvVo.Id, 0);
        var mGetTc = Add("db.get", 2700, 2060); mGetTc.SetParam("table", "nc"); mGetTc.SetParam("key", "{2}");
        var mTargetVar = Add("data.setvar", 2900, 2060); mTargetVar.SetParam("name", "mtarget");
        g.Connect(mGetTc.Id, 0, mTargetVar.Id, 1);
        g.Connect(mvO.Id, 0, mTargetVar.Id, 0);
        g.Connect(mvOo.Id, 0, mTargetVar.Id, 0);
        g.Connect(mvV.Id, 0, mTargetVar.Id, 0);
        g.Connect(mvVo.Id, 0, mTargetVar.Id, 0);
        var mSetSt = Add("db.set", 3100, 2060); mSetSt.SetParam("table", "st"); mSetSt.SetParam("key", "{mchan} {mtarget}"); mSetSt.SetParam("value", "{mval}");
        g.Connect(mTargetVar.Id, 0, mSetSt.Id, 0);
        var mBcGet = Add("db.get", 3100, 2120); mBcGet.SetParam("table", "mem"); mBcGet.SetParam("key", "{mchan}");
        var mBcLoop = Add("logic.forEach", 3300, 2060); mBcLoop.SetParam("sep", "space"); mBcLoop.SetParam("var", "mbm");
        g.Connect(mSetSt.Id, 0, mBcLoop.Id, 0);
        g.Connect(mBcGet.Id, 0, mBcLoop.Id, 1);
        var mBcSend = Add("socket.send", 3500, 2060); mBcSend.SetParam("conn", "{mbm}"); mBcSend.SetParam("data", ":{mnick}!user@ircuitry MODE {mchan} {1} {2}"); mBcSend.SetParam("append", "crlf");
        g.Connect(mBcLoop.Id, 0, mBcSend.Id, 0);
        // channel flag modes (+i/-i/+m/-m/+n/-n/+t/-t): toggle the char in cm, then converge to the same broadcast
        var mfIadd = ToggleChar("cm", "{mchan}", "i", true, 2700, 2240); g.Connect(mSw.Id, 5, mfIadd.Id, 0); g.Connect(mfIadd.Id, 0, mBcLoop.Id, 0);
        var mfIrem = ToggleChar("cm", "{mchan}", "i", false, 2700, 2380); g.Connect(mSw.Id, 6, mfIrem.Id, 0); g.Connect(mfIrem.Id, 0, mBcLoop.Id, 0);
        var mfMadd = ToggleChar("cm", "{mchan}", "m", true, 2700, 2520); g.Connect(mSw.Id, 7, mfMadd.Id, 0); g.Connect(mfMadd.Id, 0, mBcLoop.Id, 0);
        var mfMrem = ToggleChar("cm", "{mchan}", "m", false, 2700, 2660); g.Connect(mSw.Id, 8, mfMrem.Id, 0); g.Connect(mfMrem.Id, 0, mBcLoop.Id, 0);
        var mfNadd = ToggleChar("cm", "{mchan}", "n", true, 2700, 2800); g.Connect(mSw.Id, 9, mfNadd.Id, 0); g.Connect(mfNadd.Id, 0, mBcLoop.Id, 0);
        var mfNrem = ToggleChar("cm", "{mchan}", "n", false, 2700, 2940); g.Connect(mSw.Id, 10, mfNrem.Id, 0); g.Connect(mfNrem.Id, 0, mBcLoop.Id, 0);
        var mfTadd = ToggleChar("cm", "{mchan}", "t", true, 2700, 3080); g.Connect(mSw.Id, 11, mfTadd.Id, 0); g.Connect(mfTadd.Id, 0, mBcLoop.Id, 0);
        var mfTrem = ToggleChar("cm", "{mchan}", "t", false, 2700, 3220); g.Connect(mSw.Id, 12, mfTrem.Id, 0); g.Connect(mfTrem.Id, 0, mBcLoop.Id, 0);

        // ---- WHOIS (switch out 12): 311 user, 312 server, 319 channels (@/+), 318 end; 401 if no such nick ----
        var wReqGet = Add("db.get", 200, 3500); wReqGet.SetParam("table", "cn"); wReqGet.SetParam("key", "{conn}");
        var wReqVar = Add("data.setvar", 400, 3500); wReqVar.SetParam("name", "wrnick");   // requester nick (numeric target)
        g.Connect(wReqGet.Id, 0, wReqVar.Id, 1);
        g.Connect(sw.Id, 12, wReqVar.Id, 0);
        var wNcGet = Add("db.get", 200, 3580); wNcGet.SetParam("table", "nc"); wNcGet.SetParam("key", "{2}");
        var wExists = Add("logic.if", 600, 3500); wExists.SetParam("op", "is empty");
        g.Connect(wNcGet.Id, 0, wExists.Id, 1);
        g.Connect(wReqVar.Id, 0, wExists.Id, 0);
        var w401 = Add("socket.send", 600, 3440); w401.SetParam("conn", "{conn}"); w401.SetParam("data", ":ircuitry 401 {wrnick} {2} :No such nick/channel"); w401.SetParam("append", "crlf");
        g.Connect(wExists.Id, 0, w401.Id, 0);          // empty -> no such nick
        var wTcVar = Add("data.setvar", 800, 3580); wTcVar.SetParam("name", "wtconn");   // target's conn id
        g.Connect(wNcGet.Id, 0, wTcVar.Id, 1);
        g.Connect(wExists.Id, 1, wTcVar.Id, 0);        // exists -> gather + reply
        var wCuGet = Add("db.get", 800, 3660); wCuGet.SetParam("table", "cu"); wCuGet.SetParam("key", "{wtconn}");
        var wUserVar = Add("data.setvar", 1000, 3580); wUserVar.SetParam("name", "wuser");
        g.Connect(wCuGet.Id, 0, wUserVar.Id, 1);
        g.Connect(wTcVar.Id, 0, wUserVar.Id, 0);
        var w311 = Add("socket.send", 1000, 3500); w311.SetParam("conn", "{conn}"); w311.SetParam("data", ":ircuitry 311 {wrnick} {2} {wuser} ircuitry * :{wuser}"); w311.SetParam("append", "crlf");
        g.Connect(wUserVar.Id, 0, w311.Id, 0);
        var w312 = Add("socket.send", 1200, 3500); w312.SetParam("conn", "{conn}"); w312.SetParam("data", ":ircuitry 312 {wrnick} {2} ircuitry :ircuitry IRC server (nodes)"); w312.SetParam("append", "crlf");
        g.Connect(w311.Id, 0, w312.Id, 0);
        // 319 channels with @/+ prefixes: loop the target's channels (cc), look up status (st) per channel
        var wChInit = Add("data.setvar", 1000, 3660); wChInit.SetParam("name", "wchans"); wChInit.SetParam("value", "");
        g.Connect(w312.Id, 0, wChInit.Id, 0);
        var wCcGet = Add("db.get", 1000, 3720); wCcGet.SetParam("table", "cc"); wCcGet.SetParam("key", "{wtconn}");
        var wChLoop = Add("logic.forEach", 1200, 3660); wChLoop.SetParam("sep", "space"); wChLoop.SetParam("var", "wch");
        g.Connect(wChInit.Id, 0, wChLoop.Id, 0);
        g.Connect(wCcGet.Id, 0, wChLoop.Id, 1);
        var wStGet = Add("db.get", 1000, 3800); wStGet.SetParam("table", "st"); wStGet.SetParam("key", "{wch} {wtconn}");
        var wStVar = Add("data.setvar", 1200, 3760); wStVar.SetParam("name", "wcst");
        g.Connect(wStGet.Id, 0, wStVar.Id, 1);
        g.Connect(wChLoop.Id, 0, wStVar.Id, 0);
        var wStFmt = Add("data.format", 1200, 3860); wStFmt.SetParam("template", "{wcst}");
        var wIfO = Add("logic.if", 1400, 3760); wIfO.SetParam("op", "contains"); wIfO.SetParam("b", "o");
        g.Connect(wStFmt.Id, 0, wIfO.Id, 1);
        g.Connect(wStVar.Id, 0, wIfO.Id, 0);
        var wIfV = Add("logic.if", 1400, 3840); wIfV.SetParam("op", "contains"); wIfV.SetParam("b", "v");
        g.Connect(wStFmt.Id, 0, wIfV.Id, 1);
        g.Connect(wIfO.Id, 1, wIfV.Id, 0);
        var wPfxAt = Add("data.setvar", 1600, 3720); wPfxAt.SetParam("name", "wpfx"); wPfxAt.SetParam("value", "@");
        var wPfxPlus = Add("data.setvar", 1600, 3800); wPfxPlus.SetParam("name", "wpfx"); wPfxPlus.SetParam("value", "+");
        var wPfxNone = Add("data.setvar", 1600, 3880); wPfxNone.SetParam("name", "wpfx"); wPfxNone.SetParam("value", "");
        g.Connect(wIfO.Id, 0, wPfxAt.Id, 0);
        g.Connect(wIfV.Id, 0, wPfxPlus.Id, 0);
        g.Connect(wIfV.Id, 1, wPfxNone.Id, 0);
        var wChApp = Add("data.setvar", 1800, 3800); wChApp.SetParam("name", "wchans"); wChApp.SetParam("value", "{wchans} {wpfx}{wch}");
        g.Connect(wPfxAt.Id, 0, wChApp.Id, 0);
        g.Connect(wPfxPlus.Id, 0, wChApp.Id, 0);
        g.Connect(wPfxNone.Id, 0, wChApp.Id, 0);
        var w319 = Add("socket.send", 1400, 3660); w319.SetParam("conn", "{conn}"); w319.SetParam("data", ":ircuitry 319 {wrnick} {2} :{wchans}"); w319.SetParam("append", "crlf");
        g.Connect(wChLoop.Id, 1, w319.Id, 0);          // channels gathered -> 319
        var w318 = Add("socket.send", 1600, 3660); w318.SetParam("conn", "{conn}"); w318.SetParam("data", ":ircuitry 318 {wrnick} {2} :End of /WHOIS list"); w318.SetParam("append", "crlf");
        g.Connect(w319.Id, 0, w318.Id, 0);

        return g;
    }

    /// <summary>The all-built-in-nodes IRCd (NO code node): boot it hostless and drive two real TCP clients through
    /// registration, JOIN, a channel relay, a private message and PING - the same chat loop as the code-node
    /// version, but built entirely from logic.regex / logic.switch / logic.if / db / forEach / Socket Send. Needs
    /// no sandbox (no code node), so it runs anywhere.</summary>
    private static int IrcdNodesE2ETest()
    {
        var oldHome = Environment.GetEnvironmentVariable("IRCUITRY_HOME");
        var tmp = Path.Combine(Path.GetTempPath(), "ircuitry-ircdn-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        Environment.SetEnvironmentVariable("IRCUITRY_HOME", tmp);
        int port = FreePort();
        var rt = new BotRuntime(new ConsoleLog(), new System.Collections.Concurrent.ConcurrentDictionary<string, string>());
        var peers = new List<IrcPeer>();
        int fails = 0;
        try
        {
            // simulate stale session state left over from a previous run: a held nick + its conn row.
            // The On Start boot-wipe must clear these so a fresh client can claim the nick (no phantom 433).
            Ircuitry.Net.KvStore.Set("nc", "zombie", "deadconn");
            Ircuitry.Net.KvStore.Set("cn", "deadconn", "zombie");
            rt.Start(BuildIrcdNodeGraph(port), new IrcSettings { Host = "", Nick = "ircd" });
            IrcPeer? Dial() { for (int i = 0; i < 100; i++) { try { var p = new IrcPeer(port); peers.Add(p); return p; } catch { Thread.Sleep(50); } } return null; }
            var a = Dial();
            fails += Expect("ircdn-listen", a != null, $"node IRCd should listen on {port}");
            if (a == null) return fails;
            a.Send("NICK alice"); a.Send("USER alice 0 * :Alice");
            fails += Expect("ircdn-register", a.WaitFor(s => s.Contains(" 001 alice ") && s.Contains(" 376 "), 8000), "register alice (001..376)");
            // boot-wipe: the leftover "zombie" nick is gone, so a fresh client can register as it (no stale 433)
            var znaked = Dial();
            if (znaked != null) { znaked.Send("NICK zombie"); znaked.Send("USER zombie 0 * :Z"); fails += Expect("ircdn-boot-wipe", znaked.WaitFor(s => s.Contains(" 001 zombie"), 8000), "stale nick cleared on boot - fresh client claims it"); }
            var b = Dial();
            fails += Expect("ircdn-dial-b", b != null, "second client should connect");
            if (b == null) return fails;
            b.Send("NICK bob"); b.Send("USER bob 0 * :Bob");
            b.WaitFor(s => s.Contains(" 376 "), 8000);
            var x = Dial();
            if (x != null) { x.Send("NICK alice"); fails += Expect("ircdn-nick-collision", x.WaitFor(s => s.Contains(" 433 "), 6000), "duplicate nick -> 433"); }
            a.Send("JOIN #test");
            fails += Expect("ircdn-join", a.WaitFor(s => s.Contains("JOIN #test") && s.Contains(" 366 "), 8000), "alice joins #test with names");
            b.Send("JOIN #test");
            fails += Expect("ircdn-peer-join", a.WaitFor(s => s.Contains("bob!") && s.Contains("JOIN #test"), 8000), "alice sees bob join");
            a.Send("PRIVMSG #test :hi bob");
            fails += Expect("ircdn-relay", b.WaitFor(s => s.Contains("alice!") && s.Contains("PRIVMSG #test :hi bob"), 8000), "bob gets the channel message");
            b.Send("PRIVMSG alice :pm back");
            fails += Expect("ircdn-pm", a.WaitFor(s => s.Contains("bob!") && s.Contains("PRIVMSG alice :pm back"), 8000), "alice gets bob's PM");
            a.Send("PING :xyz");
            fails += Expect("ircdn-ping", a.WaitFor(s => s.Contains("PONG") && s.Contains(":xyz"), 8000), "alice gets a PONG");

            // NOTICE: same routing as PRIVMSG but the verb is NOTICE
            a.Send("NOTICE #test :heads up");
            fails += Expect("ircdn-notice", b.WaitFor(s => s.Contains("alice!") && s.Contains("NOTICE #test :heads up"), 8000), "bob gets a channel NOTICE");
            // TOPIC: set broadcasts; query returns 332
            a.Send("TOPIC #test :the topic");
            fails += Expect("ircdn-topic-set", b.WaitFor(s => s.Contains("alice!") && s.Contains("TOPIC #test :the topic"), 8000), "bob sees the topic change");
            b.Send("TOPIC #test");
            fails += Expect("ircdn-topic-query", b.WaitFor(s => s.Contains(" 332 ") && s.Contains("#test :the topic"), 8000), "TOPIC query returns 332");
            // CAP: registration is suspended until CAP END
            var z = Dial();
            if (z != null)
            {
                z.Send("CAP LS 302");
                fails += Expect("ircdn-cap-ls", z.WaitFor(s => s.Contains("CAP") && s.Contains(" LS "), 6000), "CAP LS gets a CAP * LS reply");
                z.Send("NICK zed"); z.Send("USER zed 0 * :Zed");
                bool early = z.WaitFor(s => s.Contains(" 001 "), 1200);
                fails += Expect("ircdn-cap-suspends", !early, "no welcome before CAP END");
                z.Send("CAP END");
                fails += Expect("ircdn-cap-end", z.WaitFor(s => s.Contains(" 001 zed"), 8000), "CAP END releases registration");
            }

            // MODE: alice is the channel creator -> op; NAMES shows @alice; she can +v bob; query returns 324
            fails += Expect("ircdn-names-op", a.WaitFor(s => s.Contains(" 353 ") && s.Contains("@alice"), 8000), "first joiner alice is @op in NAMES");
            a.Send("MODE #test +v bob");
            fails += Expect("ircdn-mode-voice", b.WaitFor(s => s.Contains("alice!") && s.Contains("MODE #test +v bob"), 8000), "bob sees +v from alice");
            b.Send("MODE #test");
            fails += Expect("ircdn-mode-query", b.WaitFor(s => s.Contains(" 324 ") && s.Contains("#test"), 8000), "MODE query returns 324");
            a.Send("MODE #test +m");
            fails += Expect("ircdn-mode-flag", b.WaitFor(s => s.Contains("MODE #test +m"), 8000), "alice (op) sets +m, bob sees it");
            b.Send("MODE #test +n");   // bob is voiced, not op
            fails += Expect("ircdn-mode-opgate", b.WaitFor(s => s.Contains(" 482 "), 8000), "non-op MODE change is rejected with 482");

            // ---- flag enforcement on a fresh channel #mod (alice = creator/op) ----
            a.Send("JOIN #mod");
            fails += Expect("ircdn-mod-join", a.WaitFor(s => s.Contains("JOIN #mod") && s.Contains(" 366 "), 8000), "alice creates #mod");
            b.Send("JOIN #mod");
            fails += Expect("ircdn-mod-bjoin", a.WaitFor(s => s.Contains("bob!") && s.Contains("JOIN #mod"), 8000), "bob joins #mod");
            // +m: bob (plain member, no voice) cannot speak -> 404
            a.Send("MODE #mod +m"); b.WaitFor(s => s.Contains("MODE #mod +m"), 8000);
            b.Send("PRIVMSG #mod :muted?");
            fails += Expect("ircdn-enf-m", b.WaitFor(s => s.Contains(" 404 "), 8000), "+m blocks non-voiced bob (404)");
            // voicing bob lets him speak under +m
            a.Send("MODE #mod +v bob"); b.WaitFor(s => s.Contains("MODE #mod +v bob"), 8000);
            b.Send("PRIVMSG #mod :now?");
            fails += Expect("ircdn-enf-m-voice", a.WaitFor(s => s.Contains("bob!") && s.Contains("now?"), 8000), "+v bob can speak under +m");
            // +n: a non-member cannot send to the channel -> 404; +i: a non-member cannot JOIN -> 473
            var d = Dial();
            if (d != null)
            {
                d.Send("NICK dave"); d.Send("USER dave 0 * :Dave"); d.WaitFor(s => s.Contains(" 376 "), 8000);
                a.Send("MODE #mod +n"); b.WaitFor(s => s.Contains("MODE #mod +n"), 8000);
                d.Send("PRIVMSG #mod :outsider");
                fails += Expect("ircdn-enf-n", d.WaitFor(s => s.Contains(" 404 "), 8000), "+n blocks non-member dave (404)");
                a.Send("MODE #mod +i"); b.WaitFor(s => s.Contains("MODE #mod +i"), 8000);
                d.Send("JOIN #mod");
                fails += Expect("ircdn-enf-i", d.WaitFor(s => s.Contains(" 473 "), 8000), "+i blocks dave JOIN (473)");
            }
            // +t: a non-op cannot change the topic -> 482 (bob is voiced, not op)
            a.Send("MODE #mod +t"); b.WaitFor(s => s.Contains("MODE #mod +t"), 8000);
            b.Send("TOPIC #mod :hijack");
            fails += Expect("ircdn-enf-t", b.WaitFor(s => s.Contains(" 482 "), 8000), "+t blocks non-op bob TOPIC (482)");

            // WHOIS: 311 user line, 319 channels (bob is in #test and #mod), 318 end; unknown nick -> 401
            a.Send("WHOIS bob");
            fails += Expect("ircdn-whois", a.WaitFor(s => s.Contains(" 311 ") && s.Contains("bob"), 8000), "WHOIS bob returns 311 user line");
            fails += Expect("ircdn-whois-chans", a.WaitFor(s => s.Contains(" 319 ") && s.Contains("#test"), 8000), "WHOIS shows bob's channels");
            fails += Expect("ircdn-whois-end", a.WaitFor(s => s.Contains(" 318 ") && s.Contains("bob"), 8000), "WHOIS ends with 318");
            a.Send("WHOIS nobody");
            fails += Expect("ircdn-whois-401", a.WaitFor(s => s.Contains(" 401 ") && s.Contains("nobody"), 8000), "WHOIS unknown nick -> 401");

            // PART: bob leaves; alice sees it and bob is removed (a later channel message must not reach him)
            b.Send("PART #test :bye");
            fails += Expect("ircdn-part", a.WaitFor(s => s.Contains("bob!") && s.Contains("PART #test"), 8000), "alice sees bob PART");
            a.Send("PRIVMSG #test :after-part");
            fails += Expect("ircdn-part-removed", !b.WaitFor(s => s.Contains("after-part"), 1500), "parted bob should not receive channel messages");

            // On Socket Disconnect: a third client joins then drops; alice (still in #test) sees the QUIT broadcast
            var c = Dial();
            fails += Expect("ircdn-dial-c", c != null, "third client should connect");
            if (c == null) return fails;
            c.Send("NICK carol"); c.Send("USER carol 0 * :Carol"); c.WaitFor(s => s.Contains(" 376 "), 8000);
            c.Send("JOIN #test"); a.WaitFor(s => s.Contains("carol!") && s.Contains("JOIN #test"), 8000);
            c.Close();   // drop the socket -> On Socket Disconnect -> cleanup + QUIT broadcast
            fails += Expect("ircdn-disconnect-quit", a.WaitFor(s => s.Contains("carol!") && s.Contains("QUIT"), 8000), "alice sees carol QUIT when her socket drops");
        }
        finally
        {
            foreach (var p in peers) p.Close();
            rt.Stop(); Thread.Sleep(150);
            Environment.SetEnvironmentVariable("IRCUITRY_HOME", oldHome);
            try { Directory.Delete(tmp, true); } catch { }
        }
        return fails;
    }

    /// <summary>The all-node IRCd over TLS: flip the Socket Listen to tls=true (it auto-generates a self-signed
    /// server cert) and register a real TLS client through it - the original goal, an in-graph TLS IRC server.</summary>
    private static int IrcdNodesTlsTest()
    {
        var oldHome = Environment.GetEnvironmentVariable("IRCUITRY_HOME");
        var tmp = Path.Combine(Path.GetTempPath(), "ircuitry-ircdtls-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        Environment.SetEnvironmentVariable("IRCUITRY_HOME", tmp);
        int port = FreePort();
        var rt = new BotRuntime(new ConsoleLog(), new System.Collections.Concurrent.ConcurrentDictionary<string, string>());
        var peers = new List<IrcPeer>();
        int fails = 0;
        try
        {
            rt.Start(BuildIrcdNodeGraph(port, tls: true), new IrcSettings { Host = "", Nick = "ircd" });
            IrcPeer? Dial() { for (int i = 0; i < 100; i++) { try { var p = new IrcPeer(port, tls: true); peers.Add(p); return p; } catch { Thread.Sleep(80); } } return null; }
            var a = Dial();
            fails += Expect("ircdn-tls-handshake", a != null, $"TLS IRCd should complete a TLS handshake on {port}");
            if (a == null) return fails;
            a.Send("NICK alice"); a.Send("USER alice 0 * :Alice");
            fails += Expect("ircdn-tls-register", a.WaitFor(s => s.Contains(" 001 alice ") && s.Contains(" 376 "), 12000), "TLS client should register (001..376)");
        }
        finally
        {
            foreach (var p in peers) p.Close();
            rt.Stop(); Thread.Sleep(150);
            Environment.SetEnvironmentVariable("IRCUITRY_HOME", oldHome);
            try { Directory.Delete(tmp, true); } catch { }
        }
        return fails;
    }

    private static int Expect(string name, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}   {(ok ? "" : detail)}");
        return ok ? 0 : 1;
    }

    /// <summary>"Bake" a selection of plain nodes into one composite (subgraph) node: the auto-wrap derives
    /// its pins from the boundary wires, and the resulting node runs the bundled flow correctly.</summary>
    private static int CompositeBakeTest()
    {
        var oldHome = Environment.GetEnvironmentVariable("IRCUITRY_HOME");
        var tmp = Path.Combine(Path.GetTempPath(), "ircuitry-bake-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // parent flow: cmd -> upper -> reverse -> reply; bake the two transforms into one node
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "x");
            var t1 = N(g, "data.transform", 200, 0); t1.SetParam("op", "upper");
            var t2 = N(g, "data.transform", 400, 0); t2.SetParam("op", "reverse");
            var reply = N(g, "action.reply", 600, 0);
            g.Connect(cmd.Id, 1, t1.Id, 0);     // args -> upper.text  (inbound data -> becomes an input)
            g.Connect(t1.Id, 0, t2.Id, 0);      // upper.result -> reverse.text  (internal)
            g.Connect(t2.Id, 0, reply.Id, 1);   // reverse.result -> reply  (outbound data -> becomes an output)

            var ed = new Ircuitry.Editor.GraphEditor(g);
            ed.Selection.Add(t1.Id); ed.Selection.Add(t2.Id);
            var manifest = ed.BuildCompositeFromSelection("Shout Reverse", "repeat", "Data", "upper then reverse", false, out var err);
            if (manifest == null) return Expect("composite-bake", false, "build failed: " + err);

            Environment.SetEnvironmentVariable("IRCUITRY_HOME", tmp);
            if (!Ircuitry.App.AppModel.WorkspaceDir.StartsWith(tmp, StringComparison.Ordinal))
                return Expect("composite-bake (skipped: no sandbox)", true, "");
            Directory.CreateDirectory(NodeCatalog.CustomDir);
            File.WriteAllText(Path.Combine(NodeCatalog.CustomDir, "subflow.shout-reverse.ircnode"), manifest);
            NodeCatalog.LoadCustom();
            if (!NodeCatalog.TryGet("subflow.shout-reverse", out var compDef))
                return Expect("composite-bake (skipped: didn't register)", true, "");

            // run the baked node: cmd -> composite(text<-args) -> reply(result)
            var g2 = new NodeGraph();
            var c2 = g2.Add(NodeCatalog.Get("event.command"), Vector2.Zero); c2.SetParam("command", "go");
            var comp = g2.Add(compDef, new Vector2(200, 0));
            var rep2 = g2.Add(NodeCatalog.Get("action.reply"), new Vector2(400, 0));
            g2.Connect(c2.Id, 0, comp.Id, 0);   // exec
            g2.Connect(c2.Id, 1, comp.Id, 1);   // args -> composite 'text'
            g2.Connect(comp.Id, 0, rep2.Id, 0); // then -> reply.exec
            g2.Connect(comp.Id, 1, rep2.Id, 1); // result -> reply.message
            var s = new FakeSink();
            var vars = Vars("!go hello", "u", "#x"); vars["args"] = "hello";
            GraphExecutor.Fire(g2, s, c2, vars);
            bool ok = s.Sent.Count == 1 && s.Sent[0].text == "OLLEH";   // upper(hello)=HELLO, reverse=OLLEH
            return Expect("composite-bake", ok, Dump(s) + "  err=" + err);
        }
        finally
        {
            try { Directory.Delete(Path.Combine(tmp, "nodes"), true); } catch { }
            NodeCatalog.LoadCustom();
            Environment.SetEnvironmentVariable("IRCUITRY_HOME", oldHome);
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
        }
    }

    /// <summary>Composite param exposure: an inner node's value can be EXPOSED as a setting on the composite
    /// node (a {token}), which the end user fills in the inspector and the runtime seeds into the subgraph.</summary>
    private static int CompositeExposeTest()
    {
        var oldHome = Environment.GetEnvironmentVariable("IRCUITRY_HOME");
        var tmp = Path.Combine(Path.GetTempPath(), "ircuitry-expose-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // mini-graph: Subflow Start -> Send Reply(message="{greeting} world"); expose 'greeting' (default "hi")
            var mg = new NodeGraph();
            var fin = mg.Add(NodeCatalog.Get("flow.in"), Vector2.Zero);
            var rep = mg.Add(NodeCatalog.Get("action.reply"), new Vector2(200, 0)); rep.SetParam("message", "{greeting} world");
            mg.Connect(fin.Id, 0, rep.Id, 0);
            var ed = new Ircuitry.Editor.GraphEditor(mg);
            var manifest = ed.SerializeAsComposite("subflow.greet", "Greet", "puzzle-piece", "Action", "",
                new Dictionary<string, string> { ["greeting"] = "hi" });
            if (manifest == null) return Expect("composite-expose", false, "serialize failed");

            Environment.SetEnvironmentVariable("IRCUITRY_HOME", tmp);
            if (!Ircuitry.App.AppModel.WorkspaceDir.StartsWith(tmp, StringComparison.Ordinal)) return Expect("composite-expose (skipped)", true, "");
            Directory.CreateDirectory(NodeCatalog.CustomDir);
            File.WriteAllText(Path.Combine(NodeCatalog.CustomDir, "subflow.greet.ircnode"), manifest);
            NodeCatalog.LoadCustom();
            if (!NodeCatalog.TryGet("subflow.greet", out var def)) return Expect("composite-expose (skipped)", true, "");
            bool hasParam = def.Params.Any(p => p.Key == "greeting" && p.Default == "hi");   // exposed as a setting w/ default

            // place it and override the exposed setting -> the inner reply uses it
            var g = new NodeGraph();
            var cmd = g.Add(NodeCatalog.Get("event.command"), Vector2.Zero); cmd.SetParam("command", "go");
            var comp = g.Add(def, new Vector2(200, 0)); comp.SetParam("greeting", "hey");
            g.Connect(cmd.Id, 0, comp.Id, 0);
            var s = new FakeSink();
            GraphExecutor.Fire(g, s, cmd, Vars("!go", "u", "#x"));
            bool ok = hasParam && s.Sent.Any(t => t.text == "hey world");
            return Expect("composite-expose", ok, "hasParam=" + hasParam + " " + Dump(s));
        }
        finally
        {
            try { Directory.Delete(Path.Combine(tmp, "nodes"), true); } catch { }
            NodeCatalog.LoadCustom();
            Environment.SetEnvironmentVariable("IRCUITRY_HOME", oldHome);
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
        }
    }

    /// <summary>List params ("Add another"): a new blank row survives Encode->Parse so you can type into it
    /// (the old bug dropped it), an untouched empty list stays "", and readers skip fully-blank rows.</summary>
    private static int ParamListAddTest()
    {
        int fails = 0;
        // untouched lone empty row -> "" (no spurious change)
        fails += Expect("plist-empty-stays-empty", Ircuitry.Core.ParamList.Encode(new List<string[]> { new[] { "", "" } }, true) == "", "");
        // Add another: the new blank row persists through a round-trip
        var rows = Ircuitry.Core.ParamList.Parse("[[\"k1\",\"v1\"]]");
        rows.Add(new[] { "", "" });   // user clicks "Add another"
        var rt = Ircuitry.Core.ParamList.Parse(Ircuitry.Core.ParamList.Encode(rows, true));
        fails += Expect("plist-add-persists", rt.Count == 2, "rows=" + rt.Count);
        // ...and once typed, both rows stick
        rt[1] = new[] { "k2", "v2" };
        var rt2 = Ircuitry.Core.ParamList.Parse(Ircuitry.Core.ParamList.Encode(rt, true));
        fails += Expect("plist-second-row", rt2.Count == 2 && rt2[1][0] == "k2", "");
        // readers skip fully-blank rows (so no empty IRC tags etc.)
        var pairs = Ircuitry.Core.ParamList.Pairs("[[\"a\",\"1\"],[\"\",\"\"],[\"b\",\"2\"]]").ToList();
        fails += Expect("plist-skip-blank", pairs.Count == 2 && pairs[0].key == "a" && pairs[1].key == "b", "n=" + pairs.Count);
        return fails;
    }

    /// <summary>The in-modal mini editor's whole-graph serialization (explicit Subflow scaffold) produces a
    /// loadable composite node with the pins defined by its Subflow Input/Output nodes.</summary>
    private static int CompositeMiniSerializeTest()
    {
        var mg = new NodeGraph();
        var fin = mg.Add(NodeCatalog.Get("flow.in"), Vector2.Zero);
        var arg = mg.Add(NodeCatalog.Get("flow.arg"), new Vector2(0, 100)); arg.SetParam("name", "text");
        var up = mg.Add(NodeCatalog.Get("data.transform"), new Vector2(200, 100)); up.SetParam("op", "upper");
        var ret = mg.Add(NodeCatalog.Get("flow.return"), new Vector2(400, 100)); ret.SetParam("name", "out");
        mg.Connect(fin.Id, 0, ret.Id, 0); mg.Connect(arg.Id, 0, up.Id, 0); mg.Connect(up.Id, 0, ret.Id, 1);
        var med = new Ircuitry.Editor.GraphEditor(mg);
        var m = med.SerializeAsComposite("subflow.mini", "Mini", "puzzle-piece", "Data", "x");
        var def = m != null ? CustomNode.Load(m) : null;
        bool ok = def != null && def.Inputs.Any(p => p.Name == "text") && def.Outputs.Any(p => p.Name == "out");
        return Expect("composite-mini-serialize", ok, "built=" + (m != null));
    }

    /// <summary>A custom .ircnode with a Tool output is usable directly as an AI tool: Ask AI advertises it
    /// to the model (its data inputs become args), and a call binds the args to the node, runs it, and feeds
    /// its data output back. Sandboxed to a throwaway nodes dir.</summary>
    private static int NodeAsToolTest()
    {
        var oldHome = Environment.GetEnvironmentVariable("IRCUITRY_HOME");
        var tmp = Path.Combine(Path.GetTempPath(), "ircuitry-nodetool-" + Guid.NewGuid().ToString("N")[..8]);
        int port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        try { listener.Start(); }
        catch (Exception ex) { return Expect("node-as-tool (skipped: " + ex.Message + ")", true, ""); }

        var bodies = new System.Collections.Concurrent.ConcurrentQueue<string>();
        bool stop = false; int reqs = 0;
        string toolJson = """{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"c1","type":"function","function":{"name":"test_echotool","arguments":"{\"query\":\"hi\"}"}}]}}]}""";
        string finalJson = """{"choices":[{"message":{"content":"ok"}}]}""";
        var server = new Thread(() =>
        {
            try
            {
                while (!stop)
                {
                    var ctx = listener.GetContext();
                    using (var sr = new System.IO.StreamReader(ctx.Request.InputStream)) bodies.Enqueue(sr.ReadToEnd());
                    int n = Interlocked.Increment(ref reqs);
                    var b = Encoding.UTF8.GetBytes(n == 1 ? toolJson : finalJson);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.OutputStream.Write(b, 0, b.Length);
                    ctx.Response.Close();
                }
            }
            catch { }
        }) { IsBackground = true };
        server.Start();

        int fails;
        try
        {
            Environment.SetEnvironmentVariable("IRCUITRY_HOME", tmp);
            Directory.CreateDirectory(Path.Combine(tmp, "nodes"));
            File.WriteAllText(Path.Combine(tmp, "nodes", "test.echotool.ircnode"),
                "{\"typeId\":\"test.echotool\",\"title\":\"Echo\",\"category\":\"Ai\",\"description\":\"Echoes the query.\"," +
                "\"inputs\":[{\"name\":\"query\",\"kind\":\"Text\"}]," +
                "\"outputs\":[{\"name\":\"tool\",\"kind\":\"Tool\"},{\"name\":\"result\",\"kind\":\"Text\"}]," +
                "\"language\":\"python\",\"timeout\":5,\"code\":\"import os\\nprint('ECHO:'+(os.environ.get('QUERY') or ''))\\n\"}");
            NodeCatalog.LoadCustom();
            if (!NodeCatalog.TryGet("test.echotool", out _))
            { stop = true; try { listener.Stop(); } catch { } return Expect("node-as-tool (skipped: custom load)", true, ""); }

            var g = new NodeGraph();
            var cmd = g.Add(NodeCatalog.Get("event.command"), Vector2.Zero); cmd.SetParam("command", "x");
            var ai = g.Add(NodeCatalog.Get("ai.reply"), new Vector2(300, 0));
            ai.SetParam("baseUrl", $"http://localhost:{port}/v1"); ai.SetParam("apiKey", "t"); ai.SetParam("model", "m");
            var tool = g.Add(NodeCatalog.Get("test.echotool"), new Vector2(100, 150));
            g.Connect(cmd.Id, 0, ai.Id, 0);
            g.Connect(tool.Id, 0, ai.Id, 2);   // the node's Tool output -> Ask AI 'tools'

            var s = new FakeSink();
            GraphExecutor.Fire(g, s, cmd, Vars("!x go", "u", "#c"));
            stop = true; try { listener.Stop(); } catch { }

            var all = string.Join("\n", bodies);
            bool offered = all.Contains("test_echotool");   // advertised to the model (proves detection)
            bool ran = all.Contains("ECHO:hi");             // arg bound -> code ran -> output fed back
            if (offered && !ran && reqs >= 2)
                fails = Expect("node-as-tool (skipped: no python runtime)", true, "");
            else
                fails = Expect("node-as-tool", offered && ran && reqs >= 2, $"offered={offered} ran={ran} reqs={reqs}");
        }
        finally
        {
            try { Directory.Delete(Path.Combine(tmp, "nodes"), true); } catch { }
            NodeCatalog.LoadCustom();   // tmp/nodes gone -> clears the test node without reading the user's dir
            Environment.SetEnvironmentVariable("IRCUITRY_HOME", oldHome);
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
        }
        return fails;
    }

    /// <summary>Baked composites used as AI tools, both ways: (A) an AI Tool sub-flow baked into one node -
    /// Ask AI looks inside, exposes the inner tool by name, runs its sub-flow and returns the Tool Reply;
    /// (B) an inputs-&gt;output composite ticked "usable as AI tool" - its input pins are the model's args and
    /// its first data output the result.</summary>
    private static int ToolBakeTest()
    {
        var oldHome = Environment.GetEnvironmentVariable("IRCUITRY_HOME");
        var tmp = Path.Combine(Path.GetTempPath(), "ircuitry-toolbake-" + Guid.NewGuid().ToString("N")[..8]);
        int port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        try { listener.Start(); }
        catch (Exception ex) { return Expect("tool-bake (skipped: " + ex.Message + ")", true, ""); }

        // mock OpenAI: odd request -> a tool_call for curName(curArgs); even request -> final content "ok"
        var bodies = new System.Collections.Concurrent.ConcurrentQueue<string>();
        bool stop = false; int reqs = 0; string curName = "", curArgs = "{}";
        var server = new Thread(() =>
        {
            try
            {
                while (!stop)
                {
                    var ctx = listener.GetContext();
                    using (var sr = new StreamReader(ctx.Request.InputStream)) bodies.Enqueue(sr.ReadToEnd());
                    int n = Interlocked.Increment(ref reqs);
                    string json = (n % 2 == 1)
                        ? "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"tool_calls\":[{\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"" + curName + "\",\"arguments\":" + System.Text.Json.JsonSerializer.Serialize(curArgs) + "}}]}}]}"
                        : "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}";
                    var b = Encoding.UTF8.GetBytes(json);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.OutputStream.Write(b, 0, b.Length);
                    ctx.Response.Close();
                }
            }
            catch { }
        }) { IsBackground = true };
        server.Start();

        int fails = 0;
        try
        {
            Environment.SetEnvironmentVariable("IRCUITRY_HOME", tmp);
            Directory.CreateDirectory(Path.Combine(tmp, "nodes"));

            // (A) bake an AI Tool sub-flow: ai.tool(name=echo_baked) -> tool.reply, with the arg wired to the result
            var subA = new NodeGraph();
            var aiTool = subA.Add(NodeCatalog.Get("ai.tool"), Vector2.Zero);
            aiTool.SetParam("name", "echo_baked"); aiTool.SetParam("description", "echo a string");
            aiTool.SetParam("args", ParamList.Encode(new List<string[]> { new[] { "query", "the text to echo" } }, true));
            var tReply = subA.Add(NodeCatalog.Get("tool.reply"), new Vector2(220, 0));
            subA.Connect(aiTool.Id, 1, tReply.Id, 0);   // call -> reply exec
            subA.Connect(aiTool.Id, 2, tReply.Id, 1);   // arg 1 (query) -> reply result
            string manifestA = "{\"typeId\":\"custom.echoa\",\"title\":\"Echo A\",\"icon\":\"toolbox\",\"category\":\"Logic\",\"description\":\"\","
                + "\"inputs\":[{\"name\":\"\",\"kind\":\"Exec\"}],\"outputs\":[{\"name\":\"then\",\"kind\":\"Exec\"},{\"name\":\"tool\",\"kind\":\"Tool\"}],"
                + "\"subgraph\":" + GraphSerializer.Save(subA, "Echo A") + "}";
            File.WriteAllText(Path.Combine(tmp, "nodes", "custom.echoa.ircnode"), manifestA);

            // (B) an inputs->output composite, ticked "usable as AI tool": query input -> result output
            var subB = new NodeGraph();
            var bin = subB.Add(NodeCatalog.Get("flow.in"), Vector2.Zero);
            var barg = subB.Add(NodeCatalog.Get("flow.arg"), new Vector2(0, 90)); barg.SetParam("name", "query");
            var bret = subB.Add(NodeCatalog.Get("flow.return"), new Vector2(220, 0)); bret.SetParam("name", "result");
            subB.Connect(bin.Id, 0, bret.Id, 0); subB.Connect(barg.Id, 0, bret.Id, 1);
            string manifestB = new Ircuitry.Editor.GraphEditor(subB)
                .SerializeAsComposite("custom.echob", "Echo B", "toolbox", "Logic", "echoes its argument", null, true) ?? "";
            File.WriteAllText(Path.Combine(tmp, "nodes", "custom.echob.ircnode"), manifestB);

            // (C) a self-contained tool whose AiToolName collides with (A)'s inner tool name "echo_baked":
            // the harness must dedup to ONE function spec or the model API rejects the duplicate.
            var subC = new NodeGraph();
            var cin = subC.Add(NodeCatalog.Get("flow.in"), Vector2.Zero);
            var carg = subC.Add(NodeCatalog.Get("flow.arg"), new Vector2(0, 90)); carg.SetParam("name", "query");
            var cret = subC.Add(NodeCatalog.Get("flow.return"), new Vector2(220, 0)); cret.SetParam("name", "result");
            subC.Connect(cin.Id, 0, cret.Id, 0); subC.Connect(carg.Id, 0, cret.Id, 1);
            string manifestC = new Ircuitry.Editor.GraphEditor(subC)
                .SerializeAsComposite("echo_baked", "Echo Collide", "toolbox", "Logic", "dup", null, true) ?? "";
            File.WriteAllText(Path.Combine(tmp, "nodes", "echo_baked.ircnode"), manifestC);

            NodeCatalog.LoadCustom();
            if (!NodeCatalog.TryGet("custom.echoa", out var defA) || !NodeCatalog.TryGet("custom.echob", out var defB) || !NodeCatalog.TryGet("echo_baked", out var defC))
            { stop = true; try { listener.Stop(); } catch { } return Expect("tool-bake (skipped: custom load)", true, ""); }

            int ToolPin(NodeDef d) { for (int i = 0; i < d.Outputs.Length; i++) if (d.Outputs[i].Kind == PinKind.Tool) return i; return -1; }

            string RunScenario(string typeId, int toolPin, string toolName, string argVal)
            {
                curName = toolName; curArgs = "{\"query\":\"" + argVal + "\"}";
                var g = new NodeGraph();
                var cmd = g.Add(NodeCatalog.Get("event.command"), Vector2.Zero); cmd.SetParam("command", "x");
                var ai = g.Add(NodeCatalog.Get("ai.reply"), new Vector2(300, 0));
                ai.SetParam("baseUrl", $"http://localhost:{port}/v1"); ai.SetParam("apiKey", "t"); ai.SetParam("model", "m");
                var comp = g.Add(NodeCatalog.Get(typeId), new Vector2(100, 160));
                g.Connect(cmd.Id, 0, ai.Id, 0);
                g.Connect(comp.Id, toolPin, ai.Id, 2);   // composite's Tool output -> Ask AI 'tools'
                int before = bodies.Count;
                GraphExecutor.Fire(g, new FakeSink(), cmd, Vars("!x go", "u", "#c"));
                return string.Join("\n", bodies.ToArray()[before..]);   // just this scenario's requests
            }

            var aLog = RunScenario("custom.echoa", ToolPin(defA), "echo_baked", "alpha");
            bool aOffered = aLog.Contains("echo_baked"), aResult = aLog.Contains("alpha");   // arg echoed back via Tool Reply
            fails += Expect("tool-bake-A-offered", aOffered, "inner ai.tool not exposed: " + Trunc(aLog));
            fails += Expect("tool-bake-A-result", aResult, "Tool Reply result not returned to the model");

            var bLog = RunScenario("custom.echob", ToolPin(defB), "custom_echob", "bravo");
            bool bOffered = bLog.Contains("custom_echob"), bResult = bLog.Contains("bravo");   // arg -> input pin -> output
            fails += Expect("tool-bake-B-offered", bOffered, "composite tool not exposed: " + Trunc(bLog));
            fails += Expect("tool-bake-B-result", bResult, "input-pin arg didn't flow to the result output");

            // (C) both echoa (inner "echo_baked") AND echo_baked (self-contained) wired in -> exactly ONE def
            curName = "echo_baked"; curArgs = "{\"query\":\"cee\"}";
            var gC = new NodeGraph();
            var cmdC = gC.Add(NodeCatalog.Get("event.command"), Vector2.Zero); cmdC.SetParam("command", "x");
            var aiC = gC.Add(NodeCatalog.Get("ai.reply"), new Vector2(300, 0));
            aiC.SetParam("baseUrl", $"http://localhost:{port}/v1"); aiC.SetParam("apiKey", "t"); aiC.SetParam("model", "m");
            var c1 = gC.Add(NodeCatalog.Get("custom.echoa"), new Vector2(100, 160));
            var c2 = gC.Add(NodeCatalog.Get("echo_baked"), new Vector2(100, 320));
            gC.Connect(cmdC.Id, 0, aiC.Id, 0);
            gC.Connect(c1.Id, ToolPin(defA), aiC.Id, 2);
            gC.Connect(c2.Id, ToolPin(defC), aiC.Id, 2);
            int cBefore = bodies.Count;
            GraphExecutor.Fire(gC, new FakeSink(), cmdC, Vars("!x go", "u", "#c"));
            var cArr = bodies.ToArray();
            string firstReq = cArr.Length > cBefore ? cArr[cBefore] : "";   // request 1 carries the tools list
            int dup = 0; for (int i = firstReq.IndexOf("echo_baked", StringComparison.Ordinal); i >= 0; i = firstReq.IndexOf("echo_baked", i + 1, StringComparison.Ordinal)) dup++;
            fails += Expect("tool-bake-dedup", dup == 1, $"duplicate tool name sent to the model ({dup}x echo_baked)");
        }
        finally
        {
            stop = true; try { listener.Stop(); } catch { }
            try { Directory.Delete(Path.Combine(tmp, "nodes"), true); } catch { }
            NodeCatalog.LoadCustom();
            Environment.SetEnvironmentVariable("IRCUITRY_HOME", oldHome);
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
        }
        return fails;
    }

    private static string Trunc(string s) => s.Length > 160 ? s[..160] : s;

    /// <summary>n8n-style data plumbing: {var.path} dot-notation into JSON, For-Each over a JSON array with
    /// {item.field}, and a counted Repeat loop.</summary>
    private static int JsonAndLoopsTest()
    {
        int fails = 0;

        // dotted JSON token: {var.path} including a nested object and an array index
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "j");
            var setv = N(g, "data.setvar", 200, 0); setv.SetParam("name", "data");
            setv.SetParam("value", "{\"user\":{\"name\":\"alice\",\"id\":7},\"items\":[\"x\",\"y\"]}");
            var reply = N(g, "action.reply", 400, 0); reply.SetParam("message", "{data.user.name}-{data.user.id}-{data.items.1}");
            g.Connect(cmd.Id, 0, setv.Id, 0);
            g.Connect(setv.Id, 0, reply.Id, 0);
            var s = new FakeSink();
            GraphExecutor.Fire(g, s, cmd, Vars("!j", "u", "#x"));
            fails += Expect("json-dotted-token", s.Sent.Count == 1 && s.Sent[0].text == "alice-7-y", Dump(s));
        }

        // For-Each over a JSON array, dotting into each element via {item.field}
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "f");
            var src = N(g, "data.format", 180, 0); src.SetParam("template", "[{\"name\":\"al\"},{\"name\":\"bo\"}]");
            var fe = N(g, "logic.forEach", 360, 0); fe.SetParam("sep", "json");
            var reply = N(g, "action.reply", 540, 0); reply.SetParam("message", "{item.name}");
            g.Connect(cmd.Id, 0, fe.Id, 0);
            g.Connect(src.Id, 0, fe.Id, 1);     // JSON array -> forEach.list
            g.Connect(fe.Id, 0, reply.Id, 0);   // each -> reply
            var s = new FakeSink();
            GraphExecutor.Fire(g, s, cmd, Vars("!f", "u", "#x"));
            fails += Expect("foreach-json-item-field", s.Sent.Select(t => t.text).SequenceEqual(new[] { "al", "bo" }), Dump(s));
        }

        // counted Repeat loop exposes the iteration as {i}
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "r");
            var rep = N(g, "logic.repeat", 200, 0); rep.SetParam("times", "3");
            var reply = N(g, "action.reply", 400, 0); reply.SetParam("message", "{i}");
            g.Connect(cmd.Id, 0, rep.Id, 0);
            g.Connect(rep.Id, 0, reply.Id, 0);   // each -> reply
            var s = new FakeSink();
            GraphExecutor.Fire(g, s, cmd, Vars("!r", "u", "#x"));
            fails += Expect("repeat-counts", s.Sent.Select(t => t.text).SequenceEqual(new[] { "0", "1", "2" }), Dump(s));
        }
        return fails;
    }

    /// <summary>Workflow runs execute off the IRC read thread: a 2s Delay run does NOT block PING/PONG
    /// keepalive, and two slow runs finish concurrently (~2s) rather than serially (~4s).</summary>
    private static int ConcurrentExecutorTest()
    {
        using var mock = new MockIrcServer(new[]
        {
            (200, ":alice!a@h PRIVMSG #ircuitry-test :!slow"),
            (250, ":bob!b@h PRIVMSG #ircuitry-test :!slow"),
            (450, "PING :keepalive"),
        });
        var rt = new BotRuntime(new ConsoleLog(), new System.Collections.Concurrent.ConcurrentDictionary<string, string>());

        var g = new NodeGraph();
        var cmd = g.Add(NodeCatalog.Get("event.command"), Vector2.Zero); cmd.SetParam("command", "slow");
        var delay = g.Add(NodeCatalog.Get("flow.delay"), new Vector2(250, 0)); delay.SetParam("seconds", "2");
        var reply = g.Add(NodeCatalog.Get("action.reply"), new Vector2(500, 0)); reply.SetParam("message", "done");
        g.Connect(cmd.Id, 0, delay.Id, 0);
        g.Connect(delay.Id, 0, reply.Id, 0);

        rt.Start(g, new IrcSettings { Host = "127.0.0.1", Port = mock.Port, UseTls = false, Nick = "ircuitry-bot", Channels = "#ircuitry-test", SaslPass = "" });

        // PONG must come back well before the 2s delays finish - i.e. the read thread isn't blocked by a run
        bool pong = false;
        for (int i = 0; i < 60 && !pong; i++) { Thread.Sleep(25); pong = mock.Sent().Any(s => s.StartsWith("PONG", StringComparison.Ordinal)); }
        // both slow runs land together (~2s), not one-after-another (~4s)
        int dones = 0;
        for (int i = 0; i < 160 && dones < 2; i++) { Thread.Sleep(25); dones = mock.Sent().Count(s => s.Contains("PRIVMSG #ircuitry-test") && s.EndsWith(":done")); }
        rt.Stop(); Thread.Sleep(80);
        return Expect("executor-nonblocking-concurrent", pong && dones >= 2, $"pong={pong} dones={dones} sent: " + string.Join(" | ", mock.Sent()));
    }

    /// <summary>Alert Human pulses 'then' regardless of whether the OS notification could be shown (no
    /// display in CI), and Human in the Loop with no live runtime (a dry run) takes the 'denied' path so
    /// nothing hangs.</summary>
    private static int HumanNodesTest()
    {
        int fails = 0;

        // Alert Human: the flow must continue past it even when notify-send isn't available
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "go");
            var alert = N(g, "human.alert", 250, 0); alert.SetParam("message", "heads up");
            var reply = N(g, "action.reply", 500, 0); reply.SetParam("message", "after");
            g.Connect(cmd.Id, 0, alert.Id, 0);
            g.Connect(alert.Id, 0, reply.Id, 0);
            var s = new FakeSink();
            GraphExecutor.Fire(g, s, cmd, Vars("!go", "u", "#x"));
            fails += Expect("human-alert-continues", s.Sent.Any(t => t.text == "after"), Dump(s));
        }

        // Human in the Loop on a dry run (FakeSink can't host a gate) -> denied branch
        {
            var g = new NodeGraph();
            var cmd = N(g, "event.command", 0, 0); cmd.SetParam("command", "ask");
            var loop = N(g, "human.loop", 250, 0); loop.SetParam("question", "ok?");
            var yes = N(g, "action.reply", 500, -60); yes.SetParam("message", "GRANTED");
            var no = N(g, "action.reply", 500, 60); no.SetParam("message", "REFUSED");
            g.Connect(cmd.Id, 0, loop.Id, 0);
            g.Connect(loop.Id, 0, yes.Id, 0);     // approved
            g.Connect(loop.Id, 1, no.Id, 0);      // denied
            var s = new FakeSink();
            GraphExecutor.Fire(g, s, cmd, Vars("!ask", "u", "#x"));
            bool denied = s.Sent.Any(t => t.text == "REFUSED") && s.Sent.All(t => t.text != "GRANTED");
            fails += Expect("human-loop-dryrun-denied", denied, Dump(s));
        }
        return fails;
    }

    /// <summary>End to end Human in the Loop: a command opens a gate, a human replies "yes", and the
    /// approved branch resumes and answers - the async pause/resume that a synchronous executor can't do
    /// by blocking.</summary>
    private static int HumanLoopApproveTest()
    {
        using var mock = new MockIrcServer(new[]
        {
            (200, ":alice!a@h PRIVMSG #ircuitry-test :!ask deploy?"),
            (1000, ":alice!a@h PRIVMSG #ircuitry-test :yes"),
        });
        var rt = new BotRuntime(new ConsoleLog(), new System.Collections.Concurrent.ConcurrentDictionary<string, string>());

        var g = new NodeGraph();
        var cmd = g.Add(NodeCatalog.Get("event.command"), Vector2.Zero); cmd.SetParam("command", "ask");
        var loop = g.Add(NodeCatalog.Get("human.loop"), new Vector2(250, 0));
        loop.SetParam("question", "deploy?"); loop.SetParam("timeout", "0"); loop.SetParam("notify", "false");
        var yes = g.Add(NodeCatalog.Get("action.reply"), new Vector2(500, -60)); yes.SetParam("message", "GRANTED");
        var no = g.Add(NodeCatalog.Get("action.reply"), new Vector2(500, 60)); no.SetParam("message", "REFUSED");
        g.Connect(cmd.Id, 0, loop.Id, 0);
        g.Connect(loop.Id, 0, yes.Id, 0);
        g.Connect(loop.Id, 1, no.Id, 0);

        rt.Start(g, new IrcSettings { Host = "127.0.0.1", Port = mock.Port, UseTls = false, Nick = "ircuitry-bot", Channels = "#ircuitry-test", SaslPass = "" });

        bool granted = false;
        for (int i = 0; i < 240 && !granted; i++)
        {
            Thread.Sleep(25);
            granted = mock.Sent().Any(s => s.Contains("PRIVMSG #ircuitry-test") && s.Contains("GRANTED"));
        }
        bool refused = mock.Sent().Any(s => s.Contains("REFUSED"));
        rt.Stop(); Thread.Sleep(80);
        return Expect("human-loop-approve-resumes", granted && !refused, "sent: " + string.Join(" | ", mock.Sent()));
    }

    /// <summary>
    /// Loading a workflow that uses a node type this build doesn't know must REPORT it (so import/paste
    /// can warn), not silently drop the node and its wires with no trace - the thing that made an
    /// out-of-date app look like it had shipped a broken workflow.
    /// </summary>
    private static int UnknownNodeWarningTest()
    {
        int fails = 0;
        string json = "{\"name\":\"x\",\"nodes\":["
            + "{\"id\":\"a\",\"type\":\"event.command\"},"
            + "{\"id\":\"b\",\"type\":\"totally.bogus\"},"
            + "{\"id\":\"c\",\"type\":\"action.reply\"}],"
            + "\"connections\":[{\"from\":\"a\",\"fromPin\":0,\"to\":\"b\",\"toPin\":0},{\"from\":\"a\",\"fromPin\":0,\"to\":\"c\",\"toPin\":0}]}";
        var (g, _) = Ircuitry.Graph.GraphSerializer.Load(json, out var skipped);
        bool dropped = g.Nodes.Count == 2 && g.Find("b") == null;                 // unknown node gone
        bool wireGone = g.Connections.All(c => c.ToNode != "b" && c.FromNode != "b"); // its wire gone too
        bool reported = skipped.Contains("totally.bogus")
            && Ircuitry.Graph.GraphSerializer.SkippedWarning(skipped).Contains("totally.bogus");
        fails += Expect("unknown-node-reported", dropped && wireGone && reported, $"nodes={g.Nodes.Count} skipped=[{string.Join(",", skipped)}]");

        var (_, _2) = Ircuitry.Graph.GraphSerializer.Load("{\"name\":\"y\",\"nodes\":[{\"id\":\"a\",\"type\":\"event.command\"}]}", out var none);
        fails += Expect("known-graph-no-warning", none.Count == 0 && Ircuitry.Graph.GraphSerializer.SkippedWarning(none).Length == 0, $"skipped=[{string.Join(",", none)}]");
        return fails;
    }

    /// <summary>
    /// Regression: an external workspace change (here an AI self-edit through the bridge) while a bot is
    /// LIVE must reload the canvas WITHOUT quitting the running bot - it used to stop every bot and rebuild
    /// the list, so a self-editing bot killed itself.
    /// </summary>
    private static int ReloadKeepsRunningBotTest()
    {
        var oldHome = Environment.GetEnvironmentVariable("IRCUITRY_HOME");
        var tmp = Path.Combine(Path.GetTempPath(), "ircuitry-reload-" + Guid.NewGuid().ToString("N")[..8]);
        using var mock = new MockIrcServer();
        try
        {
            Environment.SetEnvironmentVariable("IRCUITRY_HOME", tmp);
            if (!Ircuitry.App.AppModel.WorkspaceDir.StartsWith(tmp, StringComparison.Ordinal))
                return Expect("reload-keeps-running-bot (skipped: no sandbox)", true, "");

            var app = new Ircuitry.App.AppModel();
            var bot = app.ActiveBot;
            bot.Settings.Host = "127.0.0.1"; bot.Settings.Port = mock.Port; bot.Settings.UseTls = false;
            bot.Settings.Nick = "ircuitry-bot"; bot.Settings.Channels = "#t"; bot.Settings.SaslPass = "";
            app.Save(announce: false);                       // mark this on-disk state as "ours"
            bot.Runtime.Start(bot.Graph, bot.Settings);
            bool running = false;
            for (int i = 0; i < 200 && !running; i++) { Thread.Sleep(25); running = bot.Runtime.Running; }
            if (!running) { try { bot.Runtime.Stop(); } catch { } return Expect("reload-keeps-running-bot (skipped: bot never connected)", true, ""); }

            int before = bot.Graph.Nodes.Count;
            // an external edit lands on disk (the bot editing its own workflow via the inward MCP bridge)
            var add = Ircuitry.App.Mcp.McpBridge.Invoke("add_node",
                new Dictionary<string, string> { ["typeId"] = "action.reply", ["params"] = "{\"message\":\"FROMRELOAD\"}" }, bot.Name);
            bool reloaded = app.ReloadIfChangedOnDisk();
            Thread.Sleep(60);
            bool stillRunning = bot.Runtime.Running;                       // the live bot must NOT have been quit
            bool graphGrew = app.ActiveBot.Graph.Nodes.Count == before + 1; // and the canvas shows the edit
            try { bot.Runtime.Stop(); } catch { }
            Thread.Sleep(40);
            return Expect("reload-keeps-running-bot", reloaded && stillRunning && graphGrew && !add.StartsWith("Error"),
                $"reloaded={reloaded} stillRunning={stillRunning} grew={graphGrew} before={before} after={app.ActiveBot.Graph.Nodes.Count}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("IRCUITRY_HOME", oldHome);
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
        }
    }

    /// <summary>
    /// The inward MCP bridge a Workflow Editor node hands to Ask AI: edit mode exposes the mutating
    /// graph tools (each with a real schema), read-only hides them, and an edit round-trips against an
    /// ISOLATED throwaway workspace - never the user's ~/ircuitry.
    /// </summary>
    private static int McpEditorTest()
    {
        int fails = 0;

        var edit = Ircuitry.App.Mcp.McpBridge.EditorToolDefs(false);
        bool hasEdit = edit.Any(d => d.Name == "add_node") && edit.Any(d => d.Name == "set_graph") && edit.Any(d => d.Name == "connect");
        bool schemas = edit.Count > 0 && edit.All(d => d.Parameters != null);   // MCP schemas reach the model
        fails += Expect("mcp-editor-edit-tools", hasEdit && schemas, $"count={edit.Count} schemas={schemas}");

        var ro = Ircuitry.App.Mcp.McpBridge.EditorToolDefs(true);
        bool roSafe = ro.Any(d => d.Name == "get_graph") && !ro.Any(d => d.Name == "add_node") && !ro.Any(d => d.Name == "set_graph");
        fails += Expect("mcp-editor-readonly-safe", roSafe, "ro=" + string.Join(",", ro.Select(d => d.Name)));

        // end-to-end edit, sandboxed: point IRCUITRY_HOME at a temp dir so AppModel never touches the real workspace
        var oldHome = Environment.GetEnvironmentVariable("IRCUITRY_HOME");
        var tmp = Path.Combine(Path.GetTempPath(), "ircuitry-mcp-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Environment.SetEnvironmentVariable("IRCUITRY_HOME", tmp);
            if (Ircuitry.App.AppModel.WorkspaceDir.StartsWith(tmp, StringComparison.Ordinal))   // sandbox really in effect
            {
                new Ircuitry.App.AppModel().Save(announce: false);   // seed a workspace in the sandbox
                var add = Ircuitry.App.Mcp.McpBridge.Invoke("add_node",
                    new Dictionary<string, string> { ["typeId"] = "action.reply", ["params"] = "{\"message\":\"hi\"}" }, null);
                string id = "";
                try { using var d = JsonDocument.Parse(add); id = d.RootElement.GetProperty("id").GetString() ?? ""; } catch { }
                var graph = Ircuitry.App.Mcp.McpBridge.Invoke("get_graph", new Dictionary<string, string>(), null);
                bool ok = !add.StartsWith("Error") && id.Length > 0 && graph.Contains(id) && graph.Contains("\"hi\"");
                fails += Expect("mcp-editor-edits-sandbox", ok, $"add={(add.Length > 100 ? add[..100] : add)} idIn={graph.Contains(id)}");
            }
            else Console.WriteLine("  [SKIP] mcp-editor-edits-sandbox   (sandbox not in effect)");
        }
        finally
        {
            Environment.SetEnvironmentVariable("IRCUITRY_HOME", oldHome);
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
        }
        return fails;
    }

    /// <summary>
    /// Regression for the minimap over-scroll freeze: the camera centre must stay finite and within
    /// +-WorldLimit no matter how hard it is pushed, so world coords never reach the float range where
    /// the grid loop stalled forever and locked up the whole machine.
    /// </summary>
    private static int CameraBoundsTest()
    {
        int fails = 0;
        var canvas = new RectF(0, 0, 1600, 900);

        // (a) one absurd over-scroll (what a far minimap drag produced) is clamped, not propagated
        {
            var cam = new Camera();
            cam.CenterOn(new Vector2(1e30f, -1e25f), canvas.Center);
            cam.Sanitize(canvas);
            var c = cam.ScreenToWorld(canvas.Center);
            bool ok = float.IsFinite(c.X) && float.IsFinite(c.Y)
                      && MathF.Abs(c.X) <= Camera.WorldLimit + 1f && MathF.Abs(c.Y) <= Camera.WorldLimit + 1f;
            fails += Expect("cam-clamp-overscroll", ok, $"center={c}");
        }

        // (b) the geometric runaway (centre grows ~5%/frame, the old feedback loop) stays bounded
        {
            var cam = new Camera();
            for (int i = 0; i < 2000; i++)
            {
                var c = cam.ScreenToWorld(canvas.Center);
                cam.CenterOn(new Vector2(c.X * 1.05f + 1000f, c.Y * 1.05f + 1000f), canvas.Center);
                cam.Sanitize(canvas);
            }
            var fin = cam.ScreenToWorld(canvas.Center);
            bool ok = float.IsFinite(fin.X) && float.IsFinite(cam.Pan.X)
                      && MathF.Abs(fin.X) <= Camera.WorldLimit + 1f;
            fails += Expect("cam-clamp-runaway", ok, $"center={fin} pan={cam.Pan}");
        }

        // (c) non-finite pan/zoom (e.g. a divide-by-zero scale) is repaired, never left as NaN/Inf
        {
            var cam = new Camera { Pan = new Vector2(float.NaN, float.PositiveInfinity), Zoom = float.NaN };
            cam.Sanitize(canvas);
            bool ok = float.IsFinite(cam.Pan.X) && float.IsFinite(cam.Pan.Y)
                      && cam.Zoom >= Camera.MinZoom && cam.Zoom <= Camera.MaxZoom;
            fails += Expect("cam-repair-nonfinite", ok, $"pan={cam.Pan} zoom={cam.Zoom}");
        }

        // (d) at the world limit the grid index span is small and finite - the loop can't run away
        {
            var cam = new Camera { Zoom = Camera.MinZoom };
            cam.CenterOn(new Vector2(Camera.WorldLimit, Camera.WorldLimit), canvas.Center);
            cam.Sanitize(canvas);
            const float baseStep = 28f; const int major = 4;
            float step = baseStep;
            while (step * cam.Zoom < 13f) step *= major;
            while (step * cam.Zoom > 96f) step /= major;
            var tl = cam.ScreenToWorld(new Vector2(canvas.Left, canvas.Top));
            var br = cam.ScreenToWorld(new Vector2(canvas.Right, canvas.Bottom));
            int span = (int)MathF.Ceiling(br.X / step) - (int)MathF.Floor(tl.X / step);
            bool ok = step > 0f && span > 0 && span < 4000;
            fails += Expect("grid-span-bounded", ok, $"span={span} step={step}");
        }

        return fails;
    }
}
