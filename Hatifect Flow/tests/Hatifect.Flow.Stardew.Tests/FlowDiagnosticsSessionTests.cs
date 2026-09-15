using System;
using System.Collections.Generic;
using Hatifect.Flow.Diagnostics;
using Xunit;

namespace Hatifect.Flow.Stardew.Tests;

public sealed class FlowDiagnosticsSessionTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("1", null)]
    [InlineData(null, "1")]
    [InlineData("0", "1")]
    public void OrdinaryLaunchDoesNotCreateDiagnosticsOrReadGameState(string? mode, string? automated)
    {
        string? previousMode = Environment.GetEnvironmentVariable("HATIFECT_TEST_MODE");
        string? previousAutomated = Environment.GetEnvironmentVariable("HATIFECT_TEST_AUTOMATED");
        try
        {
            Environment.SetEnvironmentVariable("HATIFECT_TEST_MODE", mode);
            Environment.SetEnvironmentVariable("HATIFECT_TEST_AUTOMATED", automated);

            FlowDiagnosticsSession? session = FlowDiagnosticsSession.TryCreate(null!, null!,
                () => throw new InvalidOperationException("Game state must not be read."),
                () => throw new InvalidOperationException("UI state must not be read."));

            Assert.Null(session);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HATIFECT_TEST_MODE", previousMode);
            Environment.SetEnvironmentVariable("HATIFECT_TEST_AUTOMATED", previousAutomated);
        }
    }

    [Fact]
    public void CleanupAttemptsEveryPresentProbeInOrderAndPropagatesTheLastFailure()
    {
        var disposed = new List<string>();
        var firstFailure = new InvalidOperationException("transport cleanup");
        var lastFailure = new FormatException("UI cleanup");
        var transport = new Probe(() => { disposed.Add("transport"); throw firstFailure; });
        var resources = new Probe(() => disposed.Add("resources"));
        var ui = new Probe(() => { disposed.Add("ui"); throw lastFailure; });
        var actions = new Probe(() => disposed.Add("actions"));

        Exception? error = Record.Exception(() => FlowDiagnosticsSession.DisposeAll(transport, null, resources, ui, actions));

        Assert.Same(lastFailure, error);
        Assert.Equal(new[] { "transport", "resources", "ui", "actions" }, disposed);
    }

    [Fact]
    public void SuccessfulLaterCleanupDoesNotHideAnEarlierFailure()
    {
        var failure = new InvalidOperationException("transport cleanup");
        bool laterDisposed = false;
        var transport = new Probe(() => throw failure);
        var later = new Probe(() => laterDisposed = true);

        Exception? error = Record.Exception(() => FlowDiagnosticsSession.DisposeAll(transport, later));

        Assert.Same(failure, error);
        Assert.True(laterDisposed);
    }

    private sealed class Probe : IDisposable
    {
        private readonly Action _dispose;
        internal Probe(Action dispose) => _dispose = dispose;
        public void Dispose() => _dispose();
    }
}
