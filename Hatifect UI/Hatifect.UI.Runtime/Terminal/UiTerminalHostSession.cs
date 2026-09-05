using System;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Activation;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Runtime.Registration;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Visual.Theming;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Runtime.Terminal;

/// <summary>
/// Internal/provisional host boundary that couples one Terminal shell, portal host, interaction
/// recomposition, and route-follow lifecycle. Platform adapters normalize input into this session;
/// they do not interpret Terminal routes.
/// </summary>
internal sealed class UiTerminalHostSession : IUiPlatformInputSession, IDisposable
{
    private readonly UiTerminalShellSession _shell;
    private readonly Action<UiScene>? _validateScene;
    private readonly Action<UiSymbolId>? _onRouteRequested;
    private UiPresentationProfile _profile;
    private UiHostPlacementContext _placement;
    private UiInvocationResult _currentInvocation = null!;
    private string? _locale;
    private bool _disposed;

    public UiTerminalHostSession(
        UiRegistrySnapshot registry,
        UiTheme theme,
        UiHostPlacementContext placement,
        IUiPlatformBridge platform,
        UiPresentationProfile profile,
        UiSymbolId? initialSection = null,
        Func<UiExperienceDescriptor, UiTerminalSectionAssets>? resolveAssets = null,
        UiSemanticCatalog? catalog = null,
        string? locale = null,
        Action<UiScene>? validateScene = null,
        Action<UiSymbolId>? onRouteRequested = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(theme);
        _placement = placement ?? throw new ArgumentNullException(nameof(placement));
        ArgumentNullException.ThrowIfNull(platform);
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _locale = locale;
        _validateScene = validateScene;
        _onRouteRequested = onRouteRequested;
        _shell = new UiTerminalShellSession(registry, theme, resolveAssets, catalog);
        try
        {
            UiTerminalFrame frame = initialSection is { } section
                ? _shell.Open(section, profile, locale)
                : _shell.OpenFirstAvailable(profile, locale);
            ValidateScene(frame.Scene);
            Host = new UiPortalHostSession(
                frame.Scene,
                placement,
                platform,
                composeInteraction: ComposeInteraction);
            _currentInvocation = frame.Invocation;
            UiPortalDispatch focus = Host.MoveFocus(UiNavigationDirection.Next);
            if (!focus.Consumed || Host.Root.Interactions.Snapshot.Focused == null)
                throw new InvalidOperationException(
                    "The Terminal host could not establish its initial runtime-owned focus target.");
        }
        catch (Exception error)
        {
            throw DisposeAfterFailure(
                _shell,
                error,
                "Terminal host construction failed and its shell owner also failed to dispose.");
        }
    }

    internal UiPortalHostSession Host { get; }
    public UiSymbolId? ActiveSection => _shell.ActiveSection;
    internal UiInvocationResult CurrentInvocation
    {
        get
        {
            EnsureActive();
            return _currentInvocation;
        }
    }

    internal void SetTheme(UiTheme theme) { EnsureActive(); _shell.SetTheme(theme); }

    public UiHostUpdate Recompose(
        UiPresentationProfile profile,
        UiHostPlacementContext placement,
        string? locale = null)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(placement);
        UiTerminalFrame frame = _shell.Recompose(profile, locale, Host.Root.Interactions.Snapshot);
        ValidateScene(frame.Scene);
        UiHostUpdate update = Host.UpdateRoot(frame.Scene, placement);
        _currentInvocation = frame.Invocation;
        _profile = profile;
        _placement = placement;
        _locale = locale;
        return update;
    }

    public UiPortalDispatch PressPointer(UiPoint point)
        => Follow(Dispatch(() => Host.PressPointer(point)));

    public UiPortalDispatch MovePointer(UiPoint point)
        => Follow(Dispatch(() => Host.MovePointer(point)));

    public UiPortalDispatch ReleasePointer(UiPoint point)
        => Follow(Dispatch(() => Host.ReleasePointer(point)));

    public UiPortalDispatch Cancel()
        => Follow(Dispatch(Host.Cancel));

    public UiPortalDispatch UnhandledInput()
        => Dispatch(Host.UnhandledInput);

    public UiPortalDispatch MoveFocus(UiNavigationDirection direction)
        => Follow(Dispatch(() => Host.MoveFocus(direction)));

    public UiPortalDispatch Submit()
        => Follow(Dispatch(Host.Submit));

    public UiPortalDispatch ReplaceText(string? text)
        => Follow(Dispatch(() => Host.ReplaceText(text)));

    public UiPortalDispatch InsertText(string? text)
        => Follow(Dispatch(() => Host.InsertText(text)));

    public UiPortalDispatch EditText(UiTextEditAction action, bool extendSelection = false)
        => Follow(Dispatch(() => Host.EditText(action, extendSelection)));

    public UiTextEditingSnapshot? FocusedTextEditing
    {
        get
        {
            EnsureActive();
            return Host.FocusedTextEditing;
        }
    }

    public UiPortalScrollDispatch ScrollAt(UiPoint point, float delta)
    {
        EnsureActive();
        return Host.ScrollAt(point, delta);
    }

    internal UiHostUpdate OpenSection(UiSymbolId section)
    {
        EnsureActive();
        UiTerminalFrame frame = _shell.ComposeOpen(
            section,
            _profile,
            _locale,
            Host.Root.Interactions.Snapshot);
        ValidateScene(frame.Scene);
        UiHostUpdate update = Host.UpdateRoot(frame.Scene, _placement);
        _shell.Commit(frame);
        _currentInvocation = frame.Invocation;
        return update;
    }

    internal bool EvictSection(UiSymbolId section)
    {
        EnsureActive();
        return _shell.Evict(section);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Host.Deactivate();
        _shell.Dispose();
    }

    private UiScene ComposeInteraction(UiInteractionSnapshot interaction)
    {
        EnsureActive();
        UiScene scene = _shell.Recompose(_profile, _locale, interaction).Scene;
        ValidateScene(scene);
        return scene;
    }

    private UiPortalDispatch Dispatch(Func<UiPortalDispatch> dispatch)
    {
        EnsureActive();
        return dispatch();
    }

    private UiPortalDispatch Follow(UiPortalDispatch dispatch)
    {
        if (_disposed) return dispatch;
        if (dispatch.Portal != null || dispatch.Interaction is not { } interaction)
            return dispatch;
        if (interaction.Route is not { } route)
            return dispatch;

        _onRouteRequested?.Invoke(route);
        if (_disposed) return dispatch;
        UiTerminalFrame? frame;
        try
        {
            if (!_shell.TryComposeFollow(
                    interaction,
                    _profile,
                    out frame,
                    _locale,
                    Host.Root.Interactions.Snapshot) ||
                frame == null)
                return dispatch;
        }
        catch (UiTerminalSectionActivationPendingException pending) when (pending.Section == route)
        {
            return dispatch;
        }
        catch (UiTerminalSectionActivationRejectedException rejected) when (rejected.Section == route)
        {
            return dispatch;
        }

        if (frame != null)
        {
            ValidateScene(frame.Scene);
            Host.UpdateRoot(frame.Scene, _placement);
            _shell.Commit(frame);
            _currentInvocation = frame.Invocation;
        }
        return dispatch;
    }

    private void ValidateScene(UiScene scene) => _validateScene?.Invoke(scene);

    private void EnsureActive()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(UiTerminalHostSession));
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
