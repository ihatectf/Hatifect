using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Registration;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Visual;
using Hatifect.UI.Runtime.Visual.Theming;
using Hatifect.UI.Stardew.Semantic;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using RuntimeRect = Hatifect.UI.Runtime.Layout.UiRect;
using RuntimeSymbolId = Hatifect.UI.UiSymbolId;
using RuntimeTypography = Hatifect.UI.Runtime.Visual.UiTypography;

namespace Hatifect.UI.Stardew;

/// <summary>
/// Public-contract adapter whose implementation remains entirely inside the Stardew host assembly.
/// Consumers receive only opaque lifecycle handles; every Scene/platform object stays private.
/// </summary>
internal sealed class UiSemanticSurfaceService : IUiSemanticSurfaceApi, IUiSemanticSurfaceAutomation
{
    private readonly IModHelper _helper;

    internal UiSemanticSurfaceService(IModHelper helper)
        => _helper = helper ?? throw new ArgumentNullException(nameof(helper));

    public int ApiVersion => 1;
    public IUiSemanticSurfaceAutomation Automation => this;
    public bool IsEnabled => UiAutomatedAcceptanceScenarioRegistry.IsRegistrationEnabled;

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
        if (session is not UiActiveMenuSemanticSurfaceSession owned || !owned.IsOwnedBy(this))
            throw new ArgumentException("The semantic surface session belongs to another UI API instance.", nameof(session));
        owned.CancelForAutomatedAcceptance(input);
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

/// <summary>One active-menu surface owner with retryable, fail-closed teardown.</summary>
internal sealed class UiActiveMenuSemanticSurfaceSession : IUiSemanticSurfaceSession
{
    private readonly UiSemanticSurfaceService _owner;
    private readonly UiExperienceDefinition _experience;
    private readonly UiRegistrySnapshot _registry;
    private readonly UiSceneComposer _composer;
    private UiSemanticSurfaceOptions _options;
    private UiSemanticStardewRuntime? _runtime;
    private UiSemanticStardewOverlaySession? _overlay;
    private UiInvocationResult _invocation;
    private UiInteractionSnapshot? _interaction;
    private UiPresentationProfile _profile;
    private string _locale;
    private bool _shown;
    private bool _closedRaised;
    private bool _disposeRequested;
    private bool _disposing;
    private bool _disposed;

    internal UiActiveMenuSemanticSurfaceSession(
        UiSemanticSurfaceService owner,
        IModHelper helper,
        UiExperienceDefinition experience,
        UiSemanticSurfaceOptions options)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        ArgumentNullException.ThrowIfNull(helper);
        _experience = experience ?? throw new ArgumentNullException(nameof(experience));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        UiHostPolicy policy = UiProvisionalHostPolicies.OverlayCentered(options.CloseOnOutsidePointer);
        _registry = new UiRegistryBuilder()
            .Window(options.Id, experience.DisplayName, () => experience, policy)
            .Freeze();
        RuntimeRect viewport = UiSemanticStardewMenu.CaptureViewport();
        _profile = ResolveProfile(viewport);
        _locale = ResolveLocale();
        _invocation = new UiInvocationService(_registry).Invoke(options.Id, _profile);
        _composer = new UiSceneComposer(UiSemanticStardewTheme.Default, _registry);

        try
        {
            _runtime = new UiSemanticStardewRuntime(
                Game1.graphics.GraphicsDevice,
                ResolveFont,
                ResolveTexture);
            UiScene scene = ComposeScene();
            UiSemanticStardewOverlaySession overlay = _runtime.CreateOverlay(
                helper,
                scene,
                UiSemanticStardewOverlayRenderLayer.ActiveMenu,
                ComposeInteraction,
                OnOverlayClosed);
            _overlay = overlay;
            overlay.BackgroundDimmingOpacity = ResolveBackgroundDimmingOpacity(options);
            overlay.CloseRequestHandler = static () => true;
            overlay.Rendered += OnRendered;
        }
        catch (Exception activationFailure)
        {
            var failures = new List<Exception> { activationFailure };
            DisposeOwnedResources(failures);
            if (failures.Count > 1)
            {
                throw new AggregateException(
                    "Semantic surface activation and staged cleanup failed.",
                    failures);
            }
            throw;
        }
    }

    public bool Visible => !_disposeRequested && _overlay?.Visible == true;
    public event Action? Rendered;
    public event Action? Closed;

    public void Show()
    {
        ThrowIfUnavailable();
        _overlay!.Show();
        _shown = true;
    }

    public void Hide()
    {
        if (_disposed || _disposeRequested) return;
        if (_closedRaised) return;
        UiSemanticStardewOverlaySession? overlay = _overlay;
        if (overlay?.Visible == true)
            overlay.Hide();
        else
            OnOverlayClosed();
    }

