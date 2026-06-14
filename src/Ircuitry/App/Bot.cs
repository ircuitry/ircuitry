using System.Collections.Concurrent;
using Ircuitry.Graph;
using Ircuitry.Irc;
using Ircuitry.Runtime;

namespace Ircuitry.App;

/// <summary>One bot in the workspace: its workflow, connection, log, persistent variables and live runtime.</summary>
public sealed class Bot
{
    public string Name;
    public NodeGraph Graph = new();
    public IrcSettings Settings = new();
    public readonly ConsoleLog Log = new();
    public readonly ConcurrentDictionary<string, string> State = new();   // bot variables (persisted)
    public readonly BotRuntime Runtime;

    public Bot(string name)
    {
        Name = name;
        Runtime = new BotRuntime(Log, State);
    }
}
