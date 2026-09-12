using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Metadata;
using Hatifect.UI.Tooling.Protocol;
using Xunit;
using static Hatifect.UI.Tooling.Tests.AuthoringTestSession;

namespace Hatifect.UI.Tooling.Tests;

public sealed class CompilationProtocolTests
{
    [Theory]
    [InlineData("visual Storage\nItem\n    opacity = 1", true)]
    [InlineData("visual Storage\nItem\n    opacity = 2", false)]
    [InlineData("visual Storage\nMissing\n    opacity = 1", false)]
    [InlineData("visual Storage\nItem\n    opacity =", false)]
    public async Task CompleteResultAgreesWithCompilerAndDiagnosticSpans(string source, bool valid)
    {
        var context = new UiBindingContext(new UiSymbolId("Author.Mod", "storage")).DeclareRole("Item");
        var session = await Open(source, context);
        var compiler = new UiCompiler().Compile(source, context, DocumentUri);
        string id = await ResultId(session);

        var response = await Send(session, "hatifect/compilation", Parameters(id));

        Assert.False(response.IsError);
        var result = response.Result!.Value;
        Assert.Equal(valid, compiler.IsValid);
        Assert.Equal(valid ? "valid" : "invalid", result.GetProperty("status").GetString());
        Assert.Equal(DocumentUri, result.GetProperty("uri").GetString());
        Assert.Equal(1, result.GetProperty("version").GetInt32());
        Assert.Equal(0, result.GetProperty("bindingRevision").GetInt32());
        Assert.Equal(id, result.GetProperty("resultId").GetString());
        var diagnostics = result.GetProperty("diagnostics").EnumerateArray().ToArray();
        Assert.Equal(compiler.Diagnostics.Select(d => d.Id), diagnostics.Select(d => d.GetProperty("code").GetString()));
        var pull = await Send(session, "textDocument/diagnostic", Document());
        Assert.Equal(pull.Result!.Value.GetProperty("items").GetRawText(), result.GetProperty("diagnostics").GetRawText());
        Assert.True(session.TryGetDocument(DocumentUri, out var snapshot));
        foreach (var pair in compiler.Diagnostics.Zip(diagnostics))
        {
            var start = snapshot!.Text.PositionAt(pair.First.Span.Start);
            var end = snapshot.Text.PositionAt(pair.First.Span.End);
            var range = pair.Second.GetProperty("range");
            Assert.Equal(start.Line, range.GetProperty("start").GetProperty("line").GetInt32());
            Assert.Equal(start.Character, range.GetProperty("start").GetProperty("character").GetInt32());
            Assert.Equal(end.Line, range.GetProperty("end").GetProperty("line").GetInt32());
            Assert.Equal(end.Character, range.GetProperty("end").GetProperty("character").GetInt32());
        }
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(2, 0, false)]
    [InlineData(1, 1, false)]
    [InlineData(1, 0, true)]
    public async Task MismatchedSnapshotRejectsWithoutResultOrMutation(int version, int revision, bool wrongId)
    {
        var session = await Open("visual Storage\nItem\n    opacity = 1");
        string id = await ResultId(session);
        Assert.True(session.TryGetDocument(DocumentUri, out var before));

        var rejected = await Send(session, "hatifect/compilation", Parameters(wrongId ? "old" : id, version, revision));

        Assert.Equal(UiJsonRpcErrorCodes.InvalidParams, rejected.ErrorCode);
        Assert.Null(rejected.Result);
        Assert.True(session.TryGetDocument(DocumentUri, out var after));
        Assert.Same(before, after);
        Assert.False((await Send(session, "hatifect/compilation", Parameters(id))).IsError);
    }

