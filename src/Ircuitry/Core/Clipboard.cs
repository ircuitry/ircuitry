using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Ircuitry.Core;

/// <summary>
/// OS clipboard text access via SDL2 (already loaded by MonoGame, so it handles X11/Wayland
/// natively with no extra tools). All calls degrade to no-ops if SDL can't be resolved.
/// </summary>
public static class Clipboard
{
    static Clipboard()
    {
        try { NativeLibrary.SetDllImportResolver(typeof(Clipboard).Assembly, Resolve); } catch { /* already set */ }
    }

    private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        if (name != "SDL2") return IntPtr.Zero;
        foreach (var cand in new[] { "SDL2", "libSDL2-2.0.so.0", "libSDL2.so", "SDL2.dll", "libSDL2-2.0.0.dylib", "libSDL2.dylib" })
            if (NativeLibrary.TryLoad(cand, out var h)) return h;
        return IntPtr.Zero;
    }

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SDL_SetClipboardText([MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SDL_GetClipboardText();
    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_free(IntPtr mem);

    public static void SetText(string text)
    {
        try { SDL_SetClipboardText(text ?? ""); } catch { /* SDL unavailable */ }
    }

    public static string GetText()
    {
        try
        {
            var p = SDL_GetClipboardText();
            if (p == IntPtr.Zero) return "";
            var s = Marshal.PtrToStringUTF8(p) ?? "";
            SDL_free(p);
            return s;
        }
        catch { return ""; }
    }
}
