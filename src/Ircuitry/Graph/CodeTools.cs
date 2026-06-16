using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ircuitry.Graph;

/// <summary>Raised when a path or action would step outside the codebase root. Node behaviours catch it and
/// surface a tidy error instead of leaking anything outside the sandbox.</summary>
public sealed class CodeAccessException : Exception
{
    public CodeAccessException(string message) : base(message) { }
}

/// <summary>
/// The shared toolkit behind the <c>code.*</c> nodes and the Programmer AI: read / write / edit / search /
/// move files and run commands, all CONFINED to a codebase root. The confinement
/// (<see cref="Confine"/>) is the security primitive: every path - relative OR absolute, and after
/// resolving <c>..</c> and symlinks - must land inside the root, or the call throws. This is what lets a
/// Programmer AI be "absolutely forced to only operate inside the codebase it is working in": it never sees
/// or sets the root, and any path it invents that escapes is rejected before a single byte is touched.
/// </summary>
public static class CodeTools
{
    /// <summary>Biggest file a read/edit/search will touch, to bound memory on hostile or huge inputs.</summary>
    public const long MaxBytes = 8_000_000;
    private const int MaxGrepMatches = 800;
    private const int MaxListEntries = 5000;
    private const int MaxTreeEntries = 4000;

    // directories never worth walking for an AI working a codebase (huge, generated, or vcs internals)
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", "bin", "obj", ".vs", ".vscode", ".idea",
        "__pycache__", ".pytest_cache", ".mypy_cache", "dist", "build", "target",
        ".next", ".nuxt", ".gradle", "vendor", ".venv", "venv", ".tox", "coverage",
    };

    // =====================================================================
    //  the security primitive
    // =====================================================================

    /// <summary>
    /// Resolve <paramref name="rel"/> as a path inside the codebase <paramref name="root"/> and return its
    /// absolute form. A leading <c>/</c> is treated as root-relative (so <c>/etc/passwd</c> means
    /// <c>root/etc/passwd</c>, never the real filesystem root); <c>..</c> traversal and symlinks that would
    /// escape the root are rejected. Throws <see cref="CodeAccessException"/> if the result is outside root.
    /// </summary>
    public static string Confine(string root, string rel)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new CodeAccessException("no codebase root is set - point this node at a folder first");

        string fullRoot = Path.GetFullPath(root.Trim());
        string normRoot = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        rel = (rel ?? "").Trim().Replace('\\', '/');
        // a path the model/author writes is ALWAYS relative to the project, even if it "looks" absolute
        string tail = rel.TrimStart('/').Trim();
        string candidate = tail.Length == 0 ? normRoot : Path.GetFullPath(Path.Combine(normRoot, tail));

        if (!Inside(normRoot, candidate))
            throw new CodeAccessException("path escapes the codebase: " + rel);

        // a symlink inside the tree could still point out; resolve the deepest existing ancestor and recheck
        string real = RealPathOfExistingPrefix(candidate);
        if (!Inside(normRoot, real))
            throw new CodeAccessException("path escapes the codebase via a symlink: " + rel);

        return candidate;
    }

    /// <summary>The codebase root itself, normalised - or throws if it is blank.</summary>
    public static string Root(string root) => Confine(root, "");

    private static bool Inside(string root, string p) =>
        string.Equals(p, root, StringComparison.Ordinal) ||
        p.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    // Resolve symlinks on the nearest existing ancestor of <paramref name="path"/>, then re-append the
    // not-yet-created tail. Catches "write through a symlinked dir that leaves the tree" without needing the
    // final file to exist yet.
    private static string RealPathOfExistingPrefix(string path)
    {
        try
        {
            string cur = path;
            var tail = new List<string>();
            while (!File.Exists(cur) && !Directory.Exists(cur))
            {
                string? parent = Path.GetDirectoryName(cur);
                if (string.IsNullOrEmpty(parent) || parent == cur) return path;   // hit a drive/root
                tail.Add(Path.GetFileName(cur));
                cur = parent;
            }
            FileSystemInfo info = Directory.Exists(cur) ? new DirectoryInfo(cur) : new FileInfo(cur);
            string baseReal = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(cur);
            tail.Reverse();
            return tail.Count == 0 ? baseReal : Path.GetFullPath(Path.Combine(baseReal, string.Join('/', tail)));
        }
        catch { return path; }
    }

    /// <summary>A path shown back to the model: relative to root, forward slashes, so it can never learn the
    /// machine's real directory layout.</summary>
    public static string Rel(string root, string full)
    {
        try
        {
            string r = Path.GetFullPath(root.Trim()).TrimEnd(Path.DirectorySeparatorChar);
            string p = Path.GetFullPath(full);
            if (p == r) return ".";
            if (p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return p.Substring(r.Length + 1).Replace(Path.DirectorySeparatorChar, '/');
            return Path.GetFileName(p);
        }
        catch { return full; }
    }

    // =====================================================================
    //  file ops (all confined)
    // =====================================================================

    public static string Read(string root, string path, int startLine = 0, int endLine = 0)
    {
        string full = Confine(root, path);
        if (!File.Exists(full)) throw new CodeAccessException("no such file: " + path);
        if (new FileInfo(full).Length > MaxBytes) throw new CodeAccessException("file too big (> 8MB): " + path);
        string text = File.ReadAllText(full);
        if (startLine <= 0 && endLine <= 0) return text;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int s = Math.Max(1, startLine), e = endLine <= 0 ? lines.Length : Math.Min(endLine, lines.Length);
        if (s > lines.Length) return "";
        return string.Join("\n", lines.Skip(s - 1).Take(Math.Max(0, e - s + 1)));
    }

    public static void Write(string root, string path, string content)
    {
        string full = Confine(root, path);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(full, content);
    }

    public static void Append(string root, string path, string content)
    {
        string full = Confine(root, path);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.AppendAllText(full, content);
    }

    /// <summary>Exact string replacement, like an editor's find/replace. <paramref name="all"/> false replaces
    /// the single unique occurrence (and fails if the match is absent or ambiguous), which is the safe default
    /// for an AI editing code.</summary>
    public static int Edit(string root, string path, string find, string replace, bool all)
    {
        if (find.Length == 0) throw new CodeAccessException("edit needs a non-empty 'find' string");
        string full = Confine(root, path);
        if (!File.Exists(full)) throw new CodeAccessException("no such file: " + path);
        string text = File.ReadAllText(full);
        int count = CountOccurrences(text, find);
        if (count == 0) throw new CodeAccessException("'find' text not found in " + path);
        if (!all && count > 1) throw new CodeAccessException($"'find' appears {count} times in {path}; make it unique or set replace-all");
        string updated = all ? text.Replace(find, replace) : ReplaceFirst(text, find, replace);
        File.WriteAllText(full, updated);
        return all ? count : 1;
    }

    public static int Insert(string root, string path, int afterLine, string content)
    {
        string full = Confine(root, path);
        var lines = (File.Exists(full) ? File.ReadAllText(full).Replace("\r\n", "\n") : "").Split('\n').ToList();
        if (lines.Count == 1 && lines[0].Length == 0) lines.Clear();
        int at = Math.Clamp(afterLine, 0, lines.Count);
        lines.InsertRange(at, content.Replace("\r\n", "\n").Split('\n'));
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(full, string.Join("\n", lines));
        return at + 1;
    }

    public static void Mkdir(string root, string path) => Directory.CreateDirectory(Confine(root, path));

    public static void Delete(string root, string path)
    {
        string full = Confine(root, path);
        if (string.Equals(full, Root(root), StringComparison.Ordinal))
            throw new CodeAccessException("refusing to delete the codebase root itself");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        else if (File.Exists(full)) File.Delete(full);
        else throw new CodeAccessException("no such path: " + path);
    }

    public static void Move(string root, string from, string to)
    {
        string src = Confine(root, from), dst = Confine(root, to);
        if (!File.Exists(src) && !Directory.Exists(src)) throw new CodeAccessException("no such path: " + from);
        string? dir = Path.GetDirectoryName(dst);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (Directory.Exists(src)) Directory.Move(src, dst);
        else File.Move(src, dst, overwrite: true);
    }

    public static void Copy(string root, string from, string to)
    {
        string src = Confine(root, from), dst = Confine(root, to);
        if (Directory.Exists(src)) CopyDir(src, dst);
        else if (File.Exists(src))
        {
            string? dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(src, dst, overwrite: true);
        }
        else throw new CodeAccessException("no such path: " + from);
    }

    public static bool Exists(string root, string path)
    {
        string full = Confine(root, path);
        return File.Exists(full) || Directory.Exists(full);
    }

    public static string Stat(string root, string path)
    {
        string full = Confine(root, path);
        if (Directory.Exists(full))
        {
            int files = 0, dirs = 0;
            try { foreach (var _ in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)) files++; } catch { }
            try { foreach (var _ in Directory.EnumerateDirectories(full, "*", SearchOption.AllDirectories)) dirs++; } catch { }
            return $"directory · {files} files · {dirs} subdirs";
        }
        if (File.Exists(full))
        {
            var fi = new FileInfo(full);
            int lines = 0;
            try { if (fi.Length <= MaxBytes) lines = File.ReadLines(full).Count(); } catch { }
            return $"file · {fi.Length} bytes · {lines} lines · modified {fi.LastWriteTimeUtc:yyyy-MM-dd HH:mm} UTC";
        }
        throw new CodeAccessException("no such path: " + path);
    }

    // =====================================================================
    //  navigation & search (confined)
    // =====================================================================

    public static string List(string root, string path)
    {
        string full = Confine(root, path.Length == 0 ? "." : path);
        if (!Directory.Exists(full)) throw new CodeAccessException("no such directory: " + path);
        var sb = new StringBuilder();
        int n = 0;
        foreach (var d in Directory.EnumerateDirectories(full).OrderBy(p => p, StringComparer.Ordinal))
        {
            if (n++ >= MaxListEntries) break;
            sb.Append(Path.GetFileName(d)).Append("/\n");
        }
        foreach (var f in Directory.EnumerateFiles(full).OrderBy(p => p, StringComparer.Ordinal))
        {
            if (n++ >= MaxListEntries) break;
            sb.Append(Path.GetFileName(f)).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>A compact, depth-bounded directory tree (skipping vcs/generated dirs), for orientation.</summary>
    public static string Tree(string root, string path, int maxDepth = 3)
    {
        string full = Confine(root, path.Length == 0 ? "." : path);
        if (!Directory.Exists(full)) throw new CodeAccessException("no such directory: " + path);
        var sb = new StringBuilder();
        int count = 0;
        void Walk(string dir, string prefix, int depth)
        {
            if (depth > maxDepth || count >= MaxTreeEntries) return;
            string[] subs;
            try { subs = Directory.GetDirectories(dir).Where(d => !SkipDirs.Contains(Path.GetFileName(d))).OrderBy(p => p, StringComparer.Ordinal).ToArray(); }
            catch { return; }
            string[] files;
            try { files = Directory.GetFiles(dir).OrderBy(p => p, StringComparer.Ordinal).ToArray(); }
            catch { files = Array.Empty<string>(); }
            foreach (var d in subs)
            {
                if (count++ >= MaxTreeEntries) return;
                sb.Append(prefix).Append(Path.GetFileName(d)).Append("/\n");
                Walk(d, prefix + "  ", depth + 1);
            }
            foreach (var f in files)
            {
                if (count++ >= MaxTreeEntries) return;
                sb.Append(prefix).Append(Path.GetFileName(f)).Append('\n');
            }
        }
        Walk(full, "", 1);
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Find files by glob (e.g. <c>**/*.cs</c>, <c>src/*.py</c>), newest first, paths relative to root.</summary>
    public static string Glob(string root, string pattern)
    {
        string r = Root(root);
        pattern = (pattern ?? "").Trim().Replace('\\', '/');
        if (pattern.Length == 0) pattern = "**/*";
        var rx = GlobToRegex(pattern);
        var hits = new List<(string rel, DateTime mt)>();
        foreach (var f in EnumerateFilesSafe(r))
        {
            string rel = Rel(r, f);
            if (rx.IsMatch(rel))
            {
                DateTime mt = DateTime.MinValue;
                try { mt = File.GetLastWriteTimeUtc(f); } catch { }
                hits.Add((rel, mt));
            }
            if (hits.Count >= MaxListEntries) break;
        }
        return string.Join("\n", hits.OrderByDescending(h => h.mt).Select(h => h.rel));
    }

    /// <summary>Regex search across files (optionally filtered by a glob), as <c>path:line: text</c>.</summary>
    public static string Grep(string root, string pattern, string globFilter = "")
    {
        string r = Root(root);
        Regex rx;
        try { rx = new Regex(pattern, RegexOptions.Compiled); }
        catch (Exception ex) { throw new CodeAccessException("bad search regex: " + ex.Message); }
        Regex? fileRx = globFilter.Trim().Length > 0 ? GlobToRegex(globFilter.Trim().Replace('\\', '/')) : null;

        var sb = new StringBuilder();
        int matches = 0;
        foreach (var f in EnumerateFilesSafe(r))
        {
            string rel = Rel(r, f);
            if (fileRx != null && !fileRx.IsMatch(rel)) continue;
            try
            {
                if (new FileInfo(f).Length > MaxBytes || IsBinary(f)) continue;
                int ln = 0;
                foreach (var line in File.ReadLines(f))
                {
                    ln++;
                    if (rx.IsMatch(line))
                    {
                        sb.Append(rel).Append(':').Append(ln).Append(": ").Append(line.Trim()).Append('\n');
                        if (++matches >= MaxGrepMatches) { sb.Append("… (more matches truncated)\n"); return sb.ToString().TrimEnd('\n'); }
                    }
                }
            }
            catch { /* unreadable file, skip */ }
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Find/replace the same exact text across every matching file. Returns files changed.</summary>
    public static int ReplaceAcross(string root, string find, string replace, string globFilter)
    {
        if (find.Length == 0) throw new CodeAccessException("replace needs a non-empty 'find' string");
        string r = Root(root);
        Regex? fileRx = globFilter.Trim().Length > 0 ? GlobToRegex(globFilter.Trim().Replace('\\', '/')) : null;
        int changed = 0;
        foreach (var f in EnumerateFilesSafe(r))
        {
            string rel = Rel(r, f);
            if (fileRx != null && !fileRx.IsMatch(rel)) continue;
            try
            {
                if (new FileInfo(f).Length > MaxBytes || IsBinary(f)) continue;
                string text = File.ReadAllText(f);
                if (!text.Contains(find, StringComparison.Ordinal)) continue;
                File.WriteAllText(f, text.Replace(find, replace));
                changed++;
            }
            catch { }
        }
        return changed;
    }

    /// <summary>A unified-ish diff between two confined files (line granularity).</summary>
    public static string Diff(string root, string a, string b)
    {
        string fa = Confine(root, a), fb = Confine(root, b);
        string[] la = File.Exists(fa) ? File.ReadAllText(fa).Replace("\r\n", "\n").Split('\n') : Array.Empty<string>();
        string[] lb = File.Exists(fb) ? File.ReadAllText(fb).Replace("\r\n", "\n").Split('\n') : Array.Empty<string>();
        var sb = new StringBuilder();
        sb.Append("--- ").Append(Rel(root, fa)).Append('\n');
        sb.Append("+++ ").Append(Rel(root, fb)).Append('\n');
        // simple LCS-free line diff: walk both, emit removed/added when they differ
        int i = 0, j = 0;
        var setB = new HashSet<string>(lb);
        var setA = new HashSet<string>(la);
        while (i < la.Length || j < lb.Length)
        {
            if (i < la.Length && j < lb.Length && la[i] == lb[j]) { sb.Append("  ").Append(la[i]).Append('\n'); i++; j++; }
            else if (i < la.Length && !setB.Contains(la[i])) { sb.Append("- ").Append(la[i]).Append('\n'); i++; }
            else if (j < lb.Length && !setA.Contains(lb[j])) { sb.Append("+ ").Append(lb[j]).Append('\n'); j++; }
            else if (i < la.Length) { sb.Append("- ").Append(la[i]).Append('\n'); i++; }
            else { sb.Append("+ ").Append(lb[j]).Append('\n'); j++; }
        }
        return sb.ToString().TrimEnd('\n');
    }

    // language-agnostic "definition" lines: classes/types/functions/methods across common languages
    private static readonly Regex OutlineRx = new(
        @"^\s*(?:(?:public|private|protected|internal|export|default|static|abstract|sealed|partial|final|virtual|override|async|pub|unsafe)\s+)*" +
        @"(?:class|interface|struct|enum|record|trait|impl|namespace|module|type|def|fn|func|function|sub|proc|method)\b.*",
        RegexOptions.Compiled);
    private static readonly Regex JsConstFnRx = new(@"^\s*(?:export\s+)?(?:const|let|var)\s+\w+\s*=\s*(?:async\s*)?\(?.*=>", RegexOptions.Compiled);
    private static readonly Regex CsMethodRx = new(
        @"^\s*(?:(?:public|private|protected|internal|static|abstract|sealed|virtual|override|async|new|extern|unsafe)\s+)+[\w<>\[\],\?\.]+\s+\w+\s*\(",
        RegexOptions.Compiled);

    /// <summary>A heuristic outline of a file's top-level definitions (classes, functions, methods) as
    /// <c>line: signature</c>, so an AI can navigate without reading the whole file.</summary>
    public static string Outline(string root, string path)
    {
        string text = Read(root, path);
        var sb = new StringBuilder();
        int ln = 0, hits = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            ln++;
            string line = raw.TrimEnd();
            string t = line.TrimStart();
            if (t.StartsWith("//") || t.StartsWith("#") || t.StartsWith("*")) continue;
            if (OutlineRx.IsMatch(line) || JsConstFnRx.IsMatch(line) || CsMethodRx.IsMatch(line))
            {
                sb.Append(ln).Append(": ").Append(line.Trim().TrimEnd('{').Trim()).Append('\n');
                if (++hits >= 400) break;
            }
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>A quick overview of the codebase: file count, total lines of code, and the top file types.</summary>
    public static string Stats(string root)
    {
        string r = Root(root);
        int files = 0; long lines = 0; long bytes = 0;
        var byExt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in EnumerateFilesSafe(r))
        {
            files++;
            string ext = Path.GetExtension(f);
            if (ext.Length == 0) ext = "(none)";
            byExt[ext] = byExt.TryGetValue(ext, out var n) ? n + 1 : 1;
            try
            {
                var fi = new FileInfo(f);
                bytes += fi.Length;
                if (fi.Length <= MaxBytes && !IsBinary(f)) lines += File.ReadLines(f).LongCount();
            }
            catch { }
        }
        string top = string.Join(", ", byExt.OrderByDescending(k => k.Value).Take(8).Select(k => $"{k.Key} ×{k.Value}"));
        return $"{files} files · {lines} lines · {bytes / 1024} KB\nfile types: {top}";
    }

    // =====================================================================
    //  command execution (cwd confined to root)
    // =====================================================================

    /// <summary>Run a shell command with its working directory pinned to the codebase root, capturing
    /// stdout+stderr (truncated). The cwd is confined; the command itself is trusted by the graph author /
    /// enabled explicitly on the Programmer AI.</summary>
    public static string Run(string root, string command, int timeoutSec = 30)
    {
        string cwd = Root(root);
        if (command.Trim().Length == 0) return "(no command)";
        bool win = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = win ? "cmd.exe" : "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = cwd,
        };
        if (win) { psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(command); }
        else { psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(command); }

        var outBuf = new StringBuilder();
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return "(could not start a shell)";
            p.OutputDataReceived += (_, e) => { if (e.Data != null) outBuf.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) outBuf.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(Math.Clamp(timeoutSec, 1, 120) * 1000))
            {
                try { p.Kill(true); } catch { }
                outBuf.AppendLine("(timed out)");
            }
            else { try { p.WaitForExit(); } catch { } }
        }
        catch (Exception ex) { return "command failed to launch: " + ex.Message; }

        string outp = outBuf.ToString();
        if (outp.Length > 60_000) outp = outp.Substring(0, 60_000) + "\n… (output truncated)";
        return outp.TrimEnd('\n');
    }

    // =====================================================================
    //  archive (zip / unzip) - confined
    // =====================================================================

    /// <summary>Zip a confined source directory (or file) into a confined .zip path. Skips vcs/generated dirs
    /// when zipping a tree. Returns the byte size of the archive.</summary>
    public static long Zip(string root, string source, string zipPath)
    {
        string src = Confine(root, source.Length == 0 ? "." : source);
        string dst = Confine(root, zipPath);
        if (string.Equals(Path.GetFullPath(dst), Path.GetFullPath(src), StringComparison.Ordinal))
            throw new CodeAccessException("the zip cannot be its own source");
        string? dir = Path.GetDirectoryName(dst);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(dst)) File.Delete(dst);

        using (var fs = new FileStream(dst, FileMode.Create))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            if (File.Exists(src))
            {
                zip.CreateEntryFromFile(src, Path.GetFileName(src), CompressionLevel.Optimal);
            }
            else if (Directory.Exists(src))
            {
                string baseDir = src.TrimEnd(Path.DirectorySeparatorChar);
                foreach (var f in EnumerateFilesSafe(baseDir))
                {
                    if (string.Equals(Path.GetFullPath(f), Path.GetFullPath(dst), StringComparison.Ordinal)) continue;  // don't zip the zip
                    string entry = Path.GetRelativePath(baseDir, f).Replace(Path.DirectorySeparatorChar, '/');
                    try { zip.CreateEntryFromFile(f, entry, CompressionLevel.Optimal); } catch { }
                }
            }
            else throw new CodeAccessException("no such path to zip: " + source);
        }
        return new FileInfo(dst).Length;
    }

    /// <summary>Make a zip of a confined directory into a fresh temp file OUTSIDE the tree (so it isn't part
    /// of what gets zipped), returning that temp path. Used by "send codebase". Caller deletes it.</summary>
    public static string ZipToTemp(string root, string source)
    {
        string src = Confine(root, source.Length == 0 ? "." : source);
        if (!Directory.Exists(src) && !File.Exists(src)) throw new CodeAccessException("nothing to zip at: " + source);
        string tmp = Path.Combine(Path.GetTempPath(), "ircuitry-codebase-" + Guid.NewGuid().ToString("N") + ".zip");
        using (var fs = new FileStream(tmp, FileMode.Create))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            if (File.Exists(src)) zip.CreateEntryFromFile(src, Path.GetFileName(src), CompressionLevel.Optimal);
            else
            {
                string baseDir = src.TrimEnd(Path.DirectorySeparatorChar);
                foreach (var f in EnumerateFilesSafe(baseDir))
                {
                    string entry = Path.GetRelativePath(baseDir, f).Replace(Path.DirectorySeparatorChar, '/');
                    try { zip.CreateEntryFromFile(f, entry, CompressionLevel.Optimal); } catch { }
                }
            }
        }
        return tmp;
    }

    /// <summary>Unzip a confined .zip into a confined destination dir, rejecting any entry that would write
    /// outside the destination (zip-slip). Returns the number of entries extracted.</summary>
    public static int Unzip(string root, string zipPath, string destDir)
    {
        string zp = Confine(root, zipPath);
        if (!File.Exists(zp)) throw new CodeAccessException("no such zip: " + zipPath);
        string dest = Confine(root, destDir.Length == 0 ? "." : destDir);
        Directory.CreateDirectory(dest);
        string destFull = Path.GetFullPath(dest).TrimEnd(Path.DirectorySeparatorChar);

        int n = 0;
        using var zip = ZipFile.OpenRead(zp);
        foreach (var e in zip.Entries)
        {
            string target = Path.GetFullPath(Path.Combine(destFull, e.FullName));
            if (target != destFull && !target.StartsWith(destFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new CodeAccessException("zip entry escapes the destination (zip-slip): " + e.FullName);
            if (e.FullName.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
            string? d = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
            e.ExtractToFile(target, overwrite: true);
            n++;
        }
        return n;
    }

    // ---- absolute-path archive helpers (used by the media/archive nodes, which honour author-chosen
    //      absolute paths like the other file nodes; the confined wrappers above add root-checking) ----

    /// <summary>Zip an absolute source dir (or file) into an absolute .zip path. Skips vcs/generated dirs when
    /// <paramref name="skipVcs"/>. Returns the archive size in bytes.</summary>
    public static long ZipAbsolute(string sourceAbs, string zipAbs, bool skipVcs = false)
    {
        if (!File.Exists(sourceAbs) && !Directory.Exists(sourceAbs))
            throw new CodeAccessException("nothing to zip at: " + sourceAbs);
        string? dir = Path.GetDirectoryName(zipAbs);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(zipAbs)) File.Delete(zipAbs);

        using (var fs = new FileStream(zipAbs, FileMode.Create))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            if (File.Exists(sourceAbs))
                zip.CreateEntryFromFile(sourceAbs, Path.GetFileName(sourceAbs), CompressionLevel.Optimal);
            else
            {
                string baseDir = sourceAbs.TrimEnd(Path.DirectorySeparatorChar);
                IEnumerable<string> files = skipVcs ? EnumerateFilesSafe(baseDir)
                    : Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories);
                foreach (var f in files)
                {
                    if (string.Equals(Path.GetFullPath(f), Path.GetFullPath(zipAbs), StringComparison.Ordinal)) continue;
                    string entry = Path.GetRelativePath(baseDir, f).Replace(Path.DirectorySeparatorChar, '/');
                    try { zip.CreateEntryFromFile(f, entry, CompressionLevel.Optimal); } catch { }
                }
            }
        }
        return new FileInfo(zipAbs).Length;
    }

    /// <summary>Extract an absolute .zip into an absolute destination dir, rejecting any entry that would
    /// escape the destination (zip-slip). Returns the number of files extracted.</summary>
    public static int UnzipAbsolute(string zipAbs, string destAbs)
    {
        if (!File.Exists(zipAbs)) throw new CodeAccessException("no such zip: " + zipAbs);
        Directory.CreateDirectory(destAbs);
        string destFull = Path.GetFullPath(destAbs).TrimEnd(Path.DirectorySeparatorChar);
        int n = 0;
        using var zip = ZipFile.OpenRead(zipAbs);
        foreach (var e in zip.Entries)
        {
            string target = Path.GetFullPath(Path.Combine(destFull, e.FullName));
            if (target != destFull && !target.StartsWith(destFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new CodeAccessException("zip entry escapes the destination (zip-slip): " + e.FullName);
            if (e.FullName.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
            string? d = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
            e.ExtractToFile(target, overwrite: true);
            n++;
        }
        return n;
    }

    /// <summary>Read an image file's pixel dimensions + format from its header alone (no decode, no external
    /// deps): PNG, JPEG, GIF, BMP, WebP. Returns null for anything it doesn't recognise.</summary>
    public static (int w, int h, string fmt)? ImageInfo(string fileAbs)
    {
        try
        {
            using var fs = File.OpenRead(fileAbs);
            using var br = new BinaryReader(fs);
            var head = br.ReadBytes(32);
            if (head.Length < 24) return null;

            // PNG: 89 50 4E 47 ... IHDR width/height big-endian at offset 16
            if (head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47)
                return (Be32(head, 16), Be32(head, 20), "png");
            // GIF: "GIF87a"/"GIF89a", width/height little-endian at offset 6
            if (head[0] == 'G' && head[1] == 'I' && head[2] == 'F')
                return (head[6] | head[7] << 8, head[8] | head[9] << 8, "gif");
            // BMP: "BM", width/height little-endian at offset 18/22
            if (head[0] == 'B' && head[1] == 'M')
                return (Le32(head, 18), Le32(head, 22), "bmp");
            // WebP: "RIFF"...."WEBP"
            if (head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F' &&
                head[8] == 'W' && head[9] == 'E' && head[10] == 'B' && head[11] == 'P')
                return WebpSize(fileAbs);
            // JPEG: FF D8 - walk the segments for SOF0..SOF15
            if (head[0] == 0xFF && head[1] == 0xD8)
                return JpegSize(fileAbs);
            return null;
        }
        catch { return null; }
    }

    private static int Be32(byte[] b, int o) => b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3];
    private static int Le32(byte[] b, int o) => b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24;

    private static (int, int, string)? JpegSize(string path)
    {
        using var fs = File.OpenRead(path);
        fs.Position = 2;
        Span<byte> hdr = stackalloc byte[4];
        while (fs.Position < fs.Length)
        {
            int marker = fs.ReadByte();
            if (marker != 0xFF) continue;
            int code = fs.ReadByte();
            if (code is >= 0xC0 and <= 0xCF && code != 0xC4 && code != 0xC8 && code != 0xCC)
            {
                if (fs.Read(hdr) < 4) return null;          // skip length(2) + precision(1), then height,width
                fs.Position += 1;
                Span<byte> dim = stackalloc byte[4];
                if (fs.Read(dim) < 4) return null;
                int h = dim[0] << 8 | dim[1], w = dim[2] << 8 | dim[3];
                return (w, h, "jpeg");
            }
            if (code is 0xD8 or 0xD9 or (>= 0xD0 and <= 0xD7) or 0x01) continue;
            int len = fs.ReadByte() << 8 | fs.ReadByte();   // segment length includes these 2 bytes
            if (len < 2) return null;
            fs.Position += len - 2;
        }
        return null;
    }

    private static (int, int, string)? WebpSize(string path)
    {
        try
        {
            var b = File.ReadAllBytes(path);
            if (b.Length < 30) return null;
            string fourcc = System.Text.Encoding.ASCII.GetString(b, 12, 4);
            if (fourcc == "VP8 ")  // lossy
                return (((b[26] | b[27] << 8) & 0x3FFF), ((b[28] | b[29] << 8) & 0x3FFF), "webp");
            if (fourcc == "VP8L") // lossless
            {
                int bits = b[21] | b[22] << 8 | b[23] << 16 | b[24] << 24;
                return ((bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1, "webp");
            }
            if (fourcc == "VP8X") // extended
                return ((b[24] | b[25] << 8 | b[26] << 16) + 1, (b[27] | b[28] << 8 | b[29] << 16) + 1, "webp");
            return null;
        }
        catch { return null; }
    }

    /// <summary>Resize / convert / rotate an image via ImageMagick (magick/convert) or ffmpeg if either is
    /// installed. Returns (ok, dstPath-or-message). Degrades clearly when no image tool is present.</summary>
    public static (bool ok, string msg) TransformImage(string srcAbs, string dstAbs, string op, string arg)
    {
        if (!File.Exists(srcAbs)) return (false, "no such image: " + srcAbs);
        string? dstDir = Path.GetDirectoryName(dstAbs);
        if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);

        foreach (var im in new[] { "magick", "convert" })   // ImageMagick (magick on v7, convert on v6)
        {
            var a = new List<string> { srcAbs };
            switch (op)
            {
                case "resize": a.Add("-resize"); a.Add(arg.Length > 0 ? arg : "512x512"); break;
                case "rotate": a.Add("-rotate"); a.Add(arg.Length > 0 ? arg : "90"); break;
                // "convert" needs no extra flags - the output extension picks the format
            }
            a.Add(dstAbs);
            var (code, outp) = RunExe(im, a);
            if (code == 127) continue;                       // tool absent, try the next
            return code == 0 ? (true, dstAbs) : (false, outp.Trim());
        }

        if (op != "rotate")                                  // ffmpeg can resize/convert (not simple rotate here)
        {
            var a = new List<string> { "-y", "-i", srcAbs };
            if (op == "resize")
            {
                var wh = (arg.Length > 0 ? arg : "512x512").Split('x', 'X');
                a.Add("-vf"); a.Add($"scale={wh[0]}:{(wh.Length > 1 && wh[1].Length > 0 ? wh[1] : "-1")}");
            }
            a.Add(dstAbs);
            var (code, outp) = RunExe("ffmpeg", a);
            if (code != 127) return code == 0 ? (true, dstAbs) : (false, outp.Trim());
        }

        return (false, "no image tool found - install ImageMagick (magick/convert) or ffmpeg");
    }

    // Run an executable with arguments; returns (exitCode, combinedOutput). Exit 127 means "not installed".
    private static (int code, string outp) RunExe(string exe, IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return (127, "could not start " + exe);
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(60_000)) { try { p.Kill(true); } catch { } return (124, "timed out"); }
            return (p.ExitCode, o);
        }
        catch (System.ComponentModel.Win32Exception) { return (127, exe + " not found"); }
        catch (Exception ex) { return (1, ex.Message); }
    }

    // =====================================================================
    //  helpers
    // =====================================================================

    private static IEnumerable<string> EnumerateFilesSafe(string dir)
    {
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            string cur = stack.Pop();
            string[] subs;
            try { subs = Directory.GetDirectories(cur); } catch { subs = Array.Empty<string>(); }
            foreach (var s in subs)
                if (!SkipDirs.Contains(Path.GetFileName(s))) stack.Push(s);
            string[] files;
            try { files = Directory.GetFiles(cur); } catch { files = Array.Empty<string>(); }
            foreach (var f in files) yield return f;
        }
    }

    private static bool IsBinary(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            int n = (int)Math.Min(fs.Length, 4096);
            var buf = new byte[n];
            int read = fs.Read(buf, 0, n);
            for (int i = 0; i < read; i++) if (buf[i] == 0) return true;   // NUL byte ⇒ treat as binary
            return false;
        }
        catch { return true; }
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    private static int CountOccurrences(string text, string find)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(find, i, StringComparison.Ordinal)) >= 0) { n++; i += find.Length; }
        return n;
    }

    private static string ReplaceFirst(string text, string find, string replace)
    {
        int i = text.IndexOf(find, StringComparison.Ordinal);
        return i < 0 ? text : text.Substring(0, i) + replace + text.Substring(i + find.Length);
    }

    // Translate a glob (supporting ** , * , ? and a trailing-dir match) into a whole-string regex over a
    // forward-slash relative path.
    private static Regex GlobToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char ch = glob[i];
            switch (ch)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        sb.Append(".*");
                        i++;
                        if (i + 1 < glob.Length && glob[i + 1] == '/') i++;   // **/ matches zero or more dirs
                    }
                    else sb.Append("[^/]*");
                    break;
                case '?': sb.Append("[^/]"); break;
                case '.': sb.Append("\\."); break;
                case '/': sb.Append('/'); break;
                default:
                    if (!char.IsLetterOrDigit(ch)) sb.Append('\\');
                    sb.Append(ch);
                    break;
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
