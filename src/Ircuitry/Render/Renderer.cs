using System;
using FontStashSharp;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Ircuitry.Core;

namespace Ircuitry.Render;

public enum BlendMode { Alpha, Add }

/// <summary>
/// Thin retained wrapper over <see cref="SpriteBatch"/> giving the whole app a
/// premultiplied-alpha HUD primitive vocabulary: rects, rounded panels, lines,
/// beziers, discs, rings, glows and text. Everything tints a white procedural
/// texture, so it composes cleanly with FontStashSharp's premultiplied glyphs.
/// </summary>
public sealed class Renderer
{
    public readonly GraphicsDevice Gd;
    public readonly SpriteBatch Sb;
    public readonly Textures Tex;
    public readonly Fonts Fonts;

    public int ViewW { get; private set; }
    public int ViewH { get; private set; }

    private static readonly BlendState GlowAdd = new()
    {
        ColorSourceBlend = Blend.One,
        ColorDestinationBlend = Blend.One,
        AlphaSourceBlend = Blend.One,
        AlphaDestinationBlend = Blend.One,
    };

    private static readonly RasterizerState ScissorOn = new() { ScissorTestEnable = true, CullMode = CullMode.None };
    private static readonly RasterizerState ScissorOff = new() { ScissorTestEnable = false, CullMode = CullMode.None };

    /// <summary>The app brand icon (ircuitry), loaded from assets - null if missing.</summary>
    public Texture2D? Brand { get; }

