using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ircuitry.Core;

/// <summary>
/// Manages the active appearance: loads/saves it to disk, swaps it (with a try-before-use
/// preview/revert used by the install flow), and tracks community themes installed under
/// <c>~/ircuitry/themes/*.irctheme</c>. Colours apply live through <see cref="Theme"/>; the host
/// subscribes to <see cref="Changed"/> to re-apply the things that aren't pure colour - fonts and
/// the window's opacity/glass.
/// </summary>
public static class Themes
{
    private static readonly object Gate = new();

    public static string Home =>
        Environment.GetEnvironmentVariable("IRCUITRY_HOME") is { Length: > 0 } h
            ? h
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ircuitry");

    public static string InstalledDir => Path.Combine(Home, "themes");
    private static string ActiveFile => Path.Combine(Home, "activetheme.irctheme");

    /// <summary>Raised after the active theme changes (apply / preview / revert), so the host can
    /// re-apply fonts and window material. Colours need no hook - they're read live by <see cref="Theme"/>.</summary>
    public static event Action? Changed;

    public static ThemeData Active => Theme.Active;

    private static ThemeData? _stash;     // the appearance to restore if a preview is reverted

    /// <summary>Restore the last-saved appearance at startup. Falls back to the cozy default.</summary>
    public static void LoadActive()
    {
        try { if (File.Exists(ActiveFile)) Theme.Active = ThemeData.FromJson(File.ReadAllText(ActiveFile)); }
        catch { /* keep the default */ }
        Changed?.Invoke();
    }

    public static void SaveActive()
    {
        lock (Gate)
        {
            try { Directory.CreateDirectory(Home); File.WriteAllText(ActiveFile, Theme.Active.ToJson()); }
            catch { /* best effort */ }
        }
    }

    /// <summary>Make <paramref name="t"/> the active appearance (and persist it). Clears any pending preview.</summary>
    public static void Apply(ThemeData t, bool persist = true)
    {
        _stash = null;
        Theme.Active = t;
        Changed?.Invoke();
        if (persist) SaveActive();
    }

    /// <summary>Notify listeners that the active theme was edited in place (e.g. a colour changed in the editor).</summary>
    public static void Touch() => Changed?.Invoke();

    // ---- try-before-use (install confirmation) ----
    public static void Preview(ThemeData t)
    {
        _stash ??= Theme.Active.Clone();
        Theme.Active = t;
        Changed?.Invoke();
    }

    public static bool Previewing => _stash != null;

    public static void Revert()
    {
        if (_stash == null) return;
        Theme.Active = _stash;
        _stash = null;
        Changed?.Invoke();
    }

    /// <summary>Keep the previewed theme: persist it and drop the saved-undo.</summary>
    public static void Keep()
    {
        _stash = null;
        SaveActive();
    }

    // ---- installed community themes ----
    public static IReadOnlyList<(string Path, ThemeData Theme)> ListInstalled()
    {
        var list = new List<(string, ThemeData)>();
        try
        {
            if (Directory.Exists(InstalledDir))
                foreach (var f in Directory.EnumerateFiles(InstalledDir, "*.irctheme").OrderBy(p => p))
                    try { list.Add((f, ThemeData.FromJson(File.ReadAllText(f)))); } catch { /* skip a bad file */ }
        }
        catch { /* no dir yet */ }
        return list;
    }

    public static string SafeFileName(string name)
    {
        var cleaned = new string((name ?? "theme").Select(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_' ? ch : '-').ToArray()).Trim();
        return cleaned.Length == 0 ? "theme" : cleaned;
    }

    /// <summary>Validate and install a theme JSON into the themes folder; returns the written path.</summary>
    public static string Install(string json)
    {
        var t = ThemeData.FromJson(json);                  // throws on invalid JSON -> caller reports it
        Directory.CreateDirectory(InstalledDir);
        var path = Path.Combine(InstalledDir, SafeFileName(t.Name) + ".irctheme");
        File.WriteAllText(path, t.ToJson());               // re-serialize canonically (strips any junk)
        return path;
    }

    public static void Uninstall(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
