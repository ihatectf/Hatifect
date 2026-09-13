using System;

namespace Hatifect.UI;

/// <summary>A stable globally-scoped semantic identity. Display names are never identities.</summary>
public readonly record struct UiSymbolId
{
    public UiSymbolId(string scope, string localId)
    {
        if (!IsValidPart(scope, allowSlash: false))
            throw new ArgumentException("A symbol scope must contain only letters, digits, '.', '_' or '-'.", nameof(scope));
        if (!IsValidPart(localId, allowSlash: true))
            throw new ArgumentException("A local symbol ID must contain only letters, digits, '.', '_', '-' or '/'.", nameof(localId));

        Scope = scope;
        LocalId = localId;
    }

    public string Scope { get; }
    public string LocalId { get; }
    public bool IsValid => Scope != null && LocalId != null;

    public static UiSymbolId Parse(string value)
    {
        if (!TryParse(value, out UiSymbolId id))
            throw new FormatException($"'{value}' is not a globally-scoped UI symbol ID. Expected 'Scope/local-id'.");
        return id;
    }

    public static bool TryParse(string? value, out UiSymbolId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        int separator = value.IndexOf('/');
        if (separator <= 0 || separator == value.Length - 1) return false;
        string scope = value[..separator];
        string local = value[(separator + 1)..];
        if (!IsValidPart(scope, allowSlash: false) || !IsValidPart(local, allowSlash: true)) return false;
        id = new UiSymbolId(scope, local);
        return true;
    }

    public UiSymbolId Child(string localSegment)
        => new(Scope, $"{LocalId}/{localSegment}");

    public override string ToString() => IsValid ? $"{Scope}/{LocalId}" : string.Empty;

    private static bool IsValidPart(string? value, bool allowSlash)
    {
        if (string.IsNullOrWhiteSpace(value) || value[0] == '/' || value[^1] == '/') return false;
        bool previousSlash = false;
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c) || c is '.' or '_' or '-')
            {
                previousSlash = false;
                continue;
            }
            if (allowSlash && c == '/' && !previousSlash)
            {
                previousSlash = true;
                continue;
            }
            return false;
        }
        return true;
    }
}
