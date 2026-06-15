using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Ircuitry.App;
using Ircuitry.Core;
using Ircuitry.Graph;
using Ircuitry.Gui;
using Ircuitry.Render;

namespace Ircuitry.Screens;

// A friendly, no-JSON way to build a custom .ircnode in the app: fill a form, write a little code (or
// start from a template), validate live, then Save to your Library, Export the file, or Submit a PR.
public partial class MainScreen
{
    private bool _nbOpen, _nbJustOpened;
    private string _nbTitle = "", _nbIcon = "🧩", _nbCategory = "Action", _nbDesc = "", _nbLang = "python", _nbCode = "";
    private readonly List<(string name, string kind)> _nbIn = new();
    private readonly List<(string name, string kind)> _nbOut = new();
    private string _nbStatus = "";
    private bool _nbSeeded;

    private static readonly string[] NbKinds = { "Text", "Number", "Bool", "User", "Channel", "Tool", "Exec" };
    private static readonly string[] NbCategories = { "Action", "Data", "Logic", "Ai", "Filter", "Storage" };
    private static readonly string[] NbLangs = { "python", "js" };

    public void DebugOpenNodeBuilder() { _l = Layout.Compute(_vw, _vh); OpenNodeBuilder(); }

    public void OpenNodeBuilder()
    {
        if (!_nbSeeded) { ApplyNodeTemplate("simple"); _nbSeeded = true; }
        _nbOpen = true; _nbJustOpened = true; _ui.Focus = "nb.title";
    }

    private void ApplyNodeTemplate(string kind)
    {
        _nbIn.Clear(); _nbOut.Clear();
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
        else // "simple"
        {
            _nbTitle = ""; _nbIcon = "🧩"; _nbCategory = "Action"; _nbLang = "python";
            _nbDesc = "";
            _nbIn.Add(("text", "Text"));
            _nbOut.Add(("result", "Text"));
            _nbCode =
                "import os\n" +
                "text = os.environ.get('TEXT') or os.environ.get('INPUT') or ''\n" +
                "print('you said: ' + text)\n";
        }
        _nbStatus = "";
    }

