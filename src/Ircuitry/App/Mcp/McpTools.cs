using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Ircuitry.Core;
using Ircuitry.Graph;
using Ircuitry.Runtime;

namespace Ircuitry.App.Mcp;

/// <summary>A single MCP tool: its advertised schema and the handler that runs against an AppModel.</summary>
public sealed class McpTool
{
    public string Name = "";
    public string Description = "";
    public object Schema = new { type = "object" };
    public bool Mutates;     // when true the workspace is saved after a successful call
    public Func<JsonElement, AppModel, object> Run = (_, _) => new { };
}

/// <summary>The ircuitry MCP tool surface: introspect the node catalog, build/edit bots, validate, and
/// dry-run commands - everything an AI needs to design and verify a bot. Live connect/send are out of
/// scope for this slice (build &amp; test only).</summary>
public static class McpTools
{
    public static List<McpTool> BuildRegistry()
    {
        var t = new List<McpTool>();

        void Tool(string name, string desc, object schema, bool mutates, Func<JsonElement, AppModel, object> run) =>
            t.Add(new McpTool { Name = name, Description = desc, Schema = schema, Mutates = mutates, Run = run });

        object Obj(object props, params string[] required) =>
            new { type = "object", properties = props, required };
        object Empty() => new { type = "object", properties = new { } };
        object S(string desc) => new { type = "string", description = desc };
        object I(string desc) => new { type = "integer", description = desc };
        object B(string desc) => new { type = "boolean", description = desc };
        object BotArg() => new { type = "string", description = "Bot name or index. Defaults to the active bot." };

        // ---------------- introspection ----------------
        Tool("list_node_types",
            "List every node type the bot builder offers - categories, pins (name+kind) and params (key, type, default, choices). This is the schema to build graphs against. Optional 'category' filter.",
            Obj(new { category = S("Optional category filter: Event, Filter, Logic, Action, Data, Ai, Storage.") }),
            false, (a, app) =>
            {
                var defs = NodeCatalog.All.AsEnumerable();
                var cat = Str(a, "category");
                if (cat.Length > 0) defs = defs.Where(d => d.Category.ToString().Equals(cat, StringComparison.OrdinalIgnoreCase));
                return defs.Select(DescribeDef).ToList();
            });

        Tool("describe_node", "Full spec for one node type (pins + params).",
            Obj(new { typeId = S("e.g. event.command, action.reply, ai.reply") }, "typeId"),
            false, (a, app) => NodeCatalog.TryGet(Str(a, "typeId"), out var d) ? DescribeDef(d) : throw new Exception("unknown node type: " + Str(a, "typeId")));

        Tool("list_bots", "List the bots in the workspace with node/wire counts and connection (secrets omitted).",
            Empty(), false, (a, app) => app.Bots.Select((b, i) => new
            {
                index = i, name = b.Name, active = i == app.Active,
                nodes = b.Graph.Nodes.Count, wires = b.Graph.Connections.Count,
                connection = new { host = b.Settings.Host, port = b.Settings.Port, tls = b.Settings.UseTls, nick = b.Settings.Nick, channels = b.Settings.Channels },
            }).ToList());

        Tool("get_graph", "The full workflow of a bot as portable .ircbot JSON (nodes + connections) - the same shape set_graph accepts.",
            Obj(new { bot = BotArg() }), false, (a, app) =>
            {
                var bot = ResolveBot(a, app);
                using var doc = JsonDocument.Parse(GraphSerializer.Save(bot.Graph, bot.Name));
                return doc.RootElement.Clone();
            });

        // ---------------- build / edit ----------------
        Tool("create_bot", "Create a new blank bot and return its index.",
            Obj(new { name = S("Display name for the bot.") }), true, (a, app) =>
            {
                var b = app.AddBot("blank");
                var name = Str(a, "name");
                if (name.Length > 0) b.Name = name;
                return new { index = app.Bots.IndexOf(b), name = b.Name };
            });

        Tool("delete_bot", "Delete a bot from the workspace.",
            Obj(new { bot = BotArg() }), true, (a, app) =>
            {
                var bot = ResolveBot(a, app);
                app.RemoveBot(app.Bots.IndexOf(bot));
                return new { deleted = bot.Name, remaining = app.Bots.Count };
            });

        Tool("add_node", "Add a node to a bot's graph; returns the new node id. Optional x/y and a params object.",
            Obj(new { bot = BotArg(), typeId = S("Node type, e.g. event.command"), x = I("canvas x"), y = I("canvas y"), @params = new { type = "object", description = "Initial param values keyed by param key." } }, "typeId"),
            true, (a, app) =>
            {
                var bot = ResolveBot(a, app);
                var typeId = Str(a, "typeId");
                if (!NodeCatalog.TryGet(typeId, out var def)) throw new Exception("unknown node type: " + typeId);
                var node = bot.Graph.Add(def, new Vector2(Flt(a, "x", 0), Flt(a, "y", 0)));
                if (Has(a, "params") && a.GetProperty("params").ValueKind == JsonValueKind.Object)
                    foreach (var p in a.GetProperty("params").EnumerateObject())
                        node.SetParam(p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString());
                return new { id = node.Id, typeId = node.TypeId };
            });

        Tool("set_param", "Set one parameter on a node.",
            Obj(new { bot = BotArg(), nodeId = S("target node id"), key = S("param key"), value = S("new value (may contain {{secret.NAME}})") }, "nodeId", "key", "value"),
            true, (a, app) =>
            {
                var n = NodeOf(ResolveBot(a, app), Str(a, "nodeId"));
                n.SetParam(Str(a, "key"), Str(a, "value"));
                return new { id = n.Id, key = Str(a, "key"), value = n.GetParam(Str(a, "key")) };
            });

        Tool("connect", "Wire an output pin to an input pin (pin indices are 0-based, in pin order). Exec\u2192exec or data\u2192data.",   // intentional unicode (rightwards arrow)
            Obj(new { bot = BotArg(), from = S("source node id"), fromPin = I("source output pin index"), to = S("target node id"), toPin = I("target input pin index") }, "from", "to"),
            true, (a, app) =>
            {
                var bot = ResolveBot(a, app);
                bool ok = bot.Graph.Connect(Str(a, "from"), Int(a, "fromPin", 0), Str(a, "to"), Int(a, "toPin", 0));
                return new { connected = ok };
            });

        Tool("disconnect", "Remove a specific wire.",
            Obj(new { bot = BotArg(), from = S("source node id"), fromPin = I("output pin"), to = S("target node id"), toPin = I("input pin") }, "from", "to"),
            true, (a, app) =>
            {
                var bot = ResolveBot(a, app);
                string from = Str(a, "from"), to = Str(a, "to"); int fp = Int(a, "fromPin", 0), tp = Int(a, "toPin", 0);
                var c = bot.Graph.Connections.FirstOrDefault(x => x.FromNode == from && x.FromPin == fp && x.ToNode == to && x.ToPin == tp)
                        ?? throw new Exception("no such connection");
                bot.Graph.Disconnect(c);
                return new { disconnected = true };
            });

        Tool("remove_node", "Delete a node (and its wires) from a bot.",
            Obj(new { bot = BotArg(), nodeId = S("node id") }, "nodeId"),
            true, (a, app) => { var bot = ResolveBot(a, app); var n = NodeOf(bot, Str(a, "nodeId")); bot.Graph.Remove(n); return new { removed = n.Id }; });

        Tool("set_graph", "Replace a bot's entire workflow from .ircbot JSON (nodes + connections). Use get_graph's shape.",
            Obj(new { bot = BotArg(), json = S("the .ircbot JSON") }, "json"),
            true, (a, app) =>
            {
                var bot = ResolveBot(a, app);
                var (g, _) = GraphSerializer.Load(Str(a, "json"));
                bot.Graph.ReplaceWith(g);
                return new { nodes = bot.Graph.Nodes.Count, wires = bot.Graph.Connections.Count };
            });

        Tool("set_connection", "Set IRC connection settings on a bot (only the fields you pass).",
            Obj(new { bot = BotArg(), host = S("server host"), port = I("port"), tls = B("use TLS"), nick = S("nick"), channels = S("comma/space separated channels"), realName = S("real name"), saslUser = S("SASL user (may be {{secret.X}})"), saslPass = S("SASL pass (may be {{secret.X}})"), serverPass = S("server password (may be {{secret.X}})") }),
            true, (a, app) =>
            {
                var s = ResolveBot(a, app).Settings;
                if (Has(a, "host")) s.Host = Str(a, "host");
                if (Has(a, "port")) s.Port = Int(a, "port", s.Port);
                if (Has(a, "tls")) s.UseTls = Bool(a, "tls", s.UseTls);
                if (Has(a, "nick")) s.Nick = Str(a, "nick");
                if (Has(a, "channels")) s.Channels = Str(a, "channels");
                if (Has(a, "realName")) s.RealName = Str(a, "realName");
                if (Has(a, "saslUser")) s.SaslUser = Str(a, "saslUser");
                if (Has(a, "saslPass")) s.SaslPass = Str(a, "saslPass");
                if (Has(a, "saslMech")) s.SaslMech = Str(a, "saslMech");
                if (Has(a, "clientCertPath")) s.ClientCertPath = Str(a, "clientCertPath");
                if (Has(a, "clientCertPass")) s.ClientCertPass = Str(a, "clientCertPass");
                if (Has(a, "serverPass")) s.ServerPass = Str(a, "serverPass");
                return new { host = s.Host, port = s.Port, tls = s.UseTls, nick = s.Nick, channels = s.Channels };
            });

        Tool("auto_layout", "Tidy a bot's graph into clean left\u2192right layers.",   // intentional unicode (rightwards arrow)
            Obj(new { bot = BotArg() }), true, (a, app) => { var bot = ResolveBot(a, app); new Ircuitry.Editor.GraphEditor(bot.Graph).AutoLayout(); return new { nodes = bot.Graph.Nodes.Count }; });

        Tool("rename_bot", "Rename a bot.",
            Obj(new { bot = BotArg(), name = S("new display name") }, "name"),
            true, (a, app) => { var bot = ResolveBot(a, app); var nm = Str(a, "name").Trim(); if (nm.Length > 0) bot.Name = nm; return new { name = bot.Name }; });

        Tool("set_state", "Set a bot's persistent variables. Pass a 'state' object to replace them all, or key+value for one.",
            Obj(new { bot = BotArg(), state = new { type = "object", description = "all variables (replaces existing)" }, key = S("one variable name"), value = S("its value") }),
            true, (a, app) =>
            {
                var bot = ResolveBot(a, app);
                if (Has(a, "state") && a.GetProperty("state").ValueKind == JsonValueKind.Object)
                {
                    bot.State.Clear();
                    foreach (var p in a.GetProperty("state").EnumerateObject())
                        bot.State[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString();
                }
                else if (Has(a, "key")) bot.State[Str(a, "key")] = Str(a, "value");
                return new { count = bot.State.Count };
            });

        Tool("set_servers", "Replace a bot's full list of server/connection rows from a JSON array.",
            Obj(new { bot = BotArg(), servers = new { type = "array", description = "connection rows (host/port/tls/nick/channels/realName/sasl...)" } }, "servers"),
            true, (a, app) =>
            {
                var bot = ResolveBot(a, app);
                if (!Has(a, "servers") || a.GetProperty("servers").ValueKind != JsonValueKind.Array) throw new Exception("servers array required");
                var list = new List<Ircuitry.Irc.IrcSettings>();
                foreach (var e in a.GetProperty("servers").EnumerateArray())
                {
                    string GS(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    int GI(string k, int d) => e.TryGetProperty(k, out var v) && v.TryGetInt32(out var i) ? i : d;
                    bool GB(string k, bool d) => e.TryGetProperty(k, out var v) ? v.ValueKind == JsonValueKind.True || (v.ValueKind != JsonValueKind.False && d) : d;
                    list.Add(new Ircuitry.Irc.IrcSettings
                    {
                        Label = GS("label"), Host = GS("host"), Port = GI("port", 6697), UseTls = GB("tls", true),
                        Nick = GS("nick"), Channels = GS("channels"), RealName = GS("realName"),
                        SaslUser = GS("saslUser"), SaslPass = GS("saslPass"), ServerPass = GS("serverPass"),
                        SaslMech = e.TryGetProperty("saslMech", out var sm) && sm.ValueKind == JsonValueKind.String ? sm.GetString() ?? "auto" : "auto",
                        ClientCertPath = GS("clientCertPath"), ClientCertPass = GS("clientCertPass"),
                        ConnectOnStartup = GB("connectOnStartup", false), AcceptInvalidCerts = GB("acceptInvalidCerts", false), AutoReconnect = GB("autoReconnect", true),
                    });
                }
                if (list.Count == 0) list.Add(new Ircuitry.Irc.IrcSettings());
                bot.Servers.Clear(); bot.Servers.AddRange(list); bot.SelectedServer = 0;
                return new { servers = bot.Servers.Count };
            });

        Tool("delete_secret", "Delete a stored credential by name.",
            Obj(new { name = S("secret name") }, "name"),
            true, (a, app) => { var nm = Str(a, "name"); Ircuitry.Core.Secrets.Delete(nm); return new { deleted = nm }; });

        // ---------------- validate & test ----------------
        Tool("validate_graph", "Check a bot for problems: no trigger, unreachable nodes, undefined {{secret.X}} references, no server set.",
            Obj(new { bot = BotArg() }), false, (a, app) =>
            {
                var g = ResolveBot(a, app).Graph;
                var bot = ResolveBot(a, app);
                var issues = new List<object>();
                int errors = 0;
                void Add(string sev, string msg, string? node = null) { issues.Add(new { severity = sev, node, message = msg }); if (sev == "error") errors++; }

                if (!g.Nodes.Any(n => n.Def.IsTrigger))
                    Add("error", "No trigger node - the bot can never fire. Add an event node (e.g. event.command).");
                foreach (var n in g.Nodes)
                {
                    if (n.Def.HasExecIn && !g.Connections.Any(c => c.ToNode == n.Id))
                        Add("warn", $"'{n.DisplayTitle}' has no incoming connection - it won't run.", n.Id);
                    foreach (var kv in n.Params)
                        foreach (var miss in Secrets.Missing(kv.Value))
                            Add("error", $"references undefined secret '{miss}' in param '{kv.Key}'. Define it with set_secret.", n.Id);
                }
                if (bot.Settings.Host.Length == 0) Add("info", "No server host set - set_connection before going live.");
                return new { ok = errors == 0, errorCount = errors, issues };
            });

        Tool("test_command",
            "Dry-run a message against a bot and return what it WOULD send plus a per-node trace - no IRC connection. (Non-IRC side effects like file/db/http nodes do execute, same as the in-app test bench.)",
            Obj(new { bot = BotArg(), message = S("the incoming message, e.g. !hello"), nick = S("sender nick (default tester)"), channel = S("channel (default #test)") }, "message"),
            false, (a, app) =>
            {
                var bot = ResolveBot(a, app);
                string msg = Str(a, "message"), nick = Str(a, "nick", "tester"), chan = Str(a, "channel", "#test");
                var sink = new TestSink(new Dictionary<string, string>(bot.State));
                var baseVars = new Dictionary<string, string>
                {
                    ["botnick"] = bot.Settings.Nick, ["nick"] = nick, ["user"] = "tester", ["host"] = "test.host",
                    ["channel"] = chan, ["target"] = chan, ["message"] = msg, ["replyto"] = chan,
                    ["args"] = "", ["command"] = "", ["account"] = "", ["isbot"] = "false",
                    ["msgid"] = "mcp-msg", ["__reply"] = "mcp-msg",
                };
                RunRecord? fired = null;
                foreach (var node in bot.Graph.Nodes)
                {
                    if (node.Def.TriggerEvent != "message") continue;
                    var rec = new RunRecord { Time = DateTime.Now, Trigger = node.DisplayTitle, Icon = node.Def.Icon, Summary = msg };
                    GraphExecutor.Fire(bot.Graph, sink, node, new Dictionary<string, string>(baseVars), rec);
                    rec.Fired = rec.Nodes.Count > 0 && rec.Nodes[0].Pulsed.Count > 0;
                    if (rec.Fired && fired == null) fired = rec;
                }
                return new
                {
                    fired = fired != null,
                    sent = sink.Sent.Select(s => new { kind = s.kind, text = s.text }).ToList(),
                    trace = fired?.Nodes.Select(tr => new { id = tr.NodeId, node = tr.Title, pulsed = tr.Pulsed, outputs = tr.Outputs.Select(o => new { pin = o.pin, value = o.value }) }).ToList(),
                };
            });

        // ---------------- secrets (write-only; values are never readable) ----------------
        Tool("list_secret_names", "List the names of stored secrets (NEVER their values).",
            Empty(), false, (a, app) => new { names = Secrets.Names() });

        Tool("set_secret", "Store/overwrite a secret value so nodes can reference it as {{secret.NAME}}. Values can't be read back.",
            Obj(new { name = S("secret name"), value = S("secret value") }, "name", "value"),
            false, (a, app) => { var name = Str(a, "name"); if (name.Length == 0) throw new Exception("name required"); Secrets.Set(name, Str(a, "value")); return new { ok = true, name }; });

        return t;
    }

    // ---------------- helpers ----------------
    private static object DescribeDef(NodeDef d) => new
    {
        typeId = d.TypeId, title = d.Title, subtitle = d.Subtitle, category = d.Category.ToString(),
        trigger = d.TriggerEvent, pure = d.IsPure, description = d.Description,
        inputs = d.Inputs.Select(p => new { name = p.Name, kind = p.Kind.ToString(), multi = p.Multi }),
        outputs = d.Outputs.Select(p => new { name = p.Name, kind = p.Kind.ToString() }),
        @params = d.Params.Select(p => new { key = p.Key, label = p.Label, type = p.Type.ToString(), @default = p.Default, choices = p.Choices, placeholder = p.Placeholder }),
    };

    private static Bot ResolveBot(JsonElement a, AppModel app)
    {
        if (Has(a, "bot"))
        {
            var v = a.GetProperty("bot");
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && i >= 0 && i < app.Bots.Count) return app.Bots[i];
            var name = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
            return app.Bots.FirstOrDefault(x => x.Name == name) ?? throw new Exception("bot not found: " + name);
        }
        if (app.Bots.Count == 0) throw new Exception("no bots in workspace");
        return app.ActiveBot;
    }

    private static Node NodeOf(Bot bot, string id) => bot.Graph.Find(id) ?? throw new Exception("node not found: " + id);

    private static bool Has(JsonElement a, string k) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out _);

    private static string Str(JsonElement a, string k, string d = "")
    {
        if (!Has(a, k)) return d;
        var v = a.GetProperty(k);
        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? d : v.ValueKind == JsonValueKind.Null ? d : v.ToString();
    }

    private static int Int(JsonElement a, string k, int d)
    {
        if (!Has(a, k)) return d;
        var v = a.GetProperty(k);
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        return int.TryParse(Str(a, k), out var m) ? m : d;
    }

    private static float Flt(JsonElement a, string k, float d)
    {
        if (!Has(a, k)) return d;
        var v = a.GetProperty(k);
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n)) return (float)n;
        return float.TryParse(Str(a, k), out var m) ? m : d;
    }

    private static bool Bool(JsonElement a, string k, bool d)
    {
        if (!Has(a, k)) return d;
        var v = a.GetProperty(k);
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => v.GetString() == "true",
            _ => d,
        };
    }
}
