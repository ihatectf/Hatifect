using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Rectangle = xTile.Dimensions.Rectangle;
using StardewValley;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Semantics;
using Hatifect.UI.Stardew.Semantic;

namespace Hatifect.UI.Stardew;

internal sealed partial class UiAutomatedAcceptanceController
{
    private static readonly string[] EnvironmentCases = { "window", "terminal", "hud", "active-menu" };
    private readonly List<object> _environmentObservations = new();
    private readonly List<object> _environmentAutomaticObservations = new();
    private readonly List<TerminalGenerationCompletion> _environmentOperations = new();
    private IUiSemanticReloadSession? _environmentSurface;
    private UiPortalHandle? _environmentPortal;
    private ActionPumpCoverMenu? _environmentCover;
    private Rectangle _environmentOriginalViewport;
    private float _environmentOriginalScale;
    private float _environmentOriginalDesiredScale;
    private bool _environmentOriginalGamepad;
    private Options.GamepadModes _environmentOriginalGamepadMode;
    private LocalizedContentManager.LanguageCode _environmentOriginalLanguage;
    private StardewValley.GameData.ModLanguage? _environmentOriginalModLanguage;
    private string? _environmentOriginalLocale;
    private bool _environmentSettingsCaptured;
    private bool _environmentActive;
    private bool _environmentCompleted;
    private bool _advancingEnvironment;
    private int _environmentCase;
    private int _environmentTicks;
    private int _environmentOwnerThread;
    private TerminalGenerationCompletion? _environmentRootOperation;
    private TerminalGenerationCompletion? _environmentPopupOperation;
    private UiEnvironment? _environmentBeforeAutomatic;
    private string? _environmentAutomaticLocale;
    private UiInputMode _environmentAutomaticInput;
    private float _environmentAutomaticScale;
    private bool _environmentAwaitingAutomatic;

    private void BeginEnvironment()
    {
        _dogfood.CloseOverlay();
        _dogfood.Close();
        RequireAction(Game1.activeClickableMenu == null, "Environment acceptance requires a free isolated menu slot.");
        _environmentOriginalViewport = Game1.uiViewport;
        _environmentOriginalScale = Game1.options.baseUIScale;
        _environmentOriginalDesiredScale = Game1.options.desiredUIScale;
        _environmentOriginalGamepad = Game1.options.gamepadControls;
        _environmentOriginalGamepadMode = Game1.options.gamepadMode;
        _environmentOriginalLanguage = LocalizedContentManager.CurrentLanguageCode;
        _environmentOriginalModLanguage = LocalizedContentManager.CurrentModLanguage;
        _environmentOriginalLocale = LocalizedContentManager.LanguageCodeString(_environmentOriginalLanguage);
        _environmentSettingsCaptured = true;
        _environmentOwnerThread = Environment.CurrentManagedThreadId;
        _environmentActive = true;
        _advancingEnvironment = true;
        try { BeginEnvironmentCase(); }
        finally { _advancingEnvironment = false; }
    }

