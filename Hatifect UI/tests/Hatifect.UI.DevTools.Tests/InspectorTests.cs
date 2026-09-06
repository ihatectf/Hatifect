using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Language.Diagnostics;
using Hatifect.UI.Language.Text;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Runtime.Projection;
using Hatifect.UI.Runtime.Registration;
using Hatifect.UI.Runtime.Rendering;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Terminal;
using Hatifect.UI.Runtime.Visual;
using Hatifect.UI.Runtime.Visual.Resolution;
using Hatifect.UI.Runtime.Visual.Theming;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Inspection;
using Hatifect.UI.Tooling.Validation;
using Xunit;

namespace Hatifect.UI.DevTools.Tests;

public sealed class InspectorTests
{
    [Fact]
    public void TerminalInspectorTabReachesSearchInsteadOfAnInspectedNavigationRow()
    {
        UiSymbolId diagnosticsId = Id("native/diagnostics");
        UiSymbolId inspectorId = Id("native/inspector");
        UiTerminalHostSession? terminal = null;
        UiInspectorExperienceSession? inspector = null;
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(diagnosticsId, "Diagnostics", () => new UiExperienceBuilder(diagnosticsId, "Diagnostics")
                .Monitor("Status", new UiState<string>("Ready")).Build(), order: 10)
            .TerminalSection(inspectorId, "Inspector", () =>
            {
                var capture = UiInspector.Capture(terminal!.CurrentInvocation, terminal.Host.Root.CaptureDiagnostics());
                inspector = new UiInspectorExperienceSession(capture, inspectorId);
                return inspector.Experience;
            }, order: 20)
            .Freeze();
        using (terminal = new UiTerminalHostSession(registry, UiThemePresets.Dark(),
            new UiHostPlacementContext(new UiRect(0, 0, 1280, 720)), new TestPlatform(), UiPresentationProfiles.Wide))
        {
            UiRouteButtonSceneNode route = Assert.Single(SceneNodes(terminal.Host.Root.Scene.Root)
                .OfType<UiRouteButtonSceneNode>(), node => node.Route == inspectorId);
            Assert.True(terminal.Host.Root.Layout.TryGetEntry(route.Id, out UiLayoutEntry? routeLayout));
            UiRect bounds = routeLayout!.Bounds;
            var pointer = new UiPoint(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            Assert.True(terminal.PressPointer(pointer).Consumed);
            Assert.True(terminal.ReleasePointer(pointer).Consumed);
            Assert.Equal(inspectorId, terminal.ActiveSection);
            Assert.NotNull(inspector);

            UiTextInputSceneNode field = Assert.Single(SceneNodes(terminal.Host.Root.Scene.Root).OfType<UiTextInputSceneNode>());
            Assert.True(terminal.MoveFocus(UiNavigationDirection.Next).Consumed);
            Assert.Equal(field.Id, terminal.FocusedTextEditing?.Input);
            Assert.Equal(field.Id, terminal.Host.Root.Interactions.Snapshot.Focused);
            Assert.True(terminal.Host.Root.Layout.TryGetEntry(field.Id, out UiLayoutEntry? fieldLayout));
            Assert.True(fieldLayout!.Bounds.Width > 0 && fieldLayout.Bounds.Height > 0);

            Assert.DoesNotContain(Enumerable.Range(0, inspector!.Nodes.Count)
                .Select(index => inspector.Nodes.GetItem(index).Id), row => SceneNodes(terminal.Host.Root.Scene.Root).Any(node => node.Id == row));
            var rowsBeforeFilter = Enumerable.Range(0, inspector.Nodes.Count)
                .Select(inspector.Nodes.GetItem)
                .ToDictionary(item => ((UiInspectorNodeSnapshot)item.Value!).Id, item => item.Id);
            Assert.True(terminal.InsertText("navigation-route").Interaction?.TextChanged);
            Assert.Equal("navigation-route", inspector.Query.Value);
            Assert.NotEmpty(inspector.Nodes.Value);
            Assert.All(inspector.Nodes.Value, node => Assert.Contains("navigation-route", node.Id.ToString()));
            Assert.All(Enumerable.Range(0, inspector.Nodes.Count).Select(inspector.Nodes.GetItem), item =>
                Assert.Equal(rowsBeforeFilter[((UiInspectorNodeSnapshot)item.Value!).Id], item.Id));
            Assert.Contains(terminal.Host.Root.Frame.Primitives.OfType<UiTextPrimitive>(),
                primitive => primitive.Node == field.Id && primitive.Text == "navigation-route");
        }
        inspector?.Dispose();
    }

    private static IEnumerable<UiSceneNode> SceneNodes(UiSceneNode root)
    {
        yield return root;
        foreach (UiSceneNode child in root.Children)
        foreach (UiSceneNode node in SceneNodes(child)) yield return node;
    }

    [Fact]
    public void CaptureIncludesExperiencePlanProjectionVisualSourceLayoutAndDiagnostics()
    {
        InspectorFixture fixture = InspectorFixture.Create();
        var diagnostic = new UiDiagnostic(
            "LUI9000",
            UiDiagnosticSeverity.Info,
            "Runtime probe",
            new UiTextSpan(4, 2, 1, 5),
            "Runtime#probe");

        UiInspectorSnapshot snapshot = UiInspector.Capture(
            fixture.Invocation,
            fixture.Runtime.CaptureDiagnostics(),
            fixture.Sources,
            new[] { diagnostic });

        Assert.Equal(fixture.Experience.Id, snapshot.Experience);
        Assert.Equal(fixture.Experience.DisplayName, snapshot.DisplayName);
        Assert.Equal(UiHostKind.Popup, snapshot.Host);
        Assert.Equal(UiPresentationProfiles.Compact.Id, snapshot.Profile);
        Assert.True(snapshot.HostBounds.Width > 0);
        Assert.True(snapshot.HostBounds.Height > 0);
        Assert.Equal(UiHostPlacementKind.Center, snapshot.Placement);
        Assert.Single(snapshot.Diagnostics);
        Assert.Equal("LUI9000", snapshot.Diagnostics[0].Id);

        UiSymbolId inspectorId = fixture.Experience.Elements.Single(element => element.Name == "Inspector").Id;
        UiInspectorPlanDecisionSnapshot placement = Assert.Single(
            snapshot.Decisions,
            decision => decision.Code == UiPlanDecisionCode.ExplicitPlacement &&
                        decision.Element == inspectorId);
        Assert.NotNull(placement.Source);
        Assert.Equal(UiSourceInspectionKind.Placement, placement.SourceReveal?.Kind);
        UiInspectorProjectionSnapshot projection = Assert.Single(
            snapshot.Projection,
            element => element.Element == inspectorId);
        Assert.Equal(UiHostSlots.Context, projection.HostSlot);

        UiInspectorNodeSnapshot inspector = Assert.Single(
            snapshot.Nodes,
            node => node.SemanticName == "Inspector");
        Assert.Equal("Inspector", inspector.Kind);
        Assert.Equal("Forest storage · 36 slots", inspector.Semantic.Value);
        Assert.True(inspector.Bounds.Width > 0);
        Assert.True(inspector.Bounds.Height > 0);
        Assert.True(inspector.Clip.Width > 0);
        Assert.Equal("Initialized", snapshot.Lifecycle.Phase);
        Assert.True(snapshot.Lifecycle.LayoutBuilds > 0);
        Assert.True(snapshot.Lifecycle.FrameBuilds > 0);
        UiInspectorResolvedPropertySnapshot radius = Assert.Single(
            inspector.VisualProperties,
            property => property.Property.Name == "radius");
        Assert.Equal(UiVisualResolutionLayer.FeatureRecipe, radius.Layer);
        Assert.NotNull(radius.Source);
        Assert.Equal("radius", radius.SourceReveal?.Property?.Name);
    }

    [Fact]
    public void CaptureReportsTypedRenderInvalidationAndInteractionState()
    {
        InspectorFixture fixture = InspectorFixture.Create();
        UiInteractionUpdate focus = fixture.Runtime.Interactions.MoveFocus(UiNavigationDirection.Next);
        UiHostUpdate update = Assert.IsType<UiHostUpdate>(fixture.Runtime.RefreshInteractionVisuals());

        Assert.True(focus.StateChanged);
        Assert.Equal(UiPropertyEffects.Render, update.Diff.Effects);
        UiInspectorSnapshot snapshot = UiInspector.Capture(
            fixture.Invocation,
            fixture.Runtime.CaptureDiagnostics(),
            fixture.Sources);

        Assert.Equal(UiPropertyEffects.Render, snapshot.Invalidation.Effects);
        Assert.False(snapshot.Invalidation.LayoutChanged);
        Assert.True(snapshot.Invalidation.FrameChanged);
        UiInspectorNodeSnapshot focused = Assert.Single(snapshot.Nodes, node => node.Focused);
        Assert.Contains(focused.Id, snapshot.Invalidation.ChangedNodes);
        UiInspectorResolvedPropertySnapshot surface = Assert.Single(
            focused.VisualProperties,
            property =>
                property.Property.Name == "surface" &&
                property.Layer == UiVisualResolutionLayer.InteractionState);
        Assert.Equal(UiVisualStates.Focused.Id, surface.State);
        Assert.Equal("surface", surface.SourceReveal?.Property?.Name);
        Assert.Equal(focused.Id, snapshot.Input.Focused);
        Assert.True(focused.Focusable);
        Assert.True(focused.HitTestTarget);
        Assert.Contains(UiVisualStates.Focused.Id, focused.ActiveStates);
        Assert.Contains(focused.Id, snapshot.Input.FocusOrder);
        Assert.Contains(focused.Id, snapshot.Input.HitTestTargets);
        Assert.Equal("Updated", snapshot.Lifecycle.Phase);
    }

    [Fact]
    public void RuntimeDiagnosticCaptureIsImmutableAfterRuntimeStateChanges()
    {
        InspectorFixture fixture = InspectorFixture.Create();
        var runtimeSnapshot = fixture.Runtime.CaptureDiagnostics();
        UiInspectorSnapshot inspectorSnapshot = UiInspector.Capture(
            fixture.Invocation,
            runtimeSnapshot,
            fixture.Sources);

        Assert.Null(runtimeSnapshot.Input.Focused);
        Assert.Null(inspectorSnapshot.Input.Focused);
        Assert.Equal("Initialized", inspectorSnapshot.Lifecycle.Phase);

        Assert.True(fixture.Runtime.Interactions.MoveFocus(UiNavigationDirection.Next).StateChanged);
        Assert.NotNull(fixture.Runtime.RefreshInteractionVisuals());
        UiInspectorSnapshot updated = UiInspector.Capture(
            fixture.Invocation,
            fixture.Runtime.CaptureDiagnostics(),
            fixture.Sources);

        Assert.Null(runtimeSnapshot.Input.Focused);
        Assert.Null(inspectorSnapshot.Input.Focused);
        Assert.NotNull(updated.Input.Focused);
        Assert.Equal("Updated", updated.Lifecycle.Phase);
        Assert.NotSame(inspectorSnapshot.Nodes, updated.Nodes);
    }

    [Fact]
    public void CaptureKeepsLargeCollectionInspectionBoundedToMaterializedWindow()
    {
        UiSymbolId id = Id("devtools/large-collection");
        int[] values = Enumerable.Range(0, 10_000).ToArray();
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Large collection")
            .Browse("Items", new UiCollectionSource<int>(
                values,
                value => id.Child($"item/{value}"),
                value => $"Item {value}"))
            .Build();
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Window(id, experience.DisplayName, () => experience)
            .Freeze();
        UiInvocationResult invocation = new UiInvocationService(registry)
            .Invoke(id, UiPresentationProfiles.Wide);
        UiScene scene = new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(invocation);
        var runtime = new UiHostRuntimeSession(
            scene,
            new UiRect(0, 0, 640, 240),
            new TestPlatform());

        UiInspectorSnapshot snapshot = UiInspector.Capture(invocation, runtime.CaptureDiagnostics());
        UiInspectorNodeSnapshot collectionNode = Assert.Single(
            snapshot.Nodes,
            node => node.Collection != null);
        UiInspectorCollectionSnapshot collection = Assert.IsType<UiInspectorCollectionSnapshot>(
            collectionNode.Collection);

        Assert.Equal(10_000, collection.TotalCount);
        Assert.InRange(collection.MaterializedItems.Count, 1, 100);
        Assert.True(collection.MaterializedItems.Count < collection.TotalCount);
        Assert.InRange(snapshot.Nodes.Count, 1, 10);

        UiHostScrollUpdate scroll = runtime.ScrollCollection(collectionNode.Id, 120);
        UiInspectorSnapshot scrolled = UiInspector.Capture(invocation, runtime.CaptureDiagnostics());
        Assert.True(scroll.Consumed);
        Assert.Equal(
            UiPropertyEffects.Measure | UiPropertyEffects.Arrange | UiPropertyEffects.Render,
            scrolled.Invalidation.Effects);
        Assert.Contains(collectionNode.Id, scrolled.Invalidation.ChangedNodes);
    }

    [Fact]
    public void InspectorExperienceProjectsCaptureThroughTheSemanticPipeline()
    {
        InspectorFixture fixture = InspectorFixture.Create();
        UiInspectorSnapshot snapshot = UiInspector.Capture(
            fixture.Invocation,
            fixture.Runtime.CaptureDiagnostics(),
            fixture.Sources);
        int closed = 0;
        UiSourceInspectionEntry? revealed = null;
        using var inspector = new UiInspectorExperienceSession(
            snapshot,
            close: () => closed++,
            revealSource: source => revealed = source);

        Assert.Equal(
            new[] { "Search", "Nodes", "Details", "Actions" },
            inspector.Experience.Elements.Select(element => element.Name));
        Assert.Equal(snapshot.Nodes.Count, inspector.Nodes.Value.Count);
        Assert.False(string.IsNullOrWhiteSpace(inspector.Details.Value));
        Assert.Contains("Planner:", inspector.Details.Value, StringComparison.Ordinal);
        Assert.Contains("Semantic:", inspector.Details.Value, StringComparison.Ordinal);
        Assert.Contains("hit-test=", inspector.Details.Value, StringComparison.Ordinal);
        Assert.Contains("Invalidation:", inspector.Details.Value, StringComparison.Ordinal);
        Assert.Contains("Lifecycle:", inspector.Details.Value, StringComparison.Ordinal);

        UiInspectorNodeSnapshot sourceNode = snapshot.Nodes.First(
            node => node.VisualProperties.Any(property => property.SourceReveal != null));
        inspector.Query.Value = sourceNode.Role.LocalId;
        Assert.NotEmpty(inspector.Nodes.Value);
        Assert.All(inspector.Nodes.Value, node => Assert.Contains(
            sourceNode.Role.LocalId,
            $"{node.Id} {node.Kind} {node.SemanticName} {node.Role}",
            StringComparison.OrdinalIgnoreCase));
        Assert.Contains(inspector.Nodes.Value, node => node.Id == sourceNode.Id);
        UiSemanticCollectionItem sourceRow = Assert.Single(Enumerable.Range(0, inspector.Nodes.Count)
            .Select(inspector.Nodes.GetItem), item => ((UiInspectorNodeSnapshot)item.Value!).Id == sourceNode.Id);
        Assert.NotEqual(sourceNode.Id, sourceRow.Id);
        Assert.True(inspector.Nodes.TrySelect(sourceRow.Id));
        Assert.Contains(sourceNode.Id.ToString(), inspector.Details.Value);

        UiActionDefinition reveal = Assert.Single(
            inspector.Experience.Actions,
            action => action.Id.LocalId.EndsWith("/reveal-source", StringComparison.Ordinal));
        UiActionDefinition close = Assert.Single(
            inspector.Experience.Actions,
            action => action.Id.LocalId.EndsWith("/close", StringComparison.Ordinal));
        Assert.True(reveal.TryExecute());
        Assert.NotNull(revealed);
        Assert.True(close.TryExecute());
        Assert.Equal(1, closed);

        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Popup(
                inspector.Experience.Id,
                inspector.Experience.DisplayName,
                () => inspector.Experience,
                UiHostPolicies.Modal)
            .Freeze();
        UiInvocationResult invocation = new UiInvocationService(registry)
            .Invoke(inspector.Experience.Id, UiPresentationProfiles.Compact);
        UiScene scene = new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(invocation);
        var runtime = new UiHostRuntimeSession(
            scene,
            new UiRect(0, 0, 640, 360),
            new TestPlatform());

        Assert.NotEmpty(runtime.Frame.Primitives);
        Assert.Contains(
            runtime.Frame.Primitives.OfType<UiTextPrimitive>(),
            primitive => primitive.Text.Contains("Inspector", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OwnedSectionFactoryKeepsRegisteredIdentityAndCapturesLazily()
    {
        InspectorFixture fixture = InspectorFixture.Create();
        UiInspectorSnapshot snapshot = UiInspector.Capture(
            fixture.Invocation,
            fixture.Runtime.CaptureDiagnostics(),
            fixture.Sources);
        UiSymbolId sectionId = Id("terminal/devtools");
        int captures = 0;
        int closes = 0;
        var factory = new UiInspectorSectionFactory(
            sectionId,
            () =>
            {
                captures++;
                return (snapshot, () => closes++);
            });

        Assert.Equal(0, captures);
        (UiExperienceDefinition experience, IDisposable owner) = factory.Create();
        UiActionDefinition close;
        using (owner)
        {
            Assert.Equal(1, captures);
            Assert.Equal(sectionId, experience.Id);
            Assert.Equal($"Inspect {snapshot.DisplayName}", experience.DisplayName);
            close = Assert.Single(
                experience.Actions,
                action => action.Id.LocalId.EndsWith("/close", StringComparison.Ordinal));
            Assert.True(close.TryExecute());
            Assert.Equal(1, closes);
        }
        Assert.False(close.CanExecute);
        Assert.False(close.TryExecute());
        Assert.Equal(1, closes);
    }

    private static UiSymbolId Id(string local) => new("Hatifect.Tests", local);

    private sealed record InspectorFixture(
        UiExperienceDefinition Experience,
        UiInvocationResult Invocation,
        UiSourceInspectionIndex Sources,
        UiHostRuntimeSession Runtime)
    {
        public static InspectorFixture Create()
        {
            UiSymbolId id = Id("devtools/inspector");
            var close = new UiActionDefinition(id.Child("action/close"), "Close", () => { });
            UiExperienceDefinition experience = new UiExperienceBuilder(id, "Storage inspector")
                .Inspect("Inspector", new UiState<string?>("Forest storage · 36 slots"))
                .Actions("Actions", close)
                .VisualRole("Inspector")
                .VisualRole("Action.Primary")
                .Build();
            UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
            UiBuildValidationResult assets = new UiBuildValidator(catalog).Validate(new[]
            {
                new UiBuildAsset(
                    "StorageInspector#presentation",
                    PresentationSource,
                    experience.CreateBindingContext(),
                    UiDefinitionKind.Presentation),
                new UiBuildAsset(
                    "StorageInspector#visual",
                    VisualSource,
                    experience.CreateBindingContext(),
                    UiDefinitionKind.Visual)
            });
            Assert.True(
                assets.IsValid,
                string.Join(Environment.NewLine, assets.Diagnostics.Select(diagnostic => diagnostic.Message)));
            UiPresentationDefinition presentation = Assert.IsType<UiPresentationDefinition>(
                assets.Definitions.Single(definition =>
                    definition.Definition.Kind == UiDefinitionKind.Presentation).Definition);
            UiVisualDefinition visual = Assert.IsType<UiVisualDefinition>(
                assets.Definitions.Single(definition =>
                    definition.Definition.Kind == UiDefinitionKind.Visual).Definition);
            var sources = new UiSourceInspectionIndex(
                assets.Definitions.Select(definition => definition.Definition));
            UiRegistrySnapshot registry = new UiRegistryBuilder()
                .Popup(id, experience.DisplayName, () => experience, UiHostPolicies.Modal)
                .Freeze();
            UiInvocationResult invocation = new UiInvocationService(
                    registry,
                    planner: new UiPresentationPlanner(catalog))
                .Invoke(id, UiPresentationProfiles.Compact, presentation);
            var composer = new UiSceneComposer(UiThemePresets.Dark(), registry, catalog);
            UiScene scene = composer.Compose(invocation, visual);
            var runtime = new UiHostRuntimeSession(
                scene,
                new UiHostPlacementContext(new UiRect(0, 0, 640, 360)),
                new TestPlatform(),
                composeInteraction: interaction => composer.Compose(invocation, visual, interaction));
            return new InspectorFixture(experience, invocation, sources, runtime);
        }
    }

    private const string PresentationSource = @"presentation StorageInspector

use = Prompt
Inspector -> Context
Actions -> Actions

Inspector.view
    default = Side
    Compact = Sheet
";

    private const string VisualSource = @"visual StorageInspector

Inspector
    surface = Surface.Raised
    foreground = Text.Primary
    padding = Space.M
    radius = Radius.M
    typography = Typography.Body

Action.Primary
    surface = Surface.Secondary
    foreground = Text.Primary
    padding = Space.M
    radius = Radius.M
    typography = Typography.Label

Action.Primary@Focused
    surface = Surface.Hover
";

    private sealed class TestPlatform : IUiPlatformBridge
    {
        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
            => new(
                Math.Min(availableWidth, text.Length * typography.Size * 0.6f),
                typography.Size * typography.LineHeight);

        public void DrawSurface(UiSurfacePrimitive surface) { }
        public void DrawText(UiTextPrimitive text) { }
    }
}
