using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Experience;

namespace Hatifect.UI.Planning;

/// <summary>Immutable facts needed to choose a presentation, without a live semantic source.</summary>
public sealed class UiPlanningElement
{
    public UiPlanningElement(UiSymbolId id, IEnumerable<UiCapability> capabilities, bool isCollection)
    {
        if (!id.IsValid) throw new ArgumentException("A valid element ID is required.", nameof(id));
        ArgumentNullException.ThrowIfNull(capabilities);
        UiCapability[] copied = capabilities.ToArray();
        if (copied.Any(capability => capability is null || !capability.Id.IsValid))
            throw new ArgumentException("Capabilities must have valid identities.", nameof(capabilities));
        Id = id;
        Capabilities = Array.AsReadOnly(copied);
        IsCollection = isCollection;
    }

    public UiSymbolId Id { get; }
    public IReadOnlyList<UiCapability> Capabilities { get; }
    public bool IsCollection { get; }
}
