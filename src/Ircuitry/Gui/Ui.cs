using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Ircuitry.Core;
using Ircuitry.Input;
using Ircuitry.Render;

namespace Ircuitry.Gui;

/// <summary>
/// A tiny immediate-mode widget layer (buttons, toggles, editable text fields)
/// drawn through <see cref="Renderer"/>. One field is focused at a time; mono
/// font keeps caret/scroll math trivial.
/// </summary>
public sealed class Ui
{
    public Renderer R = null!;
    public InputState In = null!;
    public Clock Clock = null!;

    public string? Focus;             // focused text field id
    private int _caret;
    private int _anchor;              // selection anchor (== _caret means no selection)
    private float _scroll;            // horizontal scroll of focused single-line field
    private int _areaTop;            // first visible wrapped line in the focused text area
    private bool _mouseSel;           // dragging a selection with the mouse
    private double _lastClick;        // multi-click (double/triple) detection
    private int _clicks;
    private Vector2 _lastClickPos;
    private string? _lastClickId;
    private readonly System.Collections.Generic.HashSet<string> _seen = new();

    public bool Enabled = true;       // when false (e.g. a modal is open), widgets ignore input
    public bool AnyFieldFocused => Focus != null;

    public void Begin(Renderer r, InputState input, Clock clock) { R = r; In = input; Clock = clock; _seen.Clear(); }

    /// <summary>Call after all widgets are drawn: blur focus if the focused field wasn't drawn this frame
    /// (e.g. the inspected node changed), so canvas shortcuts don't stay disabled by a stale focus.</summary>
    public void EndFrame()
    {
        if (Focus != null && !_seen.Contains(Focus)) { Focus = null; _caret = 0; _scroll = 0; }
    }

    private bool Over(RectF rect) => Enabled && rect.Contains(In.Mouse);

    public void Label(string text, Vector2 pos, Color c, int size = 13, FontKind kind = FontKind.Mono)
        => R.Text(kind, size, text, pos, c);

    // ---------------------------------------------------------------
    public bool Button(string id, RectF rect, string label, Color accent, bool primary = false, bool enabled = true)
    {
        bool hover = enabled && Over(rect);
        bool down = hover && In.LeftDown;
        Color fill = primary
            ? (down ? Theme.Mix(accent, Theme.Text, 0.18f) : hover ? Theme.Mix(accent, Color.White, 0.14f) : accent)
            : (down ? Theme.Mix(Theme.PanelHi, accent, 0.22f) : hover ? Theme.Mix(Theme.PanelHi, accent, 0.12f) : Theme.PanelHi);

        // soft shadow under buttons for a chunky, tactile feel
        R.RoundFill(new RectF(rect.X, rect.Y + 2.5f, rect.W, rect.H), Theme.WithAlpha(Color.Black, 0.06f), 10f);
        R.RoundFill(rect, enabled ? fill : Theme.PanelLo, 10f);
        R.RoundOutline(rect, enabled ? Theme.WithAlpha(primary ? Theme.Mix(accent, Theme.Text, 0.25f) : accent, hover ? 1f : 0.7f) : Theme.Hairline, 10f);
        var f = R.Fonts.Get(FontKind.SansBold, 14);
        var sz = f.MeasureString(Ircuitry.Render.Renderer.SafeText(label));
        var tc = enabled ? (primary ? Theme.TextInk : Theme.Mix(Theme.Text, accent, 0.2f)) : Theme.TextFaint;
        R.Text(f, label, new Vector2(rect.Center.X - sz.X / 2f, rect.Center.Y - sz.Y / 2f - 1), tc);

        return enabled && hover && In.LeftPressed;
    }

    public bool Toggle(string id, RectF rect, bool value, string label)
    {
        float th = 20f, tw = 38f;
        var track = new RectF(rect.X, rect.Center.Y - th / 2f, tw, th);
        bool hover = Over(rect);
        Color on = Theme.Ok, off = Theme.PanelLo;
        R.RoundFill(track, value ? Theme.WithAlpha(on, 0.30f) : off, th / 2f);
        R.RoundOutline(track, value ? on : Theme.Hairline, th / 2f);
        float knobX = value ? track.Right - th / 2f : track.Left + th / 2f;
        R.Disc(new Vector2(knobX, track.Center.Y), th / 2f - 3f, value ? on : Theme.TextDim);

        var f = R.Fonts.Get(FontKind.Mono, 13);
        R.Text(f, label, new Vector2(track.Right + 12, rect.Center.Y - f.MeasureString(Ircuitry.Render.Renderer.SafeText(label)).Y / 2f), Theme.TextDim);

        bool clicked = hover && In.LeftPressed;
        return clicked ? !value : value;
    }

