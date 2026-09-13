using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using Hatifect.UI.DevTools;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Registration;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Visual;
using Hatifect.UI.Runtime.Visual.Theming;
using Hatifect.UI.Stardew.Semantic;
using RuntimeRect = Hatifect.UI.Runtime.Layout.UiRect;
using RuntimeColor = Hatifect.UI.Runtime.Visual.UiColor;
using RuntimeSymbolId = Hatifect.UI.UiSymbolId;
using RuntimeTypography = Hatifect.UI.Runtime.Visual.UiTypography;

namespace Hatifect.UI.Stardew.Dogfood;

/// <summary>
/// Explicit first-party diagnostics composition root for live semantic-host dogfooding.
/// The public Stardew surface API has its own consumer-owned session boundary.
/// </summary>
internal sealed class UiSemanticDogfoodCompositionRoot : IDisposable
{
    internal const string DefaultStatusText =
        "Clean-slate Runtime → Stardew composition is active. Use hatifect_ui_acceptance to capture live evidence.";
    private static readonly RuntimeSymbolId DiagnosticsId = new("Hatifect.UI", "dogfood/diagnostics");
    private static readonly RuntimeSymbolId InspectorId = DiagnosticsId.Child("devtools/inspector");
    private static readonly RuntimeSymbolId OverlayId = new("Hatifect.UI", "dogfood/overlay");
    private static readonly RuntimeSymbolId OverlayThemeId = new("Hatifect.UI", "dogfood/theme/overlay");

    private readonly IModHelper _helper;
    private readonly UiSemanticStardewRuntime _runtime;
    private readonly UiTheme _theme = UiSemanticStardewTheme.Default;
    private readonly UiTheme _overlayTheme;
    private readonly UiInspectorSectionFactory _inspector;
    private readonly UiRegistrySnapshot _registry;
    private UiSemanticStardewMenu? _menu;
    private UiSemanticStardewOverlaySession? _overlay;
    private bool _disposed;
    private string? _automationStatus;

    public UiSemanticDogfoodCompositionRoot(GraphicsDevice graphicsDevice, IModHelper helper)
    {
        ArgumentNullException.ThrowIfNull(graphicsDevice);
        _helper = helper ?? throw new ArgumentNullException(nameof(helper));
        _overlayTheme = new UiThemeBuilder(_theme)
            .Set(UiThemeTokens.SurfaceCanvas, UiSurface.Solid(RuntimeColor.FromRgb(0x17191D, 208)))
            .Build(OverlayThemeId);
        _inspector = new UiInspectorSectionFactory(InspectorId, CaptureInspector);
        _registry = new UiRegistryBuilder()
            .TerminalSection(
                DiagnosticsId,
                "Diagnostics",
                TerminalExperience,
                group: "System",
                order: 100)
            .TerminalSectionOwned(
                InspectorId,
                "Inspector",
                _inspector.Create,
                group: "System",
                order: 110)
            .Window(
                OverlayId,
                "Semantic overlay diagnostics",
                OverlayExperience,
                host: UiHostPolicies.Overlay)
            .Freeze();
        _runtime = new UiSemanticStardewRuntime(graphicsDevice, ResolveFont, ResolveTexture);
    }

    public bool IsOpen => _menu != null && ReferenceEquals(Game1.activeClickableMenu, _menu);
    public bool IsOverlayVisible => _overlay?.Visible == true;
    internal UiSemanticStardewMenu? AutomationMenu => IsOpen ? _menu : null;

    internal void SetAutomationStatus(string? text)
    {
        if (!UiAutomatedAcceptanceScenarioRegistry.IsRegistrationEnabled)
            throw new InvalidOperationException("The visual text probe requires the isolated automated harness.");
        _automationStatus = text;
    }

    public bool TryOpen()
    {
        ThrowIfDisposed();
        RetireDetachedMenu();
        if (_menu != null || Game1.activeClickableMenu != null) return false;

        UiSemanticStardewMenu? staged = null;
        staged = _runtime.CreateTerminalMenu(
            _registry,
            _theme,
            ResolveProfile,
            DiagnosticsId,
            resolveLocale: ResolveLocale,
            onClosed: () => RetireReference(staged));
        _menu = staged;
        Game1.activeClickableMenu = staged;
        return true;
    }

