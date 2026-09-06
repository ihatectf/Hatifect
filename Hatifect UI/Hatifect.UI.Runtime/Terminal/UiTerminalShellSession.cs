using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Activation;
using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Registration;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Visual.Theming;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Runtime.Terminal;

internal sealed record UiTerminalSectionAssets(
    UiPresentationDefinition? Presentation = null,
    UiVisualDefinition? Visual = null);

internal sealed record UiTerminalFrame(
    UiSymbolId Section,
    UiInvocationResult Invocation,
    UiScene Scene);

/// <summary>
/// Provisional host-free Terminal shell controller. It owns one activation cache and resolves each
/// section lazily from frozen registry metadata; section assets describe child content only.
/// </summary>
internal sealed class UiTerminalShellSession : IDisposable
{
    private static readonly UiTerminalSectionAssets DefaultAssets = new();
    private readonly UiRegistrySnapshot _registry;
    private readonly UiExperienceActivator _activator;
    private readonly UiInvocationService _invocations;
    private UiSceneComposer _composer;
    private UiTheme _theme;
    private readonly UiSemanticCatalog? _catalog;
    private readonly Func<UiExperienceDescriptor, UiTerminalSectionAssets> _resolveAssets;
    private UiExperienceDefinition? _activeExperience;
    private long _commitVersion;
    private bool _disposed;

    public UiTerminalShellSession(
        UiRegistrySnapshot registry,
        UiTheme theme,
        Func<UiExperienceDescriptor, UiTerminalSectionAssets>? resolveAssets = null,
        UiSemanticCatalog? catalog = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _activator = new UiExperienceActivator(registry);
        _invocations = new UiInvocationService(registry, _activator);
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _catalog = catalog;
        _composer = new UiSceneComposer(theme, registry, catalog);
        _resolveAssets = resolveAssets ?? (_ => DefaultAssets);
    }

