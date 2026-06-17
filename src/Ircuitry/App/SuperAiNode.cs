using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Ircuitry.Editor;
using Ircuitry.Graph;

namespace Ircuitry.App;

/// <summary>
/// Builds the <c>SuperAI</c> node as a composite <c>.ircnode</c> manifest - a RECIPE of ordinary nodes
/// (Ask AI wired to Recent Messages, Request History and React to Message tools), not a built-in. Emitted
/// with <c>--emit-superai</c> so it can be dropped into <c>nodes/</c> and then right-click -> Edit like any
/// other community node: rewire its tools, change its personality, swap the model, whatever.
/// </summary>
public static class SuperAiNode
{
    public static string BuildManifest()
    {
        var g = new NodeGraph();
        Node Add(string type, float x, float y) => g.Add(NodeCatalog.Get(type), new Vector2(x, y));

        var fin    = Add("flow.in", -360, -60);
        var ai     = Add("ai.reply", -20, -60);
        var recent = Add("ircv3.recent", -360, 110);
        var hist   = Add("action.chathistory", -360, 210);
        var react  = Add("action.reactid", -360, 320);
        var fret   = Add("flow.return", 320, -60); fret.SetParam("name", "reply");

        // The AI's connection settings are exposed as composite params ({model}/{system}/{goal}); the API key
        // is a secret reference so the recipe is shareable. ai.reply Resolve()s baseUrl/model/system/prompt.
        ai.SetParam("baseUrl", "https://api.openai.com/v1");
        ai.SetParam("model", "{model}");
        ai.SetParam("apiKey", "{{secret.openai}}");
        ai.SetParam("system", "{system}");
        ai.SetParam("prompt", "{goal}");
        ai.SetParam("maxTokens", "400");

        // exec: Start -> Ask AI -> Output;  data: AI reply -> the 'reply' output
        g.Connect(fin.Id, 0, ai.Id, 0);
        g.Connect(ai.Id, 0, fret.Id, 0);
        g.Connect(ai.Id, 1, fret.Id, 1);

        // wire each tool node's Tool pin into Ask AI's 'tools' (input 2) so the model can call them
        int ToolPin(Node n) { var o = n.Outputs; for (int i = 0; i < o.Length; i++) if (o[i].Kind == PinKind.Tool) return i; return -1; }
        g.Connect(recent.Id, ToolPin(recent), ai.Id, 2);
        g.Connect(hist.Id, ToolPin(hist), ai.Id, 2);
        g.Connect(react.Id, ToolPin(react), ai.Id, 2);

        var exposed = new Dictionary<string, string>
        {
            ["model"] = "gpt-4o-mini",
            ["system"] = "You are ircuitry, a friendly IRC bot. Use your tools to inspect recent messages or fetch older history, then react to the right message by its id. Keep any text short.",
            ["goal"] = "{nick} said: {message}\nDecide what to do.",
        };

        return new GraphEditor(g).SerializeAsComposite(
            "superai", "SuperAI", "robot", "Ai",
            "An AI that reads the chat it has seen, can fetch older history (even from before it joined), and reacts to a specific message by id - assembled from nodes you can edit. Right-click -> Edit to rewire it. Add an OpenAI key as a secret named 'openai', set a goal, and wire 'reply' into Send Reply.",
            exposed) ?? "";
    }
}
