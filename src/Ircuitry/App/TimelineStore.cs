using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Ircuitry.App;

/// <summary>Persists a bot's rollback timeline to a small sidecar file so it survives restarts, kept out of the
/// main workspace file (which would otherwise balloon with dozens of graph copies). Keyed by bot name.</summary>
public static class TimelineStore
{
    private const int PersistMax = 30;   // cap the on-disk history so the file stays small
    private static string Dir => Path.Combine(AppModel.WorkspaceDir, "timeline");

    private sealed class Rec
    {
        public long t { get; set; }       // unix seconds
        public string data { get; set; } = "";
        public int nodes { get; set; }
        public int wires { get; set; }
        public string note { get; set; } = "";
        public long sig { get; set; }
    }

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    private static string PathFor(string botName)
    {
        var sb = new StringBuilder();
        foreach (char c in botName) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        string safe = sb.Length == 0 ? "bot" : sb.ToString();
        if (safe.Length > 64) safe = safe[..64];
        return Path.Combine(Dir, safe + ".tl.json");
    }

    public static void Save(string botName, IReadOnlyList<GraphVersion> timeline)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var recs = timeline.Skip(Math.Max(0, timeline.Count - PersistMax)).Select(v => new Rec
            {
                t = new DateTimeOffset(v.Time.ToUniversalTime()).ToUnixTimeSeconds(),
                data = v.Data, nodes = v.Nodes, wires = v.Wires, note = v.Note, sig = v.Sig,
            }).ToList();
            File.WriteAllText(PathFor(botName), JsonSerializer.Serialize(recs, Opts));
        }
        catch { /* timeline persistence is best-effort - never break a save over it */ }
    }

    public static List<GraphVersion> Load(string botName)
    {
        var list = new List<GraphVersion>();
        try
        {
            var p = PathFor(botName);
            if (!File.Exists(p)) return list;
            var recs = JsonSerializer.Deserialize<List<Rec>>(File.ReadAllText(p)) ?? new();
            foreach (var r in recs)
                list.Add(new GraphVersion
                {
                    Time = DateTimeOffset.FromUnixTimeSeconds(r.t).LocalDateTime,
                    Data = r.data ?? "", Nodes = r.nodes, Wires = r.wires, Note = r.note ?? "", Sig = r.sig,
                });
        }
        catch { /* corrupt/missing sidecar - start fresh */ }
        return list;
    }
}
