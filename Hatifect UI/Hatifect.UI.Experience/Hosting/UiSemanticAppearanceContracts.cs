namespace Hatifect.UI.Experience;

public enum UiSemanticTheme { Dark, Light, HighContrast }

/// <summary>Optional appearance facet implemented by production semantic sessions. Call on the UI thread.</summary>
public interface IUiSemanticAppearanceSession : IUiSemanticSurfaceSession
{
    void SetTheme(UiSemanticTheme theme);
    /// <summary>Registers an owned PNG copy for this session. Dispose the lease to release it.
    /// Maximum 256 registrations, 4 MiB encoded per image, 16 MiB encoded and 32 MiB decoded per session.
    /// Missing IDs render a framework placeholder. Invalid images and duplicate IDs are rejected.</summary>
    IDisposable RegisterTexture(UiSymbolId asset, byte[] png);
}
