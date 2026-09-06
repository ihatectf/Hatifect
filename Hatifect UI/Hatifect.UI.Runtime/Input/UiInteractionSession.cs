using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Visual;
using Hatifect.UI.Runtime.Visual.Resolution;

namespace Hatifect.UI.Runtime.Input;

internal enum UiNavigationDirection
{
    Next,
    Previous,
    Up,
    Down,
    Left,
    Right
}

internal enum UiTextEditAction
{
    Left,
    Right,
    Home,
    End,
    Backspace,
    Delete,
    SelectAll
}

internal sealed record UiTextEditingSnapshot(UiSymbolId Input, int Anchor, int Caret)
{
    public int SelectionStart => Math.Min(Anchor, Caret);
    public int SelectionLength => Math.Abs(Anchor - Caret);
}

internal static class UiTextEditingGeometry
{
    public static float PrefixWidth(
        string text,
        int length,
        UiTypography typography,
        IUiTextMetrics textMetrics)
        => length <= 0
            ? 0
            : textMetrics.Measure(
                text[..Math.Clamp(length, 0, text.Length)],
                typography,
                float.MaxValue,
                UiTextOverflow.Clip).Width;

    public static float HorizontalScroll(
        string text,
        int caret,
        float contentWidth,
        UiTypography typography,
        IUiTextMetrics textMetrics)
    {
        float totalWidth = PrefixWidth(text, text.Length, typography, textMetrics);
        float caretWidth = PrefixWidth(text, caret, typography, textMetrics);
        return Math.Min(
            Math.Max(0, totalWidth - contentWidth),
            Math.Max(0, caretWidth - Math.Max(0, contentWidth - 2)));
    }
}

internal sealed record UiInteractionSnapshot(
    UiSymbolId? Hovered = null,
    UiSymbolId? Pressed = null,
    UiSymbolId? Focused = null,
    UiTextEditingSnapshot? TextEditing = null)
{
    public IReadOnlyList<UiVisualStateRef> StatesFor(UiSymbolId node, bool enabled = true)
    {
        var result = new List<UiVisualStateRef>(4);
        if (Hovered == node) result.Add(UiVisualStates.Hover);
        if (Focused == node) result.Add(UiVisualStates.Focused);
        if (Pressed == node) result.Add(UiVisualStates.Pressed);
        if (!enabled) result.Add(UiVisualStates.Disabled);
        return result;
    }
}

internal sealed record UiInteractionDiagnosticSnapshot(
    UiSymbolId? Hovered,
    UiSymbolId? Pressed,
    UiSymbolId? Focused,
    UiTextEditingSnapshot? TextEditing,
    IReadOnlyList<UiSymbolId> FocusOrder,
    IReadOnlyList<UiSymbolId> HitTestTargets);

internal sealed record UiInteractionUpdate(
    bool Consumed,
    bool StateChanged,
    bool ActionInvoked = false,
    UiSymbolId? Route = null,
    bool DismissRequested = false,
    bool TextChanged = false,
    bool TextEditingChanged = false);

/// <summary>Owns pointer/controller/focus state for exactly one host scene lifetime.</summary>
internal sealed class UiInteractionSession
{
    private UiScene _scene;
    private UiLayoutSnapshot _layout;
    private Dictionary<UiSymbolId, UiSceneNode> _nodes;
    private Dictionary<UiSymbolId, UiCollectionItemTarget> _collectionItems;
    private UiSymbolId[] _focusable;
    private UiSymbolId[] _focusGroups;
    private CollectionFocus? _collectionFocus;
    private readonly IUiTextMetrics? _textMetrics;
    private readonly Func<long>? _beforeMutation;

    public UiInteractionSession(
        UiScene scene,
        UiLayoutSnapshot layout,
        UiInteractionSnapshot? snapshot = null,
        IUiTextMetrics? textMetrics = null,
        Func<long>? beforeMutation = null)
    {
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _nodes = Index(scene);
        _collectionItems = IndexCollectionItems(_nodes, layout);
        _focusable = Focusable(scene, layout).ToArray();
        _focusGroups = FocusGroups(scene, _focusable).ToArray();
        _textMetrics = textMetrics;
        _beforeMutation = beforeMutation;
        Snapshot = Reconcile(snapshot ?? new UiInteractionSnapshot());
    }

