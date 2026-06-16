using System;
using System.Collections.Generic;
using System.Linq;
using Ircuitry.Net;

namespace Ircuitry.Graph;

/// <summary>
/// The curated toolset the Programmer AI hands to the model: read / write / edit / search / move files plus
/// (optionally) run commands, and the hidden <c>send_codebase</c>. Every tool runs through
/// <see cref="CodeTools"/>, so it is confined to the project root - the model never learns the root, never
/// sees the zip-and-upload behind <c>send_codebase</c>, and cannot widen its own sandbox.
/// </summary>
public static class CodeAgent
{
    private readonly record struct Tool(string Name, string Desc, (string, string)[] Args, Func<string, Dictionary<string, string>, string> Run);

    private static string A(Dictionary<string, string> a, string k) => a.TryGetValue(k, out var v) ? v ?? "" : "";
    private static int I(Dictionary<string, string> a, string k) => int.TryParse(A(a, k), out var n) ? n : 0;

    // the file/search/navigation tools - all path arguments are relative to the (hidden) project root
    private static readonly Tool[] FileTools =
    {
        new("read_file", "Read a file's contents. Optionally pass start_line/end_line to read a range.",
            new[] { ("path", "file path, relative to the project"), ("start_line", "optional first line"), ("end_line", "optional last line") },
            (root, a) => CodeTools.Read(root, A(a, "path"), I(a, "start_line"), I(a, "end_line"))),
        new("write_file", "Create or overwrite a file with content (parent folders are created). Use for new files or full rewrites.",
            new[] { ("path", "file path"), ("content", "the full file contents") },
            (root, a) => { CodeTools.Write(root, A(a, "path"), A(a, "content")); return "wrote " + A(a, "path"); }),
        new("edit_file", "Replace an exact, unique string in a file with new text - the precise way to change code.",
            new[] { ("path", "file path"), ("find", "exact text to replace (must be unique in the file)"), ("replace", "the new text") },
            (root, a) => { CodeTools.Edit(root, A(a, "path"), A(a, "find"), A(a, "replace"), false); return "edited " + A(a, "path"); }),
        new("insert_lines", "Insert text into a file after a given line number (0 = at the top).",
            new[] { ("path", "file path"), ("after_line", "line number to insert after"), ("content", "text to insert") },
            (root, a) => { int at = CodeTools.Insert(root, A(a, "path"), I(a, "after_line"), A(a, "content")); return "inserted at line " + at; }),
        new("append_file", "Append text to the end of a file (creating it if needed).",
            new[] { ("path", "file path"), ("content", "text to append") },
            (root, a) => { CodeTools.Append(root, A(a, "path"), A(a, "content")); return "appended to " + A(a, "path"); }),
        new("list_dir", "List the files and folders in a directory (folders end with /). Blank lists the project root.",
            new[] { ("path", "directory (blank = project root)") },
            (root, a) => Empty(CodeTools.List(root, A(a, "path")), "(empty)")),
        new("project_tree", "Show a compact tree of the project (skipping vcs/build folders) to get oriented.",
            new[] { ("path", "directory (blank = project root)") },
            (root, a) => CodeTools.Tree(root, A(a, "path"), 3)),
        new("find_files", "Find files by glob pattern (e.g. **/*.py, src/*.ts).",
            new[] { ("pattern", "glob pattern") },
            (root, a) => Empty(CodeTools.Glob(root, A(a, "pattern")), "(no files match)")),
        new("search_code", "Search the project for a regular expression; returns matches as path:line: text.",
            new[] { ("pattern", "a regular expression"), ("glob", "optional: only files matching this glob") },
            (root, a) => Empty(CodeTools.Grep(root, A(a, "pattern"), A(a, "glob")), "(no matches)")),
        new("replace_across", "Replace an exact string with another across every matching file. Returns files changed.",
            new[] { ("find", "exact text"), ("replace", "new text"), ("glob", "optional file glob") },
            (root, a) => { int n = CodeTools.ReplaceAcross(root, A(a, "find"), A(a, "replace"), A(a, "glob")); return $"changed {n} file(s)"; }),
        new("make_dir", "Create a directory (and any missing parents).",
            new[] { ("path", "directory path") },
            (root, a) => { CodeTools.Mkdir(root, A(a, "path")); return "created " + A(a, "path"); }),
        new("move_path", "Move or rename a file or folder.",
            new[] { ("from", "source path"), ("to", "destination path") },
            (root, a) => { CodeTools.Move(root, A(a, "from"), A(a, "to")); return $"moved {A(a, "from")} -> {A(a, "to")}"; }),
        new("copy_path", "Copy a file or folder.",
            new[] { ("from", "source path"), ("to", "destination path") },
            (root, a) => { CodeTools.Copy(root, A(a, "from"), A(a, "to")); return $"copied {A(a, "from")} -> {A(a, "to")}"; }),
        new("delete_path", "Delete a file or folder (recursively).",
            new[] { ("path", "path to delete") },
            (root, a) => { CodeTools.Delete(root, A(a, "path")); return "deleted " + A(a, "path"); }),
        new("file_info", "Report a path's type, size, line count and last-modified time.",
            new[] { ("path", "path") },
            (root, a) => CodeTools.Stat(root, A(a, "path"))),
        new("path_exists", "Check whether a path exists; returns true or false.",
            new[] { ("path", "path") },
            (root, a) => CodeTools.Exists(root, A(a, "path")) ? "true" : "false"),
        new("code_outline", "List the top-level definitions (classes, functions, methods) in a source file.",
            new[] { ("path", "file path") },
            (root, a) => Empty(CodeTools.Outline(root, A(a, "path")), "(no definitions found)")),
        new("diff_files", "Show the line differences between two files.",
            new[] { ("a", "file A"), ("b", "file B") },
            (root, a) => CodeTools.Diff(root, A(a, "a"), A(a, "b"))),
        new("project_stats", "Summarise the project: file count, total lines, common file types.",
            Array.Empty<(string, string)>(),
            (root, a) => CodeTools.Stats(root)),
    };

