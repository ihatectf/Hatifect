using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Activation;
using Hatifect.UI.Runtime.Hosting;

namespace Hatifect.UI.Runtime.Registration;

public sealed class UiRegistryBuilder
{
    private readonly Dictionary<UiSymbolId, UiExperienceDescriptor> _experiences = new();
    private readonly Dictionary<UiSymbolId, UiContributionPointDescriptor> _points = new();
    private readonly Dictionary<UiSymbolId, UiContributionDescriptor> _contributions = new();
    private readonly HashSet<UiSymbolId> _allIds = new();
    private bool _frozen;

    public UiRegistryBuilder Window(
        UiSymbolId id,
        string title,
        Func<UiExperienceDefinition> factory,
        UiHostPolicy? host = null,
        UiExperienceLifetime lifetime = UiExperienceLifetime.Transient,
        Func<bool>? isAvailable = null)
        => Register(new UiExperienceDescriptor(
            id, title, host ?? UiHostPolicies.Window, factory, lifetime, isAvailable));

    public UiRegistryBuilder Popup(
        UiSymbolId id,
        string title,
        Func<UiExperienceDefinition> factory,
        UiHostPolicy? host = null,
        UiExperienceLifetime lifetime = UiExperienceLifetime.Transient,
        Func<bool>? isAvailable = null)
    {
        UiHostPolicy selected = host ?? UiHostPolicies.Popup;
        if (selected.Kind is not (UiHostKind.Popup or UiHostKind.Context))
            throw new ArgumentException("Popup registration requires a Popup or Context host policy.", nameof(host));
        return Register(new UiExperienceDescriptor(id, title, selected, factory, lifetime, isAvailable));
    }

    public UiRegistryBuilder TerminalSection(
        UiSymbolId id,
        string title,
        Func<UiExperienceDefinition> factory,
        string group = "General",
        int order = 0,
        UiSymbolId? icon = null,
        UiExperienceLifetime lifetime = UiExperienceLifetime.Cached,
        Func<bool>? isAvailable = null)
        => Register(new UiExperienceDescriptor(
            id,
            title,
            UiHostPolicies.Terminal,
            factory,
            lifetime,
            isAvailable,
            new UiTerminalSectionMetadata(group, order, icon)));

    internal UiRegistryBuilder TerminalSectionOwned(
        UiSymbolId id,
        string title,
        Func<(UiExperienceDefinition Experience, IDisposable Owner)> factory,
        string group = "General",
        int order = 0,
        UiSymbolId? icon = null,
        Func<bool>? isAvailable = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return Register(UiExperienceDescriptor.OwnedTerminalSection(
            id,
            title,
            () =>
            {
                (UiExperienceDefinition experience, IDisposable owner) = factory();
                if (experience == null)
                {
                    var error = new InvalidOperationException(
                        $"Owned factory for '{id}' returned a null Experience.");
                    if (owner == null) throw error;
                    throw UiExperienceInstance.DisposeOwnerAfterFailure(
                        owner,
                        error,
                        $"Owned factory for '{id}' returned a null Experience and its owner failed to dispose.");
                }
                if (owner == null)
                    throw new InvalidOperationException($"Owned factory for '{id}' returned a null owner.");
                return new UiExperienceInstance(experience, owner);
            },
            group,
            order,
            icon,
            isAvailable));
    }

