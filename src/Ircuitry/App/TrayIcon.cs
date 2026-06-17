using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

    /// <summary>Tell the tray host the menu changed so it re-fetches (servers connect/disconnect, bots added).</summary>
    public static void MenuChanged() { try { _menu?.EmitLayoutUpdated(); } catch { } }

    private static (int r, int g, int b) _lastStatus = (-1, -1, -1);
    /// <summary>The living orb: tint the tray icon by the bots' aggregate status (idle / connecting / live / alert).
    /// Re-renders the pixmap and signals the host only when the colour actually changes, so the bus stays quiet.</summary>
    public static void SetStatus(int r, int g, int b)
    {
        if (_lastStatus == (r, g, b)) return;
        _lastStatus = (r, g, b);
        try { _item?.SetIcon(TrayIconArt.Orbs(r, g, b)); } catch { }
    }
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
    public string IconName = "";             // empty -> the host uses our dynamic IconPixmap (the living orb)
    public (int, int, byte[])[] IconPixmap = Array.Empty<(int, int, byte[])>();
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
    private readonly object _gate = new();
    private readonly List<Action> _newIcon = new();   // clients subscribed to NewIcon; invoking one emits the signal

    public StatusNotifierItem() { _p.IconPixmap = TrayIconArt.Orbs(176, 162, 132); }   // start as a calm idle orb

    public ObjectPath ObjectPath => new("/StatusNotifierItem");

    /// <summary>Swap the icon pixmap and tell every subscribed host to re-fetch it.</summary>
    public void SetIcon((int, int, byte[])[] px)
    {
        _p.IconPixmap = px;
        Action[] hs; lock (_gate) hs = _newIcon.ToArray();
        foreach (var h in hs) try { h(); } catch { }
    }

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
        "IconPixmap" => _p.IconPixmap,
        "ItemIsMenu" => _p.ItemIsMenu,
        "Menu" => _p.Menu,
        _ => "",
    });
    public Task<SniProperties> GetAllAsync() => Task.FromResult(_p);
    public Task SetAsync(string prop, object val) => Task.CompletedTask;

    private static readonly IDisposable Noop = new NoopDisposable();
    public Task<IDisposable> WatchNewTitleAsync(Action h) => Task.FromResult(Noop);
    public Task<IDisposable> WatchNewIconAsync(Action h)
    {
        lock (_gate) _newIcon.Add(h);
        return Task.FromResult<IDisposable>(new Unsub(this, h));
    }
    public Task<IDisposable> WatchNewAttentionIconAsync(Action h) => Task.FromResult(Noop);
    public Task<IDisposable> WatchNewOverlayIconAsync(Action h) => Task.FromResult(Noop);
    public Task<IDisposable> WatchNewToolTipAsync(Action h) => Task.FromResult(Noop);
    public Task<IDisposable> WatchNewStatusAsync(Action<string> h) => Task.FromResult(Noop);

    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    private sealed class Unsub : IDisposable
    {
        private readonly StatusNotifierItem _o; private readonly Action _h;
        public Unsub(StatusNotifierItem o, Action h) { _o = o; _h = h; }
        public void Dispose() { lock (_o._gate) _o._newIcon.Remove(_h); }
    }
}

/// <summary>Renders the cozy "living orb" tray icon in code (a soft glowing dot tinted by status), as
/// StatusNotifierItem pixmaps: ARGB32 in network byte order, one entry per size so the host picks the best fit.</summary>
public static class TrayIconArt
{
    public static (int, int, byte[])[] Orbs(int r, int g, int b) =>
        new[] { Orb(22, r, g, b), Orb(32, r, g, b), Orb(48, r, g, b) };

    private static (int, int, byte[]) Orb(int n, int cr, int cg, int cb)
    {
        var data = new byte[n * n * 4];
        float c = (n - 1) / 2f, radius = n * 0.40f;
        float hx = c - radius * 0.34f, hy = c - radius * 0.34f;   // glossy highlight centre (top-left)
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float d = MathF.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                float core = d <= radius ? 1f : Math.Max(0f, 1f - (d - radius) / 1.6f);       // disc + AA edge
                float halo = d > radius ? Math.Max(0f, 0.32f * (1f - (d - radius) / (radius * 0.55f))) : 0f;
                float a = Math.Clamp(Math.Max(core, halo), 0f, 1f);

                float t = Math.Clamp(1f - d / radius, 0f, 1f);                                  // centre brighter
                float hl = Math.Max(0f, 1f - MathF.Sqrt((x - hx) * (x - hx) + (y - hy) * (y - hy)) / (radius * 0.9f));
                float light = 0.12f * t + 0.55f * hl * hl;                                      // soft gloss
                int R = Mix(cr, light), G = Mix(cg, light), B = Mix(cb, light);

                int i = (y * n + x) * 4;
                data[i + 0] = (byte)Math.Clamp(a * 255f, 0, 255);   // A  (ARGB, network/big-endian byte order)
                data[i + 1] = (byte)R;
                data[i + 2] = (byte)G;
                data[i + 3] = (byte)B;
            }
        return (n, n, data);
    }

    private static int Mix(int channel, float towardWhite) => (int)Math.Clamp(channel + (255 - channel) * towardWhite, 0, 255);
}
