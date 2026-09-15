using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.HotReload;
using Hatifect.UI.Runtime.Terminal;
using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Registration;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Semantics;
using Hatifect.UI.Runtime.Visual;
using Hatifect.UI.Runtime.Visual.Theming;
using Hatifect.UI.Runtime.Diagnostics;
using Hatifect.UI.Stardew.Semantic;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using RuntimeRect = Hatifect.UI.Runtime.Layout.UiRect;
using RuntimeSymbolId = Hatifect.UI.UiSymbolId;
using RuntimeTypography = Hatifect.UI.Runtime.Visual.UiTypography;

namespace Hatifect.UI.Stardew;

/// <summary>One active-menu surface owner with retryable, fail-closed teardown.</summary>
internal sealed class UiActiveMenuSemanticSurfaceSession : IUiSemanticAppearanceSession, IUiSemanticReloadSession
{
    private readonly int _screen = Context.ScreenId;
    private readonly UiSemanticSurfaceService _owner;
    private readonly UiSurfaceObservationState? _observation;
    private readonly UiExperienceDefinition _experience;
    private readonly UiSemanticSourceChangeBinding _sourceChanges;
    private readonly UiRegistrySnapshot _registry;
    private UiSceneComposer _composer;
    private readonly UiSemanticTextureCatalog<Texture2D> _textures = UiSemanticTextureResources.Create();
    private UiSemanticTheme _theme;
    private UiSemanticTheme _acceptedTheme;
    private long _configurationVersion;
    private readonly UiSemanticLiveAssets _assets;
    private readonly UiSemanticAssetWatches _watches = new();
    private readonly UiSemanticAssetWatchBinding _watchEvents;
    private UiSemanticSurfaceOptions _options;
    private UiSemanticStardewRuntime? _runtime;
    private UiSemanticStardewOverlaySession? _overlay;
    private UiInvocationResult _invocation;
    private UiInteractionSnapshot? _interaction;
    private UiEnvironment _environment;
    private bool _shown;
    private bool _closedRaised;
    private bool _retireRequested;
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
        _watchEvents = new UiSemanticAssetWatchBinding(helper.Events.GameLoop,
            () => Context.ScreenId == _screen,
            () => { if (!_retireRequested && !_closedRaised && !_disposeRequested) _watches.Poll(); });
        _experience = experience ?? throw new ArgumentNullException(nameof(experience));
        _sourceChanges = new UiSemanticSourceChangeBinding(experience.Sources.Select(source => source.Source));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _observation = owner.IsEnabled ? new UiSurfaceObservationState(options.Id) : null;

