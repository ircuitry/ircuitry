using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Ircuitry.Core;
using Ircuitry.Graph;
using Ircuitry.Irc;

namespace Ircuitry.Runtime;

/// <summary>
/// Drives a live bot: owns the IRC client, maps incoming events to trigger
/// fires, and fulfils node effects. The running graph is a frozen snapshot taken
/// at Start, so editing on the canvas never races the executor (apply = re-run).
/// </summary>
public sealed class BotRuntime : IRuntimeSink
{
    private readonly IrcClient _client = new();
    private readonly ConsoleLog _log;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _state;
    private volatile NodeGraph? _runGraph;   // frozen snapshot; read on IRC + timer threads
    private IrcSettings _cfg = new();
    private System.Threading.Thread? _timer;
    private int _workflowSeq;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _typing = new();  // target → last +typing=active

    public ConsoleLog Logs => _log;
    public IrcState State => _client.State;
    public string CurrentNick => _client.CurrentNick;
    private volatile bool _running;          // written on IRC thread, read on UI thread
    public bool Running => _running;
    public IReadOnlyList<string> EnabledCaps => _client.EnabledCaps;

    public int MessagesSeen { get; private set; }
    public int ActionsFired { get; private set; }

    private readonly ConcurrentDictionary<string, DateTime> _activity = new();
    private readonly ConcurrentDictionary<string, int> _fireCounts = new();

    private readonly LinkedList<RunRecord> _history = new();
    private readonly object _historyLock = new();
    public long HistoryRevision { get; private set; }

    /// <summary>0..1 glow intensity for a node that recently executed (fades over ~0.9s).</summary>
    public float FireGlow(string nodeId)
    {
        if (!_activity.TryGetValue(nodeId, out var t)) return 0f;
        const float life = 0.9f;
        float e = (float)(DateTime.Now - t).TotalSeconds;
        return e < 0 || e > life ? 0f : 1f - e / life;
    }

    public int FireCount(string nodeId) => _fireCounts.TryGetValue(nodeId, out var n) ? n : 0;

    /// <summary>Snapshot of recent runs (newest last). Bounded at 1000.</summary>
    public List<RunRecord> History() { lock (_historyLock) return new List<RunRecord>(_history); }
    public int HistoryCount { get { lock (_historyLock) return _history.Count; } }
    public void ClearHistory() { lock (_historyLock) { _history.Clear(); HistoryRevision++; } }

    private void AddHistory(RunRecord rec)
    {
        lock (_historyLock)
        {
            _history.AddLast(rec);
            while (_history.Count > 1000) _history.RemoveFirst();
            HistoryRevision++;
        }
    }

    private void FireNode(Node node, Dictionary<string, string> vars)
    {
        if (_runGraph == null) return;
        var rec = new RunRecord { Time = DateTime.Now, Trigger = node.DisplayTitle, Icon = node.Def.Icon, Summary = Summarize(node.Def.TriggerEvent, vars) };
        int before = ActionsFired;
        // stream bot-tools steps LIVE as each node executes (not as a replay afterwards)
        var stream = new WorkflowStream(this, vars, node.DisplayTitle);
        GraphExecutor.Fire(_runGraph, this, node, vars, rec, stream.OnNode);
        StopAllTyping();   // a workflow run ends → drop any typing it started but didn't stop
        rec.Actions = ActionsFired - before;
        rec.Fired = rec.Nodes.Count > 0 && rec.Nodes[0].Pulsed.Count > 0;
        if (rec.Fired) AddHistory(rec);
        stream.Finish();
    }

    private static string Summarize(string? family, Dictionary<string, string> v)
    {
        v.TryGetValue("nick", out var nick); v.TryGetValue("message", out var msg); v.TryGetValue("channel", out var ch);
        return family switch
        {
            "message" => (string.IsNullOrEmpty(nick) ? "" : nick + ": ") + msg,
            "join" => (nick ?? "") + " joined " + (ch ?? ""),
            "connect" => "registered",
            "timer" => "timer tick",
            "schedule" => "scheduled fire" + (v.TryGetValue("time", out var tm) ? " @ " + tm : ""),
            _ => family ?? "",
        };
    }