    public UiRegistryBuilder Register(UiExperienceDescriptor descriptor)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(descriptor);
        AddGlobalId(descriptor.Id);
        _experiences.Add(descriptor.Id, descriptor);
        return this;
    }

    public UiRegistryBuilder Publish(UiContributionPointDescriptor point)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(point);
        AddGlobalId(point.Id);
        _points.Add(point.Id, point);
        return this;
    }

    public UiRegistryBuilder Contribute(UiContributionDescriptor contribution)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(contribution);
        AddGlobalId(contribution.Id);
        _contributions.Add(contribution.Id, contribution);
        return this;
    }

    public UiRegistrySnapshot Freeze()
    {
        EnsureMutable();
        ValidateReferences();
        _frozen = true;
        return new UiRegistrySnapshot(
            new Dictionary<UiSymbolId, UiExperienceDescriptor>(_experiences),
            new Dictionary<UiSymbolId, UiContributionPointDescriptor>(_points),
            new Dictionary<UiSymbolId, UiContributionDescriptor>(_contributions));
    }

    private void ValidateReferences()
    {
        foreach (UiContributionPointDescriptor point in _points.Values)
        {
            if (!_experiences.ContainsKey(point.Owner))
                throw new InvalidOperationException($"Contribution point '{point.Id}' has unknown owner '{point.Owner}'.");
        }

        foreach (UiContributionDescriptor contribution in _contributions.Values)
        {
            if (!_points.TryGetValue(contribution.Target, out UiContributionPointDescriptor? point))
                throw new InvalidOperationException($"Contribution '{contribution.Id}' targets unknown point '{contribution.Target}'.");
            if (!point.AllowedKinds.Contains(contribution.Kind))
                throw new InvalidOperationException($"Contribution point '{point.Id}' does not allow {contribution.Kind} contributions.");
            if (contribution is UiRouteContributionDescriptor route && !_experiences.ContainsKey(route.Route))
                throw new InvalidOperationException($"Contribution '{contribution.Id}' references unknown route '{route.Route}'.");
        }
    }

    private void AddGlobalId(UiSymbolId id)
    {
        if (!_allIds.Add(id)) throw new InvalidOperationException($"UI registration ID '{id}' is already registered.");
    }

    private void EnsureMutable()
    {
        if (_frozen) throw new InvalidOperationException("The UI registry is frozen.");
    }
}

public sealed class UiRegistrySnapshot
{
    private readonly IReadOnlyDictionary<UiSymbolId, UiExperienceDescriptor> _experiences;
    private readonly IReadOnlyDictionary<UiSymbolId, UiContributionPointDescriptor> _points;
    private readonly IReadOnlyDictionary<UiSymbolId, UiContributionDescriptor> _contributions;
    private readonly Dictionary<UiSymbolId, UiExperienceDefinition> _singletons = new();
    private readonly object _singletonLock = new();

    internal UiRegistrySnapshot(
        IDictionary<UiSymbolId, UiExperienceDescriptor> experiences,
        IDictionary<UiSymbolId, UiContributionPointDescriptor> points,
        IDictionary<UiSymbolId, UiContributionDescriptor> contributions)
    {
        _experiences = new ReadOnlyDictionary<UiSymbolId, UiExperienceDescriptor>(experiences);
        _points = new ReadOnlyDictionary<UiSymbolId, UiContributionPointDescriptor>(points);
        _contributions = new ReadOnlyDictionary<UiSymbolId, UiContributionDescriptor>(contributions);
    }

    public IReadOnlyList<UiExperienceDescriptor> Experiences => _experiences.Values.ToArray();
    public IReadOnlyList<UiContributionPointDescriptor> ContributionPoints => _points.Values.ToArray();

    public bool TryGetExperience(UiSymbolId id, out UiExperienceDescriptor? descriptor)
        => _experiences.TryGetValue(id, out descriptor);

    public IReadOnlyList<UiExperienceDescriptor> TerminalSections()
        => _experiences.Values
            .Where(descriptor => descriptor.Terminal != null)
            .OrderBy(descriptor => descriptor.Terminal!.Group, StringComparer.Ordinal)
            .ThenBy(descriptor => descriptor.Terminal!.Order)
            .ThenBy(descriptor => descriptor.Id.ToString(), StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<UiContributionDescriptor> Contributions(UiSymbolId point)
    {
        if (!_points.ContainsKey(point)) throw new KeyNotFoundException($"Unknown contribution point '{point}'.");
        return _contributions.Values
            .Where(contribution => contribution.Target == point)
            .OrderBy(contribution => contribution.Order)
            .ThenBy(contribution => contribution.Id.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<UiContributionPointDescriptor> ContributionPointsFor(UiSymbolId owner)
        => _points.Values
            .Where(point => point.Owner == owner)
            .OrderBy(point => point.Id.ToString(), StringComparer.Ordinal)
            .ToArray();

    internal UiExperienceDefinition Singleton(UiExperienceDescriptor descriptor)
    {
        lock (_singletonLock)
        {
            if (!_singletons.TryGetValue(descriptor.Id, out UiExperienceDefinition? experience))
            {
                UiExperienceInstance instance = descriptor.CreateInstance();
                if (instance.HasOwner)
                {
                    var error = new InvalidOperationException(
                        $"Singleton Experience '{descriptor.Id}' cannot attach a session-owned lifecycle.");
                    throw instance.DisposeAfterFailure(
                        error,
                        $"Singleton Experience '{descriptor.Id}' attached an invalid owner that failed to dispose.");
                }
                experience = instance.Experience;
                _singletons.Add(descriptor.Id, experience);
            }
            return experience;
        }
    }
}
