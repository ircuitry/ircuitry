using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Ircuitry.Input;

/// <summary>
/// Per-frame snapshot of mouse + keyboard with edge detection. Typed characters
/// arrive out-of-band via the SDL TextInput event (pushed by the game loop) so
/// we get correct layout/repeat behaviour for text fields. When <see cref="Active"/>
/// is false (window not focused) all inputs read as inert, so nothing fires while
/// the app is in the background or on the frame focus returns.
/// </summary>
public sealed class InputState
{
    private MouseState _mouse, _mousePrev;
    private KeyboardState _kb, _kbPrev;
    private int _scrollPrev, _scrollDelta;

    public bool Active = true;

    public readonly List<char> Typed = new();
    public bool BackspacePressed;   // convenience edges for text editing
    public bool EnterPressed;
    public bool TabPressed;
    public bool LeftArrow, RightArrow, HomePressed, EndPressed, DeletePressed;

    public Vector2 Mouse { get; private set; }
    public Vector2 MousePrev { get; private set; }
    public Vector2 MouseDelta => Mouse - MousePrev;
    public int ScrollDelta => Active ? _scrollDelta : 0;

    public bool LeftDown => Active && _mouse.LeftButton == ButtonState.Pressed;
    public bool RightDown => Active && _mouse.RightButton == ButtonState.Pressed;
    public bool MiddleDown => Active && _mouse.MiddleButton == ButtonState.Pressed;

    public bool LeftPressed => Active && _mouse.LeftButton == ButtonState.Pressed && _mousePrev.LeftButton == ButtonState.Released;
    public bool LeftReleased => Active && _mouse.LeftButton == ButtonState.Released && _mousePrev.LeftButton == ButtonState.Pressed;
    public bool RightPressed => Active && _mouse.RightButton == ButtonState.Pressed && _mousePrev.RightButton == ButtonState.Released;
    public bool MiddlePressed => Active && _mouse.MiddleButton == ButtonState.Pressed && _mousePrev.MiddleButton == ButtonState.Released;

    public bool Ctrl => Active && (_kb.IsKeyDown(Keys.LeftControl) || _kb.IsKeyDown(Keys.RightControl));
    public bool Shift => Active && (_kb.IsKeyDown(Keys.LeftShift) || _kb.IsKeyDown(Keys.RightShift));
    public bool Alt => Active && (_kb.IsKeyDown(Keys.LeftAlt) || _kb.IsKeyDown(Keys.RightAlt));

    public bool KeyDown(Keys k) => Active && _kb.IsKeyDown(k);
    public bool KeyPressed(Keys k) => Active && _kb.IsKeyDown(k) && _kbPrev.IsKeyUp(k);

    public void PushChar(char c) { if (Active) Typed.Add(c); }

    public void Update()
    {
        _mousePrev = _mouse;
        _kbPrev = _kb;
        _mouse = Microsoft.Xna.Framework.Input.Mouse.GetState();
        _kb = Keyboard.GetState();

        MousePrev = Mouse;
        Mouse = new Vector2(_mouse.X, _mouse.Y);
        _scrollDelta = _mouse.ScrollWheelValue - _scrollPrev;
        _scrollPrev = _mouse.ScrollWheelValue;

        BackspacePressed = KeyPressed(Keys.Back);
        EnterPressed = KeyPressed(Keys.Enter);
        TabPressed = KeyPressed(Keys.Tab);
        LeftArrow = KeyPressed(Keys.Left);
        RightArrow = KeyPressed(Keys.Right);
        HomePressed = KeyPressed(Keys.Home);
        EndPressed = KeyPressed(Keys.End);
        DeletePressed = KeyPressed(Keys.Delete);
    }

    /// <summary>Call at the very end of a frame, after UI consumed the events.</summary>
    public void EndFrame() => Typed.Clear();
}
