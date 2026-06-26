using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
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
    void Dialog(string title, string message, string okLabel);                                           // OK message box
    void Confirm(string pluginId, string nodeId, string title, string message, string okLabel, string cancelLabel);   // yes/no -> resumes the plugin
    string GraphEdit(string op, string a1, string a2, string a3, string a4);                             // edit the active bot's graph
    void OpenSettings(string pluginId);                                                                  // open a plugin's settings modal
    string Selection(string what);                                                                       // read the editor's current selection
    void OpenExternal(string target);                                                                    // open a url / file / deep-link in the OS
    string Clipboard(string op, string text);                                                            // read | write the system clipboard
    void Notify(string title, string message);                                                           // native OS desktop notification
    void Prompt(string pluginId, string nodeId, string title, string message, string def, string placeholder); // text input -> resumes the plugin
    void Pick(string pluginId, string nodeId, string title, string options);                             // list chooser -> resumes the plugin
    void FileDialog(string pluginId, string nodeId, string mode, string title, string def);             // native file dialog -> resumes the plugin
    void Status(string pluginId, string op, string id, string text, string icon, string tooltip);        // set/clear a status-bar item
}

/// <summary>One field of a plugin's settings form. <see cref="Type"/>: text | password | secret (secret uses the
/// key picker and stores a <c>{{secret.NAME}}</c> reference). The value is saved as persisted config + a state var.</summary>
public sealed class SettingsField
{
    public string Key = "", Label = "", Type = "text", Placeholder = "";
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
    public bool Pending;            // present on disk but never approved (or its permissions changed) -> needs review, doesn't run
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
    public void AppDialog(string title, string message, string okLabel) => _host.Manager.App.Dialog(title, message, okLabel);
    public void AppConfirm(Ircuitry.Graph.Node node, Dictionary<string, string> vars, string title, string message, string okLabel, string cancelLabel)
        => _host.BeginConfirm(node, vars, title, message, okLabel, cancelLabel);
    public string AppGraph(string op, string a1, string a2, string a3, string a4) => _host.Manager.App.GraphEdit(op, a1, a2, a3, a4);
    public void AppSettingsField(string key, string label, string type, string placeholder) => _host.AddSettingsField(key, label, type, placeholder);
    public void AppOpenSettings() => _host.Manager.App.OpenSettings(_host.Id);
    public string AppStore(string op, string key, string value) => _host.Store(op, key, value);
    public void AppBus(string channel, string payload) => _host.Manager.Emit(_host.Id, channel, payload);
    public string AppSelection(string what) => _host.Manager.App.Selection(what);
    public void AppOpen(string target) => _host.Manager.App.OpenExternal(target);
    public string AppClipboard(string op, string text) => _host.Manager.App.Clipboard(op, text);
    public void AppNotify(string title, string message) => _host.Manager.App.Notify(title, message);
    public void AppPrompt(Ircuitry.Graph.Node node, Dictionary<string, string> vars, string title, string message, string def, string placeholder) => _host.BeginPrompt(node, vars, title, message, def, placeholder);
    public void AppPick(Ircuitry.Graph.Node node, Dictionary<string, string> vars, string title, string options) => _host.BeginPick(node, vars, title, options);
    public void AppFile(Ircuitry.Graph.Node node, Dictionary<string, string> vars, string mode, string title, string def) => _host.BeginFile(node, vars, mode, title, def);
    public void AppStatus(string op, string id, string text, string icon, string tooltip) => _host.Manager.App.Status(_host.Id, op, id, text, icon, tooltip);

    // ---- in-app panel scene building (the plugin's ui.* nodes draw into a dock panel, not an OS window) ----
    public void UiWindow(string id, string title, int width, int height, uint bg) => _host.UiWindow(id, title, width, height, bg);
    public void UiUpsert(string id, Ircuitry.UiKit.UiElement element) => _host.UiUpsert(id, element);
    public void UiSetText(string id, string elementId, string text) => _host.UiSetText(id, elementId, text);
    public void UiAnimate(string id, string elementId, Ircuitry.UiKit.Tween tween) => _host.UiAnimate(id, elementId, tween);
    public void UiRemove(string id, string elementId) => _host.UiRemove(id, elementId);
    public void UiClose(string id) => _host.UiClose(id);

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

