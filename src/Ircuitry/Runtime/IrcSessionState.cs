using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Ircuitry.Irc;

namespace Ircuitry.Runtime;

/// <summary>
/// Live, properly-tracked IRC session state for one connection: which channels we're in, who's in them
/// (with their prefixes), each channel's topic, the network name, and a human-language narration of what
/// just happened ("I'm joining #x", "I've been invited to #y"). Built by observing EVERY incoming line.
/// Powers the read-only IRC window and the state-fetch nodes. All access is thread-safe (the IRC read
/// thread writes; the UI thread + worker pool read).
/// </summary>
public sealed class IrcSessionState
{
    public sealed class Chan
    {
        public string Name = "";
        public string Topic = "";
        public readonly Dictionary<string, string> Members = new(StringComparer.OrdinalIgnoreCase); // nick -> prefix(es)
    }

    public readonly record struct Note(DateTime At, string Text);

    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
    private readonly object _lock = new();
    private readonly List<Chan> _chans = new();               // insertion order (join order)
    private readonly LinkedList<Note> _notes = new();         // recent human-language narration
    private string _network = "";
    private string _filehost = "";   // IRCv3 draft/FILEHOST upload URL advertised in ISUPPORT

    // ---- read side (snapshots under lock) ----

    public string Network { get { lock (_lock) return _network; } }
    /// <summary>The server-advertised file upload endpoint (IRCv3 draft/FILEHOST), or "" if none.</summary>
    public string Filehost { get { lock (_lock) return _filehost; } }

    public List<string> Channels()
    {
        lock (_lock) return _chans.Select(c => c.Name).ToList();
    }

    public bool InChannel(string ch)
    {
        lock (_lock) return Find(ch) != null;
    }

    public string Topic(string ch)
    {
        lock (_lock) return Find(ch)?.Topic ?? "";
    }

    public int MemberCount(string ch)
    {
        lock (_lock) return Find(ch)?.Members.Count ?? 0;
    }

