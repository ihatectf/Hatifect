using System.Linq;
using Hatifect.UI.Runtime.HotReload;
using Hatifect.UI.Semantics;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class HotReloadTests
{
    [Fact]
    public void InvalidVisualRetainsLastKnownGoodDefinition()
    {
        UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
        UiBindingContext context = new UiBindingContext(RegistryTests.Id("storage")).DeclareRole("Item");
        UiVisualDefinition initial = CompileVisual(catalog, context, "Surface.Raised");
        var slot = new UiAssetSlot<UiVisualDefinition>(initial);
        UiCompilationResult invalid = new UiCompiler(catalog).Compile(
            "visual Storage\n\nItem\n    surface = Missing.Token\n",
            context,
            "Storage#visual");

        UiAssetUpdateResult result = slot.Apply(invalid);

        Assert.False(result.Accepted);
        Assert.True(result.RetainedPrevious);
        Assert.Same(initial, slot.Current);
        Assert.Equal(0, slot.Version);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void VisualReloadReportsOnlyChangedPropertyEffects()
    {
        UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
        UiBindingContext context = new UiBindingContext(RegistryTests.Id("storage")).DeclareRole("Item");
        var slot = new UiAssetSlot<UiVisualDefinition>(CompileVisual(catalog, context, "Surface.Raised"));
        UiCompilationResult candidate = new UiCompiler(catalog).Compile(
            "visual Storage\n\nItem\n    surface = Surface.Hover\n",
            context,
            "Storage#visual");

        UiAssetUpdateResult result = slot.Apply(candidate);

        Assert.True(result.Accepted);
        Assert.False(result.Recompose);
        Assert.Equal(UiPropertyEffects.Render, result.Effects);
        Assert.Equal(1, slot.Version);
    }

    [Fact]
    public void PresentationReloadRequestsRecompositionWithoutReplacingExperienceState()
    {
        UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
        var context = new UiBindingContext(RegistryTests.Id("storage"))
            .DeclareElement("Items", catalog.Capability("Browse"));
        UiPresentationDefinition initial = CompilePresentation(catalog, context, "Gallery");
        var slot = new UiAssetSlot<UiPresentationDefinition>(initial);
        UiCompilationResult candidate = new UiCompiler(catalog).Compile(
            "presentation Storage\n\nItems\n    view = List\n",
            context,
            "Storage#presentation");

        UiAssetUpdateResult result = slot.Apply(candidate);

        Assert.True(result.Accepted);
        Assert.True(result.Recompose);
        Assert.Equal(1, slot.Version);
    }

    private static UiVisualDefinition CompileVisual(
        UiSemanticCatalog catalog,
        UiBindingContext context,
        string surface)
    {
        UiCompilationResult result = new UiCompiler(catalog).Compile(
            $"visual Storage\n\nItem\n    surface = {surface}\n",
            context,
            "Storage#visual");
        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(item => item.Message)));
        return Assert.IsType<UiVisualDefinition>(result.Definition);
    }

    private static UiPresentationDefinition CompilePresentation(
        UiSemanticCatalog catalog,
        UiBindingContext context,
        string view)
    {
        UiCompilationResult result = new UiCompiler(catalog).Compile(
            $"presentation Storage\n\nItems\n    view = {view}\n",
            context,
            "Storage#presentation");
        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(item => item.Message)));
        return Assert.IsType<UiPresentationDefinition>(result.Definition);
    }
}
