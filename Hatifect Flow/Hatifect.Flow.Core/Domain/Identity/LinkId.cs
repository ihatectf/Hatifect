using System;

namespace Hatifect.Flow.Domain.Identity;

internal readonly record struct LinkId
{
    public Guid Value { get; }
    public LinkId(Guid value) => Value = IdentityValue.Require(value);
    public static LinkId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString("D");
}
