using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using Hatifect.UI.Runtime.Accessibility;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Stardew.Semantic;

namespace Hatifect.UI.Stardew;

/// <summary>Exact-harness, read-only observation of the actual native Window.</summary>
internal sealed class UiWindowInputObserver : IDisposable
{
    private const string Scenario = "flow.ui.player.input";
    private const int MaximumCaptures = 128;
    private static readonly UiSymbolId TargetExperience = new("Hatifect.Flow", "network");
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly string _runId;
    private readonly string _directory;
    private readonly string _expectedText;
    private readonly CompletedFrameComponent _component;
    private readonly UiWindowInputGate _gate;
    private readonly List<object> _captures = new();
    private UiSemanticStardewMenu? _menu;
    private long _epoch;
    private long _frame;
    private long _lastRenderSequence;
    private long _pressed, _released, _tab, _back, _text;
    private UiPoint? _pressPoint, _releasePoint;
    private (long Epoch, long Scene, long Frame)? _previousStamp, _capturedStamp;
    private object? _latest;
    private string? _failure;
    private bool _disposed;

    private UiWindowInputObserver(IModHelper helper, IMonitor monitor, string runId, string directory)
    {
        _helper = helper; _monitor = monitor; _runId = runId; _directory = directory;
        _expectedText = "native-" + Guid.Parse(runId).ToString("N")[..8];
        _gate = new(_expectedText);
        _component = new(GameRunner.instance, ObserveFrame);
        helper.Events.Input.ButtonPressed += OnPressed;
        helper.Events.Input.ButtonReleased += OnReleased;
        GameRunner.instance.Components.Add(_component);
    }

    internal static UiWindowInputObserver? TryAttach(IModHelper helper, IMonitor monitor)
    {
        if (!UiAutomatedAcceptanceScenarioRegistry.IsRegistrationEnabled
            || Environment.GetEnvironmentVariable("HATIFECT_TEST_SCENARIO") != Scenario) return null;
        string run = Environment.GetEnvironmentVariable("HATIFECT_TEST_RUN_ID") ?? "";
        string directory = Environment.GetEnvironmentVariable("HATIFECT_TEST_ARTIFACTS") ?? "";
        if (!Guid.TryParse(run, out _) || !Path.IsPathFullyQualified(directory))
            throw new InvalidOperationException("Window input observation requires an exact run and absolute artifact directory.");
        return new(helper, monitor, run, directory);
    }

    internal static bool IsTargetExperience(UiSymbolId experience) => experience == TargetExperience;

    private UiSemanticStardewMenu? CurrentMenu()
    {
        if (Game1.activeClickableMenu is not UiSemanticStardewMenu { Observation: not null } menu) return null;
        var runtime = menu.CaptureRuntimeContext();
        if (!IsTargetExperience(runtime.Scene.Experience)) return null;
        // Only the exact Flow network Window belongs to this observer. Other semantic menus can
        // legitimately exist while the save loads; their ownership must not poison this probe.
        menu.RequireAutomationOwner();
        if (!ReferenceEquals(_menu, menu))
        {
            DetachMenu();
            _menu = menu; _epoch++; _lastRenderSequence = 0;
            menu.NativeTextReceived += OnNativeText;
            _pressPoint = _releasePoint = null;
            _previousStamp = null;
        }
        return menu;
    }

    private void OnNativeText() => _text++;

    private void DetachMenu()
    {
        if (_menu is not null) _menu.NativeTextReceived -= OnNativeText;
        _menu = null;
    }

    private void OnPressed(object? sender, ButtonPressedEventArgs e)
    {
        try
        {
            if (_disposed || !Game1.game1.IsActive || CurrentMenu() is null) return;
            if (e.Button == SButton.MouseLeft) { _pressed++; _pressPoint = new(Game1.getMouseX(), Game1.getMouseY()); }
            else if (e.Button == SButton.Tab) _tab++;
            else if (e.Button == SButton.Back) _back++;
        }
        catch (Exception error) { Fail(error); }
    }

