using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.Flow.Application;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Sessions;
using Hatifect.Flow.UI.Semantic;
using Hatifect.UI;
using Hatifect.UI.Experience;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Objects;
using static Hatifect.Flow.Diagnostics.FlowHostAcceptance;

namespace Hatifect.Flow.Diagnostics;

// Semantic form/selection preparation is fixture setup, not keyboard or pointer evidence.
// Commands enter through UI-owned normalized Window actions over the real game session.
internal sealed class FlowPlayerWindowSequence : IDisposable
{
    private readonly IFlowUiHostAcceptanceApi _api;
    private readonly HostApi _host;
    private readonly FlowPlayerVisualProfile? _profile;
    private readonly FlowPlayerProfileAcquisition _acquisition = new();
    private float _originalBaseScale, _originalDesiredScale, _originalAppliedScale;
    private LocalizedContentManager.LanguageCode _originalLanguage;
    private object? _originalModLanguage;
    private Action? _restoreLanguage;
    private readonly List<object> _profileApplications = new(2);
    internal IReadOnlyList<object> ProfileApplications => _profileApplications;
    internal bool ProfilesCompleted => _profile is not null && _profileApplications.Count == 2
        && _inputRestorations.Count == 2 && !_inputOwned && !_restoringInput;
    private readonly FlowUiAcceptanceObservation _observation;
    private readonly Func<FlowGameSession, IUiSemanticHostApi, NetworkExperience> _open;
    private readonly Action _close;
    private readonly List<object> _milestones = new(12);
    private NetworkExperience? _experience;
    private IUiSemanticSurfaceSession? _surface;
    private int _stage;
    private bool _disposed, _inputOwned, _restoringInput, _historyObserved;
    private Options? _inputOptions;
    private Options.GamepadModes _originalMode;
    private bool _originalControls;
    private readonly List<object> _inputRestorations = new(2);
    internal IReadOnlyList<object> InputRestorations => _inputRestorations;
    internal Guid WholeParcel { get; private set; }
    internal Guid PartialParcel { get; private set; }
    internal IReadOnlyList<object> Milestones => _milestones;

    internal FlowPlayerWindowSequence(IModHelper helper, string artifact,
        Func<FlowGameSession, IUiSemanticHostApi, NetworkExperience> open, Action close, string scenario = FlowChestRoundtripAcceptance.PlayerScenario)
    {
        if (scenario != FlowChestRoundtripAcceptance.PlayerScenario) _profile = FlowPlayerVisualProfile.Resolve(scenario);
        _api = helper.ModRegistry.GetApi<IFlowUiHostAcceptanceApi>("Hatifect.UI")
            ?? throw new InvalidOperationException("The composite Flow Window acceptance proxy is unavailable.");
        Require(_api.ActionAutomation.IsEnabled && _api.RevealAutomation.IsEnabled && _api.Observation.IsEnabled,
            "The exact-harness Window observation/actions/reveal services must all be enabled.");
        _host = new(_api); _open = open; _close = close;
        _observation = new(_api.Observation, artifact, scenario, _api.RevealAutomation);
    }

