using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Ircuitry.UiKit;

/// <summary>
/// A declarative 3D world that rides inside a <see cref="UiScene"/> and is rendered (behind the 2D overlay) by
/// MonoGame's own 3D pipeline in the window-host process. Pure data + per-frame tween advance, same as the 2D
/// side - so a node graph can pose a camera and a pile of meshes, animate them, and stack a 2D HUD on top.
/// </summary>
public sealed class Scene3D
{
    public Camera Cam = new();
    public List<Obj3D> Objects = new();

    public void Advance(float dt) { foreach (var o in Objects) o.Advance(dt); }
    public Obj3D? Find(string id) => Objects.Find(o => o.Id == id);
}

/// <summary>A look-at camera: an eye position, a target it points at, and a vertical field of view (degrees).</summary>
public sealed class Camera
{
    public float Px = 0f, Py = 2.5f, Pz = 7f;   // eye
    public float Tx = 0f, Ty = 0f, Tz = 0f;     // look-at target
    public float Fov = 55f;
}

public enum Mesh3D { Box, Sphere, Plane, Cylinder }

/// <summary>One mesh in the world: a primitive kind, a transform (position / rotation in degrees / scale) and a
/// colour. Animatable props: px py pz (position), rx ry rz (rotation), sx sy sz (scale).</summary>
public sealed class Obj3D : ITweenTarget
{
    public string Id = "";
    public Mesh3D Mesh = Mesh3D.Box;
    public float Px, Py, Pz;
    public float Rx, Ry, Rz;
    public float Sx = 1f, Sy = 1f, Sz = 1f;
    public uint Color = 0xC8C2D6FF;
    public string Tex = "";                      // "" = solid Color; "checker"/"boing" = the red/white Boing Ball
    public List<Tween> Tweens = new();

    public float Get(string p) => p switch
    {
        "px" or "x" => Px, "py" or "y" => Py, "pz" or "z" => Pz,
        "rx" => Rx, "ry" => Ry, "rz" => Rz,
        "sx" => Sx, "sy" => Sy, "sz" => Sz, "scale" => Sx,
        _ => 0f,
    };

    public void Set(string p, float v)
    {
        switch (p)
        {
            case "px": case "x": Px = v; break;
            case "py": case "y": Py = v; break;
            case "pz": case "z": Pz = v; break;
            case "rx": Rx = v; break;
            case "ry": Ry = v; break;
            case "rz": Rz = v; break;
            case "sx": Sx = v; break;
            case "sy": Sy = v; break;
            case "sz": Sz = v; break;
            case "scale": Sx = Sy = Sz = v; break;
        }
    }

    public void Advance(float dt)
    {
        for (int i = Tweens.Count - 1; i >= 0; i--)
        {
            Tweens[i].Advance(dt, this);
            if (Tweens[i].Done) Tweens.RemoveAt(i);
        }
    }
}
