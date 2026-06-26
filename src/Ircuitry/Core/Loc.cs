using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Ircuitry.Core;

/// <summary>
/// App localization. English is the source language AND the default; when the operating system is set to
/// Chinese (any zh* locale), UI strings drawn through the renderer are translated via a lookup table keyed by
/// the exact English string. Detection happens once at startup (override with IRCUITRY_LANG for testing).
///
/// The translation is applied centrally in <see cref="Render.Renderer.SafeText"/>, so there are no call-site
/// changes: every string the app draws (and measures) is localized, and anything not in the table falls back to
/// English. That keeps English a guaranteed-correct default and makes "translate the whole app" a data problem
/// (the zh.json table) rather than a 10,000-call-site refactor.
/// </summary>
public static class Loc
{
    /// <summary>True when the active UI language is Chinese.</summary>
    public static bool Zh { get; private set; }

    private static Dictionary<string, string> _map = new(StringComparer.Ordinal);

    static Loc() => Detect();

    /// <summary>(Re)detect the UI language from IRCUITRY_LANG, then the OS locale. zh* -> Chinese, else English.</summary>
    public static void Detect()
    {
        string lang = Environment.GetEnvironmentVariable("IRCUITRY_LANG");
        if (string.IsNullOrEmpty(lang)) lang = Environment.GetEnvironmentVariable("LC_ALL");
        if (string.IsNullOrEmpty(lang)) lang = Environment.GetEnvironmentVariable("LC_MESSAGES");
        if (string.IsNullOrEmpty(lang)) lang = Environment.GetEnvironmentVariable("LANG");
        if (string.IsNullOrEmpty(lang)) { try { lang = System.Globalization.CultureInfo.CurrentUICulture.Name; } catch { lang = ""; } }
        lang ??= "";
        Zh = lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase) || lang.Contains("Hans") || lang.Contains("Hant");
    }

    /// <summary>Load the en-&gt;zh table from &lt;assetsDir&gt;/i18n/zh.json (best effort; missing = English only).</summary>
    public static void LoadFromDir(string assetsDir)
    {
        try
        {
            var path = Path.Combine(assetsDir, "i18n", "zh.json");
            if (!File.Exists(path)) return;
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (d != null) _map = new Dictionary<string, string>(d, StringComparer.Ordinal);
        }
        catch { /* keep English */ }
    }

    /// <summary>Replace the table directly (tests).</summary>
    public static void LoadMap(Dictionary<string, string> map) => _map = new Dictionary<string, string>(map, StringComparer.Ordinal);

    /// <summary>The number of loaded translations.</summary>
    public static int Count => _map.Count;

    /// <summary>Translate an English UI string. Returns it unchanged when the language is English, the string is
    /// empty, or there is no translation - so English is always a safe fallback and lookups are allocation-free.</summary>
    public static string T(string s)
    {
        if (!Zh || string.IsNullOrEmpty(s)) return s;
        return _map.TryGetValue(s, out var z) ? z : s;
    }
}
