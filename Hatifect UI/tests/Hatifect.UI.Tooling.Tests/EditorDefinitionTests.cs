using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Metadata;
using Hatifect.UI.Tooling.Protocol;
using Xunit;
using static Hatifect.UI.Tooling.Tests.AuthoringTestSession;

namespace Hatifect.UI.Tooling.Tests;

public sealed class EditorDefinitionTests
{
    [Fact]
    public async Task DefinitionUsesExplicitProvenanceAndReferencesIncludeItOnlyWhenRequested()
    {
        var context = new UiBindingContext(new UiSymbolId("Author.Mod", "storage")).DeclareRole("Item");
        var session = new UiToolingProtocolSession();
        await Initialize(session, context, new[] { Declaration(context.OwnerId.Child("role/Item").ToString()) });
        await Send(session, "textDocument/didOpen", new
        {
            textDocument = new { uri = DocumentUri, version = 1, text = "visual Storage\nItem\n    opacity = 1" }
        }, notification: true);
        var result = await Send(session, "textDocument/definition", At(1, 2));
        var location = Assert.Single(result.Result!.Value.EnumerateArray());
        Assert.Equal("file:///consumer/StorageExperience.cs", location.GetProperty("uri").GetString());
        Assert.Equal(42, location.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(17, location.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(21, location.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());
        foreach (bool include in new[] { false, true })
        {
            var references = await Send(session, "textDocument/references", new
            {
                textDocument = new { uri = DocumentUri }, position = new { line = 1, character = 2 },
                context = new { includeDeclaration = include }
            });
            Assert.Equal(include ? 2 : 1, references.Result!.Value.GetArrayLength());
        }
        var builtIn = await Send(session, "textDocument/definition", At(2, 7));
        Assert.Empty(builtIn.Result!.Value.EnumerateArray());
    }

    [Fact]
    public async Task DocumentNameDefinitionUsesItsActualSourceAndMissingProvenanceReturnsEmpty()
    {
        var session = await Open("visual Storage\nItem\n    opacity = 1");
        var declaration = await Send(session, "textDocument/definition", At(0, 9));
        var location = Assert.Single(declaration.Result!.Value.EnumerateArray());
        Assert.Equal(DocumentUri, location.GetProperty("uri").GetString());
        Assert.Equal(7, location.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        var role = await Send(session, "textDocument/definition", At(1, 2));
        Assert.Empty(role.Result!.Value.EnumerateArray());
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("range")]
    [InlineData("uri")]
    public async Task InvalidDeclarationMetadataLeavesInitializationAtomic(string failure)
    {
        var context = new UiBindingContext(new UiSymbolId("Author.Mod", "storage")).DeclareRole("Item");
        using var metadata = JsonDocument.Parse(UiBindingContextMetadataWire.Serialize(UiBindingContextMetadataExporter.Export(context)));
        string id = context.OwnerId.Child("role/Item").ToString();
        object declaration = Declaration(failure == "unknown" ? context.OwnerId.Child("role/Missing").ToString() : id,
            failure == "range" ? 16 : 21, failure == "uri" ? "relative.cs" : "file:///consumer/StorageExperience.cs");
        var session = new UiToolingProtocolSession();
        var result = await Send(session, "initialize", new
        {
            initializationOptions = new { bindingMetadata = metadata.RootElement.Clone(),
                declarations = failure == "duplicate" ? new[] { declaration, declaration } : new[] { declaration } }
        });
        Assert.True(result.IsError);
        Assert.Equal(UiJsonRpcErrorCodes.InvalidParams, result.ErrorCode);
        Assert.Equal(UiToolingProtocolState.Created, session.State);
        await Initialize(session, context);
        Assert.Equal(UiToolingProtocolState.Active, session.State);
    }

    private static object Declaration(string symbolId, int end = 21, string uri = "file:///consumer/StorageExperience.cs")
        => new { symbolId, uri, range = new { start = new { line = 42, character = 17 }, end = new { line = 42, character = end } } };
}
