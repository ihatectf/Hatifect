using System.Collections.ObjectModel;

namespace Hatifect.UI.Experience;

/// <summary>Immutable display text with exact, ordinal locale matching and an explicit fallback.</summary>
public sealed class UiLocalizedText
{
    private readonly Dictionary<string, string> _translations;
    public UiLocalizedText(string fallback, IEnumerable<KeyValuePair<string, string>> translations)
    {
        if (string.IsNullOrWhiteSpace(fallback)) throw new ArgumentException("A fallback label is required.", nameof(fallback));
        ArgumentNullException.ThrowIfNull(translations);
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var translation in translations)
        {
            if (string.IsNullOrWhiteSpace(translation.Key) || string.IsNullOrWhiteSpace(translation.Value))
                throw new ArgumentException("Translations require a locale and a nonblank label.", nameof(translations));
            if (!copy.TryAdd(translation.Key, translation.Value))
                throw new ArgumentException($"Duplicate translation locale '{translation.Key}'.", nameof(translations));
        }
        _translations = copy;
        Fallback = fallback;
        Translations = new ReadOnlyDictionary<string, string>(copy);
    }

    public string Fallback { get; }
    public IReadOnlyDictionary<string, string> Translations { get; }

    internal bool HasSameContent(UiLocalizedText other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (Fallback != other.Fallback || _translations.Count != other._translations.Count) return false;
        // Use the owned concrete map so repeated eligibility comparison does not box an enumerator.
        foreach (var pair in _translations)
            if (!other._translations.TryGetValue(pair.Key, out string? value) || pair.Value != value) return false;
        return true;
    }

    /// <summary>No parent-culture inference is performed; an unknown or invariant locale uses Fallback.</summary>
    public string Resolve(string locale)
    {
        ArgumentNullException.ThrowIfNull(locale);
        return _translations.TryGetValue(locale, out string? value) ? value : Fallback;
    }
}

internal interface IUiTextFormatter
{
    string Format(object? capturedValue, string capturedLocale);
}

internal sealed class UiTextFormatter<T> : IUiTextFormatter
{
    private readonly Func<T, string, string> _format;
    internal UiTextFormatter(Func<T, string, string> format) => _format = format;
    public string Format(object? capturedValue, string capturedLocale) => _format((T)capturedValue!, capturedLocale);
}