    public BotRuntime(ConsoleLog log, System.Collections.Concurrent.ConcurrentDictionary<string, string> state)
    {
        _log = log;
        _state = state;
        _client.RawIn = OnRawIn;
        _client.RawOut = line => _log.Add(LogLevel.Out, line);
        _client.Status = (msg, err) => _log.Add(err ? LogLevel.Error : LogLevel.System, msg);
        _client.Registered = OnRegistered;
        _client.Closed = reason => { _running = false; _log.Add(LogLevel.System, "● " + reason); };
        _client.Message = OnIrc;
    }

    public void Start(NodeGraph graph, IrcSettings cfg)
    {
        if (_running) return;
        _cfg = cfg.Clone();
        // resolve {{secret.x}} in credentials so they live in the vault, not the workspace/exports
        _cfg.ServerPass = Core.Secrets.Expand(_cfg.ServerPass);
        _cfg.SaslUser = Core.Secrets.Expand(_cfg.SaslUser);
        _cfg.SaslPass = Core.Secrets.Expand(_cfg.SaslPass);
        // freeze a snapshot so canvas edits don't race the executor
        _runGraph = GraphSerializer.Load(GraphSerializer.Save(graph, "run")).graph;
        MessagesSeen = ActionsFired = 0;
        _fireCounts.Clear();
        ClearHistory();
        _running = true;
        _log.Add(LogLevel.System, $"▶ starting bot - {CountTriggers()} trigger(s) armed");
        _client.Connect(cfg);
        StartTimers();
    }

    /// <summary>Swap in an edited graph on a LIVE bot without disconnecting (no restart needed).</summary>
    public void ApplyGraph(NodeGraph graph)
    {
        if (!_running) return;
        _runGraph = GraphSerializer.Load(GraphSerializer.Save(graph, "run")).graph;   // fresh frozen snapshot
        _log.Add(LogLevel.System, $"↻ applied workflow changes - {CountTriggers()} trigger(s) armed");
    }

    private void StartTimers()
    {
        // always run while live (cheap 500ms tick) so timers/schedules added via ApplyGraph take effect too
        _timer = new System.Threading.Thread(() =>
        {
            var last = new Dictionary<string, DateTime>();
            var firedOnce = new HashSet<string>();
            while (_running)
            {
                System.Threading.Thread.Sleep(500);
                if (State != IrcState.Connected || _runGraph == null) continue;
                var now = DateTime.Now;                  // local wall-clock - schedules are set in local time
                foreach (var n in _runGraph.Nodes)
                {
                    if (n.Def.TriggerEvent == "timer")
                    {
                        int interval = Math.Max(1, int.TryParse(n.GetParam("seconds"), out var s) ? s : 60);
                        if (!last.TryGetValue(n.Id, out var t)) { last[n.Id] = now; continue; } // wait one interval first
                        if ((now - t).TotalSeconds >= interval) { last[n.Id] = now; FireNode(n, BaseVars()); }
                    }
                    else if (n.Def.TriggerEvent == "schedule" && ScheduleDue(n, now, last, firedOnce))
                    {
                        FireNode(n, ScheduleVars(now));
                    }
                }
                // keep active typing indicators alive (servers expire +typing after ~6s)
                foreach (var kv in _typing)
                    if ((now - kv.Value).TotalSeconds >= 4) { _client.TagMsg(kv.Key, "+typing=active"); _typing[kv.Key] = now; }
            }
        })
        { IsBackground = true, Name = "bot-timer" };
        _timer.Start();
    }

    private Dictionary<string, string> ScheduleVars(DateTime now)
    {
        var v = BaseVars();
        v["time"] = now.ToString("HH:mm");
        v["date"] = now.ToString("yyyy-MM-dd");
        v["datetime"] = now.ToString("yyyy-MM-dd HH:mm:ss");
        v["weekday"] = now.DayOfWeek.ToString();
        return v;
    }

