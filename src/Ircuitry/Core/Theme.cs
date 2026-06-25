using Microsoft.Xna.Framework;

namespace Ircuitry.Core;

/// <summary>
/// Ircuitry's visual identity: a warm, cozy, hand-drawn look by default - cream paper,
/// leaf greens, sky blue, honey and coral pastels, soft rounded everything.
/// Every colour reads live from <see cref="Active"/>, so swapping or editing a
/// <see cref="ThemeData"/> re-themes the entire app instantly (see the Appearance editor).
/// </summary>
public static class Theme
{
    /// <summary>The palette the whole app draws from right now. Swap via <see cref="Ircuitry.Core.Themes"/>.</summary>
    public static ThemeData Active = ThemeData.Default();

    // ---- base surfaces ----
    public static Color Void => Active.C("void");
    public static Color Backdrop => Active.C("backdrop");
    public static Color Panel => Active.C("panel");
    public static Color PanelHi => Active.C("panelHi");
    public static Color PanelLo => Active.C("panelLo");
    public static Color Hairline => Active.C("hairline");
    public static Color Edge => Active.C("edge");

    // ---- canvas grid ----
    public static Color GridMinor => Active.C("gridMinor");
    public static Color GridMajor => Active.C("gridMajor");
    public static Color GridAxis => Active.C("gridAxis");

    // ---- accents ----
    public static Color Cyan => Active.C("cyan");
    public static Color CyanBright => Active.C("cyanBright");
    public static Color CyanDim => Active.C("cyanDim");
    public static Color CyanDeep => Active.C("cyanDeep");
    public static Color Amber => Active.C("amber");
    public static Color AmberBright => Active.C("amberBright");
    public static Color AmberDim => Active.C("amberDim");
    public static Color Magenta => Active.C("magenta");
    public static Color Violet => Active.C("violet");
    public static Color Lime => Active.C("lime");
    public static Color Berry => Active.C("berry");
    public static Color Sky => Active.C("sky");
    public static Color Teal => Active.C("teal");
    public static Color Blueberry => Active.C("blueberry");

    // ---- status ----
    public static Color Ok => Active.C("ok");
    public static Color Warn => Active.C("warn");
    public static Color Alert => Active.C("alert");
    public static Color Idle => Active.C("idle");

    // ---- text ----
    public static Color Text => Active.C("text");
    public static Color TextDim => Active.C("textDim");
    public static Color TextFaint => Active.C("textFaint");
    public static Color TextInk => Active.C("textInk");

    // ---- feel knobs ----
    public static float Glow => Active.Glow;
    public static float Twinkle => Active.Twinkle;
    public static float UiRoundness => Active.Roundness;
    public static float WindowOpacity => Active.Opacity;
    public static bool Glass => Active.Glass;

    // ---- node category palette ----
    public static Color Category(NodeCategory c) => c switch
    {
        NodeCategory.Event => Cyan,
        NodeCategory.Filter => Amber,
        NodeCategory.Logic => Violet,
        NodeCategory.Data => Magenta,
        NodeCategory.Ai => Berry,
        NodeCategory.Storage => Sky,
        NodeCategory.Network => Active.C("coral", "amber"),   // new accents fall back cozily on older themes
        NodeCategory.Irc => Lime,                             // core IRC keeps the old Action accent
        NodeCategory.Ircv3 => Teal,
        NodeCategory.Code => Blueberry,
        NodeCategory.Media => Active.C("gold", "amber"),
        NodeCategory.Ui => Active.C("mint", "teal"),
        NodeCategory.App => Active.C("plugin", "violet"),
        NodeCategory.Action => Lime,
        _ => Idle,
    };

    /// <summary>A cozy palette for per-node colour tags (and anywhere else a small named-colour set helps).
    /// Index 0..TagCount-1; persisted by index so a theme can remap the actual colours.</summary>
    public const int TagCount = 7;
    public static Color Tag(int i) => (((i % TagCount) + TagCount) % TagCount) switch
    {
        0 => Cyan, 1 => Amber, 2 => Lime, 3 => Berry, 4 => Sky, 5 => Violet, _ => Teal,
    };
    public static string TagName(int i) => (((i % TagCount) + TagCount) % TagCount) switch
    {
        0 => "Cyan", 1 => "Amber", 2 => "Lime", 3 => "Berry", 4 => "Sky", 5 => "Violet", _ => "Teal",
    };

    public static Color Mix(Color a, Color b, float t)
    {
        t = MathHelper.Clamp(t, 0f, 1f);
        return new Color(
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t),
            (int)(a.A + (b.A - a.A) * t));
    }

    public static Color WithAlpha(Color c, float a) => new(c.R, c.G, c.B, (byte)MathHelper.Clamp(a * 255f, 0, 255));
}

/// <summary>Broad families a node can belong to, driving its accent color. Order here is the palette order.</summary>
public enum NodeCategory
{
    Event,    // triggers - anything that starts a flow (IRC, system, sockets, timers)
    Filter,   // conditions / matching / guards
    Logic,    // flow control, variables, subflow, human gates
    Data,     // values & transforms
    Ai,       // AI generation, tools, memory
    Storage,  // persistence: database, files, calendar, archives
    Network,  // sockets, HTTP, mail, DCC
    Irc,      // core IRC commands (say, join, mode, kick, topic, ...)
    Ircv3,    // IRCv3 protocol extensions (caps, tags, drafts)
    Code,     // codebase / dev tools (read/write/edit/search/run/shell/containers)
    Media,    // offline knowledge (zim) + media download/transform
    Ui,       // node-authored UI: windows, panels, text, media, controls, animation
    App,      // plugins: hook ircuitry's own chrome (menus, toolbar, panels, right-click) + app events
    Action,   // vestigial (former catch-all) - no node maps here after the taxonomy fix
}
