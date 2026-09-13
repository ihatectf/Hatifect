using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using Hatifect.UI.Stardew.Dogfood;
using Hatifect.UI.Stardew.Semantic;

namespace Hatifect.UI.Stardew;

internal sealed partial class UiAutomatedAcceptanceController
{
    private static readonly float[] VisualScales = { 0.75f, 1f, 1.25f, 1.5f };
    private const int VisualStateCount = 8;
    private const int MaximumVisualWaitFrames = 300;
    private readonly List<VisualMatrixCapture> _visualCaptures = new(VisualStateCount);
    private UiRenderedStateGate? _visualGate;
    private LocalizedContentManager.LanguageCode _visualOriginalLanguage;
    private float _visualOriginalScale;
    private bool _visualSettingsSaved;
    private bool _visualSettingsRestored;
    private bool _visualAdvancePending;
    private bool _visualCompletePending;
    private int _visualIndex;
    private int _visualWaitFrames;
    private long _visualFrame;
    private string _visualProbe = string.Empty;
    private string _visualLocale = string.Empty;
    private float _visualScale;

    private void BeginVisualMatrix()
    {
        UiSemanticStardewCapabilities.Validate(UiSemanticStardewTheme.Default);
        _visualOriginalLanguage = LocalizedContentManager.CurrentLanguageCode;
        _visualOriginalScale = Game1.options.desiredUIScale;
        _visualSettingsSaved = true;
        _visualSettingsRestored = false;
        _visualCaptures.Clear();
        _visualIndex = 0;
        ApplyVisualTarget();
    }

    // All setting changes and aggregate continuation happen on Update, never inside Draw.
    private bool AdvanceVisualMatrix()
    {
        if (_visualCompletePending)
        {
            _visualCompletePending = false;
            _visualSettingsSaved = false;
            _visualSettingsRestored = true;
            Record("semantic.viewport.reflow", _visualCaptures.Count == VisualStateCount,
                "Every captured menu matches the scaled viewport; original locale/scale were restored and rendered before continuation.");
            bool asynchronous = string.Equals(_scenario, "all", StringComparison.Ordinal)
                && ExecuteAllNamedUiScenarios();
            if (!asynchronous) _capturePending = true;
            return true;
        }
        if (_visualAdvancePending)
        {
            _visualAdvancePending = false;
            ApplyVisualTarget();
            return true;
        }
        return _visualGate != null;
    }

    private void ApplyVisualTarget()
    {
        bool restoring = _visualIndex == VisualStateCount;
        LocalizedContentManager.LanguageCode language = restoring
            ? _visualOriginalLanguage
            : _visualIndex < VisualScales.Length
                ? LocalizedContentManager.LanguageCode.en
                : LocalizedContentManager.LanguageCode.ru;
        _visualLocale = language.ToString();
        _visualScale = restoring ? _visualOriginalScale : VisualScales[_visualIndex % VisualScales.Length];
        _visualProbe = restoring ? UiSemanticDogfoodCompositionRoot.DefaultStatusText
            : language == LocalizedContentManager.LanguageCode.ru
                ? "Русский интерфейс: ёжик, щука, груз №42."
                : "English interface: cargo #42, focus and scale.";
        _visualGate = new UiRenderedStateGate(_visualLocale, _visualScale, UiSemanticStardewTheme.Id);
        _visualWaitFrames = 0;
        LocalizedContentManager.CurrentLanguageCode = language;
        Game1.options.desiredUIScale = _visualScale;
        _dogfood.SetAutomationStatus(restoring ? null : _visualProbe);
        _dogfood.CloseOverlay();
        _dogfood.Close();
        EnsureMenu();
    }

