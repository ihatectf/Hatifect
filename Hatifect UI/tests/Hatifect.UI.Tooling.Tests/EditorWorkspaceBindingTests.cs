using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Editor;
using Hatifect.UI.Tooling.Metadata;
using Hatifect.UI.Tooling.Protocol;
using Xunit;
using static Hatifect.UI.Tooling.Tests.AuthoringTestSession;

namespace Hatifect.UI.Tooling.Tests;

public sealed class EditorWorkspaceBindingTests
{
    [Fact]
    public async Task PerDocumentBindingsDriveCompilationCompletionAndOwnerIsolatedReferences()
    {
        var first = new UiBindingContext(new UiSymbolId("Author.Mod", "one")).DeclareRole("Item");
        var second = new UiBindingContext(new UiSymbolId("Author.Mod", "two")).DeclareRole("Item").DeclareRole("Second");
        const string otherUri = "file:///Other.hatifect";
        var session = new UiToolingProtocolSession();
        Assert.False((await Send(session, "initialize", new
        {
            initializationOptions = new { bindingMetadata = Metadata(first), documentBindings = new[]
            {
                new { uri = otherUri, bindingMetadata = Metadata(second) }
            } }
        })).IsError);
        await Send(session, "initialized", new { }, notification: true);
        await OpenDocument(session, DocumentUri, "visual Storage\nItem\n    opacity = 1");
        await OpenDocument(session, otherUri, "visual Other\nItem\n    opacity = 1\nSecond\n    opacity = 1");
        var references = await Send(session, "textDocument/references", new
        {
            textDocument = new { uri = DocumentUri }, position = new { line = 1, character = 2 },
            context = new { includeDeclaration = false }
        });
        Assert.Equal(DocumentUri, Assert.Single(references.Result!.Value.EnumerateArray()).GetProperty("uri").GetString());
        var completion = await Send(session, "textDocument/completion", At(3, 2, otherUri));
        Assert.Equal("Second", Assert.Single(completion.Result!.Value.GetProperty("items").EnumerateArray()).GetProperty("label").GetString());
        await Send(session, "textDocument/didChange", new
        {
            textDocument = new { uri = otherUri, version = 2 },
            contentChanges = new[] { new { text = "visual Other\nSecond\n    opacity = 0.5" } }
        }, notification: true);
        Assert.True(session.TryGetDocument(otherUri, out var changed));
        Assert.True(changed!.IsValid);
        Assert.Equal(second.OwnerId, changed.Analysis.Bindings.OwnerId);
    }

    [Fact]
    public async Task BindingRefreshChangesDiagnosticsWithoutAdvancingClientVersionAndRejectsStaleOrInvalidRefresh()
    {
        var session = await Open("visual Storage\nLater\n    opacity = 1");
        var before = await Send(session, "textDocument/diagnostic", Document());
        string oldId = before.Result!.Value.GetProperty("resultId").GetString()!;
        var updatedContext = new UiBindingContext(new UiSymbolId("Author.Mod", "storage")).DeclareRole("Later");
        var refresh = await Send(session, "hatifect/updateBindings", new
        {
            bindingRevision = 1, bindingMetadata = Metadata(updatedContext)
        });
        Assert.False(refresh.IsError);
        Assert.Equal(1, refresh.Result!.Value.GetProperty("documentsReanalyzed").GetInt32());
        Assert.True(session.TryGetDocument(DocumentUri, out var rebound));
        Assert.Equal(1, rebound!.Version);
        Assert.True(rebound.IsValid);
        var diagnostics = await Send(session, "textDocument/diagnostic", new
        {
            textDocument = new { uri = DocumentUri }, previousResultId = oldId
        });
        Assert.Equal("full", diagnostics.Result!.Value.GetProperty("kind").GetString());
        Assert.Empty(diagnostics.Result.Value.GetProperty("items").EnumerateArray());
        Assert.NotEqual(oldId, diagnostics.Result.Value.GetProperty("resultId").GetString());
        var stale = await Send(session, "hatifect/updateBindings", new { bindingRevision = 1, bindingMetadata = Metadata(updatedContext) });
        Assert.True(stale.IsError);
        var invalid = await Send(session, "hatifect/updateBindings", new
        {
            bindingRevision = 2, bindingMetadata = Metadata(updatedContext),
            documentBindings = new[] { new { uri = "relative", bindingMetadata = Metadata(updatedContext) } }
        });
        Assert.True(invalid.IsError);
        Assert.True(session.TryGetDocument(DocumentUri, out var unchanged));
        Assert.Same(rebound, unchanged);
        Assert.False((await Send(session, "textDocument/didChange", new
        {
            textDocument = new { uri = DocumentUri, version = 2 }, contentChanges = new[] { Edit(2, 14, 15, "0") }
        }, notification: true)).IsError);
    }