    public UiInteractionSnapshot Snapshot { get; private set; }

    internal UiInteractionDiagnosticSnapshot CaptureDiagnostics()
    {
        UiSymbolId[] focusOrder = _focusable.ToArray();
        UiSymbolId[] targets = HitTestTargets(focusOrder, _layout).ToArray();
        return new UiInteractionDiagnosticSnapshot(
            Snapshot.Hovered,
            Snapshot.Pressed,
            Snapshot.Focused,
            Snapshot.TextEditing,
            Array.AsReadOnly(focusOrder),
            Array.AsReadOnly(targets));
    }

    public void Reconcile(UiScene scene, UiLayoutSnapshot layout)
    {
        _beforeMutation?.Invoke();
        ReconcileCore(scene, layout);
    }

    internal UiInteractionSession PrepareReconcile(UiScene scene, UiLayoutSnapshot layout)
    {
        // Indexes are replaced, never changed in place. Preserve the offscreen focus hint as well
        // as the immutable snapshot; candidate callbacks cannot mutate the accepted session.
        var candidate = (UiInteractionSession)MemberwiseClone();
        candidate.ReconcileCore(scene, layout);
        return candidate;
    }

    internal void CommitReconcile(UiInteractionSession candidate)
    {
        _scene = candidate._scene;
        _layout = candidate._layout;
        _nodes = candidate._nodes;
        _collectionItems = candidate._collectionItems;
        _focusable = candidate._focusable;
        _focusGroups = candidate._focusGroups;
        _collectionFocus = candidate._collectionFocus;
        Snapshot = candidate.Snapshot;
    }

    private void ReconcileCore(UiScene scene, UiLayoutSnapshot layout)
    {
        // Retain the old index before replacing the visible window. An absent visible target
        // may still be a focused item in the full immutable collection.
        CollectionFocus? previous = Snapshot.Focused is { } focused &&
            _collectionItems.TryGetValue(focused, out UiCollectionItemTarget? target)
                ? new CollectionFocus(target.Collection.Id, focused, target.Item.Index)
                : _collectionFocus;
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _nodes = Index(scene);
        _collectionItems = IndexCollectionItems(_nodes, layout);
        _focusable = Focusable(scene, layout).ToArray();
        _focusGroups = FocusGroups(scene, _focusable).ToArray();
        Snapshot = Reconcile(Snapshot, previous);
    }

    public UiInteractionUpdate MovePointer(UiPoint point)
    {
        _beforeMutation?.Invoke();
        UiSymbolId? hit = HitTest(point);
        UiSymbolId? hovered = hit;
        if (hovered == Snapshot.Hovered) return new UiInteractionUpdate(hit != null, StateChanged: false);
        Snapshot = Snapshot with { Hovered = hovered };
        return new UiInteractionUpdate(hit != null, StateChanged: true);
    }

    public UiInteractionUpdate PressPointer(UiPoint point)
    {
        _beforeMutation?.Invoke();
        UiSymbolId? hit = HitTest(point);
        if (hit == null)
        {
            bool dismiss = !IsInsideHost(point) && _scene.Root.Policy.Dismiss == UiDismissPolicy.OutsideOrEscape;
            bool changed = Snapshot.Pressed != null || Snapshot.Hovered != null;
            Snapshot = Snapshot with { Hovered = null, Pressed = null };
            return new UiInteractionUpdate(dismiss, changed, DismissRequested: dismiss);
        }

        bool stateChanged = Snapshot.Pressed != hit || Snapshot.Focused != hit || Snapshot.Hovered != hit;
        Snapshot = Snapshot with
        {
            Hovered = hit,
            Pressed = hit,
            Focused = hit,
            TextEditing = TextEditingForPointer(hit.Value, point)
        };
        _collectionFocus = LocateCollectionFocus(hit);
        return new UiInteractionUpdate(Consumed: true, StateChanged: stateChanged);
    }

