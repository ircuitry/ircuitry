using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Ircuitry.Core;
using Ircuitry.Net;

namespace Ircuitry.Graph;

/// <summary>
/// The library of node types Ircuitry ships with. Each entry declares its pins,
/// editable params, and the behaviour that runs against the live IRC connection.
/// </summary>
public static class NodeCatalog
{
    private static List<NodeDef> _builtins = new();
    private static List<NodeDef> _custom = new();
    private static List<NodeDef> _all = new();
    private static Dictionary<string, NodeDef> _byId = new();

    /// <summary>Built-in nodes plus any installed community/custom nodes.</summary>
    public static IReadOnlyList<NodeDef> All => _all;
    /// <summary>Just the installed community/custom nodes (from <see cref="CustomDir"/>).</summary>
    public static IReadOnlyList<NodeDef> Custom => _custom;

    public static NodeDef Get(string typeId) => _byId[typeId];
    public static bool TryGet(string typeId, out NodeDef def) => _byId.TryGetValue(typeId, out def!);

    /// <summary>True if this typeId is an installed community/custom node (not a built-in).</summary>
    public static bool IsCustom(string typeId) => _custom.Any(c => c.TypeId == typeId);

    /// <summary>
    /// Delete the installed <c>.ircnode</c> backing <paramref name="typeId"/> and reload the catalog.
    /// Scans the files (a dropped node may be named differently than its typeId) and removes the match.
    /// </summary>
    public static bool Uninstall(string typeId)
    {
        try
        {
            if (!Directory.Exists(CustomDir)) return false;
            foreach (var f in Directory.GetFiles(CustomDir, "*.ircnode"))
            {
                try
                {
                    var d = CustomNode.Load(File.ReadAllText(f));
                    if (d != null && d.TypeId == typeId) { File.Delete(f); LoadCustom(); return true; }
                }
                catch { /* skip unreadable file */ }
            }
        }
        catch { /* dir unreadable */ }
        return false;
    }

    /// <summary>Where drop-in <c>.ircnode</c> community nodes are installed.</summary>
    public static string CustomDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ircuitry", "nodes");

    /// <summary>(Re)load community nodes from <see cref="CustomDir"/> and merge them with the built-ins.
    /// Bad files are skipped; a custom node can't shadow a built-in typeId.</summary>
    public static void LoadCustom()
    {
        var custom = new List<NodeDef>();
        try
        {
            if (Directory.Exists(CustomDir))
                foreach (var f in Directory.GetFiles(CustomDir, "*.ircnode").OrderBy(x => x))
                {
                    try
                    {
                        var d = CustomNode.Load(File.ReadAllText(f));
                        if (d != null && _builtins.All(b => b.TypeId != d.TypeId) && custom.All(c => c.TypeId != d.TypeId))
                            custom.Add(d);
                    }
                    catch { /* skip a malformed .ircnode */ }
                }
        }
        catch { /* nodes dir unreadable - keep built-ins only */ }
        _custom = custom;
        _all = new List<NodeDef>(_builtins);
        _all.AddRange(_custom);
        _byId = _all.ToDictionary(d => d.TypeId);
    }

    /// <summary>Nodes that emit an IRC effect, so they get the per-node "Send via server" route override.</summary>
    private static readonly HashSet<string> IrcSenders = new()
    {
        "action.reply", "action.replythread", "action.say", "action.join", "action.part", "action.react",
        "action.setname", "action.away", "action.tagmsg", "action.redact", "action.monitor", "action.chathistory",
        "action.rename", "action.metadata", "action.multiline",
        "irc.action", "irc.kick", "irc.mode", "irc.raw", "irc.topic", "irc.typing.start", "irc.typing.stop",
    };

    // ---- tiny builder helpers ----
    private static PinDef Ex(string n = "") => new(n, PinKind.Exec);
    private static PinDef Tx(string n) => new(n, PinKind.Text);
    private static PinDef Us(string n) => new(n, PinKind.User);
    private static PinDef Ch(string n) => new(n, PinKind.Channel);
    private static PinDef Nm(string n) => new(n, PinKind.Number);
    private static PinDef To(string n, bool multi = false) => new(n, PinKind.Tool, multi);

    private static ParamDef P(string key, string label, ParamType t = ParamType.Text, string def = "", string ph = "", string[]? choices = null, Func<Node, bool>? visibleWhen = null, bool secret = false)
        => new() { Key = key, Label = label, Type = t, Default = def, Placeholder = ph, Choices = choices, VisibleWhen = visibleWhen, Secret = secret };

    /// <summary>A growable list param: rows the user adds with an "Add" button (pair = key+value rows).</summary>
    private static ParamDef PL(string key, string label, bool pair, string addLabel)
        => new() { Key = key, Label = label, Type = ParamType.List, Pair = pair, AddLabel = addLabel };

    /// <summary>Where the file nodes read/write relative paths (absolute paths are honoured as-is).</summary>
    public static readonly string FilesDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ircuitry", "files");

    /// <summary>Max bytes a file/calendar node will read, to bound memory on hostile/huge inputs.</summary>
    private const long MaxFileBytes = 8_000_000;

    /// <summary>
    /// Resolve a file-node path. Absolute paths are honoured (the graph author chose them);
    /// relative paths are sandboxed under <see cref="FilesDir"/> and may not escape via "..".
    /// Returns "" if a relative path would escape the sandbox.
    /// </summary>
    public static string ResolveFile(string path)
    {
        path = path.Trim();
        if (path.Length == 0) return "";
        if (Path.IsPathRooted(path)) return path;
        var root = Path.GetFullPath(FilesDir);
        var full = Path.GetFullPath(Path.Combine(root, path));
        return full == root || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : "";
    }

    private static bool TooBig(string path)
    {
        try { return new FileInfo(path).Length > MaxFileBytes; } catch { return false; }
    }

    /// <summary>Collapse newlines to spaces so a value can't smuggle extra IRC commands.</summary>
    private static string OneLine(string s) => s.Replace("\r", " ").Replace("\n", " ").Trim();

