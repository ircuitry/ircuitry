using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Ircuitry.Core;
using Ircuitry.Graph;
using Ircuitry.Input;
using Ircuitry.Render;
using Ircuitry.Runtime;

namespace Ircuitry.Editor;

/// <summary>
/// The node-graph canvas: pan/zoom camera, node dragging, wire routing,
/// selection and all rendering. Hit-testing is screen-space for ports (forgiving
/// at any zoom) and world-space for node bodies.
/// </summary>
public sealed class GraphEditor
{
    public readonly Camera Cam = new();
    public NodeGraph Graph;
    public readonly HashSet<string> Selection = new();
    private bool _camInit;

    /// <summary>Set by the host: returns 0..1 glow for a node that just executed.</summary>
    public Func<string, float>? FireGlow;
    /// <summary>Set by the host: seconds a node has been executing right now (slow AI/delay/HTTP), or -1 if idle.
    /// Drives a continuous "still working" animation so a long-running node never looks dead.</summary>
    public Func<string, float>? NodeBusy;
    /// <summary>Set by the host: lifetime fire count for a node (badge), and whether a bot is live.</summary>
    public Func<string, int>? FireCount;

    // debug: per-node last-run trace (#16 pin inspector / watch chips) + whole-run trace (#14 why-didn't-fire)
    public Func<string, NodeTrace?>? LastTrace;
    public Func<RunRecord?>? LastRun;
    private readonly HashSet<string> _watches = new();          // node ids whose data outputs are pinned as chips
    public void ToggleWatch(string nodeId) { if (!_watches.Remove(nodeId)) _watches.Add(nodeId); }
    public bool IsWatched(string nodeId) => _watches.Contains(nodeId);

    // why-didn't-this-fire tracer (#14): walk back from a node along exec wires using the last run trace
    private string? _explainCulprit;
    private readonly HashSet<string> _explainPath = new();
    private int _explainFrames;

    public string Explain(string targetId)
    {
        _explainPath.Clear(); _explainCulprit = null; _explainFrames = 0;
        var run = LastRun?.Invoke();
        if (run == null) return "Nothing has run yet - run the bot (or Test) first, then ask again.";
        var ran = new Dictionary<string, NodeTrace>();
        foreach (var t in run.Nodes) ran[t.NodeId] = t;

        _explainFrames = 540;   // glow the trail for ~9s
        if (ran.ContainsKey(targetId)) { _explainCulprit = targetId; _explainPath.Add(targetId); return "This node DID run in the last trace."; }

        string? cur = targetId;
        var seen = new HashSet<string>();
        while (cur != null && seen.Add(cur))
        {
            _explainPath.Add(cur);
            var curNode = Graph.Find(cur);
            var inWire = Graph.Connections.FirstOrDefault(c => c.ToNode == cur && curNode != null && c.ToPin < curNode.Inputs.Length && curNode.Inputs[c.ToPin].Kind == PinKind.Exec);
            if (inWire == null)
            {
                _explainCulprit = cur;
                return curNode?.Def.IsTrigger == true
                    ? $"'{curNode.DisplayTitle}' never fired - no matching event reached it in the last run."
                    : "Nothing is wired into this node's exec input.";
            }
            var src = Graph.Find(inWire.FromNode);
            if (src != null && ran.TryGetValue(src.Id, out var st))
            {
                _explainCulprit = src.Id; _explainPath.Add(src.Id);
                string pin = inWire.FromPin < src.Outputs.Length ? src.Outputs[inWire.FromPin].Name : "out";
                return st.Pulsed.Contains(pin)
                    ? $"'{src.DisplayTitle}' pulsed '{pin}', but the flow stopped before reaching here."
                    : $"'{src.DisplayTitle}' ran but did NOT pulse its '{pin}' output - that branch wasn't taken.";
            }
            cur = src?.Id;
        }
        _explainCulprit = _explainPath.Count > 0 ? null : targetId;
        return "The trigger never fired in the last run (no matching event reached this flow).";
    }

    /// <summary>Reveal a node and flash it red briefly (used by the error tray to jump to an offending node).</summary>
    public void PulseNode(string id, RectF canvas)
    {
        Reveal(id, canvas);
        _explainPath.Clear(); _explainPath.Add(id); _explainCulprit = id; _explainFrames = 150;
    }

    private void DrawExplain(Renderer r)
    {
        if (_explainFrames <= 0) return;
        _explainFrames--;
        float fade = Math.Min(1f, _explainFrames / 60f);
        foreach (var id in _explainPath)
        {
            var n = Graph.Find(id); if (n == null) continue;
            var card = ScreenRect(NodeLayout.For(n).Card);
            bool culprit = id == _explainCulprit;
            r.RoundOutline(card.Inflate(culprit ? 4 : 2, culprit ? 4 : 2), Theme.WithAlpha(Theme.Alert, (culprit ? 0.95f : 0.4f) * fade), 12f * Cam.Zoom + 4);
            if (culprit) r.RoundOutline(card.Inflate(8, 8), Theme.WithAlpha(Theme.Alert, 0.35f * fade), 12f * Cam.Zoom + 8);
        }
    }

    private static bool IsDataPin(PinKind k) => k != PinKind.Exec && k != PinKind.Tool;
    private string OutValue(string nodeId, int pin)
    {
        var n = Graph.Find(nodeId);
        if (n == null || LastTrace == null || pin >= n.Outputs.Length) return "";
        var tr = LastTrace(nodeId);
        if (tr == null) return "";
        string name = n.Outputs[pin].Name;
        foreach (var (p, v) in tr.Outputs) if (p == name) return v;
        return "";
    }
    /// <summary>Set by the host: surface a brief user-facing message (e.g. a paste skipped unknown nodes).</summary>
    public Action<string>? Notify;
    public bool Running;
    public bool ShowMinimap = true;
    // host-set each frame: the rect the bottom-right minimap anchors inside, so it dodges docked panels and
    // the on-map corner buttons instead of hiding behind them. Falls back to the full canvas when unset.
    public RectF MinimapArea; private bool _hasMmArea;
    public void SetMinimapArea(RectF a) { MinimapArea = a; _hasMmArea = true; }
    public bool SnapToGrid;                 // dragged nodes snap to the grid when on

    // minimap interaction (the box is a draggable viewport); mapping is captured each draw
    private bool _minimapDrag, _mmActive;
    private RectF _mmBox;
    private Vector2 _mmMin, _mmOrigin;
    private float _mmScale;
    private Vector2 _mmContentMin, _mmContentMax;   // node bbox only (no viewport) - bounds a minimap drag
    private string? _hoverWire;             // connection key the cursor is closest to (for hover highlight)

    // undo / redo (graph-JSON snapshots) + node clipboard (shared across bots)
    private readonly List<string> _undo = new();
    private readonly List<string> _redo = new();
    private string? _preDrag;
    private bool _wireUndo;
    private static List<Node>? _clipNodes;
    private static List<Connection>? _clipConns;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool CanPaste => _clipNodes is { Count: > 0 };

    private enum Mode { Idle, Panning, DragNodes, DragWire, Box, DragFrame, ResizeFrame }

    // sticky notes / region frames (#7): the selected frame (annotation, surfaced in the inspector) + drag state
    private string? _frameSel;
    private Vector2 _frameDragOff, _frameStartSize;
    private readonly Dictionary<string, Vector2> _frameContained = new();   // node id -> offset, for group-move
    public Frame? SelectedFrame => _frameSel == null ? null : Graph.Frames.FirstOrDefault(f => f.Id == _frameSel);

    // ghost-node suggester (#5): a "+" on a dangling exec output; clicking it asks the host to quick-add a node
    // already wired from that pin. The host reads + clears this each frame.
    public (string node, int pin, Vector2 world)? GhostAdd;
    private bool ShowsGhosts(Node n, Vector2 screen) => (Selection.Count == 1 && Selection.Contains(n.Id)) || NodeLayout.For(n).Card.Contains(Cam.ScreenToWorld(screen));
    private static Vector2 GhostPos(NodeLayout l, int pin) => l.OutPin(pin) + new Vector2(24, 0);
    private (string node, int pin, Vector2 world)? HitGhost(Vector2 screen)
    {
        foreach (var n in Graph.Nodes)
        {
            if (!ShowsGhosts(n, screen)) continue;
            var l = NodeLayout.For(n);
            for (int p = 0; p < n.Outputs.Length; p++)
            {
                if (n.Outputs[p].Kind != PinKind.Exec || Graph.OutputConnected(n.Id, p)) continue;
                var gw = GhostPos(l, p);
                if (Vector2.Distance(screen, Cam.WorldToScreen(gw)) <= 13) return (n.Id, p, gw + new Vector2(150, 15));
            }
        }
        return null;
    }
    public Frame AddFrame(Vector2 worldPos) { var f = Frame.Create(worldPos - new Vector2(150, 15)); Graph.Frames.Add(f); _frameSel = f.Id; Selection.Clear(); return f; }
    public void DeleteFrame(string id) { Graph.Frames.RemoveAll(f => f.Id == id); if (_frameSel == id) _frameSel = null; }
    private const float FrameHandle = 18f;   // resize grip: drawn + grab-zone size, kept constant in screen px
    private string? FrameTitleAt(Vector2 mw) { for (int i = Graph.Frames.Count - 1; i >= 0; i--) if (Graph.Frames[i].TitleBar.Contains(mw)) return Graph.Frames[i].Id; return null; }
    private string? FrameHandleAt(Vector2 mw)
    {
        float h = FrameHandle / MathF.Max(0.01f, Cam.Zoom);   // a constant on-screen grab zone, whatever the zoom
        for (int i = Graph.Frames.Count - 1; i >= 0; i--)
        {
            var f = Graph.Frames[i];
            if (!f.Collapsed && new RectF(f.Pos.X + f.Size.X - h, f.Pos.Y + f.Size.Y - h, h, h).Contains(mw)) return f.Id;
        }
        return null;
    }
    /// <summary>True when the screen-space mouse is over a frame's resize grip (or actively resizing) - drives the SizeNWSE cursor.</summary>
    public bool OverFrameResize(Vector2 screenMouse) => _mode == Mode.ResizeFrame || (_mode == Mode.Idle && FrameHandleAt(Cam.ScreenToWorld(screenMouse)) != null);

    /// <summary>True while the user is actively panning the canvas or dragging nodes (for a "grab" cursor).</summary>
    public bool IsGrabbing => _mode is Mode.Panning or Mode.DragNodes;
    private Mode _mode;

    // drag-nodes
    private Vector2 _dragStartWorld;
    private readonly Dictionary<string, Vector2> _dragOrigin = new();
    public bool Dragged { get; private set; }   // moved meaningfully (suppresses click)
    private string? _collapseNode;              // collapse multi-select to this on a click (no drag)

    // drag-wire
    private string _wireNode = "";
    private int _wirePin;
    private PinKind _wireKind;
    private bool _wireFromOutput;

    // box select
    private Vector2 _boxStart;

    // hover
    private (string node, int pin, bool input)? _hoverPort;

    public GraphEditor(NodeGraph graph) => Graph = graph;

    public Node? SelectedSingle => Selection.Count == 1 ? Graph.Find(Selection.First()) : null;

    public void FocusContent(RectF canvas)
    {
        if (Graph.Nodes.Count == 0) { Cam.CenterOn(Vector2.Zero, canvas.Center); return; }
        var min = new Vector2(float.MaxValue); var max = new Vector2(float.MinValue);
        foreach (var n in Graph.Nodes)
        {
            var l = NodeLayout.For(n);
            min = Vector2.Min(min, l.Card.Pos); max = Vector2.Max(max, new Vector2(l.Card.Right, l.Card.Bottom));
        }
        var center = (min + max) / 2f;
        Cam.Zoom = 1f;
        Cam.CenterOn(center, canvas.Center);
    }

    /// <summary>Centre the camera on a node and select it (used by find-in-graph and the error tray).</summary>
    public void Reveal(string id, RectF canvas)
    {
        var n = Graph.Find(id);
        if (n == null) return;
        if (Cam.Zoom < 0.5f) Cam.Zoom = 0.9f;
        Cam.CenterOn(NodeLayout.For(n).Card.Center, canvas.Center);
        Selection.Clear(); Selection.Add(id);
    }

    public Node Spawn(NodeDef def, Vector2 worldPos)
    {
        PushUndo();
        var n = Graph.Add(def, worldPos - new Vector2(NodeLayout.Width / 2f, NodeLayout.Header / 2f));
        Selection.Clear(); Selection.Add(n.Id);
        BringToFront(n);
        return n;
    }

