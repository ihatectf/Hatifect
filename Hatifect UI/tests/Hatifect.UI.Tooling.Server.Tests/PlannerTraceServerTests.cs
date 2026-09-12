using System.IO.Pipes;
using System.Text.Json;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Metadata;
using Hatifect.UI.Tooling.Protocol;
using Hatifect.UI.Tooling.Server;
using Xunit;

namespace Hatifect.UI.Tooling.Server.Tests;

public sealed class PlannerTraceServerTests
{
    [Theory]
    [InlineData("explicit")]
    [InlineData("generated")]
    [InlineData("rejected")]
    [InlineData("invalid")]
    public async Task FramedServerTraceAgreesWithActualPlannerAndNeverReturnsPartialPlan(string scenario)
    {
        const string uri = "file:///Storage.hatifect";
        var owner = new UiSymbolId("External.Author", "storage");
        UiExperienceDefinition experience = scenario == "rejected"
            ? new UiExperienceBuilder(owner, "Storage")
                .Element("Impossible", new UiState<string>("Ready"), UiCapabilities.Inspect, UiCapabilities.Search).Build()
            : new UiExperienceBuilder(owner, "Storage")
                .Element("ZSelection", new UiCollectionSource<int>(new[] { 1 }, n => owner.Child("item/" + n)), UiCapabilities.Select)
                .Element("AStatus", new UiState<string>("Ready"), UiCapabilities.Monitor).Build();
        string source = scenario switch
        {
            "explicit" => "presentation Storage\nZSelection -> Secondary\nAStatus\n    view = Status\n",
            "invalid" => "presentation Storage\nMissing\n    view = Unknown\n",
            _ => "presentation Storage\n"
        };
        using var metadata = JsonDocument.Parse(UiBindingContextJson.Export(experience.CreateBindingContext()));
        var captured = UiPlanningInput.Capture(experience);
        object supplement = new { schemaVersion = 1, ownerId = owner.ToString(), elements = captured.Elements.Select(e =>
            new { id = e.Id.ToString(), isCollection = e.IsCollection }).ToArray() };
        var environment = new UiEnvironment(new UiEnvironmentViewport(719, 500), 2, UiInputMode.MouseKeyboard,
            "ru", owner.Child("theme"), new UiAccessibilityPreferences(true, true),
            new UiEnvironmentOrigins("Tooling request", "Tooling request", "Tooling request", "Tooling request", "Tooling request", "Tooling request"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var serverInput = new AnonymousPipeServerStream(PipeDirection.In);
        using var clientOutput = new AnonymousPipeClientStream(PipeDirection.Out, serverInput.GetClientHandleAsString());
        using var serverOutput = new AnonymousPipeServerStream(PipeDirection.Out);
        using var clientInput = new AnonymousPipeClientStream(PipeDirection.In, serverOutput.GetClientHandleAsString());
        using var errors = new StringWriter();
        Task<int> server = UiToolingServer.RunAsync(serverInput, serverOutput, errors, deadline.Token).AsTask();
        using var frames = new UiLspMessageStream(clientInput, clientOutput);
        int nextId = 0;
        JsonElement initialized = await Request("initialize", new { initializationOptions = new
        {
            protocolVersion = 0, requiredCapabilities = new[] { "plannerTrace" }, bindingMetadata = metadata.RootElement
        } });
        Assert.Contains(initialized.GetProperty("result").GetProperty("capabilities").GetProperty("experimental")
            .GetProperty("hatifectUi").GetProperty("capabilities").EnumerateArray(), e => e.GetString() == "plannerTrace");
        await Notify("initialized", new { });
        await Notify("textDocument/didOpen", new { textDocument = new { uri, version = 1, text = source } });
        JsonElement diagnostics = (await Request("textDocument/diagnostic", new { textDocument = new { uri } })).GetProperty("result");
        string resultId = diagnostics.GetProperty("resultId").GetString()!;
        object Parameters(string identity, object planningMetadata) => new
        {
            textDocument = new { uri, version = 1 }, bindingRevision = 0, resultId = identity, planningMetadata,
            hostKind = "Window", environment = new { width = 719, height = 500, scale = 2, inputMode = "MouseKeyboard",
                locale = "ru", theme = owner.Child("theme").ToString(), reducedMotion = true, highContrast = true }
        };
        JsonElement stale = await Request("hatifect/plannerTrace", Parameters(resultId + "-stale", supplement));
        Assert.Equal(-32602, stale.GetProperty("error").GetProperty("code").GetInt32());
        Assert.False(stale.TryGetProperty("result", out _));
        JsonElement malformed = await Request("hatifect/plannerTrace", Parameters(resultId,
            new { schemaVersion = 1, ownerId = owner.ToString(), elements = Array.Empty<object>() }));
        Assert.Equal(-32602, malformed.GetProperty("error").GetProperty("code").GetInt32());
        Assert.False(malformed.TryGetProperty("result", out _));
        JsonElement response = await Request("hatifect/plannerTrace", Parameters(resultId, supplement));
        Assert.False(response.TryGetProperty("error", out _), response.ToString());
        JsonElement result = response.GetProperty("result");
        Assert.Equal(resultId, result.GetProperty("resultId").GetString());
        Assert.Equal(uri, result.GetProperty("uri").GetString());
        Assert.Equal(1, result.GetProperty("version").GetInt32());
        Assert.Equal(0, result.GetProperty("bindingRevision").GetInt32());
        var compilation = new UiCompiler().Compile(source, experience.CreateBindingContext(), uri);
        if (scenario == "invalid")
        {
            Assert.False(compilation.IsValid);
            Assert.Equal("compilation-invalid", result.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("plan").ValueKind);
            Assert.Empty(result.GetProperty("decisions").EnumerateArray());
            Assert.Equal(diagnostics.GetProperty("items").GetRawText(), result.GetProperty("diagnostics").GetRawText());
        }
        else
        {
            Assert.Empty(result.GetProperty("diagnostics").EnumerateArray());
            var presentation = Assert.IsType<UiPresentationDefinition>(compilation.Definition);
            var planner = new UiPresentationPlanner();
            var host = UiHostContext.InEnvironment(UiHostKind.Window, environment);
            if (scenario == "rejected")
            {
                var rejected = Assert.Throws<UiPlanningException>(() => planner.Plan(experience, host, presentation));
                Assert.Equal("planning-rejected", result.GetProperty("status").GetString());
                Assert.Equal(JsonValueKind.Null, result.GetProperty("plan").ValueKind);
                AssertDecisions(rejected.Decisions, result.GetProperty("decisions"));
                Assert.Equal(12, rejected.Decisions.Count(d => d.Code == UiPlanDecisionCode.PresentationRejected));
            }
            else
            {
                UiPresentationPlan expected = planner.Plan(experience, host, presentation);
                Assert.Equal("planned", result.GetProperty("status").GetString());
                JsonElement plan = result.GetProperty("plan");
                Assert.Equal(expected.Pattern.ToString(), plan.GetProperty("pattern").GetString());
                Assert.Equal(owner.ToString(), plan.GetProperty("experience").GetString());
                Assert.Equal(UiPresentationProfiles.Compact.Id.ToString(), plan.GetProperty("profile").GetString());
                Assert.Equal("Window", plan.GetProperty("hostKind").GetString());
                JsonElement[] elements = plan.GetProperty("elements").EnumerateArray().ToArray();
                Assert.Equal(expected.Elements.Count, elements.Length);
                for (int i = 0; i < elements.Length; i++)
                {
                    Assert.Equal(expected.Elements[i].Element.ToString(), elements[i].GetProperty("element").GetString());
                    Assert.Equal(expected.Elements[i].Region.ToString(), elements[i].GetProperty("region").GetString());
                    Assert.Equal(expected.Elements[i].Presentation.ToString(), elements[i].GetProperty("presentation").GetString());
                    AssertDecisions(expected.Elements[i].Decisions, elements[i].GetProperty("decisions"));
                }
                Assert.Equal("Hatifect.UI/presentation/List", elements[0].GetProperty("presentation").GetString());
                AssertDecisions(expected.Decisions, result.GetProperty("decisions"));
                if (scenario == "explicit") Assert.Contains(expected.Elements.SelectMany(e => e.Decisions), d => d.Source is not null);
            }
        }
        Assert.Equal(JsonValueKind.Null, (await Request("shutdown", new { })).GetProperty("result").ValueKind);
        await Notify("exit", new { });
        Assert.Equal(0, await server.WaitAsync(deadline.Token));
        Assert.Empty(errors.ToString());

        async Task Notify(string method, object parameters) => await frames.WriteFrameAsync(
            JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", method, @params = parameters }), deadline.Token);
        async Task<JsonElement> Request(string method, object parameters)
        {
            int id = ++nextId;
            await frames.WriteFrameAsync(JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, method, @params = parameters }), deadline.Token);
            var bytes = await frames.ReadFrameAsync(deadline.Token);
            Assert.NotNull(bytes);
            using var document = JsonDocument.Parse(bytes!);
            Assert.Equal(id, document.RootElement.GetProperty("id").GetInt32());
            return document.RootElement.Clone();
        }
    }

    private static void AssertDecisions(IReadOnlyList<UiPlanDecision> expected, JsonElement actual)
    {
        JsonElement[] decisions = actual.EnumerateArray().ToArray();
        Assert.Equal(expected.Count, decisions.Length);
        for (int i = 0; i < expected.Count; i++)
        {
            UiPlanDecision decision = expected[i];
            JsonElement item = decisions[i];
            Assert.Equal(decision.Code.ToString(), item.GetProperty("code").GetString());
            Assert.Equal(decision.Element?.ToString(), item.GetProperty("element").GetString());
            Assert.Equal(decision.Message, item.GetProperty("message").GetString());
            Assert.Equal(decision.Candidate?.ToString(), item.GetProperty("candidate").GetString());
            JsonElement source = item.GetProperty("source");
            if (decision.Source is null) Assert.Equal(JsonValueKind.Null, source.ValueKind);
            else
            {
                Assert.Equal(decision.Source.SourceName, source.GetProperty("sourceName").GetString());
                Assert.Equal(decision.Source.Span.Start, source.GetProperty("start").GetInt32());
                Assert.Equal(decision.Source.Span.Length, source.GetProperty("length").GetInt32());
                Assert.Equal(decision.Source.Span.Line, source.GetProperty("line").GetInt32());
                Assert.Equal(decision.Source.Span.Column, source.GetProperty("column").GetInt32());
            }
        }
    }
}
