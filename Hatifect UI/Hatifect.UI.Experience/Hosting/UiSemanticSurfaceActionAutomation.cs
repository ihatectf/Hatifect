namespace Hatifect.UI.Experience;

/// <summary>
/// Optional additive exact-harness input API. Create surfaces through this same API instance.
/// The production v1 surface contract remains unchanged.
/// </summary>
public interface IUiSemanticSurfaceActionAutomationApi : IUiSemanticSurfaceObservationApi
{
    IUiSemanticSurfaceActionAutomation ActionAutomation { get; }
}

/// <summary>Bounded normalized keyboard input for root action buttons in the exact harness.</summary>
public interface IUiSemanticSurfaceActionAutomation
{
    bool IsEnabled { get; }

    /// <summary>
    /// Navigate with at most 256 Tab/Shift+Tab inputs to an accepted root action and submit Enter once.
    /// Standalone Windows search backward after reaching their contained forward focus boundary.
    /// Rejects disabled automation, foreign handles, wrong thread/screen, retired menu owners,
    /// portals and changed action identity. Does not invoke delegates directly or synthesize OS
    /// events. A true result reports input admission, not asynchronous completion or rendering.
    /// </summary>
    bool Activate(IUiSemanticSurfaceSession session, UiSymbolId action);
}
