using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ircuitry.Core;
using Ircuitry.Graph;
using Ircuitry.Runtime;

namespace Ircuitry.App;

/// <summary>The app services a plugin's <see cref="AppSink"/> needs from the shell (implemented by MainScreen).
/// Kept tiny on purpose - it grows as the powers (nav, active-bot edits) land in later phases.</summary>
public interface IAppHost
{
    void Toast(string message, string kind);
    void Log(string message, LogLevel level);
    string Info(string what);                  // read app / active-bot state
    void Nav(string action, string arg);       // switch tabs / navigate
    void BotCmd(string action, string bot);    // run / stop / restart a bot
}

/// <summary>A piece of chrome a plugin contributed: a menu item, toolbar button, command, context item or panel.
/// The app reads these from <see cref="PluginManager.Contributions"/> to render them; clicking one calls
/// <see cref="PluginManager.Activate"/>.</summary>
public sealed class PluginContribution
{
    public string PluginId = "";
    public string Kind = "";    // menu | toolbar | context | command | panel
    public string Id = "";
    public string Label = "";
    public string Icon = "";
    public string At = "";      // placement hint (menu: more|file; context: node|canvas)
}

/// <summary>An installed plugin: its graph + metadata. Loaded from a <c>.ircplugin</c> file.</summary>
public sealed class PluginMeta
{
    public string Name = "";
    public string Version = "1.0.0";
    public string Icon = "puzzle-piece";
    public string Description = "";
    public List<string> Permissions = new();
    public string Path = "";        // the .ircplugin file on disk
    public bool Enabled;
    public NodeGraph Graph = new();
}

/// <summary>The <see cref="IRuntimeSink"/> a plugin graph runs under: its effects drive ircuitry's own chrome
/// (toasts, registered contributions) and the plugin's own state, not IRC. Everything IRC is a no-op.</summary>
public sealed class AppSink : IRuntimeSink
{
    private readonly PluginHost _host;
    public AppSink(PluginHost host) { _host = host; }

    // ---- app / plugin surface ----
    public void AppToast(string message, string kind) => _host.Manager.App.Toast(message, kind);
    public void AppContribute(string kind, string id, string label, string icon, string at)
        => _host.Manager.Register(_host, kind, id, label, icon, at);
    public string AppInfo(string what) => _host.Manager.App.Info(what);
    public void AppNav(string action, string arg) => _host.Manager.App.Nav(action, arg);
    public void AppBot(string action, string bot) => _host.Manager.App.BotCmd(action, bot);

    // ---- plugin's own persistent state (kept in the host) ----
    public string GetState(string key) => _host.State.TryGetValue(key, out var v) ? v : "";
    public void SetState(string key, string value) => _host.State[key] = value;

    public void Log(string message, LogLevel level) => _host.Manager.App.Log("[" + _host.Id + "] " + message, level);

    // ---- IRC + co.: an app sink doesn't do any of this ----
    public void Privmsg(string target, string text) { }
    public void Notice(string target, string text) { }
    public void React(string target, string msgid, string emoji) { }
    public void PrivmsgTagged(string target, string text, string clientTags) { }
    public void NoticeTagged(string target, string text, string clientTags) { }
    public void Join(string channel) { }
    public void Part(string channel, string reason) { }
    public void Raw(string line) { }
    public void StartTyping(string target) { }
    public void StopTyping(string target) { }
    public void NodeFired(string nodeId) { }
    public void RunCompleted(System.Collections.Generic.IReadOnlyCollection<string> executedTypes) { }
}

/// <summary>One running plugin: a frozen graph + its own state, run app-globally under an <see cref="AppSink"/>.
/// <see cref="Start"/> fires the plugin's On Plugin Start to register its chrome; <see cref="Activate"/> fires
/// On App Event when a contributed item is used.</summary>
public sealed class PluginHost
{
    public readonly string Id;
    public readonly NodeGraph Graph;
    public readonly Dictionary<string, string> State = new();
    public readonly PluginManager Manager;
    private readonly AppSink _sink;

    public PluginHost(PluginManager mgr, string id, NodeGraph graph)
    {
        Manager = mgr; Id = id; Graph = graph; _sink = new AppSink(this);
    }

    private Dictionary<string, string> BaseVars() => new() { ["botnick"] = "ircuitry", ["plugin"] = Id };

