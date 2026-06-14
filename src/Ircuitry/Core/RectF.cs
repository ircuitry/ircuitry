using System;
using Microsoft.Xna.Framework;

namespace Ircuitry.Core;

/// <summary>A float rectangle (MonoGame only ships an int Rectangle).</summary>
public struct RectF
{
    public float X, Y, W, H;

    public RectF(float x, float y, float w, float h) { X = x; Y = y; W = w; H = h; }

    public float Left => X;
    public float Top => Y;
    public float Right => X + W;
    public float Bottom => Y + H;
    public Vector2 Pos => new(X, Y);
    public Vector2 Size => new(W, H);
    public Vector2 Center => new(X + W / 2f, Y + H / 2f);

    public bool Contains(Vector2 p) => p.X >= X && p.X < X + W && p.Y >= Y && p.Y < Y + H;
    public bool Contains(float px, float py) => px >= X && px < X + W && py >= Y && py < Y + H;

    public RectF Inflate(float dx, float dy) => new(X - dx, Y - dy, W + dx * 2, H + dy * 2);
    public RectF Shrink(float by) => Inflate(-by, -by);
    public RectF Offset(float dx, float dy) => new(X + dx, Y + dy, W, H);

    public RectF Intersect(RectF o)
    {
        float l = Math.Max(Left, o.Left), t = Math.Max(Top, o.Top);
        float r = Math.Min(Right, o.Right), b = Math.Min(Bottom, o.Bottom);
        return new RectF(l, t, Math.Max(0, r - l), Math.Max(0, b - t));
    }

    public bool Overlaps(RectF o) => Left < o.Right && Right > o.Left && Top < o.Bottom && Bottom > o.Top;

    public Rectangle ToRectangle() => new((int)MathF.Round(X), (int)MathF.Round(Y), (int)MathF.Round(W), (int)MathF.Round(H));

    public static RectF FromCorners(Vector2 a, Vector2 b)
    {
        float x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        return new RectF(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }
}
