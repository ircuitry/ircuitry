using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Ircuitry.App;
using Ircuitry.Core;
using Ircuitry.Editor;
using Ircuitry.Graph;
using Ircuitry.Gui;
using Ircuitry.Render;

namespace Ircuitry.Screens;

// "Bake a node": build a new node as a RECIPE of existing nodes (ingredients), wired in a little embedded
// editor. There is no "write code" mode - if you want code, drop a Code node into the recipe like any other.
// A baked node can be created fresh or re-opened to edit, then saved into the Node Library, exported, or
// submitted as a PR to the community nodes repo.
public partial class MainScreen
{
    private bool _nbOpen, _nbJustOpened;
    private string _nbEditId = "";         // typeId being edited (overwrite that file); "" = new node
    private string _nbTitle = "", _nbIcon = "puzzle-piece", _nbCategory = "Action", _nbDesc = "";
    private string _nbStatus = "", _nbAdd = "";
    private bool _nbMax;
    private bool _nbAsTool;   // tick: the baked node carries a Tool output so it can be wired into Ask AI

    private NodeGraph? _nbGraph;            // the recipe's inner graph
    private GraphEditor? _nbEditor;         // mini editor over _nbGraph
    private readonly Dictionary<string, string> _nbExposed = new();   // exposed inner setting -> default

    private static readonly string[] NbCategories = { "Action", "Data", "Logic", "Ai", "Filter", "Storage", "Code" };

    public void DebugOpenNodeBuilder() { _l = DockLayout(); OpenNodeBuilder(); }
    public void DebugOpenMaxBuilder()
    {
        _l = DockLayout(); OpenNodeBuilder(); _nbMax = true;
        var n = _nbEditor!.Spawn(NodeCatalog.Get("action.reply"), new Vector2(0, 230));
        n.SetParam("message", "hello {nick}");
        _nbEditor.Selection.Clear(); _nbEditor.Selection.Add(n.Id);
    }
    public void DebugOpenComposite() { _l = DockLayout(); OpenNodeBuilder(); _nbTitle = "Shout"; }

    public void OpenNodeBuilder()
    {
        EnsureCompositeEditor();
        _nbEditId = ""; _nbExposed.Clear(); _nbAsTool = false; _nbOpen = true; _nbJustOpened = true; _ui.Focus = "nb.title";
    }

    /// <summary>Re-open an installed custom node to edit its recipe in the mini editor.</summary>
    public void OpenNodeBuilderForEdit(string typeId)
    {
        _l = DockLayout();
        try
        {
            var path = System.IO.Path.Combine(NodeCatalog.CustomDir, typeId + ".ircnode");
            if (!System.IO.File.Exists(path)) { PushToast("can't find that node's file to edit"); return; }
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
            var rt = doc.RootElement;
            string S(string k, string d = "") => rt.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? d : d;

            if (!rt.TryGetProperty("subgraph", out var sg) || sg.ValueKind != JsonValueKind.Object)
            {
                PushToast("that's an older code node - edit its .ircnode file directly, or rebuild it as a recipe");
                return;
            }

            _nbTitle = S("title", typeId); _nbIcon = S("icon", "puzzle-piece"); _nbCategory = S("category", "Logic"); _nbDesc = S("description");
            _nbEditId = typeId; _nbStatus = "";
            _nbAsTool = false;   // re-tick if this node already advertises a Tool output, so re-saving keeps it
            if (rt.TryGetProperty("outputs", out var outs2) && outs2.ValueKind == JsonValueKind.Array)
                foreach (var o in outs2.EnumerateArray())
                    if (o.TryGetProperty("kind", out var kk2) && kk2.ValueKind == JsonValueKind.String && string.Equals(kk2.GetString(), "Tool", StringComparison.OrdinalIgnoreCase)) _nbAsTool = true;
            _nbGraph = GraphSerializer.Load(sg.GetRawText()).graph;
            _nbEditor = new GraphEditor(_nbGraph) { ShowMinimap = false };
            _nbExposed.Clear();
            if (rt.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Array)
                foreach (var p in ps.EnumerateArray())
                {
                    string k = p.TryGetProperty("key", out var kk) && kk.ValueKind == JsonValueKind.String ? kk.GetString() ?? "" : "";
                    string dv = p.TryGetProperty("default", out var d2) && d2.ValueKind == JsonValueKind.String ? d2.GetString() ?? "" : "";
                    if (k.Length > 0) _nbExposed[k] = dv;
                }
            _nbOpen = true; _nbJustOpened = true; _ui.Focus = "nb.title";
        }
        catch (Exception ex) { PushToast("couldn't open node: " + ex.Message); }
    }

