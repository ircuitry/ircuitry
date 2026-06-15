using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Ircuitry.Core;

/// <summary>One achievement definition with its current unlocked status filled in for display.</summary>
public sealed class AchDef
{
    public string Id = "", Category = "", Title = "", Desc = "", Icon = "🏆";
    public bool Unlocked;
    public string Detail = "";
    public float Progress;
}

/// <summary>
/// An IRCv3 specification we award an achievement for. Cap specs unlock when the bot negotiates the cap;
/// node specs unlock when the bot's graph uses the spec's node(s). Mirrors the spec list at ircv3.net.
/// </summary>
public sealed class SpecDef
{
    public string Id = "", Group = "", Title = "", Icon = "🌐", Cap = "";
    public string[] Nodes = Array.Empty<string>();
    public bool Draft;
}

/// <summary>
/// Stackable gamification: bot-count milestones, per-bot uptime tiers, and a long list of IRCv3 spec
/// achievements (every ratified + draft spec). State persists to ~/ircuitry/achievements.json.
/// </summary>
public static class Achievements
{
    private sealed class Store
    {
        public int BotsCreated { get; set; }
        public Dictionary<string, double> Online { get; set; } = new();
        public Dictionary<string, string> Unlocked { get; set; } = new();
        public HashSet<string> Caps { get; set; } = new(StringComparer.OrdinalIgnoreCase);   // caps ever negotiated
    }

    private static readonly object Gate = new();
    private static Store? _s;
    private static bool _dirty;

    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ircuitry");
    private static string FilePath => Path.Combine(Dir, "achievements.json");

    public static readonly int[] BotTiers = { 1, 10, 100, 1000 };
    public static readonly long[] OnlineTiers = { 1, 10, 100, 1000, 10000, 100000, 1000000, 10000000 };

    // every IRCv3 spec from ircv3.net: ratified caps, the nodes that exercise a spec, and drafts.
    public static readonly SpecDef[] SpecList = BuildSpecs();

    // node typeIds that exercise a draft spec (for the "draft pioneer" award)
    private static readonly string[] DraftNodes =
        { "action.react", "action.replythread", "irc.typing.start", "irc.typing.stop",
          "action.redact", "action.chathistory", "action.rename", "action.metadata", "action.multiline" };

