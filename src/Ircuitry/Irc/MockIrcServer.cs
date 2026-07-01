using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Ircuitry.Irc;

/// <summary>
/// A tiny in-process IRC server for the integration self-test and the offline
/// demo. Speaks just enough protocol to register a client, then injects a
/// scripted conversation and records what the bot sends back.
/// </summary>
public sealed class MockIrcServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Thread _thread;
    private volatile bool _stop;
    private TcpClient? _accepted;
    private readonly object _lock = new();
    private readonly object _writeLock = new();
    private readonly List<string> _outgoing = new();
    private readonly IReadOnlyList<(int delayMs, string line)> _script;

    public int Port { get; }

    // ---- SASL self-test knobs: the credential the mock will accept, and what the client actually negotiated ----
    public string ExpectSaslUser = "";
    public string ExpectSaslPass = "";
    public volatile string SaslMechUsed = "";   // the mechanism the client chose (PLAIN / EXTERNAL / SCRAM-SHA-256)
    public volatile bool SaslOk;                 // set true once the mock has accepted authentication
    public bool OfferLabeledResponse = true;     // advertise labeled-response in CAP LS (off to test the fallback path)
    public bool EchoBotMessages;                 // echo the bot's own channel PRIVMSGs back (demos/captures show what it says)
    public readonly Dictionary<string, string> Metadata = new();   // key -> value the mock answers METADATA GET with
    public volatile bool SawLabeledRequest;      // set true if a METADATA GET arrived carrying a label tag

    public MockIrcServer(IReadOnlyList<(int, string)>? script = null)
    {
        _script = script ?? new[] { (60, ":alice!a@host PRIVMSG #ircuitry-test :!ping") };
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _thread = new Thread(Run) { IsBackground = true, Name = "mock-irc" };
        _thread.Start();
    }

    public string[] Sent() { lock (_lock) return _outgoing.ToArray(); }

    private void Run()
    {
        // accept connections in a loop so the bot can disconnect and reconnect (stop->restart)
        while (!_stop)
        {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch { break; }   // listener stopped
            _accepted = client;
            HandleClient(client);
        }
    }

    private void HandleClient(TcpClient client)
    {
        try
        {
            using var _ = client;
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false));

            void Send(string line)
            {
                var b = Encoding.UTF8.GetBytes(line + "\r\n");
                lock (_writeLock) { stream.Write(b, 0, b.Length); stream.Flush(); }
            }

            string nick = "user";
            bool injected = false;
            string saslMech = "", scBare = "", scServerFirst = "";
            int saslStep = 0, metaSeq = 0;
            byte[] scSalt = Array.Empty<byte>();
            string? line;
            while (!_stop && (line = reader.ReadLine()) != null)
            {
                // a client line may be tag-prefixed (@tags COMMAND ...) - detect the command after the tags
                string body = line;
                if (body.StartsWith('@')) { int sp = body.IndexOf(' '); body = sp >= 0 ? body[(sp + 1)..] : ""; }

                if (body.StartsWith("NICK ", StringComparison.Ordinal)) nick = body[5..].Trim();
                else if (body.StartsWith("CAP LS", StringComparison.Ordinal)) Send(":mock CAP * LS :message-tags server-time multi-prefix account-tag draft/bot-cmds draft/bot-tools batch echo-message setname draft/message-redaction draft/metadata-2 sasl=PLAIN,EXTERNAL,SCRAM-SHA-256" + (OfferLabeledResponse ? " labeled-response" : ""));
                else if (body.StartsWith("METADATA ", StringComparison.Ordinal) && body.Contains(" GET ", StringComparison.Ordinal))
                {
                    var parts = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);   // METADATA <target> GET <key>
                    string mtarget = parts.Length > 1 ? parts[1] : "*";
                    string mkey = parts.Length > 3 ? parts[3] : "";
                    string mval = Metadata.TryGetValue(mkey, out var mv) ? mv : "";
                    string label = TagOf(line, "label");
                    if (label.Length > 0) SawLabeledRequest = true;
                    if (label.Length > 0)   // labeled-response: wrap the answer in a labeled batch
                    {
                        string bref = "MB" + (++metaSeq);
                        Send($"@label={label} BATCH +{bref} labeled-response");
                        if (mval.Length > 0) Send($"@batch={bref} :mock 761 {nick} {mtarget} {mkey} * :{mval}");
                        Send($"BATCH -{bref}");
                    }
                    else                    // fallback: bare metadata numerics
                    {
                        if (mval.Length > 0) Send($":mock 761 {nick} {mtarget} {mkey} * :{mval}");
                        Send($":mock 762 {nick} :end of metadata");
                    }
                }
                else if (body.StartsWith("AUTHENTICATE", StringComparison.Ordinal))
                {
                    string arg = body.Length > 13 ? body[13..].Trim() : "";
                    if (arg is "PLAIN" or "EXTERNAL" or "SCRAM-SHA-256") { saslMech = arg; SaslMechUsed = arg; saslStep = 0; Send("AUTHENTICATE +"); }
                    else if (arg == "*") Send($":mock 904 {nick} :SASL aborted");
                    else if (saslMech is "PLAIN" or "EXTERNAL") { SaslOk = true; Send($":mock 900 {nick} {nick}!u@h {nick} :You are now logged in"); Send($":mock 903 {nick} :SASL authentication successful"); }
                    else if (saslMech == "SCRAM-SHA-256")
                    {
                        if (saslStep == 0)
                        {
                            string cf = B64Decode(arg);                      // "n,,n=user,r=cnonce"
                            scBare = cf.StartsWith("n,,") ? cf[3..] : cf;
                            string cnonce = ScramAttr(scBare, "r");
                            scSalt = RandomNumberGenerator.GetBytes(16);
                            string snonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12));
                            scServerFirst = $"r={cnonce}{snonce},s={Convert.ToBase64String(scSalt)},i=4096";
                            Send("AUTHENTICATE " + B64(scServerFirst));
                            saslStep = 1;
                        }
                        else if (saslStep == 1)
                        {
                            string cfin = B64Decode(arg);                    // "c=biws,r=...,p=proof"
                            int pIdx = cfin.IndexOf(",p=", StringComparison.Ordinal);
                            string noProof = pIdx >= 0 ? cfin[..pIdx] : cfin;
                            string proof = ScramAttr(cfin, "p");
                            byte[] salted = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(ExpectSaslPass), scSalt, 4096, HashAlgorithmName.SHA256, 32);
                            byte[] clientKey = ScramHmac(salted, "Client Key");
                            byte[] storedKey = SHA256.HashData(clientKey);
                            string authMsg = scBare + "," + scServerFirst + "," + noProof;
                            byte[] clientSig = ScramHmac(storedKey, authMsg);
                            string expProof = Convert.ToBase64String(ScramXor(clientKey, clientSig));
                            if (expProof == proof)
                            {
                                byte[] serverKey = ScramHmac(salted, "Server Key");
                                byte[] serverSig = ScramHmac(serverKey, authMsg);
                                Send("AUTHENTICATE " + B64("v=" + Convert.ToBase64String(serverSig)));
                                saslStep = 2;
                            }
                            else Send($":mock 904 {nick} :SASL proof mismatch");
                        }
                        else { SaslOk = true; Send($":mock 900 {nick} {nick}!u@h {nick} :You are now logged in"); Send($":mock 903 {nick} :SASL authentication successful"); }
                    }
                }
                else if (body.StartsWith("CAP REQ", StringComparison.Ordinal))
                {
                    int i = body.IndexOf(':');
                    Send($":mock CAP * ACK :{(i >= 0 ? body[(i + 1)..] : "")}");
                }
                else if (body.StartsWith("CAP END", StringComparison.Ordinal))
                {
                    Send($":mock 001 {nick} :Welcome to MockNet, {nick}");
                    Send($":mock 005 {nick} CHANTYPES=# PREFIX=(ov)@+ NETWORK=MockNet :are supported");
                    Send($":mock 376 {nick} :End of /MOTD");
                }
                else if (body.StartsWith("JOIN", StringComparison.Ordinal))
                {
                    lock (_lock) _outgoing.Add(line);   // record so tests can assert auto-join (incl. after reconnect)
                    Send($":{nick}!ircuitry@mock JOIN {body[5..].Trim()}");
                    if (!injected) { injected = true; StartInjector(Send); }
                }
                else if (body.StartsWith("PRIVMSG", StringComparison.Ordinal) || body.StartsWith("NOTICE", StringComparison.Ordinal) || body.StartsWith("TAGMSG", StringComparison.Ordinal))
                {
                    lock (_lock) _outgoing.Add(line);   // record the FULL line incl. client tags (+reply, +draft/bot-tools, ...)
                    // echo the bot's own PRIVMSGs back into the channel so a demo/capture shows what it says
                    if (EchoBotMessages && body.StartsWith("PRIVMSG", StringComparison.Ordinal))
                    {
                        int sp = body.IndexOf(' '); int co = body.IndexOf(" :", StringComparison.Ordinal);
                        if (sp > 0 && co > sp) { var tgt = body.Substring(sp + 1, co - sp - 1).Trim(); var txt = body[(co + 2)..]; if (tgt.StartsWith("#")) Send($":{nick}!ircuitry@mock PRIVMSG {tgt} :{txt}"); }
                    }
                }
                else if (body.StartsWith("PONG", StringComparison.Ordinal))
                {
                    lock (_lock) _outgoing.Add(line);   // record keepalive PONGs (tests assert the read thread stays responsive)
                }
                else if (body.StartsWith("QUIT", StringComparison.Ordinal)) break;
            }
        }
        catch { /* test server - swallow */ }
    }

    private void StartInjector(Action<string> send)
    {
        var t = new Thread(() =>
        {
            foreach (var (delay, line) in _script)
            {
                if (_stop) return;
                Thread.Sleep(delay);
                try { send(line); } catch { return; }
            }
        })
        { IsBackground = true, Name = "mock-inject" };
        t.Start();
    }

    // pull one message-tag value out of a raw wire line ("@a=1;label=X CMD ..." -> "X")
    private static string TagOf(string line, string key)
    {
        if (line.Length == 0 || line[0] != '@') return "";
        int sp = line.IndexOf(' ');
        string tags = sp >= 0 ? line[1..sp] : line[1..];
        foreach (var t in tags.Split(';'))
        {
            int eq = t.IndexOf('=');
            if (eq > 0 && t[..eq] == key) return t[(eq + 1)..];
        }
        return "";
    }

    // ---- tiny SCRAM-SHA-256 server helpers (self-test only) ----
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
    private static string B64Decode(string s) => s.Length == 0 || s == "+" ? "" : Encoding.UTF8.GetString(Convert.FromBase64String(s));
    private static string ScramAttr(string msg, string key)
    {
        foreach (var part in msg.Split(','))
            if (part.StartsWith(key + "=", StringComparison.Ordinal)) return part[(key.Length + 1)..];
        return "";
    }
    private static byte[] ScramHmac(byte[] key, string msg) { using var h = new HMACSHA256(key); return h.ComputeHash(Encoding.UTF8.GetBytes(msg)); }
    private static byte[] ScramXor(byte[] a, byte[] b) { var r = new byte[a.Length]; for (int i = 0; i < a.Length; i++) r[i] = (byte)(a[i] ^ b[i]); return r; }

    public void Dispose()
    {
        _stop = true;
        try { _listener.Stop(); } catch { }
        try { _accepted?.Close(); } catch { }   // unblock the read thread's ReadLine
    }
}
