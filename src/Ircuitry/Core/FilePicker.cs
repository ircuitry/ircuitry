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
    private static readonly ConcurrentQueue<(Action<string> apply, string path)> _results = new();

    /// <summary>Open the picker. When the user chooses a file, <paramref name="onPicked"/> runs on the next
    /// <see cref="Drain"/> (i.e. on the game thread, safe to mutate the graph).</summary>
    public static void Open(string title, Action<string> onPicked)
    {
        Task.Run(() =>
        {
            try { var p = RunDialog(title); if (!string.IsNullOrWhiteSpace(p)) _results.Enqueue((onPicked, p.Trim())); }
            catch { /* no picker tool available - silently do nothing */ }
        });
    }

    /// <summary>Apply any finished pickers. Call once per frame on the game thread.</summary>
    public static void Drain() { while (_results.TryDequeue(out var r)) { try { r.apply(r.path); } catch { } } }

    private static string? RunDialog(string title)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Run("powershell", "-NoProfile -STA -Command \"Add-Type -AssemblyName System.Windows.Forms; $d=New-Object System.Windows.Forms.OpenFileDialog; if($d.ShowDialog() -eq 'OK'){[Console]::Out.Write($d.FileName)}\"", out _);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return Run("osascript", "-e \"POSIX path of (choose file with prompt \\\"" + Esc(title) + "\\\")\"", out _);
        // Linux: prefer zenity, fall back to kdialog if zenity isn't installed (but not if the user cancelled it)
        var z = Run("zenity", "--file-selection --title=\"" + Esc(title) + "\"", out bool zenityRan);
        if (zenityRan) return z;
        return Run("kdialog", "--getopenfilename", out _);
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
