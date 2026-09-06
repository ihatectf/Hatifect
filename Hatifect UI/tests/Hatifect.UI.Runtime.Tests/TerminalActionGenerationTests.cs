using System.Threading.Tasks.Sources;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Runtime.Registration;
using Hatifect.UI.Runtime.Rendering;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Terminal;
using Hatifect.UI.Runtime.Visual;
using Hatifect.UI.Runtime.Visual.Resolution;
using Hatifect.UI.Runtime.Visual.Theming;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class TerminalActionGenerationTests
{
    [Theory]
    [InlineData("same")]
    [InlineData("other")]
    [InlineData("route")]
    public void AcceptedOpenRetiresPreviousRootAndPortalsBeforeCancellation(string transition)
    {
        using var fixture = new Fixture();
        UiTerminalHostSession session = fixture.Session;
        var previousActions = session.Host.Root.Actions;
        UiInvocationResult previousInvocation = session.CurrentInvocation;
        Assert.True(previousActions.Invoke(fixture.FirstAction.Definition));
        using var portal = fixture.PresentPortal();
        Assert.True(session.Host.Submit().Interaction?.ActionInvoked);
        UiSymbolId target = transition == "same" ? First : Second;
        UiSymbolId? observedSection = null;
        UiInvocationResult? observedInvocation = null;
        int observedPortals = -1;
        bool oldActionStillInvocable = true;
        Exception? inputRefusal = null;
        fixture.FirstAction.OnCancel = () =>
        {
            observedSection = session.ActiveSection;
            observedInvocation = session.CurrentInvocation;
            observedPortals = session.Host.Accessibility.Portals.Count;
            oldActionStillInvocable = previousActions.CanInvoke(fixture.FirstAction.Definition);
            inputRefusal = Record.Exception(() => session.Submit());
        };
        if (transition == "route") fixture.Follow(Second);
        else session.OpenSection(target);

        Assert.True(fixture.FirstAction.Operations[0].Token.IsCancellationRequested);
        Assert.True(fixture.PortalAction.Operations[0].Token.IsCancellationRequested);
        Assert.Equal(target, observedSection);
        Assert.Same(session.CurrentInvocation, observedInvocation);
        Assert.NotSame(previousInvocation, observedInvocation);
        Assert.Equal(0, observedPortals);
        Assert.Empty(session.Host.Accessibility.Portals);
        Assert.False(oldActionStillInvocable);
        Assert.IsType<InvalidOperationException>(inputRefusal);
        Assert.False(previousActions.Invoke(fixture.FirstAction.Definition));
        fixture.FirstAction.Complete(0, 11);
        fixture.PortalAction.Complete(0, 12);
        session.Host.PumpActions();
        Assert.Empty(fixture.FirstAction.Results);
        Assert.Empty(fixture.PortalAction.Results);
        Probe current = target == First ? fixture.FirstAction : fixture.SecondAction;
        Assert.True(session.Host.Root.Actions.Invoke(current.Definition));
        current.Complete(current.Operations.Count - 1, 42);
        session.Host.PumpActions();
        Assert.Equal(new[] { 42 }, current.Results);
        Assert.Same(target == First ? fixture.FirstModel : fixture.SecondModel, session.CurrentInvocation.Experience);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedExplicitOpenRetainsPendingGenerationAndCanRetry(bool follow)
    {
        using var fixture = new Fixture();
        var session = fixture.Session;
        var actions = session.Host.Root.Actions;
        var scene = session.Host.Root.Scene;
        var invocation = session.CurrentInvocation;
        Assert.True(actions.Invoke(fixture.FirstAction.Definition));
        using var portal = fixture.PresentPortal();
        Assert.True(session.Host.Submit().Interaction?.ActionInvoked);
        fixture.Platform.FailSecond = true;

        Assert.Throws<InvalidOperationException>(() =>
        {
            if (follow) fixture.Follow(Second);
            else session.OpenSection(Second);
        });

        if (!follow)
        {
            Assert.Same(actions, session.Host.Root.Actions);
            Assert.Same(scene, session.Host.Root.Scene);
        }
        Assert.Same(invocation, session.CurrentInvocation);
        Assert.Equal(First, session.ActiveSection);
        Assert.Single(session.Host.Accessibility.Portals);
        Assert.False(fixture.FirstAction.Operations[0].Token.IsCancellationRequested);
        Assert.False(fixture.PortalAction.Operations[0].Token.IsCancellationRequested);
        fixture.Platform.FailSecond = false;
        fixture.FirstAction.Complete(0, 21);
        fixture.PortalAction.Complete(0, 22);
        session.Host.PumpActions();
        Assert.Equal(new[] { 21 }, fixture.FirstAction.Results);
        Assert.Equal(new[] { 22 }, fixture.PortalAction.Results);
        session.OpenSection(Second);
        Assert.Empty(session.Host.Accessibility.Portals);
        Assert.True(session.Host.Root.Actions.Invoke(fixture.SecondAction.Definition));
        fixture.SecondAction.Complete(0, 42);
        session.Host.PumpActions();
        Assert.Equal(new[] { 42 }, fixture.SecondAction.Results);
    }

    [Fact]
    public void OrdinaryRecompositionPreservesPendingActionsAndPortals()
    {
        using var fixture = new Fixture();
        var session = fixture.Session;
        var model = session.CurrentInvocation.Experience;
        Assert.True(session.Host.Root.Actions.Invoke(fixture.FirstAction.Definition));
        using var portal = fixture.PresentPortal();
        Assert.True(session.Host.Submit().Interaction?.ActionInvoked);
        session.SetTheme(UiSemanticThemes.Resolve(UiSemanticTheme.Light));
        session.Recompose(UiPresentationProfiles.Compact,
            new UiHostPlacementContext(new UiRect(0, 0, 700, 500)), "ru");
        session.Host.MovePointer(new UiPoint(10, 10));
        Assert.Same(model, session.CurrentInvocation.Experience);
        Assert.Single(session.Host.Accessibility.Portals);
        Assert.False(fixture.FirstAction.Operations[0].Token.IsCancellationRequested);
        Assert.False(fixture.PortalAction.Operations[0].Token.IsCancellationRequested);
        fixture.FirstAction.Complete(0, 31);
        fixture.PortalAction.Complete(0, 32);
        session.Host.PumpActions();
        Assert.Equal(new[] { 31 }, fixture.FirstAction.Results);
        Assert.Equal(new[] { 32 }, fixture.PortalAction.Results);
    }

    [Fact]
    public void CancellationMayDisposeTerminalWithoutPublishingOwnerStateAfterDisposal()
    {
        using var fixture = new Fixture();
        var session = fixture.Session;
        Assert.True(session.Host.Root.Actions.Invoke(fixture.FirstAction.Definition));
        UiSymbolId? observedSection = null;
        fixture.FirstAction.OnCancel = () =>
        {
            observedSection = session.ActiveSection;
            session.Dispose();
        };

        session.OpenSection(Second);

        Assert.Equal(Second, observedSection);
        Assert.Null(session.ActiveSection);
        Assert.False(session.Host.Root.IsActive);
        Assert.Throws<ObjectDisposedException>(() => session.CurrentInvocation);
        Assert.False(session.Host.Root.Actions.Invoke(fixture.SecondAction.Definition));
        fixture.FirstAction.Complete(0, 99);
        Assert.False(session.Host.PumpActions());
        Assert.Empty(fixture.FirstAction.Results);
    }

    [Fact]
    public void OpenFromCompletionSuppressesOtherReadyResultsOfThePreviousGeneration()
    {
        var secondReady = new Probe("sibling");
        using var fixture = new Fixture(secondReady);
        var session = fixture.Session;
        Assert.True(session.Host.Root.Actions.Invoke(fixture.FirstAction.Definition));
        Assert.True(session.Host.Root.Actions.Invoke(secondReady.Definition));
        fixture.FirstAction.OnCompleted = () => session.OpenSection(First);
        fixture.FirstAction.Complete(0, 41);
        secondReady.Complete(0, 42);

        session.Host.PumpActions();

        Assert.Equal(new[] { 41 }, fixture.FirstAction.Results);
        Assert.Empty(secondReady.Results);
        Assert.True(session.Host.Root.Actions.Invoke(secondReady.Definition));
        secondReady.Complete(1, 43);
        session.Host.PumpActions();
        Assert.Equal(new[] { 43 }, secondReady.Results);
    }

    [Fact]
    public void SameCachedOpenCancelsThePreviousQueueWithoutStartingItsWaitingRequests()
    {
        using var fixture = new Fixture(concurrency: UiActionConcurrency.Queue(2));
        var session = fixture.Session;
        var actions = session.Host.Root.Actions;
        Assert.True(actions.Invoke(fixture.FirstAction.Definition));
        Assert.True(actions.Invoke(fixture.FirstAction.Definition));
        Assert.True(actions.Invoke(fixture.FirstAction.Definition));
        Assert.Single(fixture.FirstAction.Operations);

        session.OpenSection(First);
        fixture.FirstAction.Complete(0, 91);
        session.Host.PumpActions();
        session.Host.PumpActions();

        Assert.True(fixture.FirstAction.Operations[0].Token.IsCancellationRequested);
        Assert.Single(fixture.FirstAction.Operations);
        Assert.Empty(fixture.FirstAction.Results);
        Assert.True(session.Host.Root.Actions.Invoke(fixture.FirstAction.Definition));
        fixture.FirstAction.Complete(1, 42);
        session.Host.PumpActions();
        Assert.Equal(new[] { 42 }, fixture.FirstAction.Results);
    }

    private static UiSymbolId Id(string path) => new("Hatifect.Tests", "terminal-generation/" + path);
    private static UiSymbolId First => Id("first");
    private static UiSymbolId Second => Id("second");
    private static UiHostPlacementContext Placement => new(new UiRect(0, 0, 960, 600));
    private static IEnumerable<UiSceneNode> Nodes(UiSceneNode root)
    {
        yield return root;
        foreach (var child in root.Children)
        foreach (var node in Nodes(child)) yield return node;
    }
    private sealed class Fixture : IDisposable
    {
        internal Probe FirstAction { get; }
        internal Probe SecondAction { get; } = new("second-action");
        internal Probe PortalAction { get; } = new("portal-action");
        internal UiExperienceDefinition FirstModel { get; }
        internal UiExperienceDefinition SecondModel { get; }
        internal TestPlatform Platform { get; } = new();
        internal UiTerminalHostSession Session { get; }
        internal Fixture(Probe? sibling = null, UiActionConcurrency? concurrency = null)
        {
            FirstAction = new("first-action", concurrency);
            FirstModel = Model(First, "First content", sibling == null
                ? new[] { FirstAction.Definition } : new[] { FirstAction.Definition, sibling.Definition });
            SecondModel = Model(Second, "Second content", SecondAction.Definition);
            var registry = new UiRegistryBuilder()
                .TerminalSection(First, "First", () => FirstModel, order: 1)
                .TerminalSection(Second, "Second", () => SecondModel, order: 2).Freeze();
            Session = new(registry, UiThemePresets.Dark(), Placement, Platform, UiPresentationProfiles.Wide, First);
        }
        internal UiPortalHandle PresentPortal()
        {
            var id = Id("popup");
            var model = Model(id, "Popup content", PortalAction.Definition);
            var policy = new UiHostPolicy(UiHostKind.Popup, UiWindowChrome.Tool, UiDismissPolicy.Escape,
                UiModalPolicy.Modeless, UiFocusScopePolicy.Contained, UiPopupPlacement.Anchor);
            var registry = new UiRegistryBuilder().Window(id, "Popup", () => model, policy).Freeze();
            var scene = new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(
                new UiInvocationService(registry).Invoke(id, UiPresentationProfiles.Wide));
            return Session.Host.Present(new(id, new UiPortalOwner(Session.Host.Root.Scene.Root.Id), scene,
                new UiHostPlacementContext(Placement.Viewport, anchor: new UiRect(300, 250, 30, 30))));
        }
        internal void Follow(UiSymbolId target)
        {
            // A non-modal popup must not consume the root navigation route's point.
            var node = Nodes(Session.Host.Root.Scene.Root).OfType<UiRouteButtonSceneNode>().Single(n => n.Route == target);
            Assert.True(Session.Host.Root.Layout.TryGetEntry(node.Id, out var entry));
            var bounds = entry!.Bounds;
            var point = new UiPoint(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            Assert.True(Session.PressPointer(point).Consumed);
            Assert.Equal(target, Session.ReleasePointer(point).Interaction?.Route);
        }
        private static UiExperienceDefinition Model(UiSymbolId id, string content, params UiActionDefinition[] actions)
            => new UiExperienceBuilder(id, content).Monitor("Content", new UiConstantSource<string>(content))
                .Actions("Actions", actions).Build();
        public void Dispose() => Session.Dispose();
    }
    private sealed class Probe
    {
        internal UiActionDefinition Definition { get; }
        internal List<(ControlledCompletion Source, CancellationToken Token)> Operations { get; } = new();
        internal List<int> Results { get; } = new();
        internal Action? OnCancel { get; set; }
        internal Action? OnCompleted { get; set; }
        internal Probe(string id, UiActionConcurrency? concurrency = null)
        {
            Definition = new UiAction<int, int>(Id(id), "Run " + id, (_, token) =>
            {
                var source = new ControlledCompletion();
                Operations.Add((source, token));
                token.Register(() => OnCancel?.Invoke());
                return new(source, 0);
            }, concurrency ?? UiActionConcurrency.RejectWhileRunning).Bind(() => 7, (_, result) =>
            {
                Results.Add(result.Value);
                OnCompleted?.Invoke();
            });
        }
        internal void Complete(int index, int value) => Operations[index].Source.SetResult(UiActionResult<int>.Success(value));
    }
    // The configured ValueTask continuation runs inline. SetResult returns only after the
    // framework mailbox is ready; these ownership tests do not race a thread-pool callback.
    private sealed class ControlledCompletion : IValueTaskSource<UiActionResult<int>>
    {
        private ManualResetValueTaskSourceCore<UiActionResult<int>> _source;
        internal void SetResult(UiActionResult<int> result) => _source.SetResult(result);
        public UiActionResult<int> GetResult(short token) => _source.GetResult(token);
        public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _source.OnCompleted(continuation, state, token, flags);
    }
    private sealed class TestPlatform : IUiPlatformBridge
    {
        internal bool FailSecond { get; set; }
        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
        {
            if (FailSecond && text == "Second content") throw new InvalidOperationException("Second content rejected.");
            return new(Math.Min(availableWidth, text.Length * typography.Size * .5f), typography.Size * typography.LineHeight);
        }
        public void DrawSurface(UiSurfacePrimitive surface) { }
        public void DrawText(UiTextPrimitive text) { }
    }
}
