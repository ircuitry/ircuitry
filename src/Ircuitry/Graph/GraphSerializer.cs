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

    /// <summary>Parse a workflow. Unknown node types are preserved as inert placeholders (with their wires).</summary>
    public static (NodeGraph graph, string name) Load(string json) => Load(json, out _);

    /// <summary>
    /// Parse a workflow. A node type this build does not know (an out-of-date app, or a community node that is
    /// not installed) is NOT dropped: it is loaded as an inert <see cref="NodeDef.IsPlaceholder"/> stand-in that
    /// carries the original type, params and wires verbatim, so a load/save round-trip never deletes a node a
    /// newer ircuitry wrote. <paramref name="unknownTypes"/> lists those types so callers can tell the user to
    /// update or install them.
    /// </summary>
    public static (NodeGraph graph, string name) Load(string json, out List<string> unknownTypes)
    {
        unknownTypes = new List<string>();
        var doc = JsonSerializer.Deserialize<Doc>(json, Opts) ?? new Doc();
        var g = new NodeGraph();
        var live = new HashSet<string>();
        var placeholders = new HashSet<string>();

        // which records are unknown to this build, and how far their wires reach into each, so the placeholder
        // gets real pin slots to carry (and draw) those wires through
        var unknownIds = new HashSet<string>();
        foreach (var rec in doc.nodes)
            if (!string.IsNullOrEmpty(rec.id) && !NodeCatalog.TryGet(rec.type, out _)) unknownIds.Add(rec.id);
        var maxIn = new Dictionary<string, int>();
        var maxOut = new Dictionary<string, int>();
        if (unknownIds.Count > 0)
            foreach (var c in doc.connections)
            {
                if (unknownIds.Contains(c.to)) maxIn[c.to] = System.Math.Max(maxIn.GetValueOrDefault(c.to, -1), c.toPin);
                if (unknownIds.Contains(c.from)) maxOut[c.from] = System.Math.Max(maxOut.GetValueOrDefault(c.from, -1), c.fromPin);
            }

        foreach (var rec in doc.nodes)
        {
            if (!NodeCatalog.TryGet(rec.type, out var def))
            {
                if (!string.IsNullOrEmpty(rec.type) && !unknownTypes.Contains(rec.type)) unknownTypes.Add(rec.type);
                var ph = PlaceholderDef(rec.type, maxIn.GetValueOrDefault(rec.id, -1) + 1, maxOut.GetValueOrDefault(rec.id, -1) + 1);
                var pn = new Node(rec.id, rec.type) { Def = ph, Pos = new Vector2(rec.x, rec.y), Muted = rec.muted, StreamAsTool = rec.streamAsTool ?? false, Title = rec.title ?? "", ColorTag = rec.colorTag };
                foreach (var kv in rec.@params) pn.Params[kv.Key] = kv.Value;   // keep EVERY param verbatim - the real def may want them later
                g.Nodes.Add(pn);
                live.Add(pn.Id);
                placeholders.Add(pn.Id);
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
            if (placeholders.Contains(c.from) || placeholders.Contains(c.to))
            {
                // a placeholder's real pin kinds are unknown, so bypass the compatibility/single-wire rules and
                // preserve the wire exactly as written (the real def re-validates it once the type is known)
                var conn = new Connection(c.from, c.fromPin, c.to, c.toPin);
                if (!g.Connections.Any(x => x.SameEndpoints(conn))) g.Connections.Add(conn);
            }
            // route known<->known through Connect so single-wire-input and pin-kind/range rules are enforced
            else g.Connect(c.from, c.fromPin, c.to, c.toPin);
        }
        if (doc.frames != null)
            foreach (var fr in doc.frames)
                g.Frames.Add(new Frame(string.IsNullOrEmpty(fr.id) ? Frame.Create(Vector2.Zero).Id : fr.id)
                { Pos = new Vector2(fr.x, fr.y), Size = new Vector2(fr.w, fr.h), Title = fr.title ?? "Note", Body = fr.body ?? "", ColorIndex = fr.color, Collapsed = fr.collapsed });
        return (g, doc.name);
    }

    /// <summary>A stand-in for an unknown node type: inert (never triggers, no-op exec) with enough neutral pins
    /// to carry the node's wires, so it renders and round-trips instead of being dropped. <see cref="Load"/>
    /// keeps its params verbatim, and <see cref="Save"/> writes the original type + params straight back.</summary>
    private static NodeDef PlaceholderDef(string typeId, int inputs, int outputs)
    {
        var ins = new PinDef[System.Math.Max(0, inputs)];
        for (int i = 0; i < ins.Length; i++) ins[i] = new PinDef($"in{i}", PinKind.Text);
        var outs = new PinDef[System.Math.Max(0, outputs)];
        for (int i = 0; i < outs.Length; i++) outs[i] = new PinDef($"out{i}", PinKind.Text);
        return new NodeDef
        {
            TypeId = typeId,
            Title = string.IsNullOrEmpty(typeId) ? "unknown" : typeId,
            Subtitle = "unknown node",
            Icon = "warning-circle",
            Category = Ircuitry.Core.NodeCategory.Code,
            Description = "A node type this build does not know - from a newer ircuitry, or a community node you have not installed. It is preserved exactly (type, params and wires) so saving will not delete it; update ircuitry or install the node to edit it.",
            Inputs = ins,
            Outputs = outs,
            IsPlaceholder = true,
        };
    }

    /// <summary>A human warning for unknown node types <see cref="Load"/> preserved as placeholders, or "" if
    /// none. They are kept (saving will not lose them) but cannot be edited until the build catches up.</summary>
    public static string SkippedWarning(List<string> unknown) => unknown.Count == 0 ? ""
        : $"{unknown.Count} unknown node type(s) ({string.Join(", ", unknown)}) are from a newer ircuitry or an uninstalled community node - preserved as placeholders (saving keeps them), but not editable until you update or install them";
}
