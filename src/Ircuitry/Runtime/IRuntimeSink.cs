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

    /// <summary>Tune this server's outgoing flood throttle (token bucket): `burst` lines instantly, then one
    /// per `interval` seconds. No-op on sinks without a live server.</summary>
    void SetFloodBudget(int burst, double interval) { }

    /// <summary>Record AI token usage (input + output) against the bot's token meter / spend cap. No-op on
    /// sinks without a live runtime (dry runs/tests don't count tokens).</summary>
    void RecordTokens(int input, int output) { }

    /// <summary>True when the bot has hit its AI spend cap. Always false without a live runtime.</summary>
    bool AiOverBudget => false;

    /// <summary>Set the bot's AI spend cap (max tokens, optional reset window in seconds). No-op without a runtime.</summary>
    void SetTokenBudget(int maxTokens, double windowSeconds) { }

    /// <summary>Auto-heal: (re)connect a dropped server (blank = the current one). No-op without a live runtime.</summary>
    void Reconnect(string server) { }

    /// <summary>Upload a file to the server's IRCv3 draft/FILEHOST endpoint (gets a token, POSTs the body, returns
    /// the link). Returns (ok, link-or-error). False without a live connection or an advertised filehost.</summary>
    (bool ok, string result) FilehostUpload(string filePath) => (false, "no file host on this connection");

    /// <summary>Accept a DCC file offer (active connect, or passive listen + reverse offer) and download to
    /// <paramref name="savePath"/> on a background worker. No-op on sinks without a live server (dry runs/tests).</summary>
    void DccReceive(string fromNick, string ip, int port, long size, string token, string savePath) { }

    /// <summary>Offer a file to a nick via DCC SEND (listen, advertise via CTCP, stream on connect). No-op on
    /// sinks without a live server.</summary>
    void DccSend(string toNick, string filePath, string advertiseIp) { }

    /// <summary>Begin an IRCv3 typing indicator (+typing=active) to a target, resent on a cadence
    /// until <see cref="StopTyping"/> or the end of the workflow run.</summary>
    void StartTyping(string target);
    void StopTyping(string target);

    void Log(string message, LogLevel level);

    /// <summary>Signals that a node STARTED executing (for canvas activity feedback / fire glow).</summary>
    void NodeFired(string nodeId);

    /// <summary>Signals that a node FINISHED executing. Pairs with <see cref="NodeFired"/> so the canvas can
    /// keep a node animated for the whole time a slow Exec (AI call, delay, HTTP) is still running. No-op by default.</summary>
    void NodeDone(string nodeId) { }

    /// <summary>A node threw during a run - attributed for the error tray. No-op on sinks without one.</summary>
    void NodeError(string nodeId, string title, string message) { }

    /// <summary>Fired once when a run finishes, with the set of node typeIds that executed without
    /// throwing during it. Drives spec-compliance achievements: a multi-node spec only counts when all
    /// of its nodes succeeded in the same run. Dry runs/tests no-op this so only real runs count.</summary>
    void RunCompleted(System.Collections.Generic.IReadOnlyCollection<string> executedTypes);

    /// <summary>The most-recent messages this bot has seen (newest last) so a SuperAI tool can look one up
    /// and react to it by msgid. Sinks without a buffer (dry runs/tests) return empty.</summary>
    System.Collections.Generic.IReadOnlyList<RecentMsg> RecentMessages(int count) => System.Array.Empty<RecentMsg>();

    /// <summary>Ask the server for message history of a target via IRCv3 CHATHISTORY (e.g. sub "LATEST"),
    /// including messages from before the bot joined. Blocks the calling worker until the batch arrives or the
    /// timeout, then returns the messages (oldest first). The batch never fires triggers. Sinks that don't talk
    /// to a server (dry runs/tests) return empty.</summary>
    System.Collections.Generic.IReadOnlyList<RecentMsg> RequestHistory(string target, string sub, int count, int timeoutMs)
        => System.Array.Empty<RecentMsg>();

    /// <summary>Read a piece of tracked IRC session state by name (nick, network, caps, channels, topic,
    /// members, count, joined). <paramref name="channel"/> applies to the channel-specific ones. "" if unknown.</summary>
    string IrcInfo(string what, string channel) => "";

    /// <summary>True if the named IRCv3 capability is enabled on this connection. Lets a node check that a
    /// cap-gated command (SETNAME, REDACT, METADATA...) will actually work before sending it. False on sinks
    /// without a live server (dry runs/tests) unless they model caps.</summary>
    bool HasCap(string cap) => false;

    /// <summary>Read a metadata key off a target (IRCv3 METADATA GET), blocking until the reply or timeout.
    /// "" on sinks without a live server.</summary>
    string MetadataGet(string target, string key, int timeoutMs) => "";

    // ---- general-purpose sockets (TCP/UDP/WebSocket) - no-ops on sinks without a live runtime ----
    /// <summary>Start a listener; returns its id ("" if it couldn't bind).</summary>
    string SocketListen(string proto, int port, string framing, string delimiter, bool tls, string certPath, string certPass) => "";
    /// <summary>Dial out; returns the connection id ("" on failure).</summary>
    string SocketConnect(string proto, string host, int port, string framing, string delimiter, bool tls, System.Collections.Generic.IReadOnlyList<(string, string)> headers) => "";
    /// <summary>Send bytes to a connection id (or a UDP remote "host:port" when set).</summary>
    bool SocketSend(string connId, byte[] data, string udpRemote) => false;
    /// <summary>Send bytes to every connection accepted by a listener; returns how many got it.</summary>
    int SocketBroadcast(string listenerId, byte[] data) => 0;
    /// <summary>Close a connection or a listener by id.</summary>
    void SocketClose(string id) { }

    // ---- node-authored UI windows (UiKit) - real OS windows rendered by ircuitry's own renderer.
    // No-ops on sinks without a live runtime (dry runs/tests/headless servers). ----
    /// <summary>Open (or update) a window: title + size + background (0xRRGGBBAA).</summary>
    void UiWindow(string windowId, string title, int width, int height, uint bg) { }
    /// <summary>Add or replace an element (by its id) in a window's scene, then push the scene to the window.</summary>
    void UiUpsert(string windowId, Ircuitry.UiKit.UiElement element) { }
    /// <summary>Attach a tween to an element in a window's scene.</summary>
    void UiAnimate(string windowId, string elementId, Ircuitry.UiKit.Tween tween) { }
    /// <summary>Remove an element by id (empty id clears the whole window).</summary>
    void UiRemove(string windowId, string elementId) { }
    /// <summary>Close a window.</summary>
    void UiClose(string windowId) { }
    /// <summary>Set up (or update) the window's 3D world camera - it renders behind the 2D overlay.</summary>
    void UiScene3D(string windowId, Ircuitry.UiKit.Camera cam) { }
    /// <summary>Add or replace a 3D mesh (by id) in the window's 3D world.</summary>
    void UiMesh(string windowId, Ircuitry.UiKit.Obj3D mesh) { }

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
