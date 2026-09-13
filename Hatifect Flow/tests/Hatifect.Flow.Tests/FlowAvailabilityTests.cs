using System;
using System.Linq;
using Hatifect.Flow.Application;
using Hatifect.Flow.Domain.Policies;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.UI.Semantic;
using Hatifect.UI;
using Hatifect.UI.Experience;
using Xunit;

namespace Hatifect.Flow.Tests;

[Trait("Category", "flow")]
public sealed class FlowAvailabilityTests
{
    [Fact]
    public void ExistingPublicConstructorAndDeconstructSignaturesRemainAvailable()
    {
        Assert.NotNull(typeof(FlowCommandResult).GetConstructor(new[] { typeof(FlowCommandStatus), typeof(long) }));
        Assert.NotNull(typeof(FlowParcelSnapshot).GetConstructor(new[] { typeof(Guid), typeof(Guid), typeof(Guid), typeof(string), typeof(int),
            typeof(Guid), typeof(Guid), typeof(Guid), typeof(ParcelState), typeof(long), typeof(int), typeof(FlowParcelActions) }));
        Assert.Equal(2, Assert.Single(typeof(FlowCommandResult).GetMethods(), method => method.Name == "Deconstruct").GetParameters().Length);
        Assert.Equal(12, Assert.Single(typeof(FlowParcelSnapshot).GetMethods(), method => method.Name == "Deconstruct").GetParameters().Length);
        var legacy = new FlowCommandResult(FlowCommandStatus.Rejected, 3);
        Assert.Equal(FlowRejectionCode.ActionUnavailable, legacy.Code);
        Assert.Equal("flow.reason.ActionUnavailable", legacy.ReasonKey);
    }

    [Fact]
    public void CapacityReasonTracksReservationsAndCancellationWithoutClaimingASecondEffect()
    {
        var fixture = new CheckpointFixture();
        Parcel second = fixture.AddBatch(2), third = fixture.AddBatch(3);
        using var app = Open(fixture);
        FlowSnapshot initial = app.ReadSnapshot();
        Assert.Equal(FlowProviderMode.DiagnosticFake, initial.ProviderMode);
        Assert.Equal(FlowParcelActions.All, initial.SupportedOperations);
        Assert.Equal(FlowCommandStatus.Applied, app.Execute(Command(app, CheckpointFixture.Parcel.Value, FlowParcelAction.Reserve)).Status);
        Assert.Equal(FlowCommandStatus.Applied, app.Execute(Command(app, second.Id.Value, FlowParcelAction.Reserve)).Status);
        FlowSnapshot full = app.ReadSnapshot();
        FlowActionAvailability blocked = full.Parcels.Single(p => p.Id == third.Id.Value).Availability.Reserve;
        Assert.False(blocked.Available);
        Assert.Equal(FlowRejectionCode.CapacityUnavailable, blocked.Code);
        FlowCommandResult rejected = app.Execute(Command(app, third.Id.Value, FlowParcelAction.Reserve));
        Assert.Equal(blocked.Code, rejected.Code);
        Assert.Equal(blocked.ReasonKey, rejected.ReasonKey);
        Assert.Same(full, app.ReadSnapshot());
        Assert.Equal(14, fixture.Runtime.ReservedUnits(CheckpointFixture.Link));
        Assert.Equal(FlowCommandStatus.Applied, app.Execute(Command(app, second.Id.Value, FlowParcelAction.Cancel)).Status);
        Assert.True(app.ReadSnapshot().Parcels.Single(p => p.Id == third.Id.Value).Availability.Reserve.Available);
        Assert.True(initial.Parcels.Single(p => p.Id == third.Id.Value).Availability.Reserve.Available);
        Assert.False(full.Parcels.Single(p => p.Id == third.Id.Value).Availability.Reserve.Available);
    }

