using System;

namespace Hatifect.Flow.Domain.Identity;

internal readonly record struct ParcelId
{
    public Guid Value { get; }
    public ParcelId(Guid value) => Value = IdentityValue.Require(value);
    public static ParcelId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}
