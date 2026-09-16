using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using StardewValley;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Runtime.Registration;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Terminal;
using Hatifect.UI.Stardew.Semantic;

namespace Hatifect.UI.Stardew;

internal sealed partial class UiAutomatedAcceptanceController
{
    private static readonly string[] TerminalGenerationCases = { "same", "route", "failed-open", "failed-route" };
    private readonly List<TerminalGenerationAction> _terminalGenerationActions = new();
    private readonly List<object> _terminalGenerationTransitions = new();
    private UiSemanticStardewRuntime? _generationRuntime;
    private UiSemanticStardewHost? _generationHost;
    private UiSemanticStardewMenu? _generationMenu;
    private UiPortalHandle? _generationPortal;
    private TerminalGenerationAction? _generationFirst;
    private TerminalGenerationAction? _generationSecond;
    private TerminalGenerationAction? _generationPopup;
    private TerminalGenerationCompletion? _generationCurrent;
    private UiExperienceDefinition? _generationFirstModel;
    private UiExperienceDefinition? _generationSecondModel;
    private int _generationCase;
    private int _generationPhase;
    private int _generationTicks;
    private int _generationOwnerThread;
    private bool _generationRejectSecond;
    private bool _generationMetadataAtCancellation;
    private bool _advancingTerminalGeneration;
    private bool _terminalGenerationCompleted;

    private static UiSymbolId GenerationId(string path) => new("Hatifect.UI", "acceptance/terminal-generation/" + path);
    private void BeginTerminalGeneration()
    {
        _dogfood.CloseOverlay();
        _dogfood.Close();
        RequireAction(Game1.activeClickableMenu == null, "Terminal generation acceptance requires an empty isolated menu slot.");
        _generationOwnerThread = Environment.CurrentManagedThreadId;
        _generationRuntime = new UiSemanticStardewRuntime(Game1.graphics.GraphicsDevice,
            typography => typography.Family == "Display" ? Game1.dialogueFont : Game1.smallFont,
            asset => throw new InvalidOperationException($"Terminal generation acceptance has no texture '{asset}'."));
        _advancingTerminalGeneration = true;
        try { BeginTerminalGenerationCase(); }
        finally { _advancingTerminalGeneration = false; }
    }

