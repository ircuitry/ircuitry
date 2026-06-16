using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Ircuitry.Graph;

/// <summary>An instance of a <see cref="NodeDef"/> placed on the canvas.</summary>
public sealed class Node
{
    public string Id;
    public string TypeId;
    public Vector2 Pos;                               // world coords (top-left)
    public bool Muted;                                // disabled - skipped during execution
    public bool StreamAsTool;                         // stream this node as a bot-tools workflow step
    public string Title = "";                         // optional human label; blank = use Def.Title
    public Dictionary<string, string> Params = new();

    /// <summary>Resolved at load/spawn time from the catalog. Not serialized.</summary>
    public NodeDef Def = null!;

    /// <summary>The label shown on the card/inspector/history - custom title, or the catalog title.</summary>
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Def.Title : Title;

    /// <summary>This instance's effective pins. Most nodes just expose their <see cref="NodeDef"/> pins, but a
    /// few (e.g. Switch) compute extra pins from their params via <see cref="NodeDef.DynInputs"/>/
    /// <see cref="NodeDef.DynOutputs"/>. Read pins through these everywhere (rendering, wiring, execution,
    /// serialization) so per-instance pins are honoured. Cheap for static nodes (returns the Def array).</summary>
    public PinDef[] Inputs => Def.DynInputs?.Invoke(this) ?? Def.Inputs;
    public PinDef[] Outputs => Def.DynOutputs?.Invoke(this) ?? Def.Outputs;

    private static int _counter;

    public Node(string id, string typeId) { Id = id; TypeId = typeId; }

    public static Node Create(NodeDef def, Vector2 pos)
    {
        var n = new Node($"n{++_counter:x}{System.Environment.TickCount & 0xffff:x}", def.TypeId)
        {
            Def = def,
            Pos = pos,
            StreamAsTool = def.StreamByDefault,
        };
        foreach (var p in def.Params) n.Params[p.Key] = p.Default;
        return n;
    }

    public string GetParam(string key) => Params.TryGetValue(key, out var v) ? v : "";
    public void SetParam(string key, string val) => Params[key] = val;
}
