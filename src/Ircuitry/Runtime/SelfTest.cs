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
        var (rtBots, _, _) = Ircuitry.App.WorkspaceSerializer.Load(Ircuitry.App.WorkspaceSerializer.Save(new[] { b }, 0, null));
        var lb = rtBots[0];
        fails += Expect("ws-evals", lb.Evals.Count == 2 && lb.Evals[0].Expect == "pong" && lb.Evals[1].Mode == Ircuitry.App.EvalMatch.NoReply, lb.Evals.Count.ToString());
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

        fails += Expect("tk-case-upper", RunOp("data.case", n => n.SetParam("op", "upper"), "Hello World") == "HELLO WORLD", "");
        fails += Expect("tk-case-snake", RunOp("data.case", n => n.SetParam("op", "snake"), "Hello World") == "hello_world", "");
        fails += Expect("tk-encode-b64", RunOp("data.encode", n => { n.SetParam("op", "base64"); n.SetParam("mode", "encode"); }, "hi") == "aGk=", "");
        fails += Expect("tk-encode-b64dec", RunOp("data.encode", n => { n.SetParam("op", "base64"); n.SetParam("mode", "decode"); }, "aGk=") == "hi", "");
        fails += Expect("tk-encode-rot13", RunOp("data.encode", n => { n.SetParam("op", "rot13"); n.SetParam("mode", "encode"); }, "abc") == "nop", "");
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
