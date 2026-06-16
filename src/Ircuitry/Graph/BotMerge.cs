using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;

namespace Ircuitry.Graph;

/// <summary>
/// Merges two or more bots' workflows into one. Nodes from every source are re-ided and combined; the only
/// real clash is two bots binding the SAME command (e.g. both have <c>!help</c>), which the caller resolves
/// per conflict. Pure and deterministic - the UI (app modal) and the website implement the same rules.
/// </summary>
public static class BotMerge
{
    /// <summary>How a single command clash is resolved.</summary>
    public enum Mode { Keep, RunAll, Rename, Combine }

    public sealed class Conflict
    {
        public string Prefix = "";
        public string Command = "";
        public string Key => Prefix + Command;
        public List<int> Bots = new();        // source-bot indices that define this command
        public Mode Resolution = Mode.RunAll; // default: both fire
        public int KeepBot;                   // for Mode.Keep: which source bot wins
        public string Label => (Prefix.Length > 0 ? Prefix : "") + Command;
    }

    /// <summary>Find every command (event.command) bound by more than one source bot.</summary>
    public static List<Conflict> Detect(IReadOnlyList<NodeGraph> graphs)
    {
        // key -> (prefix, command, set of bot indices)
        var map = new Dictionary<string, Conflict>(StringComparer.OrdinalIgnoreCase);
        for (int b = 0; b < graphs.Count; b++)
            foreach (var n in graphs[b].Nodes.Where(IsCommand))
            {
                string prefix = n.GetParam("prefix"), cmd = n.GetParam("command").Trim();
                if (cmd.Length == 0) continue;
                string key = (prefix + cmd).ToLowerInvariant();
                if (!map.TryGetValue(key, out var c)) { c = new Conflict { Prefix = prefix, Command = cmd }; map[key] = c; }
                if (!c.Bots.Contains(b)) c.Bots.Add(b);
            }
        return map.Values.Where(c => c.Bots.Count > 1).OrderBy(c => c.Label).ToList();
    }

    private static bool IsCommand(Node n) => n.TypeId == "event.command";

    /// <summary>Build the merged graph from the sources, applying each resolved conflict.</summary>
    public static NodeGraph Merge(IReadOnlyList<NodeGraph> graphs, IReadOnlyList<Conflict> conflicts)
    {
        var merged = new NodeGraph();
        // per source bot: clone every node with a fresh id (offset so the flows don't overlap), remap wires.
        // botTrigger[b][key] -> the cloned command trigger node for that bot+command (for conflict surgery).
        var botTrigger = new List<Dictionary<string, Node>>();
        for (int b = 0; b < graphs.Count; b++)
        {
            var idMap = new Dictionary<string, string>();
            var offset = new Vector2(0, b * 680f);
            foreach (var n in graphs[b].Nodes)
            {
                var clone = new Node(NewId(), n.TypeId)
                {
                    Def = n.Def, Pos = n.Pos + offset, Muted = n.Muted, StreamAsTool = n.StreamAsTool,
                    Title = n.Title, Params = new Dictionary<string, string>(n.Params),
                };
                idMap[n.Id] = clone.Id;
                merged.Nodes.Add(clone);
            }
            foreach (var c in graphs[b].Connections)
                if (idMap.TryGetValue(c.FromNode, out var f) && idMap.TryGetValue(c.ToNode, out var t))
                    merged.Connections.Add(new Connection(f, c.FromPin, t, c.ToPin));

            var triggers = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in graphs[b].Nodes.Where(IsCommand))
            {
                string cmd = n.GetParam("command").Trim(); if (cmd.Length == 0) continue;
                string key = (n.GetParam("prefix") + cmd).ToLowerInvariant();
                triggers[key] = merged.Find(idMap[n.Id])!;
            }
            botTrigger.Add(triggers);
        }

        foreach (var c in conflicts)
        {
            string key = c.Key.ToLowerInvariant();
            var triggers = c.Bots.Where(b => botTrigger[b].ContainsKey(key)).Select(b => (bot: b, node: botTrigger[b][key])).ToList();
            if (triggers.Count < 2) continue;

            switch (c.Resolution)
            {
                case Mode.RunAll:
                    break;  // keep every trigger - they all fire

                case Mode.Keep:
                {
                    int winner = c.KeepBot;
                    foreach (var (bot, node) in triggers) if (bot != winner) RemoveNode(merged, node);
                    break;
                }

                case Mode.Rename:
                {
                    int n = 2;
                    bool first = true;
                    foreach (var (_, node) in triggers.OrderBy(t => t.bot))
                    {
                        if (first) { first = false; continue; }          // leave the first as the real command
                        node.SetParam("command", c.Command + (n++));     // help -> help2, help3, ...
                    }
                    break;
                }

                case Mode.Combine:
                {
                    // one trigger fans out to every flow: repoint all the others' outgoing wires onto the keeper
                    // (event.command instances share an identical pin layout), then drop the extra triggers.
                    var keeper = triggers.OrderBy(t => t.bot).First().node;
                    foreach (var (_, node) in triggers)
                    {
                        if (node == keeper) continue;
                        for (int i = 0; i < merged.Connections.Count; i++)
                        {
                            var w = merged.Connections[i];
                            if (w.FromNode == node.Id) merged.Connections[i] = new Connection(keeper.Id, w.FromPin, w.ToNode, w.ToPin);
                        }
                        RemoveNode(merged, node);
                    }
                    break;
                }
            }
        }

        PruneOrphans(merged);
        return merged;
    }

    private static void RemoveNode(NodeGraph g, Node n)
    {
        g.Connections.RemoveAll(c => c.FromNode == n.Id || c.ToNode == n.Id);
        g.Nodes.Remove(n);
    }

    /// <summary>Drop nodes that ended up in a connected component with no trigger (e.g. a flow whose command
    /// trigger was removed by a Keep/Combine resolution) - they could never fire.</summary>
    private static void PruneOrphans(NodeGraph g)
    {
        var adj = new Dictionary<string, List<string>>();
        foreach (var n in g.Nodes) adj[n.Id] = new List<string>();
        foreach (var c in g.Connections)
        {
            if (adj.ContainsKey(c.FromNode) && adj.ContainsKey(c.ToNode))
            { adj[c.FromNode].Add(c.ToNode); adj[c.ToNode].Add(c.FromNode); }
        }
        var live = new HashSet<string>();
        var stack = new Stack<string>();
        foreach (var n in g.Nodes) if (n.Def is { IsTrigger: true }) { if (live.Add(n.Id)) stack.Push(n.Id); }
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            foreach (var nb in adj[id]) if (live.Add(nb)) stack.Push(nb);
        }
        // a lone non-trigger pure-data island (no trigger anywhere to reach) is kept only if there were never
        // any triggers; otherwise prune everything not reachable from a trigger.
        if (g.Nodes.Any(n => n.Def is { IsTrigger: true }))
        {
            g.Nodes.RemoveAll(n => !live.Contains(n.Id));
            g.Connections.RemoveAll(c => !live.Contains(c.FromNode) || !live.Contains(c.ToNode));
        }
    }

    private static string NewId() => "m" + Guid.NewGuid().ToString("N").Substring(0, 12);
}
