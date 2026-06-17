using Microsoft.Xna.Framework;
using Ircuitry.Editor;
using Ircuitry.Graph;

namespace Ircuitry.Screens;

/// <summary>
/// "Follow the action": an optional director camera that gently drifts the editor view to whichever
/// node fired most recently while a bot runs, turning a live graph into a little watchable movie.
/// Uses the runtime's per-node fire glow (which fades over ~1s) as a recency signal, so it eases to
/// each new fire and rests when nothing is happening. Pans only - it never fights the user's zoom,
/// and it yields the moment you grab the canvas.
/// </summary>
public partial class MainScreen
{
    private bool _followCam;

    private void UpdateFollowCam()
    {
        if (!Bot.Runtime.Running || _editor.IsGrabbing) return;

        Node? hot = null;
        float best = 0.12f;     // ignore the faint tail so the camera settles instead of chasing embers
        foreach (var n in Bot.Graph.Nodes)
        {
            float g = Bot.Runtime.FireGlow(n.Id);
            if (g > best) { best = g; hot = n; }
        }
        if (hot == null) return;

        var center = NodeLayout.For(hot).Card.Center;
        var targetPan = _l.Canvas.Center - center * _editor.Cam.Zoom;
        _editor.Cam.Pan = Vector2.Lerp(_editor.Cam.Pan, targetPan, 0.10f);   // gentle cinematic drift
    }
}
