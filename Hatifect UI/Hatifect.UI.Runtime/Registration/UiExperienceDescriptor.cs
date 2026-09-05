using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Activation;
using Hatifect.UI.Runtime.Hosting;

namespace Hatifect.UI.Runtime.Registration;

public enum UiExperienceLifetime
{
    Transient,
    Cached,
    Singleton
}

public sealed record UiTerminalSectionMetadata(
    string Group,
    int Order = 0,
    UiSymbolId? Icon = null);

/// <summary>Lazy metadata and factory for an invokable Experience.</summary>
public sealed class UiExperienceDescriptor
{
    private readonly Func<UiExperienceInstance> _factory;
    private readonly Func<bool> _isAvailable;

    public UiExperienceDescriptor(
        UiSymbolId id,
        string title,
        UiHostPolicy host,
        Func<UiExperienceDefinition> factory,
        UiExperienceLifetime lifetime = UiExperienceLifetime.Transient,
        Func<bool>? isAvailable = null,
        UiTerminalSectionMetadata? terminal = null,
        IReadOnlyDictionary<string, string>? metadata = null)
        : this(
            id,
            title,
            host,
            Unowned(id, title, host, factory),
            lifetime,
            isAvailable,
            terminal,
            metadata,
            ownsInstance: false)
    {
    }

    private UiExperienceDescriptor(
        UiSymbolId id,
        string title,
        UiHostPolicy host,
        Func<UiExperienceInstance> factory,
        UiExperienceLifetime lifetime,
        Func<bool>? isAvailable,
        UiTerminalSectionMetadata? terminal,
        IReadOnlyDictionary<string, string>? metadata,
        bool ownsInstance)
    {
        if (!id.IsValid) throw new ArgumentException("A stable descriptor ID is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("A descriptor title is required.", nameof(title));
        Id = id;
        Title = title;
        Host = host ?? throw new ArgumentNullException(nameof(host));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        Lifetime = lifetime;
        _isAvailable = isAvailable ?? (() => true);
        Terminal = terminal;
        var metadataCopy = new Dictionary<string, string>(StringComparer.Ordinal);
        if (metadata != null)
        {
            foreach ((string key, string value) in metadata)
                metadataCopy.Add(key, value);
        }
        Metadata = new ReadOnlyDictionary<string, string>(metadataCopy);

        if (terminal != null && host.Kind != UiHostKind.Terminal)
            throw new ArgumentException("Terminal section metadata requires the Terminal host policy.", nameof(terminal));
        if (terminal == null && host.Kind == UiHostKind.Terminal)
            throw new ArgumentException(
                "The Terminal host policy requires section metadata. Register through TerminalSection.",
                nameof(terminal));
        if (terminal != null && string.IsNullOrWhiteSpace(terminal.Group))
            throw new ArgumentException("A terminal section group is required.", nameof(terminal));
        if (ownsInstance && lifetime != UiExperienceLifetime.Cached)
            throw new ArgumentException("Owned Experience instances require Cached lifetime.", nameof(lifetime));
    }

    public UiSymbolId Id { get; }
    public string Title { get; }
    public UiHostPolicy Host { get; }
    public UiExperienceLifetime Lifetime { get; }
    public UiTerminalSectionMetadata? Terminal { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }
    public bool IsAvailable => _isAvailable();

    internal static UiExperienceDescriptor OwnedTerminalSection(
        UiSymbolId id,
        string title,
        Func<UiExperienceInstance> factory,
        string group,
        int order,
        UiSymbolId? icon,
        Func<bool>? isAvailable)
        => new(
            id,
            title,
            UiHostPolicies.Terminal,
            factory,
            UiExperienceLifetime.Cached,
            isAvailable,
            new UiTerminalSectionMetadata(group, order, icon),
            metadata: null,
            ownsInstance: true);

    internal UiExperienceInstance CreateInstance()
    {
        UiExperienceInstance instance = _factory()
            ?? throw new InvalidOperationException($"Factory for '{Id}' returned null.");
        UiExperienceDefinition experience = instance.Experience;
        if (experience.Id != Id)
        {
            var error = new InvalidOperationException(
                $"Factory for '{Id}' returned Experience '{experience.Id}'. IDs must match.");
            throw instance.DisposeAfterFailure(
                error,
                $"Factory for '{Id}' returned an invalid Experience and its owner failed to dispose.");
        }
        return instance;
    }

    private static Func<UiExperienceInstance> Unowned(
        UiSymbolId id,
        string title,
        UiHostPolicy host,
        Func<UiExperienceDefinition> factory)
    {
        if (!id.IsValid) throw new ArgumentException("A stable descriptor ID is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("A descriptor title is required.", nameof(title));
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(factory);
        return () => factory() is { } experience ? new UiExperienceInstance(experience) : null!;
    }
}
