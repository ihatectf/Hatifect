using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Hatifect.UI.Stardew;

internal sealed class UiAcceptanceScenarioCatalog
{
    private readonly IReadOnlyDictionary<string, AcceptanceScenario> _scenarios;
    private readonly IReadOnlyList<AcceptanceScenario> _allUiScenarios;

    private UiAcceptanceScenarioCatalog(
        IDictionary<string, AcceptanceScenario> scenarios,
        IReadOnlyList<AcceptanceScenario> allUiScenarios)
    {
        _scenarios = new ReadOnlyDictionary<string, AcceptanceScenario>(
            new Dictionary<string, AcceptanceScenario>(scenarios, StringComparer.Ordinal));
        _allUiScenarios = Array.AsReadOnly(allUiScenarios.ToArray());
    }

    public IReadOnlyList<AcceptanceScenario> AllUiScenarios => _allUiScenarios;

    public bool ContainsScenario(string scenarioId) => _scenarios.ContainsKey(scenarioId);

    public bool IsContributedScenario(string scenarioId)
        => _scenarios.TryGetValue(scenarioId, out AcceptanceScenario? scenario)
           && scenario.Contribution != null;

    public AcceptanceScenario GetNamedScenario(string scenarioId)
    {
        if (_scenarios.TryGetValue(scenarioId, out AcceptanceScenario? scenario)
            && !string.Equals(scenario.Id, "all", StringComparison.Ordinal))
            return scenario;
        throw new InvalidOperationException($"Automated TestHarness scenario '{scenarioId}' is not registered.");
    }

    public bool RequiresWorld(string scenarioId)
    {
        if (string.Equals(scenarioId, "all", StringComparison.Ordinal))
        {
            foreach (AcceptanceScenario scenario in _allUiScenarios)
            {
                if (scenario.RequiresWorld) return true;
            }
            return false;
        }
        return GetNamedScenario(scenarioId).RequiresWorld;
    }

    public bool IsDeclaredCheck(string scenarioId, string checkId)
        => TryGetChecks(scenarioId, out IReadOnlyList<string> checks)
           && ContainsCheck(checks, checkId);

    public bool TryGetChecks(string scenarioId, out IReadOnlyList<string> checks)
    {
        if (_scenarios.TryGetValue(scenarioId, out AcceptanceScenario? scenario))
        {
            checks = scenario.Checks;
            return true;
        }
        checks = Array.Empty<string>();
        return false;
    }

