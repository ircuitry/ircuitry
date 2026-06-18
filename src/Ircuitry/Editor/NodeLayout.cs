using System;
using Microsoft.Xna.Framework;
using Ircuitry.Core;
using Ircuitry.Graph;

namespace Ircuitry.Editor;

/// <summary>Geometry of a node card and its pins, in world units.</summary>
public readonly struct NodeLayout
{
    public const float Width = 196f;
    public const float Header = 34f;
    public const float RowH = 24f;
    public const float SummaryH = 30f;
    public const float BottomPad = 12f;
    public const float PortR = 5.5f;

    public readonly RectF Card;
    public readonly int Rows;
    public readonly bool HasSummary;
    private readonly Node? _n;   // pins are positioned kind-aware: Tool pins sit on the top/bottom edge

    public NodeLayout(RectF card, int rows, bool hasSummary, Node? n = null) { Card = card; Rows = rows; HasSummary = hasSummary; _n = n; }

    public static NodeLayout For(Node n)
    {
        // Tool pins live on the top/bottom edges, not the side rows, so they don't count toward the row height
        int rows = Math.Max(1, Math.Max(CountSide(n.Inputs), CountSide(n.Outputs)));
        bool summary = n.Def.SummaryParam != null;
        float h = Header + rows * RowH + (summary ? SummaryH : 0) + BottomPad;
        return new NodeLayout(new RectF(n.Pos.X, n.Pos.Y, Width, h), rows, summary, n);
    }

    private static int CountSide(PinDef[] pins) { int c = 0; foreach (var p in pins) if (p.Kind != PinKind.Tool) c++; return c; }
    private static int SideRow(PinDef[] pins, int idx) { int r = 0; for (int k = 0; k < idx && k < pins.Length; k++) if (pins[k].Kind != PinKind.Tool) r++; return r; }
    // distribute the Tool pins evenly along the given edge (y = Card.Top for outputs, Card.Bottom for inputs)
    private Vector2 ToolEdge(PinDef[] pins, int idx, float edgeY)
    {
        int count = 0, k = 0;
        for (int t = 0; t < pins.Length; t++) if (pins[t].Kind == PinKind.Tool) { if (t == idx) k = count; count++; }
        return new(Card.Left + Width * (k + 1) / (float)(count + 1), edgeY);
    }

    public float RowCenterY(int index) => Card.Top + Header + RowH * (index + 0.5f);
    public Vector2 InPin(int i) => _n != null && i < _n.Inputs.Length && _n.Inputs[i].Kind == PinKind.Tool
        ? ToolEdge(_n.Inputs, i, Card.Bottom)                                   // tool slot plugged from underneath
        : new(Card.Left, RowCenterY(_n != null ? SideRow(_n.Inputs, i) : i));
    public Vector2 OutPin(int j) => _n != null && j < _n.Outputs.Length && _n.Outputs[j].Kind == PinKind.Tool
        ? ToolEdge(_n.Outputs, j, Card.Top)                                     // a tool's output plugs in from above
        : new(Card.Right, RowCenterY(_n != null ? SideRow(_n.Outputs, j) : j));
    public bool IsToolIn(int i) => _n != null && i < _n.Inputs.Length && _n.Inputs[i].Kind == PinKind.Tool;
    public bool IsToolOut(int j) => _n != null && j < _n.Outputs.Length && _n.Outputs[j].Kind == PinKind.Tool;
    public float BodyTop => Card.Top + Header;
    public float SummaryTop => Card.Top + Header + Rows * RowH;
}
