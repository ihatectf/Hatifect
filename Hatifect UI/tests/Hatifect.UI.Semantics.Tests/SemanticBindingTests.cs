using System;
using System.Linq;
using System.Reflection;
using Hatifect.UI;
using Hatifect.UI.Language.Diagnostics;
using Hatifect.UI.Language.Syntax;
using Hatifect.UI.Semantics;
using Xunit;

namespace Hatifect.UI.Semantics.Tests;

public sealed class SemanticBindingTests
{
    [Fact]
    public void LoweredDefinitionsDefensivelyCopyTheirIrArrays()
    {
        UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
        var context = new UiBindingContext(new UiSymbolId("Author.Mod", "storage")).DeclareRole("Item");
        UiCompilationResult compilation = new UiCompiler(catalog).Compile(@"visual Storage

Item
    surface = Surface.Raised
    foreground = Text.Primary
", context, "Storage#visual");
        UiVisualDefinition compiled = Assert.IsType<UiVisualDefinition>(compilation.Definition);
        UiPropertyAssignmentIr original = compiled.Recipes[0];
        UiPropertyAssignmentIr[] source = compiled.Recipes.ToArray();
        var copied = new UiVisualDefinition(new UiSymbolId("Author.Mod", "visual/copy"), source);

        source[0] = source[1];

        Assert.Same(original, copied.Recipes[0]);
    }

    [Fact]
    public void CandidatePresentationLowersToTypedIr()
    {
        UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
        UiBindingContext context = Context(catalog);
        var compiler = new UiCompiler(catalog);
        const string source = @"presentation Storage

use = MasterDetail
Search -> Utility
Items -> Primary
Inspector -> Context
Actions -> Actions

Items
    view = Gallery
    density = Compact

Inspector.view
    default = Side
    Compact = Sheet
    Controller = Route
";

        UiCompilationResult result = compiler.Compile(source, context, "Storage#presentation");

        Assert.True(result.IsValid);
        UiPresentationDefinition definition = Assert.IsType<UiPresentationDefinition>(result.Definition);
        Assert.Equal(4, definition.Placements.Count);
        Assert.Contains(definition.Assignments, assignment => assignment.Property.Name == "view" && assignment.Value.Type == UiSemanticType.Presentation);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == UiDiagnosticSeverity.Error);
    }

