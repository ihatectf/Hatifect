using System;
using System.Collections.Generic;

namespace Hatifect.UI.Semantics;

public sealed class UiBindingContext
{
    private readonly Dictionary<string, UiElementSymbol> _elements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UiSymbolId> _roles = new(StringComparer.Ordinal);
    private readonly Dictionary<UiSymbolId, UiElementSymbol> _elementsById = new();
    private readonly Dictionary<string, UiSemanticNode> _graphNodes = new(StringComparer.Ordinal);
    private readonly HashSet<UiSymbolId> _ids = new();

    public UiBindingContext(UiSymbolId ownerId)
    {
        if (!ownerId.IsValid) throw new ArgumentException("A valid Experience owner ID is required.", nameof(ownerId));
        OwnerId = ownerId;
    }

    public UiBindingContext(string scope)
        : this(new UiSymbolId(scope, "experience")) { }

    public UiBindingContext(UiSemanticGraph graph) : this(graph?.OwnerId ?? throw new ArgumentNullException(nameof(graph)))
    {
        IReadOnlyList<UiGraphDiagnostic> diagnostics = UiGraphBinder.Validate(graph);
        if (diagnostics.Count != 0) throw new UiGraphValidationException(diagnostics);

        ImportGraphNodes(graph);
        ImportGraphRoles(graph);
        Graph = graph;
    }

    private void ImportGraphNodes(UiSemanticGraph graph)
    {
        var presented = new HashSet<UiSymbolId>(graph.PresentedNodes);
        foreach (UiSemanticNode node in graph.Nodes)
        {
            _graphNodes.Add(node.Alias, node);
            if (!presented.Contains(node.Id)) continue;

            bool legacyName = node.DataType is null &&
                              node.Inputs.Count == 0 &&
                              node.Alias == node.Label &&
                              UiGraphBinder.IsCanonicalLegacyName(OwnerId, node.Id, "element", node.Alias);
            DeclareElementCore(node.Id, node.Alias, node.Label, node.Capabilities.ToArray(), legacyName);
        }
    }

    private void ImportGraphRoles(UiSemanticGraph graph)
    {
        foreach (UiGraphRole role in graph.Roles) DeclareRoleCore(role.Id, role.Alias,
            UiGraphBinder.IsCanonicalLegacyName(OwnerId, role.Id, "role", role.Alias));
    }

    public UiSymbolId OwnerId { get; }
    public string Scope => OwnerId.Scope;
    public UiSemanticGraph? Graph { get; }
    public bool RequireDeclaredElements { get; init; } = true;
    public bool RequireDeclaredRoles { get; init; } = true;

    public UiBindingContext DeclareElement(string name, params UiSymbolId[] capabilities)
        => DeclareElementCore(OwnerId.Child($"element/{name}"), name, name, capabilities, legacyName: true);

    public UiBindingContext DeclareElement(UiSymbolId id, string alias, string label, params UiSymbolId[] capabilities)
        => DeclareElementCore(id, alias, label, capabilities, legacyName: false);

    private UiBindingContext DeclareElementCore(UiSymbolId id, string alias, string label, UiSymbolId[] capabilities, bool legacyName)
    {
        EnsureDeclaration(id, alias, legacyName);
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("An element label is required.", nameof(label));
        var set = new HashSet<UiSymbolId>(capabilities ?? Array.Empty<UiSymbolId>());
        if (_elements.ContainsKey(alias)) throw new InvalidOperationException($"Semantic element '{alias}' is already declared.");
        var symbol = new UiElementSymbol(id, alias, set) { Label = label };
        _elements.Add(alias, symbol);
        _elementsById.Add(id, symbol);
        _ids.Add(id);
        return this;
    }

    public UiBindingContext DeclareRole(string name)
        => DeclareRoleCore(OwnerId.Child($"role/{name}"), name, legacyName: true);

    public UiBindingContext DeclareRole(UiSymbolId id, string alias)
        => DeclareRoleCore(id, alias, legacyName: false);

    private UiBindingContext DeclareRoleCore(UiSymbolId id, string alias, bool legacyName)
    {
        EnsureDeclaration(id, alias, legacyName);
        if (!_roles.TryAdd(alias, id)) throw new InvalidOperationException($"Visual role '{alias}' is already declared.");
        _ids.Add(id);
        return this;
    }

    private void EnsureDeclaration(UiSymbolId id, string alias, bool legacyName)
    {
        if (Graph is not null) throw new InvalidOperationException("A graph binding context is immutable.");
        if (!UiGraphBinder.IsChild(OwnerId, id)) throw new ArgumentException("Identity must be a child of the Experience owner.", nameof(id));
        if (string.IsNullOrWhiteSpace(alias) || (!legacyName && !UiGraphBinder.IsAlias(alias)))
            throw new ArgumentException("An authoring identifier is required.", nameof(alias));
        if (_ids.Contains(id)) throw new InvalidOperationException($"Identity '{id}' is already declared.");
    }

    public bool TryGetElement(string name, out UiElementSymbol? element) => _elements.TryGetValue(name, out element);
    public bool TryGetRole(string name, out UiSymbolId role) => _roles.TryGetValue(name, out role);
    public bool TryGetGraphNode(string alias, out UiSemanticNode? node) => _graphNodes.TryGetValue(alias, out node);

    internal bool TryGetElement(UiSymbolId id, out UiElementSymbol? element)
        => _elementsById.TryGetValue(id, out element);

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
