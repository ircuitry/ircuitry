using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace Ircuitry.Runtime;

/// <summary>
/// A general-purpose socket engine for a bot: open and listen on TCP / UDP / WebSocket (with optional TLS and
/// configurable framing), keep the connections alive, and turn everything that happens on the wire into graph
/// events. Every live connection (whether we dialled out or a peer connected to one of our listeners) gets a
/// stable id; data arrives via the socket.data trigger carrying that id, and Send/Close target it. Nothing here
/// is IRC-specific - it's the reusable transport the higher-level nodes (and, next, an in-graph IRC server) are
/// built on. Fires socket.connect / socket.data / socket.disconnect through the owning runtime so they run on
/// the normal worker pool.
/// </summary>
public sealed class SocketManager : IDisposable
{
    public enum Framing { Raw, Line, Delimiter, Length }

    /// <summary>How a connection is dialled / a listener is opened.</summary>
    public sealed class Opts
    {
        public bool Tls;
        public Framing Framing = Framing.Line;
        public string Delimiter = "\n";
        public string CertPath = "", CertPass = "";          // server-side TLS cert (auto if blank)
        public Dictionary<string, string> Headers = new();   // WebSocket request/handshake headers
        public bool AcceptInvalidCerts = true;               // outbound TLS: many servers are self-signed
    }

    private readonly Action<string, Dictionary<string, string>> _fire;   // (sub, vars) -> fire "socket.<sub>"
    private readonly Action<string, bool> _log;                          // (message, isError)
    private int _seq;
    private volatile bool _disposed;

    private readonly ConcurrentDictionary<string, Conn> _conns = new();
    private readonly ConcurrentDictionary<string, Listener> _listeners = new();

    public SocketManager(Action<string, Dictionary<string, string>> fire, Action<string, bool> log)
    { _fire = fire; _log = log; }

    public int ConnectionCount => _conns.Count;
    public int ListenerCount => _listeners.Count;

    // ---- a single live connection (dialled out, or accepted by a listener) ----
    private sealed class Conn
    {
        public string Id = "", Proto = "", Remote = "", ListenerId = "";
        public Opts Opt = new();
        public Stream? Stream;              // tcp / tls
        public WebSocket? Ws;               // websocket
        public UdpClient? Udp;              // udp (one socket; Remote is the peer for replies)
        public IPEndPoint? UdpPeer;
        public readonly object WriteLock = new();
        public volatile bool Closed;
    }

    private sealed class Listener
    {
        public string Id = "", Proto = "";
        public Opts Opt = new();
        public TcpListener? Tcp;
        public UdpClient? Udp;
        public HttpListener? Http;          // websocket server
        public X509Certificate2? Cert;      // tls server cert
        public volatile bool Closed;
        public readonly HashSet<string> Clients = new();   // connection ids accepted here
    }

    private string NextId(string p) => p + Interlocked.Increment(ref _seq).ToString("x");

    private static Framing ParseFraming(string s) => s?.ToLowerInvariant() switch
    {
        "raw" => Framing.Raw, "delimiter" => Framing.Delimiter, "length" => Framing.Length, _ => Framing.Line,
    };

    /// <summary>Build options from a node's string params.</summary>
    public static Opts MakeOpts(bool tls, string framing, string delimiter, string certPath, string certPass, Dictionary<string, string>? headers = null)
        => new() { Tls = tls, Framing = ParseFraming(framing), Delimiter = delimiter.Length > 0 ? delimiter : "\n",
                   CertPath = certPath, CertPass = certPass, Headers = headers ?? new() };

    // =================================================================
    //  LISTEN - start a server on a port
    // =================================================================
    public string Listen(string proto, int port, Opts opt)
    {
        proto = proto.ToLowerInvariant();
        string id = NextId("L");
        var L = new Listener { Id = id, Proto = proto, Opt = opt };
        try
        {
            if (proto == "udp")
            {
                L.Udp = new UdpClient(port);
                _listeners[id] = L;
                new Thread(() => UdpReceiveLoop(L.Udp, id, opt, "")) { IsBackground = true, Name = "sock-udp-" + id }.Start();
            }
            else if (proto == "ws")
            {
                L.Http = new HttpListener();
                L.Http.Prefixes.Add($"http://+:{port}/");
                L.Http.Start();
                _listeners[id] = L;
                new Thread(() => WsAcceptLoop(L)) { IsBackground = true, Name = "sock-wsacc-" + id }.Start();
            }
            else // tcp / tls
            {
                if (opt.Tls) L.Cert = LoadServerCert(opt);
                L.Tcp = new TcpListener(IPAddress.Any, port);
                L.Tcp.Start();
                _listeners[id] = L;
                new Thread(() => TcpAcceptLoop(L)) { IsBackground = true, Name = "sock-acc-" + id }.Start();
            }
            _log($"listening ({proto}{(opt.Tls ? "+tls" : "")}) on :{port} [{id}]", false);
            return id;
        }
        catch (Exception ex) { _log($"listen {proto} :{port} failed: {ex.Message}", true); CloseListener(L); return ""; }
    }

