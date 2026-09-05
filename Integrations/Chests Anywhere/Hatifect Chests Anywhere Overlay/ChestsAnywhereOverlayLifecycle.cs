namespace Hatifect.ChestsAnywhereOverlay;

internal enum ChestsAnywhereOverlayTransition
{
    None,
    ShowHatifect,
    HideHatifect,
    NativeSessionEnded
}

/// <summary>
/// Pure ownership state machine for the exact Chests Anywhere toggle handoff. It intentionally
/// knows nothing about SMAPI or UI geometry, so the first-B, repeated-B, RMB, and native-close
/// sequences can be proven without replacing Chests Anywhere's input implementation.
/// </summary>
internal sealed class ChestsAnywhereOverlayLifecycle
{
    private object? _nativeOverlay;

    internal bool HasActiveNativeOverlay => _nativeOverlay != null;

    internal ChestsAnywhereOverlayTransition ObserveInactive()
    {
        if (_nativeOverlay == null) return ChestsAnywhereOverlayTransition.None;
        _nativeOverlay = null;
        return ChestsAnywhereOverlayTransition.NativeSessionEnded;
    }

    internal ChestsAnywhereOverlayTransition ObserveActive(
        object nativeOverlay,
        bool hatifectVisible,
        bool nativeModal,
        bool nativeTogglePressed)
    {
        ArgumentNullException.ThrowIfNull(nativeOverlay);
        bool nativeSessionStarted = !ReferenceEquals(_nativeOverlay, nativeOverlay);
        _nativeOverlay = nativeOverlay;

        if (hatifectVisible && nativeTogglePressed)
            return ChestsAnywhereOverlayTransition.HideHatifect;
        if (nativeSessionStarted && !hatifectVisible && !nativeModal && nativeTogglePressed)
            return ChestsAnywhereOverlayTransition.ShowHatifect;
        return ChestsAnywhereOverlayTransition.None;
    }

    internal void Reset() => _nativeOverlay = null;
}
