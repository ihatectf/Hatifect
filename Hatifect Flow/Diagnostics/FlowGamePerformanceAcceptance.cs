using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Hatifect.Flow.Application;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Inventory;
using Hatifect.Flow.Sessions;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;
using static Hatifect.Flow.Diagnostics.FlowHostAcceptance;

namespace Hatifect.Flow.Diagnostics;

internal sealed class FlowGamePerformanceAcceptance : IDisposable
{
    internal const string Scenario = "flow.chest.performance";
    internal const int Shipments = 80, Routes = 3, OperationLimit = 64;
    private static readonly string[] Checks = { "loaded", "saving", "saved", "paused", "due-work", "idle", "item-fidelity", "unchanged-files" };
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly Func<FlowGameSession?> _current;
    private readonly AcceptanceRequest _request;
    private readonly string _fingerprint;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly List<string> _errors = new();
    private readonly HashSet<string> _passed = new(StringComparer.Ordinal);
    private readonly Chest[] _sources = new Chest[Routes], _destinations = new Chest[Routes];
    private readonly string[][] _xml = new string[Routes][];
    private FlowGameSession? _session;
    private Guid _sessionId;
    private FlowPerformanceWindow? _window;
    private FlowPerformanceWindowReport? _paused, _idle;
    private FlowTickCounters _dueStart, _dueEnd;
    private long _dueLastTick;
    private int _dueFrames, _workTicks, _maximumOperations, _frames, _loads, _saving, _saved;
    private string _savedTree = "";
    private bool _ownsPause, _priorPause, _disposed;
    private Stage _stage;
    private enum Stage { Startup, Loading, ReadySave, Saving, Paused, Due, Idle, Exit }

    private FlowGamePerformanceAcceptance(IModHelper helper, IMonitor monitor, Func<FlowGameSession?> current)
    {
        _helper = helper; _monitor = monitor; _current = current;
        _request = ReadAcceptanceRequest(helper, Scenario); _fingerprint = RuntimeFingerprint();
        // Build-only GC workarounds must never affect actual performance evidence.
        Require(Environment.GetEnvironmentVariable("DOTNET_gcConcurrent") is null
            && Environment.GetEnvironmentVariable("COMPlus_gcConcurrent") is null, "Performance requires the ordinary game GC environment.");
        helper.Events.GameLoop.Saving += OnSaving; helper.Events.GameLoop.Saved += OnSaved;
    }

    internal static FlowGamePerformanceAcceptance? TryCreate(IModHelper helper, IMonitor monitor, Func<FlowGameSession?> current)
        => Environment.GetEnvironmentVariable("HATIFECT_TEST_MODE") == "1"
            && Environment.GetEnvironmentVariable("HATIFECT_TEST_AUTOMATED") == "1"
            && Environment.GetEnvironmentVariable("HATIFECT_TEST_SCENARIO") == Scenario ? new(helper, monitor, current) : null;

    internal void OnSaveLoaded()
    {
        try
        {
            Require(_stage == Stage.Loading && ++_loads == 1, "Unexpected performance save load.");
            ValidateLoadedSave(); _session = _current() ?? throw new InvalidOperationException("Production session is absent.");
            FlowSnapshot snapshot = _session.ReadSnapshot(); _sessionId = snapshot.SessionId;
            Require(snapshot.Stations.Count == 0 && snapshot.Parcels.Count == 0 && !_session.IsFaulted, "Performance fixture requires a clean transport.");
            for (int route = 0; route < Routes; route++)
            {
                _sources[route] = CreateChest("source_" + route); _destinations[route] = CreateChest("destination_" + route);
                int count = route == 2 ? 26 : 27;
                Require(_sources[route].GetActualCapacity() is >= 27 and <= 128
                    && _destinations[route].GetActualCapacity() is >= 27 and <= 128, "Unsupported performance chest capacity.");
                for (int index = 0; index < count; index++)
                {
                    var wine = (StardewValley.Object)ItemRegistry.Create("(O)348", 1, 4);
                    wine.preserve.Value = StardewValley.Object.PreserveType.Wine; wine.preservedParentSheetIndex.Value = "613";
                    wine.modData["Hatifect.Flow/Performance"] = _request.RunId + ":" + route + ":" + index;
                    _sources[route].GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Add(wine);
                }
                _session.Link("source_" + route, "destination_" + route, transitTicks: 180);
            }
            _passed.Add("loaded"); _stage = Stage.ReadySave;
        }
        catch (Exception error) { Fail(error); }
    }

