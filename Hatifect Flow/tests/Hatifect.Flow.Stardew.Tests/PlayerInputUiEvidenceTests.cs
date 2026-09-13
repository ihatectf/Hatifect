using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hatifect.Flow.Application;
using Hatifect.Flow.Diagnostics;
using Xunit;
using static Hatifect.Flow.Diagnostics.FlowHostAcceptance;

namespace Hatifect.Flow.Stardew.Tests;

[Trait("Category", "flow")]
public sealed class PlayerInputUiEvidenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteProtocolOneEvidenceAcceptsInitialProbeFiveTypedResultsAndDeliveredHistory(bool russian)
    {
        using var fixture = new EvidenceFixture(russian);

        JsonElement stamp = JsonSerializer.SerializeToElement(fixture.Reader.Stamp());
        Assert.Equal(UiEpoch, stamp.GetProperty("epoch").GetInt64());
        Assert.Equal(70, stamp.GetProperty("scene").GetInt64());
        Assert.Equal(70, stamp.GetProperty("frame").GetInt64());
        Assert.Equal(70, stamp.GetProperty("completed").GetInt64());
        Assert.True(fixture.Reader.HasInitialProbe());
        Assert.True(fixture.Reader.HasCommandResults(fixture.Commands, russian));
        Assert.True(fixture.Reader.HasDeliveredHistory(fixture.Parcel, russian));
        fixture.Reader.RequireCompletedObserver();
    }

    [Theory]
    [InlineData("wrong-run")]
    [InlineData("observer-failure")]
    [InlineData("wrong-expected-text")]
    public void IdentityExpectedTextAndObserverFailureRejectTheDetachedRun(string mutation)
    {
        using var fixture = new EvidenceFixture();
        switch (mutation)
        {
            case "wrong-run": fixture.Document["runId"] = Guid.NewGuid().ToString("D"); break;
            case "observer-failure": fixture.Document["failure"] = "observer failed after capture"; break;
            case "wrong-expected-text": fixture.Document["expectedText"] = "native-deadbeef"; break;
        }
        fixture.Write();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => fixture.Reader.Stamp());

        Assert.Equal("UI input evidence identity or observer failure rejected the run.", error.Message);
    }

    [Fact]
    public void CompletedObserverRejectsAnIncompleteTerminalPhase()
    {
        using var fixture = new EvidenceFixture();
        fixture.Document["phase"] = "Text";
        fixture.Write();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(fixture.Reader.RequireCompletedObserver);

        Assert.Equal("The ordinary input observer did not finish its native probe.", error.Message);
    }

    [Theory]
    [InlineData("incomplete-phase")]
    [InlineData("wrong-field")]
    [InlineData("foreign-epoch")]
    public void InitialProbeRejectsIncompleteWrongFieldOrCrossEpochEvidence(string mutation)
    {
        using var fixture = new EvidenceFixture();
        switch (mutation)
        {
            case "incomplete-phase": fixture.Document["phase"] = "Backspace"; break;
            case "wrong-field": State(Capture(fixture.Document, "Text"))["focused"]!.AsObject()["semanticId"] =
                "Hatifect.Flow/network/field/capacity"; break;
            case "foreign-epoch": State(Capture(fixture.Document, "Tab"))["surfaceEpoch"] = UiEpoch + 1; break;
        }
        fixture.Write();

        Assert.False(fixture.Reader.HasInitialProbe());
    }

    [Theory]
    [InlineData("experience")]
    [InlineData("scene")]
    [InlineData("frame")]
    public void ResultRejectsForeignExperienceOrMismatchedRenderedSceneOrFrame(string mutation)
    {
        using var fixture = new EvidenceFixture();
        JsonObject state = State(ResultCapture(fixture.Document, 20));
        if (mutation == "experience") state["experience"] = "Hatifect.Flow/parcel";
        else if (mutation == "scene") state["renderedSceneVersion"] = 19;
        else state["renderedFrameVersion"] = 19;
        fixture.Write();

        Assert.False(fixture.Reader.HasCommandResults(fixture.Commands, russian: false));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("clipped")]
    public void CommandResultMustBePresentAndFullyInsideItsVisibleClip(string mutation)
    {
        using var fixture = new EvidenceFixture();
        JsonObject state = State(ResultCapture(fixture.Document, 50));
        JsonObject result = state["elements"]!.AsArray().Single()!.AsObject();
        if (mutation == "missing") result["semanticId"] = "Hatifect.Flow/network/element/status";
        else result["clip"]!.AsObject()["right"] = 119;
        fixture.Write();

        Assert.False(fixture.Reader.HasCommandResults(fixture.Commands, russian: false));
    }

    [Fact]
    public void CommandResultsRequireExactlyFiveAppliedCommands()
    {
        using var fixture = new EvidenceFixture();

        Assert.False(fixture.Reader.HasCommandResults(fixture.Commands[..4], russian: false));
    }

    [Fact]
    public void FloatRoundedUiEdgesAcceptExactContainmentButRejectActualClipping()
    {
        using var fixture = new EvidenceFixture();
        JsonObject result = State(ResultCapture(fixture.Document, 50))["elements"]!.AsArray().Single()!.AsObject();
        string rectangle = JsonSerializer.Serialize(new
        {
            x = .1f, y = .1f, width = .2f, height = .2f,
            right = .1f + .2f, bottom = .1f + .2f
        });
        result["bounds"] = JsonNode.Parse(rectangle);
        result["clip"] = JsonNode.Parse(rectangle);
        fixture.Write();

        Assert.True(fixture.Reader.HasCommandResults(fixture.Commands, russian: false));

        result["clip"]!.AsObject()["right"] = .299f;
        fixture.Write();
        Assert.False(fixture.Reader.HasCommandResults(fixture.Commands, russian: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SendResultRejectsTheNetworkCommandLabel(bool russian)
    {
        using var fixture = new EvidenceFixture(russian);
        JsonObject result = State(ResultCapture(fixture.Document, 50))["elements"]!.AsArray().Single()!.AsObject();
        result["value"] = russian ? "Команда выполнена" : "Command completed";
        fixture.Write();

        Assert.False(fixture.Reader.HasCommandResults(fixture.Commands, russian));
    }

    [Fact]
    public void CommandResultPublishedAfterTheNextBeforeSceneIsStale()
    {
        using var fixture = new EvidenceFixture();
        JsonObject state = State(ResultCapture(fixture.Document, 20));
        state["acceptedSceneVersion"] = 21;
        state["renderedSceneVersion"] = 21;
        fixture.Write();

        Assert.False(fixture.Reader.HasCommandResults(fixture.Commands, russian: false));
    }

    [Theory]
    [InlineData("wrong-value")]
    [InlineData("clipped")]
    [InlineData("stale-frame")]
    public void DeliveredHistoryRequiresTheExactVisibleValueInTheLatestMatchedFrame(string mutation)
    {
        using var fixture = new EvidenceFixture();
        JsonObject latest = fixture.Document["latest"]!.AsObject();
        JsonObject history = latest["elements"]!.AsArray().Single()!.AsObject();
        switch (mutation)
        {
            case "wrong-value": history["value"] = fixture.Parcel + " · In transit · attempts: 1"; break;
            case "clipped": history["clip"]!.AsObject()["bottom"] = 219; break;
            case "stale-frame": latest["renderedFrameVersion"] = 69; break;
        }
        fixture.Write();

        Assert.False(fixture.Reader.HasDeliveredHistory(fixture.Parcel, russian: false));
    }

    private const long UiEpoch = 7;
    private const string StationNameSemantic = "Hatifect.Flow/network/field/name";
    private const string ResultSemantic = "Hatifect.Flow/network/element/result";
    private const string HistorySemantic = "Hatifect.Flow/network/element/history-detail";

    private static JsonObject Capture(JsonObject document, string phase)
        => document["captures"]!.AsArray().Select(value => value!.AsObject())
            .Single(value => value["phase"]?.GetValue<string>() == phase);

    private static JsonObject ResultCapture(JsonObject document, long scene)
        => document["captures"]!.AsArray().Select(value => value!.AsObject())
            .Single(value => State(value)["acceptedSceneVersion"]!.GetValue<long>() == scene);

    private static JsonObject State(JsonObject capture) => capture["state"]!.AsObject();

    private sealed class EvidenceFixture : IDisposable
    {
        private readonly string _root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
            "hatifect-flow-player-input-ui-" + Guid.NewGuid().ToString("N"));
        private readonly string _path;

        internal EvidenceFixture(bool russian = false)
        {
            RunId = Guid.NewGuid().ToString("D");
            Parcel = Guid.NewGuid();
            string artifact = Path.Combine(_root, "artifact");
            _path = Path.Combine(artifact, "diagnostics", "ui-window-input-progress.json");
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            Commands = CreateCommands();
            Document = CreateDocument(RunId, Parcel, russian);
            Reader = new FlowPlayerInputUiEvidence(new AcceptanceRequest(RunId, "unused-save", null, artifact, "runtime"));
            Write();
        }

        internal string RunId { get; }
        internal Guid Parcel { get; }
        internal FlowPlayerCommandTrace[] Commands { get; }
        internal JsonObject Document { get; }
        internal FlowPlayerInputUiEvidence Reader { get; }

        internal void Write() => File.WriteAllText(_path, Document.ToJsonString());

        public void Dispose() => Directory.Delete(_root, recursive: true);

        private static FlowPlayerCommandTrace[] CreateCommands()
        {
            Guid session = Guid.NewGuid(), source = Guid.NewGuid(), destination = Guid.NewGuid();
            object[] commands =
            {
                new FlowNetworkCommand(session, 1, FlowNetworkAction.RegisterStation, Target: Guid.NewGuid(), Name: "Source"),
                new FlowNetworkCommand(session, 2, FlowNetworkAction.RegisterStation, Target: Guid.NewGuid(), Name: "Destination"),
                new FlowNetworkCommand(session, 3, FlowNetworkAction.AddLink, Station: source, Destination: destination),
                new FlowSendCommand(session, 4, source, destination, 0, "whole"),
                new FlowSendCommand(session, 5, source, destination, 1, "partial") { Quantity = 5 }
            };
            long[] beforeScenes = { 10, 20, 30, 40, 50 };
            return commands.Select((command, index) => new FlowPlayerCommandTrace(index + 1,
                command is FlowSendCommand ? "send" : "network", command,
                new FlowPlayerInputStamp("os-injected", index + 1, 1, 1, 1, 1),
                JsonSerializer.Serialize(new
                {
                    ui = new { epoch = UiEpoch, scene = beforeScenes[index], frame = beforeScenes[index], completed = beforeScenes[index] }
                }),
                new FlowCommandResult(FlowCommandStatus.Applied, index + 2), "{}", null)).ToArray();
        }

        private static JsonObject CreateDocument(string runId, Guid parcel, bool russian)
        {
            var captures = new JsonArray
            {
                PhaseCapture("Ready", 1),
                PhaseCapture("Pointer", 2, StationNameSemantic),
                PhaseCapture("Text", 3, StationNameSemantic),
                PhaseCapture("Backspace", 4, StationNameSemantic),
                PhaseCapture("Tab", 5, "Hatifect.Flow/network/field/destination"),
                ResultStateCapture(20, NetworkResult(russian)),
                ResultStateCapture(30, NetworkResult(russian)),
                ResultStateCapture(40, NetworkResult(russian)),
                ResultStateCapture(50, SendResult(russian)),
                ResultStateCapture(60, SendResult(russian))
            };
            return new JsonObject
            {
                ["protocolVersion"] = 1,
                ["runId"] = runId,
                ["scenario"] = FlowPlayerNativeInputCapture.Scenario,
                ["declaredOrigin"] = "os-injected",
                ["originEvidence"] = "authorized native runner",
                ["expectedText"] = "native-" + Guid.Parse(runId).ToString("N")[..8],
                ["phase"] = "Complete",
                ["completedFrame"] = 70,
                ["failure"] = null,
                ["latest"] = Frame(70, 70, null,
                    Element(HistorySemantic, parcel + (russian ? " · Доставлено · попыток: 1" : " · Delivered · attempts: 1"), 200)),
                ["captures"] = captures
            };
        }

        private static JsonObject PhaseCapture(string phase, long version, string? focusedSemantic = null)
            => Captured(phase, Frame(version, version, focusedSemantic));

        private static JsonObject ResultStateCapture(long scene, string value)
            => Captured(null, Frame(scene, scene, null, Element(ResultSemantic, value, 100)));

        private static JsonObject Captured(string? phase, JsonObject state) => new()
        {
            ["phase"] = phase,
            ["state"] = state,
            ["screenshot"] = "screenshots/input-" + state["completedFrame"] + ".png",
            ["uiLayerScreenshot"] = "screenshots/input-" + state["completedFrame"] + "-ui-layer.png"
        };

        private static JsonObject Frame(long scene, long frame, string? focusedSemantic, params JsonObject[] elements)
        {
            JsonNode? focused = focusedSemantic is null ? null : new JsonObject
            {
                ["nodeId"] = "focused-node",
                ["semanticId"] = focusedSemantic
            };
            var elementArray = new JsonArray();
            foreach (JsonObject element in elements) elementArray.Add(element);
            return new JsonObject
            {
                ["visible"] = true,
                ["completedFrame"] = frame,
                ["surfaceEpoch"] = UiEpoch,
                ["experience"] = "Hatifect.Flow/network",
                ["acceptedSceneVersion"] = scene,
                ["frameVersion"] = frame,
                ["renderedSceneVersion"] = scene,
                ["renderedFrameVersion"] = frame,
                ["renderSequence"] = frame,
                ["viewport"] = JsonSerializer.SerializeToNode(new { width = 1280, height = 720 }),
                ["focused"] = focused,
                ["observation"] = new JsonObject { ["accepted"] = true },
                ["elements"] = elementArray
            };
        }

        private static JsonObject Element(string semantic, string value, double y) => new()
        {
            ["nodeId"] = semantic,
            ["semanticId"] = semantic,
            ["actionId"] = null,
            ["role"] = "Text",
            ["name"] = value,
            ["value"] = value,
            ["enabled"] = true,
            ["focused"] = false,
            ["bounds"] = JsonSerializer.SerializeToNode(new { x = 20d, y, width = 100d, height = 20d, right = 120d, bottom = y + 20d }),
            ["clip"] = JsonSerializer.SerializeToNode(new { x = 0d, y = 0d, width = 1280d, height = 720d, right = 1280d, bottom = 720d })
        };

        private static string NetworkResult(bool russian) => russian ? "Команда выполнена" : "Command completed";
        private static string SendResult(bool russian) => russian ? "Отправление создано" : "Shipment created";
    }
}
