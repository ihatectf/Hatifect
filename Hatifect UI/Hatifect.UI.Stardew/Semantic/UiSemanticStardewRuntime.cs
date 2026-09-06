using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Accessibility;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Runtime.Registration;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Terminal;
using Hatifect.UI.Runtime.Visual.Theming;
using Hatifect.UI.Semantics;
using RuntimePoint = Hatifect.UI.Runtime.Layout.UiPoint;
using RuntimeRect = Hatifect.UI.Runtime.Layout.UiRect;
using RuntimeTypography = Hatifect.UI.Runtime.Visual.UiTypography;
using RuntimeSymbolId = Hatifect.UI.UiSymbolId;

namespace Hatifect.UI.Stardew.Semantic;

/// <summary>
/// One semantic renderer resource owner per consuming mod/service lifetime. Game/content resources
/// are borrowed; generated backend resources are released on reset or Dispose().
/// </summary>
internal sealed class UiSemanticStardewRuntime : IDisposable
{
    private readonly UiSemanticSpriteBatchBridge _bridge;
    private readonly HashSet<UiSemanticStardewHost> _hosts = new();
    private readonly HashSet<UiSemanticStardewOverlaySession> _overlays = new();
    private bool _disposeRequested;
    private bool _disposing;
    private bool _bridgeDisposed;
    private bool _disposed;

    public UiSemanticStardewRuntime(
        GraphicsDevice graphicsDevice,
        Func<RuntimeTypography, SpriteFont> resolveFont,
        Func<RuntimeSymbolId, Texture2D> resolveTexture)
        => _bridge = new UiSemanticSpriteBatchBridge(graphicsDevice, resolveFont, resolveTexture);

    internal void ValidateCandidate(UiScene scene, UiHostPlacementContext placement)
    {
        ThrowIfDisposed();
        UiSemanticStardewCapabilities.Validate(scene);
        new Hatifect.UI.Runtime.Layout.UiSceneLayoutEngine(_bridge).Build(scene, placement);
    }

    public UiSemanticStardewHost CreateHost(
        UiScene scene,
        UiHostPlacementContext placement,
        Func<UiInteractionSnapshot, UiScene>? composeInteraction = null)
    {
        ThrowIfDisposed();
        UiSemanticStardewCapabilities.Validate(scene);
        var host = new UiSemanticStardewHost(
            this,
            new UiPortalHostSession(
                scene,
                placement,
                _bridge,
                composeInteraction: UiSemanticStardewCapabilities.Guard(composeInteraction)));
        _hosts.Add(host);
        return host;
    }

    public UiSemanticStardewHost CreateTerminalHost(
        UiRegistrySnapshot registry,
        UiTheme theme,
        UiHostPlacementContext placement,
        UiPresentationProfile profile,
        RuntimeSymbolId? initialSection = null,
        Func<UiExperienceDescriptor, UiTerminalSectionAssets>? resolveAssets = null,
        UiSemanticCatalog? catalog = null,
        string? locale = null,
        Action<RuntimeSymbolId>? onRouteRequested = null)
    {
        ThrowIfDisposed();
        UiSemanticStardewCapabilities.Validate(theme);
        UiTerminalHostSession? terminal = null;
        try
        {
            terminal = new UiTerminalHostSession(
                registry,
                theme,
                placement,
                _bridge,
                profile,
                initialSection,
                resolveAssets,
                catalog,
                locale,
                validateScene: UiSemanticStardewCapabilities.Validate,
                onRouteRequested: onRouteRequested);
            var host = new UiSemanticStardewHost(this, terminal);
            if (!_hosts.Add(host))
                throw new InvalidOperationException("The Terminal host is already registered with this runtime.");
            return host;
        }
        catch (Exception error)
        {
            if (terminal == null) throw;
            throw DisposeAfterFailure(
                terminal,
                error,
                "Terminal host registration failed and its Runtime owner also failed to dispose.");
        }
    }