    private void BeginEnvironmentCase()
    {
        _environmentAwaitingAutomatic = false;
        string kind = EnvironmentCases[_environmentCase];
        UiSymbolId id = ActionId("environment/" + kind);
        Game1.uiViewport = new Rectangle(0, 0, 1200, 700);
        Game1.options.baseUIScale = Game1.options.desiredUIScale = 1;
        Game1.options.gamepadControls = false;
        LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.en;
        var source = new EnvironmentSource();
        var draft = new UiState<string>("Retained environment draft");
        UiActionDefinition action = EnvironmentAction(id.Child("run"), operation => _environmentRootOperation = operation);
        var model = new UiExperienceBuilder(id, "Environment acceptance").Monitor("Status", source)
            .Search("Name", draft).Actions("Actions", action).Build();
        var api = new UiSemanticSurfaceService(_helper);
        if (kind == "active-menu") Game1.activeClickableMenu = _environmentCover = new ActionPumpCoverMenu();
        IUiSemanticSurfaceSession surface;
        if (kind == "terminal")
        {
            UiSymbolId terminalId = id.Child("terminal");
            surface = api.CreateTerminal(new UiSemanticTerminalDefinition(terminalId,
                new[] { new UiSemanticTerminalSection(model) }), new UiSemanticSurfaceOptions(terminalId));
        }
        else surface = kind == "active-menu"
            ? api.CreateActiveMenuOverlay(model, new UiSemanticSurfaceOptions(id))
            : api.CreateSurface(model, kind == "hud" ? UiSemanticHostKind.Hud : UiSemanticHostKind.Window,
                new UiSemanticSurfaceOptions(id));
        _environmentSurface = (IUiSemanticReloadSession)surface;
        ((IUiSemanticAppearanceSession)surface).SetTheme(UiSemanticTheme.Dark);
        surface.Show();
        UiSemanticStardewHost host = CaptureReloadHost(_environmentSurface);
        RequireAction(host.Session.Root.Actions.Invoke(action), "Environment root did not admit its pending action.");
        var popupAction = EnvironmentAction(id.Child("popup-run"), operation => _environmentPopupOperation = operation);
        var popupPolicy = new UiHostPolicy(UiHostKind.Popup, UiWindowChrome.Tool, UiDismissPolicy.Escape,
            UiModalPolicy.Modeless, UiFocusScopePolicy.Contained, UiPopupPlacement.Anchor);
        _environmentPortal = host.Present(new UiPortalRequest(id.Child("popup"),
            new UiPortalOwner(host.Session.Root.Scene.Root.Id), ActionScene("environment-popup", popupPolicy, popupAction),
            new UiHostPlacementContext(new UiRect(0, 0, 1200, 700), anchor: new UiRect(400, 300, 30, 30))));
        RequireAction(host.Session.Submit().Interaction?.ActionInvoked == true && _environmentPopupOperation != null,
            "Environment popup did not admit its pending action.");
        var focused = host.Session.Root.Interactions.Snapshot.Focused;
        var modelIdentity = EnvironmentInvocation(surface, host).Experience;
        var transitions = new List<object>();
        void Observe(string facet, UiSemanticTheme theme)
        {
            UiEnvironment actual = ReloadField<UiEnvironment>(surface, "_environment");
            UiEnvironment expected = UiSemanticStardewEnvironmentCapture.Capture(UiSemanticStardewMenu.CaptureViewport(), theme);
            var invocation = EnvironmentInvocation(surface, host);
            RequireAction(actual == expected && ReferenceEquals(actual, invocation.Plan.Host.Environment)
                && invocation.Plan.Host.Profile == UiPresentationProfiles.Resolve(actual).Id
                && host.Session.Root.Scene.MeasurementContext.Locale == actual.Locale
                && host.Session.Root.Scene.MeasurementContext.Theme == actual.Theme
                && ReferenceEquals(modelIdentity, invocation.Experience)
                && host.Session.Root.Interactions.Snapshot.Focused == focused
                && host.Session.Accessibility.Portals.Count == 1
                && !_environmentRootOperation!.Cancelled && !_environmentPopupOperation!.Cancelled,
                "The " + facet + " transition mixed environment, scene, model, focus or pending work.");
            transitions.Add(new { facet, environment = actual, profile = invocation.Plan.Host.Profile.ToString(),
                decisions = invocation.Plan.Decisions.Where(decision => decision.Code == UiPlanDecisionCode.EnvironmentFacet)
                    .Select(decision => decision.Message).ToArray() });
        }
        Observe("initial", UiSemanticTheme.Dark);
        Game1.uiViewport = new Rectangle(0, 0, 1280, 700);
        surface.Synchronize();
        Observe("same-profile-viewport", UiSemanticTheme.Dark);
        Game1.options.baseUIScale = Game1.options.desiredUIScale = 1.25f;
        surface.Synchronize();
        Observe("scale", UiSemanticTheme.Dark);
        Game1.options.gamepadControls = true;
        surface.Synchronize();
        Observe("input", UiSemanticTheme.Dark);
        LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.ru;
        surface.Synchronize();
        Observe("locale", UiSemanticTheme.Dark);
        ((IUiSemanticAppearanceSession)surface).SetTheme(UiSemanticTheme.HighContrast);
        Observe("theme-accessibility", UiSemanticTheme.HighContrast);
        Record("semantic.environment." + kind + ".facets", true,
            "All environment facets reach one accepted scene without replacing the model, focus or pending root/popup work.");

        var beforeEnvironment = ReloadField<UiEnvironment>(surface, "_environment");
        var beforeInvocation = EnvironmentInvocation(surface, host);
        var beforeScene = host.Session.Root.Scene;
        source.Reject = true;
        Game1.uiViewport = new Rectangle(0, 0, 700, 600);
        Exception? rejection = null;
        try { surface.Synchronize(); }
        catch (InvalidOperationException error) { rejection = error; }
        source.Reject = false;
        bool retained = rejection?.Message == EnvironmentSource.Rejection
            && ReferenceEquals(beforeEnvironment, ReloadField<UiEnvironment>(surface, "_environment"))
            && ReferenceEquals(beforeInvocation, EnvironmentInvocation(surface, host))
            && ReferenceEquals(beforeScene, host.Session.Root.Scene)
            && !_environmentRootOperation!.Cancelled && !_environmentPopupOperation!.Cancelled;
        RequireAction(retained, "Rejected native environment preparation did not retain its accepted frame.");
        surface.Synchronize();
        Observe("retry", UiSemanticTheme.HighContrast);
        Record("semantic.environment." + kind + ".rejection", retained,
            "A throwing source rejects the candidate before publication; retry accepts a coherent new environment.");

        beforeEnvironment = ReloadField<UiEnvironment>(surface, "_environment");
        UiScene? nested = null;
        source.OnRead = () =>
        {
            source.OnRead = null;
            host.Session.Root.RefreshInteractionVisuals();
            nested = host.Session.Root.Scene;
        };
        Game1.uiViewport = new Rectangle(0, 0, 900, 600);
        rejection = null;
        try { surface.Synchronize(); }
        catch (InvalidOperationException error) { rejection = error; }
        bool nestedRetained = rejection != null && nested != null && ReferenceEquals(nested, host.Session.Root.Scene)
            && ReferenceEquals(beforeEnvironment, ReloadField<UiEnvironment>(surface, "_environment"))
            && nested.MeasurementContext.Locale == beforeEnvironment.Locale
            && nested.MeasurementContext.Theme == beforeEnvironment.Theme;
        RequireAction(nestedRetained, "An outer native environment candidate replaced the accepted nested frame.");
        surface.Synchronize();
        Observe("reentry-retry", UiSemanticTheme.HighContrast);
        Record("semantic.environment." + kind + ".reentry", nestedRetained,
            "Nested input composition uses accepted environment/theme; its accepted frame survives stale outer preparation.");

        var allocations = new long[256];
        // Warm the exact interface-dispatch call site used by measurement. A separate
        // warmup loop leaves its first dispatch allocation inside the measured region.
        MeasureEnvironmentIdle(surface, allocations);
        int reads = source.Reads;
        long version = host.Session.Root.AcceptedVersion;
        long allocated = MeasureEnvironmentIdle(surface, allocations);
        bool idle = source.Reads == reads && host.Session.Root.AcceptedVersion == version && allocated == 0;
        Record("semantic.environment." + kind + ".idle", idle,
            "256 unchanged synchronizations retain the frame without source reads or managed allocations.");
        _environmentObservations.Add(new { kind, transitions, retained, nestedRetained, idle,
            unchangedSynchronizations = 256, sourceReads = source.Reads - reads, allocatedBytes = allocated, allocations,
            draft = draft.Value });

        // Restore native options before waiting for production Update. Any further automatic
        // environment change must preserve the same pending work as the explicit transitions.
        RestoreEnvironmentSettings();
        surface.Synchronize();
        _environmentRootOperation!.CompleteFromWorker(51);
        _environmentPopupOperation!.CompleteFromWorker(52);
        _environmentTicks = 0;
    }

