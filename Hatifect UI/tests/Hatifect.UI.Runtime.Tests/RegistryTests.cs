using System;
using System.Collections.Generic;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Activation;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Registration;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class RegistryTests
{
    [Fact]
    public void RegistrationIsLazyAndCachedTerminalSectionIsSessionScoped()
    {
        int created = 0;
        UiSymbolId id = Id("storage");
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(id, "Storage", () => { created++; return Experience(id); })
            .Freeze();

        Assert.Equal(0, created);
        var firstSession = new UiExperienceActivator(registry);
        UiExperienceDefinition first = firstSession.Activate(id);
        UiExperienceDefinition second = firstSession.Activate(id);
        UiExperienceDefinition third = new UiExperienceActivator(registry).Activate(id);

        Assert.Same(first, second);
        Assert.NotSame(first, third);
        Assert.Equal(2, created);
    }

    [Fact]
    public void TransientWindowCreatesEachTime()
    {
        int created = 0;
        UiSymbolId id = Id("window");
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Window(id, "Window", () => { created++; return Experience(id); })
            .Freeze();
        var activator = new UiExperienceActivator(registry);

        Assert.NotSame(activator.Activate(id), activator.Activate(id));
        Assert.Equal(2, created);
    }

    [Fact]
    public void DuplicateIdsAreErrorsAndFrozenRegistryRejectsWrites()
    {
        UiSymbolId id = Id("window");
        var builder = new UiRegistryBuilder().Window(id, "Window", () => Experience(id));

        Assert.Throws<InvalidOperationException>(() =>
            builder.Window(id, "Other", () => Experience(id)));

        builder.Freeze();
        Assert.Throws<InvalidOperationException>(() =>
            builder.Window(Id("late"), "Late", () => Experience(Id("late"))));
    }

    [Fact]
    public void ForeignWindowContributionsRequirePublishedPointAndStableOrdering()
    {
        UiSymbolId shell = Id("terminal");
        UiSymbolId storage = Id("storage");
        UiSymbolId point = Id("terminal/navigation");
        var builder = new UiRegistryBuilder()
            .TerminalSection(shell, "Terminal", () => Experience(shell), group: "Shell")
            .TerminalSection(storage, "Storage", () => Experience(storage));
        builder.Publish(new UiContributionPointDescriptor(
            point, shell, new UiSymbolId("Hatifect.UI", "region/Navigation"), UiContributionKind.Section));
        builder.Contribute(new UiRouteContributionDescriptor(
            Id("contribution/z"), point, "Z", storage, UiContributionKind.Section, order: 10));
        builder.Contribute(new UiRouteContributionDescriptor(
            Id("contribution/a"), point, "A", storage, UiContributionKind.Section, order: 10));

        UiRegistrySnapshot registry = builder.Freeze();
        IReadOnlyList<UiContributionDescriptor> contributions = registry.Contributions(point);

        Assert.Equal(Id("contribution/a"), contributions[0].Id);
        Assert.Equal(Id("contribution/z"), contributions[1].Id);
    }

    [Fact]
    public void UnknownForeignWindowTargetFailsAtFreeze()
    {
        UiSymbolId route = Id("storage");
        var action = new UiActionDefinition(Id("action/take"), "Take", () => { });
        var builder = new UiRegistryBuilder()
            .Window(route, "Storage", () => Experience(route))
            .Contribute(new UiActionContributionDescriptor(
                Id("contribution/take"), Id("missing/point"), action));

        Assert.Throws<InvalidOperationException>(() => builder.Freeze());
    }

    [Fact]
    public void ContributionHelpIsImmutableLocalizedMetadata()
    {
        UiSymbolId point = Id("point/actions");
        var tooltip = new UiLocalizedText(
            "Run the contributed command",
            new[] { KeyValuePair.Create("ru-RU", "Выполнить добавленную команду") });
        var action = new UiActionContributionDescriptor(
            Id("contribution/action"),
            point,
            new UiActionDefinition(Id("action/run"), "Run", () => { }),
            tooltip: tooltip);
        var route = new UiRouteContributionDescriptor(
            Id("contribution/route"),
            point,
            "Open",
            Id("destination"),
            tooltip: tooltip);

        Assert.Same(tooltip, action.Tooltip);
        Assert.Same(tooltip, route.Tooltip);
        Assert.Equal("Выполнить добавленную команду", action.Tooltip!.Resolve("ru-RU"));
    }

    [Fact]
    public void AtypicalWindowUsesExplicitHostPolicy()
    {
        var policy = new UiHostPolicy(
            UiHostKind.Overlay,
            UiWindowChrome.Borderless,
            UiDismissPolicy.HostControlled,
            UiModalPolicy.Modeless,
            UiFocusScopePolicy.Shared,
            UiPopupPlacement.Automatic,
            Id("host/hud-overlay"));
        UiSymbolId route = Id("hud");
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Register(new UiExperienceDescriptor(route, "HUD", policy, () => Experience(route)))
            .Freeze();

        Assert.True(registry.TryGetExperience(route, out UiExperienceDescriptor? descriptor));
        Assert.Equal(UiWindowChrome.Borderless, descriptor!.Host.Chrome);
        Assert.Equal(Id("host/hud-overlay"), descriptor.Host.CustomPolicy.GetValueOrDefault());
    }

    [Fact]
    public void PopupPolicyOwnsPlacementDismissAndFocusBehavior()
    {
        UiSymbolId route = Id("chooser");
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Popup(route, "Chooser", () => Experience(route), UiHostPolicies.Modal)
            .Freeze();

        Assert.True(registry.TryGetExperience(route, out UiExperienceDescriptor? descriptor));
        Assert.Equal(UiHostKind.Popup, descriptor!.Host.Kind);
        Assert.Equal(UiPopupPlacement.Center, descriptor.Host.PopupPlacement);
        Assert.Equal(UiDismissPolicy.Explicit, descriptor.Host.Dismiss);
        Assert.Equal(UiFocusScopePolicy.Trapped, descriptor.Host.Focus);
    }

    [Fact]
    public void TerminalHostRequiresTerminalSectionMetadata()
    {
        UiSymbolId route = Id("invalid-terminal-window");

        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            new UiRegistryBuilder().Window(
                route,
                "Invalid Terminal",
                () => Experience(route),
                UiHostPolicies.Terminal));

        Assert.Contains("TerminalSection", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OwnedCachedRegistrationDisposesOnEvictionAndSessionTeardown()
    {
        UiSymbolId route = Id("owned-cached");
        int created = 0;
        int disposed = 0;
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSectionOwned(
                route,
                "Owned",
                () =>
                {
                    created++;
                    return (Experience(route), new RecordingOwner(() => disposed++));
                })
            .Freeze();
        var activator = new UiExperienceActivator(registry);

        Assert.Same(activator.Activate(route), activator.Activate(route));
        Assert.Equal(1, created);
        Assert.True(activator.Evict(route));
        Assert.Equal(1, disposed);
        Assert.False(activator.Evict(route));
        _ = activator.Activate(route);
        Assert.Equal(2, created);

        activator.DisposeSession();

        Assert.Equal(2, disposed);
        Assert.Throws<ObjectDisposedException>(() => activator.Activate(route));
    }

    [Fact]
    public void OwnedFactoryDisposesOwnerBeforeRejectingNullExperience()
    {
        UiSymbolId route = Id("owned-invalid-null");
        int disposed = 0;
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSectionOwned(
                route,
                "Invalid",
                () => (null!, new RecordingOwner(() => disposed++)))
            .Freeze();
        var activator = new UiExperienceActivator(registry);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => activator.Activate(route));

        Assert.Contains("null Experience", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, disposed);
    }

    [Fact]
    public void SessionTeardownAttemptsEveryOwnerAndBecomesFailClosed()
    {
        UiSymbolId first = Id("owned-dispose/first");
        UiSymbolId second = Id("owned-dispose/second");
        int firstDisposed = 0;
        int secondDisposed = 0;
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSectionOwned(
                first,
                "First",
                () => (Experience(first), new RecordingOwner(() =>
                {
                    firstDisposed++;
                    throw new InvalidOperationException("first disposal failed");
                })))
            .TerminalSectionOwned(
                second,
                "Second",
                () => (Experience(second), new RecordingOwner(() => secondDisposed++)))
            .Freeze();
        var activator = new UiExperienceActivator(registry);
        _ = activator.Activate(first);
        _ = activator.Activate(second);

        AggregateException error = Assert.Throws<AggregateException>(() => activator.DisposeSession());

        Assert.Single(error.InnerExceptions);
        Assert.Equal(1, firstDisposed);
        Assert.Equal(1, secondDisposed);
        activator.DisposeSession();
        Assert.Throws<ObjectDisposedException>(() => activator.Activate(first));
    }

    private static UiExperienceDefinition Experience(UiSymbolId id)
        => new UiExperienceBuilder(id, id.LocalId)
            .Browse("Items", new UiCollectionSource<string>(
                Array.Empty<string>(), item => id.Child($"item/{item}")))
            .Build();

    internal static UiSymbolId Id(string local) => new("Author.Mod", local);

    private sealed class RecordingOwner : IDisposable
    {
        private readonly Action _dispose;

        public RecordingOwner(Action dispose) => _dispose = dispose;

        public void Dispose() => _dispose();
    }
}
