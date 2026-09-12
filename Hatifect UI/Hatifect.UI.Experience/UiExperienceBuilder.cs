using System;
using System.Collections.Generic;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Experience;

/// <summary>Semantic-first C# authoring API. It intentionally exposes no layout or visual geometry.</summary>
public sealed class UiExperienceBuilder
{
    private readonly UiSymbolId _id;
    private readonly string _displayName;
    private readonly List<UiSemanticElementDefinition> _elements = new();
    private readonly List<UiActionDefinition> _actions = new();
    private readonly List<UiVisualRoleDefinition> _roles = new();
    private readonly List<UiSemanticElementDefinition> _sources = new();
    private readonly Dictionary<UiSymbolId, Type> _sourceTypes = new();
    private readonly List<UiSemanticRelation> _relations = new();
    private readonly Dictionary<UiSymbolId, UiSemanticNode> _actionNodes = new();
    private readonly Dictionary<UiSymbolId, UiActionDefinition> _typedActions = new();
    private readonly HashSet<string> _elementNames = new(StringComparer.Ordinal);
    private readonly HashSet<UiSymbolId> _elementIds = new();
    private readonly HashSet<UiSymbolId> _actionIds = new();
    private readonly HashSet<string> _roleNames = new(StringComparer.Ordinal);
    private UiLocalizedText? _localizedDisplayName;
    private readonly Dictionary<UiSymbolId, UiLocalizedText> _localizedElementLabels = new();
    private readonly Dictionary<UiSymbolId, UiLocalizedText> _localizedActionTitles = new();
    private readonly Dictionary<UiSymbolId, IUiTextFormatter> _textFormatters = new();
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

    /// <summary>Authors an empty, loading, success, error, or general status region.</summary>
    public UiExperienceBuilder Status(string element, IUiSemanticSource<UiStatus> source)
        => Declare(_id.Child($"element/{element}"), element, element, source, UiSourceTypes.Status,
            true, new[] { UiCapabilities.Monitor }, legacyAlias: true);

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
    public UiExperienceBuilder Status(UiSymbolId id, string name, IUiSemanticSource<UiStatus> source)
        => Declare(id, LegacyAlias(id, name), name, source, UiSourceTypes.Status,
            true, new[] { UiCapabilities.Monitor }, legacyAlias: true);

    /// <summary>Authors a typed status region with separate stable alias and localized label.</summary>
    public UiExperienceBuilder Status(UiSymbolId id, string alias, string label, IUiSemanticSource<UiStatus> source)
        => Declare(id, alias, label, source, UiSourceTypes.Status,
            true, new[] { UiCapabilities.Monitor });

    public UiExperienceBuilder Element<T>(string element, IUiSemanticSource<T> source, params UiCapability[] capabilities)
        => Declare(_id.Child($"element/{element}"), element, element, source, null, true, capabilities, legacyAlias: true);

    /// <summary>Authors a localized display name independently from its stable semantic identity.</summary>
    public UiExperienceBuilder Element<T>(UiSymbolId id, string element, IUiSemanticSource<T> source, params UiCapability[] capabilities)
        => Declare(id, LegacyAlias(id, element), element, source, null, true, capabilities, legacyAlias: true);

    public UiExperienceBuilder Element<T>(UiSymbolId id, string alias, string label,
        IUiSemanticSource<T> source, params UiCapability[] capabilities)
        => Declare(id, alias, label, source, null, true, capabilities);

    public UiExperienceBuilder Element<T>(UiSymbolId id, string alias, string label,
        IUiSemanticSource<T> source, UiSourceType<T> type, params UiCapability[] capabilities)
        => Declare(id, alias, label, source, type ?? throw new ArgumentNullException(nameof(type)), true, capabilities);

    /// <summary>Declares an observed graph source without requesting a presentation widget.</summary>
    public UiExperienceBuilder Source<T>(UiSymbolId id, string alias, string label,
        IUiSemanticSource<T> source, UiSourceType<T> type, params UiCapability[] capabilities)
        => Declare(id, alias, label, source, type ?? throw new ArgumentNullException(nameof(type)), false, capabilities);

