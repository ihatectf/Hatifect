using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Tooling.Inspection;

namespace Hatifect.UI.DevTools;

/// <summary>
/// Host-free semantic projection of one immutable inspector capture. The eventual on-screen host
/// invokes this Experience through the ordinary registry/planner/scene pipeline.
/// </summary>
internal sealed class UiInspectorExperienceSession : IDisposable
{
    private readonly UiInspectorSnapshot _snapshot;
    private readonly Action _close;
    private readonly Action<UiSourceInspectionEntry> _revealSource;
    private readonly UiState<string> _query = new(string.Empty);
    private readonly UiState<string?> _details = new(null);
    private readonly UiSelectableCollectionState<UiInspectorNodeSnapshot> _nodes;
    private bool _disposed;

    public UiInspectorExperienceSession(
        UiInspectorSnapshot snapshot,
        UiSymbolId? experienceId = null,
        Action? close = null,
        Action<UiSourceInspectionEntry>? revealSource = null)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        if (experienceId is { } configuredId && !configuredId.IsValid)
            throw new ArgumentException("A valid Inspector Experience ID is required.", nameof(experienceId));
        _close = close ?? (() => { });
        _revealSource = revealSource ?? (_ => { });
        UiSymbolId id = experienceId ?? snapshot.Experience.Child("devtools/inspector");
        // An inspection entry represents a captured node; it is not that live scene node.
        // Reusing its ID aliases Terminal navigation/focus with a row of this collection.
        UiSymbolId RowId(UiInspectorNodeSnapshot node) => id.Child("entry/" + node.Id);
        UiInspectorNodeSnapshot[] initial = snapshot.Nodes.ToArray();
        _nodes = new UiSelectableCollectionState<UiInspectorNodeSnapshot>(
            initial,
            RowId,
            NodeLabel,
            initial.Length == 0 ? null : RowId(initial[0]),
            NodeSupportingText);
        _query.Changed += OnQueryChanged;
        _nodes.Changed += OnSelectionChanged;

