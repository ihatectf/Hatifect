using System;
using Microsoft.Xna.Framework.Input;
using StardewValley;

namespace Hatifect.UI.Stardew.Semantic;

/// <summary>Shared exact-restore lease over Stardew's borrowed keyboard subscriber slot.</summary>
internal sealed class UiSemanticKeyboardSubscriberLease : IKeyboardSubscriber, IDisposable
{
    private readonly Action<string> _textInput;
    private readonly Action<Keys> _specialInput;
    private IKeyboardSubscriber? _previous;
    private bool _owns;

    public UiSemanticKeyboardSubscriberLease(Action<string> textInput, Action<Keys> specialInput)
    {
        _textInput = textInput ?? throw new ArgumentNullException(nameof(textInput));
        _specialInput = specialInput ?? throw new ArgumentNullException(nameof(specialInput));
    }

    public bool Selected { get; set; }
    public bool OwnsSubscriber => _owns;

    public void Acquire()
    {
        IKeyboardSubscriber? current = Game1.keyboardDispatcher.Subscriber;
        if (_owns && ReferenceEquals(current, this)) return;
        if (_owns)
        {
            _owns = false;
            _previous = null;
        }
        _previous = current;
        Game1.keyboardDispatcher.Subscriber = this;
        _owns = true;
    }

    public void Release()
    {
        if (!_owns) return;
        if (ReferenceEquals(Game1.keyboardDispatcher.Subscriber, this))
            Game1.keyboardDispatcher.Subscriber = _previous;
        _previous = null;
        _owns = false;
    }

    public void Dispose() => Release();

    public void RecieveTextInput(char inputChar)
    {
        if (Selected) _textInput(inputChar.ToString());
    }

    public void RecieveTextInput(string text)
    {
        if (Selected) _textInput(text);
    }

    public void RecieveCommandInput(char command) { }

    public void RecieveSpecialInput(Keys key)
    {
        if (Selected) _specialInput(key);
    }
}