    /// <summary>The settings form this plugin declared via app.settings (in order). Read by the settings modal.</summary>
    public readonly List<SettingsField> SettingsFields = new();
    public void AddSettingsField(string key, string label, string type, string placeholder)
    { lock (SettingsFields) if (!SettingsFields.Any(f => f.Key == key)) SettingsFields.Add(new SettingsField { Key = key, Label = label, Type = type, Placeholder = placeholder }); }
    /// <summary>Apply a configured setting value: persisted by the manager, and set as a state var (on the worker, so
    /// it's serialised with the plugin's flows) so {tokens} pick it up.</summary>
    public void ApplyConfig(string key, string value) => Schedule(() => State[key] = value);

    // A plugin's flows run on its OWN single worker thread (so a blocking node - container.exec, net.http, a delay -
    // never freezes the editor UI). One thread per plugin serialises that plugin's flows, so its State/Scenes are
    // never touched by two flows at once. App-facing effects marshal to the UI thread inside MainScreen's IAppHost;
    // scene reads/writes are guarded by Manager.SceneGate (held by MainScreen while it renders the panel).
    private readonly BlockingCollection<Action>? _queue;
    private volatile int _busy;       // queued + running jobs (for WaitIdle in tests)

    // in-app panel scenes the plugin's ui.* nodes build (keyed by window/panel id). Mutated on the worker, read by
    // MainScreen while rendering - all under Manager.SceneGate.
    public readonly Dictionary<string, UiKit.UiScene> Scenes = new();
    public UiKit.UiScene? SceneFor(string id)
    { lock (Manager.SceneGate) return Scenes.TryGetValue(id.Length == 0 ? "main" : id, out var s) ? s : null; }

    public PluginHost(PluginManager mgr, string id, NodeGraph graph, bool threaded)
    {
        Manager = mgr; Id = id; Graph = graph; _sink = new AppSink(this);
        if (threaded)
        {
            _queue = new BlockingCollection<Action>();
            new Thread(() => { foreach (var job in _queue.GetConsumingEnumerable()) { try { job(); } catch { } } })
            { IsBackground = true, Name = "plugin:" + id }.Start();
        }
    }

    // run a flow body: on the worker when threaded, inline otherwise (tests). Inline keeps the selftest synchronous.
    private void Schedule(Action body)
    {
        if (_queue == null) { body(); return; }
        Interlocked.Increment(ref _busy);
        _queue.Add(() => { try { body(); } finally { Interlocked.Decrement(ref _busy); } });
    }

    /// <summary>Block until this plugin's worker has drained (tests only; returns false on timeout).</summary>
    public bool WaitIdle(int ms)
    {
        for (int waited = 0; _busy > 0 && waited < ms; waited += 5) Thread.Sleep(5);
        return _busy == 0;
    }

    /// <summary>Stop the worker thread (on disable/uninstall).</summary>
    public void Dispose() => _queue?.CompleteAdding();

    private UiKit.UiScene EnsureScene(string id)   // caller holds SceneGate
    {
        if (id.Length == 0) id = "main";
        if (!Scenes.TryGetValue(id, out var s)) { s = new(); Scenes[id] = s; }
        return s;
    }

