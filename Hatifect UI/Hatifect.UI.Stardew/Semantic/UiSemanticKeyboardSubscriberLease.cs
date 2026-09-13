using System;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;

namespace Hatifect.UI.Stardew.Semantic;

/// <summary>Exact-restore lease over the dispatcher captured at acquisition, even during foreign-screen cleanup.</summary>
internal sealed class UiSemanticKeyboardSubscriberLease : IKeyboardSubscriber, IDisposable
{
    private readonly Action<string> _textInput;
    private readonly Action<Keys> _specialInput;
    private readonly Func<(Func<IKeyboardSubscriber?> Read, Action<IKeyboardSubscriber?> Write)> _capture;
    private readonly Func<bool> _isCurrentScreen;
    private Func<IKeyboardSubscriber?>? _read;
    private Action<IKeyboardSubscriber?>? _write;
    private IKeyboardSubscriber? _previous;
    private bool _owns;

    public UiSemanticKeyboardSubscriberLease(Action<string> textInput, Action<Keys> specialInput)
        : this(textInput, specialInput, CaptureDispatcher, ScreenGuard(Context.ScreenId)) { }

    internal UiSemanticKeyboardSubscriberLease(
        Action<string> textInput, Action<Keys> specialInput,
        Func<(Func<IKeyboardSubscriber?> Read, Action<IKeyboardSubscriber?> Write)> capture,
        Func<bool> isCurrentScreen)
    {
        _textInput = textInput ?? throw new ArgumentNullException(nameof(textInput));
        _specialInput = specialInput ?? throw new ArgumentNullException(nameof(specialInput));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _isCurrentScreen = isCurrentScreen ?? throw new ArgumentNullException(nameof(isCurrentScreen));
    }

    public bool Selected { get; set; }
    public bool OwnsSubscriber => _owns && ReferenceEquals(_read!(), this);

    public void Acquire()
    {
        if (!_isCurrentScreen())
            throw new InvalidOperationException("Keyboard input can only be acquired on its owning screen.");
        if (_owns && ReferenceEquals(_read!(), this)) return;
        Release();
        (_read, _write) = _capture();
        _previous = _read();
        // Preserve the captured slot if assignment throws after changing its subscriber.
        _owns = true;
        _write(this);
    }

    public void Release(bool restorePrevious = true)
    {
        if (!_owns) return;
        // A lost menu can never regain focus, including a later retry of a failed release.
        if (!restorePrevious) _previous = null;
        if (ReferenceEquals(_read!(), this)) _write!(_previous);
        _previous = null;
        _read = null;
        _write = null;
        _owns = false;
    }

    public void Dispose() => Release();

    public void RecieveTextInput(char inputChar)
    {
        if (CanReceive) _textInput(inputChar.ToString());
    }

    public void RecieveTextInput(string text)
    {
        if (CanReceive) _textInput(text);
    }

    public void RecieveCommandInput(char command)
    {
        if (!CanReceive) return;
        Keys? key = command switch
        {
            '\b' => Keys.Back,
            '\t' => Keys.Tab,
            '\r' => Keys.Enter,
            _ => null
        };
        if (key is { } normalized) _specialInput(normalized);
    }

    public void RecieveSpecialInput(Keys key)
    {
        // Stardew delivers these through RecieveCommandInput. Its legacy key path can
        // additionally send this callback; forwarding both would delete/navigate twice.
        if (CanReceive && key is not (Keys.Back or Keys.Tab or Keys.Enter)) _specialInput(key);
    }

    private bool CanReceive => Selected && _isCurrentScreen() && OwnsSubscriber;
    private static Func<bool> ScreenGuard(int screen) => () => Context.ScreenId == screen;
    private static (Func<IKeyboardSubscriber?>, Action<IKeyboardSubscriber?>) CaptureDispatcher()
    {
        var dispatcher = Game1.keyboardDispatcher;
        return (() => dispatcher.Subscriber, value => dispatcher.Subscriber = value);
    }
}
