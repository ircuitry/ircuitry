using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ircuitry.Graph;

namespace Ircuitry.Runtime;

/// <summary>
/// Encoding + schema helpers for the IRCv3 <c>draft/bot-cmds</c> / <c>draft/bot-tools</c>
/// specification. Structured tag values are compact JSON, base64-encoded (RFC 4648 §4),
/// which avoids message-tag value escaping entirely.
/// </summary>
public static class BotTools
{
    private static readonly JsonSerializerOptions Compact = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Compact-JSON-serialise then base64-encode a value for a structured tag.</summary>
    public static string Encode(object value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Compact)));

    /// <summary>Base64-decode + JSON-parse a tag value. Returns null on ANY error (untrusted input).</summary>
    public static JsonElement? Decode(string b64)
    {
        try
        {
            var bytes = Convert.FromBase64String(b64.Trim());
            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.Clone();   // Clone survives the document's disposal
        }
        catch { return null; }
    }

    /// <summary>Encoded values must fit the 4094-byte client-tag limit.</summary>
    public static bool Fits(string b64) => b64.Length <= 4094;

    // ---- JsonElement helpers (defensive: anything unexpected -> fallback) ----
    public static string Str(JsonElement e, string prop, string fallback = "")
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
           ? v.GetString() ?? fallback : fallback;

    // =====================================================================
    //  draft/bot-cmds - command list
    // =====================================================================

    /// <summary>Build the bot's command list from its graph's On Command nodes, base64-encoded.</summary>
    public static string BuildCommandList(NodeGraph g)
    {
        var commands = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? prefix = null;

        foreach (var n in g.Nodes)
        {
            if (n.TypeId != "event.command") continue;
            string name = n.GetParam("command").Trim();
            if (name.Length == 0 || !seen.Add(name)) continue;
            prefix ??= n.GetParam("prefix");
            commands.Add(new Dictionary<string, object?>
            {
                ["name"] = name,
                ["description"] = n.Title.Length > 0 ? n.Title : "Bot command: " + name,
                ["contexts"] = Contexts(n.GetParam("contexts")),
                ["options"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["name"] = "text", ["type"] = "string", ["required"] = false,
                        ["description"] = "command arguments",
                    },
                },
            });
        }

        var obj = new Dictionary<string, object?> { ["commands"] = commands };
        if (!string.IsNullOrEmpty(prefix)) obj["prefix"] = prefix;
        return Encode(obj);
    }

    private static readonly string[] AllContexts = { "public", "private", "pm" };

    /// <summary>Parse an On Command node's contexts field; defaults to all three, keeps only valid values.</summary>
    public static string[] Contexts(string spec)
    {
        var set = new List<string>();
        foreach (var tok in spec.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = tok.Trim().ToLowerInvariant();
            if (Array.IndexOf(AllContexts, t) >= 0 && !set.Contains(t)) set.Add(t);
        }
        return set.Count > 0 ? set.ToArray() : AllContexts;
    }

    /// <summary>A decoded <c>+draft/bot-cmd</c> invocation.</summary>
    public sealed class Invocation
    {
        public string Name = "";
        public string Bot = "";
        public string Channel = "";
        public readonly List<string> OptionValues = new();   // values in payload order, for {args}
        public readonly Dictionary<string, JsonElement> Options = new();
    }

    /// <summary>Parse a <c>+draft/bot-cmd</c> payload. Returns null if it isn't a usable invocation object.</summary>
    public static Invocation? ParseInvocation(string b64)
    {
        var root = Decode(b64);
        if (root is not { ValueKind: JsonValueKind.Object } e) return null;
        var name = Str(e, "name");
        if (name.Length == 0) return null;
        var inv = new Invocation { Name = name, Bot = Str(e, "bot"), Channel = Str(e, "channel") };
        if (e.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in opts.EnumerateObject())
            {
                inv.Options[p.Name] = p.Value.Clone();
                inv.OptionValues.Add(p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString() ?? "",
                    JsonValueKind.Number => p.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => p.Value.GetRawText(),
                });
            }
        }
        return inv;
    }

    /// <summary>The <c>+draft/invoked-by</c> attribution payload for a public reply.</summary>
    public static string EncodeInvokedBy(string nick, string name, Invocation inv)
    {
        var options = new Dictionary<string, object?>();
        foreach (var kv in inv.Options) options[kv.Key] = kv.Value;   // re-serialise as native JSON
        return Encode(new Dictionary<string, object?> { ["nick"] = nick, ["name"] = name, ["options"] = options });
    }

    // =====================================================================
    //  draft/bot-tools - workflow stream
    // =====================================================================

    public static string Workflow(string id, string state, string? name = null, string? trigger = null, string[]? features = null)
    {
        var o = new Dictionary<string, object?> { ["msg"] = "workflow", ["id"] = id, ["state"] = state };
        if (name != null) o["name"] = name;
        if (trigger is { Length: > 0 }) o["trigger"] = trigger;
        if (features != null) o["features"] = features;
        return Encode(o);
    }

    public static string Step(string wid, string sid, string type, string state,
        string? tool = null, object? content = null, string? label = null, bool truncated = false)
    {
        var o = new Dictionary<string, object?> { ["msg"] = "step", ["wid"] = wid, ["sid"] = sid, ["type"] = type, ["state"] = state };
        if (tool != null) o["tool"] = tool;
        if (label != null) o["label"] = label;
        if (content != null) o["content"] = content;
        if (truncated) o["truncated"] = true;   // spec: MUST flag truncated content
        return Encode(o);
    }
}