    private void ObserveVisualMatrixFrame()
    {
        UiSemanticStardewMenu? menu = _dogfood.AutomationMenu;
        var context = menu?.CaptureInspectionContext();
        var accessibility = context?.Runtime.Accessibility;
        var measurement = context?.Runtime.Scene.MeasurementContext;
        var presentation = Game1.graphics.GraphicsDevice.PresentationParameters;
        var observation = new UiRenderedStateObservation(
            LocalizedContentManager.CurrentLanguageCode.ToString(),
            measurement?.Locale ?? string.Empty,
            measurement?.Theme.ToString() ?? string.Empty,
            Game1.options.desiredUIScale,
            Game1.options.baseUIScale,
            Game1.options.uiScale,
            presentation.BackBufferWidth, presentation.BackBufferHeight,
            Game1.uiViewport.Width, Game1.uiViewport.Height,
            menu?.xPositionOnScreen ?? int.MinValue, menu?.yPositionOnScreen ?? int.MinValue,
            menu?.width ?? 0, menu?.height ?? 0,
            accessibility != null && PositiveGeometry(accessibility.Root),
            accessibility != null && Flatten(accessibility.Root).Any(node =>
                string.Equals(node.Value, _visualProbe, StringComparison.Ordinal)
                && node.Clip.Width > 0 && node.Clip.Height > 0
                && node.Clip.X + node.Clip.Width > node.Bounds.X
                && node.Clip.Y + node.Clip.Height > node.Bounds.Y
                && node.Bounds.X + node.Bounds.Width > node.Clip.X
                && node.Bounds.Y + node.Bounds.Height > node.Clip.Y),
            Game1.fadeToBlackAlpha <= 0f);
        if (!_visualGate!.Observe(++_visualFrame, observation))
        {
            if (++_visualWaitFrames >= MaximumVisualWaitFrames)
                throw new InvalidOperationException(
                    $"HARNESS-VISUAL-STATE-NOT-RENDERED: locale={_visualLocale}, scale={_visualScale}; {observation}");
            return;
        }

        if (_visualIndex == VisualStateCount)
            _visualCompletePending = true;
        else
        {
            string name = $"matrix-{_visualLocale}-{(_visualScale * 100).ToString("0", CultureInfo.InvariantCulture)}-dark";
            CaptureScreenshot(name);
            _visualCaptures.Add(new VisualMatrixCapture(
                _visualLocale, _visualScale, _visualFrame, _visualProbe, observation,
                $"screenshots/{name}.png", $"screenshots/{name}-ui-layer.png"));
            RecordVisualStateChecks();
            _visualIndex++;
            _visualAdvancePending = true;
        }
        _visualGate = null;
    }

    private void RecordVisualStateChecks()
    {
        // Record scale checks while that scale is still rendered, so host-check metrics agree.
        if (_visualLocale == "ru")
            Record($"semantic.scale.{(_visualScale * 100).ToString("0", CultureInfo.InvariantCulture)}",
                _visualCaptures.Count(item => item.Scale == _visualScale) == 2,
                $"Captured applied scale {_visualScale.ToString(CultureInfo.InvariantCulture)} after two consistent frames in each locale.");
        if ((_visualIndex % VisualScales.Length) == VisualScales.Length - 1)
            Record($"semantic.locale.{_visualLocale}",
                _visualCaptures.Count(item => item.Locale == _visualLocale) == VisualScales.Length,
                $"Captured four stable rendered {_visualLocale} states with matching scene locale and visible text probe.");
        if (_visualCaptures.Count == VisualStateCount)
            Record("semantic.theme.matrix", true,
                $"Captured the implemented {UiSemanticStardewTheme.Id} preset in eight rendered locale/scale states.");
    }

    private void FailVisualMatrixCapture(Exception error)
    {
        RetainTerminalFailure("HARNESS-VISUAL-MATRIX-CAPTURE-EXCEPTION", error);
        try
        {
            RestoreVisualSettingsAfterFailure();
            try { WriteDiagnostics(); }
            catch (Exception diagnosticsError)
            {
                _monitor.Log($"Could not persist visual matrix failure diagnostics: {diagnosticsError}", LogLevel.Error);
            }
            FailUnrecorded(_terminalFailure!.Reason);
        }
        finally
        {
            // A persistence failure must still release the owned game after the draw callback.
            _exitPending = true;
        }
    }

    private void RestoreVisualSettingsAfterFailure()
    {
        _visualGate = null;
        _visualAdvancePending = false;
        _visualCompletePending = false;
        if (!_visualSettingsSaved) return;
        bool restored = true;
        Restore(() => LocalizedContentManager.CurrentLanguageCode = _visualOriginalLanguage);
        Restore(() => Game1.options.desiredUIScale = _visualOriginalScale);
        Restore(() => _dogfood.SetAutomationStatus(null));
        _visualSettingsSaved = !restored;

        void Restore(Action action)
        {
            try { action(); }
            catch (Exception error)
            {
                restored = false;
                RetainTerminalFailure("HARNESS-VISUAL-SETTINGS-RESTORE-EXCEPTION", error);
                _monitor.Log($"Could not restore visual fixture settings: {error}", LogLevel.Error);
            }
        }
    }

    private sealed record VisualMatrixCapture(
        string Locale, float Scale, long CompletedFrame, string ProbeText,
        UiRenderedStateObservation Observation, string Screenshot, string UiLayerScreenshot);
}