    /// <summary>Decides whether an "On Schedule" node should fire this tick (interval / daily / weekly / once).</summary>
    internal static bool ScheduleDue(Node n, DateTime now, Dictionary<string, DateTime> last, HashSet<string> firedOnce)
    {
        string mode = n.GetParam("mode");

        if (mode == "once")
        {
            if (firedOnce.Contains(n.Id)) return false;
            if (DateTime.TryParse(n.GetParam("datetime"), out var when) && now >= when) { firedOnce.Add(n.Id); return true; }
            return false;
        }

        // establish a baseline on first sight so a just-passed time doesn't fire on startup
        if (!last.TryGetValue(n.Id, out var prev)) { last[n.Id] = now; return false; }

        switch (mode)
        {
            case "daily":
            case "weekly":
            {
                if (!TryParseHm(n.GetParam("time"), out int hh, out int mm)) return false;
                if (mode == "weekly" && !DayAllowed(n.GetParam("days"), now.DayOfWeek)) return false;
                var target = new DateTime(now.Year, now.Month, now.Day, hh, mm, 0, now.Kind);
                if (now >= target && prev < target) { last[n.Id] = now; return true; }
                return false;
            }
            default: // interval
            {
                int every = Math.Max(1, int.TryParse(n.GetParam("every"), out var e) ? e : 1);
                double secs = every * n.GetParam("unit") switch { "minutes" => 60.0, "hours" => 3600.0, "days" => 86400.0, _ => 1.0 };
                if ((now - prev).TotalSeconds >= secs) { last[n.Id] = now; return true; }
                return false;
            }
        }
    }

    private static bool TryParseHm(string s, out int hh, out int mm)
    {
        hh = mm = 0;
        var parts = (s ?? "").Split(':');
        return parts.Length == 2
            && int.TryParse(parts[0].Trim(), out hh) && hh is >= 0 and <= 23
            && int.TryParse(parts[1].Trim(), out mm) && mm is >= 0 and <= 59;
    }

    private static readonly string[] DayNames = { "sun", "mon", "tue", "wed", "thu", "fri", "sat" };

    private static int IsoDay(DayOfWeek d) => d == DayOfWeek.Sunday ? 7 : (int)d;  // Mon=1 … Sun=7

    private static int DayToIso(string tok)
    {
        tok = tok.Trim().ToLowerInvariant();
        if (tok.Length == 0) return 0;
        if (int.TryParse(tok, out var num)) return num is >= 1 and <= 7 ? num : 0;
        int idx = Array.FindIndex(DayNames, d => tok.StartsWith(d));
        return idx < 0 ? 0 : (idx == 0 ? 7 : idx);   // Sun(idx0)→7, Mon(idx1)→1 …
    }

    private static bool DayAllowed(string spec, DayOfWeek day)
    {
        spec = (spec ?? "").Trim();
        if (spec.Length == 0 || spec == "*") return true;
        int iso = IsoDay(day);
        foreach (var raw in spec.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int dash = raw.IndexOf('-');
            if (dash > 0)
            {
                int a = DayToIso(raw[..dash]), b = DayToIso(raw[(dash + 1)..]);
                if (a == 0 || b == 0) continue;
                if (a <= b ? iso >= a && iso <= b : iso >= a || iso <= b) return true;  // wrap (e.g. Fri-Mon)
            }
            else if (DayToIso(raw) == iso) return true;
        }
        return false;
    }

    public void Stop()
    {
        if (!Running && State == IrcState.Disconnected) return;
        _log.Add(LogLevel.System, "■ stopping bot");
        _running = false;
        _client.Disconnect();
    }

    private int CountTriggers()
    {
        int n = 0;
        if (_runGraph != null) foreach (var node in _runGraph.Nodes) if (node.Def.IsTrigger) n++;
        return n;
    }

    // raw incoming: show protocol lines, but let chat lines be shown friendly (with tag badges) from OnIrc
    private void OnRawIn(string line)
    {
        if (line.IndexOf(" PRIVMSG ", StringComparison.Ordinal) >= 0 || line.IndexOf(" NOTICE ", StringComparison.Ordinal) >= 0) return;
        _log.Add(LogLevel.In, line);
    }

    // ===================================================================
    private void OnRegistered()
    {
        // IRCv3 bot-tools: flag ourselves as a bot (+B). Channel bots advertise commands
        // on demand (reply to +draft/bot-cmds-query), so nothing is pushed at connect.
        if (_cfg.BotMode && _client.HasCap("message-tags")) _client.SendRaw($"MODE {CurrentNick} +B");
        FireFamily("connect", BaseVars());
    }

