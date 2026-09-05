using System.Collections.Generic;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Identity;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Visual.Resolution;
using Hatifect.UI.Runtime.Visual.Theming;
using Hatifect.UI.Semantics;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class UiSymbolIdOrdinalComparerTests
{
    private static readonly UiSymbolId PrefixScope = new("A", "z");
    private static readonly UiSymbolId ExtendedScope = new("A-", "a");

    [Fact]
    public void CanonicalOrderComparesTypedScopeThenLocalIdOrdinally()
    {
        UiSymbolId[] values = { ExtendedScope, new("A", "a"), PrefixScope };

        Array.Sort(values, UiSymbolIdOrdinalComparer.Instance);

        Assert.Equal(new[] { new UiSymbolId("A", "a"), PrefixScope, ExtendedScope }, values);
        Assert.True(StringComparer.Ordinal.Compare(ExtendedScope.ToString(), PrefixScope.ToString()) < 0);
    }

    [Fact]
    public void VisualResolutionUsesCanonicalTypedPropertyOrder()
    {
        UiPropertySymbol prefix = Property(PrefixScope, "prefix");
        UiPropertySymbol extended = Property(ExtendedScope, "extended");
        var context = new UiVisualContext(
            new UiSymbolId("Test", "role"),
            new UiSymbolId("Test", "profile"));

        UiVisualResolution result = new UiVisualResolver().Resolve(
            context,
            UiThemePresets.Dark(),
            roleDefaults: new[]
            {
                new UiVisualOverride(extended, new UiIntegerValue(2)),
                new UiVisualOverride(prefix, new UiIntegerValue(1))
            });

        Assert.Equal(
            new[] { PrefixScope, ExtendedScope },
            result.Properties.Select(property => property.Property.Id));
    }

    [Fact]
    public void StructuralDiffUsesCanonicalTypedNodeOrder()
    {
        UiScene before = Scene(Array.Empty<UiSceneNode>());
        UiScene after = Scene(new UiSceneNode[]
        {
            new UiTextSceneNode(ExtendedScope, new UiSymbolId("Test", "role"), EmptyVisual(), "extended"),
            new UiTextSceneNode(PrefixScope, new UiSymbolId("Test", "role"), EmptyVisual(), "prefix")
        });

        UiSceneDiff diff = new UiSceneReconciler().Compare(before, after);

        Assert.Equal(
            new[] { PrefixScope, ExtendedScope },
            diff.ChangedNodes.Where(id => id == PrefixScope || id == ExtendedScope));
    }

    private static UiPropertySymbol Property(UiSymbolId id, string name)
        => new(
            id,
            name,
            UiDefinitionKind.Visual,
            UiSemanticType.Int,
            UiPropertyEffects.Render,
            Animatable: false);

    private static UiScene Scene(UiSceneNode[] children)
    {
        UiVisualResolution visual = EmptyVisual();
        return new UiScene(
            new UiSymbolId("Test", "experience"),
            "Test",
            new UiHostSceneNode(
                new UiSymbolId("Test", "host"),
                new UiSymbolId("Test", "role"),
                visual,
                UiHostPolicies.Window,
                children),
            new UiSceneMeasurementContext(
                new UiSymbolId("Test", "profile"),
                "en-US",
                new UiSymbolId("Test", "theme")));
    }

    private static UiVisualResolution EmptyVisual()
        => new(
            new Dictionary<UiSymbolId, UiResolvedVisualProperty>(),
            Array.Empty<UiVisualResolutionStep>());
}
