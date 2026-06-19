using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ircuitry.Graph;

/// <summary>Heuristic content guardrails for the Moderate In / Moderate Out nodes. Fast, offline, and
/// provider-agnostic; an AI endpoint is layered on top by the node when the user opts in.</summary>
public static class Moderation
{
    private static readonly Regex UrlRx = new(@"\b(?:https?://|www\.)\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InviteRx = new(@"(?:\birc://|\bjoin\s+#|/join\s+#|\bcome\s+to\s+#)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Split a user-supplied block list (commas or newlines) into trimmed, non-empty terms.</summary>
    public static List<string> Terms(string raw) =>
        (raw ?? "").Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    /// <summary>Run the heuristic checks. Returns whether it's flagged, a short category, and a reason.</summary>
    public static (bool flagged, string category, string reason) Check(
        string text, IEnumerable<string> blockWords, bool links, bool caps, bool invites)
    {
        string t = text ?? "";
        string low = t.ToLowerInvariant();

        foreach (var w in blockWords)
        {
            string ww = w.ToLowerInvariant();
            if (ww.Length > 0 && low.Contains(ww)) return (true, "blocklist", "matched blocked term \"" + w + "\"");
        }
        if (links && UrlRx.IsMatch(t)) return (true, "link", "contains a link");
        if (invites && InviteRx.IsMatch(t)) return (true, "invite", "looks like a channel invite/redirect");
        if (caps)
        {
            var letters = t.Where(char.IsLetter).ToArray();
            if (letters.Length >= 8 && letters.Count(char.IsUpper) / (double)letters.Length > 0.7)
                return (true, "caps", "excessive shouting");
        }
        return (false, "", "");
    }

    /// <summary>Redact the block-list terms and any links from <paramref name="text"/> (for Moderate Out's
    /// "redact" mode), replacing each with asterisks of the same length. Returns the cleaned text.</summary>
    public static string Redact(string text, IEnumerable<string> blockWords, bool links)
    {
        string t = text ?? "";
        foreach (var w in blockWords)
            if (w.Length > 0)
                t = Regex.Replace(t, Regex.Escape(w), m => new string('*', m.Value.Length), RegexOptions.IgnoreCase);
        if (links) t = UrlRx.Replace(t, m => new string('*', Math.Min(m.Value.Length, 12)));
        return t;
    }

    /// <summary>Parse a one-line classifier verdict (e.g. "SAFE" or "FLAG: spam"). Null = unparseable.</summary>
    public static (bool flagged, string reason)? ParseVerdict(string reply)
    {
        string s = (reply ?? "").Trim();
        if (s.Length == 0) return null;
        string head = s.Split('\n', 2)[0].Trim();
        if (head.StartsWith("SAFE", StringComparison.OrdinalIgnoreCase)) return (false, "");
        if (head.StartsWith("FLAG", StringComparison.OrdinalIgnoreCase))
        {
            int colon = head.IndexOf(':');
            return (true, colon >= 0 ? head[(colon + 1)..].Trim() : "flagged by AI");
        }
        return null;
    }

    public const string ClassifierSystem =
        "You are a content-moderation classifier for an IRC bot. Reply with exactly one line: " +
        "\"SAFE\" if the message is acceptable, otherwise \"FLAG: <short reason>\". No other text.";
}
