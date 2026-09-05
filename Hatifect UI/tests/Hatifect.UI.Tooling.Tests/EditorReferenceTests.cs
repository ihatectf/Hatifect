using System.Linq;
using System.Threading.Tasks;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Editor;
using Xunit;
using static Hatifect.UI.Tooling.Tests.AuthoringTestSession;

namespace Hatifect.UI.Tooling.Tests;

public sealed class EditorReferenceTests
{
    [Fact]
    public async Task ReferencesCrossOpenFilesAndHighlightsRemainLocalWithTokenSizedRanges()
    {
        var session = await Open("visual Storage\nItem\n    opacity = 1\nItem @Hover\n    opacity = 0.5");
        await Send(session, "textDocument/didOpen", new
        {
            textDocument = new { uri = "file:///Other.hatifect", version = 1, text = "visual Other\nItem\n    surface = Surface.Raised" }
        }, notification: true);
        var response = await Send(session, "textDocument/references", new
        {
            textDocument = new { uri = DocumentUri }, position = new { line = 1, character = 2 },
            context = new { includeDeclaration = false }
        });
        var references = response.Result!.Value.EnumerateArray().ToArray();
        Assert.Equal(3, references.Length);
        Assert.Equal("file:///Other.hatifect", references[0].GetProperty("uri").GetString());
        Assert.All(references, item => Assert.Equal(4,
            item.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32()));
        var highlights = await Send(session, "textDocument/documentHighlight", At(1, 2));
        Assert.Equal(new[] { 1, 3 }, highlights.Result!.Value.EnumerateArray()
            .Select(item => item.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32()));
        Assert.All(highlights.Result.Value.EnumerateArray(), item => Assert.Equal(2, item.GetProperty("kind").GetInt32()));
    }

    [Fact]
    public void ReferenceIdentitySeparatesOwnersAndBindingSnapshotsDoNotFollowLaterMutation()
    {
        var workspace = new UiEditorWorkspace();
        var firstContext = new UiBindingContext(new UiSymbolId("Author.Mod", "one")).DeclareRole("Item");
        var first = workspace.Update("one", 1, "visual One\nItem\n    opacity = 1", firstContext);
        workspace.Update("two", 1, "visual Two\nItem\n    opacity = 1",
            new UiBindingContext(new UiSymbolId("Author.Mod", "two")).DeclareRole("Item"));
        firstContext.DeclareRole("Later");
        UiSymbolId id = first.Analysis.SymbolAt(first.Source.IndexOf("Item", System.StringComparison.Ordinal))!.Id!.Value;
        var reference = Assert.Single(UiEditorReferences.Find(workspace.Snapshots(), id, false));
        Assert.Equal("one", reference.Document.SourceName);
        Assert.DoesNotContain(first.Analysis.Bindings.Roles, role => role.Name == "Later");
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task ReferenceDeclarationFlagControlsDocumentDeclaration(bool includeDeclaration, int expected)
    {
        var session = await Open("visual Storage\n");
        var result = await Send(session, "textDocument/references", new
        {
            textDocument = new { uri = DocumentUri }, position = new { line = 0, character = 9 },
            context = new { includeDeclaration }
        });
        Assert.Equal(expected, result.Result!.Value.GetArrayLength());
        var empty = await Send(session, "textDocument/documentHighlight", At(0, 6));
        Assert.Empty(empty.Result!.Value.EnumerateArray());
    }
}