    private static SpecDef[] BuildSpecs()
    {
        var l = new List<SpecDef>();
        void Node(string id, string title, string icon, bool draft, params string[] nodes)
            => l.Add(new SpecDef { Id = "spec.node." + id, Group = draft ? "IRCv3 drafts" : "IRCv3 toolkit", Title = title, Icon = icon, Nodes = nodes, Draft = draft });
        void Cap(string id, string title, string icon, string cap, bool draft = false)
            => l.Add(new SpecDef { Id = "spec.cap." + id, Group = draft ? "IRCv3 drafts" : "IRCv3 caps", Title = title, Icon = icon, Cap = cap, Draft = draft });

        // node-based (ratified)
        Node("tags", "Tag Whisperer", "🔖", false, "data.gettag");
        Node("account", "From Account", "🪪", false, "filter.fromAccount");
        Node("botmode", "Know Your Bots", "🤖", false, "filter.isBot");
        Node("raw", "Down to the Metal", "📡", false, "irc.raw");
        Node("setname", "Name Changer", "✏️", false, "action.setname");
        Node("away", "Gone Fishing", "🌙", false, "action.away");
        Node("tagmsg", "Tag, You're It", "🏷️", false, "action.tagmsg");
        Node("monitor", "Lookout", "👀", false, "action.monitor");
        // node-based (draft)
        Node("react", "Reactive", "💜", true, "action.react");
        Node("reply", "Threadsmith", "🧵", true, "action.replythread");
        Node("typing", "Now Typing…", "✍️", true, "irc.typing.start", "irc.typing.stop");
        Node("redact", "Eraser", "🩹", true, "action.redact");
        Node("chathistory", "Time Traveller", "📜", true, "action.chathistory");
        Node("rename", "Renamer", "🔤", true, "action.rename");
        Node("metadata", "Librarian", "🗂️", true, "action.metadata");
        Node("multiline", "Poet", "📃", true, "action.multiline");

        // cap-based (ratified)
        Cap("sasl", "SASL", "🔐", "sasl");
        Cap("servertime", "Server Time", "🕒", "server-time");
        Cap("messagetags", "Message Tags", "🔖", "message-tags");
        Cap("accounttag", "Account Tag", "🪪", "account-tag");
        Cap("accountnotify", "Account Notify", "📒", "account-notify");
        Cap("awaynotify", "Away Notify", "🚶", "away-notify");
        Cap("extjoin", "Extended Join", "👋", "extended-join");
        Cap("chghost", "Chghost", "🔁", "chghost");
        Cap("multiprefix", "Multi-Prefix", "➕", "multi-prefix");
        Cap("uhnames", "Userhost in Names", "🧑", "userhost-in-names");
        Cap("echo", "Echo Message", "🪞", "echo-message");
        Cap("labeled", "Labeled Response", "🏷️", "labeled-response");
        Cap("batch", "Batch", "📦", "batch");
        Cap("capnotify", "Cap Notify", "📣", "cap-notify");
        Cap("invitenotify", "Invite Notify", "✉️", "invite-notify");
        Cap("setnamecap", "Setname (cap)", "📝", "setname");
        Cap("stdreplies", "Standard Replies", "📨", "standard-replies");
        Cap("sts", "Strict Transport", "🔒", "sts");
        Cap("utf8only", "UTF8ONLY", "🔤", "utf8only");
        Cap("extmonitor", "Extended Monitor", "🔭", "extended-monitor");
        // cap-based (draft)
        Cap("d_chathistory", "Chathistory (cap)", "📜", "draft/chathistory", true);
        Cap("d_redaction", "Message Redaction (cap)", "🩹", "draft/message-redaction", true);
        Cap("d_multiline", "Multiline (cap)", "📃", "draft/multiline", true);
        Cap("d_rename", "Channel Rename (cap)", "🔤", "draft/channel-rename", true);
        Cap("d_metadata", "Metadata (cap)", "🗂️", "draft/metadata", true);
        Cap("d_readmarker", "Read Marker", "✅", "draft/read-marker", true);
        Cap("d_preaway", "Pre-Away", "🌙", "draft/pre-away", true);
        Cap("d_accreg", "Account Registration", "📝", "draft/account-registration", true);
        Cap("d_extisupport", "Extended ISUPPORT", "📐", "draft/extended-isupport", true);

        return l.ToArray();
    }

