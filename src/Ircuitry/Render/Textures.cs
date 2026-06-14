using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Ircuitry.Render;

/// <summary>
/// Procedurally generated, anti-aliased white textures used for every HUD
/// primitive (so we never ship image assets and everything tints cleanly).
/// </summary>
public sealed class Textures : IDisposable
{
    public readonly Texture2D Pixel;     // 1x1 white
    public readonly Texture2D Disc;      // AA filled circle
    public readonly Texture2D Ring;      // AA hollow ring (thin stroke)
    public readonly Texture2D Glow;      // soft radial falloff (additive)
    public readonly Texture2D RoundFill; // rounded-rect fill, 9-sliceable
    public readonly Texture2D RoundLine; // rounded-rect outline, 9-sliceable

    // 9-slice metadata for the rounded textures.
    public const int RoundCorner = 18;   // px of corner radius region
    public const int RoundMid = 6;        // px of stretchable middle
    public const int RoundSize = RoundCorner * 2 + RoundMid;

    private const int DiscSize = 128;

    public Textures(GraphicsDevice gd)
    {
        Pixel = Solid(gd, Color.White);
        Disc = MakeDisc(gd, DiscSize);
        Ring = MakeRing(gd, DiscSize, 0.86f);
        Glow = MakeGlow(gd, DiscSize);
        RoundFill = MakeRoundFill(gd, RoundSize, RoundCorner);
        RoundLine = MakeRoundLine(gd, RoundSize, RoundCorner, 2.4f);
    }

    private static Texture2D Solid(GraphicsDevice gd, Color c)
    {
        var t = new Texture2D(gd, 1, 1);
        t.SetData(new[] { c });
        return t;
    }

    private static float Sat(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

    private static Texture2D MakeDisc(GraphicsDevice gd, int n)
    {
        var px = new Color[n * n];
        float r = n / 2f - 1f;
        var c = new Vector2(n / 2f, n / 2f);
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), c);
            float a = Sat(r - d + 0.5f); // 1px AA edge
            px[y * n + x] = new Color(a, a, a, a);
        }
        var t = new Texture2D(gd, n, n);
        t.SetData(px);
        return t;
    }

    private static Texture2D MakeRing(GraphicsDevice gd, int n, float innerFrac)
    {
        var px = new Color[n * n];
        float rOuter = n / 2f - 1f;
        float rInner = rOuter * innerFrac;
        var c = new Vector2(n / 2f, n / 2f);
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), c);
            float outer = Sat(rOuter - d + 0.5f);
            float inner = Sat(d - rInner + 0.5f);
            float a = Math.Min(outer, inner);
            px[y * n + x] = new Color(a, a, a, a);
        }
        var t = new Texture2D(gd, n, n);
        t.SetData(px);
        return t;
    }

    private static Texture2D MakeGlow(GraphicsDevice gd, int n)
    {
        var px = new Color[n * n];
        float r = n / 2f;
        var c = new Vector2(n / 2f, n / 2f);
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), c) / r;
            float a = Sat(1f - d);
            a = a * a * a;          // tighter, brighter core
            px[y * n + x] = new Color(a, a, a, a);
        }
        var t = new Texture2D(gd, n, n);
        t.SetData(px);
        return t;
    }

    // signed distance to a rounded box centered in the texture.
    private static float SdRoundBox(float px, float py, float halfW, float halfH, float r)
    {
        float qx = Math.Abs(px) - halfW + r;
        float qy = Math.Abs(py) - halfH + r;
        float ax = Math.Max(qx, 0f);
        float ay = Math.Max(qy, 0f);
        float outside = MathF.Sqrt(ax * ax + ay * ay);
        float inside = Math.Min(Math.Max(qx, qy), 0f);
        return outside + inside - r;
    }

    private static Texture2D MakeRoundFill(GraphicsDevice gd, int n, int corner)
    {
        var px = new Color[n * n];
        float half = n / 2f;
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float d = SdRoundBox(x + 0.5f - half, y + 0.5f - half, half - 0.5f, half - 0.5f, corner);
            float a = Sat(0.5f - d);
            px[y * n + x] = new Color(a, a, a, a);
        }
        var t = new Texture2D(gd, n, n);
        t.SetData(px);
        return t;
    }

    private static Texture2D MakeRoundLine(GraphicsDevice gd, int n, int corner, float stroke)
    {
        var px = new Color[n * n];
        float half = n / 2f;
        float hs = stroke * 0.5f;
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float d = SdRoundBox(x + 0.5f - half, y + 0.5f - half, half - 0.5f, half - 0.5f, corner);
            float a = Sat(hs - Math.Abs(d) + 0.5f); // stroke centered on the boundary
            px[y * n + x] = new Color(a, a, a, a);
        }
        var t = new Texture2D(gd, n, n);
        t.SetData(px);
        return t;
    }

    public void Dispose()
    {
        Pixel.Dispose();
        Disc.Dispose();
        Ring.Dispose();
        Glow.Dispose();
        RoundFill.Dispose();
        RoundLine.Dispose();
    }
}
