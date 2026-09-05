using System;

namespace Hatifect.UI.Experience;

public interface IUiSemanticSource
{
    Type ValueType { get; }
    object? UntypedValue { get; }
    event Action? Changed;
}

public interface IUiSemanticSource<out T> : IUiSemanticSource
{
    T Value { get; }
}

public interface IUiMutableSemanticSource<T> : IUiSemanticSource<T>
{
    new T Value { get; set; }
}

public sealed class UiConstantSource<T> : IUiSemanticSource<T>
{
    public UiConstantSource(T value) => Value = value;

    public T Value { get; }
    public Type ValueType => typeof(T);
    public object? UntypedValue => Value;
    public event Action? Changed
    {
        add { }
        remove { }
    }
}

public sealed class UiState<T> : IUiMutableSemanticSource<T>
{
    private T _value;

    public UiState(T value) => _value = value;

    public T Value
    {
        get => _value;
        set
        {
            if (Equals(_value, value)) return;
            _value = value;
            Changed?.Invoke();
        }
    }

    public Type ValueType => typeof(T);
    public object? UntypedValue => Value;
    public event Action? Changed;
}
