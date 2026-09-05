using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Hatifect.Flow.Application;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Sessions;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;
using static Hatifect.Flow.Diagnostics.FlowHostAcceptance;

namespace Hatifect.Flow.Diagnostics;

// Real production-session acceptance. All world mutations stay in one request-owned canonical save copy.
internal sealed class FlowChestRoundtripAcceptance : IDisposable
{
    internal const string Scenario = "flow.chest.roundtrip";
    private static readonly string[] Checks = { "loaded", "custody", "saving", "saved", "delivery", "lifecycle", "reload", "no-duplication" };
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly Func<FlowGameSession?> _current;
    private readonly AcceptanceRequest _request;
    private readonly HashSet<string> _passed = new(StringComparer.Ordinal);
    private readonly List<string> _errors = new();
    private readonly string _fingerprint;
    private Stage _stage;
    private Vector2 _sourceTile;
    private Vector2 _destinationTile;
    private Guid _parcel;
    private Guid _sessionId;
    private FlowGameSession? _retiredSession;
    private string _saveHash = "";
    private int _frames;
    private int _loads;
    private int _savings;
    private int _saves;
    private int _verificationFrames;
    private bool _disposed;
    private bool _returning;
    private enum Stage { Startup, Loading, Transit, SavingTransit, ReturnTransit, ReloadTransit, Delivery, SavingDelivered, ReturnDelivered, ReloadDelivered, Verify, Exit }

    private FlowChestRoundtripAcceptance(IModHelper helper, IMonitor monitor, Func<FlowGameSession?> current)
    {
        _helper = helper;
        _monitor = monitor;
        _current = current;
        _request = ReadAcceptanceRequest(helper, Scenario);
        _fingerprint = RuntimeFingerprint();
        helper.Events.GameLoop.Saving += OnSaving;
        helper.Events.GameLoop.Saved += OnSaved;
    }

    internal static FlowChestRoundtripAcceptance? TryCreate(IModHelper helper, IMonitor monitor, Func<FlowGameSession?> current)
        => Environment.GetEnvironmentVariable("HATIFECT_TEST_MODE") == "1" && Environment.GetEnvironmentVariable("HATIFECT_TEST_AUTOMATED") == "1"
            && Environment.GetEnvironmentVariable("HATIFECT_TEST_SCENARIO") == Scenario ? new(helper, monitor, current) : null;

    internal void OnSaveLoaded()
    {
        try
        {
            Require(_stage == Stage.Loading, "Unexpected SaveLoaded during chest acceptance.");
            ValidateLoadedSave();
            FlowGameSession session = Session();
            Require(!session.IsFaulted && session.ReadSnapshot().SessionId != _sessionId, "SaveLoaded did not create a fresh production session.");
            _sessionId = session.ReadSnapshot().SessionId;
            _loads++;
            if (_loads == 1)
            {
                Require(session.ReadSnapshot().Stations.Count == 0, "Acceptance copy already contains Flow stations.");
                Chest source = CreateChest(out _sourceTile);
                Chest destination = CreateChest(out _destinationTile);
                var wine = (StardewValley.Object)ItemRegistry.Create("(O)348", 8, 4);
                wine.preserve.Value = StardewValley.Object.PreserveType.Wine;
                wine.preservedParentSheetIndex.Value = "613";
                wine.modData["Hatifect.Flow/Acceptance"] = _request.RunId;
                source.GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Add(wine);
                session.RegisterStation("accept_source", "Farm", (int)_sourceTile.X, (int)_sourceTile.Y, source);
                session.RegisterStation("accept_destination", "Farm", (int)_destinationTile.X, (int)_destinationTile.Y, destination);
                session.Link("accept_source", "accept_destination", transitTicks: 180);
                _parcel = session.Send("accept_source", "accept_destination", 0);
                _passed.Add("loaded");
                _stage = Stage.Transit;
            }
            else if (_loads == 2)
            {
                Require(Parcel().State == ParcelState.InTransit, "In-flight parcel did not survive the actual game save.");
                Require(Items(_sourceTile).Length == 0 && Items(_destinationTile).Length == 0, "In-flight cargo appeared in a physical chest.");
                _passed.Add("reload");
                _stage = Stage.Delivery;
            }
            else
            {
                Require(_loads == 3 && Parcel().State == ParcelState.Delivered, "Delivered state did not survive the second actual save.");
                VerifyDelivery();
                _stage = Stage.Verify;
            }
        }
        catch (Exception error) { Fail(error); }
    }

