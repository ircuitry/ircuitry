using Ircuitry.Core;

namespace Ircuitry.Graph;

public enum ParamType { Text, Multiline, Int, Bool, Choice }

public sealed class PinDef
{
    public string Name;
    public PinKind Kind;
    public bool Multi;   // an input pin that accepts more than one wire (e.g. Ask AI's tools)
    public PinDef(string name, PinKind kind, bool multi = false) { Name = name; Kind = kind; Multi = multi; }
}

public sealed class ParamDef
{
    public string Key = "";
    public string Label = "";
    public ParamType Type = ParamType.Text;
    public string Default = "";
    public string Placeholder = "";
    public string[]? Choices;

    /// <summary>Optional: hide this field in the inspector unless the predicate (over the node) is true.</summary>
    public System.Func<Node, bool>? VisibleWhen;
}

/// <summary>Executes a node against the live runtime via <see cref="INodeContext"/>.</summary>
public delegate void NodeExec(INodeContext c);

/// <summary>The template for a kind of node: its pins, params and behaviour.</summary>
public sealed class NodeDef
{
    public string TypeId = "";
    public string Title = "";
    public string Subtitle = "";        // shown faint under the title
    public string Icon = "●";           // cute emoji badge shown on the node + palette
    public string? IconImage;           // optional base64 PNG icon (overrides the emoji glyph when set)
    public NodeCategory Category;
    public string Description = "";
    public PinDef[] Inputs = System.Array.Empty<PinDef>();
    public PinDef[] Outputs = System.Array.Empty<PinDef>();
    public ParamDef[] Params = System.Array.Empty<ParamDef>();
    public NodeExec Exec = _ => { };

    /// <summary>Key param shown as a one-line summary on the node body.</summary>
    public string? SummaryParam;

    /// <summary>For triggers: the IRC event family that fires this node (e.g. "message", "join", "connect").</summary>
    public string? TriggerEvent;

    /// <summary>Whether new instances default to being streamed as a bot-tools workflow step.</summary>
    public bool StreamByDefault;

    public bool IsTrigger => TriggerEvent != null;
    public bool HasExecIn => System.Array.Exists(Inputs, p => p.Kind.IsExec());
    public bool HasExecOut => System.Array.Exists(Outputs, p => p.Kind.IsExec());

    /// <summary>A pure data node has no exec pins at all - it can be pull-evaluated on demand.</summary>
    public bool IsPure => !HasExecIn && !HasExecOut;
}
