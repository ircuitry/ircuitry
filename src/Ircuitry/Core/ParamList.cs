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
        var clean = rows.Where(r => r.Any(c => !string.IsNullOrEmpty(c))).ToList();   // drop blank rows on save
        return pair
            ? JsonSerializer.Serialize(clean.Select(r => new[] { At(r, 0), At(r, 1) }))
            : JsonSerializer.Serialize(clean.Select(r => At(r, 0)));
    }

    /// <summary>Read a list param as (key, value) pairs (value empty for single-field lists).</summary>
    public static IEnumerable<(string key, string val)> Pairs(string s) =>
        Parse(s).Select(r => (At(r, 0), At(r, 1)));

    /// <summary>Read a list param as plain values (first field of each row).</summary>
    public static IEnumerable<string> Values(string s) => Parse(s).Select(r => At(r, 0));

    private static string At(string[] r, int i) => i < r.Length ? r[i] ?? "" : "";
}
