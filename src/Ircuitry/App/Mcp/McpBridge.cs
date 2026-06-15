using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Ircuitry.Net;

namespace Ircuitry.App.Mcp;

/// <summary>
/// An INWARD bridge to the MCP tool surface: lets an AI node (Ask AI + a Workflow Editor tool node)
/// introspect and edit the workspace's bots from inside a running workflow. It reuses the exact same
/// <see cref="McpTools"/> handlers the stdio MCP server exposes, run against a fresh on-disk
/// <see cref="AppModel"/> - so a mutation is saved and the running GUI hot-reloads it, the same
/// thread-safe pattern as the out-of-process server (no poking the live UI model from a bot thread).
/// </summary>
public static class McpBridge
{
    /// <summary>Override the workspace source (tests point this at a throwaway HOME). Default loads ~/ircuitry.</summary>
    public static Func<AppModel> AppFactory = () => new AppModel();

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static List<McpTool>? _reg;
    private static List<McpTool> Registry => _reg ??= McpTools.BuildRegistry();

    // The graph-editing slice an "edit" Workflow Editor exposes. Deliberately excludes secrets and
    // delete_bot - an AI editing a workflow has no business reading/writing keys or nuking whole bots.
    private static readonly string[] EditTools =
    {
        "list_bots", "list_node_types", "describe_node", "get_graph",
        "create_bot", "add_node", "set_param", "connect", "disconnect", "remove_node",
        "set_graph", "set_connection", "auto_layout", "validate_graph", "test_command",
    };

    // Look-but-don't-touch: introspection + verification only.
    private static readonly string[] ReadTools =
    {
        "list_bots", "list_node_types", "describe_node", "get_graph", "validate_graph", "test_command",
    };

    /// <summary>The tools a Workflow Editor node contributes to an Ask AI node, with the MCP tools'
    /// real JSON schemas so the model sees proper arg types.</summary>
    public static List<Ai.ToolDef> EditorToolDefs(bool readOnly)
    {
        var want = readOnly ? ReadTools : EditTools;
        var defs = new List<Ai.ToolDef>();
        foreach (var name in want)
        {
            var tool = Registry.FirstOrDefault(t => t.Name == name);
            if (tool == null) continue;
            defs.Add(new Ai.ToolDef(tool.Name, tool.Description, SchemaArgs(tool.Schema), tool.Schema));
        }
        return defs;
    }

    /// <summary>True if <paramref name="name"/> is one of the tools a Workflow Editor can run.</summary>
    public static bool Handles(string name) => EditTools.Contains(name);

    /// <summary>
    /// Run one editing tool. <paramref name="defaultBot"/> (the node's Target bot) is injected as the
    /// "bot" arg when the model didn't name one, so a node can be pinned to edit "its own" bot.
    /// Returns a JSON result string, or "Error: ..." - never throws.
    /// </summary>
    public static string Invoke(string name, IReadOnlyDictionary<string, string> args, string? defaultBot)
    {
        var tool = Registry.FirstOrDefault(t => t.Name == name);
        if (tool == null) return "Error: unknown tool '" + name + "'";
        try
        {
            var argsElem = BuildArgs(args, defaultBot);
            var app = AppFactory();
            object result = tool.Run(argsElem, app);
            if (tool.Mutates) app.Save(announce: false);   // persist -> the running GUI hot-reloads it
            return JsonSerializer.Serialize(result, Json);
        }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }

    // ---- helpers ----

    // (argName, description) pairs from an McpTool's JSON schema, for the simple ToolDef fallback.
    private static List<(string, string)> SchemaArgs(object schema)
    {
        var list = new List<(string, string)>();
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(schema, Json));
            if (doc.RootElement.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                foreach (var p in props.EnumerateObject())
                {
                    string desc = p.Value.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
                    list.Add((p.Name, desc));
                }
        }
        catch { /* fallback: no args */ }
        return list;
    }

    // Reassemble the flat string args (the chat layer stringifies every tool arg) into a JSON object,
    // re-typing values that are really numbers/bools/objects/arrays, then inject the default bot.
    private static JsonElement BuildArgs(IReadOnlyDictionary<string, string> args, string? defaultBot)
    {
        var obj = new Dictionary<string, object?>();
        foreach (var kv in args) obj[kv.Key] = Retype(kv.Value);
        if (defaultBot is { Length: > 0 } && !obj.ContainsKey("bot")) obj["bot"] = defaultBot;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(obj, Json));
        return doc.RootElement.Clone();
    }

    private static object? Retype(string v)
    {
        var t = v.Trim();
        if (t.Length > 0 && (t[0] is '{' or '[' or '-' || char.IsDigit(t[0]) || t is "true" or "false" or "null"))
            try { using var d = JsonDocument.Parse(t); return d.RootElement.Clone(); } catch { /* not JSON -> string */ }
        return v;
    }
}
