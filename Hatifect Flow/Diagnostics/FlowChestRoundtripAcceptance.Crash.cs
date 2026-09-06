using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Inventory;
using StardewValley;
using static Hatifect.Flow.Diagnostics.FlowHostAcceptance;

namespace Hatifect.Flow.Diagnostics;

internal sealed partial class FlowChestRoundtripAcceptance
{
    internal const string CrashScenario = "flow.chest.crash-after-save";
    internal const string DeliveryCrashScenario = "flow.chest.crash-after-delivery";
    internal const string ExtractionCrashScenario = "flow.chest.crash-after-unsaved-extraction";
    private string? _crashPhase;
    private string? _confirmedReservedSaveHash;
    private bool CrashAfterDelivery => _scenario == DeliveryCrashScenario;
    private bool CrashAfterUnsavedExtraction => _scenario == ExtractionCrashScenario;
    private int CrashSavedEvents => CrashAfterDelivery ? 2 : 1;
    private string CrashMarkerPhase => CrashAfterUnsavedExtraction ? "saved-reserved-unsaved-extraction"
        : CrashAfterDelivery ? "saved-delivered" : "saved-in-transit";
    private ParcelState CrashParcelState => CrashAfterDelivery ? ParcelState.Delivered : ParcelState.InTransit;

    private void InitializeCrash()
    {
        if (_scenario is not (CrashScenario or DeliveryCrashScenario or ExtractionCrashScenario)) return;
        _crashPhase = Environment.GetEnvironmentVariable("HATIFECT_TEST_CRASH_PHASE");
        Require(_crashPhase is "prepare" or "resume", "A controlled crash needs a fixed executor phase.");
        string markerPath = Path.Combine(_request.Artifact, "diagnostics", "flow-crash-ready.json");
        if (_crashPhase == "prepare")
        {
            Require(!File.Exists(markerPath), "A prepare process cannot reuse an existing crash marker.");
            return;
        }
        using JsonDocument marker = ReadBoundedJson(markerPath);
        JsonElement m = marker.RootElement;
        Require(m.GetProperty("formatVersion").GetInt32() == 1
            && m.GetProperty("runId").GetString() == _request.RunId
            && m.GetProperty("scenarioId").GetString() == _scenario
            && m.GetProperty("phase").GetString() == CrashMarkerPhase
            && m.GetProperty("saveId").GetUInt64() == 4242424242UL
            && m.GetProperty("saveName").GetString() == Path.GetFileName(_request.SavePath)
            && m.GetProperty("runtimeFingerprint").GetString() == _fingerprint
            && m.GetProperty("saveHash").GetString() == HashFile(Path.Combine(_request.SavePath, Path.GetFileName(_request.SavePath))),
            "Resume marker does not identify this saved runtime candidate.");
        int previousPid = m.GetProperty("pid").GetInt32();
        Require(previousPid > 0 && previousPid != Environment.ProcessId, "A crash resume requires a new process.");
        using JsonDocument termination = ReadBoundedJson(Path.Combine(_request.Artifact, "diagnostics", "flow-crash-termination.json"));
        JsonElement t = termination.RootElement;
        Require(t.GetProperty("formatVersion").GetInt32() == 1
            && t.GetProperty("requestId").GetString() == _request.RunId
            && t.GetProperty("scenarioId").GetString() == _scenario
            && t.GetProperty("pid").GetInt32() == previousPid
            && t.GetProperty("processGroup").GetInt32() == previousPid
            && t.GetProperty("requestedSignal").GetInt32() == 9 && t.GetProperty("rawExitCode").GetInt32() == -9,
            "Resume requires the executor's actual owned SIGKILL evidence.");
        _parcel = m.GetProperty("parcelId").GetGuid();
        _partialParcel = m.GetProperty("partialParcelId").GetGuid();
        _sessionId = m.GetProperty("sessionId").GetGuid();
        Require(_parcel != Guid.Empty && _partialParcel != Guid.Empty && _parcel != _partialParcel && _sessionId != Guid.Empty,
            "The saved crash marker has invalid parcel/session identities.");
        _sourceTile = new Vector2(m.GetProperty("sourceX").GetInt32(), m.GetProperty("sourceY").GetInt32());
        _destinationTile = new Vector2(m.GetProperty("destinationX").GetInt32(), m.GetProperty("destinationY").GetInt32());
        Require(_sourceTile != _destinationTile && _sourceTile.X is >= 0 and <= 10000 && _sourceTile.Y is >= 0 and <= 10000
            && _destinationTile.X is >= 0 and <= 10000 && _destinationTile.Y is >= 0 and <= 10000, "Invalid crash station coordinates.");
        _remainderXml = m.GetProperty("remainderXml").GetString()!;
        Require(_remainderXml is { Length: > 0 and <= 32768 }, "Invalid saved remainder proof.");
        _loads = m.GetProperty("loads").GetInt32();
        _savings = m.GetProperty("savingEvents").GetInt32();
        _saves = m.GetProperty("savedEvents").GetInt32();
        Require(_loads == CrashSavedEvents && _savings == CrashSavedEvents && _saves == CrashSavedEvents,
            "A crash marker must follow this scenario's exact confirmed save boundary.");
        foreach (string check in new[] { "loaded", "custody", "saving", "saved" }) _passed.Add(check);
        if (CrashAfterDelivery)
            foreach (string check in new[] { "reload", "delivery", "lifecycle" }) _passed.Add(check);
    }

