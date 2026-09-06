using System;
using System.Collections.Generic;
using Hatifect.UI.Experience;

namespace Hatifect.UI.Runtime.Scene;

/// <summary>One scene composition reads each publication exactly once, before formatting any values.</summary>
internal sealed class UiPublicationReadScope
{
    private readonly Dictionary<UiPublication, UiPublicationView> _views = new();
    internal UiPublicationReadScope(UiExperienceDefinition experience)
    {
        foreach (var element in experience.Sources)
        {
            Capture(element.Source);
            if (element.Source is not IUiSemanticFormSource form) continue;
            foreach (var field in form.Fields)
            {
                Capture(field.Value);
                if (field.ValidationMessage is not null) Capture(field.ValidationMessage);
            }
        }
    }
    internal bool HasPublications => _views.Count != 0;
    internal IUiSemanticSource Read(IUiSemanticSource source)
    {
        if (source is not IUiPublicationReadableSource readable || readable.Publication is not { } publication) return source;
        if (!_views.TryGetValue(publication, out var view))
            throw new InvalidOperationException("A scene source introduced an uncaptured publication during composition.");
        return readable.ReadSnapshot(view) ?? throw new InvalidOperationException("A publication source returned a null snapshot.");
    }
    private void Capture(IUiSemanticSource source)
    {
        if (source is IUiPublicationReadableSource readable && readable.Publication is { } publication && !_views.ContainsKey(publication))
            _views.Add(publication, publication.Capture());
    }
}
