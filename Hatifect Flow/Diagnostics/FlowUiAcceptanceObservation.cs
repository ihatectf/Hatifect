using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using Hatifect.UI;
using Hatifect.UI.Experience;
using Hatifect.UI.Semantics;
using static Hatifect.Flow.Diagnostics.FlowHostAcceptance;

namespace Hatifect.Flow.Diagnostics;

// Exact-harness evidence adapter. UI owns capture/materialization; this consumer compares its
// independent expectations and retains the composed native pixels after the complete game draw.
internal sealed class FlowUiAcceptanceObservation : IDisposable
{
    private readonly IUiSemanticSurfaceObservation _observation;
    private readonly string _artifact;
    private readonly CompletedFrameCapture _component;
    private readonly List<Captured> _captures = new(24);
    private readonly Dictionary<IUiSemanticSurfaceSession, UiSemanticSurfaceSnapshot> _retired = new();
    private FlowUiAcceptance.Frame? _pending;
    private Guid _armedInstance;
    private long _armedRenderPass;
    private string? _ready;
    private Exception? _failure;
    private FailedFrame? _failedFrame;
    private bool _disposed;

    private sealed record Captured(IUiSemanticSurfaceSession Surface, string Name, long Publication,
        UiSemanticSurfaceSnapshot Snapshot, string Json, string Screenshot, string ScreenshotSha256,
        string UiLayer, string UiLayerSha256);
    private sealed record FailedFrame(string Name, UiSemanticSurfaceSnapshot Snapshot, string? Screenshot, string? UiLayer);

    internal FlowUiAcceptanceObservation(IUiSemanticSurfaceObservation observation, string artifact)
    {
        Require(observation.IsEnabled, "The owning UI observation API is disabled for this request.");
        _observation = observation;
        _artifact = artifact;
        _component = new CompletedFrameCapture(CaptureCompletedFrame);
        _component.Game.Components.Add(_component);
    }

    internal bool Observe(FlowUiAcceptance.Frame expected)
    {
        Require(!_disposed, "Flow observation was already disposed.");
        if (_failure is not null) throw new InvalidOperationException("Completed Flow UI observation failed.", _failure);
        VerifyRetained();
        if (_ready == expected.Name)
        {
            Captured last = _captures[^1];
            Require(ReferenceEquals(last.Surface, expected.Surface) && last.Publication == expected.PublicationVersion,
                "The observed Flow publication changed before the driver accepted its evidence.");
            _ready = null;
            return true;
        }
        Require(_ready is null, "The driver skipped completed Flow observation evidence.");
        if (_pending is not null)
            Require(_pending.Name == expected.Name && ReferenceEquals(_pending.Surface, expected.Surface)
                && _pending.PublicationVersion == expected.PublicationVersion,
                "The driver replaced a pending Flow observation.");
        else
        {
            UiSemanticSurfaceSnapshot armed = _observation.Capture(expected.Surface);
            Require(armed.Visible && !armed.Retired, "Flow observation cannot arm a retired surface.");
            _armedInstance = armed.InstanceId;
            _armedRenderPass = armed.CompletedRenderPass;
        }
        _pending = expected;
        return false;
    }

    internal void VerifyRetired(IUiSemanticSurfaceSession surface)
    {
        VerifyRetained();
        Captured previous = _captures.LastOrDefault(value => ReferenceEquals(value.Surface, surface))
            ?? throw new InvalidOperationException("An unobserved Flow surface cannot claim rendered retirement.");
        UiSemanticSurfaceSnapshot actual = _observation.Capture(surface);
        Require(actual.InstanceId == previous.Snapshot.InstanceId && actual.SurfaceId == previous.Snapshot.SurfaceId
            && !actual.Visible && actual.Retired && actual.ExperienceId is null && actual.Environment is null
            && actual.AcceptedFrame is null && actual.RenderedFrame is null && !actual.IsAcceptedFrameRendered
            && actual.Elements.Count == 0 && actual.Texts.Count == 0 && !actual.Truncated && !actual.HasUnmappedContent
            && actual.UnobservedPortalCount == 0,
            "The owning UI retained content or authority after Flow surface retirement.");
        if (_retired.TryGetValue(surface, out UiSemanticSurfaceSnapshot? first))
            Require(JsonSerializer.Serialize(first) == JsonSerializer.Serialize(actual),
                "A retired native Flow handle changed its final lifecycle observation.");
        else
        {
            Require(_retired.Count < 8 && actual.CompletedRenderPass >= previous.Snapshot.CompletedRenderPass,
                "Flow retired-handle evidence exceeded its bound or lost render history.");
            _retired.Add(surface, actual);
        }
        WriteEvidence();
    }