    private void OnReleased(object? sender, ButtonReleasedEventArgs e)
    {
        try
        {
            if (_disposed || !Game1.game1.IsActive || CurrentMenu() is null || e.Button != SButton.MouseLeft) return;
            _released++; _releasePoint = new(Game1.getMouseX(), Game1.getMouseY());
        }
        catch (Exception error) { Fail(error); }
    }

    private void ObserveFrame()
    {
        if (_disposed) return;
        try
        {
            _frame++;
            UiSemanticStardewMenu? menu = CurrentMenu();
            if (menu is null)
            {
                DetachMenu(); _previousStamp = null;
                _gate.Observe(_frame, default);
                _latest = new { visible = false, completedFrame = _frame, surfaceEpoch = _epoch };
                if (_frame % 60 == 0) WriteProgress();
                return;
            }
            var runtime = menu.CaptureRuntimeContext();
            var origins = new Dictionary<UiSymbolId, UiSceneNode>();
            Visit(runtime.Scene.Root, node => origins.Add(node.Id, node));
            var elements = new List<Element>();
            Visit(runtime.Accessibility.Root, node =>
            {
                origins.TryGetValue(node.Id, out var origin);
                if (node.Name?.Length > 4096 || node.Value?.Length > 4096)
                    throw new InvalidOperationException("Window input element text exceeds the observation budget.");
                elements.Add(new(node.Id.ToString(), origin?.SemanticId?.ToString(),
                    origin is UiButtonSceneNode button ? button.Action.Id.ToString() : null,
                    node.Role.ToString(), node.Name, node.Value, node.Enabled, node.Focused, node.Bounds, node.Clip));
            });
            Element? focused = elements.SingleOrDefault(element => element.Focused);
            var rendered = runtime.LastCompletedRender;
            bool freshRender = rendered.Sequence > _lastRenderSequence;
            _lastRenderSequence = rendered.Sequence;
            bool visible = !menu.HasActiveInputPortal && freshRender && rendered.SceneVersion == runtime.AcceptedVersion
                && rendered.FrameVersion == runtime.FrameVersion && Game1.game1.IsActive && Game1.fadeToBlackAlpha <= 0f
                && UiNativeInputGate.IsVisible(runtime.Accessibility.Root)
                && Game1.graphics.GraphicsDevice.RenderTargetCount == 0;
            bool focusedVisible = focused is not null && focused.Enabled && FullyVisible(focused.Bounds, focused.Clip);
            bool pointerInside = focusedVisible && _pressPoint is { } press && _releasePoint is { } release
                && focused!.Bounds.Contains(press) && focused.Clip.Contains(press)
                && focused.Bounds.Contains(release) && focused.Clip.Contains(release);
            var observation = new UiWindowInputObservation(_epoch, visible, elements.Any(element => element.Role == "TextField"),
                focusedVisible ? focused!.SemanticId ?? focused.NodeId : null,
                focusedVisible && focused!.Role == "TextField" && focused.Enabled, focused?.Value, pointerInside,
                _pressed, _released, _tab, _back, _text, runtime.AcceptedVersion, runtime.FrameVersion);
            UiWindowInputPhase? completed = _gate.Observe(_frame, observation);
            var stamp = (_epoch, runtime.AcceptedVersion, runtime.FrameVersion);
            _latest = new
            {
                visible, completedFrame = _frame, surfaceEpoch = _epoch, experience = runtime.Scene.Experience.ToString(),
                acceptedSceneVersion = runtime.AcceptedVersion, frameVersion = runtime.FrameVersion,
                renderedSceneVersion = rendered.SceneVersion, renderedFrameVersion = rendered.FrameVersion, renderSequence = rendered.Sequence,
                viewport = new { width = Game1.uiViewport.Width, height = Game1.uiViewport.Height },
                focused, observation, elements
            };
            bool stable = _previousStamp == stamp;
            _previousStamp = stamp;
            if (visible && (completed is not null || stable && _capturedStamp != stamp))
            {
                if (_captures.Count == MaximumCaptures) throw new InvalidOperationException("Window input capture budget exhausted.");
                string name = "window-input-" + _captures.Count.ToString("D3");
                Capture(name);
                _captures.Add(new { phase = completed?.ToString(), state = _latest,
                    screenshot = "screenshots/" + name + ".png", uiLayerScreenshot = "screenshots/" + name + "-ui-layer.png" });
                _capturedStamp = stamp;
                WriteProgress();
            }
            else if (_frame % 60 == 0) WriteProgress();
        }
        catch (Exception error) { Fail(error); }
    }

