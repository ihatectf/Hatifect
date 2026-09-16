using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hatifect.Flow.Application;
using Hatifect.Flow.Application.Dispatch;
using Hatifect.Flow.Application.Planning;
using Hatifect.Flow.Diagnostics;
using Hatifect.Flow.Domain.Checkpoints;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Policies;
using Hatifect.Flow.Domain.Ports;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Infrastructure.Persistence;
using Hatifect.Flow.Inventory;
using StardewValley;
using StardewValley.Objects;

namespace Hatifect.Flow.Sessions;

// One main-thread, single-player owner. Physical items and this session commit in one game save.
internal sealed partial class FlowGameSession : IDisposable
{
    internal const string SaveKey = "flowline-v1";
    // Persisted in chest modData before the product rename; keep the wire key stable.
    internal const string StationKey = "Hatifect.Flow/Station";
    internal const int MaxStations = 32;
    internal const int MaxLinks = 128;
    internal const int MaxCargo = 256;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly ulong _saveId;
    private readonly Func<bool> _canMutate;
    private readonly Func<StationBinding, Chest?> _resolve;
    private readonly Action<Exception> _report;
    private readonly FlowRuntime _runtime;
    private readonly ChestInventoryAccess _inventory;
    private readonly Func<StationId, bool> _portReady;
    private readonly Dictionary<Guid, StationBinding> _stations = new();
    private readonly Dictionary<Guid, CargoPayload> _payloads = new();
    private readonly Dictionary<Guid, SaveBoundCargoPort> _ports = new();
    private bool _saving;
    private bool _closed;
    private bool _faulted;
    private bool _hostOperation;
    private long _tickInvocations, _processedOperations, _tickRefreshRequests, _checkpointCaptures, _physicalApplyCalls;
    private int _lastTickProcessedOperations;

    internal FlowGameSession(ulong saveId, long playerId, Func<bool> canMutate,
        Func<StationBinding, Chest?> resolve, Action<Exception> report, FlowGameSave? saved = null, FlowChestLocks? locks = null)
    {
        if (saveId == 0) throw new ArgumentException("A loaded save identity is required.", nameof(saveId));
        _saveId = saveId;
        _canMutate = canMutate;
        _resolve = resolve;
        _report = report;
        _inventory = new ChestInventoryAccess(ResolveChest, CanMutate, playerId, locks);
        _portReady = station => _inventory.TryEnterPort(station.Value);
        FlowCheckpoint? checkpoint = saved is null ? null : ValidateSave(saved, saveId);
        _runtime = checkpoint is null
            ? new FlowRuntime(new NetworkId(Guid.NewGuid()), Limits())
            : FlowRuntime.RestoreCheckpoint(checkpoint);
        if (saved is not null)
        {
            _faulted = saved.RequiresRecovery;
            foreach (StationBinding station in saved.Stations) _stations.Add(station.Id, station);
            Dictionary<Guid, int>? legacyQuantities = saved.Version == 1
                ? checkpoint!.Cargo.ToDictionary(cargo => cargo.Id, cargo => cargo.Manifest.Quantity) : null;
            foreach (CargoPayload payload in saved.Payloads)
                _payloads.Add(payload.Id, legacyQuantities is null ? payload : payload with { SourceQuantity = legacyQuantities[payload.Id] });
        }
        _runtime.AttachCheckpointOwner(this);
        if (checkpoint is not null)
        {
            var journals = checkpoint.Stations.ToDictionary(station => station.Id, station => station.Port);
            _runtime.AttachSavePorts(this, station => CreatePort(station.Value, journals[station.Value]));
        }
        Application = new FlowApplication(_runtime, Execute, report, () => CanMutate() && !_hostOperation && !_managing,
            FlowProviderMode.GameInventory);
        UpdateAvailability();
    }

    internal FlowApplication Application { get; }
    internal long Now => _runtime.Now;
    internal bool IsSaving => _saving;
    internal bool IsFaulted => _faulted;
    internal FlowTickCounters TickCounters => new(_tickInvocations, _runtime.Now, _runtime.PendingOperationCount,
        _runtime.RouteSearchCount, _processedOperations, _lastTickProcessedOperations, _tickRefreshRequests,
        _checkpointCaptures, _physicalApplyCalls);
    internal void FencePeerFailure(Exception error)
    {
        RequireOwnerIdle();
        _faulted = true;
        UpdateAvailability();
        _report(error);
    }
    internal string StationName(Guid id) => _stations.TryGetValue(id, out StationBinding? station) ? station.Name : "?";

