using Hatifect.UI.Stardew;
using Xunit;

namespace Hatifect.UI.Stardew.Tests;

public sealed class UiAutomatedAcceptanceScenarioRegistryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void ReturnToTitleDescriptorRejectsMissingWorldOrAggregate(bool requiresWorld, bool aggregate)
        => Assert.Throws<ArgumentException>(() => new UiAutomatedAcceptanceScenarioDescriptor(
            "ca.title", 740, requiresWorld, new[] { "ca.restored" }, _ => { }, aggregate, _ => { }));

    [Fact]
    public void ReturnToTitleDescriptorRetainsBothPhasesInIndependentWorldScenario()
    {
        Action<UiAutomatedAcceptanceScenarioContext> prepare = _ => { };
        Action<UiAutomatedAcceptanceScenarioContext> verify = _ => { };
        var descriptor = new UiAutomatedAcceptanceScenarioDescriptor(
            "ca.title", 740, true, new[] { "ca.restored" }, prepare, false, verify);

        Assert.Same(prepare, descriptor.Execute);
        Assert.Same(verify, descriptor.AfterReturnedToTitle);
        Assert.True(descriptor.RequiresWorld);
        Assert.False(descriptor.IncludeInAggregate);
    }

    [Fact]
    public void StoreFreezesInOrderThenOrdinalIdOrder()
    {
        var store = new UiAutomatedAcceptanceScenarioStore();
        store.Register(Descriptor("semantic.zeta", 200, "semantic.zeta.check"));
        store.Register(Descriptor("semantic.beta", 100, "semantic.beta.check"));
        store.Register(Descriptor("semantic.alpha", 100, "semantic.alpha.check"));

        UiAutomatedAcceptanceScenarioSnapshot first = store.Freeze();
        UiAutomatedAcceptanceScenarioSnapshot second = store.Freeze();

        Assert.Same(first, second);
        Assert.Equal(
            new[] { "semantic.alpha", "semantic.beta", "semantic.zeta" },
            first.Descriptors.Select(descriptor => descriptor.Id));
    }

    [Fact]
    public void StoreRejectsDuplicateIdsAndRegistrationAfterFreeze()
    {
        var store = new UiAutomatedAcceptanceScenarioStore();
        store.Register(Descriptor("semantic.alpha", 100, "semantic.alpha.check"));

        Assert.Throws<InvalidOperationException>(() =>
            store.Register(Descriptor("semantic.alpha", 200, "semantic.other.check")));

        store.Freeze();
        Assert.Throws<InvalidOperationException>(() =>
            store.Register(Descriptor("semantic.beta", 200, "semantic.beta.check")));
    }

    [Fact]
    public void DescriptorRejectsDuplicateChecks()
    {
        Assert.Throws<ArgumentException>(() => new UiAutomatedAcceptanceScenarioDescriptor(
            "semantic.alpha",
            100,
            requiresWorld: false,
            new[] { "semantic.alpha.check", "semantic.alpha.check" },
            _ => { }));
    }

    [Fact]
    public void ContextRejectsForeignChecksAndUseAfterCompletion()
    {
        var recorded = new List<string>();
        var context = new UiAutomatedAcceptanceScenarioContext(
            "semantic.alpha",
            new[] { "semantic.alpha.check" },
            (id, _, _) => recorded.Add(id));

        Assert.Throws<InvalidOperationException>(() =>
            context.Record("semantic.beta.check", true, "foreign"));
        context.Record("semantic.alpha.check", true, "owned");
        context.Complete();
        Assert.Throws<InvalidOperationException>(() =>
            context.Record("semantic.alpha.check", true, "late"));
        Assert.Equal(new[] { "semantic.alpha.check" }, recorded);
    }

    private static UiAutomatedAcceptanceScenarioDescriptor Descriptor(
        string id,
        int order,
        string check)
        => new(id, order, requiresWorld: true, new[] { check }, _ => { });
}