    private void EnsureCompositeEditor()
    {
        if (_nbEditor != null) return;
        _nbGraph = new NodeGraph();
        var fin = _nbGraph.Add(NodeCatalog.Get("flow.in"), new Vector2(-220, -130));
        var arg = _nbGraph.Add(NodeCatalog.Get("flow.arg"), new Vector2(-220, 10)); arg.SetParam("name", "text");
        var ret = _nbGraph.Add(NodeCatalog.Get("flow.return"), new Vector2(200, 10)); ret.SetParam("name", "out");
        _nbGraph.Connect(fin.Id, 0, ret.Id, 0);   // Start drives the Output so it runs
        _nbGraph.Connect(arg.Id, 0, ret.Id, 1);   // identity passthrough until you drop nodes in the middle
        _nbEditor = new GraphEditor(_nbGraph) { ShowMinimap = false };
    }

    private void AddCompositeNode(string typeId)
    {
        EnsureCompositeEditor();
        if (NodeCatalog.TryGet(typeId, out var def))
            _nbEditor!.Spawn(def, _nbEditor.Cam.ScreenToWorld(_nbEditorRect.Center));
        _nbAdd = ""; _ui.Focus = null;
    }

    private RectF _nbEditorRect;

    private string NbTypeId()
    {
        if (_nbEditId.Length > 0) return _nbEditId;   // editing -> stable id, overwrite the same file
        string src = _nbTitle.Trim().Length > 0 ? _nbTitle : "custom node";
        var slug = new string(src.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Length > 0 ? "custom." + slug : "custom.node";
    }

    private string NbBuildManifest()
        => _nbEditor?.SerializeAsComposite(NbTypeId(),
            _nbTitle.Trim().Length > 0 ? _nbTitle.Trim() : "Custom Node",
            _nbIcon.Trim().Length > 0 ? _nbIcon.Trim() : "puzzle-piece", _nbCategory, _nbDesc.Trim(), _nbExposed, _nbAsTool) ?? "";

    private string NbValidate(string manifest)
    {
        if (_nbTitle.Trim().Length == 0) return "Give your node a title.";
        if (manifest.Length == 0) return "Add a Subflow Start (the Start node) so your recipe can run.";
        try
        {
            var def = CustomNode.Load(manifest);
            if (def == null) return "Couldn't assemble the node - check your pins/wiring.";
            if (NodeCatalog.TryGet(def.TypeId, out var existing) && !NodeCatalog.IsCustom(existing.TypeId))
                return $"'{def.TypeId}' clashes with a built-in node - rename it.";
        }
        catch (Exception ex) { return ex.Message; }
        return "";
    }

    private void DrawNodeBuilder(Renderer r, Clock clock)
    {
        EnsureCompositeEditor();
        r.Begin();
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.5f));
        float pw = _nbMax ? _vw - 24 : Math.Min(880, _vw - 60);
        // when maximized, sit BELOW the custom title bar so this modal's own maximize/restore button never
        // overlaps (and steals clicks from) the window's close button up in the title bar.
        float topMargin = _nbMax ? Layout.TitlebarH + 8 : 0;
        float ph = _nbMax ? _vh - topMargin - 12 : Math.Min(700, _vh - 60);
        float top = _nbMax ? topMargin : (_vh - ph) / 2f;
        var panel = new RectF((_vw - pw) / 2f, top, pw, ph);
        Hud.Panel(r, panel, _nbEditId.Length > 0 ? "Edit node" : "Bake a node", Theme.Violet);
        if (_ui.Button("nb.max", new RectF(panel.Right - 40, panel.Y + 7, 28, 24), _nbMax ? "-" : "+", Theme.Idle)) _nbMax = !_nbMax;

        var lbl = r.Fonts.Get(FontKind.SansBold, 10);
        void Label(string t, float lx, float ly2) => r.Text(lbl, t, new Vector2(lx, ly2), Theme.TextDim);
        float pad = 22, cx = panel.X + pad, cw = panel.W - 2 * pad;
        float y = panel.Y + Hud.HeaderH + 12;

