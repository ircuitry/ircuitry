using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using V = Microsoft.Xna.Framework.Graphics.VertexPositionNormalTexture;

namespace Ircuitry.UiKit;

/// <summary>
/// Renders a <see cref="Scene3D"/> with MonoGame's fixed-function 3D pipeline (BasicEffect + a directional key
/// light), using cached primitive meshes. Runs before the 2D overlay, with the depth buffer on - so the node
/// graph gets real lit 3D (the start of node-authored games) composited under a 2D HUD.
/// </summary>
public sealed class Scene3DRenderer : IDisposable
{
    private readonly GraphicsDevice _gd;
    private BasicEffect? _fx;
    private readonly Dictionary<Mesh3D, Prim> _prims = new();

    private sealed class Prim { public VertexBuffer Vb = null!; public IndexBuffer Ib = null!; public int Tris; }

    public Scene3DRenderer(GraphicsDevice gd) { _gd = gd; }

    public void Draw(Scene3D s)
    {
        _fx ??= new BasicEffect(_gd) { VertexColorEnabled = false, TextureEnabled = false, PreferPerPixelLighting = true };
        var prevDepth = _gd.DepthStencilState;
        var prevRaster = _gd.RasterizerState;
        var prevBlend = _gd.BlendState;
        _gd.DepthStencilState = DepthStencilState.Default;
        _gd.RasterizerState = RasterizerState.CullNone;   // both faces: robust regardless of winding for v1
        _gd.BlendState = BlendState.Opaque;

        float aspect = _gd.Viewport.Height > 0 ? _gd.Viewport.AspectRatio : 1.6f;
        var eye = new Vector3(s.Cam.Px, s.Cam.Py, s.Cam.Pz);
        var target = new Vector3(s.Cam.Tx, s.Cam.Ty, s.Cam.Tz);
        if (eye == target) eye += new Vector3(0, 0, 0.001f);
        _fx.View = Matrix.CreateLookAt(eye, target, Vector3.Up);
        _fx.Projection = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(MathHelper.Clamp(s.Cam.Fov, 10f, 120f)), aspect, 0.1f, 1000f);
        _fx.LightingEnabled = true;
        _fx.EnableDefaultLighting();
        _fx.DirectionalLight0.Direction = Vector3.Normalize(new Vector3(-0.5f, -1f, -0.6f));
        _fx.SpecularColor = new Vector3(0.15f);
        _fx.Alpha = 1f;

