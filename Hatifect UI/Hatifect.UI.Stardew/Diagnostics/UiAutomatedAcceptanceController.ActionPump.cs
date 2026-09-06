using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Runtime.Registration;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Stardew.Semantic;

namespace Hatifect.UI.Stardew;

internal sealed partial class UiAutomatedAcceptanceController
{
    private readonly List<ActionPumpProbe> _actionProbes = new();
    private UiSemanticStardewRuntime? _actionRuntime;
    private UiSemanticStardewHost? _actionHost;
    private UiSemanticStardewMenu? _actionMenu;
    private UiSemanticStardewOverlaySession? _actionOverlay;
    private UiPortalHandle? _actionPortal;
    private IClickableMenu? _actionCover;
    private UiScene? _actionAcceptedScene;
    private int _actionOwnerThread;
    private int _actionPhase;
    private int _actionPhaseTicks;
    private int _actionTicks;
    private int _actionContextClosed;
    private bool _advancingActionPump;
    private bool _actionPumpCompleted;

    private void BeginActionPump()
    {
        _dogfood.CloseOverlay();
        _dogfood.Close();
        if (Game1.activeClickableMenu != null)
            throw new InvalidOperationException("Action acceptance requires the isolated world's empty menu slot.");
        _actionOwnerThread = Environment.CurrentManagedThreadId;
        _actionRuntime = new UiSemanticStardewRuntime(Game1.graphics.GraphicsDevice,
            typography => typography.Family == "Display" ? Game1.dialogueFont : Game1.smallFont,
            asset => throw new InvalidOperationException($"Action acceptance has no texture '{asset}'."));
        ActionPumpProbe root = NewActionProbe("menu-root");
        OpenActionMenu(root);
        ActionPumpProbe portal = NewActionProbe("menu-portal");
        PresentActionPortal(portal);
        root.CompleteFromWorker();
        portal.CompleteFromWorker();
        _actionPhase = 1;
    }