    public UiInteractionUpdate ReleasePointer(UiPoint point)
    {
        _beforeMutation?.Invoke();
        UiSymbolId? hit = HitTest(point);
        UiSymbolId? pressed = Snapshot.Pressed;
        bool activate = pressed != null && hit == pressed;
        bool stateChanged = pressed != null || Snapshot.Hovered != hit;
        Snapshot = Snapshot with { Hovered = hit, Pressed = null };
        if (!activate || hit == null)
            return new UiInteractionUpdate(hit != null, StateChanged: stateChanged);
        return Activate(hit.Value, stateChanged: true);
    }

    public UiInteractionUpdate MoveFocus(UiNavigationDirection direction)
    {
        _beforeMutation?.Invoke();
        UiSymbolId? next = FindFocus(direction);
        if (next == null) return new UiInteractionUpdate(Consumed: false, StateChanged: false);
        bool changed = Snapshot.Focused != next;
        Snapshot = Snapshot with
        {
            Focused = next,
            Pressed = null,
            TextEditing = TextEditingForFocus(next.Value)
        };
        _collectionFocus = LocateCollectionFocus(next);
        return new UiInteractionUpdate(Consumed: true, StateChanged: changed);
    }

    internal bool TryGetFocusedCollectionItem(
        out UiCollectionSceneNode collection, out UiSymbolId item, out int index)
    {
        CollectionFocus? focus = LocateCollectionFocus(Snapshot.Focused);
        if (focus is { Item: { } stable } &&
            _nodes.TryGetValue(focus.Collection, out UiSceneNode? node) && node is UiCollectionSceneNode found)
        {
            collection = found;
            item = stable;
            index = focus.Index;
            return true;
        }
        collection = null!;
        item = default;
        index = -1;
        return false;
    }

    public UiInteractionUpdate Submit()
    {
        _beforeMutation?.Invoke();
        if (Snapshot.Focused is not { } id ||
            (!_nodes.ContainsKey(id) && !_collectionItems.ContainsKey(id)))
            return new UiInteractionUpdate(Consumed: false, StateChanged: false);
        return Activate(id, stateChanged: false);
    }

    public UiInteractionUpdate Cancel()
    {
        _beforeMutation?.Invoke();
        bool dismiss = _scene.Root.Policy.Dismiss is UiDismissPolicy.Escape or UiDismissPolicy.OutsideOrEscape;
        return new UiInteractionUpdate(dismiss, StateChanged: false, DismissRequested: dismiss);
    }

    public UiInteractionUpdate ReplaceText(string? text)
    {
        _beforeMutation?.Invoke();
        if (Snapshot.Focused is not { } id ||
            !_nodes.TryGetValue(id, out UiSceneNode? node) ||
            node is not UiTextInputSceneNode input)
            return new UiInteractionUpdate(Consumed: false, StateChanged: false);
        string next = text ?? string.Empty;
        bool changed = !string.Equals(input.CurrentText, next, StringComparison.Ordinal);
        bool accepted = input.TrySetText(next);
        if (accepted)
            Snapshot = Snapshot with { TextEditing = new UiTextEditingSnapshot(id, next.Length, next.Length) };
        return new UiInteractionUpdate(
            accepted,
            StateChanged: false,
            TextChanged: accepted && changed,
            TextEditingChanged: accepted);
    }

    public UiInteractionUpdate InsertText(string? text)
    {
        _beforeMutation?.Invoke();
        if (!TryGetFocusedTextInput(out UiSymbolId id, out UiTextInputSceneNode input))
            return new UiInteractionUpdate(false, false);
        string inserted = (text ?? string.Empty)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
        if (inserted.Length == 0) return new UiInteractionUpdate(false, false);
        UiTextEditingSnapshot editing = Editing(id, input.CurrentText.Length);
        string next = input.CurrentText
            .Remove(editing.SelectionStart, editing.SelectionLength)
            .Insert(editing.SelectionStart, inserted);
        int caret = editing.SelectionStart + inserted.Length;
        bool accepted = input.TrySetText(next);
        if (accepted)
            Snapshot = Snapshot with { TextEditing = new UiTextEditingSnapshot(id, caret, caret) };
        return new UiInteractionUpdate(
            accepted,
            StateChanged: false,
            TextChanged: accepted,
            TextEditingChanged: accepted);
    }

