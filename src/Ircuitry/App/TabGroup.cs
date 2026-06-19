using Microsoft.Xna.Framework;
using Ircuitry.Core;

namespace Ircuitry.App;

/// <summary>A browser-style tab group: a named, coloured, collapsible band over a contiguous run of bot tabs.
/// Membership is by <see cref="Bot.GroupId"/>; the workspace keeps a group's tabs contiguous (see
/// <c>AppModel.NormalizeGroups</c>). The colour persists as an index into a cozy palette so themes can remap it.</summary>
public sealed class TabGroup
{
    public string Id = "";
    public string Name = "Group";
    public int ColorIndex;
    public bool Collapsed;

    public Color Color => Palette(ColorIndex);

    public const int PaletteCount = 7;
    private static int Mod(int i) => ((i % PaletteCount) + PaletteCount) % PaletteCount;
    public static Color Palette(int i) => Mod(i) switch
    {
        0 => Theme.Cyan,
        1 => Theme.Amber,
        2 => Theme.Lime,
        3 => Theme.Berry,
        4 => Theme.Sky,
        5 => Theme.Violet,
        _ => Theme.Teal,
    };
    public static string PaletteName(int i) => Mod(i) switch
    {
        0 => "Cyan",
        1 => "Amber",
        2 => "Lime",
        3 => "Berry",
        4 => "Sky",
        5 => "Violet",
        _ => "Teal",
    };
}
