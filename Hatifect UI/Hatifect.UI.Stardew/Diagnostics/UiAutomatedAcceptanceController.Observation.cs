using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;
using Hatifect.UI.Experience;
using Hatifect.UI.Stardew.Semantic;

namespace Hatifect.UI.Stardew;

internal sealed partial class UiAutomatedAcceptanceController
{
    private enum ObservationStage { Initial, Updated, Reentrant, Settled, Environment, Reopened, Restoring }
    private readonly List<object> _observationCaptures = new();
    private IUiSemanticSurfaceObservationApi? _observationApi;
    private IUiSemanticSurfaceSession? _observedSurface;
    private IUiSemanticSurfaceSession? _retiredObservedSurface;
    private UiExperienceDefinition? _observationExperience;
    private ObservationSource[] _observationSources = Array.Empty<ObservationSource>();
    private ActionPumpCoverMenu? _observationCover;
    private ObservationStage _observationStage;
    private UiSemanticSurfaceSnapshot? _observationReentered;
    private UiSemanticSurfaceSnapshot? _observationOld;
    private Exception? _observationCallbackError;
    private bool _observationActive;
    private bool _observationRendered;
    private bool _observationReenterNext;
    private bool _observationRejectAvailability;
    private bool _observationAlternatingAvailability;
    private bool _observationRussian;
    private int _observationAvailabilityReads;
    private int _observationVersion = 1;
    private int _observationTicks;
    private int _observationClosed;
    private float _observationOriginalScale;
    private float _observationOriginalDesiredScale;
    private bool _observationOriginalGamepad;
    private Options.GamepadModes _observationOriginalGamepadMode;
    private LocalizedContentManager.LanguageCode _observationOriginalLanguage;
    private StardewValley.GameData.ModLanguage? _observationOriginalModLanguage;
    private string? _observationOriginalLocale;
    private bool _observationSettingsCaptured;
    private Options? _observationOriginalOptions;
    private UiNativePresentationGate? _observationNativeGate;
    private long _observationCompletedFrames;
    private bool _observationNativeRecorded;
    private readonly List<object> _observationNativeFrames = new();

    private void BeginObservation()
    {
        _dogfood.CloseOverlay();
        _dogfood.Close();
        RequireAction(Game1.activeClickableMenu == null, "Observation requires a free isolated native menu slot.");
        _observationApi = _helper.ModRegistry.GetApi<IUiSemanticSurfaceObservationApi>("Hatifect.UI")
            ?? throw new InvalidOperationException("The additive observation API is unavailable through SMAPI.");
        RequireAction(_observationApi.ApiVersion == 1 && _observationApi.Observation.IsEnabled,
            "The exact harness did not enable the optional observation facet.");
        _observationOriginalOptions = Game1.options;
        _observationOriginalScale = Game1.options.baseUIScale;
        _observationOriginalDesiredScale = Game1.options.desiredUIScale;
        _observationOriginalGamepad = Game1.options.gamepadControls;
        _observationOriginalGamepadMode = Game1.options.gamepadMode;
        _observationOriginalLanguage = LocalizedContentManager.CurrentLanguageCode;
        _observationOriginalModLanguage = LocalizedContentManager.CurrentModLanguage;
        _observationOriginalLocale = LocalizedContentManager.LanguageCodeString(_observationOriginalLanguage);
        _observationSettingsCaptured = true;
        Game1.options.desiredUIScale = 1;
        BeginObservationNativeTransition(1,
            UiSemanticStardewEnvironmentCapture.ResolveLocale(LocalizedContentManager.LanguageCode.en),
            false, Options.GamepadModes.ForceOff);
        LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.en;
        UiSymbolId id = ActionId("observation");
        var builder = new UiExperienceBuilder(id, "Surface observation");
        _observationSources = Enumerable.Range(0, 5).Select(index => new ObservationSource(() => ObservationText(index))).ToArray();
        for (int i = 0; i < _observationSources.Length; i++) builder.Monitor("Field" + i, _observationSources[i]);
        UiActionDefinition[] actions = Enumerable.Range(0, 5).Select(index => new UiActionDefinition(id.Child("action/" + index),
            "Action " + index, () => throw new InvalidOperationException("Observation must not invoke an action."), () =>
            {
                _observationAvailabilityReads++;
                if (_observationRejectAvailability) throw new InvalidOperationException("Observation reread availability.");
                return !_observationAlternatingAvailability || index % 2 != 0;
            })).ToArray();
        _observationExperience = builder.Actions("Actions", actions).Build();
        Game1.activeClickableMenu = _observationCover = new ActionPumpCoverMenu();
        CreateObservedSurface();
        Exception? foreign = null;
        try { new UiStardewApiBridge(_helper).Observation.Capture(_observedSurface!); }
        catch (ArgumentException error) { foreign = error; }
        Record("semantic.observation.api", foreign is not null,
            "The optional API is obtained through SMAPI; another service cannot observe its opaque handle.");
        _observationActive = true;
    }

