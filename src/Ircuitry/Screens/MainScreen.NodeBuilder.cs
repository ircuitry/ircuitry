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

// "Bake a node": one modal, two kinds. RECIPE = a node built from other nodes (ingredients), wired in a
// little embedded editor. FROM SCRATCH = a code node (form + code). Both can be created fresh or re-opened
// to edit, then baked into the Node Library, exported, or submitted as a PR.
public partial class MainScreen
{
    private bool _nbOpen, _nbJustOpened;
    private string _nbMode = "code";       // "code" | "composite"
    private string _nbEditId = "";         // typeId being edited (overwrite that file); "" = new node
    private string _nbTitle = "", _nbIcon = "🧩", _nbCategory = "Action", _nbDesc = "", _nbLang = "python", _nbCode = "";
    private readonly List<(string name, string kind)> _nbIn = new();
    private readonly List<(string name, string kind)> _nbOut = new();
    private string _nbStatus = "", _nbAdd = "";
    private bool _nbSeeded;

    private NodeGraph? _nbGraph;            // the composite's inner graph
    private GraphEditor? _nbEditor;         // mini editor over _nbGraph

    private static readonly string[] NbKinds = { "Text", "Number", "Bool", "User", "Channel", "Tool", "Exec" };
    private static readonly string[] NbCategories = { "Action", "Data", "Logic", "Ai", "Filter", "Storage" };
    private static readonly string[] NbLangs = { "python", "js" };

    public void DebugOpenNodeBuilder() { _l = Layout.Compute(_vw, _vh); OpenNodeBuilder(); }
    public void DebugOpenComposite() { _l = Layout.Compute(_vw, _vh); OpenNodeBuilder(); _nbMode = "composite"; _nbTitle = "Shout"; EnsureCompositeEditor(); }

    public void OpenNodeBuilder()
    {
        if (!_nbSeeded) { ApplyNodeTemplate("simple"); _nbSeeded = true; }
        _nbEditId = ""; _nbOpen = true; _nbJustOpened = true; _ui.Focus = "nb.title";
    }

    /// <summary>Re-open an installed custom node to edit it (code in the form, composite in the mini editor).</summary>
    public void OpenNodeBuilderForEdit(string typeId)
    {
        _l = Layout.Compute(_vw, _vh);
        try
        {
            var path = System.IO.Path.Combine(NodeCatalog.CustomDir, typeId + ".ircnode");
            if (!System.IO.File.Exists(path)) { PushToast("can't find that node's file to edit"); return; }
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
            var rt = doc.RootElement;
            string S(string k, string d = "") => rt.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? d : d;
            _nbTitle = S("title", typeId); _nbIcon = S("icon", "🧩"); _nbCategory = S("category", "Logic"); _nbDesc = S("description");
            _nbEditId = typeId; _nbStatus = "";

            if (rt.TryGetProperty("subgraph", out var sg) && sg.ValueKind == JsonValueKind.Object)
            {
                _nbMode = "composite";
                _nbGraph = GraphSerializer.Load(sg.GetRawText()).graph;
                _nbEditor = new GraphEditor(_nbGraph) { ShowMinimap = false };
            }
            else
            {
                _nbMode = "code";
                _nbLang = S("language", "python"); _nbCode = S("code");
                _nbIn.Clear(); _nbOut.Clear();
                LoadPins(rt, "inputs", _nbIn); LoadPins(rt, "outputs", _nbOut);
                _nbSeeded = true;
            }
            _nbOpen = true; _nbJustOpened = true; _ui.Focus = "nb.title";
        }
        catch (Exception ex) { PushToast("couldn't open node: " + ex.Message); }
    }

