using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Ircuitry.Net;

/// <summary>
/// Runs commands inside an isolated OS container (Docker or Podman, auto-detected) so a workflow - or the
/// Programmer AI - gets "its own machine to play with" with no access to the host except a bind-mounted folder.
/// Engine-agnostic on purpose (no vendor lock-in): the node UI says "container", this picks whatever runtime works.
///
/// Two tiers:
///  - one-shot <see cref="Run"/>: a fresh `run --rm` container per command (stateless, simplest);
///  - managed <see cref="Start"/>/<see cref="ExecIn"/>/<see cref="Stop"/>: a long-lived container a workflow drives
///    (state persists between exec calls). Stable, idempotent names; everything torn down on app exit.
///
/// Defaults are safe: no network unless asked, CPU/memory/pid caps, and (rootful docker on Linux) the command runs
/// as the host user so files in the mounted folder aren't left root-owned.
/// </summary>
public static class ContainerEngine
{
    public enum Engine { None, Docker, Podman }

    private static Engine? _engine;
    private static readonly object _lock = new();
    private static readonly ConcurrentDictionary<string, byte> _started = new();   // managed containers we own
    private const string Prefix = "ircuitry-";

    public static Engine Available { get { lock (_lock) return _engine ??= Probe(); } }
    public static bool Ready => Available != Engine.None;
    public static string Cli => Available == Engine.Podman ? "podman" : "docker";
    public static string Describe => Available switch { Engine.Docker => "docker", Engine.Podman => "podman", _ => "none" };

    /// <summary>Re-probe (tests / after the user installs a runtime).</summary>
    public static void ResetProbe() { lock (_lock) _engine = null; }

    public const string Unavailable =
        "command sandbox unavailable: no usable Docker or Podman runtime. Install or start Docker (or Podman) - " +
        "commands are refused rather than run unsandboxed.";

    private static Engine Probe()
    {
        if (UsableRuntime("docker")) return Engine.Docker;
        if (UsableRuntime("podman")) return Engine.Podman;
        return Engine.None;
    }

