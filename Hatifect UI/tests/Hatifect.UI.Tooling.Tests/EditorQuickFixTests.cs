using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Hatifect.UI.Tooling.Editor;
using Xunit;
using static Hatifect.UI.Tooling.Tests.AuthoringTestSession;

namespace Hatifect.UI.Tooling.Tests;

public sealed class EditorQuickFixTests
{
    [Theory]
    [InlineData("surfac = Surface.Raised", "surface = Surface.Raised")]
    [InlineData("surface = Surface.Raisd", "surface = Surface.Raised")]
    public async Task QuickFixUsesVersionedMinimalEditAndCompilerAcceptsResult(string invalid, string corrected)
    {
        string source = "visual Storage\nItem\n    " + invalid + "\n    opacity = 0.5\n";
        var session = await Open(source);
        var result = await Send(session, "textDocument/codeAction", Parameters(2));
        var action = Assert.Single(result.Result!.Value.EnumerateArray());
        var change = Assert.Single(action.GetProperty("edit").GetProperty("documentChanges").EnumerateArray());
        Assert.Equal(1, change.GetProperty("textDocument").GetProperty("version").GetInt32());
        Assert.Equal(DocumentUri, change.GetProperty("textDocument").GetProperty("uri").GetString());
        var edit = Assert.Single(change.GetProperty("edits").EnumerateArray());
        var range = edit.GetProperty("range");
        var map = new UiEditorText(source);
        int start = map.OffsetAt(2, range.GetProperty("start").GetProperty("character").GetInt32());
        int end = map.OffsetAt(2, range.GetProperty("end").GetProperty("character").GetInt32());
        string edited = source[..start] + edit.GetProperty("newText").GetString() + source[end..];
        Assert.Equal("visual Storage\nItem\n    " + corrected + "\n    opacity = 0.5\n", edited);
        Assert.True(session.TryGetDocument(DocumentUri, out var original));
        Assert.Equal(source, original!.Source);
        await Send(session, "textDocument/didChange", new
        {
            textDocument = new { uri = DocumentUri, version = 2 }, contentChanges = new[] { new { text = edited } }
        }, notification: true);
        Assert.True(session.TryGetDocument(DocumentUri, out var updated));
        Assert.True(updated!.IsValid);
        var resolved = await Send(session, "textDocument/codeAction", Parameters(2));
        Assert.Empty(resolved.Result!.Value.EnumerateArray());
    }

    [Fact]
    public async Task QuickFixRejectsTypeRegressionAndHonorsRequestedRangeAndKind()
    {
        var session = await Open("visual Storage\nItem\n    surfac = Text.Primary\n    opacity = 0.5");
        var rejected = await Send(session, "textDocument/codeAction", Parameters(2));
        Assert.Empty(rejected.Result!.Value.EnumerateArray());
        session = await Open("visual Storage\nItem\n    surfac = Surface.Raised\n    opacity = 0.5");
        foreach (object parameters in new[] { Parameters(3), Parameters(2, "refactor") })
        {
            var result = await Send(session, "textDocument/codeAction", parameters);
            Assert.Empty(result.Result!.Value.EnumerateArray());
        }
    }

    [Fact]
    public async Task QuickFixCanRemoveOneErrorWhilePreservingAnIndependentDiagnostic()
    {
        var session = await Open("visual Storage\nItem\n    surfac = Surface.Raised\n    opacity = 2");
        var result = await Send(session, "textDocument/codeAction", Parameters(2));
        Assert.Equal("Replace with 'surface'", Assert.Single(result.Result!.Value.EnumerateArray())
            .GetProperty("title").GetString());
    }

    private static object Parameters(int line, string kind = "quickfix")
        => new { textDocument = new { uri = DocumentUri },
            range = new { start = new { line, character = 0 }, end = new { line, character = int.MaxValue } },
            context = new { diagnostics = System.Array.Empty<JsonElement>(), only = new[] { kind } } };
}
