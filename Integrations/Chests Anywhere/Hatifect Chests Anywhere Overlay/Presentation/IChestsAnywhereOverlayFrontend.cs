namespace Hatifect.ChestsAnywhereOverlay.Presentation;

internal interface IChestsAnywhereOverlayFrontend : IDisposable
{
    bool Visible { get; }
    event Action? Rendered;
    event Action? Closed;
    void Refresh(bool preserveViewState);
    void UpdateOptions(ChestsAnywhereOverlayConfig config);
    bool Show();
    void Hide();
}