    private static readonly Tool RunCmd = new("run_command",
        "Run a shell command in the project directory (build, test, lint, git, etc.) and get its output.",
        new[] { ("command", "the shell command to run") },
        (root, a) => Empty(CodeTools.Run(root, A(a, "command"), 60), "(no output)"));

    private static readonly (string, string)[] SendArgs = { ("note", "optional one-line note about what you delivered") };

    /// <summary>The tool definitions the model is offered: the file tools, optionally run_command, and the
    /// hidden send_codebase (which the model just sees as "deliver the finished codebase").</summary>
    public static List<Ai.ToolDef> ToolDefs(bool allowCommands, bool includeSend)
    {
        var defs = FileTools.Select(t => new Ai.ToolDef(t.Name, t.Desc, t.Args.ToList())).ToList();
        if (allowCommands) defs.Add(new Ai.ToolDef(RunCmd.Name, RunCmd.Desc, RunCmd.Args.ToList()));
        if (includeSend)
            defs.Add(new Ai.ToolDef("send_codebase",
                "Deliver the finished codebase you have been working on. Call this once your changes are complete. Returns a link to share.",
                SendArgs.ToList()));
        return defs;
    }

    /// <summary>
    /// Run one tool call. Returns the result string, or null if <paramref name="name"/> is not one of the
    /// built-in code tools (so the caller can fall back to externally-wired tools). Confinement breaches and
    /// IO errors come back as a tidy <c>error: …</c> string the model can react to.
    /// </summary>
    public static string? Dispatch(string root, string name, Dictionary<string, string> args, bool allowCommands,
        Func<Dictionary<string, string>, string>? onSend)
    {
        try
        {
            if (name == "send_codebase") return onSend != null ? onSend(args) : "(sending is not configured)";
            if (name == "run_command") return allowCommands ? RunCmd.Run(root, args) : "(running commands is disabled for this Programmer AI)";
            foreach (var t in FileTools) if (t.Name == name) return t.Run(root, args);
            return null;   // not a code tool - maybe an externally-wired one
        }
        catch (CodeAccessException ex) { return "error: " + ex.Message; }
        catch (Exception ex) { return "error: " + ex.Message; }
    }

    /// <summary>True if <paramref name="name"/> is one of the built-in code tools / send_codebase.</summary>
    public static bool Handles(string name) =>
        name is "send_codebase" or "run_command" || FileTools.Any(t => t.Name == name);

    private static string Empty(string s, string ifEmpty) => string.IsNullOrEmpty(s) ? ifEmpty : s;
}
