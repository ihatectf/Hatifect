using System;
using System.Collections.Generic;
using System.Linq;

namespace Hatifect.UI.Stardew;

internal enum AcceptanceScenarioKind
{
    Smoke,
    Ui
}

internal enum AcceptanceScenarioExecution
{
    RuntimeBoot,
    SaveBootstrap,
    ReturnToTitle,
    Lifecycle,
    Inspector,
    Overlay,
    Input,
    NativeInput,
    TerminalGeneration,
    SaveSwitch,
    ActionPump,
    ActionReload,
    Environment,
    Observation,
    RetiredOverlayInput,
    LocaleScaleTheme,
    Performance,
    Contribution
}

internal sealed class AcceptanceScenario
{
    public AcceptanceScenario(
        string id,
        AcceptanceScenarioKind kind,
        bool requiresWorld,
        AcceptanceScenarioExecution execution,
        bool completesAsynchronously,
        string[] checks,
        bool includeInAggregate = true)
    {
        Id = id;
        Kind = kind;
        RequiresWorld = requiresWorld;
        Execution = execution;
        CompletesAsynchronously = completesAsynchronously;
        IncludeInAggregate = includeInAggregate;
        Checks = Array.AsReadOnly((string[])checks.Clone());
    }

    public AcceptanceScenario(UiAutomatedAcceptanceScenarioDescriptor contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        Id = contribution.Id;
        Kind = AcceptanceScenarioKind.Ui;
        RequiresWorld = contribution.RequiresWorld;
        Execution = AcceptanceScenarioExecution.Contribution;
        CompletesAsynchronously = false;
        Checks = contribution.Checks;
        IncludeInAggregate = contribution.IncludeInAggregate;
        Contribution = contribution;
    }

    private AcceptanceScenario(string id, IReadOnlyList<string> checks)
    {
        Id = id;
        Kind = AcceptanceScenarioKind.Ui;
        RequiresWorld = false;
        Execution = AcceptanceScenarioExecution.Contribution;
        CompletesAsynchronously = false;
        Checks = Array.AsReadOnly(checks.ToArray());
    }

    public static AcceptanceScenario Aggregate(IReadOnlyList<string> checks)
        => new("all", checks);

    public string Id { get; }
    public AcceptanceScenarioKind Kind { get; }
    public bool RequiresWorld { get; }
    public AcceptanceScenarioExecution Execution { get; }
    public bool CompletesAsynchronously { get; }
    public bool IncludeInAggregate { get; }
    public IReadOnlyList<string> Checks { get; }
    public UiAutomatedAcceptanceScenarioDescriptor? Contribution { get; }
}
