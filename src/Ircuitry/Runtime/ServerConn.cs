using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Text;
using Ircuitry.Core;
using Ircuitry.Graph;
using Ircuitry.Irc;

namespace Ircuitry.Runtime;

/// <summary>
/// One live server connection for a bot: owns the IRC client, maps that server's incoming events to
/// trigger fires, and fulfils node effects on it. Acts as the per-fire <see cref="IRuntimeSink"/> so an
/// action's output goes back to the server the event arrived on (the "origin"), unless a node names
/// another server. Shared state (graph snapshot, run history, achievements, bot variables) lives on the
/// owning <see cref="BotRuntime"/>; this class is purely the per-server half.
/// </summary>
public sealed class ServerConn : IRuntimeSink
{
    private readonly BotRuntime _owner;
    private readonly IrcClient _client = new();
    private IrcSettings _cfg;
    private System.Threading.Thread? _timer;
    private volatile bool _running;
    private readonly ConcurrentDictionary<string, DateTime> _typing = new();   // target → last +typing=active

    public string Label { get; private set; }
    public bool Running => _running;
    public IrcState State => _client.State;
    public string CurrentNick => _client.CurrentNick;
    public IReadOnlyList<string> EnabledCaps => _client.EnabledCaps;
    public bool HasCap(string name) => _client.HasCap(name);
    public IrcSettings Config => _cfg;
    public int MessagesSeen { get; private set; }
    public int ActionsFired { get; private set; }

    public ServerConn(BotRuntime owner, IrcSettings cfg)
    {
        _owner = owner;
        _cfg = cfg.Clone();
        Label = _cfg.DisplayName;
        _client.RawIn = OnRawIn;
        _client.RawOut = line => _owner.LogFrom(Label, LogLevel.Out, line);
        _client.Status = (msg, err) => _owner.LogFrom(Label, err ? LogLevel.Error : LogLevel.System, msg);
        _client.Registered = OnRegistered;
        _client.Closed = reason => { _running = false; _owner.LogFrom(Label, LogLevel.System, "● " + reason); };
        _client.Message = OnIrc;
    }

    // matches a routing name (a node's "server" override) to this connection
    public bool Matches(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return false;
        return string.Equals(name, Label, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, _cfg.Label, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, _cfg.Host, StringComparison.OrdinalIgnoreCase);
    }

    public void Start()
    {
        if (_running) return;
        _cfg = _cfg.Clone();
        // resolve {{secret.x}} in credentials so they live in the vault, not the workspace/exports
        _cfg.ServerPass = Secrets.Expand(_cfg.ServerPass);
        _cfg.SaslUser = Secrets.Expand(_cfg.SaslUser);
        _cfg.SaslPass = Secrets.Expand(_cfg.SaslPass);
        MessagesSeen = ActionsFired = 0;
        _running = true;
        _owner.LogFrom(Label, LogLevel.System, $"▶ connecting - {_owner.CountTriggers()} trigger(s) armed");
        _client.Connect(_cfg);
        StartTimers();
    }

    public void Stop()
    {
        if (!_running && State == IrcState.Disconnected) return;
        _owner.LogFrom(Label, LogLevel.System, "■ disconnecting");
        _running = false;
        _client.Disconnect();
    }

    private void StartTimers()
    {
        // a 500ms tick so timers/schedules added via ApplyGraph take effect too; each server keeps its own
        // schedule baselines so a timed action fires once per connected server (origin-routed to each)
        _timer = new System.Threading.Thread(() =>
        {
            var last = new Dictionary<string, DateTime>();
            var firedOnce = new HashSet<string>();
            while (_running)
            {
                System.Threading.Thread.Sleep(500);
                var graph = _owner.RunGraph;
                if (State != IrcState.Connected || graph == null) continue;
                var now = DateTime.Now;                  // local wall-clock - schedules are set in local time
                foreach (var n in graph.Nodes)
                {
                    if (n.Def.TriggerEvent == "timer")
                    {
                        int interval = Math.Max(1, int.TryParse(n.GetParam("seconds"), out var s) ? s : 60);
                        if (!last.TryGetValue(n.Id, out var t)) { last[n.Id] = now; continue; } // wait one interval first
                        if ((now - t).TotalSeconds >= interval) { last[n.Id] = now; FireNode(n, BaseVars()); }
                    }
                    else if (n.Def.TriggerEvent == "schedule" && BotRuntime.ScheduleDue(n, now, last, firedOnce))
                    {
                        FireNode(n, ScheduleVars(now));
                    }
                }
                // keep active typing indicators alive (servers expire +typing after ~6s)
                foreach (var kv in _typing)
                    if ((now - kv.Value).TotalSeconds >= 4) { _client.TagMsg(kv.Key, "+typing=active"); _typing[kv.Key] = now; }
            }
        })
        { IsBackground = true, Name = "bot-timer:" + Label };
        _timer.Start();
    }

    // ===================================================================
    private void OnRawIn(string line)
    {
        if (line.IndexOf(" PRIVMSG ", StringComparison.Ordinal) >= 0 || line.IndexOf(" NOTICE ", StringComparison.Ordinal) >= 0) return;
        _owner.LogFrom(Label, LogLevel.In, line);
    }

