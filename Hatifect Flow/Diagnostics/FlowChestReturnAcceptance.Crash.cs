using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewValley;
using static Hatifect.Flow.Diagnostics.FlowHostAcceptance;

namespace Hatifect.Flow.Diagnostics;

internal sealed partial class FlowChestReturnAcceptance
{
    internal const string CrashScenario = "flow.chest.crash-after-return";
    private const string CrashMarkerPhase = "saved-returned";
    private string? _crashPhase;

    private void InitializeCrash()
    {
        if (_scenario != CrashScenario) return;
        _crashPhase = Environment.GetEnvironmentVariable("HATIFECT_TEST_CRASH_PHASE");
        Require(_crashPhase is "prepare" or "resume", "A returned crash needs a fixed executor phase.");
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
            "Resume marker does not identify this saved return candidate.");
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
        _wholeParcel = m.GetProperty("parcelId").GetGuid();
        _partialParcel = m.GetProperty("partialParcelId").GetGuid();
        _sessionId = m.GetProperty("sessionId").GetGuid();
        Require(_wholeParcel != Guid.Empty && _partialParcel != Guid.Empty && _wholeParcel != _partialParcel && _sessionId != Guid.Empty,
            "The saved crash marker has invalid parcel/session identities.");
        _sourceTile = new Vector2(m.GetProperty("sourceX").GetInt32(), m.GetProperty("sourceY").GetInt32());
        _destinationTile = new Vector2(m.GetProperty("destinationX").GetInt32(), m.GetProperty("destinationY").GetInt32());
        Require(_sourceTile != _destinationTile && _sourceTile.X is >= 5 and < 20 && _sourceTile.Y is >= 5 and < 20
            && _destinationTile.X is >= 5 and < 20 && _destinationTile.Y is >= 5 and < 20, "Invalid return crash station coordinates.");
        _remainderXml = m.GetProperty("remainderXml").GetString()!;
        Require(_remainderXml is { Length: > 0 and <= 32768 }, "Invalid saved remainder proof.");
        _loads = m.GetProperty("loads").GetInt32();
        _savings = m.GetProperty("savingEvents").GetInt32();
        _saves = m.GetProperty("savedEvents").GetInt32();
        Require(_loads == 4 && _savings == 4 && _saves == 4, "A return crash marker must follow the fourth confirmed save.");
        _boundary = 4;
        foreach (string check in Checks.Where(check => check is not ("no-duplication" or "taken-items"))) _passed.Add(check);
    }

    private void RestoreReturnFixture()
    {
        // The third chest and filler identities are derived from this fixed request, never supplied as extra authority.
        Vector2? holding = null;
        var farm = Game1.getFarm();
        for (int y = 5; y < 20; y++)
        for (int x = 5; x < 20; x++)
        {
            var tile = new Vector2(x, y);
            if (!farm.Objects.TryGetValue(tile, out var value)
                || !value.modData.TryGetValue("Hatifect.Flow/ReturnChest", out string role) || role != _request.RunId + ":holding") continue;
            Require(holding is null && value is StardewValley.Objects.Chest, "The holding chest is duplicated or replaced.");
            holding = tile;
        }
        Require(holding.HasValue && holding != _sourceTile && holding != _destinationTile, "The saved holding chest is missing or aliases a station.");
        _holdingTile = holding!.Value;
        _capacity = ChestAt(_sourceTile).GetActualCapacity();
        Require(_capacity is >= 4 and <= 128 && ChestAt(_destinationTile).GetActualCapacity() == _capacity
            && ChestAt(_holdingTile).GetActualCapacity() >= 4, "The resumed fixture has unsupported capacity.");
        InitializeFillers();
    }

    private void PrepareCrashBoundary()
    {
        Require(_loads == 4 && _savings == 4 && _saves == 4, "Controlled return crash did not reach the fourth confirmed save.");
        VerifyBoundary(4);
        Session().BeginSave(); _stage = Stage.AwaitCrash;
        AtomicJson(Path.Combine(_request.Artifact, "diagnostics", "flow-crash-ready.json"), new
        {
            formatVersion = 1, runId = _request.RunId, scenarioId = _scenario, phase = CrashMarkerPhase,
            pid = Environment.ProcessId, sessionId = _sessionId, saveName = Path.GetFileName(_request.SavePath), saveId = 4242424242UL,
            saveHash = HashFile(Path.Combine(_request.SavePath, Path.GetFileName(_request.SavePath))), runtimeFingerprint = _fingerprint,
            parcelId = _wholeParcel, partialParcelId = _partialParcel,
            sourceX = (int)_sourceTile.X, sourceY = (int)_sourceTile.Y, destinationX = (int)_destinationTile.X, destinationY = (int)_destinationTile.Y,
            remainderXml = _remainderXml, loads = _loads, savingEvents = _savings, savedEvents = _saves,
            capturedAtUtc = DateTimeOffset.UtcNow.ToString("O")
        });
    }
}