    private void BeginTerminalGenerationCase()
    {
        string kind = TerminalGenerationCases[_generationCase];
        _generationFirst = NewTerminalGenerationAction(kind + "/first");
        _generationSecond = NewTerminalGenerationAction(kind + "/second");
        _generationPopup = NewTerminalGenerationAction(kind + "/popup");
        TerminalGenerationAction idleSibling = NewTerminalGenerationAction(kind + "/idle-sibling");
        _generationFirstModel = GenerationModel("first", _generationFirst.Definition, idleSibling.Definition);
        _generationSecondModel = GenerationModel("second", _generationSecond.Definition);
        var registry = new UiRegistryBuilder()
            .TerminalSection(GenerationId("first"), "First", () => _generationFirstModel, order: 1)
            .TerminalSection(GenerationId("second"), "Second", () => _generationSecondModel, order: 2).Freeze();
        UiRect viewport = UiSemanticStardewMenu.CaptureViewport();
        _generationRejectSecond = false;
        _generationHost = _generationRuntime!.CreateTerminalHost(registry, UiSemanticStardewTheme.Default,
            new UiHostPlacementContext(viewport), UiPresentationProfiles.Wide, GenerationId("first"),
            resolveAssets: descriptor => _generationRejectSecond && descriptor.Id == GenerationId("second")
                ? throw new InvalidOperationException("Controlled Terminal candidate rejection.") : new UiTerminalSectionAssets());
        UiSemanticStardewHost host = _generationHost;
        _generationMenu = new UiSemanticStardewMenu(host, viewport,
            next => host.RecomposeTerminal(UiPresentationProfiles.Wide, new UiHostPlacementContext(next)));
        Game1.activeClickableMenu = _generationMenu;
        var previousActions = host.Session.Root.Actions;
        var previousInvocation = host.CurrentInvocation;
        var previousScene = host.Session.Root.Scene;
        RequireAction(previousActions.Invoke(_generationFirst.Definition), "Terminal root rejected its pending typed action.");
        RequireAction(previousActions.CanInvoke(idleSibling.Definition) && idleSibling.Captures == 0,
            "The old generation must contain an available idle action before retirement.");
        var popupPolicy = new UiHostPolicy(UiHostKind.Popup, UiWindowChrome.Tool, UiDismissPolicy.Escape,
            UiModalPolicy.Modeless, UiFocusScopePolicy.Contained, UiPopupPlacement.Anchor);
        _generationPortal = host.Present(new UiPortalRequest(GenerationId("popup"),
            new UiPortalOwner(host.Session.Root.Scene.Root.Id), ActionScene("terminal-generation-popup", popupPolicy,
                _generationPopup.Definition), new UiHostPlacementContext(viewport, anchor: new UiRect(400, 300, 30, 30))));
        RequireAction(host.Session.Submit().Interaction?.ActionInvoked == true && _generationPopup.Operations.Count == 1,
            "Terminal popup input did not start its typed action.");
        bool same = kind == "same";
        _generationMetadataAtCancellation = false;
        _generationFirst.Operations[0].Token.Register(() =>
        {
            _generationMetadataAtCancellation = ReferenceEquals(host.CurrentInvocation.Experience,
                    same ? _generationFirstModel : _generationSecondModel)
                && !ReferenceEquals(previousInvocation, host.CurrentInvocation)
                && host.Session.Accessibility.Portals.Count == 0
                && !previousActions.CanInvoke(idleSibling.Definition)
                && !previousActions.Invoke(idleSibling.Definition)
                && idleSibling.Captures == 0 && idleSibling.Operations.Count == 0;
        });
        if (kind.StartsWith("failed-", StringComparison.Ordinal)) ExecuteFailedTransitionCase();
        else ExecuteAcceptedTransitionCase();
        _generationTicks = 0;

        void ExecuteFailedTransitionCase()
        {
            _generationRejectSecond = true;
            Exception? rejection = null;
            try { TransitionTerminalGeneration(kind == "failed-route", GenerationId("second")); }
            catch (InvalidOperationException error) { rejection = error; }
            bool retained = rejection?.Message == "Controlled Terminal candidate rejection."
                && ReferenceEquals(previousInvocation, host.CurrentInvocation)
                && (kind == "failed-route" || ReferenceEquals(previousScene, host.Session.Root.Scene))
                && !_generationFirst.Operations[0].Token.IsCancellationRequested
                && !_generationPopup.Operations[0].Token.IsCancellationRequested
                && host.Session.Accessibility.Portals.Count == 1;
            Record("semantic.actions.terminal." + kind + ".transition", retained,
                "Rejected explicit candidate retains its accepted invocation and pending root/popup work.");
            _terminalGenerationTransitions.Add(new { kind, retained, rejection = rejection?.Message });
            RequireAction(retained, "Failed Terminal candidate did not retain its pending generation.");
            _generationRejectSecond = false;
            _generationFirst.Operations[0].CompleteFromWorker(21);
            _generationPopup.Operations[0].CompleteFromWorker(22);
            _generationPhase = 1;
        }

        void ExecuteAcceptedTransitionCase()
        {
            TransitionTerminalGeneration(kind == "route", GenerationId(same ? "first" : "second"));
            bool retired = _generationMetadataAtCancellation && _generationFirst.Operations[0].Token.IsCancellationRequested
                && _generationPopup.Operations[0].Token.IsCancellationRequested
                && !previousActions.Invoke(idleSibling.Definition) && idleSibling.Captures == 0;
            Record("semantic.actions.terminal." + kind + ".transition", retired,
                "Explicit Open/Follow accepts Terminal metadata and retires old root/popup bindings before cancellation.");
            _terminalGenerationTransitions.Add(new { kind, retired, metadataAtCancellation = _generationMetadataAtCancellation });
            RequireAction(retired, "Terminal transition did not retire its old generation.");
            _generationFirst.Operations[0].CompleteFromWorker(0, fault: true);
            _generationPopup.Operations[0].CompleteFromWorker(0, fault: true);
            StartCurrentTerminalGeneration(same ? _generationFirst : _generationSecond);
            _generationPhase = 2;
        }
    }