    private void CreateObservedSurface()
    {
        _observedSurface = _observationApi!.CreateActiveMenuOverlay(_observationExperience!, new(_observationExperience!.Id));
        _observedSurface.Rendered += OnObservedSurfaceRendered;
        _observedSurface.Closed += () => _observationClosed++;
        _observedSurface.Show();
        _observationRendered = false;
    }

    private string ObservationText(int index)
        => (_observationRussian ? "Посылка " : "Parcel ") + index + " / " + _observationVersion;

    private void OnObservedSurfaceRendered()
    {
        _observationRendered = true;
        if (!_observationReenterNext) return;
        _observationReenterNext = false;
        try
        {
            UiSemanticSurfaceSnapshot before = _observationApi!.Observation.Capture(_observedSurface!);
            RequireObservationFrame(before);
            _observationVersion = 3;
            _observedSurface!.Refresh();
            _observationReentered = _observationApi.Observation.Capture(_observedSurface);
            RequireAction(_observationReentered.AcceptedFrame != before.AcceptedFrame
                && _observationReentered.RenderedFrame == before.RenderedFrame
                && _observationReentered.CompletedRenderPass == before.CompletedRenderPass
                && !_observationReentered.IsAcceptedFrameRendered,
                "A Rendered callback attributed its freshly accepted scene to the preceding draw.");
            _observationCaptures.Add(new { phase = "inside-rendered-before", snapshot = before });
            _observationCaptures.Add(new { phase = "inside-rendered-after", snapshot = _observationReentered });
        }
        catch (Exception error) { _observationCallbackError = error; }
    }

