using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Ircuitry.Graph;

namespace Ircuitry.App;

/// <summary>
/// A tiny READ-ONLY loopback HTTP endpoint that lets the ircuitry website (and ONLY it) detect this running
/// app, so the gallery can tailor itself: suggest "Upgrade to use this" when a card needs a newer build,
/// surface community-node prerequisites, and list available node updates. It binds 127.0.0.1 only, allows
/// just the ircuitry.github.io origin (CORS + Private Network Access), serves a single GET, exposes only the
/// app version + node catalog (which are public anyway), and NEVER returns secrets/files/workspace or takes
/// any action. Best-effort: if the port is taken or HttpListener is unavailable, it silently does nothing.
/// </summary>
public static class CapabilityServer
{
    private static readonly int[] Ports = { 48457, 48458, 48459 };   // the website probes this same little range
    private static readonly string[] AllowedOrigins =
    {
        "https://ircuitry.github.io",
        "http://localhost:8099", "http://127.0.0.1:8099",   // local website dev
    };

    private static HttpListener? _listener;

    public static void Start()
    {
        if (_listener != null) return;
        foreach (var port in Ports)
        {
            try
            {
                var l = new HttpListener();
                l.Prefixes.Add($"http://127.0.0.1:{port}/");
                l.Start();
                _listener = l;
                new Thread(Loop) { IsBackground = true, Name = "ircuitry-capabilities" }.Start();
                return;
            }
            catch { /* port busy or not permitted - try the next, else give up */ }
        }
    }

    private static void Loop()
    {
        while (_listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch { break; }
            try { Handle(ctx); } catch { try { ctx.Response.Abort(); } catch { } }
        }
    }

    /// <summary>True only for the exact origins we trust (case-insensitive). Nothing else may read us.</summary>
    internal static bool IsAllowedOrigin(string origin) =>
        AllowedOrigins.Any(o => o.Equals(origin, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The CORS response headers for a given request Origin - a PURE function so it can be unit-tested without
    /// a live socket. A disallowed origin gets NO CORS grant at all (only Cache-Control), so the browser blocks
    /// it; an allowed origin gets ACAO + the Private-Network/methods grant. (Used by Handle and the self-test.)
    /// </summary>
    internal static List<(string Key, string Value)> CorsHeaders(string origin)
    {
        var h = new List<(string, string)>();
        if (IsAllowedOrigin(origin))   // only an allowed origin gets any CORS grant - everyone else is browser-blocked
        {
            h.Add(("Access-Control-Allow-Origin", origin));
            h.Add(("Vary", "Origin"));
            h.Add(("Access-Control-Allow-Private-Network", "true"));   // Chrome's local-network access preflight
            h.Add(("Access-Control-Allow-Methods", "GET, OPTIONS"));
            h.Add(("Access-Control-Allow-Headers", "*"));
        }
        h.Add(("Cache-Control", "no-store"));
        return h;
    }

    private static void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        string origin = req.Headers["Origin"] ?? "";
        bool allowed = IsAllowedOrigin(origin);

        foreach (var (k, v) in CorsHeaders(origin)) res.AddHeader(k, v);

        if (req.HttpMethod == "OPTIONS") { res.StatusCode = 204; res.Close(); return; }       // preflight
        if (!allowed) { res.StatusCode = 403; res.Close(); return; }                          // other sites can't read us
        string path = (req.Url?.AbsolutePath ?? "/").TrimEnd('/');
        if (req.HttpMethod != "GET" || (path != "" && path != "/capabilities")) { res.StatusCode = 404; res.Close(); return; }

        var bytes = Encoding.UTF8.GetBytes(BuildCapabilities());
        res.ContentType = "application/json";
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes, 0, bytes.Length);
        res.Close();
    }

    private static string BuildCapabilities()
    {
        var root = new JsonObject
        {
            ["app"] = "ircuitry",
            ["version"] = AppInfo.Version,
        };

        var builtins = new JsonArray();
        foreach (var id in NodeCatalog.All.Select(d => d.TypeId).Where(id => !NodeCatalog.IsCustom(id)).OrderBy(x => x, StringComparer.Ordinal))
            builtins.Add(id);
        root["builtins"] = builtins;

        // installed community nodes, with their manifest, so the site can diff against the gallery for updates
        var community = new JsonArray();
        try
        {
            if (Directory.Exists(NodeCatalog.CustomDir))
                foreach (var f in Directory.GetFiles(NodeCatalog.CustomDir, "*.ircnode").OrderBy(x => x, StringComparer.Ordinal))
                {
                    try { if (JsonNode.Parse(File.ReadAllText(f)) is { } m) community.Add(m); }
                    catch { /* skip a malformed file */ }
                }
        }
        catch { }
        root["community"] = community;

        return root.ToJsonString();
    }
}