    /// <summary>A horizontal slider returning the live value. Drag the knob or click the track; updates while held.</summary>
    public float Slider(string id, RectF rect, float value, float min, float max)
    {
        _seen.Add(id);
        bool hover = Over(rect);
        float t = max > min ? Math.Clamp((value - min) / (max - min), 0f, 1f) : 0f;
        var track = new RectF(rect.X, rect.Center.Y - 3f, rect.W, 6f);
        R.RoundFill(track, Theme.PanelLo, 3f);
        float knobX = rect.X + t * rect.W;
        R.RoundFill(new RectF(rect.X, track.Y, knobX - rect.X, 6f), Theme.WithAlpha(Theme.Cyan, 0.55f), 3f);   // filled portion
        R.RoundOutline(track, Theme.Hairline, 3f);

        bool active = Focus == id;
        if (Enabled && hover && In.LeftPressed) { Focus = id; active = true; }
        if (active && In.LeftDown)
        {
            float nt = Math.Clamp((In.Mouse.X - rect.X) / Math.Max(1f, rect.W), 0f, 1f);
            value = min + nt * (max - min);
            t = nt;
            knobX = rect.X + t * rect.W;
        }
        if (In.LeftReleased && active) Focus = null;

        var kc = hover || active ? Theme.Cyan : Theme.CyanDim;
        var kp = new Vector2(Math.Clamp(knobX, rect.X + 7, rect.Right - 7), track.Center.Y);
        R.Disc(kp, 9f, Theme.WithAlpha(kc, 0.20f));
        R.Disc(kp, 7f, Theme.PanelHi);
        R.Ring(kp, 7f, kc);
        return value;
    }

    /// <summary>A vertical slider (top = max, bottom = min). Drag the knob or click the track.</summary>
    public float SliderV(string id, RectF rect, float value, float min, float max)
    {
        _seen.Add(id);
        bool hover = Over(rect);
        float t = max > min ? Math.Clamp((value - min) / (max - min), 0f, 1f) : 0f;
        var track = new RectF(rect.Center.X - 3f, rect.Y, 6f, rect.H);
        R.RoundFill(track, Theme.PanelLo, 3f);
        float knobY = rect.Y + (1f - t) * rect.H;
        R.RoundFill(new RectF(track.X, knobY, 6f, rect.Bottom - knobY), Theme.WithAlpha(Theme.Cyan, 0.55f), 3f);   // filled toward fast (bottom)
        R.RoundOutline(track, Theme.Hairline, 3f);

        bool active = Focus == id;
        if (Enabled && hover && In.LeftPressed) { Focus = id; active = true; }
        if (active && In.LeftDown)
        {
            float nt = Math.Clamp(1f - (In.Mouse.Y - rect.Y) / Math.Max(1f, rect.H), 0f, 1f);
            value = min + nt * (max - min);
            knobY = rect.Y + (1f - nt) * rect.H;
        }
        if (In.LeftReleased && active) Focus = null;

        var kc = hover || active ? Theme.Cyan : Theme.CyanDim;
        var kp = new Vector2(track.Center.X, Math.Clamp(knobY, rect.Y + 7, rect.Bottom - 7));
        R.Disc(kp, 9f, Theme.WithAlpha(kc, 0.20f));
        R.Disc(kp, 7f, Theme.PanelHi);
        R.Ring(kp, 7f, kc);
        return value;
    }

