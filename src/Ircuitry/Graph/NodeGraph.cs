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
    /// Connect output→input with type checking. A data input holds at most one wire
    /// (the existing one is replaced), while exec inputs (and explicit multi pins like
    /// Ask AI's tools) fan in - many triggers can feed the same downstream flow. Output
    /// pins always fan out.
    /// </summary>
    public bool Connect(string fromNode, int fromPin, string toNode, int toPin)
    {
        if (fromNode == toNode) return false;
        var a = Find(fromNode); var b = Find(toNode);
        if (a == null || b == null) return false;
        var aOut = a.Outputs; var bIn = b.Inputs;
        if (fromPin < 0 || fromPin >= aOut.Length) return false;
        if (toPin < 0 || toPin >= bIn.Length) return false;
        if (!Pins.Compatible(aOut[fromPin].Kind, bIn[toPin].Kind)) return false;

        // a data input takes a single wire (replace the existing one); exec inputs and multi
        // pins accept fan-in, so several upstream sources can drive the same node
        if (!bIn[toPin].Multi && !bIn[toPin].Kind.IsExec())
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