    internal bool Admit(FlowGameSession session, Chest source, Chest destination, int sx, int sy, int dx, int dy)
    {
        Require(!_disposed, "The Window sequence was disposed.");
        if (_stage == 10)
        {
            if (!CompleteInputRestore()) return false;
            _stage = 11; return true;
        }
        if (_stage == 11) return true;
        if (!_inputOwned)
        {
            if (!Context.IsPlayerFree || Game1.activeClickableMenu is not null || Game1.fadeToBlackAlpha > 0) return false;
            if (!TryBeginInputProfile()) return false;
        }
        if (!_observation.SettleNativeProfile(ApplyInput) || Game1.fadeToBlackAlpha > 0) return false;
        RequireProfile();
        switch (_stage)
        {
            case 0:
                if (!Context.IsPlayerFree || Game1.activeClickableMenu is not null) return false;
                Require(session.ReadSnapshot().ProviderMode == FlowProviderMode.GameInventory
                    && session.ReadSnapshot().Stations.Count == 0 && session.ReadSnapshot().Parcels.Count == 0,
                    "Window admission must start with the clean production inventory provider.");
                session.PreparePlayerTarget("Farm", sx, sy, source); Open(session);
                Form("station-details", 0, "accept_source"); _stage++; break;
            case 1:
                if (!Observe("player-source-ready", "")) return false;
                Activate("register"); Require(session.ReadSnapshot().Stations.Count == 1, "Source registration failed.");
                Record("source-registered", session); _stage++; break;
            case 2:
                if (!Observe("player-source-registered", (_profile?.CommandResult ?? "Command completed"))) return false;
                Retire(); session.PreparePlayerTarget("Farm", dx, dy, destination); Open(session);
                Form("station-details", 0, "accept_destination"); _stage++; break;
            case 3:
                if (!Observe("player-destination-ready", "")) return false;
                Activate("register"); Require(session.ReadSnapshot().Stations.Count == 2, "Destination registration failed.");
                Record("destination-registered", session); _stage++; break;
            case 4:
                if (!Observe("player-destination-registered", (_profile?.CommandResult ?? "Command completed"))) return false;
                Retire(); session.PreparePlayerTarget("Farm", 0, 0, null); Open(session);
                Select<FlowStationDetails>("source-station", value => value.Name == "accept_source");
                Select<FlowStationDetails>("destination-station", value => value.Name == "accept_destination");
                Select<FlowInventorySlot>("source-cargo", value => value.Index == 0);
                _stage++; break;
            case 5:
                if (!Observe("player-route-ready", "")) return false;
                Activate("link"); Require(session.ReadSnapshot().Links.Count == 1, "The Window did not add one directed link.");
                Record("route-created", session); _stage++; break;
            case 6:
                if (!Observe("player-route-created", (_profile?.CommandResult ?? "Command completed"))) return false;
                Activate("send");
                FlowParcelSnapshot whole = session.ReadSnapshot().Parcels.Single();
                Require(whole.Quantity == 8 && whole.State == ParcelState.Reserved, "Whole-stack Window admission differed.");
                WholeParcel = whole.Id; Record("whole-admitted", session); _stage++; break;
            case 7:
                if (!Observe("player-whole-created", (_profile?.ShipmentResult ?? "Shipment created"))) return false;
                Select<FlowInventorySlot>("source-cargo", value => value.Index == 1);
                Form("shipment-quantity", 0, "5"); _stage++; break;
            case 8:
                if (!Observe("player-partial-ready", (_profile?.ShipmentResult ?? "Shipment created"))) return false;
                Activate("send-quantity");
                FlowParcelSnapshot partial = session.ReadSnapshot().Parcels.Single(parcel => parcel.Id != WholeParcel);
                Require(session.ReadSnapshot().Parcels.Count == 2 && partial.Quantity == 5 && partial.State == ParcelState.Reserved,
                    "Partial Window admission duplicated a shipment or changed selected quantity.");
                PartialParcel = partial.Id; Record("partial-admitted", session); _stage++; break;
            case 9:
                if (!Observe("player-partial-created", (_profile?.ShipmentResult ?? "Shipment created"))) return false;
                Require(source.GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Where(item => item is not null).Sum(item => item.Stack) == 21
                    && !destination.GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Any(item => item is not null),
                    "Admission changed physical custody before the normal game tick.");
                Retire(); RestoreInput(); _stage++; return false;
        }
        return false;
    }

    internal bool ObserveDelivered(FlowGameSession session)
    {
        if (_historyObserved) return CompleteInputRestore();
        if (!_inputOwned)
        {
            if (!Context.IsPlayerFree || Game1.activeClickableMenu is not null || Game1.fadeToBlackAlpha > 0) return false;
            if (!TryBeginInputProfile()) return false;
        }
        if (!_observation.SettleNativeProfile(ApplyInput)) return false;
        RequireProfile();
        if (_surface is null)
        {
            if (!Context.IsPlayerFree || Game1.activeClickableMenu is not null) return false;
            session.PreparePlayerTarget("Farm", 0, 0, null); Open(session);
            Select<FlowParcelSnapshot>("history", value => value.Id == PartialParcel);
        }
        string expected = _profile?.DeliveredHistory(PartialParcel) ?? PartialParcel + " · Delivered · attempts: 1";
        if (!Observe("player-delivered-reopened", expected, "history-detail")) return false;
        Record("delivered-reopened", session); Retire(); RestoreInput(); _historyObserved = true; return false;
    }

    private void Open(FlowGameSession session)
    {
        Require(_surface is null && Game1.activeClickableMenu is null, "Window creation requires an unowned native slot.");
        _experience = _open(session, _host);
        _surface = _host.Last ?? throw new InvalidOperationException("The production factory did not create a Window.");
        Require(_surface.Visible, "The production Window did not become visible.");
    }

    private void Form(string element, int field, string value)
    {
        var form = (UiFormState)Element(element);
        form.Fields[field].Value.Value = value;
        Require(form.Fields[field].Value.Value == value, "Prepared semantic form value was rejected.");
    }

    private void Select<T>(string element, Func<T, bool> predicate)
    {
        var collection = (FlowSelectionSource<T>)Element(element);
        var matches = Enumerable.Range(0, collection.Count).Where(index => predicate(collection.Value[index])).ToArray();
        Require(matches.Length == 1 && collection.TrySelect(collection.GetItem(matches[0]).Id),
            "Prepared semantic selection must identify exactly one supported item: " + element);
    }

