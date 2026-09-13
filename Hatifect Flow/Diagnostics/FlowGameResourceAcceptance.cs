using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

internal sealed partial class FlowGameResourceAcceptance : IDisposable
{
    internal const string Scenario = "flow.chest.resources";
    private static readonly string[] Checks = { "loaded", "routing", "queues", "saving", "saved", "receipt-growth", "limits", "reload", "item-fidelity", "idle", "unchanged-files" };
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly Func<FlowGameSession?> _current;
    private readonly AcceptanceRequest _request;
    private readonly string _fingerprint;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly List<string> _errors = new();
    private readonly HashSet<string> _passed = new(StringComparer.Ordinal);
    private readonly List<FlowResourceSaveSample> _saveSamples = new();
    private readonly List<FlowResourceLoadSample> _loadSamples = new();
    private readonly List<FlowResourceRouteSample> _routes = new();
    private readonly List<FlowResourceWorkSample> _waves = new();
    private readonly Chest[] _chests = new Chest[32];
    private readonly Vector2[] _tiles = new Vector2[32];
    private readonly Guid[] _stations = new Guid[32], _parcels = new Guid[256];
    private readonly string[][] _cargoXml = new string[8][];
    private readonly Dictionary<Guid, string> _payloadXml = new();
    private string[] _fillers = Array.Empty<string>();
    private string _overflowXml = "", _savedTree = "", _beforeSaveTree = "";
    private FlowGameSession? _session, _retired;
    private FlowGameSave? _captured;
    private FlowResourceWorkWindow? _wave;
    private FlowPerformanceWindow? _idle;
    private FlowPerformanceWindowReport? _idleReport;
    private FlowGameResources? _idleResources, _finalResources;
    private int _frames, _loads, _saving, _saved, _titles, _admitted, _attempt;
    private int _sendRefusals, _retryRefusals, _returnRefusals;
    private bool _ownsPause, _priorPause, _returning, _disposed;
    private Stage _stage;
    private enum Stage { Startup, Loading, ReadySave, Saving, Return, Reload, Work, Idle, Exit }

    private FlowGameResourceAcceptance(IModHelper helper, IMonitor monitor, Func<FlowGameSession?> current)
    {
        _helper = helper; _monitor = monitor; _current = current;
        _request = ReadAcceptanceRequest(helper, Scenario); _fingerprint = RuntimeFingerprint();
        Require(Environment.GetEnvironmentVariable("DOTNET_gcConcurrent") is null
            && Environment.GetEnvironmentVariable("COMPlus_gcConcurrent") is null, "Resource measurements require ordinary game GC.");
        helper.Events.GameLoop.Saving += OnSaving; helper.Events.GameLoop.Saved += OnSaved;
    }

    internal static FlowGameResourceAcceptance? TryCreate(IModHelper helper, IMonitor monitor, Func<FlowGameSession?> current)
        => Environment.GetEnvironmentVariable("HATIFECT_TEST_MODE") == "1" && Environment.GetEnvironmentVariable("HATIFECT_TEST_AUTOMATED") == "1"
            && Environment.GetEnvironmentVariable("HATIFECT_TEST_SCENARIO") == Scenario ? new(helper, monitor, current) : null;

    // Called after the session owner has committed the one newly constructed session.
    internal void ObserveLoad(FlowGameSession session, FlowGameSave? aggregate, FlowResourceCost read, FlowResourceCost restore)
    {
        Require(_stage == Stage.Loading && _loadSamples.Count == _loads && _loads < 7 && ReferenceEquals(_current(), session), "Unexpected measured resource load.");
        Require(session.TickCounters.PhysicalApplyCalls == 0 && session.TickCounters.CheckpointCaptures == 0, "Loading performed a physical effect or captured another checkpoint.");
        if (_loads == 0) Require(aggregate is null, "Resource fixture already contains transport data.");
        else Require(aggregate is not null && _captured is not null && aggregate.Checkpoint.SequenceEqual(_captured.Checkpoint)
            && aggregate.Payloads.Length == _admitted && aggregate.Payloads.All(payload => _payloadXml.GetValueOrDefault(payload.Id) == payload.Xml),
            "Reload did not read the last confirmed exact transport and item payloads.");
        _session = session;
        _loadSamples.Add(new(_loads + 1, aggregate is not null, session.ReadResources(), aggregate?.Checkpoint.Length ?? 0,
            aggregate is null ? "" : Hash(aggregate.Checkpoint), read, restore));
    }

