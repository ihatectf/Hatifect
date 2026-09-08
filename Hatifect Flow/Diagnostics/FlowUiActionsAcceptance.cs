using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Hatifect.Flow.Application;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.UI.Semantic;
using Hatifect.UI;
using Hatifect.UI.Experience;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData;
using StardewValley.Menus;
using static Hatifect.Flow.Diagnostics.FlowHostAcceptance;

namespace Hatifect.Flow.Diagnostics;

// Only the copied save and synthetic transport are owned here. Actions use the same surface
// and normalized input service as the production consumer; fixture selection is not input evidence.
internal sealed class FlowUiActionsAcceptance : IDisposable
{
    internal const string Scenario = "flow.ui.actions";
    private static readonly string[] Checks = { "loaded", "create", "repeated", "domain-rejection", "dispatch",
        "cancel", "retry", "stale", "retired", "restored", "read-only" };
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly AcceptanceRequest _request;
    private readonly string _fingerprint, _saveHash;
    private readonly Func<IFlowNetworkApplication, IUiSemanticSurfaceApi, NetworkExperience> _showNetwork;
    private readonly Func<IFlowApplication, Guid?, Func<Guid, string>, IUiSemanticSurfaceApi, ParcelExperience> _showParcel;
    private readonly Action _close;
    private readonly SurfaceApi _api;
    private readonly FlowUiAcceptanceObservation _observation;
    private readonly List<FlowUiAcceptanceWorld> _worlds = new(3);
    private readonly HashSet<string> _passed = new(StringComparer.Ordinal);
    private readonly List<Exception> _errors = new();
    private FlowUiAcceptanceWorld? _world;
    private NetworkExperience? _network;
    private ParcelExperience? _parcel;
    private IUiSemanticSurfaceSession? _surface;
    private UiExperienceDefinition? _definition;
    private Stage _stage;
    private int _frames;
    private FlowUiNativeFrame? _restoredFrame;
    private int _sendReentryAttempts, _sendReentryEffectsBefore, _sendReentryEffectsAfter;
    private bool? _sendReentryAdmitted;
    private Exception? _sendReentryError;
    private bool _failed, _disposed, _settingsOwned;
    private (int Width, int Height, int UiWidth, int UiHeight)? _readySize;
    private LocalizedContentManager.LanguageCode _originalLanguage;
    private ModLanguage? _originalModLanguage;
    private string? _originalLocale;
    private Options? _originalOptions;
    private float _originalBaseScale, _originalDesiredScale;
    private bool _originalGamepad;
    private Options.GamepadModes _originalGamepadMode;
    private enum Stage { Startup, Loading, Ready, NetworkReady, Created, Reserved, DeliveryRejected, RetryPending,
        Delivered, Unconnected, Rejected, DispatchReady, Dispatched, Cancelled, Paused, Resumed, Final, Restoring, Exit }

    private sealed class SurfaceApi : IUiSemanticSurfaceApi
    {
        private readonly IUiSemanticSurfaceRevealAutomationApi _inner;
        internal SurfaceApi(IUiSemanticSurfaceRevealAutomationApi inner) => _inner = inner;
        internal IUiSemanticSurfaceSession? Last { get; private set; }
        internal IUiSemanticSurfaceActionAutomation Actions => _inner.ActionAutomation;
        internal IUiSemanticSurfaceObservation Observation => _inner.Observation;
        public int ApiVersion => _inner.ApiVersion;
        public IUiSemanticSurfaceAutomation Automation => _inner.Automation;
        public IUiSemanticSurfaceSession CreateActiveMenuOverlay(UiExperienceDefinition experience, UiSemanticSurfaceOptions options)
            => Last = _inner.CreateActiveMenuOverlay(experience, options);
    }

