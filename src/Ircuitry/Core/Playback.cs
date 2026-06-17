namespace Ircuitry.Core;

/// <summary>
/// Visual-only playback settings. When <see cref="SlowMo"/> is on, the editor reveals each fired node
/// one at a time, <see cref="Delay"/> seconds apart, so you can actually follow a run light up. This NEVER
/// slows execution - the bot runs at full speed and only the glow's timing is deferred (see BotRuntime's
/// playback queue). Session-scoped; defaults to off at 0.3s.
/// </summary>
public static class Playback
{
    public static volatile bool SlowMo;
    public static float Delay = 0.3f;            // seconds between revealed nodes
    public const float MinDelay = 0.05f;
    public const float MaxDelay = 1.5f;
}
