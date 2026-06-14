using Ircuitry.Core;

namespace Ircuitry.Runtime;

/// <summary>The effects a running graph can produce - fulfilled by the live runtime.</summary>
public interface IRuntimeSink
{
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

    // persistent per-bot variable store
    string GetState(string key);
    void SetState(string key, string value);
}
