using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using Tmds.DBus;

namespace Ircuitry.App;

/// <summary>
/// A Linux system-tray icon via the StatusNotifierItem (SNI) spec over D-Bus - the modern tray
/// protocol KDE/GNOME(AppIndicator)/Zorin use. Left-clicking the icon restores the window; right-click
/// shows a native menu (see <see cref="TrayMenu"/>) to connect/disconnect servers, open, or exit.
/// Everything is best-effort and guarded: if there's no tray host (or no D-Bus), it silently no-ops and
/// the app falls back to a normal minimise.
/// </summary>
public static class TrayIcon
{
    public static volatile bool Available;
    public static volatile bool RestoreRequested;   // host clears it after showing the window

    /// <summary>Snapshot of bots/servers the game loop keeps current, for building the right-click menu.</summary>
    public static volatile TrayMenuModel Model = new();
    /// <summary>Menu choices the game loop drains and executes on its own thread.</summary>
    public static readonly ConcurrentQueue<TrayCommand> Commands = new();

    private static Connection? _conn;
    private static StatusNotifierItem? _item;
    private static TrayMenu? _menu;

    public static void Start()
    {
        // run async on a background task so a slow/absent bus never blocks startup
        Task.Run(async () =>
        {
            try
            {
                InstallThemeIcon();
                _conn = new Connection(Address.Session ?? throw new InvalidOperationException("no session bus"));
                var info = await _conn.ConnectAsync();
                _menu = new TrayMenu();
                await _conn.RegisterObjectAsync(_menu);
                _item = new StatusNotifierItem();
                await _conn.RegisterObjectAsync(_item);
                var watcher = _conn.CreateProxy<IStatusNotifierWatcher>("org.kde.StatusNotifierWatcher", "/StatusNotifierWatcher");
                await watcher.RegisterStatusNotifierItemAsync(info.LocalName);
                Available = true;
            }
            catch { Available = false; }
        });
    }

    // place our icon where the tray host's icon-theme lookup (IconName="ircuitry") will find it
    private static void InstallThemeIcon()
    {
        var src = Path.Combine(AppContext.BaseDirectory, "assets", "icons");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (int s in new[] { 16, 24, 32, 48, 64, 128, 256 })
        {
            var from = Path.Combine(src, $"icon-{s}.png");
            if (!File.Exists(from)) continue;
            var dir = Path.Combine(home, ".local", "share", "icons", "hicolor", $"{s}x{s}", "apps");
            Directory.CreateDirectory(dir);
            try { File.Copy(from, Path.Combine(dir, "ircuitry.png"), true); } catch { }
        }
    }
}

[DBusInterface("org.kde.StatusNotifierWatcher")]
public interface IStatusNotifierWatcher : IDBusObject
{
    Task RegisterStatusNotifierItemAsync(string service);
}

[Dictionary]
public class SniProperties
{
    public string Category = "ApplicationStatus";
    public string Id = "ircuitry";
    public string Title = "ircuitry";
    public string Status = "Active";
    public string IconName = "ircuitry";
    public int WindowId = 0;
    public bool ItemIsMenu = false;          // left-click activates (restores), right-click shows Menu
    public ObjectPath Menu = new("/MenuBar");
}

[DBusInterface("org.kde.StatusNotifierItem")]
public interface IStatusNotifierItem : IDBusObject
{
    Task ContextMenuAsync(int x, int y);
    Task ActivateAsync(int x, int y);
    Task SecondaryActivateAsync(int x, int y);
    Task ScrollAsync(int delta, string orientation);
    Task<object> GetAsync(string prop);
    Task<SniProperties> GetAllAsync();
    Task SetAsync(string prop, object val);
    Task<IDisposable> WatchNewTitleAsync(Action handler);
    Task<IDisposable> WatchNewIconAsync(Action handler);
    Task<IDisposable> WatchNewAttentionIconAsync(Action handler);
    Task<IDisposable> WatchNewOverlayIconAsync(Action handler);
    Task<IDisposable> WatchNewToolTipAsync(Action handler);
    Task<IDisposable> WatchNewStatusAsync(Action<string> handler);
}

public sealed class StatusNotifierItem : IStatusNotifierItem
{
    private readonly SniProperties _p = new();
    public ObjectPath ObjectPath => new("/StatusNotifierItem");

    public Task ActivateAsync(int x, int y) { TrayIcon.RestoreRequested = true; return Task.CompletedTask; }
    public Task SecondaryActivateAsync(int x, int y) { TrayIcon.RestoreRequested = true; return Task.CompletedTask; }
    public Task ContextMenuAsync(int x, int y) => Task.CompletedTask;   // the host renders our Menu (dbusmenu)
    public Task ScrollAsync(int delta, string orientation) => Task.CompletedTask;

    public Task<object> GetAsync(string prop) => Task.FromResult<object>(prop switch
    {
        "Category" => _p.Category,
        "Id" => _p.Id,
        "Title" => _p.Title,
        "Status" => _p.Status,
        "IconName" => _p.IconName,
        "ItemIsMenu" => _p.ItemIsMenu,
        "Menu" => _p.Menu,
        _ => "",
    });
    public Task<SniProperties> GetAllAsync() => Task.FromResult(_p);
    public Task SetAsync(string prop, object val) => Task.CompletedTask;

    private static readonly IDisposable Noop = new NoopDisposable();
    public Task<IDisposable> WatchNewTitleAsync(Action h) => Task.FromResult(Noop);
    public Task<IDisposable> WatchNewIconAsync(Action h) => Task.FromResult(Noop);
    public Task<IDisposable> WatchNewAttentionIconAsync(Action h) => Task.FromResult(Noop);
    public Task<IDisposable> WatchNewOverlayIconAsync(Action h) => Task.FromResult(Noop);
    public Task<IDisposable> WatchNewToolTipAsync(Action h) => Task.FromResult(Noop);
    public Task<IDisposable> WatchNewStatusAsync(Action<string> h) => Task.FromResult(Noop);

    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
}
