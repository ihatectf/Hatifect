using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Hatifect.Flow.Application;
using Hatifect.Flow.UI.Semantic;
using Hatifect.UI.Experience;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData;
using StardewValley.Menus;
using static Hatifect.Flow.Diagnostics.FlowHostAcceptance;

namespace Hatifect.Flow.Diagnostics;

// This driver owns only copied-world lifecycle and fake domain effects. Actual frame observation
// is supplied by the separate adapter to the framework's exact-harness observation API.
internal sealed class FlowUiAcceptance : IDisposable
{
    internal const string Scenario = "flow.ui.isolation";
    private static readonly string[] Checks = { "loaded", "empty", "missing", "publication", "locale", "scale",
        "controller-profile", "unavailable", "faulted", "close", "unsubscribe-retry", "save-isolation",
        "retired-handles", "restored", "read-only" };
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly AcceptanceRequest _request;
    private readonly string _fingerprint;
    private readonly Func<IFlowApplication, Guid?, Func<Guid, string>, IUiSemanticSurfaceApi, ParcelExperience> _show;
    private readonly Action _close;
    private readonly Func<Frame, bool> _observe;
    private readonly FlowUiAcceptanceObservation _observation;
    private readonly SurfaceApi _api;
    private readonly string[] _saveHashes;
    private readonly List<FlowUiAcceptanceWorld> _worlds = new(4);
    private readonly List<View> _views = new(8);
    private readonly List<Exception> _errors = new();
    private readonly HashSet<string> _passed = new(StringComparer.Ordinal);
    private readonly List<object> _observations = new(19);
    private readonly List<object> _restorations = new(3);
    private View? _view;
    private IClickableMenu? _coverMenu;
    private FlowUiAcceptanceWorld? _world;
    private Stage _stage;
    private int _frames, _loads, _titles, _stableFrames;
    private (int Width, int Height, int UiWidth, int UiHeight)? _readySize;
    private long _publicationBeforeLocale, _publicationBeforeUpdate;
    private float _requestedScale;
    private bool _requestedController;
    private bool _settingsOwned, _disposed, _failed;
    private LocalizedContentManager.LanguageCode _originalLanguage;
    private ModLanguage? _originalModLanguage;
    private string? _originalLocale;
    private Options? _originalOptions;
    private float _originalBaseScale, _originalDesiredScale;
    private bool _originalGamepad;
    private Options.GamepadModes _originalGamepadMode;

    private enum Stage { Startup, Loading, AwaitNativeReady, EmptyEnglish, EmptyRussian, Missing, CargoRussian, CargoEnglish,
        Scale75, Scale100, Scale125, Scale150, Controller, Paused, Recovery, Resumed, Updated,
        Faulted, Closed, Retry, BeforeReturn, Returning, Reload, ReopenedWorld, Final, RestoringForTitle, RestoringFinal, Exit }

    // Same stable native owner fixture used by UI environment acceptance. Game1.SetWindowSize
    // reconstructs an exact GameMenu; that correctly retires an overlay and cannot test retention.
    private sealed class FlowUiAcceptanceCoverMenu : IClickableMenu { }

    internal sealed record Frame(string Name, ParcelExperience Experience, IUiSemanticSurfaceSession Surface,
        string Locale, float Scale, bool Controller, string Cargo, string Route, string State,
        string Availability, string Result, string[] EnabledActions, long PublicationVersion);

    private sealed class View
    {
        internal View(FlowUiAcceptanceWorld world, ParcelExperience experience, IUiSemanticSurfaceSession surface)
        {
            World = world; Experience = experience; Surface = surface;
            Sources = experience.Experience.Elements.Select(element => element.Source).ToArray();
            Actions = experience.Experience.Actions.ToArray();
            surface.Rendered += Rendered;
            surface.Closed += Closed;
        }
        internal FlowUiAcceptanceWorld World { get; }
        internal ParcelExperience Experience { get; }
        internal IUiSemanticSurfaceSession Surface { get; }
        internal IUiSemanticSource?[] Sources { get; }
        internal object?[]? RetiredValues;
        internal long RetiredVersion;
        internal UiActionDefinition[] Actions { get; }
        internal int Renders, Closes, RetiredRenders;
        private void Rendered() => Renders++;
        private void Closed()
        {
            Closes++; RetiredRenders = Renders;
            RetiredVersion = Experience.Publication.Version;
            RetiredValues = Sources.Select(source => source?.UntypedValue).ToArray();
        }
        internal void Detach() { Surface.Rendered -= Rendered; Surface.Closed -= Closed; }
    }