    private static void LoadPins(JsonElement rt, string key, List<(string, string)> into)
    {
        if (!rt.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (var p in arr.EnumerateArray())
        {
            string nm = p.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
            string kd = p.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? "Text" : "Text";
            if (kd == "Exec" && nm.Length == 0) continue;   // the implicit exec pins are added back automatically
            into.Add((nm, kd));
        }
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

    private void ApplyNodeTemplate(string kind)
    {
        _nbMode = "code"; _nbIn.Clear(); _nbOut.Clear();
        if (kind == "aitool")
        {
            _nbTitle = "Web Search"; _nbIcon = "🔎"; _nbCategory = "Ai"; _nbLang = "python";
            _nbDesc = "AI tool: searches the web and returns the top 3 results.";
            _nbIn.Add(("query", "Text"));
            _nbOut.Add(("tool", "Tool")); _nbOut.Add(("results", "Text"));
            _nbCode =
                "import os, json, urllib.parse, urllib.request\n" +
                "q = (os.environ.get('QUERY') or '').strip()\n" +
                "url = 'https://api.duckduckgo.com/?' + urllib.parse.urlencode({'q': q, 'format':'json','no_html':1,'skip_disambig':1})\n" +
                "try:\n" +
                "    data = json.load(urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent':'ircuitry'}), timeout=10))\n" +
                "except Exception as e:\n" +
                "    print('error: ' + str(e)); raise SystemExit\n" +
                "out = [t['Text'] for t in data.get('RelatedTopics', []) if t.get('Text')][:3]\n" +
                "print('\\n'.join(out) if out else 'no results')\n";
        }
        else
        {
            _nbTitle = ""; _nbIcon = "🧩"; _nbCategory = "Action"; _nbLang = "python"; _nbDesc = "";
            _nbIn.Add(("text", "Text"));
            _nbOut.Add(("result", "Text"));
            _nbCode = "import os\ntext = os.environ.get('TEXT') or os.environ.get('INPUT') or ''\nprint('you said: ' + text)\n";
        }
        _nbStatus = "";
    }

    private string NbTypeId()
    {
        if (_nbEditId.Length > 0) return _nbEditId;   // editing -> stable id, overwrite the same file
        string src = _nbTitle.Trim().Length > 0 ? _nbTitle : "custom node";
        var slug = new string(src.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        string prefix = _nbMode == "composite" ? "subflow." : "custom.";
        return slug.Length > 0 ? prefix + slug : prefix + "node";
    }

    private string NbBuildManifest()
    {
        if (_nbMode == "composite")
            return _nbEditor?.SerializeAsComposite(NbTypeId(),
                _nbTitle.Trim().Length > 0 ? _nbTitle.Trim() : "Custom Node",
                _nbIcon.Trim().Length > 0 ? _nbIcon.Trim() : "🧩", _nbCategory, _nbDesc.Trim()) ?? "";

        var node = new Dictionary<string, object?>
        {
            ["typeId"] = NbTypeId(),
            ["title"] = _nbTitle.Trim().Length > 0 ? _nbTitle.Trim() : "Custom Node",
            ["subtitle"] = "custom",
            ["icon"] = _nbIcon.Trim().Length > 0 ? _nbIcon.Trim() : "🧩",
            ["category"] = _nbCategory,
            ["description"] = _nbDesc.Trim(),
            ["author"] = "",
            ["tags"] = Array.Empty<string>(),
            ["inputs"] = _nbIn.Where(p => p.name.Trim().Length > 0 || p.kind == "Exec")
                              .Select(p => new Dictionary<string, object?> { ["name"] = p.name.Trim(), ["kind"] = p.kind }).ToArray(),
            ["outputs"] = _nbOut.Where(p => p.name.Trim().Length > 0 || p.kind == "Exec")
                               .Select(p => new Dictionary<string, object?> { ["name"] = p.name.Trim(), ["kind"] = p.kind }).ToArray(),
            ["params"] = Array.Empty<object>(),
            ["language"] = _nbLang,
            ["timeout"] = 12,
            ["code"] = _nbCode,
        };
        return JsonSerializer.Serialize(node, new JsonSerializerOptions { WriteIndented = true });
    }

    private string NbValidate(string manifest)
    {
        if (_nbTitle.Trim().Length == 0) return "Give your node a title.";
        if (_nbMode == "composite" && manifest.Length == 0) return "Add a Subflow Start (⤵ Start) to your composite.";
        if (_nbMode == "code" && _nbCode.Trim().Length == 0) return "Write some code (or pick a starter).";
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
        r.Begin();
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.5f));
        float pw = Math.Min(880, _vw - 60), ph = Math.Min(700, _vh - 60);
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        Hud.Panel(r, panel, _nbEditId.Length > 0 ? "Edit node" : "Bake a node", Theme.Violet);

        var lbl = r.Fonts.Get(FontKind.SansBold, 10);
        void Label(string t, float lx, float ly2) => r.Text(lbl, t, new Vector2(lx, ly2), Theme.TextDim);
        float pad = 22, cx = panel.X + pad, cw = panel.W - 2 * pad;
        float y = panel.Y + Hud.HeaderH + 12;

        // ---- kind toggle ----  Recipe = combine nodes (ingredients); From scratch = write code
        bool code = _nbMode == "code";
        if (_ui.Button("nb.mode.code", new RectF(cx, y, 182, 28), "🍳 From scratch (code)", code ? Theme.Violet : Theme.Idle, primary: code)) _nbMode = "code";
        if (_ui.Button("nb.mode.comp", new RectF(cx + 190, y, 160, 28), "🥣 Recipe (nodes)", code ? Theme.Idle : Theme.Violet, primary: !code))
        { _nbMode = "composite"; EnsureCompositeEditor(); }
        y += 28 + 12;

        // ---- shared identity row + description ----
        float catW = 132, iconW = 52, gap = 10, titleW = cw - catW - iconW - 2 * gap;
        Label("TITLE", cx, y); Label("ICON", cx + titleW + gap, y); Label("CATEGORY", cx + titleW + gap + iconW + gap, y);
        float fy = y + 15;
        _nbTitle = _ui.TextField("nb.title", new RectF(cx, fy, titleW, 28), _nbTitle, "My Node");
        _nbIcon = _ui.TextField("nb.icon", new RectF(cx + titleW + gap, fy, iconW, 28), _nbIcon, "🧩");
        _nbCategory = _ui.Choice("nb.cat", new RectF(cx + titleW + gap + iconW + gap, fy, catW, 28), NbCategories, _nbCategory);
        y = fy + 28 + 10;
        _nbDesc = _ui.TextField("nb.desc", new RectF(cx, y, cw, 26), _nbDesc, "what this node does (one sentence)");
        y += 26 + 12;

        if (code) DrawNbCodeBody(r, panel, cx, cw, y, Label);
        else DrawNbCompositeBody(r, panel, cx, cw, y, clock, Label);

        // ---- footer ----
        string manifest = NbBuildManifest();
        string err = NbValidate(manifest);
        bool ok = err.Length == 0;
        var sans = r.Fonts.Get(FontKind.Sans, 12);
        r.Text(sans, _nbStatus.Length > 0 ? _nbStatus : (ok ? "✓ ready to bake  ·  " + NbTypeId() : "⚠ " + err),
            new Vector2(cx, panel.Bottom - 44), ok ? Theme.Lime : Theme.Amber);

        float bw = 150, bh = 34, bx = panel.Right - pad - bw, by = panel.Bottom - bh - 8;
        if (_ui.Button("nb.save", new RectF(bx, by, bw, bh), _nbEditId.Length > 0 ? "🧁  SAVE" : "🧁  BAKE", Theme.Violet, primary: true, enabled: ok))
            NbSave(manifest);
        if (_ui.Button("nb.submit", new RectF(bx - 10 - 110, by, 110, bh), "SUBMIT ↗", Theme.Berry, enabled: ok)) NbSubmit(manifest);
        if (_ui.Button("nb.export", new RectF(bx - 10 - 110 - 10 - 100, by, 100, bh), "EXPORT", Theme.Sky, enabled: ok)) NbExport(manifest);
        if (_ui.Button("nb.cancel", new RectF(cx, by, 90, bh), "CLOSE", Theme.Idle)) _nbOpen = false;

        if (In.LeftPressed && !panel.Contains(In.Mouse) && !_nbJustOpened) _nbOpen = false;
        _nbJustOpened = false;
        r.End();
    }

    private void DrawNbCodeBody(Renderer r, RectF panel, float cx, float cw, float colTop, Action<string, float, float> Label)
    {
        float leftW = cw * 0.46f, rightX = cx + leftW + 18, rightW = cx + cw - rightX;
        float ly = colTop;
        Label("INPUTS  (an AI tool's args)", cx, ly); ly += 16;
        for (int i = 0; i < _nbIn.Count; i++)
        {
            _nbIn[i] = (_ui.TextField($"nb.in{i}", new RectF(cx, ly, leftW - 126, 26), _nbIn[i].name, "name"),
                        _ui.Choice($"nb.ink{i}", new RectF(cx + leftW - 122, ly, 90, 26), NbKinds, _nbIn[i].kind));
            if (_ui.Button($"nb.inx{i}", new RectF(cx + leftW - 26, ly, 26, 26), "✕", Theme.Idle)) { _nbIn.RemoveAt(i); break; }
            ly += 30;
        }
        if (_nbIn.Count < 6 && _ui.Button("nb.inadd", new RectF(cx, ly, 110, 26), "＋ input", Theme.Idle)) _nbIn.Add(("", "Text"));
        ly += 38;
        Label("OUTPUTS  (Tool = AI handle · 1st data pin = result)", cx, ly); ly += 16;
        for (int i = 0; i < _nbOut.Count; i++)
        {
            _nbOut[i] = (_ui.TextField($"nb.out{i}", new RectF(cx, ly, leftW - 126, 26), _nbOut[i].name, "name"),
                         _ui.Choice($"nb.outk{i}", new RectF(cx + leftW - 122, ly, 90, 26), NbKinds, _nbOut[i].kind));
            if (_ui.Button($"nb.outx{i}", new RectF(cx + leftW - 26, ly, 26, 26), "✕", Theme.Idle)) { _nbOut.RemoveAt(i); break; }
            ly += 30;
        }
        if (_nbOut.Count < 6 && _ui.Button("nb.outadd", new RectF(cx, ly, 110, 26), "＋ output", Theme.Idle)) _nbOut.Add(("", "Text"));
        ly += 40;
        Label("START FROM A STARTER", cx, ly); ly += 16;
        if (_ui.Button("nb.tpl.simple", new RectF(cx, ly, 120, 26), "Simple", Theme.Idle)) ApplyNodeTemplate("simple");
        if (_ui.Button("nb.tpl.aitool", new RectF(cx + 128, ly, 150, 26), "🔎 AI web tool", Theme.Idle)) ApplyNodeTemplate("aitool");

        Label("LANGUAGE", rightX, colTop);
        _nbLang = _ui.Choice("nb.lang", new RectF(rightX, colTop + 15, 120, 26), NbLangs, _nbLang);
        Label("CODE  (inputs arrive as UPPERCASE vars; print the result)", rightX, colTop + 50);
        float codeY = colTop + 66;
        _nbCode = _ui.TextArea("nb.code", new RectF(rightX, codeY, rightW, (panel.Bottom - 56) - codeY), _nbCode, "print('hello')");
    }

    private void DrawNbCompositeBody(Renderer r, RectF panel, float cx, float cw, float top, Clock clock, Action<string, float, float> Label)
    {
        EnsureCompositeEditor();
        // toolbar: quick-adds + a search to drop any node in
        if (_ui.Button("nb.c.in", new RectF(cx, top, 96, 26), "＋ Input", Theme.Idle)) AddCompositeNode("flow.arg");
        if (_ui.Button("nb.c.out", new RectF(cx + 104, top, 100, 26), "＋ Output", Theme.Idle)) AddCompositeNode("flow.return");
        _nbAdd = _ui.TextField("nb.add", new RectF(cx + 214, top, cw - 214, 26), _nbAdd, "search an ingredient (node) to add…");
        float bodyTop = top + 26 + 8;

        // the mini editor fills the rest
        _nbEditorRect = new RectF(cx, bodyTop, cw, (panel.Bottom - 56) - bodyTop);
        r.RoundFill(_nbEditorRect, Theme.WithAlpha(Color.Black, 0.18f), 8f);

        var matches = _nbAdd.Trim().Length > 0
            ? NodeCatalog.All.Where(d => !d.IsTrigger && (d.Title.Contains(_nbAdd, StringComparison.OrdinalIgnoreCase) || d.TypeId.Contains(_nbAdd, StringComparison.OrdinalIgnoreCase))).Take(7).ToList()
            : new List<NodeDef>();

        // drive + draw the embedded editor (don't let it grab the mouse while a field/the picker is active).
        // The editor manages its own render batches, so close ours, let it draw, then reopen for the rest.
        bool capturing = _ui.AnyFieldFocused || matches.Count > 0;
        _nbEditor!.Running = false;
        _nbEditor.Update(In, _nbEditorRect, capturing);
        r.End();
        _nbEditor.Draw(r, _nbEditorRect, In, clock);
        r.Begin();

        r.Text(r.Fonts.Get(FontKind.Sans, 11), "drop ingredients (nodes) and wire them · Subflow Input/Output are your pins · Del removes",
            new Vector2(cx + 8, _nbEditorRect.Bottom - 18), Theme.TextFaint);

        // search results dropdown (on top of the editor)
        if (matches.Count > 0)
        {
            float my = top + 26 + 6, mw = cw - 214, mx = cx + 214;
            r.RoundFill(new RectF(mx, my, mw, matches.Count * 26 + 6), Theme.Panel, 7f);
            r.RoundOutline(new RectF(mx, my, mw, matches.Count * 26 + 6), Theme.Edge, 7f);
            for (int i = 0; i < matches.Count; i++)
                if (_ui.Button($"nb.m{i}", new RectF(mx + 3, my + 3 + i * 26, mw - 6, 24), matches[i].Icon + "  " + matches[i].Title + "   " + matches[i].TypeId, Theme.Idle))
                    AddCompositeNode(matches[i].TypeId);
        }
    }

    private void NbSave(string manifest)
    {
        try
        {
            var def = CustomNode.Load(manifest) ?? throw new Exception("invalid");
            System.IO.Directory.CreateDirectory(NodeCatalog.CustomDir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(NodeCatalog.CustomDir, def.TypeId + ".ircnode"), manifest);
            NodeCatalog.LoadCustom();
            Bot.Log.Add(LogLevel.System, $"baked node “{def.Title}” → Node Library ▸ {def.Category}");
            PushToast($"🧁 {def.Title} baked into your library");
            _nbOpen = false;
        }
        catch (Exception ex) { _nbStatus = "save failed: " + ex.Message; }
    }

    private void NbExport(string manifest)
    {
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(AppModel.WorkspaceDir, NbTypeId() + ".ircnode"), manifest);
            PushToast("📄 exported - opening folder");
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
            if (prefilled.Length <= 7000) { Ircuitry.App.DeepLink.OpenUrl(prefilled); PushToast("↗ opening a GitHub PR for your node"); }
            else { try { Clipboard.SetText(manifest); } catch { } Ircuitry.App.DeepLink.OpenUrl("https://github.com/ircuitry/community-nodes/new/main"); PushToast("node copied - paste it into the GitHub editor"); }
        }
        catch (Exception ex) { _nbStatus = "submit failed: " + ex.Message; }
    }
}
