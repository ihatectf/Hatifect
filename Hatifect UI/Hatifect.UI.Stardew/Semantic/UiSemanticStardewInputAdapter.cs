using System;
using Microsoft.Xna.Framework.Input;
using Hatifect.UI.Runtime.Platform;
using RuntimeNavigationDirection = Hatifect.UI.Runtime.Input.UiNavigationDirection;
using RuntimeTextEditAction = Hatifect.UI.Runtime.Input.UiTextEditAction;
using RuntimePoint = Hatifect.UI.Runtime.Layout.UiPoint;

namespace Hatifect.UI.Stardew.Semantic;

/// <summary>XNA input normalization only; Runtime owns hit testing, focus, modality, and scrolling.</summary>
internal sealed class UiSemanticStardewInputAdapter
{
    private readonly IUiPlatformInputSession _host;

    public UiSemanticStardewInputAdapter(IUiPlatformInputSession host)
        => _host = host ?? throw new ArgumentNullException(nameof(host));

    public UiPortalDispatch PointerMove(int x, int y)
        => _host.MovePointer(new RuntimePoint(x, y));

    public UiPortalDispatch PointerDown(int x, int y)
        => _host.PressPointer(new RuntimePoint(x, y));

    public UiPortalDispatch PointerUp(int x, int y)
        => _host.ReleasePointer(new RuntimePoint(x, y));

    public UiPortalDispatch UnhandledInput()
        => _host.UnhandledInput();

    public UiPortalScrollDispatch Wheel(int x, int y, int delta)
        => _host.ScrollAt(new RuntimePoint(x, y), -delta);

    public UiPortalDispatch ReplaceText(string? text)
        => _host.ReplaceText(text);

    public UiPortalDispatch TextInput(string? text)
        => _host.InsertText(text);

    public bool IsTextEditing => _host.FocusedTextEditing != null;

    public UiPortalDispatch KeyDown(Keys key)
    {
        KeyboardState state = Keyboard.GetState();
        bool shift = state.IsKeyDown(Keys.LeftShift) || state.IsKeyDown(Keys.RightShift);
        bool control = state.IsKeyDown(Keys.LeftControl) || state.IsKeyDown(Keys.RightControl);
        return KeyDown(key, shift, control);
    }

    public UiPortalDispatch KeyDown(Keys key, bool shift, bool control)
    {
        if (_host.FocusedTextEditing != null)
        {
            RuntimeTextEditAction? edit = key switch
            {
                Keys.Left => RuntimeTextEditAction.Left,
                Keys.Right => RuntimeTextEditAction.Right,
                Keys.Home => RuntimeTextEditAction.Home,
                Keys.End => RuntimeTextEditAction.End,
                Keys.Back => RuntimeTextEditAction.Backspace,
                Keys.Delete => RuntimeTextEditAction.Delete,
                Keys.A when control => RuntimeTextEditAction.SelectAll,
                _ => null
            };
            if (edit is { } action) return _host.EditText(action, shift);
            if (key == Keys.Space) return _host.UnhandledInput();
        }
        return key switch
        {
            Keys.Enter or Keys.Space => _host.Submit(),
            Keys.Escape => _host.Cancel(),
            Keys.Tab => _host.MoveFocus(
                shift ? RuntimeNavigationDirection.Previous : RuntimeNavigationDirection.Next),
            Keys.Left => _host.MoveFocus(RuntimeNavigationDirection.Left),
            Keys.Right => _host.MoveFocus(RuntimeNavigationDirection.Right),
            Keys.Up => _host.MoveFocus(RuntimeNavigationDirection.Up),
            Keys.Down => _host.MoveFocus(RuntimeNavigationDirection.Down),
            _ => _host.UnhandledInput()
        };
    }

    public UiPortalDispatch GamePad(Buttons button)
        => button switch
        {
            Buttons.A => _host.Submit(),
            Buttons.B => _host.Cancel(),
            Buttons.DPadLeft or Buttons.LeftThumbstickLeft => _host.MoveFocus(RuntimeNavigationDirection.Left),
            Buttons.DPadRight or Buttons.LeftThumbstickRight => _host.MoveFocus(RuntimeNavigationDirection.Right),
            Buttons.DPadUp or Buttons.LeftThumbstickUp => _host.MoveFocus(RuntimeNavigationDirection.Up),
            Buttons.DPadDown or Buttons.LeftThumbstickDown => _host.MoveFocus(RuntimeNavigationDirection.Down),
            Buttons.LeftShoulder => _host.MoveFocus(RuntimeNavigationDirection.Previous),
            Buttons.RightShoulder => _host.MoveFocus(RuntimeNavigationDirection.Next),
            _ => _host.UnhandledInput()
        };
}
