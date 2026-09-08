using System;
using Hatifect.UI.Experience;

namespace Hatifect.Flow.Diagnostics;

// Reveal proves accepted layout visibility. Only a later draw of that same frame proves presentation.
internal readonly record struct FlowUiRevealFrame(Guid Instance, UiSemanticSurfaceFrame Accepted, long CompletedPass)
{
    internal bool IsCompletedBy(Guid instance, UiSemanticSurfaceFrame? accepted,
        UiSemanticSurfaceFrame? rendered, long completedPass)
        => instance == Instance && accepted == Accepted && rendered == Accepted && completedPass > CompletedPass;
}