    private void OnRegistered()
    {
        // IRCv3 bot-tools: flag ourselves as a bot (+B). Channel bots advertise commands on demand.
        if (_cfg.BotMode && _client.HasCap("message-tags")) _client.SendRaw($"MODE {CurrentNick} +B");
        FireFamily("connect", BaseVars());
    }

    private void OnIrc(IrcMessage m)
    {
        if (!_running || _owner.RunGraph == null) return;

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
            _owner.LogFrom(Label, LogLevel.In, $"{badges}<{nick}> {text}");

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

        if (_cfg.AdvertiseCommands && m.Tags.ContainsKey("+draft/bot-cmds-query"))
            AdvertiseCommandsTo(nick);

        if (_cfg.AdvertiseCommands && m.Tag("+draft/bot-cmd") is { Length: > 0 } cmdTag)
            HandleInvocation(m, nick, target, cmdTag);
    }

    private void AdvertiseCommandsTo(string nick)
    {
        var graph = _owner.RunGraph;
        if (graph == null) return;
        var b64 = BotTools.BuildCommandList(graph);
        if (!BotTools.Fits("+draft/bot-cmds=" + b64)) { _owner.LogFrom(Label, LogLevel.System, "command list too large to advertise (batching not yet supported)"); return; }
        _client.TagMsg(nick, "+draft/bot-cmds=" + b64);
        ActionsFired++;
    }

