using System;

namespace Ircuitry.App;

/// <summary>One captured point in a bot's rollback timeline: the serialized graph plus when it was taken and
/// a few stats for the UI. Kept in memory for the session (the workspace .ircuitry already covers durable
/// snapshots), deduped by behaviour signature so only real changes add a version.</summary>
public sealed class GraphVersion
{
    public DateTime Time;
    public string Data = "";   // GraphSerializer.Save(graph)
    public int Nodes, Wires;
    public long Sig;
    public string Note = "";
}