    private void PrepareCrashBoundary()
    {
        Require(_loads == CrashSavedEvents && _savings == CrashSavedEvents && _saves == CrashSavedEvents && BothAt(CrashParcelState),
            "Controlled crash did not reach the required confirmed save boundary.");
        VerifyCrashInventory();
        string saveHash = HashFile(Path.Combine(_request.SavePath, Path.GetFileName(_request.SavePath)));
        if (CrashAfterUnsavedExtraction)
            Require(_confirmedReservedSaveHash == saveHash, "Unsaved extraction changed the confirmed Reserved save bytes.");
        // Saved has completed. Hold the existing host barrier so no new transport effect runs before SIGKILL.
        Session().BeginSave();
        _stage = Stage.AwaitCrash;
        AtomicJson(Path.Combine(_request.Artifact, "diagnostics", "flow-crash-ready.json"), new
        {
            formatVersion = 1, runId = _request.RunId, scenarioId = _scenario, phase = CrashMarkerPhase,
            pid = Environment.ProcessId, sessionId = _sessionId, saveName = Path.GetFileName(_request.SavePath), saveId = 4242424242UL,
            saveHash, runtimeFingerprint = _fingerprint,
            parcelId = _parcel, partialParcelId = _partialParcel,
            sourceX = (int)_sourceTile.X, sourceY = (int)_sourceTile.Y, destinationX = (int)_destinationTile.X, destinationY = (int)_destinationTile.Y,
            remainderXml = _remainderXml, loads = _loads, savingEvents = _savings, savedEvents = _saves,
            capturedAtUtc = DateTimeOffset.UtcNow.ToString("O")
        });
    }

    private void VerifyReservedSource()
    {
        Require(BothAt(ParcelState.Reserved), "The confirmed queue must contain both Reserved parcels before extraction.");
        Item[] source = Items(_sourceTile);
        Require(source.Length == 2 && Items(_destinationTile).Length == 0,
            "The Reserved save did not retain both full source stacks and an empty destination.");
        foreach (bool partial in new[] { false, true })
        {
            Item actual = source.Single(item => item.modData.ContainsKey("Hatifect.Flow/PartialAcceptance") == partial);
            Item expected = FlowItemCodec.Decode(_remainderXml);
            expected.Stack = partial ? 13 : 8;
            if (!partial) expected.modData.Remove("Hatifect.Flow/PartialAcceptance");
            Guid parcelId = partial ? _partialParcel : _parcel;
            expected.modData[ChestInventoryAccess.CargoKey] = Session().ReadSnapshot().Parcels.Single(parcel => parcel.Id == parcelId).CargoId.ToString("D");
            VerifyWine(actual, expected.Stack);
            Require(FlowItemCodec.Encode(actual) == FlowItemCodec.Encode(expected),
                "The Reserved source changed full item metadata, quantity or assigned cargo identity.");
        }
    }

    private void VerifyCrashInventory()
    {
        if (CrashAfterDelivery) VerifyDelivery();
        else
        {
            VerifyRemainder();
            Require(Items(_destinationTile).Length == 0, "Cargo appeared before the confirmed crash boundary.");
        }
    }
}
