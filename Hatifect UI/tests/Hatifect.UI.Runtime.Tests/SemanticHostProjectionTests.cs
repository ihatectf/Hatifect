using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Runtime.Rendering;
using Hatifect.UI.Runtime.Terminal;
using Hatifect.UI.Runtime.Visual;
using Hatifect.UI.Runtime.Visual.Theming;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class SemanticHostProjectionTests
{
    [Theory]
    [InlineData(UiSemanticHostKind.Window, UiHostKind.Window, UiFocusScopePolicy.Contained)]
    [InlineData(UiSemanticHostKind.Modal, UiHostKind.Popup, UiFocusScopePolicy.Trapped)]
    [InlineData(UiSemanticHostKind.Fullscreen, UiHostKind.Fullscreen, UiFocusScopePolicy.Contained)]
    [InlineData(UiSemanticHostKind.Hud, UiHostKind.Overlay, UiFocusScopePolicy.Shared)]
    public void PublicHostIntentComposesWithFrameworkPolicy(UiSemanticHostKind kind,
        UiHostKind host, UiFocusScopePolicy focus)
    {
        UiExperienceDefinition experience = Experience("monitor");
        var registry = UiSemanticHostProjection.Register(experience, kind);
        var invocation = new UiInvocationService(registry).Invoke(experience.Id, UiPresentationProfiles.Wide);
        UiScene scene = new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(invocation);

        Assert.Equal(host, scene.Root.Policy.Kind);
        Assert.Equal(focus, scene.Root.Policy.Focus);
        Assert.Equal(experience.Id, scene.Experience);
    }

    [Fact]
    public void TerminalSnapshotPreservesInitialSelectionAndRejectsDuplicateOrMissingSections()
    {
        var first = new UiSemanticTerminalSection(Experience("first"), order: 20);
        var second = new UiSemanticTerminalSection(Experience("second"), order: 10);
        var source = new[] { first, second };
        var terminal = new UiSemanticTerminalDefinition(Id("terminal"), source, first.Experience.Id);
        source[0] = second;

        var registry = UiSemanticHostProjection.Register(terminal);
        var invocation = new UiInvocationService(registry).Invoke(terminal.InitialSection, UiPresentationProfiles.Compact);
        Assert.Same(first.Experience, invocation.Experience);
        Assert.Equal(UiHostKind.Terminal, invocation.Descriptor.Host.Kind);
        Assert.Same(first, terminal.Sections[0]);
        Assert.Throws<ArgumentException>(() => new UiSemanticTerminalDefinition(Id("bad"), new[] { first, first }));
        Assert.Throws<ArgumentException>(() => new UiSemanticTerminalDefinition(Id("bad"), new[] { first }, second.Experience.Id));
        Assert.Throws<ArgumentException>(() => new UiSemanticTerminalDefinition(Id("empty"), Array.Empty<UiSemanticTerminalSection>()));
    }

    [Fact]
    public void UnknownHostKindIsRejectedBeforeRegistration()
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            UiSemanticHostProjection.Register(Experience("bad"), (UiSemanticHostKind)123));

    [Fact]
    public void PublicTerminalRoutesAndReflowsWithoutReplacingConsumerState()
    {
        var state = new UiState<string>("Ready");
        var first = new UiExperienceBuilder(Id("live"), "Live").Monitor("Status", state).Build();
        var second = Experience("settings");
        var definition = new UiSemanticTerminalDefinition(Id("terminal"), new[]
        {
            new UiSemanticTerminalSection(first), new UiSemanticTerminalSection(second)
        }, first.Id);
        var placement = new UiHostPlacementContext(new UiRect(0, 0, 1280, 800));
        using var session = new UiTerminalHostSession(UiSemanticHostProjection.Register(definition),
            UiThemePresets.Dark(), placement, new TestPlatform(), UiPresentationProfiles.Wide, definition.InitialSection);

        session.OpenSection(second.Id);
        state.Value = "Updated";
        session.OpenSection(first.Id);
        session.Recompose(UiPresentationProfiles.Compact,
            new UiHostPlacementContext(new UiRect(0, 0, 640, 480)), "ru");

        Assert.Equal(first.Id, session.ActiveSection);
        Assert.Same(first, session.CurrentInvocation.Experience);
        Assert.Equal("Updated", session.CurrentInvocation.Experience.Elements[0].Source.UntypedValue);
        Assert.Equal(UiPresentationProfiles.Compact.Id, session.CurrentInvocation.Plan.Host.Profile);
        Assert.NotNull(session.Host.Root.Interactions.Snapshot.Focused);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TerminalActionCanDisposeItsOwnerDuringPointerOrKeyboardDispatch(bool keyboard)
    {
        UiTerminalHostSession? session = null;
        int calls = 0;
        var action = new UiActionDefinition(Id("close"), "Close", () => { calls++; session!.Dispose(); });
        var experience = new UiExperienceBuilder(Id("closable"), "Closable").Actions("Commands", action).Build();
        var definition = new UiSemanticTerminalDefinition(Id("terminal"), new[] { new UiSemanticTerminalSection(experience) });
        using (session = new UiTerminalHostSession(UiSemanticHostProjection.Register(definition),
            UiThemePresets.Dark(), new UiHostPlacementContext(new UiRect(0, 0, 1280, 800)),
            new TestPlatform(), UiPresentationProfiles.Wide))
        {
            var button = Descendants(session.Host.Root.Scene.Root).OfType<UiButtonSceneNode>().Single();
            Assert.True(session.Host.Root.Layout.TryGetEntry(button.Id, out var entry));
            var point = new UiPoint(entry!.Bounds.X + entry.Bounds.Width / 2, entry.Bounds.Y + entry.Bounds.Height / 2);
            session.PressPointer(point);
            UiPortalDispatch result = keyboard ? session.Submit() : session.ReleasePointer(point);

            Assert.True(result.Interaction?.ActionInvoked);
            Assert.Equal(1, calls);
            Assert.Throws<ObjectDisposedException>(() => session.Submit());
        }
    }

    [Fact]
    public void ConsumedRootCancelRequestsDismissalButPortalCancelDoesNot()
    {
        var experience = Experience("cancel");
        var registry = UiSemanticHostProjection.Register(experience, UiSemanticHostKind.Window);
        var scene = new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(
            new UiInvocationService(registry).Invoke(experience.Id, UiPresentationProfiles.Wide));
        var host = new UiPortalHostSession(scene, new UiHostPlacementContext(new UiRect(0, 0, 1280, 800)), new TestPlatform());
        UiPortalDispatch root = host.Cancel();

        Assert.True(root.Consumed);
        Assert.True(root.RequestsRootDismissal);
        Assert.False((root with { Portal = Id("popup"), PortalClosed = true }).RequestsRootDismissal);
    }

    private static IEnumerable<UiSceneNode> Descendants(UiSceneNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        foreach (var item in Descendants(child)) yield return item;
    }

    private sealed class TestPlatform : IUiPlatformBridge
    {
        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
            => new(Math.Min(text.Length * 8, availableWidth), 20);
        public void DrawSurface(UiSurfacePrimitive surface) { }
        public void DrawText(UiTextPrimitive text) { }
    }

    private static UiExperienceDefinition Experience(string name) => new UiExperienceBuilder(Id(name), name)
        .Monitor("Status", new UiState<string>("Ready")).Build();
    private static UiSymbolId Id(string name) => new("Host.Tests", name);
}