    public UiInteractionUpdate EditText(UiTextEditAction action, bool extendSelection = false)
    {
        _beforeMutation?.Invoke();
        if (!TryGetFocusedTextInput(out UiSymbolId id, out UiTextInputSceneNode input))
            return new UiInteractionUpdate(false, false);
        string text = input.CurrentText;
        UiTextEditingSnapshot editing = Editing(id, text.Length);
        if (action is UiTextEditAction.Backspace or UiTextEditAction.Delete)
            return DeleteText(input, editing, action == UiTextEditAction.Backspace);

        int caret = action switch
        {
            UiTextEditAction.Left => PreviousCaret(text, editing.Caret),
            UiTextEditAction.Right => NextCaret(text, editing.Caret),
            UiTextEditAction.Home => 0,
            UiTextEditAction.End => text.Length,
            UiTextEditAction.SelectAll => text.Length,
            _ => editing.Caret
        };
        int anchor = action == UiTextEditAction.SelectAll
            ? 0
            : extendSelection ? editing.Anchor : caret;
        var next = new UiTextEditingSnapshot(id, anchor, caret);
        bool changed = next != editing;
        Snapshot = Snapshot with { TextEditing = next };
        return new UiInteractionUpdate(
            Consumed: true,
            StateChanged: false,
            TextEditingChanged: changed);
    }

    private UiInteractionUpdate Activate(UiSymbolId id, bool stateChanged)
    {
        if (_collectionItems.TryGetValue(id, out UiCollectionItemTarget? item))
        {
            bool changed = !item.Collection.IsSelected(item.Item.Item.Id);
            bool accepted = item.Collection.TrySelect(item.Item.Item.Id);
            return new UiInteractionUpdate(
                Consumed: accepted,
                StateChanged: stateChanged || accepted && changed);
        }
        if (!_nodes.TryGetValue(id, out UiSceneNode? node))
            return new UiInteractionUpdate(Consumed: false, StateChanged: stateChanged);
        return node switch
        {
            UiButtonSceneNode button => ActivateButton(button, stateChanged),
            UiRouteButtonSceneNode route => new UiInteractionUpdate(
                Consumed: true,
                StateChanged: stateChanged,
                Route: route.Route),
            UiTextInputSceneNode => new UiInteractionUpdate(Consumed: true, StateChanged: stateChanged),
            _ => new UiInteractionUpdate(Consumed: false, StateChanged: stateChanged)
        };
    }

    private UiInteractionUpdate ActivateButton(UiButtonSceneNode button, bool stateChanged)
    {
        bool invoked = button.Action.TryExecute(_beforeMutation);
        return new UiInteractionUpdate(invoked, StateChanged: stateChanged, ActionInvoked: invoked);
    }

    private UiSymbolId? HitTest(UiPoint point)
        => HitTest(_scene.Root, point);

    private bool IsInsideHost(UiPoint point)
        => _layout.TryGetEntry(_scene.Root.Id, out UiLayoutEntry? entry) &&
           entry != null && entry.Bounds.Contains(point) && entry.Clip.Contains(point);

    private UiSymbolId? HitTest(UiSceneNode node, UiPoint point)
    {
        if (!_layout.TryGetEntry(node.Id, out UiLayoutEntry? entry) || entry == null ||
            !entry.Bounds.Contains(point) || !entry.Clip.Contains(point))
            return null;

        for (int index = node.Children.Count - 1; index >= 0; index--)
        {
            UiSymbolId? child = HitTest(node.Children[index], point);
            if (child != null) return child;
        }
        if (node is UiCollectionSceneNode { IsSelectable: true } collection &&
            _layout.TryGetCollection(collection.Id, out UiCollectionLayoutWindow? window) &&
            window != null)
        {
            for (int index = window.Items.Count - 1; index >= 0; index--)
            {
                UiVirtualizedItemLayout item = window.Items[index];
                if (item.Bounds.Contains(point) && item.Clip.Contains(point)) return item.Node;
            }
        }
        return IsFocusable(node) ? node.Id : null;
    }

