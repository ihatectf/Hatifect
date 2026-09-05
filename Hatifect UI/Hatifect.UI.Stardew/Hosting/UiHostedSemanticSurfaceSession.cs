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
    private readonly UiSemanticHostKind _kind;
    private readonly UiSemanticTerminalDefinition? _terminal;
    private readonly UiRegistrySnapshot _registry;
    private readonly HashSet<IUiSemanticSource> _sources = new(ReferenceEqualityComparer.Instance);
    private readonly UiSceneComposer _composer;
    private readonly UiSemanticTextureCatalog<Texture2D> _textures = UiSemanticTextureResources.Create();
    private UiSemanticTheme _theme;
    private readonly UiSemanticLiveAssets _assets;
    private readonly UiSemanticAssetWatches _watches = new();
    private UiSemanticSurfaceOptions _options;
    private UiSemanticStardewRuntime? _runtime;
    private UiSemanticStardewMenu? _menu;
    private UiSemanticStardewOverlaySession? _overlay;
    private UiSemanticStardewHost? _host;
    private UiInvocationResult? _invocation;
    private UiInteractionSnapshot? _interaction;
    private UiPresentationProfile _profile;
    private string _locale;
    private UiSymbolId _section;
    private bool _subscribed;
    private bool _dirty = true;
    private bool _closed;
    private bool _disposing;
    private bool _disposed;

    protected UiHostedSemanticSurfaceSession(IModHelper helper, UiSemanticSurfaceOptions options,
        UiExperienceDefinition? experience, UiSemanticHostKind kind, UiSemanticTerminalDefinition? terminal)
    {
        _helper = helper ?? throw new ArgumentNullException(nameof(helper));
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
        _profile = ResolveProfile(UiSemanticStardewMenu.CaptureViewport());
        _locale = ResolveLocale();
        _composer = new UiSceneComposer(UiSemanticStardewTheme.Default, _registry);
        _assets = new UiSemanticLiveAssets(terminal != null
            ? terminal.Sections.Select(section => section.Experience) : new[] { experience! },
            ValidateAssets, _ => { _invocation = null; Refresh(); });
    }

    public bool Visible => !_closed && !_disposed && (_overlay?.Visible == true
        || (_menu != null && ReferenceEquals(Game1.activeClickableMenu, _menu)));
    public event Action? Rendered;
    public event Action? Closed;
    public event Action<UiSemanticReloadResult>? AssetsReloaded;

    public UiSemanticReloadResult Reload(UiSymbolId experience, string? presentation, string? visual)
    {
        ThrowIfUnavailable();
        UiSemanticReloadResult result = _assets.Reload(experience, presentation, visual);
        AssetsReloaded?.Invoke(result);
        return result;
    }
    public IDisposable WatchAssets(UiSymbolId experience, string? presentationPath, string? visualPath)
    {
        ThrowIfUnavailable();
        _assets.For(experience);
        return _watches.Add(experience, presentationPath, visualPath,
            (presentation, visual) => Reload(experience, presentation, visual),
            error => AssetsReloaded?.Invoke(_assets.Failure(experience, "LUI4103", error.Message)));
    }
    private void ValidateAssets(UiSymbolId experience, UiTerminalSectionAssets assets)
    {
        var invocation = new UiInvocationService(_registry).Invoke(experience, _profile, assets.Presentation);
        var scene = _composer.Compose(invocation, assets.Visual, _interaction, _locale);
        UiSemanticStardewCapabilities.Validate(scene);
        _runtime?.ValidateCandidate(scene, new UiHostPlacementContext(UiSemanticStardewMenu.CaptureViewport()));
    }

    public void Show()
    {
        ThrowIfUnavailable();
        if (Visible) return;
        if (_menu != null) { RetireDetachedMenu(); ThrowIfUnavailable(); }
        if (_kind != UiSemanticHostKind.Hud && Game1.activeClickableMenu != null)
            throw new InvalidOperationException("A standalone surface requires the native menu slot to be free.");
        _runtime ??= new UiSemanticStardewRuntime(Game1.graphics.GraphicsDevice, ResolveFont, ResolveTexture);
        try
        {
            RuntimeRect viewport = UiSemanticStardewMenu.CaptureViewport();
            _profile = ResolveProfile(viewport);
            _locale = ResolveLocale();
            if (_terminal != null)
            {
                _menu = _runtime.CreateTerminalMenu(_registry, UiSemanticThemes.Resolve(_theme), ResolveProfile,
                    _section, resolveAssets: descriptor => _assets.For(descriptor.Id), resolveLocale: ResolveLocale, onClosed: OnPresentationClosed);
            }
            else if (_kind == UiSemanticHostKind.Hud)
            {
                _overlay = _runtime.CreateOverlay(_helper, Compose(),
                    UiSemanticStardewOverlayRenderLayer.Hud, ComposeInteraction, OnPresentationClosed);
                _overlay.Rendered += OnRendered;
                _overlay.Show();
            }
            else
            {
                _host = _runtime.CreateHost(Compose(), new UiHostPlacementContext(viewport), ComposeInteraction);
                _menu = new UiSemanticStardewMenu(_host, viewport, Recompose, OnPresentationClosed);
            }
            if (_menu != null)
            {
                _menu.CloseOnCancel = _kind != UiSemanticHostKind.Modal;
                _menu.Rendered += OnRendered;
                Game1.activeClickableMenu = _menu;
            }
            Subscribe();
            _dirty = false;
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
        if (_overlay?.Visible == true) _overlay.Hide();
        if (_menu is { } menu)
        {
            if (ReferenceEquals(Game1.activeClickableMenu, menu)) menu.exitThisMenu();
            else menu.Dispose();
        }
        OnPresentationClosed();
    }

    public IDisposable RegisterTexture(UiSymbolId asset, byte[] png)
    { ThrowIfUnavailable(); return _textures.Register(asset, png); }

    public void SetTheme(UiSemanticTheme theme)
    {
        ThrowIfUnavailable();
        var resolved = UiSemanticThemes.Resolve(theme);
        if (_theme == theme) return;
        _theme = theme;
        _composer.SetTheme(resolved);
        if (_terminal != null) _menu?.SetTerminalTheme(resolved);
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
        ThrowIfUnavailable();
        _dirty = true;
        Synchronize();
    }

    public void Synchronize()
    {
        ThrowIfUnavailable();
        if (RetireDetachedMenu() || !Visible) return;
        RuntimeRect viewport = UiSemanticStardewMenu.CaptureViewport();
        UiPresentationProfile profile = ResolveProfile(viewport);
        string locale = ResolveLocale();
        if (!_dirty && profile.Id == _profile.Id && locale == _locale) return;
        _profile = profile;
        _locale = locale;
        Recompose(viewport);
        _dirty = false;
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
        _disposing = true;
        var failures = new List<Exception>();
        try
        {
            try { Hide(); } catch (Exception error) { failures.Add(error); }
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

    private UiScene Compose()
    {
        if (_invocation == null || _invocation.Plan.Host.Profile != _profile.Id)
            _invocation = new UiInvocationService(_registry).Invoke(_options.Id, _profile, _assets.For(_options.Id).Presentation);
        return _composer.Compose(_invocation, _assets.For(_options.Id).Visual, interaction: _interaction, locale: _locale);
    }

    private UiScene ComposeInteraction(UiInteractionSnapshot interaction)
    {
        _interaction = interaction;
        return Compose();
    }

    private void Recompose(RuntimeRect viewport)
    {
        _profile = ResolveProfile(viewport);
        _locale = ResolveLocale();
        if (_terminal != null) _menu?.RecomposeTerminal(_profile, viewport, _locale);
        else if (_overlay != null) _overlay.Update(Compose());
        else _host?.Update(Compose(), new UiHostPlacementContext(viewport));
    }

    private void CollectSources(UiExperienceDefinition experience)
    {
        foreach (UiSemanticElementDefinition element in experience.Elements) _sources.Add(element.Source);
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        _helper.Events.GameLoop.UpdateTicked += OnUpdate;
        _helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        _helper.Events.Display.MenuChanged += OnMenuChanged;
        foreach (IUiSemanticSource source in _sources) source.Changed += OnChanged;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        var failures = new List<Exception>();
        try { _helper.Events.GameLoop.UpdateTicked -= OnUpdate; } catch (Exception error) { failures.Add(error); }
        try { _helper.Events.GameLoop.ReturnedToTitle -= OnReturnedToTitle; } catch (Exception error) { failures.Add(error); }
        try { _helper.Events.Display.MenuChanged -= OnMenuChanged; } catch (Exception error) { failures.Add(error); }
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

    private void OnChanged() => _dirty = true;
    private void OnUpdate(object? sender, UpdateTickedEventArgs args)
    {
        if (_closed) return;
        _watches.Poll();
        if (!_closed && !_disposed) Synchronize();
    }
    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs args) => Dispose();
    private void OnMenuChanged(object? sender, MenuChangedEventArgs args) { if (!_closed) RetireDetachedMenu(); }
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
        if (_closed) throw new InvalidOperationException("Create a new session after terminal dismissal.");
    }

    private static UiPresentationProfile ResolveProfile(RuntimeRect viewport) => Game1.options.gamepadControls
        ? UiPresentationProfiles.Controller : viewport.Width < 720 ? UiPresentationProfiles.Compact
        : viewport.Width < 1100 ? UiPresentationProfiles.Medium : UiPresentationProfiles.Wide;
    private static string ResolveLocale() => LocalizedContentManager.CurrentLanguageCode.ToString();
    private static SpriteFont ResolveFont(RuntimeTypography typography) => typography.Family == "Display"
        ? Game1.dialogueFont : Game1.smallFont;
    private Texture2D ResolveTexture(UiSymbolId asset) => _textures.Resolve(asset);
}