    private void Fail(Exception error)
    {
        _failure ??= error.ToString();
        _monitor.Log("Window input observer failed: " + error, LogLevel.Error);
        try { WriteProgress(); }
        finally { Dispose(); }
    }

    private void WriteProgress()
    {
        string path = Path.Combine(_directory, "diagnostics", "ui-window-input-progress.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = new
        {
            protocolVersion = 1, runId = _runId, scenario = Scenario,
            declaredOrigin = "os-injected", originEvidence = "External controller declaration; SMAPI counters do not establish hardware origin.",
            expectedText = _expectedText, phase = _gate.Phase.ToString(), completedFrame = _frame,
            failure = _failure, latest = _latest, captures = _captures
        };
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private void Capture(string name)
    {
        GraphicsDevice graphics = Game1.graphics.GraphicsDevice;
        if (graphics.RenderTargetCount != 0) throw new InvalidOperationException("Capture requires the composed back buffer.");
        string directory = Path.Combine(_directory, "screenshots"); Directory.CreateDirectory(directory);
        int width = graphics.PresentationParameters.BackBufferWidth, height = graphics.PresentationParameters.BackBufferHeight;
        var data = new Color[checked(width * height)]; graphics.GetBackBufferData(data);
        using var texture = new Texture2D(graphics, width, height, false, SurfaceFormat.Color);
        texture.SetData(data);
        using (FileStream stream = File.Create(Path.Combine(directory, name + ".png"))) texture.SaveAsPng(stream, width, height);
        if (Game1.game1.uiScreen is not { IsDisposed: false } layer)
            throw new InvalidOperationException("Completed Window capture has no UI layer.");
        using FileStream uiStream = File.Create(Path.Combine(directory, name + "-ui-layer.png"));
        layer.SaveAsPng(uiStream, layer.Width, layer.Height);
    }

    private static bool FullyVisible(UiRect bounds, UiRect clip)
        => bounds.Width > 0 && bounds.Height > 0 && clip.X <= bounds.X && clip.Y <= bounds.Y
            && clip.Right >= bounds.Right && clip.Bottom >= bounds.Bottom;

    private static void Visit(UiSceneNode root, Action<UiSceneNode> visit)
    {
        var pending = new Stack<UiSceneNode>(); pending.Push(root); int visited = 0;
        while (pending.TryPop(out var node))
        {
            if (++visited > 1024 || node.Children.Count > 1024 - visited - pending.Count)
                throw new InvalidOperationException("Window input scene exceeds the observation budget.");
            visit(node); foreach (var child in node.Children) pending.Push(child);
        }
    }

    private static void Visit(UiAccessibilityNodeSnapshot root, Action<UiAccessibilityNodeSnapshot> visit)
    {
        var pending = new Stack<UiAccessibilityNodeSnapshot>(); pending.Push(root); int visited = 0;
        while (pending.TryPop(out var node))
        {
            if (++visited > 1024 || node.Children.Count > 1024 - visited - pending.Count)
                throw new InvalidOperationException("Window input accessibility exceeds the observation budget.");
            visit(node); foreach (var child in node.Children) pending.Push(child);
        }
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _helper.Events.Input.ButtonPressed -= OnPressed; _helper.Events.Input.ButtonReleased -= OnReleased;
        GameRunner.instance.Components.Remove(_component); _component.Dispose(); DetachMenu();
    }

    private sealed record Element(string NodeId, string? SemanticId, string? ActionId, string Role,
        string? Name, string? Value, bool Enabled, bool Focused, UiRect Bounds, UiRect Clip);

    private sealed class CompletedFrameComponent : DrawableGameComponent
    {
        private readonly Action _observe;
        internal CompletedFrameComponent(Game game, Action observe) : base(game) { _observe = observe; DrawOrder = int.MaxValue; }
        public override void Draw(GameTime gameTime) => _observe();
    }
}
