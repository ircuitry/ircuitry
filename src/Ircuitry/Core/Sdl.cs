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

    private const uint SDL_WINDOW_MINIMIZED = 0x40;

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

    public static void Maximize(IntPtr w) { try { if (w != IntPtr.Zero) SDL_MaximizeWindow(w); } catch { } }
    public static void Minimize(IntPtr w) { try { if (w != IntPtr.Zero) SDL_MinimizeWindow(w); } catch { } }
    public static void Restore(IntPtr w) { try { if (w != IntPtr.Zero) SDL_RestoreWindow(w); } catch { } }
    public static void Hide(IntPtr w) { try { if (w != IntPtr.Zero) SDL_HideWindow(w); } catch { } }       // vanish to tray
    public static void Show(IntPtr w) { try { if (w != IntPtr.Zero) { SDL_ShowWindow(w); SDL_RaiseWindow(w); } } catch { } }

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
        catch { /* no SDL → window closes normally */ }
    }
}
