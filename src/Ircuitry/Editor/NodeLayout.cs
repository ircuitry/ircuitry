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

    public NodeLayout(RectF card, int rows, bool hasSummary) { Card = card; Rows = rows; HasSummary = hasSummary; }

    public static NodeLayout For(Node n)
    {
        int rows = Math.Max(1, Math.Max(n.Def.Inputs.Length, n.Def.Outputs.Length));
        bool summary = n.Def.SummaryParam != null;
        float h = Header + rows * RowH + (summary ? SummaryH : 0) + BottomPad;
        return new NodeLayout(new RectF(n.Pos.X, n.Pos.Y, Width, h), rows, summary);
    }

    public float RowCenterY(int index) => Card.Top + Header + RowH * (index + 0.5f);
    public Vector2 InPin(int i) => new(Card.Left, RowCenterY(i));
    public Vector2 OutPin(int j) => new(Card.Right, RowCenterY(j));
    public float BodyTop => Card.Top + Header;
    public float SummaryTop => Card.Top + Header + Rows * RowH;
}
