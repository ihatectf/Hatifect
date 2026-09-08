using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Metadata;
using Hatifect.UI.Tooling.Protocol;
using Xunit;
using static Hatifect.UI.Tooling.Tests.AuthoringTestSession;

namespace Hatifect.UI.Tooling.Tests;

public sealed class PlannerTraceProtocolTests
{
    [Fact]
    public async Task CompilerOnlySessionRejectsRequiredPlannerCapabilityAndTrace()
    {
        var session = new UiToolingProtocolSession();
        using var metadata = JsonDocument.Parse(UiBindingContextJson.Export(new UiBindingContext(new UiSymbolId("Author.Mod", "storage"))));
        var required = await Send(session, "initialize", new { initializationOptions = new
        {
            bindingMetadata = metadata.RootElement, requiredCapabilities = new[] { "plannerTrace" }
        } });
        Assert.Equal(UiJsonRpcErrorCodes.InvalidParams, required.ErrorCode);
        Assert.Equal(UiToolingProtocolState.Created, session.State);
        await Initialize(session);
        var response = await Send(session, "hatifect/plannerTrace", new { });
        Assert.Equal(UiJsonRpcErrorCodes.MethodNotFound, response.ErrorCode);
        Assert.Null(response.Result);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("collection")]
    [InlineData("schema")]
    [InlineData("host")]
    [InlineData("width")]
    [InlineData("scale")]
    [InlineData("input")]
    [InlineData("locale")]
    [InlineData("accessibility")]
    [InlineData("version")]
    [InlineData("bindingRevision")]
    [InlineData("resultId")]
    public async Task InvalidPlanningFactsNeverReachProviderAndPermitRetry(string mutation)
    {
        var planner = new RecordingPlanner();
        var session = new UiToolingProtocolSession(planner: planner);
        await Initialize(session);
        await Send(session, "textDocument/didOpen", new { textDocument = new
        {
            uri = DocumentUri, version = 1, text = "presentation Storage\n"
        } }, notification: true);
        var diagnostics = await Send(session, "textDocument/diagnostic", Document());
        string resultId = diagnostics.Result!.Value.GetProperty("resultId").GetString()!;
        JsonObject valid = Parameters(resultId);
        JsonObject invalid = JsonNode.Parse(valid.ToJsonString())!.AsObject();
        switch (mutation)
        {
            case "owner": invalid["planningMetadata"]!["ownerId"] = "Other/storage"; break;
            case "missing": invalid["planningMetadata"]!["elements"] = new JsonArray(); break;
            case "foreign": invalid["planningMetadata"]!["elements"]![0]!["id"] = "Other/storage/value"; break;
            case "collection": invalid["planningMetadata"]!["elements"]![0]!["isCollection"] = "true"; break;
            case "schema": invalid["planningMetadata"]!["schemaVersion"] = 2; break;
            case "host": invalid["hostKind"] = "0"; break;
            case "width": invalid["environment"]!["width"] = 0; break;
            case "scale": invalid["environment"]!["scale"] = -1; break;
            case "input": invalid["environment"]!["inputMode"] = "0"; break;
            case "locale": invalid["environment"]!["locale"] = new string('x', 129); break;
            case "accessibility": invalid["environment"]!.AsObject().Remove("highContrast"); break;
            case "version": invalid["textDocument"]!["version"] = 2; break;
            case "bindingRevision": invalid["bindingRevision"] = 1; break;
            case "resultId": invalid["resultId"] = "stale"; break;
        }
        var response = await Send(session, "hatifect/plannerTrace", invalid);
        Assert.Equal(UiJsonRpcErrorCodes.InvalidParams, response.ErrorCode);
        Assert.Null(response.Result);
        Assert.Equal(0, planner.Calls);
        Assert.False((await Send(session, "hatifect/plannerTrace", valid)).IsError);
        Assert.Equal(1, planner.Calls);
        Assert.True(Assert.Single(planner.Input!.Elements).IsCollection);
        Assert.Equal(new[] { "Hatifect.UI/capability/Browse", "Hatifect.UI/capability/Select" },
            planner.Input.Elements[0].Capabilities.Select(c => c.ToString()));
    }

