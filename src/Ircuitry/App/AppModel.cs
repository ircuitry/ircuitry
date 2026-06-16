using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Ircuitry.Core;
using Ircuitry.Graph;

namespace Ircuitry.App;

/// <summary>Root application state: a workspace of bots, persisted to one .ircuitry file.</summary>
public sealed class AppModel
{
    public readonly List<Bot> Bots = new();
    public int Active;
    public bool Dirty;

    private string _lastPersisted = "";   // the JSON we last wrote/loaded - distinguishes our own writes from external edits

    public Bot ActiveBot => Bots[Math.Clamp(Active, 0, Bots.Count - 1)];

    // Data lives in ~/ircuitry, unless IRCUITRY_HOME points elsewhere (sandboxed/alternate workspaces,
    // headless/test runs) - honoured everywhere WorkspaceDir is read, including the MCP server/bridge.
    public static string WorkspaceDir
    {
        get
        {
            var ov = Environment.GetEnvironmentVariable("IRCUITRY_HOME");
            return !string.IsNullOrEmpty(ov)
                ? ov
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ircuitry");
        }
    }
    public string WorkspacePath => Path.Combine(WorkspaceDir, "workspace.ircuitry");
    public string ProjectName => "workspace.ircuitry";

    public AppModel()
    {
        MigrateLegacyData();
        if (!TryLoad())
        {
            Bots.Add(SeedDemoBot());
            ActiveBot.Log.Add(LogLevel.System, "ircuitry online. Build a workflow, set a connection, press RUN BOT.");
        }
    }

