using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Ircuitry.Graph;

namespace Ircuitry.App;

/// <summary>
/// Headless validator for a community workflow (.ircbot) against the real node catalog: every node type must
/// exist, every connection must reference real nodes with in-range, type-compatible pins, and the flow needs
/// a trigger. Used by `--validate-workflow &lt;file&gt;` so generated/contributed workflows are checked by the
/// same rules the app's importer uses, before they ever ship.
/// </summary>
public static class WorkflowValidator
{
    public static int Run(string path)
    {
        if (!File.Exists(path)) { Console.WriteLine($"FAIL {path}: file not found"); return 1; }
        var errors = new List<string>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(path)); }
        catch (Exception ex) { Console.WriteLine($"FAIL {path}: invalid JSON - {ex.Message}"); return 1; }

        var root = doc.RootElement;
        if (!root.TryGetProperty("name", out var nameEl) || string.IsNullOrWhiteSpace(nameEl.GetString()))
            errors.Add("missing non-empty 'name'");

        // index nodes by id → typeId; flag unknown types and duplicate ids. We also build a live Node per id
        // (with its params) so pin-range checks honour per-instance dynamic pins, e.g. a Switch's case outputs.
        var nodeType = new Dictionary<string, string>();
        var nodeById = new Dictionary<string, Node>();
        int triggers = 0;
        if (!root.TryGetProperty("nodes", out var nodesEl) || nodesEl.ValueKind != JsonValueKind.Array || nodesEl.GetArrayLength() == 0)
            errors.Add("'nodes' must be a non-empty array");
        else
            foreach (var n in nodesEl.EnumerateArray())
            {
                string id = n.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                string type = n.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                if (id.Length == 0 || type.Length == 0) { errors.Add("a node is missing 'id' or 'type'"); continue; }
                if (nodeType.ContainsKey(id)) { errors.Add($"duplicate node id '{id}'"); continue; }
                if (!NodeCatalog.TryGet(type, out var def)) { errors.Add($"node '{id}': unknown type '{type}'"); nodeType[id] = type; continue; }
                nodeType[id] = type;
                var inst = new Node(id, type) { Def = def };
                if (n.TryGetProperty("params", out var pp) && pp.ValueKind == JsonValueKind.Object)
                    foreach (var pr in pp.EnumerateObject())
                        inst.Params[pr.Name] = pr.Value.ValueKind == JsonValueKind.String ? pr.Value.GetString() ?? "" : pr.Value.ToString();
                nodeById[id] = inst;
                if (def.IsTrigger) triggers++;
            }

        if (triggers == 0 && errors.Count == 0) errors.Add("no trigger node (event.*) - nothing would ever run");

        // every connection: real endpoints, in-range pins, compatible kinds
        if (root.TryGetProperty("connections", out var connsEl) && connsEl.ValueKind == JsonValueKind.Array)
            foreach (var c in connsEl.EnumerateArray())
            {
                string from = c.TryGetProperty("from", out var f) ? f.GetString() ?? "" : "";
                string to = c.TryGetProperty("to", out var to2) ? to2.GetString() ?? "" : "";
                int fp = c.TryGetProperty("fromPin", out var fpe) ? fpe.GetInt32() : 0;
                int tp = c.TryGetProperty("toPin", out var tpe) ? tpe.GetInt32() : 0;
                if (!nodeType.TryGetValue(from, out var ft)) { errors.Add($"connection from unknown node '{from}'"); continue; }
                if (!nodeType.TryGetValue(to, out var tt)) { errors.Add($"connection to unknown node '{to}'"); continue; }
                if (!nodeById.TryGetValue(from, out var fromN) || !nodeById.TryGetValue(to, out var toN)) continue;   // unknown type already reported
                var fOut = fromN.Outputs; var tIn = toN.Inputs;   // effective pins (honours dynamic case outputs)
                if (fp < 0 || fp >= fOut.Length) { errors.Add($"{from}({ft}).out[{fp}] out of range (has {fOut.Length})"); continue; }
                if (tp < 0 || tp >= tIn.Length) { errors.Add($"{to}({tt}).in[{tp}] out of range (has {tIn.Length})"); continue; }
                if (!Pins.Compatible(fOut[fp].Kind, tIn[tp].Kind))
                    errors.Add($"incompatible wire {from}.{fOut[fp].Name}({fOut[fp].Kind}) -> {to}.{tIn[tp].Name}({tIn[tp].Kind})");
            }

        if (errors.Count == 0)
        {
            Console.WriteLine($"OK {Path.GetFileName(path)}: {nodeType.Count} nodes, {triggers} trigger(s)");
            return 0;
        }
        Console.WriteLine($"FAIL {Path.GetFileName(path)}:");
        foreach (var e in errors) Console.WriteLine("  - " + e);
        return 1;
    }
}
