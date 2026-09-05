using Hatifect.UI.Experience;
using StardewModdingAPI;

namespace Hatifect.UI.Stardew;

/// <summary>SMAPI transport for the public semantic surface contract.</summary>
public sealed class UiStardewApiBridge : IUiSemanticSurfaceApi
{
    private readonly UiSemanticSurfaceService _semanticSurfaces;

    internal UiStardewApiBridge(IModHelper helper)
        => _semanticSurfaces = new UiSemanticSurfaceService(helper);

    public int ApiVersion => _semanticSurfaces.ApiVersion;
    public IUiSemanticSurfaceAutomation Automation => _semanticSurfaces.Automation;

    public IUiSemanticSurfaceSession CreateActiveMenuOverlay(
        UiExperienceDefinition experience,
        UiSemanticSurfaceOptions options)
        => _semanticSurfaces.CreateActiveMenuOverlay(experience, options);
}