    // ---------------------------------------------------------------
    public string TextField(string id, RectF rect, string value, string placeholder, bool numeric = false, bool password = false)
    {
        _seen.Add(id);
        bool hover = Over(rect);
        bool focused = Focus == id;
        var f = R.Fonts.Get(FontKind.Mono, 14);
        float charW = f.MeasureString(Ircuitry.Render.Renderer.SafeText("M")).X, pad = 9f, innerW = rect.W - pad * 2;

        if (Enabled)
        {
            if (In.LeftPressed)
            {
                if (hover)
                {
                    if (!focused) { Focus = id; _scroll = 0; }
                    focused = true;
                    int idx = CaretFromMouseSingle(rect, value);
                    int clicks = RegisterClick(id);
                    if (clicks >= 3) { _anchor = 0; _caret = value.Length; }       // triple = select all
                    else if (clicks == 2) SelectWordAt(value, idx);                // double = select word
                    else { _caret = idx; if (!In.Shift) _anchor = idx; _mouseSel = true; }
                }
                else if (focused) { Focus = null; focused = false; }
            }
            if (focused && _mouseSel && In.LeftDown) _caret = CaretFromMouseSingle(rect, value); // drag-select
            if (In.LeftReleased) _mouseSel = false;
            if (focused) value = EditKeys(value, numeric, multiline: false);
        }

        // draw
        R.RoundFill(rect, Theme.PanelLo, 7f);
        float baseY = rect.Center.Y - f.MeasureString(Ircuitry.Render.Renderer.SafeText("M")).Y / 2f - 1;
        string display = password ? new string('•', value.Length) : value;

        if (focused)
        {
            EnsureCaretVisible(charW, innerW);
            float scroll = _scroll;
            if (_anchor != _caret)
            {
                float xlo = Math.Max(rect.X + pad, rect.X + pad + (SelLo * charW - scroll));
                float xhi = Math.Min(rect.Right - pad, rect.X + pad + (SelHi * charW - scroll));
                if (xhi > xlo) R.Fill(new RectF(xlo, rect.Y + 5, xhi - xlo, rect.H - 10), Theme.WithAlpha(Theme.Cyan, 0.28f));
            }
            int start = Math.Clamp((int)(scroll / charW), 0, display.Length);
            int count = Math.Clamp((int)(innerW / charW) + 2, 0, display.Length - start);
            R.Text(f, display.Substring(start, count), new Vector2(rect.X + pad - (scroll - start * charW), baseY), Theme.Text);
            if (Blink())
                R.Fill(new RectF(rect.X + pad + (_caret * charW - scroll), rect.Y + 6, 1.5f, rect.H - 12), Theme.CyanBright);
        }
        else
        {
            // not focused: show ellipsized text (or placeholder) - no scroll, never spills
            string shown = display.Length == 0 ? placeholder : display;
            R.Text(f, R.Ellipsize(f, shown, innerW), new Vector2(rect.X + pad, baseY), display.Length == 0 ? Theme.TextFaint : Theme.Text);
        }
        R.RoundOutline(rect, focused ? Theme.Cyan : hover ? Theme.Edge : Theme.Hairline, 7f);
        return value;
    }

    /// <summary>A cycling selector: click right half (or ›) for next, left third (or ‹) for previous.</summary>
    public string Choice(string id, RectF rect, string[] options, string current)
    {
        _seen.Add(id);
        if (options.Length == 0) return current;
        int idx = Math.Max(0, Array.IndexOf(options, current));
        bool hover = Over(rect);

        R.RoundFill(rect, Theme.PanelLo, 7f);
        R.RoundOutline(rect, hover ? Theme.Edge : Theme.Hairline, 7f);
        var f = R.Fonts.Get(FontKind.Mono, 14);
        var ac = hover ? Theme.Cyan : Theme.TextDim;
        R.Text(f, "‹", new Vector2(rect.X + 9, rect.Center.Y - f.MeasureString(Ircuitry.Render.Renderer.SafeText("‹")).Y / 2f), ac);
        R.TextRight(f, "›", rect.Right - 9, rect.Center.Y - f.MeasureString(Ircuitry.Render.Renderer.SafeText("›")).Y / 2f, ac);
        R.TextCenteredX(f, options[idx], rect.Center.X, rect.Center.Y - f.MeasureString(Ircuitry.Render.Renderer.SafeText("M")).Y / 2f, Theme.Text);

        if (hover && In.LeftPressed)
            idx = In.Mouse.X < rect.X + rect.W * 0.33f
                ? (idx - 1 + options.Length) % options.Length
                : (idx + 1) % options.Length;
        return options[idx];
    }

