using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Ircuitry.Core;
using Ircuitry.Graph;
using Ircuitry.Render;

namespace Ircuitry.Screens;

// Right-click context menu over the canvas. Adapts to what's under the cursor: a rich node menu
// when something is selected, or a canvas menu on empty space. Drawn as a lightweight popup with
// its own hit-testing (no Ui widgets), gated as a Modal so the editor underneath stays inert.
public sealed partial class MainScreen
{
    private struct CtxItem
    {
        public string Icon, Label, Shortcut;
        public bool Enabled, Sep;
        public Action? Do;
        public Color? Tint;   // overrides the icon colour (e.g. group-colour swatches)
    }

    private bool _ctxOpen, _ctxJustOpened;
    private Vector2 _ctxAnchor;                 // screen point the menu is anchored to
    private readonly List<CtxItem> _ctxItems = new();

    private void OpenContextMenu(Vector2 screen, bool onNode)
    {
        _ctxAnchor = screen;
        var world = _editor.Cam.ScreenToWorld(screen);

        _ctxItems.Clear();
        void Item(string icon, string label, string sc, bool en, Action a) =>
            _ctxItems.Add(new CtxItem { Icon = icon, Label = label, Shortcut = sc, Enabled = en, Do = a });
        void Sep() => _ctxItems.Add(new CtxItem { Sep = true });

        bool canPaste = _editor.ClipboardHasNodes();
        bool hasNodes = Bot.Graph.Nodes.Count > 0;

        if (onNode && _editor.Selection.Count > 0)
        {
            int n = _editor.Selection.Count;
            string suffix = n > 1 ? $" ({n})" : "";
            bool anyOn = _editor.Selection.Select(id => Bot.Graph.Find(id)).Any(x => x is { Muted: false });

            Item(Ircuitry.Core.Icons.Glyph("scissors"), "Cut" + suffix, "Ctrl+X", true, () => { _editor.CutSelection(); _app.MarkDirty(); });
            Item(Ircuitry.Core.Icons.Glyph("clipboard"), "Copy" + suffix, "Ctrl+C", true, () => _editor.CopySelection());
            Item(Ircuitry.Core.Icons.Glyph("bookmarks"), "Duplicate" + suffix, "Ctrl+D", true, () => { _editor.DuplicateSelection(); _app.MarkDirty(); });
            Item(Ircuitry.Core.Icons.Glyph("tray"), "Paste here", "Ctrl+V", canPaste, () => { _editor.PasteAtCursor(world); _app.MarkDirty(); });
            Item(Ircuitry.Core.Icons.Glyph("cake"), "Bake into a node…", "", _editor.SelectionCanBake, () => { _saveNodeName = "My Node"; _saveNodeIcon = "puzzle-piece"; _saveNodeCat = "Logic"; _saveNodeDesc = ""; _saveNodeAsTool = false; _saveNodeOpen = true; _saveNodeJustOpened = true; _ui.Focus = "savenode.name"; });
            var only = n == 1 ? Bot.Graph.Find(_editor.Selection.First()) : null;
            if (only != null && NodeCatalog.IsCustom(only.TypeId))
                Item(Ircuitry.Core.Icons.Glyph("pencil"), "Edit node…", "", true, () => OpenNodeBuilderForEdit(only.TypeId));
            Sep();
            Item(anyOn ? Ircuitry.Core.Icons.Glyph("speaker-slash") : Ircuitry.Core.Icons.Glyph("speaker-high"), anyOn ? "Mute" + suffix : "Unmute" + suffix, "M", true, () => { _editor.ToggleMuteSelection(); _app.MarkDirty(); });
            Item(Ircuitry.Core.Icons.Glyph("plug"), "Disconnect wires", "", true, () => { _editor.DisconnectSelection(); _app.MarkDirty(); });
            Sep();
            Item(Ircuitry.Core.Icons.Glyph("trash"), "Delete" + suffix, "Del", true, () => { _editor.DeleteSelection(); _app.MarkDirty(); });
        }
        else
        {
            Item("+", "Add node here…", "2×click", true, () => OpenQuickAdd(_ctxAnchor));
            Item(Ircuitry.Core.Icons.Glyph("note"), "Add sticky note", "", true, () => { _editor.AddFrame(world); _app.MarkDirty(); });
            Item(Ircuitry.Core.Icons.Glyph("tray"), "Paste here", "Ctrl+V", canPaste, () => { _editor.PasteAtCursor(world); _app.MarkDirty(); });
            Item(Ircuitry.Core.Icons.Glyph("square"), "Select all", "Ctrl+A", hasNodes, () => _editor.SelectAll());
            Sep();
            Item(Ircuitry.Core.Icons.Glyph("ruler"), "Tidy layout", "Ctrl+L", hasNodes, () => { _editor.AutoLayout(); _editor.FocusContent(_l.Canvas); _app.MarkDirty(); });
            Item(Ircuitry.Core.Icons.Glyph("magnifying-glass"), "Fit to view", "", hasNodes, () => _editor.FocusContent(_l.Canvas));
            Sep();
            Item(Ircuitry.Core.Icons.Glyph("graduation-cap"), "Tutorial", "", true, ForceStartTutorial);
        }

        _ctxOpen = true; _ctxJustOpened = true;
    }