    // ---- undo / redo ----
    private string Serialize() => GraphSerializer.Save(Graph, "undo");
    public void PushUndo() { _undo.Add(Serialize()); if (_undo.Count > 60) _undo.RemoveAt(0); _redo.Clear(); }
    private void Restore(string snap) { Graph.ReplaceWith(GraphSerializer.Load(snap).graph); Selection.RemoveWhere(id => Graph.Find(id) == null); }
    public void Undo() { if (_undo.Count == 0) return; _redo.Add(Serialize()); var s = _undo[^1]; _undo.RemoveAt(_undo.Count - 1); Restore(s); }
    public void Redo() { if (_redo.Count == 0) return; _undo.Add(Serialize()); var s = _redo[^1]; _redo.RemoveAt(_redo.Count - 1); Restore(s); }

    // ---- clipboard ----
    private static Node CloneForClip(Node n) =>
        new(n.Id, n.TypeId) { Def = n.Def, Pos = n.Pos, Muted = n.Muted, StreamAsTool = n.StreamAsTool, Title = n.Title, ColorTag = n.ColorTag, Params = new Dictionary<string, string>(n.Params) };

    public void CopySelection()
    {
        if (Selection.Count == 0) return;
        var sel = Selection.ToHashSet();
        _clipNodes = Graph.Nodes.Where(n => sel.Contains(n.Id)).Select(CloneForClip).ToList();
        _clipConns = Graph.Connections.Where(c => sel.Contains(c.FromNode) && sel.Contains(c.ToNode))
                          .Select(c => new Connection(c.FromNode, c.FromPin, c.ToNode, c.ToPin)).ToList();

        // also put the .ircbot JSON on the OS clipboard, so a selection can be pasted into another
        // editor instance - or pasted into a chat to share/debug.
        var sub = new NodeGraph();
        sub.Nodes.AddRange(_clipNodes);
        sub.Connections.AddRange(_clipConns);
        try { Clipboard.SetText(GraphSerializer.Save(sub, "clip")); } catch { /* clipboard optional */ }
    }

    public void PasteAtCursor(Vector2 worldCursor)
    {
        // prefer graph JSON on the OS clipboard (lets you paste between editors / from shared JSON)
        var ext = TryLoadClipboardGraph(out var skipped);
        if (skipped.Count > 0) Notify?.Invoke(Ircuitry.Core.Icons.Glyph("warning") + " " + GraphSerializer.SkippedWarning(skipped));
        if (ext != null) { InsertAtCursor(ext.Nodes, ext.Connections, worldCursor); return; }
        if (_clipNodes is { Count: > 0 }) InsertAtCursor(_clipNodes, _clipConns!, worldCursor);
    }

    public void DuplicateSelection()
    {
        if (Selection.Count == 0) return;                 // nothing selected -> nothing to duplicate (was an NRE)
        CopySelection();
        if (_clipNodes is { Count: > 0 }) InsertNodes(_clipNodes, _clipConns!, new Vector2(28, 28));
    }

    /// <summary>Copy the selection to the clipboard, then remove it.</summary>
    public void CutSelection() { if (Selection.Count == 0) return; CopySelection(); DeleteSelection(); }

    /// <summary>Select every node in the graph.</summary>
    public void SelectAll() { Selection.Clear(); foreach (var n in Graph.Nodes) Selection.Add(n.Id); }

    /// <summary>Remove every wire touching the current selection (snip a node loose).</summary>
    public void DisconnectSelection()
    {
        if (Selection.Count == 0) return;
        PushUndo();
        Graph.Connections.RemoveAll(c => Selection.Contains(c.FromNode) || Selection.Contains(c.ToNode));
    }

    /// <summary>True if there are nodes to paste - locally copied, or graph JSON on the OS clipboard.</summary>
    public bool ClipboardHasNodes() => (_clipNodes is { Count: > 0 }) || TryLoadClipboardGraph(out _) != null;

    /// <summary>True if the selection can be saved as a reusable subflow node (contains a Subflow Start).</summary>
    public bool SelectionIsSubflow => Selection.Count > 0 && Graph.Nodes.Any(n => Selection.Contains(n.Id) && n.TypeId == "flow.in");

