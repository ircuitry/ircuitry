using Ircuitry.Core;

namespace Ircuitry.Screens;

/// <summary>Computes the dock layout (top bar, bot tabs, rails, canvas, console) from the viewport.</summary>
public struct Layout
{
    public RectF TopBar;
    public RectF Tabs;
    public RectF StatusBar;
    public RectF Palette;
    public RectF Inspector;
    public RectF Canvas;
    public RectF Console;

    public const float TopH = 56f;
    public const float TabsH = 34f;
    public const float StatusH = 26f;
    public const float Margin = 10f;
    public const float Gap = 10f;
    public const float PaletteW = 252f;
    public const float InspectorW = 324f;
    public const float ConsoleH = 208f;

    /// <summary>Computes the dock layout. <paramref name="consoleH"/> overrides the event-console height when
    /// &gt; 0 (the user can drag-resize it); 0 means use the default for this viewport.</summary>
    public static Layout Compute(int vw, int vh, float consoleH = 0)
    {
        var l = new Layout();
        l.TopBar = new RectF(0, 0, vw, TopH);
        l.Tabs = new RectF(Margin, TopH + 6, vw - Margin * 2, TabsH);
        l.StatusBar = new RectF(0, vh - StatusH, vw, StatusH);

        float ix = Margin;
        float iy = TopH + 6 + TabsH + Gap;
        float iw = vw - Margin * 2;
        float ih = vh - iy - StatusH - Margin;

        l.Palette = new RectF(ix, iy, PaletteW, ih);
        l.Inspector = new RectF(ix + iw - InspectorW, iy, InspectorW, ih);

        float cx = l.Palette.Right + Gap;
        float cw = l.Inspector.Left - Gap - cx;
        float ch = consoleH > 0 ? consoleH : (ih > 520 ? ConsoleH : 150);
        // keep the canvas usable: the console may take at most ~72% of the work area
        ch = System.Math.Clamp(ch, 120f, System.MathF.Max(120f, ih * 0.72f));
        l.Canvas = new RectF(cx, iy, cw, ih - ch - Gap);
        l.Console = new RectF(cx, l.Canvas.Bottom + Gap, cw, ch);
        return l;
    }
}