    private void OnIrc(IrcMessage m)
    {
        if (!Running || _runGraph == null) return;

        if (m.Is("PRIVMSG"))
        {
            MessagesSeen++;
            string nick = m.Nick ?? "";
            if (nick.Equals(CurrentNick, StringComparison.OrdinalIgnoreCase)) return; // ignore self
            string target = m.P(0);
            string text = m.Trailing;
            bool toChannel = target.StartsWith('#') || target.StartsWith('&');
            var vars = BaseVars();
            vars["nick"] = nick;
            vars["user"] = m.User ?? "";
            vars["host"] = m.Host ?? "";
            vars["target"] = target;
            vars["channel"] = toChannel ? target : nick;
            vars["message"] = text;
            vars["replyto"] = toChannel ? target : nick;

            // IRCv3 message tags - exposed to nodes and surfaced as friendly badges
            string account = m.Tag("account");
            bool isbot = m.Tags.ContainsKey("bot") || m.Tags.ContainsKey("draft/bot");
            string msgid = m.Tag("msgid"); if (msgid.Length == 0) msgid = m.Tag("draft/msgid");
            vars["account"] = account;
            vars["isbot"] = isbot ? "true" : "false";
            vars["msgid"] = msgid;
            vars["__reply"] = msgid;                 // correlate replies to the triggering message (+reply)
            foreach (var kv in m.Tags) vars["tag." + kv.Key] = kv.Value;

            string badges = (account.Length > 0 ? "✓" + account + " " : "") + (isbot ? "🤖 " : "");
            _log.Add(LogLevel.In, $"{badges}<{nick}> {text}");

            FireFamily("message", vars);
        }
        else if (m.Is("TAGMSG"))
        {
            OnTagMsg(m);
        }
        else if (m.Is("JOIN"))
        {
            string nick = m.Nick ?? "";
            string channel = m.P(0).Length > 0 ? m.P(0) : m.Trailing;
            if (nick.Equals(CurrentNick, StringComparison.OrdinalIgnoreCase)) return; // our own join
            var vars = BaseVars();
            vars["nick"] = nick;
            vars["channel"] = channel;
            vars["target"] = channel;
            vars["replyto"] = channel;
            FireFamily("join", vars);
        }
    }

    // =====================================================================
    //  IRCv3 bot-tools spec: draft/bot-cmds + draft/bot-tools (carried in TAGMSG)
    // =====================================================================

    private void OnTagMsg(IrcMessage m)
    {
        if (!_client.HasCap("message-tags")) return;
        string nick = m.Nick ?? "";
        if (nick.Length == 0 || nick.Equals(CurrentNick, StringComparison.OrdinalIgnoreCase)) return;
        string target = m.P(0);

        // command discovery - reply privately to the asker with our command list
        if (_cfg.AdvertiseCommands && m.Tags.ContainsKey("+draft/bot-cmds-query"))
            AdvertiseCommandsTo(nick);

        // structured slash-command invocation
        if (_cfg.AdvertiseCommands && m.Tag("+draft/bot-cmd") is { Length: > 0 } cmdTag)
            HandleInvocation(m, nick, target, cmdTag);
    }

    private void AdvertiseCommandsTo(string nick)
    {
        if (_runGraph == null) return;
        var b64 = BotTools.BuildCommandList(_runGraph);
        if (!BotTools.Fits("+draft/bot-cmds=" + b64)) { _log.Add(LogLevel.System, "command list too large to advertise (batching not yet supported)"); return; }
        _client.TagMsg(nick, "+draft/bot-cmds=" + b64);
        ActionsFired++;
    }

    /// <summary>Tell channels our command set may have changed, so clients re-query (sent on connect).</summary>
    private void AdvertiseCommandsChanged()
    {
        foreach (var ch in _cfg.ChannelList) _client.TagMsg(ch, "+draft/bot-cmds-changed");
    }