    internal static FlowUiActionsAcceptance? TryCreate(IModHelper helper, IMonitor monitor,
        Func<IFlowNetworkApplication, IUiSemanticSurfaceApi, NetworkExperience> showNetwork,
        Func<IFlowApplication, Guid?, Func<Guid, string>, IUiSemanticSurfaceApi, ParcelExperience> showParcel, Action close)
    {
        if (Environment.GetEnvironmentVariable("HATIFECT_TEST_MODE") != "1"
            || Environment.GetEnvironmentVariable("HATIFECT_TEST_AUTOMATED") != "1"
            || Environment.GetEnvironmentVariable("HATIFECT_TEST_SCENARIO") != Scenario) return null;
        AcceptanceRequest request = ReadAcceptanceRequest(helper, Scenario);
        var api = helper.ModRegistry.GetApi<IUiSemanticSurfaceRevealAutomationApi>("Hatifect.UI")
            ?? throw new InvalidOperationException("Flow actions need the owning native observation, action and reveal API.");
        Require(api.ActionAutomation.IsEnabled, "The exact-harness action service is disabled.");
        Require(api.RevealAutomation.IsEnabled, "The exact-harness reveal service is disabled.");
        var observation = new FlowUiAcceptanceObservation(api.Observation, request.Artifact, Scenario, api.RevealAutomation);
        try { return new(helper, monitor, request, api, observation, showNetwork, showParcel, close); }
        catch { observation.Dispose(); throw; }
    }

    private FlowUiActionsAcceptance(IModHelper helper, IMonitor monitor, AcceptanceRequest request,
        IUiSemanticSurfaceRevealAutomationApi api, FlowUiAcceptanceObservation observation,
        Func<IFlowNetworkApplication, IUiSemanticSurfaceApi, NetworkExperience> showNetwork,
        Func<IFlowApplication, Guid?, Func<Guid, string>, IUiSemanticSurfaceApi, ParcelExperience> showParcel, Action close)
    {
        _helper = helper; _monitor = monitor; _request = request; _api = new(api); _observation = observation;
        _showNetwork = showNetwork; _showParcel = showParcel; _close = close;
        _fingerprint = RuntimeFingerprint(); _saveHash = FlowAcceptanceSaveTree.Fingerprint(request.SavePath);
    }

    internal void OnSaveLoaded()
    {
        Require(_stage == Stage.Loading && Context.IsWorldReady && Context.IsMainPlayer && !Context.IsMultiplayer,
            "Unexpected Flow action world load or authority.");
        Require(Game1.uniqueIDForThisGame == 4242424242UL && Constants.SaveFolderName == "HatifectHarness_4242424242",
            "Flow actions loaded a foreign copied world.");
        ValidateSaveTree(_request.SavePath); ValidateSaveOwner(_request.SavePath, _request.RunId, _request.RuntimeId);
        RequireSaveUnchanged(); _passed.Add("loaded"); _stage = Stage.Ready;
    }

    internal void OnReturnedToTitle() => Fail(new InvalidOperationException("Unexpected return to title during Flow actions."));

