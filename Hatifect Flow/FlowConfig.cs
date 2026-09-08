using StardewModdingAPI.Utilities;

namespace Hatifect.Flow;

/// <summary>Player-configurable entry binding; UI owns input after the surface opens.</summary>
public sealed class FlowConfig
{
    public KeybindList OpenNetwork { get; set; } = KeybindList.Parse("K, LeftShoulder + RightShoulder");
}
