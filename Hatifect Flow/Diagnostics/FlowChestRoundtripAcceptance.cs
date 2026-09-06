using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

// Real production-session acceptance. All world mutations stay in one request-owned canonical save copy.
internal sealed partial class FlowChestRoundtripAcceptance : IDisposable
{
    internal const string Scenario = "flow.chest.roundtrip";
    private static readonly string[] Checks = { "loaded", "custody", "saving", "saved", "delivery", "lifecycle", "reload", "no-duplication" };
    private readonly IModHelper _helper;
    private readonly string _scenario;
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
    private Guid _partialParcel;
    private string _remainderXml = "";
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
    private enum Stage { Startup, Loading, Transit, SavingTransit, ReturnTransit, ReloadTransit, Delivery, SavingDelivered, ReturnDelivered, ReloadDelivered, Verify, AwaitCrash, Exit }

    private FlowChestRoundtripAcceptance(IModHelper helper, IMonitor monitor, Func<FlowGameSession?> current, string scenario)
    {
        _helper = helper;
        _monitor = monitor;
        _current = current;
        _scenario = scenario;
        _request = ReadAcceptanceRequest(helper, scenario);
        _fingerprint = RuntimeFingerprint();
        InitializeCrash();
        helper.Events.GameLoop.Saving += OnSaving;
        helper.Events.GameLoop.Saved += OnSaved;
    }

    internal static FlowChestRoundtripAcceptance? TryCreate(IModHelper helper, IMonitor monitor, Func<FlowGameSession?> current)
    {
        string? scenario = Environment.GetEnvironmentVariable("HATIFECT_TEST_SCENARIO");
        return Environment.GetEnvironmentVariable("HATIFECT_TEST_MODE") == "1" && Environment.GetEnvironmentVariable("HATIFECT_TEST_AUTOMATED") == "1"
            && scenario is Scenario or CrashScenario ? new(helper, monitor, current, scenario) : null;
    }

