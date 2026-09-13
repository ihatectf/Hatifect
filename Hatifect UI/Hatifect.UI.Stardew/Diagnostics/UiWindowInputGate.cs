using System;

namespace Hatifect.UI.Stardew;

/// <summary>Initial ordinary Window input probe; never dispatches or changes UI state.</summary>
internal sealed class UiWindowInputGate
{
    private readonly string _expectedText;
    private UiWindowInputObservation _baseline;
    private UiWindowInputObservation? _previous;
    private long _lastFrame = -1;

    internal UiWindowInputGate(string expectedText)
    {
        if (string.IsNullOrWhiteSpace(expectedText)) throw new ArgumentException("A session probe is required.", nameof(expectedText));
        _expectedText = expectedText;
    }

    internal UiWindowInputPhase Phase { get; private set; }

    internal UiWindowInputPhase? Observe(long frame, UiWindowInputObservation value)
    {
        if (frame <= _lastFrame || Phase == UiWindowInputPhase.Complete) return null;
        _lastFrame = frame;
        if (Phase != UiWindowInputPhase.Ready && value.SurfaceEpoch != _baseline.SurfaceEpoch)
        {
            Phase = UiWindowInputPhase.Ready;
            _previous = null;
        }
        if (!Matches(value))
        {
            _previous = null;
            return null;
        }
        bool stable = _previous == value;
        _previous = value;
        if (!stable) return null;
        UiWindowInputPhase completed = Phase;
        _baseline = value;
        _previous = null;
        Phase++;
        return completed;
    }

    private bool Matches(UiWindowInputObservation value)
    {
        if (!value.Visible || value.SurfaceEpoch <= 0) return false;
        if (Phase != UiWindowInputPhase.Ready && (value.PointerPressed < _baseline.PointerPressed
            || value.PointerReleased < _baseline.PointerReleased || value.TabPressed < _baseline.TabPressed
            || value.BackspacePressed < _baseline.BackspacePressed || value.TextReceived < _baseline.TextReceived)) return false;
        return Phase switch
        {
            UiWindowInputPhase.Ready => value.HasTextField,
            UiWindowInputPhase.Pointer => value.FocusedTextField && value.PointerInsideFocusedField
                && value.PointerPressed > _baseline.PointerPressed && value.PointerReleased > _baseline.PointerReleased
                && !string.IsNullOrEmpty(value.FocusedSemantic),
            UiWindowInputPhase.Text => SameField(value) && value.TextReceived > _baseline.TextReceived
                && value.Text == _expectedText,
            UiWindowInputPhase.Backspace => SameField(value) && value.BackspacePressed > _baseline.BackspacePressed
                && value.Text == _expectedText[..^1],
            UiWindowInputPhase.Tab => value.TabPressed > _baseline.TabPressed
                && !string.IsNullOrEmpty(value.FocusedSemantic) && value.FocusedSemantic != _baseline.FocusedSemantic,
            _ => false
        };
    }

    private bool SameField(UiWindowInputObservation value)
        => value.FocusedTextField && value.FocusedSemantic == _baseline.FocusedSemantic;
}

internal enum UiWindowInputPhase { Ready, Pointer, Text, Backspace, Tab, Complete }

internal readonly record struct UiWindowInputObservation(long SurfaceEpoch, bool Visible, bool HasTextField,
    string? FocusedSemantic, bool FocusedTextField, string? Text, bool PointerInsideFocusedField,
    long PointerPressed, long PointerReleased, long TabPressed, long BackspacePressed, long TextReceived = 0, long SceneVersion = 0, long FrameVersion = 0);