    private bool AdvanceObservation()
    {
        if (!_observationActive) return false;
        if (_observationCallbackError is { } callbackError) throw callbackError;
        if (++_observationTicks > 3600) throw new TimeoutException("Native surface observation timed out.");
        if (!CompleteObservationNativeTransition()) return true;
        if (_observationStage == ObservationStage.Restoring)
        {
            RequireAction(LocalizedContentManager.CurrentLanguageCode == _observationOriginalLanguage
                && ReferenceEquals(LocalizedContentManager.CurrentModLanguage, _observationOriginalModLanguage)
                && LocalizedContentManager.LanguageCodeString(_observationOriginalLanguage) == _observationOriginalLocale,
                "Observation restoration lost its original locale identity after the native resize.");
            _observationActive = false;
            _observationNativeGate = null;
            _observationSettingsCaptured = false;
            Record("semantic.observation.restored", _terminalFailure is null && _observationClosed == 2
                && _observationSources.All(source => source.Subscribers == 0),
                "Both owners retire; original options and locale are confirmed after fresh completed native draws.");
            _capturePending = true;
            return true;
        }
        if (!_observationRendered) return true;
        _observationRendered = false;
        UiSemanticSurfaceSnapshot snapshot = _observationApi!.Observation.Capture(_observedSurface!);
        if (_observationStage == ObservationStage.Reentrant)
        {
            RequireAction(_observationReentered is not null && !_observationReentered.IsAcceptedFrameRendered,
                "The real Rendered observer did not retain its undrawn replacement.");
            Record("semantic.observation.reentrant", true,
                "Recomposition inside real Rendered advances accepted state without relabelling the completed surface pass.");
            _observationStage = ObservationStage.Settled;
        }
        if (!snapshot.IsAcceptedFrameRendered) return true;
        if (_observationStage == ObservationStage.Environment
            && (snapshot.Environment?.Locale != "ru-RU" || snapshot.Environment.Scale != 1.25f
                || snapshot.Environment.InputMode != Semantics.UiInputMode.Controller)) return true;
        RequireObservationFrame(snapshot);
        _observationCaptures.Add(new { phase = _observationStage.ToString(), snapshot });
        switch (_observationStage)
        {
            case ObservationStage.Initial:
                Record("semantic.observation.initial", true, "Five semantic fields and actions are tied to an actual completed native surface pass.");
                int reads = _observationSources.Sum(source => source.Reads);
                int availability = _observationAvailabilityReads;
                foreach (var source in _observationSources) source.Reject = true;
                _observationRejectAvailability = true;
                try
                {
                    UiSemanticSurfaceSnapshot inert = _observationApi.Observation.Capture(_observedSurface!);
                    RequireAction(inert.AcceptedFrame == snapshot.AcceptedFrame && inert.RenderedFrame == snapshot.RenderedFrame
                        && inert.CompletedRenderPass == snapshot.CompletedRenderPass
                        && inert.Elements.SequenceEqual(snapshot.Elements) && inert.Texts.SequenceEqual(snapshot.Texts)
                        && _observationSources.Sum(source => source.Reads) == reads && _observationAvailabilityReads == availability,
                        "Observation reread sources or advanced the accepted frame.");
                }
                finally
                {
                    foreach (var source in _observationSources) source.Reject = false;
                    _observationRejectAvailability = false;
                }
                Record("semantic.observation.inert", true, "Poisoned sources and availability delegates are never read by capture.");
                _observationVersion = 2;
                _observationAlternatingAvailability = true;
                _observedSurface!.Refresh();
                _observationStage = ObservationStage.Updated;
                break;
            case ObservationStage.Updated:
                Record("semantic.observation.updated", true, "Changed values and mixed action availability appear in the next actual rendered frame.");
                _observationReenterNext = true;
                _observationStage = ObservationStage.Reentrant;
                break;
            case ObservationStage.Settled:
                _observationRussian = true;
                LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.ru;
                Game1.options.desiredUIScale = 1.25f;
                BeginObservationNativeTransition(1.25f, "ru-RU", true, Options.GamepadModes.ForceOn);
                _observationStage = ObservationStage.Environment;
                break;
            case ObservationStage.Environment:
                RequireAction(snapshot.Environment!.Locale == "ru-RU" && snapshot.Environment.Scale == 1.25f
                    && snapshot.Environment.InputMode == Semantics.UiInputMode.Controller,
                    "Observed environment did not match the accepted native facets.");
                Record("semantic.observation.environment", true,
                    "Automatic native locale/scale/controller-profile change reaches accepted Russian text and a completed pass.");
                _observationOld = snapshot;
                IUiSemanticSurfaceSession retiring = _observedSurface!;
                _retiredObservedSurface = retiring;
                retiring.Hide();
                retiring.Dispose();
                UiSemanticSurfaceSnapshot retired = _observationApi.Observation.Capture(retiring);
                RequireAction(retired.InstanceId == snapshot.InstanceId && retired.Retired && !retired.Visible
                    && retired.Environment is null && retired.AcceptedFrame is null && retired.RenderedFrame is null
                    && retired.Elements.Count == 0 && retired.Texts.Count == 0 && _observationClosed == 1
                    && _observationSources.All(source => source.Subscribers == 0),
                    "Retired observation retained content, native ownership or subscriptions.");
                _observationCaptures.Add(new { phase = "retired", snapshot = retired });
                Record("semantic.observation.retirement", true, "Terminally retired handle returns only stable identity/lifecycle after real teardown.");
                _observationVersion = 4;
                CreateObservedSurface();
                _observationStage = ObservationStage.Reopened;
                break;
            case ObservationStage.Reopened:
                RequireAction(snapshot.InstanceId != _observationOld!.InstanceId && snapshot.SurfaceId == _observationOld.SurfaceId
                    && _observationApi.Observation.Capture(_retiredObservedSurface!).Retired,
                    "Reopening reused the retired instance or revived its old handle.");
                Record("semantic.observation.reopen", true, "Same semantic IDs reopen as a distinct instance while old snapshots/handles remain inert.");
                int ownerLossReads = _observationSources.Sum(source => source.Reads);
                int ownerLossAvailability = _observationAvailabilityReads;
                int ownerLossSubscribers = _observationSources.Sum(source => source.Subscribers);
                Game1.activeClickableMenu = _observationCover = new ActionPumpCoverMenu();
                Exception? lostOwner = null;
                try { _observationApi.Observation.Capture(_observedSurface!); }
                catch (InvalidOperationException error) { lostOwner = error; }
                RequireAction(lostOwner is not null && _observationClosed == 1 && _observedSurface!.Visible
                    && _observationSources.Sum(source => source.Reads) == ownerLossReads
                    && _observationAvailabilityReads == ownerLossAvailability
                    && _observationSources.Sum(source => source.Subscribers) == ownerLossSubscribers,
                    "Capture must reject a lost native menu owner immediately without reading or retiring the surface.");
                Record("semantic.observation.native-owner", true,
                    "Replacing the native menu rejects capture before the next Update, without callbacks or source reads.");
                StopObservation(awaitNativeRestoration: true);
                if (_terminalFailure is not null) throw new InvalidOperationException("Observation cleanup failed before restoration acceptance.");
                _observationStage = ObservationStage.Restoring;
                BeginObservationNativeTransition(_observationOriginalDesiredScale,
                    UiSemanticStardewEnvironmentCapture.ResolveLocale(_observationOriginalLanguage),
                    _observationOriginalGamepad, _observationOriginalGamepadMode);
                _observationActive = true;
                break;
        }
        _observationTicks = 0;
        return true;
    }