    private UiExperienceBuilder Declare<T>(UiSymbolId id, string alias, string label, IUiSemanticSource<T> source,
        UiSourceType<T>? type, bool presented, UiCapability[] capabilities, bool legacyAlias = false)
    {
        EnsureMutable();
        if (!id.IsValid) throw new ArgumentException("A stable element ID is required.", nameof(id));
        if (!legacyAlias && !UiGraphBinder.IsAlias(alias)) throw new ArgumentException("An authoring alias is required.", nameof(alias));
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("A semantic element label is required.", nameof(label));
        ArgumentNullException.ThrowIfNull(source);
        if (capabilities == null || capabilities.Length == 0) throw new ArgumentException("At least one semantic capability is required.", nameof(capabilities));
        if (_elementNames.Contains(alias)) throw new InvalidOperationException($"Semantic element '{alias}' is already declared.");
        if (_elementIds.Contains(id)) throw new InvalidOperationException($"Semantic element ID '{id}' is already declared.");

        var unique = new HashSet<UiSymbolId>();
        var ordered = new List<UiCapability>();
        foreach (UiCapability capability in capabilities)
        {
            ArgumentNullException.ThrowIfNull(capability);
            if (unique.Add(capability.Id)) ordered.Add(capability);
        }
        _elementNames.Add(alias);
        _elementIds.Add(id);
        var definition = new UiSemanticElementDefinition(id, label, source, ordered.AsReadOnly())
        { Alias = alias, DataType = type?.Descriptor };
        _sources.Add(definition);
        if (presented) _elements.Add(definition);
        if (type != null) _sourceTypes.Add(id, typeof(T));
        return this;
    }

