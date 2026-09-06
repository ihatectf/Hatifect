using System;
using System.Collections.Generic;
using System.Globalization;
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

internal sealed class FlowChestReturnAcceptance : IDisposable
{
    internal const string Scenario = "flow.chest.return";
    private static readonly string[] Checks = { "loaded", "custody", "capacity", "delivery-retry", "return-requested",
        "return-rejected", "returned", "saving", "saved", "lifecycle", "reload", "no-auto-retry", "no-duplication", "taken-items" };
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly Func<FlowGameSession?> _current;
    private readonly AcceptanceRequest _request;
    private readonly string _fingerprint;
    private readonly HashSet<string> _passed = new(StringComparer.Ordinal);
    private readonly List<string> _errors = new();
    private Vector2 _sourceTile, _destinationTile, _holdingTile;
    private Guid _wholeParcel, _partialParcel, _sessionId;
    private string _remainderXml = "", _saveHash = "";
    private string[] _sourceFillers = Array.Empty<string>(), _destinationFillers = Array.Empty<string>();
    private FlowGameSession? _retired;
    private Stage _stage;
    private int _capacity, _boundary, _loads, _savings, _saves, _frames, _verificationFrames;
    private bool _returning, _disposed;
    private enum Stage { Startup, Loading, AwaitDeliveryRejected, VerifyDeliveryRejected, AwaitRetryDelivery,
        AwaitReturnRejected, VerifyReturnRejected, AwaitReturned, VerifyReturned, VerifyTaken, Saving, Return, Reload, Exit }

    private FlowChestReturnAcceptance(IModHelper helper, IMonitor monitor, Func<FlowGameSession?> current)
    {
        _helper = helper; _monitor = monitor; _current = current;
        _request = ReadAcceptanceRequest(helper, Scenario); _fingerprint = RuntimeFingerprint();
        helper.Events.GameLoop.Saving += OnSaving; helper.Events.GameLoop.Saved += OnSaved;
    }

    internal static FlowChestReturnAcceptance? TryCreate(IModHelper helper, IMonitor monitor, Func<FlowGameSession?> current)
        => Environment.GetEnvironmentVariable("HATIFECT_TEST_MODE") == "1"
            && Environment.GetEnvironmentVariable("HATIFECT_TEST_AUTOMATED") == "1"
            && Environment.GetEnvironmentVariable("HATIFECT_TEST_SCENARIO") == Scenario ? new(helper, monitor, current) : null;

    internal void OnSaveLoaded()
    {
        try
        {
            Require(_stage == Stage.Loading, "Unexpected return acceptance load.");
            ValidateLoadedSave();
            Require(!Session().IsFaulted && Session().ReadSnapshot().SessionId != _sessionId, "Load did not open a fresh healthy production session.");
            _sessionId = Session().ReadSnapshot().SessionId; _loads++; _verificationFrames = 0;
            if (_loads == 1) { PrepareRoute(); _stage = Stage.AwaitDeliveryRejected; return; }
            Require(_loads is >= 2 and <= 6, "Return acceptance exceeded its fixed load sequence.");
            VerifyBoundary(_loads - 1); _passed.Add("reload");
            _stage = _loads switch
            {
                2 => Stage.VerifyDeliveryRejected,
                3 => Stage.AwaitReturnRejected,
                4 => Stage.VerifyReturnRejected,
                5 => Stage.VerifyReturned,
                _ => Stage.VerifyTaken
            };
        }
        catch (Exception error) { Fail(error); }
    }

