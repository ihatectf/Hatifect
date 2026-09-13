namespace Hatifect.UI.Experience;

/// <summary>Consumer-authored text safe to present to the user. Codes and optional field identities
/// retain domain meaning; locale resolution follows the explicit UiLocalizedText contract.</summary>
public sealed class UiActionMessage
{
    public UiActionMessage(string code, UiLocalizedText text, UiSymbolId? field = null)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("A message code is required.", nameof(code));
        ArgumentNullException.ThrowIfNull(text);
        if (field is { IsValid: false }) throw new ArgumentException("A valid field identity is required.", nameof(field));
        Code = code;
        Text = text;
        Field = field;
    }

    public string Code { get; }
    public UiLocalizedText Text { get; }
    public UiSymbolId? Field { get; }

    // Eligibility callbacks may recreate equal values each tick. Compare content, including
    // translations outside the current locale, without allocating or depending on map order.
    internal bool HasSameContent(UiActionMessage? other)
    {
        if (ReferenceEquals(this, other)) return true;
        return other is not null && Code == other.Code && Field == other.Field && Text.HasSameContent(other.Text);
    }
}
