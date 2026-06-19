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
        public List<FrameRec>? frames { get; set; }   // sticky notes / region frames (absent in old files)
    }

    private sealed class FrameRec
    {
        public string id { get; set; } = "";
        public float x { get; set; }
        public float y { get; set; }
        public float w { get; set; } = 300;
        public float h { get; set; } = 190;
        public string title { get; set; } = "Note";
        public string body { get; set; } = "";
        public int color { get; set; }
        public bool collapsed { get; set; }
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
        public int colorTag { get; set; } = -1;
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
            doc.nodes.Add(new NodeRec { id = n.Id, type = n.TypeId, x = n.Pos.X, y = n.Pos.Y, muted = n.Muted, streamAsTool = n.StreamAsTool, title = n.Title, colorTag = n.ColorTag, @params = new(n.Params) });
        foreach (var c in g.Connections)
            doc.connections.Add(new ConnRec { from = c.FromNode, fromPin = c.FromPin, to = c.ToNode, toPin = c.ToPin });
        if (g.Frames.Count > 0)
            doc.frames = g.Frames.Select(f => new FrameRec { id = f.Id, x = f.Pos.X, y = f.Pos.Y, w = f.Size.X, h = f.Size.Y, title = f.Title, body = f.Body, color = f.ColorIndex, collapsed = f.Collapsed }).ToList();
        return JsonSerializer.Serialize(doc, Opts);
    }

    /// <summary>Parse a workflow. Unknown node types are skipped (and their wires dropped).</summary>
    public static (NodeGraph graph, string name) Load(string json) => Load(json, out _);

    /// <summary>
    /// Parse a workflow, reporting any node types that were skipped because this build does not know
    /// them (an out-of-date app, or a community node that is not installed). Callers can warn the user
    /// instead of silently dropping the nodes and their wires.
    /// </summary>
    public static (NodeGraph graph, string name) Load(string json, out List<string> skippedTypes)
    {
        skippedTypes = new List<string>();
        var doc = JsonSerializer.Deserialize<Doc>(json, Opts) ?? new Doc();
        var g = new NodeGraph();
        var live = new HashSet<string>();
        foreach (var rec in doc.nodes)
        {
            if (!NodeCatalog.TryGet(rec.type, out var def))
            {
                if (!string.IsNullOrEmpty(rec.type) && !skippedTypes.Contains(rec.type)) skippedTypes.Add(rec.type);
                continue;
            }
            var n = new Node(rec.id, rec.type) { Def = def, Pos = new Vector2(rec.x, rec.y), Muted = rec.muted, StreamAsTool = rec.streamAsTool ?? def.StreamByDefault, Title = rec.title ?? "", ColorTag = rec.colorTag };
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
        if (doc.frames != null)
            foreach (var fr in doc.frames)
                g.Frames.Add(new Frame(string.IsNullOrEmpty(fr.id) ? Frame.Create(Vector2.Zero).Id : fr.id)
                { Pos = new Vector2(fr.x, fr.y), Size = new Vector2(fr.w, fr.h), Title = fr.title ?? "Note", Body = fr.body ?? "", ColorIndex = fr.color, Collapsed = fr.collapsed });
        return (g, doc.name);
    }

    /// <summary>A human warning for node types <see cref="Load"/> skipped, or "" if none were skipped.</summary>
    public static string SkippedWarning(List<string> skipped) => skipped.Count == 0 ? ""
        : $"skipped {skipped.Count} unknown node(s) ({string.Join(", ", skipped)}) - update ircuitry to the latest release, or install the missing node, then re-import";
}
