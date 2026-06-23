using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ircuitry.App.Server;

/// <summary>
/// The client half of the control plane: connects to an <see cref="ControlServer"/> (local loopback or a remote
/// box) over WebSocket, authenticates with a token, and keeps a thread-safe cache of the remote workspace and
/// live state that the GUI reads each frame. Requests are fire-and-forget with an optional reply callback; the
/// receive loop runs on a background task and the UI drains marshalled callbacks via <see cref="Pump"/>.
/// </summary>
public sealed class ControlClient : IDisposable
{
    public enum Conn { Idle, Connecting, Connected, Reconnecting, Failed, Closed }
    public volatile Conn State = Conn.Idle;
    public string Error = "";
    public string ServerName = "";
    public string User = "";
    public string Role = "";   // admin / editor / viewer (from the server's hello)
    public string Label = "";   // friendly name for the instance switcher

    public sealed class RemoteServer { public string Label = "", Host = "", Nick = "", Channels = "", RealName = ""; public int Port = 6697; public bool Tls = true, ConnectOnStartup; }

    public sealed class RemoteBot
    {
        public string Name = ""; public bool Running; public string Stat = ""; public int Nodes, Wires;
        public long Tokens; public int Errors, Queue;   // fleet-health metrics from the server
        public int Messages, Actions;                    // IRC messages observed / actions sent (server's real counts)
        public bool Unapplied;   // running, but its stored graph differs from the graph the live runtime is executing
        // sharing, as this client sees it
        public string Owner = ""; public string Visibility = "public"; public bool Editable = true;
        public bool Mine; public bool CanEdit = true;
        public bool Private => Visibility == "private";
        // the bot's server/connection rows on the server, so an opened tab reflects them
        public List<RemoteServer> Servers = new();
        public long Rev;                                  // graph revision (for optimistic-concurrency base)
        public Dictionary<string, string> Vars = new();   // the bot's persistent variables on the server
    }

    /// <summary>A snapshot of a remote bot's live IRC session, for the bot's-eye viewer of a server-hosted bot.</summary>
    public sealed class RemoteIrc
    {
        public bool Connected;
        public string Nick = "", Network = "", ChanTypes = "#&";
        public List<string> Caps = new();
        public sealed class Chan { public string Name = "", Topic = ""; public List<(string nick, string prefix)> Members = new(); }
        public List<Chan> Channels = new();
        public List<(DateTime at, string text)> Notes = new();
        public List<Ircuitry.Core.RecentMsg> Messages = new();
    }

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private long _nextId;
    private readonly ConcurrentDictionary<long, Action<JsonElement>> _pending = new();
    private readonly ConcurrentQueue<Action> _ui = new();             // run on the UI thread via Pump()

    private readonly object _gate = new();
    private List<RemoteBot> _bots = new();
    private readonly ConcurrentQueue<string> _log = new();
    private int _logCount;

    /// <summary>(bot, nodeId) when a remote node fires - for canvas glow. Invoked on the UI thread (Pump).</summary>
    public Action<string, string>? OnNode;
    /// <summary>(bot, level, text) for each remote log line - so an open remote tab can mirror it into its console.</summary>
    public Action<string, string, string>? OnLog;
    /// <summary>(bot, trigger, summary, actions) for each remote run - mirrored into the remote tab's run history.</summary>
    public Action<string, string, string, int>? OnRun;

    private readonly ConcurrentDictionary<string, DateTime> _fired = new();   // "botnode" -> last fire (for glow)

    /// <summary>0..1 glow for a remote node that recently fired (fades over ~0.9s) - mirrors BotRuntime.FireGlow.</summary>
    public float FireGlow(string bot, string node)
    {
        if (!_fired.TryGetValue(bot + "\u001f" + node, out var t)) return 0f;
        float e = (float)(DateTime.UtcNow - t).TotalSeconds;
        return e < 0 || e > 0.9f ? 0f : 1f - e / 0.9f;
    }

    public bool BotRunning(string name) { lock (_gate) return _bots.Exists(b => b.Name == name && b.Running); }
    /// <summary>The cached remote status for a bot (incl. fleet-health metrics), or null if not seen yet.</summary>
    public RemoteBot? BotInfo(string name) { lock (_gate) return _bots.FirstOrDefault(b => b.Name == name); }
    /// <summary>A running remote bot whose stored graph hasn't been hot-applied to its live runtime yet (drives
    /// the remote "apply" affordance, mirroring the local <c>BotRuntime.HasUnapplied</c>).</summary>
    public bool BotUnapplied(string name) { lock (_gate) return _bots.Exists(b => b.Name == name && b.Unapplied); }

