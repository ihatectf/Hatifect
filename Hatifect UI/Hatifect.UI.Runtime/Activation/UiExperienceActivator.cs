using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Registration;

namespace Hatifect.UI.Runtime.Activation;

/// <summary>Explicit lifecycle boundary for lazy Experience creation.</summary>
public sealed class UiExperienceActivator
{
    private readonly UiRegistrySnapshot _registry;
    private readonly Dictionary<UiSymbolId, UiExperienceInstance> _sessionCache = new();
    private bool _sessionDisposed;

    public UiExperienceActivator(UiRegistrySnapshot registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public UiExperienceDefinition Activate(UiSymbolId id)
    {
        EnsureSessionActive();
        if (!_registry.TryGetExperience(id, out UiExperienceDescriptor? descriptor) || descriptor == null)
            throw new KeyNotFoundException($"Unknown UI Experience '{id}'.");
        if (!descriptor.IsAvailable)
            throw new InvalidOperationException($"UI Experience '{id}' is currently unavailable.");

        return ActivateKnownAvailable(descriptor);
    }

    internal UiExperienceDefinition ActivateKnownAvailable(UiExperienceDescriptor descriptor)
    {
        EnsureSessionActive();
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!_registry.TryGetExperience(descriptor.Id, out UiExperienceDescriptor? registered) ||
            !ReferenceEquals(registered, descriptor))
            throw new InvalidOperationException(
                $"UI Experience descriptor '{descriptor.Id}' does not belong to this registry snapshot.");

        return descriptor.Lifetime switch
        {
            UiExperienceLifetime.Transient => Transient(descriptor),
            UiExperienceLifetime.Cached => Session(descriptor),
            UiExperienceLifetime.Singleton => _registry.Singleton(descriptor),
            _ => throw new ArgumentOutOfRangeException(nameof(descriptor.Lifetime))
        };
    }

    public bool Evict(UiSymbolId id)
    {
        EnsureSessionActive();
        if (!_sessionCache.Remove(id, out UiExperienceInstance? instance)) return false;
        instance.Dispose();
        return true;
    }

    internal void DisposeSession()
    {
        if (_sessionDisposed) return;
        _sessionDisposed = true;
        UiExperienceInstance[] instances = _sessionCache
            .OrderBy(item => item.Key.ToString(), StringComparer.Ordinal)
            .Select(item => item.Value)
            .ToArray();
        _sessionCache.Clear();
        var failures = new List<Exception>();
        foreach (UiExperienceInstance instance in instances)
        {
            try { instance.Dispose(); }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count > 0)
            throw new AggregateException("One or more cached UI Experience owners failed to dispose.", failures);
    }

    private UiExperienceDefinition Session(UiExperienceDescriptor descriptor)
    {
        if (!_sessionCache.TryGetValue(descriptor.Id, out UiExperienceInstance? instance))
        {
            instance = descriptor.CreateInstance();
            try
            {
                _sessionCache.Add(descriptor.Id, instance);
            }
            catch (Exception error)
            {
                throw instance.DisposeAfterFailure(
                    error,
                    $"Cached Experience '{descriptor.Id}' could not be retained and its owner failed to dispose.");
            }
        }
        return instance.Experience;
    }

    private static UiExperienceDefinition Transient(UiExperienceDescriptor descriptor)
    {
        UiExperienceInstance instance = descriptor.CreateInstance();
        if (!instance.HasOwner) return instance.Experience;
        var error = new InvalidOperationException(
            $"Transient Experience '{descriptor.Id}' cannot attach a session-owned lifecycle.");
        throw instance.DisposeAfterFailure(
            error,
            $"Transient Experience '{descriptor.Id}' attached an invalid owner that failed to dispose.");
    }

    private void EnsureSessionActive()
    {
        if (_sessionDisposed) throw new ObjectDisposedException(nameof(UiExperienceActivator));
    }
}