    [Fact]
    public async Task OversizedTraceHasOnlyErrorAndPreservesSnapshotForRetry()
    {
        var planner = new RecordingPlanner();
        var session = new UiToolingProtocolSession(planner: planner);
        await Initialize(session);
        await Send(session, "textDocument/didOpen", new { textDocument = new
        {
            uri = DocumentUri, version = 1, text = "presentation Storage\n"
        } }, notification: true);
        var diagnostics = await Send(session, "textDocument/diagnostic", Document());
        var parameters = Parameters(diagnostics.Result!.Value.GetProperty("resultId").GetString()!);
        session.ConfigurePayloadLimit(256);
        var oversized = await Send(session, "hatifect/plannerTrace", parameters);
        Assert.Equal(UiJsonRpcErrorCodes.InternalError, oversized.ErrorCode);
        Assert.Null(oversized.Result);
        session.ConfigurePayloadLimit(UiLspMessageStream.DefaultMaximumPayloadBytes);
        var retry = await Send(session, "hatifect/plannerTrace", parameters);
        Assert.False(retry.IsError);
        Assert.Equal("planned", retry.Result!.Value.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GraphOrderAndUniquePresentedMembershipAreRequired(bool duplicate)
    {
        var owner = new UiSymbolId("Author.Mod", "storage");
        var cap = new UiSymbolId("Hatifect.UI", "capability/Monitor");
        var first = owner.Child("element/ZStatus");
        var second = owner.Child("element/AStatus");
        var graph = new UiSemanticGraph(owner, new[]
        {
            new UiSemanticNode(first, "ZStatus", "Z", UiDataTypes.String, new[] { cap }),
            new UiSemanticNode(second, "AStatus", "A", UiDataTypes.String, new[] { cap })
        }, presentedNodes: new[] { first, second });
        var planner = new RecordingPlanner();
        var session = new UiToolingProtocolSession(planner: planner);
        await Initialize(session, new UiBindingContext(graph));
        await Send(session, "textDocument/didOpen", new { textDocument = new
        {
            uri = DocumentUri, version = 1, text = "presentation Storage\n"
        } }, notification: true);
        var diagnostics = await Send(session, "textDocument/diagnostic", Document());
        var parameters = Parameters(diagnostics.Result!.Value.GetProperty("resultId").GetString()!);
        JsonNode Rows(UiSymbolId a, UiSymbolId b) => JsonSerializer.SerializeToNode(new[]
        {
            new { id = a.ToString(), isCollection = false }, new { id = b.ToString(), isCollection = false }
        })!;
        parameters["planningMetadata"]!["elements"] = duplicate ? Rows(first, first) : Rows(second, first);
        var rejected = await Send(session, "hatifect/plannerTrace", parameters);
        Assert.Equal(UiJsonRpcErrorCodes.InvalidParams, rejected.ErrorCode);
        Assert.Null(rejected.Result);
        Assert.Equal(0, planner.Calls);
        parameters["planningMetadata"]!["elements"] = Rows(first, second);
        Assert.False((await Send(session, "hatifect/plannerTrace", parameters)).IsError);
        Assert.Equal(new[] { first, second }, planner.Input!.Elements.Select(e => e.Id));
    }

    private static JsonObject Parameters(string resultId) => JsonSerializer.SerializeToNode(new
    {
        textDocument = new { uri = DocumentUri, version = 1 }, bindingRevision = 0, resultId,
        planningMetadata = new { schemaVersion = 1, ownerId = "Author.Mod/storage", elements = new[]
        {
            new { id = "Author.Mod/storage/element/Items", isCollection = true }
        } },
        hostKind = "Window", environment = new { width = 719, height = 500, scale = 2, inputMode = "MouseKeyboard",
            locale = "ru", theme = "Author.Mod/theme", reducedMotion = false, highContrast = false }
    })!.AsObject();

    private sealed class RecordingPlanner : IUiToolingPlanner
    {
        public int Calls { get; private set; }
        public UiToolingPlanningInput? Input { get; private set; }
        public UiToolingPlanResult Plan(UiToolingPlanningInput input, UiPresentationDefinition presentation,
            UiEnvironment environment, string hostKind)
        {
            Calls++;
            Input = input;
            return new UiToolingPlanResult("planned", new { description = new string('x', 512) }, Array.Empty<object>());
        }
    }
}