        r.Text(r.Fonts.Get(FontKind.Sans, 12), "Build your node from other nodes - wire ingredients between Start and Output.",
            new Vector2(cx, y), Theme.TextDim);
        y += 22;

        // ---- shared identity row + description ----
        float catW = 132, iconW = 52, gap = 10, titleW = cw - catW - iconW - 2 * gap;
        Label("TITLE", cx, y); Label("ICON", cx + titleW + gap, y); Label("CATEGORY", cx + titleW + gap + iconW + gap, y);
        float fy = y + 15;
        _nbTitle = _ui.TextField("nb.title", new RectF(cx, fy, titleW, 28), _nbTitle, "My Node");
        _nbIcon = _ui.TextField("nb.icon", new RectF(cx + titleW + gap, fy, iconW, 28), _nbIcon, "puzzle-piece");
        _nbCategory = _ui.Choice("nb.cat", new RectF(cx + titleW + gap + iconW + gap, fy, catW, 28), NbCategories, _nbCategory);
        y = fy + 28 + 10;
        _nbDesc = _ui.TextField("nb.desc", new RectF(cx, y, cw, 26), _nbDesc, "what this node does (one sentence)");
        y += 26 + 8;
        _nbAsTool = _ui.Toggle("nb.astool", new RectF(cx, y, cw, 22), _nbAsTool, Ircuitry.Core.Icons.Glyph("toolbox") + "  Usable as an AI tool - wire into Ask AI (input pins = the model's arguments, first output = result)");
        y += 22 + 10;

        DrawNbCompositeBody(r, panel, cx, cw, y, clock, Label);

        // ---- footer ----
        string manifest = NbBuildManifest();
        string err = NbValidate(manifest);
        bool ok = err.Length == 0;
        var sans = r.Fonts.Get(FontKind.Sans, 12);
        r.Text(sans, _nbStatus.Length > 0 ? _nbStatus : (ok ? "ready to bake  ·  " + NbTypeId() : Ircuitry.Core.Icons.Glyph("warning") + " " + err),
            new Vector2(cx, panel.Bottom - 44), ok ? Theme.Lime : Theme.Amber);

        float bw = 150, bh = 34, bx = panel.Right - pad - bw, by = panel.Bottom - bh - 8;
        if (_ui.Button("nb.save", new RectF(bx, by, bw, bh), _nbEditId.Length > 0 ? Ircuitry.Core.Icons.Glyph("cake") + "  SAVE" : Ircuitry.Core.Icons.Glyph("cake") + "  BAKE", Theme.Violet, primary: true, enabled: ok))
            NbSave(manifest);
        if (_ui.Button("nb.submit", new RectF(bx - 10 - 110, by, 110, bh), "SUBMIT " + Ircuitry.Core.Icons.Glyph("arrow-up-right"), Theme.Berry, enabled: ok)) NbSubmit(manifest);
        if (_ui.Button("nb.export", new RectF(bx - 10 - 110 - 10 - 100, by, 100, bh), "EXPORT", Theme.Sky, enabled: ok)) NbExport(manifest);
        if (_ui.Button("nb.cancel", new RectF(cx, by, 90, bh), "CLOSE", Theme.Idle)) _nbOpen = false;