    // ---- ui.* scene building (mirrors ServerConn's handlers, minus the child-window streaming) ----
    public void UiWindow(string id, string title, int w, int h, uint bg)
    { lock (Manager.SceneGate) { var s = EnsureScene(id); if (title.Length > 0) s.Title = title; if (w > 0) s.Width = w; if (h > 0) s.Height = h; s.Bg = bg; } }
    public void UiUpsert(string id, UiKit.UiElement e)
    {
        if (e.Id.Length == 0) return;
        lock (Manager.SceneGate)
        {
            var s = EnsureScene(id);
            int i = s.Elements.FindIndex(x => x.Id == e.Id);
            if (i >= 0) { if (e.Tweens.Count == 0) e.Tweens = s.Elements[i].Tweens; s.Elements[i] = e; }   // keep running tweens on update
            else s.Elements.Add(e);
        }
    }
    public void UiAnimate(string id, string elementId, UiKit.Tween t)
    { lock (Manager.SceneGate) { var e = EnsureScene(id).Find(elementId); if (e != null) e.Tweens.Add(t); } }
    public void UiRemove(string id, string elementId)
    {
        lock (Manager.SceneGate)
        {
            if (!Scenes.TryGetValue(id.Length == 0 ? "main" : id, out var s)) return;
            if (elementId.Length == 0) s.Elements.Clear(); else s.Elements.RemoveAll(x => x.Id == elementId);
        }
    }
    public void UiClose(string id) { lock (Manager.SceneGate) Scenes.Remove(id.Length == 0 ? "main" : id); }
    /// <summary>Cheap live update of one element's text (e.g. a status line streamed by a slow node mid-run). The
    /// panel re-renders every frame, so this shows up immediately.</summary>
    public void UiSetText(string id, string elementId, string text)
    {
        lock (Manager.SceneGate)
        {
            if (Scenes.TryGetValue(id.Length == 0 ? "main" : id, out var s) && s.Find(elementId) is { } e) e.Text = text;
        }
    }

    // pending app.confirm gates: nodeId -> the captured vars to resume with when the user answers. Written on the
    // worker (BeginConfirm), read/removed from the UI thread (ResolveConfirm) -> guarded.
    private readonly Dictionary<string, Dictionary<string, string>> _pendingConfirms = new();

    /// <summary>Show a yes/no modal and remember the asking node + its vars; <see cref="ResolveConfirm"/> resumes
    /// the right branch when the user answers.</summary>
    public void BeginConfirm(Node node, Dictionary<string, string> vars, string title, string message, string ok, string cancel)
    {
        lock (_pendingConfirms) _pendingConfirms[node.Id] = vars;
        Manager.App.Confirm(Id, node.Id, title, message, ok, cancel);
    }

    /// <summary>The user answered an app.confirm: resume its confirmed (yes) / cancelled (no) exec output (on the
    /// worker, so the resumed branch can block too).</summary>
    public void ResolveConfirm(string nodeId, bool yes)
    {
        Dictionary<string, string>? vars;
        lock (_pendingConfirms) { if (!_pendingConfirms.Remove(nodeId, out vars)) return; }
        var node = Graph.Find(nodeId);
        if (node == null) return;
        Schedule(() =>
        {
            try { GraphExecutor.FireFrom(Graph, _sink, node, yes ? 0 : 1, vars); }
            catch (Exception ex) { Manager.App.Log("[" + Id + "] plugin error: " + ex.Message, LogLevel.Error); }
        });
    }

    /// <summary>Begin a value-returning gate (prompt/pick/file): remember the asking node + vars, raise the UI.</summary>
    public void BeginPrompt(Node node, Dictionary<string, string> vars, string title, string message, string def, string placeholder)
    { lock (_pendingConfirms) _pendingConfirms[node.Id] = vars; Manager.App.Prompt(Id, node.Id, title, message, def, placeholder); }
    public void BeginPick(Node node, Dictionary<string, string> vars, string title, string options)
    { lock (_pendingConfirms) _pendingConfirms[node.Id] = vars; Manager.App.Pick(Id, node.Id, title, options); }
    public void BeginFile(Node node, Dictionary<string, string> vars, string mode, string title, string def)
    { lock (_pendingConfirms) _pendingConfirms[node.Id] = vars; Manager.App.FileDialog(Id, node.Id, mode, title, def); }

