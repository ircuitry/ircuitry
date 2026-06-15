using System.Collections.Concurrent;
using Ircuitry.Graph;
using Ircuitry.Irc;
using Ircuitry.Runtime;

namespace Ircuitry.App;

/// <summary>One bot in the workspace: its workflow, connection, log, persistent variables and live runtime.</summary>
public sealed class Bot
{
    private string _name = "";
    public string Name { get => _name; set { _name = value; if (Runtime != null) Runtime.OwnerName = value; } }
    public NodeGraph Graph = new();
    public IrcSettings Settings = new();
    public readonly ConsoleLog Log = new();
    public readonly ConcurrentDictionary<string, string> State = new();   // bot variables (persisted)
    public readonly BotRuntime Runtime;

    public Bot(string name)
    {
        Runtime = new BotRuntime(Log, State);   // assign before Name so the setter can sync OwnerName
        Name = name;
    }
}