    /// <summary>Push a full graph to a remote bot (set_graph), carrying the base revision it was loaded from.
    /// onResult(newRev, stale): stale=true means a co-editor changed it first (newRev = the server's current rev,
    /// reload to get it); else newRev is the applied revision.</summary>
    public void PushGraph(string bot, string json, long baseRev, Action<long, bool>? onResult = null) =>
        Call("call", new { tool = "set_graph", args = new { bot, json, @base = baseRev } }, res =>
        {
            bool stale = res.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object
                && r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String && e.GetString() == "stale";
            long rev = stale
                ? (r.TryGetProperty("rev", out var sr) && sr.TryGetInt64(out var srv) ? srv : 0)
                : (res.TryGetProperty("rev", out var nr) && nr.TryGetInt64(out var nrv) ? nrv : 0);
            onResult?.Invoke(rev, stale);
        });

    /// <summary>Fetch a remote bot's graph (.ircbot JSON string) and hand it back on the UI thread.</summary>
    public void GetGraph(string bot, Action<string> onJson) =>
        Call("call", new { tool = "get_graph", args = new { bot } }, res => { if (res.TryGetProperty("result", out var g)) onJson(g.GetRawText()); });

    /// <summary>Fetch a remote bot's live IRC session (channels, members, topics, narration, recent messages) for
    /// the bot's-eye viewer of a server-hosted bot. The callback runs on the UI thread.</summary>
    public void GetIrcState(string bot, Action<RemoteIrc> onState) =>
        Call("ircstate", new { bot }, res =>
        {
            if (!res.TryGetProperty("result", out var r) || r.ValueKind != JsonValueKind.Object) return;
            var s = new RemoteIrc
            {
                Connected = r.TryGetProperty("connected", out var cn) && cn.ValueKind == JsonValueKind.True,
                Nick = JStr(r, "nick"), Network = JStr(r, "network"),
            };
            var ct = JStr(r, "chantypes"); if (ct.Length > 0) s.ChanTypes = ct;
            if (r.TryGetProperty("caps", out var caps) && caps.ValueKind == JsonValueKind.Array)
                foreach (var x in caps.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) s.Caps.Add(x.GetString()!);
            if (r.TryGetProperty("channels", out var chs) && chs.ValueKind == JsonValueKind.Array)
                foreach (var ch in chs.EnumerateArray())
                {
                    var cc = new RemoteIrc.Chan { Name = JStr(ch, "name"), Topic = JStr(ch, "topic") };
                    if (ch.TryGetProperty("members", out var ms) && ms.ValueKind == JsonValueKind.Array)
                        foreach (var m in ms.EnumerateArray()) cc.Members.Add((JStr(m, "nick"), JStr(m, "prefix")));
                    s.Channels.Add(cc);
                }
            if (r.TryGetProperty("notes", out var ns) && ns.ValueKind == JsonValueKind.Array)
                foreach (var n in ns.EnumerateArray()) s.Notes.Add((JTime(JStr(n, "at")), JStr(n, "text")));
            if (r.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
                foreach (var m in msgs.EnumerateArray())
                    s.Messages.Add(new Ircuitry.Core.RecentMsg(JStr(m, "nick"), JStr(m, "channel"), JStr(m, "text"), JStr(m, "id"), JTime(JStr(m, "at"))));
            onState(s);
        });

    private static string JStr(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static DateTime JTime(string s) => DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : default;

    /// <summary>Start/stop a bot on the server. Surfaces a server-side refusal (e.g. "no server set") into the
    /// bot's console so the button never silently does nothing.</summary>
    public void StartStop(string bot, bool start) => Call(start ? "start" : "stop", new { bot }, res =>
    {
        if (res.TryGetProperty("error", out var e)) { var msg = e.GetString() ?? (start ? "could not start" : "could not stop"); AppendLog($"[{bot}] (server) {msg}"); OnLog?.Invoke(bot, "Error", msg); }
    });

    /// <summary>Hot-apply a running remote bot's stored graph to its live runtime - no restart - the remote
    /// equivalent of the local "apply" floppy. Surfaces a server refusal into the bot's console.</summary>
    public void ApplyGraph(string bot, Action<bool>? onResult = null) => Call("apply", new { bot }, res =>
    {
        if (res.TryGetProperty("error", out var e)) { var msg = e.GetString() ?? "could not apply"; AppendLog($"[{bot}] (server) {msg}"); OnLog?.Invoke(bot, "Error", msg); onResult?.Invoke(false); }
        else onResult?.Invoke(true);
    });

    /// <summary>Push a bot's connection settings to the server (set_connection) so edits to host/port/nick/etc.
    /// on a remote tab actually take effect there.</summary>
    public void PushConnection(string bot, Ircuitry.Irc.IrcSettings s) =>
        Call("call", new { tool = "set_connection", args = new { bot, host = s.Host, port = s.Port, tls = s.UseTls, nick = s.Nick, channels = s.Channels, realName = s.RealName, saslUser = s.SaslUser, saslPass = s.SaslPass, serverPass = s.ServerPass } });

    /// <summary>Change a bot's sharing (owner/admin only). visibility is "private" or "public".</summary>
    public void SetAcl(string bot, string visibility, bool editable) => Call("set_acl", new { bot, visibility, editable });

    // ---------------- workflow management ----------------
    public void CreateBot(string name, Action<string>? onName = null) =>
        Call("call", new { tool = "create_bot", args = new { name } }, res => { if (onName != null) onName(res.TryGetProperty("result", out var r) && r.TryGetProperty("name", out var n) ? n.GetString() ?? name : name); });
    public void RenameBot(string bot, string name, Action? done = null) =>
        Call("call", new { tool = "rename_bot", args = new { bot, name } }, _ => done?.Invoke());
    public void DeleteBot(string bot, Action? done = null) =>
        Call("call", new { tool = "delete_bot", args = new { bot } }, _ => done?.Invoke());
    public void PushState(string bot, IReadOnlyDictionary<string, string> vars) =>
        Call("call", new { tool = "set_state", args = new { bot, state = vars } });
    public void PushServers(string bot, IReadOnlyList<Ircuitry.Irc.IrcSettings> servers) =>
        Call("call", new { tool = "set_servers", args = new { bot, servers = servers.Select(s => new { label = s.Label, host = s.Host, port = s.Port, tls = s.UseTls, nick = s.Nick, channels = s.Channels, realName = s.RealName, saslUser = s.SaslUser, saslPass = s.SaslPass, serverPass = s.ServerPass, connectOnStartup = s.ConnectOnStartup, acceptInvalidCerts = s.AcceptInvalidCerts, autoReconnect = s.AutoReconnect }).ToArray() } });

    // ---------------- server vault ----------------
    public void SetSecret(string name, string value, Action? done = null) =>
        Call("call", new { tool = "set_secret", args = new { name, value } }, _ => done?.Invoke());
    public void DeleteSecret(string name, Action? done = null) =>
        Call("call", new { tool = "delete_secret", args = new { name } }, _ => done?.Invoke());
    public void ListSecrets(Action<string[]> onNames) =>
        Call("call", new { tool = "list_secret_names", args = new { } }, res =>
        {
            var names = new List<string>();
            if (res.TryGetProperty("result", out var r) && r.TryGetProperty("names", out var na) && na.ValueKind == JsonValueKind.Array)
                foreach (var n in na.EnumerateArray()) if (n.ValueKind == JsonValueKind.String) names.Add(n.GetString()!);
            onNames(names.ToArray());
        });

    // ---------------- server admin / overview ----------------
    public sealed class ServerInfo { public string Version = "", Code = ""; public long UptimeSec; public int Bots; public (string user, string role, string editing)[] Clients = Array.Empty<(string, string, string)>(); }
    public sealed class RunLine { public string Time = "", Trigger = "", Summary = ""; public int Actions; public bool Fired; }
    public sealed class TokenLine { public string Id = "", User = "", Role = ""; }

    public void Info(Action<ServerInfo> onInfo) =>
        Call("info", null, res =>
        {
            var clients = new List<(string, string, string)>();
            if (res.TryGetProperty("clients", out var ca) && ca.ValueKind == JsonValueKind.Array)
                foreach (var c in ca.EnumerateArray()) clients.Add((Get(c, "user"), Get(c, "role"), Get(c, "editing")));
            onInfo(new ServerInfo
            {
                Version = Get(res, "version"), Code = Get(res, "code"),
                UptimeSec = res.TryGetProperty("uptimeSec", out var u) && u.TryGetInt64(out var us) ? us : 0,
                Bots = res.TryGetProperty("bots", out var b) && b.TryGetInt32(out var bi) ? bi : 0,
                Clients = clients.ToArray(),
            });
        });

    public void History(string bot, Action<RunLine[]> onRuns) =>
        Call("history", new { bot }, res =>
        {
            var runs = new List<RunLine>();
            if (res.TryGetProperty("runs", out var ra) && ra.ValueKind == JsonValueKind.Array)
                foreach (var r in ra.EnumerateArray())
                    runs.Add(new RunLine { Time = Get(r, "time"), Trigger = Get(r, "trigger"), Summary = Get(r, "summary"),
                        Actions = r.TryGetProperty("actions", out var ac) && ac.TryGetInt32(out var aci) ? aci : 0,
                        Fired = r.TryGetProperty("fired", out var f) && f.ValueKind == JsonValueKind.True });
            onRuns(runs.ToArray());
        });

    public void TestCommand(string bot, string message, string nick, string channel, Action<JsonElement> onResult) =>
        Call("call", new { tool = "test_command", args = new { bot, message, nick, channel } }, res => { if (res.TryGetProperty("result", out var r)) onResult(r); });

    public void Tokens(Action<TokenLine[]> onList) =>
        Call("tokens", null, res =>
        {
            var toks = new List<TokenLine>();
            if (res.TryGetProperty("tokens", out var ta) && ta.ValueKind == JsonValueKind.Array)
                foreach (var t in ta.EnumerateArray()) toks.Add(new TokenLine { Id = Get(t, "id"), User = Get(t, "user"), Role = Get(t, "role") });
            onList(toks.ToArray());
        });
    public void MintToken(string user, string role, Action<string>? onToken = null) =>
        Call("mint_token", new { user, role }, res => onToken?.Invoke(Get(res, "token")));
    public void RevokeToken(string tokenId, Action? done = null) => Call("revoke_token", new { tokenId }, _ => done?.Invoke());

    /// <summary>A co-editor's live cursor on the shared canvas, in graph (world) coordinates. Node is the id of
    /// the node they're holding right now (soft lock), or empty.</summary>
    public sealed class PeerCursor { public string User = ""; public string Bot = ""; public float X, Y; public string Node = ""; public DateTime Seen; }

    private readonly Dictionary<string, PeerCursor> _cursors = new();   // user -> their latest cursor
    private DateTime _lastCursorSend; private string _lastCursorNode = " ";

    /// <summary>Throttled report of where I'm editing on a bot's canvas (world coords) + the node I'm holding.
    /// Sends at ~15 Hz, but always flushes immediately when the held node changes (lock claim/release).</summary>
    public void SendCursor(string bot, float x, float y, string node)
    {
        var now = DateTime.UtcNow;
        if (node == _lastCursorNode && (now - _lastCursorSend).TotalMilliseconds < 66) return;
        _lastCursorSend = now; _lastCursorNode = node;
        Call("cursor", new { bot, x, y, node });
    }

    /// <summary>The fresh (seen &lt; 4s ago) cursors of everyone else editing the given bot.</summary>
    public IReadOnlyList<PeerCursor> PeersOn(string bot)
    {
        var now = DateTime.UtcNow;
        lock (_gate) return _cursors.Values.Where(p => p.Bot == bot && p.User != User && (now - p.Seen).TotalSeconds < 4).ToArray();
    }

    /// <summary>A stable, pleasant colour for a peer, derived from their name (so it matches across clients).</summary>
    public static (float r, float g, float b) PeerColor(string user)
    {
        int h = 0; foreach (var ch in user) h = unchecked(h * 31 + ch);
        float hue = (uint)h % 360 / 360f;
        return HsvToRgb(hue, 0.62f, 1f);
    }

    private static (float, float, float) HsvToRgb(float h, float s, float v)
    {
        float i = (float)Math.Floor(h * 6); float f = h * 6 - i;
        float p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
        return ((int)i % 6) switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
    }

    private string[] _peers = Array.Empty<string>();
    public IReadOnlyList<RemoteBot> Bots { get { lock (_gate) return _bots.ToArray(); } }
    public string[] Peers { get { lock (_gate) return _peers; } }
    public Action? OnWorkspaceChanged;   // remote edit by another client - invoked on the UI thread (Pump)
    public bool Connected => State == Conn.Connected;

    public string[] RecentLog(int n)
    {
        var all = _log.ToArray();
        return all.Length <= n ? all : all[^n..];
    }

    private string _url = "", _token = "";
    private volatile bool _userClosed, _authFailed;

    public void Connect(string url, string token, string label = "")
    {
        Disconnect();
        _url = url; _token = token; _userClosed = false; _authFailed = false;
        Label = label.Length > 0 ? label : url;
        State = Conn.Connecting; Error = "";
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => RunLoop(ct));
    }

    public void Disconnect()
    {
        _userClosed = true;
        try { _cts?.Cancel(); } catch { }
        try { _ws?.Abort(); } catch { }
        _ws = null;
        if (State is Conn.Connected or Conn.Connecting or Conn.Reconnecting) State = Conn.Closed;
    }

    /// <summary>Send an op; if onReply is set it's invoked (on the UI thread) with the reply's "result".</summary>
    public void Call(string op, object? args, Action<JsonElement>? onReply = null)
    {
        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open) return;
        long id = Interlocked.Increment(ref _nextId);
        // clone the element off the receive doc: it's disposed before the UI thread drains the callback
        if (onReply != null) _pending[id] = e => { var snap = e.Clone(); _ui.Enqueue(() => onReply(snap)); };
        var msg = Merge(new Dictionary<string, object?> { ["id"] = id, ["op"] = op }, args);
        _ = SendRaw(ws, msg);
    }