    private string NbTypeId()
    {
        string src = _nbTitle.Trim().Length > 0 ? _nbTitle : "custom node";
        var slug = new string(src.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Length > 0 ? "custom." + slug : "custom.node";
    }

    private string NbBuildManifest()
    {
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

    // Returns "" if the manifest is valid (loads + has a typeId), else a short reason.
    private string NbValidate(string manifest)
    {
        if (_nbTitle.Trim().Length == 0) return "Give your node a title.";
        if (_nbCode.Trim().Length == 0) return "Write some code (or pick a template).";
        try
        {
            var def = CustomNode.Load(manifest);
            if (def == null) return "Manifest didn't load - check your pins.";
            if (NodeCatalog.TryGet(def.TypeId, out var existing) && !NodeCatalog.IsCustom(existing.TypeId))
                return $"'{def.TypeId}' clashes with a built-in node - rename it.";
        }
        catch (Exception ex) { return ex.Message; }
        return "";
    }

    private void DrawNodeBuilder(Renderer r)
    {
        r.Begin();
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.5f));
        float pw = Math.Min(860, _vw - 60), ph = Math.Min(680, _vh - 60);
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        Hud.Panel(r, panel, "Bake a node", Theme.Violet);

        var sans = r.Fonts.Get(FontKind.Sans, 12);
        var lbl = r.Fonts.Get(FontKind.SansBold, 10);
        void Label(string t, float x, float y) => r.Text(lbl, t, new Vector2(x, y), Theme.TextDim);

        float pad = 22, cx = panel.X + pad, cw = panel.W - 2 * pad;
        float y = panel.Y + Hud.HeaderH + 12;

        // ---- identity row: title / icon / category ----
        float catW = 132, iconW = 52, gap = 10;
        float titleW = cw - catW - iconW - 2 * gap;
        Label("TITLE", cx, y); Label("ICON", cx + titleW + gap, y); Label("CATEGORY", cx + titleW + gap + iconW + gap, y);
        float fy = y + 15;
        _nbTitle = _ui.TextField("nb.title", new RectF(cx, fy, titleW, 28), _nbTitle, "My Node");
        _nbIcon = _ui.TextField("nb.icon", new RectF(cx + titleW + gap, fy, iconW, 28), _nbIcon, "🧩");
        _nbCategory = _ui.Choice("nb.cat", new RectF(cx + titleW + gap + iconW + gap, fy, catW, 28), NbCategories, _nbCategory);
        y = fy + 28 + 12;

        // ---- description ----
        Label("DESCRIPTION (helps the AI and people know what it does)", cx, y); y += 15;
        _nbDesc = _ui.TextField("nb.desc", new RectF(cx, y, cw, 28), _nbDesc, "One clear sentence.");
        y += 28 + 14;

        // ---- two columns: left = pins + templates, right = code ----
        float colTop = y;
        float leftW = cw * 0.46f, rightX = cx + leftW + 18, rightW = cx + cw - rightX;

        // LEFT: inputs
        float ly = colTop;
        Label("INPUTS  (the args an AI tool receives)", cx, ly); ly += 16;
        for (int i = 0; i < _nbIn.Count; i++)
        {
            _nbIn[i] = (_ui.TextField($"nb.in{i}", new RectF(cx, ly, leftW - 90 - 28 - 8, 26), _nbIn[i].name, "name"),
                        _ui.Choice($"nb.ink{i}", new RectF(cx + leftW - 90 - 28 - 4, ly, 90, 26), NbKinds, _nbIn[i].kind));
            if (_ui.Button($"nb.inx{i}", new RectF(cx + leftW - 26, ly, 26, 26), "✕", Theme.Idle)) { _nbIn.RemoveAt(i); break; }
            ly += 30;
        }
        if (_nbIn.Count < 6 && _ui.Button("nb.inadd", new RectF(cx, ly, 110, 26), "＋ input", Theme.Idle)) _nbIn.Add(("", "Text"));
        ly += 38;

        // LEFT: outputs
        Label("OUTPUTS  (Tool = AI handle · first data pin = result)", cx, ly); ly += 16;
        for (int i = 0; i < _nbOut.Count; i++)
        {
            _nbOut[i] = (_ui.TextField($"nb.out{i}", new RectF(cx, ly, leftW - 90 - 28 - 8, 26), _nbOut[i].name, "name"),
                         _ui.Choice($"nb.outk{i}", new RectF(cx + leftW - 90 - 28 - 4, ly, 90, 26), NbKinds, _nbOut[i].kind));
            if (_ui.Button($"nb.outx{i}", new RectF(cx + leftW - 26, ly, 26, 26), "✕", Theme.Idle)) { _nbOut.RemoveAt(i); break; }
            ly += 30;
        }
        if (_nbOut.Count < 6 && _ui.Button("nb.outadd", new RectF(cx, ly, 110, 26), "＋ output", Theme.Idle)) _nbOut.Add(("", "Text"));
        ly += 40;

        // LEFT: templates
        Label("START FROM A RECIPE", cx, ly); ly += 16;
        if (_ui.Button("nb.tpl.simple", new RectF(cx, ly, 120, 26), "Simple", Theme.Idle)) ApplyNodeTemplate("simple");
        if (_ui.Button("nb.tpl.aitool", new RectF(cx + 128, ly, 150, 26), "🔎 AI web tool", Theme.Idle)) ApplyNodeTemplate("aitool");

        // RIGHT: language + code
        Label("LANGUAGE", rightX, colTop);
        _nbLang = _ui.Choice("nb.lang", new RectF(rightX, colTop + 15, 120, 26), NbLangs, _nbLang);
        Label("RECIPE  (inputs arrive as UPPERCASE vars; print the result)", rightX, colTop + 50);
        float codeY = colTop + 66;
        float codeH = (panel.Bottom - 56) - codeY;
        _nbCode = _ui.TextArea("nb.code", new RectF(rightX, codeY, rightW, codeH), _nbCode, "print('hello')");

        // ---- footer: status + actions ----
        string manifest = NbBuildManifest();
        string err = NbValidate(manifest);
        bool ok = err.Length == 0;
        string status = _nbStatus.Length > 0 ? _nbStatus : (ok ? "✓ ready to bake  ·  " + NbTypeId() : "⚠ " + err);
        r.Text(sans, status, new Vector2(cx, panel.Bottom - 44), ok ? Theme.Lime : Theme.Amber);

        float bw = 150, bh = 34, bx = panel.Right - pad - bw, by = panel.Bottom - bh - 8;
        if (_ui.Button("nb.save", new RectF(bx, by, bw, bh), "🧁  BAKE", Theme.Violet, primary: true, enabled: ok))
            NbSave(manifest);
        if (_ui.Button("nb.submit", new RectF(bx - 10 - 118, by, 118, bh), "SUBMIT ↗", Theme.Berry, enabled: ok))
            NbSubmit(manifest);
        if (_ui.Button("nb.export", new RectF(bx - 10 - 118 - 10 - 110, by, 110, bh), "EXPORT", Theme.Sky, enabled: ok))
            NbExport(manifest);
        if (_ui.Button("nb.cancel", new RectF(cx, by, 96, bh), "CLOSE", Theme.Idle))
            _nbOpen = false;

        if (In.LeftPressed && !panel.Contains(In.Mouse) && !_nbJustOpened) _nbOpen = false;
        _nbJustOpened = false;
        r.End();
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
            string path = System.IO.Path.Combine(AppModel.WorkspaceDir, NbTypeId() + ".ircnode");
            System.IO.File.WriteAllText(path, manifest);
            PushToast("📄 exported - opening folder");
            Ircuitry.App.DeepLink.OpenUrl(AppModel.WorkspaceDir);
        }
        catch (Exception ex) { _nbStatus = "export failed: " + ex.Message; }
    }

    // Open a prefilled GitHub "new file" PR if it fits the URL, else copy the node and open the editor.
    private void NbSubmit(string manifest)
    {
        string file = "nodes/" + NbTypeId() + ".ircnode";
        string prefilled = $"https://github.com/ircuitry/community-nodes/new/main?filename={Uri.EscapeDataString(file)}&value={Uri.EscapeDataString(manifest)}";
        try
        {
            if (prefilled.Length <= 7000)
            {
                Ircuitry.App.DeepLink.OpenUrl(prefilled);
                PushToast("↗ opening a GitHub PR for your node");
            }
            else
            {
                try { Clipboard.SetText(manifest); } catch { }
                Ircuitry.App.DeepLink.OpenUrl("https://github.com/ircuitry/community-nodes/new/main");
                PushToast("node copied - paste it into the GitHub editor");
            }
        }
        catch (Exception ex) { _nbStatus = "submit failed: " + ex.Message; }
    }
}
