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
    /// <summary>True if the arg is one of our scheme links (community install or an irc/ircs server link).</summary>
    public static bool Is(string arg) =>
        arg.StartsWith("ircuitry://", StringComparison.OrdinalIgnoreCase) ||
        arg.StartsWith("ircbot://", StringComparison.OrdinalIgnoreCase) ||
        IsServerLink(arg);

    /// <summary>True for an <c>irc://</c> / <c>ircs://</c> link, which we treat as "save this server".</summary>
    public static bool IsServerLink(string arg) =>
        arg.StartsWith("ircs://", StringComparison.OrdinalIgnoreCase) ||
        arg.StartsWith("irc://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parse an <c>irc://host[:port]/[#]chan[,chan]</c> link. <c>ircs://</c> means TLS (default 6697);
    /// <c>irc://</c> means plaintext (default 6667). Parsed by hand (not System.Uri) because a channel's
    /// leading '#' would otherwise be read as a URL fragment.
    /// </summary>
    public static bool TryParseServer(string link, out string host, out int port, out bool tls, out string channels)
    {
        host = ""; port = 6697; tls = true; channels = "";
        try
        {
            int sep = link.IndexOf("://", StringComparison.Ordinal);
            if (sep < 0) return false;
            string scheme = link[..sep].ToLowerInvariant();
            if (scheme is not ("irc" or "ircs")) return false;
            tls = scheme == "ircs";
            port = tls ? 6697 : 6667;

            string rest = link[(sep + 3)..];
            int slash = rest.IndexOf('/');
            string authority = slash < 0 ? rest : rest[..slash];
            string path = slash < 0 ? "" : rest[(slash + 1)..];

            // host[:port]  (ignore any user@ prefix)
            int at = authority.IndexOf('@'); if (at >= 0) authority = authority[(at + 1)..];
            int colon = authority.LastIndexOf(':');
            if (colon > 0 && int.TryParse(authority[(colon + 1)..], out var pp) && pp is > 0 and <= 65535) { host = authority[..colon]; port = pp; }
            else host = authority;
            if (host.Length == 0) return false;

            // channels: comma-separated path items, '#' added if missing; known IRC-URL flags dropped
            var flags = new[] { "isnick", "ischannel", "needkey", "needpass", "ispnick" };
            var chans = new System.Collections.Generic.List<string>();
            foreach (var raw in Uri.UnescapeDataString(path).Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var t = raw.Trim();
                if (t.Length == 0 || Array.IndexOf(flags, t.ToLowerInvariant()) >= 0) continue;
                chans.Add(t.StartsWith('#') || t.StartsWith('&') ? t : "#" + t);
            }
            channels = string.Join(' ', chans);
            return true;
        }
        catch { return false; }
    }

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
                "MimeType=x-scheme-handler/ircuitry;x-scheme-handler/ircbot;x-scheme-handler/irc;x-scheme-handler/ircs;\n";
            if (!File.Exists(desktop) || File.ReadAllText(desktop) != content)
                File.WriteAllText(desktop, content);

            RunQuiet("xdg-mime", "default ircuitry-url.desktop x-scheme-handler/ircuitry");
            RunQuiet("xdg-mime", "default ircuitry-url.desktop x-scheme-handler/ircbot");
            RunQuiet("xdg-mime", "default ircuitry-url.desktop x-scheme-handler/irc");
            RunQuiet("xdg-mime", "default ircuitry-url.desktop x-scheme-handler/ircs");
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

    /// <summary>Open a URL in the user's browser.</summary>
    public static void OpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsLinux()) Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
            else if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start(new ProcessStartInfo("open", url) { UseShellExecute = false });
        }
        catch { /* no browser - nothing to do */ }
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