    [Fact]
    public async Task TextBindingAndReopenTransitionsInvalidateOldCompilationIdentity()
    {
        var session = await Open("visual Storage\nLater\n    opacity = 1");
        string original = await ResultId(session);
        Assert.False((await Send(session, "textDocument/didChange", new
        {
            textDocument = new { uri = DocumentUri, version = 2 },
            contentChanges = new[] { new { text = "visual Storage\nLater\n    opacity = 0.5" } }
        }, notification: true)).IsError);
        Assert.True((await Send(session, "hatifect/compilation", Parameters(original, 2))).IsError);
        string changed = await ResultId(session);
        Assert.Equal("invalid", (await Send(session, "hatifect/compilation", Parameters(changed, 2))).Result!.Value.GetProperty("status").GetString());

        var context = new UiBindingContext(new UiSymbolId("Author.Mod", "storage")).DeclareRole("Later");
        using var metadata = JsonDocument.Parse(UiBindingContextJson.Export(context));
        Assert.False((await Send(session, "hatifect/updateBindings", new
        {
            bindingRevision = 1, bindingMetadata = metadata.RootElement
        })).IsError);
        Assert.True((await Send(session, "hatifect/compilation", Parameters(changed, 2, 1))).IsError);
        string rebound = await ResultId(session);
        Assert.Equal("valid", (await Send(session, "hatifect/compilation", Parameters(rebound, 2, 1))).Result!.Value.GetProperty("status").GetString());

        Assert.False((await Send(session, "textDocument/didClose", Document(), notification: true)).IsError);
        Assert.True((await Send(session, "hatifect/compilation", Parameters(rebound, 2, 1))).IsError);
        Assert.False((await Send(session, "textDocument/didOpen", new
        {
            textDocument = new { uri = DocumentUri, version = 2, text = "visual Storage\nLater\n    opacity = 1" }
        }, notification: true)).IsError);
        Assert.True((await Send(session, "hatifect/compilation", Parameters(rebound, 2, 1))).IsError);
        string reopened = await ResultId(session);
        Assert.NotEqual(rebound, reopened);
        Assert.Equal("valid", (await Send(session, "hatifect/compilation", Parameters(reopened, 2, 1))).Result!.Value.GetProperty("status").GetString());
    }

    [Fact]
    public async Task OversizedCompilationReturnsOnlyErrorAndRetainsSnapshotForRetry()
    {
        var session = await Open("visual Storage\nItem\n    opacity = 2");
        string id = await ResultId(session);
        session.ConfigurePayloadLimit(256);

        var rejected = await Send(session, "hatifect/compilation", Parameters(id));

        Assert.Equal(UiJsonRpcErrorCodes.InternalError, rejected.ErrorCode);
        Assert.Null(rejected.Result);
        session.ConfigurePayloadLimit(UiLspMessageStream.DefaultMaximumPayloadBytes);
        var retry = await Send(session, "hatifect/compilation", Parameters(id));
        Assert.False(retry.IsError);
        Assert.Equal("invalid", retry.Result!.Value.GetProperty("status").GetString());
        Assert.Single(retry.Result.Value.GetProperty("diagnostics").EnumerateArray());
    }

    private static object Parameters(string id, int version = 1, int revision = 0)
        => new { textDocument = new { uri = DocumentUri, version }, bindingRevision = revision, resultId = id };

    [Fact]
    public async Task CompilationRequiresActiveRequestAndAllSnapshotFields()
    {
        var inactive = new UiToolingProtocolSession();
        Assert.Equal(UiJsonRpcErrorCodes.InvalidRequest,
            (await Send(inactive, "hatifect/compilation", Parameters("old"))).ErrorCode);
        var session = await Open("visual Storage\nItem\n    opacity = 1");
        string id = await ResultId(session);
        Assert.Equal(UiJsonRpcErrorCodes.InvalidParams,
            (await Send(session, "hatifect/compilation", Parameters(id), notification: true)).ErrorCode);
        object[] incomplete =
        {
            new { textDocument = new { uri = DocumentUri }, bindingRevision = 0, resultId = id },
            new { textDocument = new { uri = DocumentUri, version = 1 }, resultId = id },
            new { textDocument = new { uri = DocumentUri, version = 1 }, bindingRevision = 0 }
        };
        foreach (object parameters in incomplete)
        {
            var rejected = await Send(session, "hatifect/compilation", parameters);
            Assert.Equal(UiJsonRpcErrorCodes.InvalidParams, rejected.ErrorCode);
            Assert.Null(rejected.Result);
        }
        Assert.False((await Send(session, "hatifect/compilation", Parameters(id))).IsError);
    }

    private static async Task<string> ResultId(UiToolingProtocolSession session)
    {
        var result = await Send(session, "textDocument/diagnostic", Document());
        Assert.False(result.IsError);
        return result.Result!.Value.GetProperty("resultId").GetString()!;
    }
}