    private void CaptureCompletedFrame()
    {
        if (_disposed || _failure is not null || _pending is not { } expected) return;
        UiSemanticSurfaceSnapshot? observed = null;
        try
        {
            UiSemanticSurfaceSnapshot actual = observed = _observation.Capture(expected.Surface);
            Require(actual.InstanceId == _armedInstance, "The armed Flow surface changed its instance identity.");
            if (actual.CompletedRenderPass <= _armedRenderPass) return;
            if (!actual.IsAcceptedFrameRendered || Game1.fadeToBlackAlpha > 0) return;
            UiEnvironment environment = actual.Environment ?? throw new InvalidOperationException("The accepted scene has no environment.");
            if (environment.Locale != expected.Locale || environment.Scale != expected.Scale
                || environment.InputMode != (expected.Controller ? UiInputMode.Controller : UiInputMode.MouseKeyboard)) return;
            Require(expected.Experience.Publication.Version == expected.PublicationVersion,
                "The Flow publication changed before completed-frame capture.");
            Require(actual.InstanceId != Guid.Empty && actual.Visible && !actual.Retired && !actual.Truncated
                && !actual.HasUnmappedContent && actual.UnobservedPortalCount == 0
                && actual.SurfaceId == expected.Experience.Experience.Id
                && actual.ExperienceId == expected.Experience.Experience.Id && actual.CompletedRenderPass > 0,
                "Flow UI capture has incomplete identity, content or lifecycle evidence.");
            Captured? prior = _captures.LastOrDefault(value => ReferenceEquals(value.Surface, expected.Surface));
            if (prior is not null)
                Require(actual.InstanceId == prior.Snapshot.InstanceId
                    && actual.CompletedRenderPass > prior.Snapshot.CompletedRenderPass,
                    "The retained surface did not complete a fresh native draw.");
            else
                Require(_captures.All(value => value.Snapshot.InstanceId != actual.InstanceId),
                    "A new native surface reused an earlier instance identity.");
            VerifyContents(expected, actual);
            VerifyRetained();
            Require(_captures.Count < 24 && _captures.All(value => value.Name != expected.Name),
                "Flow frame evidence repeated a name or exceeded its fixed matrix.");
            (string screenshot, string layer) = CapturePixels(expected.Name);
            _captures.Add(new(expected.Surface, expected.Name, expected.PublicationVersion, actual,
                JsonSerializer.Serialize(actual), screenshot, HashFile(screenshot), layer, HashFile(layer)));
            WriteEvidence();
            _pending = null;
            _ready = expected.Name;
        }
        catch (Exception error)
        {
            _failure = error;
            if (observed is not null)
            {
                _failedFrame = new(expected.Name, observed, null, null);
                try
                {
                    (string screenshot, string layer) = CapturePixels("failed-" + expected.Name);
                    _failedFrame = _failedFrame with { Screenshot = screenshot, UiLayer = layer };
                }
                catch (Exception capture) { _failure = new AggregateException(error, capture); }
            }
            try { WriteEvidence(); }
            catch (Exception write) { _failure = new AggregateException(_failure ?? error, write); }
        }
    }

    private static void VerifyContents(FlowUiAcceptance.Frame expected, UiSemanticSurfaceSnapshot actual)
    {
        bool russian = expected.Locale == "ru-RU";
        UiSymbolId id = expected.Experience.Experience.Id;
        string title = russian ? "Отправление Flowline · диагностика" : "Flowline shipment · diagnostic";
        Require(actual.Texts.Any(row => row.Text == title) && actual.Elements.Any(row => row.Name == title),
            "The actual Flow title lost its locale or explicit diagnostic label.");
        Field("cargo", russian ? "Груз" : "Cargo", expected.Cargo);
        Field("route", russian ? "Маршрут" : "Route", expected.Route);
        Field("state", russian ? "Состояние" : "State", expected.State);
        Field("result", russian ? "Результат" : "Result", expected.Result);
        Field("availability", russian ? "Доступность" : "Availability", expected.Availability);

        string[] keys = { "reserve", "cancel", "retry", "reconcile", "return" };
        string[] labels = russian
            ? new[] { "Отправить", "Отменить", "Повторить доставку", "Проверить передачу", "Вернуть груз в источник" }
            : new[] { "Dispatch", "Cancel", "Retry delivery", "Check transfer", "Return cargo to source" };
        Require(actual.Elements.Count(row => row.ActionId is not null) == keys.Length,
            "The actual Flow action set is incomplete or duplicated.");
        for (int i = 0; i < keys.Length; i++)
        {
            UiSymbolId action = id.Child("action/" + keys[i]);
            var rows = actual.Elements.Where(row => row.ActionId == action).ToArray();
            Require(rows.Length == 1 && rows[0].SemanticId == action && rows[0].Name == labels[i]
                && rows[0].Enabled == expected.EnabledActions.Contains(keys[i], StringComparer.Ordinal)
                && actual.Texts.Any(row => row.SemanticId == action && row.Text == labels[i]),
                "The actual localized Flow action differs from its expected availability: " + keys[i]);
        }

        void Field(string key, string label, string value)
        {
            UiSymbolId semantic = id.Child("element/" + key);
            var rows = actual.Elements.Where(row => row.SemanticId == semantic).ToArray();
            Require(rows.Length == 1 && rows[0].Name == label && rows[0].Value == value,
                "The accepted Flow field differs from its expected text: " + key);
            Require(actual.Texts.Any(row => row.SemanticId == semantic && row.Text == label),
                "The prepared frame omitted the localized Flow field label: " + key);
            string displayed = rows[0].Value ?? "";
            Require(displayed.Length == 0 || actual.Texts.Any(row => row.SemanticId == semantic && row.Text == displayed),
                "The accepted accessibility value differs from the actual submitted Flow text: " + key);
        }
    }

