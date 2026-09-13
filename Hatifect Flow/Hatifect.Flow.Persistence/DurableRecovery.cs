using System;
using System.IO;
using System.Linq;
using Hatifect.Flow.Application.Dispatch;
using Hatifect.Flow.Domain.Checkpoints;
using Hatifect.Flow.Domain.Ports;

namespace Hatifect.Flow.Infrastructure.Persistence;

// Compare all provider-owned state, including unchanged Stations and full receipt
// payloads. A newer provider is valid only for the one explicitly persisted intent.
internal static class DurableRecovery
{
    public static FlowCheckpoint Recover(DurableCoreImage core, DurableProviderImage actual)
    {
        FlowCheckpoint checkpoint = core.Core.Checkpoint;
        if (actual.PairId != core.PairId || actual.NetworkId != checkpoint.NetworkId)
            throw new InvalidDataException("Core and provider do not belong to the same durable pair.");
        var transfers = checkpoint.Transfers.ToDictionary(t => t.Key);
        DurableProviderReceipt[] receipts = checkpoint.Stations.SelectMany(s => s.Port.Receipts.Select(r =>
        {
            if (!transfers.TryGetValue(r.Key, out TransferCheckpoint? transfer) || transfer.StationId != s.Id)
                throw new InvalidDataException("Core receipt has no matching transfer payload.");
            return new DurableProviderReceipt(r.Key, transfer.CargoId, s.Id, transfer.Manifest, r.Result);
        })).ToArray();
        var expected = new DurableProviderImage(core.PairId, checkpoint.NetworkId,
            core.ProviderRevision, checkpoint.Stations, receipts, core.ProviderConfigurationRevision);
        FlowCheckpoint? provisioned = null;
        if (core.Capacity is not null)
        {
            ValidateCapacity(core, expected);
        }
        if (core.Provision is not null)
        {
            provisioned = ValidateProvision(core, expected);
        }
        if (core.Admission is not null)
        {
            StationCheckpoint? station = checkpoint.Stations.SingleOrDefault(s => s.Id == core.Admission.StationId);
            if (core.Intent is not null || core.Registration is not null || core.Provision is not null || core.Capacity is not null || station is null || (station.Port.AcceptDeposits == core.Admission.AcceptDeposits
                && station.Port.AcceptExtractions == core.Admission.AcceptExtractions))
            {
                throw new InvalidDataException("Admission intent must exclusively change an existing Station.");
            }
        }
        if (core.Registration is not null)
        {
            StationRegistrationIntent registration = core.Registration;
            if (core.Intent is not null || core.Admission is not null || core.Provision is not null || core.Capacity is not null || registration.StationId == Guid.Empty
                || checkpoint.Stations.Any(s => s.Id == registration.StationId)
                || checkpoint.Stations.Length >= checkpoint.Limits.MaxStations
                || registration.MaxCargoBatches <= 0 || registration.MaxCargoBatches > 65536
                || registration.MaxReceipts <= 0 || registration.MaxReceipts > 65536
                || expected.Revision >= 262144)
                throw new InvalidDataException("Registration intent must exclusively add a new bounded Station.");
            // Prove the complete proposed provider image, even before the effect committed.
            _ = DurableProviderCodec.Encode(ProjectRegistration(expected, registration));
        }
        if (core.Intent is not null && (!transfers.TryGetValue(core.Intent, out TransferCheckpoint? pending)
            || pending.Retired || !checkpoint.Parcels.Any(p => p.Id == core.Intent.ParcelId && p.PendingTransfer == core.Intent)
            || receipts.Any(r => r.Key == core.Intent)))
            throw new InvalidDataException("Core intent must identify an unseen active pending transfer.");
        if (actual.Revision != core.ProviderRevision)
        {
            if (actual.Revision != core.ProviderRevision + 1 || (core.Intent is null && core.Admission is null && core.Registration is null && core.Provision is null && core.Capacity is null))
            {
                throw new InvalidDataException("Provider revision is not acknowledged or one pending intent ahead.");
            }
            if (core.Admission is not null)
            {
                AdmissionIntent admission = core.Admission;
                expected = expected with
                {
                    Revision = expected.Revision + 1,
                    ConfigurationRevision = expected.ConfigurationRevision + 1,
                    Stations = expected.Stations.Select(s => s.Id == admission.StationId
                        ? s with { Port = s.Port with { AcceptDeposits = admission.AcceptDeposits,
                            AcceptExtractions = admission.AcceptExtractions } } : s).ToArray()
                };
            }
            else if (core.Registration is not null)
            {
                expected = ProjectRegistration(expected, core.Registration);
            }
            else if (core.Provision is not null)
            {
                expected = CargoProvisionProjection.Provider(expected, core.Provision);
                checkpoint = provisioned!;
            }
            else if (core.Capacity is not null)
            {
                expected = PortCapacityProjection.Provider(expected, core.Capacity);
            }
            else
            {
                expected = Project(expected, transfers[core.Intent!]);
            }
        }
        if (!Equivalent(expected, actual))
            throw new InvalidDataException("Provider state differs from its exact acknowledged or pending transition.");
        return checkpoint with { Stations = actual.Stations };
    }

