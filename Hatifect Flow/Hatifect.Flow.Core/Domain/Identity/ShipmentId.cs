using System;

namespace Hatifect.Flow.Domain.Identity;

internal readonly record struct ShipmentId
{
    public Guid Value { get; }
    public ShipmentId(Guid value) => Value = IdentityValue.Require(value);
    public static ShipmentId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}
