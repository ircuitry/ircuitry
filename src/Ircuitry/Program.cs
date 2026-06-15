using System;
using System.IO;
using System.Threading;
using Ircuitry.App;

namespace Ircuitry;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (Array.IndexOf(args, "--selftest") >= 0)
        {
            Environment.Exit(Ircuitry.Runtime.SelfTest.RunAll());
            return;
        }
        if (Array.IndexOf(args, "--run") >= 0 || Array.IndexOf(args, "--headless") >= 0)
        {
            Environment.Exit(HeadlessRunner.Run(args));
            return;
        }
        if (Array.IndexOf(args, "--mcp") >= 0)
        {
            Environment.Exit(Ircuitry.App.Mcp.McpServer.RunStdio(args));
            return;
        }
        if (Array.IndexOf(args, "--register-scheme") >= 0)
        {
            DeepLink.Register();
            Console.WriteLine("registered ircuitry:// handler");
            return;
        }
        if (Array.IndexOf(args, "--updatecheck") >= 0)
        {
            var (s, b) = Ircuitry.Net.Http.Send("GET", "https://api.github.com/repos/ircuitry/ircuitry/releases/latest", System.Array.Empty<(string, string)>(), null);
            Console.WriteLine($"status={s} len={b.Length}");
            Console.WriteLine(b.Length > 240 ? b.Substring(0, 240) : b);
            return;
        }
        {
            int wi = Array.IndexOf(args, "--validate-workflow");
            if (wi >= 0 && wi + 1 < args.Length)
            {
                Environment.Exit(WorkflowValidator.Run(args[wi + 1]));
                return;
            }
            int ni = Array.IndexOf(args, "--validate-node");
            if (ni >= 0 && ni + 1 < args.Length)
            {
                Environment.Exit(NodeValidator.Run(args[ni + 1]));
                return;
            }
        }
        if (Array.IndexOf(args, "--schema") >= 0)
        {
            foreach (var d in Ircuitry.Graph.NodeCatalog.All)
            {
                var ins = new System.Collections.Generic.List<string>();
                for (int k = 0; k < d.Inputs.Length; k++) ins.Add($"{k}:{(d.Inputs[k].Name.Length > 0 ? d.Inputs[k].Name : "_")}({d.Inputs[k].Kind})");
                var outs = new System.Collections.Generic.List<string>();
                for (int k = 0; k < d.Outputs.Length; k++) outs.Add($"{k}:{(d.Outputs[k].Name.Length > 0 ? d.Outputs[k].Name : "_")}({d.Outputs[k].Kind})");
                var ps = new System.Collections.Generic.List<string>();
                foreach (var p in d.Params)
                {
                    string ch = p.Choices is { Length: > 0 } ? "[" + string.Join("|", p.Choices) + "]" : "";
                    ps.Add($"{p.Key}={p.Type}{ch}");
                }
                Console.WriteLine($"{d.TypeId} | cat={d.Category}{(d.IsTrigger ? " TRIGGER" : "")} | in: {string.Join(" ", ins)} | out: {string.Join(" ", outs)} | params: {string.Join(" ", ps)}");
            }
            Environment.Exit(0);
            return;
        }
        if (Array.IndexOf(args, "--listnodes") >= 0)
        {
            foreach (var d in Ircuitry.Graph.NodeCatalog.Custom)
                Console.WriteLine($"{d.TypeId}\t{d.Title}\t{d.Category}\t{d.Inputs.Length}in/{d.Outputs.Length}out");
            Environment.Exit(0);
            return;
        }
        // screenshot/demo launches are exempt from single-instance so capture always works
        bool ephemeral = Array.IndexOf(args, "--shot") >= 0 || Array.IndexOf(args, "--demo") >= 0;
        if (ephemeral)
        {
            using var shot = new IrcuitryGame(args);
            shot.Run();
            return;
        }

        // custom-scheme deep link (ircuitry://install-node?url=...) and single instance:
        // the first GUI holds the lock and serves an inbox; later launches forward the link and exit.
        string? deepLink = Array.Find(args, DeepLink.Is);
        string dataDir = AppModel.WorkspaceDir;
        try { Directory.CreateDirectory(dataDir); } catch { /* best effort */ }
        // an inbox DIRECTORY (one file per link): concurrent clicks each write a unique file, so nothing is
        // lost to a shared-file write lock the way appending to one file dropped most rapid clicks.
        string inboxDir = Path.Combine(dataDir, ".deeplink-inbox.d");

        bool relaunch = Array.IndexOf(args, "--relaunch") >= 0;   // a self-update restart waits for the old instance to release
        using var single = new Mutex(false, "ircuitry-singleton-" + Environment.UserName);
        bool primary;
        try { primary = single.WaitOne(relaunch ? 12000 : 0); }
        catch (AbandonedMutexException) { primary = true; }   // previous owner crashed; we take over
        if (!primary)
        {
            if (deepLink != null)
                try
                {
                    Directory.CreateDirectory(inboxDir);
                    // write to .tmp then rename to .link so the watcher only ever sees a COMPLETE file
                    string id = Guid.NewGuid().ToString("N");
                    string tmp = Path.Combine(inboxDir, id + ".tmp");
                    File.WriteAllText(tmp, deepLink);
                    File.Move(tmp, Path.Combine(inboxDir, id + ".link"), true);
                }
                catch { /* ignore */ }
            return;   // another GUI is running; we forwarded (or had nothing to do)
        }

        try
        {
            DeepLink.Register();   // register ircuitry:// with the OS (best effort)
            using var game = new IrcuitryGame(args, inboxDir, deepLink);
            game.Run();
        }
        finally { try { single.ReleaseMutex(); } catch { /* not held */ } }
    }
}