    /// <summary>The user answered a prompt/pick/file gate: resume submitted(0) with the value seeded onto the
    /// node's value output (pin 1), or cancelled(2).</summary>
    public void ResolveInput(string nodeId, bool ok, string value)
    {
        Dictionary<string, string>? vars;
        lock (_pendingConfirms) { if (!_pendingConfirms.Remove(nodeId, out vars)) return; }
        var node = Graph.Find(nodeId);
        if (node == null) return;
        Schedule(() =>
        {
            try { GraphExecutor.FireFrom(Graph, _sink, node, ok ? 0 : 2, vars, ok ? new[] { (1, value) } : null); }
            catch (Exception ex) { Manager.App.Log("[" + Id + "] plugin error: " + ex.Message, LogLevel.Error); }
        });
    }

    /// <summary>A panel interaction (button click / slider change / input submit) -> fire the plugin's ui.on flows,
    /// exactly like a node-authored UI window does via ServerConn.OnUiEvent.</summary>
    public void DeliverUiEvent(string windowId, UiKit.UiEvent ev)
    {
        var v = BaseVars();
        v["window"] = windowId;
        v["ui_event"] = ev.Type; v["ui_id"] = ev.Id; v["ui_value"] = ev.Value;
        if (ev.Fields != null) foreach (var kv in ev.Fields) v["ui_field_" + kv.Key] = kv.Value;
        FireFamily("ui", v);
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

    /// <summary>An Emit Plugin Event reached this plugin: fire its On Plugin Event triggers with the channel + payload.</summary>
    public void OnBus(string channel, string payload)
    {
        var v = BaseVars();
        v["bus_channel"] = channel; v["bus_payload"] = payload;
        FireFamily("bus", v);
    }

    // ---- the plugin's own persistent key/value store: a small JSON file under plugins/data, lazy-loaded ----
    private Dictionary<string, string>? _store;
    private string StoreFile => Path.Combine(PluginManager.Dir, "data", PluginManager.SafeName(Id) + ".json");
    private Dictionary<string, string> StoreMap()
    {
        if (_store != null) return _store;
        try { _store = File.Exists(StoreFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StoreFile)) ?? new() : new(); }
        catch { _store = new(); }
        return _store;
    }
    private void StoreSave()
    { try { Directory.CreateDirectory(Path.GetDirectoryName(StoreFile)!); File.WriteAllText(StoreFile, JsonSerializer.Serialize(_store)); } catch { /* best effort */ } }

    /// <summary>Plugin-private persistent store: get (the value) | set | delete | list (comma-joined keys) | clear.
    /// Runs on the plugin's single worker thread, so no extra locking needed.</summary>
    public string Store(string op, string key, string value)
    {
        var m = StoreMap();
        switch (op)
        {
            case "set": m[key] = value; StoreSave(); return value;
            case "delete": m.Remove(key); StoreSave(); return "";
            case "clear": m.Clear(); StoreSave(); return "";
            case "list": return string.Join(",", m.Keys);
            default: return m.TryGetValue(key, out var v) ? v : "";   // get
        }
    }

    private void FireFamily(string family, Dictionary<string, string> vars) => Schedule(() =>
    {
        // run every trigger node of this family (each filters itself), like ServerConn.FireFamily
        foreach (var n in Graph.Nodes.Where(n => n.Def.TriggerEvent == family).ToList())
        {
            try { GraphExecutor.Fire(Graph, _sink, n, new Dictionary<string, string>(vars)); }
            catch (Exception ex) { Manager.App.Log("[" + Id + "] plugin error: " + ex.Message, LogLevel.Error); }
        }
    });
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
    private readonly bool _threaded;

    /// <summary>Guards every plugin scene (read by the UI while rendering, written by plugin workers). Held briefly.</summary>
    public readonly object SceneGate = new();

