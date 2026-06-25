using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;

namespace Ircuitry.Core;

/// <summary>
/// A complete, serializable appearance: the full colour palette plus a handful of feel knobs
/// (glow, twinkle, roundness, window opacity, frosted glass) and font choices. <see cref="Theme"/>
/// reads the active instance live, so editing one in place re-themes the whole app instantly.
/// Round-trips to the <c>ircuitry.theme.v1</c> JSON (.irctheme) shared by the app, the community
/// repo's index builder, and the website gallery.
/// </summary>
public sealed class ThemeData
{
    public const string Format = "ircuitry.theme.v1";

    public string Name = "Custom";
    public string Description = "";
    public string Author = "";
    public string Category = "Custom";
    public List<string> Tags = new();
    public bool Dark;                         // a hint for previews/sorting; doesn't change behaviour

    public readonly Dictionary<string, Color> Colors = new(StringComparer.OrdinalIgnoreCase);

    // feel knobs (multipliers around 1.0 unless noted)
    public float Glow = 1f;                   // wire/firing glow intensity
    public float Twinkle = 1f;                // streaming sparkle density/brightness on active wires
    public float Roundness = 1f;              // panel corner radius multiplier
    public float Opacity = 1f;                // whole-window opacity (1 = opaque)
    public bool Glass;                        // request frosted-glass window material

    public string UiFont = "default";         // "default" | "rounded" | "mono" | a .ttf/.otf path
    public string DisplayFont = "default";    // face for the wordmark / panel titles / node names

    public Color C(string key) => Colors.TryGetValue(key, out var c) ? c : Color.Magenta;

    /// <summary>Like <see cref="C(string)"/> but falls back to a sibling key (then its default) when a theme
    /// predates the key - so newer node-category accents stay cozy on older themes instead of going magenta.</summary>
    public Color C(string key, string fallback) => Colors.TryGetValue(key, out var c) ? c : C(fallback);

    /// <summary>Canonical palette: (key, human label, group) in editor + serialization order.</summary>
    public static readonly (string Key, string Label, string Group)[] Palette =
    {
        ("void", "Window", "Surfaces"), ("backdrop", "Canvas", "Surfaces"),
        ("panel", "Panel", "Surfaces"), ("panelHi", "Panel (raised)", "Surfaces"),
        ("panelLo", "Field", "Surfaces"), ("hairline", "Hairline", "Surfaces"),
        ("edge", "Edge", "Surfaces"),
        ("gridMinor", "Grid minor", "Grid"), ("gridMajor", "Grid major", "Grid"),
        ("gridAxis", "Grid axis", "Grid"),
        ("cyan", "Primary", "Accents"), ("cyanBright", "Primary bright", "Accents"),
        ("cyanDim", "Primary dim", "Accents"), ("cyanDeep", "Primary tint", "Accents"),
        ("amber", "Honey", "Accents"), ("amberBright", "Honey bright", "Accents"),
        ("amberDim", "Honey dim", "Accents"),
        ("magenta", "Data", "Categories"), ("violet", "Logic", "Categories"),
        ("lime", "IRC", "Categories"), ("berry", "AI", "Categories"),
        ("sky", "Storage", "Categories"), ("teal", "IRCv3", "Categories"),
        ("blueberry", "Code", "Categories"),
        ("coral", "Network", "Categories"), ("gold", "Media", "Categories"), ("mint", "UI", "Categories"),
        ("plugin", "App", "Categories"),
        ("ok", "OK", "Status"), ("warn", "Warn", "Status"),
        ("alert", "Alert", "Status"), ("idle", "Idle", "Status"),
        ("text", "Text", "Text"), ("textDim", "Text dim", "Text"),
        ("textFaint", "Text faint", "Text"), ("textInk", "Text on fills", "Text"),
    };

    /// <summary>The built-in cozy baseline (never shipped as a repo theme; the app's default look).</summary>
    public static ThemeData Default()
    {
        var t = new ThemeData { Name = "Cozy (default)", Author = "ircuitry", Category = "Cozy", Description = "ircuitry's warm cream-and-pastel default." };
        void S(string k, int r, int g, int b) => t.Colors[k] = new Color(r, g, b);
        S("void", 244, 236, 216); S("backdrop", 233, 242, 216); S("panel", 252, 247, 235);
        S("panelHi", 255, 252, 244); S("panelLo", 238, 229, 208); S("hairline", 224, 212, 184); S("edge", 201, 182, 144);
        S("gridMinor", 220, 231, 198); S("gridMajor", 202, 219, 170); S("gridAxis", 183, 206, 146);
        S("cyan", 86, 192, 210); S("cyanBright", 126, 214, 228); S("cyanDim", 96, 158, 170); S("cyanDeep", 206, 236, 240);
        S("amber", 242, 174, 70); S("amberBright", 250, 200, 120); S("amberDim", 196, 146, 64);
        S("magenta", 240, 138, 158); S("violet", 176, 158, 226); S("lime", 140, 196, 84); S("berry", 198, 142, 214);
        S("sky", 116, 174, 224); S("teal", 78, 196, 178); S("blueberry", 124, 138, 210);
        S("coral", 244, 150, 116); S("gold", 228, 196, 96); S("mint", 132, 222, 166); S("plugin", 164, 130, 246);
        S("ok", 126, 196, 92); S("warn", 242, 182, 72); S("alert", 235, 116, 104); S("idle", 176, 162, 132);
        S("text", 86, 70, 48); S("textDim", 140, 122, 92); S("textFaint", 180, 164, 134); S("textInk", 251, 247, 236);
        return t;
    }

