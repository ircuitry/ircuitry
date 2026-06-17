using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Ircuitry.Core;

/// <summary>
/// Maps Phosphor icon NAMES ("robot", "dice-five", "chat-circle") to their font glyph, so node and UI icons
/// are crisp, tintable vector glyphs. The glyphs live in the Unicode Private Use Area and are drawn by the
/// Phosphor fallback font that <c>Fonts</c> adds to every typeface. An unknown name passes straight through.
/// THERE ARE NO EMOJI IN THIS CODEBASE: every icon is a Phosphor name passed through <see cref="Glyph"/>.
/// </summary>
public static class Icons
{
    private static Dictionary<string, string>? _glyph;   // name -> single PUA char
    private static bool _tried;

    private static Dictionary<string, string> Map()
    {
        if (_glyph != null) return _glyph;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!_tried)
        {
            _tried = true;
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "assets", "fonts", "phosphor-codepoints.json");
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (int.TryParse(p.Value.GetString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp))
                        map[p.Name] = char.ConvertFromUtf32(cp);
            }
            catch { /* no map -> Glyph passes names through unchanged */ }
        }
        return _glyph = map;
    }

    /// <summary>The renderable glyph for a Phosphor icon name; passes an unknown name through unchanged.</summary>
    public static string Glyph(string name)
        => string.IsNullOrEmpty(name) ? name
         : Map().TryGetValue(name, out var g) ? g : name;

    /// <summary>True if <paramref name="name"/> is a known Phosphor icon name.</summary>
    public static bool Has(string name) => !string.IsNullOrEmpty(name) && Map().ContainsKey(name);
}
