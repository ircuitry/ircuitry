using Ircuitry.Core;

namespace Ircuitry.Runtime;

/// <summary>The effects a running graph can produce - fulfilled by the live runtime.</summary>
public interface IRuntimeSink
{
    /// <summary>Returns the sink that should fulfil an action node's effects: this sink (the origin server an
    /// event arrived on) when <paramref name="server"/> is blank, or the named server when a node overrides
    /// the route. Sinks that don't model multiple servers (tests) just return themselves.</summary>
    IRuntimeSink ForServer(string server) => this;

    void Privmsg(string target, string text);
    void Notice(string target, string text);
    void React(string target, string msgid, string emoji);
    void PrivmsgTagged(string target, string text, string clientTags);
    void NoticeTagged(string target, string text, string clientTags);
    void Join(string channel);
    void Part(string channel, string reason);
    void Raw(string line);

    /// <summary>Begin an IRCv3 typing indicator (+typing=active) to a target, resent on a cadence
    /// until <see cref="StopTyping"/> or the end of the workflow run.</summary>
    void StartTyping(string target);
    void StopTyping(string target);

    void Log(string message, LogLevel level);

    /// <summary>Signals that a node just executed (for canvas activity feedback).</summary>
    void NodeFired(string nodeId);

    /// <summary>Fired once when a run finishes, with the set of node typeIds that executed without
    /// throwing during it. Drives spec-compliance achievements: a multi-node spec only counts when all
    /// of its nodes succeeded in the same run. Dry runs/tests no-op this so only real runs count.</summary>
    void RunCompleted(System.Collections.Generic.IReadOnlyCollection<string> executedTypes);

    /// <summary>The most-recent messages this bot has seen (newest last) so a SuperAI tool can look one up
    /// and react to it by msgid. Sinks without a buffer (dry runs/tests) return empty.</summary>
    System.Collections.Generic.IReadOnlyList<RecentMsg> RecentMessages(int count) => System.Array.Empty<RecentMsg>();

    // persistent per-bot variable store
    string GetState(string key);
    void SetState(string key, string value);

    /// <summary>Register a human-in-the-loop approval gate (the question has already been posted). The
    /// runtime resumes <paramref name="node"/>'s approved/denied exec output when the (optional) named
    /// approver answers with the approve/deny word in the target, or denies it on timeout. Returns false
    /// when this sink can't host one (dry runs/tests), so the node can fall back.</summary>
    bool AwaitApproval(Ircuitry.Graph.Node node, System.Collections.Generic.Dictionary<string, string> vars,
        string target, string approver, string approveWord, string denyWord, int timeoutSec) => false;
}
