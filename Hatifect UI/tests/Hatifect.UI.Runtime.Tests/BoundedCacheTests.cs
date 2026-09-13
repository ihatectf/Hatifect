using System;
using Hatifect.UI.Runtime.Caching;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class BoundedCacheTests
{
    [Fact]
    public void CapacityMustBePositive()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new UiBoundedCache<int, string>(0));

    [Fact]
    public void LeastRecentlyUsedEntryIsEvictedAtTheExplicitBound()
    {
        var cache = new UiBoundedCache<int, string>(2);
        cache.Set(1, "one");
        cache.Set(2, "two");

        Assert.True(cache.TryGetValue(1, out string? one));
        Assert.Equal("one", one);

        cache.Set(3, "three");

        Assert.Equal(2, cache.Count);
        Assert.False(cache.TryGetValue(2, out _));
        Assert.True(cache.TryGetValue(1, out _));
        Assert.True(cache.TryGetValue(3, out _));
    }

    [Fact]
    public void UpdatingAndClearingDoNotViolateTheBound()
    {
        var cache = new UiBoundedCache<int, string>(1);
        cache.Set(1, "before");
        cache.Set(1, "after");

        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGetValue(1, out string? value));
        Assert.Equal("after", value);

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGetValue(1, out _));
    }

    [Fact]
    public void OwnedValuesReleaseExactlyOnceOnReplacementEvictionAndClear()
    {
        var released = new List<string>();
        var cache = new UiBoundedCache<int, string>(2, release: released.Add);
        cache.Set(1, "one");
        cache.Set(1, "one-replaced");
        cache.Set(2, "two");
        Assert.True(cache.TryGetValue(1, out _));

        cache.Set(3, "three");
        cache.Clear();

        Assert.Equal(new[] { "one", "two", "one-replaced", "three" }, released);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void ReplacingTheSameOwnedReferenceDoesNotReleaseTheLiveValue()
    {
        var value = new object();
        var released = new List<object>();
        var cache = new UiBoundedCache<int, object>(1, release: released.Add);
        cache.Set(1, value);

        cache.Set(1, value);

        Assert.Empty(released);
        Assert.True(cache.TryGetValue(1, out object? cached));
        Assert.Same(value, cached);
    }

    [Fact]
    public void ClearAttemptsEveryOwnedReleaseAndAggregatesFailures()
    {
        var attempted = new List<int>();
        var cache = new UiBoundedCache<int, int>(3, release: value =>
        {
            attempted.Add(value);
            if (value != 2) return;
            throw new InvalidOperationException("release failed");
        });
        cache.Set(1, 1);
        cache.Set(2, 2);
        cache.Set(3, 3);

        AggregateException failure = Assert.Throws<AggregateException>(() => cache.Clear());

        Assert.Equal(new[] { 1, 2, 3 }, attempted);
        Assert.Single(failure.InnerExceptions);
        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGetValue(1, out _));
    }
}