    internal StationBinding RegisterStation(string name, string location, int x, int y, Chest chest)
    {
        using HostOperation operation = EnterHostMutation();
        var binding = new StationBinding(Guid.NewGuid(), name, location, x, y);
        ValidateStation(binding);
        if (_stations.Count >= MaxStations) throw RejectResource(FlowAdmissionResource.Stations, _stations.Count, MaxStations);
        if (_stations.Values.Any(station => string.Equals(station.Name, name, StringComparison.OrdinalIgnoreCase))
            || _stations.Values.Any(station => station.Location == location && station.X == x && station.Y == y)
            || !ChestInventoryAccess.IsSupported(chest) || chest.GetMutex().IsLocked()
            || !ReferenceEquals(_resolve(binding), chest)
            || chest.modData.TryGetValue(StationKey, out string previous) && Guid.TryParse(previous, out Guid old) && _stations.ContainsKey(old))
            throw new InvalidOperationException("Station name, chest or station capacity is unavailable.");
        using IDisposable access = _inventory.EnterChest(chest);
        _stations.EnsureCapacity(_stations.Count + 1);
        _ports.EnsureCapacity(_ports.Count + 1);
        _runtime.AddStation(new StationId(binding.Id), CreatePort(binding.Id));
        _stations.Add(binding.Id, binding);
        chest.modData[StationKey] = binding.Id.ToString("D");
        Application.Refresh();
        return binding;
    }

    internal Guid Link(string source, string destination, int capacity = 999, long transitTicks = 180)
    {
        using HostOperation operation = EnterHostMutation();
        StationBinding from = FindStation(source), to = FindStation(destination);
        if (from.Id == to.Id || capacity is <= 0 or > 999 || transitTicks is <= 0 or > 36000)
            throw new ArgumentException("A link needs different stations, capacity 1..999 and travel time 1..36000 ticks.");
        if (_runtime.RetainedLinkCount >= MaxLinks) throw RejectResource(FlowAdmissionResource.LifetimeLinks, _runtime.RetainedLinkCount, MaxLinks);
        Guid id = Guid.NewGuid();
        _runtime.AddLink(new LinkId(id), new StationId(from.Id), new StationId(to.Id), capacity, transitTicks);
        Application.Refresh();
        return id;
    }

    internal Guid Send(string source, string destination, int slot)
    {
        try { return SendCore(source, destination, slot, null)!.Value; }
        catch (CommandAdmissionFailure error)
        { throw new InvalidOperationException(error.Message, error); }
    }

    private Guid? SendCore(string source, string destination, int slot, string? expectedFingerprint, int? quantity = null)
    {
        using HostOperation operation = EnterHostMutation();
        StationBinding from = FindStation(source), to = FindStation(destination);
        if (_payloads.Count >= MaxCargo) throw RejectResource(FlowAdmissionResource.RetainedCargo, _payloads.Count, MaxCargo);
        ValidateRouteExists(from, to);
        using IDisposable access = EnterSource(from.Id);
        Item item = ReadSelectedSource(from.Id, slot);
        if (expectedFingerprint is not null
            && !MatchesSelectedFingerprint(item, expectedFingerprint))
            return null;
        (int sourceQuantity, int units) = ValidateAndResolveQuantity(item, quantity);
        EnsureNoConflictingPendingOperation(item);
        Guid cargo = Guid.NewGuid(), shipment = Guid.NewGuid(), parcel = Guid.NewGuid();
        (string payload, string taggedSourceXml) = BuildRoundTrippedCargoPayload(item, units, sourceQuantity, cargo);
        var manifest = new CargoManifest(item.QualifiedItemId, units);
        RegisterCargoShipmentAndParcel(from.Id, to.Id, cargo, shipment, parcel, manifest, payload, sourceQuantity);
        TagSourceWithCargoOrFault(from.Id, slot, item, cargo, taggedSourceXml);
        Application.Refresh();
        return parcel;
    }

    private void ValidateRouteExists(StationBinding from, StationBinding to)
    {
        RouteStatus route = from.Id == to.Id ? RouteStatus.NoRoute
            : _runtime.PlanRoute(new StationId(from.Id), new StationId(to.Id)).Status;
        if (route != RouteStatus.Found)
            throw new CommandAdmissionFailure(route == RouteStatus.SearchLimitExceeded
                ? FlowRejectionCode.RouteSearchLimit : FlowRejectionCode.RouteUnavailable);
    }

