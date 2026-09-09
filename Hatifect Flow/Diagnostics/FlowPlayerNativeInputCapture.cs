using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using Hatifect.Flow.Application;
using Hatifect.Flow.Sessions;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using Microsoft.Xna.Framework.Input;

namespace Hatifect.Flow.Diagnostics;

// Read-only SMAPI observation for the exact isolated input scenario. The external runner
// declares OS injection; counters cannot establish that a human supplied an event.
internal sealed class FlowPlayerNativeInputCapture : IDisposable
{
    internal const string Scenario = "flow.ui.player.input";
    private readonly IModHelper _helper;
    private readonly FlowGameSession _session;
    private readonly FlowPlayerInputJournal _journal;
    private readonly string _runId;
    private readonly string _path;
    private readonly List<EntryAttempt> _attempts = new(32);
    private readonly List<Opening> _openings = new(32);
    private long _update, _pressed, _released, _tab, _backspace;
    private bool _disposed;
    private readonly KeybindList? _observedBinding;
    private readonly List<InputObservation> _transitions = new(32);
    private InputObservation? _latestObservation;
    private long _buttonsChanged, _kPressed, _kReleased, _droppedTransitions;
    private Exception? _telemetryFailure;
    private readonly record struct InputObservation(long Update, string Phase, bool GameActive,
        bool RawKDown, bool SmapiKSuppressed,
        [property: JsonConverter(typeof(JsonStringEnumConverter))] SButtonState SmapiKState,
        [property: JsonConverter(typeof(JsonStringEnumConverter))] SButtonState BindingState,
        bool BindingDown, bool BindingJustPressed);


    internal sealed record EntryAttempt(int Sequence, long Update, bool PlayerFree,
        bool Authority, bool MenuOpen, bool SessionPresent, bool GuardsPassed);
    internal sealed record Opening(int Attempt, long Update, Guid Target);

    internal FlowPlayerNativeInputCapture(IModHelper helper, FlowGameSession session,
        FlowHostAcceptance.AcceptanceRequest request, Func<string> capture)
    {
        FlowHostAcceptance.Require(Environment.GetEnvironmentVariable("HATIFECT_TEST_MODE") == "1"
            && Environment.GetEnvironmentVariable("HATIFECT_TEST_AUTOMATED") == "1"
            && Environment.GetEnvironmentVariable("HATIFECT_TEST_SCENARIO") == Scenario,
            "Native input capture requires its exact isolated scenario.");
        _helper = helper;
        _session = session;
        _observedBinding = helper.ReadConfig<FlowConfig>().OpenNetwork;
        _runId = request.RunId;
        _path = Path.Combine(request.Artifact, "diagnostics", "flow-player-input-progress.json");
        _journal = new(session, () => { RequireActive(); return Stamp(); }, capture);
        helper.Events.Input.ButtonsChanged += OnButtonsChanged;
        helper.Events.Input.ButtonPressed += OnPressed;
        helper.Events.Input.ButtonReleased += OnReleased;
        helper.Events.GameLoop.UpdateTicked += OnUpdate;
    }

    internal IReadOnlyList<FlowPlayerCommandTrace> Commands => _journal.Entries;
    internal Exception? Failure => _journal.Failure ?? _telemetryFailure;
    internal int OpenedEntries => _openings.Count;
    internal bool HasRejectedBusyEntry => _attempts.Any(value => value.MenuOpen && value.SessionPresent && !value.GuardsPassed);
    internal bool HasEmptyTargetOpening => _openings.Select((value, index) => value.Target == Guid.Empty
        && _openings.Take(index).Count(previous => previous.Target != Guid.Empty) >= 2).Any(value => value);
    internal object Evidence => new
    {
        input = Stamp(), entryAttempts = _attempts.ToArray(), openings = _openings.ToArray(),
        commands = _journal.Entries.ToArray(), failure = Failure?.ToString(), inputTelemetry = InputTelemetry
    };

    internal void ConfirmOpened(Guid target)
    {
        RequireActive();
        if (_attempts.Count == 0 || !_attempts[^1].GuardsPassed || _openings.Any(value => value.Attempt == _attempts[^1].Sequence))
            throw new InvalidOperationException("A native opening has no admitted ordinary keybind attempt.");
        _openings.Add(new(_attempts[^1].Sequence, _update, target));
    }

    internal IFlowNetworkApplication ForOrdinaryEntry(FlowGameSession session)
    {
        RequireActive();
        if (!ReferenceEquals(session, _session))
            throw new InvalidOperationException("Input evidence cannot cross a game-session generation.");
        return _journal;
    }

