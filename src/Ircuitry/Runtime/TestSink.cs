using System.Collections.Generic;
using Ircuitry.Core;

namespace Ircuitry.Runtime;

/// <summary>
/// A capture sink for the Test Bench: instead of touching IRC it records what the graph *would*
/// send, and runs against a throwaway copy of the bot's variables so a dry run can't corrupt state.
/// </summary>
public sealed class TestSink : IRuntimeSink
{
    public readonly List<(string kind, string text)> Sent = new();
    private readonly Dictionary<string, string> _state;

    public TestSink(Dictionary<string, string> stateCopy) { _state = stateCopy; }

    public void Privmsg(string t, string x) => Sent.Add(("PRIVMSG", $"{t}  {x}"));
    public void Notice(string t, string x) => Sent.Add(("NOTICE", $"{t}  {x}"));
    public void React(string t, string m, string e) => Sent.Add(("REACT", $"{t}  {e}"));
    public void PrivmsgTagged(string t, string x, string tags) => Sent.Add(("PRIVMSG", $"{t}  {x}" + (tags.Length > 0 ? "   [" + tags + "]" : "")));
    public void NoticeTagged(string t, string x, string tags) => Sent.Add(("NOTICE", $"{t}  {x}" + (tags.Length > 0 ? "   [" + tags + "]" : "")));
    public void Join(string c) => Sent.Add(("JOIN", c));
    public void Part(string c, string r) => Sent.Add(("PART", $"{c}  {r}"));
    public void Raw(string l) => Sent.Add(("RAW", l));
    public bool HasCap(string cap) => true;   // a dry run has no live caps - assume yes so cap-gated commands still show what WOULD send
    public void StartTyping(string t) => Sent.Add(("TYPING", "start " + t));
    public void StopTyping(string t) => Sent.Add(("TYPING", "stop " + t));
    public void Log(string m, LogLevel lvl) => Sent.Add(("LOG", m));
    public void NodeFired(string id) { }
    public void RunCompleted(System.Collections.Generic.IReadOnlyCollection<string> executedTypes) { }   // dry run: no achievement credit
    public string GetState(string key) => _state.TryGetValue(key, out var v) ? v : "";
    public void SetState(string key, string value) => _state[key] = value;
}
