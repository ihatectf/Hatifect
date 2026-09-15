using Hatifect.UI.Stardew;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class UiAcceptanceScenarioRunnerTests
{
    [Fact]
    public void AggregateResumesAfterEachAsynchronousScenarioWithoutReplayingIt()
    {
        var store = new UiAutomatedAcceptanceScenarioStore();
        store.Register(Contribution("product.late", 20));
        store.Register(Contribution("product.early", 10));
        UiAcceptanceScenarioCatalog catalog = UiAcceptanceScenarioCatalog.Create(store.Freeze());
        var executed = new List<string>();
        var runner = new UiAcceptanceScenarioRunner(catalog, scenario =>
        {
            executed.Add(scenario.Id);
            return scenario.CompletesAsynchronously;
        });

        Assert.True(runner.ContinueAggregate());
        Assert.Equal(new[]
        {
            "semantic.lifecycle", "semantic.inspector", "semantic.overlay",
            "semantic.input", "semantic.locale-scale-theme"
        }, executed);

        Assert.True(runner.ContinueAggregate());
        Assert.Equal(new[]
        {
            "semantic.lifecycle", "semantic.inspector", "semantic.overlay",
            "semantic.input", "semantic.locale-scale-theme",
            "product.early", "product.late", "semantic.performance"
        }, executed);

        Assert.False(runner.ContinueAggregate());
        Assert.False(runner.ContinueAggregate());
        Assert.Equal(8, executed.Count);
    }

    [Fact]
    public void ExecutionFailureEscapesWithoutStartingLaterScenarios()
    {
        UiAcceptanceScenarioCatalog catalog = UiAcceptanceScenarioCatalog.Create(
            UiAutomatedAcceptanceScenarioSnapshot.Empty);
        var failure = new InvalidOperationException("Scene failed.");
        var executed = new List<string>();
        var runner = new UiAcceptanceScenarioRunner(catalog, scenario =>
        {
            executed.Add(scenario.Id);
            throw failure;
        });

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => runner.ContinueAggregate()));
        Assert.Equal(new[] { "semantic.lifecycle" }, executed);
    }

    [Theory]
    [InlineData("runtime.boot", "product.check", "runtime.boot")]
    [InlineData("product.check", "semantic.lifecycle.visual", "semantic.lifecycle.visual")]
    [InlineData("all", "product.check", "reserved")]
    public void CatalogRejectsCollisionsWithBuiltInScenariosAndChecks(string id, string check, string reason)
    {
        var contribution = new UiAutomatedAcceptanceScenarioDescriptor(
            id, 0, false, new[] { check }, _ => { });
        var snapshot = new UiAutomatedAcceptanceScenarioSnapshot(new[] { contribution });

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => UiAcceptanceScenarioCatalog.Create(snapshot));

        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IndependentContributionKeepsItsChecksOutOfTheAggregate()
    {
        var contribution = new UiAutomatedAcceptanceScenarioDescriptor(
            "product.title", 0, true, new[] { "product.title.closed" }, _ => { },
            includeInAggregate: false, afterReturnedToTitle: _ => { });
        UiAcceptanceScenarioCatalog catalog = UiAcceptanceScenarioCatalog.Create(
            new UiAutomatedAcceptanceScenarioSnapshot(new[] { contribution }));

        Assert.True(catalog.IsContributedScenario("product.title"));
        Assert.True(catalog.RequiresWorld("product.title"));
        Assert.True(catalog.IsDeclaredCheck("product.title", "product.title.closed"));
        Assert.False(catalog.IsDeclaredCheck("all", "product.title.closed"));
        Assert.DoesNotContain(catalog.AllUiScenarios, scenario => scenario.Id == "product.title");
        Assert.Same(contribution, catalog.GetNamedScenario("product.title").Contribution);
    }

    [Fact]
    public void AggregateChecksFollowExecutionOrderAndExcludeIndependentNativeScenarios()
    {
        UiAcceptanceScenarioCatalog catalog = UiAcceptanceScenarioCatalog.Create(
            UiAutomatedAcceptanceScenarioSnapshot.Empty);

        Assert.True(catalog.TryGetChecks("all", out IReadOnlyList<string> checks));
        Assert.Equal(catalog.AllUiScenarios.SelectMany(scenario => scenario.Checks), checks);
        Assert.Equal(checks.Count, checks.Distinct(StringComparer.Ordinal).Count());
        Assert.True(catalog.RequiresWorld("all"));
        Assert.False(catalog.RequiresWorld("runtime.boot"));
        Assert.True(catalog.ContainsScenario("semantic.input.native"));
        Assert.DoesNotContain(catalog.AllUiScenarios, scenario => scenario.Id == "semantic.input.native");
    }

    [Theory]
    [InlineData("Semantic.lifecycle")]
    [InlineData("unknown")]
    public void UnknownScenarioIsNotSilentlyResolved(string id)
    {
        UiAcceptanceScenarioCatalog catalog = UiAcceptanceScenarioCatalog.Create(
            UiAutomatedAcceptanceScenarioSnapshot.Empty);

        Assert.False(catalog.ContainsScenario(id));
        Assert.False(catalog.TryGetChecks(id, out IReadOnlyList<string> checks));
        Assert.Empty(checks);
        Assert.Throws<InvalidOperationException>(() => catalog.GetNamedScenario(id));
        Assert.Throws<InvalidOperationException>(() => catalog.RequiresWorld(id));
        Assert.Throws<InvalidOperationException>(() => catalog.GetNamedScenario("all"));
    }

    private static UiAutomatedAcceptanceScenarioDescriptor Contribution(string id, int order)
        => new(id, order, false, new[] { id + ".passed" }, _ => { });
}
