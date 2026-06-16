using System;
using System.IO;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Ircuitry.Render;

/// <summary>
/// The custom (Future Cursor) mouse pointers, loaded from the PNG frames extracted out of the .cur theme in
/// assets/cursors/png. Each falls back to the matching system cursor if its file is missing. Pick the right
/// one per context with <see cref="Mouse.SetCursor"/>.
/// </summary>
public sealed class Cursors
{
    public readonly MouseCursor Pointer;    // normal arrow (outside the editor)
    public readonly MouseCursor Hand;       // hovering the editor canvas
    public readonly MouseCursor Grab;       // moving nodes / panning
    public readonly MouseCursor Crosshair;  // Shift held over the editor (box-select)
    public readonly MouseCursor ResizeV;    // over the event-console resize handle

    public Cursors(GraphicsDevice gd)
    {
        Pointer = Load(gd, "cur-pointer.png", 6, 4) ?? MouseCursor.Arrow;
        Hand = Load(gd, "cur-hand.png", 12, 5) ?? MouseCursor.Hand;
        Grab = Load(gd, "cur-grab.png", 16, 16) ?? MouseCursor.SizeAll;
        Crosshair = Load(gd, "cur-crosshair.png", 16, 16) ?? MouseCursor.Crosshair;
        ResizeV = Load(gd, "cur-resizev.png", 15, 8) ?? MouseCursor.SizeNS;
    }

    private static MouseCursor? Load(GraphicsDevice gd, string file, int hotX, int hotY)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "assets", "cursors", "png", file);
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            var tex = Texture2D.FromStream(gd, fs);   // straight alpha, which SDL's cursor wants
            return MouseCursor.FromTexture2D(tex, hotX, hotY);
        }
        catch { return null; }
    }
}
