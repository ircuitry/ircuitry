using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Ircuitry.Core;
using Ircuitry.Net;
using MailKit;   // for IMailFolder.AddFlags(uid, ...) extension methods used by mail.fetch

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

    /// <summary>Make a def resolvable WITHOUT writing it to disk (used by try-before-install to run a community
    /// node in a throwaway harness). Idempotent; remove again with <see cref="UnregisterTransient"/>.</summary>
    public static void RegisterTransient(NodeDef def)
    {
        if (def == null || _builtins.Any(b => b.TypeId == def.TypeId)) return;
        _byId[def.TypeId] = def;
        if (_all.All(d => d.TypeId != def.TypeId)) _all.Add(def);
    }

    /// <summary>Undo a <see cref="RegisterTransient"/> for a type that was never actually installed.</summary>
    public static void UnregisterTransient(string typeId)
    {
        if (_builtins.Any(b => b.TypeId == typeId) || _custom.Any(c => c.TypeId == typeId)) return;   // keep real ones
        _byId.Remove(typeId);
        _all.RemoveAll(d => d.TypeId == typeId);
    }

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
    public static string CustomDir => Path.Combine(Ircuitry.App.AppModel.WorkspaceDir, "nodes");

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
        "action.reply", "action.replythread", "action.say", "action.join", "action.part", "action.react", "action.reactid",
        "action.setname", "action.away", "action.tagmsg", "action.redact", "action.monitor", "action.chathistory",
        "action.rename", "action.metadata", "action.multiline",
        "irc.action", "irc.kick", "irc.mode", "irc.raw", "irc.topic", "irc.typing.start", "irc.typing.stop",
    };

    // ---- tiny builder helpers ----
    private static PinDef Ex(string n = "") => new(n, PinKind.Exec);
    private static PinDef Tx(string n) => new(n, PinKind.Text);

    /// <summary>One-line, length-capped preview of a (possibly multi-line) string, for step logs.</summary>
    private static string Brief(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    private static string JsonStr(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    /// <summary>Build an IRC ban mask from a nick + host + account at a chosen breadth (for the Ban Mask node).</summary>
    private static string BanMask(string breadth, string nick, string host, string account)
    {
        nick = nick.Trim(); host = host.Trim(); account = account.Trim();
        string Nk() => (nick.Length > 0 ? nick : "*") + "!*@*";
        string Hs() => host.Length > 0 ? "*!*@" + host : Nk();
        string Dm() => host.Length > 0 ? "*!*@" + DomainOf(host) : Nk();
        return breadth switch
        {
            "nick" => Nk(),
            "host" => Hs(),
            "domain" => Dm(),
            "account" => account.Length > 0 ? "$a:" + account : Hs(),
            _ => account.Length > 0 ? "$a:" + account : host.Length > 0 ? Dm() : Nk(),   // smart
        };
    }

    private static string DomainOf(string h)
    {
        if (h.Length == 0) return "*";
        if (Regex.IsMatch(h, @"^\d{1,3}(\.\d{1,3}){3}$")) return h[..h.LastIndexOf('.')] + ".*";   // IPv4 -> mask last octet
        var parts = h.Split('.');
        return parts.Length >= 3 ? "*." + string.Join('.', parts[^2..]) : h;                      // keep the last two labels
    }

    /// <summary>Render HTML (e.g. a ZIM article body) down to readable plain text - drop scripts/styles, turn
    /// block tags into line breaks, strip the rest, decode entities, and tidy whitespace.</summary>
    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        html = Regex.Replace(html, "(?is)<(script|style)[^>]*>.*?</\\1>", " ");
        html = Regex.Replace(html, "(?is)<br\\s*/?>", "\n");
        html = Regex.Replace(html, "(?is)</(p|div|h[1-6]|li|tr|table|ul|ol|section|article)>", "\n");
        html = Regex.Replace(html, "(?s)<[^>]+>", " ");
        html = System.Net.WebUtility.HtmlDecode(html);
        html = Regex.Replace(html, "[ \\t\\f\\v]+", " ");
        html = Regex.Replace(html, " *\\n *", "\n");
        html = Regex.Replace(html, "\\n{3,}", "\n\n");
        return html.Trim();
    }
    private static PinDef Us(string n) => new(n, PinKind.User);
    private static PinDef Ch(string n) => new(n, PinKind.Channel);
    private static PinDef Nm(string n) => new(n, PinKind.Number);
    private static PinDef To(string n, bool multi = false) => new(n, PinKind.Tool, multi);

    // An AI Tool node's argument list: the dynamic "args" list if set, else the legacy 3 fixed args (so
    // tools saved before the list still work). Returns (name, description) pairs.
    private static List<(string name, string desc)> AiToolArgs(Node n)
    {
        var list = Ircuitry.Core.ParamList.Pairs(n.GetParam("args")).Where(p => p.key.Length > 0)
            .Select(p => (p.key, p.val)).ToList();
        if (list.Count > 0) return list;
        var legacy = new List<(string, string)>();
        for (int i = 1; i <= 3; i++) { var an = n.GetParam("arg" + i + "name"); if (an.Length > 0) legacy.Add((an, n.GetParam("arg" + i + "desc"))); }
        return legacy;
    }

    // A valid function name for a node used as an AI tool: a "name" param wins, else the typeId, with
    // anything outside [A-Za-z0-9_-] replaced by '_' (so "web.search" -> "web_search").
    private static string AiToolName(Node n)
    {
        string raw = n.GetParam("name");
        if (raw.Length == 0) raw = n.TypeId;
        return new string(raw.Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_').ToArray());
    }

    /// <summary>A node-tool's description for the calling AI: a per-node 'toolDescription' param when set (so a
    /// sub-agent Ask AI can describe itself), else the node's static description.</summary>
    private static string ToolDesc(Node n) => n.GetParam("toolDescription") is { Length: > 0 } d ? d : n.Def.Description;

    /// <summary>Connect to the MCP server an ai.mcp node points at, list its tools, and add each as a ToolDef the
    /// model can call (routing back through the cached client). Shared by Ask AI and Programmer AI.</summary>
    private static void GatherMcpTools(INodeContext c, Node tn, List<Ai.ToolDef> defs,
        Dictionary<string, Ircuitry.App.Mcp.McpClient> mcpRoute,
        Dictionary<string, Node> byName, Dictionary<string, string> editorBot,
        Dictionary<string, Node> nodeTools, Dictionary<string, (NodeGraph sub, string innerId)> subTools)
    {
        try
        {
            string transport = tn.GetParam("transport"); if (transport.Length == 0) transport = "stdio";
            string target = Ircuitry.Core.Secrets.Expand((transport == "http" ? tn.GetParam("url") : tn.GetParam("command")).Trim());
            if (target.Length == 0) return;
            var hdrs = Ircuitry.Core.ParamList.Pairs(tn.GetParam("headers")).Where(p => p.key.Length > 0)
                .Select(p => (p.key, Ircuitry.Core.Secrets.Expand(p.val))).ToList();
            var client = Ircuitry.App.Mcp.McpClient.ForConfig(transport, target, hdrs, 15000);
            foreach (var mt in client.ListTools(15000))
            {
                if (mt.Name.Length == 0 || byName.ContainsKey(mt.Name) || editorBot.ContainsKey(mt.Name)
                    || nodeTools.ContainsKey(mt.Name) || subTools.ContainsKey(mt.Name) || mcpRoute.ContainsKey(mt.Name)) continue;
                object? schema = mt.Schema.ValueKind == System.Text.Json.JsonValueKind.Undefined ? null : mt.Schema;
                defs.Add(new Ai.ToolDef(mt.Name, mt.Description, new List<(string, string)>(), schema));
                mcpRoute[mt.Name] = client;
            }
        }
        catch (Exception ex) { c.Log("MCP Tools: " + ex.Message, LogLevel.Error); }
    }

    // Serializes fetched history as a JSON array with friendly lowercase keys, so {item.text}/{item.id} work
    // when wired into For Each, and an AI can read it directly.
    private static string HistoryJson(System.Collections.Generic.IReadOnlyList<Ircuitry.Core.RecentMsg> msgs)
        => System.Text.Json.JsonSerializer.Serialize(
            msgs.Select(m => new { nick = m.Nick, channel = m.Channel, text = m.Text, id = m.Msgid }));

    private static ParamDef P(string key, string label, ParamType t = ParamType.Text, string def = "", string ph = "", string[]? choices = null, Func<Node, bool>? visibleWhen = null, bool secret = false, bool file = false)
        => new() { Key = key, Label = label, Type = t, Default = def, Placeholder = ph, Choices = choices, VisibleWhen = visibleWhen, Secret = secret, File = file };

    /// <summary>A convenience for a file-path param (Browse button + drop-a-file-on-the-node support).</summary>
    private static ParamDef PF(string key, string label, string def = "", string ph = "") => P(key, label, ParamType.Text, def, ph, file: true);

    /// <summary>A growable list param: rows the user adds with an "Add" button (pair = key+value rows).</summary>
    private static ParamDef PL(string key, string label, bool pair, string addLabel)
        => new() { Key = key, Label = label, Type = ParamType.List, Pair = pair, AddLabel = addLabel };

    /// <summary>Read a code/tool node input: a wired value wins, then the AI model's argument
    /// (<c>__arg.NAME</c>), then the node's own param (token-resolved). Used by the <c>code.*</c> tool nodes.</summary>
    private static string Arg(INodeContext c, int pin, string name)
    {
        string v = c.In(pin);
        if (v.Length == 0) v = c.Var("__arg." + name);
        if (v.Length == 0) v = c.Resolve(c.Param(name));
        return v;
    }

    /// <summary>The confined codebase root for a <c>code.*</c> node - always the node's own <c>root</c> param
    /// (never an AI argument), so the model cannot widen its own sandbox.</summary>
    // Guard a cap-gated IRCv3 command: send it only if the connection negotiated one of the required caps;
    // otherwise log a clear, user-facing reason so the action doesn't silently vanish (the bug where a SETNAME
    // / REDACT / METADATA on a server that never enabled the cap just did nothing with no feedback).
    private static void CapGated(INodeContext c, string human, string line, params string[] caps)
    {
        foreach (var cap in caps) if (c.HasCap(cap)) { c.Raw(line); return; }
        c.Log(human + " skipped: this server hasn't enabled the IRCv3 \"" + caps[0] + "\" capability it needs.", Ircuitry.Core.LogLevel.System);
    }

    // Encode a Socket Send/Broadcast payload: text (UTF-8), base64 or hex, with an optional line terminator.
    private static byte[] SocketBytes(string text, string enc, string append)
    {
        byte[] body;
        try
        {
            body = enc switch
            {
                "base64" => Convert.FromBase64String(text.Trim()),
                "hex" => Convert.FromHexString(text.Trim().Replace(" ", "").Replace(":", "")),
                _ => System.Text.Encoding.UTF8.GetBytes(text),
            };
        }
        catch { body = System.Text.Encoding.UTF8.GetBytes(text); }   // malformed base64/hex -> send the literal text
        string suffix = append switch { "lf" => "\n", "crlf" => "\r\n", _ => "" };
        if (suffix.Length == 0) return body;
        var sfx = System.Text.Encoding.ASCII.GetBytes(suffix);
        var outb = new byte[body.Length + sfx.Length];
        Array.Copy(body, outb, body.Length);
        Array.Copy(sfx, 0, outb, body.Length, sfx.Length);
        return outb;
    }

    /// <summary>Parse a UI colour - "#rrggbb", "rrggbb", or "rrggbbaa" - to packed 0xRRGGBBAA. Falls back to
    /// <paramref name="def"/> when blank/malformed (so a missing colour still draws sensibly).</summary>
    private static uint UiColor(string s, uint def)
    {
        s = (s ?? "").Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        if (s.Length == 6) s += "ff";
        if (s.Length == 8 && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var v)) return v;
        return def;
    }

    private static string? NullIf(string s) => string.IsNullOrEmpty(s) ? null : s;
    private static float ParseF(string s, float def = 0f) => float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;

    private static string CodeRoot(INodeContext c) => c.Resolve(c.Param("root"));

    /// <summary>Run a code-tool body: put the result (or a tidy error) in <paramref name="resultPin"/> and
    /// pulse <paramref name="thenPin"/>. A confinement breach becomes a logged error, never an exception.</summary>
    private static void CodeRun(INodeContext c, int resultPin, int thenPin, Func<string> body)
    {
        try { c.SetOut(resultPin, body()); }
        catch (CodeAccessException ex) { c.SetOut(resultPin, "error: " + ex.Message); c.Log("code: " + ex.Message, LogLevel.Error); }
        catch (Exception ex) { c.SetOut(resultPin, "error: " + ex.Message); c.Log("code error: " + ex.Message, LogLevel.Error); }
        c.Pulse(thenPin);
    }

    /// <summary>The codebase-root param shared by every <c>code.*</c> node - the sandbox boundary.</summary>
    private static ParamDef RootParam() => P("root", "Codebase folder", ParamType.Text, "",
        "~/projects/mybot · all paths are relative to this and can't escape it");

    /// <summary>Resolve a folder param to a real absolute host path (expanding a leading ~) for a container
    /// bind-mount. Blank stays blank (no mount).</summary>
    private static string HostDir(string raw)
    {
        string s = (raw ?? "").Trim();
        if (s.Length == 0) return "";
        if (s == "~" || s.StartsWith("~/", StringComparison.Ordinal))
            s = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), s.Length <= 2 ? "" : s[2..]);
        try { return System.IO.Path.GetFullPath(s); } catch { return s; }
    }

    /// <summary>Where an accepted DCC file lands: blank -> ~/ircuitry/files/dcc/&lt;name&gt;; a folder -> that
    /// folder + the offered name; otherwise the given path IS the file. The offered name is always sanitised.</summary>
    private static string DccSavePath(string given, string offeredFile)
    {
        string file = Ircuitry.Net.Dcc.SanitizeName(offeredFile);
        given = (given ?? "").Trim();
        if (given.Length == 0) return Path.Combine(FilesDir, "dcc", file);
        if (given.StartsWith("~")) given = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + given[1..];
        string full = Path.IsPathRooted(given) ? given : Path.Combine(FilesDir, given);
        if (Directory.Exists(full) || given.EndsWith("/") || given.EndsWith("\\")) return Path.Combine(full, file);
        return full;
    }

    /// <summary>The Programmer AI's system prompt: a senior-engineer persona that never reveals the sandbox
    /// root, the zip-and-upload behind send_codebase, or any filehost details - it just knows it can edit the
    /// codebase and deliver it.</summary>
    private static string ProgrammerSystemPrompt(string extra, bool allowCmd, bool canSend)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("You are a senior software engineer working inside a project's codebase. ");
        sb.Append("You can read, search and edit any file in the project; every path you use is relative to the project root. ");
        sb.Append("Work methodically: first explore (project_tree, list_dir, search_code) to learn the layout, read the files you need, ");
        sb.Append("then make focused changes with edit_file, or write_file for new files. Read a file before you edit it - never invent its contents. ");
        if (allowCmd) sb.Append("You can run build, test and lint commands with run_command to verify your work; they execute in an isolated container that has only this project mounted, may be offline, and starts fresh each call (state outside the project does not persist between commands). ");
        if (canSend) sb.Append("When the task is complete and your changes are in place, call send_codebase to deliver the finished project, then tell the user what you changed and include the link it returns. ");
        sb.Append("Keep going until the task is genuinely done; do not ask permission for routine edits.");
        if (extra.Trim().Length > 0) { sb.Append("\n\nAdditional instructions:\n").Append(extra.Trim()); }
        return sb.ToString();
    }

    /// <summary>Pull a shareable link out of a filehost's response - a bare URL, or a JSON body with a
    /// url/link field (covers 0x0.st, catbox, transfer.sh, imgur-style hosts). Falls back to the raw body.</summary>
    private static string ExtractUrl(string body)
    {
        body = (body ?? "").Trim();
        if (body.Length == 0) return "(no response)";
        if (body.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || body.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return body.Split('\n', ' ', '\t')[0].Trim();
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(body);
            foreach (var key in new[] { "url", "link", "href", "downloadUrl" })
                if (d.RootElement.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String) return v.GetString() ?? body;
            if (d.RootElement.TryGetProperty("data", out var data) && data.ValueKind == System.Text.Json.JsonValueKind.Object)
                foreach (var key in new[] { "link", "url" })
                    if (data.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String) return v.GetString() ?? body;
        }
        catch { /* not JSON */ }
        return body;
    }

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
        if (Directory.Exists(file))   // a whole folder of .ics files -> merge them
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

    // XML -> JSON for the data.xml primitive: repeated tags become arrays; attributes become @name keys.
    private static string XmlToJson(string xml)
    {
        var root = System.Xml.Linq.XDocument.Parse(xml).Root;
        if (root == null) return "{}";
        var top = new Dictionary<string, object?> { [root.Name.LocalName] = XmlElem(root) };
        return System.Text.Json.JsonSerializer.Serialize(top);
    }
    private static object? XmlElem(System.Xml.Linq.XElement e)
    {
        if (!e.HasElements && !e.HasAttributes) return e.Value;
        var d = new Dictionary<string, object?>();
        foreach (var a in e.Attributes()) d["@" + a.Name.LocalName] = a.Value;
        foreach (var g in e.Elements().GroupBy(x => x.Name.LocalName))
        {
            var items = g.Select(XmlElem).ToList();
            d[g.Key] = items.Count == 1 ? items[0] : items;
        }
        if (!e.HasElements && e.HasAttributes) d["#text"] = e.Value;
        return d;
    }

    static NodeCatalog()
    {
        var list = new List<NodeDef>
        {
            // ============================ EVENTS ============================
            new()
            {
                TypeId = "event.connect", Icon = "plug", Title = "On Connect", Subtitle = "trigger",
                Category = NodeCategory.Event, TriggerEvent = "connect",
                Description = "Fires once when the bot finishes registering with the server.",
                Outputs = new[] { Ex("then") },
                Exec = c => c.Pulse(0),
            },
            new()
            {
                TypeId = "event.start", Icon = "play", Title = "On Start", Subtitle = "trigger",
                Category = NodeCategory.Event, TriggerEvent = "start",
                Description = "Fires once the moment the bot starts running, before and independent of any IRC connection - the place to stand up listeners (Socket Listen), load state, or do one-time setup. Works even on a bot with NO IRC server configured (a pure socket/server bot, e.g. an in-graph IRC server).",
                Outputs = new[] { Ex("then") },
                Exec = c => c.Pulse(0),
            },
            new()
            {
                TypeId = "event.signal", Icon = "envelope", Title = "On Signal", Subtitle = "trigger",
                Category = NodeCategory.Event, TriggerEvent = "signal",
                Description = "Fires when another part of your bot emits a matching signal (via Emit Signal). Lets one workflow trigger another, or run a shared flow from several places. Carries optional {data}.",
                Outputs = new[] { Ex("then"), Tx("data") },
                Params = new[] { P("signal", "Signal name", ParamType.Text, "my-signal", "my-signal") },
                SummaryParam = "signal",
                Exec = c => { c.SetOut(1, c.Var("__signaldata")); c.Pulse(0); },
            },
            new()
            {
                TypeId = "action.signal", Icon = "megaphone", Title = "Emit Signal", Subtitle = "flow",
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
                TypeId = "event.message", Icon = "chat-circle", Title = "On Message", Subtitle = "trigger",
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
                TypeId = "event.command", Icon = "lightning", Title = "On Command", Subtitle = "trigger",
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
                TypeId = "event.join", Icon = "hand-waving", Title = "On Join", Subtitle = "trigger",
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
            new()
            {
                TypeId = "event.part", Icon = "sign-out", Title = "On Part", Subtitle = "trigger",
                Category = NodeCategory.Event, TriggerEvent = "part",
                Description = "Fires when a user leaves a channel the bot is in. Outputs who left, the channel, and the part reason.",
                Outputs = new[] { Ex("then"), Us("nick"), Ch("channel"), Tx("reason") },
                Exec = c => { c.SetOut(1, c.Var("nick")); c.SetOut(2, c.Var("channel")); c.SetOut(3, c.Var("reason")); c.Pulse(0); },
            },
            new()
            {
                TypeId = "event.quit", Icon = "power", Title = "On Quit", Subtitle = "trigger",
                Category = NodeCategory.Event, TriggerEvent = "quit",
                Description = "Fires when a user quits IRC (disconnects). Outputs who quit and the quit reason - good for seen-tracking or cleanup.",
                Outputs = new[] { Ex("then"), Us("nick"), Tx("reason") },
                Exec = c => { c.SetOut(1, c.Var("nick")); c.SetOut(2, c.Var("reason")); c.Pulse(0); },
            },
            new()
            {
                TypeId = "event.kick", Icon = "boot", Title = "On Kick", Subtitle = "trigger",
                Category = NodeCategory.Event, TriggerEvent = "kick",
                Description = "Fires when someone is kicked from a channel. Outputs who was kicked, who did it, the channel and the reason. Compare 'kicked' to the bot's nick to auto-rejoin.",
                Outputs = new[] { Ex("then"), Us("kicked"), Us("by"), Ch("channel"), Tx("reason") },
                Exec = c => { c.SetOut(1, c.Var("kicked")); c.SetOut(2, c.Var("nick")); c.SetOut(3, c.Var("channel")); c.SetOut(4, c.Var("reason")); c.Pulse(0); },
            },
            new()
            {
                TypeId = "event.nick", Icon = "user-switch", Title = "On Nick Change", Subtitle = "trigger",
                Category = NodeCategory.Event, TriggerEvent = "nick",
                Description = "Fires when a user changes their nick. Outputs the old and new nick.",
                Outputs = new[] { Ex("then"), Us("old"), Us("new") },
                Exec = c => { c.SetOut(1, c.Var("oldnick")); c.SetOut(2, c.Var("newnick")); c.Pulse(0); },
            },
            new()
            {
                TypeId = "event.mode", Icon = "sliders-horizontal", Title = "On Mode", Subtitle = "trigger",
                Category = NodeCategory.Event, TriggerEvent = "mode",
                Description = "Fires when a channel (or user) mode is set. Outputs who set it, the target channel, the mode string (e.g. +o-v) and its arguments - use it to detect (de)ops or bans.",
                Outputs = new[] { Ex("then"), Us("by"), Ch("channel"), Tx("modes"), Tx("args") },
                Exec = c => { c.SetOut(1, c.Var("nick")); c.SetOut(2, c.Var("channel")); c.SetOut(3, c.Var("modes")); c.SetOut(4, c.Var("args")); c.Pulse(0); },
            },
            new()
            {
                TypeId = "event.invite", Icon = "envelope-open", Title = "On Invite", Subtitle = "trigger",
                Category = NodeCategory.Event, TriggerEvent = "invite",
                Description = "Fires when the bot is invited to a channel. Outputs who invited it and the channel. Wire into Join (gated on a trusted nick) to accept invites.",
                Outputs = new[] { Ex("then"), Us("by"), Ch("channel") },
                Exec = c => { c.SetOut(1, c.Var("nick")); c.SetOut(2, c.Var("channel")); c.Pulse(0); },
            },
            new()
            {
                TypeId = "irc.banmask", Icon = "shield-slash", Title = "Ban Mask", Subtitle = "irc", Category = NodeCategory.Ircv3,
                Description = "Builds a sensible IRC ban mask from a nick + their host/account at a chosen breadth (nick, host, domain, or $a:account extban). 'smart' uses the account if known, else a domain mask, else the nick. host/account default to the triggering event's values; feed the mask into Temp Ban or a Raw MODE +b.",
                Inputs = new[] { Us("nick"), Tx("host"), Tx("account") },
                Outputs = new[] { Tx("mask") },
                Params = new[] { P("breadth", "Breadth", ParamType.Choice, "smart", "", new[] { "smart", "nick", "host", "domain", "account" }) },
                SummaryParam = "breadth",
                Exec = c => c.SetOut(0, BanMask(c.Param("breadth"), c.InOr(0, c.Var("nick")), c.InOr(1, c.Var("host")), c.InOr(2, c.Var("account")))),
            },
            new()
            {
                TypeId = "irc.tempban", Icon = "timer", Title = "Temp Ban", Subtitle = "irc", Category = NodeCategory.Action,
                Description = "Sets a channel ban (+b mask) and automatically lifts it after N minutes. The pending ban is stored in the bot's state, so it is still lifted after a restart. Wire a Ban Mask into 'mask'.",
                Inputs = new[] { Ex(), Ch("channel"), Tx("mask") },
                Outputs = new[] { Ex("then") },
                Params = new[]
                {
                    P("channel", "Channel", ParamType.Text, "", "#room (blank = the triggering channel)"),
                    P("minutes", "Lift after (minutes)", ParamType.Int, "30", "30"),
                },
                SummaryParam = "minutes",
                Exec = c =>
                {
                    string channel = c.InOr(1, c.Resolve(c.Param("channel"))); if (channel.Length == 0) channel = c.Var("channel");
                    string mask = c.InOr(2, "");
                    if (channel.Length == 0 || mask.Length == 0) { c.Log("Temp Ban: need a channel and a mask", LogLevel.Error); c.Pulse(0); return; }
                    int mins = Math.Max(1, c.ParamInt("minutes", 30));
                    c.Raw($"MODE {channel} +b {mask}");
                    long exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + mins * 60L;
                    c.SetState("__tempban|" + channel + "\u0001" + mask, exp.ToString());
                    c.Log($"Temp Ban: +b {mask} on {channel} for {mins}m", LogLevel.Action);
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "irc.floodbudget", Icon = "gauge", Title = "Flood Budget", Subtitle = "irc", Category = NodeCategory.Action,
                Description = "Tunes how fast the bot may send to this server before its flood throttle kicks in. Safe is gentle (avoids an Excess Flood kill on strict servers), Aggressive is faster but burstier. Wire to On Connect; watch the send gauge in the status bar.",
                Inputs = new[] { Ex() },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("level", "Budget", ParamType.Choice, "Normal", "", new[] { "Safe", "Normal", "Aggressive" }) },
                SummaryParam = "level",
                Exec = c =>
                {
                    var (burst, interval) = c.Param("level") switch { "Safe" => (3, 1.3), "Aggressive" => (8, 0.4), _ => (5, 0.7) };
                    c.SetFloodBudget(burst, interval);
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "irc.filehost", Icon = "upload-simple", Title = "Upload to Filehost", Subtitle = "ircv3", Category = NodeCategory.Ircv3,
                Description = "Uploads a local file to the server's IRCv3 draft/FILEHOST endpoint (if the server advertises one) and outputs a shareable link - the standard way to post a file on networks that host them. Use {filehost} elsewhere to check/get the URL. Branches to 'no host' when the server doesn't offer one.",
                Inputs = new[] { Ex(), Tx("file") },
                Outputs = new[] { Ex("then"), Tx("link"), Ex("no host") },
                Params = new[] { P("file", "File path", ParamType.Text, "", "an absolute path, or one under ~/ircuitry/files") },
                SummaryParam = "file",
                Exec = c =>
                {
                    string path = c.InOr(1, c.Resolve(c.Param("file"))).Trim();
                    if (path.Length == 0) { c.Log("Upload to Filehost: no file path given", LogLevel.Error); c.SetOut(1, ""); c.Pulse(2); return; }
                    var (ok, res) = c.FilehostUpload(path);
                    if (ok) { c.SetOut(1, res); c.Pulse(0); }
                    else { c.Log("Upload to Filehost: " + res, LogLevel.System); c.SetOut(1, ""); c.Pulse(2); }
                },
            },
            new()
            {
                TypeId = "irc.hasfilehost", Icon = "cloud-check", Title = "Has Filehost?", Subtitle = "ircv3", Category = NodeCategory.Filter,
                Description = "Branches on whether the bot's server advertises an IRCv3 draft/FILEHOST endpoint - so a workflow can choose the server filehost when present and an external one otherwise. Also outputs the URL (same as {filehost}).",
                Inputs = new[] { Ex() },
                Outputs = new[] { Ex("yes"), Ex("no"), Tx("url") },
                SummaryParam = null,
                Exec = c =>
                {
                    string url = c.IrcInfo("filehost", "");
                    c.SetOut(2, url);
                    c.Pulse(url.Length > 0 ? 0 : 1);
                },
            },
            new()
            {
                TypeId = "event.numeric", Icon = "hash", Title = "On Numeric", Subtitle = "trigger",
                Category = NodeCategory.Event, TriggerEvent = "numeric",
                Description = "Fires when the server sends a numeric reply you pick from the list (e.g. RPL_WELCOME, ERR_NICKNAMEINUSE, RPL_INVITING). Exposes {numeric} {numname} {message} {channel} and {arg1}, {arg2}, …",
                Outputs = new[] { Ex("then"), Tx("numeric"), Tx("text"), Ch("channel") },
                Params = new[] { P("which", "Numeric", ParamType.Choice, "(any numeric)", "", Ircuitry.Irc.IrcNumerics.Choices()) },
                SummaryParam = "which",
                Exec = c =>
                {
                    string sel = c.Param("which");
                    bool match = sel.StartsWith("(any");
                    if (!match)
                    {
                        int sp = sel.IndexOf(' ');
                        string codeStr = sp > 0 ? sel[..sp] : sel;
                        match = int.TryParse(codeStr, out int selN) && int.TryParse(c.Var("numeric"), out int gotN) && selN == gotN;
                    }
                    if (!match) return;
                    c.SetOut(1, c.Var("numeric"));
                    c.SetOut(2, c.Var("message"));
                    c.SetOut(3, c.Var("channel"));
                    c.Pulse(0);
                },
            },

            // ============================ FILTERS ===========================
            new()
            {
                TypeId = "filter.contains", Icon = "magnifying-glass", Title = "Text Contains", Subtitle = "filter",
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
                TypeId = "filter.fromUser", Icon = "user", Title = "From User", Subtitle = "filter",
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
                TypeId = "filter.fromAccount", Icon = "identification-card", Title = "From Account", Subtitle = "ircv3",
                Category = NodeCategory.Ircv3,
                Description = "Branches on the sender's authenticated account (IRCv3 account-tag) - a safe allow-list, since accounts can't be faked the way nicks can. Blank = anyone logged in; one account, or a comma-separated allow-list.",
                Inputs = new[] { Ex() },
                Outputs = new[] { Ex("match"), Ex("else") },
                Params = new[] { P("account", "Account(s)", ParamType.Text, "", "blank = any logged-in · or alice · or alice, bob, carol") },
                SummaryParam = "account",
                Exec = c =>
                {
                    var acct = c.Var("account");
                    var want = c.Resolve(c.Param("account")).Trim();
                    bool hit = want.Length == 0
                        ? acct.Length > 0
                        : acct.Length > 0 && want.Split(',').Select(s => s.Trim()).Any(a => a.Length > 0 && a.Equals(acct, StringComparison.OrdinalIgnoreCase));
                    c.Pulse(hit ? 0 : 1);
                },
            },
            new()
            {
                TypeId = "filter.isBot", Icon = "robot", Title = "Is Bot", Subtitle = "ircv3",
                Category = NodeCategory.Ircv3,
                Description = "Branches on whether the sender is flagged as a bot (IRCv3 bot mode/tag).",
                Inputs = new[] { Ex() },
                Outputs = new[] { Ex("bot"), Ex("human") },
                Exec = c => c.Pulse(c.Var("isbot") == "true" ? 0 : 1),
            },
            new()
            {
                TypeId = "logic.chance", Icon = "clover", Title = "Random Chance", Subtitle = "filter",
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
                TypeId = "data.random", Icon = "dice-five", Title = "Random Reply", Subtitle = "data",
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
                TypeId = "data.format", Icon = "translate", Title = "Format Text", Subtitle = "data",
                Category = NodeCategory.Data,
                Description = "Builds a string from a template. {a}/{b} use the inputs; {nick} etc. use the event.",
                Inputs = new[] { Tx("a"), Tx("b") },
                Outputs = new[] { Tx("text") },
                Params = new[] { P("template", "Template", ParamType.Text, "{a}", "{nick}: {a}") },
                Exec = c =>
                {
                    // single pass: {a}/{b} = the inputs; any other {token} goes through the FULL resolver
                    // (event vars, computed tokens like {unixtime}, aliases like {me}, {arg.NAME}, and stored
                    // Set Var state). Handling {a}/{b} here avoids Resolve() eating them as unknown vars.
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
                                sb.Append(key == "a" ? c.In(0) : key == "b" ? c.In(1) : c.Resolve("{" + key + "}"));
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
                TypeId = "action.reply", Icon = "heart", Title = "Send Reply", Subtitle = "action",
                Category = NodeCategory.Action,
                Description = "Replies in the channel/PM the triggering message came from.",
                Inputs = new[] { Ex(), Tx("message") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("message", "Message", ParamType.Multiline, "pong", "supports {me} {nick} {args} {arg1} {channel} {time} …") },
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
                TypeId = "action.say", Icon = "megaphone", Title = "Send to Channel", Subtitle = "action",
                Category = NodeCategory.Action,
                Description = "Sends a PRIVMSG to a specific channel or nick.",
                Inputs = new[] { Ex(), Ch("channel"), Tx("message") },
                Outputs = new[] { Ex("then") },
                Params = new[]
                {
                    P("channel", "Target", ParamType.Text, "#channel", "#channel or nick"),
                    P("message", "Message", ParamType.Multiline, "", "supports {me} {nick} {args} {arg1} …"),
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
                TypeId = "action.join", Icon = "door", Title = "Join Channel", Subtitle = "action",
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
                TypeId = "action.part", Icon = "hand-waving", Title = "Part Channel", Subtitle = "action",
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
                TypeId = "irc.raw", Icon = "broadcast", Title = "Raw IRC", Subtitle = "ircv3",
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
                TypeId = "irc.typing.start", Icon = "pencil-line", Title = "Start Typing", Subtitle = "ircv3",
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
                TypeId = "irc.typing.stop", Icon = "octagon", Title = "Stop Typing", Subtitle = "ircv3",
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
                TypeId = "action.react", Icon = "heart", Title = "Add Reaction", Subtitle = "ircv3",
                Category = NodeCategory.Ircv3,
                Description = "Reacts to the triggering message with an emoji (IRCv3 +draft/react). Needs a server that supports message tags.",
                Inputs = new[] { Ex(), Tx("emoji") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("emoji", "Emoji", ParamType.Text, "\U0001F44D", "\U0001F44D \u2764\uFE0F \U0001F389") },   // intentional unicode (thumbs-up; heart; party popper)
                SummaryParam = "emoji",
                Exec = c => { c.React(c.InOr(1, c.Param("emoji"))); c.Pulse(0); },
            },
            new()
            {
                TypeId = "action.replythread", Icon = "needle", Title = "Reply (threaded)", Subtitle = "ircv3",
                Category = NodeCategory.Ircv3,
                Description = "Replies threaded to the triggering message (IRCv3 +draft/reply), so clients show it as a reply.",
                Inputs = new[] { Ex(), Tx("message") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("message", "Message", ParamType.Multiline, "", "supports {me} {nick} {args} {arg1} …") },
                SummaryParam = "message",
                Exec = c => { var t = c.InOr(1, c.Resolve(c.Param("message"))); if (t.Length > 0) c.ReplyThreaded(t); c.Pulse(0); },
            },
            new()
            {
                TypeId = "action.setname", Icon = "pencil", Title = "Set Name", Subtitle = "ircv3",
                Category = NodeCategory.Ircv3,
                Description = "Changes the bot's realname live (IRCv3 setname). No reconnect needed on servers that support it.",
                Inputs = new[] { Ex(), Tx("name") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("name", "Real name", ParamType.Text, "", "ircuitry • cozy bot") },
                SummaryParam = "name",
                Exec = c => { var n = c.InOr(1, c.Resolve(c.Param("name"))).Trim(); if (n.Length > 0) CapGated(c, "Set Name (SETNAME)", "SETNAME :" + n, "setname"); c.Pulse(0); },
            },
            new()
            {
                TypeId = "action.away", Icon = "moon", Title = "Set Away", Subtitle = "ircv3",
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
                TypeId = "action.tagmsg", Icon = "tag", Title = "Send TAGMSG", Subtitle = "ircv3",
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
                TypeId = "action.redact", Icon = "bandaids", Title = "Redact Message", Subtitle = "draft",
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
                    if (target.Length > 0 && mid.Length > 0) CapGated(c, "Redact (REDACT)", "REDACT " + target + " " + mid + (reason.Length > 0 ? " :" + reason : ""), "draft/message-redaction", "message-redaction");
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "action.monitor", Icon = "eyes", Title = "Monitor User", Subtitle = "ircv3",
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
                TypeId = "action.chathistory", Icon = "scroll", Title = "Request History", Subtitle = "ircv3",
                Category = NodeCategory.Ircv3,
                Description = "Fetches a target's recent server history (IRCv3 CHATHISTORY), INCLUDING messages from before the bot joined. The history batch never triggers your message/join nodes - it just hands the messages back as JSON on 'messages'. Wire that into For Each, or into Ask AI as a tool. Each item has nick, channel, text and id.",
                Inputs = new[] { Ex(), Ch("target") },
                Outputs = new[] { Ex("then"), Tx("messages"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "fetch_history", "fetch_history"),
                    P("target", "Target", ParamType.Text, "", "#channel or nick (blank = where this ran)"),
                    P("count", "How many", ParamType.Int, "50", "50"),
                    P("timeout", "Wait up to (seconds)", ParamType.Int, "8", "8"),
                },
                SummaryParam = "target",
                Exec = c =>
                {
                    string target = c.In(1);
                    if (target.Length == 0) target = c.Var("__arg.target");        // when called as an AI tool
                    if (target.Length == 0) target = c.Resolve(c.Param("target"));
                    if (target.Length == 0) target = c.Var("replyto");
                    if (target.Length == 0) target = c.Var("channel");
                    int n = c.ParamInt("count", 50);
                    int to = Math.Clamp(c.ParamInt("timeout", 8), 1, 30) * 1000;
                    var msgs = target.Length > 0 ? c.History(target, "LATEST", n, to)
                                                 : (System.Collections.Generic.IReadOnlyList<Ircuitry.Core.RecentMsg>)System.Array.Empty<Ircuitry.Core.RecentMsg>();
                    c.SetOut(1, HistoryJson(msgs));
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "irc.me", Icon = "identification-card", Title = "My Info", Subtitle = "ircv3",
                Category = NodeCategory.Ircv3,
                Description = "Reads the bot's own live, tracked state: its nick, the network it's connected to, the channels it's in (comma-separated), its enabled IRCv3 caps, or what the server supports (ISUPPORT: PREFIX, CHANTYPES, CASEMAPPING, NICKLEN...). Wire 'value' anywhere.",
                Outputs = new[] { Tx("value") },
                Params = new[] { P("field", "What", ParamType.Choice, "nick", "", new[] { "nick", "network", "channels", "caps", "isupport", "prefix", "chantypes", "casemapping" }) },
                SummaryParam = "field",
                Exec = c => c.SetOut(0, c.IrcInfo(c.Param("field"), "")),
            },
            new()
            {
                TypeId = "irc.channel", Icon = "clipboard", Title = "Channel Info", Subtitle = "ircv3",
                Category = NodeCategory.Ircv3,
                Description = "Reads live, tracked state about a channel the bot is in: its topic, member list (comma-separated, with @/+ prefixes), member count, or whether the bot is in it. Wire 'value' anywhere.",
                Inputs = new[] { Ch("channel") },
                Outputs = new[] { Tx("value") },
                Params = new[]
                {
                    P("channel", "Channel", ParamType.Text, "", "#channel (blank = where this ran)"),
                    P("field", "What", ParamType.Choice, "topic", "", new[] { "topic", "members", "count", "joined" }),
                },
                SummaryParam = "field",
                Exec = c =>
                {
                    string ch = c.InOr(0, c.Resolve(c.Param("channel")));
                    if (ch.Length == 0) ch = c.Var("channel");
                    c.SetOut(0, c.IrcInfo(c.Param("field"), ch));
                },
            },
            new()
            {
                TypeId = "action.rename", Icon = "translate", Title = "Rename Channel", Subtitle = "draft",
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
                    if (ch.Length > 0 && nn.Length > 0) CapGated(c, "Rename Channel (RENAME)", "RENAME " + ch + " " + nn + (reason.Length > 0 ? " :" + reason : ""), "draft/channel-rename");
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "action.metadata", Icon = "folders", Title = "Set Metadata", Subtitle = "draft",
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
                    if (key.Length > 0) CapGated(c, "Set Metadata (METADATA)", "METADATA " + target + " SET " + key + (val.Length > 0 ? " :" + val : ""), "draft/metadata-2", "metadata", "draft/metadata");
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "irc.metadata", Icon = "folder-open", Title = "Get Metadata", Subtitle = "draft",
                Category = NodeCategory.Ircv3,
                Description = "Reads a metadata key off a target (IRCv3 draft/metadata-2 METADATA GET) - someone's avatar, status, homepage URL, and so on. Blocks until the server replies, correlating the answer with labeled-response when the server offers it (otherwise the metadata numerics). The read-side mirror of Set Metadata: wire 'value' anywhere, or hand it to Ask AI as a tool so the bot can look people up.",
                Inputs = new[] { Ex(), Tx("target"), Tx("key") },
                Outputs = new[] { Ex("then"), Tx("value"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "get_metadata", "get_metadata"),
                    P("target", "Target", ParamType.Text, "*", "* = self, or #chan / nick"),
                    P("key", "Key", ParamType.Text, "", "url / avatar / status / ..."),
                    P("timeout", "Wait up to (seconds)", ParamType.Int, "6", "6"),
                },
                SummaryParam = "key",
                Exec = c =>
                {
                    string target = c.In(1);
                    if (target.Length == 0) target = c.Var("__arg.target");      // when called as an AI tool
                    if (target.Length == 0) target = c.Resolve(c.Param("target"));
                    if (target.Length == 0) target = "*";
                    string key = c.In(2);
                    if (key.Length == 0) key = c.Var("__arg.key");
                    if (key.Length == 0) key = c.Resolve(c.Param("key"));
                    int to = Math.Clamp(c.ParamInt("timeout", 6), 1, 30) * 1000;
                    c.SetOut(1, key.Length > 0 ? c.MetadataGet(target, key, to) : "");
                    c.Pulse(0);
                },
            },

            // ===================== SOCKETS - general-purpose TCP / UDP / WebSocket =====================
            new()
            {
                TypeId = "socket.listen", Icon = "broadcast", Title = "Socket Listen", Subtitle = "socket", Category = NodeCategory.Action,
                Description = "Starts a server on a port - TCP, UDP or WebSocket, with optional TLS and a framing mode (line / raw / a custom delimiter / length-prefixed). Each client that connects fires On Socket Connect; incoming data fires On Socket Data carrying the connection id. Outputs the listener id - use it with Socket Broadcast or Socket Close. This is the engine you'd build an in-graph server (even an IRC server) on.",
                Inputs = new[] { Ex() },
                Outputs = new[] { Ex("then"), Tx("listener") },
                Params = new[]
                {
                    P("proto", "Protocol", ParamType.Choice, "tcp", "", new[] { "tcp", "udp", "ws" }),
                    P("port", "Port", ParamType.Int, "0", "6667"),
                    P("tls", "TLS", ParamType.Bool, "false"),
                    P("framing", "Framing", ParamType.Choice, "line", "", new[] { "line", "raw", "delimiter", "length" }),
                    P("delimiter", "Delimiter", ParamType.Text, "", "for delimiter framing", visibleWhen: n => n.GetParam("framing") == "delimiter"),
                    P("cert", "TLS cert (blank = auto)", ParamType.Text, "", "PEM/PFX path", file: true, visibleWhen: n => n.GetParam("tls") == "true"),
                    P("certpass", "TLS cert passphrase", ParamType.Text, "", "", secret: true, visibleWhen: n => n.GetParam("tls") == "true"),
                },
                SummaryParam = "port",
                Exec = c =>
                {
                    string id = c.SocketListen(c.Param("proto"), c.ParamInt("port", 0), c.Param("framing"), c.Resolve(c.Param("delimiter")), c.ParamBool("tls"), c.Resolve(c.Param("cert")), Ircuitry.Core.Secrets.Expand(c.Param("certpass")));
                    c.SetOut(1, id);
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "socket.connect", Icon = "plugs", Title = "Socket Connect", Subtitle = "socket", Category = NodeCategory.Action,
                Description = "Dials out: opens a persistent TCP / UDP / WebSocket connection (optionally TLS / wss, with custom WebSocket headers) and keeps it open. Incoming data fires On Socket Data with the connection id; reply with Socket Send. Outputs the connection id.",
                Inputs = new[] { Ex(), Tx("host") },
                Outputs = new[] { Ex("then"), Tx("conn") },
                Params = new[]
                {
                    P("proto", "Protocol", ParamType.Choice, "tcp", "", new[] { "tcp", "udp", "ws" }),
                    P("host", "Host", ParamType.Text, "", "host or ws(s)://url"),
                    P("port", "Port", ParamType.Int, "0", "6667"),
                    P("tls", "TLS / wss", ParamType.Bool, "false"),
                    P("framing", "Framing", ParamType.Choice, "line", "", new[] { "line", "raw", "delimiter", "length" }),
                    P("delimiter", "Delimiter", ParamType.Text, "", "for delimiter framing", visibleWhen: n => n.GetParam("framing") == "delimiter"),
                    PL("headers", "WS headers", true, "Add header"),
                },
                SummaryParam = "host",
                Exec = c =>
                {
                    string host = c.InOr(1, c.Resolve(c.Param("host")));
                    var hdrs = new List<(string, string)>();
                    foreach (var (k, v) in Ircuitry.Core.ParamList.Pairs(c.Param("headers"))) hdrs.Add((c.Resolve(k), c.Resolve(v)));
                    string id = c.SocketConnect(c.Param("proto"), host, c.ParamInt("port", 0), c.Param("framing"), c.Resolve(c.Param("delimiter")), c.ParamBool("tls"), hdrs);
                    c.SetOut(1, id);
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "socket.send", Icon = "paper-plane-tilt", Title = "Socket Send", Subtitle = "socket", Category = NodeCategory.Action,
                Description = "Sends data to a connection by its id (defaults to {conn}, the one that fired this run). Choose an encoding (text / base64 / hex) and an optional terminator to append (none / LF / CRLF - CRLF is what line protocols like IRC want). For UDP you can target a remote 'host:port' directly.",
                Inputs = new[] { Ex(), Tx("conn"), Tx("data") },
                Outputs = new[] { Ex("then") },
                Params = new[]
                {
                    P("conn", "Connection", ParamType.Text, "{conn}", "{conn}"),
                    P("data", "Data", ParamType.Multiline, "", "what to send"),
                    P("encoding", "Encoding", ParamType.Choice, "text", "", new[] { "text", "base64", "hex" }),
                    P("append", "Append", ParamType.Choice, "none", "", new[] { "none", "lf", "crlf" }),
                    P("to", "UDP remote (optional)", ParamType.Text, "", "host:port"),
                },
                SummaryParam = "conn",
                Exec = c =>
                {
                    string conn = c.InOr(1, c.Resolve(c.Param("conn")));
                    string data = c.InOr(2, c.Resolve(c.Param("data")));
                    if (conn.Length > 0) c.SocketSend(conn, SocketBytes(data, c.Param("encoding"), c.Param("append")), c.Resolve(c.Param("to")));
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "socket.broadcast", Icon = "broadcast", Title = "Socket Broadcast", Subtitle = "socket", Category = NodeCategory.Action,
                Description = "Sends data to every client connected to a listener (by its id, default {listener}) - fan-out, e.g. relaying a line to all connected clients. Outputs how many got it.",
                Inputs = new[] { Ex(), Tx("data") },
                Outputs = new[] { Ex("then"), Tx("count") },
                Params = new[]
                {
                    P("listener", "Listener", ParamType.Text, "{listener}", "{listener}"),
                    P("data", "Data", ParamType.Multiline, "", "what to send"),
                    P("encoding", "Encoding", ParamType.Choice, "text", "", new[] { "text", "base64", "hex" }),
                    P("append", "Append", ParamType.Choice, "none", "", new[] { "none", "lf", "crlf" }),
                },
                SummaryParam = "listener",
                Exec = c =>
                {
                    string lid = c.Resolve(c.Param("listener"));
                    string data = c.InOr(1, c.Resolve(c.Param("data")));
                    c.SetOut(1, (lid.Length > 0 ? c.SocketBroadcast(lid, SocketBytes(data, c.Param("encoding"), c.Param("append"))) : 0).ToString());
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "socket.close", Icon = "x-circle", Title = "Socket Close", Subtitle = "socket", Category = NodeCategory.Action,
                Description = "Closes a connection or a listener by its id (defaults to {conn}).",
                Inputs = new[] { Ex(), Tx("id") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("id", "Id", ParamType.Text, "{conn}", "{conn} or {listener}") },
                SummaryParam = "id",
                Exec = c => { string id = c.InOr(1, c.Resolve(c.Param("id"))); if (id.Length > 0) c.SocketClose(id); c.Pulse(0); },
            },
            new()
            {
                TypeId = "event.socket.connect", Icon = "plugs-connected", Title = "On Socket Connect", Subtitle = "trigger",
                Category = NodeCategory.Event, TriggerEvent = "socket.connect",
                Description = "Fires when a socket opens - a client connecting to one of your listeners, or an outbound Socket Connect completing. Outputs the connection {conn}, the {remote} address, the {proto}, and the {listener} it arrived on (blank for an outbound connection).",
                Outputs = new[] { Ex("then"), Tx("conn"), Tx("remote"), Tx("proto"), Tx("listener") },
                Exec = c => { c.SetOut(1, c.Var("conn")); c.SetOut(2, c.Var("remote")); c.SetOut(3, c.Var("proto")); c.SetOut(4, c.Var("listener")); c.Pulse(0); },
            },
            new()
            {
                TypeId = "event.socket.data", Icon = "wave-sine", Title = "On Socket Data", Subtitle = "trigger",
                Category = NodeCategory.Event, TriggerEvent = "socket.data",
                Description = "Fires for each piece of data on a socket - a framed line/message, or a raw chunk, depending on the connection's framing. Outputs the {conn} it arrived on, the {data} (also exposed as {line}), and the {remote} address. Reply with Socket Send to {conn}.",
                Outputs = new[] { Ex("then"), Tx("conn"), Tx("data"), Tx("remote") },
                Exec = c => { c.SetOut(1, c.Var("conn")); c.SetOut(2, c.Var("data")); c.SetOut(3, c.Var("remote")); c.Pulse(0); },
            },
            new()
            {
                TypeId = "event.socket.disconnect", Icon = "plugs", Title = "On Socket Disconnect", Subtitle = "trigger",
                Category = NodeCategory.Event, TriggerEvent = "socket.disconnect",
                Description = "Fires when a socket closes (from either side). Outputs the {conn} that closed and its {remote} address.",
                Outputs = new[] { Ex("then"), Tx("conn"), Tx("remote") },
                Exec = c => { c.SetOut(1, c.Var("conn")); c.SetOut(2, c.Var("remote")); c.Pulse(0); },
            },

            new()
            {
                TypeId = "action.multiline", Icon = "file-text", Title = "Send Multiline", Subtitle = "draft",
                Category = NodeCategory.Ircv3,
                Description = "Sends several lines as one logical message (draft/multiline batch). One line per row in the text.",
                Inputs = new[] { Ex(), Ch("target"), Tx("text") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("target", "Target", ParamType.Text, "", "blank = where it came from"), P("text", "Text", ParamType.Multiline, "", "line one\nline two") },
                SummaryParam = "text",
                Exec = c =>
                {
                    var target = c.InOr(1, c.Resolve(c.Param("target"))); if (target.Length == 0) target = c.Var("replyto"); if (target.Length == 0) target = c.Var("channel");
                    var text = c.InOr(2, c.Resolve(c.Param("text")));
                    if (target.Length > 0 && text.Length > 0) c.Send(target, text);   // Send handles the draft/multiline batch + fallback
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "action.log", Icon = "clipboard", Title = "Console Log", Subtitle = "action",
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
                TypeId = "ai.reply", Icon = "robot", Title = "Ask AI", Subtitle = "ai",
                Category = NodeCategory.Action,
                Description = "Generates a reply via any OpenAI-compatible API (OpenAI, Ollama, LM Studio, OpenRouter, Groq, vLLM…). Wire AI Tool nodes into 'tools' to let the model fetch data / act. Wire 'reply' into a Send Reply.",
                Inputs = new[] { Ex(), Tx("prompt"), To("tools", multi: true) },
                Outputs = new[] { Ex("then"), Tx("reply"), Ex("over budget"), To("tool") },
                Params = new[]
                {
                    P("baseUrl", "Base URL", ParamType.Text, "https://api.openai.com/v1", "http://localhost:11434/v1 · openrouter · groq …"),
                    P("model", "Model", ParamType.Text, "gpt-4o-mini", "gpt-4o-mini · llama3 · mistral · …"),
                    P("apiKey", "API key", ParamType.Text, "", "blank = $OPENAI_API_KEY (or none for local)", secret: true),
                    P("system", "System prompt", ParamType.Multiline, "You are a witty IRC bot named ircuitry. Reply in one short sentence.", ""),
                    P("prompt", "Prompt", ParamType.Multiline, "{nick} said: {message}", "supports {nick} {message} {args}"),
                    P("maxTokens", "Max tokens", ParamType.Int, "300", "300"),
                    P("timeout", "Timeout (seconds)", ParamType.Int, "120", "how long to wait for the model (AI can be slow)"),
                    // sub-agent: wire this Ask AI's 'tool' output into ANOTHER Ask AI's 'tools' to make it a tool
                    // that agent can call - agents all the way down. These only matter when used that way.
                    P("name", "Tool name (as sub-agent)", ParamType.Text, "", "e.g. researcher - only used when wired as another AI's tool"),
                    P("toolDescription", "Tool description (as sub-agent)", ParamType.Text, "", "what this agent does, shown to the calling AI"),
                },
                SummaryParam = "model",
                Exec = c =>
                {
                    // #18: a spend cap stops the model call before it costs anything - take the over-budget branch
                    if (c.AiOverBudget) { c.Log("Ask AI: over the token budget - skipped the model call", LogLevel.System); c.Pulse(2); return; }

                    // when this Ask AI is invoked AS a tool by another agent, the caller's request arrives as {arg.prompt}
                    string subReq = c.Var("__arg.prompt");
                    var prompt = c.InOr(1, subReq.Length > 0 ? subReq : c.Resolve(c.Param("prompt")));
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

                    // gather tool nodes wired into the 'tools' input (pin 2): AI Tool sub-flows and/or
                    // Workflow Editor nodes (which expose the inward MCP editing tools to the model)
                    var defs = new List<Ai.ToolDef>();
                    var byName = new Dictionary<string, Node>();          // ai.tool name -> sub-flow node
                    var editorBot = new Dictionary<string, string>();     // editor tool name -> default bot
                    var nodeTools = new Dictionary<string, Node>();       // node-tool name -> the node (e.g. a custom .ircnode)
                    var subTools = new Dictionary<string, (NodeGraph sub, string innerId)>();   // ai.tool baked inside a composite
                    var mcpRoute = new Dictionary<string, Ircuitry.App.Mcp.McpClient>();        // external MCP tool name -> its client
                    foreach (var tn in c.SourcesInto(2))
                    {
                        if (tn.TypeId == "ai.tool")
                        {
                            var nm = tn.GetParam("name");
                            if (nm.Length == 0) continue;
                            defs.Add(new Ai.ToolDef(nm, tn.GetParam("description"), AiToolArgs(tn)));
                            byName[nm] = tn;
                        }
                        else if (tn.TypeId == "ai.editor")
                        {
                            bool ro = tn.GetParam("mode") == "read-only";
                            string defBot = tn.GetParam("bot").Trim();
                            foreach (var d in Ircuitry.App.Mcp.McpBridge.EditorToolDefs(ro))
                            {
                                if (byName.ContainsKey(d.Name) || editorBot.ContainsKey(d.Name)) continue;   // first wins
                                defs.Add(d);
                                editorBot[d.Name] = defBot;
                            }
                        }
                        else if (tn.TypeId == "ai.mcp")
                        {
                            GatherMcpTools(c, tn, defs, mcpRoute, byName, editorBot, nodeTools, subTools);
                        }
                        else
                        {
                            // a BAKED composite may hold AI Tool nodes inside it: expose each inner ai.tool
                            // directly (its name/description/args), so baking a tool sub-flow just works
                            bool handled = false;
                            if (tn.Def.SubgraphProvider is { } prov)
                            {
                                var sub = prov();
                                foreach (var inner in sub.Nodes)
                                {
                                    if (inner.TypeId != "ai.tool") continue;
                                    var im = inner.GetParam("name");
                                    if (im.Length == 0 || byName.ContainsKey(im) || editorBot.ContainsKey(im) || nodeTools.ContainsKey(im) || subTools.ContainsKey(im)) continue;
                                    defs.Add(new Ai.ToolDef(im, inner.GetParam("description"), AiToolArgs(inner)));
                                    subTools[im] = (sub, inner.Id); handled = true;
                                }
                            }
                            // otherwise any node advertising a Tool output is a self-contained tool (a custom
                            // .ircnode, or a composite ticked 'usable as AI tool'): data inputs = the model's
                            // args, first data output = the result
                            if (!handled && Array.Exists(tn.Outputs, p => p.Kind == PinKind.Tool))
                            {
                                string nm = AiToolName(tn);
                                if (nm.Length == 0 || byName.ContainsKey(nm) || editorBot.ContainsKey(nm) || nodeTools.ContainsKey(nm) || subTools.ContainsKey(nm)) continue;
                                var a = new List<(string, string)>();
                                foreach (var pin in tn.Inputs)
                                    if (pin.Kind != PinKind.Exec && pin.Kind != PinKind.Tool && pin.Name.Length > 0)
                                        a.Add((pin.Name, pin.Name));   // arg name = input pin name
                                defs.Add(new Ai.ToolDef(nm, ToolDesc(tn), a));
                                nodeTools[nm] = tn;
                            }
                        }
                    }

                    // baseUrl/model are Resolve()d (not apiKey - that carries {{secret}} handled downstream) so a
                    // composite can expose them as {tokens}; e.g. the SuperAI recipe sets model = "{model}".
                    int aiTimeout = Math.Clamp(c.ParamInt("timeout", 120), 5, 1800);
                    if (defs.Count == 0)
                        reply = Ai.Chat(c.Resolve(c.Param("baseUrl")), c.Param("apiKey"), c.Resolve(c.Param("model")), c.Resolve(c.Param("system")), prompt, c.ParamInt("maxTokens", 300), out err, aiTimeout, onUsage: c.RecordTokens);
                    else
                        reply = Ai.ChatWithTools(c.Resolve(c.Param("baseUrl")), c.Param("apiKey"), c.Resolve(c.Param("model")), c.Resolve(c.Param("system")), prompt, c.ParamInt("maxTokens", 300), defs,
                            (name, args) =>
                            {
                                if (mcpRoute.TryGetValue(name, out var mc))     // an external MCP server's tool
                                    return mc.Call(name, args, 60000);
                                if (editorBot.TryGetValue(name, out var db))    // inward MCP edit against the workspace
                                    return Ircuitry.App.Mcp.McpBridge.Invoke(name, args, db.Length > 0 ? db : null);
                                if (nodeTools.TryGetValue(name, out var ntn))   // a self-contained node tool (e.g. a custom .ircnode)
                                    return c.InvokeNodeTool(ntn, args);
                                if (subTools.TryGetValue(name, out var stp))    // an AI Tool baked inside a composite
                                    return c.InvokeSubflowTool(stp.sub, stp.innerId, args);
                                if (!byName.TryGetValue(name, out var tn)) return "(unknown tool: " + name + ")";
                                foreach (var kv in args) c.SetVar("__arg." + kv.Key, kv.Value);
                                c.SetVar("__tool_result", "");
                                c.RunNode(tn);          // runs the tool's sub-flow synchronously
                                return c.Var("__tool_result");
                            }, out err, timeoutSeconds: aiTimeout, onUsage: c.RecordTokens, shouldStop: () => c.AiOverBudget);

                    if (err.Length > 0) c.Log("AI error: " + err, LogLevel.Error);
                    else c.SetOut(1, reply);
                    c.Pulse(0);
                },
            },
            new NodeDef
            {
                TypeId = "ai.budget", Icon = "gauge", Title = "AI Spend Cap", Subtitle = "ai", Category = NodeCategory.Ai,
                Description = "Caps how many AI tokens this bot may spend. Run it once (e.g. wired to On Connect) to set the budget; after that any Ask AI / Programmer AI that has hit the cap takes its 'over budget' branch instead of calling the model. Optionally resets every hour or day. The status bar shows the running token meter.",
                Inputs = new[] { Ex() },
                Outputs = new[] { Ex("then") },
                Params = new[]
                {
                    P("maxTokens", "Token budget", ParamType.Int, "100000", "0 = no cap; total tokens (input+output) allowed"),
                    P("window", "Reset every", ParamType.Choice, "session", "", new[] { "session", "hour", "day" }),
                },
                SummaryParam = "maxTokens",
                Exec = c =>
                {
                    int cap = Math.Max(0, c.ParamInt("maxTokens", 0));
                    double win = c.Param("window") switch { "hour" => 3600.0, "day" => 86400.0, _ => 0.0 };
                    c.SetTokenBudget(cap, win);
                    c.Log("AI spend cap: " + (cap == 0 ? "unlimited" : cap + " tokens" + (win > 0 ? " per " + c.Param("window") : "")), LogLevel.System);
                    c.Pulse(0);
                },
            },
            new NodeDef
            {
                TypeId = "mod.in", Icon = "shield-check", Title = "Moderate In", Subtitle = "guard", Category = NodeCategory.Filter,
                Description = "Guardrail: screens an incoming message and branches clean / flagged. Heuristic by default (block list, links, SHOUTING, channel invites). Switch mode to 'heuristic + AI' to also ask a moderation model for nuanced cases (heuristic runs first, AI only on what passes). Wire 'flagged' to a warn/kick; 'clean' to your normal flow.",
                Inputs = new[] { Ex(), Tx("text") },
                Outputs = new[] { Ex("clean"), Ex("flagged"), Tx("category"), Tx("reason") },
                Params = new[]
                {
                    P("mode", "Mode", ParamType.Choice, "heuristic", "", new[] { "heuristic", "heuristic + AI" }),
                    P("blockWords", "Block list", ParamType.Multiline, "", "words/phrases to flag, comma or newline separated"),
                    P("flagLinks", "Flag links", ParamType.Bool, "false", ""),
                    P("flagCaps", "Flag SHOUTING", ParamType.Bool, "false", ""),
                    P("flagInvites", "Flag channel invites", ParamType.Bool, "false", ""),
                    P("baseUrl", "AI base URL", ParamType.Text, "https://api.openai.com/v1", "only used in heuristic + AI mode"),
                    P("model", "AI model", ParamType.Text, "gpt-4o-mini", ""),
                    P("apiKey", "AI API key", ParamType.Text, "", "blank = $OPENAI_API_KEY", secret: true),
                },
                SummaryParam = "mode",
                Exec = c =>
                {
                    string text = c.InOr(1, c.Var("message"));
                    var terms = Moderation.Terms(c.Resolve(c.Param("blockWords")));
                    var (flagged, cat, reason) = Moderation.Check(text, terms, c.ParamBool("flagLinks"), c.ParamBool("flagCaps"), c.ParamBool("flagInvites"));
                    if (!flagged && c.Param("mode").Contains("AI") && !c.AiOverBudget)
                    {
                        var reply = Ai.Chat(c.Resolve(c.Param("baseUrl")), c.Param("apiKey"), c.Resolve(c.Param("model")), Moderation.ClassifierSystem, text, 60, out var err, 60, onUsage: c.RecordTokens);
                        if (err.Length == 0) { if (Moderation.ParseVerdict(reply) is { } v && v.flagged) { flagged = true; cat = "ai"; reason = v.reason; } }
                        else c.Log("Moderate In: AI check unavailable - " + err, LogLevel.System);   // fail open: don't block on an AI error
                    }
                    c.SetOut(2, cat); c.SetOut(3, reason);
                    c.Pulse(flagged ? 1 : 0);
                },
            },
            new NodeDef
            {
                TypeId = "mod.out", Icon = "shield", Title = "Moderate Out", Subtitle = "guard", Category = NodeCategory.Filter,
                Description = "Guardrail on the bot's OWN outgoing text before it's sent. Branches clean / flagged and outputs a 'safe text'. On flag = block (take the flagged branch, send nothing) or redact (mask the offending words and continue on clean). Heuristic by default, with an optional AI check.",
                Inputs = new[] { Ex(), Tx("text") },
                Outputs = new[] { Ex("clean"), Ex("flagged"), Tx("safe text"), Tx("reason") },
                Params = new[]
                {
                    P("mode", "Mode", ParamType.Choice, "heuristic", "", new[] { "heuristic", "heuristic + AI" }),
                    P("onFlag", "On flag", ParamType.Choice, "block", "", new[] { "block", "redact" }),
                    P("blockWords", "Block list", ParamType.Multiline, "", "words/phrases to flag, comma or newline separated"),
                    P("flagLinks", "Flag links", ParamType.Bool, "false", ""),
                    P("flagCaps", "Flag SHOUTING", ParamType.Bool, "false", ""),
                    P("baseUrl", "AI base URL", ParamType.Text, "https://api.openai.com/v1", "only used in heuristic + AI mode"),
                    P("model", "AI model", ParamType.Text, "gpt-4o-mini", ""),
                    P("apiKey", "AI API key", ParamType.Text, "", "blank = $OPENAI_API_KEY", secret: true),
                },
                SummaryParam = "onFlag",
                Exec = c =>
                {
                    string text = c.InOr(1, "");
                    var terms = Moderation.Terms(c.Resolve(c.Param("blockWords")));
                    var (flagged, _, reason) = Moderation.Check(text, terms, c.ParamBool("flagLinks"), c.ParamBool("flagCaps"), false);
                    if (!flagged && c.Param("mode").Contains("AI") && !c.AiOverBudget)
                    {
                        var reply = Ai.Chat(c.Resolve(c.Param("baseUrl")), c.Param("apiKey"), c.Resolve(c.Param("model")), Moderation.ClassifierSystem, text, 60, out var err, 60, onUsage: c.RecordTokens);
                        if (err.Length == 0) { if (Moderation.ParseVerdict(reply) is { } v && v.flagged) { flagged = true; reason = v.reason; } }
                        else c.Log("Moderate Out: AI check unavailable - " + err, LogLevel.System);
                    }
                    if (flagged && c.Param("onFlag") == "redact")
                    {
                        c.SetOut(2, Moderation.Redact(text, terms, c.ParamBool("flagLinks"))); c.SetOut(3, reason);
                        c.Pulse(0);   // sanitised - continue on the clean branch
                    }
                    else
                    {
                        c.SetOut(2, flagged ? "" : text); c.SetOut(3, reason);
                        c.Pulse(flagged ? 1 : 0);
                    }
                },
            },
            // ---- building-block tool nodes: usable standalone, or wired into Ask AI's 'tools' so the model can
            // call them. They carry a Tool output, so the SuperAI recipe (a composite .ircnode) is built FROM
            // these rather than from a built-in - drop SuperAI into nodes/ and right-click to edit/rewire it.
            new()
            {
                TypeId = "ircv3.recent", Icon = "clock", Title = "Recent Messages", Subtitle = "ircv3",
                Category = NodeCategory.Ircv3,
                Description = "Lists the messages the bot has recently seen, as JSON [{nick,channel,text,id}]. As an AI tool it lets the model find a message to act on.",
                Inputs = new[] { Ex(), Tx("count") },
                Outputs = new[] { Ex("then"), Tx("messages"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "recent_messages", "recent_messages"),
                    P("count", "How many", ParamType.Int, "15", "15"),
                },
                SummaryParam = "count",
                Exec = c =>
                {
                    string cnt = c.In(1); if (cnt.Length == 0) cnt = c.Var("__arg.count"); if (cnt.Length == 0) cnt = c.Param("count");
                    int n = int.TryParse(cnt, out var x) && x > 0 ? x : 15;
                    c.SetOut(1, HistoryJson(c.RecentMessages(n)));
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "action.reactid", Icon = "heart", Title = "React to Message", Subtitle = "ircv3",
                Category = NodeCategory.Ircv3,
                Description = "Adds an emoji reaction to a specific message by its id (from Recent Messages / Request History). As an AI tool the model passes the id + emoji it chose.",
                Inputs = new[] { Ex(), Tx("msgid"), Tx("emoji"), Ch("target") },
                Outputs = new[] { Ex("then"), Tx("result"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "react", "react"),
                    P("msgid", "Message id", ParamType.Text, "", "{arg.msgid}"),
                    P("emoji", "Emoji", ParamType.Text, "", "\U0001F382"),   // intentional unicode (birthday cake)
                    P("target", "Target (blank = here)", ParamType.Text, "", "#channel or nick"),
                },
                Exec = c =>
                {
                    string msgid = c.In(1); if (msgid.Length == 0) msgid = c.Var("__arg.msgid"); if (msgid.Length == 0) msgid = c.Resolve(c.Param("msgid"));
                    string emoji = c.In(2); if (emoji.Length == 0) emoji = c.Var("__arg.emoji"); if (emoji.Length == 0) emoji = c.Resolve(c.Param("emoji"));
                    string target = c.In(3); if (target.Length == 0) target = c.Var("__arg.target"); if (target.Length == 0) target = c.Resolve(c.Param("target"));
                    c.ReactTo(target, msgid, emoji);
                    c.SetOut(1, emoji.Length > 0 && msgid.Length > 0 ? "reacted " + emoji : "(need a message id and emoji)");
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "ai.tool", Icon = "toolbox", Title = "AI Tool", Subtitle = "ai",
                Category = NodeCategory.Logic,
                Description = "Defines a tool the AI may call, with ANY number of arguments (Add argument). Wire 'tool' into Ask AI; wire 'call' to a sub-flow that ends in a Tool Reply. Read an argument anywhere with {arg.NAME} - the first three are also on the arg pins.",
                Outputs = new[] { To("tool"), Ex("call"), Tx("arg 1"), Tx("arg 2"), Tx("arg 3") },
                Params = new[]
                {
                    P("name", "Tool name", ParamType.Text, "lookup", "get_weather"),
                    P("description", "What it does", ParamType.Text, "", "tell the model when to use it"),
                    PL("args", "Arguments (name + what it is)", true, "Add argument"),
                    // legacy fixed args: kept (hidden) so tools saved before the dynamic list still load + work
                    P("arg1name", "Arg 1 name", ParamType.Text, "", "", null, _ => false), P("arg1desc", "", ParamType.Text, "", "", null, _ => false),
                    P("arg2name", "Arg 2 name", ParamType.Text, "", "", null, _ => false), P("arg2desc", "", ParamType.Text, "", "", null, _ => false),
                    P("arg3name", "Arg 3 name", ParamType.Text, "", "", null, _ => false), P("arg3desc", "", ParamType.Text, "", "", null, _ => false),
                },
                SummaryParam = "name",
                Exec = c =>
                {
                    var names = AiToolArgs(c.Node);   // arg pins are outputs 2,3,4 (after 'tool' and 'call')
                    for (int i = 0; i < 3 && i < names.Count; i++) c.SetOut(2 + i, c.Var("__arg." + names[i].name));
                    c.Pulse(1); // run the 'call' sub-flow; read any arg as {arg.NAME}
                },
            },
            new()
            {
                TypeId = "tool.reply", Icon = "gift", Title = "Tool Reply", Subtitle = "ai",
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
                TypeId = "ai.editor", Icon = "note-pencil", Title = "Workflow Editor", Subtitle = "ai",
                Category = NodeCategory.Ai,
                Description = "Gives an Ask AI node the inward MCP tools to read and EDIT bot workflows in this " +
                              "workspace - its own or another bot's: add/remove nodes, wire pins, set params, replace " +
                              "or validate a graph, dry-run a command. Wire 'tools' into Ask AI. Edits save to the " +
                              "workspace (the canvas hot-reloads); a running bot applies them on its next start.",
                Outputs = new[] { To("tools") },
                Params = new[]
                {
                    P("bot", "Target bot", ParamType.Text, "", "blank = the active bot; or a bot name (the model can still target others by name)"),
                    P("mode", "Access", ParamType.Choice, "edit", "read-only = introspect/validate/dry-run only", new[] { "edit", "read-only" }),
                },
                SummaryParam = "bot",
                Exec = c => { },   // never runs in the flow - it only advertises tools to Ask AI
            },
            new()
            {
                TypeId = "ai.mcp", Icon = "plugs-connected", Title = "MCP Tools", Subtitle = "ai", Category = NodeCategory.Ai,
                Description = "Connects to an external MCP (Model Context Protocol) server and exposes ALL its tools to an Ask AI / Programmer AI - filesystem, git, web search, a database, anything with an MCP server. Wire 'tools' into the AI. 'stdio' runs a local command (e.g. an npx server); 'http' connects to a URL. Secrets work in the command/url/headers.",
                Outputs = new[] { To("tools") },
                Params = new[]
                {
                    P("transport", "Transport", ParamType.Choice, "stdio", "", new[] { "stdio", "http" }),
                    P("command", "Command", ParamType.Text, "", "npx -y @modelcontextprotocol/server-filesystem ~/notes", visibleWhen: n => n.GetParam("transport") != "http"),
                    P("url", "Server URL", ParamType.Text, "", "https://host/mcp", visibleWhen: n => n.GetParam("transport") == "http"),
                    PL("headers", "Headers (http)", true, "Add header"),
                },
                SummaryParam = "transport",
                Exec = c => { },   // never runs in the flow - it only advertises an MCP server's tools to the AI
            },
            new()
            {
                TypeId = "ai.programmer", Icon = "wrench", Title = "Programmer AI", Subtitle = "ai",
                Category = NodeCategory.Ai,
                Description = "An AI software engineer that reads and edits a whole codebase (read/write/edit/search/move files, run build & test commands) and delivers the finished result. File edits are locked to the codebase folder; commands run in an isolated Docker/Podman container (its own OS, no network unless you allow it) and are refused if no container runtime is available. Set a 'task' and wire 'reply' into Send Reply. Wire extra AI Tool nodes into 'tools' to give it more abilities.",
                Inputs = new[] { Ex(), Tx("task"), To("tools", multi: true) },
                Outputs = new[] { Ex("then"), Tx("reply"), Ex("over budget") },
                Params = new[]
                {
                    P("root", "Codebase folder", ParamType.Text, "", "~/projects/mybot · the AI is locked inside this folder"),
                    P("baseUrl", "Base URL", ParamType.Text, "https://api.openai.com/v1", "openai · openrouter · groq · ollama …"),
                    P("model", "Model", ParamType.Text, "gpt-4o", "gpt-4o · a coding model · llama3 …"),
                    P("apiKey", "API key", ParamType.Text, "", "blank = $OPENAI_API_KEY", secret: true),
                    P("task", "Task", ParamType.Multiline, "", "what to build or change (supports {nick} {message} {args})"),
                    P("instructions", "Extra instructions (optional)", ParamType.Multiline, "", "house style, constraints, what 'done' means"),
                    P("allowCommands", "Allow running commands", ParamType.Bool, "true", ""),
                    P("sandboxImage", "Command sandbox image", ParamType.Text, "alpine", "commands run in this isolated container (python:3.12 · node:20 · your dev image)", visibleWhen: n => n.GetParam("allowCommands") == "true"),
                    P("allowNetwork", "Allow command network", ParamType.Bool, "false", "off = commands run fully offline", visibleWhen: n => n.GetParam("allowCommands") == "true"),
                    P("maxTokens", "Max tokens", ParamType.Int, "8000", "output budget per step - must be big enough to write a whole file in one tool call"),
                    P("maxSteps", "Max tool steps", ParamType.Int, "40", "how many read/edit/run steps the AI may take before stopping"),
                    P("timeout", "Timeout per step (seconds)", ParamType.Int, "180", "AI can be slow; 180 = 3 min per model call"),
                    P("filehostUrl", "Filehost URL", ParamType.Text, "https://0x0.st", "where the finished codebase is uploaded"),
                    P("filehostField", "Filehost file field", ParamType.Text, "file", "file · fileToUpload (catbox) · image"),
                    PL("filehostFields", "Filehost extra fields (optional)", true, "Add field"),
                    PL("filehostHeaders", "Filehost headers (optional)", true, "Add header"),
                },
                SummaryParam = "model",
                Exec = c =>
                {
                    if (c.AiOverBudget) { c.Log("Programmer AI: over the token budget - skipped", LogLevel.System); c.Pulse(2); return; }   // #18
                    string root = c.Resolve(c.Param("root")).Trim();
                    if (root.Length == 0) { c.Log("Programmer AI: set a codebase folder first", LogLevel.Error); c.SetOut(1, ""); c.Pulse(0); return; }
                    try { CodeTools.Root(root); }
                    catch (Exception ex) { c.Log("Programmer AI: " + ex.Message, LogLevel.Error); c.SetOut(1, ""); c.Pulse(0); return; }

                    string task = c.InOr(1, c.Resolve(c.Param("task")));
                    bool allowCmd = c.ParamBool("allowCommands");

                    var miss = new List<string>();
                    foreach (var k in new[] { "apiKey", "baseUrl", "model" })
                        foreach (var nm in Ircuitry.Core.Secrets.Missing(c.Node.GetParam(k)))
                            if (!miss.Contains(nm)) miss.Add(nm);
                    if (miss.Count > 0)
                    {
                        c.Log("Programmer AI: secret '" + string.Join("', '", miss) + "' not defined - add it under KEYS", LogLevel.Error);
                        c.SetOut(1, ""); c.Pulse(0); return;
                    }

                    // the hidden delivery: zip the codebase to a temp file OUTSIDE the tree, upload it, return the link
                    string fhUrl = c.Resolve(c.Param("filehostUrl")).Trim();
                    string serverFh = c.IrcInfo("filehost", "");   // IRCv3 draft/FILEHOST the bot's server advertises
                    // use the server filehost when the URL points at it ({filehost}) or no URL is configured at all
                    bool useFilehost = serverFh.Length > 0 && (fhUrl.Length == 0 || string.Equals(fhUrl, serverFh, StringComparison.OrdinalIgnoreCase));
                    string fhField = c.Param("filehostField"); if (fhField.Length == 0) fhField = "file";
                    var fhFields = Ircuitry.Core.ParamList.Pairs(c.Param("filehostFields")).Where(p => p.key.Length > 0).Select(p => (p.key, p.val)).ToList();
                    var fhHeaders = Ircuitry.Core.ParamList.Pairs(c.Param("filehostHeaders")).Where(p => p.key.Length > 0).Select(p => (p.key, p.val)).ToList();
                    Func<Dictionary<string, string>, string> sendCodebase = _ =>
                    {
                        if (fhUrl.Length == 0 && !useFilehost) return "delivery is not configured (no filehost URL set, and the server doesn't advertise one)";
                        string tmp = "";
                        try
                        {
                            tmp = CodeTools.ZipToTemp(root, ".");
                            if (useFilehost)   // IRCv3 draft/FILEHOST: token + Bearer raw POST, link via Location
                            {
                                var (ok, res) = c.FilehostUpload(tmp);
                                if (!ok) { c.Log("send_codebase filehost upload failed: " + res, LogLevel.Error); return "delivery failed: " + res; }
                                c.Log("Programmer AI delivered the codebase (server filehost) -> " + res, LogLevel.Action);
                                return "Delivered. Link: " + res;
                            }
                            var (status, body) = Ircuitry.Net.Upload.PostFile(fhUrl, tmp, fhField, fhFields, fhHeaders);
                            if (!Ircuitry.Net.Upload.Ok(status)) { c.Log("send_codebase upload failed (" + status + "): " + body, LogLevel.Error); return "delivery failed: " + body; }
                            string link = ExtractUrl(body);
                            c.Log("Programmer AI delivered the codebase -> " + link, LogLevel.Action);
                            return "Delivered. Link: " + link;
                        }
                        catch (Exception ex) { c.Log("send_codebase error: " + ex.Message, LogLevel.Error); return "delivery failed: " + ex.Message; }
                        finally { if (tmp.Length > 0) { try { System.IO.File.Delete(tmp); } catch { } } }
                    };

                    // tool defs: curated code tools (+ run_command, + send_codebase), PLUS any externally-wired tools
                    var defs = CodeAgent.ToolDefs(allowCmd, includeSend: fhUrl.Length > 0 || serverFh.Length > 0);
                    var byName = new Dictionary<string, Node>();
                    var editorBot = new Dictionary<string, string>();
                    var nodeTools = new Dictionary<string, Node>();
                    var subTools = new Dictionary<string, (NodeGraph sub, string innerId)>();
                    var mcpRoute = new Dictionary<string, Ircuitry.App.Mcp.McpClient>();
                    foreach (var tn in c.SourcesInto(2))
                    {
                        if (tn.TypeId == "ai.tool")
                        {
                            var nm = tn.GetParam("name"); if (nm.Length == 0 || CodeAgent.Handles(nm)) continue;
                            defs.Add(new Ai.ToolDef(nm, tn.GetParam("description"), AiToolArgs(tn))); byName[nm] = tn;
                        }
                        else if (tn.TypeId == "ai.mcp") GatherMcpTools(c, tn, defs, mcpRoute, byName, editorBot, nodeTools, subTools);
                        else if (tn.TypeId == "ai.editor")
                        {
                            bool ro = tn.GetParam("mode") == "read-only"; string defBot = tn.GetParam("bot").Trim();
                            foreach (var d in Ircuitry.App.Mcp.McpBridge.EditorToolDefs(ro))
                            {
                                if (byName.ContainsKey(d.Name) || editorBot.ContainsKey(d.Name) || CodeAgent.Handles(d.Name)) continue;
                                defs.Add(d); editorBot[d.Name] = defBot;
                            }
                        }
                        else
                        {
                            bool handled = false;
                            if (tn.Def.SubgraphProvider is { } prov)   // an AI Tool baked inside a composite
                            {
                                var sub = prov();
                                foreach (var inner in sub.Nodes)
                                {
                                    if (inner.TypeId != "ai.tool") continue;
                                    var im = inner.GetParam("name");
                                    if (im.Length == 0 || CodeAgent.Handles(im) || byName.ContainsKey(im) || editorBot.ContainsKey(im) || nodeTools.ContainsKey(im) || subTools.ContainsKey(im)) continue;
                                    defs.Add(new Ai.ToolDef(im, inner.GetParam("description"), AiToolArgs(inner)));
                                    subTools[im] = (sub, inner.Id); handled = true;
                                }
                            }
                            if (!handled && Array.Exists(tn.Outputs, p => p.Kind == PinKind.Tool))
                            {
                                string nm = AiToolName(tn);
                                if (nm.Length == 0 || CodeAgent.Handles(nm) || byName.ContainsKey(nm) || editorBot.ContainsKey(nm) || nodeTools.ContainsKey(nm) || subTools.ContainsKey(nm)) continue;
                                var a = new List<(string, string)>();
                                foreach (var pin in tn.Inputs)
                                    if (pin.Kind != PinKind.Exec && pin.Kind != PinKind.Tool && pin.Name.Length > 0) a.Add((pin.Name, pin.Name));
                                defs.Add(new Ai.ToolDef(nm, ToolDesc(tn), a)); nodeTools[nm] = tn;
                            }
                        }
                    }

                    string system = ProgrammerSystemPrompt(c.Resolve(c.Param("instructions")), allowCmd, fhUrl.Length > 0);
                    string reply = Ai.ChatWithTools(c.Resolve(c.Param("baseUrl")), c.Param("apiKey"), c.Resolve(c.Param("model")), system,
                        task, c.ParamInt("maxTokens", 1500), defs,
                        (name, args) =>
                        {
                            var res = CodeAgent.Dispatch(root, name, args, allowCmd, sendCodebase, c.Resolve(c.Param("sandboxImage")), c.ParamBool("allowNetwork"));
                            if (res != null) return res;
                            if (mcpRoute.TryGetValue(name, out var mc)) return mc.Call(name, args, 60000);
                            if (editorBot.TryGetValue(name, out var db)) return Ircuitry.App.Mcp.McpBridge.Invoke(name, args, db.Length > 0 ? db : null);
                            if (nodeTools.TryGetValue(name, out var ntn)) return c.InvokeNodeTool(ntn, args);
                            if (subTools.TryGetValue(name, out var stp)) return c.InvokeSubflowTool(stp.sub, stp.innerId, args);
                            if (byName.TryGetValue(name, out var tn))
                            {
                                foreach (var kv in args) c.SetVar("__arg." + kv.Key, kv.Value);
                                c.SetVar("__tool_result", ""); c.RunNode(tn); return c.Var("__tool_result");
                            }
                            return "(unknown tool: " + name + ")";
                        }, out var err,
                        maxRounds: Math.Clamp(c.ParamInt("maxSteps", 40), 1, 200),
                        onTool: (name, argsJson, res) => c.Log(Ircuitry.Core.Icons.Glyph("wrench") + " " + name + Brief(argsJson, 80) + "  -> " + Brief(res, 100), LogLevel.Action),
                        timeoutSeconds: Math.Clamp(c.ParamInt("timeout", 180), 5, 1800), onUsage: c.RecordTokens, shouldStop: () => c.AiOverBudget);

                    if (err.Length > 0) c.Log("Programmer AI error: " + err, LogLevel.Error);
                    else c.SetOut(1, reply);
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "human.alert", Icon = "bell", Title = "Alert Human", Subtitle = "notify",
                Category = NodeCategory.Action,
                Description = "Pops a native desktop notification on the machine running ircuitry, so a human notices something even when they aren't watching chat. notify-send on Linux, osascript on macOS, a toast on Windows (best effort).",
                Inputs = new[] { Ex(), Tx("message") },
                Outputs = new[] { Ex("then") },
                Params = new[]
                {
                    P("title", "Title", ParamType.Text, "ircuitry", "notification title"),
                    P("message", "Message", ParamType.Multiline, "", "supports {nick} {message} {channel} …"),
                    P("urgency", "Urgency", ParamType.Choice, "normal", "", new[] { "low", "normal", "critical" }),
                },
                SummaryParam = "message",
                Exec = c =>
                {
                    string title = c.Resolve(c.Param("title")); if (title.Length == 0) title = "ircuitry";
                    string body = c.InOr(1, c.Resolve(c.Param("message")));
                    if (!Ircuitry.Core.Notifier.Send(title, body, c.Param("urgency")))
                        c.Log("Alert Human: desktop notifications unavailable on this system (is notify-send installed?)", LogLevel.Warn);
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "human.loop", Icon = "hand-palm", Title = "Human in the Loop", Subtitle = "approval",
                Category = NodeCategory.Action,
                Description = "Pauses the flow and asks a human to approve or deny before it continues (like n8n's Human in the Loop). Posts the question to a channel/PM (and optionally a desktop alert), then resumes on 'approved' or 'denied' when they reply - or 'denied' if it times out. Their reply text comes out on 'response'.",
                Inputs = new[] { Ex(), Tx("question") },
                Outputs = new[] { Ex("approved"), Ex("denied"), Tx("response") },
                Params = new[]
                {
                    P("question", "Question", ParamType.Multiline, "Approve this?", "what to ask the human"),
                    P("target", "Ask in", ParamType.Text, "{channel}", "channel or nick (blank = where the event came from)"),
                    P("approver", "Only from", ParamType.Text, "", "a specific nick, or blank for anyone there"),
                    P("approveWord", "Approve word", ParamType.Text, "yes", "yes / approve / ok"),
                    P("denyWord", "Deny word", ParamType.Text, "no", "no / deny / stop"),
                    P("timeout", "Timeout (s)", ParamType.Int, "120", "0 = wait forever"),
                    P("notify", "Desktop alert too", ParamType.Bool, "true"),
                },
                SummaryParam = "question",
                Exec = c =>
                {
                    string q = c.InOr(1, c.Resolve(c.Param("question")));
                    if (q.Length == 0) q = "Approval needed.";
                    string target = c.Resolve(c.Param("target")); if (target.Length == 0) target = c.Var("replyto");
                    string approveWord = c.Param("approveWord"); if (approveWord.Length == 0) approveWord = "yes";
                    string denyWord = c.Param("denyWord"); if (denyWord.Length == 0) denyWord = "no";
                    int timeout = Math.Max(0, c.ParamInt("timeout", 120));

                    if (target.Length > 0) c.Send(target, $"{q} (reply '{approveWord}' to approve or '{denyWord}' to deny)");
                    if (c.ParamBool("notify")) Ircuitry.Core.Notifier.Send("ircuitry: approval needed", q);

                    if (!c.AwaitApproval(target, c.Resolve(c.Param("approver")), approveWord, denyWord, timeout))
                    {
                        // no live runtime to host the gate (a dry run/test) - take 'denied' so nothing hangs
                        c.SetOut(2, "(no human available)");
                        c.Pulse(1);
                    }
                    // otherwise: do NOT pulse - the run pauses here and resumes when a human answers
                },
            },
            new()
            {
                TypeId = "net.http", Icon = "globe", Title = "HTTP Request", Subtitle = "web",
                Category = NodeCategory.Action,
                Description = "Calls a web API. Sends a text/JSON body, or uploads a file as multipart/form-data (the standard way to push an image or any file to a filehost). Outputs the response body and status. Headers are 'Key: value' lines.",
                Inputs = new[] { Ex(), Tx("url"), Tx("file") },
                Outputs = new[] { Ex("then"), Tx("response"), Tx("status") },
                Params = new[]
                {
                    P("method", "Method", ParamType.Choice, "GET", "", new[] { "GET", "POST" }, visibleWhen: n => n.GetParam("send") != "file (multipart)"),
                    P("url", "URL", ParamType.Text, "https://", "https://api.example.com/..."),
                    P("send", "Send", ParamType.Choice, "body", "", new[] { "body", "file (multipart)" }),
                    P("field", "File field name", ParamType.Text, "file", "file · fileToUpload · image", visibleWhen: n => n.GetParam("send") == "file (multipart)"),
                    P("file", "File path", ParamType.Text, "", "pic.png (under ~/ircuitry/files) or /abs/pic.png", visibleWhen: n => n.GetParam("send") == "file (multipart)"),
                    P("headers", "Headers", ParamType.Multiline, "", "Authorization: Bearer ..."),
                    P("body", "Body / form fields", ParamType.Multiline, "", "{ ... } for POST  ·  or key=value lines for multipart"),
                    P("timeout", "Timeout (seconds)", ParamType.Int, "30", "how long to wait before giving up"),
                },
                SummaryParam = "url",
                Exec = c =>
                {
                    var url = c.InOr(1, c.Resolve(c.Param("url")));
                    var headers = c.Resolve(c.Param("headers")).Split('\n')   // resolve so {{secret.X}} / {tokens} work in headers
                        .Select(l => l.Trim()).Where(l => l.Contains(':'))
                        .Select(l => (l[..l.IndexOf(':')].Trim(), l[(l.IndexOf(':') + 1)..].Trim()))
                        .Where(t => t.Item1.Length > 0)   // drop malformed ":value" lines (empty header name)
                        .ToArray();

                    if (c.Param("send") == "file (multipart)")
                    {
                        var filePath = ResolveFile(c.InOr(2, c.Resolve(c.Param("file"))));
                        if (filePath.Length == 0) { c.Log("HTTP upload: file path is empty or escapes the files sandbox", LogLevel.Error); c.SetOut(1, ""); c.SetOut(2, "0"); c.Pulse(0); return; }
                        var fields = c.Resolve(c.Param("body")).Split('\n')   // multipart: extra text fields as key=value lines
                            .Select(l => l.Trim()).Where(l => l.Contains('=') && !l.StartsWith("{"))
                            .Select(l => (l[..l.IndexOf('=')].Trim(), l[(l.IndexOf('=') + 1)..].Trim()))
                            .Where(t => t.Item1.Length > 0)   // drop malformed "=value" lines (empty field name)
                            .ToArray();
                        string field = c.Param("field"); if (field.Length == 0) field = "file";
                        var (st, rb) = Ircuitry.Net.Upload.PostFile(url, filePath, field, fields, headers);
                        c.SetOut(1, rb); c.SetOut(2, st.ToString());
                        if (!Ircuitry.Net.Upload.Ok(st)) c.Log("HTTP upload status " + st + ": " + rb, LogLevel.Error);
                        c.Pulse(0);
                        return;
                    }

                    var method = c.Param("method");
                    var body = method == "POST" ? c.Resolve(c.Param("body")) : null;
                    var (status, resp) = Http.Send(method, url, headers, body, Math.Clamp(c.ParamInt("timeout", 30), 1, 1800));
                    c.SetOut(1, resp);
                    c.SetOut(2, status.ToString());
                    if (status == 0) c.Log("HTTP error: " + resp, LogLevel.Error);
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "data.xml", Icon = "tree-structure", Title = "Parse XML", Subtitle = "data",
                Category = NodeCategory.Data,
                Description = "Parses XML - RSS/Atom feeds, SOAP, any XML API - into JSON so a JSON Field node can pull values out. Repeated tags become arrays (an RSS feed's items land at rss.channel.item), attributes become @name keys. Wire an HTTP Request response straight in.",
                Inputs = new[] { Tx("xml") },
                Outputs = new[] { Tx("json") },
                Params = new[] { P("xml", "XML", ParamType.Multiline, "", "<rss>...</rss>  (or wire a HTTP response in)") },
                Exec = c =>
                {
                    var xml = c.InOr(0, c.Resolve(c.Param("xml")));
                    try { c.SetOut(0, xml.Trim().Length == 0 ? "{}" : XmlToJson(xml)); }
                    catch (Exception ex) { c.SetOut(0, ""); c.Log("data.xml parse error: " + ex.Message, LogLevel.Error); }
                },
            },
            new()
            {
                TypeId = "mail.send", Icon = "envelope-simple", Title = "Send Email", Subtitle = "mail",
                Category = NodeCategory.Action,
                Description = "Sends an email over SMTP. The general outgoing-mail transport email community nodes build on. Use an encrypted key for the password ({{secret.NAME}}).",
                Inputs = new[] { Ex(), Tx("to"), Tx("body") },
                Outputs = new[] { Ex("then"), Ex("failed"), Tx("result") },
                Params = new[]
                {
                    P("host", "SMTP host", ParamType.Text, "", "smtp.gmail.com"),
                    P("port", "Port", ParamType.Int, "587", "587 (STARTTLS) or 465 (SSL)"),
                    P("user", "Username", ParamType.Text, "", "you@example.com"),
                    P("pass", "Password", ParamType.Text, "", "{{secret.smtp_pass}}", secret: true),
                    P("from", "From", ParamType.Text, "", "blank = username"),
                    P("to", "To", ParamType.Text, "", "a@x.com, b@y.com"),
                    P("subject", "Subject", ParamType.Text, "", "supports {tokens}"),
                    P("body", "Body", ParamType.Multiline, "", "the message body, supports {tokens}"),
                },
                SummaryParam = "to",
                Exec = c =>
                {
                    try
                    {
                        var host = c.Resolve(c.Param("host"));
                        int port = Math.Clamp(c.ParamInt("port", 587), 1, 65535);
                        var user = c.Resolve(c.Param("user"));
                        var pass = c.Resolve(c.Param("pass"));
                        var from = c.Resolve(c.Param("from")); if (from.Length == 0) from = user;
                        var to = c.InOr(1, c.Resolve(c.Param("to")));
                        var msg = new MimeKit.MimeMessage();
                        msg.From.Add(MimeKit.MailboxAddress.Parse(from));
                        foreach (var a in to.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                            if (a.Trim().Length > 0) msg.To.Add(MimeKit.MailboxAddress.Parse(a.Trim()));
                        msg.Subject = c.Resolve(c.Param("subject"));
                        msg.Body = new MimeKit.TextPart("plain") { Text = c.InOr(2, c.Resolve(c.Param("body"))) };
                        using var smtp = new MailKit.Net.Smtp.SmtpClient { Timeout = 30000 };
                        smtp.Connect(host, port, MailKit.Security.SecureSocketOptions.Auto);
                        if (user.Length > 0) smtp.Authenticate(user, pass);
                        smtp.Send(msg);
                        smtp.Disconnect(true);
                        c.SetOut(2, "sent"); c.Pulse(0);
                    }
                    catch (Exception ex) { c.Log("mail.send error: " + ex.Message, LogLevel.Error); c.SetOut(2, "error: " + ex.Message); c.Pulse(1); }
                },
            },
            new()
            {
                TypeId = "mail.fetch", Icon = "tray-arrow-down", Title = "Fetch Email", Subtitle = "mail",
                Category = NodeCategory.Action,
                Description = "Reads UNSEEN emails from an IMAP inbox as JSON [{from,subject,date,text,id}] (then marks them seen). Wire an On Timer into this for an incoming-mail watcher. Use {{secret.NAME}} for the password. Branches 'got' when there is new mail, else 'none'.",
                Inputs = new[] { Ex() },
                Outputs = new[] { Ex("got"), Ex("none"), Tx("messages"), Tx("count") },
                Params = new[]
                {
                    P("host", "IMAP host", ParamType.Text, "", "imap.gmail.com"),
                    P("port", "Port", ParamType.Int, "993", "993 (SSL)"),
                    P("user", "Username", ParamType.Text, "", "you@example.com"),
                    P("pass", "Password", ParamType.Text, "", "{{secret.imap_pass}}", secret: true),
                    P("max", "Max per fetch", ParamType.Int, "10", "10"),
                    P("markseen", "Mark fetched as seen", ParamType.Bool, "true", ""),
                },
                SummaryParam = "host",
                Exec = c =>
                {
                    try
                    {
                        var host = c.Resolve(c.Param("host"));
                        int port = Math.Clamp(c.ParamInt("port", 993), 1, 65535);
                        var user = c.Resolve(c.Param("user"));
                        var pass = c.Resolve(c.Param("pass"));
                        int max = Math.Clamp(c.ParamInt("max", 10), 1, 100);
                        bool mark = c.Param("markseen") != "false";
                        var results = new List<object>();
                        using (var imap = new MailKit.Net.Imap.ImapClient { Timeout = 30000 })
                        {
                            imap.Connect(host, port, MailKit.Security.SecureSocketOptions.SslOnConnect);
                            imap.Authenticate(user, pass);
                            var inbox = imap.Inbox;
                            inbox.Open(mark ? MailKit.FolderAccess.ReadWrite : MailKit.FolderAccess.ReadOnly);
                            foreach (var uid in inbox.Search(MailKit.Search.SearchQuery.NotSeen).Take(max))
                            {
                                var m = inbox.GetMessage(uid);
                                results.Add(new Dictionary<string, object?>
                                {
                                    ["from"] = m.From.ToString(),
                                    ["subject"] = m.Subject ?? "",
                                    ["date"] = m.Date.ToString("o"),
                                    ["text"] = (m.TextBody ?? m.HtmlBody ?? "").Trim(),
                                    ["id"] = uid.Id.ToString(),
                                });
                                if (mark) inbox.AddFlags(uid, MailKit.MessageFlags.Seen, true);
                            }
                            imap.Disconnect(true);
                        }
                        c.SetOut(2, System.Text.Json.JsonSerializer.Serialize(results));
                        c.SetOut(3, results.Count.ToString());
                        c.Pulse(results.Count > 0 ? 0 : 1);
                    }
                    catch (Exception ex) { c.Log("mail.fetch error: " + ex.Message, LogLevel.Error); c.SetOut(2, "[]"); c.SetOut(3, "0"); c.Pulse(1); }
                },
            },
            new()
            {
                TypeId = "file.read", Icon = "folder-open", Title = "Read File", Subtitle = "file",
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
                TypeId = "zim.info", Icon = "books", Title = "ZIM Info", Subtitle = "zim", Category = NodeCategory.Storage,
                Description = "Opens a .zim archive (offline Wikipedia / Kiwix content) and reports its title and article count. Branches to 'missing' if the file isn't found. Set the .zim file in the inspector (relative paths live under ~/ircuitry/files).",
                Inputs = new[] { Ex() },
                Outputs = new[] { Ex("then"), Ex("missing"), Tx("info"), Nm("articles") },
                Params = new[] { P("path", "ZIM file", ParamType.Text, "", "wikipedia.zim or /abs/path.zim") },
                SummaryParam = "path",
                Exec = c =>
                {
                    var path = ResolveFile(c.Resolve(c.Param("path")));
                    if (path.Length == 0 || !File.Exists(path)) { c.SetOut(2, ""); c.SetOut(3, "0"); c.Pulse(1); return; }
                    try
                    {
                        using var z = ZimArchive.Open(path);
                        string title = z.Metadata("Title");
                        c.SetOut(2, (title.Length > 0 ? title : Path.GetFileNameWithoutExtension(path)) + " - " + z.ArticleCount + " articles");
                        c.SetOut(3, z.ArticleCount.ToString());
                        c.Pulse(0);
                    }
                    catch (Exception ex) { c.Log("ZIM: " + ex.Message, LogLevel.Error); c.SetOut(2, ""); c.SetOut(3, "0"); c.Pulse(1); }
                },
            },
            new()
            {
                TypeId = "zim.search", Icon = "magnifying-glass", Title = "ZIM Search", Subtitle = "zim", Category = NodeCategory.Storage,
                Description = "Searches a .zim archive's article TITLES (prefix match) and returns a JSON array of {title, path}. Wire into Ask AI (or a Programmer AI) to give a bot an offline-wiki search tool. Set the .zim file in the inspector.",
                Inputs = new[] { Ex(), Tx("query") },
                Outputs = new[] { Ex("then"), Tx("results"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "search_wiki", "search_wiki"),
                    P("path", "ZIM file", ParamType.Text, "", "wikipedia.zim or /abs/path.zim"),
                    P("count", "Max results", ParamType.Int, "8", "8"),
                    P("query", "Query", ParamType.Text, "", "title to look up"),
                },
                SummaryParam = "path",
                Exec = c =>
                {
                    var path = ResolveFile(c.Resolve(c.Param("path")));
                    string query = Arg(c, 1, "query");
                    try
                    {
                        if (path.Length == 0 || !File.Exists(path)) { c.SetOut(1, "[]"); c.Pulse(0); return; }
                        using var z = ZimArchive.Open(path);
                        var hits = z.SearchTitles(query, Math.Clamp(c.ParamInt("count", 8), 1, 50));
                        var sb = new StringBuilder("[");
                        for (int i = 0; i < hits.Count; i++)
                        {
                            if (i > 0) sb.Append(',');
                            sb.Append("{\"title\":").Append(JsonStr(hits[i].Title)).Append(",\"path\":").Append(JsonStr(hits[i].Path)).Append('}');
                        }
                        c.SetOut(1, sb.Append(']').ToString());
                        c.Pulse(0);
                    }
                    catch (Exception ex) { c.Log("ZIM: " + ex.Message, LogLevel.Error); c.SetOut(1, "[]"); c.Pulse(0); }
                },
            },
            new()
            {
                TypeId = "zim.article", Icon = "article", Title = "ZIM Article", Subtitle = "zim", Category = NodeCategory.Storage,
                Description = "Reads one article from a .zim archive by title (or a namespaced path like A/Android) and returns its text - HTML stripped to plain text by default. Branches to 'missing' if not found. Wire into Ask AI as an offline-wiki reader. Set the .zim file in the inspector.",
                Inputs = new[] { Ex(), Tx("title") },
                Outputs = new[] { Ex("then"), Ex("missing"), Tx("content"), Tx("found"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "read_article", "read_article"),
                    P("path", "ZIM file", ParamType.Text, "", "wikipedia.zim or /abs/path.zim"),
                    P("html", "Keep HTML", ParamType.Bool, "false", "off = strip to plain text"),
                    P("maxChars", "Max characters", ParamType.Int, "4000", "0 = whole article"),
                    P("title", "Title", ParamType.Text, "", "article title to fetch"),
                },
                SummaryParam = "path",
                Exec = c =>
                {
                    var path = ResolveFile(c.Resolve(c.Param("path")));
                    string title = Arg(c, 1, "title");
                    try
                    {
                        if (path.Length == 0 || !File.Exists(path)) { c.SetOut(2, ""); c.SetOut(3, ""); c.Pulse(1); return; }
                        using var z = ZimArchive.Open(path);
                        var e = z.FindTitle(title);
                        if (e == null) { c.SetOut(2, ""); c.SetOut(3, ""); c.Pulse(1); return; }
                        string text = z.ReadText(e.Value);
                        if (!c.ParamBool("html")) text = StripHtml(text);
                        int max = c.ParamInt("maxChars", 4000);
                        if (max > 0 && text.Length > max) text = text[..max].TrimEnd() + " …";
                        c.SetOut(2, text);
                        c.SetOut(3, e.Value.Title);
                        c.Pulse(0);
                    }
                    catch (Exception ex) { c.Log("ZIM: " + ex.Message, LogLevel.Error); c.SetOut(2, ""); c.SetOut(3, ""); c.Pulse(1); }
                },
            },
            new()
            {
                TypeId = "file.write", Icon = "floppy-disk", Title = "Write File", Subtitle = "file",
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
                TypeId = "file.ical", Icon = "calendar", Title = "Calendar (iCal)", Subtitle = "calendar",
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
            // ===================== CODE (program a whole codebase; each is also an AI tool) =====================
            // Every code.* node carries a Tool output, so wiring it into Ask AI (or the Programmer AI) lets the
            // model call it. The 'root' param is the sandbox: CodeTools.Confine() keeps every path inside it.
            new()
            {
                TypeId = "code.read", Icon = "book-open", Title = "Read File", Subtitle = "code", Category = NodeCategory.Code,
                Description = "Reads a text file from the codebase and returns its contents (optionally a line range). Paths are relative to the codebase folder. Branches to 'missing' if the file isn't there.",
                Inputs = new[] { Ex(), Tx("path") },
                Outputs = new[] { Ex("then"), Ex("missing"), Tx("content"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "read_file", "read_file"),
                    RootParam(),
                    P("path", "Path", ParamType.Text, "", "src/main.py"),
                    P("startLine", "From line (optional)", ParamType.Int, "0", "0 = start"),
                    P("endLine", "To line (optional)", ParamType.Int, "0", "0 = end"),
                },
                SummaryParam = "path",
                Exec = c =>
                {
                    try
                    {
                        string root = CodeRoot(c), path = Arg(c, 1, "path");
                        if (!CodeTools.Exists(root, path)) { c.SetOut(2, ""); c.Pulse(1); return; }
                        c.SetOut(2, CodeTools.Read(root, path, c.ParamInt("startLine", 0), c.ParamInt("endLine", 0)));
                        c.Pulse(0);
                    }
                    catch (Exception ex) { c.SetOut(2, "error: " + ex.Message); c.Log("code: " + ex.Message, LogLevel.Error); c.Pulse(1); }
                },
            },
            new()
            {
                TypeId = "code.write", Icon = "note-pencil", Title = "Write File", Subtitle = "code", Category = NodeCategory.Code,
                Description = "Creates or overwrites a file with the given content (parent folders are made). Use for new files or full rewrites; prefer Edit File for small changes. Paths are relative to the codebase folder.",
                Inputs = new[] { Ex(), Tx("path"), Tx("content") },
                Outputs = new[] { Ex("then"), Tx("result"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "write_file", "write_file"),
                    RootParam(),
                    P("path", "Path", ParamType.Text, "", "src/new.py"),
                    P("content", "Content", ParamType.Multiline, "", "file contents"),
                },
                SummaryParam = "path",
                Exec = c => CodeRun(c, 1, 0, () => { string root = CodeRoot(c), p = Arg(c, 1, "path"); CodeTools.Write(root, p, Arg(c, 2, "content")); return "wrote " + p; }),
            },
            new()
            {
                TypeId = "code.edit", Icon = "pencil-line", Title = "Edit File", Subtitle = "code", Category = NodeCategory.Code,
                Description = "Replaces an exact piece of text in a file with new text - the precise way to change code. By default 'find' must be unique (one edit); turn on Replace all to change every occurrence. Paths are relative to the codebase folder.",
                Inputs = new[] { Ex(), Tx("path"), Tx("find"), Tx("replace") },
                Outputs = new[] { Ex("then"), Tx("result"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "edit_file", "edit_file"),
                    RootParam(),
                    P("path", "Path", ParamType.Text, "", "src/main.py"),
                    P("find", "Find (exact)", ParamType.Multiline, "", "text to replace"),
                    P("replace", "Replace with", ParamType.Multiline, "", "new text"),
                    P("all", "Replace all", ParamType.Bool, "false", ""),
                },
                SummaryParam = "path",
                Exec = c => CodeRun(c, 1, 0, () =>
                {
                    string root = CodeRoot(c), p = Arg(c, 1, "path");
                    int n = CodeTools.Edit(root, p, Arg(c, 2, "find"), Arg(c, 3, "replace"), c.ParamBool("all"));
                    return $"edited {p} ({n} replacement{(n == 1 ? "" : "s")})";
                }),
            },
            new()
            {
                TypeId = "code.insert", Icon = "plus", Title = "Insert Lines", Subtitle = "code", Category = NodeCategory.Code,
                Description = "Inserts text into a file after a given line number (0 = at the very top). Good for adding imports, functions or config blocks. Paths are relative to the codebase folder.",
                Inputs = new[] { Ex(), Tx("path"), Nm("after"), Tx("content") },
                Outputs = new[] { Ex("then"), Tx("result"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "insert_lines", "insert_lines"),
                    RootParam(),
                    P("path", "Path", ParamType.Text, "", "src/main.py"),
                    P("after", "After line", ParamType.Int, "0", "0 = top"),
                    P("content", "Content", ParamType.Multiline, "", "lines to insert"),
                },
                SummaryParam = "path",
                Exec = c => CodeRun(c, 1, 0, () =>
                {
                    string root = CodeRoot(c), p = Arg(c, 1, "path");
                    int after = int.TryParse(Arg(c, 2, "after"), out var a) ? a : c.ParamInt("after", 0);
                    int at = CodeTools.Insert(root, p, after, Arg(c, 3, "content"));
                    return $"inserted into {p} at line {at}";
                }),
            },
            new()
            {
                TypeId = "code.append", Icon = "paperclip", Title = "Append to File", Subtitle = "code", Category = NodeCategory.Code,
                Description = "Appends text to the end of a file (creating it if needed). Paths are relative to the codebase folder.",
                Inputs = new[] { Ex(), Tx("path"), Tx("content") },
                Outputs = new[] { Ex("then"), Tx("result"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "append_file", "append_file"),
                    RootParam(),
                    P("path", "Path", ParamType.Text, "", "notes.md"),
                    P("content", "Content", ParamType.Multiline, "", "text to append"),
                },
                SummaryParam = "path",
                Exec = c => CodeRun(c, 1, 0, () => { string root = CodeRoot(c), p = Arg(c, 1, "path"); CodeTools.Append(root, p, Arg(c, 2, "content")); return "appended to " + p; }),
            },
            new()
            {
                TypeId = "code.delete", Icon = "trash", Title = "Delete Path", Subtitle = "code", Category = NodeCategory.Code,
                Description = "Deletes a file or a folder (recursively) inside the codebase. Refuses to delete the codebase root itself. Paths are relative to the codebase folder.",
                Inputs = new[] { Ex(), Tx("path") },
                Outputs = new[] { Ex("then"), Tx("result"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "delete_path", "delete_path"),
                    RootParam(),
                    P("path", "Path", ParamType.Text, "", "old/temp.py"),
                },
                SummaryParam = "path",
                Exec = c => CodeRun(c, 1, 0, () => { string p = Arg(c, 1, "path"); CodeTools.Delete(CodeRoot(c), p); return "deleted " + p; }),
            },
            new()
            {
                TypeId = "code.move", Icon = "truck", Title = "Move / Rename", Subtitle = "code", Category = NodeCategory.Code,
                Description = "Moves or renames a file or folder within the codebase. Paths are relative to the codebase folder.",
                Inputs = new[] { Ex(), Tx("from"), Tx("to") },
                Outputs = new[] { Ex("then"), Tx("result"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "move_path", "move_path"),
                    RootParam(),
                    P("from", "From", ParamType.Text, "", "src/old.py"),
                    P("to", "To", ParamType.Text, "", "src/new.py"),
                },
                Exec = c => CodeRun(c, 1, 0, () => { string root = CodeRoot(c), a = Arg(c, 1, "from"), b = Arg(c, 2, "to"); CodeTools.Move(root, a, b); return $"moved {a} -> {b}"; }),
            },
            new()
            {
                TypeId = "code.copy", Icon = "bookmarks", Title = "Copy Path", Subtitle = "code", Category = NodeCategory.Code,
                Description = "Copies a file or folder (recursively) to a new path within the codebase. Paths are relative to the codebase folder.",
                Inputs = new[] { Ex(), Tx("from"), Tx("to") },
                Outputs = new[] { Ex("then"), Tx("result"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "copy_path", "copy_path"),
                    RootParam(),
                    P("from", "From", ParamType.Text, "", "src/a.py"),
                    P("to", "To", ParamType.Text, "", "src/b.py"),
                },
                Exec = c => CodeRun(c, 1, 0, () => { string root = CodeRoot(c), a = Arg(c, 1, "from"), b = Arg(c, 2, "to"); CodeTools.Copy(root, a, b); return $"copied {a} -> {b}"; }),
            },
            new()
            {
                TypeId = "code.mkdir", Icon = "folder", Title = "Make Directory", Subtitle = "code", Category = NodeCategory.Code,
                Description = "Creates a directory (and any missing parents) inside the codebase. Paths are relative to the codebase folder.",
                Inputs = new[] { Ex(), Tx("path") },
                Outputs = new[] { Ex("then"), Tx("result"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "make_dir", "make_dir"),
                    RootParam(),
                    P("path", "Path", ParamType.Text, "", "src/utils"),
                },
                SummaryParam = "path",
                Exec = c => CodeRun(c, 1, 0, () => { string p = Arg(c, 1, "path"); CodeTools.Mkdir(CodeRoot(c), p); return "created " + p; }),
            },
            new()
            {
                TypeId = "code.list", Icon = "file-text", Title = "List Directory", Subtitle = "code", Category = NodeCategory.Code,
                Description = "Lists the files and subfolders directly inside a directory (folders end with /). Paths are relative to the codebase folder; blank lists the root.",
                Inputs = new[] { Ex(), Tx("path") },
                Outputs = new[] { Ex("then"), Tx("result"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "list_dir", "list_dir"),
                    RootParam(),
                    P("path", "Path", ParamType.Text, "", "src (blank = root)"),
                },
                Exec = c => CodeRun(c, 1, 0, () => CodeTools.List(CodeRoot(c), Arg(c, 1, "path"))),
            },
            new()
            {
                TypeId = "code.tree", Icon = "tree", Title = "Project Tree", Subtitle = "code", Category = NodeCategory.Code,
                Description = "Shows a compact directory tree (skipping .git, node_modules, build output, etc.) so an AI can orient itself in the codebase. Paths are relative to the codebase folder.",
                Inputs = new[] { Ex(), Tx("path") },
                Outputs = new[] { Ex("then"), Tx("result"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "project_tree", "project_tree"),
                    RootParam(),
                    P("path", "Path", ParamType.Text, "", "(blank = root)"),
                    P("depth", "Max depth", ParamType.Int, "3", "3"),
                },
                Exec = c => CodeRun(c, 1, 0, () => CodeTools.Tree(CodeRoot(c), Arg(c, 1, "path"), c.ParamInt("depth", 3))),
            },
            new()
            {
                TypeId = "code.glob", Icon = "magnifying-glass", Title = "Find Files", Subtitle = "code", Category = NodeCategory.Code,
                Description = "Finds files by glob pattern (e.g. **/*.cs, src/*.py, **/test_*.py), newest first. Paths are relative to the codebase folder.",
                Inputs = new[] { Ex(), Tx("pattern") },
                Outputs = new[] { Ex("then"), Tx("result"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "find_files", "find_files"),
                    RootParam(),
                    P("pattern", "Glob", ParamType.Text, "**/*", "**/*.cs"),
                },
                SummaryParam = "pattern",
                Exec = c => CodeRun(c, 1, 0, () => { var r = CodeTools.Glob(CodeRoot(c), Arg(c, 1, "pattern")); return r.Length == 0 ? "(no files match)" : r; }),
            },
            new()
            {
                TypeId = "code.grep", Icon = "magnifying-glass", Title = "Search Code", Subtitle = "code", Category = NodeCategory.Code,
                Description = "Searches the codebase for a regular expression and returns matches as path:line: text. Optionally restrict to files matching a glob. The fast way to find where something is defined or used.",
                Inputs = new[] { Ex(), Tx("pattern"), Tx("glob") },
                Outputs = new[] { Ex("then"), Tx("result"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "search_code", "search_code"),
                    RootParam(),
                    P("pattern", "Regex", ParamType.Text, "", "TODO|FIXME"),
                    P("glob", "Only files (glob, optional)", ParamType.Text, "", "**/*.py"),
                },
                SummaryParam = "pattern",
                Exec = c => CodeRun(c, 1, 0, () => { var r = CodeTools.Grep(CodeRoot(c), Arg(c, 1, "pattern"), Arg(c, 2, "glob")); return r.Length == 0 ? "(no matches)" : r; }),
            },
            new()
            {
                TypeId = "code.replace", Icon = "repeat", Title = "Replace Across Files", Subtitle = "code", Category = NodeCategory.Code,
                Description = "Replaces an exact string with another across every matching file (optionally filtered by a glob). Returns the number of files changed. Powerful - keep the find text specific.",
                Inputs = new[] { Ex(), Tx("find"), Tx("replace"), Tx("glob") },
                Outputs = new[] { Ex("then"), Tx("result"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "replace_across", "replace_across"),
                    RootParam(),
                    P("find", "Find (exact)", ParamType.Multiline, "", "old string"),
                    P("replace", "Replace with", ParamType.Multiline, "", "new string"),
                    P("glob", "Only files (glob, optional)", ParamType.Text, "", "**/*.cs"),
                },
                Exec = c => CodeRun(c, 1, 0, () =>
                {
                    int n = CodeTools.ReplaceAcross(CodeRoot(c), Arg(c, 1, "find"), Arg(c, 2, "replace"), Arg(c, 3, "glob"));
                    return $"changed {n} file{(n == 1 ? "" : "s")}";
                }),
            },
            new()
            {
                TypeId = "code.stat", Icon = "chart-bar", Title = "File Info", Subtitle = "code", Category = NodeCategory.Code,
                Description = "Reports whether a path is a file or folder, plus size, line count and last-modified time. Paths are relative to the codebase folder.",
                Inputs = new[] { Ex(), Tx("path") },
                Outputs = new[] { Ex("then"), Tx("result"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "file_info", "file_info"),
                    RootParam(),
                    P("path", "Path", ParamType.Text, "", "src/main.py"),
                },
                SummaryParam = "path",
                Exec = c => CodeRun(c, 1, 0, () => CodeTools.Stat(CodeRoot(c), Arg(c, 1, "path"))),
            },
            new()
            {
                TypeId = "code.exists", Icon = "question", Title = "Path Exists", Subtitle = "code", Category = NodeCategory.Code,
                Description = "Checks whether a file or folder exists in the codebase. Branches yes/no and returns true/false. Paths are relative to the codebase folder.",
                Inputs = new[] { Ex(), Tx("path") },
                Outputs = new[] { Ex("yes"), Ex("no"), Tx("result"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "path_exists", "path_exists"),
                    RootParam(),
                    P("path", "Path", ParamType.Text, "", "README.md"),
                },
                SummaryParam = "path",
                Exec = c =>
                {
                    try { bool ex = CodeTools.Exists(CodeRoot(c), Arg(c, 1, "path")); c.SetOut(2, ex ? "true" : "false"); c.Pulse(ex ? 0 : 1); }
                    catch (Exception e) { c.SetOut(2, "error: " + e.Message); c.Log("code: " + e.Message, LogLevel.Error); c.Pulse(1); }
                },
            },
            new()
            {
                TypeId = "code.diff", Icon = "shuffle", Title = "Diff Files", Subtitle = "code", Category = NodeCategory.Code,
                Description = "Shows the line differences between two files in the codebase (- removed, + added). Paths are relative to the codebase folder.",
                Inputs = new[] { Ex(), Tx("a"), Tx("b") },
                Outputs = new[] { Ex("then"), Tx("result"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "diff_files", "diff_files"),
                    RootParam(),
                    P("a", "File A", ParamType.Text, "", "src/old.py"),
                    P("b", "File B", ParamType.Text, "", "src/new.py"),
                },
                Exec = c => CodeRun(c, 1, 0, () => CodeTools.Diff(CodeRoot(c), Arg(c, 1, "a"), Arg(c, 2, "b"))),
            },
            new()
            {
                TypeId = "code.shell", Icon = "keyboard", Title = "Run Command", Subtitle = "code", Category = NodeCategory.Code,
                Description = "Runs a shell command on THIS machine with its working directory set to the codebase folder (build, test, lint, git, etc.). The working directory is set to the codebase, but the command itself is NOT sandboxed - it has your full access. For isolation use Run in Container instead.",
                Inputs = new[] { Ex(), Tx("command") },
                Outputs = new[] { Ex("then"), Tx("result"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "run_command", "run_command"),
                    RootParam(),
                    P("command", "Command", ParamType.Text, "", "npm test"),
                    P("timeout", "Timeout (s)", ParamType.Int, "30", "30"),
                },
                SummaryParam = "command",
                Exec = c => CodeRun(c, 1, 0, () => { var r = CodeTools.Run(CodeRoot(c), Arg(c, 1, "command"), c.ParamInt("timeout", 30)); return r.Length == 0 ? "(no output)" : r; }),
            },
            // ---- container sandbox: run commands in an isolated Docker/Podman "own OS", no host access except
            // the mounted folder, no network unless allowed. One-shot (code.container) and managed lifecycle
            // (container.start/exec/stop). exec/one-shot carry a Tool output so an Ask AI can drive them - the
            // Programmer AI can be rebuilt from nodes, containers and all. Refuses if no runtime is available.
            new()
            {
                TypeId = "code.container", Icon = "cube", Title = "Run in Container", Subtitle = "sandbox", Category = NodeCategory.Code,
                Description = "Runs a shell command inside a throwaway, isolated container (Docker/Podman) - its own OS, no access to your machine except the mounted Codebase folder, and no network unless you allow it. Carries a Tool output, so wire it into Ask AI as a fully sandboxed run_command. Refuses if no container runtime is available.",
                Inputs = new[] { Ex(), Tx("command") },
                Outputs = new[] { Ex("then"), Tx("result"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "run_command", "run_command"),
                    RootParam(),
                    P("image", "Container image", ParamType.Text, "alpine", "python:3.12 · node:20 · rust:1 · your dev image"),
                    P("network", "Allow network", ParamType.Bool, "false", "off = fully offline (most isolated)"),
                    P("timeout", "Timeout (s)", ParamType.Int, "120", "120"),
                },
                SummaryParam = "image",
                Exec = c => CodeRun(c, 1, 0, () =>
                {
                    var (_, o) = Ircuitry.Net.ContainerEngine.Run(c.Resolve(c.Param("image")), HostDir(CodeRoot(c)), Arg(c, 1, "command"), c.ParamInt("timeout", 120), c.ParamBool("network"));
                    return o.Length == 0 ? "(no output)" : o;
                }),
            },
            new()
            {
                TypeId = "container.start", Icon = "shipping-container", Title = "Start Container", Subtitle = "sandbox", Category = NodeCategory.Code,
                Description = "Starts a long-lived, isolated container (Docker/Podman) you can run many commands in - state persists between them (installed packages, files). Idempotent by name: re-running reuses the running one. Outputs a 'container' handle for Container Exec / Stop Container. No network unless allowed; refuses if no runtime.",
                Inputs = new[] { Ex(), Tx("name") },
                Outputs = new[] { Ex("then"), Tx("container") },
                Params = new[]
                {
                    P("name", "Container name", ParamType.Text, "devbox", "a stable name - reused if already running"),
                    RootParam(),
                    P("image", "Container image", ParamType.Text, "alpine", "python:3.12 · node:20 · your dev image"),
                    P("network", "Allow network", ParamType.Bool, "false", "off = fully offline"),
                },
                SummaryParam = "image",
                Exec = c =>
                {
                    var (ok, handle) = Ircuitry.Net.ContainerEngine.Start(c.Resolve(c.Param("image")), c.InOr(1, c.Resolve(c.Param("name"))), HostDir(CodeRoot(c)), c.ParamBool("network"));
                    if (!ok) c.Log("Start Container: " + handle, LogLevel.Error);
                    c.SetOut(1, ok ? handle : "");
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "container.exec", Icon = "cube", Title = "Container Exec", Subtitle = "sandbox", Category = NodeCategory.Code,
                Description = "Runs a command inside a running container (started by Start Container) - state persists between calls. Set the container name to the one you started. Carries a Tool output, so an Ask AI can drive a persistent sandbox: rebuild the Programmer AI against a real dev box.",
                Inputs = new[] { Ex(), Tx("command") },
                Outputs = new[] { Ex("then"), Tx("result"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "run_command", "run_command"),
                    P("container", "Container", ParamType.Text, "devbox", "name/handle from Start Container"),
                    P("command", "Command", ParamType.Text, "", "npm test"),
                    P("timeout", "Timeout (s)", ParamType.Int, "120", "120"),
                },
                SummaryParam = "container",
                Exec = c => CodeRun(c, 1, 0, () =>
                {
                    var (_, o) = Ircuitry.Net.ContainerEngine.ExecIn(c.Resolve(c.Param("container")), Arg(c, 1, "command"), c.ParamInt("timeout", 120));
                    return o.Length == 0 ? "(no output)" : o;
                }),
            },
            new()
            {
                TypeId = "container.stop", Icon = "stop", Title = "Stop Container", Subtitle = "sandbox", Category = NodeCategory.Code,
                Description = "Stops and removes a container started by Start Container. Set/wire the container name to tear it down (idle containers are also cleaned up when ircuitry exits).",
                Inputs = new[] { Ex(), Tx("container") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("container", "Container", ParamType.Text, "devbox", "name from Start Container") },
                Exec = c =>
                {
                    var (ok, o) = Ircuitry.Net.ContainerEngine.Stop(c.InOr(1, c.Resolve(c.Param("container"))));
                    if (!ok) c.Log("Stop Container: " + o, LogLevel.System);
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "code.outline", Icon = "compass", Title = "Code Outline", Subtitle = "code", Category = NodeCategory.Code,
                Description = "Lists the top-level definitions (classes, functions, methods) in a source file as line: signature, so an AI can navigate large files. Paths are relative to the codebase folder.",
                Inputs = new[] { Ex(), Tx("path") },
                Outputs = new[] { Ex("then"), Tx("result"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "code_outline", "code_outline"),
                    RootParam(),
                    P("path", "Path", ParamType.Text, "", "src/main.py"),
                },
                SummaryParam = "path",
                Exec = c => CodeRun(c, 1, 0, () => { var r = CodeTools.Outline(CodeRoot(c), Arg(c, 1, "path")); return r.Length == 0 ? "(no definitions found)" : r; }),
            },
            new()
            {
                TypeId = "code.stats", Icon = "chart-line-up", Title = "Project Stats", Subtitle = "code", Category = NodeCategory.Code,
                Description = "Summarises the codebase: file count, total lines, size, and the most common file types. A quick overview before diving in.",
                Inputs = new[] { Ex() },
                Outputs = new[] { Ex("then"), Tx("result"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "project_stats", "project_stats"),
                    RootParam(),
                },
                Exec = c => CodeRun(c, 1, 0, () => CodeTools.Stats(CodeRoot(c))),
            },

            // ===================== MEDIA + ARCHIVE (manage media files; zip / unzip) =====================
            new()
            {
                TypeId = "media.info", Icon = "image", Title = "Image Info", Subtitle = "media", Category = NodeCategory.Storage,
                Description = "Reads an image's width, height and format straight from its header (PNG, JPEG, GIF, BMP, WebP) - no decoding, no dependencies. Relative paths live under ~/ircuitry/files. Branches to 'not image' if unrecognised.",
                Inputs = new[] { Ex(), Tx("path") },
                Outputs = new[] { Ex("then"), Ex("notimage"), Tx("info"), Nm("width"), Nm("height"), Tx("format"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "image_info", "image_info"),
                    P("path", "Path", ParamType.Text, "", "pic.png or /abs/pic.jpg"),
                },
                SummaryParam = "path",
                Exec = c =>
                {
                    try
                    {
                        string full = ResolveFile(Arg(c, 1, "path"));
                        if (full.Length == 0 || !File.Exists(full)) { c.SetOut(2, ""); c.Pulse(1); return; }
                        long bytes = new FileInfo(full).Length;
                        var info = CodeTools.ImageInfo(full);
                        if (info == null) { c.SetOut(2, $"not a recognised image · {bytes / 1024} KB"); c.Pulse(1); return; }
                        var (w, h, fmt) = info.Value;
                        c.SetOut(2, $"{w}x{h} {fmt} · {bytes / 1024} KB");
                        c.SetOut(3, w.ToString()); c.SetOut(4, h.ToString()); c.SetOut(5, fmt);
                        c.Pulse(0);
                    }
                    catch (Exception ex) { c.SetOut(2, "error: " + ex.Message); c.Log("media: " + ex.Message, LogLevel.Error); c.Pulse(1); }
                },
            },
            new()
            {
                TypeId = "media.download", Icon = "tray", Title = "Download File", Subtitle = "media", Category = NodeCategory.Storage,
                Description = "Downloads a URL (image, audio, any file) to a local file under ~/ircuitry/files and outputs the saved path. Branches to 'failed' on error.",
                Inputs = new[] { Ex(), Tx("url"), Tx("filename") },
                Outputs = new[] { Ex("then"), Ex("failed"), Tx("path"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "download_file", "download_file"),
                    P("url", "URL", ParamType.Text, "", "https://…/pic.png"),
                    P("filename", "Save as", ParamType.Text, "download.bin", "media/pic.png"),
                },
                SummaryParam = "url",
                Exec = c =>
                {
                    try
                    {
                        string url = Arg(c, 1, "url"), fn = Arg(c, 2, "filename");
                        if (fn.Length == 0) fn = "download.bin";
                        string full = ResolveFile(fn);
                        if (url.Length == 0 || full.Length == 0) { c.SetOut(2, ""); c.Log("download: bad url or path escapes the files sandbox", LogLevel.Error); c.Pulse(1); return; }
                        var (ok, gotInfo) = Ircuitry.Net.Upload.Download(url, full);
                        if (!ok) { c.SetOut(2, ""); c.Log("download failed: " + gotInfo, LogLevel.Error); c.Pulse(1); return; }
                        c.SetOut(2, full); c.Pulse(0);
                    }
                    catch (Exception ex) { c.SetOut(2, ""); c.Log("download error: " + ex.Message, LogLevel.Error); c.Pulse(1); }
                },
            },
            new()
            {
                TypeId = "media.organize", Icon = "folders", Title = "Organize Media", Subtitle = "media", Category = NodeCategory.Storage,
                Description = "Moves, copies, renames or deletes a media file under ~/ircuitry/files - handy for sorting downloads into folders.",
                Inputs = new[] { Ex(), Tx("path"), Tx("to") },
                Outputs = new[] { Ex("then"), Tx("result"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "organize_media", "organize_media"),
                    P("op", "Action", ParamType.Choice, "move", "", new[] { "move", "copy", "rename", "delete" }),
                    P("path", "Path", ParamType.Text, "", "downloads/a.png"),
                    P("to", "To (move/copy/rename)", ParamType.Text, "", "images/a.png"),
                },
                SummaryParam = "op",
                Exec = c => CodeRun(c, 1, 0, () =>
                {
                    string op = c.Param("op"), a = ResolveFile(Arg(c, 1, "path"));
                    if (a.Length == 0) throw new Exception("path escapes the files sandbox");
                    if (op == "delete")
                    {
                        if (Directory.Exists(a)) Directory.Delete(a, true);
                        else if (File.Exists(a)) File.Delete(a);
                        else throw new Exception("no such file");
                        return "deleted " + Arg(c, 1, "path");
                    }
                    string b = ResolveFile(Arg(c, 2, "to"));
                    if (b.Length == 0) throw new Exception("destination escapes the files sandbox");
                    string? d = Path.GetDirectoryName(b); if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
                    if (op == "copy") File.Copy(a, b, true); else File.Move(a, b, true);
                    return $"{op} {Arg(c, 1, "path")} -> {Arg(c, 2, "to")}";
                }),
            },
            new()
            {
                TypeId = "media.transform", Icon = "sparkle", Title = "Transform Image", Subtitle = "media", Category = NodeCategory.Storage,
                Description = "Resizes, converts or rotates an image using ImageMagick or ffmpeg if installed. Outputs the new file's path; branches to 'failed' (with a clear message) when no image tool is available. Relative paths live under ~/ircuitry/files.",
                Inputs = new[] { Ex(), Tx("path"), Tx("out") },
                Outputs = new[] { Ex("then"), Ex("failed"), Tx("path"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "transform_image", "transform_image"),
                    P("op", "Operation", ParamType.Choice, "resize", "", new[] { "resize", "convert", "rotate" }),
                    P("path", "Source", ParamType.Text, "", "pic.png"),
                    P("out", "Output", ParamType.Text, "out.png", "small.jpg"),
                    P("arg", "Size / degrees", ParamType.Text, "512x512", "512x512 · 90"),
                },
                SummaryParam = "op",
                Exec = c =>
                {
                    try
                    {
                        string src = ResolveFile(Arg(c, 1, "path")), outp = ResolveFile(Arg(c, 2, "out"));
                        if (src.Length == 0 || outp.Length == 0) { c.SetOut(2, ""); c.Log("transform: path escapes the files sandbox", LogLevel.Error); c.Pulse(1); return; }
                        var (ok, msg) = CodeTools.TransformImage(src, outp, c.Param("op"), c.Resolve(c.Param("arg")));
                        if (!ok) { c.SetOut(2, ""); c.Log("transform: " + msg, LogLevel.Error); c.Pulse(1); return; }
                        c.SetOut(2, msg); c.Pulse(0);
                    }
                    catch (Exception ex) { c.SetOut(2, ""); c.Log("transform error: " + ex.Message, LogLevel.Error); c.Pulse(1); }
                },
            },
            new()
            {
                TypeId = "archive.zip", Icon = "package", Title = "Zip", Subtitle = "archive", Category = NodeCategory.Storage,
                Description = "Compresses a folder (or a single file) into a .zip archive. Relative paths live under ~/ircuitry/files; absolute paths are honoured. As an AI tool, pass a source path and a zip path.",
                Inputs = new[] { Ex(), Tx("source"), Tx("zip") },
                Outputs = new[] { Ex("then"), Tx("result"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "zip", "zip"),
                    P("source", "Source folder / file", ParamType.Text, "", "myfolder"),
                    P("zip", "Zip path", ParamType.Text, "archive.zip", "archive.zip"),
                },
                SummaryParam = "source",
                Exec = c => CodeRun(c, 1, 0, () =>
                {
                    string s = ResolveFile(Arg(c, 1, "source")), z = ResolveFile(Arg(c, 2, "zip"));
                    if (s.Length == 0 || z.Length == 0) throw new Exception("path escapes the files sandbox");
                    long n = CodeTools.ZipAbsolute(s, z);
                    return $"zipped to {Arg(c, 2, "zip")} ({n / 1024} KB)";
                }),
            },
            new()
            {
                TypeId = "archive.unzip", Icon = "folder-open", Title = "Unzip", Subtitle = "archive", Category = NodeCategory.Storage,
                Description = "Extracts a .zip archive into a folder (created if needed), safely rejecting any entry that would escape the folder (zip-slip). Relative paths live under ~/ircuitry/files.",
                Inputs = new[] { Ex(), Tx("zip"), Tx("dest") },
                Outputs = new[] { Ex("then"), Tx("result"), To("tool") },
                Params = new[]
                {
                    P("name", "Tool name (for AI)", ParamType.Text, "unzip", "unzip"),
                    P("zip", "Zip path", ParamType.Text, "", "archive.zip"),
                    P("dest", "Extract to", ParamType.Text, "unzipped", "unzipped"),
                },
                SummaryParam = "zip",
                Exec = c => CodeRun(c, 1, 0, () =>
                {
                    string z = ResolveFile(Arg(c, 1, "zip")), d = ResolveFile(Arg(c, 2, "dest"));
                    if (z.Length == 0 || d.Length == 0) throw new Exception("path escapes the files sandbox");
                    int n = CodeTools.UnzipAbsolute(z, d);
                    return $"extracted {n} files to {Arg(c, 2, "dest")}";
                }),
            },
            // ===================== TEXT / NUMBER / TIME TOOLKIT (building blocks for community recipes) =====================
            new()
            {
                TypeId = "data.encode", Icon = "calculator", Title = "Encode / Decode", Subtitle = "data", Category = NodeCategory.Data,
                Description = "Encodes or decodes text: Base64, Base32, hex, URL, HTML entities, JSON string, binary, Morse, or ROT13. JSON escapes quotes/newlines so text can go inside a JSON request body.",
                Inputs = new[] { Tx("text") }, Outputs = new[] { Tx("result") },
                Params = new[]
                {
                    P("op", "Scheme", ParamType.Choice, "base64", "", new[] { "base64", "base32", "hex", "url", "html", "json", "binary", "morse", "rot13", "zwsp" }),
                    P("mode", "Mode", ParamType.Choice, "encode", "", new[] { "encode", "decode" }),
                },
                SummaryParam = "op",
                Exec = c => c.SetOut(0, TextTools.Encode(c.Param("op"), c.Param("mode"), c.In(0))),
            },
            new()
            {
                TypeId = "data.hash", Icon = "lock-key", Title = "Hash", Subtitle = "data", Category = NodeCategory.Data,
                Description = "Hashes text with MD5, SHA-1, SHA-256, SHA-512, or a CRC32 checksum.",
                Inputs = new[] { Tx("text") }, Outputs = new[] { Tx("result") },
                Params = new[] { P("op", "Algorithm", ParamType.Choice, "sha256", "", new[] { "md5", "sha1", "sha256", "sha512", "crc32" }) },
                SummaryParam = "op",
                Exec = c => c.SetOut(0, TextTools.Hash(c.Param("op"), c.In(0))),
            },
            new()
            {
                TypeId = "data.case", Icon = "translate", Title = "Change Case", Subtitle = "data", Category = NodeCategory.Data,
                Description = "Recases text: UPPER, lower, Title, Sentence, camelCase, snake_case, kebab-case, mOcKiNg, l33t, clap\U0001F44Fcase, and more.",   // intentional unicode (clap)
                Inputs = new[] { Tx("text") }, Outputs = new[] { Tx("result") },
                Params = new[] { P("op", "Style", ParamType.Choice, "upper", "", new[] { "upper", "lower", "title", "sentence", "capitalize", "camel", "pascal", "snake", "kebab", "constant", "mock", "leet", "clap", "invert" }) },
                SummaryParam = "op",
                Exec = c => c.SetOut(0, TextTools.Case(c.Param("op"), c.In(0))),
            },
            new()
            {
                TypeId = "data.shape", Icon = "scissors", Title = "Shape Text", Subtitle = "data", Category = NodeCategory.Data,
                Description = "Reshapes text: reverse, trim, squeeze spaces, length, repeat, truncate, pad, slugify, acronym, disemvowel, number lines, word-wrap.",
                Inputs = new[] { Tx("text") }, Outputs = new[] { Tx("result") },
                Params = new[]
                {
                    P("op", "Op", ParamType.Choice, "trim", "", new[] { "reverse", "trim", "squeeze", "length", "repeat", "truncate", "padleft", "padright", "slug", "acronym", "disemvowel", "numbering", "wrap", "zalgo", "banner" }),
                    P("n", "Amount (repeat/width/length)", ParamType.Int, "1", "1"),
                    P("fill", "Pad char", ParamType.Text, " ", " "),
                },
                SummaryParam = "op",
                Exec = c => c.SetOut(0, TextTools.Shape(c.Param("op"), c.In(0), c.ParamInt("n", 1), c.Param("fill"))),
            },
            new()
            {
                TypeId = "data.regex", Icon = "magnifying-glass", Title = "Regex", Subtitle = "data", Category = NodeCategory.Data,
                Description = "Runs a regular expression over text: test match, extract the first match / capture group, extract all matches, count, or replace.",
                Inputs = new[] { Tx("text") }, Outputs = new[] { Tx("result") },
                Params = new[]
                {
                    P("op", "Op", ParamType.Choice, "first", "", new[] { "match", "first", "all", "count", "replace" }),
                    P("pattern", "Pattern", ParamType.Text, "", "\\d+"),
                    P("replace", "Replace with", ParamType.Text, "", "$0", visibleWhen: n => n.GetParam("op") == "replace"),
                    P("flags", "Flags (ims)", ParamType.Text, "", "i"),
                },
                SummaryParam = "pattern",
                Exec = c => c.SetOut(0, TextTools.Regex_(c.Param("op"), c.Param("pattern"), c.Param("replace"), c.Param("flags"), c.In(0))),
            },
            new()
            {
                TypeId = "data.mathx", Icon = "divide", Title = "Math", Subtitle = "data", Category = NodeCategory.Data,
                Description = "Evaluates an arithmetic expression: + - * / % ^, parentheses, and functions (sqrt, abs, round, floor, ceil, sin, cos, log, …). Supports pi and e.",
                Inputs = new[] { Tx("expr") }, Outputs = new[] { Tx("result") },
                Params = new[] { P("expr", "Expression", ParamType.Text, "", "2 * (3 + 4)") },
                SummaryParam = "expr",
                Exec = c => { string e = c.In(0); if (e.Length == 0) e = c.Resolve(c.Param("expr")); c.SetOut(0, TextTools.Math_(e)); },
            },
            new()
            {
                TypeId = "data.convert", Icon = "ruler", Title = "Unit Convert", Subtitle = "data", Category = NodeCategory.Data,
                Description = "Converts a value between units: temperature (c/f/k), length (mm..mi), mass (mg..lb), speed (m/s, km/h, mph, kn), data (B..TiB).",
                Inputs = new[] { Nm("value") }, Outputs = new[] { Tx("result") },
                Params = new[]
                {
                    P("family", "Kind", ParamType.Choice, "temperature", "", new[] { "temperature", "length", "mass", "speed", "data" }),
                    P("value", "Value", ParamType.Text, "", "100"),
                    P("from", "From", ParamType.Text, "c", "c · km · kg · mph"),
                    P("to", "To", ParamType.Text, "f", "f · mi · lb · km/h"),
                },
                SummaryParam = "family",
                Exec = c => { string v = c.In(0); if (v.Length == 0) v = c.Resolve(c.Param("value")); double d = double.TryParse(v.Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : 0; c.SetOut(0, TextTools.Convert_(c.Param("family"), d, c.Param("from"), c.Param("to"))); },
            },
            new()
            {
                TypeId = "num.theory", Icon = "hash", Title = "Number Theory", Subtitle = "data", Category = NodeCategory.Data,
                Description = "Whole-number math: factorial, nth Fibonacci, GCD, LCM, is-prime, next prime, ordinal (1st/2nd), absolute value, sign.",
                Inputs = new[] { Nm("a"), Nm("b") }, Outputs = new[] { Tx("result") },
                Params = new[]
                {
                    P("op", "Op", ParamType.Choice, "factorial", "", new[] { "factorial", "fibonacci", "gcd", "lcm", "isprime", "nextprime", "ordinal", "abs", "sign", "round", "floor", "ceil" }),
                    P("a", "A", ParamType.Text, "", "5"),
                    P("b", "B", ParamType.Text, "", "10", visibleWhen: n => n.GetParam("op") is "gcd" or "lcm"),
                },
                SummaryParam = "op",
                Exec = c => { string a = c.In(0); if (a.Length == 0) a = c.Resolve(c.Param("a")); string b = c.In(1); if (b.Length == 0) b = c.Resolve(c.Param("b")); c.SetOut(0, TextTools.NumTheory(c.Param("op"), a, b)); },
            },
            new()
            {
                TypeId = "num.format", Icon = "seal-check", Title = "Number Format", Subtitle = "data", Category = NodeCategory.Data,
                Description = "Formats a number: convert between bases (bin/oct/dec/hex/any), Roman numerals (and back), number-to-words, or thousands separators.",
                Inputs = new[] { Tx("value") }, Outputs = new[] { Tx("result") },
                Params = new[]
                {
                    P("op", "Op", ParamType.Choice, "base", "", new[] { "base", "roman", "unroman", "words", "commas" }),
                    P("value", "Value", ParamType.Text, "", "255 · 0xff · MMXXIV"),
                    P("radix", "Base (for 'base')", ParamType.Int, "16", "2..36", visibleWhen: n => n.GetParam("op") == "base"),
                },
                SummaryParam = "op",
                Exec = c => { string v = c.In(0); if (v.Length == 0) v = c.Resolve(c.Param("value")); c.SetOut(0, TextTools.NumFormat(c.Param("op"), v, c.ParamInt("radix", 10))); },
            },
            new()
            {
                TypeId = "data.datetime", Icon = "clock", Title = "Date / Time", Subtitle = "data", Category = NodeCategory.Data,
                Description = "Works with dates/times: current time (any timezone), format a date, day of week, unix timestamp conversion, countdown until a date, or age in years.",
                Inputs = new[] { Tx("input") }, Outputs = new[] { Tx("result") },
                Params = new[]
                {
                    P("op", "Op", ParamType.Choice, "now", "", new[] { "now", "format", "weekday", "unix", "until", "age" }),
                    P("input", "Date / value", ParamType.Text, "", "2026-12-25", visibleWhen: n => n.GetParam("op") != "now"),
                    P("format", "Format", ParamType.Text, "", "yyyy-MM-dd HH:mm", visibleWhen: n => n.GetParam("op") is "now" or "format"),
                    P("zone", "Timezone (for now)", ParamType.Text, "", "UTC · Europe/London", visibleWhen: n => n.GetParam("op") == "now"),
                },
                SummaryParam = "op",
                Exec = c => { string i = c.In(0); if (i.Length == 0) i = c.Resolve(c.Param("input")); c.SetOut(0, TextTools.DateTime_(c.Param("op"), i, c.Param("format"), c.Param("zone"))); },
            },
            new()
            {
                TypeId = "gen.random", Icon = "dice-five", Title = "Random Generator", Subtitle = "data", Category = NodeCategory.Data,
                Description = "Generates random things: a coin flip, dice (NdM+K), a UUID, a password, a cute username, a fake name, a hex colour, an integer in a range, or lorem ipsum.",
                Inputs = new[] { Tx("spec") }, Outputs = new[] { Tx("result") },
                Params = new[]
                {
                    P("op", "Generate", ParamType.Choice, "uuid", "", new[] { "coin", "dice", "uuid", "password", "username", "fakename", "color", "int", "lorem", "gradient" }),
                    P("spec", "Spec", ParamType.Text, "", "2d6+3 · 16 · 1-100 · 24"),
                },
                SummaryParam = "op",
                Exec = c => { string s = c.In(0); if (s.Length == 0) s = c.Resolve(c.Param("spec")); c.SetOut(0, TextTools.Gen(c.Param("op"), s, c.Rng)); },
            },
            new()
            {
                TypeId = "data.pick", Icon = "hand-pointing", Title = "Pick from List", Subtitle = "data", Category = NodeCategory.Data,
                Description = "Picks from a list: a random item, the first / last / Nth, or counts them. Choose how the list is separated.",
                Inputs = new[] { Tx("list") }, Outputs = new[] { Tx("result") },
                Params = new[]
                {
                    P("op", "Pick", ParamType.Choice, "random", "", new[] { "random", "first", "last", "nth", "count" }),
                    P("sep", "Separator", ParamType.Choice, "newline", "", new[] { "newline", "comma", "space", "pipe" }),
                    P("n", "N (for nth)", ParamType.Int, "1", "1", visibleWhen: n => n.GetParam("op") == "nth"),
                },
                SummaryParam = "op",
                Exec = c => c.SetOut(0, TextTools.Pick(c.Param("op"), c.In(0), c.Param("sep"), c.ParamInt("n", 1), c.Rng())),
            },
            new()
            {
                TypeId = "data.stats", Icon = "chart-bar", Title = "Number Stats", Subtitle = "data", Category = NodeCategory.Data,
                Description = "Computes a statistic over a list of numbers found in the text: sum, mean, median, min, max, count, range, or standard deviation.",
                Inputs = new[] { Tx("numbers") }, Outputs = new[] { Tx("result") },
                Params = new[] { P("op", "Stat", ParamType.Choice, "sum", "", new[] { "sum", "mean", "median", "min", "max", "count", "range", "stdev" }) },
                SummaryParam = "op",
                Exec = c => c.SetOut(0, TextTools.Stat(c.Param("op"), c.In(0))),
            },
            new()
            {
                TypeId = "irc.color", Icon = "rainbow", Title = "IRC Color", Subtitle = "ircv3", Category = NodeCategory.Ircv3,
                Description = "Adds or removes IRC formatting: rainbow-colour text, strip all colours/formatting, or wrap in bold / italic / underline.",
                Inputs = new[] { Tx("text") }, Outputs = new[] { Tx("result") },
                Params = new[] { P("op", "Op", ParamType.Choice, "rainbow", "", new[] { "rainbow", "strip", "bold", "italic", "underline" }) },
                SummaryParam = "op",
                Exec = c => c.SetOut(0, TextTools.IrcColor(c.Param("op"), c.In(0))),
            },
            // ===================== DCC (direct client-to-client file transfer) =====================
            new()
            {
                TypeId = "event.dcc", Icon = "broadcast", Title = "On DCC Offer", Subtitle = "trigger",
                Category = NodeCategory.Event, TriggerEvent = "dcc",
                Description = "Fires when a user offers a DCC transfer over IRC - a direct file SEND, or a CHAT/RESUME/ACCEPT negotiation. Decide what to do with Accept DCC File or Decline DCC. The 'account' pin is the sender's authenticated login (use it to allow-list - nicks can be faked, accounts can't). Also exposes {dcc.file} {dcc.size} {dcc.ip} {dcc.port} {dcc.token} {dcc.type} {account}.",
                Outputs = new[] { Ex("then"), Tx("filename"), Nm("size"), Us("nick"), Tx("type"), Tx("account") },
                Params = new[] { P("only", "Only", ParamType.Choice, "send", "", new[] { "any", "send", "chat", "resume", "accept" }) },
                SummaryParam = "only",
                Exec = c =>
                {
                    string ty = c.Var("dcc.type");
                    string only = c.Param("only");
                    if (only.Length > 0 && only != "any" && !only.Equals(ty, StringComparison.OrdinalIgnoreCase)) return;
                    c.SetOut(1, c.Var("dcc.file"));
                    c.SetOut(2, c.Var("dcc.size"));
                    c.SetOut(3, c.Var("nick"));
                    c.SetOut(4, ty);
                    c.SetOut(5, c.Var("account"));
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "dcc.accept", Icon = "folder-open", Title = "Accept DCC File", Subtitle = "dcc",
                Category = NodeCategory.Action,
                Description = "Accepts the DCC file offer that triggered this flow and downloads it - you choose where it lands. Blank path = ~/ircuitry/files/dcc/. A folder saves it there under the offered name; a full path names the file. The transfer runs in the background and logs when it finishes.",
                Inputs = new[] { Ex(), Tx("save to") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("path", "Save to (file or folder)", ParamType.Text, "", "blank = ~/ircuitry/files/dcc/ · or a folder · or a full path") },
                SummaryParam = "path",
                Exec = c =>
                {
                    if (c.Var("dcc.type") != "send")
                    {
                        c.Log("Accept DCC: the triggering offer wasn't a file SEND - nothing to accept", LogLevel.Error);
                        c.Pulse(0); return;
                    }
                    string nick = c.Var("nick"), ip = c.Var("dcc.ip"), token = c.Var("dcc.token");
                    int port = int.TryParse(c.Var("dcc.port"), out var pp) ? pp : 0;
                    long size = long.TryParse(c.Var("dcc.size"), out var sz) ? sz : 0;
                    string savePath = DccSavePath(c.InOr(1, c.Resolve(c.Param("path"))), c.Var("dcc.file"));
                    c.DccReceive(nick, ip, port, size, token, savePath);
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "dcc.reject", Icon = "prohibit", Title = "Decline DCC", Subtitle = "dcc",
                Category = NodeCategory.Action,
                Description = "Declines the DCC offer that triggered this flow (DCC has no formal 'no' - we simply don't connect, and optionally send the sender a notice).",
                Inputs = new[] { Ex() },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("notice", "Notice (optional)", ParamType.Text, "", "sorry, I'm not accepting files") },
                SummaryParam = "notice",
                Exec = c =>
                {
                    string nick = c.Var("nick");
                    string msg = c.Resolve(c.Param("notice"));
                    if (nick.Length > 0 && msg.Length > 0) c.Notice(nick, msg);
                    c.Log($"DCC offer from {(nick.Length > 0 ? nick : "someone")} declined", LogLevel.Action);
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "dcc.send", Icon = "export", Title = "Send File (DCC)", Subtitle = "dcc",
                Category = NodeCategory.Action,
                Description = "Offers a file to a user over DCC: the bot listens on a port, sends the DCC SEND, and streams the file when they accept. Relative paths live under ~/ircuitry/files. Note: the receiver connects back to this machine, so a public IP / port-forwarding may be needed across the internet.",
                Inputs = new[] { Ex(), Us("nick"), Tx("file") },
                Outputs = new[] { Ex("then") },
                Params = new[]
                {
                    P("nick", "To nick", ParamType.Text, "", "{nick}"),
                    P("file", "File path", ParamType.Text, "", "report.pdf or /abs/path"),
                    P("ip", "Advertise IP (optional)", ParamType.Text, "", "blank = auto-detect this machine"),
                },
                SummaryParam = "file",
                Exec = c =>
                {
                    string nick = c.InOr(1, c.Resolve(c.Param("nick")));
                    string file = ResolveFile(c.InOr(2, c.Resolve(c.Param("file"))));
                    if (nick.Length == 0 || file.Length == 0) { c.Log("DCC send: need a nick and a file", LogLevel.Error); c.Pulse(0); return; }
                    c.DccSend(nick, file, c.Resolve(c.Param("ip")));
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "data.gettag", Icon = "bookmark-simple", Title = "Get Tag", Subtitle = "ircv3",
                Category = NodeCategory.Ircv3,
                Description = "Reads any IRCv3 message tag from the triggering message (e.g. account, time, msgid).",
                Outputs = new[] { Tx("value") },
                Params = new[] { P("name", "Tag", ParamType.Text, "account", "account · time · msgid …") },
                SummaryParam = "name",
                Exec = c => c.SetOut(0, c.Var("tag." + c.Param("name"))),
            },
            new()
            {
                TypeId = "data.json", Icon = "puzzle-piece", Title = "JSON Field", Subtitle = "data",
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
                TypeId = "event.timer", Icon = "alarm", Title = "On Timer", Subtitle = "trigger",
                Category = NodeCategory.Event, TriggerEvent = "timer",
                Description = "Fires every N seconds while the bot is connected. Pair with Send to Channel for announcements.",
                Outputs = new[] { Ex("then") },
                Params = new[] { P("seconds", "Every (seconds)", ParamType.Int, "60", "60") },
                SummaryParam = "seconds",
                Exec = c => c.Pulse(0),
            },
            new()
            {
                TypeId = "event.watchdog", Icon = "heartbeat", Title = "Watchdog", Subtitle = "trigger",
                Category = NodeCategory.Event, TriggerEvent = "watchdog",
                Description = "Health check on a cadence - fires even when a server has dropped, so you can auto-heal. Branches healthy / needs heal by your chosen rule and outputs live health. Wire 'needs heal' into Reconnect (and/or an alert).",
                Outputs = new[] { Ex("healthy"), Ex("needs heal"), Tx("down"), Tx("queue"), Tx("errors") },
                Params = new[]
                {
                    P("seconds", "Every (seconds)", ParamType.Int, "30", "how often to check (min 5)"),
                    P("when", "Needs heal when", ParamType.Choice, "a server is down", "", new[] { "a server is down", "not connected", "queue above", "any errors" }),
                    P("threshold", "Queue threshold", ParamType.Int, "8", "", visibleWhen: n => n.GetParam("when") == "queue above"),
                },
                SummaryParam = "when",
                Exec = c =>
                {
                    c.SetOut(2, c.Var("down")); c.SetOut(3, c.Var("queue")); c.SetOut(4, c.Var("errors"));
                    int down = int.TryParse(c.Var("down"), out var d) ? d : 0;
                    int queue = int.TryParse(c.Var("queue"), out var q) ? q : 0;
                    int errors = int.TryParse(c.Var("errors"), out var e) ? e : 0;
                    bool connected = c.Var("connected") == "true";
                    bool needsHeal = c.Param("when") switch
                    {
                        "not connected" => !connected,
                        "queue above" => queue > Math.Max(0, c.ParamInt("threshold", 8)),
                        "any errors" => errors > 0,
                        _ => down > 0,
                    };
                    c.Pulse(needsHeal ? 1 : 0);
                },
            },
            new()
            {
                TypeId = "action.reconnect", Icon = "plugs-connected", Title = "Reconnect", Subtitle = "action",
                Category = NodeCategory.Action,
                Description = "Auto-heal: reconnects a dropped server. Leave 'server' blank to reconnect the one this flow is running on, or name a host/label to bring a specific server back. A server that is already connected is left alone. Pair with Watchdog.",
                Inputs = new[] { Ex(), Tx("server") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("server", "Server", ParamType.Text, "", "blank = current; or a host/label") },
                Exec = c => { c.Reconnect(c.InOr(1, c.Resolve(c.Param("server")))); c.Pulse(0); },
            },
            new()
            {
                TypeId = "event.webhook", Icon = "webhooks-logo", Title = "On Webhook", Subtitle = "trigger",
                Category = NodeCategory.Event, TriggerEvent = "webhook",
                Description = "Fires on an HTTP request to this bot's webhook URL when hosted (POST http://host:port/hook/<path> on a running ircuitry --server). The path is the shared secret - use a long random one. Outputs the request {body}; pair with JSON to parse it. The bot must be connected.",
                Outputs = new[] { Ex("then"), Tx("body") },
                Params = new[] { P("path", "Path (secret)", ParamType.Text, "", "long-random-secret-path") },
                SummaryParam = "path",
                Exec = c => c.Pulse(0),
            },
            new()
            {
                TypeId = "event.schedule", Icon = "calendar", Title = "On Schedule", Subtitle = "trigger",
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
                TypeId = "logic.if", Icon = "question", Title = "If / Compare", Subtitle = "condition",
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
                TypeId = "logic.regex", Icon = "magnifying-glass", Title = "Regex Match", Subtitle = "condition",
                Category = NodeCategory.Filter,
                Description = "Matches text against a pattern; branches match / no match. The first four capture groups are on the $1-$4 pins, AND every group is exposed as a token you can drop into any field downstream for the rest of the run: {1}, {2}, {3}... for numbered groups and {name} for named groups like (?<name>...). {0} is the whole match.",
                Inputs = new[] { Ex(), Tx("text") },
                Outputs = new[] { Ex("match"), Ex("no match"), Tx("$1"), Tx("$2"), Tx("$3"), Tx("$4") },
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
                            // $1..$4 pins (group 1 falls back to the whole match when the pattern has no groups)
                            c.SetOut(2, m.Groups.Count > 1 ? m.Groups[1].Value : m.Value);
                            c.SetOut(3, m.Groups.Count > 2 ? m.Groups[2].Value : "");
                            c.SetOut(4, m.Groups.Count > 3 ? m.Groups[3].Value : "");
                            c.SetOut(5, m.Groups.Count > 4 ? m.Groups[4].Value : "");
                            // expose EVERY group as a resolvable token for the rest of the run - {1}..{n} and {name}
                            // for named groups - which is what people reach for instead of wiring the pins
                            c.SetVar("0", m.Value);
                            for (int g = 1; g < m.Groups.Count; g++)
                            {
                                var grp = m.Groups[g];
                                c.SetVar(g.ToString(), grp.Value);
                                if (!int.TryParse(grp.Name, out _)) c.SetVar(grp.Name, grp.Value);   // named group -> {name}
                            }
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
                TypeId = "logic.switch", Icon = "shuffle", Title = "Switch", Subtitle = "logic",
                Category = NodeCategory.Logic,
                Description = "Routes to the output whose case matches the value, else 'default'. Add as many cases as you like with 'Add case' - each one grows its own output pin. Great for command menus. (The 'default' pin stays on top so adding a case never disturbs your existing wires.)",
                Inputs = new[] { Ex(), Tx("value") },
                // static fallback for tooling that never sees an instance; real instances use DynOutputs below
                Outputs = new[] { Ex("default") },
                // default is output 0 (kept first so adding a case never shifts existing case wires); then one
                // exec output per case row, in list order - so case row i lines up with Pulse(i+1) in Exec.
                DynOutputs = n =>
                {
                    var outs = new List<PinDef> { Ex("default") };
                    int idx = 0;
                    foreach (var cv in Ircuitry.Core.ParamList.Values(n.GetParam("cases")))
                    {
                        idx++;
                        var label = cv.Trim();
                        if (label.Length > 16) label = label[..16];
                        outs.Add(Ex(label.Length > 0 ? label : "case " + idx));
                    }
                    return outs.ToArray();
                },
                Params = new[]
                {
                    P("value", "Value to match", ParamType.Text, "", "blank = the command (or message) · or {nick}, {arg1} …"),
                    PL("cases", "Cases (one output each)", false, "Add case"),
                    P("ci", "Ignore case", ParamType.Bool, "true"),
                },
                SummaryParam = "value",
                Exec = c =>
                {
                    var cases = Ircuitry.Core.ParamList.Values(c.Param("cases")).ToList();
                    string raw = c.Param("value");
                    string fallback = c.Var("command").Length > 0 ? c.Var("command") : c.Var("message");
                    var v = c.InOr(1, raw.Length > 0 ? c.Resolve(raw) : fallback).Trim();
                    var cmp = c.ParamBool("ci") ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    for (int i = 0; i < cases.Count; i++)
                        if (string.Equals(v, c.Resolve(cases[i]).Trim(), cmp)) { c.Pulse(i + 1); return; }
                    c.Pulse(0);   // no case matched -> default (output 0)
                },
            },
            new()
            {
                TypeId = "logic.cooldown", Icon = "hourglass", Title = "Cooldown", Subtitle = "logic",
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
                TypeId = "logic.forEach", Icon = "repeat", Title = "For Each", Subtitle = "logic",
                Category = NodeCategory.Logic,
                Description = "Runs 'each' once per list item, then 'done'. Splits text by a separator, or set Separator to 'json' to iterate a JSON array. Each item is on the 'item' pin and in a var (default {item}) so {item.field} works for JSON elements.",
                Inputs = new[] { Ex(), Tx("list") },
                Outputs = new[] { Ex("each"), Ex("done"), Tx("item"), Nm("index") },
                Params = new[]
                {
                    P("sep", "Separator", ParamType.Choice, "newline", "", new[] { "newline", "comma", "space", "json" }),
                    P("var", "Item var", ParamType.Text, "item", "reference items as {item} / {item.field}"),
                },
                SummaryParam = "sep",
                Exec = c =>
                {
                    string raw = c.InOr(1, "");
                    string varName = c.Param("var"); if (varName.Length == 0) varName = "item";
                    string[] items = c.Param("sep") == "json"
                        ? Json.ArrayItems(raw)
                        : raw.Split(c.Param("sep") switch { "comma" => new[] { ',' }, "space" => new[] { ' ', '\t' }, _ => new[] { '\n' } },
                              StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                    for (int i = 0; i < items.Length; i++)
                    {
                        c.SetOut(2, items[i]); c.SetOut(3, i.ToString());
                        c.SetVar(varName, items[i]); c.SetVar("index", i.ToString());
                        c.InvalidatePure();   // loop var changed: pure nodes that read {item} must recompute
                        c.Run(0);
                    }
                    c.Pulse(1);
                },
            },
            new()
            {
                TypeId = "logic.repeat", Icon = "repeat-once", Title = "Repeat", Subtitle = "logic",
                Category = NodeCategory.Logic,
                Description = "Runs 'each' a fixed number of times (the 0-based count on 'i' and as {i}), then 'done'. Bounded so it can never loop forever.",
                Inputs = new[] { Ex(), Nm("times") },
                Outputs = new[] { Ex("each"), Ex("done"), Nm("i") },
                Params = new[] { P("times", "Times", ParamType.Int, "3", "how many iterations") },
                SummaryParam = "times",
                Exec = c =>
                {
                    int times = Math.Clamp(int.TryParse(c.InOr(1, c.Param("times")), out var t) ? t : 0, 0, 10000);
                    for (int i = 0; i < times; i++) { c.SetOut(2, i.ToString()); c.SetVar("i", i.ToString()); c.InvalidatePure(); c.Run(0); }
                    c.Pulse(1);
                },
            },
            new()
            {
                TypeId = "data.setvar", Icon = "tray", Title = "Set Variable", Subtitle = "state",
                Category = NodeCategory.Logic,
                Description = "Stores a value under a name. Persists across events and saves with the bot.",
                Inputs = new[] { Ex(), Tx("value") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("name", "Name", ParamType.Text, "counter", "counter"), P("value", "Value", ParamType.Text, "", "supports {message} {nick} …") },
                SummaryParam = "name",
                Exec = c => { c.SetState(c.Resolve(c.Param("name")), c.InOr(1, c.Resolve(c.Param("value")))); c.Pulse(0); },
            },

            // ============================ DATA (more) =======================
            new()
            {
                TypeId = "data.getvar", Icon = "export", Title = "Get Variable", Subtitle = "state",
                Category = NodeCategory.Data,
                Description = "Reads a stored variable (or the default if it isn't set yet).",
                Outputs = new[] { Tx("value") },
                Params = new[] { P("name", "Name", ParamType.Text, "counter", "counter"), P("default", "Default", ParamType.Text, "0") },
                SummaryParam = "name",
                Exec = c => { var v = c.GetState(c.Resolve(c.Param("name"))); c.SetOut(0, v.Length > 0 ? v : c.Resolve(c.Param("default"))); },
            },
            new()
            {
                TypeId = "data.math", Icon = "calculator", Title = "Math", Subtitle = "data",
                Category = NodeCategory.Data,
                Description = "Computes A (op) B as a number. The A/B fields take {tokens} (e.g. {score}, {count}) as well as plain numbers, so you can do math on variables in place. Pair with Set Variable for counters and scores.",
                Inputs = new[] { Tx("a"), Tx("b") },
                Outputs = new[] { Tx("result") },
                Params = new[] { P("op", "Op", ParamType.Choice, "+", "", new[] { "+", "-", "×", "÷", "%", "min", "max" }), P("a", "A", ParamType.Text, "0", "0 or {var}"), P("b", "B", ParamType.Text, "1", "1 or {var}") },
                SummaryParam = "op",
                Exec = c =>
                {
                    double a = ParseNum(c.InOr(0, c.Resolve(c.Param("a")))), b = ParseNum(c.InOr(1, c.Resolve(c.Param("b"))));
                    double r = c.Param("op") switch { "+" => a + b, "-" => a - b, "×" => a * b, "÷" => b != 0 ? a / b : 0, "%" => b != 0 ? a % b : 0, "min" => Math.Min(a, b), "max" => Math.Max(a, b), _ => a };
                    c.SetOut(0, FormatNum(r));
                },
            },
            new()
            {
                TypeId = "data.transform", Icon = "text-aa", Title = "Text Transform", Subtitle = "data",
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
                TypeId = "data.randnum", Icon = "target", Title = "Random Number", Subtitle = "data",
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
                TypeId = "irc.topic", Icon = "push-pin", Title = "Set Topic", Subtitle = "channel",
                Category = NodeCategory.Action,
                Description = "Sets a channel's topic (needs the right channel privileges). Blank channel = the triggering channel.",
                Inputs = new[] { Ex(), Tx("topic") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("channel", "Channel", ParamType.Text, "", "blank = current"), P("topic", "Topic", ParamType.Text, "", "supports {me} {nick} {args} {arg1} …") },
                SummaryParam = "topic",
                Exec = c =>
                {
                    var chan = c.Resolve(c.Param("channel")); if (chan.Length == 0) chan = c.Var("channel");
                    var topic = OneLine(c.InOr(1, c.Resolve(c.Param("topic"))));
                    if (chan.Length > 0) c.Raw($"TOPIC {chan} :{topic}");
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "irc.kick", Icon = "boot", Title = "Kick User", Subtitle = "moderation",
                Category = NodeCategory.Action,
                Description = "Kicks a user from a channel (needs operator). Blank channel = the triggering channel.",
                Inputs = new[] { Ex(), Tx("nick") },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("channel", "Channel", ParamType.Text, "", "blank = current"), P("nick", "Nick", ParamType.Text, "", "{nick}"), P("reason", "Reason", ParamType.Text, "", "bye!") },
                SummaryParam = "nick",
                Exec = c =>
                {
                    var chan = c.Resolve(c.Param("channel")); if (chan.Length == 0) chan = c.Var("channel");
                    var nick = OneLine(c.InOr(1, c.Resolve(c.Param("nick"))));
                    var reason = OneLine(c.Resolve(c.Param("reason")));
                    if (chan.Length > 0 && nick.Length > 0) c.Raw($"KICK {chan} {nick} :{reason}");
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "irc.mode", Icon = "shield", Title = "Set Mode", Subtitle = "moderation",
                Category = NodeCategory.Action,
                Description = "Applies a mode, e.g. +o/-o (op), +v (voice), +b (ban). All fields take {tokens} like {nick}, {arg1} or {me}. Put a channel (or {me} / a nick) in the first field; for a user mode on the bot itself use {me} there and leave Target blank, e.g. {me} +B.",
                Inputs = new[] { Ex(), Tx("target") },
                Outputs = new[] { Ex("then") },
                Params = new[]
                {
                    P("channel", "Channel / nick", ParamType.Text, "", "blank = current · {me} = the bot"),
                    P("modes", "Modes", ParamType.Text, "+o", "+o · +v · +b · {arg1}"),
                    P("target", "Target (nick/mask)", ParamType.Text, "", "{nick} · {arg1}"),
                },
                SummaryParam = "modes",
                Exec = c =>
                {
                    var chan = c.Resolve(c.Param("channel")); if (chan.Length == 0) chan = c.Var("channel");
                    var modes = OneLine(c.Resolve(c.Param("modes")));
                    var target = OneLine(c.InOr(1, c.Resolve(c.Param("target"))));
                    if (chan.Length > 0 && modes.Length > 0) c.Raw($"MODE {chan} {modes}{(target.Length > 0 ? " " + target : "")}");
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "irc.action", Icon = "mask-happy", Title = "Send Action", Subtitle = "emote",
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
                TypeId = "flow.delay", Icon = "hourglass", Title = "Delay", Subtitle = "flow",
                Category = NodeCategory.Logic,
                Description = "Waits before continuing. Runs off the connection thread, so it never stalls keepalive or other workflows (capped at 5 min). For long or recurring waits, prefer a Schedule/Timer trigger.",
                Inputs = new[] { Ex() },
                Outputs = new[] { Ex("then") },
                Params = new[] { P("seconds", "Seconds", ParamType.Text, "1", "seconds - supports {tokens} for a computed (e.g. typing) delay") },
                SummaryParam = "seconds",
                Exec = c =>
                {
                    // resolve {tokens} so the wait can be computed per-event (e.g. a human-like typing delay).
                    double secs = double.TryParse(c.Resolve(c.Param("seconds")), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 1;
                    int ms = (int)(Math.Clamp(secs, 0, 300) * 1000);
                    if (ms > 0) System.Threading.Thread.Sleep(ms);
                    c.Pulse(0);
                },
            },

            // ----- subflow boundary nodes (used inside a saved "reusable node") -----
            new()
            {
                TypeId = "flow.in", Icon = "arrow-bend-down-right", Title = "Subflow Start", Subtitle = "subflow",
                Category = NodeCategory.Logic,
                Description = "The entry point of a reusable subflow. Wire its 'then' to the subflow's body. (Only meaningful inside a saved node.)",
                Outputs = new[] { Ex("then") },
                Exec = c => c.Pulse(0),
            },
            new()
            {
                TypeId = "flow.arg", Icon = "tray", Title = "Subflow Input", Subtitle = "subflow",
                Category = NodeCategory.Logic,
                Description = "Reads one of the subflow's named inputs. The 'name' becomes an input pin on the saved node.",
                Outputs = new[] { Tx("value") },
                Params = new[] { P("name", "Input name", ParamType.Text, "input", "city") },
                SummaryParam = "name",
                Exec = c => c.SetOut(0, c.Var(c.Param("name"))),
            },
            new()
            {
                TypeId = "flow.return", Icon = "export", Title = "Subflow Output", Subtitle = "subflow",
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
                TypeId = "db.set", Icon = "archive", Title = "DB Set", Subtitle = "database",
                Category = NodeCategory.Action,
                Description = "Stores a value in a named table on disk (~/ircuitry/data). Empty value deletes the key. Survives restarts and is shared across bots. Switch Mode to 'clear' to wipe a whole table (or several - comma/space separated), e.g. to reset ephemeral session state on boot.",
                Inputs = new[] { Ex(), Tx("value") },
                Outputs = new[] { Ex("then") },
                Params = new[]
                {
                    P("table", "Table", ParamType.Text, "main", "scores · seen · notes"),
                    P("mode", "Mode", ParamType.Choice, "set", "", new[] { "set", "clear" }),
                    P("key", "Key", ParamType.Text, "", "{nick}", visibleWhen: n => n.GetParam("mode") != "clear"),
                    P("value", "Value", ParamType.Text, "", "supports {tokens} · blank = delete", visibleWhen: n => n.GetParam("mode") != "clear"),
                },
                SummaryParam = "table",
                Exec = c =>
                {
                    var table = c.Resolve(c.Param("table"));
                    if (c.Param("mode") == "clear")
                    {
                        foreach (var t in table.Split(new[] { ',', ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            KvStore.Clear(t);
                        c.Pulse(0); return;
                    }
                    var key = c.Resolve(c.Param("key"));
                    if (key.Length == 0) { c.Pulse(0); return; }
                    var val = c.InOr(1, c.Resolve(c.Param("value")));
                    if (val.Length == 0) KvStore.Delete(table, key); else KvStore.Set(table, key, val);
                    c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "db.get", Icon = "key", Title = "DB Get", Subtitle = "database",
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
                TypeId = "ai.memory", Icon = "brain", Title = "AI Memory", Subtitle = "ai",
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
            new NodeDef
            {
                TypeId = "ai.cache", Icon = "lightning", Title = "AI Cache", Subtitle = "ai", Category = NodeCategory.Action,
                Description = "Caches AI replies so repeated prompts skip the model (and its cost). In 'look up' mode it branches hit / miss and outputs the cached reply on a hit; after an Ask AI miss, run it again in 'save' mode to remember the new reply. Matches on normalised text by default; switch Match to 'semantic' to also match similar wording via an embeddings endpoint.",
                Inputs = new[] { Ex(), Tx("prompt"), Tx("reply") },
                Outputs = new[] { Ex("hit"), Ex("miss"), Tx("cached"), Ex("saved") },
                Params = new[]
                {
                    P("mode", "Mode", ParamType.Choice, "look up", "", new[] { "look up", "save" }),
                    P("match", "Match", ParamType.Choice, "text", "", new[] { "text", "semantic" }),
                    P("namespace", "Cache name", ParamType.Text, "default", "one shared bucket; reuse the name across nodes"),
                    P("prompt", "Prompt", ParamType.Multiline, "{message}", "the text to match on"),
                    P("reply", "Reply to cache", ParamType.Multiline, "", "what to remember", visibleWhen: n => n.GetParam("mode") == "save"),
                    P("max", "Max entries", ParamType.Int, "50", "oldest are dropped past this"),
                    P("threshold", "Similarity %", ParamType.Int, "92", "semantic: 0-100, higher = stricter", visibleWhen: n => n.GetParam("match") == "semantic"),
                    P("embedBaseUrl", "Embeddings base URL", ParamType.Text, "https://api.openai.com/v1", "", visibleWhen: n => n.GetParam("match") == "semantic"),
                    P("embedModel", "Embeddings model", ParamType.Text, "text-embedding-3-small", "", visibleWhen: n => n.GetParam("match") == "semantic"),
                    P("embedKey", "Embeddings API key", ParamType.Text, "", "blank = $OPENAI_API_KEY", visibleWhen: n => n.GetParam("match") == "semantic", secret: true),
                },
                SummaryParam = "mode",
                Exec = c =>
                {
                    string prompt = c.InOr(1, c.Resolve(c.Param("prompt")));
                    string norm = AiCache.Normalize(prompt);
                    string key = "aicache/" + c.Resolve(c.Param("namespace"));
                    string json = c.GetState(key);

                    float[]? emb = null;
                    if (c.Param("match") == "semantic" && !c.AiOverBudget)
                    {
                        emb = Ai.Embed(c.Resolve(c.Param("embedBaseUrl")), c.Param("embedKey"), c.Resolve(c.Param("embedModel")), prompt, out var eerr, 60, c.RecordTokens);
                        if (eerr.Length > 0) c.Log("AI Cache: embeddings unavailable, using text match - " + eerr, LogLevel.System);
                    }

                    if (c.Param("mode") == "save")
                    {
                        string reply = c.InOr(2, c.Resolve(c.Param("reply")));
                        c.SetState(key, AiCache.Put(json, norm, reply, emb, c.ParamInt("max", 50)));
                        c.Pulse(3);
                    }
                    else
                    {
                        double thr = Math.Clamp(c.ParamInt("threshold", 92), 1, 100) / 100.0;
                        if (AiCache.Lookup(json, norm, emb, thr) is { } hit) { c.SetOut(2, hit.reply); c.Pulse(0); }
                        else c.Pulse(1);
                    }
                },
            },

            // ===================== CALENDAR (manage / search) ==============
            new()
            {
                TypeId = "cal.add", Icon = "calendar", Title = "Add Calendar Event", Subtitle = "calendar",
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
                TypeId = "cal.search", Icon = "magnifying-glass", Title = "Search Calendar", Subtitle = "calendar",
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
                TypeId = "db.sql", Icon = "cylinder", Title = "SQL Query", Subtitle = "database",
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
                TypeId = "code.run", Icon = "scroll", Title = "Code", Subtitle = "js / python",
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

            // ===================== UI (node-authored cross-platform windows) =====================
            new()
            {
                TypeId = "ui.window", Icon = "app-window", Title = "UI Window", Subtitle = "ui", Category = NodeCategory.Ui,
                Description = "Open (or update) a real OS window painted by ircuitry's own renderer. Other UI nodes that share the same Window id add panels, text, media and controls to it; the window streams clicks and text back to On UI Event.",
                Inputs = new[] { Ex() }, Outputs = new[] { Ex("then") },
                Params = new[]
                {
                    P("window", "Window", ParamType.Text, "main", "a name; share it across UI nodes"),
                    P("title", "Title", ParamType.Text, "ircuitry", ""),
                    P("width", "Width", ParamType.Int, "800", "800"),
                    P("height", "Height", ParamType.Int, "600", "600"),
                    P("bg", "Background", ParamType.Text, "#141018", "#rrggbb"),
                },
                SummaryParam = "title",
                Exec = c => { c.UiWindow(c.Resolve(c.Param("window")), c.Resolve(c.Param("title")), c.ParamInt("width", 800), c.ParamInt("height", 600), UiColor(c.Resolve(c.Param("bg")), 0x141018FF)); c.Pulse(0); },
            },
            new()
            {
                TypeId = "ui.text", Icon = "text-aa", Title = "UI Text", Subtitle = "ui", Category = NodeCategory.Ui,
                Description = "Add or update a text label in a window (by its id). Set x/y, size, font and colour. Re-running with the same id updates the text - e.g. a live value.",
                Inputs = new[] { Ex(), Tx("text") }, Outputs = new[] { Ex("then") },
                Params = new[]
                {
                    P("window", "Window", ParamType.Text, "main", ""), P("id", "Element id", ParamType.Text, "label", ""),
                    P("parent", "Parent id", ParamType.Text, "", "(optional) position relative to this element"),
                    P("text", "Text", ParamType.Text, "Hello", "{tokens} ok"),
                    P("x", "X", ParamType.Int, "20", ""), P("y", "Y", ParamType.Int, "20", ""),
                    P("size", "Font size", ParamType.Int, "18", ""),
                    P("font", "Font", ParamType.Choice, "sans", "", new[] { "sans", "bold", "mono", "monobold", "display" }),
                    P("color", "Colour", ParamType.Text, "#F2EEF7", "#rrggbb"),
                },
                SummaryParam = "text",
                Exec = c => { c.UiUpsert(c.Resolve(c.Param("window")), new Ircuitry.UiKit.UiElement { Id = c.Resolve(c.Param("id")), Kind = Ircuitry.UiKit.UiKind.Text, Parent = NullIf(c.Resolve(c.Param("parent"))), X = c.ParamInt("x", 0), Y = c.ParamInt("y", 0), Text = c.InOr(1, c.Resolve(c.Param("text"))), FontSize = c.ParamInt("size", 18), Font = c.Param("font"), Color = UiColor(c.Resolve(c.Param("color")), 0xF2EEF7FF) }); c.Pulse(0); },
            },
            new()
            {
                TypeId = "ui.panel", Icon = "square", Title = "UI Panel", Subtitle = "ui", Category = NodeCategory.Ui,
                Description = "A rounded panel/card (optionally with a centered label). Use it as a background or a container - give other elements its id as their Parent to position them inside it.",
                Inputs = new[] { Ex() }, Outputs = new[] { Ex("then") },
                Params = new[]
                {
                    P("window", "Window", ParamType.Text, "main", ""), P("id", "Element id", ParamType.Text, "panel", ""),
                    P("parent", "Parent id", ParamType.Text, "", ""),
                    P("x", "X", ParamType.Int, "20", ""), P("y", "Y", ParamType.Int, "20", ""),
                    P("w", "Width", ParamType.Int, "320", ""), P("h", "Height", ParamType.Int, "200", ""),
                    P("color", "Colour", ParamType.Text, "#1E1B26", "#rrggbb"),
                    P("radius", "Corner radius", ParamType.Int, "16", ""),
                    P("text", "Label", ParamType.Text, "", "(optional)"),
                },
                Exec = c => { c.UiUpsert(c.Resolve(c.Param("window")), new Ircuitry.UiKit.UiElement { Id = c.Resolve(c.Param("id")), Kind = Ircuitry.UiKit.UiKind.Panel, Parent = NullIf(c.Resolve(c.Param("parent"))), X = c.ParamInt("x", 0), Y = c.ParamInt("y", 0), W = c.ParamInt("w", 320), H = c.ParamInt("h", 200), Color = UiColor(c.Resolve(c.Param("color")), 0x1E1B26FF), Radius = c.ParamInt("radius", 16), Text = c.Resolve(c.Param("text")) }); c.Pulse(0); },
            },
            new()
            {
                TypeId = "ui.rect", Icon = "rectangle", Title = "UI Rect", Subtitle = "ui", Category = NodeCategory.Ui,
                Description = "A solid (or outlined) rectangle - a swatch, bar, divider or backdrop. Animate its size/position/colour with UI Animate.",
                Inputs = new[] { Ex() }, Outputs = new[] { Ex("then") },
                Params = new[]
                {
                    P("window", "Window", ParamType.Text, "main", ""), P("id", "Element id", ParamType.Text, "rect", ""),
                    P("parent", "Parent id", ParamType.Text, "", ""),
                    P("x", "X", ParamType.Int, "20", ""), P("y", "Y", ParamType.Int, "20", ""),
                    P("w", "Width", ParamType.Int, "120", ""), P("h", "Height", ParamType.Int, "120", ""),
                    P("color", "Colour", ParamType.Text, "#FF6FB5", "#rrggbb"),
                    P("filled", "Filled", ParamType.Bool, "true", ""),
                },
                Exec = c => { c.UiUpsert(c.Resolve(c.Param("window")), new Ircuitry.UiKit.UiElement { Id = c.Resolve(c.Param("id")), Kind = Ircuitry.UiKit.UiKind.Rect, Parent = NullIf(c.Resolve(c.Param("parent"))), X = c.ParamInt("x", 0), Y = c.ParamInt("y", 0), W = c.ParamInt("w", 120), H = c.ParamInt("h", 120), Color = UiColor(c.Resolve(c.Param("color")), 0xFF6FB5FF), Filled = c.ParamBool("filled") }); c.Pulse(0); },
            },
            new()
            {
                TypeId = "ui.image", Icon = "image", Title = "UI Image", Subtitle = "ui", Category = NodeCategory.Ui,
                Description = "Draw an image from a local file path into a window. Fades with its alpha (animate it with UI Animate). PNG/JPG/GIF/BMP.",
                Inputs = new[] { Ex(), Tx("src") }, Outputs = new[] { Ex("then") },
                Params = new[]
                {
                    P("window", "Window", ParamType.Text, "main", ""), P("id", "Element id", ParamType.Text, "image", ""),
                    P("parent", "Parent id", ParamType.Text, "", ""),
                    P("src", "Image path", ParamType.Text, "", "/path/to/pic.png", file: true),
                    P("x", "X", ParamType.Int, "20", ""), P("y", "Y", ParamType.Int, "20", ""),
                    P("w", "Width", ParamType.Int, "96", ""), P("h", "Height", ParamType.Int, "96", ""),
                },
                Exec = c => { c.UiUpsert(c.Resolve(c.Param("window")), new Ircuitry.UiKit.UiElement { Id = c.Resolve(c.Param("id")), Kind = Ircuitry.UiKit.UiKind.Image, Parent = NullIf(c.Resolve(c.Param("parent"))), X = c.ParamInt("x", 0), Y = c.ParamInt("y", 0), W = c.ParamInt("w", 96), H = c.ParamInt("h", 96), Src = c.InOr(1, c.Resolve(c.Param("src"))) }); c.Pulse(0); },
            },
            new()
            {
                TypeId = "ui.button", Icon = "cursor-click", Title = "UI Button", Subtitle = "ui", Category = NodeCategory.Ui,
                Description = "A clickable button. Clicking it fires On UI Event (event = click, id = this button's id) so you can wire the rest of the graph to it.",
                Inputs = new[] { Ex() }, Outputs = new[] { Ex("then") },
                Params = new[]
                {
                    P("window", "Window", ParamType.Text, "main", ""), P("id", "Element id", ParamType.Text, "go", ""),
                    P("parent", "Parent id", ParamType.Text, "", ""),
                    P("text", "Label", ParamType.Text, "Click me", ""),
                    P("x", "X", ParamType.Int, "20", ""), P("y", "Y", ParamType.Int, "20", ""),
                    P("w", "Width", ParamType.Int, "160", ""), P("h", "Height", ParamType.Int, "48", ""),
                    P("color", "Colour", ParamType.Text, "#6C5CE7", "#rrggbb"),
                    P("textColor", "Text colour", ParamType.Text, "#FFFFFF", "#rrggbb"),
                    P("radius", "Corner radius", ParamType.Int, "14", ""),
                },
                SummaryParam = "text",
                Exec = c => { c.UiUpsert(c.Resolve(c.Param("window")), new Ircuitry.UiKit.UiElement { Id = c.Resolve(c.Param("id")), Kind = Ircuitry.UiKit.UiKind.Button, Parent = NullIf(c.Resolve(c.Param("parent"))), X = c.ParamInt("x", 0), Y = c.ParamInt("y", 0), W = c.ParamInt("w", 160), H = c.ParamInt("h", 48), Text = c.Resolve(c.Param("text")), Color = UiColor(c.Resolve(c.Param("color")), 0x6C5CE7FF), TextColor = UiColor(c.Resolve(c.Param("textColor")), 0xFFFFFFFF), Radius = c.ParamInt("radius", 14) }); c.Pulse(0); },
            },
            new()
            {
                TypeId = "ui.input", Icon = "textbox", Title = "UI Text Field", Subtitle = "ui", Category = NodeCategory.Ui,
                Description = "A text field the user can type into. Pressing Enter fires On UI Event (event = submit, id = this field's id, value = the text).",
                Inputs = new[] { Ex() }, Outputs = new[] { Ex("then") },
                Params = new[]
                {
                    P("window", "Window", ParamType.Text, "main", ""), P("id", "Element id", ParamType.Text, "field", ""),
                    P("parent", "Parent id", ParamType.Text, "", ""),
                    P("text", "Initial text", ParamType.Text, "", ""),
                    P("x", "X", ParamType.Int, "20", ""), P("y", "Y", ParamType.Int, "20", ""),
                    P("w", "Width", ParamType.Int, "260", ""), P("h", "Height", ParamType.Int, "44", ""),
                    P("color", "Colour", ParamType.Text, "#554F66", "#rrggbb"),
                },
                Exec = c => { c.UiUpsert(c.Resolve(c.Param("window")), new Ircuitry.UiKit.UiElement { Id = c.Resolve(c.Param("id")), Kind = Ircuitry.UiKit.UiKind.Input, Parent = NullIf(c.Resolve(c.Param("parent"))), X = c.ParamInt("x", 0), Y = c.ParamInt("y", 0), W = c.ParamInt("w", 260), H = c.ParamInt("h", 44), Text = c.Resolve(c.Param("text")), Color = UiColor(c.Resolve(c.Param("color")), 0x554F66FF) }); c.Pulse(0); },
            },
            new()
            {
                TypeId = "ui.animate", Icon = "sparkle", Title = "UI Animate", Subtitle = "ui", Category = NodeCategory.Ui,
                Description = "Tween a property of an element from one value to another over a duration, with an easing curve - optionally looping or ping-ponging. The window advances it smoothly each frame.",
                Inputs = new[] { Ex() }, Outputs = new[] { Ex("then") },
                Params = new[]
                {
                    P("window", "Window", ParamType.Text, "main", ""), P("id", "Element id", ParamType.Text, "", "the element to animate"),
                    P("prop", "Property", ParamType.Choice, "x", "", new[] { "x", "y", "w", "h", "alpha", "scale", "rotation", "radius" }),
                    P("from", "From", ParamType.Text, "0", ""), P("to", "To", ParamType.Text, "100", ""),
                    P("duration", "Duration (s)", ParamType.Text, "0.5", ""),
                    P("ease", "Easing", ParamType.Choice, "easeInOut", "", new[] { "linear", "easeIn", "easeOut", "easeInOut", "back", "bounce" }),
                    P("loop", "Loop", ParamType.Bool, "false", ""), P("pingpong", "Ping-pong", ParamType.Bool, "false", ""),
                    P("delay", "Delay (s)", ParamType.Text, "0", ""),
                },
                SummaryParam = "prop",
                Exec = c =>
                {
                    var t = new Ircuitry.UiKit.Tween
                    {
                        Prop = c.Param("prop"),
                        From = ParseF(c.Resolve(c.Param("from"))), To = ParseF(c.Resolve(c.Param("to"))),
                        Duration = ParseF(c.Resolve(c.Param("duration")), 0.5f), Delay = ParseF(c.Resolve(c.Param("delay"))),
                        Ease = c.Param("ease"), Loop = c.ParamBool("loop"), PingPong = c.ParamBool("pingpong"),
                    };
                    c.UiAnimate(c.Resolve(c.Param("window")), c.Resolve(c.Param("id")), t); c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "ui.scene3d", Icon = "perspective", Title = "UI 3D Scene", Subtitle = "ui", Category = NodeCategory.Ui,
                Description = "Give a window a 3D world (rendered by MonoGame's 3D pipeline, behind any 2D overlay) and pose its camera: an eye position, a look-at target, and a field of view. Add meshes with UI 3D Mesh.",
                Inputs = new[] { Ex() }, Outputs = new[] { Ex("then") },
                Params = new[]
                {
                    P("window", "Window", ParamType.Text, "main", ""),
                    P("eyeX", "Eye X", ParamType.Text, "0", ""), P("eyeY", "Eye Y", ParamType.Text, "3", ""), P("eyeZ", "Eye Z", ParamType.Text, "8", ""),
                    P("lookX", "Look X", ParamType.Text, "0", ""), P("lookY", "Look Y", ParamType.Text, "0", ""), P("lookZ", "Look Z", ParamType.Text, "0", ""),
                    P("fov", "Field of view", ParamType.Text, "55", "degrees"),
                },
                Exec = c => { c.UiScene3D(c.Resolve(c.Param("window")), new Ircuitry.UiKit.Camera { Px = ParseF(c.Resolve(c.Param("eyeX"))), Py = ParseF(c.Resolve(c.Param("eyeY")), 3f), Pz = ParseF(c.Resolve(c.Param("eyeZ")), 8f), Tx = ParseF(c.Resolve(c.Param("lookX"))), Ty = ParseF(c.Resolve(c.Param("lookY"))), Tz = ParseF(c.Resolve(c.Param("lookZ"))), Fov = ParseF(c.Resolve(c.Param("fov")), 55f) }); c.Pulse(0); },
            },
            new()
            {
                TypeId = "ui.mesh", Icon = "cube", Title = "UI 3D Mesh", Subtitle = "ui", Category = NodeCategory.Ui,
                Description = "Add or update a 3D primitive (box / sphere / plane / cylinder) in a window's 3D world: position, rotation (degrees), scale and colour. Animate px/py/pz, rx/ry/rz or sx/sy/sz with UI Animate.",
                Inputs = new[] { Ex() }, Outputs = new[] { Ex("then") },
                Params = new[]
                {
                    P("window", "Window", ParamType.Text, "main", ""), P("id", "Element id", ParamType.Text, "mesh", ""),
                    P("shape", "Shape", ParamType.Choice, "box", "", new[] { "box", "sphere", "plane", "cylinder" }),
                    P("x", "X", ParamType.Text, "0", ""), P("y", "Y", ParamType.Text, "0", ""), P("z", "Z", ParamType.Text, "0", ""),
                    P("rx", "Rotate X", ParamType.Text, "0", ""), P("ry", "Rotate Y", ParamType.Text, "0", ""), P("rz", "Rotate Z", ParamType.Text, "0", ""),
                    P("sx", "Scale X", ParamType.Text, "1", ""), P("sy", "Scale Y", ParamType.Text, "1", ""), P("sz", "Scale Z", ParamType.Text, "1", ""),
                    P("color", "Colour", ParamType.Text, "#C8C2D6", "#rrggbb"),
                    P("texture", "Texture", ParamType.Choice, "none", "", new[] { "none", "checker" }),
                },
                SummaryParam = "shape",
                Exec = c =>
                {
                    var shape = c.Param("shape") switch { "sphere" => Ircuitry.UiKit.Mesh3D.Sphere, "plane" => Ircuitry.UiKit.Mesh3D.Plane, "cylinder" => Ircuitry.UiKit.Mesh3D.Cylinder, _ => Ircuitry.UiKit.Mesh3D.Box };
                    c.UiMesh(c.Resolve(c.Param("window")), new Ircuitry.UiKit.Obj3D
                    {
                        Id = c.Resolve(c.Param("id")), Mesh = shape,
                        Px = ParseF(c.Resolve(c.Param("x"))), Py = ParseF(c.Resolve(c.Param("y"))), Pz = ParseF(c.Resolve(c.Param("z"))),
                        Rx = ParseF(c.Resolve(c.Param("rx"))), Ry = ParseF(c.Resolve(c.Param("ry"))), Rz = ParseF(c.Resolve(c.Param("rz"))),
                        Sx = ParseF(c.Resolve(c.Param("sx")), 1f), Sy = ParseF(c.Resolve(c.Param("sy")), 1f), Sz = ParseF(c.Resolve(c.Param("sz")), 1f),
                        Color = UiColor(c.Resolve(c.Param("color")), 0xC8C2D6FF),
                        Tex = c.Param("texture") == "checker" ? "checker" : "",
                    }); c.Pulse(0);
                },
            },
            new()
            {
                TypeId = "ui.controls", Icon = "game-controller", Title = "UI Controls", Subtitle = "ui", Category = NodeCategory.Ui,
                Description = "Set a window's control mode. 'fps' hands the 3D camera to the player: WASD to move, arrow keys to look (camera height stays fixed). Name an element 'gun' to bob it while walking and 'flash' to muzzle-flash on click. 'none' returns to a scripted/static camera.",
                Inputs = new[] { Ex() }, Outputs = new[] { Ex("then") },
                Params = new[]
                {
                    P("window", "Window", ParamType.Text, "main", ""),
                    P("mode", "Mode", ParamType.Choice, "fps", "", new[] { "none", "fps" }),
                },
                Exec = c => { c.UiControls(c.Resolve(c.Param("window")), c.Param("mode")); c.Pulse(0); },
            },
            new()
            {
                TypeId = "ui.web", Icon = "browser", Title = "UI Web", Subtitle = "ui", Category = NodeCategory.Ui,
                Description = "Open a web-surface window - a native webview (Photino) loading a URL or your own HTML. The page is the content (build a browser, a web app, a rich dashboard). Any window.external.sendMessage(text) from the page's JS fires On UI Event (event = message, value = text), so a web UI can drive the graph. Needs a system webview (WebView2 / WebKitGTK / WKWebView).",
                Inputs = new[] { Ex(), Tx("url") }, Outputs = new[] { Ex("then") },
                Params = new[]
                {
                    P("window", "Window", ParamType.Text, "web", ""),
                    P("url", "URL", ParamType.Text, "", "https://... (leave blank to use HTML)"),
                    P("html", "HTML", ParamType.Multiline, "", "<h1>hi</h1> (used when URL is blank)"),
                    P("title", "Title", ParamType.Text, "ircuitry web", ""),
                    P("width", "Width", ParamType.Int, "900", ""), P("height", "Height", ParamType.Int, "640", ""),
                },
                SummaryParam = "url",
                Exec = c => { c.UiWeb(c.Resolve(c.Param("window")), c.InOr(1, c.Resolve(c.Param("url"))), c.Resolve(c.Param("html")), c.ParamInt("width", 900), c.ParamInt("height", 640), c.Resolve(c.Param("title"))); c.Pulse(0); },
            },
            new()
            {
                TypeId = "ui.remove", Icon = "trash", Title = "UI Remove", Subtitle = "ui", Category = NodeCategory.Ui,
                Description = "Remove an element from a window by id. Leave the id blank to clear the whole window (keeping it open).",
                Inputs = new[] { Ex() }, Outputs = new[] { Ex("then") },
                Params = new[] { P("window", "Window", ParamType.Text, "main", ""), P("id", "Element id", ParamType.Text, "", "(blank = clear all)") },
                Exec = c => { c.UiRemove(c.Resolve(c.Param("window")), c.Resolve(c.Param("id"))); c.Pulse(0); },
            },
            new()
            {
                TypeId = "ui.close", Icon = "x", Title = "UI Close", Subtitle = "ui", Category = NodeCategory.Ui,
                Description = "Close a window.",
                Inputs = new[] { Ex() }, Outputs = new[] { Ex("then") },
                Params = new[] { P("window", "Window", ParamType.Text, "main", "") },
                Exec = c => { c.UiClose(c.Resolve(c.Param("window"))); c.Pulse(0); },
            },
            new()
            {
                TypeId = "ui.on", Icon = "hand-pointing", Title = "On UI Event", Subtitle = "trigger", Category = NodeCategory.Ui,
                TriggerEvent = "ui",
                Description = "Fires when a window control is used - a button click or a text-field submit (Enter). Filter by Window, event type and element id. Outputs the {ui_id}, the typed {ui_value} (for submit) and the {ui_event}.",
                Outputs = new[] { Ex("then"), Tx("id"), Tx("value"), Tx("event") },
                Params = new[]
                {
                    P("window", "Window", ParamType.Text, "", "(any)"),
                    P("event", "Event", ParamType.Choice, "any", "", new[] { "any", "click", "submit", "close" }),
                    P("id", "Element id", ParamType.Text, "", "(any)"),
                },
                Exec = c =>
                {
                    string w = c.Resolve(c.Param("window"));
                    string ev = c.Param("event");
                    string id = c.Resolve(c.Param("id"));
                    if (w.Length > 0 && w != c.Var("window")) return;
                    if (ev.Length > 0 && ev != "any" && ev != c.Var("ui_event")) return;
                    if (id.Length > 0 && id != c.Var("ui_id")) return;
                    c.SetOut(1, c.Var("ui_id")); c.SetOut(2, c.Var("ui_value")); c.SetOut(3, c.Var("ui_event"));
                    c.Pulse(0);
                },
            },
        };

        // authoritative categorisation: one mapping is the source of truth for every node's family, so the
        // palette stays tidy regardless of where a node was authored. See CategoryFor below.
        foreach (var d in list)
        {
            d.Category = CategoryFor(d.TypeId);
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

        // file-path params: the inspector shows a Browse button and the node accepts a file dropped onto it
        var filePaths = new HashSet<string>
        {
            "file.read:path", "file.write:path",
            "zim.info:path", "zim.search:path", "zim.article:path",
            "media.info:path", "media.organize:path", "media.transform:path",
            "archive.zip:source", "archive.unzip:zip",
            "cal.add:path", "file.ical:source", "cal.search:source",
            "net.http:file", "dcc.send:file",
        };
        foreach (var d in list)
            foreach (var p in d.Params)
                if (filePaths.Contains(d.TypeId + ":" + p.Key)) p.File = true;

        _builtins = list;
        LoadCustom();   // merge in any installed community .ircnode files
    }

    /// <summary>The single source of truth for a node's category. A few type-ids are placed explicitly (the
    /// prefixes overlap - e.g. action.* and irc.* split across IRC / IRCv3 / Network / Logic); everything else
    /// falls to a prefix rule. Keep this list, not scattered per-def Category assignments, authoritative.</summary>
    public static NodeCategory CategoryFor(string id)
    {
        switch (id)
        {
            // Logic & Flow (control flow, variables, subflow plumbing, human gates, utility)
            case "logic.if": case "logic.regex": case "logic.switch": case "logic.cooldown":
            case "logic.forEach": case "logic.repeat": case "logic.chance":
            case "data.setvar": case "data.getvar":
            case "flow.delay": case "flow.in": case "flow.arg": case "flow.return":
            case "action.signal": case "action.log":
            case "human.alert": case "human.loop":
                return NodeCategory.Logic;

            // Conditions / matching / guards
            case "filter.contains": case "filter.fromUser": case "filter.fromAccount": case "filter.isBot":
            case "irc.hasfilehost": case "mod.in": case "mod.out":
                return NodeCategory.Filter;

            case "tool.reply": return NodeCategory.Ai;

            // Network
            case "net.http": case "action.reconnect": return NodeCategory.Network;

            // Core IRC commands
            case "action.say": case "action.reply": case "action.join": case "action.part":
            case "irc.topic": case "irc.kick": case "irc.mode": case "irc.action": case "irc.tempban":
            case "irc.floodbudget": case "irc.raw": case "irc.me": case "irc.channel": case "irc.color":
            case "irc.banmask":
                return NodeCategory.Irc;

            // IRCv3 extensions / drafts
            case "irc.filehost": case "irc.typing.start": case "irc.typing.stop": case "irc.metadata":
            case "action.react": case "action.reactid": case "action.replythread": case "action.setname":
            case "action.away": case "action.tagmsg": case "action.redact": case "action.monitor":
            case "action.chathistory": case "action.rename": case "action.metadata": case "action.multiline":
            case "ircv3.recent": case "data.gettag":
                return NodeCategory.Ircv3;

            case "code.run": return NodeCategory.Code;   // runs JS/Python - a dev/code node
        }

        int dot = id.IndexOf('.');
        string p = dot > 0 ? id[..dot] : id;
        return p switch
        {
            "event" => NodeCategory.Event,
            "logic" or "flow" => NodeCategory.Logic,
            "filter" or "mod" => NodeCategory.Filter,
            "ai" or "tool" => NodeCategory.Ai,
            "db" or "file" or "cal" or "archive" => NodeCategory.Storage,
            "socket" or "mail" or "dcc" or "net" => NodeCategory.Network,
            "irc" => NodeCategory.Irc,
            "ircv3" => NodeCategory.Ircv3,
            "code" or "container" => NodeCategory.Code,
            "zim" or "media" => NodeCategory.Media,
            "ui" => NodeCategory.Ui,
            "num" or "gen" or "data" => NodeCategory.Data,
            _ => NodeCategory.Action,   // vestigial; nothing should land here
        };
    }

    /// <summary>Catalog grouped by category, in palette display order.</summary>
    public static IEnumerable<IGrouping<NodeCategory, NodeDef>> ByCategory()
    {
        var order = new[]
        {
            NodeCategory.Event, NodeCategory.Filter, NodeCategory.Logic, NodeCategory.Data, NodeCategory.Ai,
            NodeCategory.Storage, NodeCategory.Network, NodeCategory.Irc, NodeCategory.Ircv3, NodeCategory.Code,
            NodeCategory.Media, NodeCategory.Ui, NodeCategory.Action,
        };
        return All.GroupBy(d => d.Category).OrderBy(g => Array.IndexOf(order, g.Key));
    }
}
