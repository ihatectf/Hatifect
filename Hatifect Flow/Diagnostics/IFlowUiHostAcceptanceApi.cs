using Hatifect.UI.Experience;

namespace Hatifect.Flow.Diagnostics;

/// <summary>Exact-harness consumer proxy: every surface and observation uses this one UI owner.</summary>
public interface IFlowUiHostAcceptanceApi : IUiSemanticHostApi, IUiSemanticSurfaceRevealAutomationApi { }
