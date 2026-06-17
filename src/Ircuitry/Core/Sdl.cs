using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Ircuitry.Core;

/// <summary>
/// Minimal SDL2 window helpers (maximize / minimize) and a close-intercept, via the SDL that
/// MonoGame already loads. MonoGame itself binds neither these window calls nor an event filter,
/// so using them here is safe. Everything degrades to a no-op if SDL can't be resolved.
/// </summary>
public static class Sdl
{
    static Sdl()
    {
        try { NativeLibrary.SetDllImportResolver(typeof(Sdl).Assembly, Resolve); } catch { /* already registered elsewhere */ }
    }

    private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        if (name != "SDL2") return IntPtr.Zero;
        foreach (var cand in new[] { "SDL2", "libSDL2-2.0.so.0", "libSDL2.so", "SDL2.dll", "libSDL2-2.0.0.dylib", "libSDL2.dylib" })
            if (NativeLibrary.TryLoad(cand, out var h)) return h;
        return IntPtr.Zero;
    }

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_MaximizeWindow(IntPtr w);
    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_MinimizeWindow(IntPtr w);
    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_RestoreWindow(IntPtr w);
    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_HideWindow(IntPtr w);
    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_ShowWindow(IntPtr w);
    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_RaiseWindow(IntPtr w);
    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] private static extern uint SDL_GetWindowFlags(IntPtr w);
    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_SetWindowBordered(IntPtr w, int bordered);
    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_SetWindowAlwaysOnTop(IntPtr w, int onTop);
    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] private static extern int SDL_SetWindowOpacity(IntPtr w, float opacity);

    private const uint SDL_WINDOW_MINIMIZED = 0x40;
    private const uint SDL_WINDOW_MAXIMIZED = 0x80;

    /// <summary>True if the window is minimised - so a caller can un-minimise WITHOUT also unmaximising it.</summary>
    public static bool IsMinimized(IntPtr w) { try { return w != IntPtr.Zero && (SDL_GetWindowFlags(w) & SDL_WINDOW_MINIMIZED) != 0; } catch { return false; } }

    /// <summary>Bring the window to the front and focus it WITHOUT changing its size/maximised state. Only
    /// un-minimises when it is actually minimised (Restore() would otherwise unmaximise a maximised window).</summary>
    public static void BringToFront(IntPtr w)
    {
        try
        {
            if (w == IntPtr.Zero) return;
            if (IsMinimized(w)) SDL_RestoreWindow(w);
            SDL_ShowWindow(w);
            SDL_RaiseWindow(w);
        }
        catch { }
    }

    public static bool IsMaximized(IntPtr w) { try { return w != IntPtr.Zero && (SDL_GetWindowFlags(w) & SDL_WINDOW_MAXIMIZED) != 0; } catch { return false; } }
    public static void ToggleMaximize(IntPtr w) { try { if (w == IntPtr.Zero) return; if (IsMaximized(w)) SDL_RestoreWindow(w); else SDL_MaximizeWindow(w); } catch { } }
    public static void SetBordered(IntPtr w, bool bordered) { try { if (w != IntPtr.Zero) SDL_SetWindowBordered(w, bordered ? 1 : 0); } catch { } }

    public static bool AlwaysOnTop { get; private set; }
    public static void ToggleAlwaysOnTop(IntPtr w)
    { try { if (w == IntPtr.Zero) return; AlwaysOnTop = !AlwaysOnTop; SDL_SetWindowAlwaysOnTop(w, AlwaysOnTop ? 1 : 0); } catch { } }

    /// <summary>Set whole-window opacity (1 = opaque). Compositor-level, so it works on our opaque GL framebuffer;
    /// on compositors with a blur effect a translucent window is frosted automatically. Clamped so it never vanishes.</summary>
    public static void SetOpacity(IntPtr w, float opacity)
    { try { if (w != IntPtr.Zero) SDL_SetWindowOpacity(w, Math.Clamp(opacity, 0.4f, 1f)); } catch { } }

    public static void Maximize(IntPtr w) { try { if (w != IntPtr.Zero) SDL_MaximizeWindow(w); } catch { } }
    public static void Minimize(IntPtr w) { try { if (w != IntPtr.Zero) SDL_MinimizeWindow(w); } catch { } }
    public static void Restore(IntPtr w) { try { if (w != IntPtr.Zero) SDL_RestoreWindow(w); } catch { } }
    public static void Hide(IntPtr w) { try { if (w != IntPtr.Zero) SDL_HideWindow(w); } catch { } }       // vanish to tray
    public static void Show(IntPtr w) { try { if (w != IntPtr.Zero) { SDL_ShowWindow(w); SDL_RaiseWindow(w); } } catch { } }

    // ---- custom client-side decorations: borderless window the OS still drags/resizes ----
    // A hit-test tells SDL which parts of the (borderless) window behave like a title bar (draggable, with
    // native edge-snap + double-click maximize) or a resize edge, and which are normal content (so our tabs
    // and buttons stay clickable). The UI publishes the title-bar height + the rects that must NOT drag.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int HitTest(IntPtr win, IntPtr area, IntPtr data);
    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] private static extern int SDL_SetWindowHitTest(IntPtr w, HitTest? cb, IntPtr data);

    private static HitTest? _hit;                       // kept alive for the native side
    private static volatile float[] _noDrag = Array.Empty<float>();   // [x,y,w,h] quads that stay clickable
    private static volatile int _tbBottom;              // title-bar height (px); above this is draggable
    private static volatile int _winW, _winH;
    private static volatile bool _maxed;
    public static bool CustomChrome { get; private set; }

    /// <summary>Make the window borderless and route dragging/resizing through a hit-test. No-op on failure.</summary>
    public static void EnableCustomChrome(IntPtr w)
    {
        try
        {
            if (w == IntPtr.Zero) return;
            SDL_SetWindowBordered(w, 0);
            _hit = HitCallback;
            if (SDL_SetWindowHitTest(w, _hit, IntPtr.Zero) == 0) CustomChrome = true;
            else { SDL_SetWindowBordered(w, 1); _hit = null; }   // hit-test unsupported -> keep a normal frame
        }
        catch { CustomChrome = false; }
    }

    /// <summary>Each frame: where the title bar ends, the window size, whether it's maximized, and the flat
    /// list of clickable (no-drag) rects in the title bar.</summary>
    public static void PublishTitlebar(int bottom, int winW, int winH, bool maximized, float[] noDrag)
    {
        _tbBottom = bottom; _winW = winW; _winH = winH; _maxed = maximized; _noDrag = noDrag;
    }

    /// <summary>The resize edge under a point in window/UI coords, using the SAME zone the hit-test resizes from,
    /// so the app can show a matching resize cursor. Returns an SDL_HitTestResult code: 0 none, 2..9 RESIZE_*
    /// (2 TL, 3 T, 4 TR, 5 R, 6 BR, 7 B, 8 BL, 9 L). Zero unless custom chrome is on and the window isn't maximized.</summary>
    public static int ResizeEdge(int x, int y)
    {
        if (!CustomChrome || _maxed) return 0;
        int W = _winW, H = _winH; const int B = 6;
        if (W <= 0 || H <= 0) return 0;
        bool l = x < B, rt = x >= W - B, t = y < B, b = y >= H - B;
        if (t && l) return 2; if (t && rt) return 4; if (b && rt) return 6; if (b && l) return 8;
        if (t) return 3; if (b) return 7; if (l) return 9; if (rt) return 5;
        return 0;
    }

    private static int HitCallback(IntPtr win, IntPtr area, IntPtr data)
    {
        // SDL_HitTestResult: 0 NORMAL, 1 DRAGGABLE, 2..9 RESIZE_{TL,T,TR,R,BR,B,BL,L}
        int x = Marshal.ReadInt32(area, 0), y = Marshal.ReadInt32(area, 4);
        int rs = ResizeEdge(x, y);
        if (rs != 0) return rs;
        if (y < _tbBottom)
        {
            var nd = _noDrag;
            for (int i = 0; i + 3 < nd.Length; i += 4)
                if (x >= nd[i] && x < nd[i] + nd[i + 2] && y >= nd[i + 1] && y < nd[i + 1] + nd[i + 3]) return 0;
            return 1;   // draggable title bar
        }
        return 0;
    }

    // ---- close interception (so the window's X can prompt instead of exiting) ----
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EventFilter(IntPtr userdata, IntPtr evt);
    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] private static extern void SDL_SetEventFilter(EventFilter filter, IntPtr userdata);

    private static EventFilter? _filter;   // keep the delegate alive for the native side
    private static volatile bool _closeRequested;

    /// <summary>True once the user tried to close the window; the host clears it after handling.</summary>
    public static bool CloseRequested { get => _closeRequested; set => _closeRequested = value; }

    private const int SDL_QUIT = 0x100;
    private const int SDL_WINDOWEVENT = 0x200;
    private const byte SDL_WINDOWEVENT_CLOSE = 14;

    /// <summary>Swallow window-close / quit events so the host can show a prompt instead of exiting.</summary>
    public static void InterceptClose()
    {
        try
        {
            _filter = (_, evt) =>
            {
                int type = Marshal.ReadInt32(evt);                 // SDL_Event.type
                if (type == SDL_QUIT) { _closeRequested = true; return 0; }
                if (type == SDL_WINDOWEVENT && Marshal.ReadByte(evt, 12) == SDL_WINDOWEVENT_CLOSE) { _closeRequested = true; return 0; }
                return 1;                                          // keep every other event
            };
            SDL_SetEventFilter(_filter, IntPtr.Zero);
        }
        catch { /* no SDL -> window closes normally */ }
    }
}
