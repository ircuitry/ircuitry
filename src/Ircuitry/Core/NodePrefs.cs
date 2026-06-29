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
        public bool devMode { get; set; } = false;
        public bool welcomed { get; set; } = false;
    }

    private const int MaxRecents = 8;
    private static readonly object Gate = new();
    private static Store? _cache;

    // Honour IRCUITRY_HOME (sandboxed/alternate/test workspaces) just like AppModel.WorkspaceDir, so
    // per-user prefs (favourites, dev mode, first-run welcome) live alongside the workspace they belong to.
    private static string Dir
    {
        get
        {
            var ov = Environment.GetEnvironmentVariable("IRCUITRY_HOME");
            return !string.IsNullOrEmpty(ov) ? ov : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ircuitry");
        }
    }
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

    /// <summary>
    /// Developer mode: off by default. When off, advanced/developer node categories (App / Plugins, UI,
    /// Code &amp; Dev) and the plugin-authoring tools (Bake a node, Plugins manager, Bundle as plugin) are
    /// hidden from discovery so the app reads as a friendly visual studio. Existing graphs using those
    /// nodes still load and run - we only gate where they're surfaced.
    /// </summary>
    public static bool DevMode
    {
        get => Load().devMode;
        set { var s = Load(); if (s.devMode == value) return; s.devMode = value; Save(); }
    }

    /// <summary>True when this category should be hidden from the palette / add menus unless dev mode is on.</summary>
    public static bool IsDevCategory(NodeCategory c) =>
        c is NodeCategory.App or NodeCategory.Ui or NodeCategory.Code;

    /// <summary>Set once the newcomer has seen the "what do you want to build?" picker, so it shows only on the very first launch.</summary>
    public static bool Welcomed
    {
        get => Load().welcomed;
        set { var s = Load(); if (s.welcomed == value) return; s.welcomed = value; Save(); }
    }
}
