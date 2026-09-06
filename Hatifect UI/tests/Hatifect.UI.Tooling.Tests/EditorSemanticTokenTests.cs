using System.Linq;
using System.Threading.Tasks;
using Hatifect.UI.Tooling.Editor;
using Xunit;
using static Hatifect.UI.Tooling.Tests.AuthoringTestSession;

namespace Hatifect.UI.Tooling.Tests;

public sealed class EditorSemanticTokenTests
{
    [Fact]
    public async Task SemanticTokensEncodeExactRelativePositionsKindsAndDeclarationModifier()
    {
        var session = await Open("visual Storage\r\nItem\r\n\topacity = 0.5 -> 1\r\n");
        var result = await Send(session, "textDocument/semanticTokens/full", Document());
        int[] data = result.Result!.Value.GetProperty("data").EnumerateArray().Select(n => n.GetInt32()).ToArray();
        Assert.Equal(new[]
        {
            0, 0, 6, 0, 0, 0, 7, 7, 1, 1, 1, 0, 4, 2, 0, 1, 1, 7, 3, 0,
            0, 10, 3, 6, 0, 0, 4, 2, 7, 0, 0, 3, 1, 6, 0
        }, data);
        Assert.Equal(new[] { "keyword", "type", "variable", "property", "enumMember", "string", "number", "operator" },
            UiEditorAnalysis.TokenTypes);
        await Send(session, "textDocument/didChange", new
        {
            textDocument = new { uri = DocumentUri, version = 2 },
            contentChanges = new[] { Edit(0, 0, 0, "\n") }
        }, notification: true);
        result = await Send(session, "textDocument/semanticTokens/full", Document());
        int[] shifted = result.Result!.Value.GetProperty("data").EnumerateArray().Select(n => n.GetInt32()).ToArray();
        Assert.Equal(1, shifted[0]);
        Assert.Equal(data.Skip(1), shifted.Skip(1));
    }

    [Fact]
    public async Task UnicodeStringIsOneLiteralAndEveryTokenStaysInsideItsLineWithoutOverlap()
    {
        const string source = "presentation Storage\rItems\r\tdensity = \"😀 Surface.Raised\"";
        var session = await Open(source);
        var first = await Send(session, "textDocument/semanticTokens/full", Document());
        var second = await Send(session, "textDocument/semanticTokens/full", Document());
        Assert.Equal(first.Result!.Value.GetRawText(), second.Result!.Value.GetRawText());
        int[] data = first.Result.Value.GetProperty("data").EnumerateArray().Select(n => n.GetInt32()).ToArray();
        var text = new UiEditorText(source);
        int line = 0, character = 0, previousEnd = 0;
        for (int i = 0; i < data.Length; i += 5)
        {
            line += data[i];
            character = data[i] == 0 ? character + data[i + 1] : data[i + 1];
            int start = text.OffsetAt(line, character);
            Assert.True(start >= previousEnd);
            Assert.InRange(data[i + 2], 1, text.LineEnd(line) - start);
            previousEnd = start + data[i + 2];
        }
        Assert.Equal(5, data.Length / 5);
        Assert.Equal(19, data[^3]);
        Assert.Equal(5, data[^2]);
    }
}
