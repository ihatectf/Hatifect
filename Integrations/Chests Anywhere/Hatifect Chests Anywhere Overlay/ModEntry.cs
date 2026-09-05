using Hatifect.ChestsAnywhereOverlay.Integration;
using Hatifect.ChestsAnywhereOverlay.Presentation;
using Hatifect.ChestsAnywhereOverlay.UI.Semantic;
using Hatifect.UI.Experience;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace Hatifect.ChestsAnywhereOverlay;

public sealed class ModEntry : Mod
{
    private ChestsAnywhereOverlayConfig _config = null!;
    private ChestsAnywhereAdapter _adapter = null!;
    private ChestsAnywhereOverlayController _navigator = null!;
    private SemanticChestsAnywhereOverlayFrontend _semanticFrontend = null!;
    private bool _initialized;

    public override void Entry(IModHelper helper)
    {
        _config = helper.ReadConfig<ChestsAnywhereOverlayConfig>();
        NormalizeConfig();

        // Mod-provided APIs aren't available during Entry. Bind Chests Anywhere from GameLaunched
        // so this integration never races another mod's initialization.
        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        if (_initialized)
            return;

        _adapter = ChestsAnywhereAdapter.CreateForCurrentHost(Helper, Monitor);
        _navigator = new ChestsAnywhereOverlayController(Helper, _adapter, () => _config);
        IUiSemanticSurfaceApi? ui = Helper.ModRegistry.GetApi<IUiSemanticSurfaceApi>("Hatifect.UI");
        if (ui == null || ui.ApiVersion < 1)
        {
            Monitor.Log(
                "Hatifect Chests Anywhere Overlay requires Hatifect UI semantic surface API v1; " +
                (ui == null ? "the API was unavailable." : $"found v{ui.ApiVersion}."),
                LogLevel.Error);
            return;
        }
        _semanticFrontend = new SemanticChestsAnywhereOverlayFrontend(_navigator, ui);
        var frontend = new ChestsAnywhereOverlayFrontendCoordinator(_semanticFrontend, Monitor);
        _navigator.AttachFrontend(frontend);
        SemanticChestsAnywhereOverlayAcceptanceScenarios.Register(
            _adapter,
            _navigator,
            _semanticFrontend,
            ui.Automation);

        Helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        Helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        Helper.Events.GameLoop.Saving += OnSaving;
        Helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        _initialized = true;
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e) => _navigator.LoadState();
    private void OnSaving(object? sender, SavingEventArgs e) => _navigator.SaveState();
    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e) => _navigator.Shutdown();
    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e) => _navigator.Update();

    private void NormalizeConfig()
    {
        _config.Normalize();
    }
}
