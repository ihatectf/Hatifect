using Hatifect.UI.Runtime.Visual.Theming;

namespace Hatifect.UI.Stardew.Semantic;

/// <summary>The implemented semantic host preset shared by surfaces and acceptance evidence.</summary>
internal static class UiSemanticStardewTheme
{
    internal static UiTheme Default { get; } = UiThemePresets.Dark();
    internal static string Id { get; } = Default.Id.ToString();
}
