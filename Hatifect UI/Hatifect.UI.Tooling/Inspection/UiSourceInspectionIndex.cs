using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Tooling.Inspection;

public enum UiSourceInspectionKind
{
    Placement,
    Assignment
}

/// <summary>Typed source-reveal entry over bound semantic IR; no syntax text is retained.</summary>
public sealed record UiSourceInspectionEntry(
    UiSourceInspectionKind Kind,
    UiSymbolId Definition,
    UiDefinitionKind DefinitionKind,
    UiSymbolId? Target,
    UiSymbolId? Region,
    UiPropertySymbol? Property,
    UiSymbolId? Profile,
    UiSymbolId? State,
    UiBoundValue? Value,
    UiSourceProvenance Source);

/// <summary>
/// Immutable source index shared by inspectors, editors, generators, and build integrations.
/// Entries are owned by the compiled definitions and sorted deterministically by source position.
/// </summary>
public sealed class UiSourceInspectionIndex
{
    private readonly UiSourceInspectionEntry[] _entries;
    private readonly Dictionary<PlacementKey, UiSourceInspectionEntry> _placements = new();
    private readonly Dictionary<AssignmentKey, UiSourceInspectionEntry> _assignments = new();
    private readonly Dictionary<string, SourceIntervalNode> _sourceIntervals = new(StringComparer.Ordinal);

    public UiSourceInspectionIndex(IEnumerable<UiBoundDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        UiBoundDefinition[] snapshot = definitions
            .Select(definition => definition ??
                throw new ArgumentException("Source inspection definitions cannot contain null.", nameof(definitions)))
            .ToArray();
        UiSymbolId? duplicate = snapshot
            .GroupBy(definition => definition.Id)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is { } duplicateId)
            throw new InvalidOperationException($"Definition '{duplicateId}' is indexed more than once.");

