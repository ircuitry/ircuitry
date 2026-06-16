using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Ircuitry.Core;

/// <summary>
/// Encoding for <c>ParamType.List</c> params: a growable list of rows stored as a JSON array in a single
/// param string. Pair rows are 2-element arrays (key + value); single rows are plain strings. Shared by the
/// inspector (editing) and node behaviour (reading), so a node can offer "Add another" instead of a fixed
/// handful of numbered fields.
/// </summary>
public static class ParamList
{
    public static List<string[]> Parse(string s)
    {
        var rows = new List<string[]>();
        if (string.IsNullOrWhiteSpace(s)) return rows;
        try
        {
            using var d = JsonDocument.Parse(s);
            if (d.RootElement.ValueKind != JsonValueKind.Array) return rows;
            foreach (var e in d.RootElement.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.Array)
                    rows.Add(e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()).ToArray());
                else if (e.ValueKind == JsonValueKind.String)
                    rows.Add(new[] { e.GetString() ?? "" });
            }
        }
        catch { /* malformed - treat as empty */ }
        return rows;
    }

    public static string Encode(List<string[]> rows, bool pair)
    {
        // Keep the rows the user added even while they're still blank - otherwise "Add another" drops the new
        // empty row before you can type into it (the old bug). Only a single lone all-blank row collapses to
        // "" so an untouched list stays at its empty default (no spurious dirty/autosave). Readers skip blanks.
        if (rows.Count <= 1 && rows.All(r => r.All(string.IsNullOrEmpty))) return "";
        return pair
            ? JsonSerializer.Serialize(rows.Select(r => new[] { At(r, 0), At(r, 1) }))
            : JsonSerializer.Serialize(rows.Select(r => At(r, 0)));
    }

    /// <summary>Read a list param as (key, value) pairs - skips fully-blank rows (the still-being-typed ones).</summary>
    public static IEnumerable<(string key, string val)> Pairs(string s) =>
        Parse(s).Select(r => (At(r, 0), At(r, 1))).Where(p => p.Item1.Length > 0 || p.Item2.Length > 0);

    /// <summary>Read a list param as plain values - skips blank rows.</summary>
    public static IEnumerable<string> Values(string s) =>
        Parse(s).Select(r => At(r, 0)).Where(v => v.Length > 0);

    private static string At(string[] r, int i) => i < r.Length ? r[i] ?? "" : "";
}
