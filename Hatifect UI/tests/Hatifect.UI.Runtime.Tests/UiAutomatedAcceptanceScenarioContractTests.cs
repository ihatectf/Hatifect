using Hatifect.UI.Experience;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class UiAutomatedAcceptanceScenarioContractTests
{
    [Fact]
    public void ReturnToTitleFactoryRequiresIndependentWorldAndCopiesChecks()
    {
        string[] checks = { "ca.open", "ca.restored" };
        Action<IUiAutomatedAcceptanceContext> prepare = _ => { };
        Action<IUiAutomatedAcceptanceContext> verify = _ => { };

        UiAutomatedAcceptanceScenario scenario = UiAutomatedAcceptanceScenario.ForReturnToTitle(
            "ca.title", 740, checks, prepare, verify);
        checks[0] = "changed";

        Assert.True(scenario.RequiresWorld);
        Assert.False(scenario.IncludeInAggregate);
        Assert.Equal(new[] { "ca.open", "ca.restored" }, scenario.Checks);
        Assert.Same(prepare, scenario.Execute);
        Assert.Same(verify, scenario.AfterReturnedToTitle);
    }

    [Fact]
    public void ExistingConstructorRemainsSynchronous()
    {
        var scenario = new UiAutomatedAcceptanceScenario(
            "ca.open", 700, false, new[] { "ca.open.check" }, _ => { });

        Assert.Null(scenario.AfterReturnedToTitle);
        Assert.True(scenario.IncludeInAggregate);
        Assert.False(scenario.RequiresWorld);
    }

    [Fact]
    public void ReturnToTitleFactoryRejectsMissingVerification()
        => Assert.Throws<ArgumentNullException>(() => UiAutomatedAcceptanceScenario.ForReturnToTitle(
            "ca.title", 740, new[] { "ca.restored" }, _ => { }, null!));
}