    private void PrepareRoute()
    {
        FlowGameSession session = Session();
        Require(session.ReadSnapshot().Stations.Count == 0 && session.ReadSnapshot().Parcels.Count == 0, "Return acceptance needs a clean network.");
        Chest source = CreateChest("source", out _sourceTile), destination = CreateChest("destination", out _destinationTile);
        Chest holding = CreateChest("holding", out _holdingTile);
        _capacity = source.GetActualCapacity();
        Require(_capacity is >= 4 and <= 128 && destination.GetActualCapacity() == _capacity && holding.GetActualCapacity() >= 4,
            "Acceptance chests do not have bounded ordinary capacity.");
        var wine = (StardewValley.Object)ItemRegistry.Create("(O)348", 8, 4);
        wine.preserve.Value = StardewValley.Object.PreserveType.Wine; wine.preservedParentSheetIndex.Value = "613";
        wine.modData["Hatifect.Flow/Acceptance"] = _request.RunId;
        source.GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Add(wine);
        Item partial = FlowItemCodec.Decode(FlowItemCodec.Encode(wine));
        partial.Stack = 13; partial.modData["Hatifect.Flow/PartialAcceptance"] = "true";
        source.GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Add(partial);
        Item remainder = FlowItemCodec.Decode(FlowItemCodec.Encode(partial)); remainder.Stack = 8;
        _remainderXml = FlowItemCodec.Encode(remainder);
        _sourceFillers = Enumerable.Range(0, _capacity - 1).Select(index => FlowItemCodec.Encode(CreateFiller("source", index))).ToArray();
        _destinationFillers = Enumerable.Range(0, _capacity).Select(index => FlowItemCodec.Encode(CreateFiller("destination", index))).ToArray();
        foreach (string xml in _destinationFillers) destination.GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Add(FlowItemCodec.Decode(xml));
        session.RegisterStation("return_source", "Farm", (int)_sourceTile.X, (int)_sourceTile.Y, source);
        session.RegisterStation("return_destination", "Farm", (int)_destinationTile.X, (int)_destinationTile.Y, destination);
        session.Link("return_source", "return_destination", transitTicks: 180);
        _wholeParcel = session.Send("return_source", "return_destination", 0);
        FlowSnapshot snapshot = session.ReadSnapshot(); FlowLinkSnapshot link = snapshot.Links.Single();
        FlowInventorySlot selected = session.ReadInventory(link.Origin).Single(item => item.Index == 1);
        Require(session.Execute(new FlowSendCommand(snapshot.SessionId, snapshot.Revision, link.Origin, link.Destination,
            selected.Index, selected.Fingerprint) { Quantity = 5 }).Status == FlowCommandStatus.Applied, "Partial admission failed.");
        _partialParcel = session.ReadSnapshot().Parcels.Single(parcel => parcel.Id != _wholeParcel).Id;
        Require(Parcel(false).State == ParcelState.Reserved && Parcel(true).State == ParcelState.Reserved && wine.Stack == 8 && partial.Stack == 13,
            "Admission spent units before its scheduled extraction.");
        _passed.Add("loaded");
    }

