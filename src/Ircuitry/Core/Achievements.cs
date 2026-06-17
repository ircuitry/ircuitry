using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Ircuitry.Core;

/// <summary>One achievement definition with its current unlocked status filled in for display.</summary>
public sealed class AchDef
{
    public string Id = "", Category = "", Title = "", Desc = "", Icon = Ircuitry.Core.Icons.Glyph("trophy");
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
    public string Id = "", Group = "", Title = "", Icon = Ircuitry.Core.Icons.Glyph("globe"), Cap = "";
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
        public HashSet<string> Specs { get; set; } = new();   // spec ids satisfied by a successful run
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
        Node("tags", "Tag Whisperer", Ircuitry.Core.Icons.Glyph("bookmark-simple"), false, "data.gettag");
        Node("account", "From Account", Ircuitry.Core.Icons.Glyph("identification-card"), false, "filter.fromAccount");
        Node("botmode", "Know Your Bots", Ircuitry.Core.Icons.Glyph("robot"), false, "filter.isBot");
        Node("raw", "Down to the Metal", Ircuitry.Core.Icons.Glyph("broadcast"), false, "irc.raw");
        Node("setname", "Name Changer", Ircuitry.Core.Icons.Glyph("pencil"), false, "action.setname");
        Node("away", "Gone Fishing", Ircuitry.Core.Icons.Glyph("moon"), false, "action.away");
        Node("tagmsg", "Tag, You're It", Ircuitry.Core.Icons.Glyph("tag"), false, "action.tagmsg");
        Node("monitor", "Lookout", Ircuitry.Core.Icons.Glyph("eyes"), false, "action.monitor");
        // node-based (draft)
        Node("react", "Reactive", Ircuitry.Core.Icons.Glyph("heart"), true, "action.react");
        Node("reply", "Threadsmith", Ircuitry.Core.Icons.Glyph("needle"), true, "action.replythread");
        Node("typing", "Now Typing…", Ircuitry.Core.Icons.Glyph("pencil-line"), true, "irc.typing.start", "irc.typing.stop");
        Node("redact", "Eraser", Ircuitry.Core.Icons.Glyph("bandaids"), true, "action.redact");
        Node("chathistory", "Time Traveller", Ircuitry.Core.Icons.Glyph("scroll"), true, "action.chathistory");
        Node("rename", "Renamer", Ircuitry.Core.Icons.Glyph("translate"), true, "action.rename");
        Node("metadata", "Librarian", Ircuitry.Core.Icons.Glyph("folders"), true, "action.metadata");
        Node("multiline", "Poet", Ircuitry.Core.Icons.Glyph("file-text"), true, "action.multiline");

        // cap-based (ratified)
        Cap("sasl", "SASL", Ircuitry.Core.Icons.Glyph("lock-key"), "sasl");
        Cap("servertime", "Server Time", Ircuitry.Core.Icons.Glyph("clock"), "server-time");
        Cap("messagetags", "Message Tags", Ircuitry.Core.Icons.Glyph("bookmark-simple"), "message-tags");
        Cap("accounttag", "Account Tag", Ircuitry.Core.Icons.Glyph("identification-card"), "account-tag");
        Cap("accountnotify", "Account Notify", Ircuitry.Core.Icons.Glyph("notebook"), "account-notify");
        Cap("awaynotify", "Away Notify", Ircuitry.Core.Icons.Glyph("person-simple-walk"), "away-notify");
        Cap("extjoin", "Extended Join", Ircuitry.Core.Icons.Glyph("hand-waving"), "extended-join");
        Cap("chghost", "Chghost", Ircuitry.Core.Icons.Glyph("repeat"), "chghost");
        Cap("multiprefix", "Multi-Prefix", Ircuitry.Core.Icons.Glyph("plus"), "multi-prefix");
        Cap("uhnames", "Userhost in Names", Ircuitry.Core.Icons.Glyph("user"), "userhost-in-names");
        Cap("echo", "Echo Message", Ircuitry.Core.Icons.Glyph("frame-corners"), "echo-message");
        Cap("labeled", "Labeled Response", Ircuitry.Core.Icons.Glyph("tag"), "labeled-response");
        Cap("batch", "Batch", Ircuitry.Core.Icons.Glyph("package"), "batch");
        Cap("capnotify", "Cap Notify", Ircuitry.Core.Icons.Glyph("megaphone"), "cap-notify");
        Cap("invitenotify", "Invite Notify", Ircuitry.Core.Icons.Glyph("envelope"), "invite-notify");
        Cap("setnamecap", "Setname (cap)", Ircuitry.Core.Icons.Glyph("note-pencil"), "setname");
        Cap("stdreplies", "Standard Replies", Ircuitry.Core.Icons.Glyph("envelope"), "standard-replies");
        Cap("sts", "Strict Transport", Ircuitry.Core.Icons.Glyph("lock"), "sts");
        Cap("utf8only", "UTF8ONLY", Ircuitry.Core.Icons.Glyph("translate"), "utf8only");
        Cap("extmonitor", "Extended Monitor", Ircuitry.Core.Icons.Glyph("binoculars"), "extended-monitor");
        // cap-based (draft)
        Cap("d_chathistory", "Chathistory (cap)", Ircuitry.Core.Icons.Glyph("scroll"), "draft/chathistory", true);
        Cap("d_redaction", "Message Redaction (cap)", Ircuitry.Core.Icons.Glyph("bandaids"), "draft/message-redaction", true);
        Cap("d_multiline", "Multiline (cap)", Ircuitry.Core.Icons.Glyph("file-text"), "draft/multiline", true);
        Cap("d_rename", "Channel Rename (cap)", Ircuitry.Core.Icons.Glyph("translate"), "draft/channel-rename", true);
        Cap("d_metadata", "Metadata (cap)", Ircuitry.Core.Icons.Glyph("folders"), "draft/metadata", true);
        Cap("d_readmarker", "Read Marker", Ircuitry.Core.Icons.Glyph("check-circle"), "draft/read-marker", true);
        Cap("d_preaway", "Pre-Away", Ircuitry.Core.Icons.Glyph("moon"), "draft/pre-away", true);
        Cap("d_accreg", "Account Registration", Ircuitry.Core.Icons.Glyph("note-pencil"), "draft/account-registration", true);
        Cap("d_extisupport", "Extended ISUPPORT", Ircuitry.Core.Icons.Glyph("ruler"), "draft/extended-isupport", true);

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
            _s.Specs ??= new();
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

