using System.Globalization;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Accessibility;
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
using Hatifect.UI.Semantics;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class LocaleCompositionTests
{
    [Fact]
    public void EnvironmentLocaleIsUsedInsteadOfAmbientCulture()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            var experience = new UiExperienceBuilder(Id, "Shipment").Monitor("State", new UiState<string>("Ready")).Build();
            var registry = new UiRegistryBuilder().Window(Id, "Shipment", () => experience).Freeze();
            var invocation = new UiInvocationService(registry).InvokeInEnvironment(Id, Environment("ru-RU"));

            var scene = new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(invocation);

            Assert.Equal("ru-RU", scene.MeasurementContext.Locale);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public void PublicationRecompositionRetainsCapturedInvariantLocaleAfterAmbientCultureChanges()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        using var publication = new UiPublication(Id);
        UiSymbolId element = Id.Child("element/state");
        var source = publication.State(Id.Child("source/state"), 12,
            UiSourceTypes.Scalar<int>(Id.Child("type/count"), false));
        var experience = new UiExperienceBuilder(Id, "Shipment")
            .Element(element, "State", "State", source, UiCapabilities.Monitor)
            .LocalizeElement(element, new UiLocalizedText("State", new Dictionary<string, string> { ["fr-FR"] = "État" }))
            .FormatText<int>(element, (value, locale) => (locale.Length == 0 ? "invariant" : locale) + ":" + value).Build();
        var registry = new UiRegistryBuilder().Window(Id, "Shipment", () => experience).Freeze();
        var invocation = new UiInvocationService(registry).Invoke(Id, UiPresentationProfiles.Wide);
        var composer = new UiSceneComposer(UiThemePresets.Dark(), registry);
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            var initial = composer.Compose(invocation);
            Assert.Equal("", initial.MeasurementContext.Locale);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            source.Value = 13;
            Assert.NotNull(initial.RecomposePublication);
            var refreshed = initial.RecomposePublication!(new UiInteractionSnapshot());
            Assert.Equal("", refreshed.MeasurementContext.Locale);
            var node = Assert.Single(Nodes(refreshed.Root).OfType<UiSourceSceneNode>());
            Assert.Equal("State", node.SemanticName);
            Assert.Equal("invariant:13", node.DisplayText);
            var independent = composer.Compose(invocation);
            Assert.Equal("fr-FR", independent.MeasurementContext.Locale);
            Assert.Equal("État", Assert.Single(Nodes(independent.Root).OfType<UiSourceSceneNode>()).SemanticName);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public void ConflictingLocaleRejectsBeforeReadingOrFormattingConsumerValues()
    {
        var source = new CountingSource();
        var experience = new UiExperienceBuilder(Id, "Shipment").Monitor("State", source).Build();
        var registry = new UiRegistryBuilder().Window(Id, "Shipment", () => experience).Freeze();
        var invocation = new UiInvocationService(registry).InvokeInEnvironment(Id, Environment("ru-RU"));
        int before = source.Reads;

        var failure = Assert.Throws<ArgumentException>(() =>
            new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(invocation, locale: "en"));

        Assert.Equal("locale", failure.ParamName);
        Assert.Equal(before, source.Reads);
    }

    [Fact]
    public void OneRetainedExperienceChangesLabelsValuesAndAccessibilityWithoutChangingActionsOrData()
    {
        using var publication = new UiPublication(Id);
        var source = publication.State(Id.Child("source/shipment"), new Shipment(12, "Ready", "Готово"),
            UiSourceTypes.Scalar<Shipment>(Id.Child("type/shipment"), false));
        UiSymbolId cargo = Id.Child("element/cargo"), state = Id.Child("element/state"), send = Id.Child("action/send");
        int formats = 0, factories = 0, commands = 0;
        var action = new UiActionDefinition(send, "Send", () => commands++);
        var experience = new UiExperienceBuilder(Id, "Shipment")
            .Element(cargo, "Cargo", "Cargo", source, UiCapabilities.Inspect)
            .Element(state, "State", "State", source, UiCapabilities.Monitor)
            .Actions("Actions", action)
            .LocalizeDisplayName(Text("Shipment", "Отправление"))
            .LocalizeElement(cargo, Text("Cargo", "Груз"))
            .LocalizeElement(state, Text("State", "Состояние"))
            .LocalizeElement(Id.Child("element/Actions"), Text("Actions", "Действия"))
            .LocalizeAction(send, Text("Send", "Отправить"))
            .FormatText<Shipment>(cargo, (value, locale) => { formats++; return (locale == "ru-RU" ? "Руда × " : "Ore × ") + value.Quantity; })
            .FormatText<Shipment>(state, (value, locale) => { formats++; return locale == "ru-RU" ? value.Russian : value.English; })
            .Build();
        var registry = new UiRegistryBuilder().Window(Id, "Shipment", () => { factories++; return experience; }).Freeze();
        var invoker = new UiInvocationService(registry);
        var original = invoker.InvokeInEnvironment(Id, Environment("en"));
        var composer = new UiSceneComposer(UiThemePresets.Dark(), registry);
        var platform = new TestPlatform();
        var host = new UiPortalHostSession(composer.Compose(original), Placement, platform);
        Assert.True(host.MoveFocus(UiNavigationDirection.Next).Consumed);
        UiSymbolId? focused = host.Root.Interactions.Snapshot.Focused;
        long revision = source.Version;
        var firstScene = host.Root.Scene;
        int beforeLocaleChanges = formats;
        foreach (string locale in new[] { "ru-RU", "en" })
        {
            var invocation = invoker.ReplanKnownAvailable(original.Descriptor, experience, UiPresentationProfiles.Wide,
                environment: Environment(locale));
            var scene = composer.Compose(invocation, interaction: host.Root.Interactions.Snapshot);
            host.UpdateRoot(scene, Placement);
            bool russian = locale == "ru-RU";
            var values = Nodes(scene.Root).OfType<UiSourceSceneNode>().ToArray();
            Assert.Contains(values, node => node.SemanticName == (russian ? "Груз" : "Cargo") && node.DisplayText == (russian ? "Руда × 12" : "Ore × 12"));
            Assert.Contains(values, node => node.SemanticName == (russian ? "Состояние" : "State") && node.DisplayText == (russian ? "Готово" : "Ready"));
            var button = Assert.Single(Nodes(scene.Root).OfType<UiButtonSceneNode>());
            Assert.Same(action, button.Action);
            Assert.Equal(russian ? "Отправить" : "Send", button.Label);
            Assert.Equal(russian ? "Действия" : "Actions", Assert.Single(Nodes(scene.Root).OfType<UiContainerSceneNode>()).SemanticName);
            Assert.Equal(russian ? "Отправление" : "Shipment", host.Root.Accessibility.Root.Name);
            var accessible = AccessibleNodes(host.Root.Accessibility.Root).ToArray();
            foreach (var value in values)
                Assert.Contains(accessible, node => node.Id == value.Id && node.Name == value.SemanticName && node.Value == value.DisplayText);
            Assert.Contains(accessible, node => node.Id == button.Id && node.Name == button.Label);
            Assert.Equal(focused, host.Root.Interactions.Snapshot.Focused);
            Assert.Equal(locale, scene.MeasurementContext.Locale);
            Assert.Same(experience, invocation.Experience);
            Assert.Same(source, experience.Elements[0].Source);
            Assert.Equal(revision, source.Version);
        }
        int beforeDraw = formats;
        for (int frame = 0; frame < 20; frame++) host.Render();
        Assert.Equal(beforeDraw, formats);
        Assert.Equal(beforeLocaleChanges + 4, formats);
        Assert.Equal(1, factories);
        Assert.Equal(0, commands);
        Assert.Equal("Shipment", firstScene.DisplayName);
        Assert.Equal("Cargo", experience.Elements[0].Alias);
        Assert.True(host.Submit().Consumed);
        Assert.Equal(1, commands);
    }

    [Fact]
    public void FormatterCannotMixPublicationRevisionsOrAmbientLocalesDuringComposition()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        using var publication = new UiPublication(Id);
        var first = publication.State(Id.Child("source/first"), 1, UiSourceTypes.Scalar<int>(Id.Child("type/count"), false));
        var second = publication.State(Id.Child("source/second"), 10, UiSourceTypes.Scalar<int>(Id.Child("type/count"), false));
        UiSymbolId a = Id.Child("element/a"), b = Id.Child("element/b");
        bool armed = false;
        var experience = new UiExperienceBuilder(Id, "Snapshot")
            .Element(a, "A", "A", first, UiCapabilities.Monitor)
            .Element(b, "B", "B", second, UiCapabilities.Monitor)
            .FormatText<int>(a, (value, locale) =>
            {
                if (armed)
                {
                    armed = false;
                    publication.BeginUpdate().Set(first, 2).Set(second, 20).Commit();
                    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
                }
                return locale + ":" + value;
            })
            .FormatText<int>(b, (value, locale) => locale + ":" + value).Build();
        var registry = new UiRegistryBuilder().Window(Id, "Snapshot", () => experience).Freeze();
        var invocation = new UiInvocationService(registry).Invoke(Id, UiPresentationProfiles.Wide);
        var composer = new UiSceneComposer(UiThemePresets.Dark(), registry);
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            armed = true;
            var captured = composer.Compose(invocation);
            Assert.Equal("en-US", captured.MeasurementContext.Locale);
            Assert.Equal(new[] { "en-US:1", "en-US:10" }, Nodes(captured.Root).OfType<UiSourceSceneNode>().Select(node => node.DisplayText));
            Assert.Equal(2, first.Value);
            Assert.Equal(20, second.Value);
            var next = composer.Compose(invocation, locale: "ru-RU");
            Assert.Equal(new[] { "ru-RU:2", "ru-RU:20" }, Nodes(next.Root).OfType<UiSourceSceneNode>().Select(node => node.DisplayText));
            Assert.Equal(new[] { "en-US:1", "en-US:10" }, Nodes(captured.Root).OfType<UiSourceSceneNode>().Select(node => node.DisplayText));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public void FailedFormatterOrMeasurementKeepsAcceptedSceneAndFocusThenRetrySucceeds()
    {
        UiSymbolId element = Id.Child("element/state");
        bool fail = false;
        var expected = new InvalidOperationException("format failed");
        var experience = new UiExperienceBuilder(Id, "Shipment")
            .Element(element, "State", "State", new UiState<int>(12), UiCapabilities.Monitor)
            .Actions("Actions", new UiActionDefinition(Id.Child("action/send"), "Send", () => { }))
            .FormatText<int>(element, (_, locale) => fail ? throw expected : locale).Build();
        var registry = new UiRegistryBuilder().Window(Id, "Shipment", () => experience).Freeze();
        var invoker = new UiInvocationService(registry);
        var original = invoker.InvokeInEnvironment(Id, Environment("en"));
        var next = invoker.ReplanKnownAvailable(original.Descriptor, experience, UiPresentationProfiles.Wide, environment: Environment("ru-RU"));
        var composer = new UiSceneComposer(UiThemePresets.Dark(), registry);
        var platform = new TestPlatform();
        var host = new UiPortalHostSession(composer.Compose(original), Placement, platform);
        host.MoveFocus(UiNavigationDirection.Next);
        var accepted = host.Root.Scene;
        long acceptedVersion = host.Root.AcceptedVersion;
        UiSymbolId? focused = host.Root.Interactions.Snapshot.Focused;

        fail = true;
        var error = Assert.Throws<InvalidOperationException>(() => host.UpdateRoot(composer.Compose(next), Placement));
        Assert.Contains(element.ToString(), error.Message);
        Assert.Same(expected, error.InnerException);
        Assert.Same(accepted, host.Root.Scene);
        Assert.Equal(acceptedVersion, host.Root.AcceptedVersion);
        Assert.Equal(focused, host.Root.Interactions.Snapshot.Focused);
        fail = false;
        platform.FailMeasurement = true;
        Assert.Throws<InvalidOperationException>(() => host.UpdateRoot(composer.Compose(next), Placement));
        Assert.Same(accepted, host.Root.Scene);
        Assert.Equal(acceptedVersion, host.Root.AcceptedVersion);
        Assert.Equal(focused, host.Root.Interactions.Snapshot.Focused);
        platform.FailMeasurement = false;
        host.UpdateRoot(composer.Compose(next), Placement);
        Assert.Equal("ru-RU", host.Root.Scene.MeasurementContext.Locale);
        Assert.Equal("ru-RU", Assert.Single(Nodes(host.Root.Scene.Root).OfType<UiSourceSceneNode>()).DisplayText);
        Assert.Equal(focused, host.Root.Interactions.Snapshot.Focused);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("value", "value:custom-A")]
    public void NullableCapturedValuesAndEmptyFormattedResultsAreSupported(string? input, string expected)
    {
        UiSymbolId element = Id.Child("element/state");
        var experience = new UiExperienceBuilder(Id, "Shipment")
            .Element(element, "State", "State", new UiState<string?>(input), UiCapabilities.Monitor)
            .FormatText<string?>(element, (value, locale) => value is null ? "" : value + ":" + locale).Build();
        var registry = new UiRegistryBuilder().Window(Id, "Shipment", () => experience).Freeze();
        var invocation = new UiInvocationService(registry).InvokeInEnvironment(Id, Environment("custom-A"));
        var scene = new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(invocation);
        Assert.Equal(expected, Assert.Single(Nodes(scene.Root).OfType<UiSourceSceneNode>()).DisplayText);
        Assert.Equal(input, experience.Elements[0].Source.UntypedValue);
    }

    [Fact]
    public void NullFormatterResultRejectsWithElementAndLocaleContext()
    {
        UiSymbolId element = Id.Child("element/state");
        var experience = new UiExperienceBuilder(Id, "Shipment")
            .Element(element, "State", "State", new UiState<int>(1), UiCapabilities.Monitor)
            .FormatText<int>(element, (_, _) => null!).Build();
        var registry = new UiRegistryBuilder().Window(Id, "Shipment", () => experience).Freeze();
        var invocation = new UiInvocationService(registry).InvokeInEnvironment(Id, Environment("custom-A"));
        var error = Assert.Throws<InvalidOperationException>(() => new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(invocation));
        Assert.Contains(element.ToString(), error.Message);
        Assert.Contains("custom-A", error.Message);
        Assert.Contains("null", error.InnerException!.Message);
    }

    [Fact]
    public void LocaleChangeAndRejectedCandidateRetainPendingTypedActionUntilOwnerPump()
    {
        var pending = new TaskCompletionSource<UiActionResult<int>>();
        CancellationToken cancellation = default;
        int captures = 0, executions = 0, completions = 0, resultValue = 0;
        var action = new UiAction<int, int>(Id.Child("action/send"), "Send", (request, token) =>
        {
            Assert.Equal(7, request);
            cancellation = token;
            executions++;
            return new(pending.Task);
        }, UiActionConcurrency.RejectWhileRunning).Bind(() => { captures++; return 7; }, (_, result) =>
        {
            completions++;
            resultValue = result.Value;
        });
        var experience = new UiExperienceBuilder(Id, "Shipment").Actions("Actions", action)
            .LocalizeAction(action.Id, Text("Send", "Отправить")).Build();
        var registry = new UiRegistryBuilder().Window(Id, "Shipment", () => experience).Freeze();
        var invoker = new UiInvocationService(registry);
        var original = invoker.InvokeInEnvironment(Id, Environment("en"));
        var next = invoker.ReplanKnownAvailable(original.Descriptor, experience, UiPresentationProfiles.Wide,
            environment: Environment("ru-RU"));
        var composer = new UiSceneComposer(UiThemePresets.Dark(), registry);
        var platform = new TestPlatform();
        var host = new UiPortalHostSession(composer.Compose(original), Placement, platform);
        try
        {
            host.MoveFocus(UiNavigationDirection.Next);
            Assert.True(host.Submit().Interaction!.ActionInvoked);
            var accepted = host.Root.Scene;
            platform.FailMeasurement = true;
            Assert.Throws<InvalidOperationException>(() => host.UpdateRoot(composer.Compose(next), Placement));
            Assert.Same(accepted, host.Root.Scene);
            Assert.False(cancellation.IsCancellationRequested);
            Assert.Equal(UiActionState.Running, host.Root.Actions.Status(action)!.State);
            platform.FailMeasurement = false;
            host.UpdateRoot(composer.Compose(next), Placement);
            Assert.Same(action, Assert.Single(Nodes(host.Root.Scene.Root).OfType<UiButtonSceneNode>()).Action);
            Assert.Equal("Отправить", Assert.Single(Nodes(host.Root.Scene.Root).OfType<UiButtonSceneNode>()).Label);
            Assert.False(cancellation.IsCancellationRequested);
            Assert.Equal(UiActionState.Running, host.Root.Actions.Status(action)!.State);
            Assert.False(host.Root.Actions.CanInvoke(action));
            pending.SetResult(UiActionResult<int>.Success(71));
            host.Render();
            Assert.Equal(0, completions);
            Assert.True(SpinWait.SpinUntil(() => { host.PumpActions(); return completions != 0; }, TimeSpan.FromSeconds(5)));
            Assert.Equal(1, captures);
            Assert.Equal(1, executions);
            Assert.Equal(1, completions);
            Assert.Equal(71, resultValue);
            Assert.Equal(UiActionState.Completed, host.Root.Actions.Status(action)!.State);
            Assert.True(host.Root.Actions.CanInvoke(action));
        }
        finally { host.Deactivate(); pending.TrySetResult(UiActionResult<int>.Success(0)); }
    }

    private static readonly UiSymbolId Id = new("Hatifect.Tests", "locale/shipment");
    private static UiEnvironment Environment(string locale)
        => new(new UiEnvironmentViewport(1280, 720), 1, UiInputMode.MouseKeyboard, locale,
            new UiSymbolId("Hatifect.Tests", "theme/dark"));
    private static readonly UiHostPlacementContext Placement = new(new UiRect(0, 0, 1280, 720));
    private static UiLocalizedText Text(string fallback, string russian)
        => new(fallback, new Dictionary<string, string> { ["ru-RU"] = russian });
    private static IEnumerable<UiSceneNode> Nodes(UiSceneNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        foreach (var descendant in Nodes(child)) yield return descendant;
    }
    private static IEnumerable<UiAccessibilityNodeSnapshot> AccessibleNodes(UiAccessibilityNodeSnapshot node)
    {
        yield return node;
        foreach (var child in node.Children)
        foreach (var descendant in AccessibleNodes(child)) yield return descendant;
    }
    private sealed record Shipment(int Quantity, string English, string Russian)
    {
        public override string ToString() => throw new InvalidOperationException("The captured typed formatter must handle this value.");
    }
    private sealed class TestPlatform : IUiPlatformBridge
    {
        internal bool FailMeasurement { get; set; }
        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
            => FailMeasurement ? throw new InvalidOperationException("measurement failed")
                : new(Math.Min(text.Length * typography.Size * .6f, availableWidth), typography.Size * typography.LineHeight);
        public void DrawSurface(UiSurfacePrimitive surface) { }
        public void DrawText(UiTextPrimitive text) { }
    }

    private sealed class CountingSource : IUiSemanticSource<string>
    {
        internal int Reads { get; private set; }
        public string Value { get { Reads++; return "Ready"; } }
        public Type ValueType => typeof(string);
        public object UntypedValue => Value;
        public event Action? Changed { add { } remove { } }
    }
}
