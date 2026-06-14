using Microsoft.Xna.Framework;

namespace Ircuitry.Core;

/// <summary>Global animation clock. <see cref="Time"/> is seconds since start.</summary>
public sealed class Clock
{
    public float Time { get; private set; }
    public float Dt { get; private set; }
    public long Frame { get; private set; }

    public void Tick(GameTime gt)
    {
        Dt = (float)gt.ElapsedGameTime.TotalSeconds;
        Time = (float)gt.TotalGameTime.TotalSeconds;
        Frame++;
    }

    /// <summary>0..1 triangle wave of the given period - handy for pulsing glows.</summary>
    public float Pulse(float period, float phase = 0f)
    {
        float t = (Time / period + phase) % 1f;
        return t < 0.5f ? t * 2f : 2f - t * 2f;
    }

    public float Sin01(float period, float phase = 0f) =>
        0.5f + 0.5f * System.MathF.Sin((Time / period + phase) * System.MathF.PI * 2f);
}
