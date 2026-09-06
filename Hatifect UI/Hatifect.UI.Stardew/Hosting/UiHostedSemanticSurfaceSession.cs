using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
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
using Hatifect.UI.Stardew.Semantic;
using RuntimeRect = Hatifect.UI.Runtime.Layout.UiRect;
using RuntimeTypography = Hatifect.UI.Runtime.Visual.UiTypography;

namespace Hatifect.UI.Stardew;

internal sealed class UiStandaloneSemanticSurfaceSession : UiHostedSemanticSurfaceSession
{
    internal UiStandaloneSemanticSurfaceSession(IModHelper helper, UiExperienceDefinition experience,
        UiSemanticHostKind kind, UiSemanticSurfaceOptions options)
        : base(helper, options, experience, kind, null) { }
}

internal sealed class UiTerminalSemanticSurfaceSession : UiHostedSemanticSurfaceSession, IUiSemanticTerminalSession
{
    internal UiTerminalSemanticSurfaceSession(IModHelper helper, UiSemanticTerminalDefinition terminal,
        UiSemanticSurfaceOptions options)
        : base(helper, options, null, UiSemanticHostKind.Window,
            terminal ?? throw new ArgumentNullException(nameof(terminal))) { }

    public UiSymbolId CurrentSection => GetCurrentSection();
    public void OpenSection(UiSymbolId section) => SelectSection(section);
}

/// <summary>Owns native presentation and subscriptions; Runtime owns scene and interaction policy.</summary>
internal abstract class UiHostedSemanticSurfaceSession : IUiSemanticAppearanceSession, IUiSemanticReloadSession
{
    private readonly IModHelper _helper;
    private readonly UiSemanticHostEventBinding _events;
    private readonly UiSemanticHostKind _kind;
    private readonly UiSemanticTerminalDefinition? _terminal;
    private readonly UiRegistrySnapshot _registry;
    private readonly HashSet<IUiSemanticSource> _sources = new(ReferenceEqualityComparer.Instance);
    private UiSceneComposer _composer;
    private readonly UiSemanticTextureCatalog<Texture2D> _textures = UiSemanticTextureResources.Create();
    private UiSemanticTheme _theme;
    private UiSemanticTheme _acceptedTheme;
    private long _configurationVersion;
    private readonly UiSemanticLiveAssets _assets;
    private readonly UiSemanticAssetWatches _watches = new();
    private UiSemanticSurfaceOptions _options;
    private UiSemanticStardewRuntime? _runtime;
    private UiSemanticStardewMenu? _menu;
    private UiSemanticStardewOverlaySession? _overlay;
    private UiSemanticStardewHost? _host;
    private UiInvocationResult? _invocation;
    private UiInteractionSnapshot? _interaction;
    private UiEnvironment? _environment;
    private UiSymbolId _section;
    private bool _subscribed;
    private bool _dirty = true;
    private long _changeVersion;
    private bool _closed;
    private bool _retireRequested;
    private bool _disposing;
    private bool _disposed;

