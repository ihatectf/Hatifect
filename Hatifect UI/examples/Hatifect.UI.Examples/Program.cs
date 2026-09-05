using System;
using Hatifect.UI;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Visual.Resolution;
using Hatifect.UI.Runtime.Visual.Theming;
using Hatifect.UI.Semantics;

UiSymbolId catalogId = new("Author.Example", "catalog");
var query = new UiState<string>(string.Empty);
var selection = new UiState<string?>(null);
var choose = new UiActionDefinition(catalogId.Child("action/choose"), "Choose", () => { });

UiExperienceDefinition experience = CreateCatalog(catalogId, query, selection, choose);

const string presentationSource = @"presentation Catalog

use = MasterDetail
Items -> Primary
Search -> Utility
Inspector -> Context
Actions -> Actions

Inspector.view
    default = Side
    Compact = Sheet
    Controller = Route
";

const string visualSource = @"visual Catalog

Item
    surface = Surface.Raised
    foreground = Text.Primary
    radius = Radius.M
    padding = Space.M
    typography = Typography.Body

Item@Selected
    border = Border.Focus

Item@Hover
    surface = Surface.Hover

Action.Primary@Pressed
    surface = Surface.Pressed
    transform = Transform.Pressed
";

UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
UiBindingContext binding = experience.CreateBindingContext();
var compiler = new UiCompiler(catalog);
UiCompilationResult presentationCompilation = compiler.Compile(
    presentationSource, binding, "Catalog#presentation");
UiCompilationResult visualCompilation = compiler.Compile(
    visualSource, binding, "Catalog#visual");
if (!presentationCompilation.IsValid || !visualCompilation.IsValid)
    throw new InvalidOperationException("Example assets must compile.");

UiPresentationDefinition presentation = (UiPresentationDefinition)presentationCompilation.Definition!;
UiVisualDefinition visual = (UiVisualDefinition)visualCompilation.Definition!;
UiPresentationPlan plan = new UiPresentationPlanner(catalog).Plan(
    experience,
    new UiHostContext(UiHostKind.Window, UiPresentationProfiles.Compact),
    presentation);

binding.TryGetRole("Item", out UiSymbolId itemRole);
UiVisualResolution itemVisual = new UiVisualResolver().Resolve(
    new UiVisualContext(
        itemRole,
        UiPresentationProfiles.Compact.Id,
        interactionStates: new[] { UiVisualStates.Selected, UiVisualStates.Hover }),
    UiThemePresets.Dark(),
    visual);

Console.WriteLine($"Experience: {experience.Id}; profile: {plan.Host.Profile}");
Console.WriteLine($"Pattern: {plan.Pattern}");
foreach (UiPlannedElement element in plan.Elements)
    Console.WriteLine($"{element.Element} -> {element.Region} as {element.Presentation}");
foreach (UiResolvedVisualProperty property in itemVisual.Properties)
    Console.WriteLine($"{property.Property.Name} = {property.Value} ({property.Layer})");

static UiExperienceDefinition CreateCatalog(
    UiSymbolId id,
    UiState<string> query,
    UiState<string?> selection,
    UiActionDefinition choose)
    => new UiExperienceBuilder(id, "Item catalog")
        .Browse("Items", new UiCollectionSource<string>(
            new[] { "Wood", "Stone", "Coal" }, item => id.Child($"item/{item}")))
        .Search("Search", query)
        .Inspect("Inspector", selection)
        .Actions("Actions", choose)
        .VisualRole("Item")
        .VisualRole("Inspector")
        .VisualRole("Action.Primary")
        .Build();
