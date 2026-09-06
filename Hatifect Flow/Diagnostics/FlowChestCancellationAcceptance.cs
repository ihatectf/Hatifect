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

// Exercises public commands and production ticks; only this request's real save/chests are mutated.
internal sealed class FlowChestCancellationAcceptance : IDisposable
{
    internal const string Scenario = "flow.chest.cancellation";
    private static readonly string[] Checks = { "loaded", "duplicate-admission", "cancellation", "source-conflict",
        "saving", "saved", "lifecycle", "reload", "re-admission", "no-late-effect" };
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly Func<FlowGameSession?> _current;
    private readonly AcceptanceRequest _request;
    private readonly string _fingerprint;
    private readonly HashSet<string> _passed = new(StringComparer.Ordinal);
    private readonly List<string> _errors = new();
    private readonly List<Guid> _cancelled = new();
    private Vector2 _sourceTile, _destinationTile, _holdingTile;
    private string[] _sourceXml = Array.Empty<string>();
    private string _holdingXml = "", _deliveredXml = "", _saveHash = "";
    private Guid _sessionId, _resent;
    private FlowGameSession? _retired;
    private Stage _stage;
    private int _frames, _loads, _savings, _saves, _verificationFrames;
    private bool _returning, _disposed;
    private enum Stage { Startup, Loading, Conflicts, SavingCancelled, ReturnCancelled, ReloadCancelled,
        VerifyCancelled, Delivery, SavingDelivered, ReturnDelivered, ReloadDelivered, VerifyDelivered, Exit }

    private FlowChestCancellationAcceptance(IModHelper helper, IMonitor monitor, Func<FlowGameSession?> current)
    {
        _helper = helper; _monitor = monitor; _current = current;
        _request = ReadAcceptanceRequest(helper, Scenario);
        _fingerprint = RuntimeFingerprint();
        helper.Events.GameLoop.Saving += OnSaving;
        helper.Events.GameLoop.Saved += OnSaved;
    }

    internal static FlowChestCancellationAcceptance? TryCreate(IModHelper helper, IMonitor monitor, Func<FlowGameSession?> current)
        => Environment.GetEnvironmentVariable("HATIFECT_TEST_MODE") == "1"
            && Environment.GetEnvironmentVariable("HATIFECT_TEST_AUTOMATED") == "1"
            && Environment.GetEnvironmentVariable("HATIFECT_TEST_SCENARIO") == Scenario ? new(helper, monitor, current) : null;

