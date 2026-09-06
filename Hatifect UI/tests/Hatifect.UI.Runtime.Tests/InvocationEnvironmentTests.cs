using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Registration;
using Hatifect.UI.Semantics;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class InvocationEnvironmentTests
{
    [Fact]
    public void EnvironmentInvocationPlansAndProjectsTheRegisteredHost()
    {
        UiSymbolId id = Id("terminal");
        int created = 0;
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(id, "Environment", () => { created++; return Experience(id); }).Freeze();
        var service = new UiInvocationService(registry);
        UiEnvironment environment = Environment(UiInputMode.Controller);

        UiInvocationResult result = service.InvokeInEnvironment(id, environment);

        Assert.Equal(1, created);
        Assert.Equal(UiHostKind.Terminal, result.Plan.Host.HostKind);
        Assert.Equal(UiPresentationProfiles.Controller.Id, result.Plan.Host.Profile);
        UiPlannedElement element = Assert.Single(result.Plan.Elements);
        Assert.Equal(new UiSymbolId("Hatifect.UI", "presentation/Route"), element.Presentation);
        Assert.Equal(element.Element, Assert.Single(result.Projection.Elements).Element);
        Assert.Contains(result.Plan.Decisions, decision => decision.Code == UiPlanDecisionCode.EnvironmentFacet
            && decision.Message.Contains(environment.Locale, StringComparison.Ordinal));
    }

    [Fact]
    public void LegacyNullCallsCompileAndInvalidEnvironmentRejectsBeforeConsumerCallbacks()
    {
        UiSymbolId id = Id("null");
        int created = 0, available = 0;
        UiRegistrySnapshot registry = new UiRegistryBuilder().Window(id, "Environment",
            () => { created++; return Experience(id); }, isAvailable: () => { available++; return true; }).Freeze();
        var service = new UiInvocationService(registry);

        // These calls intentionally leave null uncast: adding an environment overload would break source compatibility.
        Assert.Throws<ArgumentNullException>(() => new UiHostContext(UiHostKind.Window, null!));
        Assert.Throws<ArgumentNullException>(() => service.Invoke(id, null!));
        Assert.Throws<ArgumentNullException>(() => service.InvokeInEnvironment(id, null!));
        Assert.Throws<ArgumentNullException>(() => UiHostContext.InEnvironment(UiHostKind.Window, null!));
        Assert.Equal(0, available);
        Assert.Equal(0, created);

        UiInvocationResult legacy = service.Invoke(id, UiPresentationProfiles.Wide);
        Assert.Equal(1, created);
        Assert.Equal(1, available);
        Assert.Null(legacy.Plan.Host.Environment);
        Assert.Equal(new UiSymbolId("Hatifect.UI", "presentation/Side"), Assert.Single(legacy.Plan.Elements).Presentation);
    }

    [Fact]
    public void KnownAvailableRejectsProfileMismatchBeforeFactoryThenAcceptsMatchingEnvironment()
    {
        UiSymbolId id = Id("known");
        int created = 0;
        UiRegistrySnapshot registry = new UiRegistryBuilder().Window(id, "Environment",
            () => { created++; return Experience(id); }).Freeze();
        Assert.True(registry.TryGetExperience(id, out UiExperienceDescriptor? descriptor));
        var service = new UiInvocationService(registry);
        UiEnvironment environment = Environment(UiInputMode.Controller);

        ArgumentException failure = Assert.Throws<ArgumentException>(() => service.InvokeKnownAvailable(
            descriptor!, UiPresentationProfiles.Wide, environment: environment));
        Assert.Equal("profile", failure.ParamName);
        Assert.Equal(0, created);

        UiInvocationResult result = service.InvokeKnownAvailable(descriptor!, UiPresentationProfiles.Controller,
            environment: environment);
        Assert.Equal(1, created);
        Assert.Equal(new UiSymbolId("Hatifect.UI", "presentation/Route"), Assert.Single(result.Plan.Elements).Presentation);
        Assert.Equal(environment, result.Plan.Host.Environment);
    }

    [Fact]
    public void ReplanChangesEnvironmentWithoutReactivatingTheRetainedExperience()
    {
        UiSymbolId id = Id("replan");
        int created = 0;
        UiRegistrySnapshot registry = new UiRegistryBuilder().Window(id, "Environment",
            () => { created++; return Experience(id); }).Freeze();
        var service = new UiInvocationService(registry);
        UiInvocationResult original = service.InvokeInEnvironment(id, Environment(UiInputMode.Keyboard));
        UiEnvironment controller = Environment(UiInputMode.Controller);

        UiInvocationResult changed = service.ReplanKnownAvailable(original.Descriptor, original.Experience,
            UiPresentationProfiles.Controller, environment: controller);
        UiInvocationResult legacy = service.ReplanKnownAvailable(original.Descriptor, original.Experience,
            UiPresentationProfiles.Wide);

        Assert.Equal(1, created);
        Assert.Same(original.Experience, changed.Experience);
        Assert.Same(original.Experience, legacy.Experience);
        Assert.Equal(new UiSymbolId("Hatifect.UI", "presentation/Route"), Assert.Single(changed.Plan.Elements).Presentation);
        Assert.Equal(new UiSymbolId("Hatifect.UI", "presentation/Side"), Assert.Single(original.Plan.Elements).Presentation);
        Assert.Equal(controller, changed.Plan.Host.Environment);
        Assert.Null(legacy.Plan.Host.Environment);
        Assert.Equal(Assert.Single(original.Projection.Elements).Element, Assert.Single(changed.Projection.Elements).Element);
        Assert.Throws<ArgumentException>(() => service.ReplanKnownAvailable(original.Descriptor, original.Experience,
            UiPresentationProfiles.Wide, environment: controller));
        Assert.Equal(1, created);
    }

    [Fact]
    public void EnvironmentInvocationRetainsAvailabilityAndRegistryBoundaries()
    {
        UiSymbolId id = Id("unavailable");
        int created = 0, availability = 0;
        UiRegistrySnapshot registry = new UiRegistryBuilder().Window(id, "Environment",
            () => { created++; return Experience(id); }, isAvailable: () => { availability++; return false; }).Freeze();
        var service = new UiInvocationService(registry);
        UiEnvironment environment = Environment(UiInputMode.Keyboard);

        Assert.Throws<InvalidOperationException>(() => service.InvokeInEnvironment(id, environment));
        Assert.Throws<KeyNotFoundException>(() => service.InvokeInEnvironment(Id("missing"), environment));
        Assert.Equal(1, availability);
        Assert.Equal(0, created);

        UiRegistrySnapshot foreign = new UiRegistryBuilder().Window(id, "Foreign", () => Experience(id)).Freeze();
        Assert.True(foreign.TryGetExperience(id, out UiExperienceDescriptor? descriptor));
        Assert.Throws<InvalidOperationException>(() => service.InvokeKnownAvailable(descriptor!,
            UiPresentationProfiles.Wide, environment: environment));
        Assert.Throws<InvalidOperationException>(() => service.ReplanKnownAvailable(descriptor!, Experience(id),
            UiPresentationProfiles.Wide, environment: environment));
        Assert.Equal(0, created);
    }

    private static UiSymbolId Id(string suffix) => new("Hatifect.Tests", "environment/" + suffix);
    private static UiExperienceDefinition Experience(UiSymbolId id)
        => new UiExperienceBuilder(id, "Environment").Inspect("Details", new UiState<string>("Ready")).Build();
    private static UiEnvironment Environment(UiInputMode input)
        => new(new UiEnvironmentViewport(1440, 900), 2, input, "ru-RU", Id("theme/high-contrast"),
            new UiAccessibilityPreferences(ReducedMotion: true, HighContrast: true));
}