    /// <summary>Debug/screenshot hook: pop the node or canvas menu without a real right-click.</summary>
    public void DebugOpenContextMenu(bool onNode)
    {
        _l = DockLayout();
        _editor.Selection.Clear();
        if (onNode)
        {
            var first = Bot.Graph.Nodes.FirstOrDefault();
            if (first != null) _editor.Selection.Add(first.Id);
        }
        OpenContextMenu(new Vector2(_l.Canvas.Center.X - 100, _l.Canvas.Y + 120), onNode);
    }

    private void DrawContextMenu(Renderer r)
    {
        const float w = 244f, itemH = 30f, sepH = 9f, padTop = 7f, padBot = 7f;
        float h = padTop + padBot;
        foreach (var it in _ctxItems) h += it.Sep ? sepH : itemH;

        // keep the whole menu on-screen
        float x = _ctxAnchor.X, y = _ctxAnchor.Y;
        if (x + w > _vw - 8) x = _vw - 8 - w;
        if (y + h > _vh - 8) y = _vh - 8 - h;
        x = MathF.Max(8, x); y = MathF.Max(8, y);
        var panel = new RectF(x, y, w, h);

        r.RoundFill(panel.Offset(0, 6), Theme.WithAlpha(Color.Black, 0.18f), 12f);
        r.RoundFill(panel, Theme.PanelHi, 12f);
        r.RoundOutline(panel, Theme.Edge, 12f);

        var lf = r.Fonts.Get(FontKind.SansBold, 13);
        var icf = r.Fonts.Get(FontKind.Sans, 13);
        var scf = r.Fonts.Get(FontKind.Mono, 11);

        float cy = panel.Y + padTop;
        int hovered = -1;
        for (int i = 0; i < _ctxItems.Count; i++)
        {
            var it = _ctxItems[i];
            if (it.Sep) { r.HLine(panel.X + 12, panel.Right - 12, cy + sepH / 2f, Theme.Hairline, 1f); cy += sepH; continue; }

            var row = new RectF(panel.X + 4, cy, panel.W - 8, itemH);
            bool hov = it.Enabled && !_ctxJustOpened && row.Contains(In.Mouse);
            if (hov) { r.RoundFill(row, Theme.WithAlpha(Theme.Cyan, 0.16f), 8f); hovered = i; }

            var textCol = it.Enabled ? Theme.Text : Theme.TextFaint;
            var iconCol = it.Tint ?? (it.Enabled ? Theme.Mix(Theme.Text, Theme.Cyan, 0.35f) : Theme.TextFaint);
            r.Text(icf, Ircuitry.Core.Icons.Glyph(it.Icon), new Vector2(row.X + 11, row.Center.Y - icf.MeasureString(Ircuitry.Core.Icons.Glyph(it.Icon)).Y / 2f), iconCol);
            r.Text(lf, it.Label, new Vector2(row.X + 38, row.Center.Y - lf.MeasureString(it.Label).Y / 2f - 1), textCol);
            if (!string.IsNullOrEmpty(it.Shortcut))
                r.TextRight(scf, it.Shortcut, row.Right - 12, row.Center.Y - scf.MeasureString("M").Y / 2f, Theme.TextFaint);
            cy += itemH;
        }

        // interaction: click an item to run it, click/right-click away to dismiss
        if (In.LeftPressed)
        {
            if (hovered >= 0) { var act = _ctxItems[hovered].Do; _ctxOpen = false; act?.Invoke(); }
            else if (!panel.Contains(In.Mouse) && !_ctxJustOpened) _ctxOpen = false;
        }
        else if (In.RightPressed && !_ctxJustOpened) _ctxOpen = false;

        _ctxJustOpened = false;
    }
}
