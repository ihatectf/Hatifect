using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Hatifect.Flow.Application;
using Hatifect.Flow.Domain.Ports;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Objects;

namespace Hatifect.Flow.Inventory;

internal sealed class ChestInventoryAccess : IDisposable
{
    internal const string CargoKey = "Hatifect.Flow/Cargo";
    private readonly Func<Guid, Chest?> _resolve;
    private readonly Func<bool> _canMutate;
    private readonly long _playerId;
    private readonly FlowChestLocks _locks;
    private IDisposable? _portAccess;

    internal ChestInventoryAccess(Func<Guid, Chest?> resolve, Func<bool> canMutate, long playerId, FlowChestLocks? locks = null)
    { _resolve = resolve; _canMutate = canMutate; _playerId = playerId; _locks = locks ?? new FlowChestLocks(() => false, _ => { }); }

    internal IDisposable EnterChest(Chest chest) => _locks.TryAcquire(chest)
        ?? throw new InvalidOperationException("The chest is busy; retry after exclusive access becomes available.");
    internal IDisposable EnterStation(Guid station) => EnterChest(_resolve(station)
        ?? throw new InvalidOperationException("The station chest is unavailable."));

    internal bool TryEnterPort(Guid station)
    {
        _locks.RetryCleanup();
        EndPortAccess();
        if (!_locks.Required) return true;
        Chest? chest = _resolve(station);
        // Missing or unsupported endpoints remain a definite provider rejection. Contention waits.
        if (chest is null || !IsSupported(chest)) return true;
        _portAccess = _locks.TryAcquire(chest);
        return _portAccess is not null;
    }
    internal void EndPortAccess() { _portAccess?.Dispose(); _portAccess = null; }
    public void Dispose() { try { EndPortAccess(); } finally { _locks.Dispose(); } }

    internal static bool IsSupported(Chest chest)
        => chest.GetType() == typeof(Chest) && chest.playerChest.Value && !chest.fridge.Value && !chest.giftbox.Value
            && string.IsNullOrEmpty(chest.GlobalInventoryId)
            && chest.SpecialChestType is Chest.SpecialChestTypes.None or Chest.SpecialChestTypes.BigChest;

    internal Item ReadSource(Guid station, int slot)
    {
        Chest chest = GetAvailable(station) ?? throw new InvalidOperationException("The source chest is unavailable or busy.");
        IInventory inventory = chest.GetItemsForPlayer(_playerId);
        if (slot < 0 || slot >= inventory.Count || inventory[slot] is not Item item)
            throw new ArgumentException("The selected source slot is empty or outside the inventory.", nameof(slot));
        FlowItemCodec.RequireSupported(item);
        return item;
    }

    internal IReadOnlyList<FlowInventorySlot> ReadInventory(Guid station)
    {
        Chest? chest = GetAvailable(station);
        if (chest is null) return Array.Empty<FlowInventorySlot>();
        IInventory inventory = chest.GetItemsForPlayer(_playerId);
        var slots = new List<FlowInventorySlot>();
        // Supported ordinary/BigChest capacity is bounded; malformed inventories cannot expand the UI scan.
        for (int index = 0; index < Math.Min(128, inventory.Count); index++)
        {
            if (inventory[index] is not Item item || item.Stack is <= 0 or > 999) continue;
            try
            {
                slots.Add(new FlowInventorySlot(index, item.QualifiedItemId, item.Stack, Fingerprint(item),
                    item is StardewValley.Object obj ? obj.Quality.ToString(System.Globalization.CultureInfo.InvariantCulture) : ""));
            }
            catch (InvalidOperationException) { /* unsupported payloads cannot be offered for dispatch */ }
        }
        return slots.AsReadOnly();
    }

