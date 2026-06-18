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
    public enum Conn { Idle, Connecting, Connected, Failed, Closed }
    public volatile Conn State = Conn.Idle;
    public string Error = "";
    public string ServerName = "";
    public string User = "";
    public string Label = "";   // friendly name for the instance switcher

    public sealed class RemoteBot { public string Name = ""; public bool Running; public string Stat = ""; public int Nodes, Wires; }

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

    private readonly ConcurrentDictionary<string, DateTime> _fired = new();   // "botnode" -> last fire (for glow)

    /// <summary>0..1 glow for a remote node that recently fired (fades over ~0.9s) - mirrors BotRuntime.FireGlow.</summary>
    public float FireGlow(string bot, string node)
    {
        if (!_fired.TryGetValue(bot + "\u001f" + node, out var t)) return 0f;
        float e = (float)(DateTime.UtcNow - t).TotalSeconds;
        return e < 0 || e > 0.9f ? 0f : 1f - e / 0.9f;
    }

    public bool BotRunning(string name) { lock (_gate) return _bots.Exists(b => b.Name == name && b.Running); }

    /// <summary>Push a full graph (.ircbot JSON) to a remote bot (set_graph). The server applies, persists and broadcasts.</summary>
    public void PushGraph(string bot, string json) => Call("call", new { tool = "set_graph", args = new { bot, json } });

    /// <summary>Fetch a remote bot's graph (.ircbot JSON string) and hand it back on the UI thread.</summary>
    public void GetGraph(string bot, Action<string> onJson) =>
        Call("call", new { tool = "get_graph", args = new { bot } }, res => { if (res.TryGetProperty("result", out var g)) onJson(g.GetRawText()); });

    public void StartStop(string bot, bool start) => Call(start ? "start" : "stop", new { bot });

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

    public void Connect(string url, string token, string label = "")
    {
        Disconnect();
        Label = label.Length > 0 ? label : url;
        State = Conn.Connecting; Error = "";
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => Loop(url, token, ct));
    }

    public void Disconnect()
    {
        try { _cts?.Cancel(); } catch { }
        try { _ws?.Abort(); } catch { }
        _ws = null;
        if (State is Conn.Connected or Conn.Connecting) State = Conn.Closed;
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

    // ---------------- connection loop ----------------
    private async Task Loop(string url, string token, CancellationToken ct)
    {
        ClientWebSocket ws = new();
        try
        {
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            await ws.ConnectAsync(WsUri(url), ct);
            _ws = ws;
            await SendRaw(ws, new { token });
            string? hello = await Recv(ws, ct);
            if (hello == null) { Fail("no response"); return; }
            using (var doc = JsonDocument.Parse(hello))
            {
                var r = doc.RootElement;
                if (!(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True))
                { Fail(r.TryGetProperty("error", out var e) ? e.GetString() ?? "rejected" : "rejected"); return; }
                if (r.TryGetProperty("server", out var s) && s.TryGetProperty("name", out var nm)) ServerName = nm.GetString() ?? "";
                if (r.TryGetProperty("user", out var u)) User = u.GetString() ?? "";
            }
            State = Conn.Connected;
            Call("subscribe", new { topics = new[] { "logs", "status", "runs", "nodes", "workspace", "presence" } });
            Snapshot();

            string? raw;
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open && (raw = await Recv(ws, ct)) != null)
                Dispatch(raw);
            if (State == Conn.Connected) State = Conn.Closed;
        }
        catch (OperationCanceledException) { if (State != Conn.Failed) State = Conn.Closed; }
        catch (Exception ex) { Fail(ex.Message); }
        finally { try { ws.Dispose(); } catch { } }
    }

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
                AppendLog($"[{Get(m, "bot")}] ran {Get(m, "trigger")} - {Get(m, "summary")}");
                break;
            case "workspace":
                Snapshot();                                          // another editor changed the graph/bots - re-sync
                if (OnWorkspaceChanged != null) _ui.Enqueue(() => OnWorkspaceChanged?.Invoke());
                break;
            case "presence":
                if (m.TryGetProperty("users", out var us) && us.ValueKind == JsonValueKind.Array)
                { var p = new List<string>(); foreach (var u in us.EnumerateArray()) if (u.ValueKind == JsonValueKind.String) p.Add(u.GetString()!); lock (_gate) _peers = p.ToArray(); }
                break;
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
                Stat = Get(b, "state"),
                Nodes = b.TryGetProperty("nodes", out var n) && n.TryGetInt32(out var ni) ? ni : 0,
                Wires = b.TryGetProperty("wires", out var w) && w.TryGetInt32(out var wi) ? wi : 0,
            });
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
