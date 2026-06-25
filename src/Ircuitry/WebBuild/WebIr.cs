using System.Collections.Generic;

namespace Ircuitry.WebBuild;

/// <summary>
/// The Web-IR: a small, framework-agnostic description of a reactive page (the spine of the node-graph website
/// builder). The node graph compiles INTO this; codegen backends (vanilla runtime, React, later Vue) compile OUT of
/// it. Keeping the IR tiny and explicit is the whole bet - design it well and every backend is a pure function of it.
///
/// v0 scope (the "counter" proof slice): reactive state (numbers/strings) + a static element tree with two binding
/// forms - dynamic text ({state}) and event-to-action (click -> inc/dec/set/toggle). Lists, components, props,
/// effects and styling are deliberately NOT here yet; they're the next bricks, added without breaking this shape.
/// </summary>
public sealed class WebApp
{
    public string Name = "App";
    public List<WebState> States = new();
    public List<(string Name, string Value)> Tokens = new();   // design tokens -> CSS variables (:root { --name: value })
    public WebEl Root = new() { Tag = "div" };
}

/// <summary>One piece of reactive state (a signal). <see cref="Kind"/>: number | string | bool.</summary>
public sealed class WebState
{
    public string Name = "";
    public string Init = "0";
    public string Kind = "number";
}

/// <summary>One element in the view tree. A node is either static (Text), a dynamic text binding (Bind = a state
/// name), or a container (Children). On maps a DOM event to an action against a state.</summary>
public sealed class WebEl
{
    public string Tag = "div";
    public Dictionary<string, string> Attrs = new();   // static attributes (class, type, href, ...)
    public string? Style;                               // inline CSS, e.g. "padding:12px;display:flex;gap:8px;color:var(--brand)"
    public string? Text;                                // static text content (leaf)
    public string? Bind;                                // dynamic text: the name of a state whose value is shown
    public Dictionary<string, WebAction> On = new();    // dom event ("click") -> action
    public List<WebEl> Children = new();
}

/// <summary>An action a UI event triggers against a state. <see cref="Op"/>: inc | dec | set | toggle.
/// <see cref="Arg"/> is the value for set/inc/dec (default 1 for inc/dec).</summary>
public sealed class WebAction
{
    public string State = "";
    public string Op = "inc";
    public string Arg = "";
}