    private void VerifyRetained()
    {
        foreach (Captured capture in _captures)
            Require(JsonSerializer.Serialize(capture.Snapshot) == capture.Json,
                "Later Flow changes mutated a retained UI observation.");
    }

    private (string Screenshot, string Layer) CapturePixels(string name)
    {
        GraphicsDevice graphics = Game1.graphics.GraphicsDevice;
        Require(graphics.RenderTargetCount == 0, "Flow pixel capture requires the completed composed back buffer.");
        int width = graphics.PresentationParameters.BackBufferWidth, height = graphics.PresentationParameters.BackBufferHeight;
        Require(width > 0 && height > 0 && width <= 8192 && height <= 8192 && (long)width * height <= 33554432,
            "Flow pixel capture exceeds its supported buffer bound.");
        var pixels = new Color[checked(width * height)];
        graphics.GetBackBufferData(pixels);
        using var texture = new Texture2D(graphics, width, height, false, SurfaceFormat.Color);
        texture.SetData(pixels);
        var ui = Game1.game1.uiScreen ?? throw new InvalidOperationException("The composed UI layer is missing.");
        Require(!ui.IsDisposed, "The composed UI layer was disposed before capture.");
        return WritePixels(_artifact, name, stream => texture.SaveAsPng(stream, width, height),
            stream => ui.SaveAsPng(stream, ui.Width, ui.Height));
    }

    internal static (string Screenshot, string Layer) WritePixels(string artifact, string name,
        Action<Stream> screenshot, Action<Stream> uiLayer)
    {
        Require(name.Length is > 0 and <= 64 && name.All(character => character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'),
            "A Flow capture name must be a bounded file name.");
        string directory = Path.Combine(artifact, "screenshots");
        string first = Path.Combine(directory, "flow-ui-" + name + ".png");
        string second = Path.Combine(directory, "flow-ui-" + name + "-ui-layer.png");
        // Preflight the whole output set before any encoder or write can affect another path.
        RejectLinks(directory); RejectLinks(first); RejectLinks(second);
        Require(!File.Exists(first) && !Directory.Exists(first) && !File.Exists(second) && !Directory.Exists(second),
            "Flow pixel evidence already exists for this observation.");
        Directory.CreateDirectory(directory);
        Write(first, screenshot); Write(second, uiLayer);
        return (first, second);

        static void Write(string path, Action<Stream> encode)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            encode(stream);
            stream.Flush(flushToDisk: true);
        }
    }

    private void WriteEvidence()
        => AtomicJson(Path.Combine(_artifact, "diagnostics", "flow-ui-observations.json"), new
        {
            scenarioId = FlowUiAcceptance.Scenario, captureSource = "completed-composed-back-buffer-and-ui-layer",
            captures = _captures.Select(value => new { value.Name, value.Publication, value.Snapshot,
                value.Screenshot, value.ScreenshotSha256, value.UiLayer, value.UiLayerSha256 }).ToArray(),
            retired = _retired.Values.ToArray(), failedFrame = _failedFrame, error = _failure?.ToString()
        });

    public void Dispose()
    {
        if (_disposed) return;
        _component.Game.Components.Remove(_component);
        _component.Dispose();
        _pending = null;
        _disposed = true;
    }

    private sealed class CompletedFrameCapture : DrawableGameComponent
    {
        private readonly Action _capture;
        internal CompletedFrameCapture(Action capture) : base(GameRunner.instance)
        { _capture = capture; DrawOrder = int.MaxValue; }
        public override void Draw(GameTime gameTime) => _capture();
    }
}
