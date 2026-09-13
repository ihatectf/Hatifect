using Hatifect.UI.Runtime.Layout;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class TextBoundarySearchTests
{
    [Fact]
    public void MaximumFitDoesNotSplitSurrogatePairs()
    {
        const string text = "ab\U0001F642cd";

        int length = UiTextBoundarySearch.MaximumFittingLength(
            text,
            3,
            text,
            static (_, prefixLength) => prefixLength);

        Assert.Equal(2, length);
        Assert.Equal("ab", text[..length]);
    }

    [Fact]
    public void MinimumReachSkipsTheWholeIntersectingScalar()
    {
        const string text = "ab\U0001F642cd";

        int length = UiTextBoundarySearch.MinimumReachingLength(
            text,
            3,
            text,
            static (_, prefixLength) => prefixLength);

        Assert.Equal(4, length);
        Assert.Equal("ab\U0001F642", text[..length]);
    }

    [Fact]
    public void TargetBeyondMeasuredTextReturnsTheCompleteText()
    {
        const string text = "status";

        int length = UiTextBoundarySearch.MinimumReachingLength(
            text,
            20,
            text,
            static (_, prefixLength) => prefixLength);

        Assert.Equal(text.Length, length);
    }

    [Fact]
    public void RangedMaximumFitUsesLengthsRelativeToItsScalarBoundary()
    {
        const string text = "skip|ab\U0001F642cd";
        int start = text.IndexOf('a');

        int length = UiTextBoundarySearch.MaximumFittingLength(
            text,
            start,
            3,
            text,
            static (_, prefixLength) => prefixLength);

        Assert.Equal(2, length);
        Assert.Equal("ab", text.Substring(start, length));
    }

    [Fact]
    public void SearchesMatchTheScalarBoundaryReference()
    {
        const string text = "a\U0001F642bc\U0001F642d";
        int[] boundaries = { 0, 1, 3, 4, 5, 7, 8 };

        for (int width = 0; width <= text.Length; width++)
        {
            int maximum = UiTextBoundarySearch.MaximumFittingLength(
                text,
                width,
                text,
                static (_, prefixLength) => prefixLength);
            int minimum = UiTextBoundarySearch.MinimumReachingLength(
                text,
                width,
                text,
                static (_, prefixLength) => prefixLength);

            Assert.Equal(boundaries.Last(value => value <= width), maximum);
            Assert.Equal(boundaries.First(value => value >= width), minimum);
        }
    }

    [Fact]
    public void LongTextUsesLogarithmicMeasurementCount()
    {
        string text = new('x', 10_000);
        var counter = new MeasurementCounter();

        int length = UiTextBoundarySearch.MaximumFittingLength(
            text,
            4_321,
            counter,
            static (state, prefixLength) => state.Measure(prefixLength));

        Assert.Equal(4_321, length);
        Assert.InRange(counter.Count, 1, 32);
    }

    [Fact]
    public void LongClippedTextUsesLogarithmicMeasurementCount()
    {
        string text = new('x', 10_000);
        var counter = new MeasurementCounter();

        int length = UiTextBoundarySearch.MinimumReachingLength(
            text,
            4_321,
            counter,
            static (state, prefixLength) => state.Measure(prefixLength));

        Assert.Equal(4_321, length);
        Assert.InRange(counter.Count, 1, 32);
    }

    [Fact]
    public void NarrowLongTextDoesNotMeasureDistantPrefixes()
    {
        string text = new('x', 10_000);
        var fittingCounter = new MeasurementCounter();
        var clippingCounter = new MeasurementCounter();

        int fitting = UiTextBoundarySearch.MaximumFittingLength(
            text,
            1,
            fittingCounter,
            static (state, prefixLength) => state.Measure(prefixLength));
        int clipping = UiTextBoundarySearch.MinimumReachingLength(
            text,
            1,
            clippingCounter,
            static (state, prefixLength) => state.Measure(prefixLength));

        Assert.Equal(1, fitting);
        Assert.Equal(1, clipping);
        Assert.InRange(fittingCounter.Count, 1, 2);
        Assert.InRange(fittingCounter.MaximumPrefixLength, 1, 2);
        Assert.InRange(clippingCounter.Count, 1, 2);
        Assert.InRange(clippingCounter.MaximumPrefixLength, 1, 2);
    }

    [Fact]
    public void InvalidMetricFailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            UiTextBoundarySearch.MaximumFittingLength(
                "text",
                20,
                0,
                static (_, _) => float.NaN));
    }

    private sealed class MeasurementCounter
    {
        public int Count { get; private set; }
        public int MaximumPrefixLength { get; private set; }

        public float Measure(int prefixLength)
        {
            Count++;
            MaximumPrefixLength = Math.Max(MaximumPrefixLength, prefixLength);
            return prefixLength;
        }
    }
}