    public int IntField(string id, RectF rect, int value, int min, int max)
    {
        string s = TextField(id, rect, value.ToString(), "0", numeric: true);
        if (!int.TryParse(s, out int v)) v = value;
        return Math.Clamp(v, min, max);
    }

    /// <summary>Multi-line field with word-wrap. Enter inserts a newline; caret is a flat index.</summary>
    public string TextArea(string id, RectF rect, string value, string placeholder)
    {
        _seen.Add(id);
        bool hover = Over(rect);
        bool focused = Focus == id;
        var f = R.Fonts.Get(FontKind.Mono, 14);
        float charW = f.MeasureString(Ircuitry.Render.Renderer.SafeText("M")).X, lineH = f.MeasureString(Ircuitry.Render.Renderer.SafeText("M")).Y + 2f, pad = 9f;
        int maxChars = Math.Max(1, (int)((rect.W - pad * 2) / charW));
        int visibleLines = Math.Max(1, (int)((rect.H - pad * 2 + 2) / lineH));

        if (Enabled)
        {
            if (In.LeftPressed)
            {
                if (hover)
                {
                    if (!focused) { Focus = id; _areaTop = 0; }
                    focused = true;
                    int idx = CaretFromArea(rect, value, maxChars, charW, lineH, pad);
                    int clicks = RegisterClick(id);
                    if (clicks >= 3) { _anchor = LineStart(value, idx); _caret = LineEnd(value, idx); }
                    else if (clicks == 2) SelectWordAt(value, idx);
                    else { _caret = idx; if (!In.Shift) _anchor = idx; _mouseSel = true; }
                }
                else if (focused) { Focus = null; focused = false; }
            }
            if (focused && _mouseSel && In.LeftDown) _caret = CaretFromArea(rect, value, maxChars, charW, lineH, pad);
            if (In.LeftReleased) _mouseSel = false;
            if (focused) value = EditKeys(value, false, multiline: true);
        }

        R.RoundFill(rect, Theme.PanelLo, 7f);
        if (value.Length == 0 && !focused)
            R.Text(f, R.Ellipsize(f, placeholder, rect.W - pad * 2), new Vector2(rect.X + pad, rect.Y + pad), Theme.TextFaint);

        var vls = WrapVisual(value, maxChars);
        int cvl = CaretVisual(vls, _caret, out int ccol);
        if (focused)
        {
            if (cvl < _areaTop) _areaTop = cvl;
            if (cvl >= _areaTop + visibleLines) _areaTop = cvl - visibleLines + 1;
        }
        _areaTop = Math.Clamp(_areaTop, 0, Math.Max(0, vls.Count - visibleLines));

        float y = rect.Y + pad;
        for (int k = _areaTop; k < vls.Count && k < _areaTop + visibleLines; k++)
        {
            var (s, text) = vls[k];
            int e = s + text.Length;
            if (focused && _anchor != _caret && SelHi >= s && SelLo <= e)
            {
                int vlo = Math.Clamp(SelLo, s, e) - s;
                int vhi = Math.Clamp(SelHi, s, e) - s;
                float w = (vhi - vlo) * charW + (SelHi > e ? charW * 0.4f : 0f);
                if (w > 0) R.Fill(new RectF(rect.X + pad + vlo * charW, y, w, lineH), Theme.WithAlpha(Theme.Cyan, 0.28f));
            }
            R.Text(f, text, new Vector2(rect.X + pad, y), Theme.Text);
            if (focused && k == cvl && Blink())
                R.Fill(new RectF(rect.X + pad + ccol * charW, y + 1, 1.5f, lineH - 2), Theme.CyanBright);
            y += lineH;
        }
        R.RoundOutline(rect, focused ? Theme.Cyan : hover ? Theme.Edge : Theme.Hairline, 7f);
        return value;
    }

    // Wrap text to maxChars per visual line (breaking at spaces when possible).
    // Each entry is (flatStartIndex, lineText).
    private static System.Collections.Generic.List<(int start, string text)> WrapVisual(string value, int maxChars)
    {
        var res = new System.Collections.Generic.List<(int, string)>();
        if (maxChars < 1) maxChars = 1;
        int flat = 0;
        foreach (var seg in value.Split('\n'))
        {
            if (seg.Length == 0) res.Add((flat, ""));
            else
            {
                int pos = 0;
                while (pos < seg.Length)
                {
                    int take = Math.Min(maxChars, seg.Length - pos);
                    if (pos + take < seg.Length)
                    {
                        int sp = seg.LastIndexOf(' ', pos + take - 1, take);
                        if (sp >= pos) take = sp - pos + 1; // break after the space
                    }
                    res.Add((flat + pos, seg.Substring(pos, take)));
                    pos += take;
                }
            }
            flat += seg.Length + 1; // account for the '\n' separator
        }
        if (res.Count == 0) res.Add((0, ""));
        return res;
    }

