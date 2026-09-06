using Hatifect.UI.Experience;
using Hatifect.UI.Semantics;
using System.Globalization;
using Xunit;

namespace Hatifect.UI.Planning.Tests;

public sealed class EnvironmentPlanningTests
{
    [Theory]
    [InlineData("view")]
    [InlineData("prefer")]
    [InlineData("fallback")]
    public void CompilerRejectsPresentationThatOnlyCoversPartOfRequiredCapabilities(string property)
    {
        UiExperienceDefinition experience = InspectAndNavigate();
        UiCompilationResult compilation = new UiCompiler().Compile(
            $"presentation Navigation\n\nTarget\n    {property} = Side\n",
            experience.CreateBindingContext(), "Navigation#presentation");

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == "LUI2009");
        Assert.False(compilation.IsValid);
    }

    [Fact]
    public void GeneratedPresentationPreservesEveryRequiredCapability()
    {
        UiExperienceDefinition experience = InspectAndNavigate();
        UiPresentationPlan plan = new UiPresentationPlanner().Plan(experience,
            new UiHostContext(UiHostKind.Window, UiPresentationProfiles.Wide));

        UiPlannedElement target = Assert.Single(plan.Elements);
        Assert.Equal(experience.Elements[0].Id, target.Element);
        Assert.Equal(new UiSymbolId("Hatifect.UI", "presentation/Route"), target.Presentation);
        Assert.Contains(target.Decisions, decision => decision.Code == UiPlanDecisionCode.PresentationRejected
            && decision.Candidate == new UiSymbolId("Hatifect.UI", "presentation/Side")
            && decision.Message.Contains(UiCapabilities.Navigate.Id.ToString(), StringComparison.Ordinal));
        Assert.Contains(target.Decisions, decision => decision.Code == UiPlanDecisionCode.CoverageFallback
            && decision.Candidate == target.Presentation);
    }

    [Theory]
    [InlineData(719, 1, UiInputMode.MouseKeyboard, "Compact", "Sheet")]
    [InlineData(720, 2, UiInputMode.MouseKeyboard, "Medium", "Side")]
    [InlineData(1099, 1, UiInputMode.Keyboard, "Medium", "Side")]
    [InlineData(1100, 2, UiInputMode.Keyboard, "Wide", "Side")]
    [InlineData(1600, 1.5f, UiInputMode.Controller, "Controller", "Route")]
    public void LogicalViewportAndInputChooseProfileWithoutScalingTwice(
        float width, float scale, UiInputMode input, string profile, string presentation)
    {
        UiExperienceDefinition experience = ExperienceBuilderTests.CreateExperience();
        var environment = Environment(width, scale, input);
        UiPresentationPlan plan = new UiPresentationPlanner().Plan(experience,
            UiHostContext.InEnvironment(UiHostKind.Window, environment));

        Assert.Equal(new UiSymbolId("Hatifect.UI", $"profile/{profile}"), plan.Host.Profile);
        UiPlannedElement inspector = plan.Elements.Single(element => element.Element == experience.Elements[2].Id);
        Assert.Equal(new UiSymbolId("Hatifect.UI", $"presentation/{presentation}"), inspector.Presentation);
        Assert.Equal(experience.Elements.Select(element => element.Id), plan.Elements.Select(element => element.Element));
        Assert.Contains(plan.Decisions, decision => decision.Code == UiPlanDecisionCode.EnvironmentProfile);
    }

    [Fact]
    public void SameExperienceRetainsIdentityAcrossEnvironmentChangesAndPlansStayIndependent()
    {
        UiExperienceDefinition experience = ExperienceBuilderTests.CreateExperience();
        var planner = new UiPresentationPlanner();
        var wide = UiHostContext.InEnvironment(UiHostKind.Window, Environment(1440, 1, UiInputMode.MouseKeyboard));
        var controller = UiHostContext.InEnvironment(UiHostKind.Window, Environment(800, 2, UiInputMode.Controller));
        UiPresentationPlan first = planner.Plan(experience, wide);
        UiPresentationPlan second = planner.Plan(experience, controller);
        UiPresentationPlan repeated = planner.Plan(experience, wide);

        Assert.Equal(first.Elements.Select(element => element.Element), second.Elements.Select(element => element.Element));
        Assert.NotEqual(first.Elements[0].Presentation, second.Elements[0].Presentation);
        Assert.Equal(first.Elements.Select(element => element.Presentation), repeated.Elements.Select(element => element.Presentation));
        Assert.Equal(first.Decisions, repeated.Decisions);
        Assert.Equal(8, first.Decisions.Count); // Six facets, profile rule, generated pattern.
        Assert.Equal(8, second.Decisions.Count);
        Assert.Equal(UiPresentationProfiles.Wide.Id, first.Host.Profile);
    }

    [Fact]
    public void TraceReportsCapturedFacetValuesAndOriginsUsingInvariantNumbers()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            var environment = new UiEnvironment(new UiEnvironmentViewport(1234.5f, 700), 1.25f,
                UiInputMode.Keyboard, "ru-RU", new UiSymbolId("Hatifect.Tests", "theme/high-contrast"),
                new UiAccessibilityPreferences(ReducedMotion: true, HighContrast: true),
                new UiEnvironmentOrigins("viewport fixture", "platform scale", "input fixture",
                    "locale fixture", "theme fixture", "accessibility preferences"));
            UiPresentationPlan plan = new UiPresentationPlanner().Plan(ExperienceBuilderTests.CreateExperience(),
                UiHostContext.InEnvironment(UiHostKind.Window, environment));
            string[] facets = plan.Decisions.Where(decision => decision.Code == UiPlanDecisionCode.EnvironmentFacet)
                .Select(decision => decision.Message).ToArray();

            Assert.Equal(6, facets.Length);
            Assert.Contains(facets, message => message.Contains("1234.5 x 700", StringComparison.Ordinal)
                && message.Contains("viewport fixture", StringComparison.Ordinal));
            Assert.Contains(facets, message => message.Contains("1.25", StringComparison.Ordinal)
                && message.Contains("platform scale", StringComparison.Ordinal));
            Assert.Contains(facets, message => message.Contains("Keyboard", StringComparison.Ordinal)
                && message.Contains("input fixture", StringComparison.Ordinal));
            Assert.Contains(facets, message => message.Contains("ru-RU", StringComparison.Ordinal)
                && message.Contains("locale fixture", StringComparison.Ordinal));
            Assert.Contains(facets, message => message.Contains("theme/high-contrast", StringComparison.Ordinal)
                && message.Contains("theme fixture", StringComparison.Ordinal));
            Assert.Contains(facets, message => message.Contains("ReducedMotion=True, HighContrast=True", StringComparison.Ordinal)
                && message.Contains("accessibility preferences", StringComparison.Ordinal));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void ImpossibleCombinationReturnsBoundedAddressableReasonsWithoutPartialPlan()
    {
        UiExperienceDefinition experience = new UiExperienceBuilder(new UiSymbolId("Hatifect.Tests", "impossible"), "Impossible")
            .Element("Target", new UiState<string>("Details"), UiCapabilities.Inspect, UiCapabilities.Search)
            .Build();
        var planner = new UiPresentationPlanner();
        var host = UiHostContext.InEnvironment(UiHostKind.Window, Environment(1200, 1, UiInputMode.Keyboard));
        UiPlanningException first = Assert.Throws<UiPlanningException>(() => planner.Plan(experience, host));
        UiPlanningException second = Assert.Throws<UiPlanningException>(() => planner.Plan(experience, host));

        Assert.Equal(experience.Elements[0].Id, first.Element);
        UiPlanDecision[] rejected = first.Decisions.Where(decision => decision.Code == UiPlanDecisionCode.PresentationRejected).ToArray();
        Assert.Equal(12, rejected.Length);
        Assert.Equal(12, rejected.Select(decision => decision.Candidate).Distinct().Count());
        Assert.Contains(rejected, decision => decision.Candidate == new UiSymbolId("Hatifect.UI", "presentation/Side")
            && decision.Message.Contains(UiCapabilities.Search.Id.ToString(), StringComparison.Ordinal));
        Assert.Contains(rejected, decision => decision.Candidate == new UiSymbolId("Hatifect.UI", "presentation/TextField")
            && decision.Message.Contains(UiCapabilities.Inspect.Id.ToString(), StringComparison.Ordinal));
        Assert.Equal(first.Decisions, second.Decisions);
        Assert.Throws<NotSupportedException>(() => ((IList<UiPlanDecision>)first.Decisions).Clear());
    }

    [Fact]
    public void DirectIrCannotBypassCoverageAndPreservesRejectedAssignmentProvenance()
    {
        UiExperienceDefinition experience = InspectAndNavigate();
        UiCompilationResult compiled = new UiCompiler().Compile(
            "presentation Navigation\n\nTarget\n    view = Route\n", experience.CreateBindingContext(), "direct-ir");
        Assert.True(compiled.IsValid);
        UiPresentationDefinition valid = Assert.IsType<UiPresentationDefinition>(compiled.Definition);
        UiPropertyAssignmentIr assignment = Assert.Single(valid.Assignments);
        var definition = new UiPresentationDefinition(valid.Id, valid.Placements.ToArray(), new[]
        {
            assignment with { Value = new UiSymbolValue(new UiSymbolId("Hatifect.UI", "presentation/Side"),
                "Side", UiSemanticType.Presentation) }
        });

        UiPlanningException failure = Assert.Throws<UiPlanningException>(() => new UiPresentationPlanner().Plan(
            experience, new UiHostContext(UiHostKind.Window, UiPresentationProfiles.Wide), definition));

        UiPlanDecision rejected = Assert.Single(failure.Decisions, decision => decision.Code == UiPlanDecisionCode.PresentationRejected);
        Assert.Equal(experience.Elements[0].Id, rejected.Element);
        Assert.Equal(assignment.Provenance, rejected.Source);
        Assert.Contains(UiCapabilities.Navigate.Id.ToString(), rejected.Message);
    }

    [Fact]
    public void DirectIrCannotBorrowCoverageFromAnotherPresentationName()
    {
        UiExperienceDefinition experience = InspectAndNavigate();
        UiCompilationResult compiled = new UiCompiler().Compile(
            "presentation Navigation\n\nTarget\n    view = Route\n", experience.CreateBindingContext(), "identity-ir");
        Assert.True(compiled.IsValid);
        UiPresentationDefinition valid = Assert.IsType<UiPresentationDefinition>(compiled.Definition);
        UiPropertyAssignmentIr assignment = Assert.Single(valid.Assignments);
        var wrongIdentity = new UiSymbolId("Hatifect.UI", "presentation/Side");
        var definition = new UiPresentationDefinition(valid.Id, valid.Placements.ToArray(), new[]
        {
            assignment with { Value = new UiSymbolValue(wrongIdentity, "Route", UiSemanticType.Presentation) }
        });

        UiPlanningException failure = Assert.Throws<UiPlanningException>(() => new UiPresentationPlanner().Plan(
            experience, new UiHostContext(UiHostKind.Window, UiPresentationProfiles.Wide), definition));

        UiPlanDecision rejected = Assert.Single(failure.Decisions, decision => decision.Code == UiPlanDecisionCode.PresentationRejected);
        Assert.Equal(wrongIdentity, rejected.Candidate);
        Assert.Equal(assignment.Provenance, rejected.Source);
        Assert.Equal(experience.Elements[0].Id, failure.Element);
    }

    [Theory]
    [InlineData(0, 600, 1)]
    [InlineData(800, 0, 1)]
    [InlineData(-1, 600, 1)]
    [InlineData(800, -1, 1)]
    [InlineData(float.NaN, 600, 1)]
    [InlineData(800, float.PositiveInfinity, 1)]
    [InlineData(800, 600, 0)]
    [InlineData(800, 600, -1)]
    [InlineData(800, 600, float.NaN)]
    [InlineData(800, 600, float.PositiveInfinity)]
    public void InvalidGeometryOrScaleCannotEnterPlanning(float width, float height, float scale)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new UiEnvironment(
            new UiEnvironmentViewport(width, height), scale, UiInputMode.Keyboard, "en", new UiSymbolId("Hatifect.Tests", "theme")));

    [Fact]
    public void InvalidFacetIdentityAndMissingOriginAreRejected()
    {
        var viewport = new UiEnvironmentViewport(800, 600);
        var theme = new UiSymbolId("Hatifect.Tests", "theme");
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiEnvironment(viewport, 1, (UiInputMode)99, "en", theme));
        Assert.Throws<ArgumentException>(() => new UiEnvironment(viewport, 1, UiInputMode.Keyboard, " ", theme));
        Assert.Throws<ArgumentException>(() => new UiEnvironment(viewport, 1, UiInputMode.Keyboard, "en", default));
        Assert.Throws<ArgumentException>(() => new UiEnvironment(viewport, 1, UiInputMode.Keyboard, "en", theme,
            origins: new UiEnvironmentOrigins(Locale: "")));
    }

    [Fact]
    public void RecordCopyCannotContradictTheEnvironmentProfileRule()
    {
        var captured = UiHostContext.InEnvironment(UiHostKind.Window, Environment(1440, 2, UiInputMode.Controller));
        UiHostContext conflicting = captured with { Profile = UiPresentationProfiles.Wide.Id };
        var planner = new UiPresentationPlanner();

        ArgumentException failure = Assert.Throws<ArgumentException>(() => planner.Plan(InspectAndNavigate(), conflicting));
        Assert.Equal("host", failure.ParamName);
        Assert.Equal(UiPresentationProfiles.Controller.Id, captured.Profile);
        Assert.Equal(UiPresentationProfiles.Controller.Id, planner.Plan(InspectAndNavigate(), captured).Host.Profile);
    }

    private static UiEnvironment Environment(float width, float scale, UiInputMode input)
        => new(new UiEnvironmentViewport(width, 700), scale, input, "en", new UiSymbolId("Hatifect.Tests", "theme/dark"));

    private static UiExperienceDefinition InspectAndNavigate()
        => new UiExperienceBuilder(new UiSymbolId("Hatifect.Tests", "environment/navigation"), "Navigation")
            .Element("Target", new UiState<string>("Details"), UiCapabilities.Inspect, UiCapabilities.Navigate)
            .Build();
}
