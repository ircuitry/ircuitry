using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Ircuitry.Irc;

public enum IrcState { Disconnected, Connecting, Registering, Connected, Error }

/// <summary>
/// A from-scratch IRCv3 client: TCP/TLS, CAP 302 negotiation, SASL PLAIN, and
/// PING keepalive. The read loop runs on its own thread; callbacks fire there,
/// so handlers must be thread-safe (the runtime logs to a concurrent buffer).
/// </summary>
public sealed class IrcClient
{
    private static readonly string[] Wanted =
    {
        "message-tags", "server-time", "account-tag", "account-notify",
        "away-notify", "extended-join", "multi-prefix", "chghost", "sasl",
        "draft/chathistory", "chathistory",   // request both the draft and ratified cap names
        "draft/bot-cmds", "draft/bot-tools", "batch",   // IRCv3 bot-tools spec
    };

    private TcpClient? _tcp;
    private Stream? _stream;
    private Thread? _thread;
    private readonly object _writeLock = new();
    private volatile bool _quit;

    private IrcSettings _cfg = new();
    private readonly HashSet<string> _avail = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _enabled = new();
    private bool _saslActive, _capEnded;
    private int _nickTries;
    private int _reconnectAttempt;

    // outgoing flood throttle: token bucket, Burst lines instantly then 1 per Interval s
    private const int Burst = 5;
    private const double Interval = 0.7;
    private readonly Queue<string> _outQueue = new();
    private readonly object _outLock = new();
    private readonly AutoResetEvent _outSignal = new(false);
    private Thread? _writer;

    public volatile IrcState State = IrcState.Disconnected;
    public string CurrentNick { get; private set; } = "";
    public IReadOnlyList<string> EnabledCaps => _enabled;
    public bool HasCap(string name) => _enabled.Contains(name, StringComparer.OrdinalIgnoreCase);

    // callbacks (invoked on the read thread)
    public Action<string>? RawIn;
    public Action<string>? RawOut;
    public Action<string, bool>? Status;     // (message, isError)
    public Action<IrcMessage>? Message;
    public Action? Registered;
    public Action<string>? Closed;

    public bool IsLive => State == IrcState.Connected;