    internal void ObserveSave(FlowGameSession session, FlowGameSave aggregate, FlowResourceCost capture, FlowResourceCost write)
    {
        Require(_stage == Stage.Saving && _saveSamples.Count == _saved && _saved < 6 && ReferenceEquals(Session(), session)
            && session.IsSaving && session.TickCounters.CheckpointCaptures == 1, "Missing measured production save barrier.");
        Require(aggregate.Payloads.Length == _admitted && aggregate.Payloads.All(payload => _payloadXml.GetValueOrDefault(payload.Id) == payload.Xml),
            "Saving changed captured cargo XML.");
        _captured = aggregate;
        _saveSamples.Add(new(_saved + 1, session.ReadResources(), aggregate.Checkpoint.Length, Hash(aggregate.Checkpoint), capture, write));
    }

    internal void OnSaveLoaded()
    {
        try
        {
            Require(_stage == Stage.Loading && ++_loads == _loadSamples.Count, "Missing measured resource session construction.");
            ValidateLoadedSave(); AcquirePause();
            _monitor.Log($"Flowline resources: load {_loads}/7, retained {_admitted}, settled attempt {_attempt}.", LogLevel.Info);
            if (_loads == 1)
            {
                Require(Session().ReadSnapshot().Stations.Count == 0 && Session().ReadSnapshot().Parcels.Count == 0, "Resource fixture requires a clean transport.");
                CreateFixtureAndProfileRoutes(); _passed.Add("loaded");
            }
            else
            {
                RebindFixture(); VerifyState(); RequireTree();
                Require(Session().TickCounters.PhysicalApplyCalls == 0, "Restore replayed inventory effects.");
            }
            switch (_loads)
            {
                case 1: AdmitTo(32); _stage = Stage.ReadySave; break;
                case 2: AdmitTo(128); _stage = Stage.ReadySave; break;
                case 3: AdmitTo(256); _stage = Stage.ReadySave; break;
                case 4: _passed.Add("queues"); BeginWave(1); break;
                case 5: BeginWave(2); break;
                case 6: BeginWave(9); break;
                case 7:
                    VerifyRefusals(); _passed.Add("reload");
                    _idleResources = Session().ReadResources();
                    _monitor.Log(_idleResources.Format(), LogLevel.Info);
                    _idle = new FlowPerformanceWindow(_idleResources.SessionId, _thread, true, Stopwatch.Frequency, Session().TickCounters.TickInvocations);
                    RestorePause(); _stage = Stage.Idle;
                    break;
                default: throw new InvalidOperationException("Too many resource loads.");
            }
        }
        catch (Exception error) { Fail(error); }
    }

    internal void ObserveTick(FlowGameSession session, bool timePasses, long elapsed, long allocated)
    {
        if (_stage is not (Stage.Work or Stage.Idle)) return;
        try
        {
            Require(ReferenceEquals(Session(), session), "Resource measurement changed its owning session.");
            FlowSnapshot snapshot = session.ReadSnapshot();
            if (_stage == Stage.Work) _wave!.Observe(snapshot.SessionId, _thread, timePasses, elapsed, allocated, session.TickCounters, snapshot);
            else _idle!.Observe(snapshot.SessionId, _thread, timePasses, elapsed, allocated, session.TickCounters, snapshot);
        }
        catch (Exception error) { Fail(error); }
    }