    internal void Tick()
    {
        if (_disposed) return;
        try
        {
            if (_stage == Stage.Exit) { Game1.game1.Exit(); return; }
            Require(++_frames <= 36000, "Return acceptance exceeded its frame bound.");
            switch (_stage)
            {
                case Stage.Startup when _frames >= 30:
                case Stage.Reload:
                    ValidateSaveTree(_request.SavePath); _stage = Stage.Loading;
                    SaveGame.Load(Path.GetFileName(_request.SavePath)); Game1.exitActiveMenu();
                    break;
                case Stage.AwaitDeliveryRejected when Parcel(false).State == ParcelState.DeliveryRejected && Parcel(true).State == ParcelState.DeliveryRejected:
                    VerifyBoundary(1); _passed.Add("custody"); _passed.Add("capacity"); BeginGameSave(1);
                    break;
                case Stage.VerifyDeliveryRejected:
                    VerifyBoundary(1);
                    if (++_verificationFrames < 120) break;
                    MoveToHolding(_destinationTile, _destinationFillers[0]);
                    Require(Action(false, FlowParcelAction.RetryDelivery).Status == FlowCommandStatus.Applied, "Explicit delivery retry was refused.");
                    _stage = Stage.AwaitRetryDelivery;
                    break;
                case Stage.AwaitRetryDelivery when Parcel(false).State == ParcelState.Delivered:
                    Require(Parcel(false).DeliveryAttempts == 2 && Parcel(true) is { State: ParcelState.DeliveryRejected, DeliveryAttempts: 1 },
                        "Delivery retry advanced the wrong cargo or attempt.");
                    _passed.Add("delivery-retry"); FillSource();
                    Require(Action(true, FlowParcelAction.ReturnToSource).Status == FlowCommandStatus.Applied, "Return request was refused.");
                    // Preserve queued ReturnRequested before the next owning tick; real Saving recaptures this barrier.
                    Session().BeginSave(); VerifyBoundary(2); _passed.Add("return-requested"); BeginGameSave(2);
                    break;
                case Stage.AwaitReturnRejected when Parcel(true).State == ParcelState.ReturnRejected:
                    VerifyBoundary(3); _passed.Add("return-rejected"); BeginGameSave(3);
                    break;
                case Stage.VerifyReturnRejected:
                    VerifyBoundary(3);
                    if (++_verificationFrames < 120) break;
                    _passed.Add("no-auto-retry"); MoveToHolding(_sourceTile, _sourceFillers[0]);
                    Require(Action(true, FlowParcelAction.RetryDelivery).Status == FlowCommandStatus.Applied, "Explicit return retry was refused.");
                    _stage = Stage.AwaitReturned;
                    break;
                case Stage.AwaitReturned when Parcel(true).State == ParcelState.Returned:
                    VerifyBoundary(4); _passed.Add("returned"); BeginGameSave(4);
                    break;
                case Stage.VerifyReturned:
                    VerifyBoundary(4);
                    if (++_verificationFrames < 120) break;
                    RejectTerminalCommands(); VerifyBoundary(4); _passed.Add("no-duplication");
                    MoveToHolding(_destinationTile, CargoXml(false)); MoveToHolding(_sourceTile, CargoXml(true));
                    VerifyBoundary(5); BeginGameSave(5);
                    break;
                case Stage.VerifyTaken:
                    VerifyBoundary(5);
                    if (++_verificationFrames < 120) break;
                    RejectTerminalCommands(); VerifyBoundary(5); _passed.Add("taken-items");
                    Require(_loads == 6 && _savings == 5 && _saves == 5, "Return acceptance did not complete all real save/load boundaries.");
                    WriteReport(); _stage = Stage.Exit;
                    break;
                case Stage.Return:
                    if (_returning) break;
                    _returning = true; _retired = Session(); RequestReturnToTitle();
                    break;
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private void VerifyBoundary(int boundary)
    {
        Require(boundary is >= 1 and <= 5 && !Session().IsFaulted && Session().ReadSnapshot().Parcels.Count == 2,
            "The return boundary has unexpected cargo or a recovery fault.");
        FlowParcelSnapshot whole = Parcel(false), partial = Parcel(true);
        ParcelState partialState = boundary switch { 1 => ParcelState.DeliveryRejected, 2 => ParcelState.ReturnRequested, 3 => ParcelState.ReturnRejected, _ => ParcelState.Returned };
        Require(whole.State == (boundary == 1 ? ParcelState.DeliveryRejected : ParcelState.Delivered)
            && whole.DeliveryAttempts == (boundary == 1 ? 1 : 2) && whole.Quantity == 8
            && partial.State == partialState && partial.DeliveryAttempts == (boundary <= 2 ? 1 : boundary == 3 ? 2 : 3) && partial.Quantity == 5,
            "Saved custody, return direction or the shared attempt counter changed.");
        var source = new List<string> { _remainderXml };
        if (boundary >= 2) source.AddRange(_sourceFillers.Skip(boundary >= 4 ? 1 : 0));
        if (boundary == 4) source.Add(CargoXml(true));
        var destination = _destinationFillers.Skip(boundary >= 2 ? 1 : 0).ToList();
        if (boundary is >= 2 and <= 4) destination.Add(CargoXml(false));
        var holding = new List<string>();
        if (boundary >= 2) holding.Add(_destinationFillers[0]);
        if (boundary >= 4) holding.Add(_sourceFillers[0]);
        if (boundary == 5) { holding.Add(CargoXml(false)); holding.Add(CargoXml(true)); }
        VerifyChest(_sourceTile, "source", source); VerifyChest(_destinationTile, "destination", destination); VerifyChest(_holdingTile, "holding", holding);
        int physical = Items(_sourceTile).Concat(Items(_destinationTile)).Concat(Items(_holdingTile)).Where(item => item.QualifiedItemId == "(O)348").Sum(item => item.Stack);
        int retained = (boundary == 1 ? 8 : 0) + (boundary < 4 ? 5 : 0);
        Require(physical + retained == 21, "Physical items plus active parcel custody did not conserve quantity21.");
    }

    private void VerifyChest(Vector2 tile, string role, IEnumerable<string> expected)
    {
        Chest chest = ChestAt(tile);
        Require(chest.modData.TryGetValue("Hatifect.Flow/ReturnChest", out string identity) && identity == _request.RunId + ":" + role,
            "An acceptance chest was replaced or belongs to another request.");
        Require(Items(tile).Select(FlowItemCodec.Encode).OrderBy(xml => xml, StringComparer.Ordinal)
            .SequenceEqual(expected.OrderBy(xml => xml, StringComparer.Ordinal)),
            "Cargo or filler XML changed in the " + role + " chest.");
    }

    private string CargoXml(bool partial)
    {
        Item item = FlowItemCodec.Decode(_remainderXml); item.Stack = partial ? 5 : 8;
        if (!partial) item.modData.Remove("Hatifect.Flow/PartialAcceptance");
        item.modData[ChestInventoryAccess.CargoKey] = Parcel(partial).CargoId.ToString("D");
        return FlowItemCodec.Encode(item);
    }

    private Item CreateFiller(string role, int index)
    {
        Item item = ItemRegistry.Create("(O)388", 1);
        item.modData["Hatifect.Flow/ReturnFiller"] = _request.RunId + ":" + role + ":" + index.ToString(CultureInfo.InvariantCulture);
        return item;
    }

    private void FillSource()
    {
        var inventory = ChestAt(_sourceTile).GetItemsForPlayer(Game1.player.UniqueMultiplayerID);
        Require(Items(_sourceTile).Select(FlowItemCodec.Encode).SequenceEqual(new[] { _remainderXml }), "Source remainder changed before the return capacity test.");
        int next = 0;
        for (int index = 0; index < inventory.Count; index++)
            if (inventory[index] is null) inventory[index] = FlowItemCodec.Decode(_sourceFillers[next++]);
        while (inventory.Count < _capacity) inventory.Add(FlowItemCodec.Decode(_sourceFillers[next++]));
        Require(next == _sourceFillers.Length && inventory.Count == _capacity, "Source filling did not reach exact capacity.");
    }

    private void MoveToHolding(Vector2 from, string xml)
    {
        var inventory = ChestAt(from).GetItemsForPlayer(Game1.player.UniqueMultiplayerID);
        var holding = ChestAt(_holdingTile).GetItemsForPlayer(Game1.player.UniqueMultiplayerID);
        Require(holding.Count < ChestAt(_holdingTile).GetActualCapacity(), "The acceptance holding chest is full.");
        int match = -1;
        for (int index = 0; index < inventory.Count; index++)
            if (inventory[index] is Item item && FlowItemCodec.Encode(item) == xml)
            { Require(match < 0, "A moved item is already duplicated."); match = index; }
        Require(match >= 0, "The exact item to move is missing.");
        holding.Add(inventory[match]); inventory[match] = null;
    }

    private FlowParcelSnapshot Parcel(bool partial) => Session().ReadSnapshot().Parcels.Single(parcel => parcel.Id == (partial ? _partialParcel : _wholeParcel));
    private FlowCommandResult Action(bool partial, FlowParcelAction action)
    {
        FlowSnapshot snapshot = Session().ReadSnapshot();
        return Session().Execute(new FlowParcelCommand(snapshot.SessionId, snapshot.Revision, partial ? _partialParcel : _wholeParcel, action));
    }

    private void RejectTerminalCommands()
    {
        foreach (bool partial in new[] { false, true })
        foreach (FlowParcelAction action in new[] { FlowParcelAction.RetryDelivery, FlowParcelAction.ReturnToSource, FlowParcelAction.Cancel })
            Require(Action(partial, action).Status == FlowCommandStatus.Rejected, "A terminal parcel accepted another physical operation.");
    }

    private void BeginGameSave(int boundary)
    {
        ValidateLoadedSave(); Require(Game1.saveOnNewDay && _loads == boundary, "Unexpected game-save boundary.");
        _saveHash = HashFile(Path.Combine(_request.SavePath, Path.GetFileName(_request.SavePath)));
        _boundary = boundary; _stage = Stage.Saving; Game1.activeClickableMenu = new SaveGameMenu();
    }

    private void OnSaving(object? sender, SavingEventArgs e)
    {
        try
        {
            Require(_stage == Stage.Saving && Session().IsSaving && _savings + 1 == _boundary, "Unexpected Saving or missing production barrier.");
            ValidateLoadedSave(); VerifyBoundary(_boundary); _savings++; _passed.Add("saving");
        }
        catch (Exception error) { Fail(error); }
    }

    private void OnSaved(object? sender, SavedEventArgs e)
    {
        try
        {
            Require(_stage == Stage.Saving && !Session().IsSaving && _saves + 1 == _boundary && _savings == _boundary, "Unexpected Saved or retained production barrier.");
            ValidateLoadedSave(); VerifyBoundary(_boundary);
            Require(_saveHash != HashFile(Path.Combine(_request.SavePath, Path.GetFileName(_request.SavePath))), "Saved did not change the owned game save.");
            _saves++; _passed.Add("saved");
            // Keep the saved queued return untouched until title; the production tick runs before this driver.
            if (_boundary == 2) Session().BeginSave();
            _stage = Stage.Return;
        }
        catch (Exception error) { Fail(error); }
    }

    internal void OnReturnedToTitle()
    {
        try
        {
            Require(_stage == Stage.Return && _current() is null && _retired?.ReadSnapshot().State == FlowApplicationState.Closed,
                "Title retained an active production session.");
            _passed.Add("lifecycle"); _returning = false; _stage = Stage.Reload;
        }
        catch (Exception error) { Fail(error); }
    }

    private void ValidateLoadedSave()
    {
        Require(Context.IsWorldReady && Context.IsMainPlayer && !Context.IsMultiplayer, "Acceptance requires a loaded single-player save.");
        string expected = Path.GetFileName(_request.SavePath);
        Require(Game1.uniqueIDForThisGame == 4242424242UL && Game1.GetSaveGameName(true) == expected.Split('_')[0]
            && Constants.SaveFolderName == expected && Full(Constants.CurrentSavePath!) == _request.SavePath, "The game would save outside the request-owned canonical directory.");
        ValidateSaveTree(_request.SavePath); ValidateSaveOwner(_request.SavePath, _request.RunId, _request.RuntimeId);
    }

    private Chest CreateChest(string role, out Vector2 tile)
    {
        var farm = Game1.getFarm();
        for (int y = 5; y < 20; y++)
        for (int x = 5; x < 20; x++)
        {
            tile = new Vector2(x, y); if (farm.Objects.ContainsKey(tile)) continue;
            var chest = new Chest(playerChest: true, tileLocation: tile, itemId: "130");
            chest.modData["Hatifect.Flow/ReturnChest"] = _request.RunId + ":" + role;
            farm.Objects.Add(tile, chest); return chest;
        }
        throw new InvalidOperationException("No empty acceptance chest tile is available.");
    }

    private static Chest ChestAt(Vector2 tile) => (Chest)Game1.getFarm().Objects[tile];
    private static Item[] Items(Vector2 tile) => ChestAt(tile).GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Where(item => item is not null).ToArray();
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
            HostChecks = Checks.Select(id => new { Id = Scenario + "." + id, Passed = _errors.Count == 0 && _passed.Contains(id), CapturedAtUtc = captured,
                Note = _errors.Count == 0 ? "Real full-chest refusal, explicit retry/return, five game saves; exact cargo/filler XML and quantity21 retained, taken items never recreated." : string.Join("\n", _errors) }).ToArray()
        });
        AtomicJson(Path.Combine(_request.Artifact, "diagnostics", "flow-chest-return.json"), new
        { requestId = _request.RunId, scenarioId = Scenario, frames = _frames, loads = _loads, savingEvents = _savings, savedEvents = _saves,
            wholeParcelId = _wholeParcel, partialParcelId = _partialParcel, totalCargoQuantity = 21, errors = _errors.ToArray() });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _helper.Events.GameLoop.Saving -= OnSaving; _helper.Events.GameLoop.Saved -= OnSaved;
    }
}