    public bool TryOpenOverlay()
    {
        ThrowIfDisposed();
        if (_overlay != null) return false;

        RuntimeRect viewport = UiSemanticStardewMenu.CaptureViewport();
        UiScene scene = new UiSceneComposer(_overlayTheme, _registry).Compose(
            new UiInvocationService(_registry).Invoke(OverlayId, ResolveProfile(viewport)),
            locale: ResolveLocale());
        UiSemanticStardewOverlaySession? staged = null;
        try
        {
            staged = _runtime.CreateOverlay(
                _helper,
                scene,
                UiSemanticStardewOverlayRenderLayer.Hud,
                onClosed: () => RetireOverlayReference(staged));
            staged.CloseRequestHandler = CloseOverlay;
            _overlay = staged;
            staged.Show();
            return true;
        }
        catch (Exception error)
        {
            RetireOverlayReference(staged);
            if (staged == null) throw;
            throw DisposeAfterFailure(
                staged,
                error,
                "Semantic overlay activation failed and its staged session also failed to dispose.");
        }
    }

    public bool Close()
    {
        if (_disposed || _menu == null) return false;
        UiSemanticStardewMenu menu = _menu;
        _menu = null;
        if (ReferenceEquals(Game1.activeClickableMenu, menu))
            menu.exitThisMenu();
        else
            menu.Dispose();
        return true;
    }

    public bool CloseOverlay()
    {
        if (_disposed || _overlay == null) return false;
        UiSemanticStardewOverlaySession overlay = _overlay;
        _overlay = null;
        overlay.Dispose();
        return true;
    }

    public void Reset()
    {
        ThrowIfDisposed();
        var failures = new List<Exception>();
        Attempt(RetireOverlay, failures);
        Attempt(() => RetireMenu(clearActiveMenu: true), failures);
        Attempt(_runtime.ResetGeneratedResources, failures);
        ThrowIfFailures("Semantic dogfood reset failed.", failures);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var failures = new List<Exception>();
        Attempt(RetireOverlay, failures);
        Attempt(() => RetireMenu(clearActiveMenu: true), failures);
        Attempt(_runtime.Dispose, failures);
        ThrowIfFailures("Semantic dogfood disposal failed.", failures);
    }

    private UiExperienceDefinition TerminalExperience()
        => new UiExperienceBuilder(DiagnosticsId, "Semantic host diagnostics")
            .Monitor(
                "Status",
                new UiConstantSource<string>(_automationStatus ?? DefaultStatusText))
            .Build();

    private (UiInspectorSnapshot Snapshot, Action Close) CaptureInspector()
    {
        UiSemanticStardewMenu menu = _menu
            ?? throw new InvalidOperationException("The semantic Inspector requires an active Terminal host.");
        if (!ReferenceEquals(Game1.activeClickableMenu, menu))
            throw new InvalidOperationException("The semantic Inspector cannot capture a detached Terminal host.");
        var context = menu.CaptureInspectionContext();
        return (
            UiInspector.Capture(context.Invocation, context.Runtime.CaptureDiagnostics()),
            menu.RequestClose);
    }

    private UiExperienceDefinition OverlayExperience()
        => new UiExperienceBuilder(OverlayId, "Semantic overlay diagnostics")
            .Monitor(
                "Status",
                new UiConstantSource<string>(
                    "Semantic HUD overlay active. Press Escape or controller B to close."))
            .Build();

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
            $"The semantic diagnostics composition requested unregistered texture '{texture}'.");

    private void RetireDetachedMenu()
    {
        if (_menu == null || ReferenceEquals(Game1.activeClickableMenu, _menu)) return;
        UiSemanticStardewMenu detached = _menu;
        _menu = null;
        detached.Dispose();
    }

    private void RetireMenu(bool clearActiveMenu)
    {
        UiSemanticStardewMenu? menu = _menu;
        _menu = null;
        if (menu == null) return;
        try { menu.Dispose(); }
        finally
        {
            if (clearActiveMenu && ReferenceEquals(Game1.activeClickableMenu, menu))
                Game1.activeClickableMenu = null;
        }
    }

    private void RetireOverlay()
    {
        UiSemanticStardewOverlaySession? overlay = _overlay;
        _overlay = null;
        overlay?.Dispose();
    }

    private void RetireReference(UiSemanticStardewMenu? menu)
    {
        if (menu != null && ReferenceEquals(_menu, menu)) _menu = null;
    }

    private void RetireOverlayReference(UiSemanticStardewOverlaySession? overlay)
    {
        if (overlay != null && ReferenceEquals(_overlay, overlay)) _overlay = null;
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

    private static void Attempt(Action action, ICollection<Exception> failures)
    {
        try { action(); }
        catch (Exception error) { failures.Add(error); }
    }

    private static void ThrowIfFailures(string message, ICollection<Exception> failures)
    {
        if (failures.Count > 0) throw new AggregateException(message, failures);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(UiSemanticDogfoodCompositionRoot));
    }
}
