using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly UiSceneComposer _composer;
    private readonly Func<UiExperienceDescriptor, UiTerminalSectionAssets> _resolveAssets;
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
        _composer = new UiSceneComposer(theme ?? throw new ArgumentNullException(nameof(theme)), registry, catalog);
        _resolveAssets = resolveAssets ?? (_ => DefaultAssets);
    }

    internal void SetTheme(UiTheme theme) { EnsureActive(); _composer.SetTheme(theme); }

    public UiSymbolId? ActiveSection { get; private set; }

    public UiTerminalFrame OpenFirstAvailable(
        UiPresentationProfile profile,
        string? locale = null,
        UiInteractionSnapshot? interaction = null)
    {
        EnsureActive();
        UiExperienceDescriptor[] available = SnapshotAvailableSections();
        UiExperienceDescriptor? descriptor = available.FirstOrDefault();
        if (descriptor == null) throw new InvalidOperationException("No available Terminal section is registered.");
        return Open(descriptor, profile, available, locale, interaction, commit: true);
    }

    public UiTerminalFrame Open(
        UiSymbolId section,
        UiPresentationProfile profile,
        string? locale = null,
        UiInteractionSnapshot? interaction = null)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(profile);
        UiExperienceDescriptor descriptor = TerminalDescriptor(section);
        UiExperienceDescriptor[] available = SnapshotAvailableSections();
        if (!available.Any(item => item.Id == section))
            throw new InvalidOperationException($"Terminal section '{section}' is currently unavailable.");
        return Open(descriptor, profile, available, locale, interaction, commit: true);
    }

    private UiTerminalFrame Open(
        UiExperienceDescriptor descriptor,
        UiPresentationProfile profile,
        IReadOnlyList<UiExperienceDescriptor> available,
        string? locale,
        UiInteractionSnapshot? interaction,
        bool commit)
    {
        ArgumentNullException.ThrowIfNull(profile);
        UiTerminalSectionAssets assets = _resolveAssets(descriptor)
            ?? throw new InvalidOperationException($"Asset resolver for Terminal section '{descriptor.Id}' returned null.");
        UiInvocationResult invocation = _invocations.InvokeKnownAvailable(descriptor, profile, assets.Presentation);
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
        UiInteractionSnapshot? interaction = null)
    {
        EnsureActive();
        return ActiveSection is { } section
            ? Open(section, profile, locale, interaction)
            : throw new InvalidOperationException("No Terminal section is active.");
    }

    public bool TryFollow(
        UiInteractionUpdate update,
        UiPresentationProfile profile,
        out UiTerminalFrame? frame,
        string? locale = null,
        UiInteractionSnapshot? interaction = null)
        => TryFollowCore(update, profile, out frame, locale, interaction, commit: true);

    internal bool TryComposeFollow(
        UiInteractionUpdate update,
        UiPresentationProfile profile,
        out UiTerminalFrame? frame,
        string? locale = null,
        UiInteractionSnapshot? interaction = null)
        => TryFollowCore(update, profile, out frame, locale, interaction, commit: false);

    internal UiTerminalFrame ComposeOpen(
        UiSymbolId section,
        UiPresentationProfile profile,
        string? locale = null,
        UiInteractionSnapshot? interaction = null)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(profile);
        UiExperienceDescriptor descriptor = TerminalDescriptor(section);
        UiExperienceDescriptor[] available = SnapshotAvailableSections();
        if (!available.Any(item => item.Id == section))
            throw new InvalidOperationException($"Terminal section '{section}' is currently unavailable.");
        return Open(descriptor, profile, available, locale, interaction, commit: false);
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
        _ = TerminalDescriptor(frame.Section);
        ActiveSection = frame.Section;
    }

    private bool TryFollowCore(
        UiInteractionUpdate update,
        UiPresentationProfile profile,
        out UiTerminalFrame? frame,
        string? locale,
        UiInteractionSnapshot? interaction,
        bool commit)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(update);
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
        frame = Open(selected, profile, available, locale, interaction, commit);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ActiveSection = null;
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
