using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Input;
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
        Children = Array.AsReadOnly(children is { Length: > 0 }
            ? (UiSceneNode[])children.Clone() : Array.Empty<UiSceneNode>());
    }

    public UiSymbolId Id { get; }
    // Immutable authoring origin; consumers never infer this from renderer node suffixes.
    internal UiSymbolId? SemanticId { get; init; }
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
        IUiSemanticSource source,
        string? displayText = null)
        : base(id, kind, role, visual)
    {
        if (string.IsNullOrWhiteSpace(semanticName))
            throw new ArgumentException("A semantic source name is required.", nameof(semanticName));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        SemanticName = semanticName;
        DisplayText = kind is UiSceneNodeKind.Text or UiSceneNodeKind.Inspector or UiSceneNodeKind.Form
            ? displayText ?? source.UntypedValue?.ToString() ?? string.Empty
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
    private readonly IUiSemanticCollectionSource _sourceIdentity;
    private readonly bool _captured;
    private readonly IUiSemanticCollectionMetadata? _runtimeMetadata;
    private readonly long _sourceRevision;
    private readonly bool _mayHaveSupportingText;
    private readonly IReadOnlyDictionary<UiSymbolId, UiVisualResolution> _activeItemVisuals;
    private readonly UiCollectionStateVisuals? _stateVisuals;

    public UiCollectionSceneNode(
        UiSymbolId id,
        UiSymbolId role,
        UiVisualResolution visual,
        UiVisualResolution selectedItemVisual,
        string semanticName,
        IUiSemanticCollectionSource source,
        UiCollectionPresentationRecipe recipe,
        IDictionary<UiSymbolId, UiVisualResolution>? activeItemVisuals = null,
        IUiSemanticCollectionSnapshot? publicationSnapshot = null)
        : this(id, role, visual, selectedItemVisual, semanticName, source, recipe,
            activeItemVisuals, publicationSnapshot, stateVisuals: null) { }

    internal UiCollectionSceneNode(
        UiSymbolId id,
        UiSymbolId role,
        UiVisualResolution visual,
        UiVisualResolution selectedItemVisual,
        string semanticName,
        IUiSemanticCollectionSource source,
        UiCollectionPresentationRecipe recipe,
        IDictionary<UiSymbolId, UiVisualResolution>? activeItemVisuals,
        IUiSemanticCollectionSnapshot? publicationSnapshot,
        UiCollectionStateVisuals? stateVisuals)
        : base(id, UiSceneNodeKind.Collection, role, visual)
    {
        if (string.IsNullOrWhiteSpace(semanticName))
            throw new ArgumentException("A semantic collection name is required.", nameof(semanticName));
        ArgumentNullException.ThrowIfNull(source);
        IUiSemanticCollectionSnapshot? snapshot = publicationSnapshot ?? (source is IUiSemanticCollectionSnapshotSource snapshots
            ? snapshots.CaptureSnapshot() ?? throw new InvalidOperationException($"Collection '{id}' returned a null snapshot.")
            : null);
        _sourceIdentity = source;
        _source = snapshot ?? source;
        _captured = snapshot is not null || source is IUiSemanticCollectionSnapshot;
        int count = _source.Count;
        if (count < 0)
            throw new InvalidOperationException($"Collection '{id}' returned a negative item count.");
        SemanticName = semanticName;
        _runtimeMetadata = _source as IUiSemanticCollectionMetadata;
        Count = count;
        Recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));
        if (Recipe.IsAdaptive && _runtimeMetadata == null)
            throw new InvalidOperationException(
                $"Adaptive collection '{id}' requires the stable-ID lookup capability IUiSemanticCollectionMetadata.");
        _sourceRevision = _runtimeMetadata?.Revision ?? 0;
        if (_sourceRevision < 0) throw new InvalidOperationException($"Collection '{id}' returned a negative revision.");
        _mayHaveSupportingText = !Recipe.IsNavigation && (_runtimeMetadata?.HasSupportingText ?? true);
        SelectedItemVisual = selectedItemVisual ?? throw new ArgumentNullException(nameof(selectedItemVisual));
        _selection = source as IUiSelectableCollectionSource;
        SelectedItemId = _source is IUiSemanticCollectionSnapshot captured ? captured.SelectedItemId : _selection?.SelectedItemId;
        if (SelectedItemId is { } selected &&
            _runtimeMetadata != null &&
            !TryGetIndex(selected, -1, out _))
            throw new InvalidOperationException(
                $"Collection '{id}' selected item '{selected}' is absent from its semantic snapshot.");
        _activeItemVisuals = new ReadOnlyDictionary<UiSymbolId, UiVisualResolution>(
            new Dictionary<UiSymbolId, UiVisualResolution>(
                activeItemVisuals ?? new Dictionary<UiSymbolId, UiVisualResolution>()));
        _stateVisuals = stateVisuals;
    }

    public string SemanticName { get; }
    public int Count { get; }
    public object SourceIdentity => _sourceIdentity;
    public long SourceRevision => _sourceRevision;
    internal long? CapturedSourceVersion => (_source as IUiSemanticCollectionSnapshot)?.Version;
    internal bool TryVisitIndexChanges(long afterVersion, Action<UiCollectionIndexChange> visit)
        => _source is IUiCollectionIndexChangeSnapshot changes && changes.TryVisitIndexChanges(afterVersion, visit);
    public bool MayHaveSupportingText => _mayHaveSupportingText;
    public UiCollectionPresentationRecipe Recipe { get; }
    public UiVisualResolution SelectedItemVisual { get; }
    public UiSymbolId? SelectedItemId { get; }
    public bool IsSelectable => _selection != null;
    internal bool HasCapturedItems => _captured;
    internal IReadOnlyDictionary<UiSymbolId, UiVisualResolution> ActiveItemVisuals => _activeItemVisuals;

    public UiSemanticCollectionItem ItemAt(int index)
    {
        EnsureCapturedVersion();
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        return _source.GetItem(index)
            ?? throw new InvalidOperationException($"Collection '{Id}' returned a null item at index {index}.");
    }

    public UiSymbolId ItemNodeId(UiSymbolId item) => item;

    public bool TryGetIndex(UiSymbolId item, int hint, out int index)
    {
        EnsureCapturedVersion();
        if (_runtimeMetadata != null && _runtimeMetadata.TryGetIndex(item, out index))
        {
            if ((uint)index >= (uint)Count || ItemAt(index).Id != item)
                throw new InvalidOperationException($"Collection '{Id}' returned an invalid stable-ID lookup for '{item}'.");
            return true;
        }
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

    internal UiVisualResolution VisualFor(UiSymbolId node, UiSymbolId item, UiInteractionSnapshot? interaction)
        => interaction is not null && _stateVisuals is not null
            ? _stateVisuals.Resolve(node, IsSelected(item), interaction) : VisualFor(node, item);

    internal UiVisualResolution ContainerVisualFor(UiInteractionSnapshot? interaction)
    {
        if (Count != 0 || !IsSelectable) return Visual;
        if (interaction is not null && _stateVisuals is not null)
            return _stateVisuals.Resolve(Id, selected: false, interaction);
        return _activeItemVisuals.TryGetValue(Id, out UiVisualResolution? visual) ? visual : Visual;
    }

    public bool TrySelect(UiSymbolId item) => _selection?.TrySelect(item) == true;

    private void EnsureCapturedVersion()
    {
        if (!_captured && (_source.Count != Count || (_runtimeMetadata?.Revision ?? 0) != _sourceRevision))
            throw new InvalidOperationException($"Collection '{Id}' changed after scene capture; recompose before reading it or implement IUiSemanticCollectionSnapshotSource.");
    }

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
    private readonly UiButtonStateVisuals? _stateVisuals;
    public UiButtonSceneNode(UiSymbolId id, UiSymbolId role, UiVisualResolution visual, UiActionDefinition action,
        UiButtonStateVisuals? stateVisuals = null, string? label = null)
        : base(id, UiSceneNodeKind.Button, role, visual)
    {
        Action = action ?? throw new ArgumentNullException(nameof(action));
        _stateVisuals = stateVisuals;
        if (label is not null && string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("An action label must not be blank.", nameof(label));
        Label = label ?? action.Title;
    }

    public UiActionDefinition Action { get; }
    public string Label { get; }
    public bool Invoke() => Action.TryExecute();
    internal UiVisualResolution VisualFor(bool enabled, UiInteractionSnapshot? interaction)
        => _stateVisuals?.ForHost(enabled, interaction, Visual) ?? Visual;
}

internal sealed class UiRouteButtonSceneNode : UiSceneNode
{
    public UiRouteButtonSceneNode(
        UiSymbolId id,
        UiSymbolId role,
        UiVisualResolution visual,
        string label,
        UiSymbolId route,
        bool isCurrent = false,
        UiSymbolId? icon = null)
        : base(id, UiSceneNodeKind.RouteButton, role, visual)
    {
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("A route button label is required.", nameof(label));
        if (!route.IsValid) throw new ArgumentException("A stable route ID is required.", nameof(route));
        Label = label;
        Route = route;
        IsCurrent = isCurrent;
        Icon = icon;
    }

    public string Label { get; }
    public UiSymbolId Route { get; }
    public bool IsCurrent { get; }
    public UiSymbolId? Icon { get; }
    internal const float IconExtent = 20;
    internal float IconSpace => Icon == null ? 0 : IconExtent + 4;
}

internal sealed class UiTextInputSceneNode : UiSceneNode
{
    private readonly IUiSemanticSource _source;
    private readonly string _text;
    private readonly bool _captured;

    public UiTextInputSceneNode(
        UiSymbolId id,
        UiSymbolId role,
        UiVisualResolution visual,
        string semanticName,
        IUiSemanticSource source,
        IUiSemanticSource? capturedSource = null)
        : base(id, UiSceneNodeKind.TextInput, role, visual)
    {
        if (string.IsNullOrWhiteSpace(semanticName))
            throw new ArgumentException("A semantic text-input name is required.", nameof(semanticName));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        SemanticName = semanticName;
        _captured = capturedSource is not null && !ReferenceEquals(source, capturedSource);
        _text = (capturedSource ?? source).UntypedValue?.ToString() ?? string.Empty;
    }

    public string SemanticName { get; }
    public string Text => _text;
    internal string CurrentText => _captured ? _text : _source.UntypedValue?.ToString() ?? string.Empty;

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
    private readonly Lazy<UiSceneStructure> _structure;

    public UiScene(
        UiSymbolId experience,
        string displayName,
        UiHostSceneNode root,
        UiSceneMeasurementContext measurementContext,
        Func<UiInteractionSnapshot, UiScene>? recomposePublication = null)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("An Experience display name is required.", nameof(displayName));
        Experience = experience;
        DisplayName = displayName;
        Root = root ?? throw new ArgumentNullException(nameof(root));
        MeasurementContext = measurementContext ?? throw new ArgumentNullException(nameof(measurementContext));
        RecomposePublication = recomposePublication;
        _structure = new Lazy<UiSceneStructure>(() => new UiSceneStructure(Root));
    }

    internal UiSceneStructure Structure => _structure.Value;

    public UiSymbolId Experience { get; }
    public string DisplayName { get; }
    public UiHostSceneNode Root { get; }
    public UiSceneMeasurementContext MeasurementContext { get; }
    internal Func<UiInteractionSnapshot, UiScene>? RecomposePublication { get; }
}
