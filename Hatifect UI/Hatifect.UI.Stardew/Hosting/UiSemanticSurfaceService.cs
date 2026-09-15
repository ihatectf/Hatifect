using Hatifect.UI.Experience;
using Hatifect.UI.Semantics;
using StardewModdingAPI;

namespace Hatifect.UI.Stardew;

/// <summary>
/// Public-contract adapter whose implementation remains entirely inside the Stardew host assembly.
/// Consumers receive only opaque lifecycle handles; every Scene/platform object stays private.
/// </summary>
internal sealed class UiSemanticSurfaceService : IUiSemanticHostApi, IUiSemanticSurfaceAutomation,
    IUiSemanticSurfaceRevealAutomationApi, IUiSemanticSurfaceObservation, IUiSemanticSurfaceActionAutomation,
    IUiSemanticSurfaceRevealAutomation
{
    private readonly IModHelper _helper;

    internal UiSemanticSurfaceService(IModHelper helper)
        => _helper = helper ?? throw new ArgumentNullException(nameof(helper));

    public int ApiVersion => 1;
    public IUiSemanticSurfaceAutomation Automation => this;
    public IUiSemanticSurfaceObservation Observation => this;
    public IUiSemanticSurfaceActionAutomation ActionAutomation => this;
    public IUiSemanticSurfaceRevealAutomation RevealAutomation => this;
    public bool IsEnabled => UiAutomatedAcceptanceScenarioRegistry.IsRegistrationEnabled;

    public IUiSemanticSurfaceSession CreateSurface(
        UiExperienceDefinition experience, UiSemanticHostKind kind, UiSemanticSurfaceOptions options)
        => new UiStandaloneSemanticSurfaceSession(this, _helper, experience, kind, options);

    public IUiSemanticTerminalSession CreateTerminal(
        UiSemanticTerminalDefinition terminal, UiSemanticSurfaceOptions options)
        => new UiTerminalSemanticSurfaceSession(_helper, terminal, options);

    public IUiSemanticSurfaceSession CreateActiveMenuOverlay(
        UiExperienceDefinition experience,
        UiSemanticSurfaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(experience);
        ArgumentNullException.ThrowIfNull(options);
        if (experience.Id != options.Id)
        {
            throw new ArgumentException(
                $"Surface ID '{options.Id}' must match Experience ID '{experience.Id}'.",
                nameof(options));
        }
        return new UiActiveMenuSemanticSurfaceSession(this, _helper, experience, options);
    }

    public void Register(UiAutomatedAcceptanceScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        if (!IsEnabled) return;

        UiAutomatedAcceptanceScenarioRegistry.Register(
            new UiAutomatedAcceptanceScenarioDescriptor(
                scenario.Id,
                scenario.Order,
                scenario.RequiresWorld,
                scenario.Checks,
                context => scenario.Execute(new AutomatedAcceptanceContextAdapter(context)),
                scenario.IncludeInAggregate,
                scenario.AfterReturnedToTitle == null
                    ? null
                    : context => scenario.AfterReturnedToTitle(new AutomatedAcceptanceContextAdapter(context))));
    }

    public void Cancel(IUiSemanticSurfaceSession session, UiSemanticSurfaceCancelInput input)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!Enum.IsDefined(typeof(UiSemanticSurfaceCancelInput), input))
            throw new ArgumentOutOfRangeException(nameof(input));
        if (!IsEnabled)
        {
            throw new InvalidOperationException(
                "Semantic surface automation is available only in the exact automated TestHarness environment.");
        }
        if (session is UiStandaloneSemanticSurfaceSession window && window.IsOwnedBy(this))
        {
            window.CancelForAutomatedAcceptance(input);
            return;
        }
        if (session is not UiActiveMenuSemanticSurfaceSession owned || !owned.IsOwnedBy(this))
            throw new ArgumentException("The semantic surface session belongs to another UI API instance.", nameof(session));
        owned.CancelForAutomatedAcceptance(input);
    }

    public bool Activate(IUiSemanticSurfaceSession session, UiSymbolId action)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!action.IsValid) throw new ArgumentException("A valid action ID is required.", nameof(action));
        if (!IsEnabled)
            throw new InvalidOperationException("Surface action input is available only in the exact automated TestHarness environment.");
        if (session is UiStandaloneSemanticSurfaceSession window && window.IsOwnedBy(this))
            return window.ActivateForAutomatedAcceptance(action);
        if (session is not UiActiveMenuSemanticSurfaceSession owned || !owned.IsOwnedBy(this))
            throw new ArgumentException("The active-menu surface session belongs to another UI API instance.", nameof(session));
        return owned.ActivateForAutomatedAcceptance(action);
    }

    public bool Reveal(IUiSemanticSurfaceSession session, UiSymbolId semantic)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!semantic.IsValid) throw new ArgumentException("A valid semantic ID is required.", nameof(semantic));
        if (!IsEnabled)
            throw new InvalidOperationException("Surface reveal input is available only in the exact automated TestHarness environment.");
        if (session is UiStandaloneSemanticSurfaceSession window && window.IsOwnedBy(this))
            return window.RevealForAutomatedAcceptance(semantic);
        if (session is not UiActiveMenuSemanticSurfaceSession owned || !owned.IsOwnedBy(this))
            throw new ArgumentException("The active-menu surface session belongs to another UI API instance.", nameof(session));
        return owned.RevealForAutomatedAcceptance(semantic);
    }

    public UiSemanticSurfaceSnapshot Capture(IUiSemanticSurfaceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!IsEnabled)
            throw new InvalidOperationException("Surface observation is available only in the exact automated TestHarness environment.");
        if (session is UiStandaloneSemanticSurfaceSession window && window.IsOwnedBy(this))
            return window.CaptureForAutomatedAcceptance();
        if (session is not UiActiveMenuSemanticSurfaceSession owned || !owned.IsOwnedBy(this))
            throw new ArgumentException("The active-menu surface session belongs to another UI API instance.", nameof(session));
        return owned.CaptureForAutomatedAcceptance();
    }

    private sealed class AutomatedAcceptanceContextAdapter : IUiAutomatedAcceptanceContext
    {
        private readonly UiAutomatedAcceptanceScenarioContext _context;

        internal AutomatedAcceptanceContextAdapter(UiAutomatedAcceptanceScenarioContext context)
            => _context = context ?? throw new ArgumentNullException(nameof(context));

        public void Record(string checkId, bool passed, string note)
            => _context.Record(checkId, passed, note);
    }
}
