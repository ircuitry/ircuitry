using Microsoft.Xna.Framework;
using Ircuitry.Core;

namespace Ircuitry.Graph;

/// <summary>
/// A pin is either the white-hot <see cref="Exec"/> control-flow pulse or a
/// typed data value. Kinds drive port colour and connection compatibility.
/// </summary>
public enum PinKind
{
    Exec,     // control flow (triangle ports)
    Text,     // a string
    User,     // an IRC nick / source
    Channel,  // a channel name
    Number,   // numeric (stored as text)
    Bool,     // boolean (stored as "true"/"false")
    Tool,     // an AI tool definition (AI Tool → Ask AI)
}

public static class Pins
{
    public static bool IsExec(this PinKind k) => k == PinKind.Exec;

    /// <summary>Exec↔exec, Tool↔Tool, or any data kind to any data kind.</summary>
    public static bool Compatible(PinKind a, PinKind b)
    {
        if (a == PinKind.Tool || b == PinKind.Tool) return a == PinKind.Tool && b == PinKind.Tool;
        return a.IsExec() == b.IsExec();
    }

    public static Color Color(PinKind k) => k switch
    {
        PinKind.Exec => Theme.CyanBright,
        PinKind.Text => Theme.Amber,
        PinKind.User => Theme.Magenta,
        PinKind.Channel => Theme.Violet,
        PinKind.Number => Theme.Lime,
        PinKind.Bool => Theme.Ok,
        PinKind.Tool => Theme.Magenta,
        _ => Theme.TextDim,
    };
}
