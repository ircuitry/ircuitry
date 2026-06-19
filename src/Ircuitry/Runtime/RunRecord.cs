using System;
using System.Collections.Generic;

namespace Ircuitry.Runtime;

/// <summary>One node's execution within a run: the data it read and produced.</summary>
public sealed class NodeTrace
{
    public string NodeId = "";
    public string Title = "";
    public string Icon = "";
    public readonly List<(string pin, string value)> Inputs = new();
    public readonly List<(string pin, string value)> Outputs = new();
    public readonly List<string> Pulsed = new();   // exec outputs that fired
}

/// <summary>A node that threw during a run - attributed for the error tray (#15).</summary>
public sealed class BotError
{
    public string NodeId = "", Title = "", Message = "";
    public DateTime Time;
    public int Count = 1;
}

/// <summary>A single trigger firing - the whole workflow run, with every node's I/O.</summary>
public sealed class RunRecord
{
    public DateTime Time;
    public string Trigger = "";
    public string Icon = "";
    public string Summary = "";              // short context (e.g. "nick: message")
    public string Msgid = "";                // the triggering message's id, when there was one (for linking)
    public readonly List<NodeTrace> Nodes = new();
    public int Actions;                      // IRC sends produced by this run
    public bool Fired;                       // the trigger actually pulsed its flow

    // record & replay (#17): the trigger node's type and the exact event variables it fired with, so a
    // recorded real event can be re-injected through the current graph offline (a dry run).
    public string TriggerType = "";
    public Dictionary<string, string>? Envelope;
}