    public UiSemanticStardewMenu CreateTerminalMenu(
        UiRegistrySnapshot registry,
        UiTheme theme,
        Func<RuntimeRect, UiPresentationProfile> resolveProfile,
        RuntimeSymbolId? initialSection = null,
        Func<UiExperienceDescriptor, UiTerminalSectionAssets>? resolveAssets = null,
        UiSemanticCatalog? catalog = null,
        Func<string?>? resolveLocale = null,
        Action? onClosed = null,
        Action<RuntimeSymbolId>? onRouteRequested = null)
    {
        ArgumentNullException.ThrowIfNull(resolveProfile);
        RuntimeRect viewport = UiSemanticStardewMenu.CaptureViewport();
        UiPresentationProfile profile = resolveProfile(viewport)
            ?? throw new InvalidOperationException("The Terminal profile resolver returned null.");
        UiSemanticStardewHost host = CreateTerminalHost(
            registry,
            theme,
            new UiHostPlacementContext(viewport),
            profile,
            initialSection,
            resolveAssets,
            catalog,
            resolveLocale?.Invoke(),
            onRouteRequested);
        try
        {
            return new UiSemanticStardewMenu(
                host,
                viewport,
                next => host.RecomposeTerminal(
                    resolveProfile(next)
                        ?? throw new InvalidOperationException("The Terminal profile resolver returned null."),
                    new UiHostPlacementContext(next),
                    resolveLocale?.Invoke()),
                onClosed);
        }
        catch (Exception error)
        {
            throw DisposeAfterFailure(
                host,
                error,
                "Semantic Terminal menu construction failed and its host also failed to dispose.");
        }
    }

    public UiSemanticStardewOverlaySession CreateOverlay(
        IModHelper helper,
        UiScene scene,
        UiSemanticStardewOverlayRenderLayer renderLayer = UiSemanticStardewOverlayRenderLayer.Hud,
        Func<UiInteractionSnapshot, UiScene>? composeInteraction = null,
        Action? onClosed = null)
    {
        ArgumentNullException.ThrowIfNull(helper);
        ArgumentNullException.ThrowIfNull(scene);
        if (scene.Root.Policy.Kind != UiHostKind.Overlay)
            throw new ArgumentException(
                $"Semantic overlays require the Overlay host policy, not '{scene.Root.Policy.Kind}'.",
                nameof(scene));

        ThrowIfDisposed();
        RuntimeRect viewport = UiSemanticStardewMenu.CaptureViewport();
        UiSemanticStardewHost host = CreateHost(
            scene,
            new UiHostPlacementContext(viewport),
            composeInteraction);
        try
        {
            var overlay = new UiSemanticStardewOverlaySession(
                this,
                helper,
                host,
                viewport,
                renderLayer,
                onClosed);
            if (!_overlays.Add(overlay))
                throw new InvalidOperationException("The semantic overlay is already registered with this runtime.");
            return overlay;
        }
        catch (Exception error)
        {
            throw DisposeAfterFailure(
                host,
                error,
                "Semantic overlay construction failed and its host also failed to dispose.");
        }
    }

    internal void Render(UiSemanticStardewHost host, SpriteBatch batch)
    {
        ThrowIfDisposed();
        if (!_hosts.Contains(host)) throw new InvalidOperationException("The semantic host belongs to another runtime.");
        _bridge.Render(host.Session, batch);
    }

    internal void Retire(UiSemanticStardewHost host) => _hosts.Remove(host);

    internal void Retire(UiSemanticStardewOverlaySession overlay) => _overlays.Remove(overlay);

    public void ResetGeneratedResources()
    {
        ThrowIfDisposed();
        _bridge.ReleaseGeneratedResources();
    }