    private static int CaretVisual(System.Collections.Generic.List<(int start, string text)> vls, int caret, out int col)
    {
        int vi = 0;
        for (int k = 0; k < vls.Count; k++) { if (caret >= vls[k].start) vi = k; else break; }
        col = Math.Clamp(caret - vls[vi].start, 0, vls[vi].text.Length);
        return vi;
    }

    private int CaretFromArea(RectF rect, string value, int maxChars, float charW, float lineH, float pad)
    {
        var vls = WrapVisual(value, maxChars);
        int line = Math.Clamp(_areaTop + (int)((In.Mouse.Y - (rect.Y + pad)) / lineH), 0, vls.Count - 1);
        int col = Math.Clamp((int)Math.Round((In.Mouse.X - (rect.X + pad)) / charW), 0, vls[line].text.Length);
        return vls[line].start + col;
    }

    // ---- selection-aware editing core ----
    private int SelLo => Math.Min(_anchor, _caret);
    private int SelHi => Math.Max(_anchor, _caret);

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
    private static int WordLeft(string s, int i) { int j = i; while (j > 0 && char.IsWhiteSpace(s[j - 1])) j--; while (j > 0 && !char.IsWhiteSpace(s[j - 1])) j--; return j; }
    private static int WordRight(string s, int i) { int j = i, n = s.Length; while (j < n && char.IsWhiteSpace(s[j])) j++; while (j < n && !char.IsWhiteSpace(s[j])) j++; return j; }
    private static int LineStart(string s, int i) { int j = Math.Clamp(i, 0, s.Length); while (j > 0 && s[j - 1] != '\n') j--; return j; }
    private static int LineEnd(string s, int i) { int j = Math.Clamp(i, 0, s.Length); while (j < s.Length && s[j] != '\n') j++; return j; }

    private void SelectWordAt(string s, int idx)
    {
        if (s.Length == 0) { _anchor = _caret = 0; return; }
        int probe = Math.Clamp(idx, 0, s.Length - 1);
        if (idx >= s.Length) probe = s.Length - 1;
        if (IsWord(s[probe]))
        {
            int lo = probe, hi = probe + 1;
            while (lo > 0 && IsWord(s[lo - 1])) lo--;
            while (hi < s.Length && IsWord(s[hi])) hi++;
            _anchor = lo; _caret = hi;
        }
        else { _anchor = probe; _caret = probe + 1; }
    }

    private int RegisterClick(string id)
    {
        bool same = _lastClickId == id && Clock.Time - _lastClick < 0.45 && Vector2.Distance(_lastClickPos, In.Mouse) < 6f;
        _clicks = same ? _clicks + 1 : 1;
        _lastClick = Clock.Time; _lastClickPos = In.Mouse; _lastClickId = id;
        return _clicks;
    }

    private string ReplaceSel(string value, string ins)
    {
        int lo = SelLo, hi = SelHi;
        value = value.Substring(0, lo) + ins + value.Substring(hi);
        _caret = _anchor = lo + ins.Length;
        return value;
    }

    private int CaretFromMouseSingle(RectF rect, string value)
    {
        float charW = R.Fonts.Get(FontKind.Mono, 14).MeasureString(Ircuitry.Render.Renderer.SafeText("M")).X;
        return Math.Clamp((int)Math.Round((In.Mouse.X - (rect.X + 9) + _scroll) / charW), 0, value.Length);
    }


    private string EditKeys(string value, bool numeric, bool multiline)
    {
        if (In.Typed.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in In.Typed)
            {
                if (numeric && !(char.IsDigit(c) || (c == '-' && SelLo == 0 && !value.Contains('-')))) continue;
                sb.Append(c);
            }
            if (sb.Length > 0) value = ReplaceSel(value, sb.ToString());
        }

