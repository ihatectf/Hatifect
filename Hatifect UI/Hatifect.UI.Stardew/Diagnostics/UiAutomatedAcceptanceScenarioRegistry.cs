using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Hatifect.UI.Stardew;

/// <summary>
/// Test-mode-only contribution point for synchronous, product-owned automated acceptance scenarios.
/// The registry deliberately retains no product delegates outside the exact automated harness environment.
/// </summary>
internal static class UiAutomatedAcceptanceScenarioRegistry
{
    private static readonly bool RegistrationEnabled = IsExactAutomatedEnvironment();
    private static readonly UiAutomatedAcceptanceScenarioStore Store = new();

    internal static bool IsRegistrationEnabled => RegistrationEnabled;

    internal static void Register(UiAutomatedAcceptanceScenarioDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!RegistrationEnabled) return;
        Store.Register(descriptor);
    }

    internal static UiAutomatedAcceptanceScenarioSnapshot Freeze()
    {
        if (!RegistrationEnabled) return UiAutomatedAcceptanceScenarioSnapshot.Empty;
        return Store.Freeze();
    }

    private static bool IsExactAutomatedEnvironment()
        => string.Equals(Environment.GetEnvironmentVariable("HATIFECT_TEST_MODE"), "1", StringComparison.Ordinal)
           && string.Equals(Environment.GetEnvironmentVariable("HATIFECT_TEST_AUTOMATED"), "1", StringComparison.Ordinal);
}

/// <summary>Deterministic mutable registration phase with one immutable freeze boundary.</summary>
internal sealed class UiAutomatedAcceptanceScenarioStore
{
    private readonly object _sync = new();
    private readonly List<UiAutomatedAcceptanceScenarioDescriptor> _registered = new();
    private UiAutomatedAcceptanceScenarioSnapshot? _snapshot;

    internal void Register(UiAutomatedAcceptanceScenarioDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        lock (_sync)
        {
            if (_snapshot != null)
                throw new InvalidOperationException(
                    "Automated TestHarness scenario registration is frozen after GameLaunched.");
            foreach (UiAutomatedAcceptanceScenarioDescriptor existing in _registered)
            {
                if (string.Equals(existing.Id, descriptor.Id, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Automated TestHarness scenario '{descriptor.Id}' is declared more than once.");
            }
            _registered.Add(descriptor);
        }
    }

    internal UiAutomatedAcceptanceScenarioSnapshot Freeze()
    {
        lock (_sync)
        {
            if (_snapshot != null) return _snapshot;

            var descriptors = _registered.ToArray();
            Array.Sort(descriptors, static (left, right) =>
            {
                int order = left.Order.CompareTo(right.Order);
                return order != 0 ? order : string.CompareOrdinal(left.Id, right.Id);
            });
            _snapshot = new UiAutomatedAcceptanceScenarioSnapshot(descriptors);
            return _snapshot;
        }
    }
}

/// <summary>
/// Immutable product-owned automated acceptance descriptor. Each action finishes synchronously;
/// an independent title scenario may have a second action after the real world transition.
/// </summary>
internal sealed class UiAutomatedAcceptanceScenarioDescriptor
{
    private readonly ReadOnlyCollection<string> _checks;

    internal UiAutomatedAcceptanceScenarioDescriptor(
        string id,
        int order,
        bool requiresWorld,
        IEnumerable<string> checks,
        Action<UiAutomatedAcceptanceScenarioContext> execute,
        bool includeInAggregate = true,
        Action<UiAutomatedAcceptanceScenarioContext>? afterReturnedToTitle = null)
    {
        if (string.IsNullOrWhiteSpace(id)
            || !string.Equals(id, id.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("Automated TestHarness scenario IDs must be nonempty and trimmed.", nameof(id));
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(execute);
        if (afterReturnedToTitle != null && (!requiresWorld || includeInAggregate))
            throw new ArgumentException("Return-to-title scenarios require an isolated world and cannot join the aggregate.");

        var copy = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (string check in checks)
        {
            if (string.IsNullOrWhiteSpace(check)
                || !string.Equals(check, check.Trim(), StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Automated TestHarness check IDs must be nonempty and trimmed.",
                    nameof(checks));
            }
            if (!unique.Add(check))
                throw new ArgumentException(
                    $"Automated TestHarness check '{check}' is declared more than once by scenario '{id}'.",
                    nameof(checks));
            copy.Add(check);
        }
        if (copy.Count == 0)
            throw new ArgumentException(
                $"Automated TestHarness scenario '{id}' must declare at least one check.",
                nameof(checks));

        Id = id;
        Order = order;
        RequiresWorld = requiresWorld;
        _checks = Array.AsReadOnly(copy.ToArray());
        Execute = execute;
        IncludeInAggregate = includeInAggregate;
        AfterReturnedToTitle = afterReturnedToTitle;
    }

    internal string Id { get; }
    internal int Order { get; }
    internal bool RequiresWorld { get; }
    internal IReadOnlyList<string> Checks => _checks;
    internal Action<UiAutomatedAcceptanceScenarioContext> Execute { get; }
    internal bool IncludeInAggregate { get; }
    internal Action<UiAutomatedAcceptanceScenarioContext>? AfterReturnedToTitle { get; }
}

/// <summary>
/// Immutable, ordinally ordered view of contributions captured after all GameLaunched handlers.
/// </summary>
internal sealed class UiAutomatedAcceptanceScenarioSnapshot
{
    private readonly ReadOnlyCollection<UiAutomatedAcceptanceScenarioDescriptor> _descriptors;

    internal static UiAutomatedAcceptanceScenarioSnapshot Empty { get; } =
        new(Array.Empty<UiAutomatedAcceptanceScenarioDescriptor>());

    internal UiAutomatedAcceptanceScenarioSnapshot(UiAutomatedAcceptanceScenarioDescriptor[] descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        _descriptors = Array.AsReadOnly((UiAutomatedAcceptanceScenarioDescriptor[])descriptors.Clone());
    }

    internal IReadOnlyList<UiAutomatedAcceptanceScenarioDescriptor> Descriptors => _descriptors;
}

/// <summary>
/// Active synchronous execution scope for one contributed scenario. It cannot record checks owned
/// by another descriptor, including while the aggregate <c>all</c> scenario is active.
/// </summary>
internal sealed class UiAutomatedAcceptanceScenarioContext
{
    private readonly string _scenarioId;
    private readonly IReadOnlyList<string> _checks;
    private readonly Action<string, bool, string> _record;
    private bool _active = true;

    internal UiAutomatedAcceptanceScenarioContext(
        string scenarioId,
        IReadOnlyList<string> checks,
        Action<string, bool, string> record)
    {
        _scenarioId = scenarioId;
        _checks = checks ?? throw new ArgumentNullException(nameof(checks));
        _record = record ?? throw new ArgumentNullException(nameof(record));
    }

    internal void Record(string checkId, bool passed, string note)
    {
        if (!_active)
            throw new InvalidOperationException(
                $"Automated TestHarness scenario '{_scenarioId}' attempted to record after its synchronous action completed.");
        if (string.IsNullOrWhiteSpace(checkId) || !ContainsCheck(checkId))
            throw new InvalidOperationException(
                $"Automated TestHarness scenario '{_scenarioId}' produced undeclared check '{checkId}'.");
        _record(checkId, passed, note ?? string.Empty);
    }

    internal void Complete() => _active = false;

    private bool ContainsCheck(string checkId)
    {
        for (int index = 0; index < _checks.Count; index++)
        {
            if (string.Equals(_checks[index], checkId, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