    private static void ValidateCapacity(DurableCoreImage core, DurableProviderImage before)
    {
        PortCapacityIntent intent = core.Capacity!;
        FlowCheckpoint checkpoint = core.Core.Checkpoint;
        StationCheckpoint? station = checkpoint.Stations.SingleOrDefault(s => s.Id == intent.StationId);
        if (core.Intent is not null || core.Admission is not null || core.Registration is not null || core.Provision is not null
            || station is null || intent.MaxCargoBatches <= 0 || intent.MaxCargoBatches > 65536
            || intent.MaxReceipts <= 0 || intent.MaxReceipts > 65536
            || intent.MaxCargoBatches < station.Port.Inventory.Length || intent.MaxReceipts < station.Port.Receipts.Length
            || (intent.MaxCargoBatches == station.Port.MaxCargoBatches && intent.MaxReceipts == station.Port.MaxReceipts)
            || before.Revision >= 262144)
        {
            throw new InvalidDataException("Capacity intent must exclusively change an existing bounded Port.");
        }
        try
        {
            // Raising a limit must never repair an invalid baseline. Provider
            // bounds are stricter than the portable Core checkpoint bounds.
            _ = FlowRuntime.RestoreCheckpoint(checkpoint);
            _ = DurableProviderCodec.Encode(before);
            FlowCheckpoint projected = PortCapacityProjection.Core(checkpoint, intent);
            _ = FlowRuntime.RestoreCheckpoint(projected);
            DurableProviderImage next = PortCapacityProjection.Provider(before, intent);
            _ = DurableProviderCodec.Encode(next);
            _ = DurableCoreCodec.Encode(new DurableCoreImage(core.PairId, next.Revision, null,
                new CheckpointImage(checked(core.Core.Revision + 1), projected),
                ProviderConfigurationRevision: next.ConfigurationRevision));
        }
        catch (Exception error) when (error is ArgumentException or OverflowException)
        {
            throw new InvalidDataException("Capacity intent exceeds valid Core state or resource bounds.", error);
        }
    }

    private static FlowCheckpoint ValidateProvision(DurableCoreImage core, DurableProviderImage before)
    {
        CargoProvisionIntent intent = core.Provision!;
        FlowCheckpoint checkpoint = core.Core.Checkpoint;
        StationCheckpoint? station = checkpoint.Stations.SingleOrDefault(s => s.Id == intent.StationId);
        if (core.Intent is not null || core.Admission is not null || core.Registration is not null || core.Capacity is not null
            || intent.CargoId == Guid.Empty || intent.Manifest is null || station is null
            || checkpoint.Cargo.Any(cargo => cargo.Id == intent.CargoId)
            || checkpoint.Stations.Any(s => s.Port.Inventory.Any(cargo => cargo.CargoId == intent.CargoId))
            || before.Receipts.Any(receipt => receipt.CargoId == intent.CargoId)
            || checkpoint.Cargo.Length >= checkpoint.Limits.MaxParcels
            || station.Port.Inventory.Length >= station.Port.MaxCargoBatches || before.Revision >= 262144)
        {
            throw new InvalidDataException("Provision intent must exclusively introduce one new bounded batch.");
        }
        try
        {
            // An intent cannot repair invalid pre-existing cargo/claim lineage.
            _ = FlowRuntime.RestoreCheckpoint(checkpoint);
            FlowCheckpoint projected = CargoProvisionProjection.Core(checkpoint, intent);
            _ = FlowRuntime.RestoreCheckpoint(projected);
            DurableProviderImage next = CargoProvisionProjection.Provider(before, intent);
            _ = DurableProviderCodec.Encode(next);
            _ = DurableCoreCodec.Encode(new DurableCoreImage(core.PairId, next.Revision, null,
                new CheckpointImage(checked(core.Core.Revision + 1), projected),
                ProviderConfigurationRevision: next.ConfigurationRevision));
            return projected;
        }
        catch (Exception error) when (error is ArgumentException or OverflowException)
        {
            throw new InvalidDataException("Provision intent exceeds valid Core state or resource bounds.", error);
        }
    }

