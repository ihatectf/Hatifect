using System;

namespace Hatifect.Flow.Domain.Shipments;

internal sealed record CargoManifest
{
    public string ItemKey { get; }
    public int Quantity { get; }

    public CargoManifest(string itemKey, int quantity)
    {
        if (string.IsNullOrWhiteSpace(itemKey) || itemKey.Length > 256)
        {
            throw new ArgumentException("A manifest needs an item key of at most 256 characters.", nameof(itemKey));
        }
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }
        ItemKey = itemKey;
        Quantity = quantity;
    }
}
