using Ircuitry.Core;

namespace Ircuitry.Screens;

/// <summary>Computes the dock layout (top bar, bot tabs, rails, canvas, console) from the viewport.</summary>
public struct Layout
{
    public RectF Titlebar;   // custom client-side title bar (icon + bot tabs + File/More + window controls)
    public RectF TopBar;     // the toolbar row below the title bar (run/history/test/apply + status)
    public RectF StatusBar;
    public RectF Palette;
    public RectF Inspector;
    public RectF Canvas;
    public RectF Console;

    public const float TitlebarH = 46f;
    public const float TopH = 44f;        // toolbar height (kept the name so button code stays put)
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
        l.Titlebar = new RectF(0, 0, vw, TitlebarH);
        l.TopBar = new RectF(0, TitlebarH, vw, TopH);
        l.StatusBar = new RectF(0, vh - StatusH, vw, StatusH);

        float ix = Margin;
        float iy = TitlebarH + TopH + Gap;
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
