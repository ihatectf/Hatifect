using System;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Activation;
using Hatifect.UI.Runtime.Projection;
using Hatifect.UI.Runtime.Registration;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Runtime.Invocation;

public sealed record UiInvocationResult(
    UiExperienceDescriptor Descriptor,
    UiExperienceDefinition Experience,
    UiPresentationPlan Plan,
    UiSlotProjectionResult Projection);

/// <summary>Separates registration from invocation and leaves rendering to the next pipeline stage.</summary>
public sealed class UiInvocationService
{
    private readonly UiRegistrySnapshot _registry;
    private readonly UiExperienceActivator _activator;
    private readonly UiPresentationPlanner _planner;
    private readonly UiSlotProjector _projector = new();

    internal UiActivationOwnershipSnapshot CaptureActivationOwnership() => _activator.CaptureOwnership();

    public UiInvocationService(
        UiRegistrySnapshot registry,
        UiExperienceActivator? activator = null,
        UiPresentationPlanner? planner = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _activator = activator ?? new UiExperienceActivator(registry);
        _planner = planner ?? new UiPresentationPlanner();
    }

    public UiInvocationResult Invoke(
        UiSymbolId id,
        UiPresentationProfile profile,
        UiPresentationDefinition? presentation = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!_registry.TryGetExperience(id, out UiExperienceDescriptor? descriptor) || descriptor == null)
            throw new System.Collections.Generic.KeyNotFoundException($"Unknown UI Experience '{id}'.");
        UiExperienceDefinition experience = _activator.Activate(id);
        return Build(descriptor, experience, new UiHostContext(descriptor.Host.Kind, profile), presentation);
    }

    /// <summary>Plan with one captured environment while preserving the original profile-based Invoke.</summary>
    public UiInvocationResult InvokeInEnvironment(
        UiSymbolId id,
        UiEnvironment environment,
        UiPresentationDefinition? presentation = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (!_registry.TryGetExperience(id, out UiExperienceDescriptor? descriptor) || descriptor == null)
            throw new System.Collections.Generic.KeyNotFoundException($"Unknown UI Experience '{id}'.");
        UiHostContext host = UiHostContext.InEnvironment(descriptor.Host.Kind, environment);
        UiExperienceDefinition experience = _activator.Activate(id);
        return Build(descriptor, experience, host, presentation);
    }

    internal UiInvocationResult InvokeKnownAvailable(
        UiExperienceDescriptor descriptor,
        UiPresentationProfile profile,
        UiPresentationDefinition? presentation = null,
        UiEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(profile);
        UiHostContext host = CaptureHost(descriptor, profile, environment);
        UiExperienceDefinition experience = _activator.ActivateKnownAvailable(descriptor);
        return Build(descriptor, experience, host, presentation);
    }

    internal UiInvocationResult ReplanKnownAvailable(
        UiExperienceDescriptor descriptor,
        UiExperienceDefinition experience,
        UiPresentationProfile profile,
        UiPresentationDefinition? presentation = null,
        UiEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(experience);
        ArgumentNullException.ThrowIfNull(profile);
        if (!_registry.TryGetExperience(descriptor.Id, out UiExperienceDescriptor? registered) ||
            !ReferenceEquals(registered, descriptor) || experience.Id != descriptor.Id)
            throw new InvalidOperationException("The active Experience must belong to this registry descriptor.");
        return Build(descriptor, experience, CaptureHost(descriptor, profile, environment), presentation);
    }

    private UiInvocationResult Build(
        UiExperienceDescriptor descriptor,
        UiExperienceDefinition experience,
        UiHostContext host,
        UiPresentationDefinition? presentation)
    {
        UiPresentationPlan plan = _planner.Plan(experience, host, presentation);
        return new UiInvocationResult(descriptor, experience, plan, _projector.Project(plan));
    }

    private static UiHostContext CaptureHost(UiExperienceDescriptor descriptor,
        UiPresentationProfile profile, UiEnvironment? environment)
    {
        if (environment == null) return new UiHostContext(descriptor.Host.Kind, profile);
        UiHostContext host = UiHostContext.InEnvironment(descriptor.Host.Kind, environment);
        if (host.Profile != profile.Id)
            throw new ArgumentException("The selected profile contradicts the captured environment.", nameof(profile));
        return host;
    }
}
