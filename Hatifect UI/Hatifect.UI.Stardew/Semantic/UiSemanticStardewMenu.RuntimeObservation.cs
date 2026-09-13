using System;
using Hatifect.UI.Runtime.Platform;

namespace Hatifect.UI.Stardew.Semantic;

internal sealed partial class UiSemanticStardewMenu
{
    // Ordinary semantic hosts do not own a Terminal invocation. Diagnostics that need the
    // accepted runtime scene must use this host-neutral boundary instead of CurrentInvocation.
    internal UiHostRuntimeSession CaptureRuntimeContext()
        => (_host ?? throw new ObjectDisposedException(nameof(UiSemanticStardewMenu))).Session.Root;
}
