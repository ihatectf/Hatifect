using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.Flow.Application;
using Hatifect.Flow.UI.Semantic;
using Hatifect.UI;
using Hatifect.UI.Experience;
using Xunit;

namespace Hatifect.Flow.Tests;

[Trait("Category", "flow")]
public sealed class FlowProjectionSubscriptionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitialReadSubscribesFirstAndObservesPublicationDuringRead(bool network)
    {
        using var app = new PublishingApplication();
        app.AfterRead = () => app.Inner.Application.SetAvailability(FlowApplicationState.Paused);
        using IFlowExperience view = Create(network, app);
        Assert.False(app.ReadBeforeSubscription);
        Assert.Equal(Paused(network), Status(view, network).UntypedValue!.ToString());
        Assert.False(view.Pump());
        Assert.Equal(1, app.Subscribers);
        view.Dispose();
        Assert.Equal(0, app.Subscribers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicationDuringProjectionRemainsPendingForNextPump(bool network)
    {
        using var app = new PublishingApplication();
        using IFlowExperience view = Create(network, app);
        IUiSemanticSource status = Status(view, network);
        bool resume = true;
        bool reentered = false;
        status.Changed += () =>
        {
            if (!resume) return;
            resume = false;
            app.Inner.Application.SetAvailability(FlowApplicationState.Active);
            reentered |= view.Pump();
        };
        app.Inner.Application.SetAvailability(FlowApplicationState.Paused);
        Assert.True(view.Pump());
        Assert.Equal(Paused(network), status.UntypedValue!.ToString());
        Assert.False(reentered);
        Assert.Equal(FlowApplicationState.Active, app.Inner.ReadSnapshot().State);
        Assert.True(view.Pump());
        Assert.Equal(network ? "Ready" : "Ready to dispatch", status.UntypedValue!.ToString());
        Assert.False(view.Pump());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedReadRemainsRetryableWithoutAnotherPublication(bool network)
    {
        using var app = new PublishingApplication();
        using IFlowExperience view = Create(network, app);
        app.Inner.Application.SetAvailability(FlowApplicationState.Paused);
        app.AfterRead = () => throw new InvalidOperationException("read unavailable");
        Assert.Throws<InvalidOperationException>(() => view.Pump());
        Assert.True(view.Pump());
        Assert.Equal(Paused(network), Status(view, network).UntypedValue!.ToString());
        Assert.False(view.Pump());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FailedConstructionReleasesSubscriptionEvenIfAddThrowsAfterAttaching(bool network, bool failAdd)
    {
        using var app = new PublishingApplication { FailAdd = failAdd };
        if (!failAdd) app.AfterRead = () => throw new InvalidOperationException("initial read unavailable");
        Assert.Throws<InvalidOperationException>(() => Create(network, app));
        Assert.Equal(0, app.Subscribers);
        app.Inner.Application.SetAvailability(FlowApplicationState.Paused);
        app.FailAdd = false;
        using IFlowExperience replacement = Create(network, app);
        Assert.Equal(Paused(network), Status(replacement, network).UntypedValue!.ToString());
        Assert.Equal(1, app.Subscribers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedUnsubscribeRetiresViewAndCanBeRetried(bool network)
    {
        using var app = new PublishingApplication();
        using IFlowExperience view = Create(network, app);
        app.FailRemove = true;
        Assert.Throws<InvalidOperationException>(view.Dispose);
        Assert.Equal(1, app.Subscribers);
        app.Inner.Application.SetAvailability(FlowApplicationState.Paused);
        Assert.False(view.IsActive);
        Assert.False(view.Pump());
        Assert.All(view.Experience.Actions, action => Assert.False(action.CanExecute));
        app.FailRemove = false;
        view.Dispose();
        Assert.Equal(0, app.Subscribers);
    }

    private static IFlowExperience Create(bool network, IFlowNetworkApplication app)
        => network ? new NetworkExperience(new UiSymbolId("Hatifect.Flow", "network"), app)
            : new ParcelExperience(new UiSymbolId("Hatifect.Flow", "parcel"), app, CheckpointFixture.Parcel.Value);
    private static IUiSemanticSource Status(IFlowExperience view, bool network)
    {
        var source = view.Experience.Elements.Single(element => element.Name == (network ? "Transport" : "State")).Source;
        if (network) Assert.IsAssignableFrom<IUiSemanticSource<string>>(source);
        else Assert.IsAssignableFrom<IUiSemanticSource<ParcelTextValue>>(source);
        return source;
    }
    private static string Paused(bool network) => network ? "Paused" : "Transport paused";

    // Adversarial owner boundary: a read returns its captured value while publishing a newer one.
    private sealed class PublishingApplication : IFlowNetworkApplication, IDisposable
    {
        internal readonly NetworkTestApplication Inner = new();
        internal Action? AfterRead;
        internal bool ReadBeforeSubscription, FailAdd, FailRemove;
        private Action<long>? _changed;
        internal int Subscribers => _changed?.GetInvocationList().Length ?? 0;
        internal PublishingApplication() => Inner.RevisionChanged += Notify;
        public event Action<long>? RevisionChanged
        {
            add { _changed += value; if (FailAdd) throw new InvalidOperationException("subscription attached then failed"); }
            remove { if (FailRemove) throw new InvalidOperationException("unsubscribe failed"); _changed -= value; }
        }
        private void Notify(long revision) => _changed?.Invoke(revision);
        private T Read<T>(T captured)
        {
            ReadBeforeSubscription |= Subscribers == 0;
            Action? callback = AfterRead;
            AfterRead = null;
            callback?.Invoke();
            return captured;
        }
        public FlowSnapshot ReadSnapshot() => Read(Inner.ReadSnapshot());
        public FlowNetworkSnapshot ReadNetwork() => Read(Inner.ReadNetwork());
        public FlowRoutePreview PreviewRoute(Guid source, Guid destination) => Inner.PreviewRoute(source, destination);
        public IReadOnlyList<FlowInventorySlot> ReadInventory(Guid station) => Inner.ReadInventory(station);
        public IReadOnlyList<FlowRecoveryIssue> ReadRecovery() => Inner.ReadRecovery();
        public FlowCommandResult Execute(FlowParcelCommand command) => Inner.Execute(command);
        public FlowCommandResult Execute(FlowNetworkCommand command) => Inner.Execute(command);
        public FlowCommandResult Execute(FlowSendCommand command) => Inner.Execute(command);
        public FlowCommandResult Execute(FlowRecoveryCommand command) => Inner.Execute(command);
        public void Dispose() { Inner.RevisionChanged -= Notify; Inner.Dispose(); }
    }
}
