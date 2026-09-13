using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Accessibility;
using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Runtime.Projection;
using Hatifect.UI.Runtime.Registration;
using Hatifect.UI.Runtime.Rendering;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Terminal;
using Hatifect.UI.Runtime.Visual;
using Hatifect.UI.Runtime.Visual.Theming;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class TerminalShellTests
{
    [Fact]
    public void RegisteredSectionsBuildLazyStableNavigationAndProjectIntoShellSlots()
    {
        UiSymbolId flow = Id("terminal/flow");
        UiSymbolId storage = Id("terminal/storage");
        UiSymbolId diagnostics = Id("terminal/diagnostics");
        int flowCreated = 0;
        int storageCreated = 0;
        int diagnosticsCreated = 0;
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(flow, "Flow", () => { flowCreated++; return Section(flow, "Flow"); }, order: 10)
            .TerminalSection(storage, "Storage", () => { storageCreated++; return Section(storage, "Storage"); }, order: 20)
            .TerminalSection(
                diagnostics,
                "Diagnostics",
                () => { diagnosticsCreated++; return Section(diagnostics, "Diagnostics"); },
                order: 30)
            .Freeze();
        var shell = new UiTerminalShellSession(registry, UiThemePresets.Dark());

        UiTerminalFrame frame = shell.Open(storage, UiPresentationProfiles.Wide, locale: "ru-RU");

        Assert.Equal(storage, shell.ActiveSection);
        Assert.Equal(0, flowCreated);
        Assert.Equal(1, storageCreated);
        Assert.Equal(0, diagnosticsCreated);
        Assert.Equal("Hatifect Terminal", frame.Scene.DisplayName);
        Assert.Equal(UiSceneComposer.TerminalShellId.Child("scene/host"), frame.Scene.Root.Id);
        UiSlotSceneNode[] slots = frame.Scene.Root.Children.Cast<UiSlotSceneNode>().ToArray();
        Assert.Equal(7, slots.Length);
        UiSymbolId[] expectedTerminalOrder =
        {
            UiHostSlots.Navigation,
            UiHostSlots.Utility,
            UiHostSlots.Content,
            UiHostSlots.Context,
            UiHostSlots.Actions,
            UiHostSlots.Status,
            UiHostSlots.Overlay
        };
        Assert.Equal(
            expectedTerminalOrder,
            UiHostSlots.TerminalOrder);
        Assert.Equal(
            expectedTerminalOrder.Length,
            UiHostSlots.TerminalOrder.Distinct().Count());
        Assert.Equal(
            UiHostSlots.TerminalOrder,
            slots.Select(slot => slot.Slot));
        UiRouteButtonSceneNode[] routes = Slot(slots, UiHostSlots.Navigation).Children
            .Cast<UiRouteButtonSceneNode>()
            .ToArray();
        Assert.Equal(new[] { flow, storage, diagnostics }, routes.Select(route => route.Route));
        Assert.Single(routes, route => route.IsCurrent && route.Route == storage);
        Assert.Contains(Slot(slots, UiHostSlots.Content).Children, node => node is UiCollectionSceneNode);
        Assert.Contains(Slot(slots, UiHostSlots.Utility).Children, node => node is UiTextInputSceneNode);
        Assert.Contains(Slot(slots, UiHostSlots.Context).Children, node => node.Kind == UiSceneNodeKind.Inspector);
        Assert.Contains(Slot(slots, UiHostSlots.Actions).Children, node => node.Kind == UiSceneNodeKind.ActionBar);
        Assert.Contains(Slot(slots, UiHostSlots.Status).Children, node => node.Kind == UiSceneNodeKind.Text);
        Assert.Single(Nodes(frame.Scene.Root).OfType<UiHostSceneNode>());
    }

    [Fact]
    public void RouteSwitchUsesOneActivationCacheAndTerminalGridOwnsGeometryAndAccessibility()
    {
        UiSymbolId flow = Id("terminal-switch/flow");
        UiSymbolId storage = Id("terminal-switch/storage");
        int flowCreated = 0;
        int storageCreated = 0;
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(flow, "Flow", () => { flowCreated++; return Section(flow, "Flow"); }, order: 10)
            .TerminalSection(storage, "Storage", () => { storageCreated++; return Section(storage, "Storage"); }, order: 20)
            .Freeze();
        var shell = new UiTerminalShellSession(registry, UiThemePresets.Dark());
        UiTerminalFrame storageFrame = shell.Open(storage, UiPresentationProfiles.Wide);
        UiExperienceDefinition firstStorage = storageFrame.Invocation.Experience;
        var runtime = new UiHostRuntimeSession(
            storageFrame.Scene,
            new UiRect(0, 0, 960, 540),
            new TestPlatform());
        UiSlotSceneNode navigation = Slot(storageFrame.Scene, UiHostSlots.Navigation);
        UiSlotSceneNode content = Slot(storageFrame.Scene, UiHostSlots.Content);
        UiSlotSceneNode context = Slot(storageFrame.Scene, UiHostSlots.Context);
        UiSlotSceneNode actions = Slot(storageFrame.Scene, UiHostSlots.Actions);
        Assert.True(runtime.Layout.TryGetEntry(navigation.Id, out UiLayoutEntry? navigationLayout));
        Assert.True(runtime.Layout.TryGetEntry(content.Id, out UiLayoutEntry? contentLayout));
        Assert.True(runtime.Layout.TryGetEntry(context.Id, out UiLayoutEntry? contextLayout));
        Assert.True(runtime.Layout.TryGetEntry(actions.Id, out UiLayoutEntry? actionsLayout));
        Assert.NotNull(navigationLayout);
        Assert.NotNull(contentLayout);
        Assert.NotNull(contextLayout);
        Assert.NotNull(actionsLayout);
        Assert.True(navigationLayout.Bounds.Right <= contentLayout.Bounds.X + 0.01f);
        Assert.True(contentLayout.Bounds.Right <= contextLayout.Bounds.X + 0.01f);
        Assert.True(contentLayout.Bounds.Bottom <= actionsLayout.Bounds.Y + 0.01f);

        UiRouteButtonSceneNode flowRoute = navigation.Children
            .OfType<UiRouteButtonSceneNode>()
            .Single(route => route.Route == flow);
        Assert.True(runtime.Layout.TryGetEntry(flowRoute.Id, out UiLayoutEntry? flowRouteLayout));
        Assert.NotNull(flowRouteLayout);
        UiPoint pointer = Center(flowRouteLayout.Bounds);
        Assert.True(runtime.Interactions.PressPointer(pointer).Consumed);
        UiInteractionUpdate released = runtime.Interactions.ReleasePointer(pointer);
        Assert.Equal(flow, released.Route);
        Assert.True(shell.TryFollow(released, UiPresentationProfiles.Wide, out UiTerminalFrame? flowFrame));
        Assert.NotNull(flowFrame);
        Assert.Equal(1, flowCreated);
        Assert.Equal(1, storageCreated);
        Assert.Equal(storageFrame.Scene.Root.Id, flowFrame.Scene.Root.Id);
        Assert.Equal(
            storageFrame.Scene.Root.Children.Select(node => node.Id),
            flowFrame.Scene.Root.Children.Select(node => node.Id));

        UiTerminalFrame returned = shell.Open(storage, UiPresentationProfiles.Wide);
        Assert.Same(firstStorage, returned.Invocation.Experience);
        Assert.Equal(1, storageCreated);
        var returnedRuntime = new UiHostRuntimeSession(
            returned.Scene,
            new UiRect(0, 0, 960, 540),
            new TestPlatform());
        UiAccessibilityNodeSnapshot[] selectedRoutes = AccessibilityNodes(returnedRuntime.Accessibility.Root)
            .Where(node => node.Role == UiAccessibilityRole.Button && node.Selected)
            .ToArray();
        Assert.Single(selectedRoutes);
        Assert.Equal(storage.Child("terminal/navigation-route"), selectedRoutes[0].Id);
        Assert.All(
            Nodes(returned.Scene.Root).OfType<UiRouteButtonSceneNode>(),
            route =>
            {
                Assert.True(returnedRuntime.Layout.TryGetEntry(route.Id, out UiLayoutEntry? entry));
                Assert.NotNull(entry);
                Assert.True(entry.Bounds.Width > 0);
                Assert.True(entry.Bounds.Height > 0);
            });
    }

    [Fact]
    public void AvailabilityAndForeignRoutesCannotReplaceTheActiveTerminalSection()
    {
        UiSymbolId storage = Id("terminal-availability/storage");
        UiSymbolId unavailable = Id("terminal-availability/unavailable");
        UiSymbolId window = Id("terminal-availability/window");
        int unavailableCreated = 0;
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(storage, "Storage", () => Section(storage, "Storage"), order: 20)
            .TerminalSection(
                unavailable,
                "Unavailable",
                () => { unavailableCreated++; return Section(unavailable, "Unavailable"); },
                order: 10,
                isAvailable: () => false)
            .Window(window, "Window", () => Section(window, "Window"))
            .Freeze();
        var shell = new UiTerminalShellSession(registry, UiThemePresets.Dark());

        UiTerminalFrame frame = shell.OpenFirstAvailable(UiPresentationProfiles.Compact);

        Assert.Equal(storage, frame.Section);
        Assert.DoesNotContain(
            Slot(frame.Scene, UiHostSlots.Navigation).Children.OfType<UiRouteButtonSceneNode>(),
            route => route.Route == unavailable);
        Assert.False(shell.TryFollow(
            new UiInteractionUpdate(true, false, Route: unavailable),
            UiPresentationProfiles.Compact,
            out _));
        Assert.False(shell.TryFollow(
            new UiInteractionUpdate(true, false, Route: window),
            UiPresentationProfiles.Compact,
            out _));
        Assert.Equal(storage, shell.ActiveSection);
        Assert.Equal(0, unavailableCreated);
    }

    [Fact]
    public void OpenUsesOneAvailabilitySnapshotForActivationAndGeneratedNavigation()
    {
        UiSymbolId storage = Id("terminal-availability-snapshot/storage");
        int availabilityChecks = 0;
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(
                storage,
                "Storage",
                () => Section(storage, "Storage"),
                isAvailable: () => ++availabilityChecks == 1)
            .Freeze();
        var shell = new UiTerminalShellSession(registry, UiThemePresets.Dark());

        UiTerminalFrame frame = shell.Open(storage, UiPresentationProfiles.Wide);

        Assert.Equal(1, availabilityChecks);
        UiRouteButtonSceneNode route = Assert.Single(
            Slot(frame.Scene, UiHostSlots.Navigation).Children.Cast<UiRouteButtonSceneNode>());
        Assert.Equal(storage, route.Route);
        Assert.True(route.IsCurrent);
    }

    [Fact]
    public void OwnedCachedSectionIsDisposedExactlyOnceWithItsTerminalSession()
    {
        UiSymbolId storage = Id("terminal-owned/storage");
        int created = 0;
        int disposed = 0;
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSectionOwned(
                storage,
                "Storage",
                () =>
                {
                    created++;
                    return (Section(storage, "Storage"), new RecordingOwner(() => disposed++));
                })
            .Freeze();
        var shell = new UiTerminalShellSession(registry, UiThemePresets.Dark());

        UiExperienceDefinition first = shell.Open(storage, UiPresentationProfiles.Wide).Invocation.Experience;
        UiExperienceDefinition second = shell.Open(storage, UiPresentationProfiles.Compact).Invocation.Experience;

        Assert.Same(first, second);
        Assert.Equal(1, created);
        Assert.Equal(0, disposed);
        shell.Dispose();
        shell.Dispose();
        Assert.Equal(1, disposed);
        Assert.Null(shell.ActiveSection);
        Assert.Throws<ObjectDisposedException>(() => shell.Open(storage, UiPresentationProfiles.Wide));
    }

    [Fact]
    public void InvalidOwnedSectionFactoryDisposesBeforeFailingActivation()
    {
        UiSymbolId storage = Id("terminal-owned-invalid/storage");
        UiSymbolId wrong = Id("terminal-owned-invalid/wrong");
        int disposed = 0;
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSectionOwned(
                storage,
                "Storage",
                () => (Section(wrong, "Wrong"), new RecordingOwner(() => disposed++)))
            .Freeze();
        using var shell = new UiTerminalShellSession(registry, UiThemePresets.Dark());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => shell.Open(storage, UiPresentationProfiles.Wide));

        Assert.Contains("IDs must match", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, disposed);
        Assert.Null(shell.ActiveSection);
    }

    [Fact]
    public void SectionCannotInjectContentIntoShellOwnedNavigation()
    {
        UiSymbolId invalid = Id("terminal-invalid/navigation-owner");
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(
                invalid,
                "Invalid",
                () => new UiExperienceBuilder(invalid, "Invalid")
                    .Navigate(
                        "Sections",
                        new UiCollectionSource<string>(new[] { "Nested" }, item => invalid.Child($"item/{item}")))
                    .Build())
            .Freeze();
        var shell = new UiTerminalShellSession(registry, UiThemePresets.Dark());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => shell.Open(invalid, UiPresentationProfiles.Wide));

        Assert.Contains("shell-owned Navigation", error.Message, StringComparison.Ordinal);
        Assert.Null(shell.ActiveSection);
    }

    private static UiExperienceDefinition Section(UiSymbolId id, string title)
        => new UiExperienceBuilder(id, title)
            .Browse(
                "Items",
                new UiCollectionSource<string>(new[] { $"{title} item" }, _ => id.Child("item/primary")))
            .Search("Search", new UiState<string>(string.Empty))
            .Inspect("Inspector", new UiState<string?>($"{title} details"))
            .Actions("Actions", new UiActionDefinition(id.Child("action/apply"), "Apply", () => { }))
            .Monitor("Status", new UiConstantSource<string>($"{title} ready"))
            .VisualRole("Action.Primary")
            .Build();

    private static UiSlotSceneNode Slot(UiScene scene, UiSymbolId slot)
        => Slot(scene.Root.Children.Cast<UiSlotSceneNode>().ToArray(), slot);

    private static UiSlotSceneNode Slot(IEnumerable<UiSlotSceneNode> slots, UiSymbolId slot)
        => Assert.Single(slots, item => item.Slot == slot);

    private static IEnumerable<UiSceneNode> Nodes(UiSceneNode node)
    {
        yield return node;
        foreach (UiSceneNode child in node.Children)
        foreach (UiSceneNode descendant in Nodes(child))
            yield return descendant;
    }

    private static IEnumerable<UiAccessibilityNodeSnapshot> AccessibilityNodes(UiAccessibilityNodeSnapshot node)
    {
        yield return node;
        foreach (UiAccessibilityNodeSnapshot child in node.Children)
        foreach (UiAccessibilityNodeSnapshot descendant in AccessibilityNodes(child))
            yield return descendant;
    }

    private static UiPoint Center(UiRect bounds)
        => new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);

    private static UiSymbolId Id(string local) => new("Hatifect.Dogfood", local);

    private sealed class RecordingOwner : IDisposable
    {
        private readonly Action _dispose;

        public RecordingOwner(Action dispose) => _dispose = dispose;

        public void Dispose() => _dispose();
    }

    private sealed class TestPlatform : IUiPlatformBridge
    {
        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
            => new(
                Math.Min(text.Length * typography.Size * 0.6f, availableWidth),
                typography.Size * typography.LineHeight);

        public void DrawSurface(UiSurfacePrimitive surface) { }
        public void DrawText(UiTextPrimitive text) { }
    }
}
