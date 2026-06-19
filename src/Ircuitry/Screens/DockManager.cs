using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Xna.Framework;
using Ircuitry.Core;

namespace Ircuitry.Screens;

/// <summary>
/// A small tiling/floating dock manager. The editor map is always full-bleed under the title bar; panels
/// (Node Library, Inspector, Console) OVERLAY it - each docked to an edge (left/right/top/bottom, stackable)
/// or free-floating. Panels can be dragged to a new edge (with a drop highlight), resized from their inner
/// border, hidden, and the whole arrangement persists.
/// </summary>
public sealed class DockManager
{
    public enum Edge { Left, Right, Top, Bottom, Float }
    public const float HeaderH = 0f;   // panels reuse their own Hud.Panel title bar as the drag grip

    public sealed class Panel
    {
        public string Id = "";
        public Edge Dock = Edge.Left;
        public float Size = 252;         // width for L/R, height for T/B
        public RectF FloatRect;          // used when Dock==Float
        public bool Visible = true;
        public int Order;                // position among panels sharing an edge
        public RectF Rect;               // computed each layout pass (full panel incl. header)
        public RectF Content => new(Rect.X, Rect.Y + HeaderH, Rect.W, MathF.Max(0, Rect.H - HeaderH));
    }

    private readonly List<Panel> _panels = new();
    public IReadOnlyList<Panel> Panels => _panels;
    public Panel? Get(string id) => _panels.FirstOrDefault(p => p.Id == id);

    private RectF _work;                  // the area panels + map live in (under titlebar, above status)
    public RectF Map { get; private set; }

    // ---- drag / resize interaction state ----
    private Panel? _drag; private Vector2 _dragOff; private Edge _dropHint = Edge.Float;
    private Panel? _resize; private Vector2 _resizeStart; private float _resizeStartSize; private RectF _resizeStartRect;

    public void Add(Panel p) { _panels.Add(p); }

    /// <summary>Place panels for this viewport by sequentially CARVING a shrinking 'remaining' rect: Left then
    /// Right take full-height side strips, then Top then Bottom take bands spanning only the width BETWEEN the
    /// side strips. Map is whatever is left - the true uncovered central region (never overlapping a panel).
    /// Floating panels use their own rect.</summary>
    public void Layout(RectF work)
    {
        _work = work;
        RectF rem = work;
        // the panel being dragged previews at its LIVE drop target, so the arrangement is WYSIWYG: it docks into
        // the real strip near an edge, or floats under the cursor over the canvas - no separate landing ghost.
        Edge Eff(Panel p) => p == _drag ? _dropHint : p.Dock;
        foreach (var edge in new[] { Edge.Left, Edge.Right, Edge.Top, Edge.Bottom })
        {
            var on = _panels.Where(p => p.Visible && Eff(p) == edge).OrderBy(p => p == _drag ? int.MaxValue : p.Order).ToList();
            if (on.Count == 0) continue;
            bool side = edge is Edge.Left or Edge.Right;
            // clamp each panel against the FULL work area (so a panel doesn't shrink as `rem` shrinks under it)
            foreach (var p in on) p.Size = Math.Clamp(p.Size, 120, (side ? work.W : work.H) * 0.8f);
            float band = on.Max(p => p.Size);   // the strip this edge carves off `rem`
            float n = on.Count;
            for (int i = 0; i < on.Count; i++)
            {
                var p = on[i];
                if (side)
                {
                    float h = rem.H / n, y = rem.Y + h * i;             // stacked side panels split the height
                    float x = edge == Edge.Left ? rem.X : rem.Right - p.Size;
                    p.Rect = new RectF(x, y, p.Size, h);
                }
                else
                {
                    float w = rem.W / n, x = rem.X + w * i;             // top/bottom bands span the carved width
                    float y = edge == Edge.Top ? rem.Y : rem.Bottom - p.Size;
                    p.Rect = new RectF(x, y, w, p.Size);
                }
            }
            // shrink the remaining map area so later edges - and Map itself - dodge this strip
            rem = edge switch
            {
                Edge.Left  => new RectF(rem.X + band, rem.Y, MathF.Max(0, rem.W - band), rem.H),
                Edge.Right => new RectF(rem.X, rem.Y, MathF.Max(0, rem.W - band), rem.H),
                Edge.Top   => new RectF(rem.X, rem.Y + band, rem.W, MathF.Max(0, rem.H - band)),
                _          => new RectF(rem.X, rem.Y, rem.W, MathF.Max(0, rem.H - band)),
            };
        }
        Map = rem;
        foreach (var p in _panels.Where(p => p.Visible && Eff(p) == Edge.Float))
        {
            // keep a floating panel on-screen (the dragged one rides under the cursor via FloatRect, set in Tick)
            float w = Math.Clamp(p.FloatRect.W, 160, work.W), h = Math.Clamp(p.FloatRect.H, 120, work.H);
            float x = Math.Clamp(p.FloatRect.X, work.X, work.Right - w), y = Math.Clamp(p.FloatRect.Y, work.Y, work.Bottom - h);
            p.Rect = p.FloatRect = new RectF(x, y, w, h);
        }
    }

    public bool OverPanel(Vector2 m) => _panels.Any(p => p.Visible && p.Rect.Contains(m));

    /// <summary>The bottom-right of the carved Map, so corner buttons sit on the visible map and dodge any
    /// right/bottom docked panel that would cover them.</summary>
    public Vector2 VisibleMapCorner() => new(Map.Right, Map.Bottom);