    private bool AdvanceActionPump()
    {
        if (_actionPhase == 0) return false;
        _advancingActionPump = true;
        try
        {
            _actionTicks++;
            if (++_actionPhaseTicks > 600)
                throw new TimeoutException($"Native action Update did not complete phase {_actionPhase}.");
            switch (_actionPhase)
            {
                case 1:
                    if (!Delivered("menu-root") || !Delivered("menu-portal")) return true;
                    Record("semantic.actions.pump.menu",
                        ReferenceEquals(Game1.activeClickableMenu, _actionMenu) &&
                        ReferenceEquals(_actionAcceptedScene, _actionHost!.Session.Root.Scene),
                        "Root and popup worker results returned through native menu.update without scene recomposition.");
                    CloseActionPresentation();
                    ActionPumpProbe lateMenu = NewActionProbe("retired-menu");
                    OpenActionMenu(lateMenu);
                    CloseActionPresentation();
                    RequireAction(lateMenu.Cancellation.IsCancellationRequested, "Menu close did not cancel pending work.");
                    ActionPumpProbe successor = NewActionProbe("successor-menu");
                    OpenActionMenu(successor);
                    lateMenu.CompleteFromWorker(fault: true);
                    successor.CompleteFromWorker();
                    NextActionPhase();
                    return true;
                case 2:
                    if (!Delivered("successor-menu") || !ObservedLateFault("retired-menu") || _actionPhaseTicks < 4) return true;
                    Record("semantic.actions.pump.reopen", Probe("retired-menu").Callbacks == 0,
                        "Closed menu cancelled its operation; its late fault was observed once and could not mutate the new same-ID menu.");
                    CloseActionPresentation();
                    ActionPumpProbe hud = NewActionProbe("hud-root");
                    OpenActionOverlay(hud, UiSemanticStardewOverlayRenderLayer.Hud);
                    ActionPumpProbe lateHud = NewActionProbe("retired-hud-portal");
                    PresentActionPortal(lateHud);
                    _actionCover = new ActionPumpCoverMenu();
                    Game1.activeClickableMenu = _actionCover;
                    hud.CompleteFromWorker();
                    NextActionPhase();
                    return true;
                case 3:
                    if (!Delivered("hud-root")) return true;
                    RequireAction(ReferenceEquals(Game1.activeClickableMenu, _actionCover) && _actionOverlay!.Visible,
                        "HUD acceptance lost its temporary native-menu occlusion.");
                    _actionOverlay!.Hide();
                    RequireAction(Probe("retired-hud-portal").Cancellation.IsCancellationRequested,
                        "HUD Hide did not cancel its pending portal.");
                    Probe("retired-hud-portal").CompleteFromWorker(fault: true);
                    CloseActionPresentation();
                    ActionPumpProbe nextHud = NewActionProbe("successor-hud");
                    OpenActionOverlay(nextHud, UiSemanticStardewOverlayRenderLayer.Hud);
                    nextHud.CompleteFromWorker();
                    NextActionPhase();
                    return true;
                case 4:
                    if (!Delivered("successor-hud") || !ObservedLateFault("retired-hud-portal") || _actionPhaseTicks < 4) return true;
                    Record("semantic.actions.pump.hud", Probe("retired-hud-portal").Callbacks == 0,
                        "Occluded HUD progressed through SMAPI UpdateTicked; Hide cancelled its portal and late fault left the successor unchanged.");
                    CloseActionPresentation();
                    ActionPumpProbe context = NewActionProbe("retired-menu-overlay", inlineCompletion: true);
                    OpenActionOverlay(context, UiSemanticStardewOverlayRenderLayer.ActiveMenu);
                    // Only this test source completes its configured observer inline on the worker.
                    // Wait for SetResult to return after the framework mailbox is published, then
                    // change context in this same game tick, before any production Update can run.
                    context.CompleteFromWorker();
                    context.WaitForCompletion();
                    RequireAction(context.SourceReads == 1 && context.Callbacks == 0,
                        "Context replacement requires a ready result that has not reached its observer.");
                    _actionCover = new ActionPumpCoverMenu();
                    Game1.activeClickableMenu = _actionCover;
                    NextActionPhase();
                    return true;
                case 5:
                    ActionPumpProbe retired = Probe("retired-menu-overlay");
                    if (!retired.WorkerFinished || retired.SourceReads != 1 || _actionPhaseTicks < 4) return true;
                    Record("semantic.actions.pump.context",
                        retired.Callbacks == 0 && _actionContextClosed == 1 && !_actionOverlay!.Visible,
                        "Changing the exact native menu retired its overlay before a ready result could publish through UpdateTicked.");
                    _actionPumpCompleted = true;
                    StopActionPump();
                    _capturePending = true;
                    return true;
                default: throw new InvalidOperationException("Unknown native action phase.");
            }
        }
        finally { _advancingActionPump = false; }
    }