    /// <summary>Record one successful run: a spec is satisfied only when ALL its nodes ran in this run.</summary>
    public static void MarkRun(IReadOnlyCollection<string> executedTypes)
    {
        if (executedTypes == null || executedTypes.Count == 0) return;
        lock (Gate)
        {
            var s = S();
            foreach (var sp in SpecList)
                if (sp.Nodes.Length > 0 && sp.Nodes.All(t => executedTypes.Contains(t)))
                    if (s.Specs.Add(sp.Id)) _dirty = true;
            if (DraftNodes.Any(t => executedTypes.Contains(t)) && s.Specs.Add("spec.pioneer")) _dirty = true;
        }
    }

    private static bool Unlock(string id, string detail)
    {
        lock (Gate) { var s = S(); if (s.Unlocked.ContainsKey(id)) return false; s.Unlocked[id] = detail; _dirty = true; return true; }
    }

    public static List<AchDef> Evaluate()
    {
        var fresh = new List<AchDef>();
        foreach (var d in AllDefs())
            if (d.Unlocked && Unlock(d.Id, d.Detail)) fresh.Add(d);
        return fresh;
    }

    public static List<AchDef> AllDefs()
    {
        var list = new List<AchDef>();
        int bc = BotsCreated;
        foreach (var n in BotTiers)
            list.Add(Mk("bots." + n, "Bots", n == 1 ? "First bot" : $"{n} bots", $"Create {n} bot{(n == 1 ? "" : "s")}.", Ircuitry.Core.Icons.Glyph("robot"),
                bc >= n, $"{Math.Min(bc, n)}/{n}", Math.Min(1f, bc / (float)n)));

        double maxSecs = MaxOnlineSeconds();
        foreach (var h in OnlineTiers)
            list.Add(Mk("online." + h, "Uptime", Hours(h) + " online", $"Keep one bot connected for {Hours(h)}.", Ircuitry.Core.Icons.Glyph("timer"),
                maxSecs >= h * 3600.0, FormatDur(maxSecs) + " best", (float)Math.Min(1.0, maxSecs / (h * 3600.0))));

        var caps = S().Caps;
        var specs = S().Specs;   // satisfied by a successful run (all the spec's nodes ran in one fire)
        bool pioneer = specs.Contains("spec.pioneer");
        list.Add(Mk("spec.pioneer", "IRCv3 drafts", "Living on the Edge", "Run any draft-spec node (react, typing, redaction, chathistory, multiline...).", Ircuitry.Core.Icons.Glyph("star"),
            pioneer, pioneer ? "unlocked" : "run a draft node", pioneer ? 1 : 0));

        foreach (var sp in SpecList)
        {
            bool unlocked; string detail; float prog;
            if (sp.Cap.Length > 0)
            {
                unlocked = caps.Contains(sp.Cap);
                detail = unlocked ? "negotiated" : "connect to a server with it";
                prog = unlocked ? 1f : 0f;
            }
            else
            {
                unlocked = specs.Contains(sp.Id);
                detail = unlocked ? "done" : sp.Nodes.Length > 1 ? $"run all {sp.Nodes.Length} in one fire" : "run it once";
                prog = unlocked ? 1f : 0f;
            }
            string desc = sp.Cap.Length > 0 ? $"Connect to a server offering the {sp.Cap} capability."
                : sp.Nodes.Length > 1 ? "Run all of these successfully in one fire: " + string.Join(", ", sp.Nodes) + "."
                : "Successfully run " + string.Join(", ", sp.Nodes) + ".";
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
