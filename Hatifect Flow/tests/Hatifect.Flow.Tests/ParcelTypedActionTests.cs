using System;
using System.Linq;
using Hatifect.Flow.Application;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.UI.Semantic;
using Hatifect.UI;
using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Tests;
using Xunit;

namespace Hatifect.Flow.Tests;

[Trait("Category", "flow")]
public sealed class ParcelTypedActionTests
{
    [Fact]
    public void ParcelCommandsExposeTheirRealTypedContractAndRequireAnOwningHost()
    {
        using var app = new Application(FlowCommandStatus.Applied);
        using var view = Create(app);
        foreach (UiActionDefinition action in view.Experience.Actions.Take(3))
        {
            var node = Assert.Single(view.Experience.Graph.Nodes, node => node.Id == action.Id);
            Assert.Equal(ParcelExperience.ActionRequestType.Descriptor, node.DataType!.InputType);
            Assert.Equal(ParcelExperience.ActionResultType.Descriptor, node.DataType.ResultType);
            Assert.Throws<InvalidOperationException>(() => action.TryExecute());
        }
        Assert.Equal(0, app.Commands);
    }

    [Fact]
    public void RetrySchedulesOneExistingDeliveryAndCannotDuplicateItsEffect()
    {
        using var app = new Application(FlowCommandStatus.Applied, ParcelState.DeliveryRejected);
        using var view = Create(app);
        using var host = Host(view);
        UiActionDefinition retry = view.Experience.Actions[2];
        Assert.True(host.CanInvoke(retry));
        Assert.True(host.Invoke(retry));
        Assert.False(host.Invoke(retry));
        Assert.True(view.Pump());
        Assert.False(host.CanInvoke(retry));
        Assert.Equal(FlowParcelAction.RetryDelivery, app.Command!.Action);
        Assert.Equal(1, app.Commands);
        Assert.Same(app.Result, Result(view));
        app.Fixture.DestinationPort.Inner.AcceptDeposits = true;
        Assert.Equal(1, app.Fixture.Runtime.AdvanceTo(5));
        app.Inner.Refresh();
        Assert.True(view.Pump());
        Assert.Equal("Delivered", Text(view, "State"));
        Assert.False(host.Invoke(retry));
        Assert.Equal(1, app.Commands);
    }

    [Theory]
    [InlineData(FlowCommandStatus.Applied, UiActionOutcome.Success)]
    [InlineData(FlowCommandStatus.Rejected, UiActionOutcome.Rejected)]
    [InlineData(FlowCommandStatus.Conflict, UiActionOutcome.Rejected)]
    [InlineData(FlowCommandStatus.InvalidCommand, UiActionOutcome.Rejected)]
    [InlineData(FlowCommandStatus.SessionClosed, UiActionOutcome.Rejected)]
    [InlineData(FlowCommandStatus.Faulted, UiActionOutcome.Failure)]
    public void TypedDomainOutcomeRetainsOriginalResultForNextPumpAndLocalizedHostFeedback(
        FlowCommandStatus status, UiActionOutcome outcome)
    {
        using var app = new Application(status);
        using var view = Create(app);
        using var host = Host(view);
        FlowSnapshot before = app.ReadSnapshot();
        long publication = view.Publication.Version;
        UiActionDefinition action = view.Experience.Actions[0];

        Assert.True(host.Invoke(action));

        Assert.Equal(new FlowParcelCommand(before.SessionId, before.Revision, CheckpointFixture.Parcel.Value,
            FlowParcelAction.Reserve), app.Command);
        Assert.Equal(publication, view.Publication.Version);
        Assert.Null(Result(view));
        var observed = Assert.IsType<ExperienceActionStatus>(host.ActionStatus(action));
        Assert.Equal(outcome, observed.Outcome);
        Assert.Null(observed.Error);
        if (outcome != UiActionOutcome.Success)
        {
            UiActionMessage message = outcome == UiActionOutcome.Failure
                ? observed.FailureMessage! : observed.Rejection!.LocalizedMessage!;
            Assert.NotNull(message);
            Assert.Equal(app.Result!.ReasonKey, message.Code);
            Assert.Equal(FlowReasonText.Describe(app.Result.Code, app.Result.ReasonKey, false), message.Text.Resolve("en"));
            Assert.Equal(FlowReasonText.Describe(app.Result.Code, app.Result.ReasonKey, true), message.Text.Resolve("ru-RU"));
        }
        Assert.True(view.Pump());
        Assert.Same(app.Result, Result(view));
        Assert.Equal(1, app.Commands);
        Assert.False(view.Pump());
        Assert.Equal(status == FlowCommandStatus.Applied ? ParcelState.Reserved : ParcelState.Created,
            app.Inner.ReadSnapshot().Parcels.Single().State);
    }

