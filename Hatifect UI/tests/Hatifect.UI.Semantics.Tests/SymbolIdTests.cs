using Xunit;
using Hatifect.UI;

namespace Hatifect.UI.Semantics.Tests;

public sealed class SymbolIdTests
{
    [Theory]
    [InlineData("Author.Mod/terminal/storage")]
    [InlineData("Hatifect.UI/token/Surface.Raised")]
    public void ParsesStableScopedIdentity(string text)
    {
        Assert.True(UiSymbolId.TryParse(text, out UiSymbolId id));
        Assert.Equal(text, id.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing-scope")]
    [InlineData("/missing")]
    [InlineData("scope/has spaces")]
    public void RejectsInvalidIdentity(string text)
        => Assert.False(UiSymbolId.TryParse(text, out _));
}