        if (In.EnterPressed) { if (multiline) value = ReplaceSel(value, "\n"); else Focus = null; }
        if (multiline && In.TabPressed) Focus = null;

        if (In.BackspacePressed)
        {
            if (_anchor != _caret) value = ReplaceSel(value, "");
            else if (_caret > 0) { int start = In.Ctrl ? WordLeft(value, _caret) : _caret - 1; value = value.Remove(start, _caret - start); _caret = _anchor = start; }
        }
        if (In.DeletePressed)
        {
            if (_anchor != _caret) value = ReplaceSel(value, "");
            else if (_caret < value.Length) { int end = In.Ctrl ? WordRight(value, _caret) : _caret + 1; value = value.Remove(_caret, end - _caret); }
        }

        if (In.Ctrl && In.KeyPressed(Keys.A)) { _anchor = 0; _caret = value.Length; }

        // OS clipboard: copy / cut / paste within the focused field
        if (In.Ctrl && In.KeyPressed(Keys.C) && _anchor != _caret)
            Clipboard.SetText(value.Substring(SelLo, SelHi - SelLo));
        if (In.Ctrl && In.KeyPressed(Keys.X) && _anchor != _caret)
        {
            Clipboard.SetText(value.Substring(SelLo, SelHi - SelLo));
            value = ReplaceSel(value, "");
        }
        if (In.Ctrl && In.KeyPressed(Keys.V))
        {
            string paste = Clipboard.GetText();
            if (!multiline) paste = paste.Replace("\r", " ").Replace("\n", " ");
            if (numeric)
            {
                var nb = new System.Text.StringBuilder();
                foreach (char c in paste) if (char.IsDigit(c) || (c == '-' && nb.Length == 0)) nb.Append(c);
                paste = nb.ToString();
            }
            if (paste.Length > 0) value = ReplaceSel(value, paste);
        }

        if (In.LeftArrow)
        {
            if (_anchor != _caret && !In.Shift) _caret = SelLo;
            else _caret = In.Ctrl ? WordLeft(value, _caret) : Math.Max(0, _caret - 1);
            if (!In.Shift) _anchor = _caret;
        }
        if (In.RightArrow)
        {
            if (_anchor != _caret && !In.Shift) _caret = SelHi;
            else _caret = In.Ctrl ? WordRight(value, _caret) : Math.Min(value.Length, _caret + 1);
            if (!In.Shift) _anchor = _caret;
        }
        if (In.HomePressed) { _caret = multiline ? LineStart(value, _caret) : 0; if (!In.Shift) _anchor = _caret; }
        if (In.EndPressed) { _caret = multiline ? LineEnd(value, _caret) : value.Length; if (!In.Shift) _anchor = _caret; }
        if (multiline && (In.KeyPressed(Keys.Up) || In.KeyPressed(Keys.Down)))
        { MoveVertical(value, In.KeyPressed(Keys.Up) ? -1 : 1); if (!In.Shift) _anchor = _caret; }

        _caret = Math.Clamp(_caret, 0, value.Length);
        _anchor = Math.Clamp(_anchor, 0, value.Length);
        return value;
    }

    private void MoveVertical(string value, int dir)
    {
        CaretLineCol(value, _caret, out int line, out int col);
        var lines = value.Split('\n');
        int target = line + dir;
        if (target < 0) { _caret = 0; return; }
        if (target >= lines.Length) { _caret = value.Length; return; }
        int idx = 0;
        for (int i = 0; i < target; i++) idx += lines[i].Length + 1;
        _caret = idx + Math.Min(col, lines[target].Length);
    }

    private bool Blink() => ((int)(Clock.Time * 2)) % 2 == 0 || _anchor != _caret; // solid caret while selecting

    private static void CaretLineCol(string value, int caret, out int line, out int col)
    {
        line = 0; col = 0;
        for (int i = 0; i < caret && i < value.Length; i++)
        { if (value[i] == '\n') { line++; col = 0; } else col++; }
    }

    private void EnsureCaretVisible(float charW, float innerW)
    {
        float caretX = _caret * charW;
        if (caretX - _scroll > innerW) _scroll = caretX - innerW;
        if (caretX - _scroll < 0) _scroll = caretX;
        if (_scroll < 0) _scroll = 0;
    }
}
