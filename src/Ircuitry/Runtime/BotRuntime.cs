using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Ircuitry.Core;
using Ircuitry.Graph;
using Ircuitry.Irc;

namespace Ircuitry.Runtime;

/// <summary>
/// Drives a live bot across one or more server connections. Each <see cref="ServerConn"/> handles its own
/// IRC socket and fires the shared graph when its server sends an event; this coordinator owns the bits that
/// are one-per-bot: the frozen graph snapshot, run history, fire glow, persistent variables and achievement
/// crediting. The running graph is a frozen snapshot taken at Start, so editing on the canvas never races the
/// executor (apply = re-freeze).
/// </summary>
public sealed class BotRuntime
{
    private readonly ConsoleLog _log;
    private readonly ConcurrentDictionary<string, string> _state;
    private volatile NodeGraph? _runGraph;          // frozen snapshot; read on IRC + timer threads
    private readonly List<ServerConn> _conns = new();
    private readonly object _connLock = new();
    private int _workflowSeq;

    private readonly ConcurrentDictionary<string, DateTime> _activity = new();
    private readonly ConcurrentDictionary<string, int> _fireCounts = new();
    private readonly LinkedList<RunRecord> _history = new();
    private readonly object _historyLock = new();
    public long HistoryRevision { get; private set; }

    // a small ring of the most-recent messages the bot has seen across all its servers, so a SuperAI tool
    // can hand the model "what just happened" and let it react to a specific one by msgid. Server history
    // (CHATHISTORY) arrives asynchronously; this is the synchronous, in-memory view a single tool call can use.
    private readonly LinkedList<RecentMsg> _recent = new();
    private readonly object _recentLock = new();

    public ConsoleLog Logs => _log;

    /// <summary>The owning bot's name, kept in sync by <see cref="Ircuitry.App.Bot"/> for achievement crediting.</summary>
    public string OwnerName = "";

    public BotRuntime(ConsoleLog log, ConcurrentDictionary<string, string> state)
    {
        _log = log;
        _state = state;
    }

    // ===================================================================
    //  Aggregate status across connections (for the UI)
    // ===================================================================
    public bool Running { get { lock (_connLock) return _conns.Any(c => c.Running); } }

    public IrcState State
    {
        get
        {
            lock (_connLock)
            {
                if (_conns.Count == 0) return IrcState.Disconnected;
                if (_conns.Any(c => c.State == IrcState.Connected)) return IrcState.Connected;
                if (_conns.Any(c => c.State is IrcState.Connecting or IrcState.Registering)) return IrcState.Connecting;
                if (_conns.Any(c => c.State == IrcState.Error)) return IrcState.Error;
                return IrcState.Disconnected;
            }
        }
    }

    public string CurrentNick
    {
        get
        {
            lock (_connLock)
            {
                var live = _conns.FirstOrDefault(c => c.State == IrcState.Connected);
                return (live ?? _conns.FirstOrDefault())?.CurrentNick ?? "";
            }
        }
    }

