using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Visual.Resolution;

namespace Hatifect.UI.Runtime.Scene;

internal enum UiSceneNodeKind
{
    Host,
    Slot,
    Collection,
    Text,
    Button,
    RouteButton,
    TextInput,
    Inspector,
    Form,
    ActionBar
}

internal enum UiCollectionLayoutKind
{
    List,
    AdaptiveGrid
}

internal sealed record UiCollectionPresentationRecipe(
    UiCollectionLayoutKind Layout,
    int PreviewRows,
    int PreferredColumns,
    UiSymbolId ItemSizing,
    bool IsAdaptive,
    string Density,
    bool IsNavigation);

internal sealed record UiSceneMeasurementContext(
    UiSymbolId Profile,
    string Locale,
    UiSymbolId Theme);

internal abstract class UiSceneNode
{
    protected UiSceneNode(
        UiSymbolId id,
        UiSceneNodeKind kind,
        UiSymbolId role,
        UiVisualResolution visual,
        UiSceneNode[]? children = null)
    {
        if (!id.IsValid) throw new ArgumentException("A stable scene node ID is required.", nameof(id));
        if (!role.IsValid) throw new ArgumentException("A stable visual role ID is required.", nameof(role));
        Id = id;
        Kind = kind;
        Role = role;
        Visual = visual ?? throw new ArgumentNullException(nameof(visual));
        Children = Array.AsReadOnly(children ?? Array.Empty<UiSceneNode>());
    }

    public UiSymbolId Id { get; }
    public UiSceneNodeKind Kind { get; }
    public UiSymbolId Role { get; }
    public UiVisualResolution Visual { get; }
    public IReadOnlyList<UiSceneNode> Children { get; }
}

internal sealed class UiContainerSceneNode : UiSceneNode
{
    public UiContainerSceneNode(
        UiSymbolId id,
        UiSceneNodeKind kind,
        UiSymbolId role,
        UiVisualResolution visual,
        UiSceneNode[] children,
        string? semanticName = null)
        : base(id, kind, role, visual, children)
        => SemanticName = semanticName;

    public string? SemanticName { get; }
}

internal sealed class UiSlotSceneNode : UiSceneNode
{
    public UiSlotSceneNode(
        UiSymbolId id,
        UiSymbolId role,
        UiVisualResolution visual,
        UiSymbolId slot,
        UiSceneNode[] children)
        : base(id, UiSceneNodeKind.Slot, role, visual, children)
    {
        if (!slot.IsValid) throw new ArgumentException("A stable host slot ID is required.", nameof(slot));
        Slot = slot;
    }

    public UiSymbolId Slot { get; }
}

internal sealed class UiSourceSceneNode : UiSceneNode
{
    public UiSourceSceneNode(
        UiSymbolId id,
        UiSceneNodeKind kind,
        UiSymbolId role,
        UiVisualResolution visual,
        string semanticName,
        IUiSemanticSource source)
        : base(id, kind, role, visual)
    {
        if (string.IsNullOrWhiteSpace(semanticName))
            throw new ArgumentException("A semantic source name is required.", nameof(semanticName));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        SemanticName = semanticName;
        DisplayText = kind is UiSceneNodeKind.Text or UiSceneNodeKind.Inspector or UiSceneNodeKind.Form
            ? source.UntypedValue?.ToString() ?? string.Empty
            : string.Empty;
    }

    public IUiSemanticSource Source { get; }
    public string SemanticName { get; }
    public string DisplayText { get; }
}

internal sealed class UiCollectionSceneNode : UiSceneNode
{
    private readonly IUiSelectableCollectionSource? _selection;
    private readonly IUiSemanticCollectionSource _source;
    private readonly IUiCollectionRuntimeMetadata? _runtimeMetadata;
    private readonly long _sourceRevision;
    private readonly bool _mayHaveSupportingText;
    private readonly IReadOnlyDictionary<UiSymbolId, UiVisualResolution> _activeItemVisuals;

