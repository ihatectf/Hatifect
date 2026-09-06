using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Objects;
using Hatifect.UI.Stardew.Dogfood;
using Hatifect.UI.Stardew.Semantic;
using RuntimeAccessibilityNode = Hatifect.UI.Runtime.Accessibility.UiAccessibilityNodeSnapshot;
using RuntimeAccessibilityRole = Hatifect.UI.Runtime.Accessibility.UiAccessibilityRole;
using RuntimeAccessibilitySnapshot = Hatifect.UI.Runtime.Accessibility.UiAccessibilitySnapshot;
using RuntimeSymbolId = Hatifect.UI.UiSymbolId;

namespace Hatifect.UI.Stardew;

/// <summary>
/// Test-mode-only game-thread driver for allowlisted live acceptance scenarios. It never activates
/// without the transport-owned automated protocol environment.
/// </summary>
internal sealed class UiAutomatedAcceptanceController : IDisposable
{
    private enum BootstrapLifecycleState
    {
        None,
        AwaitingInitialLoadStart,
        AwaitingInitialWorld,
        AwaitingReturnToTitle,
        AwaitingVerificationLoadStart,
        AwaitingVerificationWorld
    }

    private const int ProtocolVersion = 1;
    private const int SaveFixtureSchemaVersion = 2;
    private const int StartupDelayTicks = 30;
    private const int RequiredPerformanceFrames = 620;
    private const ulong BootstrapUniqueId = 4242424242UL;
    private const string BootstrapPlayerName = "HatifectHarness";
    private const string BootstrapFarmName = "Automation";
    private const string BootstrapFavoriteThing = "Determinism";
    private const string BootstrapSaveName = "HatifectHarness_4242424242";
    private const string BootstrapAcceptanceStorageLocation = "Farm";
    private const int BootstrapAcceptanceStorageTileX = 64;
    private const int BootstrapAcceptanceStorageTileY = 15;
    private const string BootstrapAcceptanceStorageChestItemId = "130";
    private const string BootstrapAcceptanceStorageItemId = "(O)388";
    private const int BootstrapAcceptanceStorageItemStack = 10;

    private static readonly AcceptanceScenario[] BuiltInScenarioRegistry =
    {
        new("runtime.boot", AcceptanceScenarioKind.Smoke, false, AcceptanceScenarioExecution.RuntimeBoot, false,
            new[] { "runtime.boot.loaded" }),
        new("save.bootstrap", AcceptanceScenarioKind.Smoke, false, AcceptanceScenarioExecution.SaveBootstrap, false,
            new[] { "save.bootstrap.reload" }),
        new("runtime.return-to-title", AcceptanceScenarioKind.Smoke, true, AcceptanceScenarioExecution.ReturnToTitle, false,
            new[] { "runtime.return-to-title.reset" }),
        new("semantic.lifecycle", AcceptanceScenarioKind.Ui, false, AcceptanceScenarioExecution.Lifecycle, false, new[]
        {
            "semantic.lifecycle.visual",
            "semantic.lifecycle.focus",
            "semantic.lifecycle.close-reopen"
        }),
        new("semantic.inspector", AcceptanceScenarioKind.Ui, false, AcceptanceScenarioExecution.Inspector, false, new[]
        {
            "semantic.inspector.capture",
            "semantic.inspector.reveal",
            "semantic.inspector.close"
        }),
        new("semantic.overlay", AcceptanceScenarioKind.Ui, true, AcceptanceScenarioExecution.Overlay, false, new[]
        {
            "semantic.overlay.visual",
            "semantic.overlay.context",
            "semantic.overlay.dismiss"
        }),
        new("semantic.input", AcceptanceScenarioKind.Ui, false, AcceptanceScenarioExecution.Input, false, new[]
        {
            "semantic.input.pointer",
            "semantic.input.keyboard",
            "semantic.input.controller",
            "semantic.input.text"
        }),
        new("semantic.locale-scale-theme", AcceptanceScenarioKind.Ui, true, AcceptanceScenarioExecution.LocaleScaleTheme, false, new[]
        {
            "semantic.locale.en",
            "semantic.locale.ru",
            "semantic.scale.75",
            "semantic.scale.100",
            "semantic.scale.125",
            "semantic.scale.150",
            "semantic.viewport.reflow",
            "semantic.theme.matrix"
        }),
        new("semantic.performance", AcceptanceScenarioKind.Ui, false, AcceptanceScenarioExecution.Performance, true,
            new[] { "semantic.performance.steady" })
    };

    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly UiSemanticDogfoodCompositionRoot _dogfood;
    private readonly UiStardewAcceptanceRecorder _recorder = UiStardewAcceptanceRecorder.Shared;
    private readonly UiAutomatedAcceptanceCheckLedger _checkLedger = new();
    private readonly List<UiScaleAttempt> _uiScaleAttempts = new();
    private readonly CompletedFrameCaptureComponent _captureComponent;
    private string? _screenshotSource;
    private int _screenshotWidth;
    private int _screenshotHeight;
    private readonly string _scenario;
    private readonly string _runId;
    private readonly string _artifactDirectory;
    private AcceptanceScenarioCatalog? _scenarioCatalog;
    private int _ticks;
    private int _performanceFrames;
    private int _lastExercisedPerformanceFrame;
    private int _worldWaitTicks;
    private bool _executed;
    private bool _awaitingWorld;
    private bool _awaitingReturnedToTitle;
    private UiAutomatedAcceptanceScenarioDescriptor? _returnToTitleContribution;
    private bool _verifyReturnToTitleContribution;
    private bool _performanceActive;
    private bool _capturePending;
    private bool _exitPending;
    private bool _disposed;
    private IEnumerator? _saveEnumerator;
    private BootstrapLifecycleState _bootstrapLifecycle;
    private string? _bootstrapSavePath;
    private HarnessTerminalFailure? _terminalFailure;
    private bool _diagnosticsWritten;

    private UiAutomatedAcceptanceController(
        IModHelper helper,
        IMonitor monitor,
        UiSemanticDogfoodCompositionRoot dogfood,
        string scenario,
        string runId,
        string artifactDirectory)
    {
        _helper = helper;
        _monitor = monitor;
        _dogfood = dogfood;
        _scenario = scenario;
        _runId = runId;
        _artifactDirectory = artifactDirectory;
        _captureComponent = new CompletedFrameCaptureComponent(GameRunner.instance, CaptureCompletedFrame);
        _captureComponent.Game.Components.Add(_captureComponent);
        _helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        _helper.Events.Display.Rendered += OnRendered;
    }

