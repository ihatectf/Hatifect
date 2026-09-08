using System;
using Hatifect.UI.Runtime.Diagnostics;

namespace Hatifect.UI.Stardew.Semantic;

internal sealed partial class UiSemanticStardewOverlaySession
{
    internal bool RevealForAutomatedAcceptance(UiSymbolId semantic)
    {
        RequireRevealOwner();
        var sample = BeginAcceptanceSample();
        try
        {
            return UiSurfaceReveal.Reveal(Host.Session, semantic, RequireRevealOwner,
                (point, delta) => _input.Wheel(checked((int)point.X), checked((int)point.Y), checked(-(int)delta)));
        }
        finally { sample.Complete(); }
    }

    private void RequireRevealOwner()
    {
        ThrowIfDisposed();
        RequireCurrentScreen();
        if (!UiAutomatedAcceptanceScenarioRegistry.IsRegistrationEnabled)
            throw new InvalidOperationException("Reveal input requires the exact automated TestHarness environment.");
        Host.Session.Root.RequireOwner();
        if (!CanRouteInput())
            throw new InvalidOperationException("Reveal input requires a current native root owner.");
    }
}
