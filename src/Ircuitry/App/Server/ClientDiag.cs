using System;
using System.Threading;

namespace Ircuitry.App.Server;

/// <summary>Diagnostic: <c>ircuitry --connect host:port --token T</c> connects to a control server with the
/// client library, prints the workspace snapshot, and streams live events for a few seconds. Proves the
/// desktop's client path against a real server (and is a handy CLI to poke a remote instance).</summary>
public static class ClientDiag
{
    public static int Run(string[] args)
    {
        string url = Arg(args, "--connect") ?? "127.0.0.1:48700";
        string token = Arg(args, "--token") ?? "";
        int seconds = int.TryParse(Arg(args, "--for"), out var s) ? s : 6;

        var c = new ControlClient();
        c.OnNode = (bot, id) => Console.WriteLine($"  node-fired  {bot}/{id}");
        c.Connect(url, token);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (c.State is ControlClient.Conn.Connecting && sw.Elapsed.TotalSeconds < 8) { c.Pump(); Thread.Sleep(50); }
        if (c.State != ControlClient.Conn.Connected) { Console.Error.WriteLine($"connect failed: {c.State} {c.Error}"); return 1; }

        Console.WriteLine($"connected to {c.ServerName} as {c.User}");
        c.Call("call", new { tool = "list_node_types", args = new { } }, res =>
            Console.WriteLine($"  node types: {(res.TryGetProperty("result", out var rt) && rt.ValueKind == System.Text.Json.JsonValueKind.Array ? rt.GetArrayLength() : 0)}"));

        bool printedBots = false;
        int logCursor = 0;
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end && c.State == ControlClient.Conn.Connected)
        {
            c.Pump();
            var bots = c.Bots;
            if (bots.Count > 0 && !printedBots)
            {
                foreach (var b in bots) Console.WriteLine($"  bot {b.Name}: {(b.Running ? "running" : "stopped")} ({b.Stat}) {b.Nodes} nodes / {b.Wires} wires");
                printedBots = true;
            }
            var log = c.RecentLog(100);
            for (int i = logCursor; i < log.Length; i++) Console.WriteLine("  log " + log[i]);
            logCursor = log.Length;
            Thread.Sleep(80);
        }
        c.Disconnect();
        Console.WriteLine("done");
        return 0;
    }

    private static string? Arg(string[] args, string name)
    { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null; }
}
