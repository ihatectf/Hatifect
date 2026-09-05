using System;
using System.Collections.Generic;

namespace Hatifect.UI.Semantics;

public sealed class UiBindingContext
{
    private readonly Dictionary<string, UiElementSymbol> _elements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UiSymbolId> _roles = new(StringComparer.Ordinal);

    public UiBindingContext(UiSymbolId ownerId)
    {
        if (!ownerId.IsValid) throw new ArgumentException("A valid Experience owner ID is required.", nameof(ownerId));
        OwnerId = ownerId;
    }

    public UiBindingContext(string scope)
        : this(new UiSymbolId(scope, "experience")) { }

    public UiSymbolId OwnerId { get; }
    public string Scope => OwnerId.Scope;
    public bool RequireDeclaredElements { get; init; } = true;
    public bool RequireDeclaredRoles { get; init; } = true;

    public UiBindingContext DeclareElement(string name, params UiSymbolId[] capabilities)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("An element name is required.", nameof(name));
        var set = new HashSet<UiSymbolId>(capabilities ?? Array.Empty<UiSymbolId>());
        if (!_elements.TryAdd(name, new UiElementSymbol(OwnerId.Child($"element/{name}"), name, set)))
            throw new InvalidOperationException($"Semantic element '{name}' is already declared.");
        return this;
    }

    public UiBindingContext DeclareRole(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A role name is required.", nameof(name));
        if (!_roles.TryAdd(name, OwnerId.Child($"role/{name}")))
            throw new InvalidOperationException($"Visual role '{name}' is already declared.");
        return this;
    }

    public bool TryGetElement(string name, out UiElementSymbol? element) => _elements.TryGetValue(name, out element);
    public bool TryGetRole(string name, out UiSymbolId role) => _roles.TryGetValue(name, out role);

    internal bool TryGetElement(UiSymbolId id, out UiElementSymbol? element)
    {
        foreach (UiElementSymbol candidate in _elements.Values)
        {
            if (candidate.Id != id) continue;
            element = candidate;
            return true;
        }
        element = null;
        return false;
    }

    internal UiElementSymbol CreateUndeclaredElement(string name)
        => new(OwnerId.Child($"element/{name}"), name, new HashSet<UiSymbolId>());

    internal UiSymbolId CreateUndeclaredRole(string name)
        => OwnerId.Child($"role/{name}");

    internal UiElementSymbol[] SnapshotDeclaredElements()
    {
        var snapshot = new UiElementSymbol[_elements.Count];
        _elements.Values.CopyTo(snapshot, 0);
        return snapshot;
    }

    internal KeyValuePair<string, UiSymbolId>[] SnapshotDeclaredRoles()
    {
        var snapshot = new KeyValuePair<string, UiSymbolId>[_roles.Count];
        int index = 0;
        foreach (KeyValuePair<string, UiSymbolId> role in _roles)
            snapshot[index++] = role;
        return snapshot;
    }
}