    private UiSymbolId? FindFocus(UiNavigationDirection direction)
    {
        if (_focusGroups.Length == 0) return null;
        if (direction is UiNavigationDirection.Next or UiNavigationDirection.Previous)
            return Sequential(direction);
        if (TryGetFocusedCollectionItem(out UiCollectionSceneNode collection, out _, out int index) &&
            _layout.TryGetCollection(collection.Id, out UiCollectionLayoutWindow? window) && window != null)
        {
            int next = direction switch
            {
                UiNavigationDirection.Up => index - window.Columns,
                UiNavigationDirection.Down => index + window.Columns,
                UiNavigationDirection.Left when index % window.Columns > 0 => index - 1,
                UiNavigationDirection.Right when index % window.Columns < window.Columns - 1 => index + 1,
                _ => -1
            };
            if ((uint)next < (uint)collection.Count) return FocusItem(collection, next);
        }
        int current = Snapshot.Focused is { } id
            ? Array.FindIndex(_focusable, node => node == id)
            : -1;
        if (current < 0) return Sequential(direction is UiNavigationDirection.Left or UiNavigationDirection.Up
            ? UiNavigationDirection.Previous : UiNavigationDirection.Next);

        UiSymbolId origin = _focusable[current];
        if (!TryGetBounds(origin, out UiRect originBounds)) return null;
        UiPoint center = Center(originBounds);
        UiSymbolId? winner = null;
        float winnerScore = float.PositiveInfinity;
        foreach (UiSymbolId candidate in _focusable)
        {
            if (candidate == origin || !TryGetBounds(candidate, out UiRect candidateBounds))
                continue;
            UiPoint target = Center(candidateBounds);
            float dx = target.X - center.X;
            float dy = target.Y - center.Y;
            if (!InDirection(direction, dx, dy)) continue;
            float primary = direction is UiNavigationDirection.Left or UiNavigationDirection.Right ? Math.Abs(dx) : Math.Abs(dy);
            float secondary = direction is UiNavigationDirection.Left or UiNavigationDirection.Right ? Math.Abs(dy) : Math.Abs(dx);
            float score = primary * 1024 + secondary;
            if (score >= winnerScore) continue;
            winner = candidate;
            winnerScore = score;
        }
        return winner ?? (_scene.Root.Policy.Focus == UiFocusScopePolicy.Trapped
            ? Sequential(direction is UiNavigationDirection.Left or UiNavigationDirection.Up
                ? UiNavigationDirection.Previous
                : UiNavigationDirection.Next)
            : null);
    }

    private UiSymbolId? Sequential(UiNavigationDirection direction)
    {
        bool forward = direction == UiNavigationDirection.Next;
        int step = forward ? 1 : -1;
        CollectionFocus? focus = LocateCollectionFocus(Snapshot.Focused);
        UiSymbolId? group = focus?.Collection ?? Snapshot.Focused;
        if (focus is { Item: not null } && _nodes[focus.Collection] is UiCollectionSceneNode collection)
        {
            int itemIndex = focus.Index + step;
            if ((uint)itemIndex < (uint)collection.Count) return FocusItem(collection, itemIndex);
        }
        int current = group is { } id ? Array.IndexOf(_focusGroups, id) : -1;
        int next = current < 0 ? (forward ? 0 : _focusGroups.Length - 1) : current + step;
        if (next < 0 || next >= _focusGroups.Length)
        {
            if (_scene.Root.Policy.Focus != UiFocusScopePolicy.Trapped) return null;
            next = forward ? 0 : _focusGroups.Length - 1;
        }
        if (_focusGroups.Length == 0) return null;
        UiSymbolId candidate = _focusGroups[next];
        return _nodes[candidate] is UiCollectionSceneNode { Count: > 0 } items
            ? FocusItem(items, forward ? 0 : items.Count - 1)
            : candidate;
    }

    private UiSymbolId FocusItem(UiCollectionSceneNode collection, int index)
    {
        UiSymbolId item = collection.ItemAt(index).Id;
        // Uniform external sources may have random access without a reverse-ID index.
        // Keep the index already proved by navigation for lookup and viewport reveal.
        _collectionFocus = new CollectionFocus(collection.Id, item, index);
        return item;
    }

