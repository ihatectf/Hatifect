using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Hatifect.Flow.Application;
using static Hatifect.Flow.Diagnostics.FlowHostAcceptance;

namespace Hatifect.Flow.Diagnostics;

// Reads the UI owner's detached evidence contract, never runtime objects or private UI state.
internal sealed class FlowPlayerInputUiEvidence
{
    private readonly string _runId, _path;
    internal FlowPlayerInputUiEvidence(AcceptanceRequest request)
    {
        _runId = request.RunId;
        _path = Path.Combine(request.Artifact, "diagnostics", "ui-window-input-progress.json");
    }

    internal object? Stamp()
    {
        using JsonDocument? document = Read();
        if (document is null) return null;
        JsonElement latest = document.RootElement.GetProperty("latest");
        if (!HasFrame(latest)) return null;
        return new
        {
            epoch = latest.GetProperty("surfaceEpoch").GetInt64(),
            scene = latest.GetProperty("acceptedSceneVersion").GetInt64(),
            frame = latest.GetProperty("frameVersion").GetInt64(),
            completed = latest.GetProperty("completedFrame").GetInt64()
        };
    }

    internal bool HasInitialProbe()
    {
        using JsonDocument? document = Read();
        if (document is null || document.RootElement.GetProperty("phase").GetString() != "Complete") return false;
        JsonElement[] captures = document.RootElement.GetProperty("captures").EnumerateArray().ToArray();
        long epoch = -1, frame = -1;
        foreach (string phase in new[] { "Ready", "Pointer", "Text", "Backspace", "Tab" })
        {
            JsonElement[] matches = captures.Where(value => value.GetProperty("phase").GetString() == phase).ToArray();
            if (matches.Length != 1) return false;
            JsonElement state = matches[0].GetProperty("state");
            long currentEpoch = state.GetProperty("surfaceEpoch").GetInt64();
            long currentFrame = state.GetProperty("completedFrame").GetInt64();
            if (!HasFrame(state) || currentFrame <= frame || epoch != -1 && currentEpoch != epoch) return false;
            if (phase is "Pointer" or "Text" or "Backspace")
            {
                JsonElement focused = state.GetProperty("focused");
                if (focused.GetProperty("semanticId").GetString() != "Hatifect.Flow/network/field/name") return false;
            }
            epoch = currentEpoch; frame = currentFrame;
        }
        return true;
    }

    internal void RequireCompletedObserver()
    {
        using JsonDocument? document = Read();
        Require(document is not null && document.RootElement.GetProperty("phase").GetString() == "Complete",
            "The ordinary input observer did not finish its native probe.");
    }

    internal bool HasCommandResults(FlowPlayerCommandTrace[] commands, bool russian)
    {
        using JsonDocument? document = Read();
        if (document is null) return false;
        JsonElement[] captures = document.RootElement.GetProperty("captures").EnumerateArray().ToArray();
        FlowPlayerCommandTrace[] applied = commands.Where(value => value.Result?.Status == FlowCommandStatus.Applied).ToArray();
        if (applied.Length != 5) return false;
        for (int index = 0; index < applied.Length; index++)
        {
            string expectedResult = applied[index].Command is FlowSendCommand
                ? (russian ? "Отправление создано" : "Shipment created")
                : (russian ? "Команда выполнена" : "Command completed");
            using JsonDocument before = JsonDocument.Parse(applied[index].Before);
            JsonElement stamp = before.RootElement.GetProperty("ui");
            if (stamp.ValueKind == JsonValueKind.Null) return false;
            long epoch = stamp.GetProperty("epoch").GetInt64(), scene = stamp.GetProperty("scene").GetInt64();
            long ceiling = long.MaxValue;
            if (index + 1 < applied.Length)
            {
                using JsonDocument next = JsonDocument.Parse(applied[index + 1].Before);
                JsonElement nextStamp = next.RootElement.GetProperty("ui");
                if (nextStamp.ValueKind != JsonValueKind.Null && nextStamp.GetProperty("epoch").GetInt64() == epoch)
                    ceiling = nextStamp.GetProperty("scene").GetInt64();
            }
            if (!captures.Any(value =>
            {
                JsonElement state = value.GetProperty("state");
                long observed = state.GetProperty("acceptedSceneVersion").GetInt64();
                return state.GetProperty("surfaceEpoch").GetInt64() == epoch && observed > scene && observed <= ceiling
                    && HasVisible(state, "Hatifect.Flow/network/element/result", expectedResult);
            })) return false;
        }
        return true;
    }

    internal bool HasDeliveredHistory(Guid parcel, bool russian)
    {
        using JsonDocument? document = Read();
        return document is not null && HasVisible(document.RootElement.GetProperty("latest"),
            "Hatifect.Flow/network/element/history-detail", parcel + (russian ? " · Доставлено · попыток: 1" : " · Delivered · attempts: 1"));
    }

    private static bool HasVisible(JsonElement state, string semantic, string value)
    {
        if (!HasFrame(state) || !state.TryGetProperty("elements", out JsonElement elements)) return false;
        foreach (JsonElement element in elements.EnumerateArray())
        {
            if (element.GetProperty("semanticId").GetString() != semantic || element.GetProperty("value").GetString() != value) continue;
            JsonElement bounds = element.GetProperty("bounds"), clip = element.GetProperty("clip");
            double x = bounds.GetProperty("x").GetDouble(), y = bounds.GetProperty("y").GetDouble();
            double width = bounds.GetProperty("width").GetDouble(), height = bounds.GetProperty("height").GetDouble();
            return width > 0 && height > 0 && clip.GetProperty("x").GetDouble() <= x && clip.GetProperty("y").GetDouble() <= y
                && clip.GetProperty("right").GetDouble() >= bounds.GetProperty("right").GetDouble()
                && clip.GetProperty("bottom").GetDouble() >= bounds.GetProperty("bottom").GetDouble();
        }
        return false;
    }

    private static bool HasFrame(JsonElement state)
        => state.GetProperty("visible").GetBoolean()
            && state.GetProperty("experience").GetString() == "Hatifect.Flow/network"
            && state.GetProperty("acceptedSceneVersion").GetInt64() == state.GetProperty("renderedSceneVersion").GetInt64()
            && state.GetProperty("frameVersion").GetInt64() == state.GetProperty("renderedFrameVersion").GetInt64()
            && state.GetProperty("renderSequence").GetInt64() > 0;

    private JsonDocument? Read()
    {
        if (!File.Exists(_path)) return null;
        RejectLinks(_path);
        using var stream = File.OpenRead(_path);
        Require(stream.Length is > 0 and <= 16777216, "UI input evidence exceeded the Flow reader budget.");
        JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
        try
        {
            JsonElement root = document.RootElement;
            Require(root.GetProperty("protocolVersion").GetInt32() == 1 && root.GetProperty("runId").GetString() == _runId
                && root.GetProperty("scenario").GetString() == FlowPlayerNativeInputCapture.Scenario
                && root.GetProperty("declaredOrigin").GetString() == "os-injected"
                && root.GetProperty("expectedText").GetString() == "native-" + Guid.Parse(_runId).ToString("N")[..8]
                && root.GetProperty("failure").ValueKind == JsonValueKind.Null,
                "UI input evidence identity or observer failure rejected the run.");
            return document;
        }
        catch { document.Dispose(); throw; }
    }
}
