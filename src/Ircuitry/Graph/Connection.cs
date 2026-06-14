namespace Ircuitry.Graph;

/// <summary>A directed wire from an output pin to an input pin (by index).</summary>
public sealed class Connection
{
    public string FromNode;
    public int FromPin;     // index into the source node's Outputs
    public string ToNode;
    public int ToPin;       // index into the target node's Inputs

    public Connection(string fromNode, int fromPin, string toNode, int toPin)
    {
        FromNode = fromNode; FromPin = fromPin; ToNode = toNode; ToPin = toPin;
    }

    public bool SameEndpoints(Connection o) =>
        FromNode == o.FromNode && FromPin == o.FromPin && ToNode == o.ToNode && ToPin == o.ToPin;
}
