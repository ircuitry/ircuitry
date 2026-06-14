using System;
using System.Collections.Generic;
using System.Threading;

namespace Ircuitry.App;

/// <summary>
/// Runs the saved workspace's bots with no window - for `ircuitry --run [bot]` as a
/// background service / daemon. Streams each bot's console to stdout; Ctrl+C stops cleanly.
/// </summary>
public static class HeadlessRunner
{
    public static int Run(string[] args)
    {
        // optional: run only the named bot (the token after --run that isn't another flag)
        string? only = null;
        int ri = Array.IndexOf(args, "--run");
        if (ri < 0) ri = Array.IndexOf(args, "--headless");
        if (ri >= 0 && ri + 1 < args.Length && !args[ri + 1].StartsWith("--")) only = args[ri + 1];

        var app = new AppModel();
        var running = new List<Bot>();
        foreach (var bot in app.Bots)
        {
            if (only != null && !bot.Name.Equals(only, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(bot.Settings.Host)) { Console.WriteLine($"· skip '{bot.Name}' (no server set)"); continue; }
            bot.Runtime.Start(bot.Graph, bot.Settings);
            running.Add(bot);
            Console.WriteLine($"▶ {bot.Name} → {bot.Settings.Host}:{bot.Settings.Port}  {string.Join(" ", bot.Settings.ChannelList)}");
        }
        if (running.Count == 0) { Console.WriteLine("ircuitry: no runnable bots (open the app and set a server)."); return 1; }

        Console.WriteLine($"ircuitry headless - {running.Count} bot(s) live. Ctrl+C to stop.");
        using var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };

        var lastRev = new Dictionary<Bot, long>();
        foreach (var b in running) lastRev[b] = 0;
        while (!stop.IsSet)
        {
            foreach (var b in running)
            {
                long rev = b.Log.Revision;
                if (rev <= lastRev[b]) continue;
                foreach (var e in b.Log.Tail((int)Math.Min(rev - lastRev[b], 200)))
                    Console.WriteLine($"[{b.Name}] {e.Time:HH:mm:ss} {e.Level,-7} {e.Text}");
                lastRev[b] = rev;
            }
            stop.Wait(250);
        }

        Console.WriteLine("stopping…");
        foreach (var b in running) { try { b.Runtime.Stop(); } catch { } }
        Thread.Sleep(300);
        return 0;
    }
}