    private static bool ContainsCheck(IReadOnlyList<string> checks, string checkId)
    {
        for (int index = 0; index < checks.Count; index++)
        {
            if (string.Equals(checks[index], checkId, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static readonly AcceptanceScenario[] BuiltInScenarioRegistry =
    {
        new("runtime.boot", AcceptanceScenarioKind.Smoke, false, AcceptanceScenarioExecution.RuntimeBoot, false,
            new[] { "runtime.boot.loaded" }),
        new("save.bootstrap", AcceptanceScenarioKind.Smoke, false, AcceptanceScenarioExecution.SaveBootstrap, false,
            new[] { "save.bootstrap.reload" }),
        new("runtime.return-to-title", AcceptanceScenarioKind.Smoke, true, AcceptanceScenarioExecution.ReturnToTitle, false,
            new[] { "runtime.return-to-title.reset" }),
        new("semantic.lifecycle", AcceptanceScenarioKind.Ui, false, AcceptanceScenarioExecution.Lifecycle, false, new[]
        {
            "semantic.lifecycle.visual",
            "semantic.lifecycle.focus",
            "semantic.lifecycle.close-reopen"
        }),
        new("semantic.inspector", AcceptanceScenarioKind.Ui, false, AcceptanceScenarioExecution.Inspector, false, new[]
        {
            "semantic.inspector.capture",
            "semantic.inspector.reveal",
            "semantic.inspector.close"
        }),
        new("semantic.overlay", AcceptanceScenarioKind.Ui, true, AcceptanceScenarioExecution.Overlay, false, new[]
        {
            "semantic.overlay.visual",
            "semantic.overlay.context",
            "semantic.overlay.dismiss"
        }),
        new("semantic.input", AcceptanceScenarioKind.Ui, false, AcceptanceScenarioExecution.Input, false, new[]
        {
            "semantic.input.pointer",
            "semantic.input.keyboard",
            "semantic.input.controller",
            "semantic.input.text"
        }),
        new("semantic.input.native", AcceptanceScenarioKind.Ui, true, AcceptanceScenarioExecution.NativeInput, true, new[]
        {
            "semantic.input.native.pointer",
            "semantic.input.native.keyboard",
            "semantic.input.native.text",
            "semantic.input.native.backspace"
        }, includeInAggregate: false),
        new("semantic.input.retired-overlay", AcceptanceScenarioKind.Ui, true, AcceptanceScenarioExecution.RetiredOverlayInput, true, new[]
        {
            "semantic.input.retired-overlay.keyboard",
            "semantic.input.retired-overlay.retry"
        }, includeInAggregate: false),
        new("semantic.observation", AcceptanceScenarioKind.Ui, true, AcceptanceScenarioExecution.Observation, true, new[]
        {
            "semantic.observation.api",
            "semantic.observation.initial",
            "semantic.observation.inert",
            "semantic.observation.updated",
            "semantic.observation.reentrant",
            "semantic.observation.environment",
            "semantic.observation.retirement",
            "semantic.observation.reopen",
            "semantic.observation.native-owner",
            "semantic.observation.restored"
        }, includeInAggregate: false),
        new("semantic.environment", AcceptanceScenarioKind.Ui, true, AcceptanceScenarioExecution.Environment, true, new[]
        {
            "semantic.environment.window.facets",
            "semantic.environment.window.rejection",
            "semantic.environment.window.reentry",
            "semantic.environment.window.idle",
            "semantic.environment.window.delivery",
            "semantic.environment.window.automatic",
            "semantic.environment.terminal.facets",
            "semantic.environment.terminal.rejection",
            "semantic.environment.terminal.reentry",
            "semantic.environment.terminal.idle",
            "semantic.environment.terminal.delivery",
            "semantic.environment.terminal.automatic",
            "semantic.environment.hud.facets",
            "semantic.environment.hud.rejection",
            "semantic.environment.hud.reentry",
            "semantic.environment.hud.idle",
            "semantic.environment.hud.delivery",
            "semantic.environment.hud.automatic",
            "semantic.environment.active-menu.facets",
            "semantic.environment.active-menu.rejection",
            "semantic.environment.active-menu.reentry",
            "semantic.environment.active-menu.idle",
            "semantic.environment.active-menu.delivery",
            "semantic.environment.active-menu.automatic",
            "semantic.environment.active-menu.show-owner",
        }, includeInAggregate: false),
        new("semantic.actions.reload", AcceptanceScenarioKind.Ui, true, AcceptanceScenarioExecution.ActionReload, true, new[]
        {
            "semantic.actions.reload.window.publication",
            "semantic.actions.reload.window.generation",
            "semantic.actions.reload.window.retirement",
            "semantic.actions.reload.terminal.publication",
            "semantic.actions.reload.terminal.generation",
            "semantic.actions.reload.terminal.retirement",
            "semantic.actions.reload.hud.publication",
            "semantic.actions.reload.hud.generation",
            "semantic.actions.reload.hud.retirement",
            "semantic.actions.reload.active-menu.publication",
            "semantic.actions.reload.active-menu.generation",
            "semantic.actions.reload.active-menu.retirement",
            "semantic.actions.reload.hud-hide.failed-cleanup",
            "semantic.actions.reload.hud-hide.retry",
            "semantic.actions.reload.hud-dispose.failed-cleanup",
            "semantic.actions.reload.hud-dispose.retry",
            "semantic.actions.reload.active-menu-hide.failed-cleanup",
            "semantic.actions.reload.active-menu-hide.retry"
        }, includeInAggregate: false),
        new("semantic.actions.terminal", AcceptanceScenarioKind.Ui, true, AcceptanceScenarioExecution.TerminalGeneration, true, new[]
        {
            "semantic.actions.terminal.same.transition",
            "semantic.actions.terminal.same.delivery",
            "semantic.actions.terminal.route.transition",
            "semantic.actions.terminal.route.delivery",
            "semantic.actions.terminal.failed-open.transition",
            "semantic.actions.terminal.failed-open.delivery",
            "semantic.actions.terminal.failed-route.transition",
            "semantic.actions.terminal.failed-route.delivery"
        }, includeInAggregate: false),
        new("semantic.actions.save-switch", AcceptanceScenarioKind.Ui, true, AcceptanceScenarioExecution.SaveSwitch, true, new[]
        {
            "semantic.actions.save-switch.copies",
            "semantic.actions.save-switch.window.pending",
            "semantic.actions.save-switch.window.retirement",
            "semantic.actions.save-switch.window.late",
            "semantic.actions.save-switch.window.delivery",
            "semantic.actions.save-switch.terminal.pending",
            "semantic.actions.save-switch.terminal.retirement",
            "semantic.actions.save-switch.terminal.late",
            "semantic.actions.save-switch.terminal.delivery",
            "semantic.actions.save-switch.hud.pending",
            "semantic.actions.save-switch.hud.retirement",
            "semantic.actions.save-switch.hud.late",
            "semantic.actions.save-switch.hud.delivery",
            "semantic.actions.save-switch.active-menu.pending",
            "semantic.actions.save-switch.active-menu.retirement",
            "semantic.actions.save-switch.active-menu.late",
            "semantic.actions.save-switch.active-menu.delivery",
            "semantic.actions.save-switch.lifecycle"
        }, includeInAggregate: false),
        new("semantic.actions.pump", AcceptanceScenarioKind.Ui, true, AcceptanceScenarioExecution.ActionPump, true, new[]
        {
            "semantic.actions.pump.menu",
            "semantic.actions.pump.reopen",
            "semantic.actions.pump.hud",
            "semantic.actions.pump.context"
        }, includeInAggregate: false),
        new("semantic.locale-scale-theme", AcceptanceScenarioKind.Ui, true, AcceptanceScenarioExecution.LocaleScaleTheme, true, new[]
        {
            "semantic.locale.en",
            "semantic.locale.ru",
            "semantic.scale.75",
            "semantic.scale.100",
            "semantic.scale.125",
            "semantic.scale.150",
            "semantic.viewport.reflow",
            "semantic.theme.matrix"
        }),
        new("semantic.performance", AcceptanceScenarioKind.Ui, false, AcceptanceScenarioExecution.Performance, true,
            new[] { "semantic.performance.steady" })
    };

    internal static UiAcceptanceScenarioCatalog Create(UiAutomatedAcceptanceScenarioSnapshot contributions)
    {
        ArgumentNullException.ThrowIfNull(contributions);
        var scenarios = new Dictionary<string, AcceptanceScenario>(StringComparer.Ordinal);
        var checkOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var allChecks = new List<string>();
        var allScenarios = new List<AcceptanceScenario>();
        foreach (AcceptanceScenario scenario in BuiltInScenarioRegistry)
        {
            AddScenario(scenarios, checkOwners, scenario);
            if (scenario.Kind == AcceptanceScenarioKind.Ui
                && scenario.IncludeInAggregate
                && scenario.Execution != AcceptanceScenarioExecution.Performance)
            {
                allChecks.AddRange(scenario.Checks);
                allScenarios.Add(scenario);
            }
        }

        foreach (UiAutomatedAcceptanceScenarioDescriptor contribution in contributions.Descriptors)
        {
            var scenario = new AcceptanceScenario(contribution);
            AddScenario(scenarios, checkOwners, scenario);
            if (!contribution.IncludeInAggregate) continue;
            allChecks.AddRange(scenario.Checks);
            allScenarios.Add(scenario);
        }

        // Performance stays last, after the rendered-state matrix and product-owned contributions.
        foreach (AcceptanceScenario scenario in BuiltInScenarioRegistry)
        {
            if (scenario.Kind != AcceptanceScenarioKind.Ui
                || scenario.Execution != AcceptanceScenarioExecution.Performance)
                continue;
            allChecks.AddRange(scenario.Checks);
            allScenarios.Add(scenario);
        }

        if (allChecks.Count == 0)
            throw new InvalidOperationException("Automated TestHarness scenario 'all' has no named checks.");
        ValidateAllExecution(allScenarios);
        if (!scenarios.TryAdd("all", AcceptanceScenario.Aggregate(allChecks)))
            throw new InvalidOperationException("Automated TestHarness scenario 'all' is reserved.");
        return new UiAcceptanceScenarioCatalog(scenarios, allScenarios);
    }

    private static void AddScenario(
        IDictionary<string, AcceptanceScenario> scenarios,
        IDictionary<string, string> checkOwners,
        AcceptanceScenario scenario)
    {
        if (!scenarios.TryAdd(scenario.Id, scenario))
            throw new InvalidOperationException($"Automated TestHarness scenario '{scenario.Id}' is declared more than once.");

        foreach (string check in scenario.Checks)
        {
            if (string.IsNullOrWhiteSpace(check))
                throw new InvalidOperationException(
                    $"Automated TestHarness scenario '{scenario.Id}' has an empty check ID.");
            if (!checkOwners.TryAdd(check, scenario.Id))
                throw new InvalidOperationException(
                    $"Automated TestHarness check '{check}' is declared by both "
                    + $"'{checkOwners[check]}' and '{scenario.Id}'.");
        }
    }

    private static void ValidateAllExecution(IReadOnlyList<AcceptanceScenario> allScenarios)
    {
        int terminalCount = 0;
        for (int index = 0; index < allScenarios.Count; index++)
        {
            AcceptanceScenario scenario = allScenarios[index];
            bool asynchronous = scenario.Execution is AcceptanceScenarioExecution.Performance
                or AcceptanceScenarioExecution.LocaleScaleTheme;
            if (scenario.CompletesAsynchronously != asynchronous)
                throw new InvalidOperationException(
                    $"Automated TestHarness scenario '{scenario.Id}' has an inconsistent asynchronous execution contract.");
            if (scenario.Execution != AcceptanceScenarioExecution.Performance) continue;
            terminalCount++;
            if (index != allScenarios.Count - 1)
                throw new InvalidOperationException(
                    $"Automated TestHarness performance scenario '{scenario.Id}' must be last in 'all'.");
        }

        if (terminalCount != 1)
            throw new InvalidOperationException("Automated TestHarness scenario 'all' must end with exactly one performance scenario.");
    }

}
