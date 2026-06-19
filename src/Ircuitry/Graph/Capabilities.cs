using System.Collections.Generic;
using System.Linq;

namespace Ircuitry.Graph;

/// <summary>One thing a community node/workflow can do, derived from the built-in nodes it is composed of.</summary>
public sealed record Capability(string Icon, string Label, string Detail, bool Caution);

/// <summary>The "can't lie" trust card: because every community node is a composite of built-in nodes (and a
/// workflow is just a graph of them), what it can do is fully determined by the node types it contains - not by
/// its name or description. This statically scans a graph and reports those powers truthfully.</summary>
public static class Capabilities
{
    public static List<Capability> Scan(NodeGraph g, System.Collections.Generic.IEnumerable<string>? extraParamValues = null)
    {
        var found = new Dictionary<string, Capability>();
        void Add(string key, string icon, string label, string detail, bool caution)
        { if (!found.ContainsKey(key)) found[key] = new Capability(icon, label, detail, caution); }

        // the composite node's OWN exposed params can also reference secrets, not just its inner nodes
        bool usesSecret = extraParamValues != null && extraParamValues.Any(v => v != null && v.Contains("{{secret"));
        foreach (var n in g.Nodes)
        {
            string t = n.TypeId;
            foreach (var v in n.Params.Values) if (v.Contains("{{secret")) usesSecret = true;

            if (t.StartsWith("net.") || t.Contains("http") || t == "event.webhook")
                Add("net", "globe", "Network access", "fetches from or posts to the internet", true);
            if (t == "event.webhook")
                Add("hook", "webhooks-logo", "Exposes a webhook", "accepts inbound HTTP requests when hosted", true);
            if (t.StartsWith("container.") || t == "code.container")
                Add("container", "cube", "Runs containers", "starts/executes commands in Docker/Podman containers", true);
            else if (t.StartsWith("code."))
                Add("code", "code", "Runs code", "executes scripts / shell commands on this machine", true);
            if (t.StartsWith("file.") || t.StartsWith("fs.") || t.StartsWith("cal.") || t.StartsWith("zim."))
                Add("file", "folder", "Reads & writes files", "touches files on this machine", true);
            if (t.StartsWith("sql.") || t.StartsWith("db."))
                Add("db", "database", "Database access", "reads/writes a local database", true);
            if (t.StartsWith("kv."))
                Add("kv", "package", "Key-value storage", "reads/writes a local key-value store", false);
            if (t.StartsWith("dcc."))
                Add("dcc", "download-simple", "File transfer (DCC)", "sends/receives files over IRC", true);

            if (t == "ai.editor")
                Add("aiedit", "note-pencil", "Reads & edits your workflows", "lets an AI change your bots", true);
            else if (t.StartsWith("ai."))
                Add("ai", "robot", "Uses AI", "calls an AI model (may spend tokens)", false);

            if (t == "irc.raw" || t == "action.raw")
                Add("raw", "terminal-window", "Raw IRC", "sends arbitrary IRC commands", true);
            else if (t.StartsWith("action.") || t.StartsWith("irc."))
                Add("irc", "chat-circle", "Acts on IRC", "messages, joins, or moderates on your behalf", false);

            if (!NodeCatalog.TryGet(t, out _))
                Add("unk:" + t, "question", "Unknown node", "\"" + t + "\" is not a built-in - it can't be vetted and may not run", true);
        }
        if (usesSecret)
            Add("secret", "key", "Uses your secret keys", "reads stored credentials to authenticate", true);

        var list = found.Values.OrderByDescending(c => c.Caution).ThenBy(c => c.Label).ToList();
        if (list.Count == 0)
            list.Add(new Capability("check-circle", "Only basic logic & text", "no network, code, files, or IRC actions", false));
        return list;
    }

    /// <summary>A compact one-liner of the built-in node types it is built from (for the trust card footer).</summary>
    public static string Composition(NodeGraph g)
    {
        int n = g.Nodes.Count;
        var types = g.Nodes.Select(x => x.TypeId).Distinct().Count();
        return n + (n == 1 ? " node" : " nodes") + " · " + types + (types == 1 ? " type" : " types");
    }
}
