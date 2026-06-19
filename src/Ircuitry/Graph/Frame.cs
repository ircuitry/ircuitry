using Microsoft.Xna.Framework;
using Ircuitry.Core;

namespace Ircuitry.Graph;

/// <summary>A non-executing annotation on the canvas (an n8n-style sticky note / region frame): a resizable,
/// colour-tagged, titled rectangle drawn BEHIND the nodes. Dragging it by the title bar carries the nodes that
/// sit on top of it, so you can label and move a subsystem as a unit. Pure documentation - never runs.</summary>
public sealed class Frame
{
    public string Id;
    public Vector2 Pos;                       // world top-left
    public Vector2 Size = new(300, 190);
    public string Title = "Note";             // shown in the title bar
    public string Body = "";                  // free text on the note
    public int ColorIndex;                    // Theme.Tag palette index
    public bool Collapsed;                    // show just the title bar

    public const float TitleH = 30f;
    public const float MinW = 140f, MinH = 64f;

    public Frame(string id) { Id = id; }

    public RectF Rect => new(Pos.X, Pos.Y, Size.X, Collapsed ? TitleH : Size.Y);
    public RectF FullRect => new(Pos.X, Pos.Y, Size.X, Size.Y);
    public RectF TitleBar => new(Pos.X, Pos.Y, Size.X, TitleH);

    private static int _ctr;
    public static Frame Create(Vector2 pos) => new($"f{++_ctr:x}{System.Environment.TickCount & 0xffff:x}") { Pos = pos };

    public Frame Clone(string newId) => new(newId)
    { Pos = Pos, Size = Size, Title = Title, Body = Body, ColorIndex = ColorIndex, Collapsed = Collapsed };
}
