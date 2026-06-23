using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace Ircuitry.UiKit;

/// <summary>
/// Spawns and manages <c>--ui-window</c> child render processes - one real OS window each - streams scenes to
/// them and reads UI events back. Cross-platform by construction: it just relaunches THIS app in window mode,
/// so every window is ircuitry's own renderer painting a node-authored scene. One per bot (lives on its sink).
/// </summary>
public sealed class UiHost
{
    private sealed class Win { public Process Proc = null!; public TextWriter In = null!; public volatile bool Dead; }

    private readonly Dictionary<string, Win> _wins = new();
    private readonly object _gate = new();
    private readonly Action<string, UiEvent> _onEvent;   // (windowId, event)
    private readonly Action<string, bool> _log;          // (message, isError)

    public UiHost(Action<string, UiEvent> onEvent, Action<string, bool> log) { _onEvent = onEvent; _log = log; }

    /// <summary>Push the full scene to a window (spawning it if needed). The child swaps it in atomically.</summary>
    public void Send(string windowId, string sceneJson)
    {
        Win w;
        lock (_gate) { w = Ensure(windowId); }
        if (w == null! || w.Dead) return;
        try { lock (w) { w.In.WriteLine(sceneJson); w.In.Flush(); } }
        catch { w.Dead = true; }
    }

    public void Close(string windowId)
    {
        Win? w;
        lock (_gate) { _wins.TryGetValue(windowId, out w); _wins.Remove(windowId); }
        if (w != null) Kill(w);
    }

    public void StopAll()
    {
        List<Win> all;
        lock (_gate) { all = new List<Win>(_wins.Values); _wins.Clear(); }
        foreach (var w in all) Kill(w);
    }

    // caller holds _gate
    private Win Ensure(string windowId)
    {
        if (_wins.TryGetValue(windowId, out var ex) && !ex.Dead) return ex;
        if (ex != null) _wins.Remove(windowId);
        try
        {
            var p = Process.Start(SelfLaunch("--ui-window"));
            if (p == null) { _log($"could not open UI window '{windowId}'", true); return null!; }
            var w = new Win { Proc = p, In = p.StandardInput };
            _wins[windowId] = w;
            new Thread(() => ReadLoop(windowId, w)) { IsBackground = true, Name = "ui-events" }.Start();
            return w;
        }
        catch (Exception e) { _log($"UI window '{windowId}' failed to start: {e.Message}", true); return null!; }
    }

    private void ReadLoop(string windowId, Win w)
    {
        try
        {
            string? line;
            while ((line = w.Proc.StandardOutput.ReadLine()) != null)
                if (line.StartsWith("@UIEVENT ", StringComparison.Ordinal))
                    try { _onEvent(windowId, UiScene.EventFromJson(line.Substring(9))); } catch { }
        }
        catch { }
        w.Dead = true;
        lock (_gate) { if (_wins.TryGetValue(windowId, out var cur) && cur == w) _wins.Remove(windowId); }
        try { _onEvent(windowId, new UiEvent { Type = "close" }); } catch { }   // the window vanished -> a close event
    }

    private static void Kill(Win w) { try { w.Dead = true; if (!w.Proc.HasExited) w.Proc.Kill(true); } catch { } }

    /// <summary>Relaunch THIS executable in a sub-mode (dotnet &lt;dll&gt; &lt;args&gt;, or &lt;apphost&gt; &lt;args&gt;), wiring stdin+stdout.
    /// Shared by the 2D/3D window host and the web-surface host.</summary>
    public static ProcessStartInfo SelfLaunch(params string[] modeArgs)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        string exe = Environment.ProcessPath ?? "dotnet";
        psi.FileName = exe;
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var dll = Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(dll)) psi.ArgumentList.Add(dll);
        }
        foreach (var a in modeArgs) psi.ArgumentList.Add(a);
        return psi;
    }
}