    // ModEntry brackets ONLY its existing session.Tick. Observation, snapshots and fixture work follow it.
    internal void ObserveTick(FlowGameSession session, bool timePasses, long elapsed, long allocated)
    {
        if (_stage is not (Stage.Paused or Stage.Due or Stage.Idle)) return;
        try
        {
            Require(ReferenceEquals(session, _session) && ReferenceEquals(_current(), session) && !session.IsFaulted
                && Environment.CurrentManagedThreadId == _thread, "Performance lost its active owning session/thread.");
            FlowTickCounters counters = session.TickCounters; FlowSnapshot snapshot = session.ReadSnapshot();
            Require(snapshot.SessionId == _sessionId && snapshot.State == FlowApplicationState.Active, "Performance session changed or paused its authority.");
            if (_stage == Stage.Due)
            {
                Require(timePasses && counters.TickInvocations == _dueLastTick + 1 && ++_dueFrames <= 600
                    && counters.LastTickProcessedOperations is >= 0 and <= OperationLimit, "Due work lost its clock, tick or operation bound.");
                _dueLastTick = counters.TickInvocations; _dueEnd = counters;
                if (counters.LastTickProcessedOperations > 0) _workTicks++;
                _maximumOperations = Math.Max(_maximumOperations, counters.LastTickProcessedOperations);
            }
            else _window!.Observe(_sessionId, _thread, timePasses, elapsed, allocated, counters, snapshot);
        }
        catch (Exception error) { Fail(error); }
    }

