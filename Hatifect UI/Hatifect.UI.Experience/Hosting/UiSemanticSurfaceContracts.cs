namespace Hatifect.UI.Experience;

/// <summary>
/// Immutable framework-owned presentation options for a semantic surface hosted above the game's
/// current native menu. Geometry, input routing, rendering, and platform objects remain private to
/// Hatifect UI.
/// </summary>
public sealed record UiSemanticSurfaceOptions
{
    /// <summary>Create options for one active-menu semantic overlay.</summary>
    public UiSemanticSurfaceOptions(
        UiSymbolId id,
        bool closeOnOutsidePointer = true,
        bool dimUnderlyingMenu = false)
    {
        if (!id.IsValid)
            throw new ArgumentException("A stable semantic surface ID is required.", nameof(id));

        Id = id;
        CloseOnOutsidePointer = closeOnOutsidePointer;
        DimUnderlyingMenu = dimUnderlyingMenu;
    }

    /// <summary>Stable identity used for registration, diagnostics, and lifecycle ownership.</summary>
    public UiSymbolId Id { get; }

    /// <summary>Whether an unconsumed pointer press outside the surface requests dismissal.</summary>
    public bool CloseOnOutsidePointer { get; }

    /// <summary>Whether the active native menu should receive framework-themed dimming.</summary>
    public bool DimUnderlyingMenu { get; }
}

/// <summary>
/// Opaque single-use lifecycle handle for one framework-owned semantic surface. Consumers can update
/// semantic state and request recomposition, but cannot access UI scenes, platform input, layout, or
/// rendering. Dismissal is terminal: after <see cref="Closed"/> is raised, the handle can only be
/// disposed and a new session must be created for a later presentation.
/// </summary>
public interface IUiSemanticSurfaceSession : IDisposable
{
    /// <summary>Whether the surface currently owns a visible UI host.</summary>
    bool Visible { get; }

    /// <summary>Raised after the framework completes an owned render pass.</summary>
    event Action? Rendered;

    /// <summary>Raised once after the surface is dismissed or its native menu context retires.</summary>
    event Action? Closed;

    /// <summary>
    /// Show the surface above the current native menu. This is idempotent while visible and is
    /// rejected after terminal dismissal.
    /// </summary>
    void Show();

    /// <summary>
    /// Terminally dismiss the surface and synchronously release its UI input/render ownership.
    /// </summary>
    void Hide();

    /// <summary>
    /// Apply presentation-only option changes without replacing semantic state. Surface identity
    /// and outside-pointer dismissal policy are immutable for one session.
    /// </summary>
    void Configure(UiSemanticSurfaceOptions options);

    /// <summary>Recompose from the current semantic state, locale, profile, and viewport.</summary>
    void Refresh();

    /// <summary>
    /// Apply framework-owned locale/profile/viewport changes when they differ, without forcing a
    /// semantic recompose on an otherwise unchanged frame. If the exact native menu identity has
    /// changed, this terminally retires the surface without closing or replacing the new native menu.
    /// </summary>
    void Synchronize();
}

/// <summary>
/// Narrow public UI host API used by separately packaged semantic consumers. The returned handle is
/// the only supported production bridge into the UI runtime.
/// </summary>
public interface IUiSemanticSurfaceApi
{
    /// <summary>Current additive contract version.</summary>
    int ApiVersion { get; }

    /// <summary>TestHarness-only registration and normalized-input surface.</summary>
    IUiSemanticSurfaceAutomation Automation { get; }

    /// <summary>
    /// Create an overlay bound to the game's exact active native menu. Creation does not show the
    /// surface; callers own the returned session and must dispose it.
    /// </summary>
    IUiSemanticSurfaceSession CreateActiveMenuOverlay(
        UiExperienceDefinition experience,
        UiSemanticSurfaceOptions options);
}

/// <summary>Normalized cancellation inputs supported by exact automated acceptance.</summary>
public enum UiSemanticSurfaceCancelInput
{
    Escape = 0,
    ControllerBack = 1
}

/// <summary>Recorder exposed only while one registered automated scenario is executing.</summary>
public interface IUiAutomatedAcceptanceContext
{
    /// <summary>Record one declared check result.</summary>
    void Record(string checkId, bool passed, string note);
}

/// <summary>Immutable product-owned automated acceptance contribution.</summary>
public sealed class UiAutomatedAcceptanceScenario
{
    private readonly IReadOnlyList<string> _checks;

    /// <summary>Create one synchronous automated acceptance contribution.</summary>
    public UiAutomatedAcceptanceScenario(
        string id,
        int order,
        bool requiresWorld,
        IEnumerable<string> checks,
        Action<IUiAutomatedAcceptanceContext> execute,
        bool includeInAggregate = true)
    {
        if (string.IsNullOrWhiteSpace(id)
            || !string.Equals(id, id.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Scenario IDs must be nonempty and trimmed.", nameof(id));
        }
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(execute);

        string[] copy = checks.ToArray();
        if (copy.Length == 0)
            throw new ArgumentException("A scenario must declare at least one check.", nameof(checks));
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (string check in copy)
        {
            if (string.IsNullOrWhiteSpace(check)
                || !string.Equals(check, check.Trim(), StringComparison.Ordinal))
            {
                throw new ArgumentException("Check IDs must be nonempty and trimmed.", nameof(checks));
            }
            if (!unique.Add(check))
                throw new ArgumentException($"Check '{check}' is declared more than once.", nameof(checks));
        }

        Id = id;
        Order = order;
        RequiresWorld = requiresWorld;
        _checks = Array.AsReadOnly(copy);
        Execute = execute;
        IncludeInAggregate = includeInAggregate;
    }

    public string Id { get; }
    public int Order { get; }
    public bool RequiresWorld { get; }
    public IReadOnlyList<string> Checks => _checks;
    public Action<IUiAutomatedAcceptanceContext> Execute { get; }
    public bool IncludeInAggregate { get; }

    /// <summary>
    /// Optional verification phase run on the first update after the real return-to-title event
    /// has finished for every mod. This phase is available only through <see cref="ForReturnToTitle"/>.
    /// </summary>
    public Action<IUiAutomatedAcceptanceContext>? AfterReturnedToTitle { get; private init; }

    /// <summary>
    /// Create an isolated world-lifecycle scenario. The framework loads the isolated save, executes
    /// preparation, requests the game's real return to title, and then executes verification.
    /// Each phase has its own recorder lifetime. These scenarios cannot join the aggregate because
    /// they unload its world. Neither callback should request or simulate return to title itself.
    /// </summary>
    public static UiAutomatedAcceptanceScenario ForReturnToTitle(
        string id,
        int order,
        IEnumerable<string> checks,
        Action<IUiAutomatedAcceptanceContext> beforeReturnToTitle,
        Action<IUiAutomatedAcceptanceContext> afterReturnedToTitle)
    {
        ArgumentNullException.ThrowIfNull(afterReturnedToTitle);
        return new UiAutomatedAcceptanceScenario(
            id, order, requiresWorld: true, checks, beforeReturnToTitle, includeInAggregate: false)
        {
            AfterReturnedToTitle = afterReturnedToTitle
        };
    }
}

/// <summary>
/// Exact-harness-only facet. Production environments report <see cref="IsEnabled"/> as false and
/// retain no contributed delegates.
/// </summary>
public interface IUiSemanticSurfaceAutomation
{
    bool IsEnabled { get; }
    void Register(UiAutomatedAcceptanceScenario scenario);
    void Cancel(IUiSemanticSurfaceSession session, UiSemanticSurfaceCancelInput input);
}