        _entries = snapshot
            .SelectMany(EntriesFor)
            .OrderBy(entry => entry.Source.SourceName, StringComparer.Ordinal)
            .ThenBy(entry => entry.Source.Span.Start)
            .ThenBy(entry => entry.Source.Span.Length)
            .ThenBy(entry => entry.Kind)
            .ToArray();
        var sourceEntries = new Dictionary<string, List<IndexedInterval>>(StringComparer.Ordinal);
        for (int ordinal = 0; ordinal < _entries.Length; ordinal++)
        {
            UiSourceInspectionEntry entry = _entries[ordinal];
            if (entry.Kind == UiSourceInspectionKind.Placement && entry.Target is { } element)
            {
                _placements.TryAdd(new PlacementKey(entry.Definition, element), entry);
            }
            else if (entry.Kind == UiSourceInspectionKind.Assignment && entry.Property is { } property)
            {
                _assignments.TryAdd(new AssignmentKey(
                    entry.Definition,
                    entry.Target,
                    property.Id,
                    entry.Profile,
                    entry.State), entry);
            }

            if (IndexedInterval.TryCreate(entry, ordinal, out IndexedInterval interval))
            {
                if (!sourceEntries.TryGetValue(entry.Source.SourceName, out List<IndexedInterval>? entries))
                {
                    entries = new List<IndexedInterval>();
                    sourceEntries.Add(entry.Source.SourceName, entries);
                }
                entries.Add(interval);
            }
        }
        foreach ((string sourceName, List<IndexedInterval> entries) in sourceEntries)
            _sourceIntervals.Add(sourceName, SourceIntervalNode.Build(entries));
        Entries = Array.AsReadOnly(_entries);
    }

    public IReadOnlyList<UiSourceInspectionEntry> Entries { get; }

    public IReadOnlyList<UiSourceInspectionEntry> At(string sourceName, int offset)
        => At(sourceName, offset, out _);

    internal IReadOnlyList<UiSourceInspectionEntry> At(
        string sourceName,
        int offset,
        out int inspectedEntries)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
            throw new ArgumentException("A source name is required.", nameof(sourceName));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        inspectedEntries = 0;
        if (!_sourceIntervals.TryGetValue(sourceName, out SourceIntervalNode? root))
            return Array.AsReadOnly(Array.Empty<UiSourceInspectionEntry>());

        var matches = new List<IndexedInterval>();
        root.Query(offset, matches, ref inspectedEntries);
        matches.Sort(static (left, right) => left.Ordinal.CompareTo(right.Ordinal));
        var result = new UiSourceInspectionEntry[matches.Count];
        for (int index = 0; index < result.Length; index++) result[index] = matches[index].Entry;
        return Array.AsReadOnly(result);
    }

    public UiSourceInspectionEntry? RevealPlacement(UiSymbolId definition, UiSymbolId element)
        => _placements.TryGetValue(new PlacementKey(definition, element), out UiSourceInspectionEntry? entry)
            ? entry
            : null;

    public UiSourceInspectionEntry? RevealAssignment(
        UiSymbolId definition,
        UiSymbolId? target,
        UiSymbolId property,
        UiSymbolId? profile = null,
        UiSymbolId? state = null)
    {
        if (!property.IsValid) throw new ArgumentException("A stable property ID is required.", nameof(property));
        return _assignments.TryGetValue(
            new AssignmentKey(definition, target, property, profile, state),
            out UiSourceInspectionEntry? entry)
            ? entry
            : null;
    }

    private static IEnumerable<UiSourceInspectionEntry> EntriesFor(UiBoundDefinition definition)
    {
        if (definition is UiPresentationDefinition presentation)
        {
            foreach (UiPlacementIr placement in presentation.Placements)
            {
                yield return new UiSourceInspectionEntry(
                    UiSourceInspectionKind.Placement,
                    definition.Id,
                    definition.Kind,
                    placement.Element,
                    placement.Region,
                    Property: null,
                    Profile: null,
                    State: null,
                    Value: null,
                    placement.Provenance);
            }
            foreach (UiPropertyAssignmentIr assignment in presentation.Assignments)
                yield return Assignment(definition, assignment);
            yield break;
        }

        if (definition is UiVisualDefinition visual)
        {
            foreach (UiPropertyAssignmentIr recipe in visual.Recipes)
                yield return Assignment(definition, recipe);
            yield break;
        }

        throw new InvalidOperationException(
            $"Source inspection does not support definition type '{definition.GetType().Name}'.");
    }

    private static UiSourceInspectionEntry Assignment(
        UiBoundDefinition definition,
        UiPropertyAssignmentIr assignment)
        => new(
            UiSourceInspectionKind.Assignment,
            definition.Id,
            definition.Kind,
            assignment.Target,
            Region: null,
            assignment.Property,
            assignment.Profile,
            assignment.State,
            assignment.Value,
            assignment.Provenance);

    private readonly record struct PlacementKey(UiSymbolId Definition, UiSymbolId Element);

    private readonly record struct AssignmentKey(
        UiSymbolId Definition,
        UiSymbolId? Target,
        UiSymbolId Property,
        UiSymbolId? Profile,
        UiSymbolId? State);

    private readonly record struct IndexedInterval(
        UiSourceInspectionEntry Entry,
        int Ordinal,
        long Start,
        long EndExclusive)
    {
        public static bool TryCreate(
            UiSourceInspectionEntry entry,
            int ordinal,
            out IndexedInterval interval)
        {
            if (string.IsNullOrWhiteSpace(entry.Source.SourceName) || entry.Source.Span.Length < 0)
            {
                interval = default;
                return false;
            }

            long start = entry.Source.Span.Start;
            long end = entry.Source.Span.Length == 0
                ? start + 1
                : start + entry.Source.Span.Length;
            interval = new IndexedInterval(entry, ordinal, start, end);
            return true;
        }
    }

    private sealed class SourceIntervalNode
    {
        private readonly long _center;
        private readonly IndexedInterval[] _byStart;
        private readonly IndexedInterval[] _byEnd;
        private readonly SourceIntervalNode? _left;
        private readonly SourceIntervalNode? _right;

        private SourceIntervalNode(
            long center,
            IndexedInterval[] byStart,
            IndexedInterval[] byEnd,
            SourceIntervalNode? left,
            SourceIntervalNode? right)
        {
            _center = center;
            _byStart = byStart;
            _byEnd = byEnd;
            _left = left;
            _right = right;
        }

        public static SourceIntervalNode Build(List<IndexedInterval> intervals)
        {
            if (intervals.Count == 0)
                throw new ArgumentException("A source interval node requires at least one entry.", nameof(intervals));

            intervals.Sort(static (left, right) =>
            {
                int start = left.Start.CompareTo(right.Start);
                return start != 0 ? start : left.Ordinal.CompareTo(right.Ordinal);
            });
            long center = intervals[intervals.Count / 2].Start;
            var left = new List<IndexedInterval>();
            var overlap = new List<IndexedInterval>();
            var right = new List<IndexedInterval>();
            foreach (IndexedInterval interval in intervals)
            {
                if (interval.EndExclusive <= center) left.Add(interval);
                else if (interval.Start > center) right.Add(interval);
                else overlap.Add(interval);
            }

            IndexedInterval[] byStart = overlap.ToArray();
            IndexedInterval[] byEnd = overlap.ToArray();
            Array.Sort(byEnd, static (leftInterval, rightInterval) =>
            {
                int end = rightInterval.EndExclusive.CompareTo(leftInterval.EndExclusive);
                return end != 0 ? end : leftInterval.Ordinal.CompareTo(rightInterval.Ordinal);
            });
            return new SourceIntervalNode(
                center,
                byStart,
                byEnd,
                left.Count == 0 ? null : Build(left),
                right.Count == 0 ? null : Build(right));
        }

        public void Query(
            long offset,
            List<IndexedInterval> matches,
            ref int inspectedEntries)
        {
            if (offset < _center)
            {
                foreach (IndexedInterval interval in _byStart)
                {
                    inspectedEntries++;
                    if (interval.Start > offset) break;
                    matches.Add(interval);
                }
                _left?.Query(offset, matches, ref inspectedEntries);
                return;
            }

            if (offset > _center)
            {
                foreach (IndexedInterval interval in _byEnd)
                {
                    inspectedEntries++;
                    if (interval.EndExclusive <= offset) break;
                    matches.Add(interval);
                }
                _right?.Query(offset, matches, ref inspectedEntries);
                return;
            }

            inspectedEntries += _byStart.Length;
            matches.AddRange(_byStart);
        }
    }
}