    internal void Tick()
    {
        if (_disposed) return;
        try
        {
            if (_stage == Stage.Exit) { Game1.game1.Exit(); return; }
            Require(++_frames <= 36000, "Production chest roundtrip exceeded its frame bound.");
            switch (_stage)
            {
                case Stage.Startup when _frames >= 30:
                case Stage.ReloadTransit:
                case Stage.ReloadDelivered:
                    ValidateSaveTree(_request.SavePath);
                    _stage = Stage.Loading;
                    SaveGame.Load(Path.GetFileName(_request.SavePath));
                    Game1.exitActiveMenu();
                    break;
                case Stage.Transit when Parcel().State == ParcelState.InTransit:
                    Require(Items(_sourceTile).Length == 0 && Items(_destinationTile).Length == 0, "Extraction did not move exclusive custody into the parcel.");
                    _passed.Add("custody");
                    BeginGameSave(Stage.SavingTransit);
                    break;
                case Stage.ReturnTransit:
                case Stage.ReturnDelivered:
                    if (_returning) break;
                    _returning = true;
                    _retiredSession = Session();
                    RequestReturnToTitle();
                    break;
                case Stage.Delivery when Parcel().State == ParcelState.Delivered:
                    VerifyDelivery();
                    _passed.Add("delivery");
                    BeginGameSave(Stage.SavingDelivered);
                    break;
                case Stage.Verify:
                    VerifyDelivery();
                    if (++_verificationFrames < 120) break;
                    FlowSnapshot snapshot = Session().ReadSnapshot();
                    Require(Session().Execute(new FlowParcelCommand(snapshot.SessionId, snapshot.Revision, _parcel, FlowParcelAction.RetryDelivery)).Status == FlowCommandStatus.Rejected,
                        "A delivered parcel accepted another delivery.");
                    VerifyDelivery();
                    Require(_savings == 2 && _saves == 2 && _loads == 3, "The required real lifecycle events did not occur exactly twice.");
                    _passed.Add("no-duplication");
                    WriteReport();
                    _stage = Stage.Exit;
                    break;
            }
        }
        catch (Exception error) { Fail(error); }
    }

    internal void OnReturnedToTitle()
    {
        try
        {
            Require(_stage is Stage.ReturnTransit or Stage.ReturnDelivered, "Unexpected title transition.");
            Require(_current() is null && _retiredSession?.ReadSnapshot().State == FlowApplicationState.Closed,
                "Returning to title retained a production session.");
            _passed.Add("lifecycle");
            _returning = false;
            _stage = _stage == Stage.ReturnTransit ? Stage.ReloadTransit : Stage.ReloadDelivered;
        }
        catch (Exception error) { Fail(error); }
    }

    private void BeginGameSave(Stage saving)
    {
        ValidateLoadedSave();
        Require(Game1.saveOnNewDay, "The game would skip writing the save.");
        _saveHash = HashFile(Path.Combine(_request.SavePath, Path.GetFileName(_request.SavePath)));
        _stage = saving;
        // Let the game's normal draw/update loop perform SaveGame.Save; SMAPI emits real Saving/Saved.
        Game1.activeClickableMenu = new SaveGameMenu();
    }

    private void OnSaving(object? sender, SavingEventArgs e)
    {
        try
        {
            Require(_stage is Stage.SavingTransit or Stage.SavingDelivered, "Unexpected Saving event.");
            ValidateLoadedSave();
            Require(Session().IsSaving, "Production session did not enter its save barrier.");
            _savings++;
            _passed.Add("saving");
        }
        catch (Exception error) { Fail(error); }
    }

    private void OnSaved(object? sender, SavedEventArgs e)
    {
        try
        {
            Require(_stage is Stage.SavingTransit or Stage.SavingDelivered, "Unexpected Saved event.");
            ValidateLoadedSave();
            Require(!Session().IsSaving, "Production session did not release its save barrier.");
            Require(_saveHash != HashFile(Path.Combine(_request.SavePath, Path.GetFileName(_request.SavePath))), "Saved event did not update the owned on-disk game save.");
            _saves++;
            _passed.Add("saved");
            _stage = _stage == Stage.SavingTransit ? Stage.ReturnTransit : Stage.ReturnDelivered;
        }
        catch (Exception error) { Fail(error); }
    }