    private void HandleInvocation(IrcMessage m, string nick, string target, string cmdTag)
    {
        var inv = BotTools.ParseInvocation(cmdTag);
        if (inv == null) return;   // malformed/untrusted - discard silently

        bool toChannel = target.StartsWith('#') || target.StartsWith('&');
        // public invocation to a channel can carry a `bot` field for disambiguation - ignore if it names someone else
        if (toChannel && inv.Bot.Length > 0 && !inv.Bot.Equals(CurrentNick, StringComparison.OrdinalIgnoreCase)) return;

        string msgid = m.Tag("msgid");

        // find the matching On Command node(s)
        var matches = _runGraph!.Nodes.FindAll(n => n.TypeId == "event.command"
            && n.GetParam("command").Trim().Equals(inv.Name, StringComparison.OrdinalIgnoreCase));
        if (matches.Count == 0) { ReportCmdError(nick, target, toChannel, msgid, inv.Channel, "INVALID_COMMAND", $"Unknown command: {inv.Name}"); return; }

        // context: public (channel target), pm (to us, no channel), or private (to us, names a channel)
        string ctx = toChannel ? "public" : (inv.Channel.Length > 0 ? "private" : "pm");
        string chan = toChannel ? target : inv.Channel;
        string replyto = toChannel ? target : nick;
        string args = string.Join(" ", inv.OptionValues);
        string prefix = matches[0].GetParam("prefix");

        // honour each command's advertised contexts (spec: reject an unadvertised context)
        var allowed = matches.FindAll(n => Array.IndexOf(BotTools.Contexts(n.GetParam("contexts")), ctx) >= 0);
        if (allowed.Count == 0) { ReportCmdError(nick, target, toChannel, msgid, inv.Channel, "BAD_CONTEXT", $"'{inv.Name}' can't be used here"); return; }

        foreach (var node in allowed)
        {
            var vars = BaseVars();
            vars["nick"] = nick;
            vars["user"] = m.User ?? "";
            vars["host"] = m.Host ?? "";
            vars["channel"] = chan;
            vars["target"] = target;
            vars["replyto"] = replyto;
            vars["command"] = inv.Name;
            vars["args"] = args;
            // re-synthesise the legacy text form so the node's own matcher/extractor runs unchanged
            vars["message"] = prefix + inv.Name + (args.Length > 0 ? " " + args : "");
            vars["msgid"] = msgid;
            vars["account"] = m.Tag("account");
            foreach (var kv in m.Tags) vars["tag." + kv.Key] = kv.Value;
            // reply-context for the spec's correlated reply
            vars["__reply"] = msgid;
            vars["__ctx"] = ctx;
            if (ctx == "public") vars["__invokedby"] = BotTools.EncodeInvokedBy(nick, inv.Name, inv);
            if (ctx == "private") vars["__chanctx"] = chan;
            FireNode(node, vars);
        }
    }

    private void ReportCmdError(string nick, string target, bool toChannel, string msgid, string invChannel, string code, string text)
    {
        var tags = new System.Text.StringBuilder();
        if (msgid.Length > 0) tags.Append("+reply=").Append(msgid).Append(';');
        tags.Append("+draft/bot-cmd-error=").Append(code);
        // public errors may go to the channel; private/pm whisper to the invoker (with channel-context for private)
        if (toChannel) { _client.NoticeTagged(target, text, tags.ToString()); }
        else
        {
            if (invChannel.Length > 0) tags.Append(";+draft/channel-context=").Append(invChannel);
            _client.NoticeTagged(nick, text, tags.ToString());
        }
        ActionsFired++;
    }

    // ---- bot-tools workflow streaming (push-only, LIVE as nodes execute) ----
    private static string ToolName(string typeId) => typeId switch
    {
        "net.http" => "http-request",
        "ai.reply" => "ai-generate",
        "ai.tool" => "tool",
        "db.sql" => "sql",
        "code.run" => "code",
        _ => typeId.Contains('.') ? typeId[(typeId.IndexOf('.') + 1)..] : typeId,
    };

    /// <summary>
    /// Streams a single fire's "stream as tool" nodes as a draft/bot-tools workflow, emitting each step
    /// the instant its node finishes - so the workflow appears live, interleaved with the bot's actions,
    /// rather than as a replay after the fire completes. One instance per fire (so it's thread-safe).
    /// </summary>
    private sealed class WorkflowStream
    {
        private readonly BotRuntime _b;
        private readonly Dictionary<string, string> _vars;
        private readonly string _name;
        private bool _started;
        private string _wid = "", _dest = "";
        private int _sid;

        public WorkflowStream(BotRuntime b, Dictionary<string, string> vars, string name) { _b = b; _vars = vars; _name = name; }

        public void OnNode(NodeTrace t)
        {
            if (!_b._cfg.StreamWorkflows || !_b._client.HasCap("message-tags")) return;
            var node = _b._runGraph?.Find(t.NodeId);
            if (node is not { StreamAsTool: true }) return;
            if (!EnsureStarted()) return;

            string sidc = "s" + (++_sid);
            var input = new Dictionary<string, object?>();
            bool trunc = false;
            void Put(string k, string v) { if (v.Length > 200) trunc = true; input[k] = Truncate(v, 200); }
            foreach (var (pin, val) in t.Inputs) Put(pin, val);
            foreach (var p in node.Def.Params)
                if (node.GetParam(p.Key).Length > 0 && p.Key != "apiKey") Put(p.Key, node.GetParam(p.Key));
            _b.EmitBotTools(_dest, BotTools.Step(_wid, sidc, "tool-call", "complete", ToolName(node.TypeId), input, label: t.Title, truncated: trunc));

            string result = t.Outputs.Count > 0 ? t.Outputs[0].value : "";
            if (result.Length > 0)
                _b.EmitBotTools(_dest, BotTools.Step(_wid, sidc + "r", "tool-result", "complete",
                    content: Truncate(result, 300), label: t.Title + " result", truncated: result.Length > 300));
        }

