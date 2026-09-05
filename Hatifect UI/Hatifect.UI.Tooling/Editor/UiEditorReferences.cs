using System;
using System.Collections.Generic;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Tooling.Editor;

internal sealed partial class UiEditorAnalysis
{
    private readonly Dictionary<UiSymbolId, IReadOnlyList<UiEditorSymbol>> _occurrences;

    internal IReadOnlyList<UiEditorSymbol> Occurrences(UiSymbolId id)
        => _occurrences.TryGetValue(id, out IReadOnlyList<UiEditorSymbol>? symbols)
            ? symbols : Array.Empty<UiEditorSymbol>();
}

internal static class UiEditorReferences
{
    internal static IEnumerable<(UiEditorDocumentSnapshot Document, UiEditorSymbol Symbol)> Find(
        IEnumerable<UiEditorDocumentSnapshot> documents, UiSymbolId id, bool includeDeclaration)
    {
        foreach (UiEditorDocumentSnapshot document in documents)
            foreach (UiEditorSymbol symbol in document.Analysis.Occurrences(id))
                if (includeDeclaration || !symbol.IsDeclaration) yield return (document, symbol);
    }
}
