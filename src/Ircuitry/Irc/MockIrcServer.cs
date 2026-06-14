using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
        // accept connections in a loop so the bot can disconnect and reconnect (stop→restart)
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
            string? line;
            while (!_stop && (line = reader.ReadLine()) != null)
            {
                // a client line may be tag-prefixed (@tags COMMAND …) - detect the command after the tags
                string body = line;
                if (body.StartsWith('@')) { int sp = body.IndexOf(' '); body = sp >= 0 ? body[(sp + 1)..] : ""; }

                if (body.StartsWith("NICK ", StringComparison.Ordinal)) nick = body[5..].Trim();
                else if (body.StartsWith("CAP LS", StringComparison.Ordinal)) Send(":mock CAP * LS :message-tags server-time multi-prefix account-tag draft/bot-cmds draft/bot-tools batch");
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
                    lock (_lock) _outgoing.Add(line);   // record the FULL line incl. client tags (+reply, +draft/bot-tools, …)
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

    public void Dispose()
    {
        _stop = true;
        try { _listener.Stop(); } catch { }
        try { _accepted?.Close(); } catch { }   // unblock the read thread's ReadLine
    }
}