    // Called by the production keybind handler before its existing guards. It records
    // the decision without suppressing, translating, or replaying any input.
    internal void ObserveEntry(bool playerFree, bool authority, bool menuOpen, bool sessionPresent)
    {
        RequireActive();
        if (_attempts.Count >= 32)
            throw new InvalidOperationException("Native entry evidence exceeded its fixed bound.");
        _attempts.Add(new(_attempts.Count + 1, _update, playerFree, authority, menuOpen,
            sessionPresent, playerFree && authority && !menuOpen && sessionPresent));
    }

    internal void Publish(string stage, object fixture)
    {
        RequireActive();
        FlowHostAcceptance.AtomicJson(_path, new
        {
            protocolVersion = 1, requestId = _runId, scenarioId = Scenario, stage,
            declaredOrigin = "os-injected", input = Stamp(), entryAttempts = _attempts.ToArray(),
            openings = _openings.ToArray(), commands = _journal.Entries, fixture, inputTelemetry = InputTelemetry,
            limits = "SMAPI events and Flow commands only; UI owns focused element, visible bounds and completed frames."
        });
    }

    private FlowPlayerInputStamp Stamp() => new("os-injected", _update, _pressed, _released, _tab, _backspace);

    private void OnUpdate(object? sender, UpdateTickedEventArgs e)
    {
        if (_disposed) return;
        _update++;
        ObserveInput("UpdateTicked");
    }

    private object InputTelemetry => new
    {
        latest = _latestObservation,
        transitions = _transitions.ToArray(),
        buttonsChangedEventsTotal = _buttonsChanged,
        kPressedTotal = _kPressed, kReleasedTotal = _kReleased,
        samplesTruncated = _droppedTransitions > 0, droppedTransitions = _droppedTransitions,
        limit = 32, failure = _telemetryFailure?.ToString(),
        note = "Read-only polling and SMAPI event observation; not proof of hardware input origin. Binding is the scenario startup config snapshot. Update counts completed UpdateTicked callbacks, not rendered frames; input samples may follow production suppression."
    };

    private void OnButtonsChanged(object? sender, ButtonsChangedEventArgs e)
    {
        if (_disposed) return;
        _buttonsChanged++;
        bool pressed = e.Pressed.Contains(SButton.K), released = e.Released.Contains(SButton.K);
        if (pressed) _kPressed++;
        if (released) _kReleased++;
        ObserveInput("ButtonsChanged", pressed || released);
    }

    private void ObserveInput(string phase, bool force = false)
    {
        if (_telemetryFailure is not null) return;
        try
        {
            var next = new InputObservation(_update, phase, Game1.game1.IsActive,
                Keyboard.GetState().IsKeyDown(Keys.K), _helper.Input.IsSuppressed(SButton.K),
                _helper.Input.GetState(SButton.K),
                _observedBinding?.GetState() ?? SButtonState.None,
                _observedBinding?.IsDown() == true, _observedBinding?.JustPressed() == true);
            InputObservation? previous = _latestObservation;
            _latestObservation = next;
            bool changed = previous is not { } old || old.GameActive != next.GameActive
                || old.RawKDown != next.RawKDown || old.SmapiKSuppressed != next.SmapiKSuppressed
                || old.SmapiKState != next.SmapiKState
                || old.BindingState != next.BindingState || old.BindingDown != next.BindingDown
                || old.BindingJustPressed != next.BindingJustPressed;
            if (!changed && !force) return;
            if (_transitions.Count < 32) _transitions.Add(next);
            else _droppedTransitions++;
        }
        catch (Exception error) { _telemetryFailure ??= error; }
    }

    private void OnPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (_disposed) return;
        if (e.Button == SButton.MouseLeft) _pressed++;
        else if (e.Button == SButton.Tab) _tab++;
        else if (e.Button == SButton.Back) _backspace++;
    }

    private void OnReleased(object? sender, ButtonReleasedEventArgs e)
    {
        if (!_disposed && e.Button == SButton.MouseLeft) _released++;
    }

    private void RequireActive()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FlowPlayerNativeInputCapture));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _helper.Events.Input.ButtonsChanged -= OnButtonsChanged;
        _helper.Events.Input.ButtonPressed -= OnPressed;
        _helper.Events.Input.ButtonReleased -= OnReleased;
        _helper.Events.GameLoop.UpdateTicked -= OnUpdate;
    }
}