    public UiExperienceBuilder Input(UiSymbolId node, UiProjectionInput input)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(input);
        int index = _sources.FindIndex(source => source.Id == node);
        if (index < 0) throw new ArgumentException("The projection node must be declared before its input.", nameof(node));
        UiSemanticElementDefinition previous = _sources[index];
        var next = previous with { Inputs = Array.AsReadOnly(previous.Inputs.Append(input).ToArray()) };
        _sources[index] = next;
        int presented = _elements.FindIndex(element => element.Id == node);
        if (presented >= 0) _elements[presented] = next;
        return this;
    }

    public UiExperienceBuilder Relation(UiSemanticRelation relation)
    {
        EnsureMutable();
        _relations.Add(relation ?? throw new ArgumentNullException(nameof(relation)));
        return this;
    }

    /// <summary>Describes the existing consumer action and its explicit request/result/target mapping.</summary>
    public UiExperienceBuilder Action(UiActionDefinition action, string alias, UiDataType type)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(type);
        if (type.Shape != UiDataShape.Action) throw new ArgumentException("An action descriptor is required.", nameof(type));
        if (!_actionNodes.TryAdd(action.Id, new(action.Id, alias, action.Title, type, new[] { UiCapabilities.Actions.Id })))
            throw new InvalidOperationException($"Action '{action.Id}' already has a graph declaration.");
        _typedActions.Add(action.Id, action);
        return this;
    }

    public UiExperienceBuilder Actions(string element, params UiActionDefinition[] actions)
        => Actions(_id.Child($"element/{element}"), element, actions);

    public UiExperienceBuilder Actions(UiSymbolId id, string element, params UiActionDefinition[] actions)
        => ActionsCore(id, LegacyAlias(id, element), element, actions, legacyAlias: true);

    public UiExperienceBuilder Actions(UiSymbolId id, string alias, string label, params UiActionDefinition[] actions)
        => ActionsCore(id, alias, label, actions, legacyAlias: false);

    private UiExperienceBuilder ActionsCore(UiSymbolId id, string alias, string label, UiActionDefinition[] actions, bool legacyAlias)
    {
        EnsureMutable();
        if (actions == null || actions.Length == 0) throw new ArgumentException("At least one action is required.", nameof(actions));
        var staged = new HashSet<UiSymbolId>(_actionIds);
        foreach (UiActionDefinition action in actions)
        {
            ArgumentNullException.ThrowIfNull(action);
            if (!staged.Add(action.Id)) throw new InvalidOperationException($"Action '{action.Id}' is already declared.");
        }

        Declare(id, alias, label, new UiConstantSource<IReadOnlyList<UiActionDefinition>>(Array.AsReadOnly((UiActionDefinition[])actions.Clone())),
            null, true, new[] { UiCapabilities.Actions }, legacyAlias);
        foreach (UiActionDefinition action in actions)
        {
            _actionIds.Add(action.Id);
            _actions.Add(action);
        }
        return this;
    }

    public UiExperienceBuilder VisualRole(string role)
        => VisualRoleCore(_id.Child($"role/{role}"), role, legacyAlias: true);

    public UiExperienceBuilder VisualRole(UiSymbolId id, string role)
        => VisualRoleCore(id, role, legacyAlias: false);

    private UiExperienceBuilder VisualRoleCore(UiSymbolId id, string role, bool legacyAlias)
    {
        EnsureMutable();
        if (!legacyAlias && !UiGraphBinder.IsAlias(role))
            throw new ArgumentException("A visual role alias is required.", nameof(role));
        if (string.IsNullOrWhiteSpace(role)) throw new ArgumentException("A visual role name is required.", nameof(role));
        if (!_roleNames.Add(role)) throw new InvalidOperationException($"Visual role '{role}' is already declared.");
        _roles.Add(new UiVisualRoleDefinition(id, role));
        return this;
    }

    /// <summary>Localizes the display name while retaining its original authoring fallback.</summary>
    public UiExperienceBuilder LocalizeDisplayName(UiLocalizedText text)
    {
        EnsureMutable();
        ValidateText(text, _displayName);
        if (_localizedDisplayName is not null) throw new InvalidOperationException("The display name is already localized.");
        _localizedDisplayName = text;
        return this;
    }

    /// <summary>Localizes an already declared element's label without changing its ID or alias.</summary>
    public UiExperienceBuilder LocalizeElement(UiSymbolId element, UiLocalizedText text)
    {
        EnsureMutable();
        var definition = _elements.Find(value => value.Id == element)
            ?? throw new ArgumentException("A presented element must be declared before its label.", nameof(element));
        ValidateText(text, definition.Label);
        if (!_localizedElementLabels.TryAdd(element, text))
            throw new InvalidOperationException($"Element '{element}' is already localized.");
        return this;
    }

    /// <summary>Localizes an already declared action's title while retaining the action object and binding.</summary>
    public UiExperienceBuilder LocalizeAction(UiSymbolId action, UiLocalizedText text)
    {
        EnsureMutable();
        var definition = _actions.Find(value => value.Id == action)
            ?? throw new ArgumentException("An action group must declare the action before its title.", nameof(action));
        ValidateText(text, definition.Title);
        if (!_localizedActionTitles.TryAdd(action, text))
            throw new InvalidOperationException($"Action '{action}' is already localized.");
        return this;
    }

    /// <summary>
    /// Formats a presented read-only value using its captured payload and the scene's captured locale.
    /// The callback must not read live sources or mutate application state. It runs during composition,
    /// never during drawing. Empty results are valid; null or an exception rejects candidate preparation.
    /// Editable inputs, forms, collections and action groups do not support value formatting.
    /// </summary>
    public UiExperienceBuilder FormatText<T>(UiSymbolId element, Func<T, string, string> format)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(format);
        var definition = _elements.Find(value => value.Id == element)
            ?? throw new ArgumentException("A presented element must be declared before its formatter.", nameof(element));
        if (definition.Source.ValueType != typeof(T))
            throw new ArgumentException($"Formatter type '{typeof(T)}' differs from element '{element}' source type.", nameof(format));
        if (definition.Source is IUiSemanticFormSource or IUiSemanticCollectionSource ||
            definition.Capabilities.Any(capability => capability.Id == UiCapabilities.Search.Id ||
                capability.Id == UiCapabilities.Filter.Id || capability.Id == UiCapabilities.Configure.Id ||
                capability.Id == UiCapabilities.Actions.Id))
            throw new ArgumentException($"Element '{element}' is not a supported read-only text value.", nameof(element));
        if (!_textFormatters.TryAdd(element, new UiTextFormatter<T>(format)))
            throw new InvalidOperationException($"Element '{element}' already has a text formatter.");
        return this;
    }

    private static void ValidateText(UiLocalizedText text, string fallback)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!string.Equals(text.Fallback, fallback, StringComparison.Ordinal))
            throw new ArgumentException("Localized text must retain the original authoring fallback.", nameof(text));
    }

    public UiExperienceDefinition Build()
    {
        EnsureMutable();
        if (_elements.Count == 0) throw new InvalidOperationException("An Experience must declare at least one semantic element.");
        var graph = new UiSemanticGraph(_id,
            _sources.Select(source => new UiSemanticNode(source.Id, source.Alias, source.Label, source.DataType,
                source.Capabilities.Select(capability => capability.Id), source.Inputs)).Concat(_actionNodes.Values),
            _relations, _elements.Select(element => element.Id), _roles.Select(role => new UiGraphRole(role.Id, role.Name)));
        var errors = new List<UiGraphDiagnostic>(UiGraphBinder.Validate(graph));
        var nominalTypes = new Dictionary<UiSymbolId, Type>();
        foreach (UiSemanticElementDefinition source in _sources)
            if (_sourceTypes.TryGetValue(source.Id, out Type? clr)) UiSourceTypeValidation.Validate(source, clr, nominalTypes, errors);
        foreach (UiSemanticElementDefinition element in _elements.Where(element => element.DataType is not null))
        {
            if (element.Capabilities.Any(capability => capability.Id == UiCapabilities.Actions.Id))
                errors.Add(new("UIG024", "Use an Actions group for presented commands; typed Action metadata is auxiliary.", element.Id));
            if (element.Capabilities.Any(capability => capability.Id == UiCapabilities.Configure.Id) && element.Source is not IUiSemanticFormSource)
                errors.Add(new("UIG024", "Presented Configure requires a semantic form source.", element.Id));
            if (element.Capabilities.Any(capability => capability.Id == UiCapabilities.Filter.Id) && element.Source.ValueType != typeof(string))
                errors.Add(new("UIG024", "Presented Filter requires a string input; typed enum/record filters must be auxiliary sources.", element.Id));
        }
        foreach (UiSymbolId action in _actionNodes.Keys)
            if (!_actionIds.Contains(action) || !_actions.Any(candidate => ReferenceEquals(candidate, _typedActions[action])))
                errors.Add(new("UIG023", "Typed action must be the same action instance used by an authored action group.", action));
        if (errors.Count != 0) throw new UiGraphValidationException(errors);
        _built = true;
        return new UiExperienceDefinition(_id, _displayName, _elements.ToArray(), _actions.ToArray(), _roles.ToArray(), _sources.ToArray(), graph,
            _localizedDisplayName, _localizedElementLabels, _localizedActionTitles, _textFormatters);
    }

    private static string LegacyAlias(UiSymbolId id, string label)
    {
        if (id.IsValid && id.LocalId.EndsWith("/element/" + label, StringComparison.Ordinal)) return label;
        if (UiGraphBinder.IsAlias(label)) return label;
        if (!id.IsValid) throw new ArgumentException("A stable identity is required.", nameof(id));
        string segment = id.LocalId[(id.LocalId.LastIndexOf('/') + 1)..];
        return UiGraphBinder.IsAlias(segment) ? segment : "node_" + segment;
    }

    private void EnsureMutable()
    {
        if (_built) throw new InvalidOperationException("An Experience builder is immutable after Build().");
    }
}
