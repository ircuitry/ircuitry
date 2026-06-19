using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ircuitry.App;

/// <summary>The snippet shelf's backing store: reusable graph fragments saved as .ircbot files under
/// ~/ircuitry/snippets. A snippet is just a selection serialised through <c>GraphSerializer</c>, so it can be
/// dropped back in as a fresh, still-editable copy (unlike a baked node, which is sealed).</summary>
public static class SnippetStore
{
    public static string Dir => Path.Combine(AppModel.WorkspaceDir, "snippets");

    public static string Save(string json, string name)
    {
        Directory.CreateDirectory(Dir);
        string safe = SafeName(name);
        string path = Path.Combine(Dir, safe + ".ircbot");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(Dir, safe + "-" + n + ".ircbot");
        File.WriteAllText(path, json);
        return Path.GetFileNameWithoutExtension(path);
    }

    public static List<(string name, string path)> List()
    {
        var list = new List<(string, string)>();
        try { if (Directory.Exists(Dir)) foreach (var f in Directory.GetFiles(Dir, "*.ircbot")) list.Add((Path.GetFileNameWithoutExtension(f), f)); }
        catch { }
        list.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return list;
    }

    public static string Read(string path) { try { return File.ReadAllText(path); } catch { return ""; } }
    public static void Delete(string path) { try { File.Delete(path); } catch { } }

    private static string SafeName(string s)
    {
        var c = new string((s ?? "").Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or ' ' ? ch : '-').ToArray()).Trim();
        return c.Length == 0 ? "snippet" : c;
    }
}
