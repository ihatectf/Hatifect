using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Activation;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Accessibility;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Input;
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
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class TerminalHostSessionTests
{
    [Fact]
    public void RootRouteDispatchSwitchesSectionInsideOneRuntimeOwnedHost()
    {
        UiSymbolId flow = Id("terminal-host/flow");
        UiSymbolId storage = Id("terminal-host/storage");
        int flowCreated = 0;
        int storageCreated = 0;
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(flow, "Flow", () => { flowCreated++; return Section(flow, "Flow"); }, order: 10)
            .TerminalSection(
                storage,
                "Storage",
                () => { storageCreated++; return Section(storage, "Storage"); },
                order: 20)
            .Freeze();
        using var session = new UiTerminalHostSession(
            registry,
            UiThemePresets.Dark(),
            Placement,
            new TestPlatform(),
            UiPresentationProfiles.Wide,
            initialSection: storage);
        UiSymbolId root = session.Host.Root.Scene.Root.Id;
        UiSymbolId[] slots = session.Host.Root.Scene.Root.Children.Select(node => node.Id).ToArray();
        UiRouteButtonSceneNode flowRoute = Route(session.Host.Root.Scene, flow);
        UiPoint pointer = Center(Layout(session, flowRoute.Id).Bounds);

        Assert.True(session.PressPointer(pointer).Consumed);
        UiPortalDispatch released = session.ReleasePointer(pointer);

        Assert.True(released.Consumed);
        Assert.Null(released.Portal);
        Assert.Equal(flow, released.Interaction?.Route);
        Assert.Equal(flow, session.ActiveSection);
        Assert.Equal(flow, session.CurrentInvocation.Experience.Id);
        Assert.Equal(1, flowCreated);
        Assert.Equal(1, storageCreated);
        Assert.Equal(root, session.Host.Root.Scene.Root.Id);
        Assert.Equal(slots, session.Host.Root.Scene.Root.Children.Select(node => node.Id));
        Assert.True(Route(session.Host.Root.Scene, flow).IsCurrent);
        Assert.False(Route(session.Host.Root.Scene, storage).IsCurrent);
    }

    [Fact]
    public void FailedHostUpdateDoesNotCommitStagedRoute()
    {
        UiSymbolId flow = Id("terminal-host-failure/flow");
        UiSymbolId storage = Id("terminal-host-failure/storage");
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(flow, "Flow", () => Section(flow, "Flow"), order: 10)
            .TerminalSection(storage, "Storage", () => Section(storage, "Storage"), order: 20)
            .Freeze();
        var platform = new TestPlatform();
        using var session = new UiTerminalHostSession(
            registry,
            UiThemePresets.Dark(),
            Placement,
            platform,
            UiPresentationProfiles.Wide,
            initialSection: storage);
        UiRouteButtonSceneNode flowRoute = Route(session.Host.Root.Scene, flow);
        UiPoint pointer = Center(Layout(session, flowRoute.Id).Bounds);
        Assert.True(session.PressPointer(pointer).Consumed);
        platform.FailOnFlowContent = true;

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => session.ReleasePointer(pointer));

        Assert.Contains("Flow content", error.Message, StringComparison.Ordinal);
        Assert.Equal(storage, session.ActiveSection);
        Assert.Equal(storage, session.CurrentInvocation.Experience.Id);
        Assert.True(Route(session.Host.Root.Scene, storage).IsCurrent);
        Assert.False(Route(session.Host.Root.Scene, flow).IsCurrent);
    }

    [Fact]
    public void MatchingPendingRouteLeavesCurrentSceneInvocationAndSectionIntact()
    {
        UiSymbolId flow = Id("terminal-host-pending/flow");
        UiSymbolId storage = Id("terminal-host-pending/storage");
        var requested = new List<UiSymbolId>();
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(flow, "Flow", () => Section(flow, "Flow"), order: 10)
            .TerminalSection(
                storage,
                "Storage",
                () => throw new UiTerminalSectionActivationPendingException(storage),
                order: 20)
            .Freeze();
        using var session = new UiTerminalHostSession(
            registry,
            UiThemePresets.Dark(),
            Placement,
            new TestPlatform(),
            UiPresentationProfiles.Wide,
            initialSection: flow,
            onRouteRequested: requested.Add);
        UiRouteButtonSceneNode storageRoute = Route(session.Host.Root.Scene, storage);
        UiPoint pointer = Center(Layout(session, storageRoute.Id).Bounds);

        Assert.True(session.PressPointer(pointer).Consumed);
        UiPortalDispatch released = session.ReleasePointer(pointer);

        Assert.True(released.Consumed);
        Assert.Equal(storage, released.Interaction?.Route);
        Assert.Equal(new[] { storage }, requested);
        Assert.Equal(flow, session.ActiveSection);
        Assert.Equal(flow, session.CurrentInvocation.Experience.Id);
        Assert.Equal(flow, session.Host.Root.Scene.Experience);
        Assert.True(Route(session.Host.Root.Scene, flow).IsCurrent);
        Assert.False(Route(session.Host.Root.Scene, storage).IsCurrent);
    }

    [Fact]
    public void NonmatchingPendingSignalPropagates()
    {
        UiSymbolId flow = Id("terminal-host-pending-wrong/flow");
        UiSymbolId storage = Id("terminal-host-pending-wrong/storage");
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(flow, "Flow", () => Section(flow, "Flow"), order: 10)
            .TerminalSection(
                storage,
                "Storage",
                () => throw new UiTerminalSectionActivationPendingException(flow),
                order: 20)
            .Freeze();
        using var session = new UiTerminalHostSession(
            registry,
            UiThemePresets.Dark(),
            Placement,
            new TestPlatform(),
            UiPresentationProfiles.Wide,
            initialSection: flow);
        UiRouteButtonSceneNode storageRoute = Route(session.Host.Root.Scene, storage);
        UiPoint pointer = Center(Layout(session, storageRoute.Id).Bounds);
        Assert.True(session.PressPointer(pointer).Consumed);

        Assert.Throws<UiTerminalSectionActivationPendingException>(() => session.ReleasePointer(pointer));
        Assert.Equal(flow, session.ActiveSection);
    }

    [Fact]
    public void ProgrammaticOpenSectionCommitsOnlyAfterHostUpdate()
    {
        UiSymbolId flow = Id("terminal-host-programmatic/flow");
        UiSymbolId storage = Id("terminal-host-programmatic/storage");
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(flow, "Flow", () => Section(flow, "Flow"), order: 10)
            .TerminalSection(storage, "Storage", () => Section(storage, "Storage"), order: 20)
            .Freeze();
        var platform = new TestPlatform { FailOnStorageContent = true };
        using var session = new UiTerminalHostSession(
            registry,
            UiThemePresets.Dark(),
            Placement,
            platform,
            UiPresentationProfiles.Wide,
            initialSection: flow);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => session.OpenSection(storage));

        Assert.Contains("Storage content", error.Message, StringComparison.Ordinal);
        Assert.Equal(flow, session.ActiveSection);
        Assert.Equal(flow, session.CurrentInvocation.Experience.Id);
        Assert.True(Route(session.Host.Root.Scene, flow).IsCurrent);
        Assert.False(Route(session.Host.Root.Scene, storage).IsCurrent);
    }

    [Fact]
    public void SceneValidationRejectsInitialFrameAndRetiresOwnedSection()
    {
        UiSymbolId storage = Id("terminal-host-validation-initial/storage");
        int disposed = 0;
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSectionOwned(
                storage,
                "Storage",
                () => (Section(storage, "Storage"), new RecordingOwner(() => disposed++)))
            .Freeze();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new UiTerminalHostSession(
                registry,
                UiThemePresets.Dark(),
                Placement,
                new TestPlatform(),
                UiPresentationProfiles.Wide,
                validateScene: _ => throw new InvalidOperationException("Unsupported backend capability.")));

        Assert.Contains("Unsupported backend capability", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, disposed);
    }

    [Fact]
    public void SceneValidationRejectsRoutedFrameBeforeHostRouteReplacementOrCommit()
    {
        UiSymbolId flow = Id("terminal-host-validation-route/flow");
        UiSymbolId storage = Id("terminal-host-validation-route/storage");
        bool rejectFlow = false;
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(flow, "Flow", () => Section(flow, "Flow"), order: 10)
            .TerminalSection(storage, "Storage", () => Section(storage, "Storage"), order: 20)
            .Freeze();
        using var session = new UiTerminalHostSession(
            registry,
            UiThemePresets.Dark(),
            Placement,
            new TestPlatform(),
            UiPresentationProfiles.Wide,
            initialSection: storage,
            validateScene: scene =>
            {
                if (rejectFlow && Route(scene, flow).IsCurrent)
                    throw new InvalidOperationException("Unsupported routed capability.");
            });
        UiRouteButtonSceneNode flowRoute = Route(session.Host.Root.Scene, flow);
        UiPoint pointer = Center(Layout(session, flowRoute.Id).Bounds);
        Assert.True(session.PressPointer(pointer).Consumed);
        rejectFlow = true;

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => session.ReleasePointer(pointer));

        Assert.Contains("Unsupported routed capability", error.Message, StringComparison.Ordinal);
        Assert.Equal(storage, session.Host.Root.Scene.Experience);
        Assert.Equal(storage, session.ActiveSection);
        Assert.Equal(storage, session.CurrentInvocation.Experience.Id);
        Assert.True(Route(session.Host.Root.Scene, storage).IsCurrent);
        Assert.False(Route(session.Host.Root.Scene, flow).IsCurrent);
    }

    [Fact]
    public void FocusedRouteSubmitPreservesAccessibleFocusAndSelection()
    {
        UiSymbolId flow = Id("terminal-host-focus/flow");
        UiSymbolId storage = Id("terminal-host-focus/storage");
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(flow, "Flow", () => Section(flow, "Flow"), order: 10)
            .TerminalSection(storage, "Storage", () => Section(storage, "Storage"), order: 20)
            .Freeze();
        using var session = new UiTerminalHostSession(
            registry,
            UiThemePresets.Dark(),
            Placement,
            new TestPlatform(),
            UiPresentationProfiles.Wide,
            initialSection: storage);

        Assert.True(session.MoveFocus(UiNavigationDirection.Next).Consumed);
        UiPortalDispatch focused = session.MoveFocus(UiNavigationDirection.Previous);
        UiPortalDispatch submitted = session.Submit();

        Assert.True(focused.Consumed);
        Assert.True(submitted.Consumed);
        Assert.Equal(flow, submitted.Interaction?.Route);
        Assert.Equal(flow, session.ActiveSection);
        UiAccessibilityNodeSnapshot route = AccessibilityNodes(session.Host.Accessibility.Root.Root)
            .Single(node => node.Id == flow.Child("terminal/navigation-route"));
        Assert.Equal(UiAccessibilityRole.Button, route.Role);
        Assert.True(route.Focused);
        Assert.True(route.Selected);
    }

    [Fact]
    public void SparseTerminalAccessibilityExposesOnlyPositiveFiniteGeometry()
    {
        using UiTerminalHostSession session = SparseDogfoodTerminal();
        UiSlotSceneNode[] emptySlots = session.Host.Root.Scene.Root.Children
            .OfType<UiSlotSceneNode>()
            .Where(slot => slot.Children.Count == 0)
            .ToArray();
        UiSymbolId[] collapsedSlots = emptySlots
            .Where(slot =>
            {
                UiLayoutEntry entry = Layout(session, slot.Id);
                return entry.Bounds.Width <= 0 || entry.Bounds.Height <= 0;
            })
            .Select(slot => slot.Slot)
            .ToArray();

        Assert.NotEmpty(emptySlots);
        Assert.Equal(
            new[] { UiHostSlots.Utility, UiHostSlots.Context, UiHostSlots.Actions },
            collapsedSlots);
        Assert.All(
            AccessibilityNodes(session.Host.Accessibility.Root.Root),
            node =>
            {
                Assert.True(float.IsFinite(node.Bounds.X));
                Assert.True(float.IsFinite(node.Bounds.Y));
                Assert.True(node.Bounds.Width > 0, $"Accessibility node '{node.Id}' has non-positive width.");
                Assert.True(node.Bounds.Height > 0, $"Accessibility node '{node.Id}' has non-positive height.");
            });
    }

    [Fact]
    public void TerminalHostEstablishesOneInitialFocusTargetWithFocusedVisuals()
    {
        using UiTerminalHostSession session = SparseDogfoodTerminal();

        UiAccessibilityNodeSnapshot focused = Assert.Single(
            AccessibilityNodes(session.Host.Accessibility.Root.Root),
            node => node.Focused);
        UiRouteButtonSceneNode route = Assert.Single(
            session.Host.Root.Scene.Root.Children
                .OfType<UiSlotSceneNode>()
                .SelectMany(slot => slot.Children)
                .OfType<UiRouteButtonSceneNode>(),
            node => node.Id == focused.Id);
        UiResolvedVisualProperty border = Assert.Single(
            route.Visual.Properties,
            property => string.Equals(property.Property.Name, "border", StringComparison.Ordinal));

        Assert.Equal(IdForDogfood("dogfood/diagnostics/terminal/navigation-route"), focused.Id);
        Assert.Equal(UiThemeTokens.BorderFocus.Id, border.Token);
        Assert.Contains(
            route.Visual.Trace,
            step => step.Property == border.Property && step.Token == UiThemeTokens.BorderFocus.Id);
    }

    [Fact]
    public void DisposalRetiresOwnedSectionAndRejectsFurtherInput()
    {
        UiSymbolId storage = Id("terminal-host-disposal/storage");
        int disposed = 0;
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSectionOwned(
                storage,
                "Storage",
                () => (Section(storage, "Storage"), new RecordingOwner(() => disposed++)))
            .Freeze();
        var session = new UiTerminalHostSession(
            registry,
            UiThemePresets.Dark(),
            Placement,
            new TestPlatform(),
            UiPresentationProfiles.Wide);

        session.Dispose();
        session.Dispose();

        Assert.Equal(1, disposed);
        Assert.Null(session.ActiveSection);
        Assert.Throws<ObjectDisposedException>(() => session.PressPointer(new UiPoint(0, 0)));
        Assert.Throws<ObjectDisposedException>(() => session.CurrentInvocation);
    }

    [Fact]
    public void TransientHostRecompositionPreservesActivatedModelDraftAndAction()
    {
        UiSymbolId section = Id("terminal-transient/section");
        int created = 0;
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(section, "Storage", () => { created++; return Section(section, "Storage"); },
                lifetime: UiExperienceLifetime.Transient)
            .Freeze();
        using var session = new UiTerminalHostSession(registry, UiThemePresets.Dark(), Placement,
            new TestPlatform(), UiPresentationProfiles.Wide);
        UiExperienceDefinition model = session.CurrentInvocation.Experience;
        UiState<string> draft = Assert.IsType<UiState<string>>(model.Elements.Single(e => e.Name == "Search").Source);

        Assert.Equal(1, created);
        Assert.Same(model.Actions[0], Assert.Single(SceneNodes(session.Host.Root.Scene.Root).OfType<UiButtonSceneNode>()).Action);
        Assert.True(session.MoveFocus(UiNavigationDirection.Next).Consumed);
        UiTextInputSceneNode input = Assert.Single(SceneNodes(session.Host.Root.Scene.Root).OfType<UiTextInputSceneNode>());
        UiPoint pointer = Center(Layout(session, input.Id).Bounds);
        Assert.True(session.PressPointer(pointer).Consumed);
        Assert.True(session.ReleasePointer(pointer).Consumed);
        Assert.True(session.InsertText("Draft").Consumed);
        Assert.Equal("Draft", draft.Value);
        Assert.Equal("Draft", Assert.Single(SceneNodes(session.Host.Root.Scene.Root).OfType<UiTextInputSceneNode>()).Text);
        Assert.True(session.ReplaceText("Сохранённый ввод").Consumed);
        UiSymbolId? focus = session.Host.Root.Interactions.Snapshot.Focused;
        Assert.Equal(input.Id, focus);
        UiColor foreground = session.Host.Root.Frame.Primitives.OfType<UiTextPrimitive>().First().Foreground;

        session.SetTheme(UiSemanticThemes.Resolve(UiSemanticTheme.Light));
        session.Recompose(UiPresentationProfiles.Compact,
            new UiHostPlacementContext(new UiRect(0, 0, 640, 480)), "ru");

        Assert.Equal(1, created);
        Assert.Same(model, session.CurrentInvocation.Experience);
        Assert.Equal(UiPresentationProfiles.Compact.Id, session.CurrentInvocation.Plan.Host.Profile);
        Assert.Equal("Сохранённый ввод", draft.Value);
        Assert.Equal("Сохранённый ввод", Assert.Single(SceneNodes(session.Host.Root.Scene.Root).OfType<UiTextInputSceneNode>()).Text);
        Assert.Equal(focus, session.Host.Root.Interactions.Snapshot.Focused);
        Assert.Same(model.Actions[0], Assert.Single(SceneNodes(session.Host.Root.Scene.Root).OfType<UiButtonSceneNode>()).Action);
        Assert.NotEqual(foreground, session.Host.Root.Frame.Primitives.OfType<UiTextPrimitive>().First().Foreground);
    }

    [Fact]
    public void TransientRouteFailurePreservesModelAndExplicitReopenCreatesNewModel()
    {
        UiSymbolId flow = Id("terminal-transient-route/flow");
        UiSymbolId storage = Id("terminal-transient-route/storage");
        int created = 0;
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(flow, "Flow", () => { created++; return Section(flow, "Flow"); },
                lifetime: UiExperienceLifetime.Transient)
            .TerminalSection(storage, "Storage", () => Section(storage, "Storage"),
                lifetime: UiExperienceLifetime.Transient)
            .Freeze();
        var platform = new TestPlatform();
        using var session = new UiTerminalHostSession(registry, UiThemePresets.Dark(), Placement,
            platform, UiPresentationProfiles.Wide, initialSection: flow);
        UiExperienceDefinition original = session.CurrentInvocation.Experience;
        UiState<string> draft = Assert.IsType<UiState<string>>(original.Elements.Single(e => e.Name == "Search").Source);
        draft.Value = "Retain through failed route";
        UiPoint pointer = Center(Layout(session, Route(session.Host.Root.Scene, storage).Id).Bounds);
        Assert.True(session.PressPointer(pointer).Consumed);
        platform.FailOnStorageContent = true;

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => session.ReleasePointer(pointer));

        Assert.Contains("Storage content", error.Message, StringComparison.Ordinal);
        session.Recompose(UiPresentationProfiles.Wide, Placement);
        Assert.Equal(1, created);
        Assert.Equal(flow, session.ActiveSection);
        Assert.Same(original, session.CurrentInvocation.Experience);
        Assert.Equal("Retain through failed route", Assert.Single(SceneNodes(session.Host.Root.Scene.Root).OfType<UiTextInputSceneNode>()).Text);
        Assert.Same(original.Actions[0], Assert.Single(SceneNodes(session.Host.Root.Scene.Root).OfType<UiButtonSceneNode>()).Action);

        session.OpenSection(flow);

        Assert.Equal(2, created);
        Assert.NotSame(original, session.CurrentInvocation.Experience);
        Assert.NotSame(original.Actions[0], Assert.Single(SceneNodes(session.Host.Root.Scene.Root).OfType<UiButtonSceneNode>()).Action);
        Assert.Equal(string.Empty, Assert.Single(SceneNodes(session.Host.Root.Scene.Root).OfType<UiTextInputSceneNode>()).Text);
        Assert.Equal("Retain through failed route", draft.Value);
    }

    [Fact]
    public void TransientRecompositionRechecksAvailabilityWithoutActivatingSections()
    {
        UiSymbolId active = Id("terminal-transient-availability/active");
        UiSymbolId other = Id("terminal-transient-availability/other");
        int activeCreated = 0, otherCreated = 0;
        bool activeAvailable = true, otherAvailable = false;
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(active, "Active", () => { activeCreated++; return Section(active, "Active"); },
                lifetime: UiExperienceLifetime.Transient, isAvailable: () => activeAvailable)
            .TerminalSection(other, "Other", () => { otherCreated++; return Section(other, "Other"); },
                lifetime: UiExperienceLifetime.Transient, isAvailable: () => otherAvailable)
            .Freeze();
        using var session = new UiTerminalHostSession(registry, UiThemePresets.Dark(), Placement,
            new TestPlatform(), UiPresentationProfiles.Wide, initialSection: active);
        UiExperienceDefinition model = session.CurrentInvocation.Experience;
        Assert.Single(SceneNodes(session.Host.Root.Scene.Root).OfType<UiRouteButtonSceneNode>());
        otherAvailable = true;

        session.Recompose(UiPresentationProfiles.Wide, Placement);

        Assert.False(Route(session.Host.Root.Scene, other).IsCurrent);
        Assert.Equal(1, activeCreated);
        Assert.Equal(0, otherCreated);
        UiScene scene = session.Host.Root.Scene;
        activeAvailable = false;
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => session.Recompose(UiPresentationProfiles.Wide, Placement));
        Assert.Contains("currently unavailable", error.Message, StringComparison.Ordinal);
        Assert.Same(scene, session.Host.Root.Scene);
        Assert.Same(model, session.CurrentInvocation.Experience);
        activeAvailable = true;
        otherAvailable = false;

        session.Recompose(UiPresentationProfiles.Wide, Placement);

        Assert.Single(SceneNodes(session.Host.Root.Scene.Root).OfType<UiRouteButtonSceneNode>());
        Assert.Same(model, session.CurrentInvocation.Experience);
        Assert.Equal(1, activeCreated);
        Assert.Equal(0, otherCreated);
    }

    [Fact]
    public void RecompositionCallbackCannotPublishAfterTerminalDisposal()
    {
        UiTerminalHostSession? session = null;
        bool close = false;
        int validations = 0;
        UiSymbolId section = Id("terminal-compose-disposal/section");
        var action = new UiActionDefinition(section.Child("action/check"), "Check", () => { }, () =>
        {
            if (close) { close = false; session!.Dispose(); }
            return true;
        });
        UiExperienceDefinition model = new UiExperienceBuilder(section, "Section").Actions("Actions", action).Build();
        UiRegistrySnapshot registry = new UiRegistryBuilder().TerminalSection(section, "Section", () => model).Freeze();
        using (session = new UiTerminalHostSession(registry, UiThemePresets.Dark(), Placement,
            new TestPlatform(), UiPresentationProfiles.Wide, validateScene: _ => validations++))
        {
            UiScene previous = session.Host.Root.Scene;
            int before = validations;
            close = true;

            Assert.Throws<ObjectDisposedException>(() => session.Recompose(UiPresentationProfiles.Compact, Placement));

            Assert.Equal(before, validations);
            Assert.Same(previous, session.Host.Root.Scene);
            Assert.Null(session.ActiveSection);
            Assert.Throws<ObjectDisposedException>(() => session.Submit());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecompositionCallbackCannotOverwriteNestedCommittedOpen(bool reopenSameCachedModel)
    {
        UiTerminalHostSession? session = null;
        UiScene? nestedScene = null;
        Hatifect.UI.Runtime.Invocation.UiInvocationResult? nestedInvocation = null;
        bool open = false;
        UiSymbolId first = Id("terminal-compose-reentry/first");
        UiSymbolId second = Id("terminal-compose-reentry/second");
        UiSymbolId target = reopenSameCachedModel ? first : second;
        var action = new UiActionDefinition(first.Child("action/check"), "Check", () => { }, () =>
        {
            if (open)
            {
                open = false;
                session!.OpenSection(target);
                nestedScene = session.Host.Root.Scene;
                nestedInvocation = session.CurrentInvocation;
            }
            return true;
        });
        UiExperienceDefinition model = new UiExperienceBuilder(first, "First").Actions("Actions", action).Build();
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(first, "First", () => model)
            .TerminalSection(second, "Second", () => Section(second, "Second"))
            .Freeze();
        using (session = new UiTerminalHostSession(registry, UiThemePresets.Dark(), Placement,
            new TestPlatform(), UiPresentationProfiles.Wide, initialSection: first))
        {
            open = true;

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => session.Recompose(UiPresentationProfiles.Compact, Placement));

            Assert.Contains("changed during recomposition", error.Message, StringComparison.Ordinal);
            Assert.NotNull(nestedScene);
            Assert.NotNull(nestedInvocation);
            Assert.Same(nestedScene, session.Host.Root.Scene);
            Assert.Same(nestedInvocation, session.CurrentInvocation);
            Assert.Equal(target, session.ActiveSection);
            Assert.Equal(target, session.Host.Root.Scene.Experience);
            Assert.Equal(UiPresentationProfiles.Wide.Id, session.CurrentInvocation.Plan.Host.Profile);
            if (reopenSameCachedModel) Assert.Same(model, session.CurrentInvocation.Experience);
        }
    }

    [Fact]
    public void ValidationDisposalCannotPublishThePreparedTerminalFrame()
    {
        UiTerminalHostSession? session = null;
        bool close = false;
        UiSymbolId section = Id("terminal-validation-disposal/section");
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(section, "Section", () => Section(section, "Section")).Freeze();
        using (session = new UiTerminalHostSession(registry, UiThemePresets.Dark(), Placement,
            new TestPlatform(), UiPresentationProfiles.Wide,
            validateScene: _ => { if (close) { close = false; session!.Dispose(); } }))
        {
            UiScene scene = session.Host.Root.Scene;
            UiLayoutSnapshot layout = session.Host.Root.Layout;
            var frame = session.Host.Root.Frame;
            var accessibility = session.Host.Root.Accessibility;
            close = true;

            Assert.Throws<ObjectDisposedException>(() => session.Recompose(UiPresentationProfiles.Compact, Placement));

            Assert.Same(scene, session.Host.Root.Scene);
            Assert.Same(layout, session.Host.Root.Layout);
            Assert.Same(frame, session.Host.Root.Frame);
            Assert.Same(accessibility, session.Host.Root.Accessibility);
            Assert.Null(session.ActiveSection);
            Assert.Throws<ObjectDisposedException>(() => session.Host.Root.Interactions.Submit());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidationCannotOverwriteANestedAcceptedTerminalFrame(bool reopenSameCachedModel)
    {
        UiTerminalHostSession? session = null;
        UiScene? nestedScene = null;
        Hatifect.UI.Runtime.Invocation.UiInvocationResult? nestedInvocation = null;
        bool open = false;
        UiSymbolId first = Id("terminal-validation-reentry/first");
        UiSymbolId second = Id("terminal-validation-reentry/second");
        UiSymbolId target = reopenSameCachedModel ? first : second;
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(first, "First", () => Section(first, "First"))
            .TerminalSection(second, "Second", () => Section(second, "Second")).Freeze();
        using (session = new UiTerminalHostSession(registry, UiThemePresets.Dark(), Placement,
            new TestPlatform(), UiPresentationProfiles.Wide, initialSection: first,
            validateScene: _ =>
            {
                if (!open) return;
                open = false;
                session!.OpenSection(target);
                nestedScene = session.Host.Root.Scene;
                nestedInvocation = session.CurrentInvocation;
            }))
        {
            open = true;

            Assert.Throws<InvalidOperationException>(() => session.Recompose(UiPresentationProfiles.Compact, Placement));

            Assert.NotNull(nestedScene);
            Assert.NotNull(nestedInvocation);
            Assert.Same(nestedScene, session.Host.Root.Scene);
            Assert.Same(nestedInvocation, session.CurrentInvocation);
            Assert.Equal(target, session.ActiveSection);
            Assert.Equal(UiPresentationProfiles.Wide.Id, session.CurrentInvocation.Plan.Host.Profile);
        }
    }

    [Theory]
    [InlineData("open")]
    [InlineData("interaction")]
    [InlineData("follow")]
    public void ValidationRetirementFencesEveryTerminalEntryPoint(string operation)
    {
        UiTerminalHostSession? session = null;
        bool close = false;
        UiScene? accepted = null;
        UiLayoutSnapshot? layout = null;
        UiRenderFrame? frame = null;
        UiAccessibilitySnapshot? accessibility = null;
        UiSymbolId first = Id("validation-entry/first");
        UiSymbolId second = Id("validation-entry/second");
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(first, "First", () => Section(first, "First"))
            .TerminalSection(second, "Second", () => Section(second, "Second")).Freeze();
        using (session = new UiTerminalHostSession(registry, UiThemePresets.Dark(), Placement,
            new TestPlatform(), UiPresentationProfiles.Wide, initialSection: first,
            validateScene: scene =>
            {
                if (!close || operation == "follow" && scene.Experience != second) return;
                close = false;
                accepted = session!.Host.Root.Scene;
                layout = session.Host.Root.Layout;
                frame = session.Host.Root.Frame;
                accessibility = session.Host.Root.Accessibility;
                session.Dispose();
            }))
        {
            UiPoint pointer = Center(Layout(session, Route(session.Host.Root.Scene, second).Id).Bounds);
            if (operation == "follow") Assert.True(session.PressPointer(pointer).Consumed);
            close = true;

            Assert.Throws<ObjectDisposedException>(() =>
            {
                if (operation == "open") session.OpenSection(second);
                else if (operation == "interaction") session.Host.Root.RefreshInteractionVisuals();
                else session.ReleasePointer(pointer);
            });

            Assert.NotNull(accepted);
            Assert.Same(accepted, session.Host.Root.Scene);
            Assert.Same(layout, session.Host.Root.Layout);
            Assert.Same(frame, session.Host.Root.Frame);
            Assert.Same(accessibility, session.Host.Root.Accessibility);
            Assert.Null(session.ActiveSection);
        }
    }

    [Theory]
    [InlineData("open")]
    [InlineData("interaction")]
    [InlineData("follow")]
    public void NestedRecompositionDuringValidationKeepsItsAcceptedFrame(string operation)
    {
        UiTerminalHostSession? session = null;
        bool recompose = false;
        UiScene? nestedScene = null;
        Hatifect.UI.Runtime.Invocation.UiInvocationResult? nestedInvocation = null;
        UiSymbolId first = Id("validation-nested-recompose/first");
        UiSymbolId second = Id("validation-nested-recompose/second");
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(first, "First", () => Section(first, "First"))
            .TerminalSection(second, "Second", () => Section(second, "Second")).Freeze();
        using (session = new UiTerminalHostSession(registry, UiThemePresets.Dark(), Placement,
            new TestPlatform(), UiPresentationProfiles.Wide, initialSection: first,
            validateScene: scene =>
            {
                if (!recompose || operation == "follow" && scene.Experience != second) return;
                recompose = false;
                session!.Recompose(UiPresentationProfiles.Compact, Placement);
                nestedScene = session.Host.Root.Scene;
                nestedInvocation = session.CurrentInvocation;
            }))
        {
            UiPoint pointer = Center(Layout(session, Route(session.Host.Root.Scene, second).Id).Bounds);
            if (operation == "follow") Assert.True(session.PressPointer(pointer).Consumed);
            recompose = true;

            Assert.Throws<InvalidOperationException>(() =>
            {
                if (operation == "open") session.OpenSection(second);
                else if (operation == "interaction") session.Host.Root.RefreshInteractionVisuals();
                else session.ReleasePointer(pointer);
            });

            Assert.NotNull(nestedScene);
            Assert.Same(nestedScene, session.Host.Root.Scene);
            Assert.Same(nestedInvocation, session.CurrentInvocation);
            Assert.Equal(first, session.ActiveSection);
            Assert.Equal(UiPresentationProfiles.Compact.Id, session.CurrentInvocation.Plan.Host.Profile);
            session.Recompose(UiPresentationProfiles.Wide, Placement);
            Assert.Equal(UiPresentationProfiles.Wide.Id, session.CurrentInvocation.Plan.Host.Profile);
        }
    }

    [Fact]
    public void MeasurementDisposalCannotAcceptTheValidatedTerminalCandidate()
    {
        UiSymbolId section = Id("terminal-measure-disposal/section");
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(section, "Section", () => Section(section, "Section")).Freeze();
        var platform = new TestPlatform();
        int validations = 0;
        using var session = new UiTerminalHostSession(registry, UiThemePresets.Dark(), Placement,
            platform, UiPresentationProfiles.Wide, validateScene: _ => validations++);
        UiScene scene = session.Host.Root.Scene;
        UiLayoutSnapshot layout = session.Host.Root.Layout;
        var frame = session.Host.Root.Frame;
        var accessibility = session.Host.Root.Accessibility;
        int before = validations;
        platform.OnMeasure = () => { platform.OnMeasure = null; session.Dispose(); };

        Assert.Throws<ObjectDisposedException>(() => session.Recompose(UiPresentationProfiles.Compact,
            new UiHostPlacementContext(new UiRect(0, 0, 700, 400))));

        Assert.Equal(before + 1, validations);
        Assert.Same(scene, session.Host.Root.Scene);
        Assert.Same(layout, session.Host.Root.Layout);
        Assert.Same(frame, session.Host.Root.Frame);
        Assert.Same(accessibility, session.Host.Root.Accessibility);
        Assert.Null(session.ActiveSection);
    }

    [Fact]
    public void AvailabilityDisposalPreventsTheLegacyActionEffect()
    {
        UiTerminalHostSession? session = null;
        bool close = false;
        int effects = 0;
        UiSymbolId section = Id("terminal-admission-disposal/section");
        var action = new UiActionDefinition(section.Child("action/apply"), "Apply", () => effects++, () =>
        {
            if (close) { close = false; session!.Dispose(); }
            return true;
        });
        UiExperienceDefinition model = new UiExperienceBuilder(section, "Section").Actions("Actions", action).Build();
        UiRegistrySnapshot registry = new UiRegistryBuilder().TerminalSection(section, "Section", () => model).Freeze();
        using (session = new UiTerminalHostSession(registry, UiThemePresets.Dark(), Placement,
            new TestPlatform(), UiPresentationProfiles.Wide))
        {
            UiButtonSceneNode button = Assert.Single(SceneNodes(session.Host.Root.Scene.Root).OfType<UiButtonSceneNode>());
            Assert.True(session.PressPointer(Center(Layout(session, button.Id).Bounds)).Consumed);
            Assert.Equal(button.Id, session.Host.Root.Interactions.Snapshot.Focused);
            close = true;

            Assert.Throws<ObjectDisposedException>(() => session.Host.Root.Interactions.Submit());

            Assert.Equal(0, effects);
            Assert.Null(session.ActiveSection);
        }
    }

    [Theory]
    [InlineData("other-section")]
    [InlineData("same-section")]
    [InlineData("recompose")]
    public void AvailabilitySceneReplacementPreventsTheLegacyActionEffect(string replacement)
    {
        UiTerminalHostSession? session = null;
        bool replace = false;
        int effects = 0;
        UiScene? acceptedScene = null;
        UiSymbolId first = Id("terminal-admission-replacement/first");
        UiSymbolId second = Id("terminal-admission-replacement/second");
        var action = new UiActionDefinition(first.Child("action/apply"), "Apply", () => effects++, () =>
        {
            if (replace)
            {
                replace = false;
                if (replacement == "recompose") session!.Recompose(UiPresentationProfiles.Compact, Placement);
                else session!.OpenSection(replacement == "other-section" ? second : first);
                acceptedScene = session!.Host.Root.Scene;
            }
            return true;
        });
        UiExperienceDefinition model = new UiExperienceBuilder(first, "First").Actions("Actions", action).Build();
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(first, "First", () => model)
            .TerminalSection(second, "Second", () => Section(second, "Second")).Freeze();
        using (session = new UiTerminalHostSession(registry, UiThemePresets.Dark(), Placement,
            new TestPlatform(), UiPresentationProfiles.Wide, initialSection: first))
        {
            UiButtonSceneNode button = Assert.Single(SceneNodes(session.Host.Root.Scene.Root).OfType<UiButtonSceneNode>());
            Assert.True(session.PressPointer(Center(Layout(session, button.Id).Bounds)).Consumed);
            Assert.Equal(button.Id, session.Host.Root.Interactions.Snapshot.Focused);
            var retainedInput = session.Host.Root.Interactions;
            UiScene previousScene = session.Host.Root.Scene;
            replace = true;

            Exception? error = Record.Exception(() => retainedInput.Submit());

            Assert.NotNull(acceptedScene);
            Assert.NotSame(previousScene, acceptedScene);
            Assert.Same(acceptedScene, session.Host.Root.Scene);
            Assert.Equal(replacement == "other-section" ? second : first, session.ActiveSection);
            Assert.Equal(0, effects);
            Assert.IsType<InvalidOperationException>(error);

            session.OpenSection(first);
            button = Assert.Single(SceneNodes(session.Host.Root.Scene.Root).OfType<UiButtonSceneNode>());
            Assert.True(session.PressPointer(Center(Layout(session, button.Id).Bounds)).Consumed);
            Assert.True(session.Host.Root.Interactions.Submit().ActionInvoked);
            Assert.Equal(1, effects);
        }
    }

    private static IEnumerable<UiSceneNode> SceneNodes(UiSceneNode node)
    {
        yield return node;
        foreach (UiSceneNode child in node.Children)
        foreach (UiSceneNode descendant in SceneNodes(child))
            yield return descendant;
    }

    private static UiHostPlacementContext Placement { get; }
        = new(new UiRect(0, 0, 960, 540));

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

    private static UiTerminalHostSession SparseDogfoodTerminal()
    {
        UiSymbolId diagnostics = IdForDogfood("dogfood/diagnostics");
        UiSymbolId inspector = diagnostics.Child("devtools/inspector");
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(
                diagnostics,
                "Diagnostics",
                () => MonitorOnly(diagnostics, "Semantic host diagnostics"),
                group: "System",
                order: 100)
            .TerminalSection(
                inspector,
                "Inspector",
                () => MonitorOnly(inspector, "Inspector"),
                group: "System",
                order: 110)
            .Freeze();
        return new UiTerminalHostSession(
            registry,
            UiThemePresets.Dark(),
            new UiHostPlacementContext(new UiRect(0, 0, 1470, 956)),
            new TestPlatform(),
            UiPresentationProfiles.Wide,
            initialSection: diagnostics,
            locale: "English");
    }

    private static UiExperienceDefinition MonitorOnly(UiSymbolId id, string title)
        => new UiExperienceBuilder(id, title)
            .Monitor("Status", new UiConstantSource<string>("Clean-slate Runtime is active."))
            .Build();

    private static UiRouteButtonSceneNode Route(UiScene scene, UiSymbolId route)
        => scene.Root.Children
            .Cast<UiSlotSceneNode>()
            .Single(slot => slot.Slot == UiHostSlots.Navigation)
            .Children
            .OfType<UiRouteButtonSceneNode>()
            .Single(node => node.Route == route);

    private static UiLayoutEntry Layout(UiTerminalHostSession session, UiSymbolId node)
    {
        Assert.True(session.Host.Root.Layout.TryGetEntry(node, out UiLayoutEntry? entry));
        return Assert.IsType<UiLayoutEntry>(entry);
    }

    private static IEnumerable<UiAccessibilityNodeSnapshot> AccessibilityNodes(
        UiAccessibilityNodeSnapshot node)
    {
        yield return node;
        foreach (UiAccessibilityNodeSnapshot child in node.Children)
        foreach (UiAccessibilityNodeSnapshot descendant in AccessibilityNodes(child))
            yield return descendant;
    }

    private static UiPoint Center(UiRect bounds)
        => new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);

    private static UiSymbolId Id(string local) => new("Hatifect.Dogfood", local);

    private static UiSymbolId IdForDogfood(string local) => new("Hatifect.UI", local);

    private sealed class RecordingOwner : IDisposable
    {
        private readonly Action _dispose;

        public RecordingOwner(Action dispose) => _dispose = dispose;

        public void Dispose() => _dispose();
    }

    private sealed class TestPlatform : IUiPlatformBridge
    {
        public bool FailOnFlowContent { get; set; }
        public bool FailOnStorageContent { get; set; }
        public Action? OnMeasure { get; set; }

        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
        {
            OnMeasure?.Invoke();
            if (FailOnFlowContent && text.Contains("Flow item", StringComparison.Ordinal))
                throw new InvalidOperationException("Flow content measurement failed.");
            if (FailOnStorageContent && text.Contains("Storage item", StringComparison.Ordinal))
                throw new InvalidOperationException("Storage content measurement failed.");
            return new UiSize(
                Math.Min(text.Length * typography.Size * 0.6f, availableWidth),
                typography.Size * typography.LineHeight);
        }

        public void DrawSurface(UiSurfacePrimitive surface) { }
        public void DrawText(UiTextPrimitive text) { }
    }
}
