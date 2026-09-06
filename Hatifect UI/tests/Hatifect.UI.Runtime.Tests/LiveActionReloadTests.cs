using System.Threading.Tasks.Sources;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.HotReload;
using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Runtime.Registration;
using Hatifect.UI.Runtime.Rendering;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Terminal;
using Hatifect.UI.Runtime.Visual;
using Hatifect.UI.Runtime.Visual.Theming;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class LiveActionReloadTests
{
    [Fact]
    public void AcceptedTerminalReloadPublishesAssetsAndInvocationBeforeCancellingOldWork()
    {
        using var fixture = new Fixture();
        UiTerminalHostSession terminal = fixture.Terminal;
        UiInvocationResult invocation = terminal.CurrentInvocation;
        UiTerminalSectionAssets oldAssets = fixture.Assets.For(ExperienceId);
        var oldActions = terminal.Host.Root.Actions;
        Assert.True(oldActions.Invoke(fixture.Action));
        using var portal = fixture.PresentPortal();
        Assert.True(terminal.Host.Submit().Interaction?.ActionInvoked);
        UiTerminalSectionAssets? observedAssets = null;
        UiInvocationResult? observedInvocation = null;
        int observedPortals = -1;
        fixture.OnCancel = () =>
        {
            observedAssets = fixture.Assets.For(ExperienceId);
            observedInvocation = terminal.CurrentInvocation;
            observedPortals = terminal.Host.Accessibility.Portals.Count;
        };

        UiSemanticReloadResult result = fixture.Reload();

        Assert.True(result.Accepted);
        Assert.True(result.Changed);
        Assert.Equal(1, result.Version);
        Assert.NotSame(oldAssets, observedAssets);
        Assert.Same(fixture.Assets.For(ExperienceId), observedAssets);
        Assert.Same(terminal.CurrentInvocation, observedInvocation);
        Assert.NotSame(invocation, observedInvocation);
        Assert.Same(invocation.Experience, observedInvocation!.Experience);
        Assert.Equal(0, observedPortals);
        Assert.All(fixture.Operations, operation => Assert.True(operation.Token.IsCancellationRequested));
        Assert.False(oldActions.Invoke(fixture.Action));
        fixture.Complete(0, 91);
        fixture.Complete(1, 92);
        terminal.Host.PumpActions();
        Assert.Empty(fixture.Results);
        Assert.True(terminal.Host.Root.Actions.Invoke(fixture.Action));
        fixture.Complete(2, 42);
        terminal.Host.PumpActions();
        Assert.Equal(new[] { 42 }, fixture.Results);
        Assert.Equal("Draft", fixture.Text.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedLatePreparationPreservesAssetsInvocationPendingActionsAndPortals(bool ownerRefuses)
    {
        using var fixture = new Fixture();
        var terminal = fixture.Terminal;
        var assets = fixture.Assets.For(ExperienceId);
        var invocation = terminal.CurrentInvocation;
        var scene = terminal.Host.Root.Scene;
        var actions = terminal.Host.Root.Actions;
        Assert.True(actions.Invoke(fixture.Action));
        using var portal = fixture.PresentPortal();
        Assert.True(terminal.Host.Submit().Interaction?.ActionInvoked);
        // Pointer/focus presentation may recompose the accepted scene before reload begins.
        scene = terminal.Host.Root.Scene;
        actions = terminal.Host.Root.Actions;
        fixture.Platform.Fail = !ownerRefuses;
        if (!ownerRefuses) fixture.CandidatePlacement = new UiHostPlacementContext(new UiRect(0, 0, 700, 500));
        fixture.ValidateOwner = ownerRefuses ? () =>
        {
            if (++fixture.OwnerChecks == 2) throw new InvalidOperationException("Native owner changed after preparation.");
        } : null;

        UiSemanticReloadResult result = fixture.Reload();

        Assert.False(result.Accepted);
        Assert.False(result.Changed);
        Assert.Equal(0, result.Version);
        Assert.Contains(result.Diagnostics, item => item.Code == "LUI4102");
        Assert.Same(assets, fixture.Assets.For(ExperienceId));
        Assert.Same(invocation, terminal.CurrentInvocation);
        Assert.Same(scene, terminal.Host.Root.Scene);
        Assert.Same(actions, terminal.Host.Root.Actions);
        Assert.Single(terminal.Host.Accessibility.Portals);
        Assert.All(fixture.Operations, operation => Assert.False(operation.Token.IsCancellationRequested));
        fixture.Platform.Fail = false;
        fixture.ValidateOwner = null;
        fixture.Complete(0, 21);
        fixture.Complete(1, 22);
        terminal.Host.PumpActions();
        Assert.Equal(new[] { 21, 22 }, fixture.Results);
        Assert.True(fixture.Reload().Accepted);
        Assert.Empty(terminal.Host.Accessibility.Portals);
    }

    [Fact]
    public void UnchangedDocumentsDoNotRetireTheAcceptedGeneration()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Reload().Accepted);
        Assert.True(fixture.Terminal.Host.Root.Actions.Invoke(fixture.Action));
        UiInvocationResult invocation = fixture.Terminal.CurrentInvocation;
        var result = fixture.Reload();
        Assert.True(result.Accepted);
        Assert.False(result.Changed);
        Assert.Equal(1, result.Version);
        Assert.Same(invocation, fixture.Terminal.CurrentInvocation);
        Assert.False(fixture.Operations[0].Token.IsCancellationRequested);
        fixture.Complete(0, 42);
        fixture.Terminal.Host.PumpActions();
        Assert.Equal(new[] { 42 }, fixture.Results);
    }

    [Fact]
    public void ReloadCancellationMayDisposeTerminalAfterAssetsAreAccepted()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Terminal.Host.Root.Actions.Invoke(fixture.Action));
        fixture.OnCancel = fixture.Terminal.Dispose;
        UiSemanticReloadResult result = fixture.Reload();
        Assert.True(result.Accepted);
        Assert.True(result.Changed);
        Assert.NotNull(fixture.Assets.For(ExperienceId).Visual);
        Assert.False(fixture.Terminal.Host.Root.IsActive);
        Assert.Null(fixture.Terminal.ActiveSection);
        fixture.Complete(0, 99);
        Assert.False(fixture.Terminal.Host.PumpActions());
        Assert.Empty(fixture.Results);
    }

    [Fact]
    public void ReentrantExplicitOpenDuringReloadPreparationKeepsTheNewFrameAndRejectsCandidate()
    {
        using var fixture = new Fixture();
        UiInvocationResult? nested = null;
        fixture.ValidateOwner = () =>
        {
            fixture.Terminal.OpenSection(ExperienceId);
            nested = fixture.Terminal.CurrentInvocation;
        };
        UiSemanticReloadResult result = fixture.Reload();
        Assert.False(result.Accepted);
        Assert.Same(nested, fixture.Terminal.CurrentInvocation);
        Assert.Null(fixture.Assets.For(ExperienceId).Visual);
        fixture.ValidateOwner = null;
        Assert.True(fixture.Reload().Accepted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedOrUnacceptedCandidateCannotPublishThroughAnEscapedAcceptance(bool throws)
    {
        var model = Model();
        Action? escaped = null;
        var assets = new UiSemanticLiveAssets(new[] { model }, (_, _, accept) =>
        {
            escaped = accept;
            if (throws) throw new InvalidOperationException("Rejected.");
        });
        UiTerminalSectionAssets previous = assets.For(ExperienceId);
        var result = assets.Reload(ExperienceId, null, Visual);
        Assert.False(result.Accepted);
        Assert.Equal(0, result.Version);
        Assert.Same(previous, assets.For(ExperienceId));
        Assert.NotNull(escaped);
        Assert.Throws<InvalidOperationException>(escaped!);
        Assert.Same(previous, assets.For(ExperienceId));
    }

    [Fact]
    public void FailureAfterAcceptanceIsObservedWithoutRollingBackCommittedAssets()
    {
        var model = Model();
        UiTerminalSectionAssets? committed = null;
        int applications = 0;
        var assets = new UiSemanticLiveAssets(new[] { model }, (_, candidate, accept) =>
        {
            applications++;
            accept();
            committed = candidate;
            throw new InvalidOperationException("Post-acceptance cleanup failed.");
        });
        Assert.Throws<InvalidOperationException>(() => assets.Reload(ExperienceId, null, Visual));
        Assert.Same(committed, assets.For(ExperienceId));
        var unchanged = assets.Reload(ExperienceId, null, Visual);
        Assert.True(unchanged.Accepted);
        Assert.False(unchanged.Changed);
        Assert.Equal(1, unchanged.Version);
        Assert.Equal(1, applications);
    }

    private const string Visual = "visual Reload\n\nName\n    surface = Surface.Hover\n";
    private static UiSymbolId ExperienceId => new("Hatifect.Tests", "action-reload");
    private static UiExperienceDefinition Model() => new UiExperienceBuilder(ExperienceId, "Reload")
        .Monitor("Name", new UiConstantSource<string>("Ready")).VisualRole("Name").Build();
    private static UiHostPlacementContext Placement => new(new UiRect(0, 0, 960, 600));
    private sealed class Fixture : IDisposable
    {
        internal UiState<string> Text { get; } = new("Draft");
        internal TestPlatform Platform { get; } = new();
        internal UiHostPlacementContext CandidatePlacement { get; set; } = Placement;
        internal UiTerminalHostSession Terminal { get; }
        internal UiSemanticLiveAssets Assets { get; }
        internal UiActionDefinition Action { get; }
        internal List<(Completion Source, CancellationToken Token)> Operations { get; } = new();
        internal List<int> Results { get; } = new();
        internal Action? OnCancel { get; set; }
        internal Action? ValidateOwner { get; set; }
        internal int OwnerChecks { get; set; }
        internal Fixture()
        {
            Action = new UiAction<int, int>(ExperienceId.Child("action"), "Run", (_, token) =>
            {
                var source = new Completion();
                Operations.Add((source, token));
                token.Register(() => OnCancel?.Invoke());
                return new(source, 0);
            }, UiActionConcurrency.RejectWhileRunning).Bind(() => 7, (_, result) => Results.Add(result.Value));
            var model = new UiExperienceBuilder(ExperienceId, "Reload").Search("Name", Text)
                .Actions("Actions", Action).VisualRole("Name").Build();
            var registry = new UiRegistryBuilder().TerminalSection(ExperienceId, "Reload", () => model).Freeze();
            UiTerminalHostSession? terminal = null;
            Assets = new UiSemanticLiveAssets(new[] { model }, (_, candidate, accept) =>
                terminal!.Reload(candidate, UiPresentationProfiles.Wide, CandidatePlacement, "en", accept,
                    () => ValidateOwner?.Invoke()));
            Terminal = terminal = new(registry, UiThemePresets.Dark(), Placement, Platform,
                UiPresentationProfiles.Wide, ExperienceId, descriptor => Assets.For(descriptor.Id));
        }
        internal UiSemanticReloadResult Reload() => Assets.Reload(ExperienceId, null, Visual);
        internal void Complete(int index, int value) => Operations[index].Source.SetResult(UiActionResult<int>.Success(value));
        internal UiPortalHandle PresentPortal()
        {
            UiSymbolId id = ExperienceId.Child("popup");
            var model = new UiExperienceBuilder(id, "Popup").Actions("Actions", Action).Build();
            var policy = new UiHostPolicy(UiHostKind.Popup, UiWindowChrome.Tool, UiDismissPolicy.Escape,
                UiModalPolicy.Modeless, UiFocusScopePolicy.Contained, UiPopupPlacement.Anchor);
            var registry = new UiRegistryBuilder().Window(id, "Popup", () => model, policy).Freeze();
            UiScene scene = new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(
                new UiInvocationService(registry).Invoke(id, UiPresentationProfiles.Wide));
            return Terminal.Host.Present(new(id, new UiPortalOwner(Terminal.Host.Root.Scene.Root.Id), scene,
                new UiHostPlacementContext(Placement.Viewport, anchor: new UiRect(400, 300, 20, 20))));
        }
        public void Dispose() => Terminal.Dispose();
    }
    private sealed class Completion : IValueTaskSource<UiActionResult<int>>
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
        internal bool Fail { get; set; }
        public UiSize Measure(string text, UiTypography typography, float width, UiTextOverflow overflow)
        {
            if (Fail) throw new InvalidOperationException("Reload measurement rejected.");
            return new(Math.Min(width, text.Length * typography.Size / 2), typography.Size * typography.LineHeight);
        }
        public void DrawSurface(UiSurfacePrimitive surface) { }
        public void DrawText(UiTextPrimitive text) { }
    }
}