    internal void SetTheme(UiTheme theme)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(theme);
        if (ReferenceEquals(theme, _theme)) return;
        // Captured composers keep their own immutable theme even if a callback changes
        // the accepted configuration. No candidate mutates a composer used by nested work.
        _composer = new UiSceneComposer(theme, _registry, _catalog);
        _theme = theme;
        _commitVersion++;
    }

    public UiSymbolId? ActiveSection { get; private set; }

    public UiTerminalFrame OpenFirstAvailable(
        UiPresentationProfile profile,
        string? locale = null,
        UiInteractionSnapshot? interaction = null,
        UiEnvironment? environment = null)
    {
        EnsureActive();
        locale = ValidateEnvironment(profile, locale, environment);
        UiExperienceDescriptor[] available = SnapshotAvailableSections();
        UiExperienceDescriptor? descriptor = available.FirstOrDefault();
        if (descriptor == null) throw new InvalidOperationException("No available Terminal section is registered.");
        return Open(descriptor, profile, available, locale, interaction, commit: true, environment);
    }

    public UiTerminalFrame Open(
        UiSymbolId section,
        UiPresentationProfile profile,
        string? locale = null,
        UiInteractionSnapshot? interaction = null,
        UiEnvironment? environment = null)
    {
        EnsureActive();
        locale = ValidateEnvironment(profile, locale, environment);
        UiExperienceDescriptor descriptor = TerminalDescriptor(section);
        UiExperienceDescriptor[] available = SnapshotAvailableSections();
        if (!available.Any(item => item.Id == section))
            throw new InvalidOperationException($"Terminal section '{section}' is currently unavailable.");
        return Open(descriptor, profile, available, locale, interaction, commit: true, environment);
    }

    private UiTerminalFrame Open(
        UiExperienceDescriptor descriptor,
        UiPresentationProfile profile,
        IReadOnlyList<UiExperienceDescriptor> available,
        string? locale,
        UiInteractionSnapshot? interaction,
        bool commit,
        UiEnvironment? environment)
    {
        ArgumentNullException.ThrowIfNull(profile);
        UiTerminalSectionAssets assets = _resolveAssets(descriptor)
            ?? throw new InvalidOperationException($"Asset resolver for Terminal section '{descriptor.Id}' returned null.");
        UiInvocationResult invocation = _invocations.InvokeKnownAvailable(descriptor, profile, assets.Presentation, environment);
        UiScene scene = _composer.Compose(
            invocation,
            assets.Visual,
            interaction,
            locale,
            terminalSections: available);
        var frame = new UiTerminalFrame(descriptor.Id, invocation, scene);
        if (commit) Commit(frame);
        return frame;
    }

    public UiTerminalFrame Recompose(
        UiPresentationProfile profile,
        string? locale = null,
        UiInteractionSnapshot? interaction = null,
        UiEnvironment? environment = null,
        UiTheme? theme = null)
        => RecomposeCore(profile, locale, interaction, null, environment, theme);

    internal UiTerminalFrame RecomposeWithAssets(UiPresentationProfile profile,
        UiTerminalSectionAssets assets, string? locale, UiInteractionSnapshot? interaction, UiEnvironment? environment = null,
        UiTheme? theme = null)
        => RecomposeCore(profile, locale, interaction, assets ?? throw new ArgumentNullException(nameof(assets)), environment, theme);

    private UiTerminalFrame RecomposeCore(UiPresentationProfile profile, string? locale,
        UiInteractionSnapshot? interaction, UiTerminalSectionAssets? candidateAssets, UiEnvironment? environment, UiTheme? theme)
    {
        EnsureActive();
        locale = ValidateEnvironment(profile, locale, environment);
        if (ActiveSection is not { } section || _activeExperience is not { } experience)
            throw new InvalidOperationException("No Terminal section is active.");
        long commitVersion = _commitVersion;
        UiSceneComposer composer = theme == null || ReferenceEquals(theme, _theme)
            ? _composer : new UiSceneComposer(theme, _registry, _catalog);
        UiExperienceDescriptor descriptor = TerminalDescriptor(section);
        UiExperienceDescriptor[] available = SnapshotAvailableSections();
        EnsureRecompositionActive(commitVersion);
        if (!available.Any(item => item.Id == section))
            throw new InvalidOperationException($"Terminal section '{section}' is currently unavailable.");
        UiTerminalSectionAssets assets = candidateAssets ?? _resolveAssets(descriptor)
            ?? throw new InvalidOperationException($"Asset resolver for Terminal section '{section}' returned null.");
        EnsureRecompositionActive(commitVersion);
        // Input, environment and asset refresh keep the committed model. Only an explicit open
        // activates a new Transient instance, and only its accepted frame replaces that model.
        UiInvocationResult invocation = _invocations.ReplanKnownAvailable(
            descriptor, experience, profile, assets.Presentation, environment);
        UiScene scene = composer.Compose(invocation, assets.Visual, interaction, locale,
            terminalSections: available);
        EnsureRecompositionActive(commitVersion);
        return new UiTerminalFrame(section, invocation, scene);
    }

    public bool TryFollow(
        UiInteractionUpdate update,
        UiPresentationProfile profile,
        out UiTerminalFrame? frame,
        string? locale = null,
        UiInteractionSnapshot? interaction = null,
        UiEnvironment? environment = null)
        => TryFollowCore(update, profile, out frame, locale, interaction, commit: true, environment);

    internal bool TryComposeFollow(
        UiInteractionUpdate update,
        UiPresentationProfile profile,
        out UiTerminalFrame? frame,
        string? locale = null,
        UiInteractionSnapshot? interaction = null,
        UiEnvironment? environment = null)
        => TryFollowCore(update, profile, out frame, locale, interaction, commit: false, environment);

    internal UiTerminalFrame ComposeOpen(
        UiSymbolId section,
        UiPresentationProfile profile,
        string? locale = null,
        UiInteractionSnapshot? interaction = null,
        UiEnvironment? environment = null)
    {
        EnsureActive();
        locale = ValidateEnvironment(profile, locale, environment);
        UiExperienceDescriptor descriptor = TerminalDescriptor(section);
        UiExperienceDescriptor[] available = SnapshotAvailableSections();
        if (!available.Any(item => item.Id == section))
            throw new InvalidOperationException($"Terminal section '{section}' is currently unavailable.");
        return Open(descriptor, profile, available, locale, interaction, commit: false, environment);
    }

    internal bool Evict(UiSymbolId section)
    {
        EnsureActive();
        if (ActiveSection == section)
            throw new InvalidOperationException($"The active Terminal section '{section}' cannot be evicted.");
        return _activator.Evict(section);
    }

    internal void Commit(UiTerminalFrame frame)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(frame);
        UiExperienceDescriptor descriptor = TerminalDescriptor(frame.Section);
        if (!ReferenceEquals(frame.Invocation.Descriptor, descriptor) || frame.Invocation.Experience.Id != frame.Section)
            throw new InvalidOperationException("The Terminal frame does not belong to its section.");
        ActiveSection = frame.Section;
        _activeExperience = frame.Invocation.Experience;
        _commitVersion++;
    }

    private void EnsureRecompositionActive(long commitVersion)
    {
        EnsureActive();
        // Explicit reopen can reuse the same Cached model; identity equality alone is insufficient.
        if (_commitVersion != commitVersion)
            throw new InvalidOperationException("The active Terminal section changed during recomposition.");
    }

    private bool TryFollowCore(
        UiInteractionUpdate update,
        UiPresentationProfile profile,
        out UiTerminalFrame? frame,
        string? locale,
        UiInteractionSnapshot? interaction,
        bool commit,
        UiEnvironment? environment)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(update);
        locale = ValidateEnvironment(profile, locale, environment);
        if (update.Route is not { } route ||
            !_registry.TryGetExperience(route, out UiExperienceDescriptor? descriptor) ||
            descriptor?.Terminal == null)
        {
            frame = null;
            return false;
        }

        UiExperienceDescriptor[] available = SnapshotAvailableSections();
        UiExperienceDescriptor? selected = available.FirstOrDefault(item => item.Id == route);
        if (selected == null)
        {
            frame = null;
            return false;
        }
        frame = Open(selected, profile, available, locale, interaction, commit, environment);
        return true;
    }

    internal static string? ValidateEnvironment(UiPresentationProfile profile, string? locale, UiEnvironment? environment)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (environment == null) return locale;
        if (profile.Id != UiPresentationProfiles.Resolve(environment).Id)
            throw new ArgumentException("The Terminal profile contradicts its captured environment.", nameof(profile));
        if (locale != null && !StringComparer.Ordinal.Equals(locale, environment.Locale))
            throw new ArgumentException("The Terminal locale contradicts its captured environment.", nameof(locale));
        return environment.Locale;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ActiveSection = null;
        _activeExperience = null;
        _activator.DisposeSession();
    }

    private UiExperienceDescriptor[] SnapshotAvailableSections()
        => _registry.TerminalSections().Where(section => section.IsAvailable).ToArray();

    private UiExperienceDescriptor TerminalDescriptor(UiSymbolId section)
    {
        if (!_registry.TryGetExperience(section, out UiExperienceDescriptor? descriptor) || descriptor == null)
            throw new KeyNotFoundException($"Unknown UI Experience '{section}'.");
        if (descriptor.Terminal == null || descriptor.Host.Kind != UiHostKind.Terminal)
            throw new InvalidOperationException($"UI Experience '{section}' is not a registered Terminal section.");
        return descriptor;
    }

    private void EnsureActive()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(UiTerminalShellSession));
    }
}
