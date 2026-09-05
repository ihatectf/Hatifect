using System;
using System.Linq;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Visual;
using Hatifect.UI.Runtime.Visual.Resolution;
using Hatifect.UI.Runtime.Visual.Theming;
using Hatifect.UI.Semantics;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class VisualResolverTests
{
    [Fact]
    public void ThemeIsTypedInheritedAndSupportsNineSliceAssets()
    {
        UiTheme dark = UiThemePresets.Dark();
        UiColor custom = UiColor.FromRgb(0xFF00FF);
        UiTheme derived = new UiThemeBuilder(dark)
            .Set(UiThemeTokens.TextPrimary, custom)
            .Build(RegistryTests.Id("theme/custom"));
        UiSurface nineSlice = UiSurface.NineSlice(
            RegistryTests.Id("asset/panel"), new UiThickness(4), UiColor.FromRgb(0xFFFFFF));

        Assert.Equal(custom, derived.Resolve(UiThemeTokens.TextPrimary));
        Assert.Equal(dark.Resolve(UiThemeTokens.SurfaceRaised), derived.Resolve(UiThemeTokens.SurfaceRaised));
        Assert.Equal(UiSurfaceKind.NineSlice, nineSlice.Kind);
        Assert.True(nineSlice.PixelSnap);
    }

    [Fact]
    public void DuplicateThemeTokenIsAnExplicitError()
    {
        var builder = new UiThemeBuilder()
            .Set(UiThemeTokens.TextPrimary, UiColor.FromRgb(0xFFFFFF));

        Assert.Throws<InvalidOperationException>(() =>
            builder.Set(UiThemeTokens.TextPrimary, UiColor.FromRgb(0x000000)));
    }

    [Fact]
    public void ResolutionUsesFixedLayersAndTypedThemeValues()
    {
        UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
        (UiVisualDefinition visual, UiSymbolId role) = Compile(catalog, @"visual Storage

Item
    surface = Surface.Raised
    foreground = Text.Primary
    padding = Space.M
    border = Accent 2
    opacity = Opacity.Visible

Item@Offline
    foreground = Text.Danger

Item@Hover
    surface = Surface.Hover

Item@Pressed
    surface = Surface.Pressed
");
        UiPropertySymbol surface = Property(catalog, "surface");
        UiPropertySymbol foreground = Property(catalog, "foreground");
        UiPropertySymbol border = Property(catalog, "border");
        var defaults = new[]
        {
            new UiVisualOverride(surface, new UiSymbolValue(
                UiThemeTokens.SurfaceCanvas.Id, "Surface.Canvas", UiSemanticType.SurfaceToken))
        };
        var context = new UiVisualContext(
            role,
            UiPresentationProfiles.Wide.Id,
            new[] { UiVisualStates.Offline },
            new[] { UiVisualStates.Hover, UiVisualStates.Pressed });

        UiVisualResolution result = new UiVisualResolver().Resolve(
            context, UiThemePresets.Dark(), visual, defaults);

        Assert.True(result.TryGet(surface, out UiResolvedVisualProperty? resolvedSurface));
        Assert.Equal(UiVisualResolutionLayer.InteractionState, resolvedSurface!.Layer);
        Assert.Equal(UiThemePresets.Dark().Resolve(UiThemeTokens.SurfacePressed), resolvedSurface.Value);
        Assert.True(result.TryGet(foreground, out UiResolvedVisualProperty? resolvedForeground));
        Assert.Equal(UiVisualResolutionLayer.DomainState, resolvedForeground!.Layer);
        Assert.Equal(UiThemePresets.Dark().Resolve(UiThemeTokens.TextDanger), resolvedForeground.Value);
        Assert.True(result.TryGet(border, out UiResolvedVisualProperty? resolvedBorder));
        Assert.Equal(new UiBorder(UiThemePresets.Dark().Resolve(UiThemeTokens.Accent), 2), resolvedBorder!.Value);
        Assert.Contains(result.Trace, step => step.Layer == UiVisualResolutionLayer.RoleDefault);
        Assert.Contains(result.Trace, step => step.Provenance?.SourceName == "Storage#visual");
    }

    [Fact]
    public void EqualPriorityStateConflictIsRejectedInsteadOfUsingSourceOrder()
    {
        UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
        (UiVisualDefinition visual, UiSymbolId role) = Compile(catalog, @"visual Storage

Item@Congested
    foreground = Text.Danger

Item@Offline
    foreground = Text.Muted
");
        var samePriority = new[]
        {
            new UiVisualStateRef(UiVisualStates.Congested.Id, 100),
            new UiVisualStateRef(UiVisualStates.Offline.Id, 100)
        };
        var context = new UiVisualContext(role, UiPresentationProfiles.Wide.Id, samePriority);

        Assert.Throws<InvalidOperationException>(() =>
            new UiVisualResolver().Resolve(context, UiThemePresets.Dark(), visual));
    }

    [Fact]
    public void ChangedPaddingReportsMeasureArrangeAndRenderInvalidation()
    {
        UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
        (UiVisualDefinition visual, UiSymbolId role) = Compile(catalog, @"visual Storage

Item
    padding = Space.M
");
        UiPropertySymbol padding = Property(catalog, "padding");
        var context = new UiVisualContext(role, UiPresentationProfiles.Wide.Id);
        var resolver = new UiVisualResolver();
        UiTheme theme = UiThemePresets.Dark();
        UiVisualResolution before = resolver.Resolve(context, theme, visual);
        UiVisualResolution after = resolver.Resolve(context, theme, visual, localOverrides: new[]
        {
            new UiVisualOverride(padding, new UiSymbolValue(
                UiThemeTokens.SpaceL.Id, "Space.L", UiSemanticType.SpaceToken))
        });

        UiPropertyEffects effects = after.InvalidationFrom(before);

        Assert.True(effects.HasFlag(UiPropertyEffects.Measure));
        Assert.True(effects.HasFlag(UiPropertyEffects.Arrange));
        Assert.True(effects.HasFlag(UiPropertyEffects.Render));
    }

    [Fact]
    public void RecipeIndexIsReusedBoundedAndExplicitlyClearable()
    {
        UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
        var resolver = new UiVisualResolver();
        UiTheme theme = UiThemePresets.Dark();
        UiVisualDefinition? first = null;
        UiSymbolId role = default;

        for (int index = 0; index < UiVisualResolver.MaximumCachedDefinitions + 3; index++)
        {
            (UiVisualDefinition visual, UiSymbolId compiledRole) = Compile(catalog, $@"visual Storage{index}

Item
    surface = Surface.Raised
");
            first ??= visual;
            role = compiledRole;
            resolver.Resolve(new UiVisualContext(role, UiPresentationProfiles.Wide.Id), theme, visual);
            Assert.True(resolver.CachedDefinitionCount <= UiVisualResolver.MaximumCachedDefinitions);
        }

        Assert.Equal(UiVisualResolver.MaximumCachedDefinitions, resolver.CachedDefinitionCount);

        resolver.Resolve(new UiVisualContext(role, UiPresentationProfiles.Wide.Id), theme, first!);
        Assert.Equal(UiVisualResolver.MaximumCachedDefinitions, resolver.CachedDefinitionCount);

        resolver.ClearRecipeCache();
        Assert.Equal(0, resolver.CachedDefinitionCount);
    }

    private static (UiVisualDefinition Definition, UiSymbolId Role) Compile(
        UiSemanticCatalog catalog,
        string source)
    {
        var context = new UiBindingContext(RegistryTests.Id("storage")).DeclareRole("Item");
        UiCompilationResult compilation = new UiCompiler(catalog).Compile(source, context, "Storage#visual");
        Assert.True(compilation.IsValid, string.Join(Environment.NewLine, compilation.Diagnostics.Select(item => item.Message)));
        Assert.True(context.TryGetRole("Item", out UiSymbolId role));
        return (Assert.IsType<UiVisualDefinition>(compilation.Definition), role);
    }

    private static UiPropertySymbol Property(UiSemanticCatalog catalog, string name)
    {
        Assert.True(catalog.TryGetProperty(UiDefinitionKind.Visual, name, out UiPropertySymbol? property));
        return property!;
    }
}
