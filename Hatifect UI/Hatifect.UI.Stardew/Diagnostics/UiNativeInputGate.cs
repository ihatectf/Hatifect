using System;
using Hatifect.UI.Runtime.Accessibility;
using Hatifect.UI.Runtime.Layout;

namespace Hatifect.UI.Stardew;

/// <summary>Correlates external input counters with two stable rendered postconditions.</summary>
internal sealed class UiNativeInputGate
{
    private readonly string _expectedText;
    private UiNativeInputObservation _baseline;
    private UiNativeInputObservation? _previous;
    private long _lastFrame = -1;

    internal UiNativeInputGate(string expectedText)
    {
        if (string.IsNullOrWhiteSpace(expectedText))
            throw new ArgumentException("A session text probe is required.", nameof(expectedText));
        _expectedText = expectedText;
    }

    internal UiNativeInputPhase Phase { get; private set; } = UiNativeInputPhase.Ready;

    // Visibility belongs to the observed target. Empty sibling lists legitimately have no area.
    internal static bool IsVisible(UiAccessibilityNodeSnapshot node)
        => FiniteArea(node.Bounds) && FiniteArea(node.Clip)
            && node.Clip.Right > node.Bounds.X && node.Clip.Bottom > node.Bounds.Y
            && node.Bounds.Right > node.Clip.X && node.Bounds.Bottom > node.Clip.Y;

    private static bool FiniteArea(UiRect rect)
        => rect.Width > 0 && rect.Height > 0
            && float.IsFinite(rect.X) && float.IsFinite(rect.Y)
            && float.IsFinite(rect.Right) && float.IsFinite(rect.Bottom);

    internal UiNativeInputPhase? Observe(long completedFrame, UiNativeInputObservation value)
    {
        if (Phase == UiNativeInputPhase.Complete || completedFrame <= _lastFrame) return null;
        _lastFrame = completedFrame;
        if (!MatchesPhase(value))
        {
            _previous = null;
            return null;
        }
        bool stable = _previous == value;
        _previous = value;
        if (!stable) return null;

        UiNativeInputPhase completed = Phase;
        _baseline = value;
        _previous = null;
        Phase++;
        return completed;
    }

    private bool MatchesPhase(UiNativeInputObservation value)
    {
        if (!value.ValidGeometry) return false;
        return Phase switch
        {
            UiNativeInputPhase.Ready => !value.HasTextField,
            UiNativeInputPhase.Pointer => value.HasTextField
                && value.PointerPressed > _baseline.PointerPressed
                && value.PointerReleased > _baseline.PointerReleased,
            UiNativeInputPhase.Keyboard => value.HasTextField
                && value.TextFieldFocused
                && value.TabPressed > _baseline.TabPressed
                && !string.IsNullOrEmpty(value.FocusedId)
                && !string.Equals(value.FocusedId, _baseline.FocusedId, StringComparison.Ordinal),
            UiNativeInputPhase.Text => value.HasTextField
                && value.TextFieldFocused
                && value.PointerPressed > _baseline.PointerPressed
                && value.PointerReleased > _baseline.PointerReleased
                && string.Equals(value.TextValue, _expectedText, StringComparison.Ordinal),
            UiNativeInputPhase.Backspace => value.HasTextField && value.TextFieldFocused
                && value.BackspacePressed > _baseline.BackspacePressed
                && string.Equals(value.TextValue, _expectedText[..^1], StringComparison.Ordinal),
            _ => false
        };
    }
}

internal enum UiNativeInputPhase { Ready, Pointer, Keyboard, Text, Backspace, Complete }

internal readonly record struct UiNativeInputObservation(
    bool ValidGeometry,
    bool HasTextField,
    string? FocusedId,
    string? TextValue,
    long PointerPressed,
    long PointerReleased,
    long TabPressed,
    bool TextFieldFocused = false,
    long BackspacePressed = 0);
