using System;
using Hatifect.UI.Experience;
using Xunit;

namespace Hatifect.UI.Stardew.Tests;

public sealed class SourceChangeBindingTests
{
    [Fact]
    public void SourceFreeSurfaceCanAcceptItsFirstShowAndRetire()
    {
        var binding = new UiSemanticSourceChangeBinding(Array.Empty<IUiSemanticSource>());
        binding.Activate();
        Assert.True(binding.HasChanges);
        binding.Accept(binding.Version);
        Assert.False(binding.HasChanges);
        binding.Dispose();
        binding.Dispose();
        Assert.False(binding.HasChanges);
        Assert.Throws<ObjectDisposedException>(binding.Activate);
    }

    [Fact]
    public void FirstActivationAndBurstNotificationsCoalesceWithoutReadingValues()
    {
        var source = new Source();
        using var binding = new UiSemanticSourceChangeBinding(new[] { source, source });
        Assert.False(binding.HasChanges);
        source.Publish(); // Construction-to-Show changes are read by the first accepted pass.
        binding.Activate();
        binding.Activate();
        Assert.Equal(1, source.Adds);
        Assert.Equal(1, source.Subscribers);
        Assert.True(binding.HasChanges);
        binding.Accept(binding.Version);
        Assert.False(binding.HasChanges);
        for (int i = 0; i < 256; i++) source.Publish();
        Assert.True(binding.HasChanges);
        long captured = binding.Version;
        Assert.NotEqual(0, captured);
        binding.Accept(captured);
        Assert.False(binding.HasChanges);
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public void PublicationDuringPreparationRemainsPendingAfterAcceptingCapturedVersion()
    {
        var source = new Source();
        using var binding = new UiSemanticSourceChangeBinding(new[] { source });
        binding.Activate();
        long preparing = binding.Version;
        source.Publish();
        binding.Accept(preparing);
        Assert.True(binding.HasChanges);
        long next = binding.Version;
        Assert.NotEqual(preparing, next);
        binding.Accept(next);
        Assert.False(binding.HasChanges);
        source.Publish();
        long rejected = binding.Version;
        // A rejected scene never acknowledges its captured version.
        Assert.True(binding.HasChanges);
        Assert.Equal(rejected, binding.Version);
        binding.Accept(binding.Version);
        Assert.False(binding.HasChanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedActivationReleasesEveryAttemptAndRetainsOnlyFailedDetach(bool failRemove)
    {
        var first = new Source();
        var second = new Source { FailAdd = true, FailRemove = failRemove };
        var untouched = new Source();
        var binding = new UiSemanticSourceChangeBinding(new[] { first, second, untouched });
        if (failRemove) Assert.Throws<AggregateException>(binding.Activate);
        else Assert.Throws<InvalidOperationException>(binding.Activate);
        Assert.Equal(0, first.Subscribers);
        Assert.Equal(failRemove ? 1 : 0, second.Subscribers);
        Assert.Equal(0, untouched.Adds);
        Assert.False(binding.HasChanges);
        long retired = binding.Version;
        first.Publish();
        second.Publish();
        Assert.Equal(retired, binding.Version);
        if (failRemove) Assert.Throws<InvalidOperationException>(binding.Activate);
        second.FailRemove = false;
        binding.Deactivate();
        Assert.Equal(0, second.Subscribers);
        Assert.Equal(1, first.Removes);
        Assert.Equal(failRemove ? 2 : 1, second.Removes);
        binding.Deactivate();
        Assert.Equal(failRemove ? 2 : 1, second.Removes);
        second.FailAdd = false;
        binding.Activate();
        Assert.Equal(1, first.Subscribers);
        Assert.Equal(1, second.Subscribers);
        Assert.Equal(1, untouched.Subscribers);
        Assert.True(binding.HasChanges);
        binding.Dispose();
    }

    [Fact]
    public void RejectedFirstShowCanReleaseSubscriptionsAndRetryWithFreshState()
    {
        var source = new Source();
        var binding = new UiSemanticSourceChangeBinding(new[] { source });
        binding.Activate();
        long rejected = binding.Version;
        binding.Deactivate();
        Assert.Equal(0, source.Subscribers);
        Assert.False(binding.HasChanges);
        source.Publish();
        binding.Activate();
        Assert.Equal(1, source.Subscribers);
        Assert.True(binding.HasChanges);
        Assert.NotEqual(rejected, binding.Version);
        binding.Accept(binding.Version);
        Assert.False(binding.HasChanges);
        binding.Dispose();
        Assert.Equal(0, source.Subscribers);
        Assert.Throws<ObjectDisposedException>(binding.Activate);
    }

    [Fact]
    public void RetirementDisablesCapturedCallbacksAndRetriesEachOutstandingRemoval()
    {
        var first = new Source();
        var second = new Source();
        var binding = new UiSemanticSourceChangeBinding(new[] { first, second });
        binding.Activate();
        Action late = first.Capture();
        first.FailRemove = true;
        second.FailRemove = true;
        AggregateException failure = Assert.Throws<AggregateException>(binding.Dispose);
        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.False(binding.HasChanges);
        long retired = binding.Version;
        late();
        second.Publish();
        Assert.Equal(retired, binding.Version);
        first.FailRemove = false;
        Assert.Throws<AggregateException>(binding.Dispose);
        Assert.Equal(0, first.Subscribers);
        Assert.Equal(1, second.Subscribers);
        second.FailRemove = false;
        binding.Dispose();
        Assert.Equal(0, second.Subscribers);
        Assert.Equal(2, first.Removes);
        Assert.Equal(3, second.Removes);
        late();
        Assert.Equal(retired, binding.Version);
        Assert.Throws<ObjectDisposedException>(binding.Activate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetirementInsideAddAccessorCannotLeaveAnAttachedCallback(bool throwAfterAdd)
    {
        var source = new Source { FailAdd = throwAfterAdd };
        var binding = new UiSemanticSourceChangeBinding(new[] { source });
        source.BeforeAdd = binding.Dispose;
        if (throwAfterAdd) Assert.Throws<InvalidOperationException>(binding.Activate);
        else Assert.Throws<ObjectDisposedException>(binding.Activate);
        Assert.False(binding.HasChanges);
        Assert.Equal(0, source.Subscribers);
        source.Publish();
        Assert.Equal(0, binding.Version);
        binding.Dispose();
    }

    [Fact]
    public void DisposalReentryDoesNotRecursivelyRemoveTheSameSource()
    {
        var source = new Source();
        var binding = new UiSemanticSourceChangeBinding(new[] { source });
        binding.Activate();
        source.BeforeRemove = binding.Dispose;
        binding.Dispose();
        Assert.Equal(1, source.Removes);
        Assert.Equal(0, source.Subscribers);
        Assert.False(binding.HasChanges);
    }

    [Fact]
    public void IdleSynchronizationProbeDoesNotReadSourcesOrAllocate()
    {
        var source = new Source();
        using var binding = new UiSemanticSourceChangeBinding(new[] { source });
        binding.Activate();
        binding.Accept(binding.Version);
        bool dirty = false;
        for (int i = 0; i < 256; i++) dirty |= binding.HasChanges;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 256; i++) dirty |= binding.HasChanges;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.False(dirty);
        Assert.Equal(0, allocated);
        Assert.Equal(0, source.Reads);
    }

    private sealed class Source : IUiSemanticSource<int>
    {
        private Action? _changed;
        internal int Adds;
        internal int Removes;
        internal int Reads;
        internal bool FailAdd;
        internal bool FailRemove;
        internal Action? BeforeAdd;
        internal Action? BeforeRemove;
        internal int Subscribers => _changed?.GetInvocationList().Length ?? 0;
        public override bool Equals(object? other) => other is Source;
        public override int GetHashCode() => 1;
        public Type ValueType => typeof(int);
        public int Value { get { Reads++; return 42; } }
        public object UntypedValue => Value;
        public event Action? Changed
        {
            add
            {
                Adds++;
                BeforeAdd?.Invoke();
                _changed += value;
                if (FailAdd) throw new InvalidOperationException("add failed after attachment");
            }
            remove
            {
                Removes++;
                BeforeRemove?.Invoke();
                if (FailRemove) throw new InvalidOperationException("remove failed before detachment");
                _changed -= value;
            }
        }
        internal void Publish() => _changed?.Invoke();
        internal Action Capture() => _changed!;
    }
}
