using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Ircuitry.App.Server;

/// <summary>
/// Per-bot sharing, layered on top of the global token roles. A bot has an owner, a visibility (private/public)
/// and an others-can-edit toggle. The role is the ceiling (a viewer never edits anything); the ACL narrows it
/// further per bot:
///   - see:  admin, or the owner, or anyone if the bot is public
///   - edit: admin, or the owner, or (public AND others-can-edit) - but only if the role can edit at all
/// A bot with no ACL entry uses the permissive legacy default (public, editable), so an existing single-admin
/// workspace behaves exactly as before. A bot created by a (non-admin) user is claimed private to them.
/// </summary>
public static partial class ControlServer
{
    public sealed record BotAcl(string Owner, string Visibility, bool Editable)
    {
        public static readonly BotAcl Default = new("", "public", true);
        public bool Private => Visibility == "private";
    }

    private static readonly Dictionary<string, BotAcl> _acl = new(StringComparer.OrdinalIgnoreCase);
    private static string AclFile => Path.Combine(AppModel.WorkspaceDir, "server-acl.json");

    private static BotAcl AclOf(string bot) => _acl.TryGetValue(bot, out var a) ? a : BotAcl.Default;
    private static bool IsAdmin(Client c) => c.Role == "admin";
    private static bool Owns(Client c, string bot) { var a = AclOf(bot); return a.Owner.Length > 0 && a.Owner == c.User; }

    /// <summary>Can this client even know the bot exists?</summary>
    private static bool CanSee(Client c, string bot)
    { var a = AclOf(bot); return IsAdmin(c) || !a.Private || (a.Owner.Length > 0 && a.Owner == c.User); }

    /// <summary>Can this client edit or run the bot? Role is the ceiling; the ACL narrows it per bot.</summary>
    private static bool CanEditBot(Client c, string bot)
    {
        if (IsAdmin(c)) return true;
        if (!c.CanEdit) return false;
        var a = AclOf(bot);
        return (a.Owner.Length > 0 && a.Owner == c.User) || (!a.Private && a.Editable);
    }

    private static bool BotExists(string name)
    { lock (Gate) return _app.Bots.Any(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); }

    /// <summary>The bot a request targets, only if it names one explicitly (else null - a global/active op).</summary>
    private static string? ExplicitBotName(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("bot", out var b)) return null;
        if (b.ValueKind == JsonValueKind.String) return b.GetString();
        if (b.ValueKind == JsonValueKind.Number && b.TryGetInt32(out var i)) { lock (Gate) return i >= 0 && i < _app.Bots.Count ? _app.Bots[i].Name : null; }
        return null;
    }

    /// <summary>Give a freshly created bot an owner and a secure default (private to its creator).</summary>
    private static void ClaimBot(string bot, string user)
    {
        if (_acl.ContainsKey(bot) || user.Length == 0) return;
        _acl[bot] = new BotAcl(user, "private", false);
        SaveAcl();
    }

    /// <summary>The sharing view a given client sees for a bot (includes their own derived rights).</summary>
    private static object AclView(Client c, string bot)
    {
        var a = AclOf(bot);
        return new { owner = a.Owner, visibility = a.Visibility, editable = a.Editable, mine = Owns(c, bot), canEdit = CanEditBot(c, bot) };
    }

    private static void LoadAcl()
    {
        _acl.Clear();
        try
        {
            if (!File.Exists(AclFile)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(AclFile));
            if (!doc.RootElement.TryGetProperty("acl", out var t) || t.ValueKind != JsonValueKind.Object) return;
            foreach (var p in t.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Object) continue;
                string owner = p.Value.TryGetProperty("owner", out var o) ? o.GetString() ?? "" : "";
                string vis = p.Value.TryGetProperty("visibility", out var v) ? v.GetString() ?? "public" : "public";
                bool edit = !p.Value.TryGetProperty("editable", out var e) || e.ValueKind != JsonValueKind.False;
                _acl[p.Name] = new BotAcl(owner, vis == "private" ? "private" : "public", edit);
            }
        }
        catch { /* ignore a corrupt file - fall back to legacy-permissive defaults */ }
    }

    private static void SaveAcl()
    {
        try
        {
            Directory.CreateDirectory(AppModel.WorkspaceDir);
            var obj = new Dictionary<string, object?>();
            lock (_acl) foreach (var kv in _acl) obj[kv.Key] = new { owner = kv.Value.Owner, visibility = kv.Value.Visibility, editable = kv.Value.Editable };
            File.WriteAllText(AclFile, JsonSerializer.Serialize(new { acl = obj }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>Op: <c>{op:"set_acl", bot, visibility?, editable?, owner?}</c>. Owner or admin only. The first
    /// time anyone shares a previously-unowned bot, the caller becomes its owner. Admins can transfer ownership.</summary>
    private static object SetAcl(Client c, JsonElement root)
    {
        string bot = Str(root, "bot");
        if (!BotExists(bot) || !CanSee(c, bot)) return new { error = "no such bot" };   // never reveal a bot you can't see
        if (!(IsAdmin(c) || Owns(c, bot) || AclOf(bot).Owner.Length == 0))
            return new { error = "only the owner or an admin can change sharing" };

        var cur = AclOf(bot);
        string vis = root.TryGetProperty("visibility", out var vv) && vv.ValueKind == JsonValueKind.String ? (vv.GetString() == "private" ? "private" : "public") : cur.Visibility;
        bool edit = root.TryGetProperty("editable", out var ev) && (ev.ValueKind == JsonValueKind.True || ev.ValueKind == JsonValueKind.False) ? ev.GetBoolean() : cur.Editable;
        string owner = cur.Owner.Length > 0 ? cur.Owner : c.User;     // first share claims ownership
        if (root.TryGetProperty("owner", out var ov) && ov.ValueKind == JsonValueKind.String && IsAdmin(c)) owner = ov.GetString() ?? owner;

        _acl[bot] = new BotAcl(owner, vis, edit);
        SaveAcl();
        return AclView(c, bot);
    }
}
