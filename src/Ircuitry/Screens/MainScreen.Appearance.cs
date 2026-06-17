using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Ircuitry.Core;
using Ircuitry.Render;

namespace Ircuitry.Screens;

/// <summary>
/// The Appearance studio: pick an installed theme, hand-edit the full palette and feel knobs with a
/// live preview, swap fonts, and import/export/share themes. Everything applies instantly (colours are
/// read live by <see cref="Theme"/>; fonts and window opacity are pushed as they change) and persists on close.
/// </summary>
public partial class MainScreen
{
    private bool _appearanceOpen, _aprJustOpened;
    private int _aprTab;                       // 0 Themes, 1 Customize, 2 Import/Export
    private string _aprSelKey = "cyan";        // colour key being edited
    private string _aprHex = "#56C0D2";        // hex buffer for the selected colour
    private string _aprSaveName = "My Theme";  // export name
    private string _aprPath = "";              // custom font / import-export path
    private string _aprImport = "";            // pasted theme JSON
    private string _aprMsg = "";               // small status line
    private List<(string Path, ThemeData Theme)> _aprInstalled = new();

    private static readonly string[] FontOptions = { "Default", "Rounded", "Mono", "Custom" };

    public void DebugOpenAppearance() { _l = Layout.Compute(_vw, _vh, _consoleH); OpenAppearance(); _aprTab = 1; }
    public void DebugThemeInstall() => StageThemeInstall("{\"format\":\"ircuitry.theme.v1\",\"name\":\"Midnight Berry\",\"description\":\"A cozy dark theme with berry accents and a soft glow.\",\"author\":\"ircuitry\",\"category\":\"Dark\",\"dark\":true,\"colors\":{\"void\":\"#241F2E\",\"backdrop\":\"#2A2438\",\"panel\":\"#322B43\",\"panelHi\":\"#3B3350\",\"panelLo\":\"#272034\",\"hairline\":\"#473C5C\",\"edge\":\"#5A4C76\",\"text\":\"#EDE6FA\",\"textDim\":\"#B9ADD3\",\"cyan\":\"#7FD6E4\",\"amber\":\"#F2B86A\",\"magenta\":\"#F08AB0\",\"violet\":\"#B79EE8\",\"lime\":\"#9BD36A\",\"berry\":\"#C88ED6\"},\"knobs\":{\"glow\":1.4,\"glass\":true,\"opacity\":0.94}}");

    public void OpenAppearance()
    {
        _appearanceOpen = true; _aprJustOpened = true; _aprMsg = "";
        _aprSelKey = "cyan"; _aprHex = ThemeData.Hex(Theme.Active.C(_aprSelKey));
        _aprSaveName = Theme.Active.Name == "Cozy (default)" ? "My Theme" : Theme.Active.Name;
        RefreshInstalledThemes();
    }

    private void RefreshInstalledThemes() => _aprInstalled = new List<(string, ThemeData)>(Themes.ListInstalled());

    private void CloseAppearance()
    {
        if (Themes.Previewing) Themes.Revert();   // a half-tried import shouldn't stick
        Themes.SaveActive();
        _appearanceOpen = false;
    }

    private static string FontLabel(string key) => key switch
    {
        "default" or "" => "Default", "rounded" => "Rounded", "mono" => "Mono", _ => "Custom",
    };