    /// <summary>Serialize the selection into a reusable-subflow node manifest (.ircnode JSON). Pins are derived
    /// from the Subflow Input/Output nodes inside it. Returns null if there's no Subflow Start (flow.in) entry.</summary>
    public string? SaveSelectionAsNode(string title, bool asTool = false)
    {
        var sel = Selection.ToHashSet();
        var nodes = Graph.Nodes.Where(n => sel.Contains(n.Id)).ToList();
        if (!nodes.Any(n => n.TypeId == "flow.in")) return null;

        var sub = new NodeGraph();
        foreach (var n in nodes) sub.Nodes.Add(CloneForClip(n));
        foreach (var c in Graph.Connections)
            if (sel.Contains(c.FromNode) && sel.Contains(c.ToNode))
                sub.Connections.Add(new Connection(c.FromNode, c.FromPin, c.ToNode, c.ToPin));

        static string J(string s) => System.Text.Json.JsonSerializer.Serialize(s);
        var inputs = new List<string> { "{\"name\":\"\",\"kind\":\"Exec\"}" };
        foreach (var a in nodes.Where(n => n.TypeId == "flow.arg").Select(n => n.GetParam("name")).Where(s => s.Length > 0).Distinct())
            inputs.Add("{\"name\":" + J(a) + ",\"kind\":\"Text\"}");
        var outputs = new List<string> { "{\"name\":\"then\",\"kind\":\"Exec\"}" };
        foreach (var rr in nodes.Where(n => n.TypeId == "flow.return").Select(n => n.GetParam("name")).Where(s => s.Length > 0).Distinct())
            outputs.Add("{\"name\":" + J(rr) + ",\"kind\":\"Text\"}");
        if (asTool) outputs.Add("{\"name\":\"tool\",\"kind\":\"Tool\"}");

        var slug = new string(title.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        if (slug.Length == 0) slug = "node";
        return "{\"typeId\":" + J("subflow." + slug) + ",\"title\":" + J(title)
            + ",\"subtitle\":\"subflow\",\"icon\":\"puzzle-piece\",\"category\":\"Logic\","
            + "\"inputs\":[" + string.Join(",", inputs) + "],\"outputs\":[" + string.Join(",", outputs) + "],"
            + "\"subgraph\":" + GraphSerializer.Save(sub, title) + "}";
    }

    /// <summary>True once at least one ordinary node is selected (so it can be baked into a composite).</summary>
    public bool SelectionCanBake => Selection.Count > 0 && Graph.Nodes.Any(n => Selection.Contains(n.Id) && !n.Def.IsTrigger);

    /// <summary>Serialize this editor's WHOLE graph into a composite (subgraph) node manifest - used by the
    /// in-modal mini editor. Pins come from the Subflow Input/Output nodes in it; <paramref name="exposed"/>
    /// (name -> default) become the composite node's user-editable params, which inner nodes read as {name}.
    /// Needs a Subflow Start.</summary>
    public string? SerializeAsComposite(string typeId, string title, string icon, string category, string description,
        System.Collections.Generic.IReadOnlyDictionary<string, string>? exposed = null, bool asTool = false)
    {
        var nodes = Graph.Nodes;
        if (!nodes.Any(n => n.TypeId == "flow.in")) return null;
        var sub = new NodeGraph();
        foreach (var n in nodes) sub.Nodes.Add(CloneForClip(n));
        foreach (var c in Graph.Connections) sub.Connections.Add(new Connection(c.FromNode, c.FromPin, c.ToNode, c.ToPin));

        static string J(string s) => System.Text.Json.JsonSerializer.Serialize(s);
        var inputs = new List<string> { "{\"name\":\"\",\"kind\":\"Exec\"}" };
        foreach (var a in nodes.Where(n => n.TypeId == "flow.arg").Select(n => n.GetParam("name")).Where(s => s.Length > 0).Distinct())
            inputs.Add("{\"name\":" + J(a) + ",\"kind\":\"Text\"}");
        var outputs = new List<string> { "{\"name\":\"then\",\"kind\":\"Exec\"}" };
        foreach (var rr in nodes.Where(n => n.TypeId == "flow.return").Select(n => n.GetParam("name")).Where(s => s.Length > 0).Distinct())
            outputs.Add("{\"name\":" + J(rr) + ",\"kind\":\"Text\"}");
        if (asTool) outputs.Add("{\"name\":\"tool\",\"kind\":\"Tool\"}");   // makes the node wireable into Ask AI

        var prms = new List<string>();
        if (exposed != null)
            foreach (var kv in exposed)
            {
                string label = kv.Key.Length > 0 ? char.ToUpperInvariant(kv.Key[0]) + kv.Key[1..] : kv.Key;
                prms.Add("{\"key\":" + J(kv.Key) + ",\"label\":" + J(label) + ",\"type\":\"Text\",\"default\":" + J(kv.Value) + "}");
            }

        return "{\"typeId\":" + J(typeId) + ",\"title\":" + J(title) + ",\"subtitle\":\"composite\",\"icon\":" + J(icon)
            + ",\"category\":" + J(category) + ",\"description\":" + J(description ?? "")
            + ",\"inputs\":[" + string.Join(",", inputs) + "],\"outputs\":[" + string.Join(",", outputs) + "]"
            + ",\"params\":[" + string.Join(",", prms) + "],"
            + "\"subgraph\":" + GraphSerializer.Save(sub, title) + "}";
    }

    /// <summary>
    /// "Bake" a selection of ordinary nodes into ONE reusable composite (subgraph) node, deriving its pins
    /// from the wires that cross the selection boundary - so you never have to place Subflow Input/Output
    /// nodes by hand. Returns the .ircnode manifest, or null with a reason in <paramref name="error"/>.
    /// </summary>
    public string? BuildCompositeFromSelection(string title, string icon, string category, string description, bool asTool, out string error)
    {
        error = "";
        var sel = Selection.ToHashSet();
        // a composite runs as a subflow (from flow.in), so drop triggers and any existing subflow boundary nodes
        var nodes = Graph.Nodes.Where(n => sel.Contains(n.Id) && !n.Def.IsTrigger
            && n.TypeId is not ("flow.in" or "flow.arg" or "flow.return")).ToList();
        if (nodes.Count == 0) { error = "Pick a few wired-up nodes to bake together first (triggers can't go inside)."; return null; }
        var ids = nodes.Select(n => n.Id).ToHashSet();

        PinKind? InKind(string id, int p) { var n = nodes.FirstOrDefault(x => x.Id == id); if (n == null) return null; var ins = n.Inputs; return p >= 0 && p < ins.Length ? ins[p].Kind : null; }
        PinKind? OutKind(string id, int p) { var n = nodes.FirstOrDefault(x => x.Id == id); if (n == null) return null; var outs = n.Outputs; return p >= 0 && p < outs.Length ? outs[p].Kind : null; }

        var sub = new NodeGraph();
        foreach (var n in nodes) sub.Nodes.Add(CloneForClip(n));
        foreach (var c in Graph.Connections)
            if (ids.Contains(c.FromNode) && ids.Contains(c.ToNode))
                sub.Connections.Add(new Connection(c.FromNode, c.FromPin, c.ToNode, c.ToPin));

        var inbound = Graph.Connections.Where(c => !ids.Contains(c.FromNode) && ids.Contains(c.ToNode)).ToList();
        var outbound = Graph.Connections.Where(c => ids.Contains(c.FromNode) && !ids.Contains(c.ToNode)).ToList();

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Uniq(string b) { var nm = b.Length > 0 ? b : "x"; var s = nm; int i = 2; while (!used.Add(s)) s = nm + i++; return s; }

        var inPins = new List<(string name, PinKind kind)>();
        var outPins = new List<(string name, PinKind kind)>();

        var flowIn = sub.Add(NodeCatalog.Get("flow.in"), new Vector2(-280, 0));
        // exec-in pins with nothing feeding them (internally) are the entry points the Start drives
        var fedExec = new HashSet<(string, int)>(sub.Connections.Where(c => InKind(c.ToNode, c.ToPin) == PinKind.Exec).Select(c => (c.ToNode, c.ToPin)));
        foreach (var n in nodes)
        {
            var ins = n.Inputs;
            for (int p = 0; p < ins.Length; p++)
                if (ins[p].Kind == PinKind.Exec && !fedExec.Contains((n.Id, p)))
                    sub.Connect(flowIn.Id, 0, n.Id, p);
        }

        float ay = -160;
        foreach (var c in inbound)
        {
            if (InKind(c.ToNode, c.ToPin) is not { } k || k == PinKind.Exec) continue;   // exec inbound -> flow.in already
            var pinName = nodes.First(n => n.Id == c.ToNode).Inputs[c.ToPin].Name;
            var name = Uniq(pinName.Length > 0 ? pinName : "in");
            var arg = sub.Add(NodeCatalog.Get("flow.arg"), new Vector2(-280, ay)); ay += 64;
            arg.SetParam("name", name);
            sub.Connect(arg.Id, 0, c.ToNode, c.ToPin);
            inPins.Add((name, k));
        }

        var outSrc = outbound.Where(c => OutKind(c.FromNode, c.FromPin) is { } k && k != PinKind.Exec)
                             .Select(c => (c.FromNode, c.FromPin)).Distinct().ToList();
        var returns = new List<Node>();
        float ry = 120;
        foreach (var (fromNode, fromPin) in outSrc)
        {
            var name = Uniq(nodes.First(n => n.Id == fromNode).Outputs[fromPin].Name);
            var ret = sub.Add(NodeCatalog.Get("flow.return"), new Vector2(340, ry)); ry += 64;
            ret.SetParam("name", name);
            sub.Connect(fromNode, fromPin, ret.Id, 1);   // -> value
            returns.Add(ret);
            outPins.Add((name, OutKind(fromNode, fromPin)!.Value));
        }

        // make the Subflow Outputs actually run: chain them after the internal exec leaves, or straight off
        // the Start when the selection is pure data (so the returns pull their values)
        if (returns.Count > 0)
        {
            var leaf = nodes.SelectMany(n => Enumerable.Range(0, n.Outputs.Length)
                          .Where(p => n.Outputs[p].Kind == PinKind.Exec && !sub.Connections.Any(c => c.FromNode == n.Id && c.FromPin == p))
                          .Select(p => (n.Id, p))).FirstOrDefault();
            if (leaf.Id != null) sub.Connect(leaf.Id, leaf.p, returns[0].Id, 0);
            else sub.Connect(flowIn.Id, 0, returns[0].Id, 0);
            for (int i = 0; i < returns.Count - 1; i++) sub.Connect(returns[i].Id, 0, returns[i + 1].Id, 0);
        }

        static string J(string s) => System.Text.Json.JsonSerializer.Serialize(s);
        var inputs = new List<string> { "{\"name\":\"\",\"kind\":\"Exec\"}" };
        foreach (var (name, kind) in inPins) inputs.Add("{\"name\":" + J(name) + ",\"kind\":" + J(kind.ToString()) + "}");
        var outputs = new List<string> { "{\"name\":\"then\",\"kind\":\"Exec\"}" };
        foreach (var (name, kind) in outPins) outputs.Add("{\"name\":" + J(name) + ",\"kind\":" + J(kind.ToString()) + "}");
        if (asTool && !outPins.Any(o => o.kind == PinKind.Tool)) outputs.Add("{\"name\":\"tool\",\"kind\":\"Tool\"}");

        var slug = new string(title.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        if (slug.Length == 0) slug = "node";
        string cat = string.IsNullOrWhiteSpace(category) ? "Logic" : category;
        string ic = string.IsNullOrWhiteSpace(icon) ? "puzzle-piece" : icon;
        return "{\"typeId\":" + J("subflow." + slug) + ",\"title\":" + J(title)
            + ",\"subtitle\":\"composite\",\"icon\":" + J(ic) + ",\"category\":" + J(cat) + ",\"description\":" + J(description ?? "")
            + ",\"inputs\":[" + string.Join(",", inputs) + "],\"outputs\":[" + string.Join(",", outputs) + "],"
            + "\"subgraph\":" + GraphSerializer.Save(sub, title) + "}";
    }

    /// <summary>Load a whole graph (e.g. a dropped .ircbot) into the current workflow at a screen point.</summary>
    public void InsertGraphAt(NodeGraph g, Vector2 screen) => InsertAtCursor(g.Nodes, g.Connections, Cam.ScreenToWorld(screen));

    /// <summary>The current selection serialized as a reusable .ircbot fragment (for the snippet shelf), or null.</summary>
    public string? SerializeSelection()
    {
        if (Selection.Count == 0) return null;
        var sel = Selection.ToHashSet();
        var sub = new NodeGraph();
        sub.Nodes.AddRange(Graph.Nodes.Where(n => sel.Contains(n.Id)).Select(CloneForClip));
        sub.Connections.AddRange(Graph.Connections.Where(c => sel.Contains(c.FromNode) && sel.Contains(c.ToNode))
            .Select(c => new Connection(c.FromNode, c.FromPin, c.ToNode, c.ToPin)));
        return GraphSerializer.Save(sub, "snippet");
    }

    /// <summary>Drop a saved snippet (a .ircbot fragment) as a fresh, re-id'd, still-editable copy at a world point.</summary>
    public void InsertSnippet(string json, Vector2 worldCursor)
    {
        var (g, _) = GraphSerializer.Load(json);
        if (g.Nodes.Count == 0) return;
        PushUndo();
        InsertAtCursor(g.Nodes, g.Connections, worldCursor);
    }

    private static NodeGraph? TryLoadClipboardGraph(out List<string> skipped)
    {
        skipped = new List<string>();
        try
        {
            var t = Clipboard.GetText();
            if (t.Length == 0 || t.IndexOf("\"nodes\"", StringComparison.Ordinal) < 0) return null;
            var (g, _) = GraphSerializer.Load(t, out skipped);
            return g.Nodes.Count > 0 ? g : null;
        }
        catch { return null; }
    }

    private void InsertAtCursor(IReadOnlyList<Node> nodes, IReadOnlyList<Connection> conns, Vector2 worldCursor)
    {
        if (nodes.Count == 0) return;
        var min = new Vector2(float.MaxValue);
        foreach (var n in nodes) min = Vector2.Min(min, n.Pos);
        InsertNodes(nodes, conns, worldCursor - min);
    }

    private void InsertNodes(IReadOnlyList<Node> nodes, IReadOnlyList<Connection> conns, Vector2 off)
    {
        if (nodes.Count == 0) return;
        PushUndo();
        var map = new Dictionary<string, string>();
        Selection.Clear();
        foreach (var src in nodes)
        {
            var n = Graph.Add(src.Def, src.Pos + off);
            foreach (var kv in src.Params) n.Params[kv.Key] = kv.Value;
            n.Muted = src.Muted;
            n.StreamAsTool = src.StreamAsTool;
            n.Title = src.Title;
            n.ColorTag = src.ColorTag;
            map[src.Id] = n.Id;
            Selection.Add(n.Id);
        }
        foreach (var c in conns)
            if (map.TryGetValue(c.FromNode, out var f) && map.TryGetValue(c.ToNode, out var t))
                Graph.Connect(f, c.FromPin, t, c.ToPin);
        BringSelectionToFront();
    }

    private void BringSelectionToFront()
    {
        var sel = Graph.Nodes.Where(n => Selection.Contains(n.Id)).ToList();
        foreach (var n in sel) { Graph.Nodes.Remove(n); Graph.Nodes.Add(n); }
    }

    /// <summary>Tidy the graph into clean left-to-right layers (longest-path layering), undoable.</summary>
    public void AutoLayout()
    {
        if (Graph.Nodes.Count == 0) return;
        PushUndo();

        var depth = new Dictionary<string, int>();
        foreach (var n in Graph.Nodes) depth[n.Id] = 0;
        for (int iter = 0; iter < Graph.Nodes.Count; iter++)   // longest-path; capped so cycles can't spin
        {
            bool changed = false;
            foreach (var c in Graph.Connections)
                if (depth.TryGetValue(c.FromNode, out var df) && depth.TryGetValue(c.ToNode, out var dt) && df + 1 > dt)
                { depth[c.ToNode] = df + 1; changed = true; }
            if (!changed) break;
        }

        var layers = new Dictionary<int, List<Node>>();
        foreach (var n in Graph.Nodes)
        {
            if (!layers.TryGetValue(depth[n.Id], out var list)) layers[depth[n.Id]] = list = new();
            list.Add(n);
        }

        const float colGap = 90f, rowGap = 46f;
        float x = 0;
        foreach (var layer in layers.Keys.OrderBy(k => k))
        {
            var nodes = layers[layer];
            nodes.Sort((a, b) => a.Pos.Y.CompareTo(b.Pos.Y));   // keep the author's vertical ordering
            float colW = 0, y = 0;
            foreach (var n in nodes) colW = MathF.Max(colW, NodeLayout.For(n).Card.W);
            foreach (var n in nodes) { n.Pos = new Vector2(x, y); y += NodeLayout.For(n).Card.H + rowGap; }
            x += colW + colGap;
        }

        // tool nodes flow vertically: park each directly below the AI node it feeds (stacked if several)
        var toolY = new Dictionary<string, float>();
        foreach (var c in Graph.Connections)
        {
            var from = Graph.Find(c.FromNode); var to = Graph.Find(c.ToNode);
            if (from == null || to == null || c.FromPin >= from.Outputs.Length || from.Outputs[c.FromPin].Kind != PinKind.Tool) continue;
            float baseY = toolY.TryGetValue(to.Id, out var yy) ? yy : NodeLayout.For(to).Card.Bottom + rowGap;
            from.Pos = new Vector2(to.Pos.X, baseY);
            toolY[to.Id] = baseY + NodeLayout.For(from).Card.H + rowGap;
        }
        Selection.Clear();
    }

    public void ToggleMuteSelection()
    {
        if (Selection.Count == 0) return;
        PushUndo();
        bool anyOn = Selection.Select(Graph.Find).Any(n => n is { Muted: false });
        foreach (var id in Selection) { var n = Graph.Find(id); if (n != null) n.Muted = anyOn; }
    }

    /// <summary>True when nothing (node or port) sits under the given screen point - used for quick-add.</summary>
    public bool IsEmptyAt(Vector2 screen) => HitPort(screen) == null && HitNode(Cam.ScreenToWorld(screen)) == null;

    /// <summary>The node under a screen point, or null - used for drag-and-drop onto a node.</summary>
    public Node? NodeAt(Vector2 screen) => HitNode(Cam.ScreenToWorld(screen));

    /// <summary>The on-screen rectangle of a node's card (for tutorial spotlights).</summary>
    public RectF NodeScreenRect(Node n) => ScreenRect(NodeLayout.For(n).Card);

    // ===================================================================
    public void Update(InputState input, RectF canvas, bool uiCapturing)
    {
        if (!_camInit) { Cam.CenterOn(Vector2.Zero, canvas.Center); _camInit = true; }

        // panels overlay the full-bleed map now, so a click "in the canvas" that's really on a panel (or a
        // panel being dragged) must NOT pan/select the map
        bool inCanvas = canvas.Contains(input.Mouse) && !uiCapturing;
        Vector2 mw = Cam.ScreenToWorld(input.Mouse);

        if (inCanvas && input.ScrollDelta != 0) Cam.ZoomAt(input.Mouse, input.ScrollDelta / 120f);

        _hoverPort = inCanvas ? HitPort(input.Mouse) : null;

        // minimap: click or drag inside the box to pan the viewport there
        if (_mmActive && !uiCapturing)
        {
            if (input.LeftPressed && _mmBox.Contains(input.Mouse)) _minimapDrag = true;
            if (_minimapDrag)
            {
                if (input.LeftDown)
                {
                    // Clamp the target to the node bbox (+ a small margin) so the view stays tethered
                    // to content. Without this the minimap bounds include the viewport, so dragging at
                    // the edge pushed the view out, which grew the bounds, which flung it further still -
                    // a runaway that ended in a frozen machine.
                    var target = _mmMin + (input.Mouse - _mmOrigin) / _mmScale;
                    var m = (_mmContentMax - _mmContentMin) * 0.15f + new Vector2(120);
                    var lo = _mmContentMin - m; var hi = _mmContentMax + m;
                    target = Vector2.Clamp(target, lo, hi);
                    Cam.CenterOn(target, canvas.Center);
                    Cam.Sanitize(canvas);
                }
                else _minimapDrag = false;
                _hoverPort = null;
                return;   // the minimap owns the mouse this frame
            }
        }

        // hover-highlight the wire nearest the cursor (for legibility in dense graphs)
        _hoverWire = (inCanvas && _mode == Mode.Idle && _hoverPort == null) ? NearestWire(input.Mouse) : null;

        switch (_mode)
        {
            case Mode.Idle:
                // middle-drag (or Space + left-drag) always pans; otherwise a plain left-drag on empty space
                // pans and Shift + left-drag boxes-selects (decided in BeginLeftPress)
                bool panBtn = input.MiddleDown || (input.LeftDown && input.KeyDown(Keys.Space));
                if (inCanvas && panBtn) { _mode = Mode.Panning; break; }
                if (inCanvas && input.LeftPressed) BeginLeftPress(input, mw);
                break;

            case Mode.Panning:
                Cam.PanBy(input.MouseDelta);
                if (!input.MiddleDown && !input.LeftDown) _mode = Mode.Idle;
                break;

            case Mode.DragNodes:
                var delta = mw - _dragStartWorld;
                if (delta.LengthSquared() > 4) Dragged = true;
                foreach (var kv in _dragOrigin) { var n = Graph.Find(kv.Key); if (n != null) n.Pos = SnapToGrid ? SnapPos(kv.Value + delta) : kv.Value + delta; }
                if (input.LeftReleased)
                {
                    if (Dragged && _preDrag != null) { _undo.Add(_preDrag); if (_undo.Count > 60) _undo.RemoveAt(0); _redo.Clear(); }
                    _preDrag = null;
                    if (!Dragged && _collapseNode != null) { Selection.Clear(); Selection.Add(_collapseNode); }
                    _collapseNode = null;
                    _mode = Mode.Idle;
                }
                break;

            case Mode.DragWire:
                if (input.LeftReleased) { CommitWire(input.Mouse); _mode = Mode.Idle; }
                break;

            case Mode.Box:
                if (input.LeftReleased) { CommitBox(mw, input.Shift); _mode = Mode.Idle; }
                break;

            case Mode.DragFrame:
            {
                var fr = SelectedFrame;
                if (fr == null) { _mode = Mode.Idle; break; }
                var np = mw - _frameDragOff;
                if ((np - fr.Pos).LengthSquared() > 1) Dragged = true;
                fr.Pos = SnapToGrid ? SnapPos(np) : np;
                foreach (var kv in _frameContained) { var n = Graph.Find(kv.Key); if (n != null) n.Pos = fr.Pos + kv.Value; }   // carry nodes on the frame
                if (input.LeftReleased) { CommitFrameDrag(); _mode = Mode.Idle; }
                break;
            }

            case Mode.ResizeFrame:
            {
                var fr = SelectedFrame;
                if (fr == null) { _mode = Mode.Idle; break; }
                var d = mw - _dragStartWorld;
                if (d.LengthSquared() > 1) Dragged = true;
                fr.Size = new Vector2(MathF.Max(Frame.MinW, _frameStartSize.X + d.X), MathF.Max(Frame.MinH, _frameStartSize.Y + d.Y));
                if (input.LeftReleased) { CommitFrameDrag(); _mode = Mode.Idle; }
                break;
            }
        }

        if (!uiCapturing && _mode == Mode.Idle && (input.KeyPressed(Keys.Delete) || input.KeyPressed(Keys.Back)))
        {
            if (_frameSel != null) { PushUndo(); DeleteFrame(_frameSel); }
            else DeleteSelection();
        }
        if (!uiCapturing && input.Ctrl)
        {
            if (input.KeyPressed(Keys.A)) { Selection.Clear(); foreach (var n in Graph.Nodes) Selection.Add(n.Id); }
            else if (input.KeyPressed(Keys.Z)) { if (input.Shift) Redo(); else Undo(); }
            else if (input.KeyPressed(Keys.Y)) Redo();
            else if (input.KeyPressed(Keys.C)) CopySelection();
            else if (input.KeyPressed(Keys.V) && !input.Shift) PasteAtCursor(mw);   // Ctrl+Shift+V is install-node-from-clipboard
            else if (input.KeyPressed(Keys.D)) DuplicateSelection();
        }
        if (!uiCapturing && !input.Ctrl && _mode == Mode.Idle && input.KeyPressed(Keys.M))
            ToggleMuteSelection();
        if (!uiCapturing && !input.Ctrl && _mode == Mode.Idle && input.KeyPressed(Keys.F))
            FrameSelection(canvas);

        Cam.Sanitize(canvas);   // last line of defence: keep the camera finite and within world bounds
    }

    private static Vector2 SnapPos(Vector2 v) { const float g = 28f; return new(MathF.Round(v.X / g) * g, MathF.Round(v.Y / g) * g); }

    /// <summary>Zoom/pan to fit the current selection (or the whole graph if nothing is selected).</summary>
    public void FrameSelection(RectF canvas)
    {
        var list = (Selection.Count > 0 ? Graph.Nodes.Where(n => Selection.Contains(n.Id)) : Graph.Nodes).ToList();
        if (list.Count == 0) { Cam.CenterOn(Vector2.Zero, canvas.Center); return; }
        var min = new Vector2(float.MaxValue); var max = new Vector2(float.MinValue);
        foreach (var n in list) { var l = NodeLayout.For(n); min = Vector2.Min(min, l.Card.Pos); max = Vector2.Max(max, new Vector2(l.Card.Right, l.Card.Bottom)); }
        var span = Vector2.Max(max - min, new Vector2(1));
        float z = MathF.Min((canvas.W - 100f) / span.X, (canvas.H - 100f) / span.Y);
        Cam.Zoom = Math.Clamp(z, Camera.MinZoom, Camera.MaxZoom);
        Cam.CenterOn((min + max) / 2f, canvas.Center);
    }

    private static string WireKey(Connection c) => $"{c.FromNode}:{c.FromPin}>{c.ToNode}:{c.ToPin}";

    // the connection whose routed path passes closest to the cursor, within a small screen threshold
    /// <summary>The connection whose route passes under <paramref name="screen"/> (for drop-on-wire splicing), or null.</summary>
    public Connection? WireUnder(Vector2 screen)
    {
        Connection? best = null; float bestD = 11f;
        foreach (var c in Graph.Connections)
        {
            var a = Graph.Find(c.FromNode); var b = Graph.Find(c.ToNode);
            if (a == null || b == null || c.FromPin >= a.Outputs.Length || c.ToPin >= b.Inputs.Length) continue;
            var world = WorldRoute(c);
            for (int i = 1; i < world.Count; i++)
            {
                float d = DistToSegment(screen, Cam.WorldToScreen(world[i - 1]), Cam.WorldToScreen(world[i]));
                if (d < bestD) { bestD = d; best = c; }
            }
        }
        return best;
    }

    /// <summary>Drop a new node onto a wire: spawn it and re-route source -> new -> destination (the first
    /// pin-kind-compatible pins on each side), so it's spliced inline.</summary>
    public Node SpliceOnWire(Connection c, NodeDef def, Vector2 world)
    {
        PushUndo();
        string srcN = c.FromNode; int srcP = c.FromPin, dstP = c.ToPin; string dstN = c.ToNode;
        var n = Spawn(def, world);
        Graph.Disconnect(c);
        for (int i = 0; i < n.Inputs.Length; i++) if (Graph.Connect(srcN, srcP, n.Id, i)) break;    // source -> new
        for (int j = 0; j < n.Outputs.Length; j++) if (Graph.Connect(n.Id, j, dstN, dstP)) break;   // new -> destination
        Selection.Clear(); Selection.Add(n.Id);
        return n;
    }

    private string? NearestWire(Vector2 screen)
    {
        string? best = null; float bestD = 10f;   // px
        foreach (var c in Graph.Connections)
        {
            var a = Graph.Find(c.FromNode); var b = Graph.Find(c.ToNode);
            if (a == null || b == null || a.Muted || b.Muted) continue;
            if (c.FromPin >= a.Outputs.Length || c.ToPin >= b.Inputs.Length) continue;
            var world = WorldRoute(c);
            for (int i = 1; i < world.Count; i++)
            {
                float d = DistToSegment(screen, Cam.WorldToScreen(world[i - 1]), Cam.WorldToScreen(world[i]));
                if (d < bestD) { bestD = d; best = WireKey(c); }
            }
        }
        return best;
    }

    private static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a; float len2 = ab.LengthSquared();
        float t = len2 < 1e-4f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    private void CommitFrameDrag()
    {
        if (Dragged && _preDrag != null) { _undo.Add(_preDrag); if (_undo.Count > 60) _undo.RemoveAt(0); _redo.Clear(); }
        _preDrag = null;
    }

    private void BeginLeftPress(InputState input, Vector2 mw)
    {
        Dragged = false;
        _wireUndo = false;
        _frameSel = null;            // any press resets frame selection unless a frame is hit below
        if (HitGhost(input.Mouse) is { } ghost) { GhostAdd = ghost; return; }   // clicked a "+" suggestion
        var port = HitPort(input.Mouse);
        if (port.HasValue)
        {
            var (node, pin, isInput) = port.Value;
            var nd = Graph.Find(node)!;
            if (!isInput) StartWire(node, pin, nd.Outputs[pin].Kind, true);
            else
            {
                var existing = Graph.IntoPin(node, pin);
                if (existing != null)
                {
                    PushUndo(); _wireUndo = true;
                    Graph.Disconnect(existing);
                    var src = Graph.Find(existing.FromNode);
                    var kind = src != null && existing.FromPin < src.Outputs.Length
                        ? src.Outputs[existing.FromPin].Kind : nd.Inputs[pin].Kind;
                    StartWire(existing.FromNode, existing.FromPin, kind, true);   // colour by source output
                }
                else StartWire(node, pin, nd.Inputs[pin].Kind, false);
            }
            return;
        }

        var hit = HitNode(mw);
        if (hit != null)
        {
            bool already = Selection.Contains(hit.Id);
            if (input.Shift) { if (!Selection.Add(hit.Id)) Selection.Remove(hit.Id); }
            else if (!already) { Selection.Clear(); Selection.Add(hit.Id); }
            // clicking (not dragging) an already-selected node in a multi-selection collapses to just it
            _collapseNode = (!input.Shift && already && Selection.Count > 1) ? hit.Id : null;
            BringToFront(hit);
            _mode = Mode.DragNodes;
            _preDrag = Serialize();
            _dragStartWorld = mw;
            _dragOrigin.Clear();
            foreach (var id in Selection) { var n = Graph.Find(id); if (n != null) _dragOrigin[id] = n.Pos; }
        }
        else if (FrameHandleAt(mw) is { } rfId)   // bottom-right corner of a frame = resize
        {
            var fr = Graph.Frames.First(f => f.Id == rfId);
            _frameSel = rfId; Selection.Clear();
            _mode = Mode.ResizeFrame; _preDrag = Serialize(); _frameStartSize = fr.Size; _dragStartWorld = mw;
        }
        else if (FrameTitleAt(mw) is { } tfId)    // frame title bar = select + move (carrying the nodes on it)
        {
            var fr = Graph.Frames.First(f => f.Id == tfId);
            _frameSel = tfId; Selection.Clear();
            Graph.Frames.Remove(fr); Graph.Frames.Add(fr);   // bring to front
            _mode = Mode.DragFrame; _preDrag = Serialize(); _frameDragOff = mw - fr.Pos; _dragStartWorld = mw;
            _frameContained.Clear();
            foreach (var n in Graph.Nodes) if (fr.FullRect.Contains(NodeLayout.For(n).Card.Center)) _frameContained[n.Id] = n.Pos - fr.Pos;
        }
        else if (input.Shift)        // Shift + drag on empty space = box-select (additive)
        {
            _mode = Mode.Box; _boxStart = mw;
        }
        else                          // plain drag on empty space = pan; a plain click also deselects
        {
            Selection.Clear();
            _mode = Mode.Panning;
        }
    }

    private void StartWire(string node, int pin, PinKind kind, bool fromOutput)
    { _mode = Mode.DragWire; _wireNode = node; _wirePin = pin; _wireKind = kind; _wireFromOutput = fromOutput; }

    private void CommitWire(Vector2 screenMouse)
    {
        var port = HitPort(screenMouse);
        if (port == null)
        {
            // released on empty canvas while dragging FROM an output: offer to add a node already wired from it
            if (_wireFromOutput) GhostAdd = (_wireNode, _wirePin, Cam.ScreenToWorld(screenMouse) + new Vector2(40, 0));
            return;
        }
        var (tn, tp, tInput) = port.Value;
        bool willConnect = (_wireFromOutput && tInput) || (!_wireFromOutput && !tInput);
        if (willConnect && !_wireUndo) PushUndo();
        if (_wireFromOutput && tInput) Graph.Connect(_wireNode, _wirePin, tn, tp);
        else if (!_wireFromOutput && !tInput) Graph.Connect(tn, tp, _wireNode, _wirePin);
    }

    private void CommitBox(Vector2 mw, bool additive)
    {
        var box = RectF.FromCorners(_boxStart, mw);
        if (!additive) Selection.Clear();
        foreach (var n in Graph.Nodes)
            if (NodeLayout.For(n).Card.Overlaps(box)) Selection.Add(n.Id);
    }

    public void DeleteSelection()
    {
        if (Selection.Count == 0) return;
        PushUndo();
        foreach (var id in Selection.ToList()) { var n = Graph.Find(id); if (n != null) Graph.Remove(n); }
        Selection.Clear();
    }

    private void BringToFront(Node n) { Graph.Nodes.Remove(n); Graph.Nodes.Add(n); }

    // ---- hit testing ----
    private Node? HitNode(Vector2 mw)
    {
        for (int i = Graph.Nodes.Count - 1; i >= 0; i--)
            if (NodeLayout.For(Graph.Nodes[i]).Card.Contains(mw)) return Graph.Nodes[i];
        return null;
    }

    private void DrawFrames(Renderer r)
    {
        float z = Cam.Zoom;
        foreach (var f in Graph.Frames)
        {
            var col = Theme.Tag(f.ColorIndex);
            bool sel = _frameSel == f.Id;
            var rect = ScreenRect(f.Rect);
            if (!f.Collapsed)
            {
                r.RoundFill(rect, Theme.WithAlpha(col, 0.10f), 10f);
                r.RoundOutline(rect, Theme.WithAlpha(col, sel ? 0.95f : 0.45f), 10f);
            }
            var tb = ScreenRect(f.TitleBar);
            r.RoundFill(tb, Theme.WithAlpha(col, sel ? 0.95f : 0.82f), 10f);
            if (z > 0.4f)
            {
                var tf = r.Fonts.Get(FontKind.SansBold, Math.Clamp((int)(13 * z), 8, 18));
                r.Text(tf, r.Ellipsize(tf, f.Title.Length > 0 ? f.Title : "Note", tb.W - 14 * z), new Vector2(tb.X + 8 * z, tb.Center.Y - tf.MeasureString(Ircuitry.Render.Renderer.SafeText("Xg")).Y / 2f), Theme.TextInk);
            }
            if (!f.Collapsed && f.Body.Length > 0 && z > 0.5f)
            {
                var bf = r.Fonts.Get(FontKind.Sans, Math.Clamp((int)(12 * z), 8, 15));
                float lh = bf.MeasureString(Ircuitry.Render.Renderer.SafeText("Xg")).Y + 2 * z, ty = tb.Bottom + 6 * z;
                foreach (var line in f.Body.Split('\n'))
                {
                    if (ty + lh > rect.Bottom - 4 * z) break;
                    r.Text(bf, r.Ellipsize(bf, line, rect.W - 16 * z), new Vector2(rect.X + 8 * z, ty), Theme.WithAlpha(Theme.Text, 0.82f));
                    ty += lh;
                }
            }
            if (!f.Collapsed)   // always-visible resize grip (bottom-right) so the note reads as resizable
            {
                var br = new Vector2(rect.Right - 4, rect.Bottom - 4);
                var gc = Theme.WithAlpha(col, sel ? 0.98f : 0.6f);
                float w = sel ? 2.2f : 1.6f;
                for (float o = FrameHandle; o >= FrameHandle * 0.45f; o -= FrameHandle * 0.42f)
                    r.Line(new Vector2(br.X - o, br.Y), new Vector2(br.X, br.Y - o), gc, w);
            }
        }
    }

    private void DrawWireValues(Renderer r, Vector2 mouse)
    {
        if (_mode == Mode.Idle)   // hover a data wire -> tooltip of the value that last flowed through it
        {
            var hov = WireUnder(mouse);
            if (hov != null) { var n = Graph.Find(hov.FromNode); if (n != null && hov.FromPin < n.Outputs.Length && IsDataPin(n.Outputs[hov.FromPin].Kind)) DrawValueTip(r, mouse + new Vector2(16, 10), n.Outputs[hov.FromPin].Name, OutValue(hov.FromNode, hov.FromPin), false); }
        }
        foreach (var id in _watches)   // pinned watches -> chips by each watched node's data output pins
        {
            var n = Graph.Find(id); if (n == null) continue;
            var l = NodeLayout.For(n);
            for (int p = 0; p < n.Outputs.Length; p++)
            {
                if (!IsDataPin(n.Outputs[p].Kind)) continue;
                DrawValueTip(r, Cam.WorldToScreen(l.OutPin(p)) + new Vector2(12, -9), n.Outputs[p].Name, OutValue(id, p), true);
            }
        }
    }

    private void DrawValueTip(Renderer r, Vector2 pos, string label, string value, bool pinned)
    {
        string txt = value.Length == 0 ? "(none yet)" : value.Replace('\n', ' ').Replace('\r', ' ');
        if (txt.Length > 64) txt = txt[..64] + "…";
        var f = r.Fonts.Get(FontKind.Mono, 11);
        string lab = label.Length > 0 ? label + " = " : "";
        var box = new RectF(pos.X, pos.Y, f.MeasureString(Ircuitry.Render.Renderer.SafeText(lab + txt)).X + 14, 20);
        r.RoundFill(box.Offset(0, 1), Theme.WithAlpha(Color.Black, 0.18f), 6f);
        r.RoundFill(box, pinned ? Theme.WithAlpha(Theme.Sky, 0.93f) : Theme.WithAlpha(Color.Black, 0.84f), 6f);
        if (pinned) r.RoundOutline(box, Theme.WithAlpha(Theme.Mix(Theme.Sky, Color.White, 0.3f), 0.9f), 6f);
        r.Text(f, lab, new Vector2(box.X + 7, box.Center.Y - 6), pinned ? Theme.WithAlpha(Theme.TextInk, 0.6f) : Theme.WithAlpha(Color.White, 0.6f));
        r.Text(f, txt, new Vector2(box.X + 7 + f.MeasureString(Ircuitry.Render.Renderer.SafeText(lab)).X, box.Center.Y - 6), pinned ? Theme.TextInk : Color.White);
    }

    private void DrawGhosts(Renderer r, Vector2 screen)
    {
        if (_mode != Mode.Idle && _mode != Mode.DragNodes) return;   // hide while wiring/panning
        float z = Cam.Zoom;
        foreach (var n in Graph.Nodes)
        {
            if (!ShowsGhosts(n, screen)) continue;
            var l = NodeLayout.For(n);
            for (int p = 0; p < n.Outputs.Length; p++)
            {
                if (n.Outputs[p].Kind != PinKind.Exec || Graph.OutputConnected(n.Id, p)) continue;
                var ps = Cam.WorldToScreen(l.OutPin(p));
                var gs = Cam.WorldToScreen(GhostPos(l, p));
                bool hot = Vector2.Distance(screen, gs) <= 13;
                float rad = (hot ? 11 : 9) * z;
                var box = new RectF(gs.X - rad, gs.Y - rad, rad * 2, rad * 2);
                r.RoundFill(box, Theme.WithAlpha(Theme.Cyan, hot ? 0.22f : 0.10f), rad);
                r.RoundOutline(box, Theme.WithAlpha(Theme.Cyan, hot ? 0.95f : 0.5f), rad);
                r.TextCentered(r.Fonts.Get(FontKind.SansBold, Math.Clamp((int)(15 * z), 9, 20)), "+", box, Theme.WithAlpha(Theme.Cyan, hot ? 1f : 0.7f));
            }
        }
    }

    private (string node, int pin, bool input)? HitPort(Vector2 screen)
    {
        const float R = 11f;
        for (int i = Graph.Nodes.Count - 1; i >= 0; i--)
        {
            var n = Graph.Nodes[i];
            var l = NodeLayout.For(n);
            var ins = n.Inputs; var outs = n.Outputs;
            for (int p = 0; p < ins.Length; p++)
                if (Vector2.Distance(screen, Cam.WorldToScreen(l.InPin(p))) <= R) return (n.Id, p, true);
            for (int p = 0; p < outs.Length; p++)
                if (Vector2.Distance(screen, Cam.WorldToScreen(l.OutPin(p))) <= R) return (n.Id, p, false);
        }
        return null;
    }

    // ===================================================================
    public void Draw(Renderer r, RectF canvas, InputState input, Clock clock)
    {
        var scissor = canvas.ToRectangle();

        r.Begin(BlendMode.Alpha, scissor);
        DrawGrid(r, canvas);
        DrawFrames(r);          // sticky notes / region frames sit behind everything
        RefreshRouteCache();
        // draw quiet wires first, then any in-use (lit) wire on top, so the active path is never hidden
        foreach (var c in Graph.Connections) if (WireHeat(c) <= 0.02f) DrawWire(r, c, clock);
        foreach (var c in Graph.Connections) if (WireHeat(c) > 0.02f) DrawWire(r, c, clock);
        if (_mode == Mode.DragWire) DrawDragWire(r, input);
        foreach (var n in Graph.Nodes) DrawNode(r, n, clock);
        DrawGhosts(r, input.Mouse);   // "+" suggestions on dangling exec outputs
        DrawWireValues(r, input.Mouse);   // pin inspector tooltip + watch chips (#16)
        DrawExplain(r);                   // why-didn't-this-fire red trail (#14)
        // node-fired shockwave (alpha so the rings read on the cream canvas): a white-hot flash
        // outline at the instant of firing, then two rings rippling outward as it fades.
        if (FireGlow != null)
            foreach (var n in Graph.Nodes)
            {
                float g = FireGlow(n.Id);
                if (g <= 0.01f) continue;
                var card = ScreenRect(NodeLayout.For(n).Card);
                var cat = Theme.Category(n.Def.Category);
                float baseRad = 14f * Cam.Zoom;
                r.RoundOutline(card.Inflate(2f, 2f), Theme.WithAlpha(Theme.Mix(cat, Color.White, 0.55f), g), baseRad + 2f);
                for (int k = 0; k < 2; k++)
                {
                    float p = g - k * 0.22f;
                    if (p <= 0.01f) continue;
                    float spread = (1f - p) * (26f + 30f * Cam.Zoom);
                    r.RoundOutline(card.Inflate(3f + spread, 3f + spread), Theme.WithAlpha(cat, p * p * 0.75f), baseRad + 3f + spread);
                }
            }
        // "still working" indicator: a node whose Exec is still running (slow AI call, delay, HTTP, chathistory)
        // keeps a breathing outline and a ring of orbiting twinkles, so it never looks idle while it's busy.
        if (NodeBusy != null)
            foreach (var n in Graph.Nodes)
            {
                float e = NodeBusy(n.Id);
                if (e < 0f) continue;
                var card = ScreenRect(NodeLayout.For(n).Card);
                var cat = Theme.Category(n.Def.Category);
                float baseRad = 14f * Cam.Zoom;
                float ramp = MathF.Min(1f, (e - 0.18f) * 3f);                       // ease the effect in over ~0.3s
                float wave = 0.5f + 0.5f * MathF.Sin(e * 4.2f);
                r.RoundOutline(card.Inflate(2.5f, 2.5f), Theme.WithAlpha(Theme.Mix(cat, Color.White, 0.5f), (0.4f + 0.42f * wave) * ramp), baseRad + 2.5f);
                float orbit = MathF.Max(card.W, card.H) * 0.5f + 8f * Cam.Zoom;
                for (int k = 0; k < 3; k++)
                {
                    float ang = e * 2.0f + k * (MathF.Tau / 3f);
                    var p = card.Center + new Vector2(MathF.Cos(ang) * orbit, MathF.Sin(ang) * orbit * 0.6f);
                    float tw = 0.5f + 0.5f * MathF.Sin(e * 6.5f + k * 2.1f);
                    r.Disc(p, (1.4f + 1.1f * tw) * Cam.Zoom, Theme.WithAlpha(Theme.Mix(cat, Color.White, 0.7f), (0.3f + 0.5f * tw) * ramp));
                }
            }
        if (_mode == Mode.Box) DrawBox(r, input);
        if (_hoverPort.HasValue) DrawPortHover(r, _hoverPort.Value);
        if (ShowMinimap) DrawMinimap(r, canvas);
        r.End();

        // additive bloom pass: a soft glow burst around each fired node, and a luminous packet
        // that rides outward along every wire leaving a node the moment it fires.
        if (FireGlow != null)
        {
            r.Begin(BlendMode.Add, scissor);
            foreach (var n in Graph.Nodes)
            {
                float g = FireGlow(n.Id);
                if (g <= 0.01f) continue;
                var card = ScreenRect(NodeLayout.For(n).Card);
                var cat = Theme.Category(n.Def.Category);
                float ease = g * g;
                r.Glow(card.Center, MathF.Max(card.W, card.H) * (0.65f + 0.45f * (1f - g)), Theme.WithAlpha(cat, 0.45f * ease));
            }
            foreach (var c in Graph.Connections)
            {
                float hot = FireGlow(c.FromNode);
                if (hot <= 0.02f) continue;
                var a = Graph.Find(c.FromNode); var b = Graph.Find(c.ToNode);
                if (a == null || b == null || a.Muted || b.Muted) continue;
                if (c.FromPin >= a.Outputs.Length || c.ToPin >= b.Inputs.Length) continue;
                var col = Pins.Color(a.Outputs[c.FromPin].Kind);
                var world = WorldRoute(c);
                var pts = new List<Vector2>(world.Count);
                foreach (var wp in world) pts.Add(Cam.WorldToScreen(wp));
                var pos = PointAlong(pts, 1f - hot);   // sits at the node when it fires, glides to the next as it fades
                r.Glow(pos, MathF.Max(7f, 9f * Cam.Zoom), Theme.WithAlpha(col, 0.8f * hot));
                r.Disc(pos, MathF.Max(2.5f, 3.2f * Cam.Zoom), Theme.WithAlpha(Theme.Mix(col, Color.White, 0.5f), hot));
            }
            if (NodeBusy != null)
                foreach (var n in Graph.Nodes)
                {
                    float e = NodeBusy(n.Id);
                    if (e < 0f) continue;
                    var card = ScreenRect(NodeLayout.For(n).Card);
                    var cat = Theme.Category(n.Def.Category);
                    float ramp = MathF.Min(1f, (e - 0.18f) * 3f);
                    float wave = 0.5f + 0.5f * MathF.Sin(e * 4.2f);
                    r.Glow(card.Center, MathF.Max(card.W, card.H) * (0.66f + 0.12f * wave), Theme.WithAlpha(cat, (0.18f + 0.22f * wave) * ramp));
                }
            r.End();
        }
    }

    private void DrawMinimap(Renderer r, RectF canvas)
    {
        _mmActive = false;
        if (canvas.W < 360 || canvas.H < 300) return;   // only suppressed on a tiny viewport, not on an empty graph

        var vtl = Cam.ScreenToWorld(new Vector2(canvas.Left, canvas.Top));
        var vbr = Cam.ScreenToWorld(new Vector2(canvas.Right, canvas.Bottom));
        var min = new Vector2(float.MaxValue); var max = new Vector2(float.MinValue);
        foreach (var n in Graph.Nodes)
        {
            var l = NodeLayout.For(n);
            min = Vector2.Min(min, l.Card.Pos); max = Vector2.Max(max, new Vector2(l.Card.Right, l.Card.Bottom));
        }
        if (Graph.Nodes.Count == 0) { min = vtl; max = vbr; }   // empty graph: bound the content by the viewport so the box still draws
        _mmContentMin = min; _mmContentMax = max;   // node bbox before we fold in the (movable) viewport
        min = Vector2.Min(min, vtl); max = Vector2.Max(max, vbr);
        var pad = (max - min) * 0.05f + new Vector2(24);
        min -= pad; max += pad;

        const float mmW = 158, mmH = 112, marg = 12;
        var area = _hasMmArea ? MinimapArea : canvas;   // anchor inside the host-provided safe area (above buttons, left of panes)
        var box = new RectF(area.Right - mmW - marg, area.Bottom - mmH - marg, mmW, mmH);
        r.RoundFill(box.Offset(0, 3), Theme.WithAlpha(Color.Black, 0.12f), 9);
        r.RoundFill(box, Theme.WithAlpha(Theme.Panel, 0.93f), 9);
        r.RoundOutline(box, Theme.Edge, 9);

        var span = Vector2.Max(max - min, new Vector2(1));
        float sc = MathF.Min((box.W - 8) / span.X, (box.H - 8) / span.Y);
        var origin = new Vector2(box.X + (box.W - span.X * sc) / 2f, box.Y + (box.H - span.Y * sc) / 2f);
        Vector2 Map(Vector2 w) => origin + (w - min) * sc;

        foreach (var n in Graph.Nodes)
        {
            var l = NodeLayout.For(n);
            var a = Map(l.Card.Pos); var b = Map(new Vector2(l.Card.Right, l.Card.Bottom));
            var cat = n.ColorTag >= 0 ? Theme.Tag(n.ColorTag) : Theme.Category(n.Def.Category);
            var col = Selection.Contains(n.Id) ? cat : Theme.WithAlpha(cat, n.Muted ? 0.35f : 0.8f);
            r.Fill(new RectF(a.X, a.Y, MathF.Max(2.5f, b.X - a.X), MathF.Max(2.5f, b.Y - a.Y)), col);
        }
        var va = Map(vtl); var vb = Map(vbr);
        r.RectOutline(new RectF(va.X, va.Y, vb.X - va.X, vb.Y - va.Y), Theme.WithAlpha(Theme.CyanDim, _minimapDrag ? 1f : 0.95f), _minimapDrag ? 2f : 1.5f);

        // expose the mapping so Update can pan when the box is dragged
        _mmActive = true; _mmBox = box; _mmMin = min; _mmOrigin = origin; _mmScale = sc;
    }

    private void DrawGrid(Renderer r, RectF c)
    {
        const float baseStep = 28f; const int major = 4;
        float step = baseStep;
        while (step * Cam.Zoom < 13f) step *= major;
        while (step * Cam.Zoom > 96f) step /= major;

        Vector2 tl = Cam.ScreenToWorld(new Vector2(c.Left, c.Top));
        Vector2 br = Cam.ScreenToWorld(new Vector2(c.Right, c.Bottom));
        // Integer-indexed, NOT `for (float x...; x += step)`: a float counter never advances once |x|
        // grows past the point where `step` is below its ULP, which spun this loop forever and froze
        // the whole machine. The count guard is a further backstop for any absurd range.
        if (!(float.IsFinite(tl.X) && float.IsFinite(tl.Y) && float.IsFinite(br.X) && float.IsFinite(br.Y)) || step <= 0f) return;
        int ix0 = (int)MathF.Floor(tl.X / step), ix1 = (int)MathF.Ceiling(br.X / step);
        int iy0 = (int)MathF.Floor(tl.Y / step), iy1 = (int)MathF.Ceiling(br.Y / step);
        if (ix1 - ix0 > 4000 || iy1 - iy0 > 4000) return;   // nothing sane ever draws this many lines
        for (int i = ix0; i <= ix1; i++)
        {
            float sx = MathF.Round(Cam.WorldToScreen(new Vector2(i * step, 0)).X);
            r.VLine(sx, c.Top, c.Bottom, i % major == 0 ? Theme.GridMajor : Theme.GridMinor, 1f);
        }
        for (int j = iy0; j <= iy1; j++)
        {
            float sy = MathF.Round(Cam.WorldToScreen(new Vector2(0, j * step)).Y);
            r.HLine(c.Left, c.Right, sy, j % major == 0 ? Theme.GridMajor : Theme.GridMinor, 1f);
        }
        Vector2 o = Cam.WorldToScreen(Vector2.Zero);
        if (o.X >= c.Left && o.X <= c.Right) r.VLine(MathF.Round(o.X), c.Top, c.Bottom, Theme.GridAxis, 1.5f);
        if (o.Y >= c.Top && o.Y <= c.Bottom) r.HLine(c.Left, c.Right, MathF.Round(o.Y), Theme.GridAxis, 1.5f);
    }

    private float WireHeat(Connection c) => FireGlow?.Invoke(c.FromNode) ?? 0f;

    private void DrawWire(Renderer r, Connection c, Clock clock)
    {
        var a = Graph.Find(c.FromNode); var b = Graph.Find(c.ToNode);
        if (a == null || b == null) return;
        if (c.FromPin >= a.Outputs.Length || c.ToPin >= b.Inputs.Length) return;
        var col = Pins.Color(a.Outputs[c.FromPin].Kind);
        bool muted = a.Muted || b.Muted;
        // grid-snapped, obstacle-avoiding route (cached in world space; project to screen for drawing)
        var world = WorldRoute(c);
        var pts = new List<Vector2>(world.Count);
        foreach (var wp in world) pts.Add(Cam.WorldToScreen(wp));
        bool hovered = !muted && _hoverWire == WireKey(c);
        float hot = muted ? 0f : (FireGlow?.Invoke(c.FromNode) ?? 0f);   // 0..1 while data is flowing through it
        if (hovered && hot <= 0.02f) DrawTrace(r, pts, Theme.WithAlpha(Theme.Mix(col, Color.White, 0.55f), 0.5f));   // soft halo under the hovered wire

        Color main = muted ? Theme.WithAlpha(col, 0.28f) : (hovered ? Theme.Mix(col, Color.White, 0.35f) : col);
        if (hot > 0.02f) main = Theme.Mix(col, Color.White, 0.45f + 0.5f * hot);   // light up bright while in use
        DrawTrace(r, pts, main, hot);

        // cozy little shines streaming along the wire while a bot is live (think Pokémon transfer / Animal
        // Crossing sparkle) - faint and slow at rest, then brighter, bigger and quicker on a fresh fire
        if (Running && !muted)
        {
            int seed = WireKey(c).GetHashCode();
            float speed = 0.32f + 0.8f * hot;
            float alpha = 0.34f + 0.55f * hot;
            float zoom = MathF.Max(0.85f, Cam.Zoom);
            for (int d = 0; d < 2; d++)
            {
                float t = (clock.Time * speed + d * 0.5f) % 1f;
                float pulse = 0.72f + 0.28f * MathF.Sin(clock.Time * 7f + d * 2.3f + seed);   // a gentle twinkle
                float size = (3.2f + 1.8f * hot) * zoom * pulse;
                DrawTwinkle(r, PointAlong(pts, t), size, col, alpha * pulse);
            }
        }
    }

    /// <summary>One cozy 4-point shine (Animal-Crossing twinkle): a soft halo, two tapered-feeling arms and a
    /// bright white centre - the friendly stand-in for a harsh electric spark.</summary>
    private void DrawTwinkle(Renderer r, Vector2 c, float size, Color col, float alpha)
    {
        if (alpha <= 0.02f || size < 0.5f) return;
        var arm = Theme.Mix(col, Color.White, 0.5f);
        float w = MathF.Max(1.1f, size * 0.34f);
        r.Glow(c, size * 1.8f, Theme.WithAlpha(arm, alpha * 0.45f));
        r.Line(new Vector2(c.X, c.Y - size), new Vector2(c.X, c.Y + size), Theme.WithAlpha(arm, alpha), w);
        r.Line(new Vector2(c.X - size * 0.72f, c.Y), new Vector2(c.X + size * 0.72f, c.Y), Theme.WithAlpha(arm, alpha), w);
        r.Disc(c, MathF.Max(0.9f, size * 0.42f), Theme.WithAlpha(Color.White, alpha));
    }

    private void DrawDragWire(Renderer r, InputState input)
    {
        var n = Graph.Find(_wireNode); if (n == null) return;
        var l = NodeLayout.For(n);
        var anchor = Cam.WorldToScreen(_wireFromOutput ? l.OutPin(_wirePin) : l.InPin(_wirePin));
        var col = Pins.Color(_wireKind);
        var pts = _wireFromOutput ? RouteWire(anchor, input.Mouse, 0) : RouteWire(input.Mouse, anchor, 0);
        DrawTrace(r, pts, col);
        r.Disc(input.Mouse, 4f, col);
    }

    // ---- simple screen-space orthogonal route (used by the in-progress drag wire) ----
    /// <summary>Route output-to-input as right-angle segments (out the right, in the left), the vertical run snapped to the grid.</summary>
    private List<Vector2> RouteWire(Vector2 p0, Vector2 p1, float jog)
    {
        float z = Cam.Zoom;
        float stub = MathF.Max(12f, 20f * z);
        var a = new Vector2(p0.X + stub, p0.Y);     // leave the output going right
        var b = new Vector2(p1.X - stub, p1.Y);     // arrive at the input from the left
        var pts = new List<Vector2> { p0, a };

        if (MathF.Abs(a.Y - b.Y) <= 1.5f)           // same row -> straight across
        {
            pts.Add(b); pts.Add(p1);
            return pts;
        }

        float midX = (a.X + b.X) * 0.5f + jog;
        if (b.X - a.X < stub * 2) midX = MathF.Max(a.X, b.X) + stub + jog;   // backward edge: route clear to the right
        midX = SnapX(midX);
        pts.Add(new Vector2(midX, a.Y));
        pts.Add(new Vector2(midX, b.Y));
        pts.Add(b); pts.Add(p1);
        return pts;
    }

    // snap a screen-space X to the nearest minor grid column, so vertical runs sit on the grid
    private float SnapX(float screenX)
    {
        float step = GridStepScreen();
        float originX = Cam.WorldToScreen(Vector2.Zero).X;
        return originX + MathF.Round((screenX - originX) / step) * step;
    }

    private float GridStepScreen()
    {
        const float baseStep = 28f, major = 4;
        float step = baseStep;
        while (step * Cam.Zoom < 13f) step *= major;
        while (step * Cam.Zoom > 96f) step /= major;
        return step * Cam.Zoom;
    }

    private void DrawTrace(Renderer r, List<Vector2> pts, Color col, float glow = 0f)
    {
        var path = Rounded(pts, MathF.Max(6f, 11f * Cam.Zoom));   // fillet the right-angle corners so direction reads clearly
        float w = MathF.Max(1.7f, 2.4f * Cam.Zoom) * (1f + 0.5f * glow);
        if (glow > 0.02f)   // a soft halo under an in-use wire, so it reads as lit
            for (int i = 0; i + 1 < path.Count; i++)
                r.Line(path[i], path[i + 1], Theme.WithAlpha(Theme.Mix(col, Color.White, 0.5f), 0.30f * glow), w + 6f * glow);
        for (int i = 0; i + 1 < path.Count; i++)
            r.Line(path[i], path[i + 1], col, w);
        r.Disc(path[0], w * 0.85f, col);                                     // solder pads at both ends
        r.Disc(path[^1], w * 0.85f, col);
    }

    /// <summary>Replace each interior right-angle corner with a small rounded fillet (quadratic through the
    /// corner), so a wire's turn direction reads at a glance instead of as a hard L.</summary>
    private static List<Vector2> Rounded(List<Vector2> pts, float radius)
    {
        if (pts.Count < 3) return pts;
        var outp = new List<Vector2> { pts[0] };
        for (int i = 1; i + 1 < pts.Count; i++)
        {
            Vector2 p0 = pts[i - 1], p1 = pts[i], p2 = pts[i + 1];
            Vector2 d0 = p1 - p0, d1 = p2 - p1;
            float l0 = d0.Length(), l1 = d1.Length();
            if (l0 < 0.01f || l1 < 0.01f) { outp.Add(p1); continue; }
            float r = MathF.Min(radius, MathF.Min(l0, l1) * 0.5f);
            Vector2 a = p1 - d0 / l0 * r, b = p1 + d1 / l1 * r;
            outp.Add(a);
            const int seg = 5;
            for (int s = 1; s <= seg; s++)
            {
                float t = s / (float)seg;
                outp.Add(Vector2.Lerp(Vector2.Lerp(a, p1, t), Vector2.Lerp(p1, b, t), t));   // quadratic bezier
            }
        }
        outp.Add(pts[^1]);
        return outp;
    }

    private static Vector2 PointAlong(List<Vector2> pts, float t)
    {
        float total = 0;
        for (int i = 0; i + 1 < pts.Count; i++) total += Vector2.Distance(pts[i], pts[i + 1]);
        float want = total * Math.Clamp(t, 0f, 1f), acc = 0;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            float seg = Vector2.Distance(pts[i], pts[i + 1]);
            if (acc + seg >= want) return Vector2.Lerp(pts[i], pts[i + 1], seg <= 0.001f ? 0 : (want - acc) / seg);
            acc += seg;
        }
        return pts[^1];
    }

    // ===================================================================
    //  Obstacle-avoiding orthogonal routing (A* on a world grid) - circuit traces that
    //  snap to the grid, route around node bodies, and never double back. Cached per wire
    //  and rebuilt only when the node layout changes (so pan/zoom is free).
    // ===================================================================
    private const float RouteStep = 22f;
    private readonly Dictionary<string, List<Vector2>> _routeCache = new();
    private long _routeSig = long.MinValue;

    private void RefreshRouteCache()
    {
        long sig = LayoutSignature();
        if (sig != _routeSig) { _routeSig = sig; _routeCache.Clear(); }
    }

    private long LayoutSignature()
    {
        unchecked
        {
            long h = 1469598103934665603;
            void Mix(long v) { h = (h ^ v) * 1099511628211; }
            Mix(Graph.Nodes.Count); Mix(Graph.Connections.Count);
            // pin counts matter too: a node with dynamic pins (e.g. Switch) grows/shrinks as its cases change,
            // which moves every pin - so the cached wire routes must invalidate even when topology is unchanged.
            foreach (var n in Graph.Nodes) { Mix(n.Id.GetHashCode()); Mix((long)MathF.Round(n.Pos.X)); Mix((long)MathF.Round(n.Pos.Y)); Mix(n.Inputs.Length); Mix(n.Outputs.Length); }
            foreach (var c in Graph.Connections) { Mix(c.FromNode.GetHashCode()); Mix(c.FromPin); Mix(c.ToNode.GetHashCode()); Mix(c.ToPin); }
            return h;
        }
    }

    private List<Vector2> WorldRoute(Connection c)
    {
        string key = c.FromNode + ":" + c.FromPin + ">" + c.ToNode + ":" + c.ToPin;
        if (_routeCache.TryGetValue(key, out var cached)) return cached;
        var route = ToolRoute(c) ?? Simplify(ComputeRoute(c) ?? FallbackRoute(c));
        _routeCache[key] = route;
        return route;
    }

    /// <summary>Tool wires flow vertically: out the top of the tool node, into the bottom of the AI node. A
    /// simple S-route (no obstacle avoidance) keeps the magenta tool plugs reading top-to-bottom.</summary>
    private List<Vector2>? ToolRoute(Connection c)
    {
        var a = Graph.Find(c.FromNode); var b = Graph.Find(c.ToNode);
        if (a == null || b == null || c.FromPin >= a.Outputs.Length || c.ToPin >= b.Inputs.Length) return null;
        if (a.Outputs[c.FromPin].Kind != PinKind.Tool) return null;
        var p0 = NodeLayout.For(a).OutPin(c.FromPin);   // tool node's top edge
        var p1 = NodeLayout.For(b).InPin(c.ToPin);      // AI node's bottom edge
        float step = MathF.Max(28f, MathF.Abs(p1.Y - p0.Y) * 0.4f);
        return new List<Vector2> { p0, new(p0.X, p0.Y - step), new(p1.X, p1.Y + step), p1 };
    }

    /// <summary>Collapse collinear runs so a straight stretch is one segment (corner discs only at real bends).</summary>
    private static List<Vector2> Simplify(List<Vector2> p)
    {
        if (p.Count <= 2) return p;
        var o = new List<Vector2> { p[0] };
        for (int i = 1; i < p.Count - 1; i++)
        {
            var a = o[^1]; var b = p[i]; var c = p[i + 1];
            bool collinear = (MathF.Abs(a.X - b.X) < 0.5f && MathF.Abs(b.X - c.X) < 0.5f)
                          || (MathF.Abs(a.Y - b.Y) < 0.5f && MathF.Abs(b.Y - c.Y) < 0.5f);
            if (!collinear) o.Add(b);
        }
        o.Add(p[^1]);
        return o;
    }

    /// <summary>Straight right-angle fallback (out the right, vertical channel, in the left) in world space.</summary>
    private List<Vector2> FallbackRoute(Connection c)
    {
        var a = Graph.Find(c.FromNode)!; var b = Graph.Find(c.ToNode)!;
        var p0 = NodeLayout.For(a).OutPin(c.FromPin);
        var p1 = NodeLayout.For(b).InPin(c.ToPin);
        var aOut = new Vector2(p0.X + RouteStep, p0.Y);
        var bIn = new Vector2(p1.X - RouteStep, p1.Y);
        var pts = new List<Vector2> { p0, aOut };
        if (MathF.Abs(aOut.Y - bIn.Y) > 1.5f)
        {
            float midX = bIn.X - aOut.X < RouteStep * 2 ? MathF.Max(aOut.X, bIn.X) + RouteStep : (aOut.X + bIn.X) * 0.5f;
            pts.Add(new Vector2(midX, aOut.Y));
            pts.Add(new Vector2(midX, bIn.Y));
        }
        pts.Add(bIn); pts.Add(p1);
        return pts;
    }

    private List<Vector2>? ComputeRoute(Connection c)
    {
        var a = Graph.Find(c.FromNode); var b = Graph.Find(c.ToNode);
        if (a == null || b == null || c.FromPin >= a.Outputs.Length || c.ToPin >= b.Inputs.Length) return null;
        Vector2 outP = NodeLayout.For(a).OutPin(c.FromPin);
        Vector2 inP = NodeLayout.For(b).InPin(c.ToPin);
        float step = RouteStep;
        Vector2 startW = new(outP.X + step * 1.5f, outP.Y);
        Vector2 endW = new(inP.X - step * 1.5f, inP.Y);

        // grid bounds = the two endpoints plus any node within reach, padded
        float minX = MathF.Min(startW.X, endW.X), maxX = MathF.Max(startW.X, endW.X);
        float minY = MathF.Min(startW.Y, endW.Y), maxY = MathF.Max(startW.Y, endW.Y);
        foreach (var n in Graph.Nodes)
        {
            var card = NodeLayout.For(n).Card;
            if (card.Right < minX - 240 || card.X > maxX + 240 || card.Bottom < minY - 240 || card.Y > maxY + 240) continue;
            minX = MathF.Min(minX, card.X); maxX = MathF.Max(maxX, card.Right);
            minY = MathF.Min(minY, card.Y); maxY = MathF.Max(maxY, card.Bottom);
        }
        float pad = step * 4;
        minX -= pad; minY -= pad; maxX += pad; maxY += pad;
        int cols = (int)MathF.Ceiling((maxX - minX) / step) + 1;
        int rows = (int)MathF.Ceiling((maxY - minY) / step) + 1;
        if (cols < 2 || rows < 2 || (long)cols * rows > 30000) return null;   // too large -> fallback

        int Cx(float x) => Math.Clamp((int)MathF.Round((x - minX) / step), 0, cols - 1);
        int Cy(float y) => Math.Clamp((int)MathF.Round((y - minY) / step), 0, rows - 1);
        Vector2 CenterOf(int gx, int gy) => new(minX + gx * step, minY + gy * step);

        var blocked = new bool[cols * rows];
        float clr = step * 0.55f;   // keep traces a little clear of node bodies
        foreach (var n in Graph.Nodes)
        {
            var card = NodeLayout.For(n).Card;
            int x0 = Cx(card.X - clr), x1 = Cx(card.Right + clr);
            int y0 = Cy(card.Y - clr), y1 = Cy(card.Bottom + clr);
            for (int gy = y0; gy <= y1; gy++) for (int gx = x0; gx <= x1; gx++) blocked[gy * cols + gx] = true;
        }
        int sCell = Cy(startW.Y) * cols + Cx(startW.X);
        int eCell = Cy(endW.Y) * cols + Cx(endW.X);
        blocked[sCell] = false; blocked[eCell] = false;   // the approach cells must be enterable

        var path = AStar(blocked, cols, rows, sCell, eCell);
        if (path == null) return null;

        // build the world polyline with clean horizontal stubs into the ports
        var pts = new List<Vector2>(path.Count + 4) { outP };
        var first = CenterOf(path[0] % cols, path[0] / cols);
        pts.Add(new Vector2(first.X, outP.Y));
        foreach (int cell in path) pts.Add(CenterOf(cell % cols, cell / cols));
        var last = CenterOf(path[^1] % cols, path[^1] / cols);
        pts.Add(new Vector2(last.X, inP.Y));
        pts.Add(inP);
        return pts;
    }

    private static readonly int[] Dx = { 1, -1, 0, 0 };
    private static readonly int[] Dy = { 0, 0, 1, -1 };

    private static List<int>? AStar(bool[] blocked, int cols, int rows, int start, int goal)
    {
        const float turn = 2.0f;
        int sx = start % cols, sy = start / cols, gx = goal % cols, gy = goal / cols;
        // state = cell * 4 + dir (dir of the move that arrived). seed facing east so wires leave rightward.
        var g = new Dictionary<int, float>();
        var came = new Dictionary<int, int>();
        var pq = new PriorityQueue<int, float>();
        int seed = start * 4 + 0;
        g[seed] = 0;
        pq.Enqueue(seed, MathF.Abs(gx - sx) + MathF.Abs(gy - sy));
        int pops = 0;
        while (pq.TryDequeue(out int cur, out _))
        {
            if (++pops > 40000) break;
            int cell = cur / 4, dir = cur % 4;
            if (cell == goal) return Reconstruct(came, cur, cols);
            float gc = g[cur];
            int cxp = cell % cols, cyp = cell / cols;
            for (int nd = 0; nd < 4; nd++)
            {
                if ((dir == 0 && nd == 1) || (dir == 1 && nd == 0) || (dir == 2 && nd == 3) || (dir == 3 && nd == 2)) continue; // no U-turn
                int nx = cxp + Dx[nd], ny = cyp + Dy[nd];
                if (nx < 0 || ny < 0 || nx >= cols || ny >= rows) continue;
                int ncell = ny * cols + nx;
                if (blocked[ncell] && ncell != goal) continue;
                float ng = gc + 1f + (nd != dir ? turn : 0f);
                int ns = ncell * 4 + nd;
                if (!g.TryGetValue(ns, out var old) || ng < old)
                {
                    g[ns] = ng; came[ns] = cur;
                    pq.Enqueue(ns, ng + MathF.Abs(gx - nx) + MathF.Abs(gy - ny));
                }
            }
        }
        return null;
    }

    private static List<int> Reconstruct(Dictionary<int, int> came, int state, int cols)
    {
        var cells = new List<int>();
        int cur = state;
        while (true)
        {
            cells.Add(cur / 4);
            if (!came.TryGetValue(cur, out var prev)) break;
            cur = prev;
        }
        cells.Reverse();
        // collapse consecutive duplicate cells (same cell, different dir)
        var outp = new List<int>(cells.Count);
        foreach (int cell in cells) if (outp.Count == 0 || outp[^1] != cell) outp.Add(cell);
        return outp;
    }

    private void DrawBox(Renderer r, InputState input)
    {
        var box = RectF.FromCorners(Cam.WorldToScreen(_boxStart), input.Mouse);
        r.Fill(box, Theme.WithAlpha(Theme.Cyan, 0.08f));
        r.RectOutline(box, Theme.WithAlpha(Theme.Cyan, 0.6f), 1f);
    }

    // ---- node drawing ----
    private void DrawNode(Renderer r, Node n, Clock clock)
    {
        var l = NodeLayout.For(n);
        float z = Cam.Zoom;
        var card = ScreenRect(l.Card);
        float rad = MathF.Max(7f, 13f * z);
        var cat = Theme.Category(n.Def.Category);
        bool selected = Selection.Contains(n.Id);
        float fire = FireGlow?.Invoke(n.Id) ?? 0f;   // 0..1 just-fired flash

        // soft drop shadow
        r.RoundFill(card.Offset(0, 5f), Theme.WithAlpha(Color.Black, 0.13f), rad);
        // creamy body, faintly category-tinted
        var bg = Theme.Mix(Theme.Panel, cat, 0.05f);
        r.RoundFill(card, bg, rad);

        // pastel header band (rounded top)
        float hh = NodeLayout.Header * z;
        r.RoundFill(new RectF(card.X, card.Y, card.W, hh + rad), Theme.Mix(Theme.PanelHi, cat, 0.30f), rad);
        r.Fill(new RectF(card.X, card.Y + hh, card.W, rad), bg);
        r.HLine(card.X + 2, card.Right - 2, card.Y + hh, Theme.WithAlpha(cat, 0.55f), 1.5f);

        // firing flash - the header lights up category-bright-to-white under the icon/title
        if (fire > 0.01f)
        {
            float ff = fire * fire;
            r.RoundFill(new RectF(card.X, card.Y, card.W, hh + rad), Theme.WithAlpha(Theme.Mix(cat, Color.White, 0.6f), 0.5f * ff), rad);
            r.Fill(new RectF(card.X, card.Y + hh, card.W, rad), Theme.WithAlpha(Theme.Mix(cat, Color.White, 0.5f), 0.45f * ff));
        }

        // a colour tag paints the card border + a little corner flag, so a subsystem reads at a glance
        bool tagged = n.ColorTag >= 0;
        var tagCol = tagged ? Theme.Tag(n.ColorTag) : Theme.Edge;
        var edge = selected ? cat : tagged ? tagCol : Theme.Edge;
        if (fire > 0.01f) edge = Theme.Mix(edge, Color.White, fire * 0.8f);
        r.RoundOutline(card, edge, rad);
        if (tagged && !selected) r.RoundOutline(card.Inflate(1.5f, 1.5f), Theme.WithAlpha(tagCol, 0.4f), rad + 1.5f);
        if (selected) r.RoundOutline(card.Inflate(2f, 2f), Theme.WithAlpha(cat, 0.55f), rad + 2f);
        if (tagged) r.Disc(new Vector2(card.Right - 7 * z, card.Y + 7 * z), 3.4f * z, tagCol);

        // cute icon + title (display face)
        if (z > 0.42f)
        {
            int ts = Math.Clamp((int)MathF.Round(15 * z), 10, 24);
            var tf = r.Fonts.Get(FontKind.Display, ts);
            float ix = card.X + 11 * z;
            float tx;
            var img = n.Def.IconImage != null ? r.IconTexture(n.TypeId, n.Def.IconImage) : null;
            if (img != null)
            {
                float sz = ts + 3f;
                r.Image(img, new RectF(ix, card.Y + (hh - sz) / 2f, sz, sz));
                tx = ix + sz + 6 * z;
            }
            else
            {
                var isz = tf.MeasureString(Ircuitry.Render.Renderer.SafeText(Ircuitry.Core.Icons.Glyph(n.Def.Icon)));
                r.Text(tf, Ircuitry.Core.Icons.Glyph(n.Def.Icon), new Vector2(ix, card.Y + (hh - isz.Y) / 2f), cat);
                tx = ix + isz.X + 6 * z;
            }
            string title = r.Ellipsize(tf, n.DisplayTitle, card.Right - tx - 10 * z);
            r.Text(tf, title, new Vector2(tx, card.Y + (hh - tf.MeasureString(Ircuitry.Render.Renderer.SafeText(title)).Y) / 2f), Theme.Text);
        }

        // pins + labels
        var lf = r.Fonts.Get(FontKind.Mono, Math.Clamp((int)MathF.Round(12f * z), 8, 17));
        var ins = n.Inputs; var outs = n.Outputs;
        for (int p = 0; p < ins.Length; p++)
        {
            var ps = Cam.WorldToScreen(l.InPin(p));
            var pd = ins[p];
            DrawPort(r, ps, pd.Kind, Graph.InputConnected(n.Id, p), z);
            if (z > 0.55f && pd.Name.Length > 0 && pd.Kind != PinKind.Tool)   // tool pins sit on the edge - no inline label
                r.Text(lf, pd.Name, new Vector2(ps.X + 10 * z, ps.Y - lf.MeasureString(Ircuitry.Render.Renderer.SafeText(pd.Name)).Y / 2f), Theme.TextDim);
        }
        for (int p = 0; p < outs.Length; p++)
        {
            var ps = Cam.WorldToScreen(l.OutPin(p));
            var pd = outs[p];
            DrawPort(r, ps, pd.Kind, Graph.OutputConnected(n.Id, p), z);
            if (z > 0.55f && pd.Name.Length > 0 && pd.Kind != PinKind.Tool)
            {
                var m = lf.MeasureString(Ircuitry.Render.Renderer.SafeText(pd.Name));
                r.Text(lf, pd.Name, new Vector2(ps.X - 10 * z - m.X, ps.Y - m.Y / 2f), Theme.TextDim);
            }
        }

        // summary
        if (l.HasSummary && z > 0.5f)
        {
            var sy = Cam.WorldToScreen(new Vector2(l.Card.Left, l.SummaryTop)).Y;
            var sumRect = new RectF(card.X + 8 * z, sy + 3 * z, card.W - 16 * z, NodeLayout.SummaryH * z - 6 * z);
            r.RoundFill(sumRect, Theme.PanelLo, 5f);
            var sf = r.Fonts.Get(FontKind.Mono, Math.Clamp((int)MathF.Round(12.5f * z), 9, 18));
            string val = n.GetParam(n.Def.SummaryParam!);
            val = val.Length == 0 ? "-" : val.Replace("\n", " " + Ircuitry.Core.Icons.Glyph("arrow-bend-down-left") + " ");
            val = r.Ellipsize(sf, val, sumRect.W - 14 * z);
            r.Text(sf, val, new Vector2(sumRect.X + 7 * z, sumRect.Center.Y - sf.MeasureString(Ircuitry.Render.Renderer.SafeText(val)).Y / 2f), Theme.Mix(Theme.TextDim, cat, 0.3f));
        }

        // lifetime fire-count badge (top-right) while/after a run
        if (FireCount != null && z > 0.5f)
        {
            int fc = FireCount(n.Id);
            if (fc > 0)
            {
                var bf = r.Fonts.Get(FontKind.Mono, Math.Clamp((int)MathF.Round(10 * z), 8, 14));
                string s = fc > 999 ? "999+" : fc.ToString();
                var m = bf.MeasureString(Ircuitry.Render.Renderer.SafeText(s));
                float bw = m.X + 12 * z, bh = m.Y + 4 * z;
                var pill = new RectF(card.Right - bw - 5 * z, card.Y - bh * 0.45f, bw, bh);
                r.RoundFill(pill, Theme.Mix(Theme.PanelHi, cat, 0.5f), bh / 2f);
                r.RoundOutline(pill, Theme.WithAlpha(cat, 0.8f), bh / 2f);
                r.Text(bf, s, new Vector2(pill.Center.X - m.X / 2f, pill.Center.Y - m.Y / 2f), Theme.Text);
            }
        }

        // muted nodes fade out and show a tag
        if (n.Muted)
        {
            r.RoundFill(card, Theme.WithAlpha(Theme.Panel, 0.5f), rad);
            if (z > 0.45f)
            {
                var mf = r.Fonts.Get(FontKind.Mono, Math.Clamp((int)MathF.Round(10 * z), 8, 14));
                const string tag = "muted";
                var m = mf.MeasureString(Ircuitry.Render.Renderer.SafeText(tag));
                var pill = new RectF(card.Center.X - m.X / 2f - 7 * z, card.Center.Y - m.Y / 2f - 2 * z, m.X + 14 * z, m.Y + 4 * z);
                r.RoundFill(pill, Theme.WithAlpha(Theme.TextDim, 0.85f), pill.H / 2f);
                r.Text(mf, tag, new Vector2(pill.Center.X - m.X / 2f, pill.Center.Y - m.Y / 2f), Theme.TextInk);
            }
        }
    }

    private void DrawPort(Renderer r, Vector2 s, PinKind kind, bool connected, float z)
    {
        float rad = Math.Clamp(NodeLayout.PortR * z, 3.5f, 7.5f);
        var col = Pins.Color(kind);
        if (kind == PinKind.Exec)
        {
            // square port
            var box = new RectF(s.X - rad, s.Y - rad, rad * 2, rad * 2);
            if (connected) r.RoundFill(box, col, rad * 0.4f);
            else { r.RoundFill(box, Theme.PanelLo, rad * 0.4f); r.RoundOutline(box, col, rad * 0.4f); }
        }
        else
        {
            if (connected) r.Disc(s, rad, col);
            else { r.Disc(s, rad, Theme.PanelLo); r.Ring(s, rad, col); }
        }
    }

    private void DrawPortHover(Renderer r, (string node, int pin, bool input) p)
    {
        var n = Graph.Find(p.node); if (n == null) return;
        var l = NodeLayout.For(n);
        var s = Cam.WorldToScreen(p.input ? l.InPin(p.pin) : l.OutPin(p.pin));
        var kind = p.input ? n.Inputs[p.pin].Kind : n.Outputs[p.pin].Kind;
        // soft halo behind the hovered port
        r.Disc(s, 11f, Theme.WithAlpha(Pins.Color(kind), 0.22f));
    }

    private RectF ScreenRect(RectF world)
    {
        var p = Cam.WorldToScreen(world.Pos);
        return new RectF(p.X, p.Y, world.W * Cam.Zoom, world.H * Cam.Zoom);
    }
}