    /// <summary>Fire On Plugin Start (registers the plugin's menu items / buttons / panels).</summary>
    public void Start() => FireFamily("app.start", BaseVars());

    /// <summary>Fire On App Event for a contribution's activation.</summary>
    public void Activate(string kind, string id, Dictionary<string, string>? extra = null)
    {
        var v = BaseVars();
        v["app_event"] = kind; v["app_id"] = id;
        if (extra != null) foreach (var kv in extra) v[kv.Key] = kv.Value;
        FireFamily("app", v);
    }

    private void FireFamily(string family, Dictionary<string, string> vars)
    {
        // run every trigger node of this family (each filters itself), like ServerConn.FireFamily
        foreach (var n in Graph.Nodes.Where(n => n.Def.TriggerEvent == family).ToList())
        {
            try { GraphExecutor.Fire(Graph, _sink, n, new Dictionary<string, string>(vars)); }
            catch (Exception ex) { Manager.App.Log("[" + Id + "] plugin error: " + ex.Message, LogLevel.Error); }
        }
    }
}

/// <summary>Owns installed plugins, their live <see cref="PluginHost"/>s, and the contribution registry the
/// chrome renders from. Phase 1: install (auto-enabled) + uninstall + the menu/activation loop.</summary>
public sealed class PluginManager
{
    public readonly IAppHost App;
    private readonly List<PluginMeta> _plugins = new();
    private readonly Dictionary<string, PluginHost> _hosts = new();
    private readonly List<PluginContribution> _contribs = new();
    private readonly object _gate = new();

    public PluginManager(IAppHost app) { App = app; }

    public static string Dir => Path.Combine(AppModel.WorkspaceDir, "plugins");

    public IReadOnlyList<PluginMeta> Plugins { get { lock (_gate) return _plugins.ToList(); } }

    /// <summary>Live contributions of a kind (menu | toolbar | context | command | panel).</summary>
    public IReadOnlyList<PluginContribution> Contributions(string kind)
    { lock (_gate) return _contribs.Where(c => c.Kind == kind).ToList(); }

    public void Register(PluginHost host, string kind, string id, string label, string icon, string at)
    { lock (_gate) _contribs.Add(new PluginContribution { PluginId = host.Id, Kind = kind, Id = id, Label = label, Icon = icon, At = at }); }

    /// <summary>A contributed item was used - fire its plugin's On App Event.</summary>
    public void Activate(string pluginId, string kind, string id, Dictionary<string, string>? extra = null)
    {
        PluginHost? h; lock (_gate) _hosts.TryGetValue(pluginId, out h);
        h?.Activate(kind, id, extra);
    }

    public void Enable(PluginMeta p)
    {
        lock (_gate) { if (_hosts.ContainsKey(p.Name)) return; }
        var host = new PluginHost(this, p.Name, p.Graph);
        lock (_gate) _hosts[p.Name] = host;
        p.Enabled = true;
        host.Start();   // register contributions
    }

    public void Disable(PluginMeta p)
    {
        lock (_gate) { _hosts.Remove(p.Name); _contribs.RemoveAll(c => c.PluginId == p.Name); }
        p.Enabled = false;
    }

    /// <summary>Load + enable every installed plugin (called at app start). Best-effort per file.</summary>
    public void LoadInstalled()
    {
        lock (_gate) { _plugins.Clear(); _hosts.Clear(); _contribs.Clear(); }
        string dir = Dir;
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir, "*.ircplugin"))
        {
            try
            {
                var m = PluginBundle.Load(File.ReadAllText(f));
                m.Path = f;
                lock (_gate) _plugins.Add(m);
                Enable(m);
            }
            catch (Exception ex) { App.Log("plugin load failed (" + Path.GetFileName(f) + "): " + ex.Message, LogLevel.Error); }
        }
    }

    /// <summary>Install a plugin from its bundle JSON: write it to the plugins dir and enable it.</summary>
    public PluginMeta Install(string json)
    {
        var m = PluginBundle.Load(json);
        Directory.CreateDirectory(Dir);
        m.Path = Path.Combine(Dir, SafeName(m.Name) + ".ircplugin");
        File.WriteAllText(m.Path, json);
        lock (_gate) { _plugins.RemoveAll(x => x.Name == m.Name); _plugins.Add(m); }
        Disable(m);   // clear any stale host/contribs for a same-named reinstall
        Enable(m);
        return m;
    }

    public void Uninstall(PluginMeta p)
    {
        Disable(p);
        try { if (File.Exists(p.Path)) File.Delete(p.Path); } catch { /* best effort */ }
        lock (_gate) _plugins.RemoveAll(x => x.Name == p.Name);
    }

    private static string SafeName(string s)
    {
        var clean = new string(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray());
        return clean.Trim('-', '.').Length == 0 ? "plugin" : clean.Trim('-', '.');
    }
}

