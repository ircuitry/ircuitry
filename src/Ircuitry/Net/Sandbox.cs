using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Ircuitry.Net;

/// <summary>
/// Wraps a code node's child process (node / python3) in the strongest OS sandbox available, so a workflow's
/// code never runs as a bare process. No Docker involved - it uses ordinary kernel facilities.
///
/// Strength scales with trust:
///  - a shared server (ConfineFs/NoNetwork) gets a read-only filesystem, a private writable workdir, fresh
///    /proc + /dev, pid/ipc/uts namespaces and no network;
///  - a local desktop keeps the network and the real filesystem but still caps CPU time, file size and process
///    count, so a runaway or fork-bombing script can never wedge the machine.
///
/// The preferred mechanism is bubblewrap (bwrap): it works even where the kernel restricts unprivileged user
/// namespaces (Ubuntu 24's AppArmor default) because its packaged AppArmor profile permits it - raw `unshare`
/// does not. The chain is bwrap -> unshare -> a resource-capped shell, and we probe what actually WORKS on the
/// host rather than trusting mere presence on PATH (bwrap can be installed yet blocked inside a container).
/// </summary>
public static class Sandbox
{
    public sealed record Plan(string FileName, List<string> Args, string Mechanism);

    private static string? _bwrap, _unshare, _sh;
    private static bool _probed;
    private static bool? _strong;

    /// <summary>True if the host can actually confine code (network + namespaces). False means code runs only
    /// resource-capped - a hosted server should warn or refuse code in that case.</summary>
    public static bool StrongIsolation => _strong ??= ProbeStrong();

    /// <summary>Which mechanism the strong probe settled on: "bwrap", "unshare" or "rlimits only".</summary>
    public static string Mechanism { get; private set; } = "rlimits only";

    private static void ProbeTools()
    {
        if (_probed) return;
        _bwrap = Which("bwrap");
        _unshare = Which("unshare");
        _sh = Which("sh") ?? "/bin/sh";
        _probed = true;
    }

    // run a throwaway namespace as /bin/true to see whether the host actually permits it (not just whether the
    // tool exists). Caches the verdict and remembers the winning mechanism.
    private static bool ProbeStrong()
    {
        if (!OperatingSystem.IsLinux()) { Mechanism = "rlimits only"; return false; }
        ProbeTools();
        if (_bwrap != null && TryRun(_bwrap, "--ro-bind", "/", "/", "--unshare-net", "--", "/bin/true")) { Mechanism = "bwrap"; return true; }
        if (_unshare != null && TryRun(_unshare, "--user", "--map-root-user", "--net", "/bin/true")) { Mechanism = "unshare"; return true; }
        Mechanism = "rlimits only";
        return false;
    }

    /// <summary>Build the real launch for running <paramref name="exe"/> on <paramref name="scriptPath"/>.
    /// <paramref name="workDir"/> is a private, writable directory that holds the script (and the only writable
    /// place once the filesystem is confined).</summary>
    public static Plan Wrap(string exe, string scriptPath, string workDir, int cpuSec, bool noNetwork, bool confineFs)
    {
        // Windows has no sh/bwrap - rely on the caller's wall-clock kill (and a Job Object later if needed)
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return new Plan(exe, new List<string> { scriptPath }, "none (unsupported OS)");

        ProbeTools();
        _ = StrongIsolation;   // make sure Mechanism is resolved
        // resource caps that work in any POSIX shell. Deliberately NOT capping address space (-v/-d): V8 reserves
        // huge virtual ranges up front and would fail to start under such a limit. CPU time + the wall-clock kill
        // bound runtime; nproc + a pid namespace bound fork bombs.
        string limits = $"ulimit -t {Math.Clamp(cpuSec, 1, 60)} 2>/dev/null; ulimit -f 204800 2>/dev/null; ulimit -u 256 2>/dev/null; ";
        string inner = limits + "exec " + Sh(exe) + " " + Sh(scriptPath);
        bool wantStrong = noNetwork || confineFs;

        // strongest: bubblewrap. Read-only root, a fresh tmpfs /tmp with the workdir bound back in writable,
        // private /proc + /dev, and pid/ipc/uts/cgroup namespaces. The child env (the node's ctx vars) passes
        // through untouched because we do not --clearenv.
        if (wantStrong && Mechanism == "bwrap")
        {
            var a = new List<string>
            {
                "--ro-bind", "/", "/",
                "--proc", "/proc",
                "--dev", "/dev",
                "--tmpfs", "/tmp",
                "--bind", workDir, workDir,
                "--chdir", workDir,
                "--unshare-pid", "--unshare-ipc", "--unshare-uts", "--unshare-cgroup",
                "--die-with-parent", "--new-session",
            };
            if (noNetwork) a.Add("--unshare-net");
            a.Add("--"); a.Add(_sh!); a.Add("-c"); a.Add(inner);
            return new Plan(_bwrap!, a, noNetwork ? "bwrap (no net, ro fs)" : "bwrap (ro fs)");
        }

        // fallback where userns is permitted but bwrap is absent: a net + pid namespace. No filesystem
        // confinement, but the network is gone and a pid namespace contains the process tree.
        if (wantStrong && Mechanism == "unshare")
        {
            var a = new List<string> { "--user", "--map-root-user", "--pid", "--fork", "--mount-proc" };
            if (noNetwork) a.Insert(2, "--net");
            a.Add(_sh!); a.Add("-c"); a.Add(inner);
            return new Plan(_unshare!, a, noNetwork ? "unshare (no net)" : "unshare (pid ns)");
        }

        // last resort everywhere (and the default light sandbox for a trusted local desktop): a resource-capped
        // shell. No fs/net isolation, but a runaway loop, giant file or fork bomb still cannot take the box down.
        return new Plan(_sh!, new List<string> { "-c", inner }, "rlimits only");
    }

    private static bool TryRun(string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(4000)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    // single-quote for /bin/sh, escaping embedded quotes - paths only, never attacker-controlled code
    private static string Sh(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static string? Which(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            try { var p = Path.Combine(dir, name); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }
}