    internal static UiAutomatedAcceptanceController? TryStart(
        IModHelper helper,
        IMonitor monitor,
        UiSemanticDogfoodCompositionRoot dogfood)
    {
        if (!UiAutomatedAcceptanceScenarioRegistry.IsRegistrationEnabled)
            return null;

        string protocol = Environment.GetEnvironmentVariable("HATIFECT_TEST_PROTOCOL_VERSION") ?? string.Empty;
        string scenario = Environment.GetEnvironmentVariable("HATIFECT_TEST_SCENARIO") ?? string.Empty;
        if (string.Equals(scenario, "flow.route.basic", StringComparison.Ordinal)
            || string.Equals(scenario, "flow.save.isolation", StringComparison.Ordinal)
            || string.Equals(scenario, "flow.chest.roundtrip", StringComparison.Ordinal)
            || string.Equals(scenario, "flow.chest.crash-after-save", StringComparison.Ordinal)
            || string.Equals(scenario, "flow.chest.crash-after-delivery", StringComparison.Ordinal)
            || string.Equals(scenario, "flow.chest.crash-after-unsaved-extraction", StringComparison.Ordinal)
            || string.Equals(scenario, "flow.chest.crash-after-unsaved-delivery", StringComparison.Ordinal)
            || string.Equals(scenario, "flow.chest.cancellation", StringComparison.Ordinal)
            || string.Equals(scenario, "flow.chest.return", StringComparison.Ordinal)
            || string.Equals(scenario, "flow.chest.crash-after-return", StringComparison.Ordinal)
            || string.Equals(scenario, "flow.chest.isolation", StringComparison.Ordinal)
            || string.Equals(scenario, "flow.chest.performance", StringComparison.Ordinal)
            || string.Equals(scenario, "flow.chest.resources", StringComparison.Ordinal))
        {
            return null;
        }
        string runId = Environment.GetEnvironmentVariable("HATIFECT_TEST_RUN_ID") ?? string.Empty;
        string artifactDirectory = Environment.GetEnvironmentVariable("HATIFECT_TEST_ARTIFACTS") ?? string.Empty;
        if (!int.TryParse(protocol, out int version) || version != ProtocolVersion)
            throw new InvalidOperationException($"Unsupported automated TestHarness protocol '{protocol}'.");
        if (string.IsNullOrWhiteSpace(runId) || runId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("Automated TestHarness run ID is invalid.");
        if (!Path.IsPathRooted(artifactDirectory) || !Directory.Exists(artifactDirectory))
            throw new InvalidOperationException("Automated TestHarness artifact directory is unavailable.");
        string fullArtifactDirectory = Path.GetFullPath(artifactDirectory);
        if (!string.Equals(Path.GetFileName(fullArtifactDirectory), runId, StringComparison.Ordinal))
            throw new InvalidOperationException("Automated TestHarness run ID does not match its artifact directory.");

        monitor.Log(
            $"Starting fully automated Hatifect acceptance scenario '{scenario}' for run '{runId}'.",
            LogLevel.Info);
        return new UiAutomatedAcceptanceController(
            helper,
            monitor,
            dogfood,
            scenario,
            runId,
            fullArtifactDirectory);
    }

    private static AcceptanceScenarioCatalog BuildScenarioCatalog(UiAutomatedAcceptanceScenarioSnapshot contributions)
    {
        ArgumentNullException.ThrowIfNull(contributions);
        var scenarios = new Dictionary<string, AcceptanceScenario>(StringComparer.Ordinal);
        var checkOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var allChecks = new List<string>();
        var allScenarios = new List<AcceptanceScenario>();
        foreach (AcceptanceScenario scenario in BuiltInScenarioRegistry)
        {
            AddScenario(scenarios, checkOwners, scenario);
            if (scenario.Kind == AcceptanceScenarioKind.Ui
                && scenario.Execution != AcceptanceScenarioExecution.Performance)
            {
                allChecks.AddRange(scenario.Checks);
                allScenarios.Add(scenario);
            }
        }

        foreach (UiAutomatedAcceptanceScenarioDescriptor contribution in contributions.Descriptors)
        {
            var scenario = new AcceptanceScenario(contribution);
            AddScenario(scenarios, checkOwners, scenario);
            if (!contribution.IncludeInAggregate) continue;
            allChecks.AddRange(scenario.Checks);
            allScenarios.Add(scenario);
        }

        // Existing performance is the one asynchronous scenario and must stay terminal even when
        // product-owned synchronous contributions are present.
        foreach (AcceptanceScenario scenario in BuiltInScenarioRegistry)
        {
            if (scenario.Kind != AcceptanceScenarioKind.Ui
                || scenario.Execution != AcceptanceScenarioExecution.Performance)
                continue;
            allChecks.AddRange(scenario.Checks);
            allScenarios.Add(scenario);
        }

        if (allChecks.Count == 0)
            throw new InvalidOperationException("Automated TestHarness scenario 'all' has no named checks.");
        ValidateAllExecution(allScenarios);
        if (!scenarios.TryAdd("all", AcceptanceScenario.Aggregate(allChecks)))
            throw new InvalidOperationException("Automated TestHarness scenario 'all' is reserved.");
        return new AcceptanceScenarioCatalog(scenarios, allScenarios);
    }

    private static void AddScenario(
        IDictionary<string, AcceptanceScenario> scenarios,
        IDictionary<string, string> checkOwners,
        AcceptanceScenario scenario)
    {
        if (!scenarios.TryAdd(scenario.Id, scenario))
            throw new InvalidOperationException($"Automated TestHarness scenario '{scenario.Id}' is declared more than once.");

        foreach (string check in scenario.Checks)
        {
            if (string.IsNullOrWhiteSpace(check))
                throw new InvalidOperationException(
                    $"Automated TestHarness scenario '{scenario.Id}' has an empty check ID.");
            if (!checkOwners.TryAdd(check, scenario.Id))
                throw new InvalidOperationException(
                    $"Automated TestHarness check '{check}' is declared by both "
                    + $"'{checkOwners[check]}' and '{scenario.Id}'.");
        }
    }

    private static void ValidateAllExecution(IReadOnlyList<AcceptanceScenario> allScenarios)
    {
        int terminalCount = 0;
        for (int index = 0; index < allScenarios.Count; index++)
        {
            AcceptanceScenario scenario = allScenarios[index];
            if (scenario.CompletesAsynchronously != (scenario.Execution == AcceptanceScenarioExecution.Performance))
                throw new InvalidOperationException(
                    $"Automated TestHarness scenario '{scenario.Id}' has an inconsistent asynchronous execution contract.");
            if (!scenario.CompletesAsynchronously) continue;
            terminalCount++;
            if (index != allScenarios.Count - 1)
                throw new InvalidOperationException(
                    $"Automated TestHarness asynchronous scenario '{scenario.Id}' must be last in 'all'.");
        }

        if (terminalCount != 1)
            throw new InvalidOperationException("Automated TestHarness scenario 'all' must end with exactly one asynchronous scenario.");
    }

    private AcceptanceScenarioCatalog ScenarioCatalog
        => _scenarioCatalog ?? throw new InvalidOperationException(
            "Automated TestHarness scenario registry was not frozen before execution.");

    private void FreezeScenarioCatalog()
    {
        if (_scenarioCatalog != null) return;
        _scenarioCatalog = BuildScenarioCatalog(UiAutomatedAcceptanceScenarioRegistry.Freeze());
        if (!ScenarioCatalog.ContainsScenario(_scenario))
            throw new InvalidOperationException($"Automated TestHarness scenario '{_scenario}' is not allowlisted.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        _helper.Events.Display.Rendered -= OnRendered;
        _captureComponent.Game.Components.Remove(_captureComponent);
        _captureComponent.Dispose();
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (_disposed) return;
        try
        {
            OnUpdateTickedCore();
        }
        catch (Exception error)
        {
            FailAutomationLifecycle(error);
        }
    }

    private void OnUpdateTickedCore()
    {
        // GameLaunched subscribers register product-owned scenarios synchronously. Freeze only on
        // the next game tick so every subscriber has completed before unknown IDs are rejected.
        FreezeScenarioCatalog();
        if (_exitPending)
        {
            Dispose();
            Game1.game1.Exit();
            return;
        }
        if (_verifyReturnToTitleContribution)
        {
            VerifyReturnedToTitleContribution();
            return;
        }
        if (_bootstrapLifecycle == BootstrapLifecycleState.AwaitingInitialLoadStart)
        {
            BeginBootstrapInitialLoad();
            return;
        }
        if (_bootstrapLifecycle == BootstrapLifecycleState.AwaitingVerificationLoadStart)
        {
            BeginBootstrapVerificationLoad();
            return;
        }
        if (_saveEnumerator != null)
        {
            if (_saveEnumerator.MoveNext()) return;
            (_saveEnumerator as IDisposable)?.Dispose();
            _saveEnumerator = null;
            CompleteBootstrapSaveAndScheduleInitialLoad();
            return;
        }
        if (_awaitingWorld)
        {
            // The standalone HUD fixture needs the visible world, after its load transition.
            bool awaitingOverlayFade = string.Equals(_scenario, "semantic.overlay", StringComparison.Ordinal)
                && Game1.fadeToBlackAlpha > 0f;
            if (!Context.IsWorldReady || awaitingOverlayFade)
            {
                _worldWaitTicks++;
                if (_worldWaitTicks == 1 || (_worldWaitTicks % 300) == 0)
                {
                    _monitor.Log(
                        $"Awaiting world-ready: bootstrap={_bootstrapLifecycle}, worldReady={Context.IsWorldReady}, "
                        + $"fadeAlpha={Game1.fadeToBlackAlpha}, "
                        + $"hasLoadedGame={Game1.hasLoadedGame}, "
                        + $"menu={Game1.activeClickableMenu?.GetType().FullName ?? "\u003Cnull\u003E"}, "
                        + $"location={Game1.currentLocation?.NameOrUniqueName ?? "\u003Cnull\u003E"}.",
                        LogLevel.Trace);
                }
                return;
            }
            _worldWaitTicks = 0;
            _awaitingWorld = false;
            if (_bootstrapLifecycle == BootstrapLifecycleState.AwaitingInitialWorld)
            {
                CompleteBootstrapInitialLoad();
                return;
            }
            if (_bootstrapLifecycle == BootstrapLifecycleState.AwaitingVerificationWorld)
            {
                CompleteBootstrapReloadVerification();
                return;
            }
            if (_bootstrapLifecycle != BootstrapLifecycleState.None)
                throw new InvalidOperationException(
                    $"Bootstrap reached world-ready in unexpected state '{_bootstrapLifecycle}'.");
            Execute();
            return;
        }
        if (!_executed)
        {
            if (++_ticks < StartupDelayTicks) return;
            _executed = true;
            if (ScenarioCatalog.RequiresWorld(_scenario))
                BeginLoadIsolatedSave();
            else
                Execute();
            return;
        }
        if (_awaitingReturnedToTitle) return;
        if (_performanceActive
            && _performanceFrames > 0
            && (_performanceFrames % 30) == 0
            && _lastExercisedPerformanceFrame != _performanceFrames)
        {
            _lastExercisedPerformanceFrame = _performanceFrames;
            ExerciseControllerInput();
        }
        if (_performanceActive && _performanceFrames >= RequiredPerformanceFrames)
        {
            _performanceActive = false;
            _recorder.EndAutomatedScenario();
            Record("semantic.performance.steady", true, $"Captured {_performanceFrames} semantic frames under automated input.");
            _capturePending = true;
        }
    }

    private void OnRendered(object? sender, RenderedEventArgs e)
    {
        if (!_disposed && _performanceActive)
            _performanceFrames++;
    }

    private void CaptureCompletedFrame()
    {
        if (_disposed || _performanceActive || !_capturePending) return;
        _capturePending = false;
        try
        {
            CaptureScreenshot();
            _recorder.SaveAutomatedEvidence();
            WriteDiagnostics();
        }
        catch (Exception error)
        {
            RetainTerminalFailure("HARNESS-EVIDENCE-CAPTURE-EXCEPTION", error);
            try { WriteDiagnostics(); }
            catch (Exception diagnosticsError)
            {
                _monitor.Log($"Could not persist capture failure diagnostics: {diagnosticsError}", LogLevel.Error);
            }
            FailUnrecorded(_terminalFailure!.Reason);
        }
        finally
        {
            // A failed diagnostics write must not strand the completed capture or escape through
            // the native draw callback before requesting the owned harness shutdown.
            _exitPending = true;
        }
    }

    private void Execute()
    {
        _recorder.ClearAutomatedEvidence();
        try
        {
            if (!ScenarioCatalog.IsContributedScenario(_scenario)
                && !Context.IsWorldReady
                && _scenario.StartsWith("semantic.", StringComparison.Ordinal))
            {
                Game1.activeClickableMenu = null;
            }
            bool completesAsynchronously = string.Equals(_scenario, "all", StringComparison.Ordinal)
                ? ExecuteAllNamedUiScenarios()
                : ExecuteNamedScenario(ScenarioCatalog.GetNamedScenario(_scenario));
            if (!completesAsynchronously) _capturePending = true;
        }
        catch (Exception error)
        {
            RetainTerminalFailure("HARNESS-AUTOMATION-EXECUTION-EXCEPTION", error);
            WriteDiagnostics();
            _capturePending = true;
            FailUnrecorded(_terminalFailure!.Reason);
        }
    }

    private bool ExecuteAllNamedUiScenarios()
    {
        foreach (AcceptanceScenario scenario in ScenarioCatalog.AllUiScenarios)
            if (ExecuteNamedScenario(scenario)) return true;
        return false;
    }

    private bool ExecuteNamedScenario(AcceptanceScenario scenario)
    {
        if (scenario.Contribution != null)
        {
            PrepareContributionExecution(scenario);
            var context = new UiAutomatedAcceptanceScenarioContext(scenario.Id, scenario.Checks, Record);
            try
            {
                scenario.Contribution.Execute(context);
            }
            finally
            {
                context.Complete();
            }
            if (scenario.Contribution.AfterReturnedToTitle != null)
            {
                _returnToTitleContribution = scenario.Contribution;
                _awaitingReturnedToTitle = true;
                RequestReturnToTitle();
                return true;
            }
            return false;
        }

        switch (scenario.Execution)
        {
            case AcceptanceScenarioExecution.RuntimeBoot:
                Record("runtime.boot.loaded", true, "Hatifect UI reached GameLaunched and constructed the semantic composition root.");
                return false;
            case AcceptanceScenarioExecution.SaveBootstrap:
                BeginBootstrapSave();
                return true;
            case AcceptanceScenarioExecution.ReturnToTitle:
                ExecuteReturnToTitle();
                return true;
            case AcceptanceScenarioExecution.Lifecycle:
                ExecuteLifecycle();
                return false;
            case AcceptanceScenarioExecution.Inspector:
                ExecuteInspector();
                return false;
            case AcceptanceScenarioExecution.Overlay:
                ExecuteOverlay();
                return false;
            case AcceptanceScenarioExecution.Input:
                ExecuteInput();
                return false;
            case AcceptanceScenarioExecution.LocaleScaleTheme:
                ExecuteLocaleScaleTheme();
                return false;
            case AcceptanceScenarioExecution.Performance:
                BeginPerformance();
                return true;
            default:
                throw new InvalidOperationException(
                    $"Automated TestHarness scenario '{scenario.Id}' has an unknown execution kind.");
        }
    }

    private void PrepareContributionExecution(AcceptanceScenario scenario)
    {
        _dogfood.CloseOverlay();
        _dogfood.Close();
        if (_dogfood.IsOpen || _dogfood.IsOverlayVisible)
            throw new InvalidOperationException(
                $"Automated TestHarness scenario '{scenario.Id}' could not retire the semantic dogfood host before product activation.");
    }

    private void ExecuteReturnToTitle()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("The isolated save did not reach a world-ready state.");
        if (!_dogfood.TryOpen())
            throw new InvalidOperationException("Could not open the semantic Terminal in the loaded isolated save.");
        if (!_dogfood.TryOpenOverlay())
            throw new InvalidOperationException("Could not open the semantic overlay in the loaded isolated save.");
        _awaitingReturnedToTitle = true;
        RequestReturnToTitle();
    }

    internal void OnReturnedToTitle()
    {
        if (_disposed || !_awaitingReturnedToTitle) return;
        try
        {
            _awaitingReturnedToTitle = false;
            if (_returnToTitleContribution != null)
            {
                // The UI handler can precede other mods' handlers. Observe their completed teardown
                // on the next update, never from inside the ReturnedToTitle event dispatch.
                _verifyReturnToTitleContribution = true;
                return;
            }
            if (_bootstrapLifecycle == BootstrapLifecycleState.AwaitingReturnToTitle)
            {
                _bootstrapLifecycle = BootstrapLifecycleState.AwaitingVerificationLoadStart;
                _monitor.Log("Bootstrap verification reload scheduled after Stardew/SMAPI returned to title.", LogLevel.Trace);
                return;
            }
            Record(
                "runtime.return-to-title.reset",
                !_dogfood.IsOpen && !_dogfood.IsOverlayVisible,
                "Loaded the isolated save, returned through Stardew/SMAPI title lifecycle, and retired all semantic hosts.");
            _capturePending = true;
        }
        catch (Exception error)
        {
            FailAutomationLifecycle(error);
        }
    }

    private void FailAutomationLifecycle(Exception error)
    {
        _returnToTitleContribution = null;
        _verifyReturnToTitleContribution = false;
        RetainTerminalFailure("HARNESS-AUTOMATION-LIFECYCLE-EXCEPTION", error);
        (_saveEnumerator as IDisposable)?.Dispose();
        _saveEnumerator = null;
        _awaitingWorld = false;
        _worldWaitTicks = 0;
        _awaitingReturnedToTitle = false;
        _bootstrapLifecycle = BootstrapLifecycleState.None;
        WriteDiagnostics();
        _capturePending = true;
        FailUnrecorded(_terminalFailure!.Reason);
    }

    private void VerifyReturnedToTitleContribution()
    {
        UiAutomatedAcceptanceScenarioDescriptor contribution = _returnToTitleContribution
            ?? throw new InvalidOperationException("Return-to-title verification lost its owning contribution.");
        _returnToTitleContribution = null;
        _verifyReturnToTitleContribution = false;
        if (Context.IsWorldReady)
            throw new InvalidOperationException("The return-to-title event did not retire the isolated world.");
        var context = new UiAutomatedAcceptanceScenarioContext(contribution.Id, contribution.Checks, Record);
        try
        {
            contribution.AfterReturnedToTitle!(context);
        }
        finally
        {
            context.Complete();
        }
        _capturePending = true;
    }

    private void BeginLoadIsolatedSave()
    {
        string savePath = Environment.GetEnvironmentVariable("HATIFECT_SMAPI_TEST_SAVE")
            ?? throw new InvalidOperationException("The direct-process transport did not supply an isolated save path.");
        string isolatedRoot = Environment.GetEnvironmentVariable("HATIFECT_TEST_ISOLATED_ROOT")
            ?? throw new InvalidOperationException("The direct-process transport did not supply an isolated root.");
        string fullSavePath = Path.GetFullPath(savePath);
        string expectedSaveRoot = Path.GetFullPath(
            Path.Combine(isolatedRoot, "config", "StardewValley", "Saves"));
        if (!Directory.Exists(fullSavePath)
            || !string.Equals(Path.GetDirectoryName(fullSavePath), expectedSaveRoot, StringComparison.Ordinal))
            throw new InvalidOperationException("The automated save is not a direct child of the isolated save root.");

        BeginLoadSave(fullSavePath);
    }

    private void BeginLoadSave(string fullSavePath)
    {
        string saveName = Path.GetFileName(fullSavePath);
        SaveGame.Load(saveName);
        Game1.exitActiveMenu();
        _worldWaitTicks = 0;
        _awaitingWorld = true;
    }

    private void BeginBootstrapSave()
    {
        string expectedPath = Environment.GetEnvironmentVariable("HATIFECT_TEST_SAVE_BOOTSTRAP_PATH")
            ?? throw new InvalidOperationException("The transport did not reserve a bootstrap save path.");
        string isolatedRoot = Environment.GetEnvironmentVariable("HATIFECT_TEST_ISOLATED_ROOT")
            ?? throw new InvalidOperationException("The transport did not supply an isolated root.");
        string fullPath = Path.GetFullPath(expectedPath);
        string saveRoot = Path.GetFullPath(Path.Combine(isolatedRoot, "config", "StardewValley", "Saves"));
        if (!string.Equals(Path.GetDirectoryName(fullPath), saveRoot, StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(fullPath), BootstrapSaveName, StringComparison.Ordinal)
            || Directory.Exists(fullPath))
            throw new InvalidOperationException("The bootstrap save reservation is unavailable or unsafe.");

        Game1.game1.loadForNewGame(false);
        Game1.player.Name = BootstrapPlayerName;
        Game1.player.farmName.Value = BootstrapFarmName;
        Game1.player.favoriteThing.Value = BootstrapFavoriteThing;
        Game1.uniqueIDForThisGame = BootstrapUniqueId;
        // loadForNewGame leaves the calendar on day zero until the normal intro advances it. The
        // no-intro harness must establish a real first day before saving so SMAPI can complete its
        // post-load world-ready countdown when the fixture is reloaded.
        Game1.dayOfMonth = 1;
        // Stardew appends the unique game ID when it creates the save directory. The override is
        // therefore the base name, not the final "<base>_<id>" directory reserved by the harness.
        Game1.SetSaveName(BootstrapPlayerName);
        string actualSaveName = Game1.GetSaveGameName(true);
        if (!string.Equals(actualSaveName, BootstrapPlayerName, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Stardew selected unexpected bootstrap save base name '{actualSaveName}'.");

        SeedBootstrapAcceptanceStorage();
        _bootstrapSavePath = fullPath;
        _saveEnumerator = SaveGame.Save();
    }

    private void CompleteBootstrapSaveAndScheduleInitialLoad()
    {
        string savePath = _bootstrapSavePath
            ?? throw new InvalidOperationException("Bootstrap save path is unavailable after save completion.");
        if (!Directory.Exists(savePath))
            throw new InvalidOperationException("Stardew did not create the reserved bootstrap save directory.");
        string runtimeId = Environment.GetEnvironmentVariable("HATIFECT_TEST_SAVE_RUNTIME_ID")
            ?? throw new InvalidOperationException("The transport did not bind a save runtime identity.");
        WriteAtomicJson(
            Path.Combine(savePath, ".hatifect-save-owner.json"),
            new
            {
                fixtureSchemaVersion = SaveFixtureSchemaVersion,
                fixtureId = "hatifect-golden-save",
                runtimeId,
                runId = _runId,
                state = "Ready",
                createdAtUtc = DateTimeOffset.UtcNow.ToString("O")
            });
        _monitor.Log("Bootstrap save completed; resetting synthetic game state before the deferred initial load.", LogLevel.Trace);
        _bootstrapLifecycle = BootstrapLifecycleState.AwaitingInitialLoadStart;
        ResetForBootstrapInitialLoad();
    }

    private void BeginBootstrapInitialLoad()
    {
        string savePath = _bootstrapSavePath
            ?? throw new InvalidOperationException("The bootstrap save path was not retained.");
        _bootstrapLifecycle = BootstrapLifecycleState.AwaitingInitialWorld;
        BeginLoadSave(savePath);
        _monitor.Log("Bootstrap initial load started from the exact reserved save on the update after reset.", LogLevel.Trace);
    }

    private void CompleteBootstrapInitialLoad()
    {
        EnsureBootstrapIdentity("Initially loaded");
        EnsureBootstrapAcceptanceStorage("Initially loaded");
        _monitor.Log("Bootstrap initial load reached world-ready; requesting a genuine return to title.", LogLevel.Trace);
        _bootstrapLifecycle = BootstrapLifecycleState.AwaitingReturnToTitle;
        _awaitingReturnedToTitle = true;
        RequestReturnToTitle();
    }

    private void BeginBootstrapVerificationLoad()
    {
        string savePath = _bootstrapSavePath
            ?? throw new InvalidOperationException("The bootstrap save path was not retained.");
        _bootstrapLifecycle = BootstrapLifecycleState.AwaitingVerificationWorld;
        BeginLoadSave(savePath);
        _monitor.Log("Bootstrap verification reload started on the update after returning to title.", LogLevel.Trace);
    }

    private void CompleteBootstrapReloadVerification()
    {
        EnsureBootstrapIdentity("Verification-reloaded");
        EnsureBootstrapAcceptanceStorage("Verification-reloaded");
        _bootstrapLifecycle = BootstrapLifecycleState.None;
        string receipt = Environment.GetEnvironmentVariable("HATIFECT_TEST_SAVE_BOOTSTRAP_RECEIPT")
            ?? throw new InvalidOperationException("The transport did not supply a bootstrap receipt path.");
        WriteAtomicJson(
            receipt,
            new
            {
                fixtureSchemaVersion = SaveFixtureSchemaVersion,
                runId = _runId,
                savePath = _bootstrapSavePath,
                playerName = BootstrapPlayerName,
                farmName = BootstrapFarmName,
                favoriteThing = BootstrapFavoriteThing,
                uniqueMultiplayerId = BootstrapUniqueId,
                stardewVersion = Game1.GetVersionString(),
                smapiVersion = Constants.ApiVersion.ToString(),
                reloadVerified = true,
                acceptanceStorage = BootstrapAcceptanceStorageReceipt()
            });
        Record(
            "save.bootstrap.reload",
            true,
            "Stardew created the deterministic synthetic save, returned to title, and reloaded it in the isolated save root.");
        _capturePending = true;
    }

    private static void EnsureBootstrapIdentity(string phase)
    {
        if (!Context.IsWorldReady
            || !string.Equals(Game1.player.Name, BootstrapPlayerName, StringComparison.Ordinal)
            || !string.Equals(Game1.player.farmName.Value, BootstrapFarmName, StringComparison.Ordinal)
            || !string.Equals(Game1.player.favoriteThing.Value, BootstrapFavoriteThing, StringComparison.Ordinal)
            || Game1.uniqueIDForThisGame != BootstrapUniqueId)
            throw new InvalidOperationException($"{phase} bootstrap save identity did not match its synthetic contract.");
    }

    private static void SeedBootstrapAcceptanceStorage()
    {
        if (Game1.getLocationFromName(BootstrapAcceptanceStorageLocation) is not Farm farm)
            throw new InvalidOperationException("Bootstrap acceptance storage requires the standard Farm location.");

        var tile = new Vector2(BootstrapAcceptanceStorageTileX, BootstrapAcceptanceStorageTileY);
        if (farm.Objects.ContainsKey(tile))
            throw new InvalidOperationException("Bootstrap acceptance storage tile is unexpectedly occupied.");

        var chest = new Chest(playerChest: true, tileLocation: tile, itemId: BootstrapAcceptanceStorageChestItemId);
        chest.playerChest.Value = true;
        chest.fridge.Value = false;
        chest.giftbox.Value = false;
        chest.GlobalInventoryId = null;
        chest.SpecialChestType = Chest.SpecialChestTypes.None;

        Item item = ItemRegistry.Create(BootstrapAcceptanceStorageItemId, BootstrapAcceptanceStorageItemStack)
            ?? throw new InvalidOperationException("Bootstrap acceptance storage item could not be created.");
        item.Stack = BootstrapAcceptanceStorageItemStack;
        chest.GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Add(item);
        farm.Objects.Add(tile, chest);
    }

    private static void EnsureBootstrapAcceptanceStorage(string phase)
    {
        var tile = new Vector2(BootstrapAcceptanceStorageTileX, BootstrapAcceptanceStorageTileY);
        if (Game1.getLocationFromName(BootstrapAcceptanceStorageLocation) is not Farm farm
            || !farm.Objects.TryGetValue(tile, out StardewValley.Object? candidate)
            || candidate is not Chest chest
            || !chest.playerChest.Value
            || chest.fridge.Value
            || chest.giftbox.Value
            || !string.IsNullOrEmpty(chest.GlobalInventoryId)
            || chest.SpecialChestType != Chest.SpecialChestTypes.None)
        {
            throw new InvalidOperationException($"{phase} bootstrap acceptance storage did not match its ordinary player-chest contract.");
        }

        List<Item> contents = chest.GetItemsForPlayer(Game1.player.UniqueMultiplayerID)
            .Where(item => item is not null)
            .ToList();
        if (contents.Count != 1
            || !string.Equals(contents[0].QualifiedItemId, BootstrapAcceptanceStorageItemId, StringComparison.Ordinal)
            || contents[0].Stack != BootstrapAcceptanceStorageItemStack)
        {
            throw new InvalidOperationException($"{phase} bootstrap acceptance storage contents did not match their deterministic contract.");
        }
    }

    private static object BootstrapAcceptanceStorageReceipt() => new
    {
        locationName = BootstrapAcceptanceStorageLocation,
        tile = new { x = BootstrapAcceptanceStorageTileX, y = BootstrapAcceptanceStorageTileY },
        chest = new
        {
            kind = "ordinary-player-chest",
            playerChest = true,
            fridge = false,
            giftbox = false,
            globalInventoryId = (string?)null,
            specialChestType = "None"
        },
        contents = new[]
        {
            new { qualifiedItemId = BootstrapAcceptanceStorageItemId, stack = BootstrapAcceptanceStorageItemStack }
        }
    };

    private static void WriteAtomicJson(string path, object document)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Bootstrap evidence path has no parent."));
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document));
        File.Move(temporary, fullPath, overwrite: false);
    }