    internal void Tick()
    {
        if (_disposed) return;
        try
        {
            if (_stage == Stage.Exit) { Game1.game1.Exit(); return; }
            Require(++_frames <= 36000, "Resource scenario exceeded its frame bound.");
            switch (_stage)
            {
                case Stage.Startup when _frames >= 30:
                    _savedTree = FlowAcceptanceSaveTree.Fingerprint(_request.SavePath);
                    goto case Stage.Reload;
                case Stage.Reload:
                    RequireTree(); ValidateSaveTree(_request.SavePath); _stage = Stage.Loading;
                    SaveGame.Load(Path.GetFileName(_request.SavePath)); Game1.exitActiveMenu();
                    break;
                case Stage.ReadySave:
                    ValidateLoadedSave(); VerifyState(); Require(Game1.saveOnNewDay && Game1.activeClickableMenu is null, "Resource save needs the ordinary game save boundary.");
                    _beforeSaveTree = FlowAcceptanceSaveTree.Fingerprint(_request.SavePath); _stage = Stage.Saving;
                    // The menu gates shouldTimePass before the next production update.
                    RestorePause(); Game1.activeClickableMenu = new SaveGameMenu();
                    break;
                case Stage.Return when !_returning:
                    _returning = true; _retired = Session();
                    // Keep the owned pause until title cleanup closes the session; no extra capture is needed.
                    RequestReturnToTitle();
                    break;
                case Stage.Work when Session().TickCounters.PendingOperations == 0:
                    _waves.Add(_wave!.Finish()); VerifyState(); RequireTree();
                    if (_attempt is 1 or 8 or 16)
                    {
                        AcquirePause(); _stage = Stage.ReadySave;
                        if (_attempt == 16) _passed.Add("receipt-growth");
                    }
                    else BeginWave(_attempt + 1);
                    break;
                case Stage.Idle when _idle!.Complete:
                    _idleReport = _idle.Finish(); VerifyState(); RequireTree(); _finalResources = Session().ReadResources();
                    Require(_idleReport.AllocatedBytesTotal == 0 && _idleReport.P95TickMs <= 0.25 && _idleReport.P99TickMs <= 1.0
                        && _finalResources.Runtime == _idleResources!.Runtime && _finalResources.Ports.SequenceEqual(_idleResources.Ports)
                        && _finalResources.PayloadCharacters == _idleResources.PayloadCharacters
                        && _finalResources.AdmissionRejections == _idleResources.AdmissionRejections, "Retained idle state grew or exceeded its steady budget.");
                    Require(_loads == 7 && _titles == 6 && _saving == 6 && _saved == 6 && _waves.Count == 16
                        && _waves.Sum(wave => wave.End.ProcessedOperations - wave.Start.ProcessedOperations) == 4608
                        && _waves.Sum(wave => wave.End.PhysicalApplyCalls - wave.Start.PhysicalApplyCalls) == 4352, "Resource workload or lifecycle is incomplete.");
                    _passed.Add("idle"); _passed.Add("item-fidelity"); _passed.Add("unchanged-files"); WriteReport(); _stage = Stage.Exit;
                    break;
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private void BeginWave(int attempt)
    {
        Require(attempt is >= 1 and <= 16 && _waves.Count == attempt - 1, "Unexpected resource retry wave.");
        _attempt = attempt;
        if (attempt > 1)
            foreach (Guid id in _parcels)
            {
                FlowSnapshot snapshot = Session().ReadSnapshot();
                Require(Session().Execute(new FlowParcelCommand(snapshot.SessionId, snapshot.Revision, id, FlowParcelAction.RetryDelivery)).Status == FlowCommandStatus.Applied,
                    "Explicit retry was not admitted with its current revision.");
            }
        FlowSnapshot queued = Session().ReadSnapshot();
        _wave = new FlowResourceWorkWindow(attempt, queued.SessionId, _thread, Stopwatch.Frequency, Session().TickCounters);
        RestorePause(); _stage = Stage.Work;
    }

    private void OnSaving(object? sender, SavingEventArgs e)
    {
        try
        {
            Require(_stage == Stage.Saving && ++_saving == _saved + 1 && _saveSamples.Count == _saving && Session().IsSaving, "Unexpected resource Saving event.");
            ValidateLoadedSave(); if (_saving == 6) _passed.Add("saving");
        }
        catch (Exception error) { Fail(error); }
    }

    private void OnSaved(object? sender, SavedEventArgs e)
    {
        try
        {
            Require(_stage == Stage.Saving && ++_saved == _saving && !Session().IsSaving, "Unexpected resource Saved event.");
            ValidateLoadedSave(); AcquirePause();
            _savedTree = FlowAcceptanceSaveTree.Fingerprint(_request.SavePath);
            Require(_savedTree != _beforeSaveTree, "Confirmed resource save did not change its owned files.");
            VerifyState(); if (_saved == 6) _passed.Add("saved"); _stage = Stage.Return;
            _monitor.Log($"Flowline resources: confirmed save {_saved}/6, checkpoint {_captured!.Checkpoint.Length} bytes.", LogLevel.Info);
        }
        catch (Exception error) { Fail(error); }
    }

    internal void OnReturnedToTitle()
    {
        try
        {
            Require(_stage == Stage.Return && _returning && _current() is null && _retired?.ReadSnapshot().State == FlowApplicationState.Closed
                && ++_titles == _saved && _titles <= 6, "Title did not close the resource session.");
            FlowSnapshot old = _retired!.ReadSnapshot();
            Require(_retired.Execute(new FlowParcelCommand(old.SessionId, old.Revision, _parcels[0], FlowParcelAction.Cancel)).Status == FlowCommandStatus.SessionClosed,
                "A retired resource session retained command authority.");
            RequireTree(); RestorePause(); _session = null; _returning = false; _stage = Stage.Reload;
        }
        catch (Exception error) { Fail(error); }
    }

    private FlowGameSession Session()
    {
        Require(Environment.CurrentManagedThreadId == _thread && _session is not null && ReferenceEquals(_current(), _session) && !_session.IsFaulted,
            "Resource measurement lost its live owning session/thread.");
        return _session!;
    }
    private void AcquirePause()
    {
        if (_ownsPause) { Require(Game1.paused, "Resource pause was changed by another owner."); return; }
        _priorPause = Game1.paused; Require(!_priorPause, "Resource fixture was already paused.");
        _ownsPause = true; Game1.paused = true;
    }
    private void RestorePause() { if (_ownsPause) { Game1.paused = _priorPause; _ownsPause = false; } }
    private void RequireTree() => Require(_savedTree == FlowAcceptanceSaveTree.Fingerprint(_request.SavePath), "Resource work changed the confirmed saved tree.");
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private void ValidateLoadedSave()
    {
        string name = Path.GetFileName(_request.SavePath);
        Require(Context.IsWorldReady && Context.IsMainPlayer && !Context.IsMultiplayer && Game1.uniqueIDForThisGame == 4242424242UL
            && Game1.GetSaveGameName(true) == name.Split('_')[0] && Constants.SaveFolderName == name
            && Full(Constants.CurrentSavePath!) == _request.SavePath, "Resource scenario escaped its owned save.");
        ValidateSaveTree(_request.SavePath); ValidateSaveOwner(_request.SavePath, _request.RunId, _request.RuntimeId);
    }
    internal void Fail(Exception error)
    {
        RestorePause(); if (_errors.Count < 8) _errors.Add(error.ToString());
        _monitor.Log("Flowline resources failed: " + error, LogLevel.Error);
        try { WriteReport(); } finally { _stage = Stage.Exit; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        RestorePause(); _disposed = true; _helper.Events.GameLoop.Saving -= OnSaving; _helper.Events.GameLoop.Saved -= OnSaved;
    }
    private void WriteReport()
    {
        string captured = DateTimeOffset.UtcNow.ToString("O");
        var resources = new
        {
            FormatVersion = 1, RunId = _request.RunId, ScenarioId = Scenario, WorldId = 4242424242UL,
            RuntimeFingerprint = _fingerprint, StopwatchFrequency = Stopwatch.Frequency, ThreadId = _thread,
            Runtime = RuntimeInformation.FrameworkDescription, OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), ServerGc = System.Runtime.GCSettings.IsServerGC, GcOverride = false,
            Shipments = 256, Stations = 32, SourceChests = 8, RoutingQueries = 75,
            StationIds = _stations.ToArray(),
            Loads = _loads, Titles = _titles, SavingEvents = _saving, SavedEvents = _saved, SaveTreeHash = _savedTree,
            SendRefusals = _sendRefusals, RetryRefusals = _retryRefusals, ReturnRefusals = _returnRefusals,
            Routes = _routes.ToArray(), Saves = _saveSamples.ToArray(), Restores = _loadSamples.ToArray(), Waves = _waves.ToArray(),
            Idle = _idleReport, BeforeIdle = _idleResources, Final = _finalResources
        };
        AtomicJson(Path.Combine(_helper.DirectoryPath, ".acceptance", "host-acceptance-report.json"), new
        {
            FormatVersion = 3, PerformanceFormatVersion = 3, CapturedAtUtc = captured,
            RuntimeFingerprintAlgorithm = "sha256-flow-runtime-v1", RuntimeFingerprint = _fingerprint,
            GameVersion = Game1.GetVersionString(), SmapiVersion = Constants.ApiVersion.ToString(), Scenarios = Array.Empty<object>(),
            HostChecks = Checks.Select(id => new { Id = Scenario + "." + id, Passed = _errors.Count == 0 && _passed.Contains(id), CapturedAtUtc = captured,
                Note = _errors.Count == 0 ? "Fixed production resource scale, confirmed saves, exact item custody and bounded work." : string.Join("\n", _errors) }).ToArray(),
            FlowResources = resources
        });
        AtomicJson(Path.Combine(_request.Artifact, "diagnostics", "flow-resources.json"), new { resources, frames = _frames, errors = _errors.ToArray() });
    }
}