    /// <summary>The sub-rect of the map not covered by any edge-docked panel (the carved Map) - where on-map HUD
    /// (info card, zoom readout, slow-mo pill, minimap, corner buttons) lives so it dodges the panels.</summary>
    public RectF VisibleMapRect() => Map;

    /// <summary>Begin dragging a panel by its header (called when the header is pressed).</summary>
    public void BeginDrag(Panel p, Vector2 mouse) { _drag = p; _dragOff = new Vector2(mouse.X - p.Rect.X, mouse.Y - p.Rect.Y); }
    public void BeginResize(Panel p, Vector2 mouse) { _resize = p; _resizeStart = mouse; _resizeStartSize = p.Size; _resizeStartRect = p.Rect; }
    public bool Dragging => _drag != null || _resize != null;

    /// <summary>Drive an in-progress drag/resize. Returns the drop-edge highlight (or null) for the caller to draw.</summary>
    public Edge? Tick(Vector2 mouse, bool leftDown)
    {
        if (_resize != null)
        {
            if (!leftDown) { _resize = null; return null; }
            float dx = mouse.X - _resizeStart.X, dy = mouse.Y - _resizeStart.Y;
            if (_resize.Dock == Edge.Left) _resize.Size = _resizeStartSize + dx;
            else if (_resize.Dock == Edge.Right) _resize.Size = _resizeStartSize - dx;
            else if (_resize.Dock == Edge.Top) _resize.Size = _resizeStartSize + dy;
            else if (_resize.Dock == Edge.Bottom) _resize.Size = _resizeStartSize - dy;
            else _resize.FloatRect = new RectF(_resizeStartRect.X, _resizeStartRect.Y, MathF.Max(160, _resizeStartRect.W + dx), MathF.Max(120, _resizeStartRect.H + dy));
            _resize.Size = Math.Clamp(_resize.Size, 120, 2000);
            return null;
        }
        if (_drag == null) return null;
        // float the panel under the cursor while dragging
        float fw = _drag.Dock == Edge.Float ? _drag.FloatRect.W : (_drag.Dock is Edge.Left or Edge.Right ? _drag.Size : Math.Min(420, _work.W * 0.5f));
        float fh = _drag.Dock == Edge.Float ? _drag.FloatRect.H : (_drag.Dock is Edge.Top or Edge.Bottom ? _drag.Size : Math.Min(_work.H * 0.6f, 360));
        _drag.FloatRect = new RectF(mouse.X - _dragOff.X, mouse.Y - _dragOff.Y, fw, fh);
        // which edge would we dock to? (cursor within a margin of an edge)
        const float M = 56f;
        Edge hint = Edge.Float;
        if (mouse.X - _work.X < M) hint = Edge.Left;
        else if (_work.Right - mouse.X < M) hint = Edge.Right;
        else if (mouse.Y - _work.Y < M) hint = Edge.Top;
        else if (_work.Bottom - mouse.Y < M) hint = Edge.Bottom;
        _dropHint = hint;
        if (!leftDown)
        {
            // commit
            if (hint == Edge.Float) { _drag.Dock = Edge.Float; }
            else { _drag.Dock = hint; _drag.Order = (_panels.Where(p => p.Dock == hint).Select(p => p.Order).DefaultIfEmpty(-1).Max()) + 1; }
            var done = _drag; _drag = null;
            return null;
        }
        return hint;
    }

    /// <summary>The screen rect a drop-edge highlight should fill (so the caller can draw the hint).</summary>
    public RectF DropRect(Edge e) => e switch
    {
        Edge.Left => new RectF(_work.X, _work.Y, _work.W * 0.28f, _work.H),
        Edge.Right => new RectF(_work.Right - _work.W * 0.28f, _work.Y, _work.W * 0.28f, _work.H),
        Edge.Top => new RectF(_work.X, _work.Y, _work.W, _work.H * 0.28f),
        Edge.Bottom => new RectF(_work.X, _work.Bottom - _work.H * 0.28f, _work.W, _work.H * 0.28f),
        _ => default,
    };

    public Panel? DraggingPanel => _drag;
    public Panel? ResizingPanel => _resize;
    public Edge CurrentDropHint => _dropHint;

    // ---- persistence: one line per panel "id edge size visible fx fy fw fh" ----
    public string Serialize() => string.Join("\n", _panels.Select(p =>
        string.Join(' ', new[] { p.Id, p.Dock.ToString(), F(p.Size), p.Visible ? "1" : "0", F(p.FloatRect.X), F(p.FloatRect.Y), F(p.FloatRect.W), F(p.FloatRect.H), p.Order.ToString() })));

    public void Deserialize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return;
        foreach (var line in s.Split('\n'))
        {
            var t = line.Split(' ');
            if (t.Length < 4) continue;
            var p = Get(t[0]); if (p == null) continue;
            if (Enum.TryParse<Edge>(t[1], out var e)) p.Dock = e;
            if (float.TryParse(t[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var sz)) p.Size = sz;
            p.Visible = t[3] == "1";
            if (t.Length >= 8 && float.TryParse(t[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var fx)
                && float.TryParse(t[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var fy)
                && float.TryParse(t[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var fw)
                && float.TryParse(t[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var fh))
                p.FloatRect = new RectF(fx, fy, fw, fh);
            if (t.Length >= 9 && int.TryParse(t[8], out var ord)) p.Order = ord;
        }
    }

    private static string F(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);
}