    internal void OnSaveLoaded()
    {
        try
        {
            Require(_stage == Stage.Loading, "Unexpected SaveLoaded during chest acceptance.");
            ValidateLoadedSave();
            FlowGameSession session = Session();
            Require(!session.IsFaulted && session.ReadSnapshot().SessionId != _sessionId, "SaveLoaded did not create a fresh production session.");
            if (_crashPhase == "resume" && _loads == 1) _passed.Add("process-restart");
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
                var partial = (StardewValley.Object)FlowItemCodec.Decode(FlowItemCodec.Encode(wine));
                partial.Stack = 13;
                partial.modData["Hatifect.Flow/PartialAcceptance"] = "true";
                source.GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Add(partial);
                Item expectedRemainder = FlowItemCodec.Decode(FlowItemCodec.Encode(partial));
                expectedRemainder.Stack = 8;
                _remainderXml = FlowItemCodec.Encode(expectedRemainder);
                session.RegisterStation("accept_source", "Farm", (int)_sourceTile.X, (int)_sourceTile.Y, source);
                session.RegisterStation("accept_destination", "Farm", (int)_destinationTile.X, (int)_destinationTile.Y, destination);
                session.Link("accept_source", "accept_destination", transitTicks: 180);
                _parcel = session.Send("accept_source", "accept_destination", 0);
                FlowSnapshot snapshot = session.ReadSnapshot();
                FlowLinkSnapshot link = snapshot.Links.Single();
                FlowInventorySlot selected = session.ReadInventory(link.Origin).Single(slot => slot.Index == 1);
                Require(session.Execute(new FlowSendCommand(snapshot.SessionId, snapshot.Revision, link.Origin, link.Destination,
                    selected.Index, selected.Fingerprint) { Quantity = 5 }).Status == FlowCommandStatus.Applied,
                    "Typed partial-stack admission failed.");
                _partialParcel = session.ReadSnapshot().Parcels.Single(parcel => parcel.Id != _parcel).Id;
                Require(partial.Stack == 13, "Partial admission extracted units before the scheduled effect.");
                _passed.Add("loaded");
                _stage = Stage.Transit;
            }
            else if (_loads == 2)
            {
                Require(BothAt(ParcelState.InTransit), "In-flight parcels did not survive the actual game save.");
                VerifyRemainder();
                Require(Items(_destinationTile).Length == 0, "In-flight cargo appeared in the destination chest.");
                _passed.Add("reload");
                _stage = Stage.Delivery;
            }
            else
            {
                Require(_loads == 3 && BothAt(ParcelState.Delivered), "Delivered states did not survive the second actual save.");
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
                case Stage.Transit when BothAt(ParcelState.InTransit):
                    VerifyRemainder();
                    Require(Items(_destinationTile).Length == 0, "Extraction did not move exclusive custody into the parcels.");
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
                case Stage.Delivery when BothAt(ParcelState.Delivered):
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
                    Require(Session().Execute(new FlowParcelCommand(snapshot.SessionId, snapshot.Revision, _partialParcel, FlowParcelAction.RetryDelivery)).Status == FlowCommandStatus.Rejected,
                        "A delivered partial parcel accepted another delivery.");
                    VerifyDelivery();
                    Require(_savings == 2 && _saves == 2 && _loads == 3, "The required real lifecycle events did not occur exactly twice.");
                    _passed.Add("no-duplication");
                    WriteReport();
                    _stage = Stage.Exit;
                    break;
                case Stage.AwaitCrash:
                    Require(Session().IsSaving && BothAt(ParcelState.InTransit), "The saved crash boundary advanced before termination.");
                    VerifyRemainder();
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
            if (_crashPhase == "prepare" && _stage == Stage.SavingTransit)
            {
                PrepareCrashBoundary();
                return;
            }
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
    private bool BothAt(ParcelState state) => Parcel().State == state
        && Session().ReadSnapshot().Parcels.Single(parcel => parcel.Id == _partialParcel).State == state;
    private void VerifyRemainder()
    {
        Item[] remaining = Items(_sourceTile);
        Require(remaining.Length == 1 && FlowItemCodec.Encode(remaining[0]) == _remainderXml,
            "The partial source remainder changed quantity, metadata or cargo ownership.");
    }
    private void VerifyDelivery()
    {
        VerifyRemainder();
        Item[] delivered = Items(_destinationTile);
        Require(delivered.Length == 2, "Whole or partial delivery is missing or duplicated.");
        VerifyWine(delivered.Single(item => !item.modData.ContainsKey("Hatifect.Flow/PartialAcceptance")), 8);
        VerifyWine(delivered.Single(item => item.modData.ContainsKey("Hatifect.Flow/PartialAcceptance")), 5);
        Require(Items(_sourceTile).Sum(item => item.Stack) + delivered.Sum(item => item.Stack) == 21,
            "Whole plus partial transport did not conserve total quantity.");
    }
    private void VerifyWine(Item item, int quantity)
    {
        Require(item is StardewValley.Object, "Delivered cargo is not an ordinary object.");
        var wine = (StardewValley.Object)item;
        Require(wine.QualifiedItemId == "(O)348" && wine.Stack == quantity && wine.Quality == 4
            && wine.preserve.Value == StardewValley.Object.PreserveType.Wine && wine.preservedParentSheetIndex.Value == "613"
            && wine.modData.TryGetValue("Hatifect.Flow/Acceptance", out string marker) && marker == _request.RunId,
            "Saved item fidelity changed in whole or partial delivery.");
    }

    internal void Fail(Exception error)
    {
        _errors.Add(error.ToString());
        _monitor.Log("Flowline " + _scenario + " failed: " + error, LogLevel.Error);
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
            HostChecks = (_crashPhase is null ? Checks : Checks.Concat(new[] { "process-restart" })).Select(id => new { Id = _scenario + "." + id, Passed = _errors.Count == 0 && _passed.Contains(id),
                CapturedAtUtc = captured, Note = _errors.Count == 0 ? "Whole and partial stacks, real chests, two actual game saves and reloads; total quantity conserved." : string.Join("\n", _errors) }).ToArray()
        });
        AtomicJson(Path.Combine(_request.Artifact, "diagnostics", "flow-chest-roundtrip.json"), new
        { requestId = _request.RunId, scenarioId = _scenario, frames = _frames, loads = _loads, savingEvents = _savings, savedEvents = _saves,
            parcelId = _parcel, partialParcelId = _partialParcel, partialSourceQuantity = 13, partialCargoQuantity = 5, partialRemainderQuantity = 8,
            errors = _errors.ToArray() });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _helper.Events.GameLoop.Saving -= OnSaving;
        _helper.Events.GameLoop.Saved -= OnSaved;
    }
}