    private void DrawAppearanceModal(Renderer r)
    {
        r.Begin();
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.5f));
        float pw = 780, ph = 642;
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        Hud.Panel(r, panel, "Appearance", Theme.Berry);

        float x0 = panel.X + 22, w0 = panel.W - 44, y = panel.Y + Hud.HeaderH + 14;

        // ---- tabs ----
        string[] tabs = { "Themes", "Customize", "Import / Export" };
        float tw = 150;
        for (int i = 0; i < tabs.Length; i++)
        {
            var tr = new RectF(x0 + i * (tw + 8), y, tw, 30);
            bool on = _aprTab == i;
            r.RoundFill(tr, on ? Theme.Mix(Theme.PanelHi, Theme.Berry, 0.30f) : Theme.PanelLo, 9f);
            r.RoundOutline(tr, on ? Theme.Berry : Theme.Hairline, 9f);
            r.TextCenteredX(r.Fonts.Get(FontKind.SansBold, 13), tabs[i], tr.Center.X, tr.Center.Y - 8, on ? Theme.Mix(Theme.Text, Theme.Berry, 0.3f) : Theme.TextDim);
            if (_ui.Enabled && In.LeftPressed && tr.Contains(In.Mouse)) { _aprTab = i; _aprMsg = ""; }
        }
        y += 44;
        var body = new RectF(x0, y, w0, panel.Bottom - 16 - 44 - y);

        if (_aprTab == 0) DrawAprThemes(r, body);
        else if (_aprTab == 1) DrawAprCustomize(r, body);
        else DrawAprImportExport(r, body);

        // ---- footer: status + reset + close ----
        if (_aprMsg.Length > 0)
            r.Text(r.Fonts.Get(FontKind.Sans, 12), r.Ellipsize(r.Fonts.Get(FontKind.Sans, 12), _aprMsg, w0 - 320), new Vector2(x0, panel.Bottom - 38), Theme.TextDim);
        // always enabled: editing the default in place keeps its name, so a name check would wrongly disable this
        if (_ui.Button("apr.reset", new RectF(panel.Right - 22 - 104 - 8 - 170, panel.Bottom - 46, 170, 32), Icons.Glyph("arrow-counter-clockwise") + " Reset to default", Theme.Amber))
        {
            Themes.Apply(ThemeData.Default());                       // fresh cozy default, applied + persisted
            _ui.R.Fonts.SetUiFont("default"); _ui.R.Fonts.SetDisplayFont("default");
            _aprSelKey = "cyan"; _aprHex = ThemeData.Hex(Theme.Active.C(_aprSelKey)); _aprSaveName = "My Theme";
            Ircuitry.Core.Sdl.SetOpacity(WindowHandle, Theme.Active.Opacity);
            _aprMsg = "Reset to the cozy default";
        }
        if (_ui.Button("apr.close", new RectF(panel.Right - 22 - 104, panel.Bottom - 46, 104, 32), "DONE", Theme.Berry, primary: true))
            CloseAppearance();

        r.End();

        if (In.LeftPressed && !panel.Contains(In.Mouse) && !_aprJustOpened) CloseAppearance();
        _aprJustOpened = false;
    }

    private float _aprThemesScroll;

    // ---- tab 0: pick a theme (scrollable, so every installed theme stays reachable to apply or delete) ----
    private void DrawAprThemes(Renderer r, RectF area)
    {
        float x = area.X, w = area.W;
        var rows = new List<(string name, string author, string cat, ThemeData t, string? path)>
        { ("Cozy (default)", "ircuitry", "Cozy", ThemeData.Default(), null) };
        foreach (var (p, t) in _aprInstalled) rows.Add((t.Name, t.Author, t.Category, t, p));

        // the scrolling list lives in its own clipped batch; the header/tabs already flushed, the browse strip is fixed below
        var list = new RectF(x, area.Y, w, area.H - 44);
        float rh = 52, totalH = rows.Count * rh;
        _aprThemesScroll = ClampScroll("aprThemes", Wheel("aprThemes", _aprThemesScroll, list), totalH, list.H);

        r.End();
        r.Begin(BlendMode.Alpha, list.ToRectangle());
        for (int i = 0; i < rows.Count; i++)
        {
            float ry = list.Y + i * rh - _aprThemesScroll;
            if (ry + rh < list.Y || ry > list.Bottom) continue;   // cull off-screen rows (and their buttons)
            var (name, author, cat, t, path) = rows[i];
            var rr = new RectF(x, ry, w, rh - 8);
            bool active = name == Theme.Active.Name;
            r.RoundFill(rr, active ? Theme.Mix(Theme.PanelHi, Theme.Ok, 0.16f) : Theme.PanelHi, 11f);
            r.RoundOutline(rr, active ? Theme.Ok : Theme.Hairline, 11f);

            // mini palette preview
            string[] chips = { "void", "panel", "cyan", "amber", "magenta", "violet", "lime", "text" };
            for (int c = 0; c < chips.Length; c++)
                r.RoundFill(new RectF(rr.X + 12 + c * 18, rr.Center.Y - 8, 15, 16), t.C(chips[c]), 4f);

            r.Text(r.Fonts.Get(FontKind.Display, 14), name, new Vector2(rr.X + 12 + chips.Length * 18 + 12, rr.Y + 7), Theme.Text);
            r.Text(r.Fonts.Get(FontKind.Sans, 11), (cat + "  -  " + (author.Length > 0 ? author : "you")).Trim(), new Vector2(rr.X + 12 + chips.Length * 18 + 12, rr.Y + 26), Theme.TextDim);

            float bx = rr.Right - 12;
            if (path != null)
            {
                var del = new RectF(bx - 34, rr.Center.Y - 14, 34, 28); bx -= 42;
                if (_ui.Button("apr.del." + i, del, Icons.Glyph("trash"), Theme.Alert))
                { Themes.Uninstall(path); RefreshInstalledThemes(); _aprMsg = "Deleted " + name; }
            }
            var apply = new RectF(bx - 88, rr.Center.Y - 14, 88, 28);
            if (_ui.Button("apr.apply." + i, apply, active ? "ACTIVE" : "APPLY", active ? Theme.Idle : Theme.Ok, primary: !active, enabled: !active))
            { Themes.Apply(t.Clone()); _aprSaveName = name == "Cozy (default)" ? "My Theme" : name; _aprMsg = "Applied " + name; }
        }
        r.End();
        r.Begin();   // reopen the main (unclipped) batch the caller's footer draws into

        // fixed browse strip below the list
        var more = new RectF(x, area.Bottom - 36, w, 32);
        int installed = rows.Count - 1;
        r.Text(r.Fonts.Get(FontKind.Sans, 12), installed + " installed  -  " + Icons.Glyph("trash") + " removes one; scroll for more.", new Vector2(x, more.Y + 8), Theme.TextDim);
        if (_ui.Button("apr.more", new RectF(more.Right - 168, more.Y, 168, 32), Icons.Glyph("globe") + "  Browse community", Theme.Cyan))
            Ircuitry.App.DeepLink.OpenUrl("https://ircuitry.github.io/themes.html");
    }

    // ---- tab 1: live editor ----
    private void DrawAprCustomize(Renderer r, RectF area)
    {
        float x = area.X, y = area.Y;
        // colour chips - 8 columns x 4 rows
        int cols = 8; float cw = (area.W - (cols - 1) * 6) / cols, chH = 22, rowH = 36;
        for (int i = 0; i < ThemeData.Palette.Length; i++)
        {
            var (key, label, _) = ThemeData.Palette[i];
            int cx = i % cols, cy = i / cols;
            var cr = new RectF(x + cx * (cw + 6), y + cy * rowH, cw, chH);
            bool sel = key == _aprSelKey;
            r.RoundFill(cr, Theme.Active.C(key), 6f);
            r.RoundOutline(cr, sel ? Theme.Text : Theme.WithAlpha(Theme.Edge, 0.6f), 6f);
            if (sel) r.RoundOutline(new RectF(cr.X - 2, cr.Y - 2, cr.W + 4, cr.H + 4), Theme.Berry, 8f);
            r.Text(r.Fonts.Get(FontKind.Sans, 9), r.Ellipsize(r.Fonts.Get(FontKind.Sans, 9), label, cw), new Vector2(cr.X + 1, cr.Bottom + 1), Theme.TextDim);
            if (_ui.Enabled && In.LeftPressed && cr.Contains(In.Mouse)) { _aprSelKey = key; _aprHex = ThemeData.Hex(Theme.Active.C(key)); }
        }

        float edY = y + 4 * rowH + 14;
        float halfW = (area.W - 24) / 2f;

        // left: selected colour editor
        float lx = x, lw = halfW;
        var col = Theme.Active.C(_aprSelKey);
        r.Text(r.Fonts.Get(FontKind.SansBold, 12), "COLOUR  -  " + _aprSelKey, new Vector2(lx, edY), Theme.TextDim);
        r.RoundFill(new RectF(lx, edY + 20, 54, 40), col, 8f); r.RoundOutline(new RectF(lx, edY + 20, 54, 40), Theme.Edge, 8f);
        _aprHex = _ui.TextField("apr.hex", new RectF(lx + 64, edY + 20, lw - 64, 40), _aprHex, "#RRGGBB");
        if (ThemeData.TryHex(_aprHex, out var typed) && typed != col) { Theme.Active.Colors[_aprSelKey] = typed; col = typed; }

        float sy = edY + 74; string[] chan = { "R", "G", "B" };
        int[] vals = { col.R, col.G, col.B };
        for (int c = 0; c < 3; c++)
        {
            r.Text(r.Fonts.Get(FontKind.MonoBold, 12), chan[c], new Vector2(lx, sy + c * 30 + 4), Theme.TextDim);
            float nv = _ui.Slider("apr.ch" + c, new RectF(lx + 18, sy + c * 30, lw - 60, 22), vals[c], 0, 255);
            r.TextRight(r.Fonts.Get(FontKind.Mono, 12), ((int)nv).ToString(), lx + lw, sy + c * 30 + 4, Theme.TextDim);
            vals[c] = (int)MathF.Round(nv);
        }
        var fromSliders = new Color(vals[0], vals[1], vals[2]);
        if (fromSliders != col) { Theme.Active.Colors[_aprSelKey] = fromSliders; _aprHex = ThemeData.Hex(fromSliders); }

        // right: feel knobs + fonts
        float rx = x + halfW + 24, rw = halfW;
        r.Text(r.Fonts.Get(FontKind.SansBold, 12), "FEEL", new Vector2(rx, edY), Theme.TextDim);
        float ky = edY + 22;
        ky = KnobRow(r, rx, ky, rw, "Glow", 0f, 2f, v => Theme.Active.Glow = v, Theme.Active.Glow);
        ky = KnobRow(r, rx, ky, rw, "Twinkle", 0f, 2f, v => Theme.Active.Twinkle = v, Theme.Active.Twinkle);
        ky = KnobRow(r, rx, ky, rw, "Roundness", 0.25f, 1.75f, v => Theme.Active.Roundness = v, Theme.Active.Roundness);
        float beforeOp = Theme.Active.Opacity;
        ky = KnobRow(r, rx, ky, rw, "Window opacity", 0.5f, 1f, v => Theme.Active.Opacity = v, Theme.Active.Opacity);
        if (Theme.Active.Opacity != beforeOp) Ircuitry.Core.Sdl.SetOpacity(WindowHandle, Theme.Active.Opacity);

        bool glass = _ui.Toggle("apr.glass", new RectF(rx, ky, rw, 24), Theme.Active.Glass, "Frosted glass sheen");
        Theme.Active.Glass = glass; ky += 32;

        r.Text(r.Fonts.Get(FontKind.SansBold, 12), "FONTS", new Vector2(rx, ky), Theme.TextDim); ky += 20;
        string uiNow = FontLabel(Theme.Active.UiFont);
        string uiPick = _ui.Choice("apr.uif", new RectF(rx, ky, rw, 28), FontOptions, uiNow);
        if (uiPick != uiNow) ApplyFontPick(uiPick, ui: true);
        ky += 34;
        string dpNow = FontLabel(Theme.Active.DisplayFont);
        string dpPick = _ui.Choice("apr.dpf", new RectF(rx, ky, rw, 28), FontOptions, dpNow);
        if (dpPick != dpNow) ApplyFontPick(dpPick, ui: false);
        ky += 34;
        if (uiNow == "Custom" || dpNow == "Custom")
        {
            _aprPath = _ui.TextField("apr.fpath", new RectF(rx, ky, rw - 50, 28), _aprPath, "path to .ttf/.otf");
            if (_ui.Button("apr.fset", new RectF(rx + rw - 46, ky, 46, 28), "SET", Theme.Cyan))
            {
                if (uiNow == "Custom") { Theme.Active.UiFont = _aprPath; r.Fonts.SetUiFont(_aprPath); }
                if (dpNow == "Custom") { Theme.Active.DisplayFont = _aprPath; r.Fonts.SetDisplayFont(_aprPath); }
                _aprMsg = File.Exists(_aprPath) ? "Font set" : "File not found - kept previous";
            }
        }
    }

    private float KnobRow(Renderer r, float x, float y, float w, string label, float min, float max, Action<float> set, float current)
    {
        r.Text(r.Fonts.Get(FontKind.Sans, 12), label, new Vector2(x, y), Theme.TextDim);
        float nv = _ui.Slider("apr.k." + label, new RectF(x, y + 16, w - 44, 22), current, min, max);
        r.TextRight(r.Fonts.Get(FontKind.Mono, 11), nv.ToString("0.0"), x + w, y + 18, Theme.TextDim);
        if (Math.Abs(nv - current) > 0.0001f) set(nv);
        return y + 44;
    }

    private void ApplyFontPick(string label, bool ui)
    {
        string key = label switch { "Default" => "default", "Rounded" => "rounded", "Mono" => "mono", _ => _aprPath.Length > 0 ? _aprPath : "custom" };
        if (ui) { Theme.Active.UiFont = key; _ui.R.Fonts.SetUiFont(key); }
        else { Theme.Active.DisplayFont = key; _ui.R.Fonts.SetDisplayFont(key); }
    }

    // ---- tab 2: import / export ----
    private void DrawAprImportExport(Renderer r, RectF area)
    {
        float x = area.X, w = area.W, y = area.Y;
        r.Text(r.Fonts.Get(FontKind.SansBold, 12), "EXPORT THIS APPEARANCE", new Vector2(x, y), Theme.TextDim); y += 22;
        _aprSaveName = _ui.TextField("apr.savename", new RectF(x, y, w - 0, 30), _aprSaveName, "theme name"); y += 40;

        float bw = (w - 16) / 3f;
        if (_ui.Button("apr.copy", new RectF(x, y, bw, 32), Icons.Glyph("copy") + " Copy JSON", Theme.Cyan))
        { Clipboard.SetText(NamedJson()); _aprMsg = "Copied theme JSON to clipboard"; }
        if (_ui.Button("apr.savefile", new RectF(x + bw + 8, y, bw, 32), Icons.Glyph("download-simple") + " Save file", Theme.Cyan))
        {
            try { Directory.CreateDirectory(Themes.InstalledDir); var p = Path.Combine(Themes.InstalledDir, Themes.SafeFileName(_aprSaveName) + ".irctheme"); File.WriteAllText(p, NamedJson()); _aprMsg = "Saved " + p; }
            catch (Exception ex) { _aprMsg = "Save failed: " + ex.Message; }
        }
        if (_ui.Button("apr.install", new RectF(x + 2 * (bw + 8), y, bw, 32), Icons.Glyph("plus") + " Add to library", Theme.Ok, primary: true))
        { try { Themes.Install(NamedJson()); RefreshInstalledThemes(); _aprMsg = "Added to your themes"; } catch (Exception ex) { _aprMsg = ex.Message; } }
        y += 48;

        r.HLine(x, x + w, y, Theme.Hairline, 1.5f); y += 14;
        r.Text(r.Fonts.Get(FontKind.SansBold, 12), "IMPORT  -  paste a theme JSON, try it, then keep or revert", new Vector2(x, y), Theme.TextDim); y += 22;
        _aprImport = _ui.TextArea("apr.import", new RectF(x, y, w, area.Bottom - y - 50), _aprImport, "paste .irctheme JSON here, or use a Browse community install");
        float fy = area.Bottom - 40;
        if (_ui.Button("apr.preview", new RectF(x, fy, 120, 32), Icons.Glyph("eye") + " Preview", Theme.Amber))
        { try { Themes.Preview(ThemeData.FromJson(_aprImport)); _aprMsg = "Previewing - keep or revert"; } catch (Exception ex) { _aprMsg = "Invalid JSON: " + ex.Message; } }
        if (Themes.Previewing)
        {
            if (_ui.Button("apr.keep", new RectF(x + 128, fy, 110, 32), "Keep", Theme.Ok, primary: true))
            { try { Themes.Install(NamedJsonFrom(_aprImport)); } catch { } Themes.Keep(); RefreshInstalledThemes(); _aprMsg = "Kept and added to your library"; }
            if (_ui.Button("apr.revert", new RectF(x + 244, fy, 110, 32), "Revert", Theme.Alert))
            { Themes.Revert(); _aprMsg = "Reverted"; }
        }
        if (_ui.Button("apr.loadfile", new RectF(x + w - 150, fy, 150, 32), Icons.Glyph("folder-open") + " Load file", Theme.Cyan))
        {
            try { if (File.Exists(_aprPath2())) _aprImport = File.ReadAllText(_aprPath2()); _aprMsg = "Loaded - press Preview"; }
            catch (Exception ex) { _aprMsg = ex.Message; }
        }
    }

    private string NamedJson() { var c = Theme.Active.Clone(); c.Name = _aprSaveName.Trim().Length > 0 ? _aprSaveName.Trim() : "My Theme"; if (c.Author.Length == 0) c.Author = "you"; c.Category = "Custom"; return c.ToJson(); }
    private string NamedJsonFrom(string json) { try { var t = ThemeData.FromJson(json); return t.ToJson(); } catch { return NamedJson(); } }
    private string _aprPath2() => _aprPath.Length > 0 ? _aprPath : Path.Combine(Themes.InstalledDir, "theme.irctheme");

    // ============================ install confirmation (deep link / website one-click) ============================
    private bool _themeInstallOpen, _themeInstallJustOpened;
    private ThemeData? _themeStaged;
    private string _themeStagedJson = "";

    /// <summary>Stage a theme for install: apply it as a live preview behind a confirm dialog so the user
    /// sees exactly how it looks before keeping it.</summary>
    private void StageThemeInstall(string text)
    {
        try
        {
            _themeStaged = ThemeData.FromJson(text);
            _themeStagedJson = text;
            Themes.Preview(_themeStaged);
            _themeInstallOpen = true; _themeInstallJustOpened = true;
        }
        catch (Exception ex) { Notify(Icons.Glyph("warning") + " Not a valid theme: " + ex.Message); }
    }

    private void CancelThemeInstall()
    {
        Themes.Revert();
        _themeInstallOpen = false; _themeStaged = null;
    }

    private void DrawThemeInstallModal(Renderer r)
    {
        var t = _themeStaged ?? ThemeData.Default();
        r.Begin();
        r.Fill(new RectF(0, 0, _vw, _vh), Theme.WithAlpha(Color.Black, 0.45f));
        float pw = 580, ph = 376;
        var panel = new RectF((_vw - pw) / 2f, (_vh - ph) / 2f, pw, ph);
        Hud.Panel(r, panel, "Install theme?", Theme.Berry);
        float x = panel.X + 24, w = panel.W - 48, y = panel.Y + Hud.HeaderH + 18;

        r.Text(r.Fonts.Get(FontKind.Display, 20), r.Ellipsize(r.Fonts.Get(FontKind.Display, 20), t.Name, w), new Vector2(x, y), Theme.Text); y += 30;
        string meta = (t.Category.Length > 0 ? t.Category : "Theme") + (t.Author.Length > 0 ? "  -  by " + t.Author : "");
        r.Text(r.Fonts.Get(FontKind.Sans, 12), meta, new Vector2(x, y), Theme.TextDim); y += 26;

        // full palette strip
        int n = ThemeData.Palette.Length; float sw = w / n;
        for (int i = 0; i < n; i++)
            r.Fill(new RectF(x + i * sw, y, sw + 1, 26), t.C(ThemeData.Palette[i].Key));
        r.RoundOutline(new RectF(x, y, w, 26), Theme.Edge, 4f); y += 38;

        if (t.Description.Length > 0)
        { r.Text(r.Fonts.Get(FontKind.Sans, 13), r.Ellipsize(r.Fonts.Get(FontKind.Sans, 13), t.Description, w), new Vector2(x, y), Theme.TextDim); y += 30; }

        r.Text(r.Fonts.Get(FontKind.Sans, 12), Icons.Glyph("eye") + "  You're seeing it live - keep it or cancel to revert.", new Vector2(x, panel.Bottom - 86), Theme.Mix(Theme.Text, Theme.Berry, 0.3f));

        if (_ui.Button("thi.cancel", new RectF(panel.Right - 24 - 232, panel.Bottom - 56, 110, 36), "Cancel", Theme.Idle))
            CancelThemeInstall();
        if (_ui.Button("thi.use", new RectF(panel.Right - 24 - 114, panel.Bottom - 56, 114, 36), "Keep it", Theme.Ok, primary: true))
        {
            try { Themes.Install(_themeStagedJson); } catch { }
            Themes.Keep();
            RefreshInstalledThemes();
            _themeInstallOpen = false; _themeStaged = null;
            Notify(Icons.Glyph("palette") + " Theme applied - " + t.Name);
        }
        r.End();

        if (In.LeftPressed && !panel.Contains(In.Mouse) && !_themeInstallJustOpened) CancelThemeInstall();
        _themeInstallJustOpened = false;
    }
}
