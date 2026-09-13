using System;
using Hatifect.UI;

namespace Hatifect.UI.Runtime.Activation;

/// <summary>
/// Internal fail-closed signal for an expected, diagnosed Terminal route rejection. It is distinct
/// from pending activation so a composition root cannot accidentally suppress arbitrary failures.
/// </summary>
internal sealed class UiTerminalSectionActivationRejectedException : Exception
{
    public UiTerminalSectionActivationRejectedException(UiSymbolId section, Exception reason)
        : base($"Terminal section '{section}' rejected staged activation.", reason)
    {
        if (!section.IsValid)
            throw new ArgumentException("A rejected Terminal activation requires a stable section ID.", nameof(section));
        Section = section;
    }

    public UiSymbolId Section { get; }
}
