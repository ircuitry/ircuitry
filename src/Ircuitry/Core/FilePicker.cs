using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Ircuitry.Core;

/// <summary>A minimal native "open file" dialog. The OS picker runs on a background thread (zenity/kdialog on
/// Linux, osascript on macOS, PowerShell on Windows) so it never blocks the render loop; the chosen path is
/// handed back on the game thread via <see cref="Drain"/>.</summary>
public static class FilePicker
{
    private static readonly ConcurrentQueue<Action> _results = new();

    /// <summary>Open the picker. On the next <see cref="Drain"/> (game thread): <paramref name="onPicked"/>(path)
    /// if the user chose a file, or <paramref name="onUnavailable"/>() if there's no native file dialog installed -
    /// so the caller can tell the user to paste a path instead of leaving a dead button.</summary>
    public static void Open(string title, Action<string> onPicked, Action? onUnavailable = null)
    {
        Task.Run(() =>
        {
            try
            {
                var p = RunDialog(title, out bool toolFound);
                if (!toolFound) { if (onUnavailable != null) _results.Enqueue(onUnavailable); return; }
                if (!string.IsNullOrWhiteSpace(p)) { var path = p.Trim(); _results.Enqueue(() => onPicked(path)); }
            }
            catch { if (onUnavailable != null) _results.Enqueue(onUnavailable); }
        });
    }

    /// <summary>Apply any finished pickers. Call once per frame on the game thread.</summary>
    public static void Drain() { while (_results.TryDequeue(out var a)) { try { a(); } catch { } } }

    private static string? RunDialog(string title, out bool toolFound)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Run("powershell", "-NoProfile -STA -Command \"Add-Type -AssemblyName System.Windows.Forms; $d=New-Object System.Windows.Forms.OpenFileDialog; if($d.ShowDialog() -eq 'OK'){[Console]::Out.Write($d.FileName)}\"", out toolFound);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return Run("osascript", "-e \"POSIX path of (choose file with prompt \\\"" + Esc(title) + "\\\")\"", out toolFound);
        // Linux: prefer zenity, fall back to kdialog if zenity isn't installed (but not if the user cancelled it)
        var z = Run("zenity", "--file-selection --title=\"" + Esc(title) + "\"", out bool zenityRan);
        if (zenityRan) { toolFound = true; return z; }
        var k = Run("kdialog", "--getopenfilename", out bool kdialogRan);
        toolFound = kdialogRan;
        return k;
    }

    private static string Esc(string s) => s.Replace("\"", "'");

    // Runs a process and returns its stdout (or null on cancel/error). `ran` distinguishes "tool missing"
    // (false -> caller may try a fallback) from "ran but produced nothing", e.g. the user cancelled (true).
    private static string? Run(string exe, string args, out bool ran)
    {
        ran = false;
        try
        {
            var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p == null) return null;
            ran = true;
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 && outp.Trim().Length > 0 ? outp : null;
        }
        catch { return null; }   // exe not found -> ran stays false
    }
}
