using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Ircuitry.Net;

/// <summary>Synchronous HTTP used by the HTTP Request node (runs on the bot thread, bounded by a timeout).</summary>
public static class Http
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly HttpClient DlClient = new() { Timeout = TimeSpan.FromMinutes(15) };

    /// <summary>
    /// Stream a URL to a file, reporting fractional progress (0..1) when the length is known.
    /// Used by the in-app updater to download a release asset. Returns false on any failure.
    /// </summary>
    public static bool DownloadFile(string url, string destPath, Action<float>? onProgress = null)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "ircuitry");
            using var resp = DlClient.Send(req, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) return false;
            long total = resp.Content.Headers.ContentLength ?? -1;
            using var srcStream = resp.Content.ReadAsStream();
            using var dst = System.IO.File.Create(destPath);
            var buf = new byte[81920];
            long read = 0; int n;
            while ((n = srcStream.Read(buf, 0, buf.Length)) > 0)
            {
                dst.Write(buf, 0, n);
                read += n;
                if (total > 0) onProgress?.Invoke((float)((double)read / total));
            }
            onProgress?.Invoke(1f);
            return true;
        }
        catch { return false; }
    }

    public static (int status, string body) Send(string method, string url, IEnumerable<(string key, string val)> headers, string? body)
    {
        try
        {
            using var req = new HttpRequestMessage(new HttpMethod(method.ToUpperInvariant()), url);
            string contentType = "application/json";
            bool hasUserAgent = false;
            foreach (var (k, v) in headers)
            {
                if (k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) { contentType = v; continue; }
                if (k.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) hasUserAgent = true;
                req.Headers.TryAddWithoutValidation(k, v);
            }
            // some APIs (notably GitHub) reject requests with no User-Agent; .NET sends none by default
            if (!hasUserAgent) req.Headers.TryAddWithoutValidation("User-Agent", "ircuitry");
            if (body != null && method.ToUpperInvariant() != "GET")
                req.Content = new StringContent(body, Encoding.UTF8, contentType);

            using var resp = Client.Send(req);
            string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return ((int)resp.StatusCode, text);
        }
        catch (Exception ex)
        {
            return (0, "request error: " + ex.Message);
        }
    }
}

/// <summary>
/// Provider-agnostic chat completion for the "Ask AI" node. Speaks the
/// OpenAI-compatible <c>/chat/completions</c> protocol, so it works with OpenAI,
/// a local Ollama / LM Studio server, OpenRouter, Groq, Together, etc. - just
/// point Base URL + key + model at your provider. Key may be blank for keyless
/// local servers.
/// </summary>
public static class Ai
{
    public static string Chat(string baseUrl, string apiKey, string model, string system, string prompt, int maxTokens, out string error)
    {
        error = "";
        if (apiKey.Length == 0) apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
        if (prompt.Length == 0) { error = "empty prompt"; return ""; }

        baseUrl = baseUrl.Trim().TrimEnd('/');
        if (baseUrl.Length == 0) baseUrl = "https://api.openai.com/v1";
        string url = baseUrl + "/chat/completions";

        var messages = new List<Dictionary<string, string>>();
        if (system.Length > 0) messages.Add(new() { ["role"] = "system", ["content"] = system });
        messages.Add(new() { ["role"] = "user", ["content"] = prompt });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model.Length > 0 ? model : "gpt-4o-mini",
            ["messages"] = messages,
            ["max_tokens"] = Math.Clamp(maxTokens, 1, 4096),
        };

        var headers = new List<(string, string)>();
        if (apiKey.Length > 0) headers.Add(("Authorization", "Bearer " + apiKey));

