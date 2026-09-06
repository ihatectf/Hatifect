using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using Hatifect.UI.Stardew.Semantic;
using RuntimeAccessibilityRole = Hatifect.UI.Runtime.Accessibility.UiAccessibilityRole;

namespace Hatifect.UI.Stardew;

internal sealed partial class UiAutomatedAcceptanceController
{
    private const int MaximumNativeWaitTicks = 18000;
    private readonly List<NativeInputCapture> _nativeCaptures = new(5);
    private UiNativeInputGate? _nativeInputGate;
    private UiNativeInputObservation? _nativeLastObservation;
    private string _nativeExpectedText = string.Empty;
    private long _nativeFrame;
    private long _nativePointerPressed;
    private long _nativePointerReleased;
    private long _nativeTabPressed;
    private long _nativeBackspacePressed;
    private int _nativeWaitTicks;
    private bool _nativeInputSubscribed;
    private bool _nativeInputCompleted;

    private void BeginNativeInput()
    {
        _nativeExpectedText = "native-" + _runId[..Math.Min(8, _runId.Length)];
        _nativeInputGate = new UiNativeInputGate(_nativeExpectedText);
        _nativeCaptures.Clear();
        _nativeLastObservation = null;
        _nativeInputCompleted = false;
        _nativeFrame = _nativePointerPressed = _nativePointerReleased = _nativeTabPressed = _nativeBackspacePressed = 0;
        _nativeWaitTicks = 0;
        _dogfood.CloseOverlay();
        _dogfood.Close();
        _dogfood.SetAutomationStatus("Native input: click Inspector, press Tab, click Search, enter " + _nativeExpectedText + ", then press Backspace.");
        EnsureMenu();
        _helper.Events.Input.ButtonPressed += OnNativeButtonPressed;
        _helper.Events.Input.ButtonReleased += OnNativeButtonReleased;
        _nativeInputSubscribed = true;
    }

    private bool AdvanceNativeInput()
    {
        if (_nativeInputGate is null) return false;
        if (_nativeInputGate.Phase == UiNativeInputPhase.Complete)
        {
            StopNativeInput();
            _nativeInputCompleted = true;
            _capturePending = true;
            return true;
        }
        if (++_nativeWaitTicks >= MaximumNativeWaitTicks)
            throw new InvalidOperationException($"HARNESS-NATIVE-INPUT-NOT-OBSERVED: phase={_nativeInputGate.Phase}");
        return true;
    }

    private void OnNativeButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (_nativeInputGate is null || !Game1.game1.IsActive) return;
        if (e.Button == SButton.MouseLeft) _nativePointerPressed++;
        else if (e.Button == SButton.Tab) _nativeTabPressed++;
        else if (e.Button == SButton.Back) _nativeBackspacePressed++;
    }

    private void OnNativeButtonReleased(object? sender, ButtonReleasedEventArgs e)
    {
        if (_nativeInputGate is not null && Game1.game1.IsActive && e.Button == SButton.MouseLeft)
            _nativePointerReleased++;
    }

    private void ObserveNativeInputFrame()
    {
        UiSemanticStardewMenu? menu = _dogfood.AutomationMenu;
        var accessibility = menu?.CaptureInspectionContext().Runtime.Accessibility;
        var nodes = accessibility is null ? null : Flatten(accessibility.Root).ToArray();
        var field = nodes?.FirstOrDefault(node => node.Role == RuntimeAccessibilityRole.TextField);
        var observation = new UiNativeInputObservation(
            accessibility != null && UiNativeInputGate.IsVisible(accessibility.Root) && Game1.game1.IsActive && Game1.fadeToBlackAlpha <= 0f
                && menu!.xPositionOnScreen == 0 && menu.yPositionOnScreen == 0
                && menu.width == Game1.uiViewport.Width && menu.height == Game1.uiViewport.Height,
            field != null && UiNativeInputGate.IsVisible(field),
            nodes?.FirstOrDefault(node => node.Focused && UiNativeInputGate.IsVisible(node))?.Id.ToString(),
            field?.Value,
            _nativePointerPressed, _nativePointerReleased, _nativeTabPressed,
            field?.Focused == true, _nativeBackspacePressed);
        _nativeLastObservation = observation;
        UiNativeInputPhase? completed = _nativeInputGate!.Observe(++_nativeFrame, observation);
        if (completed is null)
        {
            // Retain a bounded live diagnostic while waiting, without writing on every draw.
            if (_nativeFrame % 60 == 0) WriteNativeProgress();
            return;
        }

        string name = "native-" + completed.Value.ToString().ToLowerInvariant();
        CaptureScreenshot(name);
        _nativeCaptures.Add(new NativeInputCapture(completed.Value.ToString(), _nativeFrame,
            observation, $"screenshots/{name}.png", $"screenshots/{name}-ui-layer.png"));
        _nativeWaitTicks = 0;
        switch (completed.Value)
        {
            case UiNativeInputPhase.Pointer:
                Record("semantic.input.native.pointer", true,
                    "SMAPI pointer press/release opened Inspector and rendered its text field.");
                break;
            case UiNativeInputPhase.Keyboard:
                Record("semantic.input.native.keyboard", true,
                    "SMAPI Tab input focused the rendered text field.");
                break;
            case UiNativeInputPhase.Text:
                Record("semantic.input.native.text", true,
                    "A fresh SMAPI pointer gesture selected the field and the session text probe rendered through the native input path.");
                break;
            case UiNativeInputPhase.Backspace:
                Record("semantic.input.native.backspace", true,
                    "A new SMAPI Backspace event removed exactly the final character from the focused field.");
                break;
        }
        WriteNativeProgress();
    }

    private void WriteNativeProgress()
    {
        string path = Path.Combine(_artifactDirectory, "native-input-progress.json");
        var payload = new
        {
            protocolVersion = 1,
            runId = _runId,
            scenario = _scenario,
            phase = _nativeInputGate!.Phase.ToString(),
            expectedText = _nativeExpectedText,
            completedFrame = _nativeFrame,
            lastObservation = _nativeLastObservation,
            inputEvidence = "SMAPI events and rendered postconditions; physical versus OS-injected origin is recorded by the external observer.",
            captures = _nativeCaptures.ToArray()
        };
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private void StopNativeInput()
    {
        if (_nativeInputSubscribed)
        {
            _helper.Events.Input.ButtonPressed -= OnNativeButtonPressed;
            _helper.Events.Input.ButtonReleased -= OnNativeButtonReleased;
            _nativeInputSubscribed = false;
        }
        if (_nativeInputGate is null) return;
        _nativeInputGate = null;
        _dogfood.SetAutomationStatus(null);
    }

    private void FailNativeInputCapture(Exception error)
    {
        RetainTerminalFailure("HARNESS-NATIVE-INPUT-CAPTURE-EXCEPTION", error);
        try
        {
            StopNativeInput();
            try { WriteDiagnostics(); }
            catch (Exception diagnosticsError)
            {
                _monitor.Log($"Could not persist native input failure diagnostics: {diagnosticsError}", LogLevel.Error);
            }
            FailUnrecorded(_terminalFailure!.Reason);
        }
        finally { _exitPending = true; }
    }

    private sealed record NativeInputCapture(string Phase, long CompletedFrame,
        UiNativeInputObservation Observation, string Screenshot, string UiLayerScreenshot);
}
