using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Ircuitry.Net;

/// <summary>
/// Runs a snippet of JavaScript (node) or Python (python3) in a child process - like n8n's Code node.
/// Context values are passed as environment variables (NICK, CHANNEL, MESSAGE, ARGS, INPUT, …) and as a
/// JSON object on stdin; whatever the script prints to stdout becomes the node's output.
/// </summary>
public static class CodeRunner
{
    public static (string output, string? error) Run(string language, string code, Dictionary<string, string> ctx, int timeoutSec)
    {
        bool py = language.StartsWith("py", StringComparison.OrdinalIgnoreCase);
        string exe = py ? "python3" : "node";
        string ext = py ? ".py" : ".js";
        string tmp = Path.Combine(Path.GetTempPath(), "ircuitry-code-" + Guid.NewGuid().ToString("N") + ext);

        try
        {
            File.WriteAllText(tmp, code);
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                ArgumentList = { tmp },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var kv in ctx) psi.Environment[kv.Key.ToUpperInvariant()] = kv.Value;

            using var p = Process.Start(psi);
            if (p == null) return ("", exe + " could not start");

            // hand the context to stdin as JSON too (for scripts that prefer parsing it)
            try { p.StandardInput.Write(ToJson(ctx)); } catch { }
            p.StandardInput.Close();

            var outBuf = new StringBuilder();
            var errBuf = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) outBuf.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) errBuf.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            bool exited = p.WaitForExit(Math.Clamp(timeoutSec, 1, 20) * 1000);
            if (!exited)
            {
                try { p.Kill(true); } catch { }
                try { exited = p.WaitForExit(3000); } catch { }   // bounded: never hang even if Kill fails
            }
            // the parameterless overload flushes the async readers, but only call it once the process is gone
            if (exited) { try { p.WaitForExit(); } catch { } }

            string output = outBuf.ToString().TrimEnd('\r', '\n');
            string err = errBuf.ToString().Trim();
            if (!exited) return (output, "timed out");
            if (p.ExitCode != 0) return (output, err.Length > 0 ? err : exe + " exited " + p.ExitCode);
            return (output, null);
        }
        catch (System.ComponentModel.Win32Exception) { return ("", exe + " is not installed / not on PATH"); }
        catch (Exception ex) { return ("", ex.Message); }
        finally
        {
            try { File.Delete(tmp); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"code temp cleanup failed: {ex.Message}"); }
        }
    }

    private static string ToJson(Dictionary<string, string> ctx)
    {
        var sb = new StringBuilder("{");
        bool first = true;
        foreach (var kv in ctx)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(Esc(kv.Key)).Append("\":\"").Append(Esc(kv.Value)).Append('"');
        }
        return sb.Append('}').ToString();
    }

    private static string Esc(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
}
