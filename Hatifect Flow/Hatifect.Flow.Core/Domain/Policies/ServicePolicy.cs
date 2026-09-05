using System;

namespace Hatifect.Flow.Domain.Policies;

internal sealed record ServicePolicy
{
    public ServiceClass ServiceClass { get; }
    public DeliveryGuarantee Guarantee { get; }

    public ServicePolicy(ServiceClass serviceClass, DeliveryGuarantee guarantee)
    {
        if (!Enum.IsDefined(typeof(ServiceClass), serviceClass)
            || !Enum.IsDefined(typeof(DeliveryGuarantee), guarantee))
        {
            throw new ArgumentOutOfRangeException(nameof(serviceClass), "Unknown service policy value.");
        }
        ServiceClass = serviceClass;
        Guarantee = guarantee;
    }
}
