using System;
using System.Diagnostics;

namespace Ircuitry.Core;

/// <summary>
/// Fires a native OS desktop notification, best-effort and cross-platform: notify-send on Linux,
/// osascript on macOS, a PowerShell toast on Windows. Returns false (never throws) when the platform
/// tooling isn't available, so callers can log a fallback.
/// </summary>
public static class Notifier
{
    /// <summary>Set false to suppress all desktop notifications (the self-test does this so a build never
    /// pops "heads up" / "approval needed" toasts on the dev's screen).</summary>
    public static bool Enabled = true;

    public static bool Send(string title, string body, string urgency = "normal")
    {
        if (!Enabled) return false;
        title = (title ?? "").Trim();
        if (title.Length == 0) title = "ircuitry";
        body ??= "";
        try
        {
            if (OperatingSystem.IsLinux())
                return RunQuiet("notify-send", "-a", "ircuitry", "-u", Norm(urgency), title, body);
            if (OperatingSystem.IsMacOS())
                return RunQuiet("osascript", "-e", $"display notification {Quote(body)} with title {Quote(title)}");
            if (OperatingSystem.IsWindows())
                return WindowsToast(title, body);
        }
        catch { /* tooling missing / blocked -> report unavailable */ }
        return false;
    }

    private static string Norm(string u) => u is "low" or "critical" ? u : "normal";

    private static bool RunQuiet(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        return p != null;   // launched; throws (caught above) if the binary isn't found
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static bool WindowsToast(string title, string body)
    {
        string Esc(string s) => s.Replace("'", "''");
        string ps =
            "[Windows.UI.Notifications.ToastNotificationManager,Windows.UI.Notifications,ContentType=WindowsRuntime]>$null;" +
            "$t=[Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02);" +
            "$x=$t.GetElementsByTagName('text');" +
            $"$x.Item(0).AppendChild($t.CreateTextNode('{Esc(title)}'))>$null;" +
            $"$x.Item(1).AppendChild($t.CreateTextNode('{Esc(body)}'))>$null;" +
            "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('ircuitry').Show([Windows.UI.Notifications.ToastNotification]::new($t));";
        return RunQuiet("powershell", "-NoProfile", "-NonInteractive", "-Command", ps);
    }
}
