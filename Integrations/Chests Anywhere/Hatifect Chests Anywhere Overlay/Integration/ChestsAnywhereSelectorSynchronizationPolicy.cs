namespace Hatifect.ChestsAnywhereOverlay.Integration;

/// <summary>Allocation-free update policy for synchronizing private native selector ownership.</summary>
internal static class ChestsAnywhereSelectorSynchronizationPolicy
{
    internal static bool ShouldSynchronize(
        bool nativeShapeMayHaveChanged,
        bool hideNativeSelectors,
        bool hasSuppressionLease)
        => hideNativeSelectors
            ? nativeShapeMayHaveChanged || !hasSuppressionLease
            : hasSuppressionLease;
}