    protected UiHostedSemanticSurfaceSession(IModHelper helper, UiSemanticSurfaceOptions options,
        UiExperienceDefinition? experience, UiSemanticHostKind kind, UiSemanticTerminalDefinition? terminal)
    {
        _helper = helper ?? throw new ArgumentNullException(nameof(helper));
        _events = new UiSemanticHostEventBinding(helper.Events, Context.ScreenId, static () => Context.ScreenId,
            OnUpdate, OnReturnedToTitle, OnMenuChanged);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.DimUnderlyingMenu)
            throw new ArgumentException("Dimming a native menu is supported by active-menu overlays.", nameof(options));
        _terminal = terminal;
        _kind = kind;
        if (terminal != null)
        {
            if (options.Id != terminal.Id) throw new ArgumentException("Surface and Terminal IDs must match.", nameof(options));
            _registry = UiSemanticHostProjection.Register(terminal);
            _section = terminal.InitialSection;
            foreach (UiSemanticTerminalSection section in terminal.Sections) CollectSources(section.Experience);
        }
        else
        {
            ArgumentNullException.ThrowIfNull(experience);
            if (options.Id != experience.Id) throw new ArgumentException("Surface and Experience IDs must match.", nameof(options));
            _registry = UiSemanticHostProjection.Register(experience, kind);
            CollectSources(experience);
        }
        _composer = new UiSceneComposer(UiSemanticStardewTheme.Default, _registry);
        _assets = new UiSemanticLiveAssets(terminal != null
            ? terminal.Sections.Select(section => section.Experience) : new[] { experience! },
            ApplyAssets);
    }

    public bool Visible => _events.IsOwner && !_retireRequested && !_closed && !_disposed && (_overlay?.Visible == true
        || (_menu != null && ReferenceEquals(Game1.activeClickableMenu, _menu)));
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
        return _watches.Add(experience, presentationPath, visualPath,
            (presentation, visual) => Reload(experience, presentation, visual),
            error => NotifyAssetsReloaded(_assets.Failure(experience, "LUI4103", error.Message)));
    }
    private void NotifyAssetsReloaded(UiSemanticReloadResult result)
    {
        if (AssetsReloaded is not { } observers) return;
        foreach (Action<UiSemanticReloadResult> observer in observers.GetInvocationList())
        {
            if (!_events.IsOwner || _retireRequested || _disposed || _disposing || _closed) return;
            if (RetireDetachedMenu()) return;
            observer(result);
        }
    }

    private void ValidateAssets(UiSymbolId experience, UiTerminalSectionAssets assets,
        UiEnvironment environment, UiSceneComposer composer, RuntimeRect viewport)
    {
        var invocation = new UiInvocationService(_registry).InvokeInEnvironment(experience, environment, assets.Presentation);
        var scene = composer.Compose(invocation, assets.Visual, _interaction, environment.Locale);
        UiSemanticStardewCapabilities.Validate(scene);
        _runtime?.ValidateCandidate(scene, new UiHostPlacementContext(viewport));
    }

    private void ApplyAssets(UiSymbolId experience, UiTerminalSectionAssets assets, Action acceptAssets)
    {
        ThrowIfUnavailable();
        RetireDetachedMenu();
        ThrowIfUnavailable();
        RuntimeRect viewport = UiSemanticStardewMenu.CaptureViewport();
        UiSemanticTheme theme = _theme;
        UiEnvironment environment = CaptureEnvironment(viewport);
        UiPresentationProfile profile = UiPresentationProfiles.Resolve(environment);
        UiSceneComposer composer = ResolveComposer(theme);
        long configurationVersion = _configurationVersion;
        long changeVersion = _changeVersion;
        if (_terminal != null && _menu != null && _menu.CurrentSection == experience)
        {
            _menu.ReloadTerminal(assets, profile, viewport, environment.Locale, () =>
            {
                AcceptEnvironment(environment, theme, composer, changeVersion);
                acceptAssets();
            }, environment, UiSemanticThemes.Resolve(theme), ValidateOwner);
            return;
        }
        if (_terminal != null || _host == null && _overlay == null)
        {
            // A hidden surface or inactive section owns no current action generation to replace.
            UiSemanticStardewMenu? previousMenu = _menu;
            UiSemanticStardewHost? previousHost = _host;
            UiSemanticStardewOverlaySession? previousOverlay = _overlay;
            long? version = previousMenu?.CaptureInspectionContext().Runtime.AcceptedVersion;
            ValidateAssets(experience, assets, environment, composer, viewport);
            ValidateOwner();
            if (!ReferenceEquals(previousMenu, _menu) || !ReferenceEquals(previousHost, _host) ||
                !ReferenceEquals(previousOverlay, _overlay) ||
                version != _menu?.CaptureInspectionContext().Runtime.AcceptedVersion)
                throw new InvalidOperationException("The surface changed during inactive asset preparation.");
            acceptAssets();
            return;
        }
        UiInvocationResult? invocation = null;
        UiScene Prepare()
        {
            invocation = new UiInvocationService(_registry).InvokeInEnvironment(experience, environment, assets.Presentation);
            UiScene scene = composer.Compose(invocation, assets.Visual, _interaction, environment.Locale);
            ValidateOwner();
            return scene;
        }
        void Accept()
        {
            _invocation = invocation;
            AcceptEnvironment(environment, theme, composer, changeVersion);
            acceptAssets();
        }
        if (_overlay != null) _overlay.UpdatePrepared(Prepare, viewport, Accept, ValidateOwner, renewActionGeneration: true);
        else _host!.Reload(Prepare, new UiHostPlacementContext(viewport), Accept, ValidateOwner);

        void ValidateOwner()
        {
            ThrowIfUnavailable();
            RetireDetachedMenu();
            ThrowIfUnavailable();
            if (_configurationVersion != configurationVersion)
                throw new InvalidOperationException("The requested surface environment changed during preparation.");
        }
    }

    public void Show()
    {
        ThrowIfUnavailable();
        if (Visible) return;
        if (_menu != null) { RetireDetachedMenu(); ThrowIfUnavailable(); }
        if (_kind != UiSemanticHostKind.Hud && Game1.activeClickableMenu != null)
            throw new InvalidOperationException("A standalone surface requires the native menu slot to be free.");
        _runtime ??= new UiSemanticStardewRuntime(Game1.graphics.GraphicsDevice, ResolveFont, ResolveTexture);
        long changeVersion = _changeVersion;
        try
        {
            RuntimeRect viewport = UiSemanticStardewMenu.CaptureViewport();
            UiSemanticTheme theme = _theme;
            UiEnvironment environment = CaptureEnvironment(viewport);
            UiSceneComposer composer = ResolveComposer(theme);
            if (_terminal != null)
            {
                _host = _runtime.CreateTerminalHost(_registry, UiSemanticThemes.Resolve(theme),
                    new UiHostPlacementContext(viewport), UiPresentationProfiles.Resolve(environment),
                    _section, resolveAssets: descriptor => _assets.For(descriptor.Id),
                    locale: environment.Locale, environment: environment);
                AcceptEnvironment(environment, theme, composer, changeVersion);
                _menu = new UiSemanticStardewMenu(_host, viewport, Recompose, OnPresentationClosed);
            }
            else
            {
                UiInvocationResult invocation = new UiInvocationService(_registry).InvokeInEnvironment(
                    _options.Id, environment, _assets.For(_options.Id).Presentation);
                UiScene scene = composer.Compose(invocation, _assets.For(_options.Id).Visual,
                    _interaction, environment.Locale);
                if (_kind == UiSemanticHostKind.Hud)
                {
                    _overlay = _runtime.CreateOverlay(_helper, scene,
                        UiSemanticStardewOverlayRenderLayer.Hud, ComposeInteraction, OnPresentationClosed,
                        next => Synchronize(next));
                    _invocation = invocation;
                    AcceptEnvironment(environment, theme, composer, changeVersion);
                    _overlay.Rendered += OnRendered;
                    _overlay.Show();
                }
                else
                {
                    _host = _runtime.CreateHost(scene, new UiHostPlacementContext(viewport), ComposeInteraction);
                    _invocation = invocation;
                    AcceptEnvironment(environment, theme, composer, changeVersion);
                    _menu = new UiSemanticStardewMenu(_host, viewport, Recompose, OnPresentationClosed);
                }
            }
            if (_menu != null)
            {
                _menu.CloseOnCancel = _kind != UiSemanticHostKind.Modal;
                _menu.Rendered += OnRendered;
                Game1.activeClickableMenu = _menu;
            }
            Subscribe();
            _dirty = _changeVersion != changeVersion;
        }
        catch (Exception activationFailure)
        {
            try { Dispose(); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Semantic host activation and cleanup failed.", activationFailure, cleanupFailure);
            }
            throw;
        }
    }

    public void Hide()
    {
        if (_disposed || _closed) return;
        _events.RequireOwner();
        CloseOwnedPresentation();
    }

    private void CloseOwnedPresentation()
    {
        if (_closed) return;
        _retireRequested = true;
        if (_overlay?.Visible == true) _overlay.Hide();
        if (_menu is { } menu)
        {
            if (_events.IsOwner && ReferenceEquals(Game1.activeClickableMenu, menu)) menu.exitThisMenu();
            else menu.Dispose();
        }
        OnPresentationClosed();
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
        ThrowIfUnavailable();
        ArgumentNullException.ThrowIfNull(options);
        if (options.Id != _options.Id || options.CloseOnOutsidePointer != _options.CloseOnOutsidePointer)
            throw new InvalidOperationException("Surface identity and dismissal policy are fixed for a session.");
        if (options.DimUnderlyingMenu)
            throw new ArgumentException("Dimming a native menu is supported by active-menu overlays.", nameof(options));
        _options = options;
    }

    public void Refresh()
    {
        if (!_events.IsOwner) return;
        ThrowIfUnavailable();
        OnChanged();
        Synchronize();
    }

    public void Synchronize()
    {
        if (!_events.IsOwner) return;
        ThrowIfUnavailable();
        if (RetireDetachedMenu() || !Visible) return;
        Synchronize(UiSemanticStardewMenu.CaptureViewport());
    }

    protected UiSymbolId GetCurrentSection()
    {
        ThrowIfUnavailable();
        return _menu?.CurrentSection ?? _section;
    }

    protected void SelectSection(UiSymbolId section)
    {
        ThrowIfUnavailable();
        if (!_terminal!.Sections.Any(item => item.Experience.Id == section))
            throw new ArgumentException("The section does not belong to this Terminal.", nameof(section));
        _menu?.OpenTerminalSection(section);
        _section = section;
    }

    public void Dispose()
    {
        if (_disposed || _disposing) return;
        _retireRequested = true;
        _disposing = true;
        var failures = new List<Exception>();
        try
        {
            try { CloseOwnedPresentation(); } catch (Exception error) { failures.Add(error); }
            try { Unsubscribe(); } catch (Exception error) { failures.Add(error); }
            try { _watches.Dispose(); } catch (Exception error) { failures.Add(error); }
            try { _overlay?.Dispose(); _overlay = null; } catch (Exception error) { failures.Add(error); }
            try { _menu?.Dispose(); _menu = null; } catch (Exception error) { failures.Add(error); }
            try { _runtime?.Dispose(); _runtime = null; _host = null; } catch (Exception error) { failures.Add(error); }
            if (_runtime == null) { try { _textures.Dispose(); } catch (Exception error) { failures.Add(error); } }
            _disposed = failures.Count == 0;
        }
        finally { _disposing = false; }
        if (failures.Count != 0) throw new AggregateException("Semantic host cleanup failed.", failures);
    }

    private UiScene ComposeInteraction(UiInteractionSnapshot interaction)
    {
        _interaction = interaction;
        return _composer.Compose(_invocation!, _assets.For(_options.Id).Visual,
            interaction: interaction, locale: _environment!.Locale);
    }

    private void Recompose(RuntimeRect viewport)
        => Synchronize(viewport, force: true);

    private void Synchronize(RuntimeRect viewport, bool force = false)
    {
        ThrowIfUnavailable();
        if (RetireDetachedMenu()) return;
        UiEnvironment environment = CaptureEnvironment(viewport);
        if (!force && !_dirty && ReferenceEquals(environment, _environment)) return;
        RecomposePrepared(viewport, environment);
    }

    private void RecomposePrepared(RuntimeRect viewport, UiEnvironment environment)
    {
        UiSemanticTheme theme = _theme;
        UiSceneComposer composer = ResolveComposer(theme);
        long changeVersion = _changeVersion;
        long configurationVersion = _configurationVersion;
        void ValidateOwner()
        {
            ThrowIfUnavailable();
            RetireDetachedMenu();
            ThrowIfUnavailable();
            if (_configurationVersion != configurationVersion)
                throw new InvalidOperationException("The requested surface environment changed during preparation.");
        }
        if (_terminal != null)
        {
            _menu?.RecomposeTerminal(UiPresentationProfiles.Resolve(environment), viewport, environment.Locale,
                environment, () => AcceptEnvironment(environment, theme, composer, changeVersion),
                UiSemanticThemes.Resolve(theme), ValidateOwner);
            return;
        }
        UiInvocationResult? invocation = null;
        UiScene Prepare()
        {
            invocation = ReferenceEquals(environment, _environment) ? _invocation! :
                new UiInvocationService(_registry).InvokeInEnvironment(_options.Id, environment,
                    _assets.For(_options.Id).Presentation);
            return composer.Compose(invocation, _assets.For(_options.Id).Visual, _interaction, environment.Locale);
        }
        void Accept()
        {
            _invocation = invocation;
            AcceptEnvironment(environment, theme, composer, changeVersion);
        }
        if (_overlay != null) _overlay.UpdatePrepared(Prepare, viewport, Accept, ValidateOwner);
        else _host?.UpdatePrepared(Prepare, new UiHostPlacementContext(viewport), Accept, ValidateOwner);
    }

    private UiEnvironment CaptureEnvironment(RuntimeRect viewport)
        => UiSemanticStardewEnvironmentCapture.Capture(viewport, _theme, _environment);

    private UiSceneComposer ResolveComposer(UiSemanticTheme theme)
        => theme == _acceptedTheme ? _composer : new UiSceneComposer(UiSemanticThemes.Resolve(theme), _registry);

    private void AcceptEnvironment(UiEnvironment environment, UiSemanticTheme theme,
        UiSceneComposer composer, long changeVersion)
    {
        _environment = environment;
        _acceptedTheme = theme;
        _composer = composer;
        _dirty = _changeVersion != changeVersion;
    }

    private void CollectSources(UiExperienceDefinition experience)
    {
        foreach (UiSemanticElementDefinition source in experience.Sources) _sources.Add(source.Source);
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        _events.Activate();
        foreach (IUiSemanticSource source in _sources) source.Changed += OnChanged;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        var failures = new List<Exception>();
        try { _events.Dispose(); } catch (Exception error) { failures.Add(error); }
        foreach (IUiSemanticSource source in _sources)
        {
            try { source.Changed -= OnChanged; } catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count != 0) throw new AggregateException("Semantic host subscriptions failed to release.", failures);
        _subscribed = false;
    }

    private bool RetireDetachedMenu()
    {
        if (_menu == null || ReferenceEquals(Game1.activeClickableMenu, _menu)) return false;
        _menu.Dispose();
        return true;
    }

    private void OnChanged()
    {
        if (_retireRequested || _closed || _disposed) return;
        _changeVersion++;
        _dirty = true;
    }
    private void OnUpdate(object? sender, UpdateTickedEventArgs args)
    {
        if (_retireRequested || _closed || _disposed) return;
        _watches.Poll();
        if (!_retireRequested && !_closed && !_disposed && _overlay == null) Synchronize();
    }
    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs args) => Dispose();
    private void OnMenuChanged(object? sender, MenuChangedEventArgs args)
    {
        if (!_retireRequested && !_closed && !_disposed) RetireDetachedMenu();
    }
    private void OnRendered() => Rendered?.Invoke();

    private void OnPresentationClosed()
    {
        if (_closed) return;
        _closed = true;
        try { Unsubscribe(); }
        finally { try { _watches.Dispose(); } finally { Closed?.Invoke(); } }
    }

    private void ThrowIfUnavailable()
    {
        if (_disposed || _disposing) throw new ObjectDisposedException(nameof(UiHostedSemanticSurfaceSession));
        if (_retireRequested || _closed) throw new InvalidOperationException("Create a new session after terminal dismissal.");
        _events.RequireOwner();
    }

    private static SpriteFont ResolveFont(RuntimeTypography typography) => typography.Family == "Display"
        ? Game1.dialogueFont : Game1.smallFont;
    private Texture2D ResolveTexture(UiSymbolId asset) => _textures.Resolve(asset);
}
