using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Ircuitry.Net;

/// <summary>
/// A tiny, dependency-free persistent key/value database: one JSON file per named
/// table under ~/ircuitry/data. Plug-and-play (no engine to install). All access is
/// serialised by a global lock since bots fire on several threads.
/// </summary>
public static class KvStore
{
    private static readonly object Gate = new();

    // Follows the active workspace (IRCUITRY_HOME) exactly like AppModel.WorkspaceDir, so a relocated or
    // throwaway/test workspace gets its own isolated db rather than always hitting ~/ircuitry/data.
    public static string Dir
    {
        get
        {
            var ov = Environment.GetEnvironmentVariable("IRCUITRY_HOME");
            var home = !string.IsNullOrEmpty(ov)
                ? ov
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ircuitry");
            return Path.Combine(home, "data");
        }
    }

    private static string PathFor(string table)
    {
        var name = table.Trim();
        if (name.Length == 0) name = "default";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return Path.Combine(Dir, name + ".json");
    }

    private static Dictionary<string, string> Load(string table)
    {
        try
        {
            var p = PathFor(table);
            if (!File.Exists(p)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(p)) ?? new();
        }
        catch { return new(); }
    }

    private static void Save(string table, Dictionary<string, string> data)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(PathFor(table), JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void Set(string table, string key, string value)
    {
        lock (Gate) { var d = Load(table); d[key] = value; Save(table, d); }
    }

    public static void Delete(string table, string key)
    {
        lock (Gate) { var d = Load(table); if (d.Remove(key)) Save(table, d); }
    }

    public static string Get(string table, string key, string fallback = "")
    {
        lock (Gate) { var d = Load(table); return d.TryGetValue(key, out var v) ? v : fallback; }
    }

    public static int Count(string table)
    {
        lock (Gate) return Load(table).Count;
    }

    /// <summary>All keys in a table (sorted), e.g. for a "list" mode.</summary>
    public static List<string> Keys(string table)
    {
        lock (Gate)
        {
            var keys = new List<string>(Load(table).Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            return keys;
        }
    }

    /// <summary>First key whose value contains the (case-insensitive) needle, with that value.</summary>
    public static (string key, string value)? Find(string table, string needle)
    {
        lock (Gate)
        {
            foreach (var kv in Load(table))
                if (kv.Value.Contains(needle, StringComparison.OrdinalIgnoreCase)) return (kv.Key, kv.Value);
            return null;
        }
    }
}
