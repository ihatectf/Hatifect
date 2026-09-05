using System.Buffers.Binary;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Runtime.Rendering;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Terminal;
using Hatifect.UI.Runtime.Visual;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class SemanticResourceTests
{
    [Fact]
    public void DecodedPixelsPreserveCoverageAndUsePremultipliedColorChannels()
    {
        byte[] pixels = { 255, 128, 64, 128, 73, 14, 9, 255, 255, 128, 64, 0, 255, 255, 255, 1 };

        UiSemanticTexturePixels.PremultiplyRgba(pixels);

        Assert.Equal(new byte[] { 128, 64, 32, 128, 73, 14, 9, 255, 0, 0, 0, 0, 1, 1, 1, 1 }, pixels);
        Assert.Throws<ArgumentException>(() => UiSemanticTexturePixels.PremultiplyRgba(new byte[3]));
    }

    [Fact]
    public void TextureLeasesReleaseExactlyTheirRegistrationAndMissingIdsReuseOneFallback()
    {
        var created = new List<Resource>();
        using var catalog = new UiSemanticTextureCatalog<Resource>(_ => { var r = new Resource(); created.Add(r); return r; }, () => new Resource());
        var first = catalog.Register(Id("icon"), Png());
        Resource texture = catalog.Resolve(Id("icon"));
        Assert.Same(texture, catalog.Resolve(Id("icon")));
        Assert.Throws<InvalidOperationException>(() => catalog.Register(Id("icon"), Png()));
        Assert.Single(created);
        first.Dispose();
        Assert.Equal(1, texture.Disposals);
        using var second = catalog.Register(Id("icon"), Png());
        first.Dispose();
        Assert.Same(created[1], catalog.Resolve(Id("icon")));
        Resource fallback = catalog.Resolve(Id("missing"));
        Assert.Same(fallback, catalog.Resolve(Id("another")));
        catalog.Dispose();
        Assert.Equal(1, fallback.Disposals);
        Assert.Equal(1, created[1].Disposals);
        Assert.Throws<ObjectDisposedException>(() => catalog.Resolve(Id("icon")));
    }

    [Fact]
    public void ResourceAdmissionRejectsInvalidImagesAndDecodedBudgetBeforeDecoding()
    {
        int decoded = 0;
        using var catalog = new UiSemanticTextureCatalog<Resource>(_ => { decoded++; return new Resource(); }, () => new Resource());
        Assert.Throws<ArgumentException>(() => catalog.Register(Id("bad"), new byte[33]));
        Assert.Throws<InvalidOperationException>(() => catalog.Register(Id("oversize"), Png(4096, 4096)));
        using var accepted = catalog.Register(Id("large"), Png(2048, 4096));
        Assert.Throws<InvalidOperationException>(() => catalog.Register(Id("extra"), Png()));
        Assert.Equal(1, decoded);
        accepted.Dispose();
        using var releasedBudget = catalog.Register(Id("extra"), Png());
        Assert.Equal(2, decoded);
    }

    [Fact]
    public void CollectionIconsReserveTextSpaceAndProduceTexturePrimitives()
    {
        var source = new UiCollectionSource<string>(new[] { "row" }, Id, value => value, null, _ => Id("icon"));
        var experience = new UiExperienceBuilder(Id("icons"), "Icons").Browse("Items", source).Build();
        var registry = UiSemanticHostProjection.Register(experience, UiSemanticHostKind.Window);
        var scene = new UiSceneComposer(UiSemanticThemes.Resolve(UiSemanticTheme.Dark), registry).Compose(
            new UiInvocationService(registry).Invoke(experience.Id, UiPresentationProfiles.Wide));
        var host = new UiHostRuntimeSession(scene, new UiRect(0, 0, 1280, 800), new TestPlatform());
        var item = Assert.Single(Assert.Single(host.Layout.CollectionWindows).Items);
        Assert.NotNull(item.IconBounds);
        Assert.True(item.LabelBounds.X >= item.IconBounds!.Value.Right);
        var icon = Assert.Single(host.Frame.Primitives.OfType<UiSurfacePrimitive>(), primitive => primitive.Bounds == item.IconBounds.Value);
        Assert.Equal(UiSurface.Texture(Id("icon"), new UiColor(255, 255, 255), pixelSnap: true), icon.Surface);
        Assert.Equal(Id("icon"), item.Item.Icon);
    }

    [Fact]
    public void ThemeSwitchKeepsTerminalSectionConsumerDraftAndFocus()
    {
        var state = new UiState<string>("Draft");
        var experience = new UiExperienceBuilder(Id("theme"), "Theme").Search("Name", state).Build();
        var definition = new UiSemanticTerminalDefinition(Id("terminal"), new[] { new UiSemanticTerminalSection(experience, icon: Id("section-icon")) });
        var placement = new UiHostPlacementContext(new UiRect(0, 0, 1280, 800));
        using var host = new UiTerminalHostSession(UiSemanticHostProjection.Register(definition),
            UiSemanticThemes.Resolve(UiSemanticTheme.Dark), placement, new TestPlatform(), UiPresentationProfiles.Wide);
        var navigation = Assert.Single(host.Host.Root.Scene.Root.Children.SelectMany(slot => slot.Children).OfType<UiRouteButtonSceneNode>());
        Assert.Equal(Id("section-icon"), navigation.Icon);
        var icon = Assert.Single(host.Host.Root.Frame.Primitives.OfType<UiSurfacePrimitive>(), primitive =>
            primitive.Node == navigation.Id && primitive.Bounds.Width == UiRouteButtonSceneNode.IconExtent);
        var label = Assert.Single(host.Host.Root.Frame.Primitives.OfType<UiTextPrimitive>(), primitive => primitive.Node == navigation.Id);
        Assert.True(label.Bounds.X >= icon.Bounds.Right);
        var focus = host.Host.Root.Interactions.Snapshot.Focused;
        var foreground = host.Host.Root.Frame.Primitives.OfType<UiTextPrimitive>().First().Foreground;
        host.SetTheme(UiSemanticThemes.Resolve(UiSemanticTheme.Light));
        host.Recompose(UiPresentationProfiles.Wide, placement);
        Assert.Equal(experience.Id, host.ActiveSection);
        Assert.Equal("Draft", state.Value);
        Assert.Equal(focus, host.Host.Root.Interactions.Snapshot.Focused);
        Assert.NotEqual(foreground, host.Host.Root.Frame.Primitives.OfType<UiTextPrimitive>().First().Foreground);
    }

    private static byte[] Png(uint width = 1, uint height = 1)
    {
        byte[] png = new byte[33];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82 }.CopyTo(png, 0);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), height);
        return png;
    }
    private static UiSymbolId Id(string name) => new("Resource.Tests", name);
    private sealed class Resource : IDisposable
    { public int Disposals { get; private set; } public void Dispose() => Disposals++; }
    private sealed class TestPlatform : IUiPlatformBridge
    {
        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
            => new(Math.Min(text.Length * 8, availableWidth), 20);
        public void DrawSurface(UiSurfacePrimitive surface) { }
        public void DrawText(UiTextPrimitive text) { }
    }
}