    private bool AdvanceEnvironment()
    {
        if (!_environmentActive) return false;
        _advancingEnvironment = true;
        try
        {
            if (++_environmentTicks > 600) throw new TimeoutException(_environmentAwaitingAutomatic
                ? "Native automatic environment transition timed out." : "Native environment action delivery timed out.");
            if (!_environmentAwaitingAutomatic)
            {
                if (!EnvironmentDelivered(_environmentRootOperation!, 51) || !EnvironmentDelivered(_environmentPopupOperation!, 52))
                    return true;
                Record("semantic.environment." + EnvironmentCases[_environmentCase] + ".delivery", true,
                    "Production native Update delivers both retained action results once on the owning thread.");
                _environmentBeforeAutomatic = ReloadField<UiEnvironment>(_environmentSurface!, "_environment");
                Game1.options.baseUIScale = Game1.options.desiredUIScale = Game1.options.uiScale == 1.5f ? 1 : 1.5f;
                Game1.options.gamepadControls = !Game1.options.gamepadControls;
                // Auto may replace this requested facet on the next native input poll. Use the
                // game's explicit mode while observing the host, then restore the user's mode.
                Game1.options.gamepadMode = Game1.options.gamepadControls
                    ? Options.GamepadModes.ForceOn : Options.GamepadModes.ForceOff;
                LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.ru
                    ? LocalizedContentManager.LanguageCode.en : LocalizedContentManager.LanguageCode.ru;
                _environmentAutomaticLocale = UiSemanticStardewEnvironmentCapture.ResolveLocale(LocalizedContentManager.CurrentLanguageCode);
                _environmentAutomaticInput = Game1.options.gamepadControls ? UiInputMode.Controller : UiInputMode.MouseKeyboard;
                _environmentAutomaticScale = Game1.options.uiScale;
                _environmentAwaitingAutomatic = true;
                _environmentTicks = 0;
                CaptureAutomaticEnvironment("requested");
                // Only observe on subsequent game ticks. No Refresh, Synchronize, Update or
                // Pump call in this driver may apply the requested automatic transition.
                return true;
            }
            if (_environmentTicks is 1 or 600) CaptureAutomaticEnvironment("waiting");
            var surface = _environmentSurface!;
            UiEnvironment environment = ReloadField<UiEnvironment>(surface, "_environment");
            UiEnvironment expected = UiSemanticStardewEnvironmentCapture.Capture(UiSemanticStardewMenu.CaptureViewport(), UiSemanticTheme.HighContrast);
            if (ReferenceEquals(environment, _environmentBeforeAutomatic) || environment != expected
                || environment.Locale != _environmentAutomaticLocale || environment.InputMode != _environmentAutomaticInput
                || environment.Scale != _environmentAutomaticScale) return true;
            UiSemanticStardewHost host = CaptureReloadHost(surface);
            var invocation = EnvironmentInvocation(surface, host);
            bool automatic = surface.Visible && ReferenceEquals(environment, invocation.Plan.Host.Environment)
                && invocation.Plan.Host.Profile == UiPresentationProfiles.Resolve(environment).Id
                && host.Session.Root.Scene.MeasurementContext.Locale == environment.Locale
                && host.Session.Root.Scene.MeasurementContext.Theme == environment.Theme;
            Record("semantic.environment." + EnvironmentCases[_environmentCase] + ".automatic", automatic,
                "Native lifecycle accepts changed scale/input/locale and derived viewport without consumer synchronization.");
            _environmentObservations.Add(new { kind = EnvironmentCases[_environmentCase] + "-automatic",
                automatic, ticks = _environmentTicks, environment });
            RequireAction(automatic, "Automatic environment preparation mixed accepted metadata and scene.");
            CloseEnvironmentCase();
            if (++_environmentCase < EnvironmentCases.Length) BeginEnvironmentCase();
            else
            {
                _environmentCompleted = true;
                StopEnvironment();
                ExecuteEnvironmentShowOwner();
                _capturePending = true;
            }
            return true;
        }
        finally { _advancingEnvironment = false; }
    }