    private void BeginObservationNativeTransition(float scale, string locale, bool controls, Options.GamepadModes mode)
    {
        _observationNativeGate = new(scale, locale, controls, (int)mode);
        _observationNativeRecorded = false;
    }

    private void ObserveObservationNativeFrame()
    {
        if (!_observationActive || _observationNativeGate is null) return;
        RequireAction(ReferenceEquals(Game1.options, _observationOriginalOptions),
            "Observation native options were replaced during a transition.");
        var game = Game1.game1;
        var graphics = Game1.graphics.GraphicsDevice;
        var window = game.Window.ClientBounds;
        var ui = game.uiScreen;
        var screen = game.screen;
        _observationNativeGate.ObserveCompletedDraw(++_observationCompletedFrames, new(
            Game1.options.baseUIScale, Game1.options.desiredUIScale, Game1.options.uiScale,
            window.Width, window.Height, Game1.uiViewport.Width, Game1.uiViewport.Height,
            ui is { IsDisposed: false } ? ui.Width : 0, ui is { IsDisposed: false } ? ui.Height : 0,
            screen is { IsDisposed: false } ? screen.Width : 0, screen is { IsDisposed: false } ? screen.Height : 0,
            graphics.PresentationParameters.BackBufferWidth, graphics.PresentationParameters.BackBufferHeight,
            graphics.Viewport.Width, graphics.Viewport.Height, graphics.RenderTargetCount,
            UiSemanticStardewEnvironmentCapture.ResolveLocale(LocalizedContentManager.CurrentLanguageCode),
            Game1.options.gamepadControls, (int)Game1.options.gamepadMode));
    }

    private bool CompleteObservationNativeTransition()
    {
        RequireAction(ReferenceEquals(Game1.options, _observationOriginalOptions),
            "Observation native options were replaced before profile application.");
        bool restoring = _observationStage == ObservationStage.Restoring;
        bool controller = restoring ? _observationOriginalGamepad : _observationStage is ObservationStage.Environment or ObservationStage.Reopened;
        var mode = restoring ? _observationOriginalGamepadMode
            : controller ? Options.GamepadModes.ForceOn : Options.GamepadModes.ForceOff;
        if (!_observationNativeGate!.CompleteProfile(() =>
        {
            Game1.options.gamepadMode = mode;
            Game1.options.gamepadControls = controller;
        })) return false;
        RequireAction(Game1.options.gamepadControls == controller && Game1.options.gamepadMode == mode,
            "Observation input profile changed after its completed native frame.");
        if (!_observationNativeRecorded)
        {
            RequireAction(_observationNativeFrames.Count < 3, "Observation native transitions exceeded their bound.");
            _observationNativeFrames.Add(new { phase = _observationStage.ToString(),
                completedFrame = _observationCompletedFrames, frame = _observationNativeGate.Settled,
                capturedBase = restoring ? _observationOriginalScale : (float?)null,
                capturedDesired = restoring ? _observationOriginalDesiredScale : (float?)null });
            _observationNativeRecorded = true;
        }
        return true;
    }

