using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tmds.DBus;

namespace Ircuitry.App;

// ===================================================================
//  A right-click tray menu via com.canonical.dbusmenu (the protocol KDE / the GNOME AppIndicator
//  extension / Zorin etc. render natively next to the clock). Built fresh from a snapshot of the bots
//  the game loop pushes in, so it always shows the current connect/disconnect state. Clicks are pushed
//  back as TrayCommands the game loop drains - the menu never touches app state on the D-Bus thread.
//  Everything is best-effort: with no tray host (or no D-Bus) registration silently no-ops.
// ===================================================================

/// <summary>An action chosen from the tray menu. Bot "*" = every bot; Server "*" = every server of that bot.</summary>
public readonly record struct TrayCommand(string Kind, string Bot, string Server);

public sealed class TrayServerInfo { public string Label = ""; public bool Online; }
public sealed class TrayBotInfo { public string Name = ""; public List<TrayServerInfo> Servers = new(); }
public sealed class TrayMenuModel { public List<TrayBotInfo> Bots = new(); }

[DBusInterface("com.canonical.dbusmenu")]
public interface IDbusMenu : IDBusObject
{
    Task<(uint revision, (int, IDictionary<string, object>, object[]) layout)> GetLayoutAsync(int parentId, int recursionDepth, string[] propertyNames);
    Task<(int, IDictionary<string, object>)[]> GetGroupPropertiesAsync(int[] ids, string[] propertyNames);
    Task<object> GetPropertyAsync(int id, string name);
    Task EventAsync(int id, string eventId, object data, uint timestamp);
    Task<int[]> EventGroupAsync((int, string, object, uint)[] events);
    Task<bool> AboutToShowAsync(int id);
    Task<(int[] updatesNeeded, int[] idErrors)> AboutToShowGroupAsync(int[] ids);

    Task<object> GetAsync(string prop);
    Task<DbusMenuProperties> GetAllAsync();
    Task SetAsync(string prop, object val);

    Task<IDisposable> WatchItemsPropertiesUpdatedAsync(Action<((int, IDictionary<string, object>)[], (int, string[])[])> handler);
    Task<IDisposable> WatchLayoutUpdatedAsync(Action<(uint, int)> handler);
    Task<IDisposable> WatchItemActivationRequestedAsync(Action<(int, uint)> handler);
}

[Dictionary]
public class DbusMenuProperties
{
    public uint Version = 4;
    public string Status = "normal";
    public string TextDirection = "ltr";
    public string[] IconThemePath = Array.Empty<string>();
}

public sealed class TrayMenu : IDbusMenu
{
    public ObjectPath ObjectPath => new("/MenuBar");

    private readonly DbusMenuProperties _p = new();
    private readonly object _lock = new();
    private uint _revision = 1;

    // the last-built tree: id -> (properties, child ids); plus id -> command for clickable leaves
    private Dictionary<int, (IDictionary<string, object> props, List<int> kids)> _items = new();
    private Dictionary<int, TrayCommand> _actions = new();
    // STABLE ids: a logical item (e.g. "disconnect", "disconnect.bot.h4ks") always keeps the same numeric id
    // across rebuilds, even as bots connect/disconnect. Hosts cache ids and lazily fetch submenus by id, so a
    // renumbering after the model changes was making them fetch the wrong (empty) item - the empty-submenu bug.
    private readonly Dictionary<string, int> _ids = new();
    private int _idSeq = 1;   // 0 is reserved for the root
    private int IdFor(string key) { if (!_ids.TryGetValue(key, out var id)) { id = _idSeq++; _ids[key] = id; } return id; }

    // ---- tree building (from the pushed model) ----
    private void Rebuild()
    {
        var items = new Dictionary<int, (IDictionary<string, object>, List<int>)>();
        var actions = new Dictionary<int, TrayCommand>();

        int New(string key, IDictionary<string, object> props, List<int> kids) { int id = IdFor(key); items[id] = (props, kids); return id; }
        int Leaf(string key, string label, bool enabled, TrayCommand? cmd)
        {
            int id = New(key, Std(label, enabled), new List<int>());
            if (cmd is { } c) actions[id] = c; else actions.Remove(id);
            return id;
        }
        int Sep(string key) => New(key, new Dictionary<string, object> { ["type"] = "separator" }, new List<int>());
        int Sub(string key, string label, List<int> kids) => New(key, SubProps(label, kids.Count > 0), kids);

        var model = TrayIcon.Model;
        var bots = model.Bots;

        // One flat submenu level only (no per-bot sub-submenus). The GNOME AppIndicator host pre-fetches the
        // whole menu once and caches it; a single level (verified working over D-Bus) avoids any deep
        // lazy-loading quirks. Items stay always-enabled - a cache-once host would otherwise freeze a stale
        // online state; the action re-checks the live state at click time, so clicking is always safe.
        List<int> ActionGroup(string kind)
        {
            var top = new List<int>();
            if (bots.Count == 0) { top.Add(Leaf($"{kind}.nobots", "(no bots yet)", false, null)); return top; }
            bool many = bots.Count > 1;
            bool any = false;
            foreach (var b in bots)
                foreach (var sv in b.Servers)
                {
                    string lbl = many ? $"{b.Name} · {sv.Label}" : sv.Label;
                    top.Add(Leaf($"{kind}.bot.{b.Name}.sv.{sv.Label}", lbl, true, new TrayCommand(kind, b.Name, sv.Label)));
                    any = true;
                }
            if (any) { top.Add(Sep($"{kind}.sep")); top.Add(Leaf($"{kind}.all", "All servers", true, new TrayCommand(kind, "*", "*"))); }
            return top;
        }

        var root = new List<int>
        {
            Sub("disconnect", "Disconnect", ActionGroup("disconnect")),
            Sub("reconnect", "Reconnect", ActionGroup("reconnect")),
            Leaf("open", "Open ircuitry", true, new TrayCommand("open", "", "")),
            Sep("rootsep"),
            Leaf("exit", "Exit", true, new TrayCommand("exit", "", "")),
        };
        items[0] = (new Dictionary<string, object> { ["children-display"] = "submenu" }, root);   // root is id 0

        lock (_lock) { _items = items; _actions = actions; _revision++; }
    }