    // A forwarding adapter captures the original opaque handle created by the supported production path.
    private sealed class SurfaceApi : IUiSemanticSurfaceApi
    {
        private readonly IUiSemanticSurfaceApi _inner;
        internal SurfaceApi(IUiSemanticSurfaceApi inner) => _inner = inner;
        internal IUiSemanticSurfaceActionAutomation ActionAutomation
            => ((IUiSemanticSurfaceActionAutomationApi)_inner).ActionAutomation;
        internal IUiSemanticSurfaceSession? Last { get; private set; }
        public int ApiVersion => _inner.ApiVersion;
        public IUiSemanticSurfaceAutomation Automation => _inner.Automation;
        public IUiSemanticSurfaceSession CreateActiveMenuOverlay(UiExperienceDefinition experience, UiSemanticSurfaceOptions options)
            => Last = _inner.CreateActiveMenuOverlay(experience, options);
    }

    internal static FlowUiAcceptance? TryCreate(IModHelper helper, IMonitor monitor,
        Func<IFlowApplication, Guid?, Func<Guid, string>, IUiSemanticSurfaceApi, ParcelExperience> show, Action close)
    {
        if (Environment.GetEnvironmentVariable("HATIFECT_TEST_MODE") != "1"
            || Environment.GetEnvironmentVariable("HATIFECT_TEST_AUTOMATED") != "1"
            || Environment.GetEnvironmentVariable("HATIFECT_TEST_SCENARIO") != Scenario) return null;
        AcceptanceRequest request = ReadAcceptanceRequest(helper, Scenario);
        var api = helper.ModRegistry.GetApi<IUiSemanticSurfaceActionAutomationApi>("Hatifect.UI")
            ?? throw new InvalidOperationException("Flow UI acceptance needs the owning observation and action automation API.");
        Require(api.ActionAutomation.IsEnabled, "Flow UI acceptance needs enabled exact-harness action input.");
        var observation = new FlowUiAcceptanceObservation(api.Observation, request.Artifact);
        try { return new(helper, monitor, api, show, close, observation, request); }
        catch { observation.Dispose(); throw; }
    }

    private FlowUiAcceptance(IModHelper helper, IMonitor monitor, IUiSemanticSurfaceApi api,
        Func<IFlowApplication, Guid?, Func<Guid, string>, IUiSemanticSurfaceApi, ParcelExperience> show,
        Action close, FlowUiAcceptanceObservation observation, AcceptanceRequest request)
    {
        Require(Environment.GetEnvironmentVariable("HATIFECT_TEST_MODE") == "1"
            && Environment.GetEnvironmentVariable("HATIFECT_TEST_AUTOMATED") == "1"
            && Environment.GetEnvironmentVariable("HATIFECT_TEST_SCENARIO") == Scenario,
            "Flow UI acceptance requires its exact isolated scenario.");
        _helper = helper; _monitor = monitor; _api = new(api);
        _show = show; _close = close; _observation = observation; _observe = observation.Observe;
        _request = request;
        Require(_request.SecondSavePath is not null, "Flow UI acceptance requires the provisioner's companion world.");
        _fingerprint = RuntimeFingerprint();
        _saveHashes = new[] { FlowAcceptanceSaveTree.Fingerprint(_request.SavePath),
            FlowAcceptanceSaveTree.Fingerprint(_request.SecondSavePath!) };
    }