    public ThemeData Clone()
    {
        var t = new ThemeData
        {
            Name = Name, Description = Description, Author = Author, Category = Category,
            Tags = new List<string>(Tags), Dark = Dark,
            Glow = Glow, Twinkle = Twinkle, Roundness = Roundness, Opacity = Opacity, Glass = Glass,
            UiFont = UiFont, DisplayFont = DisplayFont,
        };
        foreach (var kv in Colors) t.Colors[kv.Key] = kv.Value;
        return t;
    }

    // ---- hex <-> Color ----
    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static bool TryHex(string? s, out Color c)
    {
        c = Color.Magenta;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s.Select(ch => new string(ch, 2)));   // #abc -> #aabbcc
        if (s.Length != 6) return false;
        if (!int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return false;
        c = new Color((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
        return true;
    }

    // ---- serialization (ircuitry.theme.v1) ----
    public string ToJson()
    {
        var colors = new Dictionary<string, string>();
        foreach (var (key, _, _) in Palette) colors[key] = Hex(C(key));
        var root = new Dictionary<string, object?>
        {
            ["format"] = Format,
            ["name"] = Name,
            ["description"] = Description,
            ["author"] = Author,
            ["category"] = Category,
            ["tags"] = Tags,
            ["dark"] = Dark,
            ["colors"] = colors,
            ["knobs"] = new Dictionary<string, object?>
            {
                ["glow"] = Round(Glow), ["twinkle"] = Round(Twinkle), ["roundness"] = Round(Roundness),
                ["opacity"] = Round(Opacity), ["glass"] = Glass,
            },
            ["fonts"] = new Dictionary<string, object?> { ["ui"] = UiFont, ["display"] = DisplayFont },
        };
        return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
    }

    private static double Round(float f) => Math.Round(f, 3);

    /// <summary>Parse an .irctheme. Missing colours fall back to the cozy default so a partial theme still loads.</summary>
    public static ThemeData FromJson(string json)
    {
        var t = Default();
        t.Name = "Custom"; t.Description = ""; t.Author = ""; t.Category = "Custom";
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) t.Name = n.GetString() ?? t.Name;
        if (root.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String) t.Description = d.GetString() ?? "";
        if (root.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.String) t.Author = a.GetString() ?? "";
        if (root.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.String) t.Category = cat.GetString() ?? "Custom";
        if (root.TryGetProperty("dark", out var dk) && (dk.ValueKind == JsonValueKind.True || dk.ValueKind == JsonValueKind.False)) t.Dark = dk.GetBoolean();
        if (root.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array)
        { t.Tags.Clear(); foreach (var e in tg.EnumerateArray()) if (e.ValueKind == JsonValueKind.String) t.Tags.Add(e.GetString()!); }

        if (root.TryGetProperty("colors", out var cols) && cols.ValueKind == JsonValueKind.Object)
            foreach (var p in cols.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && TryHex(p.Value.GetString(), out var col)) t.Colors[p.Name] = col;

        if (root.TryGetProperty("knobs", out var k) && k.ValueKind == JsonValueKind.Object)
        {
            t.Glow = Knob(k, "glow", t.Glow); t.Twinkle = Knob(k, "twinkle", t.Twinkle);
            t.Roundness = Knob(k, "roundness", t.Roundness); t.Opacity = Knob(k, "opacity", t.Opacity);
            if (k.TryGetProperty("glass", out var g) && (g.ValueKind == JsonValueKind.True || g.ValueKind == JsonValueKind.False)) t.Glass = g.GetBoolean();
        }
        if (root.TryGetProperty("fonts", out var f) && f.ValueKind == JsonValueKind.Object)
        {
            if (f.TryGetProperty("ui", out var uf) && uf.ValueKind == JsonValueKind.String) t.UiFont = uf.GetString() ?? "default";
            if (f.TryGetProperty("display", out var df) && df.ValueKind == JsonValueKind.String) t.DisplayFont = df.GetString() ?? "default";
        }
        return t;
    }

    private static float Knob(JsonElement obj, string key, float fallback)
    {
        if (obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d))
            return Math.Clamp((float)d, 0f, 3f);
        return fallback;
    }
}
