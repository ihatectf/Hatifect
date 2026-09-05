using Hatifect.ChestsAnywhereOverlay.Integration;
using Hatifect.ChestsAnywhereOverlay.Models;

namespace Hatifect.ChestsAnywhereOverlay;

/// <summary>Atomically retires Hatifect-owned state when native overlay capture fails.</summary>
internal static class ChestsAnywhereOverlayCapturePolicy
{
    internal static bool TryCaptureOrRetire(
        IChestsAnywhereOverlayAdapter adapter,
        Action retireSession,
        out StorageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(retireSession);
        if (adapter.TryCapture(out snapshot))
            return true;
        retireSession();
        return false;
    }
}