        if (In.LeftPressed && !panel.Contains(In.Mouse) && !_nbJustOpened) _nbOpen = false;
        _nbJustOpened = false;
        r.End();
    }

    private void DrawNbCompositeBody(Renderer r, RectF panel, float cx, float cw, float top, Clock clock, Action<string, float, float> Label)
    {
        EnsureCompositeEditor();
        float inspW = Math.Min(320, cw * 0.38f), gap = 12, edW = cw - inspW - gap;

        // toolbar over the editor column: quick-adds + a search to drop any node in
        if (_ui.Button("nb.c.in", new RectF(cx, top, 92, 26), "+ Input", Theme.Idle)) AddCompositeNode("flow.arg");
        if (_ui.Button("nb.c.out", new RectF(cx + 100, top, 96, 26), "+ Output", Theme.Idle)) AddCompositeNode("flow.return");
        _nbAdd = _ui.TextField("nb.add", new RectF(cx + 204, top, edW - 204, 26), _nbAdd, "search a node to add…");
        float bodyTop = top + 26 + 8;

        _nbEditorRect = new RectF(cx, bodyTop, edW, (panel.Bottom - 56) - bodyTop);
        r.RoundFill(_nbEditorRect, Theme.WithAlpha(Color.Black, 0.18f), 8f);

        var matches = _nbAdd.Trim().Length > 0
            ? NodeCatalog.All.Where(d => !d.IsTrigger && (d.Title.Contains(_nbAdd, StringComparison.OrdinalIgnoreCase) || d.TypeId.Contains(_nbAdd, StringComparison.OrdinalIgnoreCase))).Take(7).ToList()
            : new List<NodeDef>();

        // the editor manages its own render batches: close ours, let it draw, reopen for the rest
        bool capturing = _ui.AnyFieldFocused || matches.Count > 0;
        _nbEditor!.Running = false;
        _nbEditor.Update(In, _nbEditorRect, capturing);
        r.End();
        _nbEditor.Draw(r, _nbEditorRect, In, clock);
        r.Begin();

        r.Text(r.Fonts.Get(FontKind.Sans, 11), "drop nodes and wire them · select a node to edit/expose its settings " + Ircuitry.Core.Icons.Glyph("arrow-right") + " · Del removes",
            new Vector2(cx + 8, _nbEditorRect.Bottom - 18), Theme.TextFaint);

        // inspector for the selected inner node (edit its settings, choose hardcode vs expose)
        var inspRect = new RectF(cx + edW + gap, top, inspW, (panel.Bottom - 56) - top);
        r.RoundFill(inspRect, Theme.WithAlpha(Color.Black, 0.12f), 8f);
        DrawNbInspector(r, inspRect);

        if (matches.Count > 0)
        {
            float my = top + 26 + 6, mw = edW - 204, mx = cx + 204;
            r.RoundFill(new RectF(mx, my, mw, matches.Count * 26 + 6), Theme.Panel, 7f);
            r.RoundOutline(new RectF(mx, my, mw, matches.Count * 26 + 6), Theme.Edge, 7f);
            for (int i = 0; i < matches.Count; i++)
                if (_ui.Button($"nb.m{i}", new RectF(mx + 3, my + 3 + i * 26, mw - 6, 24), matches[i].Icon + "  " + matches[i].Title, Theme.Idle))
                    AddCompositeNode(matches[i].TypeId);
        }
    }

    private bool NbOtherExposes(string skipNodeId, string token)
    {
        foreach (var n in _nbGraph!.Nodes)
            if (n.Id != skipNodeId)
                foreach (var p in n.Def.Params)
                    if (n.GetParam(p.Key) == token) return true;
        return false;
    }

    private void DrawNbInspector(Renderer r, RectF box)
    {
        float x = box.X + 12, w = box.W - 24, y = box.Y + 12;
        var lbl = r.Fonts.Get(FontKind.SansBold, 9);
        var sans = r.Fonts.Get(FontKind.Sans, 11);
        var sel = _nbEditor!.Selection;
        if (sel.Count != 1)
        {
            foreach (var line in Wrap(sans, "Select one node to edit its settings - and pick which become " + Ircuitry.Core.Icons.Glyph("lock") + " hard-coded vs " + Ircuitry.Core.Icons.Glyph("user") + " filled by whoever uses the node.", w))
            { r.Text(sans, line, new Vector2(x, y), Theme.TextDim); y += 16; }
            return;
        }
        var node = _nbGraph!.Nodes.FirstOrDefault(n => sel.Contains(n.Id));
        if (node == null) return;
        bool boundary = node.TypeId is "flow.in" or "flow.arg" or "flow.return";
        r.Text(r.Fonts.Get(FontKind.SansBold, 13), Ircuitry.Core.Icons.Glyph(node.Def.Icon) + "  " + node.Def.Title, new Vector2(x, y), Theme.Text); y += 24;
        if (node.Def.Params.Length == 0) { r.Text(sans, "no settings on this node", new Vector2(x, y), Theme.TextFaint); return; }

        foreach (var p in node.Def.Params)
        {
            if (p.VisibleWhen != null && !p.VisibleWhen(node)) continue;
            if (y > box.Bottom - 30) { r.Text(sans, "…", new Vector2(x, y), Theme.TextFaint); break; }
            string val = node.GetParam(p.Key);
            string token = "{" + p.Key + "}";
            bool exposed = val == token;
            bool canExpose = !boundary && (p.Type == ParamType.Text || p.Type == ParamType.Multiline);
            r.Text(lbl, p.Label.ToUpperInvariant(), new Vector2(x, y), Theme.TextDim); y += 13;

            if (exposed)
            {
                r.Text(sans, Ircuitry.Core.Icons.Glyph("user") + " filled by the user", new Vector2(x, y + 4), Theme.Lime);
                if (_ui.Button($"nb.unx.{node.Id}.{p.Key}", new RectF(x + w - 96, y, 96, 24), Ircuitry.Core.Icons.Glyph("lock") + " Hard-code", Theme.Idle))
                {
                    node.SetParam(p.Key, _nbExposed.TryGetValue(p.Key, out var d) ? d : p.Default);
                    if (!NbOtherExposes(node.Id, token)) _nbExposed.Remove(p.Key);
                }
            }
            else
            {
                float fw = canExpose ? w - 78 : w;
                if (p.Type == ParamType.Choice && p.Choices is { Length: > 0 })
                    node.SetParam(p.Key, _ui.Choice($"nb.pv.{node.Id}.{p.Key}", new RectF(x, y, fw, 24), p.Choices, val));
                else if (p.Type == ParamType.Bool)
                    node.SetParam(p.Key, _ui.Choice($"nb.pv.{node.Id}.{p.Key}", new RectF(x, y, fw, 24), BoolChoices, val == "true" || val == "1" ? "true" : "false"));
                else
                    node.SetParam(p.Key, _ui.TextField($"nb.pv.{node.Id}.{p.Key}", new RectF(x, y, fw, 24), val, p.Placeholder));
                if (canExpose && _ui.Button($"nb.exp.{node.Id}.{p.Key}", new RectF(x + w - 74, y, 74, 24), Ircuitry.Core.Icons.Glyph("user") + " Expose", Theme.Berry))
                {
                    _nbExposed[p.Key] = val.Length > 0 ? val : p.Default;   // current value becomes the setting's default
                    node.SetParam(p.Key, token);
                }
            }
            y += 30;
        }
    }

    private static readonly string[] BoolChoices = { "true", "false" };

    private void NbSave(string manifest)
    {
        try
        {
            var def = CustomNode.Load(manifest) ?? throw new Exception("invalid");
            System.IO.Directory.CreateDirectory(NodeCatalog.CustomDir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(NodeCatalog.CustomDir, def.TypeId + ".ircnode"), manifest);
            NodeCatalog.LoadCustom();
            Bot.Log.Add(LogLevel.System, $"baked node “{def.Title}” " + Ircuitry.Core.Icons.Glyph("arrow-right") + " Node Library " + Ircuitry.Core.Icons.Glyph("caret-right") + $" {def.Category}");
            PushToast(Ircuitry.Core.Icons.Glyph("cake") + $" {def.Title} baked into your library");
            _nbOpen = false;
        }
        catch (Exception ex) { _nbStatus = "save failed: " + ex.Message; }
    }

    private void NbExport(string manifest)
    {
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(AppModel.WorkspaceDir, NbTypeId() + ".ircnode"), manifest);
            PushToast(Ircuitry.Core.Icons.Glyph("file") + " exported - opening folder");
            Ircuitry.App.DeepLink.OpenUrl(AppModel.WorkspaceDir);
        }
        catch (Exception ex) { _nbStatus = "export failed: " + ex.Message; }
    }

    private void NbSubmit(string manifest)
    {
        string file = "nodes/" + NbTypeId() + ".ircnode";
        string prefilled = $"https://github.com/ircuitry/community-nodes/new/main?filename={Uri.EscapeDataString(file)}&value={Uri.EscapeDataString(manifest)}";
        try
        {
            if (prefilled.Length <= 7000) { Ircuitry.App.DeepLink.OpenUrl(prefilled); PushToast(Ircuitry.Core.Icons.Glyph("arrow-up-right") + " opening a GitHub PR for your node"); }
            else { try { Clipboard.SetText(manifest); } catch { } Ircuitry.App.DeepLink.OpenUrl("https://github.com/ircuitry/community-nodes/new/main"); PushToast("node copied - paste it into the GitHub editor"); }
        }
        catch (Exception ex) { _nbStatus = "submit failed: " + ex.Message; }
    }
}
