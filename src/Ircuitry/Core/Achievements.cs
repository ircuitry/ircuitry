using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Ircuitry.Core;

/// <summary>One achievement definition (with its current unlocked status filled in for display).</summary>
public sealed class AchDef
{
    public string Id = "", Category = "", Title = "", Desc = "", Icon = "🏆";
    public bool Unlocked;
    public string Detail = "";       // e.g. progress, or which bot earned it
    public float Progress;           // 0..1 toward the next/this tier (for locked ones)
}

/// <summary>
/// Stackable gamification: bot-count milestones, per-bot uptime tiers, and IRCv3 spec compliance (use every
/// node in a spec on one bot). State persists to ~/ircuitry/achievements.json. The evaluator is fed the
/// current bots' node-type sets so it has no dependency on the graph layer.
/// </summary>
public static class Achievements
{
    private sealed class Store
    {
        public int BotsCreated { get; set; }
        public Dictionary<string, double> Online { get; set; } = new();      // bot name -> accumulated online seconds
        public Dictionary<string, string> Unlocked { get; set; } = new();    // id -> "earned" detail
    }

    private static readonly object Gate = new();
    private static Store? _s;
    private static bool _dirty;

    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ircuitry");
    private static string FilePath => Path.Combine(Dir, "achievements.json");

    // ---- definitions ----
    public static readonly int[] BotTiers = { 1, 10, 100, 1000 };
    public static readonly long[] OnlineTiers = { 1, 10, 100, 1000, 10000, 100000, 1000000, 10000000 };  // hours
    public static readonly (string key, string title, string icon, string[] nodes)[] Specs =
    {
        ("tags", "Tag Whisperer", "🔖", new[] { "data.gettag", "filter.fromAccount", "filter.isBot" }),
        ("react", "Threadsmith", "🧵", new[] { "action.react", "action.replythread" }),
        ("typing", "Now Typing…", "✍️", new[] { "irc.typing.start", "irc.typing.stop" }),
        ("raw", "Down to the Metal", "📡", new[] { "irc.raw" }),
    };

    private static Store S()
    {
        lock (Gate)
        {
            if (_s != null) return _s;
            try { _s = File.Exists(FilePath) ? JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath)) ?? new() : new(); }
            catch { _s = new(); }
            return _s;
        }
    }

    public static int BotsCreated => S().BotsCreated;
    public static bool IsUnlocked(string id) => S().Unlocked.ContainsKey(id);
    public static double OnlineSeconds(string bot) => S().Online.TryGetValue(bot, out var v) ? v : 0;
    public static double MaxOnlineSeconds() { var o = S().Online; return o.Count == 0 ? 0 : o.Values.Max(); }

    public static void BotCreated() { lock (Gate) { S().BotsCreated++; _dirty = true; } }

    public static void AddOnline(string bot, double secs)
    {
        if (secs <= 0 || string.IsNullOrEmpty(bot)) return;
        lock (Gate) { var s = S(); s.Online.TryGetValue(bot, out var v); s.Online[bot] = v + secs; _dirty = true; }
    }

    private static bool Unlock(string id, string detail)
    {
        lock (Gate) { var s = S(); if (s.Unlocked.ContainsKey(id)) return false; s.Unlocked[id] = detail; _dirty = true; return true; }
    }

    /// <summary>Check every achievement against current state + bots; returns the ones newly unlocked.</summary>
    public static List<AchDef> Evaluate(IReadOnlyList<(string name, IReadOnlyCollection<string> types)> bots)
    {
        var fresh = new List<AchDef>();
        foreach (var d in AllDefs(bots))
        {
            if (d.Unlocked && Unlock(d.Id, d.Detail)) fresh.Add(d);   // Unlock returns true only the first time
        }
        return fresh;
    }

    /// <summary>All achievement definitions with unlocked status + progress, for the trophy case.</summary>
    public static List<AchDef> AllDefs(IReadOnlyList<(string name, IReadOnlyCollection<string> types)>? bots = null)
    {
        var list = new List<AchDef>();
        int bc = BotsCreated;
        foreach (var n in BotTiers)
            list.Add(Mk("bots." + n, "Bots", n == 1 ? "First bot" : $"{n} bots", $"Create {n} bot{(n == 1 ? "" : "s")}.", "🤖",
                bc >= n || IsUnlocked("bots." + n), $"{Math.Min(bc, n)}/{n}", n == 0 ? 1 : Math.Min(1f, bc / (float)n)));

        double maxSecs = MaxOnlineSeconds();
        foreach (var h in OnlineTiers)
        {
            double need = h * 3600.0;
            list.Add(Mk("online." + h, "Uptime", Hours(h) + " online", $"Keep one bot connected for {Hours(h)}.", "⏱️",
                maxSecs >= need || IsUnlocked("online." + h), FormatDur(maxSecs) + " best", (float)Math.Min(1.0, maxSecs / need)));
        }

        foreach (var sp in Specs)
        {
            bool met = IsUnlocked("spec." + sp.key);
            int best = 0;
            if (bots != null)
                foreach (var b in bots)
                {
                    int have = sp.nodes.Count(t => b.types.Contains(t));
                    best = Math.Max(best, have);
                    if (have == sp.nodes.Length) met = true;
                }
            list.Add(Mk("spec." + sp.key, "IRCv3 specs", sp.title, "Use every node of the " + sp.key + " spec on one bot: " + string.Join(", ", sp.nodes) + ".", sp.icon,
                met, $"{(met ? sp.nodes.Length : best)}/{sp.nodes.Length} nodes", sp.nodes.Length == 0 ? 1 : Math.Min(1f, best / (float)sp.nodes.Length)));
        }
        return list;
    }

    private static AchDef Mk(string id, string cat, string title, string desc, string icon, bool unlocked, string detail, float prog)
        => new() { Id = id, Category = cat, Title = title, Desc = desc, Icon = icon, Unlocked = unlocked, Detail = detail, Progress = prog };

    public static int UnlockedCount => S().Unlocked.Count;
    public static int TotalCount => BotTiers.Length + OnlineTiers.Length + Specs.Length;

    public static void Save()
    {
        lock (Gate)
        {
            if (!_dirty || _s == null) return;
            try { Directory.CreateDirectory(Dir); File.WriteAllText(FilePath, JsonSerializer.Serialize(_s, new JsonSerializerOptions { WriteIndented = true })); _dirty = false; }
            catch { /* best effort */ }
        }
    }

    private static string Hours(long h) => h >= 1_000_000 ? (h / 1_000_000) + "M h" : h >= 1000 ? (h / 1000) + "k h" : h + " h";

    public static string FormatDur(double secs)
    {
        if (secs < 3600) return $"{(int)(secs / 60)}m";
        double h = secs / 3600.0;
        if (h < 1000) return $"{h:0.#}h";
        if (h < 1_000_000) return $"{h / 1000:0.#}k h";
        return $"{h / 1_000_000:0.#}M h";
    }
}
