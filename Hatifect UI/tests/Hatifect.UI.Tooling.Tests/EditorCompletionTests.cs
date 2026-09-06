using System.Linq;
using System.Threading.Tasks;
using Xunit;
using static Hatifect.UI.Tooling.Tests.AuthoringTestSession;

namespace Hatifect.UI.Tooling.Tests;

public sealed class EditorCompletionTests
{
    [Theory]
    [InlineData("vis", 0, 3, "visual", "presentation")]
    [InlineData("visual Storage\nIt", 1, 2, "Item", "Items")]
    [InlineData("presentation Storage\nItems -> Pr", 1, 11, "Primary", "Surface.Raised")]
    [InlineData("visual Storage\nItem\n    sur", 2, 7, "surface", "view")]
    [InlineData("visual Storage\nItem @Ho", 1, 8, "Hover", "Wide")]
    [InlineData("visual Storage\n@Wi", 1, 3, "Wide", "Hover")]
    [InlineData("presentation Storage\nItems.itemSizing\n    Wi", 2, 6, "Wide", "surface")]
    [InlineData("presentation Storage\nItems\n    itemSizing = Ad", 2, 19, "Adaptive", "Uniform")]
    public async Task RecoveredContextOffersOnlyRelevantCompletion(string source, int line, int character,
        string included, string excluded)
    {
        var session = await Open(source);
        var response = await Send(session, "textDocument/completion", At(line, character));
        Assert.False(response.IsError);
        var labels = response.Result!.Value.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("label").GetString()).ToArray();
        Assert.Contains(included, labels);
        Assert.DoesNotContain(excluded, labels);
    }

    [Fact]
    public async Task TokenCompletionReplacesWholeQualifiedNameAndFiltersSemanticType()
    {
        var session = await Open("visual Storage\nItem\n    surface = Surface.Raissed");
        var response = await Send(session, "textDocument/completion", At(2, 24));
        var item = Assert.Single(response.Result!.Value.GetProperty("items").EnumerateArray());
        Assert.Equal("Surface.Raised", item.GetProperty("label").GetString());
        var range = item.GetProperty("textEdit").GetProperty("range");
        Assert.Equal(14, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(29, range.GetProperty("end").GetProperty("character").GetInt32());
    }

    [Fact]
    public async Task PresentationValuesRespectElementCapabilitiesAndStringsStayLiteral()
    {
        var session = await Open("presentation Storage\nItems\n    view = ");
        var response = await Send(session, "textDocument/completion", At(2, 11));
        var labels = response.Result!.Value.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("label").GetString()).ToArray();
        Assert.Contains("Gallery", labels);
        Assert.Contains("List", labels);
        Assert.DoesNotContain("NavigationList", labels);
        session = await Open("presentation Storage\n    density = \"Sur");
        response = await Send(session, "textDocument/completion", At(1, 18));
        Assert.Empty(response.Result!.Value.GetProperty("items").EnumerateArray());
    }
}
