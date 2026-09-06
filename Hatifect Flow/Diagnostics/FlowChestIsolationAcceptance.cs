using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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

internal sealed class FlowChestIsolationAcceptance : IDisposable
{
    internal const string Scenario = "flow.chest.isolation";
    private static readonly string[] Checks = { "loaded", "custody", "saving", "saved", "lifecycle", "save-isolation",
        "session-fencing", "reload", "delivery", "item-fidelity", "no-duplication", "inactive-files" };
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly Func<FlowGameSession?> _current;
    private readonly AcceptanceRequest _request;
    private readonly string _fingerprint;
    private readonly HashSet<string> _passed = new(StringComparer.Ordinal);
    private readonly List<string> _errors = new();
    private readonly List<Guid> _sessions = new();
    private readonly World[] _worlds = { new(4242424242UL, 13, 5), new(4242424243UL, 17, 7) };
    private readonly string[] _savedTrees = { "", "" };
    private FlowGameSession? _retired;
    private string _saveBefore = "";
    private Stage _stage;
    private int _loads, _savings, _saves, _titles, _frames, _verificationFrames;
    private bool _returning, _disposed;
    private enum Stage { Startup, Loading, AwaitFirstTransit, AwaitSecondDelivery, AwaitFinalDelivery, Saving, Return, Reload, VerifyFinal, Exit }
    private sealed class World
    {
        internal readonly ulong Id;
        internal readonly int SourceQuantity, CargoQuantity;
        internal Vector2 Source, Destination;
        internal Guid Parcel;
        internal string RemainderXml = "", CargoXml = "";
        internal World(ulong id, int source, int cargo) { Id = id; SourceQuantity = source; CargoQuantity = cargo; }
    }
    private int WorldIndex => _loads == 2 ? 1 : 0;
    private string SavePath(int world) => world == 0 ? _request.SavePath : _request.SecondSavePath!;
    private FlowGameSession Session() => _current() ?? throw new InvalidOperationException("Production session is absent.");

    private FlowChestIsolationAcceptance(IModHelper helper, IMonitor monitor, Func<FlowGameSession?> current)
    {
        _helper = helper; _monitor = monitor; _current = current;
        _request = ReadAcceptanceRequest(helper, Scenario); _fingerprint = RuntimeFingerprint();
        Require(_request.SecondSavePath is not null, "Production isolation needs its derived second save.");
        helper.Events.GameLoop.Saving += OnSaving; helper.Events.GameLoop.Saved += OnSaved;
    }

    internal static FlowChestIsolationAcceptance? TryCreate(IModHelper helper, IMonitor monitor, Func<FlowGameSession?> current)
        => Environment.GetEnvironmentVariable("HATIFECT_TEST_MODE") == "1"
            && Environment.GetEnvironmentVariable("HATIFECT_TEST_AUTOMATED") == "1"
            && Environment.GetEnvironmentVariable("HATIFECT_TEST_SCENARIO") == Scenario ? new(helper, monitor, current) : null;

