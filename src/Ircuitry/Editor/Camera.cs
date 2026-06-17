using System;
using Microsoft.Xna.Framework;
using Ircuitry.Core;

namespace Ircuitry.Editor;

/// <summary>
/// 2D pan/zoom camera. World->screen = world*Zoom + Pan (Pan is screen pixels).
/// Hit-testing and node geometry live in world space; we draw in screen space
/// (manual transform) so text stays crisp at any zoom.
/// </summary>
public sealed class Camera
{
    public Vector2 Pan;
    public float Zoom = 1f;
    public const float MinZoom = 0.25f;
    public const float MaxZoom = 2.6f;
    // Hard ceiling on how far the view can travel. Well below the float magnitude (~2e8) where a
    // fixed grid step falls under the ULP and the grid loop would never terminate, yet far larger
    // than any real graph - so the camera can never run away (e.g. a far minimap drag) and freeze.
    public const float WorldLimit = 1_000_000f;

    public Vector2 WorldToScreen(Vector2 w) => w * Zoom + Pan;
    public Vector2 ScreenToWorld(Vector2 s) => (s - Pan) / Zoom;
    public float ToScreen(float worldLen) => worldLen * Zoom;

    public void PanBy(Vector2 screenDelta) => Pan += screenDelta;

    public void ZoomAt(Vector2 cursorScreen, float notches)
    {
        float old = Zoom;
        float factor = MathF.Pow(1.12f, notches);
        Zoom = Math.Clamp(old * factor, MinZoom, MaxZoom);
        Vector2 world = (cursorScreen - Pan) / old;     // world point under cursor
        Pan = cursorScreen - world * Zoom;               // keep it fixed
    }

    /// <summary>Center the camera so world point <paramref name="world"/> sits at screen point.</summary>
    public void CenterOn(Vector2 world, Vector2 screen) => Pan = screen - world * Zoom;

    /// <summary>
    /// Repair any non-finite pan/zoom and keep the world point under the viewport centre within
    /// +-WorldLimit. Call every frame so nothing - a far minimap drag, a runaway pan - can push the
    /// camera into the float range where grid/route math degrades or loops forever.
    /// </summary>
    public void Sanitize(RectF canvas)
    {
        if (!float.IsFinite(Zoom) || Zoom <= 0f) Zoom = 1f;
        Zoom = Math.Clamp(Zoom, MinZoom, MaxZoom);
        if (!float.IsFinite(Pan.X) || !float.IsFinite(Pan.Y)) Pan = canvas.Center;
        Vector2 c = ScreenToWorld(canvas.Center);
        float cx = Math.Clamp(c.X, -WorldLimit, WorldLimit);
        float cy = Math.Clamp(c.Y, -WorldLimit, WorldLimit);
        if (cx != c.X || cy != c.Y) CenterOn(new Vector2(cx, cy), canvas.Center);
    }
}
