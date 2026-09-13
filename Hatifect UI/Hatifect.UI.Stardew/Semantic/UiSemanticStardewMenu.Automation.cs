using System;
using Microsoft.Xna.Framework.Input;
using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Diagnostics;
using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Platform;
using StardewValley;

namespace Hatifect.UI.Stardew.Semantic;

internal sealed partial class UiSemanticStardewMenu
{
    internal void RequireAutomationOwner()
    {
        if (!UiAutomatedAcceptanceScenarioRegistry.IsRegistrationEnabled || Observation is null)
            throw new InvalidOperationException("Window input requires the exact automated TestHarness environment.");
        Observation.RequireOwner();
        if (_cleaned || _host is null || !_slot.CanDispatch || !ReferenceEquals(Game1.activeClickableMenu, this))
            throw new InvalidOperationException("Window input requires its current native menu owner.");
        _host.Session.Root.RequireOwner();
        if (!_host.Session.Root.IsActive)
            throw new InvalidOperationException("Window input requires an active host.");
    }

    internal bool ActivateForAutomatedAcceptance(UiSymbolId action)
    {
        RequireAutomationOwner();
        var sample = BeginAcceptanceSample();
        try
        {
            return UiSurfaceActionInput.Activate(_host!.Session, action, RequireAutomationOwner,
                direction => DispatchAutomationKey(Keys.Tab, direction == UiNavigationDirection.Previous),
                () => DispatchAutomationKey(Keys.Enter, false));
        }
        finally { sample.Complete(); }
    }

    private UiPortalDispatch DispatchAutomationKey(Keys key, bool shift)
    {
        UiPortalDispatch dispatch = _input.KeyDown(key, shift, control: false);
        CompleteInput();
        return dispatch;
    }

    internal bool RevealForAutomatedAcceptance(UiSymbolId semantic)
    {
        RequireAutomationOwner();
        var sample = BeginAcceptanceSample();
        try
        {
            return UiSurfaceReveal.Reveal(_host!.Session, semantic, RequireAutomationOwner,
                (point, delta) => _input.Wheel(checked((int)point.X), checked((int)point.Y), checked(-(int)delta)),
                UiSurfaceRevealDiagnostics.RecordFailure);
        }
        finally { sample.Complete(); }
    }

    internal void CancelForAutomatedAcceptance(UiSemanticSurfaceCancelInput input)
    {
        RequireAutomationOwner();
        var sample = BeginAcceptanceSample();
        try
        {
            UiPortalDispatch dispatch = input switch
            {
                UiSemanticSurfaceCancelInput.Escape => _input.KeyDown(Keys.Escape, shift: false, control: false),
                UiSemanticSurfaceCancelInput.ControllerBack => _input.GamePad(Buttons.B),
                _ => throw new ArgumentOutOfRangeException(nameof(input))
            };
            if (RequestsRootCancel(dispatch)) TryCloseFromUnhandledCancel();
            CompleteInput();
        }
        finally { sample.Complete(); }
    }
}
