using System.Runtime.ExceptionServices;
using Hatifect.ChestsAnywhereOverlay.Integration;
using Hatifect.ChestsAnywhereOverlay.Models;
using Hatifect.UI;
using Hatifect.UI.Experience;

namespace Hatifect.ChestsAnywhereOverlay.UI.Semantic;

/// <summary>
/// Product-owned live acceptance for the optional Chests Anywhere Overlay. Compatible, absent,
/// and incompatible dependency states are deliberately independent scenarios so one fixture can
/// never turn a negative contract into a false positive for another.
/// </summary>
internal static class SemanticChestsAnywhereOverlayAcceptanceScenarios
{
    private const string CompatibleScenario = "semantic.chests-anywhere-overlay";
    private const string AbsentScenario = "semantic.chests-anywhere-overlay.absent";
    private const string IncompatibleScenario = "semantic.chests-anywhere-overlay.incompatible";
    private const string CaptureExceptionScenario = "semantic.chests-anywhere-overlay.capture-exception";
    private const string ReturnToTitleScenario = "semantic.chests-anywhere-overlay.return-to-title";

    private static readonly string[] CompatibleChecks =
    {
        CompatibleScenario + ".open",
        CompatibleScenario + ".views",
        CompatibleScenario + ".handoff",
        CompatibleScenario + ".restoration",
        CompatibleScenario + ".lifecycle"
    };

    internal static void Register(
        ChestsAnywhereAdapter adapter,
        ChestsAnywhereOverlayController controller,
        SemanticChestsAnywhereOverlayFrontend frontend,
        IUiSemanticSurfaceApi ui)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(frontend);
        ArgumentNullException.ThrowIfNull(ui);
        IUiSemanticSurfaceAutomation automation = ui.Automation;
        if (!automation.IsEnabled) return;