    [Fact]
    public void TwoHostsRejectReentryAndRepeatedDispatchThenObserveTheSameCommittedDomainState()
    {
        using var app = new Application(FlowCommandStatus.Applied);
        using var first = Create(app);
        using var second = Create(app);
        using var firstHost = Host(first);
        using var secondHost = Host(second);
        ExperienceTextSnapshot initialFirst = firstHost.Compose("en");
        ExperienceTextSnapshot initialSecond = secondHost.Compose("en");
        AssertHostedState(initialFirst, "Ready to dispatch");
        AssertHostedState(initialSecond, "Ready to dispatch");
        bool? nestedSameHost = null, nestedOtherHost = null;
        UiActionOutcome? nestedOutcome = null;
        app.BeforeExecute = () =>
        {
            nestedSameHost = firstHost.Invoke(first.Experience.Actions[0]);
            nestedOtherHost = secondHost.Invoke(second.Experience.Actions[0]);
            nestedOutcome = secondHost.ActionStatus(second.Experience.Actions[0])!.Outcome;
        };

        Assert.True(firstHost.Invoke(first.Experience.Actions[0]));
        Assert.False(nestedSameHost);
        Assert.True(nestedOtherHost);
        Assert.Equal(UiActionOutcome.Rejected, nestedOutcome);
        Assert.False(firstHost.Invoke(first.Experience.Actions[0]));
        Assert.False(secondHost.Invoke(second.Experience.Actions[0]));
        Assert.Equal(1, app.Commands);
        Assert.True(first.Pump());
        Assert.True(second.Pump());
        Assert.Equal("Scheduled", Text(first, "State"));
        Assert.Equal(Text(first, "State"), Text(second, "State"));
        ExperienceTextSnapshot scheduledFirst = firstHost.Compose("en");
        ExperienceTextSnapshot scheduledSecond = secondHost.Compose("en");
        AssertHostedState(scheduledFirst, "Scheduled");
        AssertHostedState(scheduledSecond, "Scheduled");
        AssertHostedState(initialFirst, "Ready to dispatch");
        AssertHostedState(initialSecond, "Ready to dispatch");
        Assert.Same(app.Result, Result(first));
        Assert.Equal(FlowRejectionCode.ProviderUnavailable, Result(second)!.Code);
        Assert.True(secondHost.CanInvoke(second.Experience.Actions[1]));
        app.BeforeExecute = null;
        Assert.True(secondHost.Invoke(second.Experience.Actions[1]));
        Assert.True(first.Pump());
        Assert.True(second.Pump());
        Assert.Equal("Cancelled", Text(first, "State"));
        Assert.Equal(Text(first, "State"), Text(second, "State"));
        AssertHostedState(firstHost.Compose("en"), "Cancelled");
        AssertHostedState(secondHost.Compose("en"), "Cancelled");
        AssertHostedState(scheduledFirst, "Scheduled");
        AssertHostedState(scheduledSecond, "Scheduled");
        Assert.Equal(2, app.Commands);
    }

