using Microsoft.Xna.Framework;

namespace Ircuitry.Core;

public enum LogLevel { System, In, Out, Event, Action, Warn, Error }

public static class LogColors
{
    public static Color Of(LogLevel l) => l switch
    {
        LogLevel.System => Theme.CyanDim,
        LogLevel.In => Theme.TextDim,
        LogLevel.Out => Theme.CyanBright,
        LogLevel.Event => Theme.Amber,
        LogLevel.Action => Theme.Ok,
        LogLevel.Warn => Theme.Warn,
        LogLevel.Error => Theme.Alert,
        _ => Theme.TextDim,
    };

    public static string Tag(LogLevel l) => l switch
    {
        LogLevel.System => "SYS",
        LogLevel.In => "<<<",
        LogLevel.Out => ">>>",
        LogLevel.Event => "EVT",
        LogLevel.Action => "ACT",
        LogLevel.Warn => "WRN",
        LogLevel.Error => "ERR",
        _ => "-",
    };
}
