using System;
using System.Collections.Generic;
using System.Text;
using Ircuitry.Core;
using Ircuitry.Graph;

namespace Ircuitry.Runtime;

/// <summary>
/// Executes a graph from a fired trigger. Control flows along exec wires
/// (depth-first); data pins are pull-evaluated, transparently running pure data
/// nodes on demand. A step cap guards against accidental cycles.
/// </summary>
public static class GraphExecutor
{
    private const int MaxSteps = 5000;

    public static void Fire(NodeGraph graph, IRuntimeSink sink, Node trigger, Dictionary<string, string> vars,
        RunRecord? trace = null, Action<NodeTrace>? onNode = null)
    {
        var run = new Run(graph, sink, vars, trace, onNode);
        run.RunExec(trigger);
        if (run.ExecutedTypes.Count > 0) sink.RunCompleted(run.ExecutedTypes);   // spec-compliance achievements
    }

    /// <summary>
    /// Resume a graph from one exec OUTPUT of an existing node (rather than from a trigger) - used by the
    /// Human in the Loop gate to continue an approved/denied branch when a human answers later, on a
    /// fresh run scope seeded with the captured vars and any of the node's data outputs.
    /// </summary>
    public static void FireFrom(NodeGraph graph, IRuntimeSink sink, Node node, int outPin,
        Dictionary<string, string> vars, (int pin, string value)[]? seedOutputs = null, Action<NodeTrace>? onNode = null)
    {
        var run = new Run(graph, sink, vars, null, onNode);
        if (seedOutputs != null) foreach (var (pin, value) in seedOutputs) run.Outputs[(node.Id, pin)] = value;
        run.RunOutput(node, outPin);
        if (run.ExecutedTypes.Count > 0) sink.RunCompleted(run.ExecutedTypes);
    }

    private sealed class Run
    {
        public readonly NodeGraph Graph;
        public readonly IRuntimeSink Sink;
        public readonly Dictionary<string, string> Vars;
        public readonly Dictionary<(string, int), string> Outputs = new();
        public readonly HashSet<string> ExecutedTypes = new();   // node typeIds that ran without throwing, this run
        public readonly RunRecord? Trace;
        public readonly Action<NodeTrace>? OnNode;   // fired the instant a node finishes (live bot-tools streaming)
        private int _steps;
        private readonly int _depth;                 // subflow nesting depth (recursion guard)

        public Run(NodeGraph g, IRuntimeSink sink, Dictionary<string, string> vars, RunRecord? trace, Action<NodeTrace>? onNode, int depth = 0)
        { Graph = g; Sink = sink; Vars = vars; Trace = trace; OnNode = onNode; _depth = depth; }

        public void RunExec(Node node)
        {
            if (++_steps > MaxSteps) return;
            if (node.Muted) return;                 // disabled nodes are skipped entirely
            Sink.NodeFired(node.Id);
            var ctx = new NodeCtx(this, node);
            bool ok = true;
            try { node.Def.Exec(ctx); }
            catch (Exception ex) { ok = false; Sink.Log($"node '{node.DisplayTitle}' error: {ex.Message}", LogLevel.Error); }
            if (ok) ExecutedTypes.Add(node.TypeId);   // only a successful execution counts toward spec achievements

            Record(node, ctx.Pulses);
            foreach (int pin in ctx.Pulses) RunOutput(node, pin);
        }

        private static string PinName(PinDef p, int i) => p.Name.Length > 0 ? p.Name : "in" + i;

        private void Record(Node node, List<int> pulses)
        {
            bool addToTrace = Trace != null && Trace.Nodes.Count < 400;
            if (!addToTrace && OnNode == null) return;
            var t = new NodeTrace { NodeId = node.Id, Title = node.DisplayTitle, Icon = node.Def.Icon };
            var ins = node.Inputs; var outs = node.Outputs;
            for (int i = 0; i < ins.Length; i++)
            {
                var pd = ins[i];
                if (pd.Kind == PinKind.Exec || pd.Kind == PinKind.Tool) continue;
                if (Graph.InputConnected(node.Id, i)) t.Inputs.Add((PinName(pd, i), ResolveInput(node, i)));
            }
            for (int i = 0; i < outs.Length; i++)
            {
                var pd = outs[i];
                if (pd.Kind == PinKind.Exec || pd.Kind == PinKind.Tool) continue;
                if (Outputs.TryGetValue((node.Id, i), out var v)) t.Outputs.Add((PinName(pd, i), v));
            }
            foreach (var p in pulses) if (p >= 0 && p < outs.Length) t.Pulsed.Add(PinName(outs[p], p));
            if (addToTrace) Trace!.Nodes.Add(t);
            OnNode?.Invoke(t);   // emit the bot-tools step right now, as the node completes
        }