    internal void OnSaveLoaded()
    {
        try
        {
            Require(_stage == Stage.Loading && _loads < 3, "Unexpected production isolation load.");
            _loads++; ValidateLoadedSave(); RequireTree(0); RequireTree(1);
            FlowSnapshot snapshot = Session().ReadSnapshot();
            Require(!Session().IsFaulted && !_sessions.Contains(snapshot.SessionId), "A world reused a prior or faulted session.");
            if (_loads == 1)
            {
                _sessions.Add(snapshot.SessionId); PrepareRoute(0); _passed.Add("loaded"); _stage = Stage.AwaitFirstTransit;
                return;
            }
            if (_loads == 2)
            {
                Require(snapshot.Stations.Count == 0 && snapshot.Parcels.Count == 0, "Save B inherited save A's transport.");
                RejectStale(_sessions[0], _worlds[0].Parcel);
                _sessions.Add(snapshot.SessionId); PrepareRoute(1);
                Require(_worlds[1].Parcel != _worlds[0].Parcel, "Two saves reused parcel identity.");
                _stage = Stage.AwaitSecondDelivery;
            }
            else
            {
                VerifyWorld(0, ParcelState.InTransit);
                RejectStale(_sessions[0], _worlds[0].Parcel); RejectStale(_sessions[1], _worlds[1].Parcel);
                _sessions.Add(snapshot.SessionId); _passed.Add("reload"); _passed.Add("session-fencing");
                _stage = Stage.AwaitFinalDelivery;
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private void PrepareRoute(int index)
    {
        World world = _worlds[index]; FlowGameSession session = Session();
        Require(session.ReadSnapshot().Stations.Count == 0 && session.ReadSnapshot().Parcels.Count == 0, "The new world is not clean.");
        Chest source = CreateChest("source", out world.Source), destination = CreateChest("destination", out world.Destination);
        var item = (StardewValley.Object)ItemRegistry.Create("(O)348", world.SourceQuantity, 4);
        item.preserve.Value = StardewValley.Object.PreserveType.Wine; item.preservedParentSheetIndex.Value = "613";
        item.modData["Hatifect.Flow/Isolation"] = _request.RunId + ":" + index;
        source.GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Add(item);
        Item remainder = FlowItemCodec.Decode(FlowItemCodec.Encode(item)); remainder.Stack -= world.CargoQuantity;
        world.RemainderXml = FlowItemCodec.Encode(remainder);
        session.RegisterStation("isolation_source", "Farm", (int)world.Source.X, (int)world.Source.Y, source);
        session.RegisterStation("isolation_destination", "Farm", (int)world.Destination.X, (int)world.Destination.Y, destination);
        session.Link("isolation_source", "isolation_destination", transitTicks: 180);
        FlowSnapshot snapshot = session.ReadSnapshot(); FlowLinkSnapshot link = snapshot.Links.Single();
        FlowInventorySlot selected = session.ReadInventory(link.Origin).Single();
        Require(session.Execute(new FlowSendCommand(snapshot.SessionId, snapshot.Revision, link.Origin, link.Destination,
            selected.Index, selected.Fingerprint) { Quantity = world.CargoQuantity }).Status == FlowCommandStatus.Applied, "Partial admission failed.");
        FlowParcelSnapshot parcel = session.ReadSnapshot().Parcels.Single(); world.Parcel = parcel.Id;
        Item cargo = FlowItemCodec.Decode(world.RemainderXml); cargo.Stack = world.CargoQuantity;
        cargo.modData[ChestInventoryAccess.CargoKey] = parcel.CargoId.ToString("D"); world.CargoXml = FlowItemCodec.Encode(cargo);
        Require(parcel.State == ParcelState.Reserved && item.Stack == world.SourceQuantity, "Admission extracted before the owning tick.");
    }

    internal void Tick()
    {
        if (_disposed) return;
        try
        {
            if (_stage == Stage.Exit) { Game1.game1.Exit(); return; }
            Require(++_frames <= 36000, "Production isolation exceeded its frame bound.");
            switch (_stage)
            {
                case Stage.Startup when _frames >= 30:
                    _savedTrees[0] = FingerprintTree(SavePath(0)); _savedTrees[1] = FingerprintTree(SavePath(1));
                    goto case Stage.Reload;
                case Stage.Reload:
                    RequireTree(0); RequireTree(1);
                    string next = SavePath(_loads == 1 ? 1 : 0); ValidateSaveTree(next); _stage = Stage.Loading;
                    SaveGame.Load(Path.GetFileName(next)); Game1.exitActiveMenu();
                    break;
                case Stage.AwaitFirstTransit when Parcel().State == ParcelState.InTransit:
                    VerifyWorld(0, ParcelState.InTransit); _passed.Add("custody"); BeginGameSave();
                    break;
                case Stage.AwaitSecondDelivery when Parcel().State == ParcelState.Delivered:
                    VerifyWorld(1, ParcelState.Delivered); RequireTree(0); BeginGameSave();
                    break;
                case Stage.AwaitFinalDelivery when Parcel().State == ParcelState.Delivered:
                    VerifyWorld(0, ParcelState.Delivered); RequireTree(1); _passed.Add("delivery"); BeginGameSave();
                    break;
                case Stage.Return:
                    if (_returning) break;
                    _returning = true; _retired = Session(); RequestReturnToTitle();
                    break;
                case Stage.VerifyFinal:
                    VerifyWorld(0, ParcelState.Delivered);
                    if (++_verificationFrames < 120) break;
                    FlowSnapshot snapshot = Session().ReadSnapshot();
                    Require(Session().Execute(new FlowParcelCommand(snapshot.SessionId, snapshot.Revision, _worlds[0].Parcel,
                        FlowParcelAction.RetryDelivery)).Status == FlowCommandStatus.Rejected, "A completed parcel accepted repeat delivery.");
                    VerifyWorld(0, ParcelState.Delivered); RequireTree(0); RequireTree(1);
                    Require(_loads == 3 && _savings == 3 && _saves == 3 && _titles == 2 && _sessions.Count == 3,
                        "Production isolation did not complete its fixed lifecycle.");
                    _passed.Add("save-isolation"); _passed.Add("inactive-files"); _passed.Add("item-fidelity"); _passed.Add("no-duplication");
                    WriteReport(); _stage = Stage.Exit;
                    break;
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private FlowParcelSnapshot Parcel() => Session().ReadSnapshot().Parcels.Single();
    private void VerifyWorld(int index, ParcelState state)
    {
        Require(WorldIndex == index && !Session().IsFaulted, "Unexpected active world or recovery fault.");
        World world = _worlds[index]; FlowSnapshot snapshot = Session().ReadSnapshot();
        FlowParcelSnapshot parcel = snapshot.Parcels.Single();
        Require(snapshot.Stations.Count == 2 && snapshot.Links.Count == 1 && parcel.Id == world.Parcel && parcel.State == state
            && parcel.Quantity == world.CargoQuantity && parcel.DeliveryAttempts == (state == ParcelState.Delivered ? 1 : 0),
            "The loaded world has foreign cargo, wrong custody or repeated delivery.");
        Require(Items(world.Source).Select(FlowItemCodec.Encode).SequenceEqual(new[] { world.RemainderXml }), "Source XML changed across save isolation.");
        string[] destination = Items(world.Destination).Select(FlowItemCodec.Encode).ToArray();
        Require(destination.SequenceEqual(state == ParcelState.Delivered ? new[] { world.CargoXml } : Array.Empty<string>()),
            "Destination XML/custody changed across save isolation.");
        Require(Items(world.Source).Sum(item => item.Stack) + Items(world.Destination).Sum(item => item.Stack)
            + (state == ParcelState.InTransit ? world.CargoQuantity : 0) == world.SourceQuantity, "World cargo quantity was lost or duplicated.");
        foreach (var pair in new[] { (world.Source, "source"), (world.Destination, "destination") })
            Require(ChestAt(pair.Item1).modData.TryGetValue("Hatifect.Flow/IsolationChest", out string identity)
                && identity == _request.RunId + ":" + index + ":" + pair.Item2, "A world contains foreign acceptance containers.");
    }

    private void RejectStale(Guid session, Guid parcel)
    {
        FlowSnapshot before = Session().ReadSnapshot();
        FlowCommandResult result = Session().Execute(new FlowParcelCommand(session, before.Revision, parcel, FlowParcelAction.Cancel));
        Require(result.Status == FlowCommandStatus.Conflict && result.Code == FlowRejectionCode.StaleSession
            && ReferenceEquals(before, Session().ReadSnapshot()), "A stale world command was not fenced before mutation.");
    }

    private void BeginGameSave()
    {
        ValidateLoadedSave(); Require(Game1.saveOnNewDay && _loads == _saves + 1, "Unexpected game-save boundary.");
        _saveBefore = FingerprintTree(SavePath(WorldIndex)); _stage = Stage.Saving; Game1.activeClickableMenu = new SaveGameMenu();
    }
    private void OnSaving(object? sender, SavingEventArgs e)
    {
        try
        {
            Require(_stage == Stage.Saving && Session().IsSaving && _savings + 1 == _loads, "Unexpected Saving or missing production barrier.");
            ValidateLoadedSave(); VerifyWorld(WorldIndex, _loads == 1 ? ParcelState.InTransit : ParcelState.Delivered);
            RequireTree(1 - WorldIndex);
            _savings++; _passed.Add("saving");
        }
        catch (Exception error) { Fail(error); }
    }
    private void OnSaved(object? sender, SavedEventArgs e)
    {
        try
        {
            Require(_stage == Stage.Saving && !Session().IsSaving && _saves + 1 == _loads && _savings == _loads,
                "Unexpected Saved or retained production barrier.");
            ValidateLoadedSave(); VerifyWorld(WorldIndex, _loads == 1 ? ParcelState.InTransit : ParcelState.Delivered);
            string saved = FingerprintTree(SavePath(WorldIndex)); Require(saved != _saveBefore, "Saved did not change the active world's files.");
            _savedTrees[WorldIndex] = saved;
            RequireTree(1 - WorldIndex);
            _saves++; _passed.Add("saved");
            if (_loads == 1) Session().BeginSave();
            _stage = _loads == 3 ? Stage.VerifyFinal : Stage.Return;
        }
        catch (Exception error) { Fail(error); }
    }
    internal void OnReturnedToTitle()
    {
        try
        {
            Require(_stage == Stage.Return && _current() is null && _retired?.ReadSnapshot().State == FlowApplicationState.Closed,
                "Title retained an active production session.");
            FlowSnapshot old = _retired!.ReadSnapshot();
            Require(_retired.Execute(new FlowParcelCommand(old.SessionId, old.Revision, _worlds[WorldIndex].Parcel,
                FlowParcelAction.Cancel)).Status == FlowCommandStatus.SessionClosed, "A retired session retained command authority.");
            RequireTree(0); RequireTree(1); _titles++; _passed.Add("lifecycle"); _returning = false; _stage = Stage.Reload;
        }
        catch (Exception error) { Fail(error); }
    }
    private void ValidateLoadedSave()
    {
        Require(Context.IsWorldReady && Context.IsMainPlayer && !Context.IsMultiplayer, "Isolation needs a loaded single-player world.");
        string save = SavePath(WorldIndex), name = Path.GetFileName(save);
        Require(Game1.uniqueIDForThisGame == _worlds[WorldIndex].Id && Game1.GetSaveGameName(true) == name.Split('_')[0]
            && Constants.SaveFolderName == name && Full(Constants.CurrentSavePath!) == save, "The game would save outside the derived owned world.");
        ValidateSaveTree(save); ValidateSaveOwner(save, WorldIndex == 0 ? _request.RunId : CompanionRunId(_request.RunId), _request.RuntimeId);
    }
    private void RequireTree(int index) => Require(_savedTrees[index].Length > 0 && _savedTrees[index] == FingerprintTree(SavePath(index)),
        "An inactive or unchanged world save tree was modified.");
    private static string FingerprintTree(string path)
    {
        ValidateSaveTree(path); var lines = new List<string>(); long total = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories).OrderBy(value => value, StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(path, entry);
            if (Directory.Exists(entry)) lines.Add("D\t" + relative + "\n");
            else
            {
                long size = new FileInfo(entry).Length; total = checked(total + size);
                Require(size <= 64 * 1024 * 1024 && total <= 256 * 1024 * 1024, "Save tree hashing exceeds its byte bound.");
                lines.Add("F\t" + relative + "\t" + HashFile(entry) + "\n");
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(lines)))).ToLowerInvariant();
    }
    private Chest CreateChest(string role, out Vector2 tile)
    {
        var farm = Game1.getFarm();
        for (int y = 5; y < 20; y++)
        for (int x = 5; x < 20; x++)
        {
            tile = new Vector2(x, y); if (farm.Objects.ContainsKey(tile)) continue;
            var chest = new Chest(playerChest: true, tileLocation: tile, itemId: "130");
            chest.modData["Hatifect.Flow/IsolationChest"] = _request.RunId + ":" + WorldIndex + ":" + role;
            farm.Objects.Add(tile, chest); return chest;
        }
        throw new InvalidOperationException("No empty acceptance chest tile is available.");
    }
    private static Chest ChestAt(Vector2 tile) => (Chest)Game1.getFarm().Objects[tile];
    private static Item[] Items(Vector2 tile) => ChestAt(tile).GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Where(item => item is not null).ToArray();
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
                Note = _errors.Count == 0 ? "Production A/B/A: distinct worlds and sessions, exact partial cargo XML, three real saves and unchanged inactive save trees." : string.Join("\n", _errors) }).ToArray()
        });
        AtomicJson(Path.Combine(_request.Artifact, "diagnostics", "flow-chest-isolation.json"), new
        {
            requestId = _request.RunId, scenarioId = Scenario, frames = _frames, loads = _loads, savingEvents = _savings, savedEvents = _saves,
            titleEvents = _titles, sessionIds = _sessions.ToArray(), worldIds = _worlds.Select(world => world.Id).ToArray(),
            parcelIds = _worlds.Select(world => world.Parcel).ToArray(), saveTreeHashes = _savedTrees, errors = _errors.ToArray()
        });
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _helper.Events.GameLoop.Saving -= OnSaving; _helper.Events.GameLoop.Saved -= OnSaved;
    }
}
