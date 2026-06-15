using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Ircuitry.Core;
using Ircuitry.Net;

namespace Ircuitry.Graph;

/// <summary>
/// Loads a community/custom node from a <c>.ircnode</c> manifest: a JSON file declaring the node's
/// pins, params and a JS/Python script. The script runs through the same <see cref="CodeRunner"/> as the
/// Code node - inputs/params arrive as UPPERCASE env vars (and the first data input as INPUT); whatever it
/// prints becomes the node's output (a JSON object maps to named data outputs, otherwise raw stdout fills
/// the first data output). Drop one onto the editor to install it; no recompile needed.
/// </summary>
public static class CustomNode
{
    public static NodeDef? Load(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string typeId = Str(r, "typeId");
        if (typeId.Length == 0) return null;

        var prms = ParseParams(r, "params");
        var def = new NodeDef
        {
            TypeId = typeId,
            Title = Str(r, "title", typeId),
            Subtitle = Str(r, "subtitle", "community"),
            Icon = Str(r, "icon", "🧩"),
            IconImage = r.TryGetProperty("iconImage", out var ii) && ii.ValueKind == JsonValueKind.String && ii.GetString() is { Length: > 0 } b64 ? b64 : null,
            Category = ParseCategory(Str(r, "category", "Action")),
            Description = Str(r, "description", ""),
            Inputs = ParsePins(r, "inputs"),
            Outputs = ParsePins(r, "outputs"),
            Params = prms,
            SummaryParam = prms.Length > 0 ? prms[0].Key : null,
        };

        // a subflow node carries a saved subgraph; a script node carries code
        if (r.TryGetProperty("subgraph", out var sg) && sg.ValueKind == JsonValueKind.Object)
        {
            string subJson = sg.GetRawText();   // deserialized lazily on first run, once the catalog is fully built
            NodeGraph? sub = null;
            def.Exec = c => { sub ??= GraphSerializer.Load(subJson).graph; RunSubflow(c, sub); };
        }
        else
        {
            string lang = Str(r, "language", "python");
            string code = Str(r, "code");
            int timeout = r.TryGetProperty("timeout", out var t)
                ? (t.ValueKind == JsonValueKind.Number ? t.GetInt32() : int.TryParse(t.GetString(), out var ti) ? ti : 5)
                : 5;
            def.Exec = c => Run(c, lang, code, timeout);
        }
        return def;
    }

    // subflow node: feed the node's data inputs in by name, run the subgraph, read named outputs back out
    private static void RunSubflow(INodeContext c, NodeGraph sub)
    {
        var d = c.Node.Def;
        var inputs = new Dictionary<string, string>();
        for (int i = 0; i < d.Inputs.Length; i++)
            if (!d.Inputs[i].Kind.IsExec()) inputs[d.Inputs[i].Name] = c.In(i);
        var outv = c.RunSubflow(sub, inputs);
        for (int i = 0; i < d.Outputs.Length; i++)
            if (!d.Outputs[i].Kind.IsExec()) c.SetOut(i, outv.TryGetValue(d.Outputs[i].Name, out var v) ? v : "");
        for (int i = 0; i < d.Outputs.Length; i++)
            if (d.Outputs[i].Kind.IsExec()) { c.Pulse(i); break; }
    }