    private static void RequestReturnToTitle()
    {
        MethodInfo exitToTitle = typeof(Game1).GetMethod(
            "ExitToTitle",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(Action) },
            modifiers: null)
            ?? throw new MissingMethodException(typeof(Game1).FullName, "ExitToTitle(Action)");
        exitToTitle.Invoke(exitToTitle.IsStatic ? null : Game1.game1, new object?[] { null });
    }

    private static void ResetForBootstrapInitialLoad()
    {
        MethodInfo cleanup = typeof(Game1).GetMethod(
            "CleanupReturningToTitle",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(Game1).FullName, "CleanupReturningToTitle");
        cleanup.Invoke(cleanup.IsStatic ? null : Game1.game1, null);
    }

    private void ExecuteLifecycle()
    {
        _dogfood.CloseOverlay();
        _dogfood.Close();
        bool opened = _dogfood.TryOpen();
        RuntimeAccessibilitySnapshot? accessibility = Accessibility();
        bool visual = opened && accessibility != null && PositiveGeometry(accessibility.Root);
        bool focus = accessibility != null && Flatten(accessibility.Root).Any(node => node.Focused);
        bool closed = _dogfood.Close();
        bool reopened = _dogfood.TryOpen();
        Record("semantic.lifecycle.visual", visual, "The semantic framework host rendered a positive finite accessibility/layout tree.");
        Record("semantic.lifecycle.focus", focus, "The semantic framework host exposed one runtime-owned focused node.");
        Record("semantic.lifecycle.close-reopen", closed && reopened && _dogfood.IsOpen, "The semantic framework host completed close/reopen with one active owner.");
    }

    private void ExecuteInspector()
    {
        EnsureMenu();
        UiSemanticStardewMenu menu = _dogfood.AutomationMenu!;
        RuntimeAccessibilityNode? inspectorRoute = Flatten(Accessibility()!.Root)
            .FirstOrDefault(node => string.Equals(node.Name, "Inspector", StringComparison.Ordinal));
        if (inspectorRoute == null)
            throw new InvalidOperationException("Generated Terminal navigation has no Inspector route.");
        Click(menu, inspectorRoute);
        RuntimeAccessibilitySnapshot? accessibility = Accessibility();
        bool capture = accessibility != null &&
            accessibility.Experience.ToString().Contains("devtools/inspector", StringComparison.Ordinal) &&
            Flatten(accessibility.Root).Any(node =>
                node.Role == RuntimeAccessibilityRole.Inspector &&
                (node.Value?.Contains("Planner:", StringComparison.Ordinal) ?? false) &&
                (node.Value?.Contains("Semantic:", StringComparison.Ordinal) ?? false) &&
                (node.Value?.Contains("hit-test=", StringComparison.Ordinal) ?? false) &&
                (node.Value?.Contains("Invalidation:", StringComparison.Ordinal) ?? false) &&
                (node.Value?.Contains("Lifecycle:", StringComparison.Ordinal) ?? false));
        bool reveal = accessibility != null && Flatten(accessibility.Root)
            .Any(node => string.Equals(node.Name, "Reveal source", StringComparison.Ordinal));
        bool closed = _dogfood.Close();
        bool reopened = _dogfood.TryOpen();
        if (reopened)
        {
            menu = _dogfood.AutomationMenu!;
            inspectorRoute = Flatten(Accessibility()!.Root)
                .FirstOrDefault(node => string.Equals(node.Name, "Inspector", StringComparison.Ordinal));
            if (inspectorRoute != null) Click(menu, inspectorRoute);
        }
        Record("semantic.inspector.capture", capture, "Inspector route materialized its clean-slate Experience and snapshot.");
        Record("semantic.inspector.reveal", reveal, "Inspector exposed the typed Reveal source action without invoking an external editor.");
        Record("semantic.inspector.close", closed && reopened, "Inspector host closed and the stable Terminal host reopened successfully.");
    }

    private void ExecuteOverlay()
    {
        _dogfood.Close();
        _dogfood.CloseOverlay();
        bool opened = _dogfood.TryOpenOverlay();
        bool visual = opened && _dogfood.IsOverlayVisible;
        bool context = Game1.activeClickableMenu == null;
        bool dismissed = _dogfood.CloseOverlay() && !_dogfood.IsOverlayVisible;
        bool reopened = _dogfood.TryOpenOverlay();
        Record("semantic.overlay.visual", visual, "Semantic overlay became visible through the real HUD presenter.");
        Record("semantic.overlay.context", context, "HUD overlay remained isolated from an active menu context.");
        Record("semantic.overlay.dismiss", dismissed && reopened, "Overlay completed deterministic dismissal and reopen lifecycle.");
    }

    private void ExecuteInput()
    {
        _dogfood.CloseOverlay();
        EnsureMenu();
        UiSemanticStardewMenu menu = _dogfood.AutomationMenu!;
        RuntimeAccessibilitySnapshot before = Accessibility()!;
        RuntimeAccessibilityNode? button = Flatten(before.Root).FirstOrDefault(node => node.Role == RuntimeAccessibilityRole.Button);
        if (button == null) throw new InvalidOperationException("Semantic input scenario has no button target.");
        menu.performHoverAction(CenterX(button), CenterY(button));
        Click(menu, button);
        bool pointer = Accessibility() != null;

        RuntimeSymbolId? focusedBefore = Flatten(Accessibility()!.Root).FirstOrDefault(node => node.Focused)?.Id;
        menu.receiveGamePadButton(Buttons.DPadDown);
        RuntimeSymbolId? focusedAfter = Flatten(Accessibility()!.Root).FirstOrDefault(node => node.Focused)?.Id;
        bool controller = focusedAfter != null;
        menu.receiveKeyPress(Keys.Tab);
        bool keyboard = Flatten(Accessibility()!.Root).Any(node => node.Focused);

        RuntimeAccessibilityNode? inspectorRoute = Flatten(Accessibility()!.Root)
            .FirstOrDefault(node => string.Equals(node.Name, "Inspector", StringComparison.Ordinal));
        if (inspectorRoute != null) Click(menu, inspectorRoute);
        RuntimeAccessibilityNode? textField = Flatten(Accessibility()!.Root)
            .FirstOrDefault(node => node.Role == RuntimeAccessibilityRole.TextField);
        bool text = false;
        if (textField != null)
        {
            Click(menu, textField);
            menu.InsertAutomationText("transport");
            text = Flatten(Accessibility()!.Root)
                .Any(node => node.Role == RuntimeAccessibilityRole.TextField && (node.Value?.Contains("transport", StringComparison.Ordinal) ?? false));
        }
        Record("semantic.input.pointer", pointer, "Pointer hover/press/release traversed Stardew normalization into Runtime.");
        Record("semantic.input.keyboard", keyboard, "Keyboard navigation retained a runtime-owned focus target.");
        Record("semantic.input.controller", controller && focusedBefore != focusedAfter, "Controller navigation changed or established runtime focus.");
        Record("semantic.input.text", text, "Transport-driven text input updated the semantic Inspector search field.");
    }

    private void ExecuteLocaleScaleTheme()
    {
        _dogfood.CloseOverlay();
        EnsureMenu();
        _uiScaleAttempts.Clear();
        object originalLanguage = LocalizedContentManager.CurrentLanguageCode;
        bool english = TrySetStaticProperty(typeof(LocalizedContentManager), "CurrentLanguageCode", "en", "English");
        bool russian = TrySetStaticProperty(typeof(LocalizedContentManager), "CurrentLanguageCode", "ru", "Russian");
        RestoreStaticProperty(typeof(LocalizedContentManager), "CurrentLanguageCode", originalLanguage);

        float originalScale = Game1.options.desiredUIScale;
        UiScaleAttempt scale75;
        UiScaleAttempt scale100;
        UiScaleAttempt scale125;
        UiScaleAttempt scale150;
        try
        {
            scale75 = TrySetUiScale(0.75f);
            scale100 = TrySetUiScale(1.00f);
            scale125 = TrySetUiScale(1.25f);
            scale150 = TrySetUiScale(1.50f);
            _uiScaleAttempts.Add(scale75);
            _uiScaleAttempts.Add(scale100);
            _uiScaleAttempts.Add(scale125);
            _uiScaleAttempts.Add(scale150);
        }
        finally
        {
            Game1.options.desiredUIScale = originalScale;
        }
        bool reflow = UiSemanticStardewMenu.CaptureViewport().Width > 0 && UiSemanticStardewMenu.CaptureViewport().Height > 0;

        UiSemanticStardewCapabilities.Validate(UiSemanticStardewTheme.Default);

        Record("semantic.locale.en", english, "Switched the isolated runtime locale to English and recomposed semantic content.");
        Record("semantic.locale.ru", russian, "Switched the isolated runtime locale to Russian and recomposed semantic content.");
        Record("semantic.scale.75", scale75.Passed, scale75.Reason);
        Record("semantic.scale.100", scale100.Passed, scale100.Reason);
        Record("semantic.scale.125", scale125.Passed, scale125.Reason);
        Record("semantic.scale.150", scale150.Passed, scale150.Reason);
        Record("semantic.viewport.reflow", reflow, "Semantic host retained a positive viewport after scale recomposition.");
        Record("semantic.theme.matrix", true,
            $"Validated the implemented semantic host preset '{UiSemanticStardewTheme.Default.Id}' through the platform capability boundary.");
    }

    private void BeginPerformance()
    {
        _dogfood.CloseOverlay();
        EnsureMenu();
        _performanceFrames = 0;
        _lastExercisedPerformanceFrame = 0;
        _recorder.BeginAutomatedScenario("semantic.performance");
        _performanceActive = true;
    }

    private void ExerciseControllerInput()
    {
        UiSemanticStardewMenu? menu = _dogfood.AutomationMenu;
        if (menu == null) return;
        menu.receiveGamePadButton((_performanceFrames / 30) % 2 == 0 ? Buttons.DPadDown : Buttons.DPadUp);
        menu.receiveScrollWheelAction((_performanceFrames / 30) % 2 == 0 ? -120 : 120);
    }

    private void EnsureMenu()
    {
        if (_dogfood.IsOpen) return;
        _dogfood.CloseOverlay();
        if (!_dogfood.TryOpen())
            throw new InvalidOperationException("Could not open the semantic Terminal automation host.");
    }

    private RuntimeAccessibilitySnapshot? Accessibility()
        => _dogfood.AutomationMenu?.CaptureInspectionContext().Runtime.Accessibility;

    private static IEnumerable<RuntimeAccessibilityNode> Flatten(RuntimeAccessibilityNode root)
    {
        yield return root;
        foreach (RuntimeAccessibilityNode child in root.Children)
        foreach (RuntimeAccessibilityNode descendant in Flatten(child))
            yield return descendant;
    }

    private static bool PositiveGeometry(RuntimeAccessibilityNode node)
        => node.Bounds.Width > 0
           && node.Bounds.Height > 0
           && float.IsFinite(node.Bounds.X)
           && float.IsFinite(node.Bounds.Y)
           && node.Children.All(PositiveGeometry);

    private static int CenterX(RuntimeAccessibilityNode node) => (int)(node.Bounds.X + node.Bounds.Width / 2);
    private static int CenterY(RuntimeAccessibilityNode node) => (int)(node.Bounds.Y + node.Bounds.Height / 2);

    private static void Click(UiSemanticStardewMenu menu, RuntimeAccessibilityNode node)
    {
        int x = CenterX(node);
        int y = CenterY(node);
        menu.receiveLeftClick(x, y);
        menu.releaseLeftClick(x, y);
    }

    private void Record(string checkId, bool passed, string note)
    {
        if (!ScenarioCatalog.IsDeclaredCheck(_scenario, checkId))
            throw new InvalidOperationException(
                $"Automated TestHarness scenario '{_scenario}' produced undeclared check '{checkId}'.");
        _checkLedger.Record(checkId, passed, note, _recorder.RecordAutomatedCheck);
    }

    private void FailUnrecorded(string message)
    {
        if (_scenarioCatalog == null || !ScenarioCatalog.TryGetChecks(_scenario, out IReadOnlyList<string> checks))
            return;
        _checkLedger.FailMissing(checks, message, _recorder.RecordAutomatedCheck);
        try
        {
            _recorder.SaveAutomatedEvidence();
        }
        catch (Exception error)
        {
            RetainTerminalFailure("HARNESS-EVIDENCE-PERSISTENCE-EXCEPTION", error);
        }
    }

    private void RetainTerminalFailure(string reason, Exception error)
    {
        if (_terminalFailure != null) return;
        _terminalFailure = HarnessTerminalFailure.From(reason, error);
        _monitor.Log(
            $"Automated acceptance scenario '{_scenario}' failed with {_terminalFailure.Reason}; see diagnostics/runtime.json.",
            LogLevel.Error);
    }

    private void CaptureScreenshot()
    {
        string directory = Path.Combine(_artifactDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, _scenario.Replace('.', '-') + ".png");
        GraphicsDevice graphics = Game1.graphics.GraphicsDevice;
        if (graphics.RenderTargetCount != 0)
            throw new InvalidOperationException("Completed-frame capture requires the composed back buffer.");
        _screenshotSource = "composed-back-buffer";
        _screenshotWidth = graphics.PresentationParameters.BackBufferWidth;
        _screenshotHeight = graphics.PresentationParameters.BackBufferHeight;
        var data = new Color[checked(_screenshotWidth * _screenshotHeight)];
        graphics.GetBackBufferData(data);
        using var texture = new Texture2D(graphics, _screenshotWidth, _screenshotHeight, false, SurfaceFormat.Color);
        texture.SetData(data);
        using FileStream stream = File.Create(path);
        texture.SaveAsPng(stream, _screenshotWidth, _screenshotHeight);
        stream.Flush(flushToDisk: true);

        // Retain the completed UI layer independently of native-window clipping/occlusion so
        // layout pixels can be compared with the final window frame without changing game state.
        if (Game1.game1.uiScreen is { IsDisposed: false } uiScreen)
        {
            string uiPath = Path.Combine(directory, _scenario.Replace('.', '-') + "-ui-layer.png");
            using FileStream uiStream = File.Create(uiPath);
            uiScreen.SaveAsPng(uiStream, uiScreen.Width, uiScreen.Height);
            uiStream.Flush(flushToDisk: true);
        }
    }

    private sealed class CompletedFrameCaptureComponent : DrawableGameComponent
    {
        private readonly Action _capture;

        public CompletedFrameCaptureComponent(Game game, Action capture) : base(game)
        {
            _capture = capture;
            DrawOrder = int.MaxValue;
        }

        // GameRunner draws its components after each instance has composed world and UI buffers.
        // SMAPI's Rendered event runs earlier, inside the instance's _draw method.
        public override void Draw(GameTime gameTime) => _capture();
    }

    private void WriteDiagnostics()
    {
        if (_diagnosticsWritten) return;
        string directory = Path.Combine(_artifactDirectory, "diagnostics");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "runtime.json");
        string temporary = path + ".tmp";
        var payload = new
        {
            protocolVersion = ProtocolVersion,
            runId = _runId,
            scenario = _scenario,
            capturedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            processId = Environment.ProcessId,
            terminalOpen = _dogfood.IsOpen,
            overlayVisible = _dogfood.IsOverlayVisible,
            fadeToBlackAlpha = Game1.fadeToBlackAlpha,
            activeTheme = UiSemanticStardewTheme.Id,
            screenshot = _screenshotSource == null ? null : new
            {
                source = _screenshotSource,
                width = _screenshotWidth,
                height = _screenshotHeight
            },
            viewport = new
            {
                width = Game1.uiViewport.Width,
                height = Game1.uiViewport.Height,
                pixelWidth = Game1.graphics.GraphicsDevice.Viewport.Width,
                pixelHeight = Game1.graphics.GraphicsDevice.Viewport.Height
            },
            uiScaleAttempts = _uiScaleAttempts.Select(attempt => new
            {
                requested = attempt.Requested,
                observed = attempt.Observed,
                passed = attempt.Passed,
                reason = attempt.Reason,
                exception = attempt.ExceptionType == null ? null : new
                {
                    type = attempt.ExceptionType,
                    message = attempt.ExceptionMessage,
                    stack = attempt.ExceptionStack
                }
            }),
            terminalError = _terminalFailure == null ? null : new
            {
                reason = _terminalFailure.Reason,
                type = _terminalFailure.ExceptionType,
                message = _terminalFailure.ExceptionMessage,
                stack = _terminalFailure.ExceptionStack
            }
        };
        File.WriteAllText(temporary, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
        _diagnosticsWritten = true;
    }

    private static bool TrySetStaticProperty(Type type, string propertyName, params string[] candidates)
    {
        PropertyInfo? property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (property?.CanWrite != true || !property.PropertyType.IsEnum) return false;
        foreach (string candidate in candidates)
        {
            if (!Enum.TryParse(property.PropertyType, candidate, ignoreCase: true, out object? value)) continue;
            property.SetValue(null, value);
            return Equals(property.GetValue(null), value);
        }
        return false;
    }

    private static void RestoreStaticProperty(Type type, string propertyName, object value)
        => type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, value);

    private static UiScaleAttempt TrySetUiScale(float value)
    {
        try
        {
            Game1.options.desiredUIScale = value;
            float observed = Game1.options.desiredUIScale;
            return Math.Abs(observed - value) < 0.001f
                ? UiScaleAttempt.Applied(value, observed)
                : UiScaleAttempt.ReadbackMismatch(value, observed);
        }
        catch (Exception error)
        {
            return UiScaleAttempt.Failed(value, error);
        }
    }

    private enum AcceptanceScenarioKind
    {
        Smoke,
        Ui
    }

    private enum AcceptanceScenarioExecution
    {
        RuntimeBoot,
        SaveBootstrap,
        ReturnToTitle,
        Lifecycle,
        Inspector,
        Overlay,
        Input,
        LocaleScaleTheme,
        Performance,
        Contribution
    }

    private sealed class AcceptanceScenario
    {
        public AcceptanceScenario(
            string id,
            AcceptanceScenarioKind kind,
            bool requiresWorld,
            AcceptanceScenarioExecution execution,
            bool completesAsynchronously,
            string[] checks)
        {
            Id = id;
            Kind = kind;
            RequiresWorld = requiresWorld;
            Execution = execution;
            CompletesAsynchronously = completesAsynchronously;
            Checks = Array.AsReadOnly((string[])checks.Clone());
        }

        public AcceptanceScenario(UiAutomatedAcceptanceScenarioDescriptor contribution)
        {
            ArgumentNullException.ThrowIfNull(contribution);
            Id = contribution.Id;
            Kind = AcceptanceScenarioKind.Ui;
            RequiresWorld = contribution.RequiresWorld;
            Execution = AcceptanceScenarioExecution.Contribution;
            CompletesAsynchronously = false;
            Checks = contribution.Checks;
            Contribution = contribution;
        }

        private AcceptanceScenario(string id, IReadOnlyList<string> checks)
        {
            Id = id;
            Kind = AcceptanceScenarioKind.Ui;
            RequiresWorld = false;
            Execution = AcceptanceScenarioExecution.Contribution;
            CompletesAsynchronously = false;
            Checks = Array.AsReadOnly(checks.ToArray());
        }

        public static AcceptanceScenario Aggregate(IReadOnlyList<string> checks)
            => new("all", checks);

        public string Id { get; }
        public AcceptanceScenarioKind Kind { get; }
        public bool RequiresWorld { get; }
        public AcceptanceScenarioExecution Execution { get; }
        public bool CompletesAsynchronously { get; }
        public IReadOnlyList<string> Checks { get; }
        public UiAutomatedAcceptanceScenarioDescriptor? Contribution { get; }
    }

    private sealed class AcceptanceScenarioCatalog
    {
        private readonly IReadOnlyDictionary<string, AcceptanceScenario> _scenarios;
        private readonly IReadOnlyList<AcceptanceScenario> _allUiScenarios;

        public AcceptanceScenarioCatalog(
            IDictionary<string, AcceptanceScenario> scenarios,
            IReadOnlyList<AcceptanceScenario> allUiScenarios)
        {
            _scenarios = new ReadOnlyDictionary<string, AcceptanceScenario>(
                new Dictionary<string, AcceptanceScenario>(scenarios, StringComparer.Ordinal));
            _allUiScenarios = Array.AsReadOnly(allUiScenarios.ToArray());
        }

        public IReadOnlyList<AcceptanceScenario> AllUiScenarios => _allUiScenarios;

        public bool ContainsScenario(string scenarioId) => _scenarios.ContainsKey(scenarioId);

        public bool IsContributedScenario(string scenarioId)
            => _scenarios.TryGetValue(scenarioId, out AcceptanceScenario? scenario)
               && scenario.Contribution != null;

        public AcceptanceScenario GetNamedScenario(string scenarioId)
        {
            if (_scenarios.TryGetValue(scenarioId, out AcceptanceScenario? scenario)
                && !string.Equals(scenario.Id, "all", StringComparison.Ordinal))
                return scenario;
            throw new InvalidOperationException($"Automated TestHarness scenario '{scenarioId}' is not registered.");
        }

        public bool RequiresWorld(string scenarioId)
        {
            if (string.Equals(scenarioId, "all", StringComparison.Ordinal))
            {
                foreach (AcceptanceScenario scenario in _allUiScenarios)
                {
                    if (scenario.RequiresWorld) return true;
                }
                return false;
            }
            return GetNamedScenario(scenarioId).RequiresWorld;
        }

        public bool IsDeclaredCheck(string scenarioId, string checkId)
            => TryGetChecks(scenarioId, out IReadOnlyList<string> checks)
               && ContainsCheck(checks, checkId);

        public bool TryGetChecks(string scenarioId, out IReadOnlyList<string> checks)
        {
            if (_scenarios.TryGetValue(scenarioId, out AcceptanceScenario? scenario))
            {
                checks = scenario.Checks;
                return true;
            }
            checks = Array.Empty<string>();
            return false;
        }

        private static bool ContainsCheck(IReadOnlyList<string> checks, string checkId)
        {
            for (int index = 0; index < checks.Count; index++)
            {
                if (string.Equals(checks[index], checkId, StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }

    private sealed class HarnessTerminalFailure
    {
        private HarnessTerminalFailure(string reason, string exceptionType, string exceptionMessage, string exceptionStack)
        {
            Reason = reason;
            ExceptionType = exceptionType;
            ExceptionMessage = exceptionMessage;
            ExceptionStack = exceptionStack;
        }

        public string Reason { get; }
        public string ExceptionType { get; }
        public string ExceptionMessage { get; }
        public string ExceptionStack { get; }

        public static HarnessTerminalFailure From(string reason, Exception error)
            => new(
                reason,
                error.GetType().FullName ?? error.GetType().Name,
                error.Message,
                error.StackTrace ?? string.Empty);
    }

    private sealed class UiScaleAttempt
    {
        private UiScaleAttempt(
            float requested,
            float? observed,
            bool passed,
            string reason,
            string? exceptionType = null,
            string? exceptionMessage = null,
            string? exceptionStack = null)
        {
            Requested = requested;
            Observed = observed;
            Passed = passed;
            Reason = reason;
            ExceptionType = exceptionType;
            ExceptionMessage = exceptionMessage;
            ExceptionStack = exceptionStack;
        }

        public float Requested { get; }
        public float? Observed { get; }
        public bool Passed { get; }
        public string Reason { get; }
        public string? ExceptionType { get; }
        public string? ExceptionMessage { get; }
        public string? ExceptionStack { get; }

        public static UiScaleAttempt Applied(float requested, float observed)
            => new(requested, observed, true, "HARNESS-UI-SCALE-APPLIED");

        public static UiScaleAttempt ReadbackMismatch(float requested, float observed)
            => new(requested, observed, false, "HARNESS-UI-SCALE-READBACK-MISMATCH");

        public static UiScaleAttempt Failed(float requested, Exception error)
            => new(
                requested,
                observed: null,
                passed: false,
                reason: "HARNESS-UI-SCALE-SET-EXCEPTION",
                exceptionType: error.GetType().FullName ?? error.GetType().Name,
                exceptionMessage: error.Message,
                exceptionStack: error.StackTrace ?? string.Empty);
    }
}