        /// <summary>Run everything wired to one exec output (also used directly by loop nodes).</summary>
        public void RunOutput(Node node, int pin)
        {
            foreach (var conn in Graph.FromPin(node.Id, pin))
            {
                var target = Graph.Find(conn.ToNode);
                if (target == null) continue;
                var tIn = target.Inputs;
                if (conn.ToPin >= 0 && conn.ToPin < tIn.Length &&
                    tIn[conn.ToPin].Kind == PinKind.Exec)
                    RunExec(target);
            }
        }

        /// <summary>Run a saved subgraph as a reusable unit: a child scope seeded with the node's named
        /// inputs runs from the subgraph's flow.in; flow.return nodes write named outputs we read back.
        /// Shares the sink (effects go out normally) and the trace/stream.</summary>
        public Dictionary<string, string> RunSubflow(NodeGraph sub, Dictionary<string, string> inputs)
        {
            var child = new Dictionary<string, string>(Vars);
            foreach (var kv in inputs) child[kv.Key] = kv.Value;
            if (_depth >= 16) { Sink.Log("subflow nesting too deep - aborted", LogLevel.Error); return child; }
            Node? entry = null;
            foreach (var n in sub.Nodes) if (n.TypeId == "flow.in") { entry = n; break; }
            if (entry != null)
            {
                var cr = new Run(sub, Sink, child, Trace, OnNode, _depth + 1);
                cr.RunExec(entry);
                foreach (var t in cr.ExecutedTypes) ExecutedTypes.Add(t);   // count subflow nodes toward this run
            }
            return child;
        }

        /// <summary>Fire every On Signal trigger with a matching name, in this same run (shared step budget
        /// guards against signal loops). A signal carries optional data on the {data} pin / __signaldata var.</summary>
        public void EmitSignal(string name, string data)
        {
            if (name.Length == 0) return;
            Vars["__signaldata"] = data ?? "";
            foreach (var n in Graph.Nodes)
                if (!n.Muted && n.TypeId == "event.signal" &&
                    string.Equals(n.GetParam("signal"), name, System.StringComparison.OrdinalIgnoreCase))
                    RunExec(n);
        }

        // The visiting set is threaded through nested input reads so the cycle
        // guard survives pure-node-calls-pure-node chains (a fresh set per level
        // would let A↔B data cycles recurse forever → StackOverflow).
        public string ResolveInput(Node node, int inputIndex, HashSet<string>? visiting = null)
        {
            var conn = Graph.IntoPin(node.Id, inputIndex);
            return conn == null ? "" : ResolveOutput(conn.FromNode, conn.FromPin, visiting ?? new HashSet<string>());
        }

        public List<Node> SourcesInto(Node node, int inputIndex)
        {
            var res = new List<Node>();
            foreach (var c in Graph.Connections)
                if (c.ToNode == node.Id && c.ToPin == inputIndex)
                { var s = Graph.Find(c.FromNode); if (s != null) res.Add(s); }
            return res;
        }

        private string ResolveOutput(string nodeId, int outIdx, HashSet<string> visiting)
        {
            if (Outputs.TryGetValue((nodeId, outIdx), out var v)) return v;
            var src = Graph.Find(nodeId);
            if (src == null) return "";
            // pull-evaluate pure data nodes on demand (guard against cycles)
            if (src.Def.IsPure && !src.Muted && visiting.Add(nodeId))
            {
                Sink.NodeFired(src.Id);
                var ctx = new NodeCtx(this, src) { Visiting = visiting };
                try { src.Def.Exec(ctx); } catch { /* leave outputs empty */ }
                Record(src, ctx.Pulses);
                if (Outputs.TryGetValue((nodeId, outIdx), out var w)) return w;
            }
            return "";
        }

        public string Resolve(string template)
        {
            if (string.IsNullOrEmpty(template) || template.IndexOf('{') < 0) return template;
            template = Ircuitry.Core.Secrets.Expand(template);   // {{secret.x}} first, then {var} tokens
            var sb = new StringBuilder(template.Length + 16);
            for (int i = 0; i < template.Length; i++)
            {
                if (template[i] == '{')
                {
                    int j = template.IndexOf('}', i + 1);
                    if (j > i && Ircuitry.Core.Tokens.IsName(template, i + 1, j))   // {name} only; leave JSON/code braces ({}, {"k":"v"}) alone
                    {
                        sb.Append(ResolveToken(template[(i + 1)..j]));
                        i = j;
                        continue;
                    }
                }
                sb.Append(template[i]);
            }
            return sb.ToString();
        }