    private void NextActionPhase() { _actionPhase++; _actionPhaseTicks = 0; }
    private static void RequireAction(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }
    private ActionPumpProbe Probe(string name) => _actionProbes.Single(probe => probe.Name == name);
    private ActionPumpProbe NewActionProbe(string name, bool inlineCompletion = false)
    {
        var probe = new ActionPumpProbe(name, this, inlineCompletion);
        _actionProbes.Add(probe);
        return probe;
    }
    private bool Delivered(string name)
    {
        ActionPumpProbe probe = Probe(name);
        if (!probe.WorkerFinished || probe.Callbacks == 0) return false;
        RequireAction(probe.Callbacks == 1 && probe.CallbackThread == _actionOwnerThread &&
            probe.WorkerThread != _actionOwnerThread && probe.CapturedRequest == 7 && probe.Result == 42 &&
            probe.SourceReads == 1 && !probe.ObserverDuringDriver,
            $"Action '{name}' violated immutable capture, thread ownership or single completion delivery.");
        return true;
    }
    private bool ObservedLateFault(string name)
    {
        ActionPumpProbe probe = Probe(name);
        if (!probe.WorkerFinished || probe.SourceReads == 0) return false;
        RequireAction(probe.SourceReads == 1 && probe.SourceFaultObserved && probe.Callbacks == 0,
            $"Retired action '{name}' did not observe its late fault exactly once without an observer effect.");
        return true;
    }
    private static UiSymbolId ActionId(string path) => new("Hatifect.UI", "acceptance/action-pump/" + path);
    private static UiScene ActionScene(string id, UiHostPolicy policy, UiActionDefinition action)
    {
        var experience = new UiExperienceBuilder(ActionId(id), "Action lifecycle")
            .Monitor("Status", new UiConstantSource<string>("Controlled async acceptance"))
            .Actions("Actions", action).Build();
        var registry = new UiRegistryBuilder().Window(ActionId(id), "Action lifecycle", () => experience, policy).Freeze();
        return new UiSceneComposer(UiSemanticStardewTheme.Default, registry).Compose(
            new UiInvocationService(registry).Invoke(ActionId(id), UiPresentationProfiles.Wide));
    }
    private void OpenActionMenu(ActionPumpProbe probe)
    {
        UiScene scene = ActionScene("menu", UiHostPolicies.Window, probe.Definition);
        UiRect viewport = UiSemanticStardewMenu.CaptureViewport();
        UiSemanticStardewHost host = _actionRuntime!.CreateHost(scene, new UiHostPlacementContext(viewport));
        _actionHost = host;
        _actionAcceptedScene = scene;
        _actionMenu = new UiSemanticStardewMenu(host, viewport, next => host.Update(scene, new UiHostPlacementContext(next)));
        Game1.activeClickableMenu = _actionMenu;
        _actionMenu.receiveKeyPress(Keys.Tab);
        _actionMenu.receiveKeyPress(Keys.Enter);
        RequireAction(probe.Starts == 1, "Native menu input did not admit its typed action.");
        probe.CaptureValue = 99;
    }
    private void OpenActionOverlay(ActionPumpProbe probe, UiSemanticStardewOverlayRenderLayer layer)
    {
        UiScene scene = ActionScene("overlay", UiHostPolicies.Overlay, probe.Definition);
        UiRect viewport = UiSemanticStardewMenu.CaptureViewport();
        _actionHost = _actionRuntime!.CreateHost(scene, new UiHostPlacementContext(viewport));
        _actionOverlay = new UiSemanticStardewOverlaySession(_actionRuntime, _helper, _actionHost, viewport, layer,
            onClosed: layer == UiSemanticStardewOverlayRenderLayer.ActiveMenu ? () => _actionContextClosed++ : null);
        _actionOverlay.Show();
        RequireAction(_actionHost.Session.Root.Actions.Invoke(probe.Definition) && probe.Starts == 1,
            "Overlay host binding did not admit its typed action.");
        probe.CaptureValue = 99;
    }
    private void PresentActionPortal(ActionPumpProbe probe)
    {
        UiRect viewport = UiSemanticStardewMenu.CaptureViewport();
        _actionPortal = _actionHost!.Present(new UiPortalRequest(ActionId("popup"),
            new UiPortalOwner(_actionHost.Session.Root.Scene.Root.Id),
            ActionScene("popup", UiHostPolicies.Popup, probe.Definition),
            new UiHostPlacementContext(viewport, anchor: new UiRect(320, 200, 40, 30))));
        RequireAction(_actionHost.Session.Submit().Interaction?.ActionInvoked == true && probe.Starts == 1,
            "Portal input did not admit its typed action.");
        probe.CaptureValue = 99;
    }
    private void CloseActionPresentation()
    {
        _actionPortal?.Dispose();
        _actionPortal = null;
        _actionOverlay?.Dispose();
        _actionOverlay = null;
        _actionMenu?.Dispose();
        _actionMenu = null;
        _actionHost = null;
    }
    private void StopActionPump()
    {
        _actionPhase = 0;
        CloseActionPresentation();
        if (ReferenceEquals(Game1.activeClickableMenu, _actionCover)) Game1.activeClickableMenu = null;
        _actionCover = null;
        _actionRuntime?.Dispose();
        _actionRuntime = null;
        foreach (ActionPumpProbe probe in _actionProbes) probe.CompleteFromWorker(fault: true);
    }

