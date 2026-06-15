using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Ircuitry.Core;

/// <summary>
/// Per-user node-library preferences (~/ircuitry/nodeprefs.json): pinned favourites and a short
/// most-recently-used list, so the palette can surface the nodes a user actually reaches for.
/// </summary>
public static class NodePrefs
{
    private sealed class Store
    {
        public List<string> favorites { get; set; } = new();
        public List<string> recents { get; set; } = new();
    }

    private const int MaxRecents = 8;
    private static readonly object Gate = new();
    private static Store? _cache;

    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ircuitry");
    private static string FilePath => Path.Combine(Dir, "nodeprefs.json");

    private static Store Load()
    {
        if (_cache != null) return _cache;
        lock (Gate)
        {
            if (_cache != null) return _cache;
            try { _cache = File.Exists(FilePath) ? JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath)) ?? new() : new(); }
            catch { _cache = new(); }
            return _cache;
        }
    }

    private static void Save()
    {
        lock (Gate)
        {
            try { Directory.CreateDirectory(Dir); File.WriteAllText(FilePath, JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true })); }
            catch { /* disk unavailable - best effort */ }
        }
    }

    public static IReadOnlyList<string> Favorites => Load().favorites;
    public static IReadOnlyList<string> Recents => Load().recents;

    public static bool IsFavorite(string typeId) => Load().favorites.Contains(typeId);

    public static void ToggleFavorite(string typeId)
    {
        var s = Load();
        if (!s.favorites.Remove(typeId)) s.favorites.Add(typeId);
        Save();
    }

    /// <summary>Record that a node of this type was just added, moving it to the front of the recents.</summary>
    public static void RecordUse(string typeId)
    {
        var s = Load();
        s.recents.RemoveAll(t => t == typeId);
        s.recents.Insert(0, typeId);
        while (s.recents.Count > MaxRecents) s.recents.RemoveAt(s.recents.Count - 1);
        Save();
    }
}
