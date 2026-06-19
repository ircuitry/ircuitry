using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ircuitry.App.Mcp;

namespace Ircuitry.App.Server;

/// <summary>
/// The headless control plane: <c>ircuitry --server [--bind H] [--port N] [--data DIR] [--web]</c>. It hosts
/// one live <see cref="AppModel"/> (the workspace + running bots) and exposes an authenticated WebSocket so a
/// remote desktop editor or the cockpit can build, run and watch bots. Every edit op reuses the existing MCP
/// tool registry (the same handlers Claude uses), so the operation surface is shared. Live state - log lines,
/// node fires, bot status, run history - streams to subscribed clients.
///
/// Auth is a bearer token (first run mints an admin token and prints it). Global roles (admin/editor/viewer)
/// set a ceiling; per-bot sharing (owner + private/public + others-can-edit) narrows it - see ControlServer.Acl.cs.
/// </summary>
public static partial class ControlServer
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private static readonly object Gate = new();          // serialises all workspace mutations
    private static AppModel _app = null!;
    private static List<McpTool> _tools = null!;
    private static readonly Dictionary<string, (string User, string Role)> _tokens = new();   // token -> account
    private static readonly ConcurrentDictionary<Client, byte> _clients = new();   // everyone connected (for collab broadcast + presence)
    private static bool _web;
    private static DateTime _started;
    // per-bot graph revision: bumped on every graph-changing edit; a set_graph push carries the base it loaded
    // from, so a stale push (someone else edited first) is rejected instead of silently clobbering.
    private static readonly Dictionary<string, long> _botRev = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _graphTools = new() { "set_graph", "add_node", "set_param", "connect", "disconnect", "remove_node", "auto_layout" };
    private static long BotRev(string name) { lock (Gate) return _botRev.TryGetValue(name, out var r) ? r : 0; }
    private static long BumpRev(string name) { lock (Gate) { var r = (_botRev.TryGetValue(name, out var x) ? x : 0) + 1; _botRev[name] = r; return r; } }
    private static string Mask(string token) => token.Length > 12 ? token[..6] + "…" + token[^4..] : token;

    public static int Run(string[] args)
    {
        string bind = Arg(args, "--bind") ?? "127.0.0.1";
        int port = int.TryParse(Arg(args, "--port"), out var p) ? p : 48700;
        _web = Array.IndexOf(args, "--web") >= 0;
        if (Array.IndexOf(args, "--no-code") >= 0) Ircuitry.Net.CodeRunner.Disabled = true;   // refuse untrusted code on a shared host
        // a shared server hardens the code sandbox by default: no network, read-only filesystem. Operators who
        // trust their workflows can loosen each with --code-net / --code-fs.
        Ircuitry.Net.CodeRunner.NoNetwork = Array.IndexOf(args, "--code-net") < 0;
        Ircuitry.Net.CodeRunner.ConfineFs = Array.IndexOf(args, "--code-fs") < 0;
        string? data = Arg(args, "--data");
        if (!string.IsNullOrWhiteSpace(data)) Environment.SetEnvironmentVariable("IRCUITRY_HOME", data);

        _started = DateTime.UtcNow;
        _app = new AppModel();
        _tools = McpTools.BuildRegistry();
        LoadOrMintTokens();
        LoadAcl();

        // bring up the bots their servers asked to auto-start (matches the GUI); clients can start the rest
        foreach (var bot in _app.Bots)
        {
            var live = bot.Servers.Where(s => s.Host.Length > 0 && s.ConnectOnStartup).ToList();
            if (live.Count > 0) { try { bot.Runtime.Start(bot.Graph, live); } catch { } }
        }

        string host = bind is "0.0.0.0" or "*" or "+" or "any" ? "+" : bind;
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://{host}:{port}/");
        try { listener.Start(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ircuitry server: cannot bind http://{host}:{port}/  ({ex.Message})");
            if (host == "+") Console.Error.WriteLine("  (binding all interfaces may need elevated rights on some systems; try --bind 127.0.0.1 or a specific IP)");
            return 1;
        }

        Console.WriteLine($"ircuitry control server on http://{(host == "+" ? bind : host)}:{port}/  -  workspace {AppModel.WorkspaceDir}");
        Console.WriteLine($"  bots: {_app.Bots.Count}    connect a desktop with: Server > Connect ({bind}:{port})");
        if (_web) Console.WriteLine($"  cockpit (PWA): http://{(host == "+" ? bind : host)}:{port}/");
        Console.WriteLine($"  admin token: {AdminToken()}");
        Console.WriteLine($"  ({_tokens.Count} token(s); add more with: ircuitry --add-token NAME --role editor|viewer)");
        // be honest about how well code nodes are contained on THIS host
        if (Ircuitry.Net.CodeRunner.Disabled)
            Console.WriteLine("  code nodes: disabled (--no-code)");
        else if (Ircuitry.Net.Sandbox.StrongIsolation)
            Console.WriteLine($"  code nodes: sandboxed via {Ircuitry.Net.Sandbox.Mechanism}" + (Ircuitry.Net.CodeRunner.NoNetwork ? ", no network" : "") + (Ircuitry.Net.CodeRunner.ConfineFs ? ", read-only fs" : ""));
        else
            Console.WriteLine("  code nodes: WARNING - strong isolation unavailable on this host (no working bubblewrap/userns).\n"
                + "              code runs resource-capped only, with network + filesystem access. On a shared host\n"
                + "              install bubblewrap (apt install bubblewrap) for a real sandbox, or pass --no-code.");

        using var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); try { listener.Stop(); } catch { } };

        _ = AcceptLoop(listener);
        stop.Wait();

        Console.WriteLine("stopping…");
        foreach (var b in _app.Bots) { try { b.Runtime.Stop(); } catch { } }
        Thread.Sleep(300);
        return 0;
    }

    private static async Task AcceptLoop(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => Dispatch(ctx));
        }
    }

    private static async Task Dispatch(HttpListenerContext ctx)
    {
        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (path == "/healthz")
            {
                WriteJson(ctx, 200, new { ok = true, app = "ircuitry", version = AppInfo.Version, bots = _app.Bots.Count });
                return;
            }
            if (path.StartsWith("/hook/", StringComparison.Ordinal))   // webhook trigger (path IS the shared secret; no token)
            {
                string hook = Uri.UnescapeDataString(path["/hook/".Length..]);
                string body; using (var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8)) body = sr.ReadToEnd();
                var vars = new Dictionary<string, string> { ["body"] = body, ["webhook"] = hook, ["method"] = ctx.Request.HttpMethod };
                foreach (string? k in ctx.Request.QueryString.AllKeys) if (!string.IsNullOrEmpty(k)) vars["query." + k] = ctx.Request.QueryString[k] ?? "";
                int fired = 0;
                lock (Gate) foreach (var b in _app.Bots) fired += b.Runtime.FireWebhook(hook, vars);
                WriteJson(ctx, fired > 0 ? 200 : 404, new { fired });
                return;
            }
            if (path == "/ws" && ctx.Request.IsWebSocketRequest)
            {
                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                await Serve(wsCtx.WebSocket);
                return;
            }
            if (_web && ctx.Request.HttpMethod == "GET" && TryServeWeb(ctx, path)) return;
            WriteJson(ctx, 404, new { error = "not found" });
        }
        catch { try { ctx.Response.Abort(); } catch { } }
    }

    // ---------------- web cockpit (served when --web) ----------------
    private static readonly string[] WebFiles = { "index.html", "cockpit.js", "cockpit.css", "manifest.webmanifest", "sw.js" };

    private static bool TryServeWeb(HttpListenerContext ctx, string path)
    {
        string file = path is "/" or "" ? "index.html" : path.TrimStart('/');
        if (file == "icon.png") { ServeIcon(ctx); return true; }
        if (Array.IndexOf(WebFiles, file) < 0) return false;
        var asm = typeof(ControlServer).Assembly;
        string? res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".web." + file, StringComparison.OrdinalIgnoreCase));
        if (res == null) return false;
        using var s = asm.GetManifestResourceStream(res);
        if (s == null) return false;
        var ms = new MemoryStream(); s.CopyTo(ms); var bytes = ms.ToArray();
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = ContentType(file);
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
        return true;
    }

    private static void ServeIcon(HttpListenerContext ctx)
    {
        try
        {
            string p = Path.Combine(AppContext.BaseDirectory, "assets", "icons", "icon-256.png");
            if (!File.Exists(p)) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
            var bytes = File.ReadAllBytes(p);
            ctx.Response.StatusCode = 200; ctx.Response.ContentType = "image/png";
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length); ctx.Response.Close();
        }
        catch { try { ctx.Response.Abort(); } catch { } }
    }

    private static string ContentType(string file) => file.EndsWith(".html") ? "text/html; charset=utf-8"
        : file.EndsWith(".js") ? "application/javascript; charset=utf-8"
        : file.EndsWith(".css") ? "text/css; charset=utf-8"
        : file.EndsWith(".webmanifest") ? "application/manifest+json; charset=utf-8"
        : "application/octet-stream";

    // ---------------- one connected client ----------------
    private sealed class Client
    {
        public readonly WebSocket Ws;
        public readonly SemaphoreSlim SendLock = new(1, 1);
        public volatile bool Closed;
        public string User = "";
        public string Role = "viewer";
        public bool CanEdit => Role is "admin" or "editor";
        public readonly HashSet<string> Topics = new();
        // live cursor / soft-lock: where this client is editing right now
        public string CurBot = ""; public float CurX, CurY; public string CurNode = "";
        public readonly Dictionary<App.Bot, long> LogRev = new();
        public readonly Dictionary<App.Bot, long> HistRev = new();
        public readonly Dictionary<App.Bot, bool> Running = new();
        public readonly Dictionary<App.Bot, bool> Unapplied = new();
        public readonly List<(App.Bot bot, Action<string> handler)> FireHooks = new();
        public Client(WebSocket ws) => Ws = ws;
    }

    private static async Task Serve(WebSocket ws)
    {
        var c = new Client(ws);
        try
        {
            // first message must authenticate
            var hello = await Read(ws);
            if (hello == null) return;
            using (var doc = JsonDocument.Parse(hello))
            {
                var root = doc.RootElement;
                string token = Str(root, "token");
                if (!_tokens.TryGetValue(token, out var acct))
                { await Send(c, new { evt = "hello", ok = false, error = "invalid token" }); return; }
                c.User = acct.User; c.Role = acct.Role;
            }
            await Send(c, new { evt = "hello", ok = true, server = new { name = "ircuitry", version = AppInfo.Version }, user = c.User, role = c.Role });
            _clients[c] = 0;
            BroadcastPresence();

            var pump = Task.Run(() => Pump(c));
            string? raw;
            while ((raw = await Read(ws)) != null) await Handle(c, raw);
            c.Closed = true;
            await pump;
        }
        catch { c.Closed = true; }
        finally
        {
            c.Closed = true;
            _clients.TryRemove(c, out _);
            BroadcastPresence();
            BroadcastCursor(c, gone: true);   // pull this editor's cursor from everyone else's canvas
            foreach (var (bot, h) in c.FireHooks) try { bot.Runtime.OnFired -= h; } catch { }
            try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        }
    }

    // ---------------- collab broadcast + presence ----------------
    private static void Broadcast(string topic, object o)
    { foreach (var c in _clients.Keys) if (!c.Closed && c.Topics.Contains(topic)) _ = Send(c, o); }

    private static void BroadcastPresence()
    {
        var users = _clients.Keys.Where(c => !c.Closed).Select(c => c.User).ToArray();
        foreach (var c in _clients.Keys) if (!c.Closed) _ = Send(c, new { evt = "presence", users });
    }

    // relay a client's cursor (or its departure) to the other people editing a bot they can see
    private static void BroadcastCursor(Client from, bool gone)
    {
        foreach (var c in _clients.Keys)
        {
            if (c == from || c.Closed || !c.Topics.Contains("cursors")) continue;
            if (!gone && !CanSee(c, from.CurBot)) continue;
            _ = Send(c, gone
                ? (object)new { evt = "cursor", user = from.User, gone = true }
                : new { evt = "cursor", user = from.User, bot = from.CurBot, x = from.CurX, y = from.CurY, node = from.CurNode });
        }
    }

    // ---------------- request ops ----------------
    private static async Task Handle(Client c, string raw)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(raw).RootElement; } catch { return; }
        object? id = root.TryGetProperty("id", out var idEl) ? IdVal(idEl) : null;
        string op = Str(root, "op");
        try
        {
            switch (op)
            {
                case "ping": await Reply(c, id, new { pong = true }); break;
                case "snapshot": await Reply(c, id, Snapshot(c)); break;
                case "set_acl":
                    await Reply(c, id, SetAcl(c, root));
                    Broadcast("workspace", new { evt = "workspace", tool = "set_acl", by = c.User });
                    break;
                case "cursor":   // fire-and-forget live cursor + soft node lock; relayed to co-editors of the same bot
                {
                    string cb = Str(root, "bot");
                    c.CurBot = cb;
                    c.CurX = root.TryGetProperty("x", out var xe) && xe.TryGetSingle(out var xf) ? xf : 0;
                    c.CurY = root.TryGetProperty("y", out var ye) && ye.TryGetSingle(out var yf) ? yf : 0;
                    c.CurNode = Str(root, "node");
                    BroadcastCursor(c, false);
                    break;
                }
                case "info":   // server overview: version, uptime, who's connected, sandbox/code state
                    await Reply(c, id, new
                    {
                        name = "ircuitry", version = AppInfo.Version,
                        uptimeSec = (long)(DateTime.UtcNow - _started).TotalSeconds,
                        bots = BotsSnapshot().Length,
                        clients = _clients.Keys.Where(x => !x.Closed).Select(x => new { user = x.User, role = x.Role, editing = x.CurBot }).ToArray(),
                        web = _web,
                        code = Ircuitry.Net.CodeRunner.Disabled ? "disabled" : (Ircuitry.Net.Sandbox.StrongIsolation ? "sandboxed (" + Ircuitry.Net.Sandbox.Mechanism + ")" : "unconfined"),
                    });
                    break;
                case "history":   // a bot's recent run records (the server's own runtime, not the desktop's idle one)
                {
                    var bn = ExplicitBotName(root) ?? "";
                    var b = BotsSnapshot().FirstOrDefault(x => x.Name.Equals(bn, StringComparison.OrdinalIgnoreCase));
                    if (b == null || !CanSee(c, b.Name)) { await ReplyErr(c, id, "no such bot: " + bn); break; }
                    var runs = b.Runtime.History();
                    await Reply(c, id, new
                    {
                        bot = b.Name,
                        runs = runs.Select(r => new { time = r.Time.ToString("HH:mm:ss"), trigger = r.Trigger, summary = r.Summary, actions = r.Actions, fired = r.Fired }).ToArray(),
                    });
                    break;
                }
                case "tokens":   // admin: list access tokens (masked)
                    if (!IsAdmin(c)) { await ReplyErr(c, id, "admin only"); break; }
                    await Reply(c, id, new { tokens = _tokens.Select(kv => new { id = Mask(kv.Key), user = kv.Value.User, role = kv.Value.Role }).ToArray() });
                    break;
                case "mint_token":   // admin: create a new token
                {
                    if (!IsAdmin(c)) { await ReplyErr(c, id, "admin only"); break; }
                    string user = Str(root, "user"); string role = Str(root, "role").ToLowerInvariant();
                    if (role is not ("admin" or "editor" or "viewer")) role = "viewer";
                    var tk = NewToken();
                    lock (Gate) { _tokens[tk] = (user.Length > 0 ? user : "user", role); SaveTokens(); }
                    await Reply(c, id, new { token = tk, id = Mask(tk), user = _tokens[tk].User, role });
                    break;
                }
                case "revoke_token":   // admin: revoke a token by its masked id (or full value)
                {
                    if (!IsAdmin(c)) { await ReplyErr(c, id, "admin only"); break; }
                    string tid = Str(root, "tokenId");   // NOT "id" - that's the message envelope's request id
                    string? match;
                    lock (Gate)
                    {
                        match = _tokens.Keys.FirstOrDefault(k => k == tid || Mask(k) == tid);
                        if (match != null && _tokens[match].Role == "admin" && _tokens.Count(kv => kv.Value.Role == "admin") <= 1) match = "__lastadmin__";
                        else if (match != null) { _tokens.Remove(match); SaveTokens(); }
                    }
                    if (match == null) { await ReplyErr(c, id, "no such token"); break; }
                    if (match == "__lastadmin__") { await ReplyErr(c, id, "can't revoke the last admin token"); break; }
                    await Reply(c, id, new { revoked = tid });
                    break;
                }
                case "subscribe":
                    c.Topics.Clear();
                    if (root.TryGetProperty("topics", out var ts) && ts.ValueKind == JsonValueKind.Array)
                        foreach (var t in ts.EnumerateArray()) if (t.ValueKind == JsonValueKind.String) c.Topics.Add(t.GetString()!);
                    if (c.Topics.Contains("nodes")) HookFires(c);
                    await Reply(c, id, new { topics = c.Topics.ToArray() });
                    if (c.Topics.Contains("presence")) await Send(c, new { evt = "presence", users = _clients.Keys.Where(x => !x.Closed).Select(x => x.User).ToArray() });
                    break;
                case "start": case "stop":
                {
                    if (!c.CanEdit) { await ReplyErr(c, id, "this token is read-only (viewer)"); break; }
                    var sb = ExplicitBotName(root);
                    if (sb != null && BotExists(sb) && !CanEditBot(c, sb)) { await ReplyErr(c, id, $"you don't have access to '{sb}'"); break; }
                    await Reply(c, id, StartStop(root, op == "start")); break;
                }
                case "apply":   // hot-swap a running bot's stored graph into its live runtime (no restart)
                {
                    if (!c.CanEdit) { await ReplyErr(c, id, "this token is read-only (viewer)"); break; }
                    // apply must name an explicit, visible bot you can edit - never silently fall back to the active bot
                    var ab = ExplicitBotName(root);
                    if (ab == null || !BotExists(ab) || !CanSee(c, ab)) { await ReplyErr(c, id, "no such bot"); break; }
                    if (!CanEditBot(c, ab)) { await ReplyErr(c, id, $"you don't have edit access to '{ab}'"); break; }
                    await Reply(c, id, ApplyLive(root)); break;
                }
                case "call":
                {
                    string tool = Str(root, "tool");
                    var args = root.TryGetProperty("args", out var a) ? a : default;
                    var t = _tools.FirstOrDefault(x => x.Name == tool);
                    if (t == null) { await ReplyErr(c, id, "unknown tool: " + tool); break; }
                    if (t.Mutates && !c.CanEdit) { await ReplyErr(c, id, "this token is read-only (viewer) and cannot edit"); break; }
                    // per-bot sharing: a request that names a bot is gated by that bot's ACL (read -> see, mutate -> edit)
                    var target = ExplicitBotName(args);
                    if (target != null && BotExists(target))
                    {
                        if (!CanSee(c, target)) { await ReplyErr(c, id, "no such bot: " + target); break; }   // hide its existence
                        if (t.Mutates && !CanEditBot(c, target)) { await ReplyErr(c, id, $"you don't have edit access to '{target}'"); break; }
                    }
                    // optimistic concurrency: a full-graph push carries the rev it loaded from; reject if stale
                    long? baseRev = tool == "set_graph" && args.ValueKind == JsonValueKind.Object
                        && args.TryGetProperty("base", out var be) && be.TryGetInt64(out var br) ? br : null;
                    var before = tool == "create_bot" ? BotsSnapshot().Select(x => x.Name).ToHashSet() : null;
                    object result = null!; long newRev = 0; bool stale = false;
                    lock (Gate)
                    {
                        if (baseRev.HasValue && target != null && baseRev.Value != BotRev(target))
                        { stale = true; result = new { error = "stale", rev = BotRev(target) }; }
                        else
                        {
                            result = t.Run(args, _app);
                            if (t.Mutates) _app.Save(announce: false);
                            if (target != null && _graphTools.Contains(tool)) newRev = BumpRev(target);
                        }
                    }
                    if (before != null) foreach (var nb in BotsSnapshot().Select(x => x.Name)) if (!before.Contains(nb)) ClaimBot(nb, c.User);   // a new bot belongs to its creator
                    await Reply(c, id, new { tool, result, rev = newRev });
                    if (!stale && t.Mutates) Broadcast("workspace", new { evt = "workspace", tool, by = c.User });   // other editors re-sync
                    break;
                }
                default: await ReplyErr(c, id, "unknown op: " + op); break;
            }
        }
        catch (Exception ex) { await ReplyErr(c, id, ex.Message); }
    }

    private static object StartStop(JsonElement root, bool start)
    {
        lock (Gate)
        {
            var bot = ResolveBot(root);
            if (bot == null) return new { error = "no such bot" };
            if (start)
            {
                var live = bot.Servers.Where(s => s.Host.Length > 0).ToList();
                if (live.Count == 0) return new { error = "no server set" };
                bot.Runtime.Start(bot.Graph, live);
            }
            else bot.Runtime.Stop();
            return new { bot = bot.Name, running = bot.Runtime.Running };
        }
    }

    /// <summary>Apply the bot's current (already-pushed) graph to its live runtime without a restart - the
    /// server twin of the desktop's "apply to the live bot". No-op-with-error if the bot isn't running.</summary>
    private static object ApplyLive(JsonElement root)
    {
        lock (Gate)
        {
            var bot = ResolveBot(root);
            if (bot == null) return new { error = "no such bot" };
            if (!bot.Runtime.Running) return new { error = "bot is not running" };
            bot.Runtime.ApplyGraph(bot.Graph);
            return new { bot = bot.Name, running = true, unapplied = false };
        }
    }

    private static App.Bot? ResolveBot(JsonElement root)
    {
        if (root.TryGetProperty("bot", out var b))
        {
            if (b.ValueKind == JsonValueKind.Number && b.TryGetInt32(out var i) && i >= 0 && i < _app.Bots.Count) return _app.Bots[i];
            if (b.ValueKind == JsonValueKind.String) { var n = b.GetString(); return _app.Bots.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase)); }
        }
        return _app.Bots.Count > 0 ? _app.ActiveBot : null;
    }

    private static object Snapshot(Client c)
    {
        lock (Gate)
        {
            var visible = _app.Bots.Where(b => CanSee(c, b.Name)).ToList();   // private bots you don't own are hidden
            var active = _app.Bots.Count > 0 ? visible.IndexOf(_app.ActiveBot) : -1;
            return new
            {
                active,
                bots = visible.Select(b => new
                {
                    name = b.Name,
                    running = b.Runtime.Running,
                    unapplied = b.Runtime.HasUnapplied(b.Graph),
                    state = b.Runtime.State.ToString(),
                    rev = BotRev(b.Name),
                    vars = new Dictionary<string, string>(b.State),
                    nodes = b.Graph.Nodes.Count,
                    wires = b.Graph.Connections.Count,
                    tokens = b.Runtime.TokensTotal,        // fleet health: AI tokens spent
                    errors = b.Runtime.ErrorCount,         // node errors attributed
                    queue = b.Runtime.OutQueueDepth,       // outgoing send backlog
                    graph = JsonDocument.Parse(Ircuitry.Graph.GraphSerializer.Save(b.Graph, b.Name)).RootElement.Clone(),
                    servers = b.Servers.Select(s => new { s.Label, s.Host, s.Port, tls = s.UseTls, s.Nick, s.Channels, s.RealName, s.ConnectOnStartup }).ToArray(),
                    acl = AclView(c, b.Name),
                }).ToArray(),
            };
        }
    }

    // ---------------- live event feed ----------------
    private static void HookFires(Client c)
    {
        foreach (var bot in BotsSnapshot())
        {
            if (c.FireHooks.Any(h => h.bot == bot)) continue;
            if (!CanSee(c, bot.Name)) continue;   // don't stream node fires of a bot this client can't see
            var b = bot;
            Action<string> h = nodeId => { if (!c.Closed && c.Topics.Contains("nodes")) _ = Send(c, new { evt = "node", bot = b.Name, id = nodeId }); };
            b.Runtime.OnFired += h;
            c.FireHooks.Add((b, h));
        }
    }

    private static App.Bot[] BotsSnapshot() { lock (Gate) return _app.Bots.ToArray(); }

    private static async Task Pump(Client c)
    {
        foreach (var b in BotsSnapshot()) { c.LogRev[b] = b.Log.Revision; c.HistRev[b] = b.Runtime.HistoryRevision; c.Running[b] = b.Runtime.Running; c.Unapplied[b] = b.Runtime.HasUnapplied(b.Graph); }
        while (!c.Closed && c.Ws.State == WebSocketState.Open)
        {
            foreach (var b in BotsSnapshot())
            {
                if (!c.LogRev.ContainsKey(b)) { c.LogRev[b] = b.Log.Revision; c.HistRev[b] = b.Runtime.HistoryRevision; c.Running[b] = b.Runtime.Running; c.Unapplied[b] = b.Runtime.HasUnapplied(b.Graph); continue; }   // a bot created after connect
                if (!CanSee(c, b.Name)) continue;   // never stream a private bot's log/status/runs to a client who can't see it
                if (c.Topics.Contains("logs"))
                {
                    long rev = b.Log.Revision;
                    if (rev > c.LogRev[b])
                    {
                        foreach (var e in b.Log.Tail((int)Math.Min(rev - c.LogRev[b], 200)))
                            await Send(c, new { evt = "log", bot = b.Name, time = e.Time.ToString("HH:mm:ss"), level = e.Level.ToString(), text = e.Text, server = e.Server });
                        c.LogRev[b] = rev;
                    }
                }
                if (c.Topics.Contains("status"))
                {
                    bool run = b.Runtime.Running, un = b.Runtime.HasUnapplied(b.Graph);
                    if (run != c.Running[b] || un != c.Unapplied[b])
                    { c.Running[b] = run; c.Unapplied[b] = un; await Send(c, new { evt = "status", bot = b.Name, running = run, state = b.Runtime.State.ToString(), unapplied = un }); }
                }
                if (c.Topics.Contains("runs"))
                {
                    long hr = b.Runtime.HistoryRevision;
                    if (hr > c.HistRev[b]) { c.HistRev[b] = hr; var h = b.Runtime.History(); if (h.Count > 0) { var r = h[^1]; await Send(c, new { evt = "run", bot = b.Name, trigger = r.Trigger, summary = r.Summary, actions = r.Actions }); } }
                }
            }
            try { await Task.Delay(120); } catch { break; }
        }
    }

    // ---------------- ws + json plumbing ----------------
    private static async Task<string?> Read(WebSocket ws)
    {
        var buf = new byte[8192];
        var sb = new MemoryStream();
        try
        {
            while (true)
            {
                var res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                if (res.MessageType == WebSocketMessageType.Close) return null;
                sb.Write(buf, 0, res.Count);
                if (res.EndOfMessage) break;
                if (sb.Length > 8 * 1024 * 1024) return null;   // 8MB guard
            }
        }
        catch { return null; }
        return Encoding.UTF8.GetString(sb.ToArray());
    }

    private static async Task Send(Client c, object o)
    {
        if (c.Closed) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(o, Json);
        await c.SendLock.WaitAsync();
        try { if (c.Ws.State == WebSocketState.Open) await c.Ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        catch { c.Closed = true; }
        finally { c.SendLock.Release(); }
    }

    private static Task Reply(Client c, object? id, object result) => Send(c, new { id, ok = true, result });
    private static Task ReplyErr(Client c, object? id, string error) => Send(c, new { id, ok = false, error });

    private static void WriteJson(HttpListenerContext ctx, int status, object body)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }
        catch { try { ctx.Response.Abort(); } catch { } }
    }

    private static string Str(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static object? IdVal(JsonElement id) => id.ValueKind switch { JsonValueKind.Number => id.TryGetInt64(out var l) ? l : id.GetDouble(), JsonValueKind.String => id.GetString(), _ => (object?)null };
    private static string? Arg(string[] args, string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null; }

    // ---------------- tokens ----------------
    private static string TokenFile => Path.Combine(AppModel.WorkspaceDir, "server-tokens.json");

    private static void LoadTokens()
    {
        _tokens.Clear();
        try
        {
            if (!File.Exists(TokenFile)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(TokenFile));
            if (!doc.RootElement.TryGetProperty("tokens", out var t) || t.ValueKind != JsonValueKind.Object) return;
            foreach (var p in t.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String) _tokens[p.Name] = (p.Value.GetString() ?? "user", "admin");   // legacy: token -> name
                else if (p.Value.ValueKind == JsonValueKind.Object)
                    _tokens[p.Name] = (p.Value.TryGetProperty("user", out var u) ? u.GetString() ?? "user" : "user",
                                       p.Value.TryGetProperty("role", out var r) ? r.GetString() ?? "viewer" : "viewer");
            }
        }
        catch { /* ignore a corrupt file; mint fresh */ }
    }

    private static void SaveTokens()
    {
        try
        {
            Directory.CreateDirectory(AppModel.WorkspaceDir);
            var obj = new Dictionary<string, object?>();
            foreach (var kv in _tokens) obj[kv.Key] = new { user = kv.Value.User, role = kv.Value.Role };
            File.WriteAllText(TokenFile, JsonSerializer.Serialize(new { tokens = obj }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static void LoadOrMintTokens()
    {
        LoadTokens();
        if (!_tokens.Values.Any(v => v.Role == "admin"))   // always guarantee an admin can get in
        {
            _tokens[NewToken()] = ("admin", "admin");
            SaveTokens();
        }
    }

    private static string AdminToken() => _tokens.FirstOrDefault(kv => kv.Value.Role == "admin").Key ?? _tokens.Keys.FirstOrDefault() ?? "(none)";

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    /// <summary>CLI: <c>ircuitry --add-token NAME [--role admin|editor|viewer] [--data DIR]</c> mints a token.</summary>
    public static int AddToken(string[] args)
    {
        string? data = Arg(args, "--data");
        if (!string.IsNullOrWhiteSpace(data)) Environment.SetEnvironmentVariable("IRCUITRY_HOME", data);
        string user = Arg(args, "--add-token") ?? "user";
        string role = (Arg(args, "--role") ?? "editor").ToLowerInvariant();
        if (role is not ("admin" or "editor" or "viewer")) role = "editor";
        LoadTokens();
        string tok = NewToken();
        _tokens[tok] = (user, role);
        SaveTokens();
        Console.WriteLine($"added {role} token for '{user}':");
        Console.WriteLine(tok);
        return 0;
    }
}
