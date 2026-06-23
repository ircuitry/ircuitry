using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Ircuitry.UiKit;

/// <summary>
/// Manages <c>--ui-web</c> child processes - native webview windows (Photino). One per window id; opening an id
/// again replaces it. JS messages from the page arrive as @UIEVENT lines and are forwarded to the host. (Lighter
/// than <see cref="UiHost"/>: no scene streaming - the content is the page itself.)
/// </summary>
public sealed class UiWebHost
{
    private sealed class Win { public Process Proc = null!; public volatile bool Dead; public string? TmpHtml; }

    private readonly Dictionary<string, Win> _wins = new();
    private readonly object _gate = new();
    private readonly Action<string, UiEvent> _onEvent;
    private readonly Action<string, bool> _log;

    public UiWebHost(Action<string, UiEvent> onEvent, Action<string, bool> log) { _onEvent = onEvent; _log = log; }

    public void Open(string windowId, string url, string html, int width, int height, string title)
    {
        Close(windowId);   // re-opening an id replaces the window
        var modeArgs = new List<string> { "--ui-web", "--title", title.Length > 0 ? title : "ircuitry web", "--width", width.ToString(), "--height", height.ToString() };
        string? tmp = null;
        if (html.Length > 0)
        {
            tmp = Path.Combine(Path.GetTempPath(), "ircuitry-web-" + Guid.NewGuid().ToString("N") + ".html");
            try { File.WriteAllText(tmp, html); modeArgs.Add("--html-file"); modeArgs.Add(tmp); } catch { tmp = null; }
        }
        else if (url.Length > 0) { modeArgs.Add("--url"); modeArgs.Add(url); }

        try
        {
            var p = Process.Start(UiHost.SelfLaunch(modeArgs.ToArray()));
            if (p == null) { _log($"could not open web window '{windowId}'", true); return; }
            var w = new Win { Proc = p, TmpHtml = tmp };
            lock (_gate) _wins[windowId] = w;
            new Thread(() => ReadLoop(windowId, w)) { IsBackground = true, Name = "ui-web-events" }.Start();
        }
        catch (Exception e) { _log($"web window '{windowId}' failed to start: {e.Message}", true); if (tmp != null) try { File.Delete(tmp); } catch { } }
    }

    public void Close(string windowId)
    {
        Win? w; lock (_gate) { _wins.TryGetValue(windowId, out w); _wins.Remove(windowId); }
        if (w != null) Kill(w);
    }

    public void StopAll()
    {
        List<Win> all; lock (_gate) { all = new List<Win>(_wins.Values); _wins.Clear(); }
        foreach (var w in all) Kill(w);
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
        try { _onEvent(windowId, new UiEvent { Type = "close" }); } catch { }
        if (w.TmpHtml != null) try { File.Delete(w.TmpHtml); } catch { }
    }

    private static void Kill(Win w)
    {
        try { w.Dead = true; if (!w.Proc.HasExited) w.Proc.Kill(true); } catch { }
        if (w.TmpHtml != null) try { File.Delete(w.TmpHtml); } catch { }
    }
}
