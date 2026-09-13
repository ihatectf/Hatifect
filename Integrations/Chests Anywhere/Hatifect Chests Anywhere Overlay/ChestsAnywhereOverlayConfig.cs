namespace Hatifect.ChestsAnywhereOverlay;

public sealed class ChestsAnywhereOverlayConfig
{
    public bool HideNativeSelectors { get; set; } = true;
    public bool DimUnderlyingMenu { get; set; } = true;
    public bool CloseOnOutsideClick { get; set; } = true;
    public bool RememberLastStoragePerCategory { get; set; } = true;
    public int RecentLimit { get; set; } = 8;
    public void Normalize()
    {
        RecentLimit = Math.Clamp(RecentLimit, 1, 30);
    }
}