    /// <summary>Members of a channel as (nick, prefix), ops first then voiced then the rest, each alphabetical.</summary>
    public List<(string nick, string prefix)> Members(string ch)
    {
        lock (_lock)
        {
            var c = Find(ch);
            if (c == null) return new();
            return c.Members
                .Select(kv => (nick: kv.Key, prefix: kv.Value))
                .OrderBy(m => Rank(m.prefix))
                .ThenBy(m => m.nick, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public List<Note> RecentNotes(int max)
    {
        lock (_lock)
        {
            int skip = Math.Max(0, _notes.Count - max);
            return _notes.Skip(skip).ToList();
        }
    }

    private static int Rank(string prefix) =>
        prefix.Contains('~') ? 0 : prefix.Contains('&') ? 1 : prefix.Contains('@') ? 2 :
        prefix.Contains('%') ? 3 : prefix.Contains('+') ? 4 : 5;

    // ---- write side: observe one parsed line (call for every non-batched message) ----

    public void Observe(IrcMessage m, string selfNick)
    {
        lock (_lock) ObserveLocked(m, selfNick ?? "");
    }

    public void Reset()
    {
        lock (_lock) { _chans.Clear(); _notes.Clear(); _network = ""; }
    }

    private void ObserveLocked(IrcMessage m, string self)
    {
        bool IsSelf(string n) => n.Length > 0 && n.Equals(self, OIC);
        string nick = m.Nick ?? "";

        switch (m.Command.ToUpperInvariant())
        {
            case "JOIN":
            {
                string ch = m.P(0).Length > 0 ? m.P(0) : m.Trailing;
                if (ch.Length == 0) break;
                var c = Ensure(ch);
                if (nick.Length > 0) c.Members[nick] = "";
                if (IsSelf(nick)) Narrate($"I'm joining {ch}");
                break;
            }
            case "PART":
            {
                string ch = m.P(0);
                if (IsSelf(nick)) { Remove(ch); Narrate($"I'm leaving {ch}"); }
                else { var c = Find(ch); c?.Members.Remove(nick); }
                break;
            }
            case "QUIT":
                foreach (var c in _chans) c.Members.Remove(nick);
                break;
            case "KICK":
            {
                string ch = m.P(0); string who = m.P(1);
                if (IsSelf(who)) { Remove(ch); Narrate($"I was kicked from {ch}"); }
                else { var c = Find(ch); c?.Members.Remove(who); }
                break;
            }
            case "NICK":
            {
                string to = m.P(0).Length > 0 ? m.P(0) : m.Trailing;
                foreach (var c in _chans)
                    if (c.Members.Remove(nick, out var pfx)) c.Members[to] = pfx;
                if (IsSelf(nick)) Narrate($"I'm now known as {to}");
                break;
            }
            case "TOPIC":
            {
                string ch = m.P(0); var c = Find(ch);
                if (c != null) c.Topic = m.Trailing;
                Narrate($"The topic of {ch} changed: {m.Trailing}");
                break;
            }
            case "INVITE":
            {
                // :inviter INVITE <us> :#channel   (or trailing carries the channel)
                string ch = m.Params.Count > 1 ? m.P(1) : m.Trailing;
                Narrate($"I've been invited to {ch}" + (nick.Length > 0 ? $" by {nick}" : ""));
                break;
            }
            default:
                if (m.IsNumeric(out int n)) ObserveNumeric(n, m);
                break;
        }
    }

    private void ObserveNumeric(int n, IrcMessage m)
    {
        switch (n)
        {
            case 1:   // RPL_WELCOME
                Narrate("I've connected to the server");
                break;
            case 5:   // RPL_ISUPPORT - pull the network name + IRCv3 draft/FILEHOST URL out (values are \xHH-escaped)
                foreach (var tok in m.Params)
                {
                    if (tok.StartsWith("NETWORK=", OIC))
                    {
                        string net = DecodeIsupport(tok[8..]);
                        if (net.Length > 0 && net != _network) { _network = net; Narrate($"I'm connected to {net}"); }
                    }
                    else if (IsFilehostTok(tok, out string fhVal))   // draft/FILEHOST= / soju.im/FILEHOST= / FILEHOST=
                    {
                        string url = DecodeIsupport(fhVal);
                        if (url != _filehost) { _filehost = url; if (url.Length > 0) Narrate("the server offers a file host"); }
                    }
                    else if (tok.Length > 1 && tok[0] == '-' && IsFilehostTok(tok[1..] + "=", out _))   // -FILEHOST removes it
                        _filehost = "";
                }
                break;
            case 332: { var c = Find(m.P(1)); if (c != null) c.Topic = m.Trailing; break; }   // RPL_TOPIC
            case 331: { var c = Find(m.P(1)); if (c != null) c.Topic = ""; break; }            // RPL_NOTOPIC
            case 353:   // RPL_NAMREPLY: <me> <sym> <channel> :names
            {
                string ch = m.Params.Count >= 4 ? m.P(2) : m.Params.Count >= 2 ? m.P(m.Params.Count - 2) : "";
                if (ch.Length == 0) break;
                var c = Ensure(ch);
                foreach (var raw in m.Trailing.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    int i = 0; var pfx = new StringBuilder();
                    while (i < raw.Length && "~&@%+".IndexOf(raw[i]) >= 0) pfx.Append(raw[i++]);
                    string nm = raw[i..];
                    if (nm.Length > 0) c.Members[nm] = pfx.ToString();
                }
                break;
            }
        }
    }

    // ---- helpers (all under _lock) ----

    private Chan? Find(string ch) => _chans.FirstOrDefault(c => c.Name.Equals(ch, OIC));

    private Chan Ensure(string ch)
    {
        var c = Find(ch);
        if (c == null) { c = new Chan { Name = ch }; _chans.Add(c); }
        return c;
    }

    private void Remove(string ch)
    {
        int i = _chans.FindIndex(c => c.Name.Equals(ch, OIC));
        if (i >= 0) _chans.RemoveAt(i);
    }

    private void Narrate(string text)
    {
        if (_notes.Count > 0 && _notes.Last!.Value.Text == text) return;   // skip exact repeats
        _notes.AddLast(new Note(DateTime.Now, text));
        while (_notes.Count > 60) _notes.RemoveFirst();
    }

    /// <summary>Match an ISUPPORT FILEHOST token in any of its forms (draft/FILEHOST, the soju.im vendor name,
    /// or the eventual unprefixed FILEHOST) and pull out its value.</summary>
    private static bool IsFilehostTok(string tok, out string value)
    {
        foreach (var name in new[] { "draft/FILEHOST=", "soju.im/FILEHOST=", "FILEHOST=" })
            if (tok.StartsWith(name, OIC)) { value = tok[name.Length..]; return true; }
        value = "";
        return false;
    }

    /// <summary>Decodes ISUPPORT value escapes: \xHH (e.g. \x20 -> space).</summary>
    public static string DecodeIsupport(string s)
    {
        if (s.IndexOf("\\x", OIC) < 0) return s;
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
