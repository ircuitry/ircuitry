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
    private readonly ConcurrentDictionary<string, DateTime> _typing = new();   // target -> last +typing=active

    // Workflow runs execute on this pool, NOT the IRC read thread - so a slow node (delay, http, ai)
    // never blocks PING/PONG keepalive, and independent runs proceed concurrently instead of queueing.
    private BlockingCollection<Action>? _runQueue;
    private readonly List<System.Threading.Thread> _workers = new();
    private const int RunWorkers = 8;

    // CHATHISTORY: collects batched history and delivers it to waiting nodes; its messages never trigger.
    private readonly HistoryBatches _history = new();

    // live IRC session model (channels, members, topics, network, narration) for the read-only view + state nodes
    private readonly IrcSessionState _session = new();
    public IrcSessionState Session => _session;

    public string Label { get; private set; }
    public bool Running => _running;
    public IrcState State => _client.State;
    public string CurrentNick => _client.CurrentNick;
    public IReadOnlyList<string> EnabledCaps => _client.EnabledCaps;
    public bool HasCap(string name) => _client.HasCap(name);
    public IrcSettings Config => _cfg;
    public int MessagesSeen { get; private set; }
    private int _actionsFired;
    public int ActionsFired => _actionsFired;

    public ServerConn(BotRuntime owner, IrcSettings cfg)
    {
        _owner = owner;
        _cfg = cfg.Clone();
        Label = _cfg.DisplayName;
        _client.RawIn = OnRawIn;
        _client.RawOut = line => _owner.LogFrom(Label, LogLevel.Out, line);
        _client.Status = (msg, err) => _owner.LogFrom(Label, err ? LogLevel.Error : LogLevel.System, msg);
        _client.Registered = OnRegistered;
        _client.Closed = reason => { _running = false; _owner.LogFrom(Label, LogLevel.System, Ircuitry.Core.Icons.Glyph("circle") + " " + reason); };
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
        MessagesSeen = 0; _actionsFired = 0;
        _session.Reset();
        _running = true;
        StartRunWorkers();
        _owner.LogFrom(Label, LogLevel.System, $"{Ircuitry.Core.Icons.Glyph("play")} connecting - {_owner.CountTriggers()} trigger(s) armed");
        _client.Connect(_cfg);
        StartTimers();
    }

    public void Stop()
    {
        if (!_running && State == IrcState.Disconnected) return;
        _owner.LogFrom(Label, LogLevel.System, Ircuitry.Core.Icons.Glyph("square") + " disconnecting");
        _running = false;
        lock (_pendLock) _pending.Clear();   // drop any waiting human-in-the-loop gates
        try { _runQueue?.CompleteAdding(); } catch { /* already completed */ }   // workers drain + exit
        StopAllTyping();   // send +typing=done for anything still active before we drop the link
        _client.Disconnect();
    }

    // Background pool that executes workflow runs off the IRC read thread (see _runQueue).
    private void StartRunWorkers()
    {
        var q = new BlockingCollection<Action>();
        _runQueue = q;
        _workers.Clear();
        for (int i = 0; i < RunWorkers; i++)
        {
            var w = new System.Threading.Thread(() =>
            {
                foreach (var job in q.GetConsumingEnumerable())
                    try { job(); } catch { /* a run never takes the connection down */ }
            })
            { IsBackground = true, Name = "bot-run:" + Label + ":" + i };
            w.Start();
            _workers.Add(w);
        }
    }

    // Queue a workflow run for the pool; falls back to inline if the pool isn't up (shouldn't happen live).
    private void Dispatch(Action job)
    {
        var q = _runQueue;
        if (q != null && !q.IsAddingCompleted)
        {
            try { q.Add(job); return; } catch { /* completed between the check and the add */ }
        }
        job();
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

                ExpireApprovals(DateTime.UtcNow);   // deny any human-in-the-loop gate that has timed out
                SweepTempBans();                    // lift any temp bans whose TTL elapsed (survives restart via State)
            }
        })
        { IsBackground = true, Name = "bot-timer:" + Label };
        _timer.Start();
    }

    // ===================================================================
    private void OnRawIn(string line)
    {
        // log EVERY incoming raw wire line (including PRIVMSG/NOTICE) - the event console parses it into a
        // pretty, fully-broken-down row, so we want the real line, not a pre-decorated summary.
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

        // IRCv3 CHATHISTORY: a BATCH line is never a trigger, and every message inside a chathistory batch is
        // DATA ONLY - we record it for lookups and hand it to the requesting node, but fire NOTHING. This is
        // what stops history (incl. messages from before we joined) from re-triggering message/join/etc nodes.
        if (m.Is("BATCH")) { _history.OnBatch(m); return; }
        if (_history.AnyOpen && _history.Capture(m, out var hrec))
        {
            if (hrec != null) _owner.RecordMessage(hrec.Nick, hrec.Channel, hrec.Text, hrec.Msgid);
            return;
        }

        // keep the live session model (channels, members, topics, network, narration) current for EVERY line
        _session.Observe(m, CurrentNick);

        if (m.Is("PRIVMSG"))
        {
            MessagesSeen++;
            string nick = m.Nick ?? "";
            if (nick.Equals(CurrentNick, StringComparison.OrdinalIgnoreCase)) return; // ignore self
            string target = m.P(0);
            string text = m.Trailing;

            // CTCP DCC (file transfer / chat negotiation) - not a chat message; route it to On DCC nodes
            if (text.StartsWith(Ircuitry.Net.Dcc.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                if (Ircuitry.Net.Dcc.TryParse(Ircuitry.Net.Dcc.Strip(text), out var off))
                {
                    var dv = BaseVars();
                    dv["nick"] = nick; dv["user"] = m.User ?? ""; dv["host"] = m.Host ?? ""; dv["replyto"] = nick;
                    // expose IRCv3 tags so a flow can allow-list by AUTHENTICATED account (nicks can be faked)
                    dv["account"] = m.Tag("account");
                    dv["isbot"] = (m.Tags.ContainsKey("bot") || m.Tags.ContainsKey("draft/bot")) ? "true" : "false";
                    foreach (var kv in m.Tags) dv["tag." + kv.Key] = kv.Value;
                    dv["dcc.type"] = off.Type; dv["dcc.file"] = off.File; dv["dcc.size"] = off.Size.ToString();
                    dv["dcc.ip"] = off.Ip; dv["dcc.port"] = off.Port.ToString(); dv["dcc.token"] = off.Token;
                    dv["dcc.position"] = off.Position.ToString();
                    FireFamily("dcc", dv);
                }
                return;
            }

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
            vars["host"] = m.Host ?? "";             // for the Ban Mask node to build host/domain masks
            vars["isbot"] = isbot ? "true" : "false";
            vars["msgid"] = msgid;
            vars["__reply"] = msgid;                 // correlate replies to the triggering message (+reply)
            foreach (var kv in m.Tags) vars["tag." + kv.Key] = kv.Value;

            // the raw PRIVMSG line was already logged by OnRawIn (and is parsed prettily by the console);
            // here we only feed the recent-message ring used by SuperAI and the read-only IRC view.
            _owner.RecordMessage(nick, vars["channel"], text, msgid);

            if (TryResolveApproval(nick, target, toChannel, text)) return;   // a human answered a pending gate
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
        else if (m.Is("PART"))
        {
            string nick = m.Nick ?? "";
            if (nick.Equals(CurrentNick, StringComparison.OrdinalIgnoreCase)) return;
            var vars = BaseVars();
            vars["nick"] = nick;
            vars["channel"] = vars["target"] = vars["replyto"] = m.P(0);
            vars["reason"] = m.Trailing;
            FireFamily("part", vars);
        }
        else if (m.Is("QUIT"))
        {
            string nick = m.Nick ?? "";
            if (nick.Equals(CurrentNick, StringComparison.OrdinalIgnoreCase)) return;
            var vars = BaseVars();
            vars["nick"] = nick;
            vars["reason"] = m.Trailing;
            FireFamily("quit", vars);
        }
        else if (m.Is("KICK"))
        {
            var vars = BaseVars();
            vars["nick"] = m.Nick ?? "";                          // the actor (who did the kicking)
            vars["channel"] = vars["target"] = vars["replyto"] = m.P(0);
            vars["kicked"] = m.P(1);                              // the victim
            vars["reason"] = m.Trailing;
            FireFamily("kick", vars);
        }
        else if (m.Is("NICK"))
        {
            var vars = BaseVars();
            vars["nick"] = vars["oldnick"] = m.Nick ?? "";
            vars["newnick"] = m.P(0).Length > 0 ? m.P(0) : m.Trailing;
            FireFamily("nick", vars);
        }
        else if (m.Is("MODE"))
        {
            var vars = BaseVars();
            vars["nick"] = m.Nick ?? "";                          // who set the mode
            vars["channel"] = vars["target"] = vars["replyto"] = m.P(0);
            vars["modes"] = m.P(1);
            vars["args"] = string.Join(' ', m.Params.Skip(2));
            FireFamily("mode", vars);
        }
        else if (m.Is("INVITE"))
        {
            var vars = BaseVars();
            vars["nick"] = m.Nick ?? "";                          // who invited us
            vars["channel"] = vars["target"] = vars["replyto"] = m.P(1).Length > 0 ? m.P(1) : m.Trailing;
            FireFamily("invite", vars);
        }
        else if (m.IsNumeric(out int num))
        {
            // a server numeric (001, 005, 353, 433, INVITE-related, ...) - lets On Numeric nodes react
            var vars = BaseVars();
            vars["numeric"] = num.ToString();
            vars["numname"] = Ircuitry.Irc.IrcNumerics.Name(num) ?? "";
            vars["nick"] = m.Nick ?? "";
            vars["channel"] = m.Params.Count > 1 && (m.P(1).StartsWith('#') || m.P(1).StartsWith('&')) ? m.P(1) : "";
            vars["message"] = m.Trailing;
            vars["args"] = string.Join(' ', m.Params);
            for (int i = 0; i < m.Params.Count; i++) vars["arg" + (i + 1)] = m.P(i);
            FireFamily("numeric", vars);
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
        System.Threading.Interlocked.Increment(ref _actionsFired);
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
        System.Threading.Interlocked.Increment(ref _actionsFired);
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

    /// <summary>Fire every On Webhook node whose path matches, with the request vars (body/method/query.*) merged
    /// into the run scope. Returns how many fired.</summary>
    public int FireWebhook(string path, Dictionary<string, string> extra)
    {
        var graph = _owner.RunGraph;
        if (graph == null) return 0;
        int fired = 0;
        foreach (var node in graph.Nodes)
        {
            if (node.Def.TriggerEvent != "webhook" || node.Muted) continue;
            if (!string.Equals(node.GetParam("path"), path, StringComparison.Ordinal)) continue;
            var vars = BaseVars();
            foreach (var kv in extra) vars[kv.Key] = kv.Value;
            FireNode(node, vars);
            fired++;
        }
        return fired;
    }

    internal void FireNode(Node node, Dictionary<string, string> vars) => Dispatch(() => RunNode(node, vars));

    private void RunNode(Node node, Dictionary<string, string> vars)
    {
        var graph = _owner.RunGraph;
        if (graph == null) return;
        vars = new Dictionary<string, string>(vars);   // isolate this run's scope from any concurrent run
        var rec = new RunRecord { Time = DateTime.Now, Trigger = node.DisplayTitle, Icon = node.Def.Icon, Summary = Summarize(node.Def.TriggerEvent, vars),
            Msgid = vars.TryGetValue("msgid", out var _mid) ? _mid : "" };
        int before = _owner.TotalActions;
        var stream = new WorkflowStream(this, vars, node.DisplayTitle);
        GraphExecutor.Fire(graph, this, node, vars, rec, stream.OnNode);
        // typing is a persistent state, NOT per-run: a Start Typing keeps refreshing (+typing=active every 4s,
        // above) and ends only when a Stop Typing node runs, a real message is sent to the target (the message
        // implicitly ends typing - see Privmsg), or the bot disconnects. Don't auto-send +typing=done here.
        rec.Actions = Math.Max(0, _owner.TotalActions - before);   // approximate under concurrency, never negative
        rec.Fired = rec.Nodes.Count > 0 && rec.Nodes[0].Pulsed.Count > 0;
        if (rec.Fired) _owner.AddHistory(rec);
        stream.Finish();
    }

    // ===================================================================
    //  Human in the Loop: gates that pause a run until a human answers
    // ===================================================================
    private sealed class Pending
    {
        public string NodeId = "";
        public Dictionary<string, string> Vars = new();
        public string Target = "", Approver = "", ApproveWord = "yes", DenyWord = "no";
        public bool HasTimeout;
        public DateTime Expires;
    }
    private readonly List<Pending> _pending = new();
    private readonly object _pendLock = new();

    public bool AwaitApproval(Node node, Dictionary<string, string> vars, string target, string approver,
        string approveWord, string denyWord, int timeoutSec)
    {
        lock (_pendLock) _pending.Add(new Pending
        {
            NodeId = node.Id, Vars = vars, Target = target, Approver = approver,
            ApproveWord = approveWord.Length > 0 ? approveWord : "yes",
            DenyWord = denyWord.Length > 0 ? denyWord : "no",
            HasTimeout = timeoutSec > 0, Expires = DateTime.UtcNow.AddSeconds(timeoutSec),
        });
        return true;
    }

    /// <summary>An incoming line may be the answer to a pending gate; resume it and report consumed.</summary>
    private bool TryResolveApproval(string fromNick, string target, bool toChannel, string text)
    {
        string answer = text.Trim();
        Pending? hit = null; int pin = -1;
        lock (_pendLock)
        {
            foreach (var p in _pending)
            {
                // the answer must come from where we asked (the channel, or a PM with the asker) and, if a
                // specific approver was named, from them
                string askChan = p.Target.StartsWith('#') || p.Target.StartsWith('&') ? p.Target : "";
                bool here = askChan.Length > 0 ? string.Equals(askChan, target, StringComparison.OrdinalIgnoreCase) : !toChannel;
                if (!here) continue;
                if (p.Approver.Length > 0 && !string.Equals(p.Approver, fromNick, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(answer, p.ApproveWord, StringComparison.OrdinalIgnoreCase)) { hit = p; pin = 0; break; }
                if (string.Equals(answer, p.DenyWord, StringComparison.OrdinalIgnoreCase)) { hit = p; pin = 1; break; }
            }
            if (hit != null) _pending.Remove(hit);
        }
        if (hit == null) return false;
        ResumeApproval(hit, pin, answer, fromNick);
        return true;
    }

    private void ResumeApproval(Pending p, int outPin, string response, string approver)
    {
        var graph = _owner.RunGraph;
        var node = graph?.Find(p.NodeId);
        if (graph == null || node == null) return;
        var vars = new Dictionary<string, string>(p.Vars) { ["response"] = response, ["approver"] = approver };
        Dispatch(() => GraphExecutor.FireFrom(graph, this, node, outPin, vars, new[] { (2, response) }));
    }

    /// <summary>Deny any gate that has waited past its timeout (called from the timer tick).</summary>
    private void ExpireApprovals(DateTime utcNow)
    {
        List<Pending>? due = null;
        lock (_pendLock)
            for (int i = _pending.Count - 1; i >= 0; i--)
                if (_pending[i].HasTimeout && utcNow >= _pending[i].Expires) { (due ??= new()).Add(_pending[i]); _pending.RemoveAt(i); }
        if (due != null) foreach (var p in due) ResumeApproval(p, 1, "(timed out)", "");
    }

    // auto-lift temp bans (State key "__tempban|<channel><mask>" = expiry unix sec). Only the server
    // actually in the channel lifts it; the entry persists in State so a restart still cleans it up.
    private void SweepTempBans()
    {
        foreach (var (key, val) in _owner.StateWithPrefix("__tempban|"))
        {
            if (!long.TryParse(val, out var exp) || DateTimeOffset.UtcNow.ToUnixTimeSeconds() < exp) continue;
            var rest = key.Substring("__tempban|".Length);
            int i = rest.IndexOf('\u0001');
            if (i < 0) { _owner.RemoveState(key); continue; }
            string channel = rest[..i], mask = rest[(i + 1)..];
            if (!_session.InChannel(channel)) continue;          // a different server owns this channel
            _client.SendRaw($"MODE {channel} -b {mask}");
            _owner.RemoveState(key);
        }
    }

    private static string Summarize(string? family, Dictionary<string, string> v)
    {
        v.TryGetValue("nick", out var nick); v.TryGetValue("message", out var msg); v.TryGetValue("channel", out var ch);
        return family switch
        {
            "message" => (string.IsNullOrEmpty(nick) ? "" : nick + ": ") + msg,
            "join" => (nick ?? "") + " joined " + (ch ?? ""),
            "part" => (nick ?? "") + " left " + (ch ?? ""),
            "quit" => (nick ?? "") + " quit",
            "kick" => (v.TryGetValue("kicked", out var kd) ? kd : "") + " kicked from " + (ch ?? ""),
            "nick" => (nick ?? "") + " -> " + (v.TryGetValue("newnick", out var nn) ? nn : ""),
            "mode" => "mode " + (v.TryGetValue("modes", out var md) ? md : "") + " on " + (ch ?? ""),
            "invite" => "invited to " + (ch ?? ""),
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
            // Resolved (wired) inputs first - these are the values the node actually ran with. A param
            // whose key a connected input already supplied must NOT overwrite it: a node reads InOr(pin,
            // Param(key)) where pin and param share a name, so the static param still holds the DEFAULT.
            // Clobbering here showed the default instead of the value passed in down the wire.
            foreach (var (pin, val) in t.Inputs) Put(pin, val);
            foreach (var p in node.Def.Params)
                if (!input.ContainsKey(p.Key) && node.GetParam(p.Key).Length > 0 && p.Key != "apiKey")
                    Put(p.Key, node.GetParam(p.Key));
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

    // sending a real message to a target implicitly ends any typing indicator there (per the +typing spec),
    // so stop refreshing it - but don't send an explicit +typing=done, the message itself ended it.
    public void Privmsg(string target, string text) { _client.Privmsg(target, text); _typing.TryRemove(target, out _); Bump(); }
    public void Notice(string target, string text) { _client.Notice(target, text); Bump(); }
    public void React(string target, string msgid, string emoji) { _client.React(target, msgid, emoji); Bump(); }
    public void PrivmsgTagged(string target, string text, string tags) { _client.PrivmsgTagged(target, text, tags); _typing.TryRemove(target, out _); Bump(); }
    public void NoticeTagged(string target, string text, string tags) { _client.NoticeTagged(target, text, tags); Bump(); }
    public void Join(string channel) => _client.Join(channel);
    public void Part(string channel, string reason) => _client.Part(channel, reason);
    public void Raw(string line) => _client.SendRaw(line);

    // ---- DCC: direct file transfer over a side TCP socket, negotiated by CTCP over IRC ----
    private static void Bg(string name, Action job) => new System.Threading.Thread(() => job()) { IsBackground = true, Name = name }.Start();

    public void DccReceive(string fromNick, string ip, int port, long size, string token, string savePath)
    {
        string label = System.IO.Path.GetFileName(savePath);
        Bg("dcc-recv", () =>
        {
            try
            {
                System.Net.Sockets.TcpClient tcp;
                if (port == 0 && token.Length > 0)   // passive / reverse DCC: we listen, then send a reverse offer
                {
                    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, 0);
                    listener.Start();
                    int p = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
                    ulong ipInt = Ircuitry.Net.Dcc.IpToInt(Ircuitry.Net.Dcc.LocalIp());
                    _client.SendRaw($"PRIVMSG {fromNick} :{Ircuitry.Net.Dcc.SendLine(label, ipInt, p, size, token)}");
                    Log($"DCC: waiting for {fromNick} to connect for {label}…", LogLevel.System);
                    var at = listener.AcceptTcpClientAsync();
                    bool ok = at.Wait(90000);
                    listener.Stop();
                    if (!ok) { Log($"DCC: {fromNick} never connected for {label}", LogLevel.Error); return; }
                    tcp = at.Result;
                }
                else   // active DCC: connect to the IP/port the sender advertised
                {
                    tcp = new System.Net.Sockets.TcpClient();
                    var ct = tcp.ConnectAsync(System.Net.IPAddress.Parse(ip), port);
                    if (!ct.Wait(30000) || !tcp.Connected) { Log($"DCC: couldn't reach {ip}:{port} for {label}", LogLevel.Error); tcp.Dispose(); return; }
                }
                using (tcp)
                {
                    long got = Ircuitry.Net.Dcc.StreamIn(tcp.GetStream(), savePath, size);
                    Log($"DCC: received {label} ({got} bytes) {Ircuitry.Core.Icons.Glyph("arrow-right")} {savePath}", LogLevel.System);
                }
            }
            catch (Exception ex) { Log($"DCC receive failed: {ex.Message}", LogLevel.Error); }
        });
    }

    public void DccSend(string toNick, string filePath, string advertiseIp)
    {
        if (!System.IO.File.Exists(filePath)) { Log($"DCC send: no such file '{filePath}'", LogLevel.Error); return; }
        long size = new System.IO.FileInfo(filePath).Length;
        string name = System.IO.Path.GetFileName(filePath);
        Bg("dcc-send", () =>
        {
            try
            {
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, 0);
                listener.Start();
                int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
                string ip = advertiseIp.Trim().Length > 0 ? advertiseIp.Trim() : Ircuitry.Net.Dcc.LocalIp();
                ulong ipInt = Ircuitry.Net.Dcc.IpToInt(ip);
                _client.SendRaw($"PRIVMSG {toNick} :{Ircuitry.Net.Dcc.SendLine(name, ipInt, port, size)}");
                Log($"DCC: offering {name} ({size} bytes) to {toNick}…", LogLevel.System);
                var at = listener.AcceptTcpClientAsync();
                bool ok = at.Wait(120000);
                listener.Stop();
                if (!ok) { Log($"DCC: {toNick} never accepted {name}", LogLevel.Error); return; }
                using var tcp = at.Result;
                long sent = Ircuitry.Net.Dcc.StreamOut(tcp.GetStream(), filePath);
                Log($"DCC: sent {name} ({sent} bytes) to {toNick}", LogLevel.System);
            }
            catch (Exception ex) { Log($"DCC send failed: {ex.Message}", LogLevel.Error); }
        });
    }
    public IReadOnlyList<RecentMsg> RecentMessages(int count) => _owner.RecentMessages(count);

    public string IrcInfo(string what, string channel)
    {
        switch (what)
        {
            case "nick": return CurrentNick;
            case "network": return _session.Network;
            case "caps": return string.Join(",", EnabledCaps);
            case "channels": return string.Join(",", _session.Channels());
            case "topic": return _session.Topic(channel);
            case "members": return string.Join(",", _session.Members(channel).Select(m => m.prefix + m.nick));
            case "count": return _session.MemberCount(channel).ToString();
            case "joined": return _session.InChannel(channel) ? "true" : "false";
            default: return "";
        }
    }

    public IReadOnlyList<RecentMsg> RequestHistory(string target, string sub, int count, int timeoutMs)
    {
        if (!_running) return System.Array.Empty<RecentMsg>();
        target = target.Trim();
        if (target.Length == 0) return System.Array.Empty<RecentMsg>();
        count = Math.Clamp(count, 1, 1000);
        sub = sub.Trim().ToUpperInvariant(); if (sub.Length == 0) sub = "LATEST";

        var w = new HistoryBatches.Waiter { Target = target };
        _history.Await(w);                                   // register BEFORE asking, so a fast batch isn't lost
        _client.SendRaw($"CHATHISTORY {sub} {target} * {count}");
        // a successful Wait() carries an acquire barrier, so Result (published before Done.Set()) is visible
        // here; on timeout we mark the waiter abandoned and return empty rather than read Result unsynchronized.
        if (w.Done.Wait(Math.Clamp(timeoutMs, 500, 30000))) return w.Result;
        w.Abandoned = true;
        return System.Array.Empty<RecentMsg>();
    }
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

    private void Bump() { System.Threading.Interlocked.Increment(ref _actionsFired); }

    // shared effects route to the owner (one log/state/history/achievements per bot)
    public void Log(string message, LogLevel level) => _owner.LogFrom(Label, level, message);
    public void NodeFired(string nodeId) => _owner.NodeFired(nodeId);
    public void RunCompleted(IReadOnlyCollection<string> executedTypes) => _owner.CreditRun(executedTypes);
    public string GetState(string key) => _owner.GetState(key);
    public void SetState(string key, string value) => _owner.SetState(key, value);
}
