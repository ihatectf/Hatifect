namespace Hatifect.UI.Experience;

/// <summary>Framework-owned ways to present an experience independently of a native menu.</summary>
public enum UiSemanticHostKind
{
    Window,
    Modal,
    Fullscreen,
    Hud
}

/// <summary>
/// Optional additive host facet. Request this interface from the UI mod when standalone surfaces
/// are needed. The original overlay API remains supported without additional consumer members.
/// Calls and semantic source notifications belong to the game's UI thread.
/// </summary>
public interface IUiSemanticHostApi : IUiSemanticSurfaceApi
{
    IUiSemanticSurfaceSession CreateSurface(
        UiExperienceDefinition experience,
        UiSemanticHostKind kind,
        UiSemanticSurfaceOptions options);

    IUiSemanticTerminalSession CreateTerminal(
        UiSemanticTerminalDefinition terminal,
        UiSemanticSurfaceOptions options);
}

public interface IUiSemanticTerminalSession : IUiSemanticSurfaceSession
{
    UiSymbolId CurrentSection { get; }
    void OpenSection(UiSymbolId section);
}

/// <summary>Semantic section content. The caller retains ownership of its sources.</summary>
public sealed class UiSemanticTerminalSection
{
    public UiSemanticTerminalSection(UiExperienceDefinition experience, string group = "General", int order = 0, UiSymbolId? icon = null)
    {
        Experience = experience ?? throw new ArgumentNullException(nameof(experience));
        if (string.IsNullOrWhiteSpace(group)) throw new ArgumentException("A section group is required.", nameof(group));
        if (icon is { IsValid: false }) throw new ArgumentException("A stable icon ID is required.", nameof(icon));
        Group = group;
        Order = order;
        Icon = icon;
    }

    public UiExperienceDefinition Experience { get; }
    public string Group { get; }
    public int Order { get; }
    public UiSymbolId? Icon { get; }
}

public sealed class UiSemanticTerminalDefinition
{
    public UiSemanticTerminalDefinition(
        UiSymbolId id,
        IEnumerable<UiSemanticTerminalSection> sections,
        UiSymbolId? initialSection = null)
    {
        if (!id.IsValid) throw new ArgumentException("A stable Terminal ID is required.", nameof(id));
        ArgumentNullException.ThrowIfNull(sections);
        UiSemanticTerminalSection[] copy = sections.ToArray();
        if (copy.Length == 0) throw new ArgumentException("A Terminal requires sections.", nameof(sections));
        var ids = new HashSet<UiSymbolId>();
        foreach (UiSemanticTerminalSection section in copy)
        {
            ArgumentNullException.ThrowIfNull(section);
            if (!ids.Add(section.Experience.Id)) throw new ArgumentException("Terminal section IDs must be unique.", nameof(sections));
        }
        if (initialSection is { } initial && !ids.Contains(initial))
            throw new ArgumentException("The initial section must belong to this Terminal.", nameof(initialSection));
        Id = id;
        Sections = Array.AsReadOnly(copy);
        InitialSection = initialSection ?? copy.OrderBy(section => section.Group, StringComparer.Ordinal)
            .ThenBy(section => section.Order).ThenBy(section => section.Experience.Id.ToString(), StringComparer.Ordinal)
            .First().Experience.Id;
    }

    public UiSymbolId Id { get; }
    public IReadOnlyList<UiSemanticTerminalSection> Sections { get; }
    public UiSymbolId InitialSection { get; }
}
