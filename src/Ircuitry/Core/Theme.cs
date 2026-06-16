using Microsoft.Xna.Framework;

namespace Ircuitry.Core;

/// <summary>
/// Ircuitry's visual identity: a warm, cozy, hand-drawn look - cream paper,
/// leaf greens, sky blue, honey and coral pastels, soft rounded everything.
/// (The accent fields keep their old names so the whole app re-themes from here.)
/// </summary>
public static class Theme
{
    // ---- base surfaces (light & warm) ----
    public static readonly Color Void = new(244, 236, 216);      // window background - warm cream
    public static readonly Color Backdrop = new(233, 242, 216);  // canvas - soft grass cream
    public static readonly Color Panel = new(252, 247, 235);     // panel fill - creamy paper
    public static readonly Color PanelHi = new(255, 252, 244);   // raised panel / header
    public static readonly Color PanelLo = new(238, 229, 208);   // sunken fields - soft sand
    public static readonly Color Hairline = new(224, 212, 184);  // subtle separators
    public static readonly Color Edge = new(201, 182, 144);      // panel edges - warm tan

    // ---- canvas grid ----
    public static readonly Color GridMinor = new(220, 231, 198);
    public static readonly Color GridMajor = new(202, 219, 170);
    public static readonly Color GridAxis = new(183, 206, 146);

    // ---- accents (cute pastels). Names kept from the old theme; colours are cozy now. ----
    public static readonly Color Cyan = new(86, 192, 210);        // sky teal (primary)
    public static readonly Color CyanBright = new(126, 214, 228);
    public static readonly Color CyanDim = new(96, 158, 170);
    public static readonly Color CyanDeep = new(206, 236, 240);   // light teal tint for fills

    public static readonly Color Amber = new(242, 174, 70);       // honey
    public static readonly Color AmberBright = new(250, 200, 120);
    public static readonly Color AmberDim = new(196, 146, 64);

    public static readonly Color Magenta = new(240, 138, 158);    // coral pink
    public static readonly Color Violet = new(176, 158, 226);     // lavender
    public static readonly Color Lime = new(140, 196, 84);        // leaf green
    public static readonly Color Berry = new(198, 142, 214);      // orchid (AI)
    public static readonly Color Sky = new(116, 174, 224);        // cornflower (storage)
    public static readonly Color Teal = new(78, 196, 178);        // seafoam (IRCv3)
    public static readonly Color Blueberry = new(124, 138, 210);  // indigo (code)

    // ---- status ----
    public static readonly Color Ok = new(126, 196, 92);          // leaf
    public static readonly Color Warn = new(242, 182, 72);        // honey
    public static readonly Color Alert = new(235, 116, 104);      // soft coral
    public static readonly Color Idle = new(176, 162, 132);       // warm taupe

    // ---- text (dark warm on cream) ----
    public static readonly Color Text = new(86, 70, 48);          // cocoa
    public static readonly Color TextDim = new(140, 122, 92);
    public static readonly Color TextFaint = new(180, 164, 134);
    public static readonly Color TextInk = new(251, 247, 236);    // cream text on bright fills

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
