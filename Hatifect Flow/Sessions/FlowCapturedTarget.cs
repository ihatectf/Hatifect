using System;
using StardewValley.Objects;

namespace Hatifect.Flow.Sessions;

// Kept only by its host-side peer owner. No physical reference crosses the wire.
internal sealed record FlowCapturedTarget(Guid Token, StationBinding Binding, Chest Chest)
{
    internal string Description => $"{Binding.Location} ({Binding.X}, {Binding.Y})";
}
