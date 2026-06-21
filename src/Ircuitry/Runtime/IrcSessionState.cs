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
        public readonly Dictionary<string, string> Members;   // nick -> prefix(es), keyed per the server's CASEMAPPING
        public Chan(IEqualityComparer<string> cmp) { Members = new(cmp); }
    }

    public readonly record struct Note(DateTime At, string Text);

    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
    private readonly object _lock = new();
    private readonly List<Chan> _chans = new();               // insertion order (join order)
    private readonly LinkedList<Note> _notes = new();         // recent human-language narration
    private string _network = "";
    private string _filehost = "";   // IRCv3 draft/FILEHOST upload URL advertised in ISUPPORT
    private readonly IrcIsupport _isupport = new();           // full parsed RPL_ISUPPORT (PREFIX, CHANTYPES, CASEMAPPING...)
    private IEqualityComparer<string> _cmp = new IrcCaseComparer("rfc1459");   // nick/channel equality, updated from CASEMAPPING

    /// <summary>The server's advertised ISUPPORT (005) tokens and the typed views derived from them.</summary>
    public IrcIsupport Isupport => _isupport;
    /// <summary>True if <paramref name="target"/> names a channel per the server's CHANTYPES (default "#&amp;").</summary>
    public bool IsChannel(string target) => _isupport.IsChannel(target);

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
                .OrderBy(m => _isupport.Rank(m.prefix))     // op > halfop > voice > none, per the server's PREFIX
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

    // ---- write side: observe one parsed line (call for every non-batched message) ----

    public void Observe(IrcMessage m, string selfNick)
    {
        lock (_lock) ObserveLocked(m, selfNick ?? "");
    }

    public void Reset()
    {
        lock (_lock) { _chans.Clear(); _notes.Clear(); _network = ""; _filehost = ""; _isupport.Reset(); _cmp = new IrcCaseComparer("rfc1459"); }
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
            case 5:   // RPL_ISUPPORT - parse the full token set (PREFIX/CHANTYPES/CASEMAPPING/...) and refresh the
                      // nick/channel comparer, then pull the network name + draft/FILEHOST URL for narration
                _isupport.Feed(m.Params.Skip(1));      // Skip(1): drop the leading <nick>; the trailing sentence is space-checked out
                _cmp = _isupport.CaseComparer;
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
                string prefixChars = _isupport.PrefixChars;
                foreach (var raw in m.Trailing.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    int i = 0; var pfx = new StringBuilder();
                    while (i < raw.Length && prefixChars.IndexOf(raw[i]) >= 0) pfx.Append(raw[i++]);
                    string nm = raw[i..];
                    if (nm.Length > 0) c.Members[nm] = pfx.ToString();
                }
                break;
            }
        }
    }

    // ---- helpers (all under _lock) ----

    private Chan? Find(string ch) => _chans.FirstOrDefault(c => _cmp.Equals(c.Name, ch));

    private Chan Ensure(string ch)
    {
        var c = Find(ch);
        if (c == null) { c = new Chan(_cmp) { Name = ch }; _chans.Add(c); }
        return c;
    }

    private void Remove(string ch)
    {
        int i = _chans.FindIndex(c => _cmp.Equals(c.Name, ch));
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

    /// <summary>Decodes ISUPPORT value escapes: \xHH (e.g. \x20 -> space). Kept for callers; the canonical
    /// implementation now lives on <see cref="IrcIsupport"/>.</summary>
    public static string DecodeIsupport(string s) => IrcIsupport.DecodeIsupport(s);
}
