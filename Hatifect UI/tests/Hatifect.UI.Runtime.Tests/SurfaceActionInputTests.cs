using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Diagnostics;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Runtime.Registration;
using Hatifect.UI.Runtime.Rendering;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Visual;
using Hatifect.UI.Runtime.Visual.Theming;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class SurfaceActionInputTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContainedWindowReachesAnActionFromEitherEndThroughOrdinaryInput(bool fromLast)
    {
        var fixture = new Fixture();
        if (fromLast) fixture.MoveToLast();
        UiSurfaceRenderStamp rendered = fixture.Host.Root.LastCompletedRender;

        Assert.True(fixture.Activate());

        Assert.Equal(1, fixture.Effects);
        Assert.Equal(1, fixture.Submits);
        Assert.InRange(fixture.Directions.Count, 1, 256);
        Assert.Contains(fromLast ? UiNavigationDirection.Previous : UiNavigationDirection.Next, fixture.Directions);
        Assert.Equal(fixture.Target.Id, fixture.Host.Root.Interactions.Snapshot.Focused);
        Assert.True(fixture.Host.Root.Layout.TryGetEntry(fixture.Target.Id, out var entry));
        Assert.True(entry!.Bounds.Height > 0 && entry.Bounds.Y >= entry.Clip.Y
            && entry.Bounds.Bottom <= entry.Clip.Bottom);
        Assert.Equal(rendered, fixture.Host.Root.LastCompletedRender);
    }

    [Fact]
    public void MissingActionAndRejectedOwnerDoNotDispatchOrChangeTheFrame()
    {
        var fixture = new Fixture();
        var frame = fixture.Host.Root.Frame;

        Assert.False(UiSurfaceActionInput.Activate(fixture.Host, new("test", "absent"), () => { },
            fixture.Navigate, fixture.Submit));
        Assert.Throws<InvalidOperationException>(() => UiSurfaceActionInput.Activate(fixture.Host, fixture.Action,
            () => throw new InvalidOperationException("Lost owner."), fixture.Navigate, fixture.Submit));

        Assert.Empty(fixture.Directions);
        Assert.Equal(0, fixture.Submits);
        Assert.Equal(0, fixture.Effects);
        Assert.Same(frame, fixture.Host.Root.Frame);
    }

    [Fact]
    public void ForeignThreadIsRejectedBeforeOwnerCallbackOrInput()
    {
        var fixture = new Fixture();
        bool ownerRead = false;
        Exception? error = null;
        var thread = new System.Threading.Thread(() =>
        {
            error = Record.Exception(() => UiSurfaceActionInput.Activate(fixture.Host, fixture.Action,
                () => ownerRead = true, fixture.Navigate, fixture.Submit));
        });
        thread.Start();
        thread.Join();

        Assert.IsType<InvalidOperationException>(error);
        Assert.False(ownerRead);
        Assert.Empty(fixture.Directions);
        Assert.Equal(0, fixture.Submits);
    }

    [Fact]
    public void OwnerLossAfterNavigationPreventsSubmission()
    {
        var fixture = new Fixture();
        bool owner = true;

        Assert.Throws<InvalidOperationException>(() => UiSurfaceActionInput.Activate(fixture.Host, fixture.Action,
            () => { if (!owner) throw new InvalidOperationException("Replaced native owner."); }, direction =>
            {
                var result = fixture.Navigate(direction);
                owner = false;
                return result;
            }, fixture.Submit));

        Assert.Single(fixture.Directions);
        Assert.Equal(0, fixture.Submits);
        Assert.Equal(0, fixture.Effects);
    }

    [Fact]
    public void RetirementAfterNavigationCannotActivateTheRetiredTarget()
    {
        var fixture = new Fixture();

        Assert.Throws<ObjectDisposedException>(() => UiSurfaceActionInput.Activate(fixture.Host, fixture.Action,
            () => { }, direction =>
            {
                var result = fixture.Navigate(direction);
                fixture.Host.Deactivate();
                return result;
            }, fixture.Submit));

        Assert.Single(fixture.Directions);
        Assert.Equal(0, fixture.Submits);
        Assert.Equal(0, fixture.Effects);
    }

    [Fact]
    public void ReplacingTheActionDuringNavigationRejectsTheNewDefinition()
    {
        var fixture = new Fixture();
        var replacement = new Fixture();

        Assert.Throws<InvalidOperationException>(() => UiSurfaceActionInput.Activate(fixture.Host, fixture.Action,
            () => { }, direction =>
            {
                var result = fixture.Navigate(direction);
                fixture.Host.UpdateRoot(replacement.Host.Root.Scene, Fixture.Placement);
                return result;
            }, fixture.Submit));

        Assert.Single(fixture.Directions);
        Assert.Equal(0, fixture.Submits);
        Assert.Equal(0, fixture.Effects);
        Assert.Equal(0, replacement.Effects);
    }

    [Fact]
    public void OrdinaryFocusRecompositionPreservesTheOriginalAction()
    {
        var fixture = new Fixture(recomposeFocus: true);
        var initialScene = fixture.Host.Root.Scene;

        Assert.True(fixture.Activate());

        Assert.NotSame(initialScene, fixture.Host.Root.Scene);
        Assert.Equal(1, fixture.Effects);
        Assert.Equal(1, fixture.Submits);
    }

    [Fact]
    public void NonProgressingInputTriesBothDirectionsThenStops()
    {
        var fixture = new Fixture();
        var directions = new List<UiNavigationDirection>();
        var frame = fixture.Host.Root.Frame;

        Assert.False(UiSurfaceActionInput.Activate(fixture.Host, fixture.Action, () => { }, direction =>
        {
            directions.Add(direction);
            return new(true, null, null);
        }, fixture.Submit));

        Assert.Equal(new[] { UiNavigationDirection.Next, UiNavigationDirection.Previous }, directions);
        Assert.Equal(0, fixture.Submits);
        Assert.Same(frame, fixture.Host.Root.Frame);
    }

    [Fact]
    public void NavigationBudgetStopsEvenWhenTheInputKeepsMoving()
    {
        var fixture = new Fixture();
        fixture.MoveToLast();
        UiSymbolId? last = fixture.Host.Root.Interactions.Snapshot.Focused;
        Assert.IsType<UiTextInputSceneNode>(Nodes(fixture.Host.Root.Scene.Root).Single(node => node.Id == last));
        Assert.True(fixture.Host.MoveFocus(UiNavigationDirection.Previous).Consumed);
        UiSymbolId? previous = fixture.Host.Root.Interactions.Snapshot.Focused;
        Assert.NotEqual(last, previous);
        Assert.IsType<UiTextInputSceneNode>(Nodes(fixture.Host.Root.Scene.Root).Single(node => node.Id == previous));
        Assert.True(fixture.Host.MoveFocus(UiNavigationDirection.Next).Consumed);
        Assert.Equal(last, fixture.Host.Root.Interactions.Snapshot.Focused);
        int inputs = 0;

        Assert.False(UiSurfaceActionInput.Activate(fixture.Host, fixture.Action, () => { }, _ =>
        {
            // Alternate two real controls so the requested action is never reached.
            return fixture.Host.MoveFocus(++inputs % 2 == 1 ? UiNavigationDirection.Previous : UiNavigationDirection.Next);
        }, fixture.Submit));

        Assert.Equal(256, inputs);
        Assert.Equal(0, fixture.Submits);
        Assert.Equal(0, fixture.Effects);
    }

    [Fact]
    public void PortalRedirectedNavigationIsRejectedBeforeSubmit()
    {
        var fixture = new Fixture();

        Assert.Throws<InvalidOperationException>(() => UiSurfaceActionInput.Activate(fixture.Host, fixture.Action,
            () => { }, direction => fixture.Navigate(direction) with { Portal = new UiSymbolId("test", "portal") },
            fixture.Submit));

        Assert.Single(fixture.Directions);
        Assert.Equal(0, fixture.Submits);
    }

    [Fact]
    public void OpenPortalRejectsActionBeforeAnyRootOrPortalInput()
    {
        var fixture = new Fixture();
        var popup = new Fixture(count: 1, policy: UiHostPolicies.Popup);
        using var portal = fixture.Host.Present(new(new("test", "popup"),
            new(fixture.Host.Root.Scene.Root.Id), popup.Host.Root.Scene, Fixture.Placement));
        var frame = fixture.Host.Root.Frame;

        Assert.Throws<InvalidOperationException>(() => fixture.Activate());

        Assert.Empty(fixture.Directions);
        Assert.Equal(0, fixture.Submits);
        Assert.Equal(0, fixture.Effects);
        Assert.Same(frame, fixture.Host.Root.Frame);
        Assert.Equal(1, fixture.Host.ActivePortalCount);
    }

    [Fact]
    public void OversizedSceneRejectsActionBeforeInput()
    {
        var fixture = new Fixture(count: 4100);

        var error = Assert.Throws<InvalidOperationException>(() => fixture.Activate());

        Assert.Contains("bounded node limit", error.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Directions);
        Assert.Equal(0, fixture.Submits);
    }

    [Fact]
    public void DuplicateActionTargetsRejectAmbiguousInput()
    {
        var fixture = new Fixture(count: 1);
        UiScene original = fixture.Host.Root.Scene;
        UiButtonSceneNode target = fixture.Target;
        var scene = new UiScene(original.Experience, original.DisplayName,
            new(original.Root.Id, original.Root.Role, original.Root.Visual, original.Root.Policy,
                new UiSceneNode[] { target, new UiButtonSceneNode(new("test", "duplicate"), target.Role,
                    target.Visual, target.Action) }), original.MeasurementContext);
        var host = new UiPortalHostSession(scene, Fixture.Placement, new Platform());

        var error = Assert.Throws<InvalidOperationException>(() => UiSurfaceActionInput.Activate(host,
            fixture.Action, () => { }, fixture.Navigate, fixture.Submit));

        Assert.Contains("multiple root input targets", error.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Directions);
        Assert.Equal(0, fixture.Submits);
        Assert.Equal(0, fixture.Effects);
    }

    [Fact]
    public void SuccessfulActionMayRetireItsOwnHost()
    {
        var fixture = new Fixture();
        fixture.OnAction = fixture.Host.Deactivate;

        Assert.True(fixture.Activate());

        Assert.Equal(1, fixture.Effects);
        Assert.Equal(1, fixture.Submits);
        Assert.False(fixture.Host.Root.IsActive);
    }

    private sealed class Fixture
    {
        internal static readonly UiHostPlacementContext Placement = new(new UiRect(0, 0, 1280, 720));
        internal readonly UiSymbolId Action = new("test", "activate/send");
        internal readonly UiPortalHostSession Host;
        internal readonly List<UiNavigationDirection> Directions = new();
        internal int Effects;
        internal int Submits;
        internal Action? OnAction;

        internal Fixture(bool recomposeFocus = false, int count = 40, UiHostPolicy? policy = null)
        {
            var id = new UiSymbolId("test", "activate");
            var builder = new UiExperienceBuilder(id, "Window action input");
            for (int i = 0; i < count; i++) builder.Search("Field/" + i, new UiState<string>("Value"));
            var experience = builder.Actions("Actions",
                new UiActionDefinition(Action, "Send", () => { Effects++; OnAction?.Invoke(); }),
                new UiActionDefinition(id.Child("other"), "Other", () => throw new InvalidOperationException("Wrong action.")))
                .Build();
            var registry = policy is null ? UiSemanticHostProjection.Register(experience, UiSemanticHostKind.Window)
                : new UiRegistryBuilder().Window(id, "Popup", () => experience, policy).Freeze();
            var composer = new UiSceneComposer(UiThemePresets.Dark(), registry);
            var invocation = new UiInvocationService(registry).Invoke(id, UiPresentationProfiles.Wide);
            Host = new(composer.Compose(invocation), Placement, new Platform(),
                composeInteraction: recomposeFocus ? interaction => composer.Compose(invocation, interaction: interaction) : null);
        }

        internal UiButtonSceneNode Target => Nodes(Host.Root.Scene.Root).OfType<UiButtonSceneNode>()
            .Single(node => node.Action.Id == Action);
        internal bool Activate() => UiSurfaceActionInput.Activate(Host, Action, () => { }, Navigate, Submit);
        internal UiPortalDispatch Navigate(UiNavigationDirection direction)
        {
            Directions.Add(direction);
            return Host.MoveFocus(direction);
        }
        internal UiPortalDispatch Submit() { Submits++; return Host.Submit(); }
        internal void MoveToLast()
        {
            for (int i = 0; i < 256; i++)
                if (!Host.MoveFocus(UiNavigationDirection.Next).Consumed) return;
            throw new InvalidOperationException("Contained Window navigation did not terminate.");
        }
    }

    private static IEnumerable<UiSceneNode> Nodes(UiSceneNode node)
    {
        yield return node;
        foreach (UiSceneNode child in node.Children)
        foreach (UiSceneNode descendant in Nodes(child)) yield return descendant;
    }

    private sealed class Platform : IUiPlatformBridge
    {
        public UiSize Measure(string text, UiTypography typography, float width, UiTextOverflow overflow)
            => new(Math.Min(text.Length * 8, width), typography.Size * typography.LineHeight);
        public void DrawSurface(UiSurfacePrimitive surface) { }
        public void DrawText(UiTextPrimitive text) { }
    }
}