    public Renderer(GraphicsDevice gd, Fonts fonts)
    {
        Gd = gd;
        Sb = new SpriteBatch(gd);
        Tex = new Textures(gd);
        Fonts = fonts;
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "icons", "icon-256.png");
            if (System.IO.File.Exists(path))
            {
                using var fs = System.IO.File.OpenRead(path);
                Brand = Texture2D.FromStream(gd, fs);   // straight alpha - composites under AlphaBlend
            }
        }
        catch { Brand = null; }
    }

    /// <summary>Draw a texture into a destination rect (straight alpha, white tint).</summary>
    public void Image(Texture2D tex, RectF dest) =>
        Sb.Draw(tex, dest.ToRectangle(), Color.White);

    // decoded base64 node icons, cached by key (typeId). null = decode failed (fall back to the emoji glyph).
    private readonly System.Collections.Generic.Dictionary<string, Texture2D?> _iconCache = new();
    public Texture2D? IconTexture(string key, string base64)
    {
        if (_iconCache.TryGetValue(key, out var t)) return t;
        Texture2D? tex = null;
        try
        {
            using var ms = new System.IO.MemoryStream(Convert.FromBase64String(base64.Trim()));
            tex = Texture2D.FromStream(Gd, ms);
        }
        catch { tex = null; }
        _iconCache[key] = tex;
        return tex;
    }

    public void SetViewport(int w, int h) { ViewW = w; ViewH = h; }

    // ---- batch control -------------------------------------------------
    public void Begin(BlendMode mode = BlendMode.Alpha, Rectangle? scissor = null)
    {
        var blend = mode == BlendMode.Add ? GlowAdd : BlendState.AlphaBlend;
        var raster = scissor.HasValue ? ScissorOn : ScissorOff;
        Sb.Begin(SpriteSortMode.Deferred, blend, SamplerState.LinearClamp, DepthStencilState.None, raster);
        if (scissor.HasValue)
            Gd.ScissorRectangle = Rectangle.Intersect(scissor.Value, new Rectangle(0, 0, ViewW, ViewH));
    }

    public void End() => Sb.End();

    // ---- premultiplied tint --------------------------------------------
    public static Color Pm(Color c)
    {
        if (c.A == 255) return c;
        float a = c.A / 255f;
        return new Color((byte)(c.R * a), (byte)(c.G * a), (byte)(c.B * a), c.A);
    }

    // ---- solid rects & lines -------------------------------------------
    public void Fill(RectF r, Color c) =>
        Sb.Draw(Tex.Pixel, new Vector2(r.X, r.Y), null, Pm(c), 0f, Vector2.Zero, new Vector2(r.W, r.H), SpriteEffects.None, 0f);

    public void Fill(Rectangle r, Color c) => Sb.Draw(Tex.Pixel, r, Pm(c));

    public void Line(Vector2 a, Vector2 b, Color c, float width = 1f)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 0.001f) return;
        float ang = MathF.Atan2(d.Y, d.X);
        Sb.Draw(Tex.Pixel, a, null, Pm(c), ang, new Vector2(0, 0.5f), new Vector2(len, width), SpriteEffects.None, 0f);
    }

    public void HLine(float x0, float x1, float y, Color c, float t = 1f) => Fill(new RectF(x0, y - t / 2f, x1 - x0, t), c);
    public void VLine(float x, float y0, float y1, Color c, float t = 1f) => Fill(new RectF(x - t / 2f, y0, t, y1 - y0), c);

    public void RectOutline(RectF r, Color c, float t = 1f)
    {
        Fill(new RectF(r.X, r.Y, r.W, t), c);
        Fill(new RectF(r.X, r.Bottom - t, r.W, t), c);
        Fill(new RectF(r.X, r.Y, t, r.H), c);
        Fill(new RectF(r.Right - t, r.Y, t, r.H), c);
    }

    // ---- rounded panels (9-slice) --------------------------------------
    private void NineSlice(Texture2D tex, RectF dest, int srcCorner, float cornerDest, Color color)
    {
        var col = Pm(color);
        int mid = tex.Width - srcCorner * 2;
        cornerDest = MathF.Min(cornerDest, MathF.Min(dest.W, dest.H) / 2f);
        float mx = dest.W - cornerDest * 2f;
        float my = dest.H - cornerDest * 2f;

        // 3×3 patches computed inline (no per-call array allocations - this runs many times per frame)
        for (int r = 0; r < 3; r++)
        {
            float dpy = r == 0 ? dest.Y : r == 1 ? dest.Y + cornerDest : dest.Bottom - cornerDest;
            float dph = r == 1 ? my : cornerDest;
            int spy = r == 0 ? 0 : r == 1 ? srcCorner : srcCorner + mid;
            int sph = r == 1 ? mid : srcCorner;
            if (dph <= 0 || sph <= 0) continue;
            for (int c = 0; c < 3; c++)
            {
                float dpx = c == 0 ? dest.X : c == 1 ? dest.X + cornerDest : dest.Right - cornerDest;
                float dpw = c == 1 ? mx : cornerDest;
                int spx = c == 0 ? 0 : c == 1 ? srcCorner : srcCorner + mid;
                int spw = c == 1 ? mid : srcCorner;
                if (dpw <= 0 || spw <= 0) continue;
                Sb.Draw(tex, new Vector2(dpx, dpy), new Rectangle(spx, spy, spw, sph), col,
                    0f, Vector2.Zero, new Vector2(dpw / spw, dph / sph), SpriteEffects.None, 0f);
            }
        }
    }

    public void RoundFill(RectF r, Color c, float radius = Textures.RoundCorner) =>
        NineSlice(Tex.RoundFill, r, Textures.RoundCorner, radius, c);

    public void RoundOutline(RectF r, Color c, float radius = Textures.RoundCorner) =>
        NineSlice(Tex.RoundLine, r, Textures.RoundCorner, radius, c);

    // ---- discs, rings, glows -------------------------------------------
    public void Disc(Vector2 center, float radius, Color c)
    {
        float s = radius * 2f / Tex.Disc.Width;
        Sb.Draw(Tex.Disc, center, null, Pm(c), 0f, new Vector2(Tex.Disc.Width / 2f), new Vector2(s), SpriteEffects.None, 0f);
    }

    public void Ring(Vector2 center, float radius, Color c)
    {
        float s = radius * 2f / Tex.Ring.Width;
        Sb.Draw(Tex.Ring, center, null, Pm(c), 0f, new Vector2(Tex.Ring.Width / 2f), new Vector2(s), SpriteEffects.None, 0f);
    }

    /// <summary>Soft radial glow. Use inside a <see cref="BlendMode.Add"/> batch.</summary>
    public void Glow(Vector2 center, float radius, Color c)
    {
        float s = radius * 2f / Tex.Glow.Width;
        Sb.Draw(Tex.Glow, center, null, Pm(c), 0f, new Vector2(Tex.Glow.Width / 2f), new Vector2(s), SpriteEffects.None, 0f);
    }

    // ---- bezier wires ---------------------------------------------------
    public static Vector2 Bezier(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float u = 1f - t;
        float w0 = u * u * u, w1 = 3 * u * u * t, w2 = 3 * u * t * t, w3 = t * t * t;
        return new Vector2(
            w0 * p0.X + w1 * p1.X + w2 * p2.X + w3 * p3.X,
            w0 * p0.Y + w1 * p1.Y + w2 * p2.Y + w3 * p3.Y);
    }

    /// <summary>A point at parameter t∈[0,1] along the same curve BezierLine draws (for travelling dots).</summary>
    public static Vector2 BezierPoint(Vector2 a, Vector2 b, float t)
    {
        float dx = MathF.Max(40f, MathF.Abs(b.X - a.X) * 0.5f);
        return Bezier(a, a + new Vector2(dx, 0), b - new Vector2(dx, 0), b, t);
    }

    public void BezierLine(Vector2 a, Vector2 b, Color c, float width = 2f, int segments = 28)
    {
        // horizontal tangents - classic node-wire look
        float dx = MathF.Max(40f, MathF.Abs(b.X - a.X) * 0.5f);
        Vector2 c1 = a + new Vector2(dx, 0);
        Vector2 c2 = b - new Vector2(dx, 0);
        Vector2 prev = a;
        for (int i = 1; i <= segments; i++)
        {
            Vector2 cur = Bezier(a, c1, c2, b, i / (float)segments);
            Line(prev, cur, c, width);
            // round the joints so thick wires don't look chiseled
            if (i < segments) Disc(cur, width * 0.5f, c);
            prev = cur;
        }
    }

    // ---- text -----------------------------------------------------------
    public Vector2 Measure(DynamicSpriteFont font, string text) => font.MeasureString(text);

    public void Text(DynamicSpriteFont font, string text, Vector2 pos, Color c)
    {
        if (string.IsNullOrEmpty(text)) return;
        Sb.DrawString(font, text, new Vector2(MathF.Round(pos.X), MathF.Round(pos.Y)), Pm(c));
    }

    public void Text(FontKind kind, int size, string text, Vector2 pos, Color c) => Text(Fonts.Get(kind, size), text, pos, c);

    public void TextCentered(DynamicSpriteFont font, string text, RectF box, Color c)
    {
        var m = Measure(font, text);
        Text(font, text, new Vector2(box.X + (box.W - m.X) / 2f, box.Y + (box.H - m.Y) / 2f), c);
    }

    public void TextCenteredX(DynamicSpriteFont font, string text, float cx, float y, Color c)
    {
        var m = Measure(font, text);
        Text(font, text, new Vector2(cx - m.X / 2f, y), c);
    }

    public void TextRight(DynamicSpriteFont font, string text, float right, float y, Color c)
    {
        var m = Measure(font, text);
        Text(font, text, new Vector2(right - m.X, y), c);
    }

    /// <summary>Truncate text with an ellipsis so it fits within maxWidth.</summary>
    public string Ellipsize(DynamicSpriteFont font, string text, float maxWidth)
    {
        if (Measure(font, text).X <= maxWidth) return text;
        const string ell = "…";
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Measure(font, text[..mid] + ell).X <= maxWidth) lo = mid; else hi = mid - 1;
        }
        return lo <= 0 ? ell : text[..lo] + ell;
    }
}