    internal void OnSaveLoaded()
    {
        try
        {
            Require(_stage == Stage.Loading, "Unexpected cancellation acceptance load.");
            ValidateLoadedSave();
            FlowGameSession session = Session();
            Require(!session.IsFaulted && session.ReadSnapshot().SessionId != _sessionId, "SaveLoaded did not create a fresh healthy production session.");
            _sessionId = session.ReadSnapshot().SessionId;
            _loads++;
            if (_loads == 1) PrepareSources();
            else
            {
                Require(_loads is 2 or 3, "Too many cancellation acceptance loads.");
                VerifyWorld(delivered: _loads == 3);
                _passed.Add("reload");
                _verificationFrames = 0;
                _stage = _loads == 2 ? Stage.VerifyCancelled : Stage.VerifyDelivered;
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private void PrepareSources()
    {
        FlowGameSession session = Session();
        Require(session.ReadSnapshot().Stations.Count == 0 && session.ReadSnapshot().Parcels.Count == 0,
            "Cancellation acceptance requires a clean production network.");
        Chest source = CreateChest(out _sourceTile), destination = CreateChest(out _destinationTile);
        Chest holding = CreateChest(out _holdingTile);
        foreach ((string name, int quantity) in new[] { ("cancel", 8), ("quantity", 13), ("metadata", 17) })
        {
            var wine = (StardewValley.Object)ItemRegistry.Create("(O)348", quantity, 4);
            wine.preserve.Value = StardewValley.Object.PreserveType.Wine;
            wine.preservedParentSheetIndex.Value = "613";
            wine.modData["Hatifect.Flow/Acceptance"] = _request.RunId;
            wine.modData["Hatifect.Flow/CancellationCase"] = name;
            source.GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Add(wine);
        }
        session.RegisterStation("cancel_source", "Farm", (int)_sourceTile.X, (int)_sourceTile.Y, source);
        session.RegisterStation("cancel_destination", "Farm", (int)_destinationTile.X, (int)_destinationTile.Y, destination);
        session.Link("cancel_source", "cancel_destination", transitTicks: 180);
        FlowSendCommand first = SendCommand(0, 3);
        _cancelled.Add(Admit(first));
        string[] admittedSource = Items(_sourceTile).Select(FlowItemCodec.Encode).ToArray();
        long revision = session.ReadSnapshot().Revision;
        Require(session.Execute(first).Status == FlowCommandStatus.Conflict, "A stale admission was not rejected.");
        Require(session.Execute(SendCommand(0, 2)).Status == FlowCommandStatus.Rejected, "The active source stack accepted a second shipment.");
        Require(session.ReadSnapshot().Revision == revision && session.ReadSnapshot().Parcels.Count == 1
            && Items(_sourceTile).Select(FlowItemCodec.Encode).SequenceEqual(admittedSource),
            "Rejected admissions changed the source or created another parcel.");
        _passed.Add("duplicate-admission");
        Require(Action(_cancelled[0], FlowParcelAction.Cancel).Status == FlowCommandStatus.Applied,
            "Reserved cancellation was not applied.");
        Require(Items(_sourceTile).Select(FlowItemCodec.Encode).SequenceEqual(admittedSource)
            && session.ReadSnapshot().Parcels.Single().State == ParcelState.Cancelled,
            "Cancellation extracted units or changed source metadata.");
        _passed.Add("cancellation");
        _cancelled.Add(Admit(SendCommand(1, 5)));
        _cancelled.Add(Admit(SendCommand(2, 7)));
        Item[] items = Items(_sourceTile);
        Require(items.Select(item => item.Stack).SequenceEqual(new[] { 8, 13, 17 }), "Admission spent physical units before its scheduled effect.");
        Item moved = FlowItemCodec.Decode(FlowItemCodec.Encode(items[1]));
        moved.Stack = 1;
        holding.GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Add(moved);
        items[1].Stack--;
        items[2].modData["Hatifect.Flow/ChangedAfterAdmission"] = "true";
        _sourceXml = items.Select(FlowItemCodec.Encode).ToArray();
        _holdingXml = FlowItemCodec.Encode(moved);
        _passed.Add("loaded");
        // All setup occurs inside SaveLoaded, before the owning production tick.
        _stage = Stage.Conflicts;
    }

    internal void Tick()
    {
        if (_disposed) return;
        try
        {
            if (_stage == Stage.Exit) { Game1.game1.Exit(); return; }
            Require(++_frames <= 36000, "Cancellation acceptance exceeded its frame bound.");
            switch (_stage)
            {
                case Stage.Startup when _frames >= 30:
                case Stage.ReloadCancelled:
                case Stage.ReloadDelivered:
                    ValidateSaveTree(_request.SavePath);
                    _stage = Stage.Loading;
                    SaveGame.Load(Path.GetFileName(_request.SavePath));
                    Game1.exitActiveMenu();
                    break;
                case Stage.Conflicts when AllCancelled():
                    VerifyWorld(delivered: false);
                    _passed.Add("source-conflict");
                    BeginGameSave(Stage.SavingCancelled);
                    break;
                case Stage.ReturnCancelled:
                case Stage.ReturnDelivered:
                    if (_returning) break;
                    _returning = true; _retired = Session();
                    RequestReturnToTitle();
                    break;
                case Stage.VerifyCancelled:
                    VerifyWorld(delivered: false);
                    if (++_verificationFrames < 120) break;
                    _passed.Add("no-late-effect");
                    Item remainder = FlowItemCodec.Decode(_sourceXml[0]);
                    remainder.Stack = 6; remainder.modData.Remove(ChestInventoryAccess.CargoKey);
                    Item payload = FlowItemCodec.Decode(_sourceXml[0]);
                    payload.Stack = 2;
                    _resent = Admit(SendCommand(0, 2));
                    Require(Items(_sourceTile)[0].Stack == 8, "Re-admission spent quantity before extraction.");
                    payload.modData[ChestInventoryAccess.CargoKey] = Session().ReadSnapshot().Parcels.Single(parcel => parcel.Id == _resent).CargoId.ToString("D");
                    _deliveredXml = FlowItemCodec.Encode(payload);
                    _sourceXml[0] = FlowItemCodec.Encode(remainder);
                    _stage = Stage.Delivery;
                    break;
                case Stage.Delivery when Session().ReadSnapshot().Parcels.Single(parcel => parcel.Id == _resent).State == ParcelState.Delivered:
                    VerifyWorld(delivered: true);
                    _passed.Add("re-admission");
                    BeginGameSave(Stage.SavingDelivered);
                    break;
                case Stage.VerifyDelivered:
                    VerifyWorld(delivered: true);
                    if (++_verificationFrames < 120) break;
                    Require(Action(_resent, FlowParcelAction.RetryDelivery).Status == FlowCommandStatus.Rejected,
                        "The reused source parcel accepted duplicate delivery.");
                    foreach (Guid parcel in _cancelled)
                        Require(Action(parcel, FlowParcelAction.Reserve).Status == FlowCommandStatus.Rejected,
                            "An old cancelled parcel was reactivated.");
                    VerifyWorld(delivered: true);
                    Require(_loads == 3 && _savings == 2 && _saves == 2, "The required cancellation save/load lifecycle did not complete.");
                    WriteReport(); _stage = Stage.Exit;
                    break;
            }
        }
        catch (Exception error) { Fail(error); }
    }

    internal void OnReturnedToTitle()
    {
        try
        {
            Require(_stage is Stage.ReturnCancelled or Stage.ReturnDelivered && _current() is null
                && _retired?.ReadSnapshot().State == FlowApplicationState.Closed,
                "Title transition retained a live production session.");
            _passed.Add("lifecycle"); _returning = false;
            _stage = _stage == Stage.ReturnCancelled ? Stage.ReloadCancelled : Stage.ReloadDelivered;
        }
        catch (Exception error) { Fail(error); }
    }

    private FlowSendCommand SendCommand(int slot, int quantity)
    {
        FlowSnapshot snapshot = Session().ReadSnapshot();
        FlowLinkSnapshot link = snapshot.Links.Single();
        FlowInventorySlot selected = Session().ReadInventory(link.Origin).Single(item => item.Index == slot);
        return new FlowSendCommand(snapshot.SessionId, snapshot.Revision, link.Origin, link.Destination, slot, selected.Fingerprint) { Quantity = quantity };
    }

    private Guid Admit(FlowSendCommand command)
    {
        HashSet<Guid> before = Session().ReadSnapshot().Parcels.Select(parcel => parcel.Id).ToHashSet();
        Require(Session().Execute(command).Status == FlowCommandStatus.Applied, "Partial-stack admission was not applied.");
        FlowParcelSnapshot added = Session().ReadSnapshot().Parcels.Single(parcel => !before.Contains(parcel.Id));
        Require(added.State == ParcelState.Reserved, "The admitted parcel was not reserved before its scheduled effect.");
        return added.Id;
    }

    private FlowCommandResult Action(Guid parcel, FlowParcelAction action)
    {
        FlowSnapshot snapshot = Session().ReadSnapshot();
        return Session().Execute(new FlowParcelCommand(snapshot.SessionId, snapshot.Revision, parcel, action));
    }

    private bool AllCancelled() => _cancelled.Count == 3 && _cancelled.All(id =>
        Session().ReadSnapshot().Parcels.Single(parcel => parcel.Id == id) is { State: ParcelState.Cancelled, DeliveryAttempts: 0 });

    private void VerifyWorld(bool delivered)
    {
        Require(!Session().IsFaulted && AllCancelled(), "Changed or cancelled sources retained live work or a recovery fault.");
        Require(Session().ReadSnapshot().Parcels.Count == (delivered ? 4 : 3), "An unexpected parcel appeared.");
        Require(Items(_sourceTile).Select(FlowItemCodec.Encode).SequenceEqual(_sourceXml), "A cancelled/changed source lost quantity, metadata or ownership.");
        Require(Items(_holdingTile).Select(FlowItemCodec.Encode).SequenceEqual(new[] { _holdingXml }), "The separated source unit changed or disappeared.");
        Item[] destination = Items(_destinationTile);
        Require(delivered ? destination.Length == 1 && FlowItemCodec.Encode(destination[0]) == _deliveredXml
            && Session().ReadSnapshot().Parcels.Single(parcel => parcel.Id == _resent).State == ParcelState.Delivered : destination.Length == 0,
            "Destination cargo is missing, duplicated or arrived from a cancelled parcel.");
        Require(Items(_sourceTile).Sum(item => item.Stack) + Items(_holdingTile).Sum(item => item.Stack) + destination.Sum(item => item.Stack) == 38,
            "Cancellation/source changes did not conserve total physical quantity.");
    }

    private void BeginGameSave(Stage saving)
    {
        ValidateLoadedSave();
        Require(Game1.saveOnNewDay, "The game would skip writing the save.");
        _saveHash = HashFile(Path.Combine(_request.SavePath, Path.GetFileName(_request.SavePath)));
        _stage = saving; Game1.activeClickableMenu = new SaveGameMenu();
    }

    private void OnSaving(object? sender, SavingEventArgs e)
    {
        try
        {
            Require(_stage is Stage.SavingCancelled or Stage.SavingDelivered && Session().IsSaving, "Unexpected Saving or missing production save barrier.");
            ValidateLoadedSave(); VerifyWorld(_stage == Stage.SavingDelivered);
            _savings++; _passed.Add("saving");
        }
        catch (Exception error) { Fail(error); }
    }

    private void OnSaved(object? sender, SavedEventArgs e)
    {
        try
        {
            Require(_stage is Stage.SavingCancelled or Stage.SavingDelivered && !Session().IsSaving, "Unexpected Saved or retained production save barrier.");
            ValidateLoadedSave(); VerifyWorld(_stage == Stage.SavingDelivered);
            Require(_saveHash != HashFile(Path.Combine(_request.SavePath, Path.GetFileName(_request.SavePath))), "Saved did not update the owned game save.");
            _saves++; _passed.Add("saved");
            _stage = _stage == Stage.SavingCancelled ? Stage.ReturnCancelled : Stage.ReturnDelivered;
        }
        catch (Exception error) { Fail(error); }
    }

    private void ValidateLoadedSave()
    {
        Require(Context.IsWorldReady && Context.IsMainPlayer && !Context.IsMultiplayer, "Acceptance requires the loaded single-player world.");
        string expected = Path.GetFileName(_request.SavePath);
        Require(Game1.uniqueIDForThisGame == 4242424242UL && Game1.GetSaveGameName(true) == expected.Split('_')[0]
            && Constants.SaveFolderName == expected && Full(Constants.CurrentSavePath!) == _request.SavePath,
            "The game would write outside the request's canonical save.");
        ValidateSaveTree(_request.SavePath); ValidateSaveOwner(_request.SavePath, _request.RunId, _request.RuntimeId);
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
            farm.Objects.Add(tile, chest); return chest;
        }
        throw new InvalidOperationException("No empty acceptance chest tile is available.");
    }

    private static Item[] Items(Vector2 tile) => ((Chest)Game1.getFarm().Objects[tile]).GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Where(item => item is not null).ToArray();
    private FlowGameSession Session() => _current() ?? throw new InvalidOperationException("Production session is absent.");

    internal void Fail(Exception error)
    {
        _errors.Add(error.ToString()); _monitor.Log("Flowline " + Scenario + " failed: " + error, LogLevel.Error);
        try { WriteReport(); } finally { _stage = Stage.Exit; }
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
                CapturedAtUtc = captured, Note = _errors.Count == 0 ? "Cancelled and changed sources survive real game saves; full XML and quantity38 conserved, explicit source reuse delivers2 once." : string.Join("\n", _errors) }).ToArray()
        });
        AtomicJson(Path.Combine(_request.Artifact, "diagnostics", "flow-chest-cancellation.json"), new
        { requestId = _request.RunId, scenarioId = Scenario, frames = _frames, loads = _loads, savingEvents = _savings, savedEvents = _saves,
            cancelledParcelIds = _cancelled.ToArray(), resentParcelId = _resent, totalQuantity = 38, errors = _errors.ToArray() });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _helper.Events.GameLoop.Saving -= OnSaving; _helper.Events.GameLoop.Saved -= OnSaved;
    }
}