    private void HandleInvocation(IrcMessage m, string nick, string target, string cmdTag)
    {
        var graph = _owner.RunGraph;
        if (graph == null) return;
        var inv = BotTools.ParseInvocation(cmdTag);
        if (inv == null) return;   // malformed/untrusted - discard silently

        bool toChannel = target.StartsWith('#') || target.StartsWith('&');
        if (toChannel && inv.Bot.Length > 0 && !inv.Bot.Equals(CurrentNick, StringComparison.OrdinalIgnoreCase)) return;

        string msgid = m.Tag("msgid");

        var matches = graph.Nodes.FindAll(n => n.TypeId == "event.command"
            && n.GetParam("command").Trim().Equals(inv.Name, StringComparison.OrdinalIgnoreCase));
        if (matches.Count == 0) { ReportCmdError(nick, target, toChannel, msgid, inv.Channel, "INVALID_COMMAND", $"Unknown command: {inv.Name}"); return; }

        string ctx = toChannel ? "public" : (inv.Channel.Length > 0 ? "private" : "pm");
        string chan = toChannel ? target : inv.Channel;
        string replyto = toChannel ? target : nick;
        string args = string.Join(" ", inv.OptionValues);
        string prefix = matches[0].GetParam("prefix");

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
            vars["message"] = prefix + inv.Name + (args.Length > 0 ? " " + args : "");
            vars["msgid"] = msgid;
            vars["account"] = m.Tag("account");
            foreach (var kv in m.Tags) vars["tag." + kv.Key] = kv.Value;
            vars["__reply"] = msgid;
            vars["__ctx"] = ctx;
            if (ctx == "public") vars["__invokedby"] = BotTools.EncodeInvokedBy(nick, inv.Name, inv);
            if (ctx == "private") vars["__chanctx"] = chan;
            FireNode(node, vars);
        }
    }

    private void ReportCmdError(string nick, string target, bool toChannel, string msgid, string invChannel, string code, string text)
    {
        var tags = new StringBuilder();
        if (msgid.Length > 0) tags.Append("+reply=").Append(msgid).Append(';');
        tags.Append("+draft/bot-cmd-error=").Append(code);
        if (toChannel) { _client.NoticeTagged(target, text, tags.ToString()); }
        else
        {
            if (invChannel.Length > 0) tags.Append(";+draft/channel-context=").Append(invChannel);
            _client.NoticeTagged(nick, text, tags.ToString());
        }
        ActionsFired++;
    }

    // ===================================================================
    //  Fire orchestration (per origin server)
    // ===================================================================

    private void FireFamily(string family, Dictionary<string, string> vars)
    {
        var graph = _owner.RunGraph;
        if (graph == null) return;
        foreach (var node in graph.Nodes)
        {
            if (node.Def.TriggerEvent != family) continue;
            FireNode(node, new Dictionary<string, string>(vars));   // each trigger gets its own var scope
        }
    }

    internal void FireNode(Node node, Dictionary<string, string> vars)
    {
        var graph = _owner.RunGraph;
        if (graph == null) return;
        var rec = new RunRecord { Time = DateTime.Now, Trigger = node.DisplayTitle, Icon = node.Def.Icon, Summary = Summarize(node.Def.TriggerEvent, vars) };
        int before = _owner.TotalActions;
        var stream = new WorkflowStream(this, vars, node.DisplayTitle);
        GraphExecutor.Fire(graph, this, node, vars, rec, stream.OnNode);
        StopAllTyping();   // a workflow run ends → drop any typing it started but didn't stop
        rec.Actions = _owner.TotalActions - before;
        rec.Fired = rec.Nodes.Count > 0 && rec.Nodes[0].Pulsed.Count > 0;
        if (rec.Fired) _owner.AddHistory(rec);
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

    private Dictionary<string, string> BaseVars() => new()
    {
        ["botnick"] = CurrentNick, ["server"] = Label,
        ["nick"] = "", ["channel"] = "", ["message"] = "", ["args"] = "",
        ["command"] = "", ["target"] = "", ["replyto"] = "",
    };

    private Dictionary<string, string> ScheduleVars(DateTime now)
    {
        var v = BaseVars();
        v["time"] = now.ToString("HH:mm");
        v["date"] = now.ToString("yyyy-MM-dd");
        v["datetime"] = now.ToString("yyyy-MM-dd HH:mm:ss");
        v["weekday"] = now.DayOfWeek.ToString();
        return v;
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

    private sealed class WorkflowStream
    {
        private readonly ServerConn _c;
        private readonly Dictionary<string, string> _vars;
        private readonly string _name;
        private bool _started;
        private string _wid = "", _dest = "";
        private int _sid;

        public WorkflowStream(ServerConn c, Dictionary<string, string> vars, string name) { _c = c; _vars = vars; _name = name; }

        public void OnNode(NodeTrace t)
        {
            if (!_c._cfg.StreamWorkflows || !_c._client.HasCap("message-tags")) return;
            var node = _c._owner.RunGraph?.Find(t.NodeId);
            if (node is not { StreamAsTool: true }) return;
            if (!EnsureStarted()) return;

            string sidc = "s" + (++_sid);
            var input = new Dictionary<string, object?>();
            bool trunc = false;
            void Put(string k, string v) { if (v.Length > 200) trunc = true; input[k] = Truncate(v, 200); }
            foreach (var (pin, val) in t.Inputs) Put(pin, val);
            foreach (var p in node.Def.Params)
                if (node.GetParam(p.Key).Length > 0 && p.Key != "apiKey") Put(p.Key, node.GetParam(p.Key));
            _c.EmitBotTools(_dest, BotTools.Step(_wid, sidc, "tool-call", "complete", ToolName(node.TypeId), input, label: t.Title, truncated: trunc));

            string result = t.Outputs.Count > 0 ? t.Outputs[0].value : "";
            if (result.Length > 0)
                _c.EmitBotTools(_dest, BotTools.Step(_wid, sidc + "r", "tool-result", "complete",
                    content: Truncate(result, 300), label: t.Title + " result", truncated: result.Length > 300));
        }

        private bool EnsureStarted()
        {
            if (_started) return _dest.Length > 0;
            _started = true;
            _dest = _vars.TryGetValue("channel", out var ch) && ch.Length > 0 ? ch
                  : _vars.TryGetValue("replyto", out var rt) ? rt : "";
            if (_dest.Length == 0) return false;
            _wid = "w" + _c._owner.NextWorkflowSeq().ToString("x");
            string trigger = _vars.TryGetValue("msgid", out var mid) ? mid : "";
            _c.EmitBotTools(_dest, BotTools.Workflow(_wid, "start", _name, trigger, new[] { "reasoning" }));
            return true;
        }

        public void Finish()
        {
            if (_started && _dest.Length > 0) _c.EmitBotTools(_dest, BotTools.Workflow(_wid, "complete"));
        }
    }

    private void EmitBotTools(string dest, string payloadB64)
    {
        var tag = "+draft/bot-tools=" + payloadB64;
        if (BotTools.Fits(tag)) _client.TagMsg(dest, tag);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ===================================================================
    //  IRuntimeSink - send via this server; route to another only on a node override
    // ===================================================================
    public IRuntimeSink ForServer(string server)
    {
        if (string.IsNullOrWhiteSpace(server) || Matches(server)) return this;
        return _owner.Route(server) ?? (IRuntimeSink)this;
    }

    public void Privmsg(string target, string text) { _client.Privmsg(target, text); Bump(); }
    public void Notice(string target, string text) { _client.Notice(target, text); Bump(); }
    public void React(string target, string msgid, string emoji) { _client.React(target, msgid, emoji); Bump(); }
    public void PrivmsgTagged(string target, string text, string tags) { _client.PrivmsgTagged(target, text, tags); Bump(); }
    public void NoticeTagged(string target, string text, string tags) { _client.NoticeTagged(target, text, tags); Bump(); }
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
    private void StopAllTyping() { foreach (var t in new List<string>(_typing.Keys)) StopTyping(t); }

    private void Bump() { ActionsFired++; }

    // shared effects route to the owner (one log/state/history/achievements per bot)
    public void Log(string message, LogLevel level) => _owner.LogFrom(Label, level, message);
    public void NodeFired(string nodeId) => _owner.NodeFired(nodeId);
    public void RunCompleted(IReadOnlyCollection<string> executedTypes) => _owner.CreditRun(executedTypes);
    public string GetState(string key) => _owner.GetState(key);
    public void SetState(string key, string value) => _owner.SetState(key, value);
}
