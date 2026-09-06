using System;
using System.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using Hatifect.TestHarness;
using Hatifect.UI.Stardew.Dogfood;

namespace Hatifect.UI.Stardew;

/// <summary>
/// SMAPI owner for the semantic UI host API and opt-in runtime acceptance diagnostics.
/// </summary>
public sealed class ModEntry : Mod
{
    private UiSemanticDogfoodCompositionRoot? _semanticDogfood;
    private UiAutomatedAcceptanceController? _automatedAcceptance;
    private AutomatedBackgroundProgress? _automatedBackgroundProgress;

    public override void Entry(IModHelper helper)
    {
        _automatedBackgroundProgress = AutomatedBackgroundProgress.TryAttach(helper);
        UiStardewAcceptanceRecorder.Shared.Configure(helper, Monitor, ModManifest.Version.ToString());
        helper.ConsoleCommands.Add(
            "hatifect_ui_acceptance",
            "Capture real-host RC evidence. Usage: hatifect_ui_acceptance [status|begin <scenario>|end|check <id> <pass|fail> [note]|save [path]|clear]",
            OnAcceptanceCommand);
        if (UiAutomatedAcceptanceScenarioRegistry.IsRegistrationEnabled)
        {
            helper.ConsoleCommands.Add(
                "hatifect_ui_semantic",
                "Control TestHarness-only semantic diagnostics. Usage: hatifect_ui_semantic [status|open|close|overlay-open|overlay-close]",
                OnSemanticCommand);
        }
        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        Monitor.Log($"Hatifect UI {ModManifest.Version} semantic host loaded.", LogLevel.Trace);
    }

    public override object GetApi()
        => new UiStardewApiBridge(Helper);

    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing)
            {
                _automatedBackgroundProgress?.Dispose();
            }
        }
        finally
        {
            base.Dispose(disposing);
        }
    }

    private void OnAcceptanceCommand(string command, string[] args)
        => UiStardewAcceptanceRecorder.Shared.ExecuteCommand(args);

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        if (!UiAutomatedAcceptanceScenarioRegistry.IsRegistrationEnabled)
            return;
        _semanticDogfood ??= new UiSemanticDogfoodCompositionRoot(Game1.graphics.GraphicsDevice, Helper);
        _automatedAcceptance ??= UiAutomatedAcceptanceController.TryStart(
            Helper,
            Monitor,
            _semanticDogfood);
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        _semanticDogfood?.Reset();
        _automatedAcceptance?.OnReturnedToTitle();
    }

    private void OnSemanticCommand(string command, string[] args)
    {
        string action = args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "open";
        UiSemanticDogfoodCompositionRoot? root = _semanticDogfood;
        if (root == null)
        {
            Monitor.Log("The semantic dogfood host is not initialized until GameLaunched.", LogLevel.Warn);
            return;
        }

        switch (action)
        {
            case "status":
                Monitor.Log(
                    $"Semantic dogfood: Terminal {(root.IsOpen ? "open" : "closed")}; " +
                    $"HUD overlay {(root.IsOverlayVisible ? "open" : "closed")}.",
                    LogLevel.Info);
                break;
            case "open":
                bool opened = root.TryOpen();
                Monitor.Log(
                    opened
                        ? "Opened the clean-slate semantic Terminal dogfood host."
                        : root.IsOpen
                            ? "The clean-slate semantic Terminal dogfood host is already open."
                            : "The semantic Terminal could not open because another menu is active.",
                    opened || root.IsOpen ? LogLevel.Info : LogLevel.Warn);
                break;
            case "close":
                Monitor.Log(
                    root.Close() ? "Closed the semantic Terminal dogfood host." : "The semantic Terminal is not open.",
                    LogLevel.Info);
                break;
            case "overlay-open":
                bool overlayOpened = root.TryOpenOverlay();
                Monitor.Log(
                    overlayOpened
                        ? "Opened the clean-slate semantic HUD overlay dogfood host."
                        : "The semantic HUD overlay dogfood host is already open.",
                    LogLevel.Info);
                break;
            case "overlay-close":
                Monitor.Log(
                    root.CloseOverlay()
                        ? "Closed the semantic HUD overlay dogfood host."
                        : "The semantic HUD overlay is not open.",
                    LogLevel.Info);
                break;
            default:
                Monitor.Log(
                    "Usage: hatifect_ui_semantic [status|open|close|overlay-open|overlay-close]",
                    LogLevel.Warn);
                break;
        }
    }
}
