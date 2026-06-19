using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;
using Ircuitry.Graph;
using Ircuitry.Irc;

namespace Ircuitry.App;

/// <summary>
/// Persists the whole workspace - every bot's workflow AND its connection
/// settings - to a single .ircuitry JSON file.
/// </summary>
public static class WorkspaceSerializer
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class Doc
    {
        public string format { get; set; } = "ircuitry.workspace.v1";
        public int active { get; set; }
        public List<BotDoc> bots { get; set; } = new();
        public List<GroupDoc>? groups { get; set; }                 // browser-style tab groups (absent in old files)
    }

    private sealed class BotDoc
    {
        public string name { get; set; } = "bot";
        public string? groupId { get; set; }                        // the tab group this bot belongs to (null = ungrouped)
        public ConnDoc? connection { get; set; }                    // legacy single connection (still read)
        public List<ConnDoc>? servers { get; set; }                 // a bot may hold several servers (absent in old files)
        public Dictionary<string, string> state { get; set; } = new();
        public List<NodeDoc> nodes { get; set; } = new();
        public List<WireDoc> wires { get; set; } = new();
    }

    private sealed class GroupDoc
    {
        public string id { get; set; } = "";
        public string name { get; set; } = "Group";
        public int color { get; set; }
        public bool collapsed { get; set; }
    }

    private sealed class ConnDoc
    {
        public string label { get; set; } = "";
        public bool connectOnStartup { get; set; }
        public string host { get; set; } = "";
        public int port { get; set; } = 6697;
        public bool tls { get; set; } = true;
        public bool acceptInvalidCerts { get; set; } = true;
        public bool autoReconnect { get; set; } = true;
        public bool botMode { get; set; } = true;
        public bool advertiseCommands { get; set; } = true;
        public bool streamWorkflows { get; set; } = true;
        public string nick { get; set; } = "";
        public string user { get; set; } = "";
        public string realName { get; set; } = "";
        public string serverPass { get; set; } = "";
        public string saslUser { get; set; } = "";
        public string saslPass { get; set; } = "";
        public string channels { get; set; } = "";
    }

    private sealed class NodeDoc
    {
        public string id { get; set; } = "";
        public string type { get; set; } = "";
        public float x { get; set; }
        public float y { get; set; }
        public bool muted { get; set; }
        public bool? streamAsTool { get; set; }
        public string title { get; set; } = "";
        public int colorTag { get; set; } = -1;
        public Dictionary<string, string> @params { get; set; } = new();
    }

    private sealed class WireDoc
    {
        public string from { get; set; } = "";
        public int fromPin { get; set; }
        public string to { get; set; } = "";
        public int toPin { get; set; }
    }

    public static string Save(IReadOnlyList<Bot> bots, int active, IReadOnlyList<TabGroup>? groups = null)
    {
        var doc = new Doc { active = active };
        // only keep groups that actually have a saved member (remote-only or emptied groups are dropped)
        if (groups != null)
            doc.groups = groups.Where(g => bots.Any(b => b.GroupId == g.Id))
                .Select(g => new GroupDoc { id = g.Id, name = g.Name, color = g.ColorIndex, collapsed = g.Collapsed }).ToList();
        foreach (var b in bots)
        {
            var bd = new BotDoc { name = b.Name, groupId = b.GroupId, state = new(b.State), servers = new() };
            foreach (var s in b.Servers) bd.servers.Add(FromSettings(s));
            foreach (var n in b.Graph.Nodes)
                bd.nodes.Add(new NodeDoc { id = n.Id, type = n.TypeId, x = n.Pos.X, y = n.Pos.Y, muted = n.Muted, streamAsTool = n.StreamAsTool, title = n.Title, colorTag = n.ColorTag, @params = new(n.Params) });
            foreach (var c in b.Graph.Connections)
                bd.wires.Add(new WireDoc { from = c.FromNode, fromPin = c.FromPin, to = c.ToNode, toPin = c.ToPin });
            doc.bots.Add(bd);
        }
        return JsonSerializer.Serialize(doc, Opts);
    }

    public static (List<Bot> bots, int active, List<TabGroup> groups) Load(string json)
    {
        var doc = JsonSerializer.Deserialize<Doc>(json, Opts) ?? new Doc();
        var bots = new List<Bot>();
        var groups = (doc.groups ?? new List<GroupDoc>())
            .Select(g => new TabGroup { Id = g.id, Name = g.name, ColorIndex = g.color, Collapsed = g.collapsed }).ToList();
        foreach (var bd in doc.bots)
        {
            var bot = new Bot(bd.name) { GroupId = string.IsNullOrEmpty(bd.groupId) ? null : bd.groupId };
            bot.Servers.Clear();
            if (bd.servers is { Count: > 0 })
                foreach (var c in bd.servers) bot.Servers.Add(ToSettings(c));
            else
                bot.Servers.Add(ToSettings(bd.connection ?? new ConnDoc()));   // legacy single-connection file
            foreach (var kv in bd.state) bot.State[kv.Key] = kv.Value;
            var live = new HashSet<string>();
            foreach (var rec in bd.nodes)
            {
                if (!NodeCatalog.TryGet(rec.type, out var def)) continue;
                var n = new Node(rec.id, rec.type) { Def = def, Pos = new Vector2(rec.x, rec.y), Muted = rec.muted, StreamAsTool = rec.streamAsTool ?? def.StreamByDefault, Title = rec.title ?? "", ColorTag = rec.colorTag };
                foreach (var p in def.Params) n.Params[p.Key] = rec.@params.TryGetValue(p.Key, out var v) ? v : p.Default;
                bot.Graph.Nodes.Add(n);
                live.Add(n.Id);
            }
            foreach (var w in bd.wires)
                if (live.Contains(w.from) && live.Contains(w.to))
                    bot.Graph.Connect(w.from, w.fromPin, w.to, w.toPin);
            bots.Add(bot);
        }
        int active = doc.active >= 0 && doc.active < bots.Count ? doc.active : 0;
        return (bots, active, groups);
    }

    private static ConnDoc FromSettings(IrcSettings s) => new()
    {
        label = s.Label, connectOnStartup = s.ConnectOnStartup,
        host = s.Host, port = s.Port, tls = s.UseTls, acceptInvalidCerts = s.AcceptInvalidCerts, autoReconnect = s.AutoReconnect,
        botMode = s.BotMode, advertiseCommands = s.AdvertiseCommands, streamWorkflows = s.StreamWorkflows,
        nick = s.Nick, user = s.User, realName = s.RealName, serverPass = s.ServerPass,
        saslUser = s.SaslUser, saslPass = s.SaslPass, channels = s.Channels,
    };

    private static IrcSettings ToSettings(ConnDoc c) => new()
    {
        Label = c.label, ConnectOnStartup = c.connectOnStartup,
        Host = c.host, Port = c.port, UseTls = c.tls, AcceptInvalidCerts = c.acceptInvalidCerts, AutoReconnect = c.autoReconnect,
        BotMode = c.botMode, AdvertiseCommands = c.advertiseCommands, StreamWorkflows = c.streamWorkflows,
        Nick = c.nick, User = c.user, RealName = c.realName, ServerPass = c.serverPass,
        SaslUser = c.saslUser, SaslPass = c.saslPass, Channels = c.channels,
    };
}