    private void RequireObservationFrame(UiSemanticSurfaceSnapshot snapshot)
    {
        RequireAction(snapshot.Visible && !snapshot.Retired && snapshot.IsAcceptedFrameRendered
            && !snapshot.Truncated && !snapshot.HasUnmappedContent && snapshot.UnobservedPortalCount == 0
            && snapshot.ExperienceId == _observationExperience!.Id,
            "The observed frame is incomplete, unmapped, undrawn or belongs to another Experience.");
        for (int i = 0; i < 5; i++)
        {
            UiSymbolId element = _observationExperience!.Elements.Single(item => item.Alias == "Field" + i).Id;
            RequireAction(snapshot.Elements.Any(row => row.SemanticId == element && row.Value == ObservationText(i))
                && snapshot.Texts.Any(row => row.SemanticId == element && row.Text == ObservationText(i)),
                "Materialized field " + i + " does not match the accepted state.");
            UiSymbolId action = _observationExperience.Id.Child("action/" + i);
            RequireAction(snapshot.Elements.Any(row => row.ActionId == action && row.SemanticId == action
                && row.Enabled == (!_observationAlternatingAvailability || i % 2 != 0)),
                "Materialized action " + i + " has the wrong identity or availability.");
        }
    }

    private void StopObservation(bool awaitNativeRestoration = false)
    {
        _observationActive = false;
        _observationNativeGate = null;
        List<Exception>? failures = null;
        try { _observedSurface?.Dispose(); } catch (Exception error) { (failures ??= new()).Add(error); }
        try
        {
            if (_observationSettingsCaptured)
            {
                RequireAction(ReferenceEquals(Game1.options, _observationOriginalOptions),
                    "Observation no longer owns the original native options.");
                Game1.options.baseUIScale = _observationOriginalScale;
                Game1.options.desiredUIScale = _observationOriginalDesiredScale;
                Game1.options.gamepadMode = _observationOriginalGamepadMode;
                Game1.options.gamepadControls = _observationOriginalGamepad;
                if (_observationOriginalLanguage == LocalizedContentManager.LanguageCode.mod)
                    LocalizedContentManager.SetModLanguage(_observationOriginalModLanguage
                        ?? throw new InvalidOperationException("The original observation locale descriptor is unavailable."));
                else LocalizedContentManager.CurrentLanguageCode = _observationOriginalLanguage;
                RequireAction(LocalizedContentManager.CurrentLanguageCode == _observationOriginalLanguage
                    && ReferenceEquals(LocalizedContentManager.CurrentModLanguage, _observationOriginalModLanguage)
                    && LocalizedContentManager.LanguageCodeString(_observationOriginalLanguage) == _observationOriginalLocale,
                    "Observation did not restore the original locale identity.");
                RequireAction(ReferenceEquals(Game1.options, _observationOriginalOptions)
                    && Game1.options.baseUIScale == _observationOriginalScale
                    && Game1.options.desiredUIScale == _observationOriginalDesiredScale
                    && Game1.options.gamepadMode == _observationOriginalGamepadMode
                    && Game1.options.gamepadControls == _observationOriginalGamepad,
                    "Observation locale restoration changed the captured options.");
                Game1.game1.refreshWindowSettings();
                // Keep the captured settings lease through successful native restoration.
                // If that later barrier fails, the ordinary cleanup path restores them again.
                if (!awaitNativeRestoration) _observationSettingsCaptured = false;
            }
        }
        catch (Exception error) { (failures ??= new()).Add(error); }
        try
        {
            if (_observationCover is not null && ReferenceEquals(Game1.activeClickableMenu, _observationCover)) Game1.activeClickableMenu = null;
        }
        catch (Exception error) { (failures ??= new()).Add(error); }
        if (failures is not null)
            RetainTerminalFailure("HARNESS-OBSERVATION-CLEANUP", new AggregateException(failures));
    }

    private sealed class ObservationSource : IUiSemanticSource<string>
    {
        private readonly Func<string> _read;
        internal ObservationSource(Func<string> read) => _read = read;
        internal int Reads { get; private set; }
        internal int Subscribers { get; private set; }
        internal bool Reject { get; set; }
        public string Value { get { Reads++; if (Reject) throw new InvalidOperationException("Observation reread its source."); return _read(); } }
        public object UntypedValue => Value;
        public Type ValueType => typeof(string);
        public event Action? Changed { add => Subscribers++; remove => Subscribers--; }
    }
}
