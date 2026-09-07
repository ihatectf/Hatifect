using Hatifect.UI.Experience;
using StardewModdingAPI;

namespace Hatifect.UI.Stardew;

/// <summary>SMAPI transport for the public semantic surface contract.</summary>
public sealed class UiStardewApiBridge : IUiSemanticHostApi, IUiSemanticSurfaceObservationApi
{
    private readonly UiSemanticSurfaceService _semanticSurfaces;

    internal UiStardewApiBridge(IModHelper helper)
        => _semanticSurfaces = new UiSemanticSurfaceService(helper);

    public int ApiVersion => _semanticSurfaces.ApiVersion;
    public IUiSemanticSurfaceAutomation Automation => _semanticSurfaces.Automation;
    public IUiSemanticSurfaceObservation Observation => _semanticSurfaces.Observation;

    public IUiSemanticSurfaceSession CreateActiveMenuOverlay(
        UiExperienceDefinition experience,
        UiSemanticSurfaceOptions options)
        => _semanticSurfaces.CreateActiveMenuOverlay(experience, options);

    public IUiSemanticSurfaceSession CreateSurface(
        UiExperienceDefinition experience, UiSemanticHostKind kind, UiSemanticSurfaceOptions options)
        => _semanticSurfaces.CreateSurface(experience, kind, options);

    public IUiSemanticTerminalSession CreateTerminal(
        UiSemanticTerminalDefinition terminal, UiSemanticSurfaceOptions options)
        => _semanticSurfaces.CreateTerminal(terminal, options);
}