    internal void OnSaveLoaded()
    {
        Require(_stage == Stage.Loading && _loads < 3 && Context.IsWorldReady && Context.IsMainPlayer && !Context.IsMultiplayer,
            "Unexpected Flow UI load or world authority.");
        int index = _loads == 1 ? 1 : 0;
        ulong identity = index == 0 ? 4242424242UL : 4242424243UL;
        string copyRunId = index == 0 ? _request.RunId : CompanionRunId(_request.RunId);
        string logicalName = "HatifectHarness" + Guid.Parse(copyRunId).ToString("N") + "_" + identity;
        Require(Game1.uniqueIDForThisGame == identity && Constants.SaveFolderName == logicalName,
            "Flow UI acceptance loaded a foreign logical world.");
        string path = SavePath(index);
        ValidateSaveTree(path);
        ValidateSaveOwner(path, copyRunId, _request.RuntimeId);
        RequireSavesUnchanged();
        RequireRetiredViews();
        _loads++;
        _world = new FlowUiAcceptanceWorld(index == 1);
        _worlds.Add(_world);
        _readySize = null;
        _stage = Stage.AwaitNativeReady;
        _passed.Add("loaded");
    }

    private bool NativeWorldReady()
    {
        // Initial native window normalization may reconstruct an exact GameMenu. Wait for
        // consecutive settled post-load ticks before binding the overlay to its menu owner.
        if (!Context.IsWorldReady || Game1.gameMode != 3 || Game1.gameModeTicks <= 1 || Game1.fadeToBlackAlpha > 0)
        { _readySize = null; return false; }
        var bounds = Game1.game1.Window.ClientBounds;
        var size = (bounds.Width, bounds.Height, Game1.uiViewport.Width, Game1.uiViewport.Height);
        if (size.Item1 <= 0 || size.Item2 <= 0 || size.Item3 <= 0 || size.Item4 <= 0)
        { _readySize = null; return false; }
        bool settled = _readySize == size;
        _readySize = size;
        return settled;
    }

    private void BeginWorldView()
    {
        CaptureSettings();
        Game1.activeClickableMenu = _coverMenu = new FlowUiAcceptanceCoverMenu();
        // Retain the native menu identity while the real game scale/render-target path changes.
        // Input/options are still leased before menu creation and restored after the matrix.
        SetEnvironment(false, 1, false);
        if (_loads == 1) { Open(null); _stage = Stage.EmptyEnglish; }
        else
        {
            _world!.Seed();
            Open(_world.Parcel);
            foreach (FlowUiAcceptanceWorld prior in _worlds.Take(_worlds.Count - 1))
            {
                var before = _world.ReadSnapshot();
                var rejected = _world.Execute(new FlowParcelCommand(prior.ReadSnapshot().SessionId,
                    before.Revision, prior.Parcel, FlowParcelAction.Reserve));
                Require(rejected.Status == FlowCommandStatus.Conflict && rejected.Code == FlowRejectionCode.StaleSession
                    && ReferenceEquals(before, _world.ReadSnapshot()) && _world.EffectAttempts == 0,
                    "A previous world retained mutation authority.");
            }
            _stage = Stage.ReopenedWorld;
        }
    }