    private UiInteractionSnapshot Reconcile(UiInteractionSnapshot snapshot, CollectionFocus? previous = null)
    {
        UiSymbolId? focused = Retain(snapshot.Focused, focusableOnly: true);
        if (previous != null &&
            _nodes.TryGetValue(previous.Collection, out UiSceneNode? owner) &&
            owner is UiCollectionSceneNode { IsSelectable: true } collection)
        {
            focused = previous.Item is { } item && collection.TryGetIndex(item, previous.Index, out _)
                ? item
                : collection.Count == 0 ? collection.Id
                : FocusItem(collection, Math.Min(previous.Index, collection.Count - 1));
        }
        else if (focused == null && LocateCollectionFocus(snapshot.Focused) is { Item: { } retained })
            focused = retained;
        _collectionFocus = LocateCollectionFocus(focused);
        UiTextEditingSnapshot? editing = focused is { } id &&
            _nodes.TryGetValue(id, out UiSceneNode? node) &&
            node is UiTextInputSceneNode input
                ? ClampEditing(snapshot.TextEditing, id, input.CurrentText)
                : null;
        return new UiInteractionSnapshot(
            Retain(snapshot.Hovered, focusableOnly: true),
            Retain(snapshot.Pressed, focusableOnly: true),
            focused,
            editing);
    }

    private UiTextEditingSnapshot? TextEditingForPointer(UiSymbolId id, UiPoint point)
    {
        if (!_nodes.TryGetValue(id, out UiSceneNode? node) || node is not UiTextInputSceneNode input)
            return null;
        string text = input.CurrentText;
        int caret = text.Length;
        if (_textMetrics != null &&
            _layout.TryGetEntry(id, out UiLayoutEntry? entry) && entry != null &&
            TryTypography(input, out UiTypography typography))
        {
            float scroll = Snapshot.TextEditing is { } editing && editing.Input == id
                ? UiTextEditingGeometry.HorizontalScroll(
                    text,
                    editing.Caret,
                    entry.ContentBounds.Width,
                    typography,
                    _textMetrics)
                : 0;
            float local = Math.Max(0, point.X - entry.ContentBounds.X + scroll);
            int low = 0;
            int high = text.Length;
            while (low < high)
            {
                int mid = NormalizeCaret(text, (low + high + 1) / 2);
                if (mid <= low) mid = NextCaret(text, low);
                float width = UiTextEditingGeometry.PrefixWidth(
                    text, mid, typography, _textMetrics);
                if (width <= local) low = mid;
                else high = PreviousCaret(text, mid);
            }
            caret = NormalizeCaret(text, low);
        }
        return new UiTextEditingSnapshot(id, caret, caret);
    }

    private UiTextEditingSnapshot? TextEditingForFocus(UiSymbolId id)
    {
        if (!_nodes.TryGetValue(id, out UiSceneNode? node) || node is not UiTextInputSceneNode input)
            return null;
        if (Snapshot.TextEditing is { } existing && existing.Input == id)
            return ClampEditing(existing, id, input.CurrentText);
        int caret = input.CurrentText.Length;
        return new UiTextEditingSnapshot(id, caret, caret);
    }

    private UiInteractionUpdate DeleteText(
        UiTextInputSceneNode input,
        UiTextEditingSnapshot editing,
        bool backward)
    {
        string text = input.CurrentText;
        int start = editing.SelectionStart;
        int length = editing.SelectionLength;
        if (length == 0)
        {
            if (backward)
            {
                if (editing.Caret <= 0) return new UiInteractionUpdate(true, false);
                start = PreviousCaret(text, editing.Caret);
                length = editing.Caret - start;
            }
            else
            {
                if (editing.Caret >= text.Length) return new UiInteractionUpdate(true, false);
                int next = NextCaret(text, editing.Caret);
                length = next - editing.Caret;
            }
        }
        string value = text.Remove(start, length);
        bool accepted = input.TrySetText(value);
        if (accepted)
            Snapshot = Snapshot with { TextEditing = new UiTextEditingSnapshot(editing.Input, start, start) };
        return new UiInteractionUpdate(
            accepted,
            StateChanged: false,
            TextChanged: accepted,
            TextEditingChanged: accepted);
    }

    private bool TryGetFocusedTextInput(out UiSymbolId id, out UiTextInputSceneNode input)
    {
        if (Snapshot.Focused is { } focused &&
            _nodes.TryGetValue(focused, out UiSceneNode? node) &&
            node is UiTextInputSceneNode textInput)
        {
            id = focused;
            input = textInput;
            return true;
        }
        id = default;
        input = null!;
        return false;
    }

