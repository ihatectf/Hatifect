using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Registration;

namespace Hatifect.UI.Runtime.Hosting;

/// <summary>One translation from consumer host intent to framework presentation policy.</summary>
internal static class UiSemanticHostProjection
{
    internal static UiHostPolicy Policy(UiSemanticHostKind kind) => kind switch
    {
        UiSemanticHostKind.Window => UiProvisionalHostPolicies.SemanticWindow,
        UiSemanticHostKind.Modal => UiHostPolicies.Modal,
        UiSemanticHostKind.Fullscreen => UiHostPolicies.Fullscreen,
        UiSemanticHostKind.Hud => UiProvisionalHostPolicies.OverlayTopRight,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static UiRegistrySnapshot Register(UiExperienceDefinition experience, UiSemanticHostKind kind)
    {
        ArgumentNullException.ThrowIfNull(experience);
        return new UiRegistryBuilder().Window(experience.Id, experience.DisplayName, () => experience, Policy(kind)).Freeze();
    }

    internal static UiRegistrySnapshot Register(UiSemanticTerminalDefinition terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        var builder = new UiRegistryBuilder();
        foreach (UiSemanticTerminalSection section in terminal.Sections)
            builder.TerminalSection(section.Experience.Id, section.Experience.DisplayName,
                () => section.Experience, section.Group, section.Order, section.Icon);
        return builder.Freeze();
    }
}