    public UiCollectionSceneNode(
        UiSymbolId id,
        UiSymbolId role,
        UiVisualResolution visual,
        UiVisualResolution selectedItemVisual,
        string semanticName,
        IUiSemanticCollectionSource source,
        UiCollectionPresentationRecipe recipe,
        IDictionary<UiSymbolId, UiVisualResolution>? activeItemVisuals = null)
        : base(id, UiSceneNodeKind.Collection, role, visual)
    {
        if (string.IsNullOrWhiteSpace(semanticName))
            throw new ArgumentException("A semantic collection name is required.", nameof(semanticName));
        ArgumentNullException.ThrowIfNull(source);
        int count = source.Count;
        if (count < 0)
            throw new InvalidOperationException($"Collection '{id}' returned a negative item count.");
        SemanticName = semanticName;
        _source = source;
        _runtimeMetadata = source as IUiCollectionRuntimeMetadata;
        Count = count;
        Recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));
        if (Recipe.IsAdaptive && _runtimeMetadata == null)
            throw new InvalidOperationException(
                $"Adaptive collection '{id}' requires the provisional stable-ID lookup capability. " +
                "Use a built-in Hatifect semantic collection source until the public source contract is frozen.");
        _sourceRevision = _runtimeMetadata?.Revision ?? 0;
        _mayHaveSupportingText = !Recipe.IsNavigation && (_runtimeMetadata?.HasSupportingText ?? true);
        SelectedItemVisual = selectedItemVisual ?? throw new ArgumentNullException(nameof(selectedItemVisual));
        _selection = source as IUiSelectableCollectionSource;
        SelectedItemId = _selection?.SelectedItemId;
        if (SelectedItemId is { } selected &&
            _runtimeMetadata != null &&
            !_runtimeMetadata.TryGetIndex(selected, out _))
            throw new InvalidOperationException(
                $"Collection '{id}' selected item '{selected}' is absent from its semantic snapshot.");
        _activeItemVisuals = new ReadOnlyDictionary<UiSymbolId, UiVisualResolution>(
            new Dictionary<UiSymbolId, UiVisualResolution>(
                activeItemVisuals ?? new Dictionary<UiSymbolId, UiVisualResolution>()));
    }

    public string SemanticName { get; }
    public int Count { get; }
    public object SourceIdentity => _source;
    public long SourceRevision => _sourceRevision;
    public bool MayHaveSupportingText => _mayHaveSupportingText;
    public UiCollectionPresentationRecipe Recipe { get; }
    public UiVisualResolution SelectedItemVisual { get; }
    public UiSymbolId? SelectedItemId { get; }
    public bool IsSelectable => _selection != null;
    internal IReadOnlyDictionary<UiSymbolId, UiVisualResolution> ActiveItemVisuals => _activeItemVisuals;

    public UiSemanticCollectionItem ItemAt(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        return _source.GetItem(index)
            ?? throw new InvalidOperationException($"Collection '{Id}' returned a null item at index {index}.");
    }

    public UiSymbolId ItemNodeId(UiSymbolId item) => item;

    public bool TryGetIndex(UiSymbolId item, int hint, out int index)
    {
        if (_runtimeMetadata != null && _runtimeMetadata.TryGetIndex(item, out index)) return true;
        if ((uint)hint < (uint)Count && ItemAt(hint).Id == item)
        {
            index = hint;
            return true;
        }
        index = -1;
        return false;
    }

    public bool IsSelected(UiSymbolId item) => SelectedItemId == item;

    public UiVisualResolution VisualFor(UiSymbolId node, UiSymbolId item)
        => _activeItemVisuals.TryGetValue(node, out UiVisualResolution? visual)
            ? visual
            : IsSelected(item) ? SelectedItemVisual : Visual;

    public bool TrySelect(UiSymbolId item) => _selection?.TrySelect(item) == true;

}

internal sealed class UiTextSceneNode : UiSceneNode
{
    public UiTextSceneNode(UiSymbolId id, UiSymbolId role, UiVisualResolution visual, string text)
        : base(id, UiSceneNodeKind.Text, role, visual)
        => Text = text ?? throw new ArgumentNullException(nameof(text));

    public string Text { get; }
}

internal sealed class UiButtonSceneNode : UiSceneNode
{
    public UiButtonSceneNode(UiSymbolId id, UiSymbolId role, UiVisualResolution visual, UiActionDefinition action)
        : base(id, UiSceneNodeKind.Button, role, visual)
        => Action = action ?? throw new ArgumentNullException(nameof(action));

    public UiActionDefinition Action { get; }
    public string Label => Action.Title;
    public bool Invoke() => Action.TryExecute();
}

internal sealed class UiRouteButtonSceneNode : UiSceneNode
{
    public UiRouteButtonSceneNode(
        UiSymbolId id,
        UiSymbolId role,
        UiVisualResolution visual,
        string label,
        UiSymbolId route,
        bool isCurrent = false)
        : base(id, UiSceneNodeKind.RouteButton, role, visual)
    {
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("A route button label is required.", nameof(label));
        if (!route.IsValid) throw new ArgumentException("A stable route ID is required.", nameof(route));
        Label = label;
        Route = route;
        IsCurrent = isCurrent;
    }

    public string Label { get; }
    public UiSymbolId Route { get; }
    public bool IsCurrent { get; }
}

internal sealed class UiTextInputSceneNode : UiSceneNode
{
    private readonly IUiSemanticSource _source;
    private readonly string _text;

    public UiTextInputSceneNode(
        UiSymbolId id,
        UiSymbolId role,
        UiVisualResolution visual,
        string semanticName,
        IUiSemanticSource source)
        : base(id, UiSceneNodeKind.TextInput, role, visual)
    {
        if (string.IsNullOrWhiteSpace(semanticName))
            throw new ArgumentException("A semantic text-input name is required.", nameof(semanticName));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        SemanticName = semanticName;
        _text = source.UntypedValue?.ToString() ?? string.Empty;
    }

    public string SemanticName { get; }
    public string Text => _text;
    internal string CurrentText => _source.UntypedValue?.ToString() ?? string.Empty;

    public bool TrySetText(string text)
    {
        if (_source is not IUiMutableSemanticSource<string> mutable) return false;
        mutable.Value = text ?? string.Empty;
        return true;
    }
}

internal sealed class UiHostSceneNode : UiSceneNode
{
    public UiHostSceneNode(
        UiSymbolId id,
        UiSymbolId role,
        UiVisualResolution visual,
        UiHostPolicy policy,
        UiSceneNode[] children)
        : base(id, UiSceneNodeKind.Host, role, visual, children)
        => Policy = policy ?? throw new ArgumentNullException(nameof(policy));

    public UiHostPolicy Policy { get; }
}

internal sealed class UiScene
{
    public UiScene(
        UiSymbolId experience,
        string displayName,
        UiHostSceneNode root,
        UiSceneMeasurementContext measurementContext)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("An Experience display name is required.", nameof(displayName));
        Experience = experience;
        DisplayName = displayName;
        Root = root ?? throw new ArgumentNullException(nameof(root));
        MeasurementContext = measurementContext ?? throw new ArgumentNullException(nameof(measurementContext));
    }

    public UiSymbolId Experience { get; }
    public string DisplayName { get; }
    public UiHostSceneNode Root { get; }
    public UiSceneMeasurementContext MeasurementContext { get; }
}