    private object Element(string key) => _experience!.Experience.Elements.Single(element =>
        element.Id == _experience.Experience.Id.Child("element/" + key)).Source!;

    private void Activate(string key)
        => Require(_api.ActionAutomation.Activate(_surface!, _experience!.Experience.Id.Child("action/" + key)),
            "The owning Window did not admit normalized action: " + key);

    private bool Observe(string name, string result, string element = "result")
    {
        RequireProfile();
        var experience = _experience!;
        UiSymbolId id = experience.Experience.Id.Child("element/" + element);
        if (!_api.Observation.Capture(_surface!).Elements.Any(row => row.SemanticId == id && row.Value == result)) return false;
        return _observation.Observe(new FlowUiAcceptanceObservation.Frame(name, experience.Experience, _surface!, _profile?.Locale ?? "en", _profile?.Scale ?? 1, false,
            experience.Publication.Version, () => experience.Publication.Version, snapshot =>
            {
                Require(snapshot.Elements.Count(row => row.SemanticId == id && row.Value == result) == 1
                    && (result.Length == 0 || snapshot.Texts.Any(row => row.SemanticId == id && row.Text == result)),
                    "The committed production result was not submitted in the fresh native frame.");
            }, id));
    }

    private void Record(string milestone, FlowGameSession session)
    {
        Require(_milestones.Count < 12, "Window evidence exceeded its fixed bound.");
        // Only called from Update. Draw checks immutable semantic publication and rendered pixels.
        _milestones.Add(new { milestone, snapshot = session.ReadSnapshot(), resources = session.ReadResources() });
    }

    private void Retire()
    {
        if (_surface is null) return;
        var surface = _surface; _close(); _observation.VerifyRetired(surface);
        _surface = null; _experience = null;
    }

    private bool TryBeginInputProfile()
    {
        Require(!_inputOwned && !_restoringInput, "A prior input profile lease is still active.");
        if (_profile is not null)
        {
            if (_acquisition.ObserveOwner(Game1.options))
            {
                _observation.ResetNativeSettling();
                return false;
            }
            if (!_observation.SettleNativeProfile(static () => { })
                || !_acquisition.CanAcquire(Game1.options, Game1.options.baseUIScale,
                    Game1.options.desiredUIScale, Game1.options.uiScale, _observation.SettledNativeFrame)) return false;
        }
        _inputOptions = Game1.options; _originalMode = Game1.options.gamepadMode;
        _originalControls = Game1.options.gamepadControls; _inputOwned = true;
        if (_profile is not null)
        {
            _originalBaseScale = Game1.options.baseUIScale;
            _originalDesiredScale = Game1.options.desiredUIScale;
            _originalAppliedScale = Game1.options.uiScale;
            _originalLanguage = LocalizedContentManager.CurrentLanguageCode;
            var modLanguage = LocalizedContentManager.CurrentModLanguage;
            _originalModLanguage = modLanguage;
            var language = _originalLanguage;
            _restoreLanguage = () =>
            {
                if (language == LocalizedContentManager.LanguageCode.mod)
                    LocalizedContentManager.SetModLanguage(modLanguage
                        ?? throw new InvalidOperationException("The original mod language was lost."));
                else LocalizedContentManager.CurrentLanguageCode = language;
            };
            Game1.options.desiredUIScale = _profile.Scale;
            LocalizedContentManager.CurrentLanguageCode = _profile.Language;
        }
        ApplyInput(); _observation.ResetNativeSettling();
        _acquisition.Release();
        return true;
    }

    private void ApplyInput()
    {
        Require(ReferenceEquals(_inputOptions, Game1.options), "The native options owner changed before input profile application.");
        Game1.options.gamepadMode = Options.GamepadModes.ForceOff;
        Game1.options.gamepadControls = false;
    }

    private void ApplyOriginalInput()
    {
        Require(ReferenceEquals(_inputOptions, Game1.options), "The native options owner changed during the Window profile lease.");
        Game1.options.gamepadMode = _originalMode;
        Game1.options.gamepadControls = _originalControls;
    }

    private void RestoreInput()
    {
        if (!_inputOwned) return;
        ApplyOriginalInput();
        if (_profile is not null)
        {
            Game1.options.baseUIScale = _originalBaseScale;
            Game1.options.desiredUIScale = _originalDesiredScale;
            _restoreLanguage!();
            RequireRestoredSettings(requireApplied: false);
            Game1.game1.refreshWindowSettings();
        }
        _observation.ResetNativeSettling();
        _inputOwned = false; _restoringInput = true;
    }

