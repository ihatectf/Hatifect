using System;
using System.Collections.Generic;
using Hatifect.Flow.Application;
using Xunit;

namespace Hatifect.Flow.Tests;

[Trait("Category", "flow")]
public sealed class FlowApplicationLifecycleTests
{
    [Fact]
    public void FaultedApplicationPublishesClosedToCurrentAndLateObserversExactlyOnce()
    {
        var fixture = new CheckpointFixture();
        int attempts = 0, errors = 0;
        using var app = new FlowApplication(fixture.Runtime,
            _ => { attempts++; throw new InvalidOperationException("transport failed"); }, _ => errors++);
        var observed = new List<FlowSnapshot>();
        app.RevisionChanged += _ => observed.Add(app.ReadSnapshot());
        var command = FlowApplicationTests.Command(app.ReadSnapshot(), FlowParcelAction.Reserve);

        Assert.Equal(FlowCommandStatus.Faulted, app.Execute(command).Status);
        FlowSnapshot faulted = app.ReadSnapshot();
        Assert.Same(faulted, Assert.Single(observed));
        Assert.Equal(FlowApplicationState.Faulted, faulted.State);
        Assert.Equal(FlowRejectionCode.Faulted, app.Execute(command).Code);
        app.SetAvailability(FlowApplicationState.Active);
        app.Refresh(force: true);
        Assert.Same(faulted, app.ReadSnapshot());
        var late = new List<FlowSnapshot>();
        app.RevisionChanged += _ => late.Add(app.ReadSnapshot());

        app.Dispose();

        FlowSnapshot closed = app.ReadSnapshot();
        Assert.Equal(FlowApplicationState.Closed, closed.State);
        Assert.Equal(faulted.Revision + 1, closed.Revision);
        Assert.Equal(faulted.SessionId, closed.SessionId);
        Assert.Equal(faulted.NetworkId, closed.NetworkId);
        Assert.Equal(faulted.ProviderMode, closed.ProviderMode);
        Assert.Equal(faulted.SupportedOperations, closed.SupportedOperations);
        Assert.Equal(2, observed.Count);
        Assert.Same(closed, observed[1]);
        Assert.Same(closed, Assert.Single(late));
        Assert.Equal(FlowApplicationState.Faulted, faulted.State);
        Assert.Equal(FlowRejectionCode.Faulted, faulted.Code);
        Assert.Equal(FlowRejectionCode.SessionClosed, app.Execute(command).Code);
        app.Dispose();
        app.Refresh(force: true);
        app.SetAvailability(FlowApplicationState.Active);
        Assert.Same(closed, app.ReadSnapshot());
        Assert.Equal(2, observed.Count);
        Assert.Single(late);
        Assert.Equal(1, attempts);
        Assert.Equal(1, errors);
    }

    [Fact]
    public void FaultObserverFailureAndReentrantDisposalCannotSuppressTheFinalOwnerClose()
    {
        var fixture = new CheckpointFixture();
        int attempts = 0;
        var errors = new List<Exception>();
        using var app = new FlowApplication(fixture.Runtime,
            _ => { attempts++; throw new FormatException("transport failed"); }, errors.Add);
        var observed = new List<FlowApplicationState>();
        int reentrantRejections = 0;
        app.RevisionChanged += _ =>
        {
            Assert.Throws<InvalidOperationException>(app.Dispose);
            reentrantRejections++;
            throw new ArgumentException("observer failed");
        };
        app.RevisionChanged += _ => observed.Add(app.ReadSnapshot().State);

        Assert.Equal(FlowCommandStatus.Faulted,
            app.Execute(FlowApplicationTests.Command(app.ReadSnapshot(), FlowParcelAction.Reserve)).Status);
        Assert.Equal(new[] { FlowApplicationState.Faulted }, observed);
        Assert.Equal(1, reentrantRejections);

        app.Dispose();

        Assert.Equal(new[] { FlowApplicationState.Faulted, FlowApplicationState.Closed }, observed);
        Assert.Equal(2, reentrantRejections);
        Assert.Equal(FlowApplicationState.Closed, app.ReadSnapshot().State);
        Assert.Collection(errors, error => Assert.IsType<ArgumentException>(error),
            error => Assert.IsType<FormatException>(error), error => Assert.IsType<ArgumentException>(error));
        app.Dispose();
        Assert.Equal(2, observed.Count);
        Assert.Equal(3, errors.Count);
        Assert.Equal(1, attempts);
    }
}
