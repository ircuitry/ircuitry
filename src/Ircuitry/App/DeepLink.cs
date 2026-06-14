using System;
using System.Diagnostics;
using System.IO;

namespace Ircuitry.App;

/// <summary>
/// Custom URI scheme support so the website can install community content with one click:
/// <c>ircuitry://install-node?url=&lt;https&gt;</c> and <c>ircuitry://install-bot?url=&lt;https&gt;</c>
/// (the <c>ircbot://</c> scheme is accepted as an alias). The link names a file to fetch; the app
/// downloads it and shows the same confirm dialog as a manual install, so it is always two clicks:
/// one on the page, one in the app.
/// </summary>
public static class DeepLink
{
    /// <summary>True if the arg is one of our scheme links.</summary>
    public static bool Is(string arg) =>
        arg.StartsWith("ircuitry://", StringComparison.OrdinalIgnoreCase) ||
        arg.StartsWith("ircbot://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parse <c>scheme://action?url=...</c> into the action (install-node/install-bot) and target url.</summary>
    public static bool TryParse(string link, out string action, out string url)
    {
        action = ""; url = "";
        try
        {
            var u = new Uri(link);
            if (!u.Scheme.Equals("ircuitry", StringComparison.OrdinalIgnoreCase) &&
                !u.Scheme.Equals("ircbot", StringComparison.OrdinalIgnoreCase)) return false;
            action = u.Host.ToLowerInvariant();
            foreach (var kv in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int i = kv.IndexOf('=');
                if (i > 0 && Uri.UnescapeDataString(kv[..i]) == "url") url = Uri.UnescapeDataString(kv[(i + 1)..]);
            }
            return action.Length > 0 && url.Length > 0;
        }
        catch { return false; }
    }

    /// <summary>Only fetch from the ircuitry community repos over https (no arbitrary SSRF).</summary>
    public static bool IsAllowedUrl(string url)
    {
        try
        {
            var u = new Uri(url);
            return u.Scheme == "https"
                && (u.Host == "raw.githubusercontent.com" || u.Host == "github.com")
                && u.AbsolutePath.StartsWith("/ircuitry/", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Register the scheme so the OS routes links to this app. Linux only here (a self-installing
    /// .desktop handler that also covers the AppImage and portable zip); the .deb ships one too, and
    /// Windows/macOS register via their installer/bundle. Best-effort and idempotent.
    /// </summary>
    public static void Register()
    {
        if (!OperatingSystem.IsLinux()) return;
        try
        {
            string exec = ResolveExec();
            if (exec.Length == 0) return;

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string appsDir = Path.Combine(home, ".local", "share", "applications");
            Directory.CreateDirectory(appsDir);
            string desktop = Path.Combine(appsDir, "ircuitry-url.desktop");
            string content =
                "[Desktop Entry]\n" +
                "Type=Application\n" +
                "Name=ircuitry\n" +
                "Exec=" + exec + "\n" +   // ResolveExec includes quoting and the %u placeholder
                "Icon=ircuitry\n" +
                "Terminal=false\n" +
                "NoDisplay=true\n" +
                "MimeType=x-scheme-handler/ircuitry;x-scheme-handler/ircbot;\n";
            if (!File.Exists(desktop) || File.ReadAllText(desktop) != content)
                File.WriteAllText(desktop, content);

            RunQuiet("xdg-mime", "default ircuitry-url.desktop x-scheme-handler/ircuitry");
            RunQuiet("xdg-mime", "default ircuitry-url.desktop x-scheme-handler/ircbot");
            RunQuiet("update-desktop-database", appsDir);
        }
        catch { /* registration is best-effort */ }
    }

    // Build the .desktop Exec line, choosing a launcher that the OS can actually run.
    private static string ResolveExec()
    {
        string appImage = Environment.GetEnvironmentVariable("APPIMAGE") ?? "";
        if (appImage.Length > 0) return Q(appImage) + " %u";   // AppImage: self-contained, runs directly

        string proc = Environment.ProcessPath ?? "";
        string procName = Path.GetFileName(proc);
        bool selfContained = File.Exists(Path.Combine(AppContext.BaseDirectory, "libcoreclr.so"))
                          || File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll"));

        // A framework-dependent apphost (e.g. a dev build) cannot find the .NET runtime when the
        // desktop launches it with a bare environment, so route through `dotnet <App.dll>` instead.
        // A self-contained apphost (the released builds) bundles the runtime and runs directly.
        bool viaDotnet = procName is "dotnet" or "dotnet.exe" || (!selfContained && proc.Length > 0);
        if (viaDotnet)
        {
            string dll = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "";
            string dotnet = procName is "dotnet" or "dotnet.exe" ? proc : FindDotnet();
            if (dll.Length > 0 && dotnet.Length > 0) return Q(dotnet) + " " + Q(dll) + " %u";
        }
        return proc.Length > 0 ? Q(proc) + " %u" : "";
    }

    private static string Q(string s) => "\"" + s + "\"";

    private static string FindDotnet()
    {
        var cands = new System.Collections.Generic.List<string>();
        string? root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(root)) cands.Add(Path.Combine(root, "dotnet"));
        cands.Add("/usr/bin/dotnet");
        cands.Add("/usr/share/dotnet/dotnet");
        cands.Add("/usr/local/share/dotnet/dotnet");
        cands.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet"));
        foreach (var c in cands) if (File.Exists(c)) return c;
        return "";
    }

    private static void RunQuiet(string file, string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(file, args)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            p?.WaitForExit(3000);
        }
        catch { /* tool missing - nothing to do */ }
    }
}