    private void CaptureAutomaticEnvironment(string phase)
    {
        var surface = _environmentSurface!;
        UiEnvironment accepted = ReloadField<UiEnvironment>(surface, "_environment");
        UiEnvironment native = UiSemanticStardewEnvironmentCapture.Capture(UiSemanticStardewMenu.CaptureViewport(), UiSemanticTheme.HighContrast);
        _environmentAutomaticObservations.Add(new { kind = EnvironmentCases[_environmentCase], phase, ticks = _environmentTicks,
            accepted, native, requestedScale = _environmentAutomaticScale, requestedInput = _environmentAutomaticInput,
            requestedLocale = _environmentAutomaticLocale, sameAcceptedFrame = ReferenceEquals(accepted, _environmentBeforeAutomatic),
            matchesNative = accepted == native, visible = surface.Visible, gameIsActiveNoOverlay = Game1.game1.IsActiveNoOverlay,
            gamepadMode = Game1.options.gamepadMode.ToString(), gamepadControls = Game1.options.gamepadControls,
            baseScale = Game1.options.baseUIScale, desiredScale = Game1.options.desiredUIScale, appliedScale = Game1.options.uiScale });
    }

    private bool EnvironmentDelivered(TerminalGenerationCompletion operation, int expected)
    {
        if (!operation.WorkerFinished || operation.Callbacks == 0) return false;
        RequireAction(operation.Callbacks == 1 && operation.Result == expected && operation.CapturedValue == 7
            && operation.CallbackThread == _environmentOwnerThread && operation.WorkerThread != _environmentOwnerThread
            && operation.SourceReads == 1 && !operation.ObserverDuringDriver && !operation.Cancelled,
            "Native environment result violated pending retention or owner-thread delivery.");
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureEnvironmentIdle(IUiSemanticSurfaceSession surface, long[] allocations)
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < allocations.Length; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            surface.Synchronize();
            allocations[i] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        return GC.GetAllocatedBytesForCurrentThread() - allocated;
    }