        automation.Register(
            new UiAutomatedAcceptanceScenario(
                CompatibleScenario,
                order: 700,
                requiresWorld: true,
                CompatibleChecks,
                context => ExecuteCompatible(adapter, controller, frontend, ui, context)));
        automation.Register(
            new UiAutomatedAcceptanceScenario(
                AbsentScenario,
                order: 710,
                requiresWorld: false,
                new[] { AbsentScenario + ".fail-closed" },
                context => ExecuteAbsent(adapter, context),
                includeInAggregate: false));
        automation.Register(
            new UiAutomatedAcceptanceScenario(
                IncompatibleScenario,
                order: 720,
                requiresWorld: false,
                new[] { IncompatibleScenario + ".fail-closed" },
                context => ExecuteIncompatible(adapter, context),
                includeInAggregate: false));
        automation.Register(
            new UiAutomatedAcceptanceScenario(
                CaptureExceptionScenario,
                order: 730,
                requiresWorld: true,
                new[] { CaptureExceptionScenario + ".fail-closed" },
                context => ExecuteCaptureException(adapter, controller, frontend, context),
                includeInAggregate: false));
        automation.Register(UiAutomatedAcceptanceScenario.ForReturnToTitle(
            ReturnToTitleScenario,
            order: 740,
            new[] { ReturnToTitleScenario + ".open", ReturnToTitleScenario + ".restored" },
            beforeReturnToTitle: context => PrepareReturnToTitle(adapter, controller, frontend, context),
            afterReturnedToTitle: context => VerifyReturnToTitle(adapter, controller, frontend, context)));
    }

    private static void PrepareReturnToTitle(
        ChestsAnywhereAdapter adapter,
        ChestsAnywhereOverlayController controller,
        SemanticChestsAnywhereOverlayFrontend frontend,
        IUiAutomatedAcceptanceContext context)
    {
        controller.Hide();
        adapter.EndSession();
        OpenThroughNativeToggle(adapter, controller);
        frontend.PumpAutomatedAcceptance();
        bool opened = controller.IsVisible && frontend.Visible
            && controller.HasActiveSessionForAcceptance
            && adapter.HasSuppressedNativeSelectors && adapter.HasMutedNativeToggle
            && adapter.HasCurrentAutomatedOverlayLease;
        context.Record(ReturnToTitleScenario + ".open", opened,
            "The real native CA menu and one semantic surface acquired all owners before the framework requested return to title.");
        if (!opened)
            throw new InvalidOperationException("The title-lifecycle scenario could not establish a complete CA session.");
    }

    private static void VerifyReturnToTitle(
        ChestsAnywhereAdapter adapter,
        ChestsAnywhereOverlayController controller,
        SemanticChestsAnywhereOverlayFrontend frontend,
        IUiAutomatedAcceptanceContext context)
    {
        // Observe before cleanup. Calling Shutdown here would conceal a missing production event handler.
        bool restored = IsRestored(controller, frontend, adapter)
            && !controller.HasActiveSessionForAcceptance
            && !adapter.IsOverlayActive;
        bool leaseRetired = adapter.TryReleaseAutomatedLeaseAfterReturnedToTitle(out string diagnostic);
        context.Record(ReturnToTitleScenario + ".restored",
            restored && leaseRetired && !adapter.HasAutomatedOverlayLease,
            restored && leaseRetired
                ? "After every real ReturnedToTitle handler finished, CA had no frontend, snapshot, selector/toggle lease, or active native overlay; bookkeeping retired without mutating the title menu."
                : "CA retained ownership after the real ReturnedToTitle lifecycle: " + diagnostic);
    }

    private static void ExecuteCompatible(
        ChestsAnywhereAdapter adapter,
        ChestsAnywhereOverlayController controller,
        SemanticChestsAnywhereOverlayFrontend frontend,
        IUiSemanticSurfaceApi ui,
        IUiAutomatedAcceptanceContext context)
    {
        var recorder = new CheckRecorder(context, CompatibleChecks);
        Exception? failure = null;
        string? favoriteKey = null;
        bool favoriteInitiallySet = false;
        var activations = new List<string>();

        try
        {
            var actionApi = ui as IUiSemanticSurfaceActionAutomationApi
                ?? throw new InvalidOperationException("Typed CA acceptance requires the optional action-input API.");
            if (!actionApi.ActionAutomation.IsEnabled)
                throw new InvalidOperationException("Typed CA acceptance requires enabled action input.");
            IUiSemanticSurfaceAutomation automation = ui.Automation;
            controller.Hide();
            adapter.EndSession();
            OpenThroughNativeToggle(adapter, controller);
            ChestsAnywhereOverlayApplicationSnapshot snapshot = controller.CaptureApplicationSnapshot();
            if (snapshot.Storages.Count == 0)
                throw new InvalidOperationException(
                    "Chests Anywhere opened, but the isolated acceptance fixture exposes no storage entries.");

            frontend.PumpAutomatedAcceptance();
            ChestsAnywhereNavigatorExperienceSession? experience = frontend.AcceptanceExperience;
            IUiSemanticSurfaceSession? firstSurface = frontend.AcceptanceSurface;
            bool opened = controller.IsVisible
                && frontend.Visible
                && experience is { IsCompleted: false }
                && firstSurface is { Visible: true }
                && adapter.HasSuppressedNativeSelectors
                && adapter.HasMutedNativeToggle;
            recorder.Record(
                CompatibleChecks[0],
                opened,
                opened
                    ? "The production overlay owns one semantic session while the exact native selector/toggle leases are active."
                    : "The production overlay did not acquire a complete semantic/native ownership session.");
            if (!opened) return;

            ChestsAnywhereNavigatorExperienceSession active = experience!;
            controller.Update();
            frontend.PumpAutomatedAcceptance();
            bool repeatedNativeTickKeptOneSession = ReferenceEquals(firstSurface, frontend.AcceptanceSurface)
                && ReferenceEquals(active, frontend.AcceptanceExperience);
            ChestsAnywhereNavigatorStorage selected = Selected(active);
            favoriteKey = selected.Key;
            favoriteInitiallySet = controller.IsFavorite(favoriteKey);
            if (favoriteInitiallySet) Activate(frontend, actionApi, active, "toggle-favorite", activations);
            Activate(frontend, actionApi, active, "toggle-favorite", activations);
            Activate(frontend, actionApi, active, "mode-favorites", activations);
            bool favorites = active.Mode.Value == ChestsAnywhereNavigatorMode.Favorites
                && active.Storages.Value.Any(storage => storage.Key == favoriteKey);
            Activate(frontend, actionApi, active, "mode-recent", activations);
            bool recent = active.Mode.Value == ChestsAnywhereNavigatorMode.Recent;
            Activate(frontend, actionApi, active, "mode-categories", activations);
            Activate(frontend, actionApi, active, "select-category", activations);
            Activate(frontend, actionApi, active, "refresh", activations);
            bool category = active.Mode.Value == ChestsAnywhereNavigatorMode.Category
                && active.Categories.Value.Count > 0;
            bool views = category && favorites && recent;
            recorder.Record(
                CompatibleChecks[1],
                views,
                views
                    ? "Category, Favorites, Recent, category selection and refresh used normalized Tab/Enter through typed host bindings: " + string.Join("; ", activations)
                    : "The narrow Category/Favorites/Recent view contract did not round-trip through the product port.");
            if (!views) return;

            automation.Cancel(firstSurface!, UiSemanticSurfaceCancelInput.ControllerBack);
            bool controllerBackRestored = IsRestored(controller, frontend, adapter);

            CloseAutomatedNativeSession(adapter, controller);
            OpenThroughNativeToggle(adapter, controller);
            frontend.PumpAutomatedAcceptance();
            IUiSemanticSurfaceSession? escapeSurface = frontend.AcceptanceSurface;
            ChestsAnywhereNavigatorExperienceSession? escapeExperience = frontend.AcceptanceExperience;
            bool freshAfterControllerBack = escapeSurface is { Visible: true }
                && escapeExperience is { IsCompleted: false }
                && !ReferenceEquals(firstSurface, escapeSurface)
                && !ReferenceEquals(active, escapeExperience);
            controller.Update();
            frontend.PumpAutomatedAcceptance();
            bool repeatedOpenKeptOneSession = ReferenceEquals(escapeSurface, frontend.AcceptanceSurface)
                && ReferenceEquals(escapeExperience, frontend.AcceptanceExperience);
            if (escapeSurface != null)
                automation.Cancel(escapeSurface, UiSemanticSurfaceCancelInput.Escape);
            bool escapeRestored = IsRestored(controller, frontend, adapter);
            bool restored = controllerBackRestored
                && freshAfterControllerBack
                && repeatedNativeTickKeptOneSession
                && repeatedOpenKeptOneSession
                && escapeRestored;
            recorder.Record(
                CompatibleChecks[3],
                restored,
                restored
                    ? "Controller B and Escape each terminally closed one semantic session, restored the exact native leases, and repeated Show retained one owner."
                    : "Controller B/Escape restoration or single-session reopening did not preserve native ownership.");
            if (!restored) return;

            CloseAutomatedNativeSession(adapter, controller);
            OpenThroughNativeToggle(adapter, controller);
            frontend.PumpAutomatedAcceptance();
            ChestsAnywhereNavigatorExperienceSession? handoffExperience = frontend.AcceptanceExperience;
            IUiSemanticSurfaceSession? handoffSurface = frontend.AcceptanceSurface;
            if (handoffExperience is not { IsCompleted: false } || handoffSurface is not { Visible: true })
                throw new InvalidOperationException("The semantic handoff session did not open.");
            ChestsAnywhereNavigatorStorage handoffStorage = Selected(handoffExperience);
            Activate(frontend, actionApi, handoffExperience, "open", activations);
            controller.Update();
            ChestsAnywhereStorageHandoff? handoff = handoffExperience.Handoff.Value;
            ChestsAnywhereOverlayApplicationSnapshot afterHandoff = controller.CaptureApplicationSnapshot();
            bool handedOff = handoff?.StorageId == handoffStorage.Id
                && handoff.StorageKey == handoffStorage.Key
                && afterHandoff.CurrentStorageKey == handoffStorage.Key
                && afterHandoff.RecentKeys.Contains(handoffStorage.Key, StringComparer.Ordinal)
                && adapter.IsOverlayActive
                && adapter.HasCurrentAutomatedOverlayLease
                && IsRestored(controller, frontend, adapter);
            recorder.Record(
                CompatibleChecks[2],
                handedOff,
                handedOff
                    ? "Typed Open via normalized Tab/Enter delegated to SelectChest, retired the stale semantic menu identity and left the native chest open: " + activations[^1]
                    : "The semantic handoff did not leave the selected native Chests Anywhere menu current with all Hatifect leases restored.");
            if (!handedOff) return;

            CloseAutomatedNativeSession(adapter, controller);
            OpenThroughNativeToggle(adapter, controller);
            frontend.PumpAutomatedAcceptance();
            IUiSemanticSurfaceSession? postHandoffSurface = frontend.AcceptanceSurface;
            bool freshAfterHandoff = postHandoffSurface is { Visible: true }
                && !ReferenceEquals(handoffSurface, postHandoffSurface);
            ChestsAnywhereNavigatorExperienceSession postHandoffExperience = frontend.AcceptanceExperience
                ?? throw new InvalidOperationException("The typed close session did not open.");
            Activate(frontend, actionApi, postHandoffExperience, "close", activations);
            bool typedCloseRestored = IsRestored(controller, frontend, adapter);
            bool retiredInputRejected = false;
            try { actionApi.ActionAutomation.Activate(postHandoffSurface!, postHandoffExperience.Experience.Id.Child("action/refresh")); }
            catch (InvalidOperationException) { retiredInputRejected = true; }
            CloseAutomatedNativeSession(adapter, controller);
            OpenThroughNativeToggle(adapter, controller);
            frontend.PumpAutomatedAcceptance();
            controller.Shutdown();
            bool shutdownRestored = IsRestored(controller, frontend, adapter);
            string closeDiagnostic = string.Empty;
            bool nativeClosed;
            try
            {
                CloseAutomatedNativeSession(adapter, controller);
                nativeClosed = true;
            }
            catch (Exception closeFailure)
            {
                closeDiagnostic = closeFailure.Message;
                nativeClosed = false;
            }
            bool lifecycle = freshAfterHandoff
                && typedCloseRestored && retiredInputRejected
                && shutdownRestored
                && nativeClosed
                && !adapter.IsOverlayActive
                && IsRestored(controller, frontend, adapter);
            recorder.Record(
                CompatibleChecks[4],
                lifecycle,
                lifecycle
                    ? "Typed Close restored every owner, retired input was rejected, fresh reopen and Shutdown/native close also restored ownership: " + activations[^1]
                    : "Overlay shutdown/close lifecycle did not fully reset: " + closeDiagnostic);
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            if (favoriteKey != null)
            {
                Attempt(() =>
                {
                    if (controller.IsFavorite(favoriteKey) != favoriteInitiallySet)
                        controller.ToggleFavorite(favoriteKey);
                }, cleanupFailures);
            }
            Attempt(controller.Hide, cleanupFailures);
            Attempt(adapter.EndSession, cleanupFailures);
            Attempt(() =>
            {
                if (!adapter.TryCloseAutomatedOverlay(out string diagnostic))
                    throw new InvalidOperationException(diagnostic);
            }, cleanupFailures);
            Attempt(controller.Update, cleanupFailures);
            if (cleanupFailures.Count > 0)
            {
                Exception cleanup = cleanupFailures.Count == 1
                    ? cleanupFailures[0]
                    : new AggregateException(cleanupFailures);
                failure = failure == null ? cleanup : new AggregateException(failure, cleanup);
            }
            recorder.Complete(failure);
        }
    }

    private static void Activate(
        SemanticChestsAnywhereOverlayFrontend frontend,
        IUiSemanticSurfaceActionAutomationApi api,
        ChestsAnywhereNavigatorExperienceSession experience,
        string actionName,
        ICollection<string> evidence)
    {
        frontend.PumpAutomatedAcceptance();
        IUiSemanticSurfaceSession surface = frontend.AcceptanceSurface
            ?? throw new InvalidOperationException("The typed input surface is missing.");
        if (!ReferenceEquals(experience, frontend.AcceptanceExperience))
            throw new InvalidOperationException("The typed input experience was replaced.");
        UiSymbolId action = experience.Experience.Id.Child("action/" + actionName);
        UiSemanticSurfaceSnapshot before = api.Observation.Capture(surface);
        bool admitted = api.ActionAutomation.Activate(surface, action);
        if (!admitted) throw new InvalidOperationException("The host did not admit typed action " + action);
        evidence.Add($"{action}; surface={before.InstanceId}; initialScene={before.AcceptedFrame?.SceneVersion}; initialFrame={before.AcceptedFrame?.FrameVersion}; input=Tab/Enter; admitted=true");
        frontend.PumpAutomatedAcceptance();
    }

    private static void ExecuteAbsent(
        ChestsAnywhereAdapter adapter,
        IUiAutomatedAcceptanceContext context)
    {
        bool passed = !adapter.IsSupported
            && !adapter.HasSuppressedNativeSelectors
            && !adapter.HasMutedNativeToggle;
        context.Record(
            AbsentScenario + ".fail-closed",
            passed,
            passed
                ? "Without Chests Anywhere the optional overlay remains inert and owns no native state."
                : "The absent-dependency fixture unexpectedly exposed a supported or leased Chests Anywhere boundary.");
    }

    private static void ExecuteIncompatible(
        ChestsAnywhereAdapter adapter,
        IUiAutomatedAcceptanceContext context)
    {
        bool passed = !adapter.IsSupported
            && !adapter.HasSuppressedNativeSelectors
            && !adapter.HasMutedNativeToggle
            && adapter.HasRejectedAutomatedIncompatibleApi;
        context.Record(
            IncompatibleScenario + ".fail-closed",
            passed,
            passed
                ? "The installed-dependency preflight passed, then the production adapter rejected the exact harness-injected incompatible raw API without acquiring native leases."
                : "The incompatible raw API did not traverse the production binding path or acquired native ownership unexpectedly.");
    }

    private static void ExecuteCaptureException(
        ChestsAnywhereAdapter adapter,
        ChestsAnywhereOverlayController controller,
        SemanticChestsAnywhereOverlayFrontend frontend,
        IUiAutomatedAcceptanceContext context)
    {
        Exception? failure = null;
        bool failClosed = false;
        bool nativeClosed = false;
        try
        {
            controller.Hide();
            adapter.EndSession();
            OpenThroughNativeToggle(adapter, controller);
            frontend.PumpAutomatedAcceptance();
            bool ownedBeforeFailure = controller.IsVisible
                && frontend.Visible
                && adapter.HasSuppressedNativeSelectors
                && adapter.HasMutedNativeToggle
                && adapter.HasCurrentAutomatedOverlayLease;

            adapter.ArmAutomatedCaptureFailure();
            controller.Update();
            failClosed = ownedBeforeFailure
                && !adapter.IsSupported
                && adapter.HasCurrentAutomatedOverlayLease
                && IsRestored(controller, frontend, adapter);
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            Attempt(controller.Hide, cleanupFailures);
            Attempt(adapter.EndSession, cleanupFailures);
            Attempt(() =>
            {
                if (!adapter.TryCloseAutomatedOverlay(out string diagnostic))
                    throw new InvalidOperationException(diagnostic);
                nativeClosed = !adapter.HasCurrentAutomatedOverlayLease;
            }, cleanupFailures);
            Attempt(controller.Update, cleanupFailures);
            if (cleanupFailures.Count > 0)
            {
                Exception cleanup = cleanupFailures.Count == 1
                    ? cleanupFailures[0]
                    : new AggregateException(cleanupFailures);
                failure = failure == null ? cleanup : new AggregateException(failure, cleanup);
            }
        }

        bool passed = failure == null && failClosed && nativeClosed;
        context.Record(
            CaptureExceptionScenario + ".fail-closed",
            passed,
            passed
                ? "One production capture acquisition threw; the adapter disabled only the integration, synchronously restored UI/native leases, kept the native menu owned until cleanup, and then closed the exact automated menu."
                : "Capture exception recovery did not restore every integration owner synchronously: " + failure?.Message);
    }

    private static bool IsRestored(
        ChestsAnywhereOverlayController controller,
        SemanticChestsAnywhereOverlayFrontend frontend,
        ChestsAnywhereAdapter adapter)
        => !controller.IsVisible
            && !frontend.Visible
            && frontend.AcceptanceExperience == null
            && frontend.AcceptanceSurface == null
            && !adapter.HasSuppressedNativeSelectors
            && !adapter.HasMutedNativeToggle;

    private static void OpenThroughNativeToggle(
        ChestsAnywhereAdapter adapter,
        ChestsAnywhereOverlayController controller)
    {
        if (!adapter.TryOpenAutomatedOverlay(out string diagnostic))
            throw new InvalidOperationException(diagnostic);
        controller.Update();
        if (!controller.IsVisible)
        {
            throw new InvalidOperationException(
                "The exact automated native toggle edge did not open Hatifect through the production controller lifecycle.");
        }
    }

    private static void CloseAutomatedNativeSession(
        ChestsAnywhereAdapter adapter,
        ChestsAnywhereOverlayController controller)
    {
        adapter.EndSession();
        if (!adapter.TryCloseAutomatedOverlay(out string diagnostic))
            throw new InvalidOperationException(diagnostic);
        controller.Update();
        if (adapter.IsOverlayActive)
            throw new InvalidOperationException("The exact automated Chests Anywhere session remained active after close.");
    }

    private static ChestsAnywhereNavigatorStorage Selected(ChestsAnywhereNavigatorExperienceSession session)
    {
        UiSymbolId id = session.Storages.SelectedItemId
            ?? throw new InvalidOperationException("The semantic overlay has no selected storage.");
        return session.Storages.Value.Single(storage => storage.Id == id);
    }

    private static void Attempt(Action action, ICollection<Exception> failures)
    {
        try { action(); }
        catch (Exception error) { failures.Add(error); }
    }

    internal sealed class CheckRecorder
    {
        private readonly IUiAutomatedAcceptanceContext _context;
        private readonly IReadOnlyList<string> _checks;
        private readonly HashSet<string> _recorded = new(StringComparer.Ordinal);

        internal CheckRecorder(IUiAutomatedAcceptanceContext context, IReadOnlyList<string> checks)
        {
            _context = context;
            _checks = checks;
        }

        internal void Record(string id, bool passed, string note)
        {
            if (_recorded.Add(id)) _context.Record(id, passed, note);
        }

        internal void Complete(Exception? failure)
        {
            string note = failure?.Message
                ?? "The compatible Chests Anywhere Overlay scenario stopped before every required gate.";
            foreach (string id in _checks)
            {
                if (_recorded.Add(id))
                    _context.Record(id, false, note);
            }
            if (failure != null)
                ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
