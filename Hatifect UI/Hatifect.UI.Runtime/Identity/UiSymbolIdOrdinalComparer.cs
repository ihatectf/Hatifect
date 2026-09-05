using System.Collections.Generic;

namespace Hatifect.UI.Runtime.Identity;

/// <summary>
/// Canonical Runtime ordering for stable semantic identities: ordinal scope, then ordinal local ID.
/// The typed comparison avoids formatting allocations and does not depend on the display form.
/// </summary>
internal sealed class UiSymbolIdOrdinalComparer : IComparer<UiSymbolId>
{
    internal static UiSymbolIdOrdinalComparer Instance { get; } = new();

    private UiSymbolIdOrdinalComparer()
    {
    }

    public int Compare(UiSymbolId left, UiSymbolId right)
    {
        int scope = StringComparer.Ordinal.Compare(left.Scope, right.Scope);
        return scope != 0
            ? scope
            : StringComparer.Ordinal.Compare(left.LocalId, right.LocalId);
    }
}