        var reveal = new UiActionDefinition(
            id.Child("action/reveal-source"),
            "Reveal source",
            RevealSelectedSource,
            () => !_disposed && SelectedSource != null);
        var closeAction = new UiActionDefinition(
            id.Child("action/close"),
            "Close",
            () => _close(),
            () => !_disposed);
        Experience = new UiExperienceBuilder(id, $"Inspect {snapshot.DisplayName}")
            .Search("Search", _query)
            .Element("Nodes", _nodes, UiCapabilities.Browse, UiCapabilities.Select)
            .Inspect("Details", _details)
            .Actions("Actions", reveal, closeAction)
            .VisualRole("Node")
            .VisualRole("Details")
            .VisualRole("Action.Primary")
            .Build();
        SynchronizeDetails();
    }

    public UiExperienceDefinition Experience { get; }
    public UiState<string> Query => _query;
    public UiSelectableCollectionState<UiInspectorNodeSnapshot> Nodes => _nodes;
    public UiState<string?> Details => _details;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _query.Changed -= OnQueryChanged;
        _nodes.Changed -= OnSelectionChanged;
    }

    private UiInspectorNodeSnapshot? SelectedNode
        => _nodes.SelectedItemId is { } selected
            && ((IUiSemanticCollectionMetadata)_nodes).TryGetIndex(selected, out int index)
            ? _nodes.Value[index]
            : null;

    private UiSourceInspectionEntry? SelectedSource
        => SelectedNode?.VisualProperties
            .Select(property => property.SourceReveal)
            .FirstOrDefault(source => source != null);

    private void OnQueryChanged()
    {
        if (_disposed) return;
        string query = _query.Value.Trim();
        UiInspectorNodeSnapshot[] filtered = string.IsNullOrEmpty(query)
            ? _snapshot.Nodes.ToArray()
            : _snapshot.Nodes.Where(node => Matches(node, query)).ToArray();
        _nodes.Replace(filtered);
        if (_nodes.SelectedItemId == null && filtered.Length > 0)
            _nodes.TrySelect(_nodes.GetItem(0).Id);
        else
            SynchronizeDetails();
    }

    private void OnSelectionChanged()
    {
        if (!_disposed) SynchronizeDetails();
    }

    private void SynchronizeDetails()
        => _details.Value = SelectedNode is { } selected ? Describe(selected, _snapshot) : null;

    private void RevealSelectedSource()
    {
        UiSourceInspectionEntry? source = SelectedSource;
        if (!_disposed && source != null) _revealSource(source);
    }

    private static bool Matches(UiInspectorNodeSnapshot node, string query)
        => Contains(node.Id.ToString(), query) ||
           Contains(node.Kind, query) ||
           Contains(node.SemanticName, query) ||
           Contains(node.Role.ToString(), query);

    private static bool Contains(string? value, string query)
        => value?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string NodeLabel(UiInspectorNodeSnapshot node)
        => string.IsNullOrWhiteSpace(node.SemanticName)
            ? node.Kind
            : $"{node.Kind} · {node.SemanticName}";

    private static string NodeSupportingText(UiInspectorNodeSnapshot node)
        => $"{node.Id} · depth {node.Depth.ToString(CultureInfo.InvariantCulture)}";

    private static string Describe(UiInspectorNodeSnapshot node, UiInspectorSnapshot snapshot)
    {
        string states = node.ActiveStates.Count == 0
            ? "none"
            : string.Join(", ", node.ActiveStates.Select(state => state.ToString()));
        string semantic = node.Semantic.Value ?? "null";
        var lines = new List<string>
        {
            NodeLabel(node),
            $"Experience: {snapshot.Experience}",
            $"Planner: pattern={snapshot.Pattern}, host={snapshot.Host}, profile={snapshot.Profile}, decisions={snapshot.Decisions.Count.ToString(CultureInfo.InvariantCulture)}",
            $"ID: {node.Id}",
            $"Semantic: name={node.Semantic.Name ?? "none"}, value={semantic}, enabled={node.Semantic.Enabled?.ToString() ?? "n/a"}",
            $"Role: {node.Role} · states: {states}",
            FormattableString.Invariant(
                $"Bounds: {node.Bounds.X:0.##}, {node.Bounds.Y:0.##} · {node.Bounds.Width:0.##}×{node.Bounds.Height:0.##}"),
            FormattableString.Invariant(
                $"Content: {node.ContentBounds.X:0.##}, {node.ContentBounds.Y:0.##} · {node.ContentBounds.Width:0.##}×{node.ContentBounds.Height:0.##}"),
            FormattableString.Invariant(
                $"Clip: {node.Clip.X:0.##}, {node.Clip.Y:0.##} · {node.Clip.Width:0.##}×{node.Clip.Height:0.##}"),
            $"Input: focused={node.Focused}, hovered={node.Hovered}, pressed={node.Pressed}, focusable={node.Focusable}, hit-test={node.HitTestTarget}",
            $"Resolved properties: {node.VisualProperties.Count.ToString(CultureInfo.InvariantCulture)} · tokens={node.VisualProperties.Count(property => property.Token != null).ToString(CultureInfo.InvariantCulture)}",
            $"Invalidation: effects={snapshot.Invalidation.Effects}, layout={snapshot.Invalidation.LayoutChanged}, frame={snapshot.Invalidation.FrameChanged}, changed={snapshot.Invalidation.ChangedNodes.Count.ToString(CultureInfo.InvariantCulture)}",
            $"Lifecycle: {snapshot.Lifecycle.Phase}, layout builds={snapshot.Lifecycle.LayoutBuilds.ToString(CultureInfo.InvariantCulture)}, frame builds={snapshot.Lifecycle.FrameBuilds.ToString(CultureInfo.InvariantCulture)}",
            $"Capture diagnostics: {snapshot.Diagnostics.Count.ToString(CultureInfo.InvariantCulture)}"
        };
        if (node.Collection is { } collection)
        {
            lines.Add(FormattableString.Invariant(
                $"Collection: {collection.MaterializedItems.Count}/{collection.TotalCount} materialized, offset {collection.ScrollOffset:0.##}, columns {collection.Columns}"));
        }
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Internal composition boundary for a lazily captured, owned Inspector Terminal section. The
/// registered section identity stays stable even when the inspected Experience changes.
/// </summary>
internal sealed class UiInspectorSectionFactory
{
    private readonly UiSymbolId _id;
    private readonly Func<(UiInspectorSnapshot Snapshot, Action Close)> _capture;
    private readonly Action<UiSourceInspectionEntry>? _revealSource;

    public UiInspectorSectionFactory(
        UiSymbolId id,
        Func<(UiInspectorSnapshot Snapshot, Action Close)> capture,
        Action<UiSourceInspectionEntry>? revealSource = null)
    {
        if (!id.IsValid) throw new ArgumentException("A valid Inspector section ID is required.", nameof(id));
        _id = id;
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _revealSource = revealSource;
    }

    public (UiExperienceDefinition Experience, IDisposable Owner) Create()
    {
        (UiInspectorSnapshot snapshot, Action close) = _capture();
        if (snapshot == null)
            throw new InvalidOperationException("The Inspector capture factory returned a null snapshot.");
        if (close == null)
            throw new InvalidOperationException("The Inspector capture factory returned a null close action.");
        var session = new UiInspectorExperienceSession(
            snapshot,
            _id,
            close,
            _revealSource);
        return (session.Experience, session);
    }
}
