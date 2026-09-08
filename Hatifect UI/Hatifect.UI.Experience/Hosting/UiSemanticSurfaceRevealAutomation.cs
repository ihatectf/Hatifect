namespace Hatifect.UI.Experience;

/// <summary>Optional additive exact-harness reveal API. Create surfaces through this same API instance.</summary>
public interface IUiSemanticSurfaceRevealAutomationApi : IUiSemanticSurfaceActionAutomationApi
{
    IUiSemanticSurfaceRevealAutomation RevealAutomation { get; }
}

/// <summary>Bounded normalized wheel input for an accepted root semantic element in the exact harness.</summary>
public interface IUiSemanticSurfaceRevealAutomation
{
    bool IsEnabled { get; }

    /// <summary>
    /// Find one semantic scene element within 1024 nodes and try at most 64 wheel inputs to
    /// bring its complete bounds inside the accepted clip. Preserves passive focus and does
    /// not submit actions. Rejects foreign, retired or changed owners/scenes, portals and
    /// ambiguous identities. Returns false for missing targets, unsupported root wheel routes,
    /// oversized or nested-clipped elements, exhausted input budget or lack of progress.
    /// A true result confirms accepted layout visibility only: wait for a new completed draw
    /// before capturing rendered evidence. Earlier successful wheel steps remain accepted
    /// when a later step fails, just as with ordinary input. This does not synthesize OS events.
    /// </summary>
    bool Reveal(IUiSemanticSurfaceSession session, UiSymbolId semantic);
}
