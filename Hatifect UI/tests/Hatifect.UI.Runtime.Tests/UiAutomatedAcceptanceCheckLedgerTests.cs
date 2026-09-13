using Hatifect.UI.Stardew;
using Xunit;

namespace Hatifect.UI.Stardew.Tests;

public sealed class UiAutomatedAcceptanceCheckLedgerTests
{
    [Fact]
    public void TerminalFailure_FillsOnlyMissingChecksAndPreservesOriginalVerdicts()
    {
        var ledger = new UiAutomatedAcceptanceCheckLedger();
        var evidence = new List<RecordedCheck>();
        void Capture(string id, bool passed, string note) => evidence.Add(new RecordedCheck(id, passed, note));

        ledger.Record("check.pass", passed: true, "original pass", Capture);
        ledger.Record("check.fail", passed: false, "original product failure", Capture);
        ledger.FailMissing(
            new[] { "check.pass", "check.fail", "check.missing" },
            "terminal harness failure",
            Capture);

        Assert.Equal(
            new[]
            {
                new RecordedCheck("check.pass", true, "original pass"),
                new RecordedCheck("check.fail", false, "original product failure"),
                new RecordedCheck("check.missing", false, "terminal harness failure")
            },
            evidence);
    }

    [Fact]
    public void DuplicateVerdict_IsRejectedWithoutCallingSinkAgain()
    {
        var ledger = new UiAutomatedAcceptanceCheckLedger();
        var evidence = new List<RecordedCheck>();
        void Capture(string id, bool passed, string note) => evidence.Add(new RecordedCheck(id, passed, note));
        ledger.Record("check", passed: true, "first", Capture);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ledger.Record("check", passed: false, "replacement", Capture));

        Assert.Contains("already has a verdict", error.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { new RecordedCheck("check", true, "first") }, evidence);
    }

    [Fact]
    public void FailMissing_IsIdempotentOrderedAndOrdinalCaseSensitive()
    {
        var ledger = new UiAutomatedAcceptanceCheckLedger();
        var evidence = new List<RecordedCheck>();
        void Capture(string id, bool passed, string note) => evidence.Add(new RecordedCheck(id, passed, note));

        ledger.FailMissing(new[] { "check", "Check", "check" }, "missing", Capture);
        ledger.FailMissing(new[] { "Check", "check" }, "replacement", Capture);

        Assert.Equal(
            new[]
            {
                new RecordedCheck("check", false, "missing"),
                new RecordedCheck("Check", false, "missing")
            },
            evidence);
    }

    [Fact]
    public void SinkFailure_StillReservesFirstVerdictAgainstTerminalReplacement()
    {
        var ledger = new UiAutomatedAcceptanceCheckLedger();
        int sinkCalls = 0;

        Assert.Throws<IOException>(() => ledger.Record(
            "check",
            passed: true,
            "first verdict",
            (_, _, _) =>
            {
                sinkCalls++;
                throw new IOException("persistence failed after accepting the verdict");
            }));
        ledger.FailMissing(
            new[] { "check" },
            "terminal replacement",
            (_, _, _) => sinkCalls++);

        Assert.Equal(1, sinkCalls);
    }

    private sealed record RecordedCheck(string Id, bool Passed, string Note);
}