    /// <summary>One-time carry-over from the old ~/obbie home to ~/ircuitry: workspace, secrets and the
    /// files/data folders. Only the known artifacts are copied (the old folder may coincide with a source
    /// checkout, so we never blanket-copy it), and the old copies are left untouched as a backup. No-op
    /// once a workspace.ircuitry exists.</summary>
    private static void MigrateLegacyData()
    {
        try
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IRCUITRY_HOME"))) return;   // explicit workspace, nothing to migrate
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var oldDir = Path.Combine(home, "obbie");
            var newDir = WorkspaceDir;
            if (File.Exists(Path.Combine(newDir, "workspace.ircuitry")) || !Directory.Exists(oldDir)) return;
            Directory.CreateDirectory(newDir);

            void CopyFile(string srcName, string dstName)
            {
                var src = Path.Combine(oldDir, srcName); var dst = Path.Combine(newDir, dstName);
                if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst);
            }
            void CopyTree(string name)
            {
                var src = Path.Combine(oldDir, name); var dst = Path.Combine(newDir, name);
                if (Directory.Exists(src) && !Directory.Exists(dst)) CopyDir(src, dst);
            }
            CopyFile("workspace.obbie", "workspace.ircuitry");
            CopyFile("secrets.json", "secrets.json");
            CopyTree("files");
            CopyTree("data");
        }
        catch { /* migration is best-effort */ }
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: false);
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    public void MarkDirty() => Dirty = true;

    public Bot AddBot(string template = "blank")
    {
        var bot = new Bot($"bot-{Bots.Count + 1}");
        bot.Settings.Nick = Ircuitry.Core.BakeryNames.Random();   // a fresh cozy default handle per bot
        BuildTemplate(bot.Graph, template);
        bot.Log.Add(LogLevel.System, bot.Graph.Nodes.Count == 0
            ? "New blank bot. Add an event node and a connection."
            : $"New bot from “{template}” template - {bot.Graph.Nodes.Count} node(s) ready.");
        Bots.Add(bot);
        Active = Bots.Count - 1;
        Dirty = true;
        Ircuitry.Core.Achievements.BotCreated();   // milestone tracking (first bot, 10th, ...)
        return bot;
    }

    /// <summary>Create a new bot tab from a ready-made graph (e.g. a Bot Bakery merge), cloning the chosen
    /// connection settings so the new bot is independent of its sources.</summary>
    public Bot AddBotFrom(string name, NodeGraph graph, System.Collections.Generic.IEnumerable<Ircuitry.Irc.IrcSettings>? servers = null)
    {
        var bot = new Bot(name.Trim().Length > 0 ? name.Trim() : $"bot-{Bots.Count + 1}");
        bot.Graph = graph;
        if (servers != null)
        {
            bot.Servers.Clear();
            foreach (var s in servers) bot.Servers.Add(s.Clone());
            if (bot.Servers.Count == 0) bot.Servers.Add(new Ircuitry.Irc.IrcSettings());
        }
        bot.Log.Add(LogLevel.System, $"🧁 baked “{bot.Name}” - {graph.Nodes.Count} node(s) merged in.");
        Bots.Add(bot);
        Active = Bots.Count - 1;
        Dirty = true;
        Ircuitry.Core.Achievements.BotCreated();
        return bot;
    }

    /// <summary>Starter workflows offered when creating a bot: (key, emoji, label, blurb).</summary>
    public static readonly (string Key, string Icon, string Label, string Blurb)[] Templates =
    {
        ("blank",    "📄", "Blank",        "Start from an empty canvas."),
        ("pingpong", "🏓", "Ping-Pong",    "Classic !ping → pong command."),
        ("greeter",  "👋", "Welcomer",     "Greet people when they join a channel."),
        ("reactor",  "💜", "Auto-React",   "React with an emoji when a keyword appears."),
        ("ai",       "🤖", "AI Chatbot",   "Answer !ask questions with any OpenAI-compatible model."),
    };

    private static void BuildTemplate(NodeGraph g, string template)
    {
        Node N(string type, float x, float y) => g.Add(NodeCatalog.Get(type), new Vector2(x, y));
        switch (template)
        {
            case "pingpong":
            {
                var cmd = N("event.command", -280, -40); cmd.SetParam("command", "ping");
                var reply = N("action.reply", 60, -40); reply.SetParam("message", "pong! 🏓");
                g.Connect(cmd.Id, 0, reply.Id, 0);
                break;
            }
            case "greeter":
            {
                var join = N("event.join", -280, -40);
                var reply = N("action.reply", 60, -40); reply.SetParam("message", "welcome to {channel}, {nick}! 🎉");
                g.Connect(join.Id, 0, reply.Id, 0);
                break;
            }
            case "reactor":
            {
                var msg = N("event.message", -320, -40);
                var has = N("filter.contains", -20, -40); has.SetParam("needle", "ircuitry");
                var react = N("action.react", 300, -60); react.SetParam("emoji", "💜");
                g.Connect(msg.Id, 0, has.Id, 0);
                g.Connect(msg.Id, 1, has.Id, 1);
                g.Connect(has.Id, 0, react.Id, 0);
                break;
            }
            case "ai":
            {
                var cmd = N("event.command", -360, -40); cmd.SetParam("command", "ask");
                var ai = N("ai.reply", -40, -40); ai.SetParam("prompt", "{args}");
                var reply = N("action.reply", 320, -40);
                g.Connect(cmd.Id, 0, ai.Id, 0);     // exec
                g.Connect(ai.Id, 0, reply.Id, 0);   // then → reply exec
                g.Connect(ai.Id, 1, reply.Id, 1);   // AI text → reply message
                break;
            }
            // "blank" and anything unknown → empty canvas
        }
    }

    public void RemoveBot(int index)
    {
        if (Bots.Count <= 1 || index < 0 || index >= Bots.Count) return;
        Bots[index].Runtime.Stop();
        Bots.RemoveAt(index);
        Active = Math.Clamp(Active, 0, Bots.Count - 1);
        Dirty = true;
    }

    public void SetActive(int index)
    {
        if (index >= 0 && index < Bots.Count) Active = index;
    }

    public bool Save(bool announce = true)
    {
        try
        {
            Directory.CreateDirectory(WorkspaceDir);
            // keep one backup of the previous save as a safety net
            if (File.Exists(WorkspacePath)) { try { File.Copy(WorkspacePath, WorkspacePath + ".bak", true); } catch { } }
            var json = WorkspaceSerializer.Save(Bots, Active);
            File.WriteAllText(WorkspacePath, json);
            _lastPersisted = json;        // remember our own write so the file-watcher ignores it
            Dirty = false;
            if (announce) ActiveBot.Log.Add(LogLevel.System, "saved → " + WorkspacePath);
            return true;
        }
        catch (Exception ex) { ActiveBot.Log.Add(LogLevel.Error, "save failed: " + ex.Message); return false; }
    }

    // ---- export / import individual flows as .ircbot files (JSON content) ----
    public string ExportActive()
    {
        try
        {
            Directory.CreateDirectory(WorkspaceDir);
            string path = Path.Combine(WorkspaceDir, SafeName(ActiveBot.Name) + ".ircbot");
            File.WriteAllText(path, GraphSerializer.Save(ActiveBot.Graph, ActiveBot.Name));
            ActiveBot.Log.Add(LogLevel.System, "exported nodes → " + path);
            return path;
        }
        catch (Exception ex) { ActiveBot.Log.Add(LogLevel.Error, "export failed: " + ex.Message); return ""; }
    }

    public Bot? ImportFile(string path)
    {
        try
        {
            var (g, name) = GraphSerializer.Load(File.ReadAllText(path), out var skipped);
            var bot = new Bot(name.Length > 0 ? name : Path.GetFileNameWithoutExtension(path)) { Graph = g };
            Bots.Add(bot);
            Active = Bots.Count - 1;
            Dirty = true;
            bot.Log.Add(LogLevel.System, $"imported {g.Nodes.Count} node(s) from {Path.GetFileName(path)}");
            if (skipped.Count > 0) bot.Log.Add(LogLevel.Warn, "⚠ " + GraphSerializer.SkippedWarning(skipped));
            return bot;
        }
        catch (Exception ex) { ActiveBot.Log.Add(LogLevel.Error, "import failed: " + ex.Message); return null; }
    }

    /// <summary>Import a .ircbot from its JSON text (used by deep-link workflow installs) as a new bot tab.</summary>
    public Bot? ImportText(string json)
    {
        try
        {
            var (g, name) = GraphSerializer.Load(json, out var skipped);
            var bot = new Bot(name.Length > 0 ? name : "imported") { Graph = g };
            Bots.Add(bot);
            Active = Bots.Count - 1;
            Dirty = true;
            bot.Log.Add(LogLevel.System, $"imported {g.Nodes.Count} node(s)");
            if (skipped.Count > 0) bot.Log.Add(LogLevel.Warn, "⚠ " + GraphSerializer.SkippedWarning(skipped));
            return bot;
        }
        catch (Exception ex) { ActiveBot.Log.Add(LogLevel.Error, "import failed: " + ex.Message); return null; }
    }

    public IReadOnlyList<string> Importable()
    {
        try { return Directory.Exists(WorkspaceDir) ? Directory.GetFiles(WorkspaceDir, "*.ircbot") : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static string SafeName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '-');
        s = s.Trim();
        return s.Length == 0 ? "bot" : s;
    }

    /// <summary>
    /// If the workspace file on disk differs from what we last wrote/loaded (an external edit),
    /// reload it - discarding unsaved in-memory changes. Returns true if a reload happened.
    /// </summary>
    public bool ReloadIfChangedOnDisk()
    {
        try
        {
            if (!File.Exists(WorkspacePath)) return false;
            var text = File.ReadAllText(WorkspacePath);
            if (text.Length == 0 || text == _lastPersisted) return false;   // our own write, or unchanged
            var (bots, active) = WorkspaceSerializer.Load(text);
            if (bots.Count == 0) return false;

            // Never quit a LIVE bot just because the workspace changed on disk - an AI self-edit, the MCP
            // server, git or another editor. A running bot keeps its own in-memory graph, so we keep the
            // live instance and fold the reloaded definition into its (editor) graph in place: the canvas
            // shows the change and a restart picks it up. Only stopped bots are swapped out wholesale.
            int keptLive = 0;
            var merged = new List<Bot>();
            var consumed = new HashSet<Bot>();
            foreach (var cur in Bots)
            {
                if (!cur.Runtime.Running) { try { cur.Runtime.Stop(); } catch { } continue; }
                var match = bots.FirstOrDefault(nb => nb.Name == cur.Name && !consumed.Contains(nb));
                if (match != null) { consumed.Add(match); try { cur.Graph.ReplaceWith(match.Graph); } catch { } }
                merged.Add(cur);
                keptLive++;
            }
            foreach (var nb in bots) if (!consumed.Contains(nb)) merged.Add(nb);

            Bots.Clear();
            Bots.AddRange(merged);
            Active = Math.Clamp(active, 0, Bots.Count - 1);
            _lastPersisted = text;
            Dirty = false;
            ActiveBot.Log.Add(LogLevel.System, keptLive > 0
                ? $"⟳ reloaded workspace from disk (external change; {keptLive} running bot(s) kept live - restart to apply)"
                : "⟳ reloaded workspace from disk (external change)");
            return true;
        }
        catch (Exception ex) { ActiveBot.Log.Add(LogLevel.Error, "reload failed: " + ex.Message); return false; }
    }

    // ---- snapshots: named copies of the whole workspace (the File menu's Save As / Load) ----
    public string SnapshotDir => Path.Combine(WorkspaceDir, "snapshots");

    public string SaveSnapshot()
    {
        try
        {
            Save(announce: false);                       // flush live state to disk first
            Directory.CreateDirectory(SnapshotDir);
            string name = "workspace-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".ircuitry";
            string path = Path.Combine(SnapshotDir, name);
            File.Copy(WorkspacePath, path, true);
            ActiveBot.Log.Add(LogLevel.System, "📸 snapshot saved → " + name);
            return path;
        }
        catch (Exception ex) { ActiveBot.Log.Add(LogLevel.Error, "snapshot failed: " + ex.Message); return ""; }
    }

    public string[] Snapshots()
    {
        try
        {
            if (!Directory.Exists(SnapshotDir)) return Array.Empty<string>();
            var files = Directory.GetFiles(SnapshotDir, "*.ircuitry");
            Array.Sort(files); Array.Reverse(files);     // newest first
            return files;
        }
        catch { return Array.Empty<string>(); }
    }

    public bool RestoreSnapshot(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            Save(announce: false);                       // keep current state recoverable
            var (bots, active) = WorkspaceSerializer.Load(File.ReadAllText(path));
            if (bots.Count == 0) return false;
            foreach (var b in Bots) { try { b.Runtime.Stop(); } catch { } }
            Bots.Clear();
            Bots.AddRange(bots);
            Active = Math.Clamp(active, 0, Bots.Count - 1);
            Dirty = true;
            ActiveBot.Log.Add(LogLevel.System, "↩ restored snapshot " + Path.GetFileName(path));
            return true;
        }
        catch (Exception ex) { ActiveBot.Log.Add(LogLevel.Error, "restore failed: " + ex.Message); return false; }
    }

    private bool TryLoad()
    {
        try
        {
            if (!File.Exists(WorkspacePath)) return false;
            var text = File.ReadAllText(WorkspacePath);
            var (bots, active) = WorkspaceSerializer.Load(text);
            if (bots.Count == 0) return false;
            Bots.AddRange(bots);
            Active = active;
            _lastPersisted = text;
            ActiveBot.Log.Add(LogLevel.System, "loaded workspace - " + bots.Count + " bot(s).");
            return true;
        }
        catch { return false; }
    }

    private static Bot SeedDemoBot()
    {
        var bot = new Bot("main");
        bot.Settings.Nick = Ircuitry.Core.BakeryNames.Random();
        var g = bot.Graph;
        Node N(string type, float x, float y) => g.Add(NodeCatalog.Get(type), new Vector2(x, y));

        // chain 1: !ping -> pong
        var cmd = N("event.command", -300, -150); cmd.SetParam("command", "ping");
        var reply = N("action.reply", 40, -150); reply.SetParam("message", "pong! 🏓");
        g.Connect(cmd.Id, 0, reply.Id, 0);

        // chain 2: message contains 'ircuitry' -> reply with the nick
        var msg = N("event.message", -300, 90);
        var contains = N("filter.contains", 40, 90); contains.SetParam("needle", "ircuitry");
        var reply2 = N("action.reply", 360, 70); reply2.SetParam("message", "you rang, {nick}? 👋");
        g.Connect(msg.Id, 0, contains.Id, 0);
        g.Connect(msg.Id, 1, contains.Id, 1);
        g.Connect(contains.Id, 0, reply2.Id, 0);
        return bot;
    }
}
