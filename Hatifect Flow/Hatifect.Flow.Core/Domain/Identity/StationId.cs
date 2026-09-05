using System;

namespace Hatifect.Flow.Domain.Identity;

internal readonly record struct StationId
{
    public Guid Value { get; }
    public StationId(Guid value) => Value = IdentityValue.Require(value);
    public static StationId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}