    private UiTextEditingSnapshot Editing(UiSymbolId id, int length)
        => ClampEditing(Snapshot.TextEditing, id, length);

    private static UiTextEditingSnapshot ClampEditing(
        UiTextEditingSnapshot? editing,
        UiSymbolId id,
        int length)
    {
        if (editing == null || editing.Input != id)
            return new UiTextEditingSnapshot(id, length, length);
        return editing with
        {
            Anchor = Math.Clamp(editing.Anchor, 0, length),
            Caret = Math.Clamp(editing.Caret, 0, length)
        };
    }

    private static UiTextEditingSnapshot ClampEditing(
        UiTextEditingSnapshot? editing,
        UiSymbolId id,
        string text)
    {
        UiTextEditingSnapshot clamped = ClampEditing(editing, id, text.Length);
        return clamped with
        {
            Anchor = NormalizeCaret(text, clamped.Anchor),
            Caret = NormalizeCaret(text, clamped.Caret)
        };
    }

    private static bool TryTypography(UiTextInputSceneNode input, out UiTypography typography)
    {
        foreach (UiResolvedVisualProperty property in input.Visual.Properties)
        {
            if (!string.Equals(property.Property.Name, "typography", StringComparison.Ordinal)) continue;
            if (property.Value is not UiTypography value) break;
            typography = value;
            return true;
        }
        typography = null!;
        return false;
    }

    private static int NormalizeCaret(string text, int index)
    {
        int value = Math.Clamp(index, 0, text.Length);
        if (value > 0 && value < text.Length &&
            char.IsLowSurrogate(text[value]) && char.IsHighSurrogate(text[value - 1]))
            value--;
        return value;
    }

    private static int PreviousCaret(string text, int index)
    {
        int value = Math.Clamp(index, 0, text.Length);
        if (value <= 0) return 0;
        value--;
        if (value > 0 && char.IsLowSurrogate(text[value]) && char.IsHighSurrogate(text[value - 1]))
            value--;
        return value;
    }

    private static int NextCaret(string text, int index)
    {
        int value = Math.Clamp(index, 0, text.Length);
        if (value >= text.Length) return text.Length;
        if (char.IsHighSurrogate(text[value]) && value + 1 < text.Length && char.IsLowSurrogate(text[value + 1]))
            return value + 2;
        return value + 1;
    }

    private UiSymbolId? Retain(UiSymbolId? id, bool focusableOnly)
    {
        if (id is not { } value) return null;
        if (!focusableOnly && (_nodes.ContainsKey(value) || _collectionItems.ContainsKey(value))) return value;
        return _focusable.Contains(value) ? value : null;
    }

    private bool TryGetBounds(UiSymbolId id, out UiRect bounds)
    {
        if (_layout.TryGetEntry(id, out UiLayoutEntry? entry) && entry != null)
        {
            bounds = entry.Bounds;
            return true;
        }
        if (_collectionItems.TryGetValue(id, out UiCollectionItemTarget? item))
        {
            bounds = item.Item.Bounds;
            return true;
        }
        bounds = default;
        return false;
    }

    private static bool InDirection(UiNavigationDirection direction, float dx, float dy)
        => direction switch
        {
            UiNavigationDirection.Left => dx < 0,
            UiNavigationDirection.Right => dx > 0,
            UiNavigationDirection.Up => dy < 0,
            UiNavigationDirection.Down => dy > 0,
            _ => false
        };

