using System.Text;

namespace Ircuitry.Irc;

/// <summary>
/// Parses a single IRCv3 wire line:
///   [@tags] [':' source] command [params] [':' trailing]
/// Tolerant of multiple spaces; unescapes message-tag values.
/// </summary>
public static class IrcParser
{
    public static IrcMessage Parse(string line)
    {
        var msg = new IrcMessage { Raw = line };
        // strip CRLF
        line = line.TrimEnd('\r', '\n');
        int i = 0, n = line.Length;

        // ---- tags ----
        if (i < n && line[i] == '@')
        {
            int sp = line.IndexOf(' ', i);
            if (sp < 0) sp = n;
            ParseTags(line.Substring(i + 1, sp - (i + 1)), msg);
            i = SkipSpaces(line, sp);
        }

        // ---- source / prefix ----
        if (i < n && line[i] == ':')
        {
            int sp = line.IndexOf(' ', i);
            if (sp < 0) sp = n;
            msg.Source = line.Substring(i + 1, sp - (i + 1));
            SplitSource(msg.Source, msg);
            i = SkipSpaces(line, sp);
        }

        // ---- command ----
        {
            int sp = line.IndexOf(' ', i);
            if (sp < 0) sp = n;
            msg.Command = line.Substring(i, sp - i);
            i = SkipSpaces(line, sp);
        }

        // ---- params ----
        while (i < n)
        {
            if (line[i] == ':')
            {
                msg.Params.Add(line.Substring(i + 1)); // trailing - rest of line
                break;
            }
            int sp = line.IndexOf(' ', i);
            if (sp < 0) sp = n;
            msg.Params.Add(line.Substring(i, sp - i));
            i = SkipSpaces(line, sp);
        }

        return msg;
    }

    private static int SkipSpaces(string s, int i)
    {
        while (i < s.Length && s[i] == ' ') i++;
        return i;
    }

    private static void SplitSource(string src, IrcMessage msg)
    {
        int bang = src.IndexOf('!');
        int at = src.IndexOf('@');
        if (bang < 0 && at < 0) { msg.Nick = src; return; }     // server source
        if (bang >= 0)
        {
            msg.Nick = src[..bang];
            if (at > bang) { msg.User = src[(bang + 1)..at]; msg.Host = src[(at + 1)..]; }
            else msg.User = src[(bang + 1)..];
        }
        else
        {
            msg.Nick = src[..at];
            msg.Host = src[(at + 1)..];
        }
    }

    private static void ParseTags(string tagStr, IrcMessage msg)
    {
        foreach (var part in tagStr.Split(';'))
        {
            if (part.Length == 0) continue;
            int eq = part.IndexOf('=');
            if (eq < 0) msg.Tags[part] = "";
            else msg.Tags[part[..eq]] = Unescape(part[(eq + 1)..]);
        }
    }

    private static string Unescape(string v)
    {
        if (v.IndexOf('\\') < 0) return v;
        var sb = new StringBuilder(v.Length);
        for (int i = 0; i < v.Length; i++)
        {
            if (v[i] != '\\') { sb.Append(v[i]); continue; }
            if (i + 1 >= v.Length) break; // lone trailing backslash dropped
            char c = v[++i];
            sb.Append(c switch
            {
                ':' => ';',
                's' => ' ',
                '\\' => '\\',
                'r' => '\r',
                'n' => '\n',
                _ => c,
            });
        }
        return sb.ToString();
    }
}