    internal static string Fingerprint(Item item) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FlowItemCodec.Encode(item))));

    internal PortResult Apply(PortTransfer transfer, string payload, int sourceQuantity = 0)
    {
        Chest? chest = GetAvailable(transfer.StationId.Value);
        if (chest is null) return PortResult.Rejected;
        if (_locks.Required && !_locks.Owns(chest))
            throw new InvalidOperationException("Physical multiplayer cargo transfer requires its own inventory lease.");
        IInventory inventory = chest.GetItemsForPlayer(_playerId);
        string cargo = transfer.CargoId.Value.ToString("D");
        if (transfer.Kind == PortTransferKind.Extract)
        {
            int match = -1;
            for (int index = 0; index < inventory.Count; index++)
            {
                Item? item = inventory[index];
                if (item is null || !item.modData.TryGetValue(CargoKey, out string token) || token != cargo) continue;
                if (match >= 0) return PortResult.Rejected;
                match = index;
            }
            if (match < 0) return PortResult.Rejected;
            Item original = inventory[match];
            Item? remainder = null;
            string? remainderXml = null;
            try
            {
                int expectedQuantity = sourceQuantity == 0 ? transfer.Manifest.Quantity : sourceQuantity;
                Item captured = FlowItemCodec.Decode(payload);
                if (expectedQuantity < transfer.Manifest.Quantity || expectedQuantity > 999
                    || captured.Stack != transfer.Manifest.Quantity || captured.QualifiedItemId != transfer.Manifest.ItemKey
                    || original.Stack != expectedQuantity || original.QualifiedItemId != transfer.Manifest.ItemKey)
                    return PortResult.Rejected;
                captured.Stack = expectedQuantity;
                if (!string.Equals(FlowItemCodec.Encode(original), FlowItemCodec.Encode(captured), StringComparison.Ordinal))
                    return PortResult.Rejected;
                if (expectedQuantity > transfer.Manifest.Quantity)
                {
                    // Prepare the entire remainder before one physical slot write. A later shipment gets its own cargo ID.
                    captured.Stack = expectedQuantity - transfer.Manifest.Quantity;
                    captured.modData.Remove(CargoKey);
                    remainder = captured;
                    remainderXml = FlowItemCodec.Encode(remainder);
                }
            }
            catch (InvalidOperationException) { return PortResult.Rejected; }
            try { inventory[match] = remainder; }
            catch
            {
                // Net inventory callbacks may throw after the single slot assignment. Observe that exact write.
                if (MatchesRemainder(inventory, match, remainder, remainderXml)) return PortResult.Applied;
                // Reentrant observers can move items elsewhere. Never turn an ambiguous write into Rejected.
                throw;
            }
            if (!MatchesRemainder(inventory, match, remainder, remainderXml))
                throw new InvalidOperationException("The extracted source remainder was changed by an inventory observer.");
            return PortResult.Applied;
        }
        Item delivered = FlowItemCodec.Decode(payload);
        if (delivered.Stack != transfer.Manifest.Quantity || delivered.QualifiedItemId != transfer.Manifest.ItemKey)
            throw new InvalidOperationException("Cargo payload does not match its manifest.");
        int empty = -1;
        int capacity = chest.GetActualCapacity();
        for (int index = 0; index < Math.Min(inventory.Count, capacity); index++)
        {
            if (inventory[index] is null) { empty = index; break; }
        }
        if (empty < 0 && inventory.Count >= capacity) return PortResult.Rejected;
        if (empty < 0)
        {
            // Add(item) can throw after writing a hidden slot but before incrementing Count.
            // Growth transfers no cargo; a failure is fenced by the game session and persisted for recovery.
            empty = inventory.Count;
            inventory.Add(null);
        }
        try { inventory[empty] = delivered; }
        catch
        {
            if (empty < inventory.Count && ReferenceEquals(inventory[empty], delivered)) return PortResult.Applied;
            throw;
        }
        return PortResult.Applied;
    }

    private Chest? GetAvailable(Guid station)
    {
        if (!_canMutate()) return null;
        Chest? chest = _resolve(station);
        return chest is not null && IsSupported(chest) && (!chest.GetMutex().IsLocked() || _locks.Owns(chest)) ? chest : null;
    }

    private static bool MatchesRemainder(IInventory inventory, int slot, Item? remainder, string? xml)
        => slot < inventory.Count && ReferenceEquals(inventory[slot], remainder)
            && (remainder is null || FlowItemCodec.Encode(remainder) == xml);
}
