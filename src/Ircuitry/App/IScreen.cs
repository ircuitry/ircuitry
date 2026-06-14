using Ircuitry.Core;
using Ircuitry.Input;
using Ircuitry.Render;

namespace Ircuitry.App;

public interface IScreen
{
    void Update(InputState input, Clock clock);
    void Draw(Renderer r, Clock clock);

    /// <summary>True while the user is mid-edit (a text field is focused) - defer autosave until they blur.</summary>
    bool SuppressAutosave { get; }
}
