using System;
using System.Threading;
using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Activation;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Registration;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class ActivationOwnershipTests
{
    [Fact]
    public void InventoryReadsActualSessionCacheWithoutActivatingOrTakingBorrowedOwnership()
    {
        UiSymbolId owned = Id("a-owned"), borrowed = Id("b-borrowed"), transient = Id("transient"), singleton = Id("singleton");
        int created = 0, disposed = 0;
        var registry = new UiRegistryBuilder()
            .TerminalSectionOwned(owned, "Owned", () => { created++; return (Experience(owned), new Owner(() => disposed++)); })
            .TerminalSection(borrowed, "Borrowed", () => { created++; return Experience(borrowed); })
            .Window(transient, "Transient", () => { created++; return Experience(transient); })
            .Window(singleton, "Singleton", () => { created++; return Experience(singleton); }, lifetime: UiExperienceLifetime.Singleton)
            .Freeze();
        var activator = new UiExperienceActivator(registry);
        var invocation = new UiInvocationService(registry, activator);
        Assert.Empty(invocation.CaptureActivationOwnership().CachedExperiences);
        Assert.Equal(0, created);
        activator.Activate(borrowed);
        activator.Activate(owned);
        activator.Activate(transient);
        activator.Activate(singleton);
        UiActivationOwnershipSnapshot snapshot = invocation.CaptureActivationOwnership();
        Assert.False(snapshot.Terminal);
        Assert.Equal(Environment.CurrentManagedThreadId, snapshot.OwnerThread);
        Assert.Equal(new[] { new UiCachedExperienceOwnership(owned, UiActivationOwnership.SessionOwnedLifecycle),
            new UiCachedExperienceOwnership(borrowed, UiActivationOwnership.BorrowedDefinition) }, snapshot.CachedExperiences);
        Assert.Empty(new UiExperienceActivator(registry).CaptureOwnership().CachedExperiences);
        Assert.Equal(4, created);
        Assert.Equal(0, disposed);
        activator.DisposeSession();
        Assert.Equal(1, disposed);
        Assert.Empty(invocation.CaptureActivationOwnership().CachedExperiences);
        Assert.Equal(2, snapshot.CachedExperiences.Count);
        Assert.False(snapshot.Terminal);
    }

    [Fact]
    public void TeardownPublishesTerminalEmptyInventoryBeforeFallibleOwnerCleanup()
    {
        UiSymbolId id = Id("failing-owner");
        UiExperienceActivator? activator = null;
        UiActivationOwnershipSnapshot? during = null;
        int disposed = 0;
        var registry = new UiRegistryBuilder().TerminalSectionOwned(id, "Owned", () =>
            (Experience(id), new Owner(() =>
            {
                disposed++;
                during = activator!.CaptureOwnership();
                throw new InvalidOperationException("cleanup failed");
            }))).Freeze();
        activator = new(registry);
        activator.Activate(id);
        Assert.Throws<AggregateException>(() => activator.DisposeSession());
        Assert.NotNull(during);
        Assert.True(during!.Terminal);
        Assert.Empty(during.CachedExperiences);
        activator.DisposeSession();
        Assert.Equal(1, disposed);
        Assert.Throws<ObjectDisposedException>(() => activator.Activate(id));
    }

    [Fact]
    public void FactoryCannotAttachAnOwnerAfterItClosesTheActivationSession()
    {
        UiSymbolId id = Id("late-owner");
        UiExperienceActivator? activator = null;
        int disposed = 0;
        var registry = new UiRegistryBuilder().TerminalSectionOwned(id, "Owned", () =>
        {
            activator!.DisposeSession();
            return (Experience(id), new Owner(() => disposed++));
        }).Freeze();
        activator = new(registry);
        Assert.Throws<ObjectDisposedException>(() => activator.Activate(id));
        Assert.Equal(1, disposed);
        Assert.True(activator.CaptureOwnership().Terminal);
        Assert.Empty(activator.CaptureOwnership().CachedExperiences);
        activator.DisposeSession();
        Assert.Equal(1, disposed);
    }

    [Theory]
    [InlineData(UiExperienceLifetime.Transient)]
    [InlineData(UiExperienceLifetime.Singleton)]
    public void FactoryCannotReturnABorrowedDefinitionFromAClosedSession(UiExperienceLifetime lifetime)
    {
        UiSymbolId id = Id("late-borrowed");
        UiExperienceActivator? activator = null;
        var registry = new UiRegistryBuilder().Window(id, "Borrowed", () =>
        {
            activator!.DisposeSession();
            return Experience(id);
        }, lifetime: lifetime).Freeze();
        activator = new(registry);
        Assert.Throws<ObjectDisposedException>(() => activator.Activate(id));
        Assert.True(activator.CaptureOwnership().Terminal);
        Assert.Empty(activator.CaptureOwnership().CachedExperiences);
    }

    [Fact]
    public void FailedEvictionRemovesOnlyThatCacheEntryBeforeCleanupAndAllowsReactivation()
    {
        UiSymbolId id = Id("evict");
        UiExperienceActivator? activator = null;
        UiActivationOwnershipSnapshot? during = null;
        int created = 0, disposed = 0;
        var registry = new UiRegistryBuilder().TerminalSectionOwned(id, "Owned", () =>
        {
            int generation = ++created;
            return (Experience(id), new Owner(() =>
            {
                disposed++;
                during = activator!.CaptureOwnership();
                if (generation == 1) throw new InvalidOperationException("eviction cleanup failed");
            }));
        }).Freeze();
        activator = new(registry);
        activator.Activate(id);
        UiActivationOwnershipSnapshot retained = activator.CaptureOwnership();
        Assert.Throws<InvalidOperationException>(() => activator.Evict(id));
        Assert.NotNull(during);
        Assert.False(during!.Terminal);
        Assert.Empty(during.CachedExperiences);
        Assert.Single(retained.CachedExperiences);
        Assert.False(activator.Evict(id));
        activator.Activate(id);
        Assert.Equal(2, created);
        Assert.Single(activator.CaptureOwnership().CachedExperiences);
        activator.DisposeSession();
        Assert.Equal(2, disposed);
    }

    [Theory]
    [InlineData("activate")]
    [InlineData("evict")]
    [InlineData("dispose")]
    [InlineData("capture")]
    public void ForeignThreadCannotMutateOrReadTheActivationOwner(string operation)
    {
        UiSymbolId id = Id("thread-owned");
        int created = 0, disposed = 0, availabilityReads = 0;
        var registry = new UiRegistryBuilder().TerminalSectionOwned(id, "Owned", () =>
        { created++; return (Experience(id), new Owner(() => disposed++)); },
            isAvailable: () => { availabilityReads++; return true; }).Freeze();
        var activator = new UiExperienceActivator(registry);
        activator.Activate(id);
        Assert.Equal(1, availabilityReads);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                switch (operation)
                {
                    case "activate": activator.Activate(id); break;
                    case "evict": activator.Evict(id); break;
                    case "dispose": activator.DisposeSession(); break;
                    case "capture": activator.CaptureOwnership(); break;
                }
            }
            catch (Exception error) { failure = error; }
        });
        thread.Start();
        thread.Join();
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(1, availabilityReads);
        Assert.Equal(1, created);
        Assert.Equal(0, disposed);
        Assert.False(activator.CaptureOwnership().Terminal);
        Assert.Single(activator.CaptureOwnership().CachedExperiences);
        activator.DisposeSession();
        Assert.Equal(1, disposed);
    }

    private static UiSymbolId Id(string local) => new("test", "activation-ownership/" + local);
    private static UiExperienceDefinition Experience(UiSymbolId id)
        => new UiExperienceBuilder(id, id.LocalId).Monitor("Value", new UiState<string>("value")).Build();
    private sealed class Owner : IDisposable
    {
        private readonly Action _dispose;
        internal Owner(Action dispose) => _dispose = dispose;
        public void Dispose() => _dispose();
    }
}