    [Theory]
    [InlineData("route", FlowRejectionCode.RouteUnavailable)]
    [InlineData("search", FlowRejectionCode.RouteSearchLimit)]
    [InlineData("queue", FlowRejectionCode.WorkLimit)]
    [InlineData("retry", FlowRejectionCode.RetryLimit)]
    [InlineData("pending", FlowRejectionCode.OperationPending)]
    public void OwnerExplainsAdmissionAndRetryLimitsWithMatchingCommandResults(string condition, FlowRejectionCode code)
    {
        var fixture = new CheckpointFixture(limits: condition == "search" ? new FlowLimits(maxRouteVisits: 1)
            : condition == "queue" ? new FlowLimits(maxPendingOperations: 1)
            : condition == "retry" ? new FlowLimits(maxDeliveryAttempts: 1) : null);
        Guid id = CheckpointFixture.Parcel.Value;
        FlowParcelAction action = FlowParcelAction.Reserve;
        if (condition == "route") fixture.Runtime.RemoveLink(CheckpointFixture.Link);
        if (condition == "queue")
        {
            id = fixture.AddBatch(2).Id.Value;
            Assert.True(fixture.Runtime.TryReserve(CheckpointFixture.Parcel));
        }
        if (condition is "retry" or "pending")
        {
            fixture.Reach(ParcelState.DeliveryRejected);
            action = FlowParcelAction.RetryDelivery;
            if (condition == "pending") Assert.True(fixture.Runtime.RetryDelivery(CheckpointFixture.Parcel));
        }
        using var app = Open(fixture);
        FlowSnapshot before = app.ReadSnapshot();
        FlowActionAvailability availability = before.Parcels.Single(p => p.Id == id).Availability[action];
        Assert.False(availability.Available);
        Assert.Equal(code, availability.Code);
        FlowCommandResult result = app.Execute(Command(app, id, action));
        Assert.Equal(FlowCommandStatus.Rejected, result.Status);
        Assert.Equal(code, result.Code);
        Assert.Equal("flow.reason." + code, result.ReasonKey);
        Assert.Same(before, app.ReadSnapshot());
    }

    [Fact]
    public void ProviderCapabilityAndSessionReasonsRemainExplicitAndReadDoesNotSearchOrPublish()
    {
        var fixture = new CheckpointFixture();
        using var app = new FlowApplication(fixture.Runtime, command => command(fixture.Runtime), _ => { },
            providerMode: FlowProviderMode.GameInventory, supportedOperations: FlowParcelActions.Cancel);
        FlowSnapshot before = app.ReadSnapshot();
        Assert.Equal(FlowProviderMode.GameInventory, before.ProviderMode);
        Assert.Equal(FlowParcelActions.Cancel, before.SupportedOperations);
        Assert.Equal(FlowRejectionCode.UnsupportedAction, Assert.Single(before.Parcels).Availability.Reserve.Code);
        Assert.Equal(FlowRejectionCode.UnsupportedAction, app.Execute(Command(app, CheckpointFixture.Parcel.Value, FlowParcelAction.Reserve)).Code);
        long searches = fixture.Runtime.RouteSearchCount;
        for (int i = 0; i < 1000; i++) Assert.Same(before, app.ReadSnapshot());
        app.Refresh();
        Assert.Same(before, app.ReadSnapshot());
        Assert.Equal(searches, fixture.Runtime.RouteSearchCount);
        foreach (var pair in new[] { (FlowApplicationState.Paused, FlowRejectionCode.Paused), (FlowApplicationState.RecoveryRequired, FlowRejectionCode.RecoveryRequired) })
        {
            app.SetAvailability(pair.Item1);
            FlowSnapshot snapshot = app.ReadSnapshot();
            Assert.Equal(pair.Item2, snapshot.Code);
            Assert.Equal("flow.reason." + pair.Item2, snapshot.ReasonKey);
            Assert.Equal(pair.Item2, Assert.Single(snapshot.Parcels).Availability.Cancel.Code);
            Assert.Equal(pair.Item2, app.Execute(Command(app, CheckpointFixture.Parcel.Value, FlowParcelAction.Cancel)).Code);
        }
        app.Dispose();
        Assert.Equal(FlowProviderMode.GameInventory, app.ReadSnapshot().ProviderMode);
        Assert.Equal(FlowRejectionCode.SessionClosed, app.Execute(Command(app, CheckpointFixture.Parcel.Value, FlowParcelAction.Cancel)).Code);
    }

