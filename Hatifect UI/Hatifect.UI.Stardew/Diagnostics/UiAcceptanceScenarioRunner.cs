using System;

namespace Hatifect.UI.Stardew;

/// <summary>Resumes the aggregate after a frame-driven scenario yields control to the game.</summary>
internal sealed class UiAcceptanceScenarioRunner
{
    private readonly UiAcceptanceScenarioCatalog _catalog;
    private readonly Func<AcceptanceScenario, bool> _execute;
    private int _nextScenario;

    internal UiAcceptanceScenarioRunner(UiAcceptanceScenarioCatalog catalog, Func<AcceptanceScenario, bool> execute)
    {
        _catalog = catalog;
        _execute = execute;
    }

    internal bool ContinueAggregate()
    {
        while (_nextScenario < _catalog.AllUiScenarios.Count)
        {
            // Consume before executing: a yielded scenario must not be started again on resume.
            AcceptanceScenario scenario = _catalog.AllUiScenarios[_nextScenario++];
            if (_execute(scenario)) return true;
        }
        return false;
    }
}