    public IReadOnlyList<string> EnabledCaps
    {
        get { lock (_connLock) return _conns.SelectMany(c => c.EnabledCaps).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); }
    }

    /// <summary>The connection the read-only IRC view follows: a connected one if any, else the first.</summary>
    public ServerConn? PrimaryConn
    {
        get { lock (_connLock) return _conns.FirstOrDefault(c => c.State == IrcState.Connected) ?? _conns.FirstOrDefault(); }
    }

    public int MessagesSeen { get { lock (_connLock) return _conns.Sum(c => c.MessagesSeen); } }
    public int ActionsFired { get { lock (_connLock) return _conns.Sum(c => c.ActionsFired); } }
    internal int TotalActions => ActionsFired;

    /// <summary>A snapshot of the live connections, for the inspector, network map and tray menu.</summary>
    public IReadOnlyList<ServerConn> Conns { get { lock (_connLock) return _conns.ToList(); } }

    // ===================================================================
    //  Lifecycle
    // ===================================================================
    public void Start(NodeGraph graph, IrcSettings cfg) => Start(graph, new[] { cfg });

    /// <summary>Connect every given server, replacing any current connections with a fresh run.</summary>
    public void Start(NodeGraph graph, IEnumerable<IrcSettings> servers)
    {
        Stop();
        FreezeGraph(graph);
        _fireCounts.Clear();
        ClearHistory();
        var list = servers?.ToList() ?? new List<IrcSettings>();
        if (list.Count == 0) return;
        lock (_connLock)
            foreach (var cfg in list)
            {
                var c = new ServerConn(this, cfg);
                _conns.Add(c);
                c.Start();
            }
    }

    /// <summary>Bring one more server online (or reconnect it) without disturbing the others.</summary>
    public void ConnectServer(NodeGraph graph, IrcSettings cfg)
    {
        lock (_connLock)
        {
            if (_runGraph == null) { FreezeGraph(graph); _fireCounts.Clear(); ClearHistory(); }
            var existing = _conns.FirstOrDefault(c => c.Matches(cfg.DisplayName));
            if (existing != null) { if (existing.Running) return; _conns.Remove(existing); }
            var c = new ServerConn(this, cfg);
            _conns.Add(c);
            c.Start();
        }
    }

    /// <summary>Disconnect one server by its label/host (others stay live).</summary>
    public void DisconnectServer(string name)
    {
        ServerConn? target;
        lock (_connLock) target = _conns.FirstOrDefault(c => c.Matches(name) || c.Label == name);
        target?.Stop();
    }

    public void Stop()
    {
        List<ServerConn> snapshot;
        lock (_connLock) { snapshot = _conns.ToList(); _conns.Clear(); }
        foreach (var c in snapshot) { try { c.Stop(); } catch { } }
    }

    /// <summary>Swap in an edited graph on a LIVE bot without disconnecting (no restart needed).</summary>
    public void ApplyGraph(NodeGraph graph)
    {
        if (!Running) return;
        FreezeGraph(graph);
        _log.Add(LogLevel.System, $"↻ applied workflow changes - {CountTriggers()} trigger(s) armed");
    }

    private void FreezeGraph(NodeGraph graph) =>
        _runGraph = GraphSerializer.Load(GraphSerializer.Save(graph, "run")).graph;

    internal NodeGraph? RunGraph => _runGraph;
    internal int NextWorkflowSeq() => System.Threading.Interlocked.Increment(ref _workflowSeq);

    internal int CountTriggers()
    {
        var g = _runGraph;
        if (g == null) return 0;
        int n = 0;
        foreach (var node in g.Nodes) if (node.Def.IsTrigger) n++;
        return n;
    }

    /// <summary>Find the connection a node's "server" override names, or null to fall back to origin.</summary>
    internal ServerConn? Route(string name)
    {
        lock (_connLock) return _conns.FirstOrDefault(c => c.Matches(name));
    }

    /// <summary>The live connection for a given server label/host, or null if that server isn't connected.</summary>
    public ServerConn? FindConn(string name)
    {
        lock (_connLock) return _conns.FirstOrDefault(c => c.Matches(name) || c.Label == name);
    }

    // ===================================================================
    //  Shared (one-per-bot) effects, called from the connections
    // ===================================================================
    internal void LogFrom(string server, LogLevel level, string message)
    {
        // keep the server label as a separate field (not prefixed into the text) so the console can still
        // parse a raw IRC line cleanly and show the origin as its own chip.
        bool multi; lock (_connLock) multi = _conns.Count > 1;
        _log.Add(level, message, multi && server.Length > 0 ? server : "");
    }

    public void NodeFired(string nodeId) { _activity[nodeId] = DateTime.Now; _fireCounts.AddOrUpdate(nodeId, 1, (_, n) => n + 1); }

    internal void CreditRun(IReadOnlyCollection<string> executedTypes)
    {
        if (OwnerName.Length > 0) Achievements.MarkRun(executedTypes);
    }

    public string GetState(string key) => _state.TryGetValue(key, out var v) ? v : "";
    public void SetState(string key, string value) => _state[key] = value;

    // ---- run history (newest last, bounded at 1000) ----
    public List<RunRecord> History() { lock (_historyLock) return new List<RunRecord>(_history); }
    public int HistoryCount { get { lock (_historyLock) return _history.Count; } }
    public void ClearHistory() { lock (_historyLock) { _history.Clear(); HistoryRevision++; } }

    internal void AddHistory(RunRecord rec)
    {
        lock (_historyLock)
        {
            _history.AddLast(rec);
            while (_history.Count > 1000) _history.RemoveFirst();
            HistoryRevision++;
        }
    }

    /// <summary>Record a message the bot just saw (from any server) into the recent-message ring.</summary>
    internal void RecordMessage(string nick, string channel, string text, string msgid)
    {
        lock (_recentLock)
        {
            _recent.AddLast(new RecentMsg(nick, channel, text, msgid, DateTime.Now));
            while (_recent.Count > 200) _recent.RemoveFirst();
        }
    }

    /// <summary>The most-recent messages the bot has seen, oldest first, capped at <paramref name="count"/>
    /// (or all of them when count &lt;= 0). Feeds SuperAI's recent_messages tool.</summary>
    public IReadOnlyList<RecentMsg> RecentMessages(int count)
    {
        lock (_recentLock)
        {
            int take = count <= 0 ? _recent.Count : Math.Min(count, _recent.Count);
            var arr = new RecentMsg[take];
            var node = _recent.Last;
            for (int i = take - 1; i >= 0 && node != null; i--, node = node.Previous) arr[i] = node.Value;
            return arr;
        }
    }

    // ---- canvas fire glow / counters ----
    /// <summary>0..1 glow intensity for a node that recently executed (fades over ~0.9s).</summary>
    public float FireGlow(string nodeId)
    {
        if (!_activity.TryGetValue(nodeId, out var t)) return 0f;
        const float life = 0.9f;
        float e = (float)(DateTime.Now - t).TotalSeconds;
        return e < 0 || e > life ? 0f : 1f - e / life;
    }

    public int FireCount(string nodeId) => _fireCounts.TryGetValue(nodeId, out var n) ? n : 0;

    // ===================================================================
    //  Schedule maths (static, also exercised directly by the self-tests)
    // ===================================================================

    /// <summary>Decides whether an "On Schedule" node should fire this tick (interval / daily / weekly / once).</summary>
    internal static bool ScheduleDue(Node n, DateTime now, Dictionary<string, DateTime> last, HashSet<string> firedOnce)
    {
        string mode = n.GetParam("mode");

        if (mode == "once")
        {
            if (firedOnce.Contains(n.Id)) return false;
            if (DateTime.TryParse(n.GetParam("datetime"), out var when) && now >= when) { firedOnce.Add(n.Id); return true; }
            return false;
        }

        if (!last.TryGetValue(n.Id, out var prev)) { last[n.Id] = now; return false; }

        switch (mode)
        {
            case "daily":
            case "weekly":
            {
                if (!TryParseHm(n.GetParam("time"), out int hh, out int mm)) return false;
                if (mode == "weekly" && !DayAllowed(n.GetParam("days"), now.DayOfWeek)) return false;
                var target = new DateTime(now.Year, now.Month, now.Day, hh, mm, 0, now.Kind);
                if (now >= target && prev < target) { last[n.Id] = now; return true; }
                return false;
            }
            default: // interval
            {
                int every = Math.Max(1, int.TryParse(n.GetParam("every"), out var e) ? e : 1);
                double secs = every * n.GetParam("unit") switch { "minutes" => 60.0, "hours" => 3600.0, "days" => 86400.0, _ => 1.0 };
                if ((now - prev).TotalSeconds >= secs) { last[n.Id] = now; return true; }
                return false;
            }
        }
    }

    private static bool TryParseHm(string s, out int hh, out int mm)
    {
        hh = mm = 0;
        var parts = (s ?? "").Split(':');
        return parts.Length == 2
            && int.TryParse(parts[0].Trim(), out hh) && hh is >= 0 and <= 23
            && int.TryParse(parts[1].Trim(), out mm) && mm is >= 0 and <= 59;
    }

    private static readonly string[] DayNames = { "sun", "mon", "tue", "wed", "thu", "fri", "sat" };

    private static int IsoDay(DayOfWeek d) => d == DayOfWeek.Sunday ? 7 : (int)d;  // Mon=1 … Sun=7

    private static int DayToIso(string tok)
    {
        tok = tok.Trim().ToLowerInvariant();
        if (tok.Length == 0) return 0;
        if (int.TryParse(tok, out var num)) return num is >= 1 and <= 7 ? num : 0;
        int idx = Array.FindIndex(DayNames, d => tok.StartsWith(d));
        return idx < 0 ? 0 : (idx == 0 ? 7 : idx);   // Sun(idx0)→7, Mon(idx1)→1 …
    }

    private static bool DayAllowed(string spec, DayOfWeek day)
    {
        spec = (spec ?? "").Trim();
        if (spec.Length == 0 || spec == "*") return true;
        int iso = IsoDay(day);
        foreach (var raw in spec.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int dash = raw.IndexOf('-');
            if (dash > 0)
            {
                int a = DayToIso(raw[..dash]), b = DayToIso(raw[(dash + 1)..]);
                if (a == 0 || b == 0) continue;
                if (a <= b ? iso >= a && iso <= b : iso >= a || iso <= b) return true;  // wrap (e.g. Fri-Mon)
            }
            else if (DayToIso(raw) == iso) return true;
        }
        return false;
    }
}
