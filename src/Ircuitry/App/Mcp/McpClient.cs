using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ircuitry.App.Mcp;

/// <summary>
/// An OUTWARD MCP client: connects to an external Model Context Protocol server (stdio command or Streamable
/// HTTP), lists its tools, and forwards tool calls. Lets an Ask AI use any MCP server's tools (filesystem, git,
/// web, a database, …). JSON-RPC 2.0; stdio is newline-delimited, the same framing our own server uses.
/// Clients are cached per config and reused across runs (one persistent connection, not one per message).
/// </summary>
public sealed class McpClient : IDisposable
{
    public sealed record ToolInfo(string Name, string Description, JsonElement Schema);

    private const string ProtocolVersion = "2024-11-05";
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private readonly string _transport;
    private readonly string _url;
    private readonly List<(string key, string val)> _headers = new();

    private Process? _proc;
    private StreamWriter? _w;
    private readonly object _writeLock = new();
    private int _id;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private bool _initialized;
    private string _command = "";                       // the stdio command, for diagnostics
    private readonly object _errLock = new();
    private readonly Queue<string> _stderr = new();     // bounded tail of the server's stderr (the real failure reason)

    private McpClient(string transport, string url = "") { _transport = transport; _url = url; }

    // ---- cache: reuse a live connection per config so we don't spawn a process per message ----
    private static readonly ConcurrentDictionary<string, McpClient> _cache = new();
    // configs that just failed to start (e.g. a stdio command missing in this container) - back off so we report
    // the real reason instead of respawning the process and writing to a dead pipe on every single message
    private static readonly ConcurrentDictionary<string, (string msg, DateTime until)> _failed = new();

    /// <summary>Get (or create + initialize) a cached client for this config. Key it uniquely per node config.</summary>
    public static McpClient ForConfig(string transport, string commandOrUrl, List<(string, string)>? headers, int timeoutMs)
    {
        string key = transport + "" + commandOrUrl + "" + string.Join(";", (headers ?? new()).ConvertAll(h => h.Item1 + "=" + h.Item2));
        if (_failed.TryGetValue(key, out var f))   // a recent hard failure: report the real reason, don't respawn every message
        {
            if (f.until > DateTime.UtcNow) throw new Exception(f.msg);
            _failed.TryRemove(key, out _);          // cooldown elapsed - allow a fresh attempt (maybe it's fixed now)
        }
        var c = _cache.GetOrAdd(key, _ => Create(transport, commandOrUrl, headers));
        if (!c.Alive)   // a stdio server that died - drop and respawn
        {
            _cache.TryRemove(key, out _);
            try { c.Dispose(); } catch { }
            c = _cache.GetOrAdd(key, _ => Create(transport, commandOrUrl, headers));
        }
        if (!c._initialized) lock (c) if (!c._initialized)
        {
            try { c.Initialize(timeoutMs); c._initialized = true; }
            catch (Exception ex)
            {
                string msg = c.FailureMessage(ex);
                _failed[key] = (msg, DateTime.UtcNow.AddSeconds(60));   // back off so we don't respawn every message
                _cache.TryRemove(key, out _);
                try { c.Dispose(); } catch { }
                throw new Exception(msg);
            }
        }
        return c;
    }

    private static McpClient Create(string transport, string commandOrUrl, List<(string, string)>? headers)
    {
        if (transport == "http")
        {
            var c = new McpClient("http", commandOrUrl);
            if (headers != null) c._headers.AddRange(headers);
            return c;
        }
        var s = new McpClient("stdio");
        s.SpawnStdio(commandOrUrl);
        return s;
    }

    private bool Alive => _transport == "http" || (_proc is { HasExited: false });

