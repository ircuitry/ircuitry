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
    }

    private sealed class Run
    {
        public readonly NodeGraph Graph;
        public readonly IRuntimeSink Sink;
        public readonly Dictionary<string, string> Vars;
        public readonly Dictionary<(string, int), string> Outputs = new();
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
            try { node.Def.Exec(ctx); }
            catch (Exception ex) { Sink.Log($"node '{node.DisplayTitle}' error: {ex.Message}", LogLevel.Error); }

            Record(node, ctx.Pulses);
            foreach (int pin in ctx.Pulses) RunOutput(node, pin);
        }

        private static string PinName(PinDef p, int i) => p.Name.Length > 0 ? p.Name : "in" + i;

        private void Record(Node node, List<int> pulses)
        {
            bool addToTrace = Trace != null && Trace.Nodes.Count < 400;
            if (!addToTrace && OnNode == null) return;
            var t = new NodeTrace { NodeId = node.Id, Title = node.DisplayTitle, Icon = node.Def.Icon };
            for (int i = 0; i < node.Def.Inputs.Length; i++)
            {
                var pd = node.Def.Inputs[i];
                if (pd.Kind == PinKind.Exec || pd.Kind == PinKind.Tool) continue;
                if (Graph.InputConnected(node.Id, i)) t.Inputs.Add((PinName(pd, i), ResolveInput(node, i)));
            }
            for (int i = 0; i < node.Def.Outputs.Length; i++)
            {
                var pd = node.Def.Outputs[i];
                if (pd.Kind == PinKind.Exec || pd.Kind == PinKind.Tool) continue;
                if (Outputs.TryGetValue((node.Id, i), out var v)) t.Outputs.Add((PinName(pd, i), v));
            }
            foreach (var p in pulses) if (p >= 0 && p < node.Def.Outputs.Length) t.Pulsed.Add(PinName(node.Def.Outputs[p], p));
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
                if (conn.ToPin >= 0 && conn.ToPin < target.Def.Inputs.Length &&
                    target.Def.Inputs[conn.ToPin].Kind == PinKind.Exec)
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
            if (entry != null) new Run(sub, Sink, child, Trace, OnNode, _depth + 1).RunExec(entry);
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
                        sb.Append(Vars.TryGetValue(template[(i + 1)..j], out var val) ? val : "");
                        i = j;
                        continue;
                    }
                }
                sb.Append(template[i]);
            }
            return sb.ToString();
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
        public double NowSeconds() => (System.DateTime.UtcNow - System.DateTime.UnixEpoch).TotalSeconds;

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

            if (ctx is "private" or "pm")
            {
                string who = Var("nick");
                if (who.Length > 0) _run.Sink.NoticeTagged(who, text, t);
            }
            else
            {
                string target = Var("replyto");
                if (target.Length == 0) target = Var("channel");
                if (target.Length == 0) target = Var("nick");
                if (target.Length > 0) _run.Sink.PrivmsgTagged(target, text, t);
            }
        }

        private static void Append(StringBuilder sb, string tag) { if (sb.Length > 0) sb.Append(';'); sb.Append(tag); }

        public void React(string emoji)
        {
            string t = Var("replyto"); if (t.Length == 0) t = Var("channel");
            if (t.Length > 0 && emoji.Length > 0) _run.Sink.React(t, Var("msgid"), emoji);
        }
        public void Send(string target, string text) => _run.Sink.Privmsg(target, text);
        public void Notice(string target, string text) => _run.Sink.Notice(target, text);
        public void Join(string channel) => _run.Sink.Join(channel);
        public void Part(string channel, string reason) => _run.Sink.Part(channel, reason);
        public void Raw(string line) => _run.Sink.Raw(line);
        public void StartTyping(string target) => _run.Sink.StartTyping(target);
        public void StopTyping(string target) => _run.Sink.StopTyping(target);
        public void Log(string message, LogLevel level = LogLevel.Action) => _run.Sink.Log(message, level);
    }
}
