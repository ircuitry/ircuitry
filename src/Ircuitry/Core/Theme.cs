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
        NodeCategory.Action => Lime,
        NodeCategory.Data => Magenta,
        NodeCategory.Ai => Berry,
        NodeCategory.Storage => Sky,
        NodeCategory.Code => Blueberry,
        NodeCategory.Ircv3 => Teal,
        _ => Idle,
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

/// <summary>Broad families a node can belong to, driving its accent color.</summary>
public enum NodeCategory
{
    Event,   // triggers - incoming IRC events
    Filter,  // conditions / matching
    Logic,   // flow control, variables
    Action,  // outgoing IRC commands & effects
    Data,    // values, transforms
    Ai,      // AI generation, tools, memory
    Storage, // files, database, calendar
    Code,    // codebase programming tools (read/write/edit/search/run)
    Ircv3,   // IRCv3 protocol extensions (caps, tags, drafts)
}
