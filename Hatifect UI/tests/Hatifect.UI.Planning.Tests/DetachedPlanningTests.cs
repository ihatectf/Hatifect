using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Semantics;
using Xunit;

namespace Hatifect.UI.Planning.Tests;

public sealed class DetachedPlanningTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DetachedAndExperiencePlansPreserveOrderRecipesAndFullTrace(bool explicitPresentation)
    {
        var experience = ExperienceBuilderTests.CreateExperience();
        var input = UiPlanningInput.Capture(experience);
        var host = UiHostContext.InEnvironment(UiHostKind.Window, new UiEnvironment(
            new UiEnvironmentViewport(720, 500), 1.5f, UiInputMode.Controller, "ru", experience.Id.Child("theme")));
        UiPresentationDefinition? presentation = explicitPresentation
            ? Assert.IsType<UiPresentationDefinition>(new UiCompiler().Compile(
                "presentation Storage\nItems -> Secondary\nItems\n    view = List\n",
                experience.CreateBindingContext(), "storage.presentation").Definition) : null;
        var planner = new UiPresentationPlanner();

        UiPresentationPlan live = planner.Plan(experience, host, presentation);
        UiPresentationPlan detached = planner.PlanInput(input, host, presentation);

        Assert.Equal(experience.Elements.Select(e => e.Id), input.Elements.Select(e => e.Id));
        Assert.Equal(live.Experience, detached.Experience);
        Assert.Equal(live.Pattern, detached.Pattern);
        Assert.Same(host, detached.Host);
        Assert.Equal(live.Decisions, detached.Decisions);
        Assert.Equal(live.Elements.Select(e => (e.Element, e.Region, e.Presentation)),
            detached.Elements.Select(e => (e.Element, e.Region, e.Presentation)));
        foreach (var pair in live.Elements.Zip(detached.Elements))
            Assert.Equal(pair.First.Decisions, pair.Second.Decisions);
        var collection = input.Elements.Single(e => e.IsCollection);
        Assert.Equal(live.CollectionRecipeFor(collection.Id), detached.CollectionRecipeFor(collection.Id));
    }

    [Fact]
    public void LegacySelectCollectionKeepsListAndRecipeWithoutGuessingFromDataType()
    {
        var id = new UiSymbolId("Hatifect.Tests", "detached");
        var collection = new UiCollectionSource<int>(new[] { 1 }, value => id.Child("item/" + value));
        var experience = new UiExperienceBuilder(id, "Selection").Element("Selection", collection, UiCapabilities.Select).Build();
        Assert.Null(Assert.Single(experience.Elements).DataType);
        var input = UiPlanningInput.Capture(experience);

        var plan = new UiPresentationPlanner().PlanInput(input, new UiHostContext(UiHostKind.Window, UiPresentationProfiles.Wide));

        Assert.True(Assert.Single(input.Elements).IsCollection);
        var element = Assert.Single(plan.Elements);
        Assert.Equal(new UiSymbolId("Hatifect.UI", "presentation/List"), element.Presentation);
        Assert.Equal("Default", plan.CollectionRecipeFor(element.Element).Density);
    }

    [Fact]
    public void DetachedRejectionPreservesRealCandidatesAndDoesNotReturnPartialPlan()
    {
        var id = new UiSymbolId("Hatifect.Tests", "rejected");
        var experience = new UiExperienceBuilder(id, "Rejected")
            .Element("Impossible", new UiState<string>("Ready"), UiCapabilities.Inspect, UiCapabilities.Search).Build();
        var planner = new UiPresentationPlanner();
        var host = new UiHostContext(UiHostKind.Window, UiPresentationProfiles.Wide);

        var live = Assert.Throws<UiPlanningException>(() => planner.Plan(experience, host));
        var detached = Assert.Throws<UiPlanningException>(() => planner.PlanInput(UiPlanningInput.Capture(experience), host));

        Assert.Equal(live.Element, detached.Element);
        Assert.Equal(live.Decisions, detached.Decisions);
        var rejected = detached.Decisions.Where(d => d.Code == UiPlanDecisionCode.PresentationRejected).ToArray();
        Assert.Equal(12, rejected.Length);
        Assert.All(rejected, decision => Assert.NotNull(decision.Candidate));
    }

    [Fact]
    public void DetachedInputCopiesCallerCollectionsAndRejectsInvalidIdentities()
    {
        var id = new UiSymbolId("Hatifect.Tests", "immutable");
        var capabilities = new List<UiCapability> { UiCapabilities.Monitor };
        var element = new UiPlanningElement(id.Child("value"), capabilities, false);
        var elements = new List<UiPlanningElement> { element };
        var input = new UiPlanningInput(id, elements);
        capabilities.Clear();
        elements.Clear();

        var plan = new UiPresentationPlanner().PlanInput(input, new UiHostContext(UiHostKind.Window, UiPresentationProfiles.Wide));

        Assert.Equal(UiCapabilities.Monitor, Assert.Single(Assert.Single(input.Elements).Capabilities));
        Assert.Equal(new UiSymbolId("Hatifect.UI", "presentation/Status"), Assert.Single(plan.Elements).Presentation);
        Assert.Throws<ArgumentException>(() => new UiPlanningInput(default, new[] { element }));
        Assert.Throws<ArgumentException>(() => new UiPlanningInput(id, new[] { element, element }));
        Assert.Throws<ArgumentException>(() => new UiPlanningInput(id, new UiPlanningElement[] { null! }));
        Assert.Throws<ArgumentException>(() => new UiPlanningInput(id, Array.Empty<UiPlanningElement>()));
        Assert.Throws<ArgumentException>(() => new UiPlanningInput(id,
            new[] { new UiPlanningElement(id, new[] { UiCapabilities.Monitor }, false) }));
        Assert.Throws<ArgumentException>(() => new UiPlanningInput(id,
            new[] { new UiPlanningElement(new UiSymbolId("Other", "value"), new[] { UiCapabilities.Monitor }, false) }));
        Assert.Throws<ArgumentException>(() => new UiPlanningElement(default, new[] { UiCapabilities.Monitor }, false));
        Assert.Throws<ArgumentException>(() => new UiPlanningElement(id, new UiCapability[] { null! }, false));
        Assert.Throws<ArgumentException>(() => new UiPlanningElement(id, new[] { new UiCapability(default, "invalid") }, false));
    }

    [Fact]
    public void CaptureAndDetachedPlanningNeverReadOrSubscribeToSource()
    {
        var id = new UiSymbolId("Hatifect.Tests", "no-source-access");
        var experience = new UiExperienceBuilder(id, "Detached")
            .Element("Status", new UnreadableSource(), UiCapabilities.Monitor).Build();

        var input = UiPlanningInput.Capture(experience);
        var plan = new UiPresentationPlanner().PlanInput(input,
            new UiHostContext(UiHostKind.Window, UiPresentationProfiles.Wide));

        Assert.Equal(new UiSymbolId("Hatifect.UI", "presentation/Status"), Assert.Single(plan.Elements).Presentation);
    }

    private sealed class UnreadableSource : IUiSemanticSource<string>
    {
        public Type ValueType => typeof(string);
        public string Value => throw new InvalidOperationException("Value must not be read.");
        public object UntypedValue => throw new InvalidOperationException("Value must not be read.");
        public event Action? Changed
        {
            add => throw new InvalidOperationException("Must not subscribe.");
            remove => throw new InvalidOperationException("Must not unsubscribe.");
        }
    }

    [Fact]
    public void CaptureExcludesAuxiliarySourcesFromPlanning()
    {
        var id = new UiSymbolId("Hatifect.Tests", "auxiliary");
        var filter = id.Child("filter");
        var experience = new UiExperienceBuilder(id, "View")
            .Monitor("Status", new UiState<string>("Ready"))
            .Source(filter, "Filter", "Filter", new UiState<FilterMode>(FilterMode.All),
                UiSourceTypes.Scalar<FilterMode>(id.Child("type/mode"), false), UiCapabilities.Filter).Build();

        var input = UiPlanningInput.Capture(experience);
        var plan = new UiPresentationPlanner().PlanInput(input,
            new UiHostContext(UiHostKind.Window, UiPresentationProfiles.Wide));

        Assert.Equal(2, experience.Sources.Count);
        Assert.Equal(Assert.Single(experience.Elements).Id, Assert.Single(input.Elements).Id);
        Assert.DoesNotContain(input.Elements, element => element.Id == filter);
        Assert.Equal(new UiSymbolId("Hatifect.UI", "presentation/Status"), Assert.Single(plan.Elements).Presentation);
    }

    private enum FilterMode { All, Selected }
}
