using System;

namespace Hatifect.Flow.Domain.Identity;

internal readonly record struct NetworkId
{
    public Guid Value { get; }
    public NetworkId(Guid value) => Value = IdentityValue.Require(value);
    public static NetworkId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}
