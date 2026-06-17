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

    /// <summary>Parse <c>scheme://action?url=...</c> (a hosted file) or <c>?data=&lt;base64&gt;</c> (an inline
    /// workflow, e.g. a bot merged in the browser) into the action (install-node/install-bot) and its payload.</summary>
    public static bool TryParse(string link, out string action, out string url, out string data)
    {
        action = ""; url = ""; data = "";
        try
        {
            var u = new Uri(link);
            if (!u.Scheme.Equals("ircuitry", StringComparison.OrdinalIgnoreCase) &&
                !u.Scheme.Equals("ircbot", StringComparison.OrdinalIgnoreCase)) return false;
            action = u.Host.ToLowerInvariant();
            foreach (var kv in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int i = kv.IndexOf('=');
                if (i <= 0) continue;
                var key = Uri.UnescapeDataString(kv[..i]);
                if (key == "url") url = Uri.UnescapeDataString(kv[(i + 1)..]);
                else if (key == "data") data = Uri.UnescapeDataString(kv[(i + 1)..]);
            }
            return action.Length > 0 && (url.Length > 0 || data.Length > 0);
        }
        catch { return false; }
    }

    /// <summary>Decode an inline <c>data=</c> payload (standard base64 of the UTF-8 JSON). Returns "" on failure.
    /// This carries no SSRF risk - nothing is fetched - and the install is still gated by a confirm dialog.</summary>
    public static string DecodeData(string data)
    {
        try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(data.Trim())); }
        catch { return ""; }
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
    /// Register the scheme so the OS routes ircuitry:// links to this app. Linux installs a self-contained
    /// .desktop handler; Windows writes a per-user HKCU\Software\Classes entry (via reg.exe); macOS declares
    /// the scheme in its .app Info.plist (see tools/package-macos.sh) and we nudge LaunchServices. Best-effort
    /// and idempotent on every platform.
    /// </summary>
    public static void Register()
    {
        try
        {
            if (OperatingSystem.IsLinux()) RegisterLinux();
            else if (OperatingSystem.IsWindows()) RegisterWindows();
            else if (OperatingSystem.IsMacOS()) RegisterMac();
        }
        catch { /* registration is best-effort */ }
    }

    private static void RegisterLinux()
    {
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

    // Windows: a per-user protocol handler under HKCU\Software\Classes (no admin needed), written via reg.exe so
    // we don't pull in the Microsoft.Win32.Registry package. Points ircuitry:// / ircbot:// at this exe.
    private static void RegisterWindows()
    {
        string exe = Environment.ProcessPath ?? "";
        if (exe.Length == 0 || Path.GetFileName(exe).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return;  // skip a dev `dotnet` host
        foreach (var scheme in new[] { "ircuitry", "ircbot" })
        {
            string key = @"HKCU\Software\Classes\" + scheme;
            Reg("add", key, "/ve", "/d", "URL:" + scheme + " Protocol", "/f");
            Reg("add", key, "/v", "URL Protocol", "/d", "", "/f");
            Reg("add", key + @"\shell\open\command", "/ve", "/d", "\"" + exe + "\" \"%1\"", "/f");
        }
    }

    private static void Reg(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("reg") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);   // ArgumentList escapes quotes/spaces for us
            Process.Start(psi)?.WaitForExit(3000);
        }
        catch { /* reg unavailable - best effort */ }
    }

    // macOS: the .app bundle's Info.plist declares the ircuitry/ircbot URL schemes (tools/package-macos.sh).
    // LaunchServices registers them when the app is discovered; nudge it to (re)scan this bundle so it works
    // straight away (e.g. run from Downloads before being moved to /Applications).
    private static void RegisterMac()
    {
        string exe = Environment.ProcessPath ?? "";
        string? app = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(exe)));   // <app>.app/Contents/MacOS/<exe>
        if (app == null || !app.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return;     // not a bundle -> nothing to register
        RunQuiet("/System/Library/Frameworks/CoreServices.framework/Versions/A/Frameworks/LaunchServices.framework/Versions/A/Support/lsregister", "-f \"" + app + "\"");
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
