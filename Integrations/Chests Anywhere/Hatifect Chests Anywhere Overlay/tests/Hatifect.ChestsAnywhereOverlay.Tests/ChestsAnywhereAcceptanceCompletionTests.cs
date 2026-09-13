using Hatifect.ChestsAnywhereOverlay.UI.Semantic;
using Hatifect.UI.Experience;
using Xunit;

namespace Hatifect.ChestsAnywhereOverlay.Tests;

public sealed class ChestsAnywhereAcceptanceCompletionTests
{
    private static readonly string[] Checks = { "open", "views", "handoff", "restoration", "lifecycle" };

    [Theory]
    [InlineData(5)]
    [InlineData(1)]
    public void CompletionPropagatesCleanupFailureEvenAfterRecordedPasses(int completed)
    {
        var context = new RecordingContext();
        var recorder = new SemanticChestsAnywhereOverlayAcceptanceScenarios.CheckRecorder(context, Checks);
        for (int index = 0; index < completed; index++)
            recorder.Record(Checks[index], true, "passed");
        var failure = new InvalidOperationException("final native cleanup failed");

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(() => recorder.Complete(failure));

        Assert.Same(failure, actual);
        Assert.Equal(Checks, context.Results.Select(result => result.Id));
        for (int index = 0; index < Checks.Length; index++)
        {
            Assert.Equal(index < completed, context.Results[index].Passed);
            Assert.Equal(index < completed ? "passed" : failure.Message, context.Results[index].Note);
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(1)]
    public void CompletionPreservesResultsAndFailsMissingChecksWithoutInventingException(int completed)
    {
        var context = new RecordingContext();
        var recorder = new SemanticChestsAnywhereOverlayAcceptanceScenarios.CheckRecorder(context, Checks);
        for (int index = 0; index < completed; index++)
            recorder.Record(Checks[index], true, "passed");

        recorder.Complete(null);

        Assert.Equal(Checks, context.Results.Select(result => result.Id));
        for (int index = 0; index < Checks.Length; index++)
            Assert.Equal(index < completed, context.Results[index].Passed);
    }

    private sealed class RecordingContext : IUiAutomatedAcceptanceContext
    {
        internal List<(string Id, bool Passed, string Note)> Results { get; } = new();

        public void Record(string checkId, bool passed, string note)
            => Results.Add((checkId, passed, note));
    }
}