    private bool AdvanceTerminalGeneration()
    {
        if (_generationPhase == 0) return false;
        _advancingTerminalGeneration = true;
        try
        {
            if (++_generationTicks > 600) throw new TimeoutException("Native Terminal generation action delivery timed out.");
            string kind = TerminalGenerationCases[_generationCase];
            if (_generationPhase == 1)
            {
                if (!TerminalGenerationDelivered(_generationFirst!.Operations[0], 21)
                    || !TerminalGenerationDelivered(_generationPopup!.Operations[0], 22)) return true;
                TransitionTerminalGeneration(kind == "failed-route", GenerationId("second"));
                RequireAction(_generationHost!.Session.Accessibility.Portals.Count == 0
                    && ReferenceEquals(_generationHost.CurrentInvocation.Experience, _generationSecondModel),
                    "Terminal retry failed to accept the second section and retire the popup.");
                StartCurrentTerminalGeneration(_generationSecond!);
                _generationPhase = 2;
                _generationTicks = 0;
                return true;
            }
            if (!TerminalGenerationDelivered(_generationCurrent!, 42)) return true;
            if (!kind.StartsWith("failed-", StringComparison.Ordinal))
            {
                if (!TerminalGenerationFaultObserved(_generationFirst!.Operations[0])
                    || !TerminalGenerationFaultObserved(_generationPopup!.Operations[0])) return true;
            }
            Record("semantic.actions.terminal." + kind + ".delivery",
                ReferenceEquals(Game1.activeClickableMenu, _generationMenu),
                "Actual native menu.update delivers the current typed result on its owner thread; retired results have no observer effects.");
            CloseTerminalGenerationCase();
            if (++_generationCase < TerminalGenerationCases.Length) BeginTerminalGenerationCase();
            else
            {
                _terminalGenerationCompleted = true;
                StopTerminalGeneration();
                _capturePending = true;
            }
            return true;
        }
        finally { _advancingTerminalGeneration = false; }
    }