    [Theory]
    [InlineData(ParcelState.InTransit)]
    [InlineData(ParcelState.Arrived)]
    [InlineData(ParcelState.Delivered)]
    public void CancelPastItsDomainBoundaryRemainsDisabledWithLocalizedReason(ParcelState state)
    {
        using var app = new Application(FlowCommandStatus.Applied, state);
        using var view = Create(app);
        using var host = Host(view);
        UiActionDefinition cancel = view.Experience.Actions[1];
        Assert.False(host.CanInvoke(cancel));
        Assert.False(host.Invoke(cancel));
        var reason = host.ActionStatus(cancel)!.Rejection!;
        Assert.Equal("flow.reason.InvalidState", reason.Code);
        Assert.Equal("Unavailable at this shipment stage", reason.LocalizedMessage!.Text.Resolve("en"));
        Assert.Equal("Недоступно на этой стадии отправления", reason.LocalizedMessage.Text.Resolve("ru"));
        Assert.Equal(0, app.Commands);
        Assert.Equal(state, app.Inner.ReadSnapshot().Parcels.Single().State);
    }

    [Fact]
    public void ActualThrownFailureKeepsDiagnosticExceptionSeparateFromSafeUserText()
    {
        using var app = new Application(FlowCommandStatus.Applied);
        using var view = Create(app);
        using var host = Host(view);
        var error = new InvalidOperationException("private provider diagnostic");
        app.BeforeExecute = () => throw error;
        Assert.True(host.Invoke(view.Experience.Actions[0]));
        var result = host.ActionStatus(view.Experience.Actions[0])!;
        Assert.Equal(UiActionOutcome.Failure, result.Outcome);
        Assert.Same(error, result.Error);
        Assert.DoesNotContain(error.Message, result.FailureMessage!.Text.Resolve("en"));
        Assert.Equal(ParcelState.Created, app.Inner.ReadSnapshot().Parcels.Single().State);
        Assert.Null(Result(view));
    }

    private static ParcelExperience Create(IFlowApplication app)
        => new(new UiSymbolId("Hatifect.Flow", "parcel"), app, CheckpointFixture.Parcel.Value);
    private static ExperienceTextProbe Host(ParcelExperience view)
    {
        var host = new ExperienceTextProbe(view.Experience);
        host.Compose("en");
        return host;
    }
    private static ParcelTextValue Value(ParcelExperience view, string name)
        => Assert.IsType<ParcelTextValue>(view.Experience.Elements.Single(e => e.Name == name).Source.UntypedValue);
    private static string Text(ParcelExperience view, string name) => Value(view, name).Format("en");
    private static FlowCommandResult? Result(ParcelExperience view) => Value(view, "Result").Facts.Result;

    private static void AssertHostedState(ExperienceTextSnapshot frame, string expected)
    {
        ExperienceTextValue state = Assert.Single(frame.Values, value => value.SemanticName == "State");
        Assert.Equal(expected, state.DisplayText);
        ExperienceAccessibleText accessible = Assert.Single(frame.Accessibility, value => value.Id == state.Id);
        Assert.Equal(expected, accessible.Value);
    }

    private sealed class Application : IFlowApplication, IDisposable
    {
        internal readonly CheckpointFixture Fixture = new();
        internal readonly FlowApplication Inner;
        private readonly FlowCommandStatus _status;
        internal FlowCommandResult? Result;
        internal FlowParcelCommand? Command;
        internal Action? BeforeExecute;
        internal int Commands;
        private bool _executing;
        internal Application(FlowCommandStatus status, ParcelState state = ParcelState.Created)
        {
            Fixture.Reach(state);
            Inner = new(Fixture.Runtime, action => action(Fixture.Runtime), _ => { }, () => !_executing);
            _status = status;
        }
        public event Action<long>? RevisionChanged { add => Inner.RevisionChanged += value; remove => Inner.RevisionChanged -= value; }
        public FlowSnapshot ReadSnapshot() => Inner.ReadSnapshot();
        public FlowCommandResult Execute(FlowParcelCommand command)
        {
            // Model the existing application owner's cross-surface operation admission fence.
            if (_executing) return new(FlowCommandStatus.Rejected, Inner.ReadSnapshot().Revision) { Code = FlowRejectionCode.ProviderUnavailable };
            Commands++; Command = command;
            _executing = true;
            try { BeforeExecute?.Invoke(); }
            finally { _executing = false; }
            return Result = _status == FlowCommandStatus.Applied ? Inner.Execute(command)
                : new(_status, Inner.ReadSnapshot().Revision);
        }
        public void Dispose() => Inner.Dispose();
    }
}
