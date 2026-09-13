using System;

namespace Hatifect.Flow.Domain.Identity;

internal readonly record struct CargoId
{
    public Guid Value { get; }
    public CargoId(Guid value) => Value = IdentityValue.Require(value);
    public static CargoId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}