    private UiActionDefinition EnvironmentAction(UiSymbolId id, Action<TerminalGenerationCompletion> acceptOperation)
    {
        TerminalGenerationCompletion? operation = null;
        return new UiAction<int, int>(id, "Run environment probe", (_, token) =>
        {
            operation = new TerminalGenerationCompletion(token);
            _environmentOperations.Add(operation);
            acceptOperation(operation);
            return new ValueTask<UiActionResult<int>>(operation, 0);
        }, UiActionConcurrency.RejectWhileRunning).Bind(() => 7, (request, result) =>
        {
            operation!.Callbacks++;
            operation.Result = result.Value;
            operation.CapturedValue = request;
            operation.CallbackThread = Environment.CurrentManagedThreadId;
            operation.ObserverDuringDriver = _advancingEnvironment;
        });
    }

    private static UiInvocationResult EnvironmentInvocation(IUiSemanticSurfaceSession surface, UiSemanticStardewHost host)
        => surface is IUiSemanticTerminalSession ? host.CurrentInvocation : ReloadField<UiInvocationResult>(surface, "_invocation");

    private void CloseEnvironmentCase()
    {
        _environmentPortal?.Dispose();
        _environmentPortal = null;
        _environmentSurface?.Dispose();
        _environmentSurface = null;
        if (_environmentCover != null && ReferenceEquals(Game1.activeClickableMenu, _environmentCover))
            Game1.activeClickableMenu = null;
        _environmentCover = null;
    }