    [Fact]
    public void CapabilityMismatchIsACompileTimeError()
    {
        UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
        UiBindingContext context = Context(catalog);
        var compiler = new UiCompiler(catalog);
        const string source = @"presentation Storage

Inspector
    view = Gallery
";

        UiCompilationResult result = compiler.Compile(source, context, "Invalid#presentation");

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "LUI2009");
    }

    [Fact]
    public void ItemSizingLowersToTypedCatalogIdentity()
    {
        UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
        UiBindingContext context = Context(catalog);
        const string source = @"presentation Storage

Items
    view = List
    itemSizing = Adaptive
";

        UiCompilationResult result = new UiCompiler(catalog)
            .Compile(source, context, "AdaptiveList#presentation");

        Assert.True(result.IsValid);
        UiPresentationDefinition definition = Assert.IsType<UiPresentationDefinition>(result.Definition);
        UiPropertyAssignmentIr sizing = Assert.Single(
            definition.Assignments,
            assignment => assignment.Property.Name == "itemSizing");
        UiSymbolValue value = Assert.IsType<UiSymbolValue>(sizing.Value);
        Assert.True(catalog.TryGetEnumValue(value.Symbol, out UiEnumValueSymbol? symbol));
        Assert.NotNull(symbol);
        Assert.Equal(sizing.Property.Id, symbol.Property);
        Assert.Equal(UiSemanticType.EnumValue, sizing.Value.Type);
        Assert.Equal("Adaptive", value.Name);
    }

    [Fact]
    public void NavigationListRejectsAdaptiveSizingWithSemanticDiagnostic()
    {
        UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
        var context = new UiBindingContext(new UiSymbolId("Author.Mod", "navigation"))
            .DeclareElement("Routes", catalog.Capability("Navigate"));
        const string source = @"presentation Navigation

Routes
    view = NavigationList
    itemSizing = Adaptive
";

        UiCompilationResult result = new UiCompiler(catalog)
            .Compile(source, context, "AdaptiveNavigation#presentation");

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "LUI2019");
    }

    [Fact]
    public void SelectionIsCapabilityStateRatherThanPresentationRecipe()
    {
        UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();

        Assert.False(catalog.TryGetPresentation("Selection", out _));
        Assert.True(catalog.TryGetPresentation("List", out UiPresentationSymbol? list));
        Assert.NotNull(list);
        Assert.Contains(catalog.Capability("Select"), list.SupportedCapabilities);
    }

    [Fact]
    public void VisualValuesAreTypedAndStructuralSelectorsAreNotNeeded()
    {
        UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
        UiBindingContext context = Context(catalog);
        var compiler = new UiCompiler(catalog);
        const string source = @"visual Storage

Item
    surface = Surface.Raised
    foreground = Text.Primary
    radius = Radius.M
    padding = Space.M

Item@Selected
    border = Accent 2
    typography = Typography.Body
    elevation = Elevation.Low
    opacity = Opacity.Visible
";

        UiCompilationResult result = compiler.Compile(source, context, "Storage#visual");

        Assert.True(result.IsValid);
        UiVisualDefinition definition = Assert.IsType<UiVisualDefinition>(result.Definition);
        Assert.Contains(definition.Recipes, recipe => recipe.Property.Name == "surface" && recipe.Value.Type == UiSemanticType.SurfaceToken);
        Assert.Contains(definition.Recipes, recipe => recipe.Property.Name == "border" && recipe.Value is UiBorderValue);
        Assert.Contains(definition.Recipes, recipe => recipe.Property.Name == "typography" && recipe.Value.Type == UiSemanticType.TypographyToken);
        Assert.Contains(definition.Recipes, recipe => recipe.Property.Name == "opacity" && recipe.Value.Type == UiSemanticType.Opacity);
        Assert.All(definition.Recipes, recipe => Assert.NotNull(recipe.Provenance.SourceName));
    }

    [Fact]
    public void OpacityOutsideUnitRangeIsRejected()
    {
        UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
        UiCompilationResult result = new UiCompiler(catalog).Compile(
            "visual Storage\n\nItem\n    opacity = 2\n",
            Context(catalog),
            "InvalidOpacity#visual");

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "LUI2017");
    }

    [Fact]
    public void DuplicateResolutionLayerIsRejectedInsteadOfUsingSourceOrder()
    {
        UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
        UiBindingContext context = Context(catalog);
        var compiler = new UiCompiler(catalog);
        const string source = @"visual Storage

Item@Hover
    surface = Surface.Raised
    surface = Surface.Hover
";

        UiCompilationResult result = compiler.Compile(source, context, "Duplicate#visual");

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "LUI2008");
    }

    [Fact]
    public void SamePropertyInDistinctProfileAndStateLayersIsValid()
    {
        UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
        UiCompilationResult result = new UiCompiler(catalog).Compile(@"visual Storage

Item
    surface = Surface.Raised

Item@Hover
    surface = Surface.Hover

@Compact
    Item
        surface = Surface.Raised
", Context(catalog), "Layered#visual");

        Assert.True(result.IsValid);
        UiVisualDefinition definition = Assert.IsType<UiVisualDefinition>(result.Definition);
        Assert.Equal(3, definition.Recipes.Count(recipe => recipe.Property.Name == "surface"));
        Assert.Contains(definition.Recipes, recipe => recipe.State != null && recipe.Profile == null);
        Assert.Contains(definition.Recipes, recipe => recipe.State == null && recipe.Profile != null);
    }

    [Fact]
    public void BinderDuplicateIdentityIsPrivateValueKey()
    {
        Type binder = typeof(UiCompiler).Assembly.GetType("Hatifect.UI.Semantics.UiBinder")!;
        Type? assignmentKey = binder.GetNestedType("AssignmentKey", BindingFlags.NonPublic);

        Assert.NotNull(assignmentKey);
        Assert.True(assignmentKey!.IsValueType);
        Assert.True(assignmentKey.IsNestedPrivate);
    }

    [Fact]
    public void RuntimeIrDoesNotReferenceSyntaxNodes()
    {
        Assert.NotEqual(typeof(UiDocumentSyntax).Assembly, typeof(UiPresentationDefinition).Assembly);
        Assert.DoesNotContain(
            typeof(UiPresentationDefinition).GetProperties(),
            property => property.PropertyType.Namespace?.StartsWith("Hatifect.UI.Language.Syntax") == true);
    }

    private static UiBindingContext Context(UiSemanticCatalog catalog)
        => new UiBindingContext(new UiSymbolId("Author.Mod", "storage"))
            .DeclareElement("Search")
            .DeclareElement("Items", catalog.Capability("Browse"))
            .DeclareElement("Inspector", catalog.Capability("Inspect"))
            .DeclareElement("Actions")
            .DeclareRole("Item")
            .DeclareRole("Inspector");
}
