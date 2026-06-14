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
        string inboxPath = Path.Combine(dataDir, ".deeplink-inbox");

        using var single = new Mutex(false, "ircuitry-singleton-" + Environment.UserName);
        bool primary;
        try { primary = single.WaitOne(0); }
        catch (AbandonedMutexException) { primary = true; }   // previous owner crashed; we take over
        if (!primary)
        {
            if (deepLink != null) { try { File.AppendAllText(inboxPath, deepLink + "\n"); } catch { /* ignore */ } }
            return;   // another GUI is running; we forwarded (or had nothing to do)
        }

        try
        {
            DeepLink.Register();   // register ircuitry:// with the OS (best effort)
            using var game = new IrcuitryGame(args, inboxPath, deepLink);
            game.Run();
        }
        finally { try { single.ReleaseMutex(); } catch { /* not held */ } }
    }
}