        private bool EnsureStarted()
        {
            if (_started) return _dest.Length > 0;
            _started = true;
            _dest = _vars.TryGetValue("channel", out var ch) && ch.Length > 0 ? ch
                  : _vars.TryGetValue("replyto", out var rt) ? rt : "";
            if (_dest.Length == 0) return false;
            _wid = "w" + (++_b._workflowSeq).ToString("x");
            string trigger = _vars.TryGetValue("msgid", out var mid) ? mid : "";
            _b.EmitBotTools(_dest, BotTools.Workflow(_wid, "start", _name, trigger, new[] { "reasoning" }));
            return true;
        }

        public void Finish()
        {
            if (_started && _dest.Length > 0) _b.EmitBotTools(_dest, BotTools.Workflow(_wid, "complete"));
        }
    }

    // send a bot-tools payload only if it fits the client-tag size limit (spec: verify before sending)
    private void EmitBotTools(string dest, string payloadB64)
    {
        var tag = "+draft/bot-tools=" + payloadB64;
        if (BotTools.Fits(tag)) _client.TagMsg(dest, tag);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private Dictionary<string, string> BaseVars() => new()
    {
        ["botnick"] = CurrentNick,
        ["nick"] = "", ["channel"] = "", ["message"] = "", ["args"] = "",
        ["command"] = "", ["target"] = "", ["replyto"] = "",
    };

    private void FireFamily(string family, Dictionary<string, string> vars)
    {
        if (_runGraph == null) return;
        foreach (var node in _runGraph.Nodes)
        {
            if (node.Def.TriggerEvent != family) continue;
            // each trigger gets its own var scope so On Command's args don't leak
            FireNode(node, new Dictionary<string, string>(vars));
        }
    }

    // ---- IRuntimeSink ----
    public void Privmsg(string target, string text) { _client.Privmsg(target, text); ActionsFired++; }
    public void Notice(string target, string text) { _client.Notice(target, text); ActionsFired++; }
    public void React(string target, string msgid, string emoji) { _client.React(target, msgid, emoji); ActionsFired++; }
    public void PrivmsgTagged(string target, string text, string tags) { _client.PrivmsgTagged(target, text, tags); ActionsFired++; }
    public void NoticeTagged(string target, string text, string tags) { _client.NoticeTagged(target, text, tags); ActionsFired++; }
    public void Join(string channel) => _client.Join(channel);
    public void Part(string channel, string reason) => _client.Part(channel, reason);
    public void Raw(string line) => _client.SendRaw(line);
    public void StartTyping(string target)
    {
        if (string.IsNullOrEmpty(target) || !_client.HasCap("message-tags")) return;
        _typing[target] = DateTime.Now;
        _client.TagMsg(target, "+typing=active");
    }
    public void StopTyping(string target)
    {
        if (_typing.TryRemove(target, out _) && _client.HasCap("message-tags"))
            _client.TagMsg(target, "+typing=done");
    }
    private void StopAllTyping() { foreach (var t in new System.Collections.Generic.List<string>(_typing.Keys)) StopTyping(t); }
    public void Log(string message, LogLevel level) => _log.Add(level, message);
    public void NodeFired(string nodeId) { _activity[nodeId] = DateTime.Now; _fireCounts.AddOrUpdate(nodeId, 1, (_, n) => n + 1); }

    /// <summary>The owning bot's name, kept in sync by <see cref="Ircuitry.App.Bot"/> for achievement crediting.</summary>
    public string OwnerName = "";
    public void RunCompleted(System.Collections.Generic.IReadOnlyCollection<string> executedTypes)
    {
        if (OwnerName.Length > 0) Ircuitry.Core.Achievements.MarkRun(executedTypes);
    }
    public string GetState(string key) => _state.TryGetValue(key, out var v) ? v : "";
    public void SetState(string key, string value) => _state[key] = value;
}