        // Resolve a single {token}: a context variable if set, else a handy computed/alias shortcode.
        private string ResolveToken(string name)
        {
            if (Vars.TryGetValue(name, out var v)) return v;
            var now = System.DateTime.Now;
            switch (name)
            {
                // who we are
                case "me": case "self": case "bot": return Vars.TryGetValue("botnick", out var bn) ? bn : "";
                // date & time (live, unless a scheduled fire already pinned them above)
                case "time": return now.ToString("HH:mm");
                case "time12": return now.ToString("h:mm tt");
                case "date": return now.ToString("yyyy-MM-dd");
                case "datetime": return now.ToString("yyyy-MM-dd HH:mm:ss");
                case "weekday": return now.DayOfWeek.ToString();
                case "day": return now.Day.ToString();
                case "month": return now.Month.ToString();
                case "monthname": return now.ToString("MMMM");
                case "year": return now.Year.ToString();
                case "hour": return now.ToString("HH");
                case "minute": return now.ToString("mm");
                case "second": return now.ToString("ss");
                case "unixtime": return ((long)(System.DateTime.UtcNow - System.DateTime.UnixEpoch).TotalSeconds).ToString();
                // little extras
                case "rand": return System.Random.Shared.Next(0, 100).ToString();
                case "rand1000": return System.Random.Shared.Next(0, 1000).ToString();
                case "dice": return System.Random.Shared.Next(1, 7).ToString();
                case "coin": return System.Random.Shared.Next(2) == 0 ? "heads" : "tails";
                case "nl": return "\n";
                case "version": return Ircuitry.App.AppInfo.Version;
                default:
                    // {arg1}, {arg2}, ... → the Nth word of the command args
                    if (name.Length > 3 && name.StartsWith("arg") && int.TryParse(name[3..], out var n) && n > 0)
                    {
                        var parts = (Vars.TryGetValue("args", out var a) ? a : "").Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                        return n <= parts.Length ? parts[n - 1] : "";
                    }
                    // {arg.NAME} -> an AI tool argument the model supplied (set as __arg.NAME by the tool call)
                    if (name.StartsWith("arg.", System.StringComparison.Ordinal) && name.Length > 4)
                        return Vars.TryGetValue("__arg." + name[4..], out var av) ? av : "";
                    // dotted JSON path: {var.a.b.0.c} - if `var` holds JSON (a run var, or a stored value
                    // from Set Var), walk the path (n8n's $json.x). A flat var with dots, e.g. {tag.account},
                    // already matched above.
                    int dot = name.IndexOf('.');
                    if (dot > 0)
                    {
                        var head = name[..dot];
                        string doc = Vars.TryGetValue(head, out var rv) ? rv : Sink.GetState(head);
                        if (doc.Length > 0) return Ircuitry.Net.Json.Extract(doc, name[(dot + 1)..]);
                    }
                    return "";
            }
        }
    }

    private sealed class NodeCtx : INodeContext
    {
        private readonly Run _run;
        private readonly Node _node;
        public readonly List<int> Pulses = new(2);

        /// <summary>The active cycle-guard set while this node is being pull-evaluated (null at exec top level).</summary>
        public HashSet<string>? Visiting;

        public NodeCtx(Run run, Node node) { _run = run; _node = node; }

        public Node Node => _node;

        public string Param(string key) => Ircuitry.Core.Secrets.Expand(_node.GetParam(key));
        public bool ParamBool(string key) => Param(key) is "true" or "1" or "yes" or "on";
        public int ParamInt(string key, int fallback = 0) => int.TryParse(Param(key), out var n) ? n : fallback;

        public string In(int inputIndex) => _run.ResolveInput(_node, inputIndex, Visiting);
        public string InOr(int inputIndex, string fallback) =>
            _run.Graph.InputConnected(_node.Id, inputIndex) ? In(inputIndex) : fallback;
        public void SetOut(int outputIndex, string value) => _run.Outputs[(_node.Id, outputIndex)] = value;

        public void Pulse(int execOutputIndex) => Pulses.Add(execOutputIndex);
        public void Run(int execOutputIndex) => _run.RunOutput(_node, execOutputIndex);
        public System.Collections.Generic.IReadOnlyList<Node> SourcesInto(int inputIndex) => _run.SourcesInto(_node, inputIndex);
        public void RunNode(Node node) => _run.RunExec(node);
        public Dictionary<string, string> RunSubflow(NodeGraph sub, Dictionary<string, string> inputs) => _run.RunSubflow(sub, inputs);
        public void EmitSignal(string name, string data) => _run.EmitSignal(name, data);