    private void TransitionTerminalGeneration(bool follow, UiSymbolId target)
    {
        if (!follow) { _generationMenu!.OpenTerminalSection(target); return; }
        var node = GenerationNodes(_generationHost!.Session.Root.Scene.Root)
            .OfType<UiRouteButtonSceneNode>().Single(candidate => candidate.Route == target);
        RequireAction(_generationHost.Session.Root.Layout.TryGetEntry(node.Id, out var entry), "Terminal route has no layout.");
        UiRect bounds = entry!.Bounds;
        int x = (int)(bounds.X + bounds.Width / 2), y = (int)(bounds.Y + bounds.Height / 2);
        _generationMenu!.receiveLeftClick(x, y);
        _generationMenu.releaseLeftClick(x, y);
    }
    private void StartCurrentTerminalGeneration(TerminalGenerationAction action)
    {
        RequireAction(_generationHost!.Session.Root.Actions.Invoke(action.Definition), "New Terminal generation rejected its typed action.");
        _generationCurrent = action.Operations[^1];
        _generationCurrent.CompleteFromWorker(42);
    }
    private bool TerminalGenerationDelivered(TerminalGenerationCompletion operation, int expected)
    {
        if (!operation.WorkerFinished || operation.Callbacks == 0) return false;
        RequireAction(operation.Callbacks == 1 && operation.Result == expected && operation.CapturedValue == 7
            && operation.CallbackThread == _generationOwnerThread && operation.WorkerThread != _generationOwnerThread
            && operation.SourceReads == 1 && !operation.ObserverDuringDriver,
            "Terminal action delivery violated capture, single-result or owner-thread behavior.");
        return true;
    }
    private static bool TerminalGenerationFaultObserved(TerminalGenerationCompletion operation)
    {
        if (!operation.WorkerFinished || operation.SourceReads == 0) return false;
        RequireAction(operation.SourceReads == 1 && operation.FaultObserved && operation.Callbacks == 0,
            "Retired Terminal result was not observed exactly once without callback effects.");
        return true;
    }
    private TerminalGenerationAction NewTerminalGenerationAction(string name)
    {
        var action = new TerminalGenerationAction(name, this);
        _terminalGenerationActions.Add(action);
        return action;
    }
    private static UiExperienceDefinition GenerationModel(string id, params UiActionDefinition[] actions)
        => new UiExperienceBuilder(GenerationId(id), id).Monitor("Content", new UiConstantSource<string>(id))
            .Actions("Actions", actions).Build();
    private static IEnumerable<UiSceneNode> GenerationNodes(UiSceneNode root)
    {
        yield return root;
        foreach (var child in root.Children)
        foreach (var node in GenerationNodes(child)) yield return node;
    }
    private void CloseTerminalGenerationCase()
    {
        _generationPortal?.Dispose();
        _generationPortal = null;
        _generationMenu?.Dispose();
        _generationMenu = null;
        _generationHost = null;
    }
    private void StopTerminalGeneration()
    {
        _generationPhase = 0;
        CloseTerminalGenerationCase();
        _generationRuntime?.Dispose();
        _generationRuntime = null;
        foreach (var action in _terminalGenerationActions)
        foreach (var operation in action.Operations) operation.CompleteFromWorker(0, fault: true);
    }
    private sealed record TerminalGenerationRequest(int Index, int Value);
    private sealed class TerminalGenerationAction
    {
        private int _captured;
        internal TerminalGenerationAction(string name, UiAutomatedAcceptanceController owner)
        {
            Name = name;
            Definition = new UiAction<TerminalGenerationRequest, int>(GenerationId(name), "Run async", (request, token) =>
            {
                var operation = new TerminalGenerationCompletion(token);
                RequireAction(request.Index == Operations.Count, "Terminal typed request order changed.");
                Operations.Add(operation);
                return new ValueTask<UiActionResult<int>>(operation, 0);
            }, UiActionConcurrency.RejectWhileRunning).Bind(() => new TerminalGenerationRequest(_captured++, 7), (request, result) =>
            {
                var operation = Operations[request.Index];
                operation.Callbacks++;
                operation.Result = result.Value;
                operation.CapturedValue = request.Value;
                operation.CallbackThread = Environment.CurrentManagedThreadId;
                operation.ObserverDuringDriver = owner._advancingTerminalGeneration;
            });
        }
        public string Name { get; }
        public int Captures => _captured;
        internal UiActionDefinition Definition { get; }
        public List<TerminalGenerationCompletion> Operations { get; } = new();
    }
    // The driver never consumes the ValueTask or calls host Pump/update. Only production
    // native menu.update can deliver observers; the worker completes a fresh source per invoke.
    private sealed class TerminalGenerationCompletion : IValueTaskSource<UiActionResult<int>>
    {
        private ManualResetValueTaskSourceCore<UiActionResult<int>> _source;
        private int _started, _reads, _fault;
        private Task? _worker;
        internal TerminalGenerationCompletion(CancellationToken token) { Token = token; }
        internal CancellationToken Token { get; }
        public bool Cancelled => Token.IsCancellationRequested;
        public int SourceReads => Volatile.Read(ref _reads);
        public bool FaultObserved => Volatile.Read(ref _fault) != 0;
        public int Callbacks { get; internal set; }
        public int Result { get; internal set; }
        public int CapturedValue { get; internal set; }
        public int CallbackThread { get; internal set; }
        public int WorkerThread { get; private set; }
        public bool ObserverDuringDriver { get; internal set; }
        internal bool WorkerFinished
        {
            get
            {
                if (_worker?.IsCompleted != true) return false;
                _worker.GetAwaiter().GetResult();
                return true;
            }
        }
        internal void CompleteFromWorker(int value, bool fault = false)
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;
            _worker = Task.Run(() =>
            {
                WorkerThread = Environment.CurrentManagedThreadId;
                if (fault) _source.SetException(new InvalidOperationException("Controlled late Terminal fault."));
                else _source.SetResult(UiActionResult<int>.Success(value));
            });
        }
        public UiActionResult<int> GetResult(short token)
        {
            try { return _source.GetResult(token); }
            catch { Interlocked.Exchange(ref _fault, 1); throw; }
            finally { Interlocked.Increment(ref _reads); }
        }
        public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _source.OnCompleted(continuation, state, token, flags);
    }
}
