using System.Collections.Concurrent;
using System.Collections.Generic;
using Ircuitry.Graph;
using Ircuitry.Irc;
using Ircuitry.Runtime;

namespace Ircuitry.App;

/// <summary>One bot in the workspace: its workflow, connections, log, persistent variables and live runtime.
/// A bot can hold several servers; the graph reacts to events from all of them and routes outgoing actions
/// back to the server an event arrived on (with an optional per-node override).</summary>
public sealed class Bot
{
    private string _name = "";
    public string Name { get => _name; set { _name = value; if (Runtime != null) Runtime.OwnerName = value; } }
    public NodeGraph Graph = new();

    /// <summary>The bot's server connections (always at least one).</summary>
    public readonly List<IrcSettings> Servers = new() { new IrcSettings() };
    /// <summary>Which server the connection inspector is currently editing.</summary>
    public int SelectedServer;

    /// <summary>The server currently selected for editing - lets the existing single-server UI/code keep
    /// working unchanged while a bot can hold many.</summary>
    public IrcSettings Settings
    {
        get { if (SelectedServer < 0 || SelectedServer >= Servers.Count) SelectedServer = 0; return Servers[SelectedServer]; }
        set { if (Servers.Count == 0) Servers.Add(value); else Servers[SelectedServer < Servers.Count ? SelectedServer : 0] = value; }
    }

    public readonly ConsoleLog Log = new();
    public readonly ConcurrentDictionary<string, string> State = new();   // bot variables (persisted)
    public readonly BotRuntime Runtime;

    // ---- remote link: when set, this tab edits a bot living on a remote ircuitry --server. The local runtime
    // is idle; edits push to the server, and run/glow/console come from the session. (Local bots leave these null.)
    public Ircuitry.App.Server.ControlClient? Remote;
    public string RemoteName = "";
    public bool IsRemote => Remote != null;

    public Bot(string name)
    {
        Runtime = new BotRuntime(Log, State);   // assign before Name so the setter can sync OwnerName
        Name = name;
    }
}
