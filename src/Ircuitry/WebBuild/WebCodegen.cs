using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Ircuitry.WebBuild;

/// <summary>
/// Compiles the <see cref="WebApp"/> IR to source. Two backends share the IR (the point of the IR): a self-contained
/// VANILLA page (HTML + a ~40-line signal runtime) used for the live preview, and a REACT/Vite project used for
/// eject. Both are pure functions of the IR, so "what you preview is what you ship".
/// </summary>
public static class WebCodegen
{
    // ---------------- vanilla (live preview): one HTML file, no build step ----------------
    public static string Vanilla(WebApp app)
    {
        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"" + Esc(app.Lang.Length > 0 ? app.Lang : "en") + "\"><head><meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.AppendLine("<title>" + Esc(app.Name) + "</title>");
        if (app.Description.Length > 0) html.AppendLine("<meta name=\"description\" content=\"" + Esc(app.Description) + "\">");
        if (app.Favicon.Length > 0) html.AppendLine("<link rel=\"icon\" href=\"" + Esc(app.Favicon) + "\">");
        if (app.Tokens.Count > 0) html.AppendLine("<style>" + TokenCss(app) + "</style>");
        foreach (var css in app.Css) if (css.Trim().Length > 0) html.AppendLine("<style>" + css + "</style>");
        html.AppendLine("</head><body><div id=\"__app\"></div>");
        html.AppendLine("<script>");
        html.AppendLine("var IR = " + IrToJs(app) + ";");
        html.AppendLine(Interpreter);
        html.AppendLine("</script></body></html>");
        return html.ToString();
    }

    // the IR shipped to the live page as a JS object literal (the preview interprets it; the eject compiles it)
    private static string IrToJs(WebApp app)
    {
        var sb = new StringBuilder();
        sb.Append("{ states: [");
        sb.Append(string.Join(", ", app.States.Select(s => "{ name: " + JsStr(s.Name) + ", init: " + JsStr(s.Init) + ", kind: " + JsStr(s.Kind) + " }")));
        sb.Append("], ");
        if (app.Fetches.Count > 0)
            sb.Append("fetches: [").Append(string.Join(", ", app.Fetches.Select(f => "{ url: " + JsStr(f.Url) + ", into: " + JsStr(f.Into) + " }"))).Append("], ");
        sb.Append("root: ");
        NodeToJs(app.Root, sb);
        sb.Append(" }");
        return sb.ToString();
    }

    private static void NodeToJs(WebEl el, StringBuilder sb)
    {
        sb.Append("{ tag: ").Append(JsStr(el.Tag));
        if (el.Attrs.Count > 0) sb.Append(", attrs: { ").Append(string.Join(", ", el.Attrs.Select(kv => JsStr(kv.Key) + ": " + JsStr(kv.Value)))).Append(" }");
        if (!string.IsNullOrEmpty(el.Style)) sb.Append(", style: ").Append(JsStr(el.Style!));
        if (el.Bind != null) sb.Append(", bind: ").Append(JsStr(el.Bind));
        if (el.Model != null) sb.Append(", model: ").Append(JsStr(el.Model));
        if (el.Text != null) sb.Append(", text: ").Append(JsStr(el.Text));
        if (el.Repeat != null) sb.Append(", repeat: { list: ").Append(JsStr(el.Repeat.List)).Append(", item: ").Append(JsStr(el.Repeat.Item)).Append(", key: ").Append(JsStr(el.Repeat.Key)).Append(" }");
        if (el.On.Count > 0) sb.Append(", on: { ").Append(string.Join(", ", el.On.Select(kv => JsStr(kv.Key) + ": { state: " + JsStr(kv.Value.State) + ", op: " + JsStr(kv.Value.Op) + ", arg: " + JsStr(kv.Value.Arg) + " }"))).Append(" }");
        if (el.Children.Count > 0)
        {
            sb.Append(", children: [");
            for (int i = 0; i < el.Children.Count; i++) { if (i > 0) sb.Append(", "); NodeToJs(el.Children[i], sb); }
            sb.Append(']');
        }
        sb.Append(" }");
    }

