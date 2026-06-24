using Ircuitry.Core;

namespace Ircuitry.Graph;

public enum ParamType { Text, Multiline, Int, Bool, Choice, List }

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

    // ---- ParamType.List: a repeatable list of rows the user grows with an "Add" button ----
    /// <summary>List rows hold two fields (key + value) instead of one.</summary>
    public bool Pair;
    /// <summary>Caption for the list's add button, e.g. "Add tag".</summary>
    public string AddLabel = "Add row";

    /// <summary>A credential: not typed in the clear; the inspector offers a secret picker that stores
    /// the value in secrets.json and writes a <c>{{secret.NAME}}</c> reference here.</summary>
    public bool Secret;

    /// <summary>A file path: the inspector shows a Browse button (native picker) and the node accepts a
    /// file dropped onto it from the OS, which fills this param.</summary>
    public bool File;

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
    public string Icon = "circle";      // Phosphor icon NAME shown on the node + palette (data keeps the name)
    public string? IconImage;           // optional base64 PNG icon (overrides the glyph when set)
    /// <summary>The Icon name resolved to its renderable Phosphor glyph (falls back to the raw string).</summary>
    public string IconGlyph => Ircuitry.Core.Icons.Glyph(Icon);
    public NodeCategory Category;
    public string Description = "";
    public PinDef[] Inputs = System.Array.Empty<PinDef>();
    public PinDef[] Outputs = System.Array.Empty<PinDef>();
    public ParamDef[] Params = System.Array.Empty<ParamDef>();
    public NodeExec Exec = _ => { };

    /// <summary>For composite (subflow) nodes: returns the saved inner graph. Lets the AI-tool harness look
    /// inside a baked node and expose an inner AI Tool as a callable tool. Null for plain/code nodes.</summary>
    public System.Func<NodeGraph>? SubgraphProvider;

    /// <summary>Optional per-instance pins: when set, a node instance computes its OWN pin list from its
    /// params (e.g. Switch grows one exec output per case). The delegate returns the COMPLETE array,
    /// including any fixed prefix. Null means every instance uses the static <see cref="Inputs"/>/
    /// <see cref="Outputs"/> above. Always read pins through <see cref="Node.Inputs"/>/<see cref="Node.Outputs"/>,
    /// never <c>Def.Inputs</c>/<c>Def.Outputs</c>, so dynamic pins are honoured everywhere.</summary>
    public System.Func<Node, PinDef[]>? DynInputs;
    public System.Func<Node, PinDef[]>? DynOutputs;

    /// <summary>True for a synthesized stand-in created when loading a node type this build does not know (a
    /// newer ircuitry, or an uninstalled community node). It is inert - never triggers, no-op exec - and exists
    /// only so the node + its wires survive a load/save round-trip instead of being dropped.</summary>
    public bool IsPlaceholder;

    /// <summary>Key param shown as a one-line summary on the node body.</summary>
    public string? SummaryParam;

    /// <summary>For triggers: the IRC event family that fires this node (e.g. "message", "join", "connect").</summary>
    public string? TriggerEvent;

    /// <summary>Whether new instances default to being streamed as a bot-tools workflow step.</summary>
    public bool StreamByDefault;

    /// <summary>The first file-path param, if any (drives drop-a-file-on-the-node and the inspector Browse button).</summary>
    public ParamDef? FileParam => System.Array.Find(Params, p => p.File);

    public bool IsTrigger => TriggerEvent != null;
    public bool HasExecIn => System.Array.Exists(Inputs, p => p.Kind.IsExec());
    public bool HasExecOut => System.Array.Exists(Outputs, p => p.Kind.IsExec());

    /// <summary>A pure data node has no exec pins at all - it can be pull-evaluated on demand.</summary>
    public bool IsPure => !HasExecIn && !HasExecOut;
}
