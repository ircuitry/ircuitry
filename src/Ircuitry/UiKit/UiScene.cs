using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ircuitry.UiKit;

/// <summary>
/// A declarative UI scene: a window (title/size/background) plus a flat list of elements. This is the wire
/// format between the host (the node graph builds it) and a window-host render process (which paints it with
/// ircuitry's own <see cref="Render.Renderer"/>). Pure data + a per-frame tween advance - no rendering here.
/// </summary>
public sealed class UiScene
{
    public string Title = "ircuitry";
    public int Width = 800;
    public int Height = 600;
    public uint Bg = 0x141018FF;                 // RGBA
    public string Controls = "";                 // "" = none; "fps" = WASD move + arrow-keys look on the 3D camera
    public Scene3D? World;                       // optional 3D world, drawn behind the 2D overlay (game + HUD)
    public List<UiElement> Elements = new();

    /// <summary>Advance the 3D world + every 2D element's tweens by <paramref name="dt"/> seconds.</summary>
    public void Advance(float dt) { World?.Advance(dt); foreach (var e in Elements) e.Advance(dt); }

    public UiElement? Find(string id) => Elements.Find(e => e.Id == id);

    private static readonly JsonSerializerOptions J = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters = { new JsonStringEnumConverter() },
    };
    public string ToJson() => JsonSerializer.Serialize(this, J);
    public static UiScene FromJson(string s) { try { return JsonSerializer.Deserialize<UiScene>(s, J) ?? new(); } catch { return new(); } }

    public static string EventJson(UiEvent e) => JsonSerializer.Serialize(e, J);
    public static UiEvent EventFromJson(string s) { try { return JsonSerializer.Deserialize<UiEvent>(s, J) ?? new(); } catch { return new(); } }
}

/// <summary>An interaction the window-host process streams back to the graph: a button click, a text-input
/// submit (Enter), or a window close. Becomes a UI-event trigger in the node runtime.</summary>
public sealed class UiEvent
{
    public string Type = "";    // click | submit | close
    public string Id = "";      // the element id that fired it
    public string Value = "";   // for submit: the input's text
}

public enum UiKind { Panel, Text, Image, Rect, Button, Input }

/// <summary>Anything a <see cref="Tween"/> can animate (2D elements and 3D objects) - exposes named float props.</summary>
public interface ITweenTarget
{
    float Get(string prop);
    void Set(string prop, float value);
}

/// <summary>One drawable: a kind, geometry, style, optional text/image, and any running tweens. Coordinates are
/// in window pixels; when <see cref="Parent"/> is set they are relative to that element's top-left.</summary>
public sealed class UiElement : ITweenTarget
{
    public string Id = "";
    public UiKind Kind = UiKind.Panel;
    public string? Parent;                       // parent element id (null = window root)
    public float X, Y, W = 120, H = 40;
    public uint Color = 0xFFFFFFFF;              // RGBA
    public uint TextColor = 0xF2EEF7FF;          // label colour for panels/buttons/inputs
    public float Alpha = 1f, Scale = 1f, Rotation = 0f, Radius = 12f;
    public bool Filled = true;
    public bool Visible = true;
    public string Text = "";
    public int FontSize = 16;
    public string Font = "sans";                 // sans | bold | mono | monobold | display
    public string Src = "";                      // image path (local file for now)
    public List<Tween> Tweens = new();

    [JsonIgnore] public object? Cache;            // render-side scratch (decoded texture, etc.)

    public float Get(string p) => p switch
    {
        "x" => X, "y" => Y, "w" => W, "h" => H,
        "alpha" => Alpha, "scale" => Scale, "rotation" => Rotation, "radius" => Radius,
        _ => 0f,
    };

    public void Set(string p, float v)
    {
        switch (p)
        {
            case "x": X = v; break;
            case "y": Y = v; break;
            case "w": W = v; break;
            case "h": H = v; break;
            case "alpha": Alpha = v; break;
            case "scale": Scale = v; break;
            case "rotation": Rotation = v; break;
            case "radius": Radius = v; break;
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

/// <summary>A declarative animation on one property: from -&gt; to over a duration with an easing curve, optionally
/// looping or ping-ponging. The render process advances it each frame, so the node's worker thread never blocks.</summary>
public sealed class Tween
{
    public string Prop = "x";                    // x|y|w|h|alpha|scale|rotation|radius
    public float From, To, Duration = 0.5f, Delay, Elapsed;
    public string Ease = "easeInOut";            // linear|easeIn|easeOut|easeInOut|back|bounce
    public bool Loop, PingPong, Done;

    public void Advance(float dt, ITweenTarget e)
    {
        if (Done) return;
        Elapsed += dt;
        float t = Elapsed - Delay;
        if (t < 0f) { e.Set(Prop, From); return; }
        float d = Math.Max(0.0001f, Duration);
        float raw = t / d;
        bool finished = raw >= 1f && !Loop && !PingPong;
        float cycle;
        if (PingPong) { float two = raw % 2f; cycle = two <= 1f ? two : 2f - two; }
        else if (Loop) cycle = raw % 1f;
        else cycle = Math.Clamp(raw, 0f, 1f);
        e.Set(Prop, From + (To - From) * Easing(Ease, cycle));
        if (finished) Done = true;
    }

    private static float Easing(string ease, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return ease switch
        {
            "linear" => t,
            "easeIn" => t * t,
            "easeOut" => 1f - (1f - t) * (1f - t),
            "back" => Back(t),
            "bounce" => Bounce(t),
            _ => t < 0.5f ? 2f * t * t : 1f - MathF.Pow(-2f * t + 2f, 2) / 2f,   // easeInOut
        };
    }

    private static float Back(float t)
    {
        const float c1 = 1.70158f, c3 = c1 + 1f;
        return 1f + c3 * MathF.Pow(t - 1f, 3) + c1 * MathF.Pow(t - 1f, 2);
    }

    private static float Bounce(float t)
    {
        const float n1 = 7.5625f, d1 = 2.75f;
        if (t < 1f / d1) return n1 * t * t;
        if (t < 2f / d1) { t -= 1.5f / d1; return n1 * t * t + 0.75f; }
        if (t < 2.5f / d1) { t -= 2.25f / d1; return n1 * t * t + 0.9375f; }
        t -= 2.625f / d1; return n1 * t * t + 0.984375f;
    }
}
