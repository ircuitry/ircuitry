using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;

namespace Ircuitry.Graph;

/// <summary>The workflow document: nodes + wires, with connection rules.</summary>
public sealed class NodeGraph
{
    public readonly List<Node> Nodes = new();
    public readonly List<Connection> Connections = new();

    public Node? Find(string id) => Nodes.FirstOrDefault(n => n.Id == id);

    public Node Add(NodeDef def, Vector2 worldPos)
    {
        var n = Node.Create(def, worldPos);
        Nodes.Add(n);
        return n;
    }

    public void Remove(Node n)
    {
        Connections.RemoveAll(c => c.FromNode == n.Id || c.ToNode == n.Id);
        Nodes.Remove(n);
    }

    /// <summary>
    /// Connect output→input with type checking. Input pins hold at most one wire
    /// (the existing one is replaced); output pins may fan out.
    /// </summary>
    public bool Connect(string fromNode, int fromPin, string toNode, int toPin)
    {
        if (fromNode == toNode) return false;
        var a = Find(fromNode); var b = Find(toNode);
        if (a == null || b == null) return false;
        if (fromPin < 0 || fromPin >= a.Def.Outputs.Length) return false;
        if (toPin < 0 || toPin >= b.Def.Inputs.Length) return false;
        if (!Pins.Compatible(a.Def.Outputs[fromPin].Kind, b.Def.Inputs[toPin].Kind)) return false;

        // an input accepts a single wire (unless it's a multi pin like Ask AI's tools) - drop any existing one
        if (!b.Def.Inputs[toPin].Multi)
            Connections.RemoveAll(c => c.ToNode == toNode && c.ToPin == toPin);
        // avoid exact duplicates
        var conn = new Connection(fromNode, fromPin, toNode, toPin);
        if (!Connections.Any(c => c.SameEndpoints(conn))) Connections.Add(conn);
        return true;
    }

    public void Disconnect(Connection c) => Connections.Remove(c);

    public IEnumerable<Connection> FromPin(string node, int pin) =>
        Connections.Where(c => c.FromNode == node && c.FromPin == pin);

    public Connection? IntoPin(string node, int pin) =>
        Connections.FirstOrDefault(c => c.ToNode == node && c.ToPin == pin);

    public bool InputConnected(string node, int pin) => IntoPin(node, pin) != null;
    public bool OutputConnected(string node, int pin) => Connections.Any(c => c.FromNode == node && c.FromPin == pin);

    public IEnumerable<Node> Triggers => Nodes.Where(n => n.Def.IsTrigger);

    public void Clear() { Nodes.Clear(); Connections.Clear(); }

    /// <summary>Replace this graph's contents in place (keeps the reference stable for undo/redo).</summary>
    public void ReplaceWith(NodeGraph other)
    {
        Nodes.Clear(); Nodes.AddRange(other.Nodes);
        Connections.Clear(); Connections.AddRange(other.Connections);
    }
}
