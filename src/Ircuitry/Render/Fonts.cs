using System;
using System.IO;
using FontStashSharp;

namespace Ircuitry.Render;

public enum FontKind { Sans, SansBold, Mono, MonoBold, Display }

/// <summary>
/// One <see cref="FontSystem"/> per typeface. Warm + friendly set by default: Ubuntu for UI,
/// Ubuntu Mono for code/console/fields, and Fredoka (rounded) as the cute display face for the
/// wordmark, panel titles and node names. The UI and display faces can be re-pointed at runtime
/// (a bundled face or any .ttf/.otf on disk) so a theme can change the typography; the mono face
/// stays fixed so code stays monospaced.
/// </summary>
public sealed class Fonts : IDisposable
{
    private FontSystem _sans;
    private FontSystem _sansBold;
    private readonly FontSystem _mono;
    private readonly FontSystem _monoBold;
    private FontSystem _display;

    private readonly string _dir;
    private readonly byte[] _icons, _emoji;
    private readonly byte[]? _cjk;   // Noto Sans SC fallback so Chinese (CJK) glyphs render in every face
    // kept so we can rebuild a face after a swap
    private readonly byte[] _ubuntuR, _ubuntuB, _monoR, _fredoka;
    private string _uiKey = "default", _displayKey = "default";   // current faces, so a no-op swap never rebuilds

    public Fonts(string fontDir)
    {
        FontSystemDefaults.FontResolutionFactor = 2.0f;
        FontSystemDefaults.KernelWidth = 2;
        FontSystemDefaults.KernelHeight = 2;

        _dir = fontDir;
        // fallbacks - glyph lookup falls through in add-order (disjoint codepoint ranges): the Phosphor
        // icon font renders our named icons (PUA glyphs, tintable), NotoEmoji covers any leftover emoji.
        _icons = File.ReadAllBytes(Path.Combine(fontDir, "Phosphor.ttf"));
        _emoji = File.ReadAllBytes(Path.Combine(fontDir, "NotoEmoji.ttf"));
        _ubuntuR = File.ReadAllBytes(Path.Combine(fontDir, "Ubuntu-R.ttf"));
        _ubuntuB = File.ReadAllBytes(Path.Combine(fontDir, "Ubuntu-B.ttf"));
        _monoR = File.ReadAllBytes(Path.Combine(fontDir, "UbuntuMono-R.ttf"));
        _fredoka = File.ReadAllBytes(Path.Combine(fontDir, "Fredoka.ttf"));
        try { _cjk = File.ReadAllBytes(Path.Combine(fontDir, "NotoSansSC.ttf")); } catch { _cjk = null; }   // optional CJK fallback

        _sans = Build(_ubuntuR);
        _sansBold = Build(_ubuntuB);
        _mono = Build(_monoR);
        _monoBold = Build(File.ReadAllBytes(Path.Combine(fontDir, "UbuntuMono-B.ttf")));
        _display = Build(_fredoka);
    }

    private FontSystem Build(byte[] main)
    {
        var fs = new FontSystem();
        fs.AddFont(main);
        if (_cjk != null) fs.AddFont(_cjk);   // Chinese glyphs fall back here when the main face lacks them
        fs.AddFont(_icons);
        fs.AddFont(_emoji);
        return fs;
    }

    /// <summary>Resolve a font choice ("default" | "rounded" | "mono" | a file path) to (regular, bold) bytes.
    /// Unknown keys and unreadable paths fall back to <paramref name="fallback"/>.</summary>
    private (byte[] reg, byte[] bold) Resolve(string? choice, (byte[] reg, byte[] bold) fallback)
    {
        switch ((choice ?? "default").Trim().ToLowerInvariant())
        {
            case "" or "default": return (_ubuntuR, _ubuntuB);
            case "rounded": return (_fredoka, _fredoka);
            case "mono": return (_monoR, _monoR);
            default:
                try
                {
                    var path = choice!.Trim();
                    if (File.Exists(path) && (path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)))
                    {
                        var bytes = File.ReadAllBytes(path);
                        return (bytes, bytes);   // one file serves both weights
                    }
                }
                catch { /* fall through to fallback */ }
                return fallback;
        }
    }

    private static string NormKey(string? c)
    {
        var s = (c ?? "default").Trim();
        var l = s.ToLowerInvariant();
        return l.Length == 0 || l == "default" ? "default" : l == "rounded" ? "rounded" : l == "mono" ? "mono" : s;
    }

    /// <summary>Re-point the UI (sans) face. A no-op when the face is unchanged, so re-applying a theme that
    /// doesn't touch fonts never rebuilds the atlas (which would flash un-rasterised glyphs for a frame).</summary>
    public void SetUiFont(string? choice)
    {
        var key = NormKey(choice);
        if (key == _uiKey) return;
        var (reg, bold) = Resolve(choice, (_ubuntuR, _ubuntuB));
        var oldR = _sans; var oldB = _sansBold;
        _sans = Build(reg); _sansBold = Build(bold);
        _uiKey = key;
        try { oldR.Dispose(); oldB.Dispose(); } catch { }
    }

    /// <summary>Re-point the display face (wordmark / titles / node names). No-op when unchanged.</summary>
    public void SetDisplayFont(string? choice)
    {
        var key = NormKey(choice);
        if (key == _displayKey) return;
        var (reg, _) = Resolve(choice, (_fredoka, _fredoka));
        var old = _display;
        _display = Build(reg);
        _displayKey = key;
        try { old.Dispose(); } catch { }
    }

    private FontSystem System(FontKind kind) => kind switch
    {
        FontKind.SansBold => _sansBold,
        FontKind.Mono => _mono,
        FontKind.MonoBold => _monoBold,
        FontKind.Display => _display,
        _ => _sans,
    };

    public DynamicSpriteFont Get(FontKind kind, int size) => System(kind).GetFont(Math.Clamp(size, 6, 200));

    public void Dispose()
    {
        _sans.Dispose();
        _sansBold.Dispose();
        _mono.Dispose();
        _monoBold.Dispose();
        _display.Dispose();
    }
}