    [Fact]
    public async Task RejectedBindingRequestPreservesSnapshotsAndAllowsSameRevisionRetry()
    {
        var session = await Open("visual Storage\nLater\n    opacity = 1");
        session.ConfigurePayloadLimit(4096);
        Assert.True(session.TryGetDocument(DocumentUri, out var before));
        var context = new UiBindingContext(new UiSymbolId("Author.Mod", "storage")).DeclareRole("Later");
        var parameters = new { bindingRevision = 1, bindingMetadata = Metadata(context) };
        var rejected = await session.HandleAsync(new UiJsonRpcRequest("hatifect/updateBindings", false,
            JsonSerializer.SerializeToElement(new string('я', 660)), JsonSerializer.SerializeToElement(parameters)));
        Assert.True(rejected.IsError);
        Assert.Equal(UiJsonRpcErrorCodes.InvalidRequest, rejected.ErrorCode);
        Assert.True(session.TryGetDocument(DocumentUri, out var retained));
        Assert.Same(before, retained);
        var retry = await Send(session, "hatifect/updateBindings", parameters);
        Assert.False(retry.IsError);
        Assert.Equal(1, retry.Result!.Value.GetProperty("bindingRevision").GetInt32());
        Assert.True(session.TryGetDocument(DocumentUri, out var rebound));
        Assert.NotSame(before, rebound);
        Assert.True(rebound!.IsValid);
        Assert.Equal(before!.Version, rebound.Version);
    }

    [Fact]
    public void WorkspaceRebindFailurePreservesEveryDocumentSnapshot()
    {
        var context = new UiBindingContext(new UiSymbolId("Author.Mod", "storage")).DeclareRole("Item");
        var workspace = new UiEditorWorkspace();
        var first = workspace.Update("one", 1, "visual One", context);
        var second = workspace.Update("two", 1, "visual Two", context);
        Assert.Throws<InvalidOperationException>(() => workspace.Rebind(uri => uri == "two"
            ? throw new InvalidOperationException("Metadata unavailable") : context));
        Assert.True(workspace.TryGet("one", out var afterFirst));
        Assert.True(workspace.TryGet("two", out var afterSecond));
        Assert.Same(first, afterFirst);
        Assert.Same(second, afterSecond);
    }

    [Fact]
    public async Task WorkspaceDiagnosticsReuseIdsClearClosedFilesAndInvalidateReopenedDocuments()
    {
        var session = await Open("visual Storage\nItem\n    opacity = 2");
        const string otherUri = "file:///Other.hatifect";
        await OpenDocument(session, otherUri, "visual Other\nItem\n    opacity = 1");
        var first = await Send(session, "workspace/diagnostic", new { previousResultIds = Array.Empty<object>() });
        JsonElement[] reports = first.Result!.Value.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, reports.Length);
        var ids = reports.Select(report => new { uri = report.GetProperty("uri").GetString(), value = report.GetProperty("resultId").GetString() }).ToArray();
        var unchanged = await Send(session, "workspace/diagnostic", new { previousResultIds = ids });
        Assert.All(unchanged.Result!.Value.GetProperty("items").EnumerateArray(), report =>
            Assert.Equal("unchanged", report.GetProperty("kind").GetString()));
        await Send(session, "textDocument/didClose", Document(), notification: true);
        await Send(session, "textDocument/didChange", new
        {
            textDocument = new { uri = otherUri, version = 2 },
            contentChanges = new[] { new { text = "visual Other\nItem\n    opacity = 2" } }
        }, notification: true);
        var changed = await Send(session, "workspace/diagnostic", new { previousResultIds = ids });
        reports = changed.Result!.Value.GetProperty("items").EnumerateArray().ToArray();
        var closed = reports.Single(r => r.GetProperty("uri").GetString() == DocumentUri);
        Assert.Equal(JsonValueKind.Null, closed.GetProperty("version").ValueKind);
        Assert.Empty(closed.GetProperty("items").EnumerateArray());
        var modified = reports.Single(r => r.GetProperty("uri").GetString() == otherUri);
        Assert.Equal(2, modified.GetProperty("version").GetInt32());
        Assert.Equal("LUI2017", Assert.Single(modified.GetProperty("items").EnumerateArray()).GetProperty("code").GetString());
        await OpenDocument(session, DocumentUri, "visual Storage\nItem\n    opacity = 2");
        var reopened = await Send(session, "textDocument/diagnostic", new
        {
            textDocument = new { uri = DocumentUri }, previousResultId = ids.Single(id => id.uri == DocumentUri).value
        });
        Assert.Equal("full", reopened.Result!.Value.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task OpenDocumentLimitRejectsOverflowWithoutEvictingSynchronizedText()
    {
        var session = new UiToolingProtocolSession(documentCapacity: 1);
        await Initialize(session);
        await OpenDocument(session, DocumentUri, "visual Storage");
        var rejected = await Send(session, "textDocument/didOpen", new
        {
            textDocument = new { uri = "file:///Overflow.hatifect", version = 1, text = "visual Overflow" }
        }, notification: true);
        Assert.True(rejected.IsError);
        Assert.Equal(1, session.DocumentCount);
        Assert.True(session.TryGetDocument(DocumentUri, out var retained));
        Assert.Equal("visual Storage", retained!.Source);
        await Send(session, "textDocument/didClose", Document(), notification: true);
        await OpenDocument(session, "file:///Overflow.hatifect", "visual Overflow");
        Assert.Equal(1, session.DocumentCount);
    }

    private static JsonElement Metadata(UiBindingContext context)
    {
        using var json = JsonDocument.Parse(UiBindingContextMetadataWire.Serialize(UiBindingContextMetadataExporter.Export(context)));
        return json.RootElement.Clone();
    }

    private static async Task OpenDocument(UiToolingProtocolSession session, string uri, string source)
        => Assert.False((await Send(session, "textDocument/didOpen", new
        {
            textDocument = new { uri, version = 1, text = source }
        }, notification: true)).IsError);
}
