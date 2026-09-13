using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Diagnostics;
using Hatifect.UI.Stardew.Semantic;
using StardewValley;

namespace Hatifect.UI.Stardew;

internal abstract partial class UiHostedSemanticSurfaceSession
{
    internal bool IsOwnedBy(UiSemanticSurfaceService owner) => ReferenceEquals(_owner, owner);

    internal UiSemanticSurfaceSnapshot CaptureForAutomatedAcceptance()
    {
        UiSurfaceObservationState observation = RequireObservationOwner();
        if (_retireRequested || _closed || _disposing || _disposed) return observation.Retire();
        UiSemanticStardewMenu menu = RequireLiveObservationMenu();
        menu.RequireAutomationOwner();
        return observation.Capture(_host!.Session, _environment!, visible: true);
    }

    internal bool ActivateForAutomatedAcceptance(UiSymbolId action)
    {
        RequireObservationOwner();
        ThrowIfUnavailable();
        return RequireLiveObservationMenu().ActivateForAutomatedAcceptance(action);
    }

    internal bool RevealForAutomatedAcceptance(UiSymbolId semantic)
    {
        RequireObservationOwner();
        ThrowIfUnavailable();
        return RequireLiveObservationMenu().RevealForAutomatedAcceptance(semantic);
    }

    internal void CancelForAutomatedAcceptance(UiSemanticSurfaceCancelInput input)
    {
        RequireObservationOwner();
        ThrowIfUnavailable();
        RequireLiveObservationMenu().CancelForAutomatedAcceptance(input);
    }

    private UiSurfaceObservationState RequireObservationOwner()
    {
        if (_kind != UiSemanticHostKind.Window || _terminal is not null)
            throw new NotSupportedException("Hosted acceptance supports standalone Window surfaces only.");
        UiSurfaceObservationState observation = _observation
            ?? throw new InvalidOperationException("Hosted acceptance requires the exact automated TestHarness environment.");
        observation.RequireOwner();
        _events.RequireOwner();
        return observation;
    }

    private UiSemanticStardewMenu RequireLiveObservationMenu()
    {
        if (_menu is null || _host is null || _environment is null)
            throw new InvalidOperationException("Hosted acceptance requires a shown Window surface.");
        // Reading never repairs native ownership, synchronizes sources, or retires a replacement.
        if (!ReferenceEquals(Game1.activeClickableMenu, _menu))
            throw new InvalidOperationException("The Window no longer owns the native menu slot.");
        return _menu;
    }
}