    private static DurableProviderImage ProjectRegistration(DurableProviderImage before, StationRegistrationIntent intent)
        => before with
        {
            Revision = before.Revision + 1,
            ConfigurationRevision = before.ConfigurationRevision + 1,
            Stations = before.Stations.Append(new StationCheckpoint(intent.StationId,
                new PortCheckpoint(intent.MaxCargoBatches, intent.MaxReceipts, intent.AcceptDeposits,
                    intent.AcceptExtractions, Array.Empty<InventoryCheckpoint>(), Array.Empty<ReceiptCheckpoint>()))).ToArray()
        };

    private static DurableProviderImage Project(DurableProviderImage before, TransferCheckpoint transfer)
    {
        StationCheckpoint station = before.Stations.Single(s => s.Id == transfer.StationId);
        PortCheckpoint port = station.Port;
        if (port.Receipts.Length >= port.MaxReceipts)
            throw new InvalidDataException("An exhausted provider journal cannot advance.");
        bool extract = transfer.Key.Kind == (int)PortTransferKind.Extract;
        InventoryCheckpoint? cargo = port.Inventory.SingleOrDefault(c => c.CargoId == transfer.CargoId);
        bool applied = extract
            ? port.AcceptExtractions && cargo?.Manifest == transfer.Manifest
            : port.AcceptDeposits && !before.Stations.Any(s => s.Port.Inventory.Any(c => c.CargoId == transfer.CargoId))
                && port.Inventory.Length < port.MaxCargoBatches;
        int result = (int)(applied ? PortResult.Applied : PortResult.Rejected);
        InventoryCheckpoint[] inventory = port.Inventory;
        if (applied)
            inventory = extract ? inventory.Where(c => c.CargoId != transfer.CargoId).ToArray()
                : inventory.Append(new InventoryCheckpoint(transfer.CargoId, transfer.Manifest)).ToArray();
        var next = station with { Port = port with { Inventory = inventory,
            Receipts = port.Receipts.Append(new ReceiptCheckpoint(transfer.Key, result)).ToArray() } };
        return before with { Revision = before.Revision + 1,
            Stations = before.Stations.Select(s => s.Id == next.Id ? next : s).ToArray(),
            Receipts = before.Receipts.Append(new DurableProviderReceipt(transfer.Key, transfer.CargoId,
                transfer.StationId, transfer.Manifest, result)).ToArray() };
    }

    private static bool Equivalent(DurableProviderImage expected, DurableProviderImage actual)
    {
        if (expected.Revision != actual.Revision || expected.ConfigurationRevision != actual.ConfigurationRevision || expected.Stations.Length != actual.Stations.Length
            || expected.Receipts.Length != actual.Receipts.Length) return false;
        var stations = actual.Stations.ToDictionary(s => s.Id);
        foreach (StationCheckpoint station in expected.Stations)
        {
            if (!stations.TryGetValue(station.Id, out StationCheckpoint? other)) return false;
            PortCheckpoint a = station.Port, b = other.Port;
            if (a.MaxCargoBatches != b.MaxCargoBatches || a.MaxReceipts != b.MaxReceipts
                || a.AcceptDeposits != b.AcceptDeposits || a.AcceptExtractions != b.AcceptExtractions
                || !a.Inventory.OrderBy(c => c.CargoId).SequenceEqual(b.Inventory.OrderBy(c => c.CargoId))
                || !a.Receipts.OrderBy(r => r.Key.ParcelId).ThenBy(r => r.Key.Kind).ThenBy(r => r.Key.Attempt)
                    .SequenceEqual(b.Receipts.OrderBy(r => r.Key.ParcelId).ThenBy(r => r.Key.Kind).ThenBy(r => r.Key.Attempt))) return false;
        }
        return expected.Receipts.OrderBy(r => r.Key.ParcelId).ThenBy(r => r.Key.Kind).ThenBy(r => r.Key.Attempt)
            .SequenceEqual(actual.Receipts.OrderBy(r => r.Key.ParcelId).ThenBy(r => r.Key.Kind).ThenBy(r => r.Key.Attempt));
    }
}
