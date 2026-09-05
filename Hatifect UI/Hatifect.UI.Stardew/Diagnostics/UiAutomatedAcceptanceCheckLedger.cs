using System;
using System.Collections.Generic;

namespace Hatifect.UI.Stardew;

/// <summary>
/// Per-run first-verdict ledger for automated acceptance checks. Manual re-attestation remains a
/// recorder concern; terminal automation failures may only fill checks that never produced evidence.
/// </summary>
internal sealed class UiAutomatedAcceptanceCheckLedger
{
    private readonly HashSet<string> _recorded = new(StringComparer.Ordinal);

    internal void Record(
        string checkId,
        bool passed,
        string note,
        Action<string, bool, string> sink)
    {
        if (string.IsNullOrWhiteSpace(checkId))
            throw new ArgumentException("An automated acceptance check ID is required.", nameof(checkId));
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(sink);
        if (!_recorded.Add(checkId))
            throw new InvalidOperationException($"Automated acceptance check '{checkId}' already has a verdict for this run.");
        sink(checkId, passed, note);
    }

    internal void FailMissing(
        IEnumerable<string> requiredChecks,
        string note,
        Action<string, bool, string> sink)
    {
        ArgumentNullException.ThrowIfNull(requiredChecks);
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(sink);
        foreach (string checkId in requiredChecks)
        {
            if (string.IsNullOrWhiteSpace(checkId))
                throw new ArgumentException("An automated acceptance check ID is required.", nameof(requiredChecks));
            if (_recorded.Add(checkId))
                sink(checkId, false, note);
        }
    }
}