    private static UiPoint Center(UiRect bounds)
        => new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);

    private static Dictionary<UiSymbolId, UiSceneNode> Index(UiScene scene)
        => Nodes(scene.Root).ToDictionary(node => node.Id);

    private static IEnumerable<UiSymbolId> Focusable(UiScene scene, UiLayoutSnapshot layout)
        => Focusable(scene.Root, layout);

    private static IEnumerable<UiSymbolId> HitTestTargets(
        IReadOnlyList<UiSymbolId> focusable,
        UiLayoutSnapshot layout)
    {
        var visible = new HashSet<UiSymbolId>();
        foreach (UiSymbolId id in focusable)
        {
            if (layout.TryGetEntry(id, out UiLayoutEntry? entry) && entry != null)
            {
                if (entry.Bounds.Width > 0 && entry.Bounds.Height > 0 &&
                    entry.Clip.Width > 0 && entry.Clip.Height > 0)
                    visible.Add(id);
            }
        }
        foreach (UiCollectionLayoutWindow window in layout.CollectionWindows)
        foreach (UiVirtualizedItemLayout item in window.Items)
            if (item.Bounds.Width > 0 && item.Bounds.Height > 0 &&
                item.Clip.Width > 0 && item.Clip.Height > 0)
                visible.Add(item.Node);
        foreach (UiSymbolId id in focusable)
            if (visible.Contains(id)) yield return id;
    }

    private static IEnumerable<UiSymbolId> Focusable(UiSceneNode node, UiLayoutSnapshot layout)
    {
        if (IsFocusable(node)) yield return node.Id;
        if (node is UiCollectionSceneNode { IsSelectable: true } collection &&
            layout.TryGetCollection(collection.Id, out UiCollectionLayoutWindow? window) &&
            window != null)
        {
            foreach (UiVirtualizedItemLayout item in window.Items)
                if (item.Clip.Width > 0 && item.Clip.Height > 0)
                    yield return item.Node;
        }
        foreach (UiSceneNode child in node.Children)
        foreach (UiSymbolId descendant in Focusable(child, layout))
            yield return descendant;
    }

    private static bool IsFocusable(UiSceneNode node)
        => node switch
        {
            UiButtonSceneNode button => button.Action.CanExecute,
            UiRouteButtonSceneNode => true,
            UiTextInputSceneNode => true,
            UiCollectionSceneNode { IsSelectable: true, Count: 0 } => true,
            _ => false
        };

    private static IEnumerable<UiSymbolId> FocusGroups(UiScene scene, UiSymbolId[] focusable)
    {
        // Reuse the resolved eligibility; asking CanExecute again could observe another owner
        // state and disagree with this same reconciliation's visible focus order.
        var eligible = new HashSet<UiSymbolId>(focusable);
        foreach (UiSceneNode node in Nodes(scene.Root))
            if (node is UiCollectionSceneNode { IsSelectable: true } || eligible.Contains(node.Id))
                yield return node.Id;
    }

    private CollectionFocus? LocateCollectionFocus(UiSymbolId? id)
    {
        if (id is not { } stable) return null;
        if (_nodes.TryGetValue(stable, out UiSceneNode? owner) &&
            owner is UiCollectionSceneNode { IsSelectable: true, Count: 0 })
            return new CollectionFocus(stable, null, 0);
        if (_collectionItems.TryGetValue(stable, out UiCollectionItemTarget? visible))
            return new CollectionFocus(visible.Collection.Id, stable, visible.Item.Index);
        foreach (UiSceneNode node in _nodes.Values)
        {
            if (node is not UiCollectionSceneNode { IsSelectable: true } collection) continue;
            int hint = _collectionFocus is { } old && old.Collection == collection.Id && old.Item == stable
                ? old.Index : -1;
            if (collection.TryGetIndex(stable, hint, out int index))
                return new CollectionFocus(collection.Id, stable, index);
        }
        return null;
    }

    private sealed record CollectionFocus(UiSymbolId Collection, UiSymbolId? Item, int Index);

    private static IEnumerable<UiSceneNode> Nodes(UiSceneNode node)
    {
        yield return node;
        foreach (UiSceneNode child in node.Children)
        foreach (UiSceneNode descendant in Nodes(child))
            yield return descendant;
    }

    private static Dictionary<UiSymbolId, UiCollectionItemTarget> IndexCollectionItems(
        IReadOnlyDictionary<UiSymbolId, UiSceneNode> nodes,
        UiLayoutSnapshot layout)
    {
        var result = new Dictionary<UiSymbolId, UiCollectionItemTarget>();
        foreach (UiCollectionLayoutWindow window in layout.CollectionWindows)
        {
            if (!nodes.TryGetValue(window.Collection, out UiSceneNode? node) ||
                node is not UiCollectionSceneNode { IsSelectable: true } collection)
                continue;
            foreach (UiVirtualizedItemLayout item in window.Items)
                result.Add(item.Node, new UiCollectionItemTarget(collection, item));
        }
        return result;
    }

    private sealed record UiCollectionItemTarget(
        UiCollectionSceneNode Collection,
        UiVirtualizedItemLayout Item);
}
