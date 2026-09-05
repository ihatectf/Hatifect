using System;
using System.Collections.Generic;

namespace Hatifect.UI.Experience;

/// <summary>Semantic-first C# authoring API. It intentionally exposes no layout or visual geometry.</summary>
public sealed class UiExperienceBuilder
{
    private readonly UiSymbolId _id;
    private readonly string _displayName;
    private readonly List<UiSemanticElementDefinition> _elements = new();
    private readonly List<UiActionDefinition> _actions = new();
    private readonly List<UiVisualRoleDefinition> _roles = new();
    private readonly HashSet<string> _elementNames = new(StringComparer.Ordinal);
    private readonly HashSet<UiSymbolId> _elementIds = new();
    private readonly HashSet<UiSymbolId> _actionIds = new();
    private readonly HashSet<string> _roleNames = new(StringComparer.Ordinal);
    private bool _built;

    public UiExperienceBuilder(UiSymbolId id, string displayName)
    {
        if (!id.IsValid) throw new ArgumentException("A stable Experience ID is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("An Experience display name is required.", nameof(displayName));
        _id = id;
        _displayName = displayName;
    }

    public UiExperienceBuilder Browse<T>(string element, IUiSemanticSource<T> source)
        => Element(element, source, UiCapabilities.Browse);

    public UiExperienceBuilder Search<T>(string element, IUiSemanticSource<T> source)
        => Element(element, source, UiCapabilities.Search);

    public UiExperienceBuilder Filter<T>(string element, IUiSemanticSource<T> source)
        => Element(element, source, UiCapabilities.Filter);

    public UiExperienceBuilder Select<T>(string element, IUiSemanticSource<T> source)
        => Element(element, source, UiCapabilities.Select);

    public UiExperienceBuilder Inspect<T>(string element, IUiSemanticSource<T> source)
        => Element(element, source, UiCapabilities.Inspect);

    public UiExperienceBuilder Configure<T>(string element, IUiSemanticSource<T> source)
        => Element(element, source, UiCapabilities.Configure);

    public UiExperienceBuilder Monitor<T>(string element, IUiSemanticSource<T> source)
        => Element(element, source, UiCapabilities.Monitor);

    public UiExperienceBuilder Navigate<T>(string element, IUiSemanticSource<T> source)
        => Element(element, source, UiCapabilities.Navigate);

    public UiExperienceBuilder Inspect<T>(UiSymbolId id, string name, IUiSemanticSource<T> source)
        => Element(id, name, source, UiCapabilities.Inspect);
    public UiExperienceBuilder Configure<T>(UiSymbolId id, string name, IUiSemanticSource<T> source)
        => Element(id, name, source, UiCapabilities.Configure);
    public UiExperienceBuilder Select<T>(UiSymbolId id, string name, IUiSemanticSource<T> source)
        => Element(id, name, source, UiCapabilities.Select);
    public UiExperienceBuilder Monitor<T>(UiSymbolId id, string name, IUiSemanticSource<T> source)
        => Element(id, name, source, UiCapabilities.Monitor);

    public UiExperienceBuilder Element<T>(string element, IUiSemanticSource<T> source, params UiCapability[] capabilities)
        => Element(_id.Child($"element/{element}"), element, source, capabilities);

    /// <summary>Authors a localized display name independently from its stable semantic identity.</summary>
    public UiExperienceBuilder Element<T>(UiSymbolId id, string element, IUiSemanticSource<T> source, params UiCapability[] capabilities)
    {
        EnsureMutable();
        if (!id.IsValid) throw new ArgumentException("A stable element ID is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(element)) throw new ArgumentException("A semantic element name is required.", nameof(element));
        ArgumentNullException.ThrowIfNull(source);
        if (capabilities == null || capabilities.Length == 0) throw new ArgumentException("At least one semantic capability is required.", nameof(capabilities));
        if (_elementNames.Contains(element)) throw new InvalidOperationException($"Semantic element '{element}' is already declared.");
        if (_elementIds.Contains(id)) throw new InvalidOperationException($"Semantic element ID '{id}' is already declared.");

        var unique = new HashSet<UiSymbolId>();
        var ordered = new List<UiCapability>();
        foreach (UiCapability capability in capabilities)
        {
            ArgumentNullException.ThrowIfNull(capability);
            if (unique.Add(capability.Id)) ordered.Add(capability);
        }
        _elementNames.Add(element);
        _elementIds.Add(id);
        _elements.Add(new UiSemanticElementDefinition(id, element, source, ordered.ToArray()));
        return this;
    }

    public UiExperienceBuilder Actions(string element, params UiActionDefinition[] actions)
        => Actions(_id.Child($"element/{element}"), element, actions);

    public UiExperienceBuilder Actions(UiSymbolId id, string element, params UiActionDefinition[] actions)
    {
        EnsureMutable();
        if (actions == null || actions.Length == 0) throw new ArgumentException("At least one action is required.", nameof(actions));
        var staged = new HashSet<UiSymbolId>(_actionIds);
        foreach (UiActionDefinition action in actions)
        {
            ArgumentNullException.ThrowIfNull(action);
            if (!staged.Add(action.Id)) throw new InvalidOperationException($"Action '{action.Id}' is already declared.");
        }

        Element(id, element, new UiConstantSource<IReadOnlyList<UiActionDefinition>>(actions), UiCapabilities.Actions);
        foreach (UiActionDefinition action in actions)
        {
            _actionIds.Add(action.Id);
            _actions.Add(action);
        }
        return this;
    }

    public UiExperienceBuilder VisualRole(string role)
    {
        EnsureMutable();
        if (string.IsNullOrWhiteSpace(role)) throw new ArgumentException("A visual role name is required.", nameof(role));
        if (!_roleNames.Add(role)) throw new InvalidOperationException($"Visual role '{role}' is already declared.");
        _roles.Add(new UiVisualRoleDefinition(_id.Child($"role/{role}"), role));
        return this;
    }

    public UiExperienceDefinition Build()
    {
        EnsureMutable();
        if (_elements.Count == 0) throw new InvalidOperationException("An Experience must declare at least one semantic element.");
        _built = true;
        return new UiExperienceDefinition(_id, _displayName, _elements.ToArray(), _actions.ToArray(), _roles.ToArray());
    }

    private void EnsureMutable()
    {
        if (_built) throw new InvalidOperationException("An Experience builder is immutable after Build().");
    }
}
