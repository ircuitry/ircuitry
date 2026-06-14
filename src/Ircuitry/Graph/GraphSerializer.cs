using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;

namespace Ircuitry.Graph;

/// <summary>JSON persistence for a workflow (the .ircuitry file format).</summary>
public static class GraphSerializer
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class Doc
    {
        public string format { get; set; } = "ircuitry.workflow.v1";
        public string name { get; set; } = "untitled";
        public List<NodeRec> nodes { get; set; } = new();
        public List<ConnRec> connections { get; set; } = new();
    }

    private sealed class NodeRec
    {
        public string id { get; set; } = "";
        public string type { get; set; } = "";
        public float x { get; set; }
        public float y { get; set; }
        public bool muted { get; set; }
        public bool? streamAsTool { get; set; }
        public string title { get; set; } = "";
        public Dictionary<string, string> @params { get; set; } = new();
    }

    private sealed class ConnRec
    {
        public string from { get; set; } = "";
        public int fromPin { get; set; }
        public string to { get; set; } = "";
        public int toPin { get; set; }
    }

    public static string Save(NodeGraph g, string name)
    {
        var doc = new Doc { name = name };
        foreach (var n in g.Nodes)
            doc.nodes.Add(new NodeRec { id = n.Id, type = n.TypeId, x = n.Pos.X, y = n.Pos.Y, muted = n.Muted, streamAsTool = n.StreamAsTool, title = n.Title, @params = new(n.Params) });
        foreach (var c in g.Connections)
            doc.connections.Add(new ConnRec { from = c.FromNode, fromPin = c.FromPin, to = c.ToNode, toPin = c.ToPin });
        return JsonSerializer.Serialize(doc, Opts);
    }

    /// <summary>Parse a workflow. Unknown node types are skipped (and their wires dropped).</summary>
    public static (NodeGraph graph, string name) Load(string json)
    {
        var doc = JsonSerializer.Deserialize<Doc>(json, Opts) ?? new Doc();
        var g = new NodeGraph();
        var live = new HashSet<string>();
        foreach (var rec in doc.nodes)
        {
            if (!NodeCatalog.TryGet(rec.type, out var def)) continue;
            var n = new Node(rec.id, rec.type) { Def = def, Pos = new Vector2(rec.x, rec.y), Muted = rec.muted, StreamAsTool = rec.streamAsTool ?? def.StreamByDefault, Title = rec.title ?? "" };
            foreach (var p in def.Params) n.Params[p.Key] = rec.@params.TryGetValue(p.Key, out var v) ? v : p.Default;
            g.Nodes.Add(n);
            live.Add(n.Id);
        }
        foreach (var c in doc.connections)
        {
            if (!live.Contains(c.from) || !live.Contains(c.to)) continue;
            // route through Connect so single-wire-input and pin-kind/range rules are enforced
            g.Connect(c.from, c.fromPin, c.to, c.toPin);
        }
        return (g, doc.name);
    }
}