    internal void Tick()
    {
        if (_disposed) return;
        try
        {
            if (_stage == Stage.Exit) { Game1.game1.Exit(); return; }
            Require(++_frames <= 18000, "Flow actions exceeded their bounded frame count.");
            switch (_stage)
            {
                case Stage.Startup when _frames >= 30:
                    RequireSaveUnchanged(); _stage = Stage.Loading;
                    SaveGame.Load(Path.GetFileName(_request.SavePath)); Game1.exitActiveMenu(); break;
                case Stage.Ready:
                    if (!NativeWorldReady()) break;
                    CaptureSettings(); Game1.activeClickableMenu = new GameMenu(); SetEnglish();
                    NewWorld(network: true); OpenNetwork(); _stage = Stage.NetworkReady; break;
                case Stage.NetworkReady:
                    if (!ObserveNetwork("network-ready", "", true)) break;
                    CreateWithSynchronousReentryProbe();
                    Require(_world!.CreateEffects == 1 && CurrentParcel().State == ParcelState.Reserved,
                        "Native create did not produce exactly one real reserved parcel.");
                    Activate("send", false);
                    Require(_world.CreateEffects == 1 && _world.ReadSnapshot().Parcels.Count == 1,
                        "Repeated native create duplicated the shipment.");
                    _passed.Add("create"); _passed.Add("repeated"); _stage = Stage.Created; break;
                case Stage.Created:
                    if (!ObserveNetwork("network-created", "Shipment created", false)) break;
                    Retire(); OpenParcel(); _stage = Stage.Reserved; break;
                case Stage.Reserved:
                    if (!ObserveParcel("created-parcel", "Scheduled", "", "cancel")) break;
                    _world!.AcceptNetworkDelivery(false);
                    Require(_world.AdvanceNetworkTo(1) == 1 && _world.AdvanceNetworkTo(9) == 2,
                        "The fake transport did not reach its actual rejected delivery.");
                    Require(CurrentParcel().State == ParcelState.DeliveryRejected, "Expected the provider's delivery rejection.");
                    _stage = Stage.DeliveryRejected; break;
                case Stage.DeliveryRejected:
                    if (!ObserveParcel("delivery-rejected", "Destination could not accept the cargo", "", "retry", "return")) break;
                    Activate("cancel", false); _world!.AcceptNetworkDelivery(true); Activate("retry", true);
                    Require(_world.CreateEffects == 1 && _world.EffectAttempts == 1,
                        "Retry created another shipment or repeated the domain command.");
                    _stage = Stage.RetryPending; break;
                case Stage.RetryPending:
                    if (!ObserveParcelWithReason("retry-pending", "Destination could not accept the cargo", "Command completed", operationPending: true)) break;
                    Require(_world!.AdvanceNetworkTo(10) == 1 && CurrentParcel().State == ParcelState.Delivered,
                        "Retry did not deliver the existing parcel at the next transport tick.");
                    _passed.Add("retry"); _stage = Stage.Delivered; break;
                case Stage.Delivered:
                    if (!ObserveParcel("delivered", "Delivered", "Command completed")) break;
                    Retire(); NewWorld(network: true, connected: false); OpenNetwork(); _stage = Stage.Unconnected; break;
                case Stage.Unconnected:
                    if (!ObserveNetwork("no-route-ready", "", true)) break;
                    Activate("send", true);
                    Require(_world!.CreateEffects == 0 && _world.ReadSnapshot().Parcels.Count == 0,
                        "A rejected route changed transport state.");
                    _stage = Stage.Rejected; break;
                case Stage.Rejected:
                    if (!ObserveNetwork("no-route-result", "No route connects these stations", true)) break;
                    _passed.Add("domain-rejection"); Retire(); NewWorld(network: false); OpenParcel();
                    _stage = Stage.DispatchReady; break;
                case Stage.DispatchReady:
                    if (!ObserveParcel("dispatch-ready", "Ready to dispatch", "", "reserve", "cancel")) break;
                    _world!.SetAvailability(FlowApplicationState.Paused);
                    long version = _parcel!.Publication.Version; int effects = _world.EffectAttempts;
                    Activate("reserve", false); Activate("cancel", false);
                    Require(_parcel.Publication.Version == version && _world.EffectAttempts == effects,
                        "A stale screen mutated its owner or published from action admission.");
                    _stage = Stage.Paused; break;
                case Stage.Dispatched:
                    if (!ObserveParcel("dispatched", "Scheduled", "Command completed", "cancel")) break;
                    Activate("cancel", true); Activate("cancel", false);
                    Require(CurrentParcel().State == ParcelState.Cancelled && _world!.EffectAttempts == 2,
                        "Native cancellation failed or repeated its effect.");
                    _passed.Add("cancel"); _stage = Stage.Cancelled; break;
                case Stage.Cancelled:
                    if (!ObserveParcel("cancelled", "Cancelled", "Command completed")) break;
                    Retire(); _stage = Stage.Final; break;
                case Stage.Paused:
                    if (!ObserveParcelWithReason("paused", "Transport paused", "", ownerReason: "Transport is paused")) break;
                    _passed.Add("stale"); _world!.SetAvailability(FlowApplicationState.Active); _stage = Stage.Resumed; break;
                case Stage.Resumed:
                    if (!ObserveParcel("resumed", "Ready to dispatch", "", "reserve", "cancel")) break;
                    Activate("reserve", true); Activate("reserve", false);
                    Require(CurrentParcel().State == ParcelState.Reserved && _world!.EffectAttempts == 1,
                        "Native dispatch failed or repeated its reservation effect.");
                    _passed.Add("dispatch"); _stage = Stage.Dispatched; break;
                case Stage.Final:
                    Require(_worlds.Count == 3 && _worlds.All(world => world.Subscribers == 0 && world.Errors.Count == 0),
                        "Flow actions retained subscriptions or swallowed owner errors.");
                    RestoreSettings(); _stage = Stage.Restoring; break;
                case Stage.Restoring:
                    if (!_observation.SettleNativeProfile(ApplyOriginalInput)
                        || _observation.SettledNativeFrame is not { } restored) break;
                    Require(ReferenceEquals(_originalOptions, Game1.options)
                        && restored.DesiredScale == _originalDesiredScale
                        && Game1.options.gamepadControls == _originalGamepad && Game1.options.gamepadMode == _originalGamepadMode,
                        "The restored action settings changed before their completed draw.");
                    _restoredFrame = restored; RequireSaveUnchanged();
                    _passed.Add("retired"); _passed.Add("restored"); _passed.Add("read-only");
                    WriteReport(); _stage = Stage.Exit; break;
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private void NewWorld(bool network, bool connected = true)
    {
        Require(_surface is null && _worlds.Count < 3, "Cannot replace an active or unbounded action fixture.");
        _world?.Dispose(); _world = new(false); _worlds.Add(_world);
        if (network) _world.PrepareNetwork(connected); else _world.Seed();
    }

    private void OpenNetwork()
    {
        _network = _showNetwork(_world!, _api); _definition = _network.Experience; _surface = _api.Last;
        // No collection input facet exists in the owning harness contract. These are synthetic
        // fixture preconditions, deliberately separate from the native action assertions below.
        Select<FlowStationDetails>("source-station", 0);
        Select<FlowStationDetails>("destination-station", 1);
        Select<FlowInventorySlot>("source-cargo", 0);
        void Select<T>(string key, int index)
        {
            var source = (FlowSelectionSource<T>)_definition.Elements.Single(element => element.Id == _definition.Id.Child("element/" + key)).Source!;
            Require(source.TrySelect(source.GetItem(index).Id), "The synthetic selection precondition was rejected: " + key);
        }
    }

    private void OpenParcel()
    { _parcel = _showParcel(_world!, _world!.Parcel, _world.StationName, _api); _definition = _parcel.Experience; _surface = _api.Last; }

    private void Activate(string key, bool expected)
        => Require(_api.Actions.Activate(_surface!, _definition!.Id.Child("action/" + key)) == expected,
            "Unexpected normalized native action admission: " + key);

    private FlowParcelSnapshot CurrentParcel() => _world!.ReadSnapshot().Parcels.Single(parcel => parcel.Id == _world.Parcel);

    private bool ObserveNetwork(string name, string result, bool sendEnabled)
    {
        if (!SettleEnglishInput()) return false;
        UiSymbolId field = _definition!.Id.Child("element/result");
        // Wait until the owning accepted scene contains the committed result before revealing it.
        if (result.Length > 0 && !_api.Observation.Capture(_surface!).Elements
            .Any(row => row.SemanticId == field && row.Value == result)) return false;
        return _observation.Observe(new FlowUiAcceptanceObservation.Frame(name, _definition!, _surface!, "en", 1, false,
            _network!.Publication.Version, () => _network.Publication.Version, actual =>
            {
                Require(actual.Texts.Any(row => row.Text == "Flowline network · diagnostic"), "The native network lost its diagnostic title.");
                UiSymbolId action = _definition!.Id.Child("action/send");
                var rows = actual.Elements.Where(row => row.SemanticId == field).ToArray();
                Require(rows.Length == 1 && rows[0].Value == result && rows[0].Name == "Result"
                    && (result.Length == 0 || actual.Texts.Any(row => row.SemanticId == field && row.Text == result)),
                    "The rendered network result differs from the committed command outcome.");
                var buttons = actual.Elements.Where(row => row.ActionId == action).ToArray();
                Require(buttons.Length == 1 && buttons[0].Enabled == sendEnabled && buttons[0].Name == "Send whole stack",
                    "The native Send action lost its exact label or availability.");
            }, result.Length > 0 ? field : null));
    }

    private bool ObserveParcel(string name, string state, string result, params string[] enabled)
        => ObserveParcelWithReason(name, state, result, enabled, null);

    private bool ObserveParcelWithReason(string name, string state, string result, string[]? enabled = null, string? ownerReason = null, bool operationPending = false)
    {
        if (!SettleEnglishInput()) return false;
        enabled ??= Array.Empty<string>();
        string[] keys = { "reserve", "cancel", "retry", "reconcile", "return" };
        string[] labels = { "Dispatch", "Cancel", "Retry delivery", "Check transfer", "Return" };
        string availability = string.Join("\n", keys.Select((key, index) => (key, index))
            .Where(value => !enabled.Contains(value.key, StringComparer.Ordinal))
            .Select(value => labels[value.index] + ": " + (ownerReason ?? (operationPending && value.key is "retry" or "return"
                ? "An operation is already scheduled" : "Unavailable at this shipment stage"))));
        return _observation.Observe(new FlowUiAcceptance.Frame(name, _parcel!, _surface!, "en", 1, false,
            "Copper Ore × " + _world!.Quantity, "A source → A destination", state, availability, result, enabled,
            _parcel!.Publication.Version));
    }

    private void CreateWithSynchronousReentryProbe()
    {
        FlowUiAcceptanceWorld world = _world!;
        IUiSemanticSurfaceSession surface = _surface!;
        UiSymbolId send = _definition!.Id.Child("action/send");
        int subscribers = world.Subscribers;
        world.RevisionChanged += DuringCreatePublication;
        try { Activate("send", true); }
        finally { world.RevisionChanged -= DuringCreatePublication; }
        Require(_sendReentryAttempts == 1 && _sendReentryAdmitted == false && _sendReentryError is null,
            "Native Send re-entry during synchronous create was not rejected cleanly.");
        Require(_sendReentryEffectsBefore == 1 && _sendReentryEffectsAfter == 1
            && world.CreateEffects == 1 && world.Commands == 0 && world.Subscribers == subscribers,
            "The synchronous native re-entry probe duplicated effects or retained its callback.");

        void DuringCreatePublication(long revision)
        {
            // A real owner callback while create is executing, not an artificial asynchronous delay.
            // Bound the probe even if a regression incorrectly admits a second create.
            if (++_sendReentryAttempts != 1) return;
            _sendReentryEffectsBefore = world.CreateEffects;
            try { _sendReentryAdmitted = _api.Actions.Activate(surface, send); }
            catch (Exception error) { _sendReentryError = error; }
            finally { _sendReentryEffectsAfter = world.CreateEffects; }
        }
    }

    private void Retire()
    {
        if (_surface is null) return;
        var surface = _surface; var definition = _definition!;
        _close(); int reads = _world!.ReadCalls, commands = _world.Commands, effects = _world.EffectAttempts;
        foreach (var action in definition.Actions)
        {
            bool admitted = false;
            try { admitted = _api.Actions.Activate(surface, action.Id); } catch (ObjectDisposedException) { }
            Require(!admitted, "A retired surface admitted another action.");
        }
        Require(_world.ReadCalls == reads && _world.Commands == commands && _world.EffectAttempts == effects
            && _world.Subscribers == 0, "Retired native actions retained owner access.");
        _observation.VerifyRetired(surface); _surface = null; _network = null; _parcel = null; _definition = null;
    }

    private void RequireSaveUnchanged() => Require(_saveHash == FlowAcceptanceSaveTree.Fingerprint(_request.SavePath),
        "Read-only Flow actions changed the copied save.");

    private void SetEnglish()
    {
        Game1.options.desiredUIScale = 1;
        _observation.ResetNativeSettling();
        ApplyEnglishInput();
        LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.en;
    }

    private static void ApplyEnglishInput()
    {
        Game1.options.gamepadMode = Options.GamepadModes.ForceOff;
        Game1.options.gamepadControls = false;
    }

    private bool SettleEnglishInput()
    {
        if (!_observation.SettleNativeProfile(ApplyEnglishInput)) return false;
        RequireEnglishInput(Game1.options.gamepadMode, Game1.options.gamepadControls);
        return true;
    }

    internal static void RequireEnglishInput(Options.GamepadModes mode, bool controls)
        => Require(mode == Options.GamepadModes.ForceOff && !controls,
            "The native action input profile changed after settling: mode=" + mode + ", controller=" + controls + ".");

    private void ApplyOriginalInput()
    {
        Game1.options.gamepadMode = _originalGamepadMode;
        Game1.options.gamepadControls = _originalGamepad;
    }

    internal void Fail(Exception error)
    {
        if (_failed) return;
        _failed = true; _errors.Add(error); _monitor.Log("Flowline " + Scenario + " failed: " + error, LogLevel.Error);
        try { _close(); } catch (Exception cleanup) { _errors.Add(cleanup); }
        try { RestoreSettings(); } catch (Exception restore) { _errors.Add(restore); }
        try { WriteReport(); } finally { _stage = Stage.Exit; }
    }

    private void WriteReport()
    {
        string captured = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        AtomicJson(Path.Combine(_helper.DirectoryPath, ".acceptance", "host-acceptance-report.json"), new
        {
            FormatVersion = 3, PerformanceFormatVersion = 3, CapturedAtUtc = captured,
            RuntimeFingerprintAlgorithm = "sha256-flow-runtime-v1", RuntimeFingerprint = _fingerprint,
            GameVersion = Game1.GetVersionString(), SmapiVersion = Constants.ApiVersion.ToString(), Scenarios = Array.Empty<object>(),
            HostChecks = Checks.Select(id => new { Id = Scenario + "." + id, Passed = _errors.Count == 0 && _passed.Contains(id),
                CapturedAtUtc = captured, Note = _errors.Count == 0 ? "Owning native action input and real synthetic transport effects." : string.Join("\n", _errors) }).ToArray()
        });
        AtomicJson(Path.Combine(_request.Artifact, "diagnostics", "flow-ui-actions.json"), new
        {
            requestId = _request.RunId, scenarioId = Scenario, frames = _frames, settingsRestored = !_settingsOwned,
            restoration = new { capturedBase = _originalBaseScale, capturedDesired = _originalDesiredScale,
                pendingAtCapture = _originalBaseScale != _originalDesiredScale, settledFrame = _restoredFrame },
            saveHash = _saveHash, selection = "synthetic fixture preselection; not native input",
            input = "owning normalized Tab/Enter action automation; not physical OS input",
            sendReentry = new { scope = "synchronous owner publication; not a long-lived Runtime Running frame",
                attempts = _sendReentryAttempts, admitted = _sendReentryAdmitted,
                effectsBefore = _sendReentryEffectsBefore, effectsAfter = _sendReentryEffectsAfter,
                error = _sendReentryError?.ToString() },
            worlds = _worlds.Select(world => new { world.Subscribers, world.Commands, world.EffectAttempts, world.CreateEffects }).ToArray(),
            errors = _errors.Select(error => error.ToString()).ToArray()
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        var failures = new List<Exception>();
        Attempt(_close); Attempt(RestoreSettings);
        foreach (var world in _worlds) Attempt(world.Dispose);
        Attempt(_observation.Dispose);
        if (failures.Count != 0) throw new AggregateException("Flow action cleanup failed.", failures);
        _disposed = true;
        void Attempt(Action action) { try { action(); } catch (Exception error) { failures.Add(error); } }
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

}