    public void Connect(IrcSettings settings)
    {
        if (State is IrcState.Connecting or IrcState.Registering or IrcState.Connected) return;
        _cfg = settings.Clone();
        _quit = false;
        _avail.Clear();
        _enabled = new();
        _saslActive = _capEnded = false;
        _nickTries = 0;
        CurrentNick = _cfg.Nick;
        _reconnectAttempt = 0;
        if (_writer == null)
        {
            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "irc-write" };
            _writer.Start();
        }
        _thread = new Thread(RunLoop) { IsBackground = true, Name = "irc-read" };
        _thread.Start();
    }

    public void Disconnect(string reason = "Ircuitry out.")
    {
        _quit = true;
        try { if (State == IrcState.Connected) SendNow($"QUIT :{reason}"); } catch { /* ignore */ }
        try { _stream?.Close(); } catch { }
        try { _tcp?.Close(); } catch { }
        _outSignal.Set();
    }

    // ---- outgoing helpers ----
    /// <summary>Queue a line for throttled sending (bot actions - flood protection).</summary>
    public void SendRaw(string line)
    {
        lock (_outLock) _outQueue.Enqueue(line);
        _outSignal.Set();
    }

    /// <summary>Send immediately, bypassing the throttle (protocol: PONG, CAP, SASL, registration).</summary>
    private void SendNow(string line)
    {
        RawOut?.Invoke(line);
        var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
        lock (_writeLock)
        {
            var s = _stream;                 // re-read under the lock - RunLoop disposes it under the same lock
            if (s == null) return;
            try { s.Write(bytes, 0, bytes.Length); s.Flush(); }
            catch (Exception ex) { Status?.Invoke("write failed: " + ex.Message, true); }
        }
    }

    // drains the outgoing queue with a token bucket so the bot can't flood off the server.
    // Lives for the whole client (background thread) so it survives stop->restart and reconnects;
    // it parks on the signal when idle and only writes while a stream is live.
    private void WriterLoop()
    {
        double tokens = Burst;
        var last = DateTime.UtcNow;
        while (true)
        {
            _outSignal.WaitOne(200);
            while (true)
            {
                string? line = null;
                lock (_outLock) if (_outQueue.Count > 0 && _stream != null) line = _outQueue.Dequeue();
                if (line == null) break;

                var now = DateTime.UtcNow;
                tokens = Math.Min(Burst, tokens + (now - last).TotalSeconds / Interval);
                last = now;
                if (tokens < 1)
                {
                    int ms = (int)((1 - tokens) * Interval * 1000);
                    if (ms > 0) Thread.Sleep(ms);
                    tokens = 1; last = DateTime.UtcNow;
                }
                tokens -= 1;
                SendNow(line);
            }
        }
    }

    private static string Clean(string s) => s.Replace("\r", " ").Replace("\n", " ");
    public void Privmsg(string target, string text) => SendRaw($"PRIVMSG {target} :{Trim(Clean(text))}");
    public void Notice(string target, string text) => SendRaw($"NOTICE {target} :{Trim(Clean(text))}");
    public void Join(string channel) => SendRaw($"JOIN {Clean(channel)}");
    public void Part(string channel, string reason) => SendRaw($"PART {Clean(channel)} :{Clean(reason)}");
    public void Nick(string nick) => SendRaw($"NICK {Clean(nick)}");

    private static string EscapeTag(string v) =>
        v.Replace("\\", "\\\\").Replace(";", "\\:").Replace(" ", "\\s").Replace("\r", "\\r").Replace("\n", "\\n");

    /// <summary>React to a message (IRCv3 +draft/react via TAGMSG), optionally threaded to its msgid.</summary>
    public void React(string target, string msgid, string emoji)
    {
        var tags = "+draft/react=" + EscapeTag(Clean(emoji));
        if (msgid.Length > 0) tags += ";+draft/reply=" + msgid;
        SendRaw("@" + tags + " TAGMSG " + Clean(target));
    }

    /// <summary>Reply threaded to a message (IRCv3 +draft/reply client tag).</summary>
    public void ReplyThreaded(string target, string msgid, string text)
    {
        string pre = msgid.Length > 0 ? "@+draft/reply=" + msgid + " " : "";
        SendRaw(pre + $"PRIVMSG {Clean(target)} :{Trim(Clean(text))}");
    }

    // ---- generic client-tagged sends (bot-cmds / bot-tools) ----
    private static string Pre(string clientTags) => string.IsNullOrEmpty(clientTags) ? "" : "@" + clientTags + " ";
    public void PrivmsgTagged(string target, string text, string clientTags) => SendRaw(Pre(clientTags) + $"PRIVMSG {Clean(target)} :{Trim(Clean(text))}");
    public void NoticeTagged(string target, string text, string clientTags) => SendRaw(Pre(clientTags) + $"NOTICE {Clean(target)} :{Trim(Clean(text))}");
    public void TagMsg(string target, string clientTags) => SendRaw(Pre(clientTags) + "TAGMSG " + Clean(target));

    // Truncate to ~400 *bytes* of UTF-8 (IRC limits are byte-based), without splitting a char.
    private static string Trim(string s)
    {
        if (Encoding.UTF8.GetByteCount(s) <= 400) return s;
        int chars = s.Length;
        while (chars > 0 && Encoding.UTF8.GetByteCount(s[..chars]) > 400) chars -= Math.Max(1, (Encoding.UTF8.GetByteCount(s[..chars]) - 400) / 4);
        while (chars > 0 && Encoding.UTF8.GetByteCount(s[..chars]) > 400) chars--;
        return s[..chars];
    }

    // ===================================================================
    private void RunLoop()
    {
        while (!_quit)
        {
            try { ConnectOnce(); }
            catch (Exception ex)
            {
                if (!_quit) { State = IrcState.Error; Status?.Invoke("connection error: " + ex.Message, true); }
            }
            finally
            {
                lock (_writeLock)        // never dispose the stream mid-write on the writer thread
                {
                    try { _stream?.Dispose(); } catch { }
                    try { _tcp?.Close(); } catch { }
                    _stream = null; _tcp = null;
                }
            }

            if (_quit) break;
            if (!_cfg.AutoReconnect) break;

            // unexpected drop -> exponential backoff, then retry
            _reconnectAttempt++;
            int delay = Math.Min(30, 1 << Math.Min(_reconnectAttempt, 5));   // 2,4,8,16,30,30…
            State = IrcState.Connecting;
            Status?.Invoke($"reconnecting in {delay}s (attempt {_reconnectAttempt})…", false);
            for (int i = 0; i < delay * 10 && !_quit; i++) Thread.Sleep(100);
            if (_quit) break;

            // reset per-connection negotiation state
            _avail.Clear(); _enabled = new(); _saslActive = _capEnded = false; _nickTries = 0;
            CurrentNick = _cfg.Nick;
        }

        State = IrcState.Disconnected;
        Closed?.Invoke(_quit ? "disconnected." : "connection closed.");
    }

    private void ConnectOnce()
    {
        lock (_outLock) _outQueue.Clear();      // drop stale outgoing from a previous connection
        State = IrcState.Connecting;
        Status?.Invoke($"connecting to {_cfg.Host}:{_cfg.Port} {(_cfg.UseTls ? "(TLS)" : "")}…", false);

        _tcp = new TcpClient();
        if (!_tcp.ConnectAsync(_cfg.Host, _cfg.Port).Wait(12000))
            throw new TimeoutException("connection timed out");

        Stream raw = _tcp.GetStream();
        if (_cfg.UseTls)
        {
            var ssl = new SslStream(raw, false, (_, _, _, errors) =>
                _cfg.AcceptInvalidCerts || errors == SslPolicyErrors.None);
            ssl.AuthenticateAsClient(_cfg.Host);
            raw = ssl;
            if (_cfg.AcceptInvalidCerts) Status?.Invoke("TLS established (certificate not verified)", false);
        }
        _stream = raw;

        State = IrcState.Registering;
        Status?.Invoke("registering…", false);
        Register();

        using var reader = new StreamReader(_stream, new UTF8Encoding(false), false, 8192);
        string? line;
        while (!_quit && (line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            RawIn?.Invoke(line);
            try { Handle(IrcParser.Parse(line)); }
            catch (Exception ex) { Status?.Invoke("handler error: " + ex.Message, true); }
        }
    }

    private void Register()
    {
        SendNow("CAP LS 302");
        if (_cfg.ServerPass.Length > 0) SendNow($"PASS {_cfg.ServerPass}");
        // never send an empty NICK/USER/realname - servers reject "USER  0 * :" with 461. Fall back to
        // sane values: nick -> "ircuitry-bot", ident -> the nick (first word, no spaces), realname -> the nick.
        string nick = Clean(_cfg.Nick).Trim();
        if (nick.Length == 0) nick = "ircuitry-bot";
        string ident = Clean(_cfg.User).Trim();
        int sp = ident.IndexOf(' '); if (sp >= 0) ident = ident[..sp];   // ident is a single token, no spaces
        if (ident.Length == 0) ident = nick;
        string real = Clean(_cfg.RealName).Trim();
        if (real.Length == 0) real = nick;
        SendNow($"NICK {nick}");
        SendNow($"USER {ident} 0 * :{real}");
    }

    private void Handle(IrcMessage m)
    {
        // protocol plumbing first
        if (m.Is("PING")) { SendNow("PONG :" + m.Trailing); }
        else if (m.Is("CAP")) HandleCap(m);
        else if (m.Is("AUTHENTICATE")) HandleAuthenticate(m);
        else if (m.IsNumeric(out int num)) HandleNumeric(num, m);

        // surface everything to the runtime (it filters PRIVMSG/JOIN/etc.)
        Message?.Invoke(m);
    }

    private void HandleCap(IrcMessage m)
    {
        string sub = m.P(1);
        if (sub.Equals("LS", StringComparison.OrdinalIgnoreCase))
        {
            bool more = m.Count >= 4 && m.P(2) == "*";
            foreach (var tok in m.Trailing.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                _avail.Add(tok.Split('=')[0]);
            if (!more)
            {
                var req = Wanted.Where(_avail.Contains).ToList();
                if (req.Count > 0) { SendNow("CAP REQ :" + string.Join(' ', req)); }
                else EndCap();
            }
        }
        else if (sub.Equals("ACK", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var c in m.Trailing.Split(' ', StringSplitOptions.RemoveEmptyEntries)) _enabled.Add(c);
            Status?.Invoke("caps: " + string.Join(' ', _enabled), false);
            if (_enabled.Contains("sasl", StringComparer.OrdinalIgnoreCase) && _cfg.UseSasl)
            {
                _saslActive = true;
                SendNow("AUTHENTICATE PLAIN");
            }
            else EndCap();
        }
        else if (sub.Equals("NAK", StringComparison.OrdinalIgnoreCase))
        {
            Status?.Invoke("server refused caps: " + m.Trailing, false);
            EndCap();
        }
    }

    private void HandleAuthenticate(IrcMessage m)
    {
        if (m.P(0) != "+") return;
        string authcid = _cfg.SaslUser.Length > 0 ? _cfg.SaslUser : _cfg.Nick;
        var raw = Encoding.UTF8.GetBytes($"\0{authcid}\0{_cfg.SaslPass}");
        string b64 = Convert.ToBase64String(raw);

        // IRCv3 SASL: split into ≤400-byte AUTHENTICATE chunks; if the payload is
        // an exact multiple of 400 (incl. a final full chunk), send a trailing "+".
        for (int i = 0; i < b64.Length; i += 400)
            SendNow("AUTHENTICATE " + b64.Substring(i, Math.Min(400, b64.Length - i)));
        if (b64.Length % 400 == 0) SendNow("AUTHENTICATE +");
    }

    private void HandleNumeric(int num, IrcMessage m)
    {
        switch (num)
        {
            case 1: // RPL_WELCOME
                State = IrcState.Connected;
                _reconnectAttempt = 0;                 // healthy connection - reset backoff
                CurrentNick = m.P(0);
                Status?.Invoke("registered as " + CurrentNick, false);
                Registered?.Invoke();
                foreach (var ch in _cfg.ChannelList) Join(ch);
                break;

            case 433: // ERR_NICKNAMEINUSE - try suffixed nicks until registered
                if (State != IrcState.Connected && _nickTries < 8)
                {
                    _nickTries++;
                    string n = _cfg.Nick + new string('_', _nickTries);
                    CurrentNick = n;
                    SendNow($"NICK {n}");
                }
                break;

            case 903: // SASL success
                if (_saslActive) { _saslActive = false; Status?.Invoke("SASL authenticated.", false); EndCap(); }
                break;
            case 902: case 904: case 905: case 906: case 907: case 908:
                if (_saslActive) { _saslActive = false; Status?.Invoke("SASL failed (" + num + "): " + m.Trailing, true); EndCap(); }
                break;
        }
    }

    private void EndCap()
    {
        if (_capEnded) return;
        _capEnded = true;
        SendNow("CAP END");
    }
}