    /// <summary>Persisted record of which plugins the user approved (and at what permissions) + their on/off state.
    /// A plugin only auto-loads if it's here with matching permissions - dropping a file into the dir doesn't run it.</summary>
    public sealed class RegEntry { public bool Enabled { get; set; } = true; public List<string> Permissions { get; set; } = new(); }
    private Dictionary<string, RegEntry> _registry = new();
    private static string RegistryPath => Path.Combine(Dir, "registry.json");
    private void LoadRegistry()
    { try { if (File.Exists(RegistryPath)) _registry = JsonSerializer.Deserialize<Dictionary<string, RegEntry>>(File.ReadAllText(RegistryPath)) ?? new(); } catch { _registry = new(); } }
    private void SaveRegistry()
    { try { Directory.CreateDirectory(Dir); File.WriteAllText(RegistryPath, JsonSerializer.Serialize(_registry, new JsonSerializerOptions { WriteIndented = true })); } catch { /* best effort */ } }

    // persisted plugin settings: pluginName -> (settingKey -> value). Loaded into each host's state on enable so a
    // plugin's {tokens} resolve to what the user configured in its settings modal.
    private Dictionary<string, Dictionary<string, string>> _configs = new();
    private static string ConfigPath => Path.Combine(Dir, "config.json");
    private void LoadConfigs()
    { try { if (File.Exists(ConfigPath)) _configs = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(ConfigPath)) ?? new(); } catch { _configs = new(); } }
    private void SaveConfigs()
    { try { Directory.CreateDirectory(Dir); File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_configs, new JsonSerializerOptions { WriteIndented = true })); } catch { /* best effort */ } }

    /// <summary>The settings form a plugin declared (empty if none / disabled).</summary>
    public IReadOnlyList<SettingsField> SettingsSchema(string pluginId)
    {
        PluginHost? h; lock (_gate) _hosts.TryGetValue(pluginId, out h);
        if (h == null) return new List<SettingsField>();
        lock (h.SettingsFields) return h.SettingsFields.ToList();
    }

    /// <summary>The current saved value of one of a plugin's settings ("" if unset).</summary>
    public string GetConfig(string pluginId, string key)
    { lock (_gate) return _configs.TryGetValue(pluginId, out var d) && d.TryGetValue(key, out var v) ? v : ""; }

    /// <summary>Save one of a plugin's settings: persist it + push it into the running plugin's state.</summary>
    public void SetConfig(string pluginId, string key, string value)
    {
        lock (_gate) { if (!_configs.TryGetValue(pluginId, out var d)) { d = new(); _configs[pluginId] = d; } d[key] = value; }
        SaveConfigs();
        PluginHost? h; lock (_gate) _hosts.TryGetValue(pluginId, out h);
        h?.ApplyConfig(key, value);
    }

    /// <summary><paramref name="threaded"/> runs each plugin's flows on its own worker thread (the app); tests pass
    /// false to keep flows synchronous + inline.</summary>
    public PluginManager(IAppHost app, bool threaded = false) { App = app; _threaded = threaded; }

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

    /// <summary>Broadcast an in-app event (app.bus) to every running plugin's On Plugin Event trigger - sender
    /// included, so a plugin can talk to its own other flows too.</summary>
    public void Emit(string fromId, string channel, string payload)
    {
        List<PluginHost> hosts; lock (_gate) hosts = _hosts.Values.ToList();
        foreach (var h in hosts) h.OnBus(channel, payload);
    }

    /// <summary>The app answered a prompt/pick/file gate (value + ok), or cancelled - resume the plugin's flow.</summary>
    public void ResolveInput(string pluginId, string nodeId, bool ok, string value)
    {
        PluginHost? h; lock (_gate) _hosts.TryGetValue(pluginId, out h);
        h?.ResolveInput(nodeId, ok, value);
    }

    /// <summary>(Re)build a panel's contents: fire its On App Event (event=panel, id=panelId) so the plugin's ui.*
    /// nodes repopulate the scene.</summary>
    public void BuildPanel(string pluginId, string panelId)
    {
        PluginHost? h; lock (_gate) _hosts.TryGetValue(pluginId, out h);
        h?.Activate("panel", panelId);
    }

    /// <summary>(Re)build a panel, passing its current content size so the plugin can lay out responsively
    /// (the build flow gets {app_panel_w} / {app_panel_h}).</summary>
    public void BuildPanel(string pluginId, string panelId, int w, int h)
    {
        PluginHost? host; lock (_gate) _hosts.TryGetValue(pluginId, out host);
        host?.Activate("panel", panelId, new Dictionary<string, string> { ["app_panel_w"] = w.ToString(), ["app_panel_h"] = h.ToString() });
    }

    /// <summary>The live scene a plugin built for one of its panels (null until built / if disabled).</summary>
    public Ircuitry.UiKit.UiScene? PanelScene(string pluginId, string windowId)
    {
        PluginHost? h; lock (_gate) _hosts.TryGetValue(pluginId, out h);
        return h?.SceneFor(windowId);
    }

    /// <summary>Route a panel interaction back to its plugin (fires ui.on).</summary>
    public void PanelEvent(string pluginId, string windowId, Ircuitry.UiKit.UiEvent ev)
    {
        PluginHost? h; lock (_gate) _hosts.TryGetValue(pluginId, out h);
        h?.DeliverUiEvent(windowId, ev);
    }

    /// <summary>The user answered a plugin's app.confirm modal: resume its confirmed/cancelled branch.</summary>
    public void ResolveConfirm(string pluginId, string nodeId, bool yes)
    {
        PluginHost? h; lock (_gate) _hosts.TryGetValue(pluginId, out h);
        h?.ResolveConfirm(nodeId, yes);
    }

    /// <summary>Block until a plugin's worker has drained (tests only).</summary>
    public bool WaitIdle(string pluginId, int ms)
    {
        PluginHost? h; lock (_gate) _hosts.TryGetValue(pluginId, out h);
        return h?.WaitIdle(ms) ?? true;
    }

    /// <summary>Installed plugins that are present but not approved (or whose permissions changed) - they don't run
    /// until the user reviews + approves them.</summary>
    public IReadOnlyList<PluginMeta> PendingPlugins { get { lock (_gate) return _plugins.Where(p => p.Pending).ToList(); } }

    /// <summary>A plugin is approval-compatible only if it asks for nothing beyond what was approved (asking for the
    /// same or fewer permissions is fine; any new one means re-review).</summary>
    public static bool PermsApproved(IEnumerable<string> wanted, IEnumerable<string> approved)
        => !wanted.Except(approved, StringComparer.Ordinal).Any();

    /// <summary>Start a plugin (register its chrome). <paramref name="persist"/> records approval at the plugin's
    /// current permissions - the consent that lets it auto-load next time. Loading already-approved plugins passes
    /// false (the registry already has them).</summary>
    public void Enable(PluginMeta p, bool persist = true)
    {
        lock (_gate) { if (_hosts.ContainsKey(p.Name)) return; }
        var host = new PluginHost(this, p.Name, p.Graph, _threaded);
        lock (_gate) _hosts[p.Name] = host;
        p.Enabled = true; p.Pending = false;
        if (persist) { _registry[p.Name] = new RegEntry { Enabled = true, Permissions = p.Permissions.ToList() }; SaveRegistry(); }
        // seed saved settings into the plugin's state so its {tokens} resolve to what the user configured, before it runs
        Dictionary<string, string>? cfg; lock (_gate) _configs.TryGetValue(p.Name, out cfg);
        if (cfg != null) foreach (var kv in cfg) host.ApplyConfig(kv.Key, kv.Value);
        host.Start();   // register contributions
    }

    public void Disable(PluginMeta p)
    {
        PluginHost? h;
        lock (_gate) { _hosts.Remove(p.Name, out h); _contribs.RemoveAll(c => c.PluginId == p.Name); }
        h?.Dispose();   // stop its worker thread
        p.Enabled = false;
        // persist the off state, keeping the approved permissions so it stays approved-but-off across restarts
        var perms = _registry.TryGetValue(p.Name, out var e) ? e.Permissions : p.Permissions.ToList();
        _registry[p.Name] = new RegEntry { Enabled = false, Permissions = perms };
        SaveRegistry();
    }

    /// <summary>Load installed plugins (called at app start). Only plugins recorded as approved (with no new
    /// permissions since approval) auto-enable; anything else loads as Pending - present but inert until reviewed.</summary>
    public void LoadInstalled()
    {
        List<PluginHost> old;
        lock (_gate) { old = _hosts.Values.ToList(); _plugins.Clear(); _hosts.Clear(); _contribs.Clear(); }
        foreach (var h in old) h.Dispose();
        LoadRegistry();
        LoadConfigs();
        string dir = Dir;
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir, "*.ircplugin"))
        {
            try
            {
                var m = PluginBundle.Load(File.ReadAllText(f));
                m.Path = f;
                // approved only if recorded AND it asks for nothing beyond what was approved (a swapped-in file that
                // wants more permissions is treated as untrusted and held for review).
                bool approved = _registry.TryGetValue(m.Name, out var e) && PermsApproved(m.Permissions, e.Permissions);
                m.Pending = !approved;
                lock (_gate) _plugins.Add(m);
                if (approved && e!.Enabled) Enable(m, persist: false);   // trusted + on -> run it
            }
            catch (Exception ex) { App.Log("plugin load failed (" + Path.GetFileName(f) + "): " + ex.Message, LogLevel.Error); }
        }
    }

    /// <summary>Install/approve a plugin from its bundle JSON: write it to the plugins dir, record approval, enable it.
    /// This is the consent step (reached only from the trust card).</summary>
    public PluginMeta Install(string json)
    {
        var m = PluginBundle.Load(json);
        Directory.CreateDirectory(Dir);
        m.Path = Path.Combine(Dir, SafeName(m.Name) + ".ircplugin");
        File.WriteAllText(m.Path, json);
        lock (_gate) { _plugins.RemoveAll(x => x.Name == m.Name); _plugins.Add(m); }
        PluginHost? stale; lock (_gate) _hosts.Remove(m.Name, out stale);
        stale?.Dispose();                            // clear any stale host/contribs for a same-named reinstall
        lock (_gate) _contribs.RemoveAll(c => c.PluginId == m.Name);
        Enable(m);                                   // records approval at the current permissions
        return m;
    }

    public void Uninstall(PluginMeta p)
    {
        Disable(p);
        try { if (File.Exists(p.Path)) File.Delete(p.Path); } catch { /* best effort */ }
        lock (_gate) _plugins.RemoveAll(x => x.Name == p.Name);
        _registry.Remove(p.Name); SaveRegistry();    // forget the approval too
    }

    internal static string SafeName(string s)
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
                case "app.settings": case "app.opensettings": perms.Add("settings"); break;
                case "app.store": perms.Add("storage"); break;
                case "app.bus": case "app.onbus": perms.Add("events"); break;
                case "app.selection": perms.Add("editor"); break;
                case "app.open": perms.Add("open-external"); break;
                case "app.clipboard": perms.Add("clipboard"); break;
                case "app.notify": perms.Add("notify"); break;
                case "app.prompt": case "app.pick": perms.Add("dialogs"); break;
                case "app.file": perms.Add("files"); break;
                case "app.status": perms.Add("statusbar"); break;
                case "ai.editor": perms.Add("edit-graph"); break;   // gives the AI the workflow-editing tools
                default:
                    if (n.TypeId.StartsWith("app.graph", StringComparison.Ordinal)) perms.Add("edit-graph");
                    else if (n.TypeId.StartsWith("container.", StringComparison.Ordinal) || n.TypeId == "code.run") perms.Add("run-commands");
                    else if (n.TypeId == "net.http" || n.TypeId.StartsWith("socket.", StringComparison.Ordinal) || n.TypeId == "ai.reply" || n.TypeId == "ai.programmer" || n.TypeId == "ai.mcp") perms.Add("network");
                    break;
            }
        }
        return perms.OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    /// <summary>True if a graph is plugin-shaped (uses at least one app.* hook) - gates "Bundle as Plugin".</summary>
    public static bool LooksLikePlugin(NodeGraph g) =>
        g.Nodes.Any(n => n.TypeId == "app.start" || n.TypeId.StartsWith("app.", StringComparison.Ordinal));
}