        foreach (var o in s.Objects)
        {
            var prim = PrimFor(o.Mesh);
            _fx.World =
                Matrix.CreateScale(o.Sx, o.Sy, o.Sz)
                * Matrix.CreateFromYawPitchRoll(MathHelper.ToRadians(o.Ry), MathHelper.ToRadians(o.Rx), MathHelper.ToRadians(o.Rz))
                * Matrix.CreateTranslation(o.Px, o.Py, o.Pz);
            byte r = (byte)(o.Color >> 24), g = (byte)(o.Color >> 16), b = (byte)(o.Color >> 8);
            _fx.DiffuseColor = new Vector3(r / 255f, g / 255f, b / 255f);
            _gd.SetVertexBuffer(prim.Vb);
            _gd.Indices = prim.Ib;
            foreach (var pass in _fx.CurrentTechnique.Passes)
            {
                pass.Apply();
                _gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, prim.Tris);
            }
        }

        _gd.DepthStencilState = prevDepth;
        _gd.RasterizerState = prevRaster;
        _gd.BlendState = prevBlend;
    }

    private Prim PrimFor(Mesh3D m)
    {
        if (_prims.TryGetValue(m, out var p)) return p;
        var (verts, idx) = m switch
        {
            Mesh3D.Plane => Plane(),
            Mesh3D.Sphere => Sphere(20, 28),
            Mesh3D.Cylinder => Cylinder(28),
            _ => Box(),
        };
        var vb = new VertexBuffer(_gd, typeof(VertexPositionNormalTexture), verts.Length, BufferUsage.WriteOnly);
        vb.SetData(verts);
        var ib = new IndexBuffer(_gd, IndexElementSize.SixteenBits, idx.Length, BufferUsage.WriteOnly);
        ib.SetData(idx);
        p = new Prim { Vb = vb, Ib = ib, Tris = idx.Length / 3 };
        _prims[m] = p;
        return p;
    }

    // ---- primitive builders (unit-sized, centered; object scale stretches them) ----
    private static (V[] v, short[] i) Box()
    {
        var v = new List<V>(); var idx = new List<short>();
        void Face(Vector3 nrm, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            short s = (short)v.Count;
            v.Add(new V(a, nrm, Vector2.Zero)); v.Add(new V(b, nrm, Vector2.Zero));
            v.Add(new V(c, nrm, Vector2.Zero)); v.Add(new V(d, nrm, Vector2.Zero));
            idx.AddRange(new short[] { s, (short)(s + 1), (short)(s + 2), s, (short)(s + 2), (short)(s + 3) });
        }
        const float h = 0.5f;
        Face(Vector3.UnitZ, new(-h, -h, h), new(h, -h, h), new(h, h, h), new(-h, h, h));         // front
        Face(-Vector3.UnitZ, new(h, -h, -h), new(-h, -h, -h), new(-h, h, -h), new(h, h, -h));     // back
        Face(Vector3.UnitX, new(h, -h, h), new(h, -h, -h), new(h, h, -h), new(h, h, h));          // right
        Face(-Vector3.UnitX, new(-h, -h, -h), new(-h, -h, h), new(-h, h, h), new(-h, h, -h));     // left
        Face(Vector3.UnitY, new(-h, h, h), new(h, h, h), new(h, h, -h), new(-h, h, -h));          // top
        Face(-Vector3.UnitY, new(-h, -h, -h), new(h, -h, -h), new(h, -h, h), new(-h, -h, h));     // bottom
        return (v.ToArray(), idx.ToArray());
    }

    private static (V[] v, short[] i) Plane()
    {
        const float h = 0.5f;
        var n = Vector3.UnitY;
        var v = new[]
        {
            new V(new(-h, 0, h), n, Vector2.Zero), new V(new(h, 0, h), n, Vector2.Zero),
            new V(new(h, 0, -h), n, Vector2.Zero), new V(new(-h, 0, -h), n, Vector2.Zero),
        };
        return (v, new short[] { 0, 1, 2, 0, 2, 3 });
    }

    private static (V[] v, short[] i) Sphere(int rings, int segs)
    {
        var v = new List<V>(); var idx = new List<short>();
        for (int r = 0; r <= rings; r++)
        {
            float phi = MathHelper.Pi * r / rings;                 // 0..pi
            float y = MathF.Cos(phi) * 0.5f, rad = MathF.Sin(phi) * 0.5f;
            for (int s = 0; s <= segs; s++)
            {
                float theta = MathHelper.TwoPi * s / segs;
                var p = new Vector3(rad * MathF.Cos(theta), y, rad * MathF.Sin(theta));
                v.Add(new V(p, Vector3.Normalize(p), Vector2.Zero));
            }
        }
        int stride = segs + 1;
        for (int r = 0; r < rings; r++)
            for (int s = 0; s < segs; s++)
            {
                short a = (short)(r * stride + s), b = (short)(a + stride);
                idx.AddRange(new short[] { a, b, (short)(a + 1), (short)(a + 1), b, (short)(b + 1) });
            }
        return (v.ToArray(), idx.ToArray());
    }

    private static (V[] v, short[] i) Cylinder(int segs)
    {
        var v = new List<V>(); var idx = new List<short>();
        const float h = 0.5f, rad = 0.5f;
        // side
        for (int s = 0; s <= segs; s++)
        {
            float t = MathHelper.TwoPi * s / segs;
            var nrm = new Vector3(MathF.Cos(t), 0, MathF.Sin(t));
            v.Add(new V(new(nrm.X * rad, -h, nrm.Z * rad), nrm, Vector2.Zero));
            v.Add(new V(new(nrm.X * rad, h, nrm.Z * rad), nrm, Vector2.Zero));
        }
        for (int s = 0; s < segs; s++)
        {
            short a = (short)(s * 2);
            idx.AddRange(new short[] { a, (short)(a + 1), (short)(a + 2), (short)(a + 1), (short)(a + 3), (short)(a + 2) });
        }
        // caps
        void Cap(float y, Vector3 nrm, bool flip)
        {
            short center = (short)v.Count; v.Add(new V(new(0, y, 0), nrm, Vector2.Zero));
            short first = (short)v.Count;
            for (int s = 0; s <= segs; s++)
            {
                float t = MathHelper.TwoPi * s / segs;
                v.Add(new V(new(MathF.Cos(t) * rad, y, MathF.Sin(t) * rad), nrm, Vector2.Zero));
            }
            for (int s = 0; s < segs; s++)
            {
                short a = (short)(first + s), b = (short)(first + s + 1);
                if (flip) idx.AddRange(new short[] { center, b, a }); else idx.AddRange(new short[] { center, a, b });
            }
        }
        Cap(h, Vector3.UnitY, false);
        Cap(-h, -Vector3.UnitY, true);
        return (v.ToArray(), idx.ToArray());
    }

    public void Dispose()
    {
        _fx?.Dispose();
        foreach (var p in _prims.Values) { p.Vb?.Dispose(); p.Ib?.Dispose(); }
        _prims.Clear();
    }
}