    private sealed class ActionPumpCoverMenu : IClickableMenu { }
    private sealed record ActionPumpRequest(int Value);

    // The controlled non-cooperative source records the framework's GetResult, including faults.
    // The driver never consumes the ValueTask itself and never calls any host Pump/update method.
    private sealed class ActionPumpProbe : IValueTaskSource<UiActionResult<int>>
    {
        private ManualResetValueTaskSourceCore<UiActionResult<int>> _source;
        private int _completionStarted;
        private int _sourceReads;
        private int _sourceFaultObserved;
        private Task? _worker;
        internal ActionPumpProbe(string name, UiAutomatedAcceptanceController owner, bool inlineCompletion)
        {
            Name = name;
            _source.RunContinuationsAsynchronously = !inlineCompletion;
            Definition = new UiAction<ActionPumpRequest, int>(ActionId("run"), "Run async", (_, token) =>
            {
                Starts++;
                Cancellation = token;
                return new ValueTask<UiActionResult<int>>(this, _source.Version);
            }, UiActionConcurrency.RejectWhileRunning).Bind(() => new ActionPumpRequest(CaptureValue), (request, result) =>
            {
                Callbacks++;
                CallbackThread = Environment.CurrentManagedThreadId;
                CapturedRequest = request.Value;
                Result = result.Value;
                ObserverDuringDriver = owner._advancingActionPump;
            });
        }
        public string Name { get; }
        internal UiActionDefinition Definition { get; }
        internal int CaptureValue { get; set; } = 7;
        public int Starts { get; private set; }
        public int Callbacks { get; private set; }
        public int CallbackThread { get; private set; }
        public int WorkerThread { get; private set; }
        public int CapturedRequest { get; private set; }
        public int Result { get; private set; }
        public bool ObserverDuringDriver { get; private set; }
        internal CancellationToken Cancellation { get; private set; }
        public bool CancellationRequested => Cancellation.IsCancellationRequested;
        public int SourceReads => Volatile.Read(ref _sourceReads);
        public bool SourceFaultObserved => Volatile.Read(ref _sourceFaultObserved) != 0;
        internal bool WorkerFinished
        {
            get
            {
                if (_worker?.IsCompleted != true) return false;
                _worker.GetAwaiter().GetResult();
                return true;
            }
        }
        internal void WaitForCompletion()
        {
            if (_worker == null || !_worker.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The controlled context worker did not publish its ready result.");
            _worker.GetAwaiter().GetResult();
        }
        internal void CompleteFromWorker(bool fault = false)
        {
            if (Interlocked.CompareExchange(ref _completionStarted, 1, 0) != 0) return;
            _worker = Task.Run(() =>
            {
                WorkerThread = Environment.CurrentManagedThreadId;
                if (fault) _source.SetException(new InvalidOperationException("Controlled late fault: " + Name));
                else _source.SetResult(UiActionResult<int>.Success(42));
            });
        }
        public UiActionResult<int> GetResult(short token)
        {
            try { return _source.GetResult(token); }
            catch { Interlocked.Exchange(ref _sourceFaultObserved, 1); throw; }
            // Publish completion of the source read after its fault flag. The game-thread
            // observer must not see a started read as a completed, non-faulting read.
            finally { Interlocked.Increment(ref _sourceReads); }
        }
        public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _source.OnCompleted(continuation, state, token, flags);
    }
}
