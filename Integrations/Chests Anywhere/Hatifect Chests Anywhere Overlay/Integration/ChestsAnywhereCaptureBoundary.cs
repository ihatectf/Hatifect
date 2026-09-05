namespace Hatifect.ChestsAnywhereOverlay.Integration;

/// <summary>
/// Converts private-overlay acquisition drift into the adapter's one fail-closed disable path.
/// The boundary owns no native menu and therefore never closes or replaces Chests Anywhere UI.
/// </summary>
internal static class ChestsAnywhereCaptureBoundary
{
    internal static bool TryAcquire(
        Func<object?> getOverlay,
        Action<Exception> disableIntegration,
        out object overlay)
    {
        ArgumentNullException.ThrowIfNull(getOverlay);
        ArgumentNullException.ThrowIfNull(disableIntegration);
        try
        {
            overlay = getOverlay()
                ?? throw new InvalidOperationException(
                    "Chests Anywhere reported an active overlay without exposing its private overlay instance.");
            return true;
        }
        catch (Exception error)
        {
            overlay = null!;
            disableIntegration(error);
            return false;
        }
    }
}
