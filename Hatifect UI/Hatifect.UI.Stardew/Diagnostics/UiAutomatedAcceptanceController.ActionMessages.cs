using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;
using Hatifect.UI.Runtime.Actions;
using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Rendering;

namespace Hatifect.UI.Stardew;

internal sealed partial class UiAutomatedAcceptanceController
{
    private readonly List<ActionMessageCapture> _actionMessageCaptures = new();
    private bool _actionMessageLanguageSaved;
    private LocalizedContentManager.LanguageCode _actionMessageOriginalLanguage;
    private StardewValley.GameData.ModLanguage? _actionMessageOriginalModLanguage;
    private Exception? _actionMessageCaptureError;

    private void BeginActionMessageFrames()
    {
        _actionMessageOriginalLanguage = LocalizedContentManager.CurrentLanguageCode;
        _actionMessageOriginalModLanguage = LocalizedContentManager.CurrentModLanguage;
        _actionMessageLanguageSaved = true;
        LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.en;
        OpenActionMenu(NewActionProbe("messages-en"), "en");
        _actionPhase = -2;
        _actionPhaseTicks = 0;
    }

    private bool AdvanceActionMessageFrames()
    {
        if (_actionMessageCaptureError is { } error)
            throw new InvalidOperationException("Completed action message capture failed.", error);
        string locale = _actionPhase == -2 ? "en" : "ru-RU";
        ActionPumpProbe probe = Probe(_actionPhase == -2 ? "messages-en" : "messages-ru");
        if (!_actionMessageCaptures.Any(capture => capture.Locale == locale && capture.State == "Running"))
            return true;
        // A real native Update/Draw has presented Running before the worker is released.
        probe.CompleteFromWorker();
        if (!_actionMessageCaptures.Any(capture => capture.Locale == locale && capture.State == "Completed"))
            return true;
        RequireAction(Delivered(probe.Name), "The visible action result did not preserve owning-thread delivery.");
        CloseActionPresentation();
        if (_actionPhase == -2)
        {
            LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.ru;
            OpenActionMenu(NewActionProbe("messages-ru"), "ru-RU");
            _actionPhase = -1;
            _actionPhaseTicks = 0;
        }
        else
        {
            RestoreActionMessageLanguage();
            BeginActionPumpLifecycle();
            _actionPhaseTicks = 0;
        }
        return true;
    }

    private void ObserveActionMessageFrame()
    {
        var runtime = _actionHost?.Session.Root;
        if (runtime is null || !ReferenceEquals(Game1.activeClickableMenu, _actionMenu) || Game1.fadeToBlackAlpha > 0)
            return;
        var completed = runtime.LastCompletedRender;
        if (completed.Sequence == 0 || completed.FrameVersion != runtime.FrameVersion || completed.SceneVersion != runtime.AcceptedVersion)
            return;
        string locale = _actionPhase == -2 ? "en" : "ru-RU";
        ActionPumpProbe probe = Probe(_actionPhase == -2 ? "messages-en" : "messages-ru");
        var status = runtime.Actions.Status(probe.Definition);
        if (status?.State is not (UiActionState.Running or UiActionState.Completed)) return;
        string state = status.State.ToString();
        if (_actionMessageCaptures.Any(capture => capture.Locale == locale && capture.State == state)) return;
        string expected = status.State == UiActionState.Running
            ? (locale == "en" ? "Running…" : "Выполняется…")
            : (locale == "en" ? "Completed." : "Выполнено.");
        var node = Flatten(runtime.Accessibility.Root).Single(item => item.Id == probe.Definition.Id.Child("scene/button"));
        UiTextPrimitive text = runtime.Frame.Primitives.OfType<UiTextPrimitive>().Single(item => item.Node == node.Id && item.Text == expected);
        RequireAction(node.Value == expected && text.Bounds.Width > 0 && text.Bounds.Height > 0 &&
            text.Clip.Width > 0 && text.Clip.Height > 0 && runtime.Scene.MeasurementContext.Locale == locale,
            "Rendered and accessible action messages disagree.");
        string name = "action-message-" + locale + "-" + state.ToLowerInvariant();
        CaptureScreenshot(name);
        _actionMessageCaptures.Add(new(locale, state, expected, completed.Sequence, runtime.FrameVersion,
            node.Enabled, probe.Callbacks, $"screenshots/{name}.png", $"screenshots/{name}-ui-layer.png"));
    }

    private void RestoreActionMessageLanguage()
    {
        if (!_actionMessageLanguageSaved) return;
        if (_actionMessageOriginalLanguage == LocalizedContentManager.LanguageCode.mod)
            LocalizedContentManager.SetModLanguage(_actionMessageOriginalModLanguage
                ?? throw new InvalidOperationException("The original custom language is missing."));
        else LocalizedContentManager.CurrentLanguageCode = _actionMessageOriginalLanguage;
        RequireAction(LocalizedContentManager.CurrentLanguageCode == _actionMessageOriginalLanguage &&
            ReferenceEquals(LocalizedContentManager.CurrentModLanguage, _actionMessageOriginalModLanguage),
            "Action message acceptance did not restore the original language.");
        _actionMessageLanguageSaved = false;
    }

    private sealed record ActionMessageCapture(string Locale, string State, string Text, long CompletedPass,
        long FrameVersion, bool Enabled, int Callbacks, string Screenshot, string UiLayerScreenshot);
}