    private bool CompleteInputRestore()
    {
        Require(_restoringInput, "There is no pending Window input restoration.");
        if (!_observation.SettleNativeProfile(ApplyOriginalInput)) return false;
        Require(ReferenceEquals(_inputOptions, Game1.options) && Game1.options.gamepadMode == _originalMode
            && Game1.options.gamepadControls == _originalControls, "The exact original input profile did not survive fresh completed draws.");
        if (_profile is not null) RequireRestoredSettings(requireApplied: true);
        Require(_inputRestorations.Count < 2, "Window input restoration exceeded its two leases.");
        _inputRestorations.Add(new { mode = _originalMode.ToString(), controls = _originalControls,
            originalLanguage = _profile is null ? null : _originalLanguage.ToString(),
            originalBaseScale = _profile is null ? (float?)null : _originalBaseScale,
            originalDesiredScale = _profile is null ? (float?)null : _originalDesiredScale,
            originalAppliedScale = _profile is null ? (float?)null : _originalAppliedScale,
            settledFrame = _observation.SettledNativeFrame });
        _restoringInput = false; _inputOptions = null; _restoreLanguage = null; _originalModLanguage = null;
        return true;
    }

    private void RequireProfile()
    {
        Require(ReferenceEquals(_inputOptions, Game1.options), "The player profile options owner changed.");
        if (_profile is null)
        {
            Require(LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.en
                && Game1.options.uiScale == 1 && Game1.options.desiredUIScale == 1,
                "This bounded prepared Window scenario requires the canonical EN/100% profile.");
            FlowUiActionsAcceptance.RequireEnglishInput(Game1.options.gamepadMode, Game1.options.gamepadControls);
            return;
        }
        _profile.RequireActual(LocalizedContentManager.CurrentLanguageCode, Game1.options.desiredUIScale,
            Game1.options.uiScale, Game1.options.gamepadMode, Game1.options.gamepadControls,
            _observation.SettledNativeFrame);
        if (_profileApplications.Count == _inputRestorations.Count)
        {
            Require(_profileApplications.Count < 2, "Player profile application exceeded two leases.");
            _profileApplications.Add(new { _profile.Scenario, _profile.Locale, _profile.Scale,
                language = LocalizedContentManager.CurrentLanguageCode.ToString(),
                mode = Game1.options.gamepadMode.ToString(), controls = Game1.options.gamepadControls,
                settledFrame = _observation.SettledNativeFrame });
        }
    }

    private void RequireRestoredSettings(bool requireApplied)
    {
        Require(ReferenceEquals(_inputOptions, Game1.options)
            && Game1.options.baseUIScale == _originalBaseScale && Game1.options.desiredUIScale == _originalDesiredScale
            && Game1.options.gamepadMode == _originalMode && Game1.options.gamepadControls == _originalControls
            && LocalizedContentManager.CurrentLanguageCode == _originalLanguage
            && ReferenceEquals(LocalizedContentManager.CurrentModLanguage, _originalModLanguage),
            "The exact original player visual settings were not restored.");
        if (requireApplied)
            Require(Game1.options.uiScale == _originalAppliedScale && _observation.SettledNativeFrame is { } frame
                && frame.BaseScale == _originalBaseScale && frame.DesiredScale == _originalDesiredScale
                && frame.AppliedScale == _originalAppliedScale,
                "The original player scale did not survive fresh completed native draws.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _close(); }
        finally
        {
            try { if (_inputOwned) RestoreInput(); else if (_restoringInput) ApplyOriginalInput(); }
            finally { _observation.Dispose(); }
        }
    }

    private sealed class HostApi : IUiSemanticHostApi
    {
        private readonly IFlowUiHostAcceptanceApi _inner;
        internal HostApi(IFlowUiHostAcceptanceApi inner) => _inner = inner;
        internal IUiSemanticSurfaceSession? Last { get; private set; }
        public int ApiVersion => _inner.ApiVersion;
        public IUiSemanticSurfaceAutomation Automation => _inner.Automation;
        public IUiSemanticSurfaceSession CreateSurface(UiExperienceDefinition experience, UiSemanticHostKind kind, UiSemanticSurfaceOptions options)
        {
            Require(kind == UiSemanticHostKind.Window, "Production Flow selected an unexpected host kind.");
            return Last = _inner.CreateSurface(experience, kind, options);
        }
        public IUiSemanticSurfaceSession CreateActiveMenuOverlay(UiExperienceDefinition experience, UiSemanticSurfaceOptions options)
            => throw new InvalidOperationException("Player Window acceptance must not substitute an overlay.");
        public IUiSemanticTerminalSession CreateTerminal(UiSemanticTerminalDefinition terminal, UiSemanticSurfaceOptions options)
            => throw new InvalidOperationException("Player Window acceptance must not substitute a Terminal.");
    }
}
