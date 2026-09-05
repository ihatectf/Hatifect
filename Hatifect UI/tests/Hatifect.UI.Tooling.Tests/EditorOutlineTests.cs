using System.Linq;
using System.Threading.Tasks;
using Hatifect.UI.Tooling.Protocol;
using Xunit;
using static Hatifect.UI.Tooling.Tests.AuthoringTestSession;

namespace Hatifect.UI.Tooling.Tests;

public sealed class EditorOutlineTests
{
    [Fact]
    public async Task OutlineAndFoldingRetainProfileRoleAndRecoveredAssignmentBoundaries()
    {
        var session = await Open("visual Storage\r\n@Wide\r\n    Item @Hover\r\n        surface =\r\n\r\nItem\r\n    opacity = 1\r\n");
        var symbols = await Send(session, "textDocument/documentSymbol", Document());
        var root = Assert.Single(symbols.Result!.Value.EnumerateArray());
        Assert.Equal("Storage", root.GetProperty("name").GetString());
        var profile = root.GetProperty("children")[0];
        Assert.Equal("@Wide", profile.GetProperty("name").GetString());
        var role = Assert.Single(profile.GetProperty("children").EnumerateArray());
        Assert.Equal("Item @Hover", role.GetProperty("name").GetString());
        var property = Assert.Single(role.GetProperty("children").EnumerateArray());
        Assert.Equal("surface", property.GetProperty("name").GetString());
        Assert.Equal(8, property.GetProperty("selectionRange").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(3, profile.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal("Item", root.GetProperty("children")[1].GetProperty("name").GetString());
        var folding = await Send(session, "textDocument/foldingRange", Document());
        Assert.Equal(new[] { (1, 3), (2, 3), (5, 6) }, folding.Result!.Value.EnumerateArray()
            .Select(r => (r.GetProperty("startLine").GetInt32(), r.GetProperty("endLine").GetInt32())).ToArray());
    }

    [Fact]
    public async Task EmptyDocumentHasNoInventedOutlineOrFolding()
    {
        var session = await Open(string.Empty);
        foreach (string method in new[] { "textDocument/documentSymbol", "textDocument/foldingRange" })
        {
            var result = await Send(session, method, Document());
            Assert.False(result.IsError);
            Assert.Empty(result.Result!.Value.EnumerateArray());
        }
    }

    [Fact]
    public async Task DeepRecoveredOutlineFallsBackToFlatSymbolsWithoutDroppingNodes()
    {
        string source = "visual Storage\n" + string.Concat(Enumerable.Range(0, 26)
            .Select(depth => new string(' ', depth * 4) + "@Wide\n"));
        var session = await Open(source);
        var result = await Send(session, "textDocument/documentSymbol", Document());
        Assert.False(result.IsError);
        Assert.Equal(27, result.Result!.Value.GetArrayLength());
        Assert.Equal(DocumentUri, result.Result.Value[26].GetProperty("location").GetProperty("uri").GetString());
        Assert.Equal("@Wide", result.Result.Value[26].GetProperty("containerName").GetString());
    }
}
