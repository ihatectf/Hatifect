using System;
using Hatifect.UI;

namespace Hatifect.UI.Runtime.Activation;

/// <summary>
/// Internal control-flow signal for a routed Terminal section whose composition-root-owned input is
/// still loading. It is intentionally caught only by the matching Terminal route, so ordinary
/// activation failures retain their propagation and failure-atomic behavior.
/// </summary>
internal sealed class UiTerminalSectionActivationPendingException : Exception
{
    public UiTerminalSectionActivationPendingException(UiSymbolId section)
        : base($"Terminal section '{section}' is pending staged activation.")
    {
        if (!section.IsValid)
            throw new ArgumentException("A pending Terminal activation requires a stable section ID.", nameof(section));
        Section = section;
    }

    public UiSymbolId Section { get; }
}