    private static bool UsableRuntime(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("info");
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(6000)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;   // `info` only succeeds when the daemon is actually reachable
        }
        catch { return false; }
    }

    [DllImport("libc")] private static extern uint geteuid();
    [DllImport("libc")] private static extern uint getegid();

    /// <summary>A stable, prefixed, runtime-safe container name from a user-supplied label.</summary>
    public static string SafeName(string label)
    {
        var sb = new StringBuilder(Prefix);
        foreach (char c in (label ?? "").ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '-');
        string s = sb.ToString();
        if (s.Length <= Prefix.Length) s = Prefix + "box";
        if (s.Length > 64) s = s[..64];
        return s;
    }

    private static void ResourceCaps(List<string> a) { a.Add("--memory=2g"); a.Add("--cpus=2"); a.Add("--pids-limit=512"); }

    private static void UserMap(Engine engine, List<string> a)
    {
        // rootful docker on Linux would otherwise write root-owned files into the bind mount; podman is rootless
        if (engine == Engine.Docker && OperatingSystem.IsLinux()) { a.Add("--user"); a.Add(geteuid() + ":" + getegid()); }
    }

    private static void Mount(List<string> a, string hostDir)
    {
        if (!string.IsNullOrEmpty(hostDir)) { a.Add("-v"); a.Add(hostDir + ":/work"); a.Add("-w"); a.Add("/work"); }
    }

    private static string Img(string image) => string.IsNullOrWhiteSpace(image) ? "alpine" : image.Trim();

    // ---- one-shot ------------------------------------------------------------
    public static List<string> BuildRunArgs(Engine engine, string image, string hostDir, string command, bool allowNetwork, string name)
    {
        var a = new List<string> { "run", "--rm", "--name", name };
        if (!allowNetwork) a.Add("--network=none");
        ResourceCaps(a);
        UserMap(engine, a);
        a.Add("-e"); a.Add("HOME=/tmp");
        Mount(a, hostDir);
        a.Add(Img(image));
        a.Add("sh"); a.Add("-c"); a.Add(command);
        return a;
    }

    /// <summary>Run one command in a fresh throwaway container. Returns (started-ok, combined output).</summary>
    public static (bool ok, string output) Run(string image, string hostDir, string command, int timeoutSec, bool allowNetwork)
    {
        if (string.IsNullOrWhiteSpace(command)) return (true, "(no command)");
        if (!Ready) return (false, Unavailable);
        string name = Prefix + "run-" + ShortId();
        return Exec(Cli, BuildRunArgs(Available, image, hostDir, command, allowNetwork, name), timeoutSec, name);
    }

    // ---- managed lifecycle ---------------------------------------------------
    public static List<string> BuildStartArgs(Engine engine, string image, string name, string hostDir, bool allowNetwork)
    {
        var a = new List<string> { "run", "-d", "--name", name };
        if (!allowNetwork) a.Add("--network=none");
        ResourceCaps(a);
        UserMap(engine, a);
        a.Add("-e"); a.Add("HOME=/tmp");
        Mount(a, hostDir);
        a.Add(Img(image));
        a.Add("sh"); a.Add("-c"); a.Add("while :; do sleep 3600; done");   // keep it alive to exec into
        return a;
    }

    /// <summary>Start (or reuse) a long-lived container by name. Idempotent: re-running with the same name reuses
    /// the running one. Returns (ok, container-name-handle | error).</summary>
    public static (bool ok, string handle) Start(string image, string name, string hostDir, bool allowNetwork)
    {
        if (!Ready) return (false, Unavailable);
        string cname = SafeName(name);
        if (IsRunning(cname)) { _started[cname] = 1; return (true, cname); }
        RemoveStale(cname);   // a stopped container with this name would block the create
        var (ok, outp) = Exec(Cli, BuildStartArgs(Available, image, cname, hostDir, allowNetwork), 120, cname);
        if (ok) { _started[cname] = 1; return (true, cname); }
        return (false, outp);
    }

    public static (bool ok, string output) ExecIn(string handle, string command, int timeoutSec, bool asUser = true)
    {
        if (!Ready) return (false, Unavailable);
        if (string.IsNullOrWhiteSpace(command)) return (true, "(no command)");
        var a = new List<string> { "exec" };
        if (asUser && Available == Engine.Docker && OperatingSystem.IsLinux()) { a.Add("--user"); a.Add(geteuid() + ":" + getegid()); }
        a.Add(SafeName(handle)); a.Add("sh"); a.Add("-c"); a.Add(command);
        return Exec(Cli, a, timeoutSec, null);
    }

    public static (bool ok, string output) Stop(string handle)
    {
        if (!Ready) return (false, Unavailable);
        string cname = SafeName(handle);
        _started.TryRemove(cname, out _);
        return Exec(Cli, new List<string> { "rm", "-f", cname }, 30, null);
    }

    /// <summary>Tear down every managed container we started (call on app exit so none leak).</summary>
    public static void StopAll()
    {
        if (Available == Engine.None) return;
        foreach (var name in _started.Keys)
            try { Exec(Cli, new List<string> { "rm", "-f", name }, 15, null); } catch { }
        _started.Clear();
    }

    private static bool IsRunning(string cname)
    {
        var (ok, outp) = Exec(Cli, new List<string> { "ps", "-q", "-f", "name=^" + cname + "$" }, 10, null);
        return ok && outp.Trim().Length > 0;
    }

    private static void RemoveStale(string cname)
    {
        try { Exec(Cli, new List<string> { "rm", "-f", cname }, 10, null); } catch { }
    }

    private static string ShortId()
    {
        var g = Guid.NewGuid().ToString("N");
        return g.Substring(0, 10);
    }

    // ---- process plumbing ----------------------------------------------------
    private static (bool ok, string output) Exec(string exe, List<string> args, int timeoutSec, string? killName)
    {
        var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var buf = new StringBuilder();
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return (false, "could not start the container runtime");
            p.OutputDataReceived += (_, e) => { if (e.Data != null) buf.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) buf.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(Math.Clamp(timeoutSec, 1, 1800) * 1000))
            {
                try { p.Kill(true); } catch { }
                if (killName != null) try { using var k = Process.Start(new ProcessStartInfo(exe) { ArgumentList = { "kill", killName } }); k?.WaitForExit(5000); } catch { }
                buf.AppendLine("(timed out)");
                return (false, Trunc(buf.ToString()));
            }
            try { p.WaitForExit(); } catch { }
            return (p.ExitCode == 0, Trunc(buf.ToString()));
        }
        catch (Exception ex) { return (false, "container run failed: " + ex.Message); }
    }

    private static string Trunc(string s)
    {
        s = s.TrimEnd('\n');
        return s.Length > 60_000 ? s[..60_000] + "\n… (output truncated)" : s;
    }
}
