using System.Threading.Tasks.Sources;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Hosting;
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
using Hatifect.UI.Semantics;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class TerminalEnvironmentTests
{
    [Theory]
    [InlineData(1300, UiInputMode.MouseKeyboard)]
    [InlineData(700, UiInputMode.MouseKeyboard)]
    [InlineData(1200, UiInputMode.Controller)]
    public void EnvironmentRecomposeRetainsModelFocusAndPendingRootAndPortal(float width, UiInputMode input)
    {
        using var fixture = new Fixture();
        var session = fixture.Session;
        UiExperienceDefinition model = session.CurrentInvocation.Experience;
        var focused = session.Host.Root.Interactions.Snapshot.Focused;
        Assert.True(session.Host.Root.Actions.Invoke(fixture.RootAction.Definition));
        using var portal = fixture.PresentPortal();
        Assert.True(session.Host.Submit().Interaction?.ActionInvoked);
        UiEnvironment changed = Capture(width, 1.5f, input, "ru-RU");

        session.Recompose(UiPresentationProfiles.Resolve(changed), Placement(changed), environment: changed);

        Assert.Same(changed, session.CurrentInvocation.Plan.Host.Environment);
        Assert.Equal(UiPresentationProfiles.Resolve(changed).Id, session.CurrentInvocation.Plan.Host.Profile);
        Assert.Equal("ru-RU", session.Host.Root.Scene.MeasurementContext.Locale);
        Assert.Contains(session.CurrentInvocation.Plan.Decisions, decision => decision.Code == UiPlanDecisionCode.EnvironmentFacet
            && decision.Message.Contains("ru-RU", StringComparison.Ordinal));
        Assert.Same(model, session.CurrentInvocation.Experience);
        Assert.Equal(1, fixture.Created);
        Assert.Equal(focused, session.Host.Root.Interactions.Snapshot.Focused);
        Assert.Single(session.Host.Accessibility.Portals);
        Assert.False(fixture.RootAction.Operations[0].Token.IsCancellationRequested);
        Assert.False(fixture.PortalAction.Operations[0].Token.IsCancellationRequested);
        fixture.RootAction.Operations[0].Source.Complete(31);
        fixture.PortalAction.Operations[0].Source.Complete(32);
        session.Host.PumpActions();
        Assert.Equal(new[] { 31 }, fixture.RootAction.Results);
        Assert.Equal(new[] { 32 }, fixture.PortalAction.Results);
        // Async and input recomposition must continue to use the accepted snapshot.
        session.Host.MovePointer(new UiPoint(10, 10));
        Assert.Same(changed, session.CurrentInvocation.Plan.Host.Environment);
        Assert.Equal("ru-RU", session.Host.Root.Scene.MeasurementContext.Locale);
        Assert.Equal(1, fixture.Created);
    }

    [Fact]
    public void FailedMeasurementRetainsAcceptedEnvironmentAndPendingWorkThenRetries()
    {
        using var fixture = new Fixture();
        var session = fixture.Session;
        var invocation = session.CurrentInvocation;
        var scene = session.Host.Root.Scene;
        Assert.True(session.Host.Root.Actions.Invoke(fixture.RootAction.Definition));
        using var portal = fixture.PresentPortal();
        Assert.True(session.Host.Submit().Interaction?.ActionInvoked);
        UiEnvironment candidate = Capture(700, 1.25f, UiInputMode.MouseKeyboard, "ru-RU");
        fixture.Platform.Fail = true;

        Assert.Throws<InvalidOperationException>(() => session.Recompose(UiPresentationProfiles.Compact,
            Placement(candidate), environment: candidate));

        Assert.Same(invocation, session.CurrentInvocation);
        Assert.Same(scene, session.Host.Root.Scene);
        Assert.Same(fixture.InitialEnvironment, session.CurrentInvocation.Plan.Host.Environment);
        Assert.Equal("en", scene.MeasurementContext.Locale);
        Assert.Single(session.Host.Accessibility.Portals);
        Assert.False(fixture.RootAction.Operations[0].Token.IsCancellationRequested);
        Assert.False(fixture.PortalAction.Operations[0].Token.IsCancellationRequested);
        fixture.Platform.Fail = false;
        session.Recompose(UiPresentationProfiles.Compact, Placement(candidate), environment: candidate);
        fixture.RootAction.Operations[0].Source.Complete(41);
        fixture.PortalAction.Operations[0].Source.Complete(42);
        session.Host.PumpActions();
        Assert.Same(candidate, session.CurrentInvocation.Plan.Host.Environment);
        Assert.Equal("ru-RU", session.Host.Root.Scene.MeasurementContext.Locale);
        Assert.Equal(new[] { 41 }, fixture.RootAction.Results);
        Assert.Equal(new[] { 42 }, fixture.PortalAction.Results);
        Assert.Equal(1, fixture.Created);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("locale")]
    [InlineData("viewport")]
    [InlineData("theme")]
    public void ContradictoryEnvironmentRejectsBeforeAvailabilityOrFactory(string mismatch)
    {
        int created = 0, availability = 0;
        var registry = new UiRegistryBuilder().TerminalSection(Id("first"), "First", () =>
        {
            created++;
            return new UiExperienceBuilder(Id("first"), "First").Monitor("Status", new UiConstantSource<string>("Ready")).Build();
        }, isAvailable: () => { availability++; return true; }).Freeze();
        UiEnvironment environment = Capture(1200, 1, UiInputMode.MouseKeyboard, "en");
        var profile = mismatch == "profile" ? UiPresentationProfiles.Compact : UiPresentationProfiles.Wide;
        string? locale = mismatch == "locale" ? "ru-RU" : null;
        var placement = mismatch == "viewport" ? new UiHostPlacementContext(new UiRect(0, 0, 900, 600)) : Placement(environment);
        UiTheme theme = mismatch == "theme" ? UiSemanticThemes.Resolve(UiSemanticTheme.Light) : UiThemePresets.Dark();

        Assert.Throws<ArgumentException>(() => new UiTerminalHostSession(registry, theme, placement,
            new TestPlatform(), profile, locale: locale, environment: environment));

        Assert.Equal(0, availability);
        Assert.Equal(0, created);
    }

    [Fact]
    public void FailedThemeCandidateCannotLeakIntoLaterInteractionComposition()
    {
        using var fixture = new Fixture();
        var session = fixture.Session;
        UiTheme light = UiSemanticThemes.Resolve(UiSemanticTheme.Light);
        var candidate = new UiEnvironment(new UiEnvironmentViewport(700, 600), 1.25f,
            UiInputMode.MouseKeyboard, "ru-RU", light.Id);
        var before = session.CurrentInvocation;
        var oldScene = session.Host.Root.Scene;
        fixture.Platform.Fail = true;

        Assert.Throws<InvalidOperationException>(() => session.Recompose(UiPresentationProfiles.Compact,
            Placement(candidate), environment: candidate, theme: light));

        Assert.Same(before, session.CurrentInvocation);
        Assert.Same(oldScene, session.Host.Root.Scene);
        fixture.Platform.Fail = false;
        session.Host.Root.RefreshInteractionVisuals();
        Assert.Equal(fixture.InitialEnvironment.Theme, session.Host.Root.Scene.MeasurementContext.Theme);
        Assert.Equal("en", session.Host.Root.Scene.MeasurementContext.Locale);
        Assert.Same(fixture.InitialEnvironment, session.CurrentInvocation.Plan.Host.Environment);
        session.Recompose(UiPresentationProfiles.Compact, Placement(candidate), environment: candidate, theme: light);
        session.Host.Root.RefreshInteractionVisuals();
        Assert.Equal(light.Id, session.Host.Root.Scene.MeasurementContext.Theme);
        Assert.Equal("ru-RU", session.Host.Root.Scene.MeasurementContext.Locale);
        Assert.Same(candidate, session.CurrentInvocation.Plan.Host.Environment);
        Assert.Equal(1, fixture.Created);
    }

    [Theory]
    [InlineData("availability")]
    [InlineData("measure")]
    public void ThemeRequestDuringPreparationRejectsTheStaleCandidateAndAllowsRetry(string callback)
    {
        using var fixture = new Fixture();
        var session = fixture.Session;
        var before = session.CurrentInvocation;
        var scene = session.Host.Root.Scene;
        UiTheme light = UiSemanticThemes.Resolve(UiSemanticTheme.Light);
        var candidate = new UiEnvironment(new UiEnvironmentViewport(700, 600), 1,
            UiInputMode.MouseKeyboard, "ru-RU", light.Id);
        int requests = 0;
        void RequestTheme()
        {
            fixture.OnAvailable = null;
            fixture.Platform.OnMeasure = null;
            requests++;
            session.SetTheme(fixture.Theme); // Exact same accepted object still changes the requested configuration.
        }
        if (callback == "availability") fixture.OnAvailable = RequestTheme;
        else fixture.Platform.OnMeasure = RequestTheme;

        Assert.Throws<InvalidOperationException>(() => session.Recompose(UiPresentationProfiles.Compact,
            Placement(candidate), environment: candidate, theme: light));

        Assert.Equal(1, requests);
        Assert.Same(before, session.CurrentInvocation);
        Assert.Same(scene, session.Host.Root.Scene);
        session.Host.Root.RefreshInteractionVisuals();
        Assert.Equal(fixture.Theme.Id, session.Host.Root.Scene.MeasurementContext.Theme);
        session.Recompose(UiPresentationProfiles.Compact, Placement(candidate), environment: candidate, theme: light);
        Assert.Same(candidate, session.CurrentInvocation.Plan.Host.Environment);
        Assert.Equal(light.Id, session.Host.Root.Scene.MeasurementContext.Theme);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("interaction")]
    public void CandidateThemeIsPrivateToItsCompositionAndCannotReachNestedAcceptedFrame(string operation)
    {
        using var fixture = new Fixture();
        var session = fixture.Session;
        UiTheme light = UiSemanticThemes.Resolve(UiSemanticTheme.Light);
        var candidate = new UiEnvironment(new UiEnvironmentViewport(700, 600), 1,
            UiInputMode.MouseKeyboard, "ru-RU", light.Id);
        UiScene? nestedScene = null;
        UiInvocationResult? nestedInvocation = null;
        fixture.OnAvailable = () =>
        {
            fixture.OnAvailable = null;
            if (operation == "open") session.OpenSection(Id("first"));
            else session.Host.Root.RefreshInteractionVisuals();
            nestedScene = session.Host.Root.Scene;
            nestedInvocation = session.CurrentInvocation;
        };

        Assert.Throws<InvalidOperationException>(() => session.Recompose(UiPresentationProfiles.Compact,
            Placement(candidate), environment: candidate, theme: light));

        Assert.NotNull(nestedScene);
        Assert.Same(nestedScene, session.Host.Root.Scene);
        Assert.Same(nestedInvocation, session.CurrentInvocation);
        Assert.Same(fixture.InitialEnvironment, nestedInvocation!.Plan.Host.Environment);
        Assert.Equal(fixture.Theme.Id, nestedScene.MeasurementContext.Theme);
        Assert.Equal("en", nestedScene.MeasurementContext.Locale);
        session.Recompose(UiPresentationProfiles.Compact, Placement(candidate), environment: candidate, theme: light);
        Assert.Same(candidate, session.CurrentInvocation.Plan.Host.Environment);
        Assert.Equal(light.Id, session.Host.Root.Scene.MeasurementContext.Theme);
    }

    [Fact]
    public void ExplicitOpenCarriesTheAcceptedEnvironmentIntoItsNewGeneration()
    {
        using var fixture = new Fixture();
        var session = fixture.Session;
        UiEnvironment changed = Capture(700, 1.5f, UiInputMode.Controller, "ru-RU");
        session.Recompose(UiPresentationProfiles.Controller, Placement(changed), environment: changed);
        Assert.True(session.Host.Root.Actions.Invoke(fixture.RootAction.Definition));
        UiEnvironment? observedEnvironment = null;
        fixture.RootAction.Operations[0].Token.Register(() => observedEnvironment = session.CurrentInvocation.Plan.Host.Environment);

        session.OpenSection(Id("first"));

        Assert.True(fixture.RootAction.Operations[0].Token.IsCancellationRequested);
        Assert.Same(changed, observedEnvironment);
        Assert.Same(changed, session.CurrentInvocation.Plan.Host.Environment);
        Assert.Equal("ru-RU", session.Host.Root.Scene.MeasurementContext.Locale);
        Assert.Equal(2, fixture.Created); // This descriptor is Transient: explicit Open may reactivate it.
        fixture.RootAction.Operations[0].Source.Complete(99);
        session.Host.PumpActions();
        Assert.Empty(fixture.RootAction.Results);
        Assert.True(session.Host.Root.Actions.Invoke(fixture.RootAction.Definition));
        fixture.RootAction.Operations[1].Source.Complete(42);
        session.Host.PumpActions();
        Assert.Equal(new[] { 42 }, fixture.RootAction.Results);
    }

    [Fact]
    public void ReloadPublishesTheCandidateEnvironmentAndAssetsBeforeCancellation()
    {
        using var fixture = new Fixture();
        var session = fixture.Session;
        Assert.True(session.Host.Root.Actions.Invoke(fixture.RootAction.Definition));
        UiEnvironment candidate = Capture(700, 1.25f, UiInputMode.MouseKeyboard, "ru-RU");
        bool assetsAccepted = false, cancellationObserved = false;
        fixture.RootAction.Operations[0].Token.Register(() => cancellationObserved = assetsAccepted
            && ReferenceEquals(candidate, session.CurrentInvocation.Plan.Host.Environment)
            && session.Host.Root.Scene.MeasurementContext.Locale == "ru-RU");

        session.Reload(new UiTerminalSectionAssets(), UiPresentationProfiles.Compact, Placement(candidate), null,
            () => assetsAccepted = true, environment: candidate);

        Assert.True(cancellationObserved);
        Assert.Equal(1, fixture.Created);
        fixture.RootAction.Operations[0].Source.Complete(99);
        session.Host.PumpActions();
        Assert.Empty(fixture.RootAction.Results);
    }

    private static UiSymbolId Id(string path) => new("Hatifect.Tests", "terminal-environment/" + path);
    private static UiEnvironment Capture(float width, float scale, UiInputMode input, string locale)
        => new(new UiEnvironmentViewport(width, 600), scale, input, locale, UiThemePresets.Dark().Id);
    private static UiHostPlacementContext Placement(UiEnvironment environment)
        => new(new UiRect(0, 0, environment.Viewport.Width, environment.Viewport.Height));
    private sealed class Fixture : IDisposable
    {
        internal UiEnvironment InitialEnvironment { get; } = Capture(1200, 1, UiInputMode.MouseKeyboard, "en");
        internal UiTheme Theme { get; } = UiSemanticThemes.Resolve(UiSemanticTheme.Dark);
        internal Action? OnAvailable { get; set; }
        internal int Created { get; private set; }
        internal Probe RootAction { get; } = new("root");
        internal Probe PortalAction { get; } = new("portal");
        internal TestPlatform Platform { get; } = new();
        internal UiTerminalHostSession Session { get; }
        internal Fixture()
        {
            var registry = new UiRegistryBuilder().TerminalSection(Id("first"), "First", () =>
            {
                Created++;
                return Model(Id("first"), RootAction.Definition);
            }, lifetime: UiExperienceLifetime.Transient, isAvailable: () => { OnAvailable?.Invoke(); return true; }).Freeze();
            Session = new(registry, Theme, Placement(InitialEnvironment), Platform,
                UiPresentationProfiles.Wide, environment: InitialEnvironment);
        }
        internal UiPortalHandle PresentPortal()
        {
            var id = Id("popup");
            var policy = new UiHostPolicy(UiHostKind.Popup, UiWindowChrome.Tool, UiDismissPolicy.Escape,
                UiModalPolicy.Modeless, UiFocusScopePolicy.Contained, UiPopupPlacement.Anchor);
            var registry = new UiRegistryBuilder().Window(id, "Popup", () => Model(id, PortalAction.Definition), policy).Freeze();
            var scene = new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(
                new UiInvocationService(registry).Invoke(id, UiPresentationProfiles.Wide));
            return Session.Host.Present(new(id, new UiPortalOwner(Session.Host.Root.Scene.Root.Id), scene,
                new UiHostPlacementContext(Placement(InitialEnvironment).Viewport, anchor: new UiRect(300, 250, 30, 30))));
        }
        private static UiExperienceDefinition Model(UiSymbolId id, UiActionDefinition action)
            => new UiExperienceBuilder(id, "Environment").Monitor("Status", new UiConstantSource<string>("Ready"))
                .Actions("Actions", action).Build();
        public void Dispose() => Session.Dispose();
    }
    private sealed class Probe
    {
        internal UiActionDefinition Definition { get; }
        internal List<(Completion Source, CancellationToken Token)> Operations { get; } = new();
        internal List<int> Results { get; } = new();
        internal Probe(string path)
            => Definition = new UiAction<int, int>(Id(path), "Run", (_, token) =>
            {
                var source = new Completion();
                Operations.Add((source, token));
                return new(source, 0);
            }, UiActionConcurrency.RejectWhileRunning).Bind(() => 7, (_, result) => Results.Add(result.Value));
    }
    private sealed class Completion : IValueTaskSource<UiActionResult<int>>
    {
        private ManualResetValueTaskSourceCore<UiActionResult<int>> _source;
        internal void Complete(int value) => _source.SetResult(UiActionResult<int>.Success(value));
        public UiActionResult<int> GetResult(short token) => _source.GetResult(token);
        public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _source.OnCompleted(continuation, state, token, flags);
    }
    private sealed class TestPlatform : IUiPlatformBridge
    {
        internal bool Fail { get; set; }
        internal Action? OnMeasure { get; set; }
        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
        {
            OnMeasure?.Invoke();
            if (Fail) throw new InvalidOperationException("Controlled environment measurement rejection.");
            return new(Math.Min(availableWidth, text.Length * typography.Size * .5f), typography.Size * typography.LineHeight);
        }
        public void DrawSurface(UiSurfacePrimitive surface) { }
        public void DrawText(UiTextPrimitive text) { }
    }
}
