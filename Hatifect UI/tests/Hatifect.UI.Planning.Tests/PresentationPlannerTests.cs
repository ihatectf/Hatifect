using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Semantics;
using Xunit;

namespace Hatifect.UI.Planning.Tests;

public sealed class PresentationPlannerTests
{
    [Fact]
    public void ExperienceOnlyGeneratesMasterDetailPlan()
    {
        UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
        UiExperienceDefinition experience = ExperienceBuilderTests.CreateExperience();
        var planner = new UiPresentationPlanner(catalog);

        UiPresentationPlan plan = planner.Plan(experience, new UiHostContext(UiHostKind.Window, UiPresentationProfiles.Wide));

        Assert.Equal(new UiSymbolId("Hatifect.UI", "pattern/MasterDetail"), plan.Pattern);
        Assert.Equal(new UiSymbolId("Hatifect.UI", "presentation/Gallery"), Element(plan, experience, "Items").Presentation);
        Assert.Equal(new UiSymbolId("Hatifect.UI", "region/Primary"), Element(plan, experience, "Items").Region);
        Assert.Equal(new UiSymbolId("Hatifect.UI", "presentation/Side"), Element(plan, experience, "Inspector").Presentation);
        Assert.Equal(new UiSymbolId("Hatifect.UI", "region/Context"), Element(plan, experience, "Inspector").Region);
        Assert.Contains(plan.Decisions, decision => decision.Code == UiPlanDecisionCode.GeneratedPattern);
    }

    [Theory]
    [InlineData("Compact", "Sheet")]
    [InlineData("Controller", "Route")]
    public void ProfileAdaptsInspectorWithoutExperienceChanges(string profile, string expectedPresentation)
    {
        UiExperienceDefinition experience = ExperienceBuilderTests.CreateExperience();
        UiPresentationProfile selected = profile == "Compact" ? UiPresentationProfiles.Compact : UiPresentationProfiles.Controller;
        var planner = new UiPresentationPlanner();

        UiPresentationPlan plan = planner.Plan(experience, new UiHostContext(UiHostKind.Window, selected));

        UiPlannedElement inspector = Element(plan, experience, "Inspector");
        Assert.Equal(new UiSymbolId("Hatifect.UI", $"presentation/{expectedPresentation}"), inspector.Presentation);
        Assert.Contains(inspector.Decisions, decision => decision.Code == UiPlanDecisionCode.ProfileAdaptation);
    }

    [Fact]
    public void PresentationAssetOverridesGenerationAndKeepsProvenance()
    {
        UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
        UiExperienceDefinition experience = ExperienceBuilderTests.CreateExperience();
        const string source = @"presentation Storage

use = MasterDetail
Items -> Secondary
Inspector -> Context

Items
    view = List

Inspector
    prefer = Side
    fallback = Sheet
";
        UiCompilationResult compilation = new UiCompiler(catalog)
            .Compile(source, experience.CreateBindingContext(), "Storage#presentation");
        UiPresentationDefinition definition = Assert.IsType<UiPresentationDefinition>(compilation.Definition);
        var planner = new UiPresentationPlanner(catalog);

        UiPresentationPlan plan = planner.Plan(
            experience,
            new UiHostContext(UiHostKind.Window, UiPresentationProfiles.Compact),
            definition);

        UiPlannedElement items = Element(plan, experience, "Items");
        Assert.Equal(new UiSymbolId("Hatifect.UI", "region/Secondary"), items.Region);
        Assert.Equal(new UiSymbolId("Hatifect.UI", "presentation/List"), items.Presentation);
        Assert.Contains(items.Decisions, decision => decision.Source?.SourceName == "Storage#presentation");
        Assert.Equal(new UiSymbolId("Hatifect.UI", "presentation/Sheet"), Element(plan, experience, "Inspector").Presentation);
    }

    [Fact]
    public void IndexedLookupsPreserveFirstMatchForDuplicateIrInputs()
    {
        UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
        UiExperienceDefinition experience = ExperienceBuilderTests.CreateExperience();
        const string source = @"presentation Storage

Items -> Secondary

Items
    view = List
";
        UiPresentationDefinition compiled = Assert.IsType<UiPresentationDefinition>(
            new UiCompiler(catalog)
                .Compile(source, experience.CreateBindingContext(), "Storage#presentation")
                .Definition);
        UiPlacementIr firstPlacement = Assert.Single(compiled.Placements);
        UiPropertyAssignmentIr firstAssignment = Assert.Single(compiled.Assignments);
        Assert.True(catalog.TryGetRegion("Primary", out UiSymbolId duplicateRegion));
        Assert.True(catalog.TryGetPresentation("Gallery", out UiPresentationSymbol? duplicatePresentation));
        Assert.NotNull(duplicatePresentation);
        var definition = new UiPresentationDefinition(
            compiled.Id,
            new[]
            {
                firstPlacement,
                firstPlacement with { Region = duplicateRegion }
            },
            new[]
            {
                firstAssignment,
                firstAssignment with
                {
                    Value = new UiSymbolValue(
                        duplicatePresentation.Id,
                        duplicatePresentation.Name,
                        UiSemanticType.Presentation)
                }
            });

        UiPresentationPlan plan = new UiPresentationPlanner(catalog).Plan(
            experience,
            new UiHostContext(UiHostKind.Window, UiPresentationProfiles.Wide),
            definition);

        UiPlannedElement items = Element(plan, experience, "Items");
        Assert.Equal(firstPlacement.Region, items.Region);
        Assert.Equal(((UiSymbolValue)firstAssignment.Value).Symbol, items.Presentation);
    }

    [Fact]
    public void PlanningIsDeterministicForTheSameInputs()
    {
        UiExperienceDefinition experience = ExperienceBuilderTests.CreateExperience();
        var planner = new UiPresentationPlanner();
        var host = new UiHostContext(UiHostKind.Terminal, UiPresentationProfiles.Wide);

        UiPresentationPlan first = planner.Plan(experience, host);
        UiPresentationPlan second = planner.Plan(experience, host);

        Assert.Equal(first.Pattern, second.Pattern);
        Assert.Equal(
            first.Elements.Select(element => (element.Element, element.Region, element.Presentation)),
            second.Elements.Select(element => (element.Element, element.Region, element.Presentation)));
    }

    [Fact]
    public void MonitorDefaultsToStatusPresentationInFooterRegion()
    {
        UiSymbolId id = new("Hatifect.Tests", "planner/status");
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Status")
            .Monitor("Status", new UiConstantSource<string>("Ready"))
            .Build();

        UiPresentationPlan plan = new UiPresentationPlanner().Plan(
            experience,
            new UiHostContext(UiHostKind.Terminal, UiPresentationProfiles.Wide));

        UiPlannedElement status = Element(plan, experience, "Status");
        Assert.Equal(new UiSymbolId("Hatifect.UI", "region/Footer"), status.Region);
        Assert.Equal(new UiSymbolId("Hatifect.UI", "presentation/Status"), status.Presentation);
    }

    private static UiPlannedElement Element(UiPresentationPlan plan, UiExperienceDefinition experience, string name)
    {
        UiSymbolId id = experience.Elements.Single(element => element.Name == name).Id;
        return plan.Elements.Single(element => element.Element == id);
    }
}
