using System.Threading.Tasks;
using Xunit;
using static Hatifect.UI.Tooling.Tests.AuthoringTestSession;

namespace Hatifect.UI.Tooling.Tests;

public sealed class EditorHoverTests
{
    [Fact]
    public async Task PropertyHoverIncludesTypeEffectsAndExactUtf16RangeEvenWithInvalidValue()
    {
        var session = await Open("visual Storage\r\nItem\r\n\toffset.y = broken");
        var result = await Send(session, "textDocument/hover", At(2, 4));
        var hover = result.Result!.Value;
        string description = hover.GetProperty("contents").GetProperty("value").GetString()!;
        Assert.Contains("offset.y: Length", description);
        Assert.Contains("Arrange, Render", description);
        Assert.Contains("Animatable: True", description);
        Assert.Equal(1, hover.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(9, hover.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());
    }

    [Theory]
    [InlineData("presentation Storage\nItems\n    itemSizing = Uniform", 2, 6, "Values: Adaptive, Uniform")]
    [InlineData("visual Storage\nItem\n    surface = Surface.Raised", 2, 18, "Surface.Raised: SurfaceToken")]
    [InlineData("presentation Storage\nItems\n    view = Gallery", 1, 2, "capability/Browse")]
    public async Task HoverUsesEnumTokenAndTargetMetadata(string source, int line, int character, string expected)
    {
        var session = await Open(source);
        var result = await Send(session, "textDocument/hover", At(line, character));
        Assert.Contains(expected, result.Result!.Value.GetProperty("contents").GetProperty("value").GetString());
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(2, 18)]
    public async Task WhitespaceAndQuotedTokenNamesDoNotProduceSemanticHover(int line, int character)
    {
        var session = await Open("presentation Storage\nItems\n    density = \"Surface.Raised\"");
        var result = await Send(session, "textDocument/hover", At(line, character));
        Assert.False(result.IsError);
        Assert.Null(result.Result);
    }
}