        public string Var(string name) => _run.Vars.TryGetValue(name, out var v) ? v : "";
        public void SetVar(string name, string value) => _run.Vars[name] = value;
        public string Resolve(string template) => _run.Resolve(template);
        public double Rng() => System.Random.Shared.NextDouble();   // thread-safe (timers + IRC fire concurrently)

        public string GetState(string key) => _run.Sink.GetState(key);
        public void SetState(string key, string value) => _run.Sink.SetState(key, value);

        public bool AwaitApproval(string target, string approver, string approveWord, string denyWord, int timeoutSec)
            => _run.Sink.AwaitApproval(_node, new Dictionary<string, string>(_run.Vars), target, approver, approveWord, denyWord, timeoutSec);

        public string InvokeNodeTool(Node node, Dictionary<string, string> args)
        {
            foreach (var kv in args) _run.Vars["__arg." + kv.Key] = kv.Value;
            _run.RunExec(node);
            var outs = node.Outputs;
            for (int i = 0; i < outs.Length; i++)
            {
                var k = outs[i].Kind;
                if (k != PinKind.Exec && k != PinKind.Tool && _run.Outputs.TryGetValue((node.Id, i), out var v)) return v;
            }
            return "";
        }
        public double NowSeconds() => (System.DateTime.UtcNow - System.DateTime.UnixEpoch).TotalSeconds;

        /// <summary>The sink an IRC effect from this node should use: the origin server by default, or the
        /// server named by an optional "server" param (lets one flow act across several connections).</summary>
        private IRuntimeSink Out() => _run.Sink.ForServer(Ircuitry.Core.Secrets.Expand(_node.GetParam("server")));

        public void Reply(string text) => SendReply(text, "");
        public void ReplyThreaded(string text) => SendReply(text, Var("msgid"));

        // Sends a reply, correlating it to the triggering message/invocation and (for the
        // bot-cmds spec) attaching +reply / +draft/invoked-by / +draft/channel-context per context.
        private void SendReply(string text, string forcedMsgid)
        {
            if (text.Length == 0) return;
            string ctx = Var("__ctx");                       // "public" | "private" | "pm" | "" (legacy chat)
            string rid = forcedMsgid.Length > 0 ? forcedMsgid : Var("__reply");

            var tags = new StringBuilder();
            if (rid.Length > 0) tags.Append("+reply=").Append(rid);
            if (ctx == "public" && Var("__invokedby") is { Length: > 0 } ib) Append(tags, "+draft/invoked-by=" + ib);
            if (ctx == "private" && Var("__chanctx") is { Length: > 0 } cc) Append(tags, "+draft/channel-context=" + cc);
            string t = tags.ToString();

            var sink = Out();
            if (ctx is "private" or "pm")
            {
                string who = Var("nick");
                if (who.Length > 0) sink.NoticeTagged(who, text, t);
            }
            else
            {
                string target = Var("replyto");
                if (target.Length == 0) target = Var("channel");
                if (target.Length == 0) target = Var("nick");
                if (target.Length > 0) sink.PrivmsgTagged(target, text, t);
            }
        }

        private static void Append(StringBuilder sb, string tag) { if (sb.Length > 0) sb.Append(';'); sb.Append(tag); }

        public void React(string emoji)
        {
            string t = Var("replyto"); if (t.Length == 0) t = Var("channel");
            if (t.Length > 0 && emoji.Length > 0) Out().React(t, Var("msgid"), emoji);
        }
        public void ReactTo(string target, string msgid, string emoji)
        {
            if (target.Length == 0) { target = Var("replyto"); if (target.Length == 0) target = Var("channel"); }
            if (msgid.Length == 0) msgid = Var("msgid");
            if (target.Length > 0 && msgid.Length > 0 && emoji.Length > 0) Out().React(target, msgid, emoji);
        }
        public IReadOnlyList<RecentMsg> RecentMessages(int count) => _run.Sink.RecentMessages(count);
        public void Send(string target, string text) => Out().Privmsg(target, text);
        public void Notice(string target, string text) => Out().Notice(target, text);
        public void Join(string channel) => Out().Join(channel);
        public void Part(string channel, string reason) => Out().Part(channel, reason);
        public void Raw(string line) => Out().Raw(line);
        public void StartTyping(string target) => Out().StartTyping(target);
        public void StopTyping(string target) => Out().StopTyping(target);
        public void Log(string message, LogLevel level = LogLevel.Action) => _run.Sink.Log(message, level);
    }
}