    private (int, IDictionary<string, object>, object[]) Build(int id, int depth)
    {
        if (!_items.TryGetValue(id, out var it)) return (id, new Dictionary<string, object>(), Array.Empty<object>());
        object[] kids;
        if (depth == 0) kids = Array.Empty<object>();
        else
        {
            kids = new object[it.kids.Count];
            for (int i = 0; i < it.kids.Count; i++) kids[i] = Build(it.kids[i], depth < 0 ? -1 : depth - 1);
        }
        return (id, it.props, kids);
    }

    private static IDictionary<string, object> Std(string label, bool enabled) =>
        new Dictionary<string, object> { ["label"] = label, ["enabled"] = enabled, ["visible"] = true };

    private static IDictionary<string, object> SubProps(string label, bool hasKids)
    {
        var d = new Dictionary<string, object> { ["label"] = label, ["enabled"] = true, ["visible"] = true };
        if (hasKids) d["children-display"] = "submenu";
        return d;
    }

    // ---- com.canonical.dbusmenu ----
    public Task<(uint revision, (int, IDictionary<string, object>, object[]) layout)> GetLayoutAsync(int parentId, int recursionDepth, string[] propertyNames)
    {
        Rebuild();
        // always return the full subtree (ignore the requested depth): some hosts ask for depth 1 and never
        // re-fetch deeper, which left submenus (the per-server lists) looking empty.
        return Task.FromResult((_revision, Build(parentId, -1)));
    }

    public Task<(int, IDictionary<string, object>)[]> GetGroupPropertiesAsync(int[] ids, string[] propertyNames)
    {
        var list = new List<(int, IDictionary<string, object>)>();
        lock (_lock)
            foreach (var id in ids)
                if (_items.TryGetValue(id, out var it)) list.Add((id, it.props));
        return Task.FromResult(list.ToArray());
    }

    public Task<object> GetPropertyAsync(int id, string name)
    {
        lock (_lock)
            if (_items.TryGetValue(id, out var it) && it.props.TryGetValue(name, out var v)) return Task.FromResult(v);
        return Task.FromResult<object>("");
    }

    public Task EventAsync(int id, string eventId, object data, uint timestamp)
    {
        if (eventId == "clicked")
            lock (_lock)
                if (_actions.TryGetValue(id, out var cmd)) TrayIcon.Commands.Enqueue(cmd);
        return Task.CompletedTask;
    }

    public Task<int[]> EventGroupAsync((int, string, object, uint)[] events)
    {
        foreach (var (id, ev, data, ts) in events) EventAsync(id, ev, data, ts);
        return Task.FromResult(Array.Empty<int>());
    }

    // Return false ("no update needed"): the menu is kept current via pre-population + the model push, so the
    // host should just display its already-fetched layout. Returning true made the host re-fetch GetLayout the
    // instant a submenu opened, tearing down and collapsing it (the expand-then-vanish flicker).
    public Task<bool> AboutToShowAsync(int id) => Task.FromResult(false);
    public Task<(int[] updatesNeeded, int[] idErrors)> AboutToShowGroupAsync(int[] ids)
        => Task.FromResult((Array.Empty<int>(), Array.Empty<int>()));

    public Task<object> GetAsync(string prop) => Task.FromResult<object>(prop switch
    {
        "Version" => _p.Version,
        "Status" => _p.Status,
        "TextDirection" => _p.TextDirection,
        "IconThemePath" => _p.IconThemePath,
        _ => "",
    });
    public Task<DbusMenuProperties> GetAllAsync() => Task.FromResult(_p);
    public Task SetAsync(string prop, object val) => Task.CompletedTask;

    private static readonly IDisposable Noop = new NoopDisposable();
    public Task<IDisposable> WatchItemsPropertiesUpdatedAsync(Action<((int, IDictionary<string, object>)[], (int, string[])[])> h) => Task.FromResult(Noop);
    public Task<IDisposable> WatchItemActivationRequestedAsync(Action<(int, uint)> h) => Task.FromResult(Noop);

    // LayoutUpdated: hosts (libdbusmenu / AppIndicator) cache the menu and only re-fetch GetLayout when this
    // signal fires. Without it the first (often empty) layout sticks and submenus never fill in. We keep the
    // subscriber handler and raise it whenever the bots/servers actually change.
    private Action<(uint, int)>? _layoutUpdated;
    public Task<IDisposable> WatchLayoutUpdatedAsync(Action<(uint, int)> h)
    {
        _layoutUpdated = h;
        return Task.FromResult<IDisposable>(new NoopDisposable());
    }

    public void EmitLayoutUpdated()
    {
        uint rev; lock (_lock) rev = ++_revision;
        try { _layoutUpdated?.Invoke((rev, 0)); } catch { /* no subscriber / bus busy - host will still re-fetch on open */ }
    }

    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
}
