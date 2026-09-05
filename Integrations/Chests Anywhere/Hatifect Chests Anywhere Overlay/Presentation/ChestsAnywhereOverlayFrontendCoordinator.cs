using StardewModdingAPI;

namespace Hatifect.ChestsAnywhereOverlay.Presentation;

/// <summary>
/// Hatifect-only presentation fault barrier. The application controller owns capture/state/selection;
/// this wrapper prevents a renderer failure from reviving the retired UI Core path or corrupting state.
/// </summary>
internal sealed class ChestsAnywhereOverlayFrontendCoordinator : IChestsAnywhereOverlayFrontend
{
    private readonly IChestsAnywhereOverlayFrontend _frontend;
    private readonly IMonitor _monitor;
    private bool _faulted;

    public ChestsAnywhereOverlayFrontendCoordinator(IChestsAnywhereOverlayFrontend frontend, IMonitor monitor)
    {
        _frontend = frontend ?? throw new ArgumentNullException(nameof(frontend));
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _frontend.Rendered += () => Rendered?.Invoke();
        _frontend.Closed += () => Closed?.Invoke();
    }

    public bool Visible => !_faulted && _frontend.Visible;
    public event Action? Rendered;
    public event Action? Closed;

    public void Refresh(bool preserveViewState)
    {
        if (_faulted) return;
        try { _frontend.Refresh(preserveViewState); }
        catch (Exception ex) { TripFault("refresh", ex); }
    }

    public void UpdateOptions(ChestsAnywhereOverlayConfig config)
    {
        if (_faulted) return;
        try { _frontend.UpdateOptions(config); }
        catch (Exception ex) { TripFault("options update", ex); }
    }

    public bool Show()
    {
        if (_faulted) return false;
        try { return _frontend.Show(); }
        catch (Exception ex)
        {
            TripFault("open", ex);
            return false;
        }
    }

    public void Hide()
    {
        try { _frontend.Hide(); }
        catch (Exception ex) { _monitor.Log($"Hatifect Chests Anywhere Overlay failed while closing: {ex.Message}", LogLevel.Warn); }
    }

    public void Dispose()
    {
        try { _frontend.Dispose(); }
        catch (Exception ex) { _monitor.Log($"Hatifect Chests Anywhere Overlay failed while disposing: {ex.Message}", LogLevel.Warn); }
    }

    private void TripFault(string phase, Exception ex)
    {
        _faulted = true;
        try { _frontend.Hide(); } catch { }
        _monitor.Log($"Hatifect Chests Anywhere Overlay failed during {phase} and has been disabled for this process: {ex}", LogLevel.Error);
        Closed?.Invoke();
    }
}
