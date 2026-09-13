using System.Diagnostics;
using Hatifect.UI.Experience;
using Hatifect.UI.Semantics;
using Xunit;
using Xunit.Abstractions;

namespace Hatifect.UI.Planning.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentPlanningPerformanceCollection
{
    public const string Name = "Environment planning performance";
}

[Collection(EnvironmentPlanningPerformanceCollection.Name)]
public sealed class EnvironmentPlanningPerformanceTests
{
    private readonly ITestOutputHelper _output;
    public EnvironmentPlanningPerformanceTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "performance")]
    public void RepeatedEnvironmentPlanningKeepsTraceBoundedWithinTheUiBudget(bool needsCoverageFallback)
    {
        const int samples = 600;
        var id = new UiSymbolId("Hatifect.Tests", "environment/performance");
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Environment performance")
            .Browse("Items", new UiCollectionSource<int>(Enumerable.Range(0, 100).ToArray(), value => id.Child("item/" + value)))
            .Search("Query", new UiState<string>(string.Empty))
            .Element("Details", new UiState<string>("Ready"), needsCoverageFallback
                ? new[] { UiCapabilities.Inspect, UiCapabilities.Navigate } : new[] { UiCapabilities.Inspect })
            .Actions("Commands", new UiActionDefinition(id.Child("action"), "Open", () => { }))
            .Build();
        var wide = UiHostContext.InEnvironment(UiHostKind.Window, new UiEnvironment(
            new UiEnvironmentViewport(1440, 900), 1.5f, UiInputMode.Keyboard, "en", id.Child("theme")));
        var controller = UiHostContext.InEnvironment(UiHostKind.Window, new UiEnvironment(
            new UiEnvironmentViewport(720, 500), 2, UiInputMode.Controller, "ru", id.Child("theme"),
            new UiAccessibilityPreferences(ReducedMotion: true)));
        var planner = new UiPresentationPlanner();
        UiPresentationPlan first = planner.Plan(experience, wide);
        var elapsed = new double[samples];
        int totalElements = 0, totalGlobalDecisions = 0, maximumElementDecisions = 0;
        UiPresentationPlan last = first;
        for (int index = 0; index < 100; index++) _ = planner.Plan(experience, index % 2 == 0 ? wide : controller);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < samples; index++)
        {
            long started = Stopwatch.GetTimestamp();
            last = planner.Plan(experience, index % 2 == 0 ? wide : controller);
            totalElements += last.Elements.Count;
            totalGlobalDecisions += last.Decisions.Count;
            foreach (UiPlannedElement element in last.Elements)
                maximumElementDecisions = Math.Max(maximumElementDecisions, element.Decisions.Count);
            elapsed[index] = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Array.Sort(elapsed);
        double p95 = elapsed[(int)Math.Ceiling(samples * .95) - 1];
        double p99 = elapsed[(int)Math.Ceiling(samples * .99) - 1];
        double bytes = allocated / (double)samples;
        _output.WriteLine($"environment-planning fallback={needsCoverageFallback} samples={samples} p95Ms={p95:F6} p99Ms={p99:F6} bytesPerPlan={bytes:F2} maxElementDecisions={maximumElementDecisions}");

        Assert.Equal(4 * samples, totalElements);
        Assert.Equal(8 * samples, totalGlobalDecisions);
        Assert.InRange(maximumElementDecisions, 2, 14);
        Assert.Equal(UiPresentationProfiles.Wide.Id, first.Host.Profile);
        Assert.Equal(UiPresentationProfiles.Controller.Id, last.Host.Profile);
        Assert.Equal(new UiSymbolId("Hatifect.UI", "presentation/Route"), last.Elements[2].Presentation);
        // Existing PERFORMANCE_BUDGETS.json limits; this measures planning for this four-element
        // fixture, not native frame/render cost or arbitrarily large author models.
        Assert.True(p95 <= 2, $"p95 {p95:F6} ms exceeds 2 ms.");
        Assert.True(p99 <= 4, $"p99 {p99:F6} ms exceeds 4 ms.");
        Assert.True(bytes <= 16384, $"{bytes:F2} bytes per plan exceeds 16384 bytes.");
    }
}