    public void Start(string bot) => Call("start", new { bot });
    public void Stop(string bot) => Call("stop", new { bot });
    public void Snapshot() => Call("snapshot", null, UpdateBots);

    /// <summary>Run queued reply callbacks + events on the calling (UI) thread. Call once per frame.</summary>
    public void Pump() { while (_ui.TryDequeue(out var a)) { try { a(); } catch { } } }

    // ---------------- connection loop (auto-reconnecting) ----------------
    private async Task RunLoop(CancellationToken ct)
    {
        int attempt = 0;
        while (!_userClosed && !ct.IsCancellationRequested)
        {
            bool connected = await ConnectOnce(ct);
            if (_userClosed || ct.IsCancellationRequested || _authFailed) break;   // a bad token shouldn't spin forever
            attempt = connected ? 1 : attempt + 1;   // a good session resets the backoff
            State = Conn.Reconnecting;
            int delayMs = Math.Min(15000, 700 * (1 << Math.Min(attempt, 5)));
            try { await Task.Delay(delayMs, ct); } catch { break; }
        }
        if (State != Conn.Failed) State = Conn.Closed;
    }

    private async Task<bool> ConnectOnce(CancellationToken ct)
    {
        ClientWebSocket ws = new();
        bool connected = false;
        try
        {
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            if (State != Conn.Reconnecting) State = Conn.Connecting;
            await ws.ConnectAsync(WsUri(_url), ct);
            _ws = ws;
            await SendRaw(ws, new { token = _token });
            string? hello = await Recv(ws, ct);
            if (hello == null) { Error = "no response"; return false; }
            using (var doc = JsonDocument.Parse(hello))
            {
                var r = doc.RootElement;
                if (!(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True))
                { _authFailed = true; Fail(r.TryGetProperty("error", out var e) ? e.GetString() ?? "rejected" : "rejected"); return false; }
                if (r.TryGetProperty("server", out var s) && s.TryGetProperty("name", out var nm)) ServerName = nm.GetString() ?? "";
                if (r.TryGetProperty("user", out var u)) User = u.GetString() ?? "";
                if (r.TryGetProperty("role", out var ro)) Role = ro.GetString() ?? "";
            }
            State = Conn.Connected; connected = true;
            Call("subscribe", new { topics = new[] { "logs", "status", "runs", "nodes", "workspace", "presence", "cursors" } });
            Snapshot();
            if (OnReconnected != null) _ui.Enqueue(() => OnReconnected?.Invoke());

            string? raw;
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open && (raw = await Recv(ws, ct)) != null)
                Dispatch(raw);
            return connected;
        }
        catch (OperationCanceledException) { return connected; }
        catch (Exception ex) { Error = ex.Message; return connected; }
        finally { try { ws.Dispose(); } catch { } _ws = null; }
    }

    /// <summary>Fired (UI thread) each time the session (re)connects - so the desktop can re-baseline open tabs.</summary>
    public Action? OnReconnected;

    private void Fail(string why) { Error = why; State = Conn.Failed; }

    private void Dispatch(string raw)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw); } catch { return; }
        using (doc)
        {
            var m = doc.RootElement;
            if (m.TryGetProperty("evt", out var ev)) { HandleEvent(ev.GetString() ?? "", m); return; }
            if (m.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt64(out var id) && _pending.TryRemove(id, out var cb))
            {
                if (m.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True && m.TryGetProperty("result", out var res)) cb(res);
                else if (m.TryGetProperty("error", out var er)) AppendLog("(error) " + (er.GetString() ?? ""));
            }
        }
    }

    private void HandleEvent(string evt, JsonElement m)
    {
        switch (evt)
        {
            case "log":
            {
                string bot = Get(m, "bot"), lvl = Get(m, "level"), txt = Get(m, "text");
                AppendLog($"[{bot}] {Get(m, "time")} {lvl,-6} {txt}");
                if (OnLog != null) _ui.Enqueue(() => OnLog?.Invoke(bot, lvl, txt));
                break;
            }
            case "status":
                Snapshot();   // refresh the bot list's running/state
                break;
            case "node":
            {
                string bot = Get(m, "bot"), id = Get(m, "id");
                _fired[bot + "\u001f" + id] = DateTime.UtcNow;                       // drive remote canvas glow
                if (OnNode != null) _ui.Enqueue(() => OnNode?.Invoke(bot, id));
                break;
            }
            case "run":
            {
                string bot = Get(m, "bot"), trig = Get(m, "trigger"), sum = Get(m, "summary");
                int acts = m.TryGetProperty("actions", out var ae) && ae.TryGetInt32(out var ai) ? ai : 0;
                AppendLog($"[{bot}] ran {trig} - {sum}");
                if (OnRun != null) _ui.Enqueue(() => OnRun?.Invoke(bot, trig, sum, acts));   // mirror into the remote tab's run history
                break;
            }
            case "workspace":
                Snapshot();                                          // another editor changed the graph/bots - re-sync
                if (OnWorkspaceChanged != null) _ui.Enqueue(() => OnWorkspaceChanged?.Invoke());
                break;
            case "presence":
                if (m.TryGetProperty("users", out var us) && us.ValueKind == JsonValueKind.Array)
                { var p = new List<string>(); foreach (var u in us.EnumerateArray()) if (u.ValueKind == JsonValueKind.String) p.Add(u.GetString()!); lock (_gate) _peers = p.ToArray(); }
                break;
            case "cursor":
            {
                string user = Get(m, "user");
                if (user.Length == 0) break;
                if (m.TryGetProperty("gone", out var g) && g.ValueKind == JsonValueKind.True) { lock (_gate) _cursors.Remove(user); break; }
                var pc = new PeerCursor
                {
                    User = user,
                    Bot = Get(m, "bot"),
                    X = m.TryGetProperty("x", out var xe) && xe.TryGetSingle(out var xf) ? xf : 0,
                    Y = m.TryGetProperty("y", out var ye) && ye.TryGetSingle(out var yf) ? yf : 0,
                    Node = Get(m, "node"),
                    Seen = DateTime.UtcNow,
                };
                lock (_gate) _cursors[user] = pc;
                break;
            }
        }
    }

    private void UpdateBots(JsonElement result)
    {
        if (!result.TryGetProperty("bots", out var bots) || bots.ValueKind != JsonValueKind.Array) return;
        var list = new List<RemoteBot>();
        foreach (var b in bots.EnumerateArray())
            list.Add(new RemoteBot
            {
                Name = Get(b, "name"),
                Running = b.TryGetProperty("running", out var r) && r.ValueKind == JsonValueKind.True,
                Unapplied = b.TryGetProperty("unapplied", out var ua) && ua.ValueKind == JsonValueKind.True,
                Stat = Get(b, "state"),
                Nodes = b.TryGetProperty("nodes", out var n) && n.TryGetInt32(out var ni) ? ni : 0,
                Wires = b.TryGetProperty("wires", out var w) && w.TryGetInt32(out var wi) ? wi : 0,
                Tokens = b.TryGetProperty("tokens", out var tk) && tk.TryGetInt64(out var tkl) ? tkl : 0,
                Errors = b.TryGetProperty("errors", out var er) && er.TryGetInt32(out var eri) ? eri : 0,
                Queue = b.TryGetProperty("queue", out var qd) && qd.TryGetInt32(out var qdi) ? qdi : 0,
                Messages = b.TryGetProperty("messages", out var ms) && ms.TryGetInt32(out var msi) ? msi : 0,
                Actions = b.TryGetProperty("actions", out var ax) && ax.TryGetInt32(out var axi) ? axi : 0,
                Rev = b.TryGetProperty("rev", out var rv) && rv.TryGetInt64(out var rvl) ? rvl : 0,
                Vars = b.TryGetProperty("vars", out var vv) && vv.ValueKind == JsonValueKind.Object
                    ? vv.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString())
                    : new Dictionary<string, string>(),
            });
        // fold in each bot's sharing info (acl), when the server sent it
        int i = 0;
        foreach (var b in bots.EnumerateArray())
        {
            if (i < list.Count && b.TryGetProperty("acl", out var acl) && acl.ValueKind == JsonValueKind.Object)
            {
                var rb = list[i];
                rb.Owner = Get(acl, "owner");
                rb.Visibility = acl.TryGetProperty("visibility", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "public" : "public";
                rb.Editable = !acl.TryGetProperty("editable", out var e) || e.ValueKind != JsonValueKind.False;
                rb.Mine = acl.TryGetProperty("mine", out var mi) && mi.ValueKind == JsonValueKind.True;
                rb.CanEdit = !acl.TryGetProperty("canEdit", out var ce) || ce.ValueKind != JsonValueKind.False;
            }
            if (i < list.Count && b.TryGetProperty("servers", out var srv) && srv.ValueKind == JsonValueKind.Array)
                foreach (var s in srv.EnumerateArray())
                    list[i].Servers.Add(new RemoteServer
                    {
                        Label = Get(s, "Label"), Host = Get(s, "Host"), Nick = Get(s, "Nick"),
                        Channels = Get(s, "Channels"), RealName = Get(s, "RealName"),
                        Port = s.TryGetProperty("Port", out var po) && po.TryGetInt32(out var pi) ? pi : 6697,
                        Tls = !s.TryGetProperty("tls", out var tl) || tl.ValueKind != JsonValueKind.False,
                        ConnectOnStartup = s.TryGetProperty("ConnectOnStartup", out var co) && co.ValueKind == JsonValueKind.True,
                    });
            i++;
        }
        lock (_gate) _bots = list;
    }

    private void AppendLog(string line)
    {
        _log.Enqueue(line);
        if (Interlocked.Increment(ref _logCount) > 600) { _log.TryDequeue(out _); Interlocked.Decrement(ref _logCount); }
    }

    // ---------------- ws plumbing ----------------
    private async Task SendRaw(ClientWebSocket ws, object o)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(o);
        await _sendLock.WaitAsync();
        try { if (ws.State == WebSocketState.Open) await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        catch { }
        finally { _sendLock.Release(); }
    }

    private static async Task<string?> Recv(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[8192];
        var sb = new System.IO.MemoryStream();
        try
        {
            while (true)
            {
                var res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                if (res.MessageType == WebSocketMessageType.Close) return null;
                sb.Write(buf, 0, res.Count);
                if (res.EndOfMessage) break;
                if (sb.Length > 16 * 1024 * 1024) return null;
            }
        }
        catch { return null; }
        return Encoding.UTF8.GetString(sb.ToArray());
    }

    private static Uri WsUri(string url)
    {
        string u = url.Trim();
        if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) u = "ws://" + u[7..];
        else if (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) u = "wss://" + u[8..];
        else if (!u.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) && !u.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            u = "ws://" + u;
        // ensure a /ws path (host:port -> ws://host:port/ws)
        var uri = new Uri(u);
        if (uri.AbsolutePath is "/" or "") uri = new Uri(uri, "/ws");
        return uri;
    }

    private static string Get(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : (e.TryGetProperty(k, out var v2) ? v2.ToString() : "");

    private static Dictionary<string, object?> Merge(Dictionary<string, object?> baseObj, object? extra)
    {
        if (extra != null)
            foreach (var pi in extra.GetType().GetProperties()) baseObj[pi.Name] = pi.GetValue(extra);
        return baseObj;
    }

    public void Dispose() => Disconnect();
}