        var (status, body) = Http.Send("POST", url, headers, JsonSerializer.Serialize(payload));
        if (status != 200) { error = $"API {status}: {Truncate(body, 200)}"; return ""; }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0) { error = "no choices in response"; return ""; }
            return (choices[0].GetProperty("message").GetProperty("content").GetString() ?? "").Trim();
        }
        catch (Exception ex) { error = "parse error: " + ex.Message; }
        return "";
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>A tool the model may call: a name, a description, and named string args. An optional
    /// <see cref="Parameters"/> JSON-schema object (e.g. an MCP tool's schema) overrides the simple
    /// all-strings <see cref="Args"/> shape so richer arg types reach the model.</summary>
    public sealed class ToolDef
    {
        public string Name;
        public string Description;
        public List<(string name, string desc)> Args;
        public object? Parameters;
        public ToolDef(string name, string description, List<(string, string)> args, object? parameters = null)
        { Name = name; Description = description; Args = args; Parameters = parameters; }
    }

    /// <summary>
    /// Chat with OpenAI-compatible function calling: sends tool schemas, and whenever the model
    /// asks to call one, invokes <paramref name="execTool"/> and feeds the result back, looping
    /// until the model returns a final answer (capped).
    /// </summary>
    public static string ChatWithTools(string baseUrl, string apiKey, string model, string system, string prompt,
        int maxTokens, List<ToolDef> tools, Func<string, Dictionary<string, string>, string> execTool, out string error,
        int maxRounds = 6, Action<string, string, string>? onTool = null)
    {
        error = "";
        if (apiKey.Length == 0) apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
        if (prompt.Length == 0) { error = "empty prompt"; return ""; }
        baseUrl = baseUrl.Trim().TrimEnd('/');
        if (baseUrl.Length == 0) baseUrl = "https://api.openai.com/v1";
        string url = baseUrl + "/chat/completions";
        var headers = new List<(string, string)>();
        if (apiKey.Length > 0) headers.Add(("Authorization", "Bearer " + apiKey));

        var messages = new List<Dictionary<string, object?>>();
        if (system.Length > 0) messages.Add(new() { ["role"] = "system", ["content"] = system });
        messages.Add(new() { ["role"] = "user", ["content"] = prompt });

        List<Dictionary<string, object?>>? toolSpecs = tools.Count == 0 ? null : tools.Select(t =>
            new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.Parameters ?? new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = t.Args.ToDictionary(a => a.name, a => (object?)new Dictionary<string, object?> { ["type"] = "string", ["description"] = a.desc }),
                        ["required"] = t.Args.Select(a => a.name).ToArray(),
                    },
                },
            }).ToList();

        int rounds = Math.Max(1, maxRounds);
        for (int round = 0; round < rounds; round++)
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = model.Length > 0 ? model : "gpt-4o-mini",
                ["messages"] = messages,
                ["max_tokens"] = Math.Clamp(maxTokens, 1, 4096),
            };
            if (toolSpecs != null) payload["tools"] = toolSpecs;

            var (status, body) = Http.Send("POST", url, headers, JsonSerializer.Serialize(payload));
            if (status != 200) { error = $"API {status}: {Truncate(body, 200)}"; return ""; }

            JsonElement msg;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() == 0) { error = "no choices in response"; return ""; }
                msg = choices[0].GetProperty("message").Clone();
            }
            catch (Exception ex) { error = "parse error: " + ex.Message; return ""; }

            if (msg.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array && tcs.GetArrayLength() > 0)
            {
                var asst = new Dictionary<string, object?> { ["role"] = "assistant", ["tool_calls"] = tcs };
                asst["content"] = msg.TryGetProperty("content", out var cnt) && cnt.ValueKind == JsonValueKind.String ? cnt.GetString() : null;
                messages.Add(asst);

                foreach (var tc in tcs.EnumerateArray())
                {
                    string id = tc.TryGetProperty("id", out var idv) ? idv.GetString() ?? "" : "";
                    var fn = tc.GetProperty("function");
                    string name = fn.GetProperty("name").GetString() ?? "";
                    string argsJson = fn.TryGetProperty("arguments", out var ar)
                        ? (ar.ValueKind == JsonValueKind.String ? ar.GetString() ?? "{}" : ar.GetRawText()) : "{}";
                    var argd = new Dictionary<string, string>();
                    try
                    {
                        using var ad = JsonDocument.Parse(argsJson);
                        if (ad.RootElement.ValueKind == JsonValueKind.Object)
                            foreach (var p in ad.RootElement.EnumerateObject())
                                argd[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
                    }
                    catch { /* malformed args -> empty */ }

                    string result;
                    try { result = execTool(name, argd) ?? ""; }
                    catch (Exception ex) { result = "tool error: " + ex.Message; }
                    messages.Add(new() { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = result });
                    onTool?.Invoke(name, argsJson, result);   // surface each step so the run is observable
                }
                continue; // let the model use the results
            }

            return (msg.TryGetProperty("content", out var fc) && fc.ValueKind == JsonValueKind.String ? fc.GetString() ?? "" : "").Trim();
        }
        error = $"tool loop exceeded ({rounds} steps) - raise 'Max tool steps' on the node, or narrow the task";
        return "";
    }
}

/// <summary>Minimal dotted-path extraction from a JSON string for the JSON Field node.</summary>
public static class Json
{
    /// <summary>Split a JSON array into element strings: scalars as their text, objects/arrays as raw JSON
    /// (so a downstream {item.field} can dot into them). Empty array if the input isn't a JSON array.</summary>
    public static string[] ArrayItems(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return System.Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return System.Array.Empty<string>();
            var list = new System.Collections.Generic.List<string>();
            foreach (var el in doc.RootElement.EnumerateArray())
                list.Add(el.ValueKind switch
                {
                    JsonValueKind.String => el.GetString() ?? "",
                    JsonValueKind.Null => "",
                    _ => el.GetRawText(),
                });
            return list.ToArray();
        }
        catch { return System.Array.Empty<string>(); }
    }

    public static string Extract(string json, string path)
    {
        if (json.Length == 0) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var el = doc.RootElement;
            foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (el.ValueKind == JsonValueKind.Array && int.TryParse(part, out int i))
                {
                    if (i < 0 || i >= el.GetArrayLength()) return "";
                    el = el[i];
                }
                else if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(part, out var next))
                    el = next;
                else return "";
            }
            return el.ValueKind switch
            {
                JsonValueKind.String => el.GetString() ?? "",
                JsonValueKind.Null => "",
                JsonValueKind.Object or JsonValueKind.Array => el.GetRawText(),
                _ => el.GetRawText(),
            };
        }
        catch { return ""; }
    }
}
