namespace Hatifect.UI.Experience;

public sealed record UiSemanticAssetDiagnostic(string Code, string Message, string? Source = null);
public sealed record UiSemanticReloadResult(UiSymbolId Experience, bool Accepted, bool Changed,
    long Version, IReadOnlyList<UiSemanticAssetDiagnostic> Diagnostics);

/// <summary>Optional live-asset facet. Reload preserves semantic sources, selection, and editing state.
/// Calls and result events occur on the UI thread. Null text resets that document to framework defaults.</summary>
public interface IUiSemanticReloadSession : IUiSemanticSurfaceSession
{
    UiSemanticReloadResult Reload(UiSymbolId experience, string? presentation, string? visual);
    /// <summary>Watches an explicit pair of local files. Null path uses defaults. Changes coalesce on the UI tick.
    /// At most 16 watches per session; each document is limited to 1 MiB. Dispose the lease to stop watching.</summary>
    IDisposable WatchAssets(UiSymbolId experience, string? presentationPath, string? visualPath);
    event Action<UiSemanticReloadResult>? AssetsReloaded;
}