    private static (int SourceQuantity, int Units) ValidateAndResolveQuantity(Item item, int? quantity)
    {
        if (item.Stack > 999) throw new CommandAdmissionFailure(FlowRejectionCode.StateChanged);
        int sourceQuantity = item.Stack, units = quantity ?? sourceQuantity;
        if (units <= 0 || units > sourceQuantity)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be between one and the complete source stack.");
        return (sourceQuantity, units);
    }

    private void EnsureNoConflictingPendingOperation(Item item)
    {
        item.modData.TryGetValue(ChestInventoryAccess.CargoKey, out string oldToken);
        if (Guid.TryParse(oldToken, out Guid previous) && Application.ReadSnapshot().Parcels.Any(parcel => parcel.CargoId == previous
            && parcel.State is not (ParcelState.Cancelled or ParcelState.Delivered or ParcelState.Returned)))
            throw new CommandAdmissionFailure(FlowRejectionCode.OperationPending);
    }

    private static (string Payload, string TaggedSourceXml) BuildRoundTrippedCargoPayload(Item item, int units, int sourceQuantity, Guid cargo)
    {
        string sourceXml = FlowItemCodec.Encode(item);
        Item captured = FlowItemCodec.Decode(sourceXml);
        if (FlowItemCodec.Encode(captured) != sourceXml)
            throw new InvalidOperationException("The item's saved representation does not round-trip exactly.");
        captured.modData[ChestInventoryAccess.CargoKey] = cargo.ToString("D");
        string taggedSourceXml = FlowItemCodec.Encode(captured);
        captured.Stack = units;
        string payload = FlowItemCodec.Encode(captured);
        Item restored = FlowItemCodec.Decode(payload);
        if (FlowItemCodec.Encode(restored) != payload)
            throw new InvalidOperationException("The selected cargo does not round-trip exactly.");
        restored.Stack = sourceQuantity;
        if (FlowItemCodec.Encode(restored) != taggedSourceXml)
            throw new InvalidOperationException("The selected cargo cannot prove the complete source representation.");
        return (payload, taggedSourceXml);
    }

    private void RegisterCargoShipmentAndParcel(Guid fromId, Guid toId, Guid cargo, Guid shipment, Guid parcel,
        CargoManifest manifest, string payload, int sourceQuantity)
    {
        _payloads.Add(cargo, new CargoPayload(cargo, payload) { SourceQuantity = sourceQuantity });
        _ports[fromId].Register(new CargoId(cargo), manifest);
        _runtime.RegisterCargo(new CargoId(cargo), new StationId(fromId), manifest);
        _runtime.CreateShipment(new ShipmentId(shipment), new StationId(fromId), new StationId(toId), manifest,
            new ServicePolicy(ServiceClass.Standard, DeliveryGuarantee.Reserved));
        _runtime.SplitShipment(new ShipmentId(shipment), new ParcelId(parcel), new CargoId(cargo));
        _runtime.TryReserve(new ParcelId(parcel));
    }

    private void TagSourceWithCargoOrFault(Guid fromId, int slot, Item item, Guid cargo, string taggedSourceXml)
    {
        try
        {
            item.modData[ChestInventoryAccess.CargoKey] = cargo.ToString("D");
            if (!ReferenceEquals(_inventory.ReadSource(fromId, slot), item) || FlowItemCodec.Encode(item) != taggedSourceXml)
                throw new InvalidOperationException("Source admission was changed by an inventory observer.");
        }
        catch
        {
            // The admitted aggregate remains saveable even if a metadata observer throws.
            // No quantity has left the physical source; preserve evidence and prohibit dispatch.
            _faulted = true;
            UpdateAvailability();
            throw;
        }
    }

    private IDisposable EnterSource(Guid station)
    {
        try { return _inventory.EnterStation(station); }
        catch (FlowInventoryUnavailableException)
        { throw new CommandAdmissionFailure(FlowRejectionCode.ProviderUnavailable); }
    }