    private static string JsStr(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";

    // the live runtime: interprets the IR against reactive state. All JS literals use single quotes + Q for the
    // attribute-value quote, so this verbatim string needs no escaping.
    private const string Interpreter = @"
var Q = '\'';
var s = {};
IR.states.forEach(function(st){
  s[st.name] = st.kind === 'number' ? (parseFloat(st.init) || 0)
    : st.kind === 'bool' ? (st.init === 'true')
    : st.kind === 'list' ? (function(){ try { return JSON.parse(st.init || '[]'); } catch(e){ return []; } })()
    : st.init;
});
var KEYS = {};
function esc(x){ return String(x == null ? '' : x).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); }
function lookup(expr, scope){ var p = expr.split('.'); var v = (p[0] in scope) ? scope[p[0]] : s[p[0]]; for (var i = 1; i < p.length; i++){ v = v == null ? '' : v[p[i]]; } return v; }
function attrs(n, scope, key){
  var a = '';
  if (n.attrs) for (var k in n.attrs) a += ' ' + k + '=' + Q + esc(n.attrs[k]) + Q;
  if (n.style) a += ' style=' + Q + esc(n.style) + Q;
  if (n.model) a += ' data-model=' + Q + n.model + Q + ' value=' + Q + esc(s[n.model]) + Q;
  if (n.on) for (var ev in n.on){ var ac = n.on[ev]; a += ' data-on-' + ev + '=' + Q + ac.state + ':' + ac.op + ':' + (ac.arg || '') + (key != null ? '#' + esc(key) : '') + Q; }
  return a;
}
function renderTag(n, scope, key){
  var inner = '';
  if (n.children && n.children.length) for (var i = 0; i < n.children.length; i++) inner += renderNode(n.children[i], scope);
  else if (n.bind) inner = esc(lookup(n.bind, scope));
  else if (n.text != null) inner = esc(n.text);
  return '<' + n.tag + attrs(n, scope, key) + '>' + inner + '</' + n.tag + '>';
}
function renderNode(n, scope){
  if (n.repeat){
    KEYS[n.repeat.list] = n.repeat.key;
    var arr = s[n.repeat.list] || [], out = '';
    for (var i = 0; i < arr.length; i++){ var sc = {}; for (var k in scope) sc[k] = scope[k]; sc[n.repeat.item] = arr[i]; out += renderTag(n, sc, arr[i][n.repeat.key]); }
    return out;
  }
  return renderTag(n, scope, null);
}
function render(){ document.getElementById('__app').innerHTML = renderNode(IR.root, {}); }
function act(spec){
  var key = null, h = spec.indexOf('#'); if (h >= 0){ key = spec.slice(h + 1); spec = spec.slice(0, h); }
  var p = spec.split(':'), n = p[0], op = p[1], arg = p[2];
  if (op === 'inc') s[n] = (parseFloat(s[n]) || 0) + (arg ? +arg : 1);
  else if (op === 'dec') s[n] = (parseFloat(s[n]) || 0) - (arg ? +arg : 1);
  else if (op === 'set') s[n] = (arg !== '' && !isNaN(+arg)) ? +arg : arg;
  else if (op === 'toggle') s[n] = !s[n];
  else if (op === 'remove'){ var kf = KEYS[n] || 'id'; s[n] = (s[n] || []).filter(function(x){ return String(x[kf]) !== String(key); }); }
  else if (op === 'add'){ var kf = KEYS[n] || 'id'; var it = {}; it[kf] = Date.now() + Math.floor(Math.random() * 1000); it.text = s[arg]; if (it.text){ s[n] = (s[n] || []).concat([it]); s[arg] = ''; } }
  render();
}
document.addEventListener('input', function(e){ var t = e.target.closest && e.target.closest('[data-model]'); if (t) s[t.getAttribute('data-model')] = t.value; });
document.addEventListener('click', function(e){ var t = e.target.closest && e.target.closest('[data-on-click]'); if (t) act(t.getAttribute('data-on-click')); });
document.addEventListener('change', function(e){ var t = e.target.closest && e.target.closest('[data-on-change]'); if (t) act(t.getAttribute('data-on-change')); });
(IR.fetches || []).forEach(function(f){ fetch(f.url).then(function(r){ return r.json(); }).then(function(d){ s[f.into] = d; render(); }).catch(function(){}); });
render();
";

