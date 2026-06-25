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
        var body = new StringBuilder();
        RenderHtml(app.Root, body, 4);

        // initial state object, e.g. { count: 0, name: "" }
        string init = "{ " + string.Join(", ", app.States.Select(s => s.Name + ": " + JsLiteral(s))) + " }";

        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.AppendLine("<title>" + Esc(app.Name) + "</title></head>");
        html.AppendLine("<body>");
        html.Append(body);
        html.AppendLine("<script>");
        html.AppendLine("(function(){");
        html.AppendLine("  var s = " + init + ";");
        html.AppendLine("  function render(){ document.querySelectorAll('[data-b]').forEach(function(el){ el.textContent = s[el.getAttribute('data-b')]; }); }");
        html.AppendLine("  function act(spec){ var p = spec.split(':'), n = p[0], op = p[1], arg = p[2];");
        html.AppendLine("    if (op === 'inc') s[n] = (+s[n]) + (arg ? +arg : 1);");
        html.AppendLine("    else if (op === 'dec') s[n] = (+s[n]) - (arg ? +arg : 1);");
        html.AppendLine("    else if (op === 'set') s[n] = (arg !== '' && !isNaN(+arg)) ? +arg : arg;");
        html.AppendLine("    else if (op === 'toggle') s[n] = !s[n];");
        html.AppendLine("    render(); }");
        html.AppendLine("  ['click','input','change'].forEach(function(ev){ document.addEventListener(ev, function(e){");
        html.AppendLine("    var t = e.target.closest('[data-on-' + ev + ']'); if (t) act(t.getAttribute('data-on-' + ev)); }); });");
        html.AppendLine("  render();");
        html.AppendLine("})();");
        html.AppendLine("</script>");
        html.AppendLine("</body></html>");
        return html.ToString();
    }

    private static void RenderHtml(WebEl el, StringBuilder sb, int indent)
    {
        string pad = new string(' ', indent);
        var attrs = new StringBuilder();
        foreach (var kv in el.Attrs) attrs.Append(' ').Append(kv.Key).Append("=\"").Append(Esc(kv.Value)).Append('"');
        if (el.Bind != null) attrs.Append(" data-b=\"").Append(Esc(el.Bind)).Append('"');
        foreach (var kv in el.On) attrs.Append(" data-on-").Append(kv.Key).Append("=\"").Append(Esc(ActionSpec(kv.Value))).Append('"');

        bool leaf = el.Children.Count == 0;
        sb.Append(pad).Append('<').Append(el.Tag).Append(attrs).Append('>');
        if (leaf)
        {
            if (el.Bind != null) sb.Append(Esc(el.Bind));                 // initial = state name's value, filled by render()
            else if (el.Text != null) sb.Append(Esc(el.Text));
            sb.Append("</").Append(el.Tag).Append(">\n");
        }
        else
        {
            sb.Append('\n');
            foreach (var c in el.Children) RenderHtml(c, sb, indent + 2);
            sb.Append(pad).Append("</").Append(el.Tag).Append(">\n");
        }
    }

    private static string ActionSpec(WebAction a) => a.State + ":" + a.Op + ":" + a.Arg;

    // ---------------- React (eject): the App component (+ a Vite project around it) ----------------
    public static string React(WebApp app)
    {
        var sb = new StringBuilder();
        sb.AppendLine("import { useState } from 'react';");
        sb.AppendLine();
        sb.AppendLine("export default function " + Ident(app.Name) + "() {");
        foreach (var s in app.States)
            sb.AppendLine("  const [" + s.Name + ", set" + Cap(s.Name) + "] = useState(" + JsLiteral(s) + ");");
        sb.AppendLine("  return (");
        var jsx = new StringBuilder();
        RenderJsx(app.Root, jsx, 4);
        sb.Append(jsx);
        sb.AppendLine("  );");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void RenderJsx(WebEl el, StringBuilder sb, int indent)
    {
        string pad = new string(' ', indent);
        var attrs = new StringBuilder();
        foreach (var kv in el.Attrs)
        {
            string name = kv.Key == "class" ? "className" : kv.Key == "for" ? "htmlFor" : kv.Key;
            attrs.Append(' ').Append(name).Append("=\"").Append(Esc(kv.Value)).Append('"');
        }
        foreach (var kv in el.On) attrs.Append(' ').Append(JsxEvent(kv.Key)).Append("={").Append(ReactHandler(kv.Value)).Append('}');

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
            foreach (var c in el.Children) RenderJsx(c, sb, indent + 2);
            sb.Append(pad).Append("</").Append(el.Tag).Append(">\n");
        }
    }

    private static string JsxEvent(string dom) => dom switch
    {
        "click" => "onClick", "input" => "onInput", "change" => "onChange", "submit" => "onSubmit",
        _ => "on" + Cap(dom),
    };

    private static string ReactHandler(WebAction a)
    {
        string set = "set" + Cap(a.State);
        return a.Op switch
        {
            "inc" => "() => " + set + "(v => v + " + (a.Arg.Length > 0 ? a.Arg : "1") + ")",
            "dec" => "() => " + set + "(v => v - " + (a.Arg.Length > 0 ? a.Arg : "1") + ")",
            "toggle" => "() => " + set + "(v => !v)",
            "set" => "() => " + set + "(" + SetLiteral(a.Arg) + ")",
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
            ["index.html"] = "<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">\n" +
                "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<title>" + Esc(app.Name) + "</title></head>\n" +
                "<body><div id=\"root\"></div><script type=\"module\" src=\"/src/main.jsx\"></script></body></html>\n",
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

    private static string Esc(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    private static string JsxText(string s) => (s ?? "").Replace("{", "{'{'}").Replace("}", "{'}'}");
    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    private static string Ident(string s) { var c = new string((s ?? "App").Where(char.IsLetterOrDigit).ToArray()); return c.Length == 0 ? "App" : (char.IsDigit(c[0]) ? "App" + c : Cap(c)); }
    private static string Slug(string s) { var c = new string((s ?? "app").ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-'); return c.Length == 0 ? "app" : c; }
}
