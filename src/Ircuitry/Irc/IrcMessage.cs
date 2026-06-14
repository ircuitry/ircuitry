using System.Collections.Generic;

namespace Ircuitry.Irc;

/// <summary>A parsed IRCv3 line: tags, source, command and params (trailing is the last param).</summary>
public sealed class IrcMessage
{
    public Dictionary<string, string> Tags = new();
    public string? Source;        // raw prefix, e.g. nick!user@host or server
    public string? Nick;
    public string? User;
    public string? Host;
    public string Command = "";
    public List<string> Params = new();
    public string Raw = "";

    public int Count => Params.Count;
    public string P(int i) => i >= 0 && i < Params.Count ? Params[i] : "";
    public string Trailing => Params.Count > 0 ? Params[^1] : "";

    public bool Is(string cmd) => string.Equals(Command, cmd, System.StringComparison.OrdinalIgnoreCase);
    public bool IsNumeric(out int n) => int.TryParse(Command, out n);

    public string Tag(string key) => Tags.TryGetValue(key, out var v) ? v : "";
}