    private void TcpAcceptLoop(Listener L)
    {
        while (!L.Closed && !_disposed)
        {
            TcpClient client;
            try { client = L.Tcp!.AcceptTcpClient(); }
            catch { break; }   // listener stopped
            new Thread(() => AcceptTcp(L, client)) { IsBackground = true, Name = "sock-conn" }.Start();
        }
    }

    private void AcceptTcp(Listener L, TcpClient client)
    {
        string id = NextId("C");
        string remote = client.Client.RemoteEndPoint?.ToString() ?? "";
        try
        {
            Stream stream = client.GetStream();
            if (L.Opt.Tls && L.Cert != null)
            {
                var ssl = new SslStream(stream, false);
                ssl.AuthenticateAsServer(L.Cert, false, true);
                stream = ssl;
            }
            var conn = new Conn { Id = id, Proto = L.Proto, Remote = remote, ListenerId = L.Id, Opt = L.Opt, Stream = stream };
            _conns[id] = conn;
            lock (L.Clients) L.Clients.Add(id);
            FireConn("connect", conn);
            StreamReadLoop(conn);
        }
        catch (Exception ex) { _log($"accept failed: {ex.Message}", true); try { client.Close(); } catch { } }
    }

    // =================================================================
    //  CONNECT - dial out
    // =================================================================
    public string Connect(string proto, string host, int port, Opts opt)
    {
        proto = proto.ToLowerInvariant();
        string id = NextId("C");
        try
        {
            if (proto == "udp")
            {
                var udp = new UdpClient();
                udp.Connect(host, port);   // sets the default peer; we can still receive
                var conn = new Conn { Id = id, Proto = "udp", Remote = host + ":" + port, Opt = opt, Udp = udp, UdpPeer = (IPEndPoint?)udp.Client.RemoteEndPoint };
                _conns[id] = conn;
                FireConn("connect", conn);
                new Thread(() => UdpReceiveLoop(udp, id, opt, host + ":" + port)) { IsBackground = true, Name = "sock-udp-" + id }.Start();
                return id;
            }
            if (proto == "ws")
            {
                var ws = new ClientWebSocket();
                foreach (var h in opt.Headers) try { ws.Options.SetRequestHeader(h.Key, h.Value); } catch { }
                string scheme = opt.Tls ? "wss" : "ws";
                string url = host.StartsWith("ws", StringComparison.OrdinalIgnoreCase) ? host : $"{scheme}://{host}:{port}/";
                ws.ConnectAsync(new Uri(url), CancellationToken.None).GetAwaiter().GetResult();
                var conn = new Conn { Id = id, Proto = "ws", Remote = url, Opt = opt, Ws = ws };
                _conns[id] = conn;
                FireConn("connect", conn);
                new Thread(() => WsReadLoop(conn)) { IsBackground = true, Name = "sock-ws-" + id }.Start();
                return id;
            }
            // tcp / tls
            var tcp = new TcpClient();
            if (!tcp.ConnectAsync(host, port).Wait(12000)) throw new TimeoutException("connect timed out");
            Stream stream = tcp.GetStream();
            if (opt.Tls)
            {
                var ssl = new SslStream(stream, false, (_, _, _, errs) => opt.AcceptInvalidCerts || errs == SslPolicyErrors.None);
                ssl.AuthenticateAsClient(host);
                stream = ssl;
            }
            var c = new Conn { Id = id, Proto = "tcp", Remote = host + ":" + port, Opt = opt, Stream = stream };
            _conns[id] = c;
            FireConn("connect", c);
            new Thread(() => StreamReadLoop(c)) { IsBackground = true, Name = "sock-rd-" + id }.Start();   // return now; read on its own thread
            return id;
        }
        catch (Exception ex) { _log($"connect {proto} {host}:{port} failed: {ex.Message}", true); _conns.TryRemove(id, out _); return ""; }
    }

    /// <summary>Connect on a background thread and hand the new connection id back via <paramref name="onId"/>
    /// (so an action node returns immediately instead of blocking the worker on the dial + read loop).</summary>
    public void ConnectAsync(string proto, string host, int port, Opts opt, Action<string> onId)
        => new Thread(() => onId(Connect(proto, host, port, opt))) { IsBackground = true, Name = "sock-dial" }.Start();