    private Item ReadSelectedSource(Guid station, int slot)
    {
        try { return _inventory.ReadSource(station, slot); }
        catch (FlowInventoryUnavailableException)
        { throw new CommandAdmissionFailure(FlowRejectionCode.ProviderUnavailable); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { throw new CommandAdmissionFailure(FlowRejectionCode.StateChanged); }
    }

    private static bool MatchesSelectedFingerprint(Item item, string expected)
    {
        try { return string.Equals(expected, ChestInventoryAccess.Fingerprint(item), StringComparison.Ordinal); }
        catch (InvalidOperationException)
        { throw new CommandAdmissionFailure(FlowRejectionCode.StateChanged); }
    }

    internal void Tick(bool timePasses)
    {
        RequireOwnerIdle();
        _tickInvocations++;
        _lastTickProcessedOperations = 0;
        if (_closed) return;
        UpdateAvailability();
        if (!timePasses || !CanMutate()) return;
        try
        {
            using HostOperation operation = EnterHostMutation();
            // No scans, snapshots, serialization or I/O on idle ticks. Queue work is bounded by 64 operations.
            try
            {
                _lastTickProcessedOperations = _runtime.AdvanceTo(checked(_runtime.Now + 1), canAccessPort: _portReady);
                _processedOperations += _lastTickProcessedOperations;
                if (_lastTickProcessedOperations > 0)
                {
                    _tickRefreshRequests++;
                    Application.Refresh();
                }
            }
            finally { _inventory.EndPortAccess(); }
        }
        catch (Exception error)
        {
            _faulted = true;
            UpdateAvailability();
            _report(error);
        }
    }

    internal FlowGameSave BeginSave()
    {
        RequireOwnerIdle();
        if (_closed) throw new InvalidOperationException("The game session is closed.");
        _saving = true;
        UpdateAvailability();
        // Faulted transfers remain saveable. Reload retains the journal AND the recovery fence.
        var saved = new FlowGameSave(2, _saveId, CheckpointCodec.Encode(new CheckpointImage(0, _runtime.CaptureCheckpoint())),
            _stations.Values.OrderBy(station => station.Id).ToArray(), _payloads.Values.OrderBy(payload => payload.Id).ToArray(), _faulted);
        _checkpointCaptures++;
        return saved;
    }

    internal void EndSave() { RequireOwnerIdle(); if (!_closed) { _saving = false; UpdateAvailability(); } }

    public void Dispose()
    {
        RequireOwnerIdle();
        if (_closed) { _inventory.Dispose(); return; }
        _closed = true;
        Application.Dispose();
        try { _runtime.FenceCheckpointOwner(this); }
        finally { _inventory.Dispose(); }
    }

    private void Execute(Action<FlowRuntime> command)
    {
        using HostOperation operation = EnterHostMutation();
        try { command(_runtime); }
        catch { _faulted = true; throw; }
    }

    private SaveBoundCargoPort CreatePort(Guid station, PortCheckpoint? checkpoint = null)
    {
        var port = new SaveBoundCargoPort(transfer =>
        {
            CargoPayload payload = _payloads[transfer.CargoId.Value];
            _physicalApplyCalls++;
            return _inventory.Apply(transfer, payload.Xml, payload.SourceQuantity);
        }, checkpoint);
        _ports.Add(station, port);
        return port;
    }

    private Chest? ResolveChest(Guid id)
    {
        if (!_stations.TryGetValue(id, out StationBinding? binding)) return null;
        Chest? chest = _resolve(binding);
        return chest is not null && chest.modData.TryGetValue(StationKey, out string value)
            && value == id.ToString("D") ? chest : null;
    }

    private StationBinding FindStation(string name) => _stations.Values.FirstOrDefault(
        station => string.Equals(station.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException("Unknown station: " + name, nameof(name));
    private bool CanMutate() => !_closed && !_saving && !_faulted && _canMutate();
    private void UpdateAvailability() => Application.SetAvailability(_faulted ? FlowApplicationState.RecoveryRequired
        : _saving || !_canMutate() ? FlowApplicationState.Paused : FlowApplicationState.Active);
    private HostOperation EnterHostMutation()
    {
        RequireThread();
        if (_hostOperation || !CanMutate()) throw new InvalidOperationException("Flow needs an idle active single-player session outside saving.");
        _hostOperation = true;
        return new HostOperation(this);
    }
    private void RequireOwnerIdle()
    {
        RequireThread();
        if (_hostOperation || _managing) throw new InvalidOperationException("Game-session lifecycle cannot reenter an active command.");
    }
    private readonly struct HostOperation : IDisposable
    {
        private readonly FlowGameSession _owner;
        internal HostOperation(FlowGameSession owner) => _owner = owner;
        public void Dispose() => _owner._hostOperation = false;
    }
    private void RequireThread() { if (_thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Flow game sessions require their owning thread."); }
    private static FlowLimits Limits() => new(maxStations: MaxStations, maxLinks: MaxLinks, maxParcels: MaxCargo,
        maxRouteVisits: MaxStations, maxRoutePlans: 64, maxDeliveryAttempts: 16, maxCargoUnits: 999, maxPendingOperations: MaxCargo);

    private static void ValidateStation(StationBinding station)
    {
        if (station is null || station.Id == Guid.Empty || string.IsNullOrEmpty(station.Name) || station.Name.Length > 32
            || !station.Name.All(character => char.IsLetterOrDigit(character) || character is '_' or '-')
            || string.IsNullOrEmpty(station.Location) || station.Location.Length > 256
            || station.X is < 0 or > 10000 || station.Y is < 0 or > 10000)
            throw new InvalidDataException("Invalid saved station binding.");
    }

    private static FlowCheckpoint ValidateSave(FlowGameSave saved, ulong saveId)
    {
        ValidateShape(saved, saveId);
        FlowCheckpoint checkpoint = DecodeCheckpointWithinLimits(saved);
        ValidateStations(saved, checkpoint);
        Dictionary<Guid, Item> payloads = ValidatePayloads(saved);
        ValidateCargoAgainstPayloads(checkpoint, payloads);
        return checkpoint;
    }

    private static void ValidateShape(FlowGameSave saved, ulong saveId)
    {
        if (saved.Version is not (1 or 2) || saved.SaveId != saveId || saved.Checkpoint is null
            || saved.Stations is null || saved.Stations.Length > MaxStations || saved.Payloads is null || saved.Payloads.Length > MaxCargo)
            throw new InvalidDataException("Unsupported or foreign Flow game-save aggregate.");
    }

    private static FlowCheckpoint DecodeCheckpointWithinLimits(FlowGameSave saved)
    {
        FlowCheckpoint checkpoint = CheckpointCodec.Decode(saved.Checkpoint).Checkpoint;
        var limits = Limits();
        var expected = new LimitsCheckpoint(limits.MaxStations, limits.MaxLinks, limits.MaxParcels, limits.MaxRouteVisits,
            limits.MaxRoutePlans, limits.MaxDeliveryAttempts, limits.MaxEvents, limits.MaxOperationsPerAdvance,
            limits.MaxCargoUnits, limits.MaxPendingOperations);
        if (checkpoint.Limits != expected) throw new InvalidDataException("Unsupported Flow game-save bounds.");
        return checkpoint;
    }

    private static void ValidateStations(FlowGameSave saved, FlowCheckpoint checkpoint)
    {
        var stations = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var locations = new HashSet<(string, int, int)>();
        foreach (StationBinding station in saved.Stations)
        {
            ValidateStation(station);
            if (!stations.Add(station.Id) || !names.Add(station.Name) || !locations.Add((station.Location, station.X, station.Y)))
                throw new InvalidDataException("Duplicate saved station binding.");
        }
        if (!stations.SetEquals(checkpoint.Stations.Select(station => station.Id)))
            throw new InvalidDataException("Station bindings do not match the transport checkpoint.");
    }

    private static Dictionary<Guid, Item> ValidatePayloads(FlowGameSave saved)
    {
        var payloads = new Dictionary<Guid, Item>();
        foreach (CargoPayload payload in saved.Payloads)
        {
            if (payload is null || payload.Id == Guid.Empty || !payloads.TryAdd(payload.Id, FlowItemCodec.Decode(payload.Xml)))
                throw new InvalidDataException("Invalid or duplicate cargo payload.");
            if (saved.Version == 1 ? payload.SourceQuantity != 0
                : payload.SourceQuantity < payloads[payload.Id].Stack || payload.SourceQuantity > 999)
                throw new InvalidDataException("Invalid captured source quantity for the game-save version.");
        }
        return payloads;
    }

    private static void ValidateCargoAgainstPayloads(FlowCheckpoint checkpoint, Dictionary<Guid, Item> payloads)
    {
        if (payloads.Count != checkpoint.Cargo.Length) throw new InvalidDataException("Cargo payload set does not match the checkpoint.");
        foreach (CargoCheckpoint cargo in checkpoint.Cargo)
        {
            if (!payloads.TryGetValue(cargo.Id, out Item? item) || item.QualifiedItemId != cargo.Manifest.ItemKey || item.Stack != cargo.Manifest.Quantity
                || !item.modData.TryGetValue(ChestInventoryAccess.CargoKey, out string token) || token != cargo.Id.ToString("D"))
                throw new InvalidDataException("Cargo payload does not match its authoritative identity and manifest.");
        }
    }
}
