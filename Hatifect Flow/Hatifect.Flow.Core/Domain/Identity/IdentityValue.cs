using System;

namespace Hatifect.Flow.Domain.Identity;

internal static class IdentityValue
{
    internal static Guid Require(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A durable identity must not be empty.", nameof(value));
        }
        return value;
    }
}
