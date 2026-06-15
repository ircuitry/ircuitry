using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Ircuitry.Graph;

namespace Ircuitry.App;

/// <summary>
/// Headless validator for a community node (.ircnode): structural checks (typeId, category, pin kinds, a
/// script or subgraph) plus a real <see cref="CustomNode.Load"/> so a generated/contributed node is verified
/// by the same loader the app uses. Invoked via `--validate-node &lt;file&gt;`.
/// </summary>
public static class NodeValidator
{
    private static readonly HashSet<string> Categories = new(StringComparer.OrdinalIgnoreCase)
        { "Event", "Filter", "Logic", "Action", "Data", "Ai", "Storage", "Ircv3" };
    private static readonly HashSet<string> Kinds = new()
        { "Exec", "Text", "User", "Channel", "Number", "Bool", "Tool" };

    public static int Run(string path)
    {
        if (!File.Exists(path)) { Console.WriteLine($"FAIL {path}: file not found"); return 1; }
        var errors = new List<string>();
        string raw = File.ReadAllText(path);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw); }
        catch (Exception ex) { Console.WriteLine($"FAIL {Path.GetFileName(path)}: invalid JSON - {ex.Message}"); return 1; }
        var r = doc.RootElement;

        string Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        string tid = Str("typeId");
        if (tid.Length == 0) errors.Add("missing non-empty 'typeId'");
        if (Str("title").Length == 0) errors.Add("missing 'title'");
        string cat = r.TryGetProperty("category", out var ce) && ce.ValueKind == JsonValueKind.String ? ce.GetString() ?? "Action" : "Action";
        if (!Categories.Contains(cat)) errors.Add($"category '{cat}' not in Event/Filter/Logic/Action/Data/Ai/Storage/Ircv3");

        void CheckPins(string key)
        {
            if (!r.TryGetProperty(key, out var a)) return;   // optional
            if (a.ValueKind != JsonValueKind.Array) { errors.Add($"'{key}' must be an array"); return; }
            foreach (var p in a.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("name", out _)) { errors.Add($"each {key} pin needs a 'name'"); continue; }
                string kind = p.TryGetProperty("kind", out var ke) ? ke.GetString() ?? "Text" : "Text";
                if (!Kinds.Contains(kind)) errors.Add($"{key} pin kind '{kind}' not in Exec/Text/User/Channel/Number/Bool/Tool");
            }
        }
        CheckPins("inputs"); CheckPins("outputs");

        bool hasCode = r.TryGetProperty("code", out var cc) && cc.ValueKind == JsonValueKind.String && (cc.GetString() ?? "").Trim().Length > 0;
        bool hasSub = r.TryGetProperty("subgraph", out var se) && se.ValueKind == JsonValueKind.Object;
        if (!hasCode && !hasSub) errors.Add("must define either 'code' (a script) or 'subgraph'");
        if (hasCode)
        {
            string lang = Str("language"); if (lang.Length == 0) lang = "python";
            if (lang is not ("python" or "js" or "javascript" or "node")) errors.Add($"language '{lang}' must be python or js");
        }

        // the real loader is the final word
        NodeDef? def = null;
        try { def = CustomNode.Load(raw); } catch (Exception ex) { errors.Add("CustomNode.Load threw: " + ex.Message); }
        if (def == null && errors.Count == 0) errors.Add("CustomNode.Load returned null (unloadable)");

        if (errors.Count == 0)
        {
            Console.WriteLine($"OK {Path.GetFileName(path)}: {tid} [{cat}] {def!.Inputs.Length}in/{def.Outputs.Length}out");
            return 0;
        }
        Console.WriteLine($"FAIL {Path.GetFileName(path)}:");
        foreach (var e in errors) Console.WriteLine("  - " + e);
        return 1;
    }
}
