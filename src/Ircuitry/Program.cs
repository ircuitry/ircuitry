using System;
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
        if (Array.IndexOf(args, "--listnodes") >= 0)
        {
            foreach (var d in Ircuitry.Graph.NodeCatalog.Custom)
                Console.WriteLine($"{d.TypeId}\t{d.Title}\t{d.Category}\t{d.Inputs.Length}in/{d.Outputs.Length}out");
            Environment.Exit(0);
            return;
        }
        using var game = new IrcuitryGame(args);
        game.Run();
    }
}
