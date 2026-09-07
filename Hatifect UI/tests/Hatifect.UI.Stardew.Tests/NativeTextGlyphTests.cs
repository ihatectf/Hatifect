using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Hatifect.UI.Stardew.Semantic;
using Xunit;

namespace Hatifect.UI.Stardew.Tests;

public sealed class NativeTextGlyphTests
{
    [Fact]
    public void MissingRightArrowUsesMeasuredDirectionalAsciiInsteadOfDefaultGlyph()
    {
        SpriteFont font = Font();
        string[] lines = Lines("A source → B destination", font, 500, "Wrap");
        string line = Assert.Single(lines);
        Assert.Equal("A source -> B destination", line);
        Assert.Equal(25 * 8, font.MeasureString(line).X);
        Assert.DoesNotContain(font.DefaultCharacter!.Value, line);
    }

    [Fact]
    public void AvailableArrowRemainsTheOriginalGlyph()
    {
        SpriteFont font = Font("→");
        Assert.Equal("A → B", Assert.Single(Lines("A → B", font, 500, "Clip")));
    }

    [Fact]
    public void WrappingUsesExpandedArrowWidth()
    {
        SpriteFont font = Font();
        var lines = Lines("A→B", font, 24, "Wrap");
        Assert.Equal(new[] { "A->", "B" }, lines);
        Assert.All(lines, line => Assert.True(font.MeasureString(line).X <= 24));
    }

    [Theory]
    [InlineData("", "AB...")]
    [InlineData("…", "ABCD…")]
    public void EllipsisFitsUsingGlyphsActuallyPresentInTheFont(string extra, string expected)
    {
        SpriteFont font = Font(extra);
        string line = Assert.Single(Lines("ABCDEFG", font, 40, "Ellipsis"));
        Assert.Equal(expected, line);
        Assert.Equal(40, font.MeasureString(line).X);
    }

    [Fact]
    public void UnrepresentableDirectionFailsExplicitlyInsteadOfDrawingUnrelatedGlyph()
    {
        SpriteFont font = Font(omit: '>');
        var failure = Assert.Throws<UiSemanticStardewCapabilityException>(() =>
            Lines("A → B", font, 500, "Wrap"));
        Assert.Contains("direction", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Exercise the existing native preparation path without exposing Runtime internals
    // or requiring a GraphicsDevice just to use SpriteFont's real glyph metrics.
    private static string[] Lines(string text, SpriteFont font, float width, string overflow)
    {
        var method = typeof(UiSemanticSpriteBatchBridge).GetMethod("BuildLines", BindingFlags.Static | BindingFlags.NonPublic)!;
        object mode = Enum.Parse(method.GetParameters()[4].ParameterType, overflow);
        try { return (string[])method.Invoke(null, new object[] { text, font, 1f, width, mode })!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    [Theory]
    [InlineData(40, true)]
    [InlineData(500, false)]
    public void MissingEllipsisFallbackIsRejectedOnlyWhenTruncationNeedsIt(float width, bool truncated)
    {
        SpriteFont font = Font(omit: '.');
        if (truncated)
            Assert.Throws<UiSemanticStardewCapabilityException>(() => Lines("ABCDEFG", font, width, "Ellipsis"));
        else
            Assert.Equal("ABCDEFG", Assert.Single(Lines("ABCDEFG", font, width, "Ellipsis")));
    }

    private static SpriteFont Font(string extra = "", char? omit = null)
    {
        var characters = Enumerable.Range(32, 95).Select(code => (char)code)
            .Concat(extra).Where(value => value != omit).Distinct().OrderBy(value => value).ToList();
        return new SpriteFont(null!, characters.Select(_ => new Rectangle(0, 0, 8, 16)).ToList(),
            characters.Select(_ => new Rectangle(0, 0, 8, 16)).ToList(), characters, 16, 0,
            characters.Select(_ => new Vector3(0, 8, 0)).ToList(), '*');
    }
}
