namespace Hatifect.UI.Experience;

public sealed record UiCapability(UiSymbolId Id, string Name);

public static class UiCapabilities
{
    public static readonly UiCapability Browse = Create("Browse");
    public static readonly UiCapability Search = Create("Search");
    public static readonly UiCapability Filter = Create("Filter");
    public static readonly UiCapability Select = Create("Select");
    public static readonly UiCapability Inspect = Create("Inspect");
    public static readonly UiCapability Configure = Create("Configure");
    public static readonly UiCapability Monitor = Create("Monitor");
    public static readonly UiCapability Navigate = Create("Navigate");
    public static readonly UiCapability Actions = Create("Actions");

    private static UiCapability Create(string name)
        => new(new UiSymbolId("Hatifect.UI", $"capability/{name}"), name);
}