    // =================================================================
    //  read loops -> socket.data
    // =================================================================
    private void StreamReadLoop(Conn conn)
    {
        var buf = new byte[16384];
        var acc = new List<byte>();
        try
        {
            int n;
            while (!conn.Closed && conn.Stream != null && (n = conn.Stream.Read(buf, 0, buf.Length)) > 0)
            {
                if (conn.Opt.Framing == Framing.Raw) { FireData(conn, Encoding.UTF8.GetString(buf, 0, n)); continue; }
                for (int i = 0; i < n; i++) acc.Add(buf[i]);
                DrainFrames(conn, acc);
            }
        }
        catch { /* connection dropped */ }
        finally { Drop(conn); }
    }

    private void DrainFrames(Conn conn, List<byte> acc)
    {
        if (conn.Opt.Framing == Framing.Length)
        {
            while (acc.Count >= 4)
            {
                int len = (acc[0] << 24) | (acc[1] << 16) | (acc[2] << 8) | acc[3];
                if (len < 0 || len > 64 * 1024 * 1024) { acc.Clear(); break; }   // sanity
                if (acc.Count < 4 + len) break;
                var payload = Encoding.UTF8.GetString(acc.GetRange(4, len).ToArray());
                acc.RemoveRange(0, 4 + len);
                FireData(conn, payload);
            }
            return;
        }
        // Line / Delimiter
        byte[] delim = conn.Opt.Framing == Framing.Line ? new byte[] { (byte)'\n' } : Encoding.UTF8.GetBytes(conn.Opt.Delimiter);
        if (delim.Length == 0) delim = new byte[] { (byte)'\n' };
        int idx;
        while ((idx = IndexOf(acc, delim)) >= 0)
        {
            var line = acc.GetRange(0, idx).ToArray();
            acc.RemoveRange(0, idx + delim.Length);
            var s = Encoding.UTF8.GetString(line);
            if (conn.Opt.Framing == Framing.Line && s.EndsWith("\r")) s = s[..^1];   // tolerate CRLF
            FireData(conn, s);
        }
    }