    public void Configure(UiSemanticSurfaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfUnavailable();
        if (options.Id != _options.Id)
            throw new ArgumentException("A live semantic surface cannot change identity.", nameof(options));
        if (options.CloseOnOutsidePointer != _options.CloseOnOutsidePointer)
        {
            throw new InvalidOperationException(
                "Dismissal policy is fixed for one semantic surface session; create a new session to change it.");
        }

        _options = options;
        _overlay!.BackgroundDimmingOpacity = ResolveBackgroundDimmingOpacity(options);
    }

    public void Refresh()
    {
        ThrowIfUnavailable();
        UiSemanticStardewOverlaySession overlay = _overlay!;
        if (_shown && !overlay.SynchronizeMenuContext()) return;
        SynchronizeState();
        overlay.Update(ComposeScene());
    }

    public void Synchronize()
    {
        ThrowIfUnavailable();
        UiSemanticStardewOverlaySession overlay = _overlay!;
        if (_shown && !overlay.SynchronizeMenuContext()) return;
        if (SynchronizeState())
            overlay.Update(ComposeScene());
    }

    public void Dispose()
    {
        if (_disposed || _disposing) return;
        _disposeRequested = true;
        _disposing = true;
        var failures = new List<Exception>();
        try
        {
            DisposeOwnedResources(failures);
            if (_overlay == null && _runtime == null)
                _disposed = true;
        }
        finally
        {
            _disposing = false;
        }
        if (failures.Count > 0)
            throw new AggregateException("Semantic surface resources failed to dispose.", failures);
    }

    internal void CancelForAutomatedAcceptance(UiSemanticSurfaceCancelInput input)
    {
        ThrowIfUnavailable();
        _overlay!.CancelForAutomatedAcceptance(input);
    }

    internal bool IsOwnedBy(UiSemanticSurfaceService owner)
    {
        return ReferenceEquals(_owner, owner);
    }

    private UiScene ComposeInteraction(UiInteractionSnapshot interaction)
    {
        _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        return ComposeScene();
    }

    private UiScene ComposeScene()
        => _composer.Compose(_invocation, interaction: _interaction, locale: _locale);

    private bool SynchronizeState()
    {
        RuntimeRect viewport = UiSemanticStardewMenu.CaptureViewport();
        UiPresentationProfile profile = ResolveProfile(viewport);
        string locale = ResolveLocale();
        bool changed = false;
        if (_profile.Id != profile.Id)
        {
            _profile = profile;
            _invocation = new UiInvocationService(_registry).Invoke(_options.Id, profile);
            changed = true;
        }
        if (!string.Equals(_locale, locale, StringComparison.Ordinal))
        {
            _locale = locale;
            changed = true;
        }
        return changed;
    }

    private void OnRendered() => Rendered?.Invoke();

    private void OnOverlayClosed()
    {
        if (_closedRaised) return;
        _closedRaised = true;
        Closed?.Invoke();
    }

    private void DisposeOwnedResources(ICollection<Exception> failures)
    {
        UiSemanticStardewOverlaySession? overlay = _overlay;
        if (overlay != null)
        {
            overlay.Rendered -= OnRendered;
            try
            {
                overlay.Dispose();
                if (ReferenceEquals(_overlay, overlay)) _overlay = null;
            }
            catch (Exception error)
            {
                failures.Add(error);
            }
        }

        UiSemanticStardewRuntime? runtime = _runtime;
        if (_overlay == null && runtime != null)
        {
            try
            {
                runtime.Dispose();
                if (ReferenceEquals(_runtime, runtime)) _runtime = null;
            }
            catch (Exception error)
            {
                failures.Add(error);
            }
        }
    }

    private static UiPresentationProfile ResolveProfile(RuntimeRect viewport)
    {
        if (Game1.options.gamepadControls) return UiPresentationProfiles.Controller;
        if (viewport.Width < 720) return UiPresentationProfiles.Compact;
        if (viewport.Width < 1100) return UiPresentationProfiles.Medium;
        return UiPresentationProfiles.Wide;
    }

    private static string ResolveLocale()
        => LocalizedContentManager.CurrentLanguageCode.ToString();

    private static SpriteFont ResolveFont(RuntimeTypography typography)
        => string.Equals(typography.Family, "Display", StringComparison.Ordinal)
            ? Game1.dialogueFont
            : Game1.smallFont;

    private static Texture2D ResolveTexture(RuntimeSymbolId texture)
        => throw new InvalidOperationException(
            $"The semantic surface requested unregistered texture '{texture}'.");

    private static float ResolveBackgroundDimmingOpacity(UiSemanticSurfaceOptions options)
        => options.DimUnderlyingMenu ? 0.42f : 0f;

    private void ThrowIfUnavailable()
    {
        if (_disposed || _disposeRequested)
            throw new ObjectDisposedException(nameof(UiActiveMenuSemanticSurfaceSession));
        if (_closedRaised)
        {
            throw new InvalidOperationException(
                "A dismissed semantic surface session can only be disposed; create a new session.");
        }
    }
}
