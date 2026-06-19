using Ircuitry.Core;

namespace Ircuitry.Graph;

/// <summary>
/// The surface a node's behaviour uses at runtime: read params and data inputs,
/// set data outputs, pulse exec outputs, touch context variables, and act on IRC.
/// Implemented by the runtime executor.
/// </summary>
public interface INodeContext
{
    Node Node { get; }

    // params & data
    string Param(string key);
    bool ParamBool(string key);
    int ParamInt(string key, int fallback = 0);
    string In(int inputIndex);                 // resolved upstream value, or ""
    string InOr(int inputIndex, string fallback);
    void SetOut(int outputIndex, string value);

    // control flow
    void Pulse(int execOutputIndex);

    /// <summary>Synchronously run the subgraph wired to an exec output now (used by loops).</summary>
    void Run(int execOutputIndex);

    /// <summary>All source nodes wired into one of this node's input pins (for multi-connect pins like AI tools).</summary>
    System.Collections.Generic.IReadOnlyList<Node> SourcesInto(int inputIndex);

    /// <summary>Synchronously run another node's behaviour within the same execution (used to invoke AI tools).</summary>
    void RunNode(Node node);

    /// <summary>Run a saved subgraph as a reusable unit: seed it with named inputs and read back the named
    /// outputs its flow.return nodes wrote (reusable subflows / community subflow nodes).</summary>
    System.Collections.Generic.Dictionary<string, string> RunSubflow(NodeGraph sub, System.Collections.Generic.Dictionary<string, string> inputs);

    /// <summary>Fire every On Signal trigger whose name matches (one flow triggering another).</summary>
    void EmitSignal(string name, string data);

    // persistent per-bot state (survives across events, saved with the workspace)
    string GetState(string key);
    void SetState(string key, string value);

    /// <summary>Seconds since the Unix epoch (for cooldowns/timers).</summary>
    double NowSeconds();

    // context variables (nick, channel, message, args, replytarget…)
    string Var(string name);
    void SetVar(string name, string value);

    /// <summary>Expand {tokens} in <paramref name="template"/> from context vars.</summary>
    string Resolve(string template);

    /// <summary>Uniform random in [0,1).</summary>
    double Rng();

    // IRC effects
    void Reply(string text);                   // to the triggering channel/user
    void React(string emoji);                  // react to the triggering message (+draft/react)

    /// <summary>React to a specific message by its msgid (defaults to the triggering message when blank).</summary>
    void ReactTo(string target, string msgid, string emoji);

    /// <summary>The most-recent messages the bot has seen (newest last) so an AI can look one up and act on it.</summary>
    System.Collections.Generic.IReadOnlyList<RecentMsg> RecentMessages(int count);

    /// <summary>Ask the server for a target's message history (IRCv3 CHATHISTORY), including messages from before
    /// the bot joined. Blocks until the batch arrives or the timeout; the batch fires no triggers.</summary>
    System.Collections.Generic.IReadOnlyList<RecentMsg> History(string target, string sub, int count, int timeoutMs);

    /// <summary>Read tracked IRC session state: nick, network, caps, channels, topic, members, count, joined.</summary>
    string IrcInfo(string what, string channel);
    void ReplyThreaded(string text);           // threaded reply to the triggering message (+draft/reply)
    void Send(string target, string text);     // PRIVMSG target :text
    void Notice(string target, string text);
    void Join(string channel);
    void Part(string channel, string reason);
    void Raw(string line);
    void SetFloodBudget(int burst, double interval);

    /// <summary>Record AI token usage (input + output) against this bot's token meter and any spend cap.</summary>
    void RecordTokens(int input, int output);

    /// <summary>True when this bot has hit its AI spend cap - an AI node fires its over-budget exec output
    /// instead of calling the model. Always false in dry runs (no live meter).</summary>
    bool AiOverBudget { get; }

    /// <summary>Set this bot's AI spend cap: at most <paramref name="maxTokens"/> tokens (0 = no cap), optionally
    /// resetting the count every <paramref name="windowSeconds"/> (0 = never reset).</summary>
    void SetTokenBudget(int maxTokens, double windowSeconds);

    /// <summary>Auto-heal: (re)connect a dropped server. Blank reconnects the server this flow is running on; a
    /// name targets a specific one. A server that's already connected is left alone.</summary>
    void Reconnect(string server);

    /// <summary>Accept an incoming DCC file offer: active (connect to ip:port) or passive (port 0 + token).
    /// Downloads <paramref name="size"/> bytes to <paramref name="savePath"/> on a background worker.</summary>
    void DccReceive(string fromNick, string ip, int port, long size, string token, string savePath);

    /// <summary>Send a file to <paramref name="toNick"/> via DCC SEND: listen on a port, advertise it via CTCP,
    /// stream the file when they connect. Blank <paramref name="advertiseIp"/> auto-detects this machine's IP.</summary>
    void DccSend(string toNick, string filePath, string advertiseIp);
    void StartTyping(string target);           // IRCv3 +typing indicator until stopped / workflow end
    void StopTyping(string target);

    void Log(string message, LogLevel level = LogLevel.Action);

    /// <summary>Hold this run until a human approves/denies (or it times out), resuming this node's
    /// approved/denied exec output. The question must already be posted. Returns false if no human gate is
    /// available (e.g. a dry run), so the node can fall back instead of stalling forever.</summary>
    bool AwaitApproval(string target, string approver, string approveWord, string denyWord, int timeoutSec);

    /// <summary>Run another node as an AI tool: bind the model's <paramref name="args"/> to its inputs by
    /// name, execute it, and return its first data output as the tool result.</summary>
    string InvokeNodeTool(Node node, System.Collections.Generic.Dictionary<string, string> args);

    /// <summary>Run an AI Tool that lives INSIDE a baked composite: seed the model's <paramref name="args"/>,
    /// run the inner <paramref name="innerToolId"/> node in a child scope over <paramref name="sub"/>, and
    /// return what its Tool Reply produced.</summary>
    string InvokeSubflowTool(NodeGraph sub, string innerToolId, System.Collections.Generic.Dictionary<string, string> args);
}