    /// <summary>Load an iCal source: inline text, an http(s) URL, or a file path.</summary>
    private static string LoadCalendar(string source)
    {
        source = source.Trim();
        if (source.IndexOf("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase) >= 0) return source;
        if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var (status, body) = Http.Send("GET", source, Array.Empty<(string, string)>(), null);
            return status is >= 200 and < 300 ? body : "";
        }
        var file = ResolveFile(source);
        if (file.Length == 0) return "";
        if (Directory.Exists(file))   // a whole folder of .ics files → merge them
            return string.Join("\n", Directory.GetFiles(file, "*.ics")
                .Where(f => !TooBig(f))
                .Select(f => { try { return File.ReadAllText(f); } catch { return ""; } }));
        return File.Exists(file) && !TooBig(file) ? File.ReadAllText(file) : "";
    }

    private static bool SafeRegex(string input, string pattern, bool ci)
    {
        if (pattern.Length == 0) return false;
        try { return Regex.IsMatch(input, pattern, ci ? RegexOptions.IgnoreCase : RegexOptions.None, TimeSpan.FromMilliseconds(250)); }
        catch { return false; }
    }

    private static double ParseNum(string s) => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
    private static string FormatNum(double v) =>
        !double.IsInfinity(v) && v == Math.Floor(v) ? ((long)v).ToString(CultureInfo.InvariantCulture) : v.ToString("0.###", CultureInfo.InvariantCulture);

    static NodeCatalog()
    {
        var list = new List<NodeDef>
        {
            // ============================ EVENTS ============================
            new()
            {
                TypeId = "event.connect", Icon = "🔌", Title = "On Connect", Subtitle = "trigger",
                Category = NodeCategory.Event, TriggerEvent = "connect",
                Description = "Fires once when the bot finishes registering with the server.",
                Outputs = new[] { Ex("then") },
                Exec = c => c.Pulse(0),
            },
            new()
            {
                TypeId = "event.signal", Icon = "📨", Title = "On Signal", Subtitle = "trigger",
                Category = NodeCategory.Event, TriggerEvent = "signal",
                Description = "Fires when another part of your bot emits a matching signal (via Emit Signal). Lets one workflow trigger another, or run a shared flow from several places. Carries optional {data}.",
                Outputs = new[] { Ex("then"), Tx("data") },
                Params = new[] { P("signal", "Signal name", ParamType.Text, "my-signal", "my-signal") },
                SummaryParam = "signal",
                Exec = c => { c.SetOut(1, c.Var("__signaldata")); c.Pulse(0); },
            },
            new()
            {
                TypeId = "action.signal", Icon = "📣", Title = "Emit Signal", Subtitle = "flow",
                Category = NodeCategory.Logic,
                Description = "Triggers every On Signal node with the matching name, running their flows now. A way for one workflow to call another. Pass optional data along.",
                Inputs = new[] { Ex(), Tx("data") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("signal", "Signal name", ParamType.Text, "my-signal", "my-signal"), P("data", "Data (optional)", ParamType.Text, "", "{message}") },
                SummaryParam = "signal",
                Exec = c => { c.EmitSignal(c.Param("signal"), c.InOr(1, c.Resolve(c.Param("data")))); c.Pulse(0); },
            },
            new()
            {
                TypeId = "event.message", Icon = "💬", Title = "On Message", Subtitle = "trigger",
                Category = NodeCategory.Event, TriggerEvent = "message",
                Description = "Fires for every channel/PM message. Optionally limit to one channel.",
                Outputs = new[] { Ex("then"), Tx("message"), Us("nick"), Ch("channel") },
                Params = new[] { P("channel", "Only channel", ParamType.Text, "", "#any (blank = all)") },
                SummaryParam = "channel",
                Exec = c =>
                {
                    var only = c.Param("channel");
                    if (only.Length > 0 && !only.Equals(c.Var("target"), StringComparison.OrdinalIgnoreCase)) return;
                    c.SetOut(1, c.Var("message"));
                    c.SetOut(2, c.Var("nick"));
                    c.SetOut(3, c.Var("channel"));
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "event.command", Icon = "⚡", Title = "On Command", Subtitle = "trigger",
                Category = NodeCategory.Event, TriggerEvent = "message",
                Description = "Fires when a message begins with a prefix + command word, e.g. !ping.",
                Outputs = new[] { Ex("then"), Tx("args"), Us("nick"), Ch("channel") },
                Params = new[]
                {
                    P("prefix", "Prefix", ParamType.Text, "!", "!"),
                    P("command", "Command", ParamType.Text, "ping", "ping"),
                    P("contexts", "Contexts (IRCv3 slash)", ParamType.Text, "public, private, pm", "public, private, pm"),
                },
                SummaryParam = "command",
                Exec = c =>
                {
                    var msg = c.Var("message");
                    var prefix = c.Param("prefix");
                    var cmd = c.Param("command");
                    if (prefix.Length > 0 && !msg.StartsWith(prefix, StringComparison.Ordinal)) return;
                    var rest = msg[prefix.Length..].TrimStart();
                    int sp = rest.IndexOf(' ');
                    var word = sp < 0 ? rest : rest[..sp];
                    var args = sp < 0 ? "" : rest[(sp + 1)..].Trim();
                    if (!word.Equals(cmd, StringComparison.OrdinalIgnoreCase)) return;
                    c.SetVar("args", args);
                    c.SetVar("command", word);
                    c.SetOut(1, args);
                    c.SetOut(2, c.Var("nick"));
                    c.SetOut(3, c.Var("channel"));
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "event.join", Icon = "👋", Title = "On Join", Subtitle = "trigger",
                Category = NodeCategory.Event, TriggerEvent = "join",
                Description = "Fires when a user joins a channel the bot is in.",
                Outputs = new[] { Ex("then"), Us("nick"), Ch("channel") },
                Exec = c =>
                {
                    c.SetOut(1, c.Var("nick"));
                    c.SetOut(2, c.Var("channel"));
                    c.Pulse(0);
                },
            },

            // ============================ FILTERS ===========================
            new()
            {
                TypeId = "filter.contains", Icon = "🔍", Title = "Text Contains", Subtitle = "filter",
                Category = NodeCategory.Filter,
                Description = "Branches on whether the text contains a substring.",
                Inputs = new[] { Ex(), Tx("text") },
                Outputs = new[] { Ex("match"), Ex("no match") },
                Params = new[]
                {
                    P("needle", "Contains", ParamType.Text, "", "hello"),
                    P("ci", "Ignore case", ParamType.Bool, "true"),
                },
                SummaryParam = "needle",
                Exec = c =>
                {
                    var text = c.InOr(1, c.Var("message"));
                    var needle = c.Param("needle");
                    var cmp = c.ParamBool("ci") ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    bool hit = needle.Length == 0 || text.IndexOf(needle, cmp) >= 0;
                    c.Pulse(hit ? 0 : 1);
                },
            },
            new()
            {
                TypeId = "filter.fromUser", Icon = "👤", Title = "From User", Subtitle = "filter",
                Category = NodeCategory.Filter,
                Description = "Branches on whether the event came from a specific nick.",
                Inputs = new[] { Ex(), Us("nick") },
                Outputs = new[] { Ex("match"), Ex("else") },
                Params = new[] { P("nick", "Nick", ParamType.Text, "", "someuser") },
                SummaryParam = "nick",
                Exec = c =>
                {
                    var u = c.InOr(1, c.Var("nick"));
                    bool hit = u.Equals(c.Param("nick"), StringComparison.OrdinalIgnoreCase);
                    c.Pulse(hit ? 0 : 1);
                },
            },
            new()
            {
                TypeId = "filter.fromAccount", Icon = "🪪", Title = "From Account", Subtitle = "ircv3",
                Category = NodeCategory.Ircv3,
                Description = "Branches on the sender's logged-in account (IRCv3 account-tag). Blank = anyone logged in.",
                Inputs = new[] { Ex() },
                Outputs = new[] { Ex("match"), Ex("else") },
                Params = new[] { P("account", "Account", ParamType.Text, "", "blank = any logged-in user") },
                SummaryParam = "account",
                Exec = c =>
                {
                    var acct = c.Var("account");
                    var want = c.Param("account");
                    bool hit = want.Length == 0 ? acct.Length > 0 : acct.Equals(want, StringComparison.OrdinalIgnoreCase);
                    c.Pulse(hit ? 0 : 1);
                },
            },
            new()
            {
                TypeId = "filter.isBot", Icon = "🤖", Title = "Is Bot", Subtitle = "ircv3",
                Category = NodeCategory.Ircv3,
                Description = "Branches on whether the sender is flagged as a bot (IRCv3 bot mode/tag).",
                Inputs = new[] { Ex() },
                Outputs = new[] { Ex("bot"), Ex("human") },
                Exec = c => c.Pulse(c.Var("isbot") == "true" ? 0 : 1),
            },
            new()
            {
                TypeId = "logic.chance", Icon = "🍀", Title = "Random Chance", Subtitle = "filter",
                Category = NodeCategory.Filter,
                Description = "Branches randomly: 'hit' with the given percent chance.",
                Inputs = new[] { Ex() },
                Outputs = new[] { Ex("hit"), Ex("miss") },
                Params = new[] { P("percent", "Percent", ParamType.Int, "50", "50") },
                SummaryParam = "percent",
                Exec = c =>
                {
                    int pct = c.ParamInt("percent", 50);
                    c.Pulse(c.Rng() * 100.0 < pct ? 0 : 1);
                },
            },

            // ============================ DATA ==============================
            new()
            {
                TypeId = "data.random", Icon = "🎲", Title = "Random Reply", Subtitle = "data",
                Category = NodeCategory.Data,
                Description = "Outputs a random line from the list (one option per line).",
                Outputs = new[] { Tx("text") },
                Params = new[] { P("options", "Options", ParamType.Multiline, "hi there!\nhello!\nhey :)", "one per line") },
                Exec = c =>
                {
                    var opts = c.Param("options").Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                    c.SetOut(0, opts.Length == 0 ? "" : opts[(int)(c.Rng() * opts.Length) % opts.Length]);
                },
            },
            new()
            {
                TypeId = "data.format", Icon = "🔤", Title = "Format Text", Subtitle = "data",
                Category = NodeCategory.Data,
                Description = "Builds a string from a template. {a}/{b} use the inputs; {nick} etc. use the event.",
                Inputs = new[] { Tx("a"), Tx("b") },
                Outputs = new[] { Tx("text") },
                Params = new[] { P("template", "Template", ParamType.Text, "{a}", "{nick}: {a}") },
                Exec = c =>
                {
                    // single pass: {a}/{b} = the inputs, any other {token} = an event var.
                    // (doing it in one pass avoids Resolve() eating {a}/{b} as unknown vars.)
                    var tmpl = c.Param("template");
                    var sb = new System.Text.StringBuilder(tmpl.Length + 16);
                    for (int i = 0; i < tmpl.Length; i++)
                    {
                        if (tmpl[i] == '{')
                        {
                            int j = tmpl.IndexOf('}', i + 1);
                            if (j > i && Core.Tokens.IsName(tmpl, i + 1, j))   // {a}/{b}/{name} only; leave literal { } alone
                            {
                                var key = tmpl.Substring(i + 1, j - i - 1);
                                sb.Append(key == "a" ? c.In(0) : key == "b" ? c.In(1) : c.Var(key));
                                i = j;
                                continue;
                            }
                        }
                        sb.Append(tmpl[i]);
                    }
                    c.SetOut(0, sb.ToString());
                },
            },

            // ============================ ACTIONS ===========================
            new()
            {
                TypeId = "action.reply", Icon = "💗", Title = "Send Reply", Subtitle = "action",
                Category = NodeCategory.Action,
                Description = "Replies in the channel/PM the triggering message came from.",
                Inputs = new[] { Ex(), Tx("message") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("message", "Message", ParamType.Multiline, "pong", "supports {nick} {args} {channel}") },
                SummaryParam = "message",
                Exec = c =>
                {
                    var text = c.InOr(1, c.Resolve(c.Param("message")));
                    if (text.Length > 0) c.Reply(text);
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "action.say", Icon = "📣", Title = "Send to Channel", Subtitle = "action",
                Category = NodeCategory.Action,
                Description = "Sends a PRIVMSG to a specific channel or nick.",
                Inputs = new[] { Ex(), Ch("channel"), Tx("message") },
                Outputs = new[] { Ex("then") },
                Params = new[]
                {
                    P("channel", "Target", ParamType.Text, "#channel", "#channel or nick"),
                    P("message", "Message", ParamType.Multiline, "", "supports {nick} {args}"),
                },
                SummaryParam = "message",
                Exec = c =>
                {
                    var ch = c.InOr(1, c.Resolve(c.Param("channel")));
                    var msg = c.InOr(2, c.Resolve(c.Param("message")));
                    if (ch.Length > 0 && msg.Length > 0) c.Send(ch, msg);
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "action.join", Icon = "🚪", Title = "Join Channel", Subtitle = "action",
                Category = NodeCategory.Action,
                Description = "Makes the bot join a channel.",
                Inputs = new[] { Ex(), Ch("channel") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("channel", "Channel", ParamType.Text, "#channel", "#channel") },
                SummaryParam = "channel",
                Exec = c =>
                {
                    var ch = c.InOr(1, c.Resolve(c.Param("channel")));
                    if (ch.Length > 0) c.Join(ch);
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "action.part", Icon = "👋", Title = "Part Channel", Subtitle = "action",
                Category = NodeCategory.Action,
                Description = "Makes the bot leave a channel. Blank channel = the triggering channel.",
                Inputs = new[] { Ex(), Ch("channel") },
                Outputs = new[] { Ex("then") },
                Params = new[]
                {
                    P("channel", "Channel", ParamType.Text, "", "blank = current"),
                    P("reason", "Reason", ParamType.Text, "", "optional"),
                },
                SummaryParam = "channel",
                Exec = c =>
                {
                    var ch = c.InOr(1, c.Resolve(c.Param("channel")));
                    if (ch.Length == 0) ch = c.Var("channel");
                    if (ch.Length > 0) c.Part(ch, c.Resolve(c.Param("reason")));
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "irc.raw", Icon = "📡", Title = "Raw IRC", Subtitle = "ircv3",
                Category = NodeCategory.Ircv3,
                Description = "Sends a raw IRC line, with up to 4 IRCv3 client-tags composed for you. Tag keys may omit the leading '+'. The line is the rest of the protocol, e.g. PRIVMSG #lobby :lmao",
                Inputs = new[] { Ex(), Tx("line") },
                Outputs = new[] { Ex("then") },
                Params = new[]
                {
                    PL("tags", "IRCv3 client tags", true, "Add tag"),
                    P("line", "Raw line", ParamType.Multiline, "", "PRIVMSG #lobby :lmao"),
                },
                SummaryParam = "line",
                Exec = c =>
                {
                    var tags = new System.Text.StringBuilder();
                    foreach (var (rk, rv) in Ircuitry.Core.ParamList.Pairs(c.Param("tags")))
                    {
                        var k = c.Resolve(rk).Trim();
                        if (k.Length == 0) continue;
                        if (k[0] != '+') k = "+" + k;            // client tags carry a leading '+'
                        var v = c.Resolve(rv);
                        if (tags.Length > 0) tags.Append(';');
                        tags.Append(k);
                        if (v.Length > 0) tags.Append('=').Append(v);
                    }
                    var line = c.InOr(1, c.Resolve(c.Param("line"))).Trim();
                    if (line.Length == 0) { c.Pulse(0); return; }
                    c.Raw((tags.Length > 0 ? "@" + tags + " " : "") + line);
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "irc.typing.start", Icon = "✍️", Title = "Start Typing", Subtitle = "ircv3",
                Category = NodeCategory.Ircv3,
                Description = "Shows an IRCv3 typing indicator (+typing=active) on the target, refreshed every few seconds until Stop Typing or the workflow ends. Great before a slow AI reply. Needs a server with message-tags.",
                Inputs = new[] { Ex(), Tx("target") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("target", "Target", ParamType.Text, "", "blank = where it came from") },
                SummaryParam = "target",
                Exec = c =>
                {
                    var t = c.InOr(1, c.Resolve(c.Param("target")));
                    if (t.Length == 0) t = c.Var("replyto");
                    if (t.Length == 0) t = c.Var("channel");
                    if (t.Length == 0) t = c.Var("nick");
                    if (t.Length > 0) c.StartTyping(t);
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "irc.typing.stop", Icon = "🛑", Title = "Stop Typing", Subtitle = "ircv3",
                Category = NodeCategory.Ircv3,
                Description = "Stops the IRCv3 typing indicator (+typing=done) on the target.",
                Inputs = new[] { Ex(), Tx("target") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("target", "Target", ParamType.Text, "", "blank = where it came from") },
                SummaryParam = "target",
                Exec = c =>
                {
                    var t = c.InOr(1, c.Resolve(c.Param("target")));
                    if (t.Length == 0) t = c.Var("replyto");
                    if (t.Length == 0) t = c.Var("channel");
                    if (t.Length == 0) t = c.Var("nick");
                    if (t.Length > 0) c.StopTyping(t);
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "action.react", Icon = "💜", Title = "Add Reaction", Subtitle = "ircv3",
                Category = NodeCategory.Ircv3,
                Description = "Reacts to the triggering message with an emoji (IRCv3 +draft/react). Needs a server that supports message tags.",
                Inputs = new[] { Ex(), Tx("emoji") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("emoji", "Emoji", ParamType.Text, "👍", "👍 ❤️ 🎉") },
                SummaryParam = "emoji",
                Exec = c => { c.React(c.InOr(1, c.Param("emoji"))); c.Pulse(0); },
            },
            new()
            {
                TypeId = "action.replythread", Icon = "🧵", Title = "Reply (threaded)", Subtitle = "ircv3",
                Category = NodeCategory.Ircv3,
                Description = "Replies threaded to the triggering message (IRCv3 +draft/reply), so clients show it as a reply.",
                Inputs = new[] { Ex(), Tx("message") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("message", "Message", ParamType.Multiline, "", "supports {nick} {args}") },
                SummaryParam = "message",
                Exec = c => { var t = c.InOr(1, c.Resolve(c.Param("message"))); if (t.Length > 0) c.ReplyThreaded(t); c.Pulse(0); },
            },
            new()
            {
                TypeId = "action.setname", Icon = "✏️", Title = "Set Name", Subtitle = "ircv3",
                Category = NodeCategory.Ircv3,
                Description = "Changes the bot's realname live (IRCv3 setname). No reconnect needed on servers that support it.",
                Inputs = new[] { Ex(), Tx("name") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("name", "Real name", ParamType.Text, "", "ircuitry • cozy bot") },
                SummaryParam = "name",
                Exec = c => { var n = c.InOr(1, c.Resolve(c.Param("name"))).Trim(); if (n.Length > 0) c.Raw("SETNAME :" + n); c.Pulse(0); },
            },
            new()
            {
                TypeId = "action.away", Icon = "🌙", Title = "Set Away", Subtitle = "ircv3",
                Category = NodeCategory.Ircv3,
                Description = "Sets or clears the bot's away status (away-notify / pre-away). Blank message = back.",
                Inputs = new[] { Ex(), Tx("message") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("message", "Away message", ParamType.Text, "", "blank = mark as back") },
                SummaryParam = "message",
                Exec = c => { var m = c.InOr(1, c.Resolve(c.Param("message"))).Trim(); c.Raw(m.Length > 0 ? "AWAY :" + m : "AWAY"); c.Pulse(0); },
            },
            new()
            {
                TypeId = "action.tagmsg", Icon = "🏷️", Title = "Send TAGMSG", Subtitle = "ircv3",
                Category = NodeCategory.Ircv3,
                Description = "Sends a tags-only message (IRCv3 TAGMSG) - client tags with no text, e.g. a reaction or typing hint.",
                Inputs = new[] { Ex(), Ch("target") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("target", "Target", ParamType.Text, "", "blank = where it came from"), PL("tags", "Client tags", true, "Add tag") },
                SummaryParam = "target",
                Exec = c =>
                {
                    var tags = new System.Text.StringBuilder();
                    foreach (var (rk, rv) in Ircuitry.Core.ParamList.Pairs(c.Param("tags")))
                    {
                        var k = c.Resolve(rk).Trim(); if (k.Length == 0) continue; if (k[0] != '+') k = "+" + k;
                        var v = c.Resolve(rv); if (tags.Length > 0) tags.Append(';'); tags.Append(k); if (v.Length > 0) tags.Append('=').Append(v);
                    }
                    var target = c.InOr(1, c.Resolve(c.Param("target"))); if (target.Length == 0) target = c.Var("replyto");
                    if (target.Length > 0 && tags.Length > 0) c.Raw("@" + tags + " TAGMSG " + target);
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "action.redact", Icon = "🩹", Title = "Redact Message", Subtitle = "draft",
                Category = NodeCategory.Ircv3,
                Description = "Deletes/hides a message by id (draft/message-redaction REDACT). Needs the target message's id (msgid).",
                Inputs = new[] { Ex(), Ch("target"), Tx("msgid") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("target", "Target", ParamType.Text, "", "blank = where it came from"), P("msgid", "Message id", ParamType.Text, "", "{msgid}"), P("reason", "Reason", ParamType.Text, "", "optional") },
                SummaryParam = "msgid",
                Exec = c =>
                {
                    var target = c.InOr(1, c.Resolve(c.Param("target"))); if (target.Length == 0) target = c.Var("replyto");
                    var mid = c.InOr(2, c.Resolve(c.Param("msgid"))).Trim();
                    var reason = c.Resolve(c.Param("reason"));
                    if (target.Length > 0 && mid.Length > 0) c.Raw("REDACT " + target + " " + mid + (reason.Length > 0 ? " :" + reason : ""));
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "action.monitor", Icon = "👀", Title = "Monitor User", Subtitle = "ircv3",
                Category = NodeCategory.Ircv3,
                Description = "Watches nicks for online/offline (IRCv3 MONITOR). Add or remove a comma-separated list.",
                Inputs = new[] { Ex(), Us("nicks") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("nicks", "Nicks", ParamType.Text, "", "alice,bob"), P("mode", "Mode", ParamType.Choice, "add", choices: new[] { "add", "remove" }) },
                SummaryParam = "nicks",
                Exec = c =>
                {
                    var nicks = c.InOr(1, c.Resolve(c.Param("nicks"))).Trim();
                    if (nicks.Length > 0) c.Raw("MONITOR " + (c.Param("mode") == "remove" ? "-" : "+") + " " + nicks);
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "action.chathistory", Icon = "📜", Title = "Request History", Subtitle = "draft",
                Category = NodeCategory.Ircv3,
                Description = "Asks the server for recent messages of a target (draft/chathistory CHATHISTORY LATEST).",
                Inputs = new[] { Ex(), Ch("target") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("target", "Target", ParamType.Text, "", "#channel or nick"), P("count", "How many", ParamType.Int, "50", "50") },
                SummaryParam = "target",
                Exec = c =>
                {
                    var target = c.InOr(1, c.Resolve(c.Param("target"))); if (target.Length == 0) target = c.Var("replyto");
                    int n = c.ParamInt("count", 50);
                    if (target.Length > 0) c.Raw($"CHATHISTORY LATEST {target} * {n}");
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "action.rename", Icon = "🔤", Title = "Rename Channel", Subtitle = "draft",
                Category = NodeCategory.Ircv3,
                Description = "Renames a channel in place (draft/channel-rename RENAME), keeping members. Needs the right privileges.",
                Inputs = new[] { Ex(), Ch("channel") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("channel", "Channel", ParamType.Text, "", "#old"), P("newname", "New name", ParamType.Text, "", "#new"), P("reason", "Reason", ParamType.Text, "", "optional") },
                SummaryParam = "newname",
                Exec = c =>
                {
                    var ch = c.Resolve(c.Param("channel")).Trim(); var nn = c.Resolve(c.Param("newname")).Trim();
                    var reason = c.Resolve(c.Param("reason"));
                    if (ch.Length > 0 && nn.Length > 0) c.Raw("RENAME " + ch + " " + nn + (reason.Length > 0 ? " :" + reason : ""));
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "action.metadata", Icon = "🗂️", Title = "Set Metadata", Subtitle = "draft",
                Category = NodeCategory.Ircv3,
                Description = "Sets a metadata key on a target (draft/metadata METADATA SET), e.g. an avatar or status URL.",
                Inputs = new[] { Ex(), Tx("value") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("target", "Target", ParamType.Text, "*", "* = self, or #chan / nick"), P("key", "Key", ParamType.Text, "", "url / avatar / ..."), P("value", "Value", ParamType.Text, "", "blank = clear the key") },
                SummaryParam = "key",
                Exec = c =>
                {
                    var target = c.Resolve(c.Param("target")).Trim(); if (target.Length == 0) target = "*";
                    var key = c.Resolve(c.Param("key")).Trim();
                    var val = c.InOr(1, c.Resolve(c.Param("value")));
                    if (key.Length > 0) c.Raw("METADATA " + target + " SET " + key + (val.Length > 0 ? " :" + val : ""));
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "action.multiline", Icon = "📃", Title = "Send Multiline", Subtitle = "draft",
                Category = NodeCategory.Ircv3,
                Description = "Sends several lines as one logical message (draft/multiline batch). One line per row in the text.",
                Inputs = new[] { Ex(), Ch("target"), Tx("text") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("target", "Target", ParamType.Text, "", "blank = where it came from"), P("text", "Text", ParamType.Multiline, "", "line one\nline two") },
                SummaryParam = "text",
                Exec = c =>
                {
                    var target = c.InOr(1, c.Resolve(c.Param("target"))); if (target.Length == 0) target = c.Var("replyto");
                    var text = c.InOr(2, c.Resolve(c.Param("text")));
                    var lines = text.Replace("\r", "").Split('\n');
                    if (target.Length == 0 || lines.Length == 0) { c.Pulse(0); return; }
                    string refid = "ml" + c.Node.Id;
                    c.Raw("BATCH +" + refid + " draft/multiline " + target);
                    foreach (var l in lines) c.Raw("@batch=" + refid + " PRIVMSG " + target + " :" + l);
                    c.Raw("BATCH -" + refid);
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "action.log", Icon = "📋", Title = "Console Log", Subtitle = "action",
                Category = NodeCategory.Action,
                Description = "Writes a line to the ircuitry event console.",
                Inputs = new[] { Ex(), Tx("text") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("text", "Text", ParamType.Text, "{nick}: {message}", "{nick}: {message}") },
                SummaryParam = "text",
                Exec = c =>
                {
                    var text = c.InOr(1, c.Resolve(c.Param("text")));
                    c.Log(text, LogLevel.Action);
                    c.Pulse(0);
                },
            },

            // ============================ AI + WEB =========================
            new()
            {
                TypeId = "ai.reply", Icon = "🤖", Title = "Ask AI", Subtitle = "ai",
                Category = NodeCategory.Action,
                Description = "Generates a reply via any OpenAI-compatible API (OpenAI, Ollama, LM Studio, OpenRouter, Groq, vLLM…). Wire AI Tool nodes into 'tools' to let the model fetch data / act. Wire 'reply' into a Send Reply.",
                Inputs = new[] { Ex(), Tx("prompt"), To("tools", multi: true) },
                Outputs = new[] { Ex("then"), Tx("reply") },
                Params = new[]
                {
                    P("baseUrl", "Base URL", ParamType.Text, "https://api.openai.com/v1", "http://localhost:11434/v1 · openrouter · groq …"),
                    P("model", "Model", ParamType.Text, "gpt-4o-mini", "gpt-4o-mini · llama3 · mistral · …"),
                    P("apiKey", "API key", ParamType.Text, "", "blank = $OPENAI_API_KEY (or none for local)", secret: true),
                    P("system", "System prompt", ParamType.Multiline, "You are a witty IRC bot named ircuitry. Reply in one short sentence.", ""),
                    P("prompt", "Prompt", ParamType.Multiline, "{nick} said: {message}", "supports {nick} {message} {args}"),
                    P("maxTokens", "Max tokens", ParamType.Int, "300", "300"),
                },
                SummaryParam = "model",
                Exec = c =>
                {
                    var prompt = c.InOr(1, c.Resolve(c.Param("prompt")));
                    string err;
                    string reply;

                    // a referenced-but-undefined {{secret.X}} would resolve to "" and the server would
                    // bounce it as "unauthorized" - name the missing secret instead of guessing.
                    var miss = new List<string>();
                    foreach (var k in new[] { "apiKey", "baseUrl", "model", "system", "prompt" })
                        foreach (var nm in Ircuitry.Core.Secrets.Missing(c.Node.GetParam(k)))
                            if (!miss.Contains(nm)) miss.Add(nm);
                    if (miss.Count > 0)
                    {
                        c.Log("AI error: secret '" + string.Join("', '", miss) + "' not defined - add it under KEYS (names are case-insensitive)", LogLevel.Error);
                        c.Pulse(0);
                        return;
                    }

                    // gather AI Tool nodes wired into the 'tools' input (pin 2)
                    var defs = new List<Ai.ToolDef>();
                    var byName = new Dictionary<string, Node>();
                    foreach (var tn in c.SourcesInto(2))
                    {
                        if (tn.TypeId != "ai.tool") continue;
                        var nm = tn.GetParam("name");
                        if (nm.Length == 0) continue;
                        var a = new List<(string, string)>();
                        for (int i = 1; i <= 3; i++) { var an = tn.GetParam("arg" + i + "name"); if (an.Length > 0) a.Add((an, tn.GetParam("arg" + i + "desc"))); }
                        defs.Add(new Ai.ToolDef(nm, tn.GetParam("description"), a));
                        byName[nm] = tn;
                    }

                    if (defs.Count == 0)
                        reply = Ai.Chat(c.Param("baseUrl"), c.Param("apiKey"), c.Param("model"), c.Resolve(c.Param("system")), prompt, c.ParamInt("maxTokens", 300), out err);
                    else
                        reply = Ai.ChatWithTools(c.Param("baseUrl"), c.Param("apiKey"), c.Param("model"), c.Resolve(c.Param("system")), prompt, c.ParamInt("maxTokens", 300), defs,
                            (name, args) =>
                            {
                                if (!byName.TryGetValue(name, out var tn)) return "(unknown tool: " + name + ")";
                                foreach (var kv in args) c.SetVar("__arg." + kv.Key, kv.Value);
                                c.SetVar("__tool_result", "");
                                c.RunNode(tn);          // runs the tool's sub-flow synchronously
                                return c.Var("__tool_result");
                            }, out err);

                    if (err.Length > 0) c.Log("AI error: " + err, LogLevel.Error);
                    else c.SetOut(1, reply);
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "ai.tool", Icon = "🧰", Title = "AI Tool", Subtitle = "ai",
                Category = NodeCategory.Logic,
                Description = "Defines a tool the AI may call. Wire 'tool' into Ask AI; wire 'call' to a sub-flow that ends in a Tool Reply. The model's arguments arrive on the arg outputs.",
                Outputs = new[] { To("tool"), Ex("call"), Tx("arg 1"), Tx("arg 2"), Tx("arg 3") },
                Params = new[]
                {
                    P("name", "Tool name", ParamType.Text, "lookup", "get_weather"),
                    P("description", "What it does", ParamType.Text, "", "tell the model when to use it"),
                    P("arg1name", "Arg 1 name", ParamType.Text, "query"), P("arg1desc", "Arg 1 desc", ParamType.Text, ""),
                    P("arg2name", "Arg 2 name", ParamType.Text, ""), P("arg2desc", "Arg 2 desc", ParamType.Text, ""),
                    P("arg3name", "Arg 3 name", ParamType.Text, ""), P("arg3desc", "Arg 3 desc", ParamType.Text, ""),
                },
                SummaryParam = "name",
                Exec = c =>
                {
                    for (int i = 1; i <= 3; i++) { var an = c.Param("arg" + i + "name"); if (an.Length > 0) c.SetOut(1 + i, c.Var("__arg." + an)); }
                    c.Pulse(1); // run the 'call' sub-flow
                },
            },
            new()
            {
                TypeId = "tool.reply", Icon = "🎁", Title = "Tool Reply", Subtitle = "ai",
                Category = NodeCategory.Logic,
                Description = "Ends an AI Tool's sub-flow: whatever you feed it is the result the model gets back.",
                Inputs = new[] { Ex(), Tx("result") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("result", "Result", ParamType.Text, "", "value returned to the AI") },
                SummaryParam = "result",
                Exec = c => { c.SetVar("__tool_result", c.InOr(1, c.Resolve(c.Param("result")))); c.Pulse(0); },
            },
            new()
            {
                TypeId = "net.http", Icon = "🌐", Title = "HTTP Request", Subtitle = "web",
                Category = NodeCategory.Action,
                Description = "Calls a web API. Outputs the response body and status. Headers are 'Key: value' lines.",
                Inputs = new[] { Ex(), Tx("url") },
                Outputs = new[] { Ex("then"), Tx("response"), Tx("status") },
                Params = new[]
                {
                    P("method", "Method", ParamType.Choice, "GET", "", new[] { "GET", "POST" }),
                    P("url", "URL", ParamType.Text, "https://", "https://api.example.com/..."),
                    P("headers", "Headers", ParamType.Multiline, "", "Authorization: Bearer ..."),
                    P("body", "Body", ParamType.Multiline, "", "{ ... } for POST"),
                },
                SummaryParam = "url",
                Exec = c =>
                {
                    var url = c.InOr(1, c.Resolve(c.Param("url")));
                    var headers = c.Resolve(c.Param("headers")).Split('\n')   // resolve so {{secret.X}} / {tokens} work in headers
                        .Select(l => l.Trim()).Where(l => l.Contains(':'))
                        .Select(l => (l[..l.IndexOf(':')].Trim(), l[(l.IndexOf(':') + 1)..].Trim()))
                        .ToArray();
                    var method = c.Param("method");
                    var body = method == "POST" ? c.Resolve(c.Param("body")) : null;
                    var (status, resp) = Http.Send(method, url, headers, body);
                    c.SetOut(1, resp);
                    c.SetOut(2, status.ToString());
                    if (status == 0) c.Log("HTTP error: " + resp, LogLevel.Error);
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "file.read", Icon = "📂", Title = "Read File", Subtitle = "file",
                Category = NodeCategory.Action,
                Description = "Reads a text file and outputs its contents. Relative paths live under ~/ircuitry/files. Branches to 'missing' if the file isn't there.",
                Inputs = new[] { Ex(), Tx("path") },
                Outputs = new[] { Ex("then"), Ex("missing"), Tx("content") },
                Params = new[] { P("path", "Path", ParamType.Text, "", "notes.txt or /abs/path.txt") },
                SummaryParam = "path",
                Exec = c =>
                {
                    var path = ResolveFile(c.InOr(1, c.Resolve(c.Param("path"))));
                    try
                    {
                        if (path.Length == 0 || !File.Exists(path)) { c.SetOut(2, ""); c.Pulse(1); return; }
                        if (TooBig(path)) { c.Log("read skipped: file exceeds 8MB", LogLevel.Error); c.SetOut(2, ""); c.Pulse(1); return; }
                        c.SetOut(2, File.ReadAllText(path));
                        c.Pulse(0);
                    }
                    catch (Exception ex) { c.Log("read failed: " + ex.Message, LogLevel.Error); c.SetOut(2, ""); c.Pulse(1); }
                },
            },
            new()
            {
                TypeId = "file.write", Icon = "💾", Title = "Write File", Subtitle = "file",
                Category = NodeCategory.Action,
                Description = "Writes (or appends) text to a file. Relative paths live under ~/ircuitry/files, which is created if missing.",
                Inputs = new[] { Ex(), Tx("text"), Tx("path") },
                Outputs = new[] { Ex("then") },
                Params = new[]
                {
                    P("path", "Path", ParamType.Text, "", "log.txt or /abs/path.txt"),
                    P("mode", "Mode", ParamType.Choice, "overwrite", "", new[] { "overwrite", "append" }),
                    P("text", "Text", ParamType.Multiline, "", "supports {nick} {message} …"),
                },
                SummaryParam = "path",
                Exec = c =>
                {
                    var path = ResolveFile(c.InOr(2, c.Resolve(c.Param("path"))));
                    if (path.Length == 0) { c.Log("write blocked: path escapes the ~/ircuitry/files sandbox", LogLevel.Error); c.Pulse(0); return; }
                    var text = c.InOr(1, c.Resolve(c.Param("text")));
                    try
                    {
                        var dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        if (c.Param("mode") == "append") File.AppendAllText(path, text + "\n");
                        else File.WriteAllText(path, text);
                        c.Pulse(0);
                    }
                    catch (Exception ex) { c.Log("write failed: " + ex.Message, LogLevel.Error); }
                },
            },
            new()
            {
                TypeId = "file.ical", Icon = "📅", Title = "Calendar (iCal)", Subtitle = "calendar",
                Category = NodeCategory.Action,
                Description = "Reads an iCalendar (.ics) feed - a file path, an http(s) URL, or pasted text - and pulls out events. Modes: next upcoming, today's, a count, or a list. Outputs summary/when/location/description; branches to 'none' when empty.",
                Inputs = new[] { Ex(), Tx("source") },
                Outputs = new[] { Ex("then"), Ex("none"), Tx("summary"), Tx("when"), Tx("location"), Tx("description"), Nm("count") },
                Params = new[]
                {
                    P("source", "Source (.ics path / URL)", ParamType.Text, "", "calendar.ics · https://… · pasted text"),
                    P("mode", "Mode", ParamType.Choice, "next", "", new[] { "next", "today", "count", "list" }),
                    P("max", "Max (list/today)", ParamType.Int, "5", "5", visibleWhen: n => n.GetParam("mode") is "list" or "today"),
                },
                SummaryParam = "mode",
                Exec = c =>
                {
                    string ics;
                    try { ics = LoadCalendar(c.InOr(1, c.Resolve(c.Param("source")))); }
                    catch (Exception ex) { c.Log("calendar load failed: " + ex.Message, LogLevel.Error); c.Pulse(1); return; }

                    var events = Ical.Parse(ics);
                    var now = DateTime.Now;
                    int max = Math.Max(1, c.ParamInt("max", 5));
                    c.SetOut(6, events.Count.ToString());

                    switch (c.Param("mode"))
                    {
                        case "today":
                        {
                            var today = Ical.OnDay(events, now);
                            c.SetOut(6, today.Count.ToString());
                            if (today.Count == 0) { c.Pulse(1); return; }
                            c.SetOut(2, string.Join(", ", today.Take(max).Select(e => e.Summary)));
                            c.SetOut(3, now.ToString("yyyy-MM-dd"));
                            var f = today[0];
                            c.SetOut(4, f.Location); c.SetOut(5, f.Description);
                            c.Pulse(0);
                            break;
                        }
                        case "count":
                            c.Pulse(0);
                            break;
                        case "list":
                        {
                            if (events.Count == 0) { c.Pulse(1); return; }
                            var ordered = events.Where(e => e.HasStart).OrderBy(e => e.Start).Take(max).ToList();
                            c.SetOut(2, string.Join(", ", ordered.Select(e => e.Summary)));
                            c.Pulse(0);
                            break;
                        }
                        default: // next
                        {
                            var e = Ical.Next(events, now);
                            if (e == null) { c.Pulse(1); return; }
                            c.SetOut(2, e.Summary);
                            c.SetOut(3, e.When);
                            c.SetOut(4, e.Location);
                            c.SetOut(5, e.Description);
                            c.Pulse(0);
                            break;
                        }
                    }
                },
            },
            new()
            {
                TypeId = "data.gettag", Icon = "🔖", Title = "Get Tag", Subtitle = "ircv3",
                Category = NodeCategory.Ircv3,
                Description = "Reads any IRCv3 message tag from the triggering message (e.g. account, time, msgid).",
                Outputs = new[] { Tx("value") },
                Params = new[] { P("name", "Tag", ParamType.Text, "account", "account · time · msgid …") },
                SummaryParam = "name",
                Exec = c => c.SetOut(0, c.Var("tag." + c.Param("name"))),
            },
            new()
            {
                TypeId = "data.json", Icon = "🧩", Title = "JSON Field", Subtitle = "data",
                Category = NodeCategory.Data,
                Description = "Extracts a value from JSON by dotted path, e.g. results.0.title.",
                Inputs = new[] { Tx("json") },
                Outputs = new[] { Tx("value") },
                Params = new[] { P("path", "Path", ParamType.Text, "", "results.0.title") },
                SummaryParam = "path",
                Exec = c => c.SetOut(0, Net.Json.Extract(c.In(0), c.Param("path"))),
            },

            // ============================ EVENTS (timer) ====================
            new()
            {
                TypeId = "event.timer", Icon = "⏰", Title = "On Timer", Subtitle = "trigger",
                Category = NodeCategory.Event, TriggerEvent = "timer",
                Description = "Fires every N seconds while the bot is connected. Pair with Send to Channel for announcements.",
                Outputs = new[] { Ex("then") },
                Params = new[] { P("seconds", "Every (seconds)", ParamType.Int, "60", "60") },
                SummaryParam = "seconds",
                Exec = c => c.Pulse(0),
            },
            new()
            {
                TypeId = "event.schedule", Icon = "📅", Title = "On Schedule", Subtitle = "trigger",
                Category = NodeCategory.Event, TriggerEvent = "schedule",
                Description = "Fires on a schedule: an interval, every day at a time, on chosen weekdays, or once at a date/time. Uses your computer's local time. Outputs {time} and {date}; pair with Send to Channel.",
                Outputs = new[] { Ex("then"), Tx("time"), Tx("date") },
                Params = new[]
                {
                    P("mode", "Mode", ParamType.Choice, "interval", "", new[] { "interval", "daily", "weekly", "once" }),
                    P("every", "Every", ParamType.Int, "5", "5", visibleWhen: n => n.GetParam("mode") == "interval"),
                    P("unit", "Unit", ParamType.Choice, "minutes", "", new[] { "seconds", "minutes", "hours", "days" }, n => n.GetParam("mode") == "interval"),
                    P("time", "Time (HH:MM)", ParamType.Text, "09:00", "09:00", visibleWhen: n => n.GetParam("mode") is "daily" or "weekly"),
                    P("days", "Days", ParamType.Text, "Mon-Fri", "Mon-Fri · Mon,Wed,Fri · 1-5 · *", visibleWhen: n => n.GetParam("mode") == "weekly"),
                    P("datetime", "Run once at", ParamType.Text, "", "2026-12-31 09:00", visibleWhen: n => n.GetParam("mode") == "once"),
                },
                SummaryParam = "mode",
                Exec = c => { c.SetOut(1, c.Var("time")); c.SetOut(2, c.Var("date")); c.Pulse(0); },
            },

            // ============================ CONDITIONS ========================
            new()
            {
                TypeId = "logic.if", Icon = "❓", Title = "If / Compare", Subtitle = "condition",
                Category = NodeCategory.Filter,
                Description = "Compares A to B and branches. A defaults to the message; B uses the input if wired, else the value field.",
                Inputs = new[] { Ex(), Tx("A"), Tx("B") },
                Outputs = new[] { Ex("true"), Ex("false") },
                Params = new[]
                {
                    P("op", "Operator", ParamType.Choice, "=", "", new[] { "=", "≠", "contains", "starts with", "ends with", ">", "<", "is empty", "matches" }),
                    P("b", "Value (B)", ParamType.Text, "", "compare against"),
                    P("ci", "Ignore case", ParamType.Bool, "true"),
                },
                SummaryParam = "op",
                Exec = c =>
                {
                    var a = c.InOr(1, c.Var("message"));
                    var b = c.InOr(2, c.Resolve(c.Param("b")));
                    var cmp = c.ParamBool("ci") ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    bool r = c.Param("op") switch
                    {
                        "=" => string.Equals(a, b, cmp),
                        "≠" => !string.Equals(a, b, cmp),
                        "contains" => b.Length == 0 || a.IndexOf(b, cmp) >= 0,
                        "starts with" => a.StartsWith(b, cmp),
                        "ends with" => a.EndsWith(b, cmp),
                        ">" => ParseNum(a) > ParseNum(b),
                        "<" => ParseNum(a) < ParseNum(b),
                        "is empty" => a.Trim().Length == 0,
                        "matches" => SafeRegex(a, b, c.ParamBool("ci")),
                        _ => false,
                    };
                    c.Pulse(r ? 0 : 1);
                },
            },
            new()
            {
                TypeId = "logic.regex", Icon = "🔎", Title = "Regex Match", Subtitle = "condition",
                Category = NodeCategory.Filter,
                Description = "Matches text against a pattern; branches and outputs capture groups $1 and $2.",
                Inputs = new[] { Ex(), Tx("text") },
                Outputs = new[] { Ex("match"), Ex("no match"), Tx("$1"), Tx("$2") },
                Params = new[] { P("pattern", "Pattern", ParamType.Text, "", "(\\d+)"), P("ci", "Ignore case", ParamType.Bool, "true") },
                SummaryParam = "pattern",
                Exec = c =>
                {
                    var text = c.InOr(1, c.Var("message"));
                    try
                    {
                        var m = Regex.Match(text, c.Param("pattern"), c.ParamBool("ci") ? RegexOptions.IgnoreCase : RegexOptions.None, TimeSpan.FromMilliseconds(250));
                        if (m.Success)
                        {
                            c.SetOut(2, m.Groups.Count > 1 ? m.Groups[1].Value : m.Value);
                            c.SetOut(3, m.Groups.Count > 2 ? m.Groups[2].Value : "");
                            c.Pulse(0);
                        }
                        else c.Pulse(1);
                    }
                    catch (Exception ex) { c.Log("regex error: " + ex.Message, LogLevel.Error); c.Pulse(1); }
                },
            },

            // ============================ LOGIC ============================
            new()
            {
                TypeId = "logic.switch", Icon = "🔀", Title = "Switch", Subtitle = "logic",
                Category = NodeCategory.Logic,
                Description = "Routes to the output whose case matches the value (else 'default'). Great for command menus.",
                Inputs = new[] { Ex(), Tx("value") },
                Outputs = new[] { Ex("1"), Ex("2"), Ex("3"), Ex("4"), Ex("default") },
                Params = new[]
                {
                    P("case1", "Case 1", ParamType.Text, ""), P("case2", "Case 2", ParamType.Text, ""),
                    P("case3", "Case 3", ParamType.Text, ""), P("case4", "Case 4", ParamType.Text, ""),
                    P("ci", "Ignore case", ParamType.Bool, "true"),
                },
                SummaryParam = "case1",
                Exec = c =>
                {
                    var v = c.InOr(1, c.Var("command").Length > 0 ? c.Var("command") : c.Var("message"));
                    var cmp = c.ParamBool("ci") ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    for (int i = 1; i <= 4; i++)
                    {
                        var cs = c.Param("case" + i);
                        if (cs.Length > 0 && string.Equals(v, cs, cmp)) { c.Pulse(i - 1); return; }
                    }
                    c.Pulse(4);
                },
            },
            new()
            {
                TypeId = "logic.cooldown", Icon = "⏳", Title = "Cooldown", Subtitle = "logic",
                Category = NodeCategory.Logic,
                Description = "Only passes once per N seconds (optionally per-user). Stops spam and rate-limits commands.",
                Inputs = new[] { Ex(), Us("user") },
                Outputs = new[] { Ex("ready"), Ex("wait") },
                Params = new[] { P("seconds", "Seconds", ParamType.Int, "10", "10"), P("perUser", "Per user", ParamType.Bool, "true") },
                SummaryParam = "seconds",
                Exec = c =>
                {
                    string who = c.ParamBool("perUser") ? c.InOr(1, c.Var("nick")) : "global";
                    string key = "cd:" + c.Node.Id + ":" + who;
                    double now = c.NowSeconds();
                    double last = double.TryParse(c.GetState(key), NumberStyles.Any, CultureInfo.InvariantCulture, out var l) ? l : 0;
                    if (now - last >= Math.Max(1, c.ParamInt("seconds", 10)))
                    {
                        c.SetState(key, now.ToString(CultureInfo.InvariantCulture));
                        c.Pulse(0);
                    }
                    else c.Pulse(1);
                },
            },
            new()
            {
                TypeId = "logic.forEach", Icon = "🔁", Title = "For Each", Subtitle = "logic",
                Category = NodeCategory.Logic,
                Description = "Splits a list and runs 'each' once per item (with item + index), then 'done'.",
                Inputs = new[] { Ex(), Tx("list") },
                Outputs = new[] { Ex("each"), Ex("done"), Tx("item"), Nm("index") },
                Params = new[] { P("sep", "Separator", ParamType.Choice, "newline", "", new[] { "newline", "comma", "space" }) },
                SummaryParam = "sep",
                Exec = c =>
                {
                    char[] seps = c.Param("sep") switch { "comma" => new[] { ',' }, "space" => new[] { ' ', '\t' }, _ => new[] { '\n' } };
                    var items = c.InOr(1, "").Split(seps, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                    for (int i = 0; i < items.Length; i++) { c.SetOut(2, items[i]); c.SetOut(3, i.ToString()); c.Run(0); }
                    c.Pulse(1);
                },
            },
            new()
            {
                TypeId = "data.setvar", Icon = "📥", Title = "Set Variable", Subtitle = "state",
                Category = NodeCategory.Logic,
                Description = "Stores a value under a name. Persists across events and saves with the bot.",
                Inputs = new[] { Ex(), Tx("value") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("name", "Name", ParamType.Text, "counter", "counter"), P("value", "Value", ParamType.Text, "", "supports {message} {nick} …") },
                SummaryParam = "name",
                Exec = c => { c.SetState(c.Param("name"), c.InOr(1, c.Resolve(c.Param("value")))); c.Pulse(0); },
            },

            // ============================ DATA (more) =======================
            new()
            {
                TypeId = "data.getvar", Icon = "📤", Title = "Get Variable", Subtitle = "state",
                Category = NodeCategory.Data,
                Description = "Reads a stored variable (or the default if it isn't set yet).",
                Outputs = new[] { Tx("value") },
                Params = new[] { P("name", "Name", ParamType.Text, "counter", "counter"), P("default", "Default", ParamType.Text, "0") },
                SummaryParam = "name",
                Exec = c => { var v = c.GetState(c.Param("name")); c.SetOut(0, v.Length > 0 ? v : c.Param("default")); },
            },
            new()
            {
                TypeId = "data.math", Icon = "🧮", Title = "Math", Subtitle = "data",
                Category = NodeCategory.Data,
                Description = "Computes A (op) B as a number. Pair with variables for counters and scores.",
                Inputs = new[] { Tx("a"), Tx("b") },
                Outputs = new[] { Tx("result") },
                Params = new[] { P("op", "Op", ParamType.Choice, "+", "", new[] { "+", "-", "×", "÷", "%", "min", "max" }), P("a", "A", ParamType.Text, "0"), P("b", "B", ParamType.Text, "1") },
                SummaryParam = "op",
                Exec = c =>
                {
                    double a = ParseNum(c.InOr(0, c.Param("a"))), b = ParseNum(c.InOr(1, c.Param("b")));
                    double r = c.Param("op") switch { "+" => a + b, "-" => a - b, "×" => a * b, "÷" => b != 0 ? a / b : 0, "%" => b != 0 ? a % b : 0, "min" => Math.Min(a, b), "max" => Math.Max(a, b), _ => a };
                    c.SetOut(0, FormatNum(r));
                },
            },
            new()
            {
                TypeId = "data.transform", Icon = "🔠", Title = "Text Transform", Subtitle = "data",
                Category = NodeCategory.Data,
                Description = "Transforms text: lower / upper / trim / length / reverse / replace.",
                Inputs = new[] { Tx("text") },
                Outputs = new[] { Tx("result") },
                Params = new[] { P("op", "Op", ParamType.Choice, "lower", "", new[] { "lower", "upper", "trim", "length", "reverse", "replace" }), P("find", "Find", ParamType.Text, ""), P("replace", "Replace", ParamType.Text, "") },
                SummaryParam = "op",
                Exec = c =>
                {
                    var t = c.In(0);
                    c.SetOut(0, c.Param("op") switch
                    {
                        "upper" => t.ToUpperInvariant(),
                        "trim" => t.Trim(),
                        "length" => t.Length.ToString(),
                        "reverse" => new string(t.Reverse().ToArray()),
                        "replace" => c.Param("find").Length > 0 ? t.Replace(c.Param("find"), c.Param("replace")) : t,
                        _ => t.ToLowerInvariant(),
                    });
                },
            },
            new()
            {
                TypeId = "data.randnum", Icon = "🎯", Title = "Random Number", Subtitle = "data",
                Category = NodeCategory.Data,
                Description = "A random whole number between min and max (inclusive). For !roll, !8ball, etc.",
                Outputs = new[] { Nm("value") },
                Params = new[] { P("min", "Min", ParamType.Int, "1", "1"), P("max", "Max", ParamType.Int, "6", "6") },
                SummaryParam = "max",
                Exec = c =>
                {
                    int lo = c.ParamInt("min", 1), hi = c.ParamInt("max", 6);
                    if (hi < lo) (lo, hi) = (hi, lo);
                    int v = lo + (int)(c.Rng() * (hi - lo + 1));
                    c.SetOut(0, Math.Min(v, hi).ToString());
                },
            },

            // ===================== IRCv3 MODERATION / CHANNEL ===============
            new()
            {
                TypeId = "irc.topic", Icon = "📌", Title = "Set Topic", Subtitle = "channel",
                Category = NodeCategory.Action,
                Description = "Sets a channel's topic (needs the right channel privileges). Blank channel = the triggering channel.",
                Inputs = new[] { Ex(), Tx("topic") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("channel", "Channel", ParamType.Text, "", "blank = current"), P("topic", "Topic", ParamType.Text, "", "supports {nick} {args}") },
                SummaryParam = "topic",
                Exec = c =>
                {
                    var chan = c.Param("channel"); if (chan.Length == 0) chan = c.Var("channel");
                    var topic = OneLine(c.InOr(1, c.Resolve(c.Param("topic"))));
                    if (chan.Length > 0) c.Raw($"TOPIC {chan} :{topic}");
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "irc.kick", Icon = "🥾", Title = "Kick User", Subtitle = "moderation",
                Category = NodeCategory.Action,
                Description = "Kicks a user from a channel (needs operator). Blank channel = the triggering channel.",
                Inputs = new[] { Ex(), Tx("nick") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("channel", "Channel", ParamType.Text, "", "blank = current"), P("nick", "Nick", ParamType.Text, "", "{nick}"), P("reason", "Reason", ParamType.Text, "", "bye!") },
                SummaryParam = "nick",
                Exec = c =>
                {
                    var chan = c.Param("channel"); if (chan.Length == 0) chan = c.Var("channel");
                    var nick = OneLine(c.InOr(1, c.Resolve(c.Param("nick"))));
                    var reason = OneLine(c.Resolve(c.Param("reason")));
                    if (chan.Length > 0 && nick.Length > 0) c.Raw($"KICK {chan} {nick} :{reason}");
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "irc.mode", Icon = "🛡️", Title = "Set Mode", Subtitle = "moderation",
                Category = NodeCategory.Action,
                Description = "Applies a channel mode, e.g. +o/-o (op), +v (voice), +b (ban). Target is a nick or mask; leave blank for channel-wide modes like +m.",
                Inputs = new[] { Ex(), Tx("target") },
                Outputs = new[] { Ex("then") },
                Params = new[]
                {
                    P("channel", "Channel", ParamType.Text, "", "blank = current"),
                    P("modes", "Modes", ParamType.Text, "+o", "+o · +v · +b · -o"),
                    P("target", "Target (nick/mask)", ParamType.Text, "", "{nick}"),
                },
                SummaryParam = "modes",
                Exec = c =>
                {
                    var chan = c.Param("channel"); if (chan.Length == 0) chan = c.Var("channel");
                    var modes = OneLine(c.Param("modes"));
                    var target = OneLine(c.InOr(1, c.Resolve(c.Param("target"))));
                    if (chan.Length > 0 && modes.Length > 0) c.Raw($"MODE {chan} {modes}{(target.Length > 0 ? " " + target : "")}");
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "irc.action", Icon = "🎭", Title = "Send Action", Subtitle = "emote",
                Category = NodeCategory.Action,
                Description = "Sends a CTCP ACTION (a /me emote), e.g. \"waves hello\". Replies to the triggering channel/user.",
                Inputs = new[] { Ex(), Tx("text") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("text", "Action text", ParamType.Text, "", "waves at {nick}") },
                SummaryParam = "text",
                Exec = c =>
                {
                    var target = c.Var("replyto"); if (target.Length == 0) target = c.Var("channel");
                    var text = OneLine(c.InOr(1, c.Resolve(c.Param("text"))));
                    if (target.Length > 0 && text.Length > 0) c.Send(target, "ACTION " + text + "");
                    c.Pulse(0);
                },
            },

            // ===================== FLOW =========================
            new()
            {
                TypeId = "flow.delay", Icon = "⏳", Title = "Delay", Subtitle = "flow",
                Category = NodeCategory.Logic,
                Description = "Waits a moment before continuing (capped at 10s so it never stalls the connection). Handy for paced multi-line replies.",
                Inputs = new[] { Ex() },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("seconds", "Seconds", ParamType.Int, "1", "1") },
                SummaryParam = "seconds",
                Exec = c =>
                {
                    int ms = Math.Clamp(c.ParamInt("seconds", 1), 0, 10) * 1000;
                    if (ms > 0) System.Threading.Thread.Sleep(ms);
                    c.Pulse(0);
                },
            },

            // ----- subflow boundary nodes (used inside a saved "reusable node") -----
            new()
            {
                TypeId = "flow.in", Icon = "⤵️", Title = "Subflow Start", Subtitle = "subflow",
                Category = NodeCategory.Logic,
                Description = "The entry point of a reusable subflow. Wire its 'then' to the subflow's body. (Only meaningful inside a saved node.)",
                Outputs = new[] { Ex("then") },
                Exec = c => c.Pulse(0),
            },
            new()
            {
                TypeId = "flow.arg", Icon = "📥", Title = "Subflow Input", Subtitle = "subflow",
                Category = NodeCategory.Logic,
                Description = "Reads one of the subflow's named inputs. The 'name' becomes an input pin on the saved node.",
                Outputs = new[] { Tx("value") },
                Params = new[] { P("name", "Input name", ParamType.Text, "input", "city") },
                SummaryParam = "name",
                Exec = c => c.SetOut(0, c.Var(c.Param("name"))),
            },
            new()
            {
                TypeId = "flow.return", Icon = "📤", Title = "Subflow Output", Subtitle = "subflow",
                Category = NodeCategory.Logic,
                Description = "Writes one of the subflow's named outputs. The 'name' becomes an output pin on the saved node.",
                Inputs = new[] { Ex(), Tx("value") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("name", "Output name", ParamType.Text, "result", "result") },
                SummaryParam = "name",
                Exec = c => { c.SetVar(c.Param("name"), c.InOr(1, "")); c.Pulse(0); },
            },

            // ===================== DATABASE (file-backed KV) ================
            new()
            {
                TypeId = "db.set", Icon = "🗃️", Title = "DB Set", Subtitle = "database",
                Category = NodeCategory.Action,
                Description = "Stores a value in a named table on disk (~/ircuitry/data). Empty value deletes the key. Survives restarts and is shared across bots.",
                Inputs = new[] { Ex(), Tx("value") },
                Outputs = new[] { Ex("then") },
                Params = new[]
                {
                    P("table", "Table", ParamType.Text, "main", "scores · seen · notes"),
                    P("key", "Key", ParamType.Text, "", "{nick}"),
                    P("value", "Value", ParamType.Text, "", "supports {tokens} · blank = delete"),
                },
                SummaryParam = "table",
                Exec = c =>
                {
                    var table = c.Resolve(c.Param("table"));
                    var key = c.Resolve(c.Param("key"));
                    if (key.Length == 0) { c.Pulse(0); return; }
                    var val = c.InOr(1, c.Resolve(c.Param("value")));
                    if (val.Length == 0) KvStore.Delete(table, key); else KvStore.Set(table, key, val);
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "db.get", Icon = "🔑", Title = "DB Get", Subtitle = "database",
                Category = NodeCategory.Data,
                Description = "Reads from a named table on disk. Modes: a key's value, the row count, or a substring search across values.",
                Outputs = new[] { Tx("value"), Nm("count") },
                Params = new[]
                {
                    P("table", "Table", ParamType.Text, "main", "scores · seen · notes"),
                    P("mode", "Mode", ParamType.Choice, "value", "", new[] { "value", "count", "find" }),
                    P("key", "Key", ParamType.Text, "", "{nick}", visibleWhen: n => n.GetParam("mode") == "value"),
                    P("default", "Default", ParamType.Text, "", "if missing", visibleWhen: n => n.GetParam("mode") == "value"),
                    P("find", "Find (substring)", ParamType.Text, "", "needle", visibleWhen: n => n.GetParam("mode") == "find"),
                },
                SummaryParam = "table",
                Exec = c =>
                {
                    var table = c.Resolve(c.Param("table"));
                    switch (c.Param("mode"))
                    {
                        case "count": c.SetOut(0, KvStore.Count(table).ToString()); c.SetOut(1, KvStore.Count(table).ToString()); break;
                        case "find":
                            var hit = KvStore.Find(table, c.Resolve(c.Param("find")));
                            c.SetOut(0, hit?.value ?? "");
                            c.SetOut(1, hit == null ? "0" : "1");
                            break;
                        default:
                            c.SetOut(0, KvStore.Get(table, c.Resolve(c.Param("key")), c.Resolve(c.Param("default"))));
                            c.SetOut(1, "1");
                            break;
                    }
                },
            },

            // ===================== AI CONTINUITY ============================
            new()
            {
                TypeId = "ai.memory", Icon = "🧠", Title = "AI Memory", Subtitle = "ai",
                Category = NodeCategory.Action,
                Description = "Per-conversation memory for Ask AI. 'recall' outputs the running transcript to feed into the prompt; 'remember' appends a turn; 'clear' wipes it. Keyed by session (default the channel), kept to the last N turns, and persisted.",
                Inputs = new[] { Ex(), Tx("text") },
                Outputs = new[] { Ex("then"), Tx("history") },
                Params = new[]
                {
                    P("session", "Session", ParamType.Text, "{channel}", "{channel} · {nick}"),
                    P("mode", "Mode", ParamType.Choice, "recall", "", new[] { "recall", "remember", "clear" }),
                    P("role", "Role", ParamType.Choice, "user", "", new[] { "user", "assistant" }, n => n.GetParam("mode") == "remember"),
                    P("text", "Text", ParamType.Multiline, "{message}", "what to remember", visibleWhen: n => n.GetParam("mode") == "remember"),
                    P("max", "Max turns", ParamType.Int, "8", "8"),
                },
                SummaryParam = "mode",
                Exec = c =>
                {
                    string skey = "aimem/" + c.Resolve(c.Param("session"));
                    string hist = c.GetState(skey);
                    int max = Math.Max(1, c.ParamInt("max", 8));
                    switch (c.Param("mode"))
                    {
                        case "clear": c.SetState(skey, ""); hist = ""; break;
                        case "remember":
                            var line = c.Param("role") + ": " + OneLine(c.InOr(1, c.Resolve(c.Param("text"))));
                            var lines = (hist.Length > 0 ? hist.Split('\n').ToList() : new List<string>());
                            lines.Add(line);
                            while (lines.Count > max) lines.RemoveAt(0);
                            hist = string.Join("\n", lines);
                            c.SetState(skey, hist);
                            break;
                    }
                    c.SetOut(1, hist);
                    c.Pulse(0);
                },
            },

            // ===================== CALENDAR (manage / search) ==============
            new()
            {
                TypeId = "cal.add", Icon = "🗓️", Title = "Add Calendar Event", Subtitle = "calendar",
                Category = NodeCategory.Action,
                Description = "Appends an event to an .ics file (created if missing). Start accepts '2026-12-31 09:00' or a date; duration is in minutes.",
                Inputs = new[] { Ex(), Tx("summary") },
                Outputs = new[] { Ex("then"), Tx("uid") },
                Params = new[]
                {
                    P("path", "Calendar file (.ics)", ParamType.Text, "calendar.ics", "calendar.ics"),
                    P("summary", "Title", ParamType.Text, "", "Team standup"),
                    P("start", "Start", ParamType.Text, "", "2026-12-31 09:00"),
                    P("duration", "Duration (min)", ParamType.Int, "60", "60"),
                    P("location", "Location", ParamType.Text, "", "optional"),
                    P("description", "Notes", ParamType.Multiline, "", "optional"),
                },
                SummaryParam = "summary",
                Exec = c =>
                {
                    var summary = c.InOr(1, c.Resolve(c.Param("summary")));
                    if (summary.Length == 0 || !DateTime.TryParse(c.Resolve(c.Param("start")), out var start))
                    { c.Log("Add Calendar Event: need a title and a valid start", LogLevel.Error); c.Pulse(0); return; }
                    var end = start.AddMinutes(Math.Max(0, c.ParamInt("duration", 60)));
                    string uid = "ircuitry-" + start.ToString("yyyyMMddHHmmss") + "-" + Math.Abs(summary.GetHashCode()).ToString("x") + "@ircuitry";
                    var vevent = Ical.FormatEvent(summary, start, end, c.Resolve(c.Param("location")), c.Resolve(c.Param("description")), uid);
                    try
                    {
                        var path = ResolveFile(c.Param("path"));
                        if (path.Length == 0) { c.Log("calendar write blocked: path escapes the sandbox", LogLevel.Error); c.Pulse(0); return; }
                        var dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        var existing = File.Exists(path) ? File.ReadAllText(path) : "";
                        File.WriteAllText(path, Ical.AppendEvent(existing, vevent));
                        c.SetOut(1, uid);
                    }
                    catch (Exception ex) { c.Log("calendar write failed: " + ex.Message, LogLevel.Error); }
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "cal.search", Icon = "🔎", Title = "Search Calendar", Subtitle = "calendar",
                Category = NodeCategory.Action,
                Description = "Searches an .ics source (file/URL/text) for events whose title, location or notes contain the query, optionally within a date range. Branches to 'none' if nothing matches.",
                Inputs = new[] { Ex(), Tx("query") },
                Outputs = new[] { Ex("then"), Ex("none"), Tx("summary"), Tx("when"), Nm("count") },
                Params = new[]
                {
                    P("source", "Source (.ics path / URL)", ParamType.Text, "calendar.ics", "calendar.ics · https://…"),
                    P("query", "Query", ParamType.Text, "", "text to find"),
                    P("from", "From (date, optional)", ParamType.Text, "", "2026-01-01"),
                    P("to", "To (date, optional)", ParamType.Text, "", "2026-12-31"),
                    P("max", "Max", ParamType.Int, "5", "5"),
                },
                SummaryParam = "query",
                Exec = c =>
                {
                    string ics;
                    try { ics = LoadCalendar(c.Resolve(c.Param("source"))); }
                    catch (Exception ex) { c.Log("calendar load failed: " + ex.Message, LogLevel.Error); c.Pulse(1); return; }

                    var q = c.InOr(1, c.Resolve(c.Param("query")));
                    bool hasFrom = DateTime.TryParse(c.Resolve(c.Param("from")), out var from);
                    bool hasTo = DateTime.TryParse(c.Resolve(c.Param("to")), out var to);
                    int max = Math.Max(1, c.ParamInt("max", 5));

                    var matches = Ical.Parse(ics).Where(e =>
                            (q.Length == 0 ||
                             e.Summary.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                             e.Location.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                             e.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
                            && (!hasFrom || !e.HasStart || e.Start >= from)
                            && (!hasTo || !e.HasStart || e.Start <= to.AddDays(1)))
                        .OrderBy(e => e.HasStart ? e.Start : DateTime.MaxValue)
                        .ToList();

                    c.SetOut(4, matches.Count.ToString());
                    if (matches.Count == 0) { c.Pulse(1); return; }
                    c.SetOut(2, string.Join(", ", matches.Take(max).Select(e => e.Summary)));
                    c.SetOut(3, matches[0].When);
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "db.sql", Icon = "🛢️", Title = "SQL Query", Subtitle = "database",
                Category = NodeCategory.Action,
                Description = "Advanced: runs raw SQL against a SQLite file (created on first use). SELECT returns rows (pipe-separated); INSERT/UPDATE/DDL return the affected count.",
                Inputs = new[] { Ex(), Tx("sql") },
                Outputs = new[] { Ex("then"), Tx("result"), Nm("rows") },
                Params = new[]
                {
                    P("file", "Database file", ParamType.Text, "data.sqlite", "data.sqlite"),
                    P("sql", "SQL", ParamType.Multiline, "", "SELECT * FROM scores ORDER BY n DESC LIMIT 5;"),
                },
                SummaryParam = "file",
                Exec = c =>
                {
                    var path = ResolveFile(c.Param("file"));
                    if (path.Length == 0) { c.Log("SQL blocked: path escapes the sandbox", LogLevel.Error); c.Pulse(0); return; }
                    var (res, rows, err) = Sql.Run(path, c.InOr(1, c.Resolve(c.Param("sql"))));
                    if (err != null) c.Log("SQL error: " + err, LogLevel.Error);
                    c.SetOut(1, res);
                    c.SetOut(2, rows.ToString());
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "code.run", Icon = "📜", Title = "Code", Subtitle = "js / python",
                Category = NodeCategory.Logic,
                Description = "Runs JavaScript (node) or Python (python3). Reads context from env vars (NICK, CHANNEL, MESSAGE, ARGS, INPUT) or JSON on stdin; whatever it prints to stdout becomes 'output'.",
                Inputs = new[] { Ex(), Tx("input") },
                Outputs = new[] { Ex("then"), Tx("output") },
                Params = new[]
                {
                    P("language", "Language", ParamType.Choice, "javascript", "", new[] { "javascript", "python" }),
                    P("code", "Code", ParamType.Multiline, "console.log('hi ' + (process.env.NICK || 'there'))", "print your result to stdout"),
                    P("timeout", "Timeout (s)", ParamType.Int, "5", "5"),
                },
                SummaryParam = "language",
                Exec = c =>
                {
                    var ctx = new Dictionary<string, string>
                    {
                        ["nick"] = c.Var("nick"), ["user"] = c.Var("user"), ["host"] = c.Var("host"),
                        ["channel"] = c.Var("channel"), ["message"] = c.Var("message"), ["args"] = c.Var("args"),
                        ["command"] = c.Var("command"), ["botnick"] = c.Var("botnick"), ["input"] = c.InOr(1, ""),
                    };
                    var (output, err) = CodeRunner.Run(c.Param("language"), c.Resolve(c.Param("code")), ctx, c.ParamInt("timeout", 5));
                    if (err != null) c.Log("code error: " + err, LogLevel.Error);
                    c.SetOut(1, output);
                    c.Pulse(0);
                },
            },
        };

        // tidy categorisation: AI + storage nodes live in their own groups regardless of where they were authored
        foreach (var d in list)
        {
            d.Category = d.TypeId switch
            {
                "ai.reply" or "ai.tool" or "tool.reply" or "ai.memory" => NodeCategory.Ai,
                "file.read" or "file.write" or "db.set" or "db.get" or "db.sql"
                    or "file.ical" or "cal.add" or "cal.search" => NodeCategory.Storage,
                _ => d.Category,
            };
            // streaming as a bot-tools step is opt-in (advanced) - off for every node by default
            d.StreamByDefault = false;
            // every node that talks to IRC gains an optional "Send via server" override: blank routes the
            // effect to the server the event came from (origin), or name one of the bot's other servers
            if (IrcSenders.Contains(d.TypeId))
            {
                var ps = new List<ParamDef>(d.Params) { P("server", "Send via server", ParamType.Text, "", "(origin)") };
                d.Params = ps.ToArray();
            }
        }

        _builtins = list;
        LoadCustom();   // merge in any installed community .ircnode files
    }

    /// <summary>Catalog grouped by category, in palette display order.</summary>
    public static IEnumerable<IGrouping<NodeCategory, NodeDef>> ByCategory()
    {
        var order = new[] { NodeCategory.Event, NodeCategory.Filter, NodeCategory.Logic, NodeCategory.Data, NodeCategory.Ai, NodeCategory.Storage, NodeCategory.Action };
        return All.GroupBy(d => d.Category).OrderBy(g => Array.IndexOf(order, g.Key));
    }
}