    public void Dispose()
    {
        if (_disposed || _disposing) return;
        _disposeRequested = true;
        _disposing = true;
        var failures = new List<Exception>();
        try
        {
            foreach (UiSemanticStardewOverlaySession overlay in new List<UiSemanticStardewOverlaySession>(_overlays))
            {
                try
                {
                    overlay.Retire();
                    _overlays.Remove(overlay);
                }
                catch (Exception error)
                {
                    failures.Add(error);
                }
            }

            if (_overlays.Count == 0)
            {
                foreach (UiSemanticStardewHost host in new List<UiSemanticStardewHost>(_hosts))
                {
                    try
                    {
                        host.Retire();
                        _hosts.Remove(host);
                    }
                    catch (Exception error)
                    {
                        failures.Add(error);
                    }
                }
            }

            if (_overlays.Count == 0 && _hosts.Count == 0 && !_bridgeDisposed)
            {
                try
                {
                    _bridge.ReleaseGeneratedResources();
                    _bridge.Dispose();
                    _bridgeDisposed = true;
                }
                catch (Exception error)
                {
                    failures.Add(error);
                }
            }

            if (failures.Count == 0
                && _overlays.Count == 0
                && _hosts.Count == 0
                && _bridgeDisposed)
            {
                _disposed = true;
            }
        }
        finally
        {
            _disposing = false;
        }

        if (failures.Count > 0)
            throw new AggregateException("One or more semantic Stardew resources failed to dispose.", failures);
    }

    private void ThrowIfDisposed()
    {
        if (_disposeRequested) throw new ObjectDisposedException(nameof(UiSemanticStardewRuntime));
    }

    private static Exception DisposeAfterFailure(IDisposable owner, Exception failure, string message)
    {
        try
        {
            owner.Dispose();
            return failure;
        }
        catch (Exception disposalFailure)
        {
            return new AggregateException(message, failure, disposalFailure);
        }
    }
}

/// <summary>One root scene plus its host-owned portal stack and normalized input adapter.</summary>
internal sealed class UiSemanticStardewHost : IDisposable
{
    private UiSemanticStardewRuntime? _runtime;
    private IDisposable? _owner;
    private UiTerminalHostSession? _terminal;
    private bool _retireRequested;
    private bool _retiring;

    internal UiSemanticStardewHost(
        UiSemanticStardewRuntime runtime,
        UiPortalHostSession session,
        IUiPlatformInputSession? input = null,
        IDisposable? owner = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _owner = owner;
        Input = new UiSemanticStardewInputAdapter(input ?? session);
    }

    internal UiSemanticStardewHost(
        UiSemanticStardewRuntime runtime,
        UiTerminalHostSession terminal)
        : this(
            runtime,
            (terminal ?? throw new ArgumentNullException(nameof(terminal))).Host,
            terminal,
            terminal)
        => _terminal = terminal;

    internal UiPortalHostSession Session { get; }
    internal UiInvocationResult CurrentInvocation
        => (_terminal ?? throw new InvalidOperationException("This semantic host does not own a Terminal invocation."))
            .CurrentInvocation;
    public UiSemanticStardewInputAdapter Input { get; }
    internal UiHostRuntimePerformanceSnapshot Performance
    {
        get
        {
            ThrowIfRetired();
            return Session.Performance;
        }
    }

    internal bool Contains(RuntimePoint point)
    {
        ThrowIfRetired();
        return Session.Root.Contains(point);
    }

    public UiPortalHandle Present(UiPortalRequest request)
    {
        ThrowIfRetired();
        ArgumentNullException.ThrowIfNull(request);
        UiSemanticStardewCapabilities.Validate(request.Scene);
        return Session.Present(new UiPortalRequest(
            request.Id,
            request.Owner,
            request.Scene,
            request.Placement,
            UiSemanticStardewCapabilities.Guard(request.ComposeInteraction)));
    }