    private static int IndexOf(List<byte> hay, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= hay.Count; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++) if (hay[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    private void UdpReceiveLoop(UdpClient udp, string id, Opts opt, string boundRemote)
    {
        while (!_disposed)
        {
            try
            {
                IPEndPoint? ep = null;
                var data = udp.Receive(ref ep);
                var vars = new Dictionary<string, string>
                {
                    ["conn"] = id, ["proto"] = "udp", ["remote"] = ep?.ToString() ?? boundRemote,
                    ["data"] = Encoding.UTF8.GetString(data), ["line"] = Encoding.UTF8.GetString(data),
                };
                _fire("data", vars);
            }
            catch { break; }   // socket closed
        }
    }

    private void WsReadLoop(Conn conn)
    {
        var buf = new byte[16384];
        var acc = new List<byte>();
        try
        {
            while (!conn.Closed && conn.Ws != null && conn.Ws.State == WebSocketState.Open)
            {
                var res = conn.Ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).GetAwaiter().GetResult();
                if (res.MessageType == WebSocketMessageType.Close) break;
                for (int i = 0; i < res.Count; i++) acc.Add(buf[i]);
                if (res.EndOfMessage)
                {
                    FireData(conn, Encoding.UTF8.GetString(acc.ToArray()));
                    acc.Clear();
                }
            }
        }
        catch { }
        finally { Drop(conn); }
    }

    private void WsAcceptLoop(Listener L)
    {
        while (!L.Closed && !_disposed)
        {
            HttpListenerContext ctx;
            try { ctx = L.Http!.GetContext(); }
            catch { break; }
            new Thread(() => AcceptWs(L, ctx)) { IsBackground = true, Name = "sock-wsconn" }.Start();
        }
    }

    private void AcceptWs(Listener L, HttpListenerContext ctx)
    {
        if (!ctx.Request.IsWebSocketRequest) { try { ctx.Response.StatusCode = 426; ctx.Response.Close(); } catch { } return; }
        string id = NextId("C");
        try
        {
            var wsCtx = ctx.AcceptWebSocketAsync(null).GetAwaiter().GetResult();
            var conn = new Conn { Id = id, Proto = "ws", Remote = ctx.Request.RemoteEndPoint?.ToString() ?? "", ListenerId = L.Id, Opt = L.Opt, Ws = wsCtx.WebSocket };
            _conns[id] = conn;
            lock (L.Clients) L.Clients.Add(id);
            FireConn("connect", conn);
            WsReadLoop(conn);
        }
        catch (Exception ex) { _log("ws accept failed: " + ex.Message, true); }
    }

    // =================================================================
    //  SEND / BROADCAST / CLOSE
    // =================================================================
    public bool Send(string connId, byte[] data)
    {
        if (!_conns.TryGetValue(connId, out var conn) || conn.Closed) return false;
        try
        {
            if (conn.Ws != null) { lock (conn.WriteLock) conn.Ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult(); return true; }
            if (conn.Udp != null) { lock (conn.WriteLock) conn.Udp.Send(data, data.Length); return true; }
            if (conn.Stream != null) { lock (conn.WriteLock) { conn.Stream.Write(data, 0, data.Length); conn.Stream.Flush(); } return true; }
        }
        catch (Exception ex) { _log($"send to {connId} failed: {ex.Message}", true); Drop(conn); }
        return false;
    }

    /// <summary>Send to a UDP target by address (host:port), for connectionless replies on a listener socket.</summary>
    public bool SendTo(string connId, byte[] data, string remote)
    {
        if (!_conns.TryGetValue(connId, out var conn) || conn.Udp == null) return Send(connId, data);
        try
        {
            int colon = remote.LastIndexOf(':');
            if (colon <= 0) return false;
            var ep = new IPEndPoint(IPAddress.Parse(remote[..colon]), int.Parse(remote[(colon + 1)..]));
            lock (conn.WriteLock) conn.Udp.Send(data, data.Length, ep);
            return true;
        }
        catch (Exception ex) { _log($"udp send to {remote} failed: {ex.Message}", true); return false; }
    }

    public int Broadcast(string listenerId, byte[] data)
    {
        if (!_listeners.TryGetValue(listenerId, out var L)) return 0;
        int n = 0;
        string[] ids; lock (L.Clients) ids = new List<string>(L.Clients).ToArray();
        foreach (var cid in ids) if (Send(cid, data)) n++;
        return n;
    }

    public void Close(string id)
    {
        if (_conns.TryGetValue(id, out var conn)) { Drop(conn); return; }
        if (_listeners.TryGetValue(id, out var L)) CloseListener(L);
    }

    private void Drop(Conn conn)
    {
        if (conn.Closed) return;
        conn.Closed = true;
        _conns.TryRemove(conn.Id, out _);
        if (conn.ListenerId.Length > 0 && _listeners.TryGetValue(conn.ListenerId, out var L)) lock (L.Clients) L.Clients.Remove(conn.Id);
        try { conn.Stream?.Dispose(); } catch { }
        try { conn.Ws?.Abort(); } catch { }
        try { conn.Udp?.Dispose(); } catch { }
        FireConn("disconnect", conn);
    }

    private void CloseListener(Listener L)
    {
        L.Closed = true;
        _listeners.TryRemove(L.Id, out _);
        try { L.Tcp?.Stop(); } catch { }
        try { L.Udp?.Dispose(); } catch { }
        try { L.Http?.Close(); } catch { }
        string[] ids; lock (L.Clients) ids = new List<string>(L.Clients).ToArray();
        foreach (var cid in ids) if (_conns.TryGetValue(cid, out var c)) Drop(c);
    }

    public void StopAll()
    {
        foreach (var L in new List<Listener>(_listeners.Values)) CloseListener(L);
        foreach (var c in new List<Conn>(_conns.Values)) Drop(c);
    }

    public void Dispose() { _disposed = true; StopAll(); }

    // ---- helpers ----
    private void FireConn(string sub, Conn conn) => _fire(sub, new Dictionary<string, string>
    {
        ["conn"] = conn.Id, ["proto"] = conn.Proto, ["remote"] = conn.Remote, ["listener"] = conn.ListenerId,
    });

    private void FireData(Conn conn, string payload) => _fire("data", new Dictionary<string, string>
    {
        ["conn"] = conn.Id, ["proto"] = conn.Proto, ["remote"] = conn.Remote, ["listener"] = conn.ListenerId,
        ["data"] = payload, ["line"] = payload,
    });

    private X509Certificate2 LoadServerCert(Opts opt)
    {
        if (opt.CertPath.Length > 0)
        {
            string ext = Path.GetExtension(opt.CertPath).ToLowerInvariant();
            var c = ext is ".pfx" or ".p12" ? new X509Certificate2(opt.CertPath, opt.CertPass) : X509Certificate2.CreateFromPemFile(opt.CertPath);
            return new X509Certificate2(c.Export(X509ContentType.Pkcs12));
        }
        // a self-signed cert is fine for a TLS listener (clients can opt to trust it / pin its fingerprint)
        string path = Ircuitry.App.ClientCert.EnsureDefault("socket-server");
        return new X509Certificate2(path);
    }
}
