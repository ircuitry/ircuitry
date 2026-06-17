using System;
using System.IO;
using FontStashSharp;

namespace Ircuitry.Render;

public enum FontKind { Sans, SansBold, Mono, MonoBold, Display }

/// <summary>
/// One <see cref="FontSystem"/> per typeface. Warm + friendly set: Ubuntu for UI,
/// Ubuntu Mono for code/console/fields, and Fredoka (rounded) as the cute display
/// face for the wordmark, panel titles and node names.
/// </summary>
public sealed class Fonts : IDisposable
{
    private readonly FontSystem _sans;
    private readonly FontSystem _sansBold;
    private readonly FontSystem _mono;
    private readonly FontSystem _monoBold;
    private readonly FontSystem _display;

    public Fonts(string fontDir)
    {
        FontSystemDefaults.FontResolutionFactor = 2.0f;
        FontSystemDefaults.KernelWidth = 2;
        FontSystemDefaults.KernelHeight = 2;

        // fallbacks - glyph lookup falls through in add-order (disjoint codepoint ranges): the Phosphor
        // icon font renders our named icons (PUA glyphs, tintable), NotoEmoji covers any leftover emoji.
        var icons = File.ReadAllBytes(Path.Combine(fontDir, "Phosphor.ttf"));
        var emoji = File.ReadAllBytes(Path.Combine(fontDir, "NotoEmoji.ttf"));
        _sans = Load(fontDir, "Ubuntu-R.ttf", icons, emoji);
        _sansBold = Load(fontDir, "Ubuntu-B.ttf", icons, emoji);
        _mono = Load(fontDir, "UbuntuMono-R.ttf", icons, emoji);
        _monoBold = Load(fontDir, "UbuntuMono-B.ttf", icons, emoji);
        _display = Load(fontDir, "Fredoka.ttf", icons, emoji);
    }

    private static FontSystem Load(string dir, string file, params byte[][] fallbacks)
    {
        var fs = new FontSystem();
        fs.AddFont(File.ReadAllBytes(Path.Combine(dir, file)));
        foreach (var fb in fallbacks) if (fb != null) fs.AddFont(fb);
        return fs;
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