    [Fact]
    public void StaleSessionRevisionAndLiveProviderLossHaveDistinctStableReasons()
    {
        var fixture = new CheckpointFixture();
        bool available = true;
        using var app = new FlowApplication(fixture.Runtime, command => command(fixture.Runtime), _ => { }, () => available);
        FlowParcelCommand command = Command(app, CheckpointFixture.Parcel.Value, FlowParcelAction.Reserve);
        Assert.Equal(FlowRejectionCode.StaleSession, app.Execute(command with { SessionId = Guid.NewGuid() }).Code);
        Assert.Equal(FlowRejectionCode.StaleRevision, app.Execute(command with { ExpectedRevision = 99 }).Code);
        available = false;
        Assert.Equal(FlowRejectionCode.ProviderUnavailable, app.Execute(command).Code);
        Assert.Equal(ParcelState.Created, fixture.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        available = true;
        FlowCommandResult applied = app.Execute(command);
        Assert.Equal(FlowRejectionCode.None, applied.Code);
        Assert.Empty(applied.ReasonKey);
        Assert.Equal(FlowRejectionCode.StaleRevision, app.Execute(command).Code);
    }

    [Theory]
    [InlineData(false, "No route connects these stations", "diagnostic")]
    [InlineData(true, "Между станциями нет маршрута", "диагностика")]
    public void ParcelAndNetworkConsumersDisplayOwnerReasonsAndDiagnosticMode(bool russian, string reason, string mode)
    {
        using var app = new NetworkTestApplication();
        app.Fixture.Runtime.RemoveLink(CheckpointFixture.Link);
        app.Application.Refresh();
        using var parcel = new ParcelExperience(new UiSymbolId("Hatifect.Flow", "parcel"), app, CheckpointFixture.Parcel.Value, russian);
        using var network = new NetworkExperience(new UiSymbolId("Hatifect.Flow", "network"), app, russian);
        Assert.NotNull(parcel.Experience.CreateBindingContext());
        Assert.Contains(mode, parcel.Experience.DisplayName);
        Assert.Contains(mode, network.Experience.DisplayName);
        Assert.False(parcel.Experience.Actions[0].CanExecute);
        Assert.Contains(reason, parcel.Experience.Elements.Single(e => e.Name == (russian ? "Доступность" : "Availability")).Source.UntypedValue!.ToString());
        var history = Assert.IsType<FlowSelectionSource<FlowParcelSnapshot>>(network.Experience.Elements.Single(e => e.Name == (russian ? "Отправления" : "Shipments")).Source);
        history.TrySelect(history.GetItem(0).Id);
        Assert.Contains(reason, network.Experience.Elements.Single(e => e.Name == (russian ? "Доступность" : "Availability")).Source.UntypedValue!.ToString());
        Assert.Equal(reason, FlowReasonText.Describe(FlowRejectionCode.RouteUnavailable, "unknown.translation", russian));
        Assert.Equal(reason, FlowReasonText.Describe(FlowRejectionCode.ActionUnavailable, "flow.reason.RouteUnavailable", russian));
    }

    [Theory]
    [InlineData(FlowRejectionCode.StationLimit, false, "The station limit for this save has been reached")]
    [InlineData(FlowRejectionCode.StationLimit, true, "Достигнут предел станций для этого сейва")]
    [InlineData(FlowRejectionCode.LifetimeLinkLimit, false, "The link history limit has been reached; removing a link does not free it")]
    [InlineData(FlowRejectionCode.LifetimeLinkLimit, true, "Достигнут предел истории связей; удаление связи не освобождает его")]
    [InlineData(FlowRejectionCode.RetainedCargoLimit, false, "The shipment history limit has been reached; completed shipments do not free it")]
    [InlineData(FlowRejectionCode.RetainedCargoLimit, true, "Достигнут предел истории отправлений; завершённые отправления не освобождают его")]
    public void ResourceLimitReasonsExplainPersistentBackpressure(FlowRejectionCode code, bool russian, string expected)
    {
        Assert.Equal(expected, FlowReasonText.Describe(code, "flow.reason." + code, russian));
    }

    private static FlowApplication Open(CheckpointFixture fixture) => new(fixture.Runtime, command => command(fixture.Runtime), _ => { });
    private static FlowParcelCommand Command(FlowApplication app, Guid id, FlowParcelAction action)
        => new(app.ReadSnapshot().SessionId, app.ReadSnapshot().Revision, id, action);
}