    private void RestoreEnvironmentSettings()
    {
        if (!_environmentSettingsCaptured) return;
        Game1.uiViewport = _environmentOriginalViewport;
        Game1.options.baseUIScale = _environmentOriginalScale;
        Game1.options.desiredUIScale = _environmentOriginalDesiredScale;
        Game1.options.gamepadMode = _environmentOriginalGamepadMode;
        Game1.options.gamepadControls = _environmentOriginalGamepad;
        if (_environmentOriginalLanguage == LocalizedContentManager.LanguageCode.mod)
            LocalizedContentManager.SetModLanguage(_environmentOriginalModLanguage
                ?? throw new InvalidOperationException("The original environment custom language descriptor is unavailable."));
        else
            LocalizedContentManager.CurrentLanguageCode = _environmentOriginalLanguage;
        RequireAction(LocalizedContentManager.CurrentLanguageCode == _environmentOriginalLanguage
            && ReferenceEquals(LocalizedContentManager.CurrentModLanguage, _environmentOriginalModLanguage)
            && LocalizedContentManager.LanguageCodeString(_environmentOriginalLanguage) == _environmentOriginalLocale,
            "Environment acceptance did not restore the original language identity and custom descriptor.");
    }

    private void StopEnvironment()
    {
        _environmentActive = false;
        try { CloseEnvironmentCase(); }
        finally
        {
            try { RestoreEnvironmentSettings(); }
            finally
            {
                foreach (var operation in _environmentOperations) operation.CompleteFromWorker(0, fault: true);
            }
        }
    }

    private void ExecuteEnvironmentShowOwner()
    {
        var first = new ActionPumpCoverMenu();
        var replacement = new ActionPumpCoverMenu();
        Game1.activeClickableMenu = first;
        UiSymbolId id = ActionId("environment/show-owner");
        var source = new EnvironmentSource();
        var model = new UiExperienceBuilder(id, "Show owner").Monitor("Status", source).Build();
        var surface = new UiSemanticSurfaceService(_helper).CreateActiveMenuOverlay(model, new UiSemanticSurfaceOptions(id));
        var host = CaptureReloadHost((IUiSemanticReloadSession)surface);
        var before = host.Session.Root.Scene;
        var environment = ReloadField<UiEnvironment>(surface, "_environment");
        try
        {
            Game1.options.baseUIScale = Game1.options.uiScale == 1.25f ? 1 : 1.25f;
            source.OnRead = () => { source.OnRead = null; Game1.activeClickableMenu = replacement; };
            Exception? rejection = null;
            try { surface.Show(); }
            catch (InvalidOperationException error) { rejection = error; }
            bool retained = rejection != null && !surface.Visible && ReferenceEquals(Game1.activeClickableMenu, replacement)
                && ReferenceEquals(before, host.Session.Root.Scene)
                && ReferenceEquals(environment, ReloadField<UiEnvironment>(surface, "_environment"));
            surface.Show();
            bool retried = surface.Visible && ReferenceEquals(Game1.activeClickableMenu, replacement)
                && !ReferenceEquals(environment, ReloadField<UiEnvironment>(surface, "_environment"));
            Record("semantic.environment.active-menu.show-owner", retained && retried,
                "First Show rejects a candidate whose callback replaces its native owner; retry binds the new owner.");
            _environmentObservations.Add(new { kind = "active-menu-show-owner", retained, retried, failure = rejection?.Message,
                visible = surface.Visible, sceneRetained = ReferenceEquals(before, host.Session.Root.Scene) });
        }
        finally
        {
            surface.Dispose();
            RestoreEnvironmentSettings();
            if (ReferenceEquals(Game1.activeClickableMenu, replacement) || ReferenceEquals(Game1.activeClickableMenu, first))
                Game1.activeClickableMenu = null;
        }
    }

    private sealed class EnvironmentSource : IUiSemanticSource<string>
    {
        internal const string Rejection = "Controlled native environment source rejection.";
        internal bool Reject { get; set; }
        internal Action? OnRead { get; set; }
        internal int Reads { get; private set; }
        public Type ValueType => typeof(string);
        public string Value
        {
            get
            {
                Reads++;
                if (Reject) throw new InvalidOperationException(Rejection);
                OnRead?.Invoke();
                return "Environment source";
            }
        }
        public object UntypedValue => Value;
        public event Action? Changed { add { } remove { } }
    }
}
