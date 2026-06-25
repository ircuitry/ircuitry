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

    /// <summary>Invalidate cached pure-node outputs so they recompute on the next pull (loops call this between
    /// iterations so a pure node reading the loop var isn't pinned to its first-iteration value).</summary>
    void InvalidatePure();

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

    /// <summary>True if the named IRCv3 capability is enabled on the connection this flow is acting on, so a
    /// node can check a cap-gated command (SETNAME / REDACT / METADATA) will work before sending it.</summary>
    bool HasCap(string cap);

    /// <summary>Read a metadata key off a target (IRCv3 METADATA GET): blocks the worker until the reply or
    /// timeout, then returns the value ("" if unset / unsupported). Correlated by labeled-response when available.</summary>
    string MetadataGet(string target, string key, int timeoutMs);

    // ---- general-purpose sockets (TCP/UDP/WebSocket) ----
    string SocketListen(string proto, int port, string framing, string delimiter, bool tls, string certPath, string certPass);
    string SocketConnect(string proto, string host, int port, string framing, string delimiter, bool tls, System.Collections.Generic.IReadOnlyList<(string, string)> headers);
    bool SocketSend(string connId, byte[] data, string udpRemote);
    int SocketBroadcast(string listenerId, byte[] data);
    void SocketClose(string id);

    // ---- node-authored UI windows (UiKit) ----
    void UiWindow(string windowId, string title, int width, int height, uint bg);
    void UiUpsert(string windowId, Ircuitry.UiKit.UiElement element);
    void UiAnimate(string windowId, string elementId, Ircuitry.UiKit.Tween tween);
    void UiRemove(string windowId, string elementId);
    void UiClose(string windowId);
    void UiScene3D(string windowId, Ircuitry.UiKit.Camera cam);
    void UiMesh(string windowId, Ircuitry.UiKit.Obj3D mesh);
    void UiControls(string windowId, string mode);
    void UiWeb(string windowId, string url, string html, int width, int height, string title);
    // ---- app / plugin surface (active under the app's AppSink; no-op for bot graphs) ----
    void AppToast(string message, string kind);
    void AppContribute(string kind, string id, string label, string icon, string at);
    string AppInfo(string what);
    void AppNav(string action, string arg);
    void AppBot(string action, string bot);
    void AppDialog(string title, string message, string okLabel);
    void AppConfirm(string title, string message, string okLabel, string cancelLabel);
    string AppGraph(string op, string a1, string a2, string a3, string a4);
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

    /// <summary>Upload a file to the server's IRCv3 draft/FILEHOST endpoint. Returns (ok, link-or-error).</summary>
    (bool ok, string result) FilehostUpload(string filePath);

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