    // ---------------- React (eject): the App component (+ a Vite project around it) ----------------
    public static string React(WebApp app)
    {
        var sb = new StringBuilder();
        sb.AppendLine("import { useState" + (app.Fetches.Count > 0 ? ", useEffect" : "") + " } from 'react';");
        sb.AppendLine();
        sb.AppendLine("export default function " + Ident(app.Name) + "() {");
        foreach (var s in app.States)
            sb.AppendLine("  const [" + s.Name + ", set" + Cap(s.Name) + "] = useState(" + JsLiteral(s) + ");");
        foreach (var f in app.Fetches)
            sb.AppendLine("  useEffect(() => { fetch('" + f.Url + "').then(r => r.json()).then(set" + Cap(f.Into) + ").catch(() => {}); }, []);");
        sb.AppendLine("  return (");
        var jsx = new StringBuilder();
        RenderJsx(app.Root, jsx, 4, null, null);
        sb.Append(jsx);
        sb.AppendLine("  );");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void RenderJsx(WebEl el, StringBuilder sb, int indent, WebRepeat? rep, string? keyAttr)
    {
        string pad = new string(' ', indent);

        if (el.Repeat != null)   // a list: <TAG>{LIST.map((item) => ( <template key=.../> ))}</TAG>
        {
            var template = el.Children.Count > 0 ? el.Children[0] : new WebEl { Tag = "div" };
            sb.Append(pad).Append('<').Append(el.Tag).Append(JsxAttrs(el, rep, keyAttr)).Append(">\n");
            sb.Append(pad).Append("  {").Append(el.Repeat.List).Append(".map((").Append(el.Repeat.Item).Append(") => (\n");
            RenderJsx(template, sb, indent + 4, el.Repeat, "key={" + el.Repeat.Item + "." + el.Repeat.Key + "}");
            sb.Append(pad).Append("  ))}\n");
            sb.Append(pad).Append("</").Append(el.Tag).Append(">\n");
            return;
        }

        var attrs = JsxAttrs(el, rep, keyAttr);
        bool hasContent = el.Children.Count > 0 || el.Bind != null || el.Text != null;
        sb.Append(pad).Append('<').Append(el.Tag).Append(attrs);
        if (!hasContent) { sb.Append(" />\n"); return; }
        sb.Append('>');
        if (el.Children.Count == 0)
        {
            if (el.Bind != null) sb.Append('{').Append(el.Bind).Append('}');
            else if (el.Text != null) sb.Append(JsxText(el.Text));
            sb.Append("</").Append(el.Tag).Append(">\n");
        }
        else
        {
            sb.Append('\n');
            foreach (var c in el.Children) RenderJsx(c, sb, indent + 2, rep, null);
            sb.Append(pad).Append("</").Append(el.Tag).Append(">\n");
        }
    }

    private static string JsxAttrs(WebEl el, WebRepeat? rep, string? keyAttr)
    {
        var a = new StringBuilder();
        if (keyAttr != null) a.Append(' ').Append(keyAttr);
        foreach (var kv in el.Attrs)
        {
            string name = kv.Key == "class" ? "className" : kv.Key == "for" ? "htmlFor" : kv.Key;
            a.Append(' ').Append(name).Append("=\"").Append(Esc(kv.Value)).Append('"');
        }
        if (!string.IsNullOrWhiteSpace(el.Style)) a.Append(" style={{ ").Append(CssToReactStyle(el.Style!)).Append(" }}");
        if (el.Model != null) a.Append(" value={").Append(el.Model).Append("} onChange={(e) => set").Append(Cap(el.Model)).Append("(e.target.value)}");
        foreach (var kv in el.On) a.Append(' ').Append(JsxEvent(kv.Key)).Append("={").Append(ReactHandler(kv.Value, rep)).Append('}');
        return a.ToString();
    }

    private static string JsxEvent(string dom) => dom switch
    {
        "click" => "onClick", "input" => "onInput", "change" => "onChange", "submit" => "onSubmit",
        _ => "on" + Cap(dom),
    };

    private static string ReactHandler(WebAction a, WebRepeat? rep)
    {
        string set = "set" + Cap(a.State);
        return a.Op switch
        {
            "inc" => "() => " + set + "(v => v + " + (a.Arg.Length > 0 ? a.Arg : "1") + ")",
            "dec" => "() => " + set + "(v => v - " + (a.Arg.Length > 0 ? a.Arg : "1") + ")",
            "toggle" => "() => " + set + "(v => !v)",
            "set" => "() => " + set + "(" + SetLiteral(a.Arg) + ")",
            "remove" => rep != null ? "() => " + set + "(a => a.filter(x => x." + rep.Key + " !== " + rep.Item + "." + rep.Key + "))" : "() => {}",
            "add" => "() => { " + set + "(a => [...a, { id: Date.now(), text: " + a.Arg + " }]); set" + Cap(a.Arg) + "('') }",
            _ => "() => {}",
        };
    }

    /// <summary>The full Vite + React project for eject (file path -> contents).</summary>
    public static Dictionary<string, string> ReactProject(WebApp app)
    {
        string comp = Ident(app.Name);
        return new Dictionary<string, string>
        {
            ["package.json"] = "{\n  \"name\": \"" + Slug(app.Name) + "\",\n  \"private\": true,\n  \"type\": \"module\",\n" +
                "  \"scripts\": { \"dev\": \"vite\", \"build\": \"vite build\", \"preview\": \"vite preview\" },\n" +
                "  \"dependencies\": { \"react\": \"^18.3.1\", \"react-dom\": \"^18.3.1\" },\n" +
                "  \"devDependencies\": { \"@vitejs/plugin-react\": \"^4.3.1\", \"vite\": \"^5.4.0\" }\n}\n",
            ["vite.config.js"] = "import react from '@vitejs/plugin-react';\nexport default { plugins: [react()], server: { host: true } };\n",
            ["index.html"] = "<!doctype html>\n<html lang=\"" + Esc(app.Lang.Length > 0 ? app.Lang : "en") + "\"><head><meta charset=\"utf-8\">\n" +
                "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<title>" + Esc(app.Name) + "</title>\n" +
                (app.Description.Length > 0 ? "<meta name=\"description\" content=\"" + Esc(app.Description) + "\">\n" : "") +
                (app.Favicon.Length > 0 ? "<link rel=\"icon\" href=\"" + Esc(app.Favicon) + "\">\n" : "") +
                (app.Tokens.Count > 0 ? "<style>" + TokenCss(app) + "</style>\n" : "") +
                string.Concat(app.Css.Where(s => s.Trim().Length > 0).Select(s => "<style>" + s + "</style>\n")) +
                "</head>\n<body><div id=\"root\"></div><script type=\"module\" src=\"/src/main.jsx\"></script></body></html>\n",
            ["src/main.jsx"] = "import { StrictMode } from 'react';\nimport { createRoot } from 'react-dom/client';\nimport " + comp + " from './" + comp + ".jsx';\n\n" +
                "createRoot(document.getElementById('root')).render(<StrictMode><" + comp + " /></StrictMode>);\n",
            ["src/" + comp + ".jsx"] = React(app),
        };
    }

    // ---------------- shared helpers ----------------
    private static string JsLiteral(WebState s) => s.Kind switch
    {
        "string" => "\"" + Esc(s.Init).Replace("\"", "\\\"") + "\"",
        "bool" => s.Init.Trim().ToLowerInvariant() == "true" ? "true" : "false",
        _ => double.TryParse(s.Init, NumberStyles.Any, CultureInfo.InvariantCulture, out _) ? s.Init.Trim() : "0",
    };

    private static string SetLiteral(string arg) =>
        double.TryParse(arg, NumberStyles.Any, CultureInfo.InvariantCulture, out _) ? arg
        : arg == "true" || arg == "false" ? arg
        : "\"" + arg.Replace("\"", "\\\"") + "\"";

    // design tokens -> ":root { --name: value; ... }"
    private static string TokenCss(WebApp app) =>
        ":root { " + string.Join(" ", app.Tokens.Where(t => t.Name.Length > 0).Select(t => "--" + t.Name + ": " + t.Value + ";")) + " }";

    // "padding:12px; display:flex; color:var(--brand)" -> "padding: '12px', display: 'flex', color: 'var(--brand)'"
    private static string CssToReactStyle(string css)
    {
        var parts = new List<string>();
        foreach (var decl in css.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int i = decl.IndexOf(':');
            if (i <= 0) continue;
            string k = decl[..i].Trim(), v = decl[(i + 1)..].Trim();
            // CSS custom properties (--x) stay quoted-string keys; normal props become camelCase
            string key = k.StartsWith("--") ? "'" + k + "'" : CamelCss(k);
            parts.Add(key + ": '" + v.Replace("'", "\\'") + "'");
        }
        return string.Join(", ", parts);
    }

    private static string CamelCss(string k)
    {
        var sb = new StringBuilder();
        bool up = false;
        foreach (var ch in k) { if (ch == '-') up = true; else { sb.Append(up ? char.ToUpperInvariant(ch) : ch); up = false; } }
        return sb.ToString();
    }

    private static string Esc(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    private static string JsxText(string s) => (s ?? "").Replace("{", "{'{'}").Replace("}", "{'}'}");
    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    private static string Ident(string s) { var c = new string((s ?? "App").Where(char.IsLetterOrDigit).ToArray()); return c.Length == 0 ? "App" : (char.IsDigit(c[0]) ? "App" + c : Cap(c)); }
    private static string Slug(string s) { var c = new string((s ?? "app").ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-'); return c.Length == 0 ? "app" : c; }
}