        UiHostPolicy policy = UiProvisionalHostPolicies.OverlayCentered(options.CloseOnOutsidePointer);
        _registry = new UiRegistryBuilder()
            .Window(options.Id, experience.DisplayName, () => experience, policy)
            .Freeze();
        RuntimeRect viewport = UiSemanticStardewMenu.CaptureViewport();
        _environment = UiSemanticStardewEnvironmentCapture.Capture(viewport, _theme);
        _invocation = new UiInvocationService(_registry).InvokeInEnvironment(options.Id, _environment);
        _composer = new UiSceneComposer(UiSemanticStardewTheme.Default, _registry);
        _assets = new UiSemanticLiveAssets(new[] { experience }, ApplyAssets);

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
                OnOverlayClosed,
                next => SynchronizeState(next));
            _overlay = overlay;
            overlay.Observation = _observation;
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

    public bool Visible => !_retireRequested && !_disposeRequested && _overlay?.Visible == true;
    public event Action? Rendered;
    public event Action? Closed;
    public event Action<UiSemanticReloadResult>? AssetsReloaded;

    public UiSemanticReloadResult Reload(UiSymbolId experience, string? presentation, string? visual)
    {
        ThrowIfUnavailable();
        UiSemanticReloadResult result = _assets.Reload(experience, presentation, visual);
        NotifyAssetsReloaded(result);
        return result;
    }
    public IDisposable WatchAssets(UiSymbolId experience, string? presentationPath, string? visualPath)
    {
        ThrowIfUnavailable();
        _assets.For(experience);
        IDisposable lease = _watches.Add(experience, presentationPath, visualPath,
            (presentation, visual) => Reload(experience, presentation, visual),
            error => NotifyAssetsReloaded(_assets.Failure(experience, "LUI4103", error.Message)));
        try { _watchEvents.Activate(); }
        catch (Exception activation)
        {
            try { lease.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(activation, cleanup); }
            throw;
        }
        return lease;
    }
    private void NotifyAssetsReloaded(UiSemanticReloadResult result)
    {
        if (AssetsReloaded is not { } observers) return;
        foreach (Action<UiSemanticReloadResult> observer in observers.GetInvocationList())
        {
            if (Context.ScreenId != _screen || _retireRequested || _disposed || _disposing || _disposeRequested || _closedRaised) return;
            if (_shown && !_overlay!.SynchronizeMenuContext()) return;
            observer(result);
        }
    }

    private void StopSubscriptions()
    {
        List<Exception>? failures = null;
        try { _sourceChanges.Dispose(); } catch (Exception error) { (failures ??= new()).Add(error); }
        try { StopWatches(); } catch (Exception error) { (failures ??= new()).Add(error); }
        if (failures is not null) throw new AggregateException(failures);
    }

    private void StopWatches()
    {
        List<Exception>? failures = null;
        try { _watchEvents.Dispose(); } catch (Exception error) { (failures ??= new()).Add(error); }
        try { _watches.Dispose(); } catch (Exception error) { (failures ??= new()).Add(error); }
        if (failures is not null) throw new AggregateException(failures);
    }
    private void ApplyAssets(UiSymbolId experience, UiTerminalSectionAssets assets, Action acceptAssets)
    {
        ThrowIfUnavailable();
        RuntimeRect viewport = UiSemanticStardewMenu.CaptureViewport();
        UiEnvironment environment = CaptureEnvironment(viewport);
        UiSemanticTheme theme = _theme;
        UiSceneComposer composer = ResolveComposer(theme);
        long configurationVersion = _configurationVersion;
        long sourceVersion = _sourceChanges.Version;
        UiInvocationResult? invocation = null;
        _overlay!.UpdatePrepared(ComposeReloadedScene, viewport, AcceptReloadedAssets,
            () => ValidateConfiguration(configurationVersion), renewActionGeneration: true);

        UiScene ComposeReloadedScene()
        {
            invocation = new UiInvocationService(_registry).InvokeInEnvironment(experience, environment, assets.Presentation);
            return composer.Compose(invocation, assets.Visual, _interaction, environment.Locale);
        }

        void AcceptReloadedAssets()
        {
            _invocation = invocation!;
            AcceptEnvironment(environment, theme, composer, sourceVersion);
            acceptAssets();
        }
    }

    public void Show()
    {
        ThrowIfUnavailable();
        if (_shown) { _overlay!.Show(); return; }
        try
        {
            _sourceChanges.Activate();
            // Show synchronizes through the owning overlay before accepting native input ownership.
            _overlay!.Show();
            _shown = true;
        }
        catch (Exception activation)
        {
            try { _sourceChanges.Deactivate(); }
            catch (Exception cleanup) { throw new AggregateException(activation, cleanup); }
            throw;
        }
    }

    public void Hide()
    {
        if (_disposed || _disposeRequested) return;
        if (_closedRaised) return;
        if (Context.ScreenId != _screen)
            throw new InvalidOperationException("A semantic surface belongs to its creating screen.");
        _retireRequested = true;
        UiSemanticStardewOverlaySession? overlay = _overlay;
        if (overlay?.Visible == true)
            overlay.Hide();
        else
            OnOverlayClosed();
    }

    public IDisposable RegisterTexture(UiSymbolId asset, byte[] png)
    { ThrowIfUnavailable(); return _textures.Register(asset, png); }

    public void SetTheme(UiSemanticTheme theme)
    {
        ThrowIfUnavailable();
        UiSemanticThemes.Resolve(theme);
        if (_theme == theme) return;
        _theme = theme;
        _configurationVersion++;
        Refresh();
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
        if (Context.ScreenId != _screen) return;
        ThrowIfUnavailable();
        UiSemanticStardewOverlaySession overlay = _overlay!;
        if (_shown && !overlay.SynchronizeMenuContext()) return;
        SynchronizeState(UiSemanticStardewMenu.CaptureViewport(), force: true);
    }

    public void Synchronize()
    {
        if (Context.ScreenId != _screen) return;
        ThrowIfUnavailable();
        UiSemanticStardewOverlaySession overlay = _overlay!;
        if (_shown && !overlay.SynchronizeMenuContext()) return;
        SynchronizeState(UiSemanticStardewMenu.CaptureViewport());
    }

    public void Dispose()
    {
        if (_disposed || _disposing) return;
        _disposeRequested = true;
        _disposing = true;
        var failures = new List<Exception>();
        try
        {
            try { StopSubscriptions(); } catch (Exception error) { failures.Add(error); }
            DisposeOwnedResources(failures);
            if (_runtime == null) { try { _textures.Dispose(); } catch (Exception error) { failures.Add(error); } }
            if (_overlay == null && _runtime == null)
                _disposed = failures.Count == 0;
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

    internal bool ActivateForAutomatedAcceptance(UiSymbolId action)
    {
        ThrowIfUnavailable();
        return _overlay!.ActivateForAutomatedAcceptance(action);
    }

    internal bool RevealForAutomatedAcceptance(UiSymbolId semantic)
    {
        ThrowIfUnavailable();
        return _overlay!.RevealForAutomatedAcceptance(semantic);
    }

    internal bool IsOwnedBy(UiSemanticSurfaceService owner)
    {
        return ReferenceEquals(_owner, owner);
    }

    internal UiSemanticSurfaceSnapshot CaptureForAutomatedAcceptance()
    {
        if (Context.ScreenId != _screen)
            throw new InvalidOperationException("Surface observation requires its owning screen.");
        UiSurfaceObservationState observation = _observation
            ?? throw new InvalidOperationException("This surface was created outside the exact automated TestHarness.");
        if (_retireRequested || _closedRaised || _disposeRequested || _disposed || _overlay?.IsRetired != false)
            return observation.Retire();
        return _overlay.CaptureObservation(observation, _environment);
    }

    private UiScene ComposeInteraction(UiInteractionSnapshot interaction)
    {
        _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        return ComposeScene();
    }

    private UiScene ComposeScene()
        => _composer.Compose(_invocation, _assets.For(_options.Id).Visual, interaction: _interaction,
            locale: _environment.Locale);

    private void SynchronizeState(RuntimeRect viewport, bool force = false)
    {
        ThrowIfUnavailable();
        UiEnvironment environment = CaptureEnvironment(viewport);
        if (!force && !_sourceChanges.HasChanges && ReferenceEquals(_environment, environment)) return;
        RecomposePrepared(viewport, environment);
    }

    private void RecomposePrepared(RuntimeRect viewport, UiEnvironment environment)
    {
        UiSemanticTheme theme = _theme;
        UiSceneComposer composer = ResolveComposer(theme);
        long configurationVersion = _configurationVersion;
        long sourceVersion = _sourceChanges.Version;
        UiInvocationResult? invocation = null;
        _overlay!.UpdatePrepared(ComposeCurrentScene, viewport, AcceptPreparedEnvironment,
            () => ValidateConfiguration(configurationVersion));

        UiScene ComposeCurrentScene()
        {
            invocation = ReferenceEquals(_environment, environment) ? _invocation :
                new UiInvocationService(_registry).InvokeInEnvironment(_options.Id, environment,
                    _assets.For(_options.Id).Presentation);
            return composer.Compose(invocation, _assets.For(_options.Id).Visual, _interaction, environment.Locale);
        }

        void AcceptPreparedEnvironment()
        {
            _invocation = invocation!;
            AcceptEnvironment(environment, theme, composer, sourceVersion);
        }
    }

    private UiEnvironment CaptureEnvironment(RuntimeRect viewport)
        => UiSemanticStardewEnvironmentCapture.Capture(viewport, _theme, _environment);

    private UiSceneComposer ResolveComposer(UiSemanticTheme theme)
        => theme == _acceptedTheme ? _composer : new UiSceneComposer(UiSemanticThemes.Resolve(theme), _registry);

    private void AcceptEnvironment(UiEnvironment environment, UiSemanticTheme theme, UiSceneComposer composer,
        long sourceVersion)
    {
        _environment = environment;
        _acceptedTheme = theme;
        _composer = composer;
        _sourceChanges.Accept(sourceVersion);
    }

    private void ValidateConfiguration(long version)
    {
        ThrowIfUnavailable();
        if (_configurationVersion != version)
            throw new InvalidOperationException("The requested surface environment changed during preparation.");
    }

    private void OnRendered() => Rendered?.Invoke();

    private void OnOverlayClosed()
    {
        if (_closedRaised) return;
        _closedRaised = true;
        _observation?.Retire();
        try { StopSubscriptions(); } finally { Closed?.Invoke(); }
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

    private static SpriteFont ResolveFont(RuntimeTypography typography)
        => string.Equals(typography.Family, "Display", StringComparison.Ordinal)
            ? Game1.dialogueFont
            : Game1.smallFont;

    private Texture2D ResolveTexture(RuntimeSymbolId texture) => _textures.Resolve(texture);

    private static float ResolveBackgroundDimmingOpacity(UiSemanticSurfaceOptions options)
        => options.DimUnderlyingMenu ? 0.42f : 0f;

    private void ThrowIfUnavailable()
    {
        if (_disposed || _disposeRequested)
            throw new ObjectDisposedException(nameof(UiActiveMenuSemanticSurfaceSession));
        if (_retireRequested || _closedRaised)
        {
            throw new InvalidOperationException(
                "A dismissed semantic surface session can only be disposed; create a new session.");
        }
        if (Context.ScreenId != _screen)
            throw new InvalidOperationException("A semantic surface belongs to its creating screen.");
    }
}