    private static Store S()
    {
        lock (Gate)
        {
            if (_s != null) return _s;
            try { _s = File.Exists(FilePath) ? JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath)) ?? new() : new(); }
            catch { _s = new(); }
            _s.Caps ??= new(StringComparer.OrdinalIgnoreCase);
            return _s;
        }
    }

    public static int BotsCreated => S().BotsCreated;
    public static double MaxOnlineSeconds() { var o = S().Online; return o.Count == 0 ? 0 : o.Values.Max(); }
    public static int UnlockedCount => S().Unlocked.Count;
    public static int TotalCount => BotTiers.Length + OnlineTiers.Length + SpecList.Length + 1;

    public static void BotCreated() { lock (Gate) { S().BotsCreated++; _dirty = true; } }

    public static void AddOnline(string bot, double secs)
    {
        if (secs <= 0 || string.IsNullOrEmpty(bot)) return;
        lock (Gate) { var s = S(); s.Online.TryGetValue(bot, out var v); s.Online[bot] = v + secs; _dirty = true; }
    }

    public static void AddCaps(IEnumerable<string> caps)
    {
        if (caps == null) return;
        lock (Gate) { var s = S(); foreach (var c in caps) if (!string.IsNullOrWhiteSpace(c) && s.Caps.Add(c.Trim())) _dirty = true; }
    }

    private static bool Unlock(string id, string detail)
    {
        lock (Gate) { var s = S(); if (s.Unlocked.ContainsKey(id)) return false; s.Unlocked[id] = detail; _dirty = true; return true; }
    }

    public static List<AchDef> Evaluate(IReadOnlyList<(string name, IReadOnlyCollection<string> types)> bots)
    {
        var fresh = new List<AchDef>();
        foreach (var d in AllDefs(bots))
            if (d.Unlocked && Unlock(d.Id, d.Detail)) fresh.Add(d);
        return fresh;
    }

    public static List<AchDef> AllDefs(IReadOnlyList<(string name, IReadOnlyCollection<string> types)>? bots = null)
    {
        var list = new List<AchDef>();
        int bc = BotsCreated;
        foreach (var n in BotTiers)
            list.Add(Mk("bots." + n, "Bots", n == 1 ? "First bot" : $"{n} bots", $"Create {n} bot{(n == 1 ? "" : "s")}.", "🤖",
                bc >= n, $"{Math.Min(bc, n)}/{n}", Math.Min(1f, bc / (float)n)));

        double maxSecs = MaxOnlineSeconds();
        foreach (var h in OnlineTiers)
            list.Add(Mk("online." + h, "Uptime", Hours(h) + " online", $"Keep one bot connected for {Hours(h)}.", "⏱️",
                maxSecs >= h * 3600.0, FormatDur(maxSecs) + " best", (float)Math.Min(1.0, maxSecs / (h * 3600.0))));

        var caps = S().Caps;
        // draft pioneer (special): use any draft-spec node
        int draftUsed = bots == null ? 0 : DraftNodes.Count(t => bots.Any(b => b.types.Contains(t)));
        bool pioneer = IsUnlocked("spec.pioneer") || draftUsed > 0;
        list.Add(Mk("spec.pioneer", "IRCv3 drafts", "Living on the Edge", "Use any draft-spec node (react, typing, redaction, chathistory, multiline...).", "🌟",
            pioneer, draftUsed > 0 ? draftUsed + " draft nodes" : "use a draft node", DraftNodes.Length == 0 ? 1 : Math.Min(1f, draftUsed / 3f)));

        foreach (var sp in SpecList)
        {
            bool unlocked = IsUnlocked(sp.Id);
            string detail; float prog;
            if (sp.Cap.Length > 0)
            {
                if (caps.Contains(sp.Cap)) unlocked = true;
                detail = unlocked ? "negotiated" : "connect to a server with it";
                prog = unlocked ? 1f : 0f;
            }
            else
            {
                int best = (bots == null || bots.Count == 0) ? 0 : bots.Max(b => sp.Nodes.Count(t => b.types.Contains(t)));
                if (best == sp.Nodes.Length && sp.Nodes.Length > 0) unlocked = true;
                detail = $"{(unlocked ? sp.Nodes.Length : best)}/{sp.Nodes.Length} nodes";
                prog = sp.Nodes.Length == 0 ? 0 : Math.Min(1f, best / (float)sp.Nodes.Length);
            }
            string desc = sp.Cap.Length > 0 ? $"Connect to a server offering the {sp.Cap} capability."
                : "Use this spec on a bot: " + string.Join(", ", sp.Nodes) + ".";
            list.Add(Mk(sp.Id, sp.Group, sp.Title + (sp.Draft ? "  ·  draft" : ""), desc, sp.Icon, unlocked, detail, prog));
        }
        return list;
    }

    public static bool IsUnlocked(string id) => S().Unlocked.ContainsKey(id);

    private static AchDef Mk(string id, string cat, string title, string desc, string icon, bool unlocked, string detail, float prog)
        => new() { Id = id, Category = cat, Title = title, Desc = desc, Icon = icon, Unlocked = unlocked, Detail = detail, Progress = prog };

    public static void Save()
    {
        lock (Gate)
        {
            if (!_dirty || _s == null) return;
            try { Directory.CreateDirectory(Dir); File.WriteAllText(FilePath, JsonSerializer.Serialize(_s, new JsonSerializerOptions { WriteIndented = true })); _dirty = false; }
            catch { }
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
