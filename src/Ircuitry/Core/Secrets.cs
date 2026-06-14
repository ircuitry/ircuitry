using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ircuitry.Core;

/// <summary>
/// A separate credential store (~/ircuitry/secrets.json) referenced as <c>{{secret.NAME}}</c> in any node
/// param or connection field. Crucially it is NOT part of workspace.ircuitry, .ircbot exports, or clipboard
/// copies - so sharing a flow never leaks keys. Values are resolved at run time.
/// </summary>
public static class Secrets
{
    private static readonly object Gate = new();
    private static Dictionary<string, string>? _cache;
    private static bool _testing;       // when set, the cache is in-memory only - never touch disk
    private static DateTime _stamp;     // file mtime the cache was built from (for change detection)
    private static readonly Regex Ref = new(@"\{\{\s*secret\.([^}\s]+)\s*\}\}", RegexOptions.Compiled);

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ircuitry");
    public static string FilePath => Path.Combine(Dir, "secrets.json");

    // case-insensitive: {{secret.Pollinations}} must still find a secret named "pollinations".
    // A name-case mismatch should never silently resolve to an empty (→ "unauthorized") key.
    private static Dictionary<string, string> Empty() => new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> Load()
    {
        if (_testing) return _cache ??= Empty();
        try
        {
            var fi = new FileInfo(FilePath);
            // serve the cache unless the file changed underneath us (external edit / another process)
            if (_cache != null && (fi.Exists ? fi.LastWriteTimeUtc == _stamp : _stamp == default)) return _cache;
            var raw = fi.Exists ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) : null;
            _cache = new Dictionary<string, string>(raw ?? new(), StringComparer.OrdinalIgnoreCase);
            _stamp = fi.Exists ? fi.LastWriteTimeUtc : default;
        }
        catch { _cache ??= Empty(); }
        return _cache;
    }

    private static void Save(Dictionary<string, string> d)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
        try { _stamp = new FileInfo(FilePath).LastWriteTimeUtc; } catch { }   // keep the cache in sync with what we just wrote
    }

    public static string Get(string name) { lock (Gate) return Load().TryGetValue(name, out var v) ? v : ""; }
    public static void Set(string name, string value) { lock (Gate) { var d = Load(); d[name] = value; Save(d); } }
    public static void Delete(string name) { lock (Gate) { var d = Load(); if (d.Remove(name)) Save(d); } }
    public static List<string> Names() { lock (Gate) { var k = new List<string>(Load().Keys); k.Sort(StringComparer.OrdinalIgnoreCase); return k; } }
    public static bool Has(string name) { lock (Gate) return Load().ContainsKey(name); }

    /// <summary>Replace every <c>{{secret.NAME}}</c> with its value (missing → empty). Tolerates inner
    /// whitespace (<c>{{ secret.x }}</c>) and any name case.</summary>
    public static string Expand(string s)
    {
        // cheap guard on the double-brace marker only - whitespace variants like "{{ secret.x }}"
        // must still reach the (whitespace-tolerant) regex below.
        if (string.IsNullOrEmpty(s) || s.IndexOf("{{", StringComparison.Ordinal) < 0) return s;
        lock (Gate)
        {
            var d = Load();
            return Ref.Replace(s, m => d.TryGetValue(m.Groups[1].Value, out var v) ? v : "");
        }
    }

    /// <summary>True if the text references a secret (used to keep them out of streamed/displayed data).</summary>
    public static bool References(string s) => !string.IsNullOrEmpty(s) && Ref.IsMatch(s);

    /// <summary>Names referenced by the text that have no matching secret - for surfacing a clear
    /// "secret 'X' is not defined" error instead of a silent empty value.</summary>
    public static List<string> Missing(string s)
    {
        var miss = new List<string>();
        if (string.IsNullOrEmpty(s) || s.IndexOf("{{", StringComparison.Ordinal) < 0) return miss;
        lock (Gate)
        {
            var d = Load();
            foreach (Match m in Ref.Matches(s))
            {
                var name = m.Groups[1].Value;
                if (!d.ContainsKey(name) && !miss.Contains(name)) miss.Add(name);
            }
        }
        return miss;
    }

    // test hook - pin an in-memory store and stop the loader from ever touching disk
    internal static void UseForTesting(Dictionary<string, string> d) { lock (Gate) { _testing = true; _cache = new Dictionary<string, string>(d, StringComparer.OrdinalIgnoreCase); } }
}
