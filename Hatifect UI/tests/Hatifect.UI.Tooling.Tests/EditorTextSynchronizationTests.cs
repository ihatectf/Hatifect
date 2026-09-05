using System;
using System.Linq;
using System.Threading.Tasks;
using Hatifect.UI.Tooling.Editor;
using Xunit;
using static Hatifect.UI.Tooling.Tests.AuthoringTestSession;

namespace Hatifect.UI.Tooling.Tests;

public sealed class EditorTextSynchronizationTests
{
    [Fact]
    public void PositionsCountUtf16AndTabsAndRecognizeEveryLineEnding()
    {
        var text = new UiEditorText("\t😀x\r\ny\rz\n");
        Assert.Equal(4, text.LineCount);
        Assert.Equal(3, text.OffsetAt(0, 3));
        Assert.Equal(4, text.OffsetAt(0, int.MaxValue));
        Assert.Equal(6, text.OffsetAt(1, 0));
        Assert.Equal(new UiEditorPosition(2, 1), text.PositionAt(9));
        Assert.Equal(new UiEditorPosition(3, 0), text.PositionAt(10));
        Assert.Throws<ArgumentException>(() => text.OffsetAt(0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => text.OffsetAt(4, 0));
    }

    [Fact]
    public async Task IncrementalEditsUseEachPreviousResultAndCommitOneVersion()
    {
        var session = await Open("visual Storage\r\n\t😀x\r\n");
        var result = await Send(session, "textDocument/didChange", new
        {
            textDocument = new { uri = DocumentUri, version = 2 },
            contentChanges = new[] { Edit(1, 1, 3, "Item"), Edit(1, 5, 6, " @Hover") }
        }, notification: true);
        Assert.False(result.IsError);
        Assert.True(session.TryGetDocument(DocumentUri, out var document));
        Assert.Equal("visual Storage\r\n\tItem @Hover\r\n", document!.Source);
        Assert.Equal(2, document.Version);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task InvalidChangeBatchPreservesTextAndVersion(int failure)
    {
        const string original = "visual Storage\n";
        var session = await Open(original, maximumSourceLength: 32);
        object invalid = failure switch
        {
            0 => Edit(8, 0, 0, "x"),
            1 => Edit(0, 5, 2, "x"),
            2 => new { text = new string('x', 33) },
            _ => new { range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 1 } }, rangeLength = 2, text = "x" }
        };
        var result = await Send(session, "textDocument/didChange", new
        {
            textDocument = new { uri = DocumentUri, version = 2 },
            contentChanges = new[] { Edit(0, 7, 14, "Changed"), invalid }
        }, notification: true);
        Assert.True(result.IsError);
        Assert.True(session.TryGetDocument(DocumentUri, out var document));
        Assert.Equal(original, document!.Source);
        Assert.Equal(1, document.Version);
    }

    [Fact]
    public async Task DiagnosticRangesUseRawTabWidthAndCrLines()
    {
        var session = await Open("visual Storage\rItem\r\tmystery = Surface.Raised\r");
        var result = await Send(session, "textDocument/diagnostic", Document());
        var diagnostic = result.Result!.Value.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("code").GetString() == "LUI2007");
        var start = diagnostic.GetProperty("range").GetProperty("start");
        Assert.Equal(2, start.GetProperty("line").GetInt32());
        Assert.Equal(11, start.GetProperty("character").GetInt32());
    }
}
