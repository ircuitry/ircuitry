using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Ircuitry.Irc;

/// <summary>
/// Parsed RPL_ISUPPORT (005): the raw KEY-&gt;VALUE map the server advertises, plus typed views of the
/// tokens that actually change how we parse (PREFIX, CHANTYPES, CHANMODES, CASEMAPPING, NICKLEN...). Tokens
/// accumulate across the several 005 lines a server sends; a "-KEY" token removes one. Honoring these instead
/// of hardcoding (e.g. status-prefix chars, channel-type chars, casemapping for nick equality) is what makes
/// the bot correct across networks rather than just on the ones that match the defaults.
/// </summary>
public sealed class IrcIsupport
{
    private readonly Dictionary<string, string> _t = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>Feed the parameters of one 005 line (the leading nick and the trailing "are supported"
    /// sentence are ignored automatically).</summary>
    public void Feed(IEnumerable<string> tokens)
    {
        lock (_lock)
            foreach (var raw in tokens)
            {
                if (raw.Length == 0 || raw.IndexOf(' ') >= 0) continue;   // the trailing ":are supported" sentence
                if (raw[0] == '-') { _t.Remove(raw[1..]); continue; }      // -KEY removes a token
                int eq = raw.IndexOf('=');
                if (eq < 0) _t[raw] = "";
                else _t[raw[..eq]] = DecodeIsupport(raw[(eq + 1)..]);
            }
    }

    public void Reset() { lock (_lock) _t.Clear(); }

    public string Get(string key, string fallback = "")
    {
        lock (_lock) return _t.TryGetValue(key, out var v) ? v : fallback;
    }

    public bool Has(string key) { lock (_lock) return _t.ContainsKey(key); }

    public int IntValue(string key, int fallback)
        => int.TryParse(Get(key), out var n) ? n : fallback;

    /// <summary>A stable snapshot of every advertised token as "KEY=VALUE" (boolean tokens as just "KEY").</summary>
    public List<string> Snapshot()
    {
        lock (_lock) return _t.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Value.Length == 0 ? kv.Key : kv.Key + "=" + kv.Value).ToList();
    }

    // ---- typed views of the parsing-relevant tokens ----

    /// <summary>The mode letters of PREFIX, highest-rank first, e.g. "ohv" from "(ohv)@%+". Default "ov".</summary>
    public string PrefixModes
    {
        get { var (m, _) = SplitPrefix(); return m; }
    }

    /// <summary>The status characters of PREFIX, highest-rank first, e.g. "@%+" from "(ohv)@%+".
    /// Defaults to the de-facto modern set so name-list parsing still strips every common prefix.</summary>
    public string PrefixChars
    {
        get { var (_, c) = SplitPrefix(); return c; }
    }

    private (string modes, string chars) SplitPrefix()
    {
        string p = Get("PREFIX");
        int close = p.IndexOf(')');
        if (p.StartsWith('(') && close > 1 && close < p.Length)
            return (p[1..close], p[(close + 1)..]);
        return ("qaohv", "~&@%+");   // sensible default when unadvertised
    }

    /// <summary>Map a status character (e.g. '@') to its mode letter (e.g. 'o'), or '\0' if unknown.</summary>
    public char ModeForPrefix(char prefix)
    {
        var (modes, chars) = SplitPrefix();
        int i = chars.IndexOf(prefix);
        return i >= 0 && i < modes.Length ? modes[i] : '\0';
    }

    /// <summary>Rank a member's prefix string (0 = highest). Used to sort op &gt; halfop &gt; voice &gt; none.</summary>
    public int Rank(string prefix)
    {
        var chars = PrefixChars;
        int best = chars.Length;
        foreach (var ch in prefix)
        {
            int i = chars.IndexOf(ch);
            if (i >= 0 && i < best) best = i;
        }
        return best;
    }

    /// <summary>Channel-type characters, e.g. "#&". Default "#&".</summary>
    public string ChanTypes => Get("CHANTYPES", "#&");

    public bool IsChannel(string target) => target.Length > 0 && ChanTypes.IndexOf(target[0]) >= 0;

    /// <summary>Server casemapping: "ascii", "rfc1459" or "rfc1459-strict". Default "rfc1459".</summary>
    public string CaseMapping => Get("CASEMAPPING", "rfc1459").ToLowerInvariant();

    /// <summary>An equality comparer for nicks/channels that honors the server's CASEMAPPING.</summary>
    public IEqualityComparer<string> CaseComparer => new IrcCaseComparer(CaseMapping);

    /// <summary>Decodes ISUPPORT value escapes: \xHH (e.g. \x20 -> space).</summary>
    public static string DecodeIsupport(string s)
    {
        if (s.IndexOf("\\x", StringComparison.OrdinalIgnoreCase) < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 3 < s.Length && (s[i + 1] == 'x' || s[i + 1] == 'X')
                && Uri.IsHexDigit(s[i + 2]) && Uri.IsHexDigit(s[i + 3]))
            {
                sb.Append((char)Convert.ToInt32(s.Substring(i + 2, 2), 16));
                i += 3;
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }
}

/// <summary>Case-insensitive nick/channel comparison per an IRC CASEMAPPING. "ascii" folds only A-Z; the
/// RFC 1459 mappings additionally treat {}|(^) as the lowercase of []\(~), because IRC inherited that from
/// its Scandinavian origins and servers still enforce it (Nick[] and nick{} are the SAME user).</summary>
public sealed class IrcCaseComparer : IEqualityComparer<string>
{
    private readonly bool _rfc;
    private readonly bool _strict;
    public IrcCaseComparer(string mapping)
    {
        mapping = (mapping ?? "").ToLowerInvariant();
        _rfc = mapping != "ascii";                 // rfc1459 / rfc1459-strict (and the default) fold the bracket set
        _strict = mapping == "rfc1459-strict";     // strict excludes ~ <-> ^
    }

    public char Lower(char c)
    {
        if (c >= 'A' && c <= 'Z') return (char)(c + 32);
        if (_rfc)
            switch (c)
            {
                case '[': return '{';
                case ']': return '}';
                case '\\': return '|';
                case '~': return _strict ? '~' : '^';
            }
        return c;
    }

    public string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(Lower(c));
        return sb.ToString();
    }

    public bool Equals(string? a, string? b)
    {
        if (a == null || b == null) return a == b;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (Lower(a[i]) != Lower(b[i])) return false;
        return true;
    }

    public int GetHashCode(string s)
    {
        var h = new HashCode();
        foreach (var c in s) h.Add(Lower(c));
        return h.ToHashCode();
    }
}
