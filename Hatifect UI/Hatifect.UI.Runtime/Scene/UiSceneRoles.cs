namespace Hatifect.UI.Runtime.Scene;

internal static class UiSceneRoles
{
    public static readonly UiSymbolId Host = Role("Host");
    public static readonly UiSymbolId Slot = Role("Slot");
    public static readonly UiSymbolId Collection = Role("Collection");
    public static readonly UiSymbolId Text = Role("Text");
    public static readonly UiSymbolId Button = Role("Button");
    public static readonly UiSymbolId TextInput = Role("TextInput");
    public static readonly UiSymbolId Inspector = Role("Inspector");
    public static readonly UiSymbolId Form = Role("Form");
    public static readonly UiSymbolId Status = Role("Status");
    public static readonly UiSymbolId ActionBar = Role("ActionBar");

    private static UiSymbolId Role(string name) => new("Hatifect.UI", $"internal-role/{name}");
}