/// <summary>The <c>.ircplugin</c> bundle: a workflow graph (same nodes/connections as <c>.ircbot</c>) plus plugin
/// metadata (name, version, icon, declared permissions). A superset of <c>.ircbot</c>, so the graph still loads
/// through <see cref="GraphSerializer"/>.</summary>
public static class PluginBundle
{
    public static bool Is(string json)
    {
        try { return JsonNode.Parse(json)?["format"]?.GetValue<string>() == "ircuitry.plugin.v1"; }
        catch { return false; }
    }

    public static PluginMeta Load(string json)
    {
        var (graph, name) = GraphSerializer.Load(json);   // reads nodes/connections + name; ignores extra keys
        var m = new PluginMeta { Graph = graph, Name = name };
        var root = JsonNode.Parse(json)?.AsObject();
        if (root != null)
        {
            if (root["name"]?.GetValue<string>() is { Length: > 0 } nm) m.Name = nm;
            if (root["version"]?.GetValue<string>() is { } v) m.Version = v;
            if (root["icon"]?.GetValue<string>() is { } ic) m.Icon = ic;
            if (root["description"]?.GetValue<string>() is { } d) m.Description = d;
            if (root["permissions"] is JsonArray pa) m.Permissions = pa.Select(e => e?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList();
        }
        if (m.Permissions.Count == 0) m.Permissions = PermissionsFor(graph);
        return m;
    }

    public static string Save(string name, string version, string icon, string description, NodeGraph g)
    {
        var node = JsonNode.Parse(GraphSerializer.Save(g, name))!.AsObject();   // {format, name, nodes, connections}
        node["format"] = "ircuitry.plugin.v1";
        node["name"] = name;
        node["version"] = version;
        node["icon"] = icon;
        node["description"] = description;
        node["permissions"] = new JsonArray(PermissionsFor(g).Select(p => (JsonNode)p).ToArray());
        // keep metadata near the top: rebuild with a friendly key order
        var ordered = new JsonObject
        {
            ["format"] = "ircuitry.plugin.v1", ["name"] = name, ["version"] = version, ["icon"] = icon,
            ["description"] = description, ["permissions"] = node["permissions"]!.DeepClone(),
            ["nodes"] = node["nodes"]!.DeepClone(), ["connections"] = node["connections"]!.DeepClone(),
        };
        return ordered.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The app-permissions a graph needs, derived from the app.* nodes it uses (shown on the trust card).</summary>
    public static List<string> PermissionsFor(NodeGraph g)
    {
        var perms = new HashSet<string>();
        foreach (var n in g.Nodes)
        {
            switch (n.TypeId)
            {
                case "app.menu": perms.Add("menu"); break;
                case "app.toolbar": perms.Add("toolbar"); break;
                case "app.context": perms.Add("context"); break;
                case "app.command": perms.Add("command"); break;
                case "app.panel": perms.Add("panel"); break;
                case "app.toast": case "app.dialog": case "app.confirm": perms.Add("dialogs"); break;
                case "app.bot": perms.Add("control-bots"); break;
                case "app.nav": perms.Add("navigate"); break;
                case "app.info": perms.Add("app-state"); break;
                default:
                    if (n.TypeId.StartsWith("app.graph", StringComparison.Ordinal)) perms.Add("edit-graph");
                    break;
            }
        }
        return perms.OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    /// <summary>True if a graph is plugin-shaped (uses at least one app.* hook) - gates "Bundle as Plugin".</summary>
    public static bool LooksLikePlugin(NodeGraph g) =>
        g.Nodes.Any(n => n.TypeId == "app.start" || n.TypeId.StartsWith("app.", StringComparison.Ordinal));
}
