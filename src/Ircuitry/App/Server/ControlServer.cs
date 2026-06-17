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
/// Auth is a bearer token (first run mints an admin token and prints it). Accounts/roles/sharing layer on later.
/// </summary>
public static class ControlServer
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private static readonly object Gate = new();          // serialises all workspace mutations
    private static AppModel _app = null!;
    private static List<McpTool> _tools = null!;
    private static readonly Dictionary<string, (string User, string Role)> _tokens = new();   // token -> account
    private static bool _web;

    public static int Run(string[] args)
    {
        string bind = Arg(args, "--bind") ?? "127.0.0.1";
        int port = int.TryParse(Arg(args, "--port"), out var p) ? p : 48700;
        _web = Array.IndexOf(args, "--web") >= 0;
        string? data = Arg(args, "--data");
        if (!string.IsNullOrWhiteSpace(data)) Environment.SetEnvironmentVariable("IRCUITRY_HOME", data);

        _app = new AppModel();
        _tools = McpTools.BuildRegistry();
        LoadOrMintTokens();

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
        public readonly Dictionary<App.Bot, long> LogRev = new();
        public readonly Dictionary<App.Bot, long> HistRev = new();
        public readonly Dictionary<App.Bot, bool> Running = new();
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
            foreach (var (bot, h) in c.FireHooks) try { bot.Runtime.OnFired -= h; } catch { }
            try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
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
                case "snapshot": await Reply(c, id, Snapshot()); break;
                case "subscribe":
                    c.Topics.Clear();
                    if (root.TryGetProperty("topics", out var ts) && ts.ValueKind == JsonValueKind.Array)
                        foreach (var t in ts.EnumerateArray()) if (t.ValueKind == JsonValueKind.String) c.Topics.Add(t.GetString()!);
                    if (c.Topics.Contains("nodes")) HookFires(c);
                    await Reply(c, id, new { topics = c.Topics.ToArray() });
                    break;
                case "start": case "stop":
                    if (!c.CanEdit) { await ReplyErr(c, id, "this token is read-only (viewer)"); break; }
                    await Reply(c, id, StartStop(root, op == "start")); break;
                case "call":
                {
                    string tool = Str(root, "tool");
                    var args = root.TryGetProperty("args", out var a) ? a : default;
                    var t = _tools.FirstOrDefault(x => x.Name == tool);
                    if (t == null) { await ReplyErr(c, id, "unknown tool: " + tool); break; }
                    if (t.Mutates && !c.CanEdit) { await ReplyErr(c, id, "this token is read-only (viewer) and cannot edit"); break; }
                    object result;
                    lock (Gate) { result = t.Run(args, _app); if (t.Mutates) _app.Save(announce: false); }
                    await Reply(c, id, new { tool, result });
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

    private static App.Bot? ResolveBot(JsonElement root)
    {
        if (root.TryGetProperty("bot", out var b))
        {
            if (b.ValueKind == JsonValueKind.Number && b.TryGetInt32(out var i) && i >= 0 && i < _app.Bots.Count) return _app.Bots[i];
            if (b.ValueKind == JsonValueKind.String) { var n = b.GetString(); return _app.Bots.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase)); }
        }
        return _app.Bots.Count > 0 ? _app.ActiveBot : null;
    }

    private static object Snapshot()
    {
        lock (Gate)
        {
            return new
            {
                active = _app.Active,
                bots = _app.Bots.Select(b => new
                {
                    name = b.Name,
                    running = b.Runtime.Running,
                    state = b.Runtime.State.ToString(),
                    nodes = b.Graph.Nodes.Count,
                    wires = b.Graph.Connections.Count,
                    graph = JsonDocument.Parse(Ircuitry.Graph.GraphSerializer.Save(b.Graph, b.Name)).RootElement.Clone(),
                    servers = b.Servers.Select(s => new { s.Label, s.Host, s.Port, tls = s.UseTls, s.Nick, s.Channels, s.ConnectOnStartup }).ToArray(),
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
            var b = bot;
            Action<string> h = nodeId => { if (!c.Closed && c.Topics.Contains("nodes")) _ = Send(c, new { evt = "node", bot = b.Name, id = nodeId }); };
            b.Runtime.OnFired += h;
            c.FireHooks.Add((b, h));
        }
    }

    private static App.Bot[] BotsSnapshot() { lock (Gate) return _app.Bots.ToArray(); }

    private static async Task Pump(Client c)
    {
        foreach (var b in BotsSnapshot()) { c.LogRev[b] = b.Log.Revision; c.HistRev[b] = b.Runtime.HistoryRevision; c.Running[b] = b.Runtime.Running; }
        while (!c.Closed && c.Ws.State == WebSocketState.Open)
        {
            foreach (var b in BotsSnapshot())
            {
                if (!c.LogRev.ContainsKey(b)) { c.LogRev[b] = b.Log.Revision; c.HistRev[b] = b.Runtime.HistoryRevision; c.Running[b] = b.Runtime.Running; continue; }   // a bot created after connect
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
                    bool run = b.Runtime.Running;
                    if (run != c.Running[b]) { c.Running[b] = run; await Send(c, new { evt = "status", bot = b.Name, running = run, state = b.Runtime.State.ToString() }); }
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