    private static void Run(INodeContext c, string lang, string code, int timeout)
    {
        var def = c.Node.Def;
        var ctx = new Dictionary<string, string>
        {
            ["nick"] = c.Var("nick"), ["user"] = c.Var("user"), ["channel"] = c.Var("channel"),
            ["message"] = c.Var("message"), ["args"] = c.Var("args"), ["command"] = c.Var("command"),
            ["botnick"] = c.Var("botnick"),
        };
        string? firstIn = null;
        for (int i = 0; i < def.Inputs.Length; i++)
            if (!def.Inputs[i].Kind.IsExec())
            {
                var v = c.In(i);
                // when this node is invoked as an AI tool, its unwired inputs are filled from the model's args
                if (v.Length == 0 && def.Inputs[i].Name.Length > 0)
                {
                    var a = c.Var("__arg." + def.Inputs[i].Name);
                    if (a.Length > 0) v = a;
                }
                if (def.Inputs[i].Name.Length > 0) ctx[def.Inputs[i].Name] = v;
                firstIn ??= v;
            }
        if (firstIn != null) ctx["input"] = firstIn;
        foreach (var p in def.Params) ctx[p.Key] = c.Param(p.Key);

        var (output, err) = CodeRunner.Run(lang, code, ctx, timeout);
        if (!string.IsNullOrEmpty(err)) c.Log(def.Title + " error: " + err, LogLevel.Error);

        // a JSON object maps to named outputs; anything else fills the first data output
        var obj = TryObject(output);
        bool rawUsed = false;
        for (int i = 0; i < def.Outputs.Length; i++)
        {
            var o = def.Outputs[i];
            if (o.Kind.IsExec() || o.Kind == PinKind.Tool) continue;   // a Tool output is the AI-tool handle, not a data sink
            string val = "";
            if (obj != null) obj.TryGetValue(o.Name, out val!);
            else if (!rawUsed) { val = output; rawUsed = true; }
            c.SetOut(i, val ?? "");
        }
        // pulse the first exec output (branching custom nodes can come later via a {"__pulse":"name"} convention)
        for (int i = 0; i < def.Outputs.Length; i++)
            if (def.Outputs[i].Kind.IsExec()) { c.Pulse(i); break; }
    }

    // ---- manifest parsing ----
    private static PinDef[] ParsePins(JsonElement r, string key)
    {
        if (!r.TryGetProperty(key, out var a) || a.ValueKind != JsonValueKind.Array) return Array.Empty<PinDef>();
        var list = new List<PinDef>();
        foreach (var e in a.EnumerateArray())
            list.Add(new PinDef(Str(e, "name"), ParseKind(Str(e, "kind", "Text")),
                e.TryGetProperty("multi", out var m) && m.ValueKind == JsonValueKind.True));
        return list.ToArray();
    }

    private static ParamDef[] ParseParams(JsonElement r, string key)
    {
        if (!r.TryGetProperty(key, out var a) || a.ValueKind != JsonValueKind.Array) return Array.Empty<ParamDef>();
        var list = new List<ParamDef>();
        foreach (var e in a.EnumerateArray())
        {
            var p = new ParamDef
            {
                Key = Str(e, "key"), Label = Str(e, "label", Str(e, "key")),
                Type = ParseType(Str(e, "type", "Text")), Default = Str(e, "default"), Placeholder = Str(e, "placeholder"),
            };
            if (e.TryGetProperty("choices", out var ch) && ch.ValueKind == JsonValueKind.Array)
                p.Choices = ch.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            if (p.Key.Length > 0) list.Add(p);
        }
        return list.ToArray();
    }

    private static Dictionary<string, string>? TryObject(string s)
    {
        s = s.Trim();
        if (s.Length == 0 || s[0] != '{') return null;
        try
        {
            using var d = JsonDocument.Parse(s);
            if (d.RootElement.ValueKind != JsonValueKind.Object) return null;
            var map = new Dictionary<string, string>();
            foreach (var p in d.RootElement.EnumerateObject())
                map[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString();
            return map;
        }
        catch { return null; }
    }

    private static PinKind ParseKind(string s) => s.Trim().ToLowerInvariant() switch
    {
        "exec" => PinKind.Exec,
        "user" => PinKind.User,
        "channel" => PinKind.Channel,
        "number" => PinKind.Number,
        "bool" => PinKind.Bool,
        "tool" => PinKind.Tool,
        _ => PinKind.Text,
    };

    private static ParamType ParseType(string s) => s.Trim().ToLowerInvariant() switch
    {
        "multiline" => ParamType.Multiline,
        "int" => ParamType.Int,
        "bool" => ParamType.Bool,
        "choice" => ParamType.Choice,
        _ => ParamType.Text,
    };

    private static NodeCategory ParseCategory(string s) =>
        Enum.TryParse<NodeCategory>(s.Trim(), true, out var cat) ? cat : NodeCategory.Action;

    private static string Str(JsonElement e, string key, string def = "")
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? def : def;
}
