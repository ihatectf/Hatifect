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
        if (!_registry.TryGetExperience(id, out UiExperienceDescriptor? descriptor) || descriptor == null)
            throw new System.Collections.Generic.KeyNotFoundException($"Unknown UI Experience '{id}'.");
        UiExperienceDefinition experience = _activator.Activate(id);
        return Build(descriptor, experience, profile, presentation);
    }

    internal UiInvocationResult InvokeKnownAvailable(
        UiExperienceDescriptor descriptor,
        UiPresentationProfile profile,
        UiPresentationDefinition? presentation = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(profile);
        UiExperienceDefinition experience = _activator.ActivateKnownAvailable(descriptor);
        return Build(descriptor, experience, profile, presentation);
    }

    private UiInvocationResult Build(
        UiExperienceDescriptor descriptor,
        UiExperienceDefinition experience,
        UiPresentationProfile profile,
        UiPresentationDefinition? presentation)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var host = new UiHostContext(descriptor.Host.Kind, profile);
        UiPresentationPlan plan = _planner.Plan(experience, host, presentation);
        return new UiInvocationResult(descriptor, experience, plan, _projector.Project(plan));
    }
}
