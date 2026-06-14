using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Ircuitry.App.Mcp;

/// <summary>
/// A standard Model Context Protocol server over stdio (newline-delimited JSON-RPC 2.0), launched
/// with <c>Ircuitry.dll --mcp</c>. It lets any MCP client (Claude Desktop/Code, or anything else -
/// the protocol is open) introspect, build, validate and dry-run ircuitry bots. It operates on the
/// on-disk workspace (~/ircuitry/workspace.ircuitry); a running GUI hot-reloads the file, so edits
/// the AI makes appear live on the canvas.
///
/// stdout carries the protocol ONLY - all diagnostics go to stderr.
/// </summary>
public static class McpServer
{
    private const string ProtocolVersion = "2024-11-05";
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static TextWriter _out = TextWriter.Null;
    private static List<McpTool> _tools = new();
    private static bool _allowLive;

    public static int RunStdio(string[] args)
    {
        _allowLive = Array.IndexOf(args, "--mcp-allow-live") >= 0;   // reserved for a future live-control slice
        _tools = McpTools.BuildRegistry();

        var stdin = new StreamReader(Console.OpenStandardInput());
        _out = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true, NewLine = "\n" };
        Log($"ircuitry MCP server ready - {_tools.Count} tools, workspace {AppModel.WorkspaceDir}");

        string? line;
        while ((line = stdin.ReadLine()) != null)
        {
            if (line.Trim().Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                Handle(doc.RootElement);
            }
            catch (Exception ex) { Log("bad message: " + ex.Message); }
        }
        return 0;
    }

    private static void Handle(JsonElement msg)
    {
        string method = msg.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
        bool isRequest = msg.TryGetProperty("id", out var id);   // notifications have no id
        if (method.Length == 0) return;

        switch (method)
        {
            case "initialize":
                Reply(id, new
                {
                    protocolVersion = ProtocolVersion,
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "ircuitry", version = "1.0.0" },
                });
                break;

            case "ping":
                Reply(id, new { });
                break;

            case "tools/list":
                Reply(id, new { tools = _tools.Select(t => new { name = t.Name, description = t.Description, inputSchema = t.Schema }) });
                break;

            case "tools/call":
                CallTool(id, msg);
                break;

            case "resources/list": Reply(id, new { resources = Array.Empty<object>() }); break;
            case "prompts/list": Reply(id, new { prompts = Array.Empty<object>() }); break;

            default:
                if (method.StartsWith("notifications/", StringComparison.Ordinal)) return;   // fire-and-forget
                if (isRequest) Error(id, -32601, "method not found: " + method);
                break;
        }
    }

    private static void CallTool(JsonElement id, JsonElement msg)
    {
        var prms = msg.TryGetProperty("params", out var p) ? p : default;
        string name = prms.ValueKind == JsonValueKind.Object && prms.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        JsonElement args = prms.ValueKind == JsonValueKind.Object && prms.TryGetProperty("arguments", out var a) ? a : default;

        var tool = _tools.FirstOrDefault(t => t.Name == name);
        if (tool == null) { ToolResult(id, "Unknown tool: " + name, isError: true); return; }

        try
        {
            var app = new AppModel();                 // always operate on the latest on-disk workspace
            object result = tool.Run(args, app);
            if (tool.Mutates) app.Save(announce: false);   // persist → the running GUI hot-reloads it
            ToolResult(id, JsonSerializer.Serialize(result, Json), isError: false);
        }
        catch (Exception ex)
        {
            ToolResult(id, "Error: " + ex.Message, isError: true);
        }
    }

    private static void ToolResult(JsonElement id, string text, bool isError) =>
        Reply(id, new { content = new[] { new { type = "text", text } }, isError });

    private static void Reply(JsonElement id, object result) =>
        Write(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = IdValue(id), ["result"] = result });

    private static void Error(JsonElement id, int code, string message) =>
        Write(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = IdValue(id), ["error"] = new { code, message } });

    // echo the request id back verbatim (number or string), or null for notifications
    private static object? IdValue(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.Number => id.TryGetInt64(out var l) ? l : id.GetDouble(),
        JsonValueKind.String => id.GetString(),
        _ => null,
    };

    private static void Write(object o) => _out.WriteLine(JsonSerializer.Serialize(o, Json));

    internal static void Log(string s) => Console.Error.WriteLine("[ircuitry-mcp] " + s);
}