    private void ValidateLoadedSave()
    {
        Require(Context.IsWorldReady && Context.IsMainPlayer && !Context.IsMultiplayer, "Acceptance needs the loaded single-player world.");
        string expected = Path.GetFileName(_request.SavePath);
        Require(Game1.uniqueIDForThisGame == 4242424242UL && Game1.GetSaveGameName(true) == expected.Split('_')[0]
            && Constants.SaveFolderName == expected && Full(Constants.CurrentSavePath!) == _request.SavePath,
            "The game would write outside this request's canonical save directory.");
        ValidateSaveTree(_request.SavePath);
        ValidateSaveOwner(_request.SavePath, _request.RunId, _request.RuntimeId);
    }

    private static Chest CreateChest(out Vector2 tile)
    {
        var farm = Game1.getFarm();
        for (int y = 5; y < 20; y++)
        for (int x = 5; x < 20; x++)
        {
            tile = new Vector2(x, y);
            if (farm.Objects.ContainsKey(tile)) continue;
            var chest = new Chest(playerChest: true, tileLocation: tile, itemId: "130");
            farm.Objects.Add(tile, chest);
            return chest;
        }
        throw new InvalidOperationException("No empty acceptance chest tile is available.");
    }

    private static Item[] Items(Vector2 tile) => ((Chest)Game1.getFarm().Objects[tile]).GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Where(item => item is not null).ToArray();
    private FlowGameSession Session() => _current() ?? throw new InvalidOperationException("Production game session is absent.");
    private FlowParcelSnapshot Parcel() => Session().ReadSnapshot().Parcels.Single(parcel => parcel.Id == _parcel);
    private void VerifyDelivery()
    {
        Item[] delivered = Items(_destinationTile);
        Require(Items(_sourceTile).Length == 0 && delivered.Length == 1 && delivered[0] is StardewValley.Object, "Delivery is missing or duplicated.");
        var wine = (StardewValley.Object)delivered[0];
        Require(wine.QualifiedItemId == "(O)348" && wine.Stack == 8 && wine.Quality == 4
            && wine.preserve.Value == StardewValley.Object.PreserveType.Wine && wine.preservedParentSheetIndex.Value == "613"
            && wine.modData.TryGetValue("Hatifect.Flow/Acceptance", out string marker) && marker == _request.RunId,
            "Whole-stack saved item fidelity changed.");
    }

    internal void Fail(Exception error)
    {
        _errors.Add(error.ToString());
        _monitor.Log("Flowline chest roundtrip failed: " + error, LogLevel.Error);
        try { WriteReport(); }
        finally { _stage = Stage.Exit; }
    }

    private void WriteReport()
    {
        string captured = DateTimeOffset.UtcNow.ToString("O");
        AtomicJson(Path.Combine(_helper.DirectoryPath, ".acceptance", "host-acceptance-report.json"), new
        {
            FormatVersion = 3, PerformanceFormatVersion = 3, CapturedAtUtc = captured,
            RuntimeFingerprintAlgorithm = "sha256-flow-runtime-v1", RuntimeFingerprint = _fingerprint,
            GameVersion = Game1.GetVersionString(), SmapiVersion = Constants.ApiVersion.ToString(), Scenarios = Array.Empty<object>(),
            HostChecks = Checks.Select(id => new { Id = Scenario + "." + id, Passed = _errors.Count == 0 && _passed.Contains(id),
                CapturedAtUtc = captured, Note = _errors.Count == 0 ? "Production session, real chests, two actual game saves and reloads." : string.Join("\n", _errors) }).ToArray()
        });
        AtomicJson(Path.Combine(_request.Artifact, "diagnostics", "flow-chest-roundtrip.json"), new
        { requestId = _request.RunId, scenarioId = Scenario, frames = _frames, loads = _loads, savingEvents = _savings, savedEvents = _saves, parcelId = _parcel, errors = _errors.ToArray() });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _helper.Events.GameLoop.Saving -= OnSaving;
        _helper.Events.GameLoop.Saved -= OnSaved;
    }
}