    public UiHostUpdate UpdatePortal(
        RuntimeSymbolId id,
        UiScene scene,
        UiHostPlacementContext placement)
    {
        ThrowIfRetired();
        if (!id.IsValid) throw new ArgumentException("A stable portal ID is required.", nameof(id));
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(placement);
        UiSemanticStardewCapabilities.Validate(scene);
        return Session.UpdatePortal(id, scene, placement);
    }

    public UiHostUpdate Update(UiScene scene, UiHostPlacementContext placement)
    {
        ThrowIfRetired();
        if (_terminal != null)
            throw new InvalidOperationException(
                "Terminal hosts must be recomposed through their Runtime-owned Terminal session.");
        UiSemanticStardewCapabilities.Validate(scene);
        return Session.UpdateRoot(scene, placement);
    }

    internal void SetTerminalTheme(UiTheme theme) => _terminal!.SetTheme(theme);

    public UiHostUpdate RecomposeTerminal(
        UiPresentationProfile profile,
        UiHostPlacementContext placement,
        string? locale = null)
    {
        ThrowIfRetired();
        return (_terminal ?? throw new InvalidOperationException("This semantic host does not own a Terminal session."))
            .Recompose(profile, placement, locale);
    }

    internal UiHostUpdate OpenTerminalSection(RuntimeSymbolId section)
    {
        ThrowIfRetired();
        return (_terminal ?? throw new InvalidOperationException("This semantic host does not own a Terminal session."))
            .OpenSection(section);
    }

    internal bool EvictTerminalSection(RuntimeSymbolId section)
    {
        ThrowIfRetired();
        return (_terminal ?? throw new InvalidOperationException("This semantic host does not own a Terminal session."))
            .EvictSection(section);
    }

    internal UiHostUpdate UpdateOverlay(UiScene scene, RuntimeRect viewport)
    {
        ThrowIfRetired();
        ArgumentNullException.ThrowIfNull(scene);
        if (_terminal != null)
            throw new InvalidOperationException("Terminal hosts cannot be updated through the overlay lifecycle.");
        if (Session.Root.Policy.Kind != UiHostKind.Overlay || scene.Root.Policy.Kind != UiHostKind.Overlay)
            throw new InvalidOperationException("Only Overlay hosts can be updated through the overlay lifecycle.");
        UiSemanticStardewCapabilities.Validate(scene);
        return Session.UpdateRoot(scene, new UiHostPlacementContext(viewport));
    }

    internal UiHostUpdate ReflowOverlay(RuntimeRect viewport)
    {
        ThrowIfRetired();
        return UpdateOverlay(Session.Root.Scene, viewport);
    }

    public void Render(SpriteBatch batch)
    {
        ThrowIfRetired();
        _runtime!.Render(this, batch);
    }

    internal UiAccessibilityHostSnapshot CaptureAccessibility()
    {
        ThrowIfRetired();
        return Session.Accessibility;
    }

    public void Dispose()
    {
        if (_retiring) return;
        UiSemanticStardewRuntime? runtime = _runtime;
        if (runtime == null && _owner == null) return;
        _retireRequested = true;
        Session.Deactivate();
        _retiring = true;
        try
        {
            DisposeOwner();
            _runtime = null;
            runtime?.Retire(this);
        }
        finally
        {
            _retiring = false;
        }
    }

    internal void Retire()
    {
        if (_retiring) return;
        _retireRequested = true;
        Session.Deactivate();
        _retiring = true;
        try
        {
            DisposeOwner();
            _runtime = null;
        }
        finally
        {
            _retiring = false;
        }
    }

    private void DisposeOwner()
    {
        IDisposable? owner = _owner;
        if (owner == null) return;
        owner.Dispose();
        if (ReferenceEquals(_owner, owner)) _owner = null;
        _terminal = null;
    }

    private void ThrowIfRetired()
    {
        if (_retireRequested || _runtime == null)
            throw new ObjectDisposedException(nameof(UiSemanticStardewHost));
    }
}