    internal void Tick()
    {
        if (_disposed) return;
        try
        {
            if (_stage == Stage.Exit) { Game1.game1.Exit(); return; }
            Require(++_frames <= 18000, "Flow UI acceptance exceeded its bounded frame count.");
            switch (_stage)
            {
                case Stage.Startup when _frames >= 30:
                case Stage.Reload:
                    RequireSavesUnchanged();
                    _stage = Stage.Loading;
                    SaveGame.Load(Path.GetFileName(SavePath(_loads == 1 ? 1 : 0)));
                    Game1.exitActiveMenu();
                    break;
                case Stage.AwaitNativeReady:
                    if (NativeWorldReady()) BeginWorldView();
                    break;
                case Stage.EmptyEnglish:
                    if (!Observe("empty-en", false, "", "", "No shipment selected", "Select a shipment to inspect")) break;
                    _publicationBeforeLocale = _view!.Experience.Publication.Version;
                    SetEnvironment(true, 1, false); _stage = Stage.EmptyRussian;
                    break;
                case Stage.EmptyRussian:
                    if (!Observe("empty-ru", true, "", "", "Отправление не выбрано", "Выберите отправление для просмотра")) break;
                    Require(_view!.Experience.Publication.Version == _publicationBeforeLocale, "Locale switch republished the empty Flow model.");
                    _passed.Add("empty"); Open(_world!.Parcel); _stage = Stage.Missing;
                    break;
                case Stage.Missing:
                    if (!Observe("missing-ru", true, "", "", "Отправление больше недоступно", "Отправление больше недоступно")) break;
                    _passed.Add("missing"); _world!.Seed(); _stage = Stage.CargoRussian;
                    break;
                case Stage.CargoRussian:
                    if (!ObserveCargo("cargo-ru", true, "Готово к отправке", null, "reserve", "cancel")) break;
                    _publicationBeforeLocale = _view!.Experience.Publication.Version;
                    SetEnvironment(false, 1, false); _stage = Stage.CargoEnglish;
                    break;
                case Stage.CargoEnglish:
                    if (!ObserveCargo("cargo-en", false, "Ready to dispatch", null, "reserve", "cancel")) break;
                    Require(_view!.Experience.Publication.Version == _publicationBeforeLocale, "Locale switch republished the cargo model.");
                    _passed.Add("locale"); SetEnvironment(false, .75f, false); _stage = Stage.Scale75;
                    break;
                case Stage.Scale75:
                case Stage.Scale100:
                case Stage.Scale125:
                case Stage.Scale150:
                    if (!ObserveCargo("scale-" + _stage, false, "Ready to dispatch", null, "reserve", "cancel")) break;
                    Require(_view!.Experience.Publication.Version == _publicationBeforeLocale, "Scale switch republished the Flow model.");
                    if (_stage == Stage.Scale150)
                    { _passed.Add("scale"); SetEnvironment(true, 1.25f, true); _stage = Stage.Controller; }
                    else
                    {
                        float scale = _stage == Stage.Scale75 ? 1 : _stage == Stage.Scale100 ? 1.25f : 1.5f;
                        SetEnvironment(false, scale, false); _stage++;
                    }
                    break;
                case Stage.Controller:
                    if (!ObserveCargo("controller-ru", true, "Готово к отправке", null, "reserve", "cancel")) break;
                    _passed.Add("controller-profile"); _world!.SetAvailability(FlowApplicationState.Paused); _stage = Stage.Paused;
                    break;
                case Stage.Paused:
                    if (!ObserveCargo("paused-ru", true, "Перевозки приостановлены", "Перевозки приостановлены")) break;
                    _world!.SetAvailability(FlowApplicationState.RecoveryRequired); _stage = Stage.Recovery;
                    break;
                case Stage.Recovery:
                    if (!ObserveCargo("recovery-ru", true, "Требуется восстановление; груз сохранён", "Требуется восстановление; груз сохранён")) break;
                    _passed.Add("unavailable"); _world!.SetAvailability(FlowApplicationState.Active); _stage = Stage.Resumed;
                    break;
                case Stage.Resumed:
                    if (!ObserveCargo("resumed-ru", true, "Готово к отправке", null, "reserve", "cancel")) break;
                    _publicationBeforeUpdate = _view!.Experience.Publication.Version;
                    var before = _world!.ReadSnapshot();
                    Require(_world.Execute(new FlowParcelCommand(before.SessionId, before.Revision, _world.Parcel,
                        FlowParcelAction.Reserve)).Status == FlowCommandStatus.Applied, "The fixture could not publish a real reservation.");
                    _stage = Stage.Updated;
                    break;
                case Stage.Updated:
                    if (!ObserveCargo("updated-ru", true, "Запланировано", null, "cancel")) break;
                    Require(_view!.Experience.Publication.Version == _publicationBeforeUpdate + 1 && _world!.EffectAttempts == 1,
                        "The real domain change did not update the retained view exactly once.");
                    _passed.Add("publication");
                    _world!.FailNextOperation = true;
                    var current = _world.ReadSnapshot();
                    Require(_world.Execute(new FlowParcelCommand(current.SessionId, current.Revision, _world.Parcel,
                        FlowParcelAction.Cancel)).Status == FlowCommandStatus.Faulted, "The isolated executor did not fault through FlowApplication.");
                    _stage = Stage.Faulted;
                    break;
                case Stage.Faulted:
                    if (!Observe("faulted-ru", true, "", "", "Ошибка перевозки; см. журнал диагностики", "Ошибка перевозки; см. журнал диагностики")) break;
                    Require(_world!.EffectAttempts == 2 && _world.Errors.Count == 1
                        && ReferenceEquals(_world.Errors[0], _world.InjectedFailure), "The fault repeated an effect or hid another error.");
                    _passed.Add("faulted"); _world.CloseApplication(); _stage = Stage.Closed;
                    break;
                case Stage.Closed:
                    if (_view!.Surface.Visible) break;
                    RequireRetired(_view); _close(); _passed.Add("close");
                    _world!.Dispose(); _world = new FlowUiAcceptanceWorld(false); _world.Seed(); _worlds.Add(_world);
                    SetEnvironment(false, 1, false); Open(_world.Parcel); _stage = Stage.Retry;
                    break;
                case Stage.Retry:
                    if (!ObserveCargo("reopened-en", false, "Ready to dispatch", null, "reserve", "cancel")) break;
                    _world!.FailNextUnsubscribe = true;
                    Exception? failure = null;
                    try { Open(_world.Parcel); } catch (Exception error) { failure = error; }
                    Require(ReferenceEquals(failure, _world.UnsubscribeFailure) && _world.Subscribers == 1
                        && !_view!.Experience.IsActive, "Failed unsubscribe lost its retryable consumer handle.");
                    _close(); RequireRetired(_view!); Require(_world.Subscribers == 0, "Retry retained a Flow subscriber.");
                    _passed.Add("unsubscribe-retry"); Open(_world.Parcel); _stage = Stage.BeforeReturn;
                    break;
                case Stage.BeforeReturn:
                    if (!ObserveCargo("before-title-en", false, "Ready to dispatch", null, "reserve", "cancel")) break;
                    BeginReturn();
                    break;
                case Stage.ReopenedWorld:
                    if (!ObserveCargo("world-" + _loads, false, "Ready to dispatch", null, "reserve", "cancel")) break;
                    RequireRetiredViews();
                    if (_loads == 2) BeginReturn();
                    else { _passed.Add("save-isolation"); _stage = Stage.Final; }
                    break;
                case Stage.Final:
                    if (++_stableFrames < 120) break;
                    RequireRetiredViews(); _close(); RequireRetired(_view!); _world!.Dispose();
                    Require(_loads == 3 && _titles == 2, "Flow UI did not complete A to B to A.");
                    Require(_observations.Count == 19 && _views.Count == 6 && _worlds.Count == 4,
                        "Flow UI did not observe its complete bounded state and reopening matrix.");
                    RestoreSettings(); _stage = Stage.RestoringFinal;
                    break;
                case Stage.RestoringForTitle:
                    if (!ObserveRestoration()) break;
                    _stage = Stage.Returning; RequestReturnToTitle();
                    break;
                case Stage.RestoringFinal:
                    if (!ObserveRestoration()) break;
                    RequireSavesUnchanged();
                    _passed.Add("retired-handles"); _passed.Add("restored"); _passed.Add("read-only");
                    WriteReport(); _stage = Stage.Exit;
                    break;
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private void Open(Guid? parcel)
    {
        var candidate = _show(_world!, parcel, _world!.StationName, _api);
        var view = new View(_world, candidate, _api.Last ?? throw new InvalidOperationException("No native surface was created."));
        _view = view; _views.Add(view);
        Require(_views.Count <= 8 && _world.Subscribers == 1, "Opening leaked or exceeded the bounded Flow views.");
    }

    private bool ObserveCargo(string name, bool russian, string state, string? availability, params string[] enabled)
        => Observe(name, russian, (russian ? "Медная руда" : "Copper Ore") + " × " + _world!.Quantity,
            _world.World + " source → " + _world.World + " destination", state,
            ExpectedAvailability(russian, availability, enabled), enabled);

    private static string ExpectedAvailability(bool russian, string? ownerReason, string[] enabled)
    {
        string[] keys = { "reserve", "cancel", "retry", "reconcile", "return" };
        string[] titles = russian
            ? new[] { "Отправка", "Отмена", "Повторная доставка", "Проверка передачи", "Возврат" }
            : new[] { "Dispatch", "Cancel", "Retry delivery", "Check transfer", "Return" };
        string reason = ownerReason ?? (russian ? "Недоступно на этой стадии отправления" : "Unavailable at this shipment stage");
        return string.Join("\n", keys.Select((key, index) => (key, index))
            .Where(value => !enabled.Contains(value.key, StringComparer.Ordinal))
            .Select(value => titles[value.index] + ": " + reason));
    }

    private bool Observe(string name, bool russian, string cargo, string route, string state, string availability, params string[] enabled)
    {
        var view = _view!;
        if (view.Renders == 0 || Game1.fadeToBlackAlpha > 0 || Game1.options.uiScale != _requestedScale) return false;
        if (!_observation.SettleNativeProfile(ApplyRequestedInput)) return false;
        if (Game1.options.desiredUIScale != _requestedScale || Game1.options.gamepadControls != _requestedController
            || Game1.options.gamepadMode != (_requestedController ? Options.GamepadModes.ForceOn : Options.GamepadModes.ForceOff)
            || LocalizedContentManager.CurrentLanguageCode != (russian ? LocalizedContentManager.LanguageCode.ru : LocalizedContentManager.LanguageCode.en))
            throw new InvalidOperationException(FormattableString.Invariant(
                $"The driver's requested environment is no longer active at {name}: requested scale={_requestedScale}, controller={_requestedController}, russian={russian}; actual base={Game1.options.baseUIScale}, desired={Game1.options.desiredUIScale}, applied={Game1.options.uiScale}, controller={Game1.options.gamepadControls}, mode={Game1.options.gamepadMode}, language={LocalizedContentManager.CurrentLanguageCode}, sameOptions={ReferenceEquals(_originalOptions, Game1.options)}."));
        Require(ReferenceEquals(Game1.activeClickableMenu, _coverMenu), "The retained scale fixture lost its native menu owner.");
        Require(view.Surface.Visible && view.Experience.IsActive && view.World.Subscribers == 1,
            $"The current Flow view retired prematurely at {name}: visible={view.Surface.Visible}, active={view.Experience.IsActive}, subscribers={view.World.Subscribers}, renders={view.Renders}, closes={view.Closes}.");
        Require(view.Sources.SequenceEqual(view.Experience.Experience.Elements.Select(element => element.Source))
            && view.Actions.SequenceEqual(view.Experience.Experience.Actions), "The retained experience replaced source/action identity.");
        var expected = new Frame(name, view.Experience, view.Surface, russian ? "ru-RU" : "en",
            _requestedScale, _requestedController, cargo, route, state, availability, "", enabled,
            view.Experience.Publication.Version);
        if (!_observe(expected)) return false;
        _observations.Add(new { name, world = view.World.World, session = view.World.ReadSnapshot().SessionId,
            publication = expected.PublicationVersion, renders = view.Renders, cargo, route, state, availability });
        Require(_observations.Count <= 24, "Flow UI observations exceeded their fixed matrix.");
        return true;
    }

    private void BeginReturn()
    {
        RestoreSettings(); _stage = Stage.RestoringForTitle;
    }

    internal void OnReturnedToTitle()
    {
        Require(_stage == Stage.Returning, "Unexpected Flow UI return to title.");
        RequireRetired(_view!); _world!.Dispose(); RequireSavesUnchanged(); _titles++; _stage = Stage.Reload;
    }

    private void RequireRetired(View view)
    {
        Require(!view.Surface.Visible && !view.Experience.IsActive && view.Closes == 1
            && view.Renders == view.RetiredRenders, "A retired native Flow surface kept render ownership.");
        int reads = view.World.ReadCalls, commands = view.World.Commands;
        long version = view.Experience.Publication.Version;
        Require(view.RetiredValues is not null && version == view.RetiredVersion
            && view.RetiredValues.SequenceEqual(view.Sources.Select(source => source?.UntypedValue)),
            "A retired Flow source changed its final captured value.");
        foreach (UiActionDefinition action in view.Actions)
        {
            bool admitted = false;
            try { admitted = _api.ActionAutomation.Activate(view.Surface, action.Id); }
            catch (ObjectDisposedException) { /* The owning host rejects its disposed opaque handle. */ }
            Require(!admitted, "A retired Flow action remained active.");
        }
        Require(!view.Experience.Pump(), "A retired Flow source remained active.");
        Require(view.World.ReadCalls == reads && view.World.Commands == commands && view.Experience.Publication.Version == version,
            "A retired view read or mutated its previous application.");
        _observation.VerifyRetired(view.Surface);
    }

    private void RequireRetiredViews()
    {
        foreach (View view in _views) if (!ReferenceEquals(view, _view)) RequireRetired(view);
        foreach (FlowUiAcceptanceWorld world in _worlds) if (!ReferenceEquals(world, _world))
            Require(world.Subscribers == 0, "A previous world retains Flow subscriptions.");
    }

    private string SavePath(int index) => index == 0 ? _request.SavePath : _request.SecondSavePath!;
    private void RequireSavesUnchanged()
    {
        for (int i = 0; i < 2; i++) Require(_saveHashes[i] == FlowAcceptanceSaveTree.Fingerprint(SavePath(i)),
            "Read-only Flow UI acceptance changed a copied world.");
    }

    private void CaptureSettings()
    {
        Require(!_settingsOwned, "A prior world retained its settings lease.");
        _originalLanguage = LocalizedContentManager.CurrentLanguageCode;
        _originalModLanguage = LocalizedContentManager.CurrentModLanguage;
        _originalLocale = LocalizedContentManager.LanguageCodeString(_originalLanguage);
        _originalOptions = Game1.options;
        _originalBaseScale = Game1.options.baseUIScale; _originalDesiredScale = Game1.options.desiredUIScale;
        _originalGamepad = Game1.options.gamepadControls; _originalGamepadMode = Game1.options.gamepadMode;
        _settingsOwned = true;
    }

    private void SetEnvironment(bool russian, float scale, bool controller)
    {
        _requestedScale = scale; _requestedController = controller;
        // Let the native mismatch path invalidate and rebuild its UI render target.
        Game1.options.desiredUIScale = scale;
        _observation.ResetNativeSettling();
        ApplyRequestedInput();
        LocalizedContentManager.CurrentLanguageCode = russian ? LocalizedContentManager.LanguageCode.ru : LocalizedContentManager.LanguageCode.en;
    }

    private void ApplyRequestedInput()
    {
        Game1.options.gamepadMode = _requestedController ? Options.GamepadModes.ForceOn : Options.GamepadModes.ForceOff;
        Game1.options.gamepadControls = _requestedController;
    }

    private void ApplyOriginalInput()
    {
        Game1.options.gamepadMode = _originalGamepadMode;
        Game1.options.gamepadControls = _originalGamepad;
    }

    private void RestoreSettings()
    {
        if (!_settingsOwned) return;
        Require(ReferenceEquals(_originalOptions, Game1.options), "Native options changed before their owned restoration.");
        Game1.options.baseUIScale = _originalBaseScale; Game1.options.desiredUIScale = _originalDesiredScale;
        Game1.options.gamepadMode = _originalGamepadMode; Game1.options.gamepadControls = _originalGamepad;
        if (_originalLanguage == LocalizedContentManager.LanguageCode.mod)
            LocalizedContentManager.SetModLanguage(_originalModLanguage ?? throw new InvalidOperationException("The custom language descriptor was lost."));
        else LocalizedContentManager.CurrentLanguageCode = _originalLanguage;
        Require(ReferenceEquals(Game1.options, _originalOptions)
            && Game1.options.baseUIScale == _originalBaseScale && Game1.options.desiredUIScale == _originalDesiredScale
            && Game1.options.gamepadControls == _originalGamepad && Game1.options.gamepadMode == _originalGamepadMode,
            "Native locale callbacks changed the restored options before lease release.");
        Require(LocalizedContentManager.CurrentLanguageCode == _originalLanguage
            && ReferenceEquals(LocalizedContentManager.CurrentModLanguage, _originalModLanguage)
            && LocalizedContentManager.LanguageCodeString(_originalLanguage) == _originalLocale,
            "The original native locale was not restored.");
        Game1.game1.refreshWindowSettings();
        _observation.ResetNativeSettling();
        _settingsOwned = false;
    }

    private bool ObserveRestoration()
    {
        if (!_observation.SettleNativeProfile(ApplyOriginalInput)
            || _observation.SettledNativeFrame is not { } frame) return false;
        Require(ReferenceEquals(_originalOptions, Game1.options)
            && frame.DesiredScale == _originalDesiredScale
            && Game1.options.gamepadControls == _originalGamepad && Game1.options.gamepadMode == _originalGamepadMode,
            "The restored native settings changed before their completed draw.");
        Require(_restorations.Count < 3, "Restoration observations exceeded the three-world bound.");
        _restorations.Add(new { capturedBase = _originalBaseScale, capturedDesired = _originalDesiredScale,
            pendingAtCapture = _originalBaseScale != _originalDesiredScale, exactSnapshotRestored = true, settledFrame = frame });
        return true;
    }

    internal void Fail(Exception error)
    {
        if (_failed) return;
        _failed = true;
        _errors.Add(error);
        _monitor.Log("Flowline " + Scenario + " failed: " + error, LogLevel.Error);
        try { _close(); } catch (Exception cleanup) { _errors.Add(cleanup); }
        try { RestoreSettings(); } catch (Exception restore) { _errors.Add(restore); }
        try { WriteReport(); } finally { _stage = Stage.Exit; }
    }

    private void WriteReport()
    {
        string captured = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        string note = _errors.Count == 0 ? "Actual copied-world Flow UI observations, owner transitions and retirement."
            : string.Join("\n", _errors);
        AtomicJson(Path.Combine(_helper.DirectoryPath, ".acceptance", "host-acceptance-report.json"), new
        {
            FormatVersion = 3, PerformanceFormatVersion = 3, CapturedAtUtc = captured,
            RuntimeFingerprintAlgorithm = "sha256-flow-runtime-v1", RuntimeFingerprint = _fingerprint,
            GameVersion = Game1.GetVersionString(), SmapiVersion = Constants.ApiVersion.ToString(), Scenarios = Array.Empty<object>(),
            HostChecks = Checks.Select(id => new { Id = Scenario + "." + id, Passed = _errors.Count == 0 && _passed.Contains(id),
                CapturedAtUtc = captured, Note = note }).ToArray()
        });
        AtomicJson(Path.Combine(_request.Artifact, "diagnostics", "flow-ui-lifecycle.json"), new
        {
            requestId = _request.RunId, scenarioId = Scenario, frames = _frames, loads = _loads, titles = _titles,
            settingsRestored = !_settingsOwned, restorationFrames = _restorations.ToArray(),
            saveHashes = _saveHashes, observations = _observations.ToArray(),
            worlds = _worlds.Select(world => new { world.World, world.Subscribers, world.Commands, world.EffectAttempts }).ToArray(),
            views = _views.Select(view => new { world = view.World.World, view.Renders, view.Closes, view.RetiredRenders }).ToArray(),
            errors = _errors.Select(error => error.ToString()).ToArray()
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        var failures = new List<Exception>();
        Attempt(_close);
        Attempt(RestoreSettings);
        foreach (View view in _views) Attempt(view.Detach);
        foreach (FlowUiAcceptanceWorld world in _worlds) Attempt(world.Dispose);
        Attempt(_observation.Dispose);
        if (failures.Count != 0) throw new AggregateException("Flow UI acceptance cleanup failed.", failures);
        _disposed = true;
        void Attempt(Action action) { try { action(); } catch (Exception error) { failures.Add(error); } }
    }
}
