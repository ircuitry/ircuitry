using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Ircuitry.Core;

/// <summary>
/// Maps Phosphor icon NAMES ("robot", "dice-five", "chat-circle") to their font glyph, so node and UI icons
/// are crisp, tintable vector glyphs instead of multicolour emoji. The glyphs live in the Unicode Private
/// Use Area and are drawn by the Phosphor fallback font that <c>Fonts</c> adds to every typeface. An unknown
/// name - or a literal emoji from an un-migrated node - passes straight through, so a missing mapping
/// degrades to the old emoji rather than rendering blank.
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
            var dir = Path.Combine(AppContext.BaseDirectory, "assets", "fonts");
            try
            {
                // name -> glyph (Phosphor PUA codepoints)
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "phosphor-codepoints.json")));
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (int.TryParse(p.Value.GetString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp))
                        map[p.Name] = char.ConvertFromUtf32(cp);
                // and emoji -> the same glyph (via emoji -> name), so any leftover UI emoji still renders an icon
                using var em = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "emoji-icons.json")));
                foreach (var p in em.RootElement.EnumerateObject())
                    if (map.TryGetValue(p.Value.GetString() ?? "", out var g)) map[p.Name] = g;
            }
            catch { /* no map -> Glyph passes names through unchanged */ }
        }
        return _glyph = map;
    }

    /// <summary>The renderable glyph for an icon name; passes an emoji / unknown name through unchanged.</summary>
    public static string Glyph(string nameOrGlyph)
        => string.IsNullOrEmpty(nameOrGlyph) ? nameOrGlyph
         : Map().TryGetValue(nameOrGlyph, out var g) ? g : nameOrGlyph;

    /// <summary>True if <paramref name="name"/> is a known Phosphor icon name.</summary>
    public static bool Has(string name) => !string.IsNullOrEmpty(name) && Map().ContainsKey(name);

    /// <summary>Replace every mapped emoji EMBEDDED in a string with its Phosphor glyph (so chrome labels,
    /// toasts and titles render icons, not emoji). ASCII text returns instantly; user-typed text with an
    /// unmapped emoji is left as-is. Variation selectors (U+FE0F) are ignored when matching.</summary>
    public static string Swap(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        bool any = false;
        foreach (var ch in s) if (ch >= 0x2190) { any = true; break; }   // fast path: no symbol/emoji range
        if (!any) return s;
        var map = Map();
        System.Text.StringBuilder? sb = null;   // allocate only if something is actually replaced (keep the instance otherwise)
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext())
        {
            var te = (string)e.Current;
            var key = te.Replace("️", "");   // strip the emoji variation selector to match the map keys
            if (map.TryGetValue(key, out var g))
            {
                sb ??= new System.Text.StringBuilder(s.Length).Append(s, 0, e.ElementIndex);
                sb.Append(g);
            }
            else sb?.Append(te);
        }
        return sb?.ToString() ?? s;
    }
}