    internal void Tick()
    {
        if (_disposed) return;
        try
        {
            if (_stage == Stage.Exit) { Game1.game1.Exit(); return; }
            Require(++_frames <= 36000, "Performance scenario exceeded its frame bound.");
            switch (_stage)
            {
                case Stage.Startup when _frames >= 30:
                    ValidateSaveTree(_request.SavePath); _stage = Stage.Loading;
                    SaveGame.Load(Path.GetFileName(_request.SavePath)); Game1.exitActiveMenu();
                    break;
                case Stage.ReadySave:
                    ValidateLoadedSave(); Require(Game1.saveOnNewDay, "Performance needs the real game save boundary.");
                    _savedTree = FlowAcceptanceSaveTree.Fingerprint(_request.SavePath);
                    _stage = Stage.Saving; Game1.activeClickableMenu = new SaveGameMenu();
                    break;
                case Stage.Paused when _window!.Complete:
                    _paused = _window.Finish(); VerifyItems(delivered: false); RequireTree();
                    Require(_paused.Start.PendingOperations == Shipments, "The paused queue did not retain all shipments.");
                    _passed.Add("paused"); _dueStart = _session!.TickCounters; _dueLastTick = _dueStart.TickInvocations;
                    RestorePause(); _stage = Stage.Due;
                    break;
                case Stage.Due when _session!.TickCounters.PendingOperations == 0:
                    VerifyItems(delivered: true); RequireTree();
                    // Each one-hop parcel executes Departure, Arrival and Delivery; two physical effects.
                    Require(_dueFrames > 1 && _dueEnd.TickInvocations - _dueStart.TickInvocations == _dueFrames
                        && _dueEnd.Now - _dueStart.Now == _dueFrames && _maximumOperations == OperationLimit && _workTicks > 1
                        && _dueEnd.ProcessedOperations - _dueStart.ProcessedOperations == Shipments * 3
                        && _dueEnd.PhysicalApplyCalls - _dueStart.PhysicalApplyCalls == Shipments * 2
                        && _dueEnd.RefreshRequests - _dueStart.RefreshRequests == _workTicks
                        && _dueEnd.RouteSearches == _dueStart.RouteSearches && _dueEnd.CheckpointCaptures == _dueStart.CheckpointCaptures,
                        "The actual due queue did not saturate, drain and deliver within its bounded work contract.");
                    _passed.Add("due-work"); _window = NewWindow(timePasses: true); _stage = Stage.Idle;
                    break;
                case Stage.Idle when _window!.Complete:
                    _idle = _window.Finish(); VerifyItems(delivered: true); RequireTree();
                    Require(_loads == 1 && _saving == 1 && _saved == 1 && _session!.TickCounters.CheckpointCaptures == 1,
                        "Measurement caused another lifecycle or checkpoint capture.");
                    _passed.Add("idle"); _passed.Add("item-fidelity"); _passed.Add("unchanged-files");
                    WriteReport(); _stage = Stage.Exit;
                    break;
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private FlowPerformanceWindow NewWindow(bool timePasses) => new(_sessionId, _thread, timePasses,
        Stopwatch.Frequency, _session!.TickCounters.TickInvocations);
    private void OnSaving(object? sender, SavingEventArgs e)
    {
        try
        {
            Require(_stage == Stage.Saving && ++_saving == 1 && _session!.IsSaving
                && _session.TickCounters.CheckpointCaptures == 1, "Missing production save/checkpoint barrier.");
            ValidateLoadedSave(); _passed.Add("saving");
        }
        catch (Exception error) { Fail(error); }
    }
    private void OnSaved(object? sender, SavedEventArgs e)
    {
        try
        {
            Require(_stage == Stage.Saving && _saving == 1 && ++_saved == 1 && !_session!.IsSaving,
                "Unexpected Saved or retained barrier.");
            ValidateLoadedSave(); string saved = FlowAcceptanceSaveTree.Fingerprint(_request.SavePath);
            Require(saved != _savedTree, "The confirmed save did not update the owned files."); _savedTree = saved;
            _priorPause = Game1.paused; Require(!_priorPause, "The fixture was already paused before admission.");
            _ownsPause = true; Game1.paused = true;
            for (int route = 0; route < Routes; route++)
            {
                Item[] items = Items(_sources[route]);
                for (int index = 0; index < items.Length; index++) _session!.Send("source_" + route, "destination_" + route, index);
                _xml[route] = items.Select(FlowItemCodec.Encode).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            }
            Require(_session!.TickCounters.PendingOperations == Shipments && _session.ReadSnapshot().Parcels.Count == Shipments,
                "The fixture did not admit the entire queue before its first tick.");
            VerifyItems(delivered: false); _passed.Add("saved"); _window = NewWindow(timePasses: false); _stage = Stage.Paused;
        }
        catch (Exception error) { Fail(error); }
    }
    private void VerifyItems(bool delivered)
    {
        FlowSnapshot snapshot = _session!.ReadSnapshot();
        Require(snapshot.Stations.Count == Routes * 2 && snapshot.Links.Count == Routes && snapshot.Parcels.Count == Shipments
            && snapshot.Parcels.All(parcel => parcel.Quantity == 1 && parcel.State == (delivered ? ParcelState.Delivered : ParcelState.Reserved)
                && parcel.DeliveryAttempts == (delivered ? 1 : 0)), "The retained performance transport lost cargo or duplicated delivery.");
        for (int route = 0; route < Routes; route++)
        {
            string[] source = Items(_sources[route]).Select(FlowItemCodec.Encode).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            string[] destination = Items(_destinations[route]).Select(FlowItemCodec.Encode).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            Require(source.SequenceEqual(delivered ? Array.Empty<string>() : _xml[route])
                && destination.SequenceEqual(delivered ? _xml[route] : Array.Empty<string>()), "Performance cargo changed its exact item XML or quantity.");
        }
    }
    private Chest CreateChest(string name)
    {
        var farm = Game1.getFarm();
        for (int y = 5; y < 20; y++)
        for (int x = 5; x < 20; x++)
        {
            var tile = new Vector2(x, y); if (farm.Objects.ContainsKey(tile)) continue;
            var chest = new Chest(playerChest: true, tileLocation: tile, itemId: "130");
            chest.modData["Hatifect.Flow/PerformanceChest"] = _request.RunId + ":" + name;
            farm.Objects.Add(tile, chest); _session!.RegisterStation(name, "Farm", x, y, chest); return chest;
        }
        throw new InvalidOperationException("No empty performance chest tile is available.");
    }
    private static Item[] Items(Chest chest) => chest.GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Where(item => item is not null).ToArray();
    private void ValidateLoadedSave()
    {
        string name = Path.GetFileName(_request.SavePath);
        Require(Context.IsWorldReady && Context.IsMainPlayer && !Context.IsMultiplayer && Game1.uniqueIDForThisGame == 4242424242UL
            && Game1.GetSaveGameName(true) == name.Split('_')[0] && Constants.SaveFolderName == name
            && Full(Constants.CurrentSavePath!) == _request.SavePath, "Performance would save outside its canonical owned copy.");
        ValidateSaveTree(_request.SavePath); ValidateSaveOwner(_request.SavePath, _request.RunId, _request.RuntimeId);
    }
    private void RequireTree() => Require(_savedTree == FlowAcceptanceSaveTree.Fingerprint(_request.SavePath), "Measured work changed the saved file tree.");
    private void RestorePause() { if (_ownsPause) { Game1.paused = _priorPause; _ownsPause = false; } }
    internal void OnReturnedToTitle() => Fail(new InvalidOperationException("Performance unexpectedly returned to title."));
    internal void Fail(Exception error)
    {
        RestorePause(); _errors.Add(error.ToString()); _monitor.Log("Flowline performance failed: " + error, LogLevel.Error);
        try { WriteReport(); } finally { _stage = Stage.Exit; }
    }
    private void WriteReport()
    {
        string captured = DateTimeOffset.UtcNow.ToString("O");
        var performance = new
        {
            FormatVersion = 1, RunId = _request.RunId, ScenarioId = Scenario, SessionId = _sessionId, WorldId = 4242424242UL,
            RuntimeFingerprint = _fingerprint, StopwatchFrequency = Stopwatch.Frequency, ThreadId = _thread,
            Runtime = RuntimeInformation.FrameworkDescription, OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), ServerGc = System.Runtime.GCSettings.IsServerGC,
            GcOverride = false, Shipments, Routes, Stations = Routes * 2, MaxOperationsPerTick = OperationLimit,
            Loads = _loads, SavingEvents = _saving, SavedEvents = _saved, SaveTreeHash = _savedTree,
            Paused = _paused, Idle = _idle,
            Due = new { SessionId = _sessionId, ThreadId = _thread, Frames = _dueFrames, WorkTicks = _workTicks, MaximumOperationsPerTick = _maximumOperations,
                Start = _dueStart, End = _dueEnd, Delivered = _passed.Contains("due-work") ? Shipments : 0 }
        };
        AtomicJson(Path.Combine(_helper.DirectoryPath, ".acceptance", "host-acceptance-report.json"), new
        {
            FormatVersion = 3, PerformanceFormatVersion = 3, CapturedAtUtc = captured,
            RuntimeFingerprintAlgorithm = "sha256-flow-runtime-v1", RuntimeFingerprint = _fingerprint,
            GameVersion = Game1.GetVersionString(), SmapiVersion = Constants.ApiVersion.ToString(), Scenarios = Array.Empty<object>(),
            HostChecks = Checks.Select(id => new { Id = Scenario + "." + id, Passed = _errors.Count == 0 && _passed.Contains(id), CapturedAtUtc = captured,
                Note = _errors.Count == 0 ? "One actual production tick; fixed paused/backlog/idle workload and full physical item XML." : string.Join("\n", _errors) }).ToArray(),
            FlowPerformance = performance
        });
        AtomicJson(Path.Combine(_request.Artifact, "diagnostics", "flow-performance.json"), new { performance, frames = _frames, errors = _errors.ToArray() });
    }
    public void Dispose()
    {
        if (_disposed) return;
        RestorePause(); _disposed = true; _helper.Events.GameLoop.Saving -= OnSaving; _helper.Events.GameLoop.Saved -= OnSaved;
    }
}