    private void SpawnStdio(string command)
    {
        _command = command;
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows()) { psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(command); }
        else { psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(command); }
        _proc = Process.Start(psi) ?? throw new Exception("could not start MCP server: " + command);
        _w = _proc.StandardInput;
        new Thread(() => { try { string? line; while ((line = _proc!.StandardOutput.ReadLine()) != null) OnLine(line); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("MCP stdout reader stopped: " + ex.Message); } })
            { IsBackground = true, Name = "mcp-read" }.Start();
        // capture stderr so a failure ("npx: not found", a playwright crash, ...) has a real reason attached,
        // instead of just the "Broken pipe" we'd otherwise hit writing to the dead process
        new Thread(() => { try { string? line; while ((line = _proc!.StandardError.ReadLine()) != null) lock (_errLock) { _stderr.Enqueue(line.Trim()); while (_stderr.Count > 12) _stderr.Dequeue(); } } catch { } })
            { IsBackground = true, Name = "mcp-err" }.Start();
    }

    private string StderrTail() { lock (_errLock) return string.Join(" | ", _stderr).Trim(); }

    // Turn a write/init failure into a message that says WHY: if the stdio server already exited, surface its exit
    // code and the tail of its stderr (e.g. "sh: npx: not found"), not the bare "Broken pipe" from the dead pipe.
    private string FailureMessage(Exception ex)
    {
        if (_transport == "stdio" && _proc != null)
        {
            try { _proc.WaitForExit(400); } catch { }
            if (_proc.HasExited)
            {
                int code = -1; try { code = _proc.ExitCode; } catch { }
                string err = StderrTail();
                string hint = (code == 127 || err.Contains("not found") || err.Contains("No such file")) ? " - is node/npx installed in this container?" : "";
                return $"MCP server exited (code {code}){hint}" + (err.Length > 0 ? ": " + err : $" [{_command}]");
            }
        }
        return $"MCP server '{_command}': {ex.Message}";
    }

    private void WriteLine(string msg)
    {
        try { lock (_writeLock) { _w!.WriteLine(msg); _w.Flush(); } }
        catch (Exception ex) { throw new Exception(FailureMessage(ex)); }
    }

    private void OnLine(string line)
    {
        if (line.Trim().Length == 0) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement.Clone();
            if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id) && _pending.TryRemove(id, out var tcs))
                tcs.TrySetResult(root);
        }
        catch { /* a log line or notification, not a reply - ignore */ }
    }

    public void Initialize(int timeoutMs)
    {
        Request("initialize", new { protocolVersion = ProtocolVersion, capabilities = new { }, clientInfo = new { name = "ircuitry", version = "1.0.0" } }, timeoutMs);
        Notify("notifications/initialized", null);
    }

    public List<ToolInfo> ListTools(int timeoutMs)
    {
        var r = Request("tools/list", new { }, timeoutMs);
        var list = new List<ToolInfo>();
        if (r.TryGetProperty("result", out var res) && res.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
            foreach (var t in tools.EnumerateArray())
            {
                string name = t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (name.Length == 0) continue;
                string desc = t.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                JsonElement schema = t.TryGetProperty("inputSchema", out var s) ? s.Clone() : default;
                list.Add(new ToolInfo(name, desc, schema));
            }
        return list;
    }

    public string Call(string name, Dictionary<string, string> args, int timeoutMs)
    {
        var r = Request("tools/call", new { name, arguments = BuildArgs(args) }, timeoutMs);
        if (r.TryGetProperty("error", out var err)) return "MCP error: " + err.GetRawText();
        if (!r.TryGetProperty("result", out var res)) return "";
        if (res.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var item in content.EnumerateArray())
                sb.AppendLine(item.TryGetProperty("text", out var tx) ? tx.GetString() : item.GetRawText());
            return sb.ToString().Trim();
        }
        return res.GetRawText();
    }

    // model args arrive as strings (with non-string values pre-serialized as raw JSON) - re-type them so the MCP
    // server gets numbers/objects, not stringified ones.
    private static Dictionary<string, object?> BuildArgs(Dictionary<string, string> args)
    {
        var d = new Dictionary<string, object?>();
        foreach (var kv in args)
        {
            string v = kv.Value;
            object? val = v;
            if (v.Length > 0 && (v[0] is '{' or '[' || v is "true" or "false" or "null" || v[0] == '-' || char.IsDigit(v[0])))
                try { using var jd = JsonDocument.Parse(v); val = jd.RootElement.Clone(); } catch { val = v; }
            d[kv.Key] = val;
        }
        return d;
    }

    private void Notify(string method, object? prms)
    {
        if (_transport == "http") { try { HttpCall(method, prms, null, 5000); } catch { } return; }
        var msg = JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = prms }, Json);
        WriteLine(msg);
    }

    private JsonElement Request(string method, object? prms, int timeoutMs)
    {
        int id = Interlocked.Increment(ref _id);
        if (_transport == "http") return HttpCall(method, prms, id, timeoutMs);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var msg = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = prms }, Json);
        WriteLine(msg);
        if (!tcs.Task.Wait(Math.Clamp(timeoutMs, 500, 60000))) { _pending.TryRemove(id, out _); throw new TimeoutException("MCP " + method + " timed out"); }
        return tcs.Task.Result;
    }

    // Streamable HTTP: POST one JSON-RPC message; the reply is JSON or an SSE 'data:' event.
    private JsonElement HttpCall(string method, object? prms, int? id, int timeoutMs)
    {
        var headers = new List<(string, string)> { ("Content-Type", "application/json"), ("Accept", "application/json, text/event-stream") };
        headers.AddRange(_headers);
        object payload = id.HasValue ? new { jsonrpc = "2.0", id = id.Value, method, @params = prms } : new { jsonrpc = "2.0", method, @params = prms };
        var (status, body) = Net.Http.Send("POST", _url, headers, JsonSerializer.Serialize(payload, Json), Math.Clamp(timeoutMs / 1000, 1, 60));
        if (status is < 200 or >= 300) throw new Exception("MCP http " + status + ": " + body);
        string jsonText = body.TrimStart();
        if (jsonText.StartsWith("event:") || jsonText.StartsWith("data:"))   // SSE: take the first data: line
            foreach (var ln in body.Split('\n'))
                if (ln.StartsWith("data:")) { jsonText = ln[5..].Trim(); break; }
        if (jsonText.Length == 0) return default;
        using var doc = JsonDocument.Parse(jsonText);
        return doc.RootElement.Clone();
    }

    public void Dispose()
    {
        try { _w?.Dispose(); } catch { }
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
        try { _proc?.Dispose(); } catch { }
    }

    /// <summary>Tear down every cached MCP connection (call on app exit).</summary>
    public static void StopAll()
    {
        foreach (var kv in _cache) try { kv.Value.Dispose(); } catch { }
        _cache.Clear();
        _failed.Clear();
    }
}
