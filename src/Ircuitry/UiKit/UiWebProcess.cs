using System;
using System.IO;
using Photino.NET;

namespace Ircuitry.UiKit;

/// <summary>
/// The web-surface render process (<c>--ui-web</c>): one native OS window hosting a system webview (Photino -
/// WebView2 on Windows, WebKitGTK on Linux, WKWebView on macOS). It loads a URL or inline HTML and bridges the
/// page to the node graph: any <c>window.external.sendMessage(s)</c> from JS is emitted as an @UIEVENT line, so
/// a web page (or a whole web app) can drive the bot. The host kills the process to close the window.
/// </summary>
public static class UiWebProcess
{
    public static int Run(string[] args)
    {
        string url = "", htmlFile = "", title = "ircuitry web";
        int w = 900, h = 640;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--url" && i + 1 < args.Length) url = args[i + 1];
            if (args[i] == "--html-file" && i + 1 < args.Length) htmlFile = args[i + 1];
            if (args[i] == "--title" && i + 1 < args.Length) title = args[i + 1];
            if (args[i] == "--width" && i + 1 < args.Length && int.TryParse(args[i + 1], out var pw)) w = pw;
            if (args[i] == "--height" && i + 1 < args.Length && int.TryParse(args[i + 1], out var ph)) h = ph;
        }

        try
        {
            var win = new PhotinoWindow()
                .SetTitle(title)
                .SetUseOsDefaultSize(false)
                .SetSize(w, h);

            win.RegisterWebMessageReceivedHandler((object? sender, string message) =>
            {
                try { Console.Out.WriteLine("@UIEVENT " + UiScene.EventJson(new UiEvent { Type = "message", Value = message })); Console.Out.Flush(); }
                catch { }
            });

            if (htmlFile.Length > 0 && File.Exists(htmlFile)) win.LoadRawString(File.ReadAllText(htmlFile));
            else if (url.Length > 0) win.Load(new Uri(url));
            else win.LoadRawString("<!doctype html><html><body style=\"margin:0;font-family:system-ui,sans-serif;background:#141018;color:#f2eef7;display:grid;place-items:center;height:100vh\"><div style=\"text-align:center\"><h2>ircuitry web surface</h2><p style=\"color:#9c95ae\">give the UI Web node a URL or some HTML.</p></div></body></html>");

            win.WaitForClose();
            return 0;
        }
        catch (Exception e)
        {
            // Photino wraps native faults as "Native code exception. Error # 0  See inner exception for details."
            // and the real cause (a missing/too-new native dependency, e.g. a GLIBC version) lives in InnerException -
            // surface it, otherwise the message is useless for diagnosing why the webview would not open.
            Console.Error.WriteLine("ui-web failed: " + e.Message);
            if (e.InnerException != null) Console.Error.WriteLine("  cause: " + e.InnerException.Message.Trim());
            return 1;
        }
    }
}
