using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using StardewValley;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.HotReload;
using Hatifect.UI.Runtime.Rendering;
using Hatifect.UI.Stardew.Semantic;

namespace Hatifect.UI.Stardew;

internal sealed partial class UiAutomatedAcceptanceController
{
    private readonly List<object> _reloadObservations = new();
    private bool _reloadCompleted;
    private readonly List<object> _reloadFailedCleanup = new();

    private void ExecuteActionReload()
    {
        _dogfood.CloseOverlay();
        _dogfood.Close();
        RequireAction(Game1.activeClickableMenu == null, "Reload acceptance requires an empty isolated menu slot.");
        var api = new UiSemanticSurfaceService(_helper);
        foreach (string kind in new[] { "window", "terminal", "hud", "active-menu" })
            ExecuteActionReloadTarget(api, kind);
        ExecuteReloadFailedCleanup("hud-hide", activeMenu: false, dispose: false);
        ExecuteReloadFailedCleanup("hud-dispose", activeMenu: false, dispose: true);
        ExecuteReloadFailedCleanup("active-menu-hide", activeMenu: true, dispose: false);
        _reloadCompleted = true;
    }

    private void ExecuteActionReloadTarget(UiSemanticSurfaceService api, string kind)
    {
        UiSymbolId id = ActionId("reload/" + kind);
        using var publication = new UiPublication(id);
        var status = publication.State(id.Child("status"), "Before reload", UiSourceTypes.String);
        var draft = new UiState<string>("Retained draft");
        var sourceProbe = new ReloadSourceProbe("Created source");
        var operations = new List<ReloadCompletion>();
        var results = new List<int>();
        bool publishDuringAvailability = false;
        bool closeDuringCancellation = false;
        int publicationCallbacks = 0;
        IUiSemanticReloadSession? surface = null;
        var definition = new UiAction<int, int>(id.Child("run"), "Run reload probe", (_, token) =>
        {
            var operation = new ReloadCompletion(token);
            operations.Add(operation);
            token.Register(() => { if (closeDuringCancellation) surface!.Dispose(); });
            return new ValueTask<UiActionResult<int>>(operation, 0);
        }, UiActionConcurrency.RejectWhileRunning, () =>
        {
            if (publishDuringAvailability)
            {
                publishDuringAvailability = false;
                publicationCallbacks++;
                status.Value = "Published during reload";
            }
            return UiActionAvailability.Available;
        }).Bind(() => 7, (_, result) => results.Add(result.Value));
        var model = new UiExperienceBuilder(id, "Reload acceptance").Monitor("Status", status)
            .Monitor("SourceProbe", sourceProbe).Search("Name", draft).Actions("Actions", definition).VisualRole("Name").Build();
        ActionPumpCoverMenu? cover = null;
        if (kind == "active-menu") Game1.activeClickableMenu = cover = new ActionPumpCoverMenu();
        try
        {
            IUiSemanticSurfaceSession created;
            if (kind == "terminal")
            {
                UiSymbolId terminalId = id.Child("terminal");
                created = api.CreateTerminal(new UiSemanticTerminalDefinition(terminalId,
                    new[] { new UiSemanticTerminalSection(model) }), new UiSemanticSurfaceOptions(terminalId));
            }
            else created = kind == "active-menu"
                ? api.CreateActiveMenuOverlay(model, new UiSemanticSurfaceOptions(id))
                : api.CreateSurface(model, kind == "hud" ? UiSemanticHostKind.Hud : UiSemanticHostKind.Window,
                    new UiSemanticSurfaceOptions(id));
            surface = (IUiSemanticReloadSession)created;
            sourceProbe.Value = "Published before Show";
            surface.Show();
            UiSemanticStardewHost host = CaptureReloadHost(surface);
            bool firstShowCurrent = sourceProbe.Subscribers == 1 &&
                host.Session.Root.Frame.Primitives.OfType<UiTextPrimitive>().Any(text => text.Text == sourceProbe.Value);
            var beforeBurst = host.Session.Root.Frame;
            long beforeBurstVersion = host.Session.Root.AcceptedVersion;
            int readsBeforeBurst = sourceProbe.Reads;
            sourceProbe.Value = "Intermediate source";
            sourceProbe.Value = "Latest source";
            bool deferredSource = ReferenceEquals(beforeBurst, host.Session.Root.Frame) && sourceProbe.Reads == readsBeforeBurst;
            surface.Synchronize();
            bool burstVisible = host.Session.Root.AcceptedVersion == beforeBurstVersion + 1 &&
                host.Session.Root.Frame.Primitives.OfType<UiTextPrimitive>().Any(text => text.Text == sourceProbe.Value);
            var acceptedSourceFrame = host.Session.Root.Frame;
            int readsBeforeIdle = sourceProbe.Reads;
            for (int iteration = 0; iteration < 256; iteration++) surface.Synchronize();
            bool idleSource = ReferenceEquals(acceptedSourceFrame, host.Session.Root.Frame) && sourceProbe.Reads == readsBeforeIdle;
            Action retiredSourceCallback = sourceProbe.Capture();
            UiSemanticLiveAssets assets = ReloadField<UiSemanticLiveAssets>(surface, "_assets");
            string visual = "visual Reload\n\nName\n    surface = Surface.Hover\n";
            var notifications = new List<UiSemanticReloadResult>();
            surface.AssetsReloaded += notifications.Add;
            publishDuringAvailability = true;
            UiSemanticReloadResult first = surface.Reload(id, null, visual);
            RequireAction(first.Accepted && first.Changed && publicationCallbacks == 1 &&
                status.Value == "Published during reload", "The candidate must publish its source during new action availability.");
            // Every semantic host coalesces source changes until its owning synchronization pass.
            // Synchronize itself does not pump actions.
            surface.Synchronize();
            bool publicationVisible = host.Session.Root.Frame.Primitives.OfType<UiTextPrimitive>()
                .Any(text => text.Text == status.Value);
            Record("semantic.actions.reload." + kind + ".publication",
                firstShowCurrent && deferredSource && burstVisible && idleSource && publicationVisible,
                "First Show reads current state; source bursts wait for synchronization; 256 unchanged passes do not read sources; publication during reload remains pending.");

            RequireAction(host.Session.Root.Actions.Invoke(definition) && operations.Count == 1,
                "The first accepted generation did not admit pending work.");
            RequireAction(notifications.Count == 1 && ReferenceEquals(notifications[0], first),
                "A live surface must deliver its accepted reload result exactly once.");
            var before = assets.For(id);
            UiSemanticReloadResult rejected = surface.Reload(id, "invalid presentation", visual);
            bool rejectedRetained = !rejected.Accepted && !rejected.Changed && rejected.Version == first.Version &&
                ReferenceEquals(before, assets.For(id)) && !operations[0].Token.IsCancellationRequested;
            UiSemanticReloadResult replacement = surface.Reload(id, null, visual.Replace("Hover", "Pressed"));
            operations[0].Fault();
            RequireAction(notifications.Count == 3 && ReferenceEquals(notifications[1], rejected) &&
                ReferenceEquals(notifications[2], replacement),
                "A live surface must deliver both rejected and replacement reload results exactly once.");
            bool generationRetired = replacement.Accepted && replacement.Changed &&
                replacement.Version == first.Version + 1 && operations[0].Token.IsCancellationRequested &&
                operations[0].Reads == 1 && operations[0].FaultObserved && results.Count == 0;
            Record("semantic.actions.reload." + kind + ".generation", rejectedRetained && generationRetired,
                "Rejected reload retains pending work; accepted reload cancels its old generation and observes a late fault without effects.");

            RequireAction(host.Session.Root.Actions.Invoke(definition) && operations.Count == 2,
                "The replacement generation must remain independently invocable.");
            int liveAssetEvents = notifications.Count;
            int closed = 0;
            surface.Closed += () => closed++;
            closeDuringCancellation = true;
            UiSemanticReloadResult closing = surface.Reload(id, null, visual.Replace("Hover", "Raised"));
            operations[1].Fault();
            int assetEvents = notifications.Count - liveAssetEvents;
            var retiredFrame = host.Session.Root.Frame;
            int retiredSourceReads = sourceProbe.Reads;
            retiredSourceCallback();
            sourceProbe.Value = "After retirement";
            bool sourceReleased = sourceProbe.Subscribers == 0 &&
                ReferenceEquals(retiredFrame, host.Session.Root.Frame) && sourceProbe.Reads == retiredSourceReads;
            bool retiredObservers = sourceReleased && closing.Accepted && closing.Changed && !surface.Visible &&
                !host.Session.Root.IsActive && closed == 1 && assetEvents == 0 &&
                operations[1].Token.IsCancellationRequested && operations[1].Reads == 1 && results.Count == 0;
            Record("semantic.actions.reload." + kind + ".retirement", retiredObservers,
                "Cancellation may dispose the accepted surface; no AssetsReloaded observer is called after its retirement.");
            _reloadObservations.Add(new { kind, firstShowCurrent, deferredSource, burstVisible, idleSource, sourceReleased,
                publicationCallbacks, publicationVisible,
                rejectedRetained, generationRetired, retiredObservers, liveAssetEvents, assetEvents, closed,
                finalVersion = closing.Version, draft = draft.Value, callbacks = results.Count,
                operations = operations.Select(operation => new { cancelled = operation.Token.IsCancellationRequested,
                    reads = operation.Reads, faultObserved = operation.FaultObserved }).ToArray() });
        }
        finally
        {
            closeDuringCancellation = false;
            surface?.Dispose();
            foreach (ReloadCompletion operation in operations) operation.Fault();
            if (cover != null && ReferenceEquals(Game1.activeClickableMenu, cover)) Game1.activeClickableMenu = null;
        }
    }

    private sealed class ReloadSourceProbe : IUiSemanticSource<string>, IUiVersionedSemanticSource
    {
        private string _value;
        private Action? _changed;
        internal ReloadSourceProbe(string value) => _value = value;
        internal int Reads { get; private set; }
        internal int Subscribers => _changed?.GetInvocationList().Length ?? 0;
        public Type ValueType => typeof(string);
        public object UntypedValue => Value;
        public long Version { get; private set; }
        public string Value
        {
            get { Reads++; return _value; }
            set { _value = value; Version++; _changed?.Invoke(); }
        }
        public event Action? Changed { add => _changed += value; remove => _changed -= value; }
        internal Action Capture() => _changed ?? throw new InvalidOperationException("The source must be subscribed while shown.");
    }

    private void ExecuteReloadFailedCleanup(string kind, bool activeMenu, bool dispose)
    {
        IGameLoopEvents loop = RetiredInputForwarder.Wrap(_helper.Events.GameLoop);
        var fault = (RetiredInputForwarder)loop;
        IModEvents events = RetiredInputForwarder.Wrap(_helper.Events);
        ((RetiredInputForwarder)events).Properties.Add("GameLoop", loop);
        IModHelper helper = RetiredInputForwarder.Wrap(_helper);
        ((RetiredInputForwarder)helper).Properties.Add("Events", events);
        var api = new UiSemanticSurfaceService(helper);
        UiSymbolId id = ActionId("reload/failed-cleanup/" + kind);
        IUiSemanticReloadSession? surface = null;
        ReloadCompletion? operation = null;
        IDisposable? watch = null;
        int results = 0;
        var action = new UiAction<int, int>(id.Child("run"), "Run", (_, token) =>
        {
            operation = new ReloadCompletion(token);
            token.Register(() => { if (dispose) surface!.Dispose(); else surface!.Hide(); });
            return new ValueTask<UiActionResult<int>>(operation, 0);
        }, UiActionConcurrency.RejectWhileRunning).Bind(() => 7, (_, _) => results++);
        var model = new UiExperienceBuilder(id, "Failed cleanup").Monitor("Name", new UiConstantSource<string>("Ready"))
            .Actions("Actions", action).VisualRole("Name").Build();
        ActionPumpCoverMenu? cover = null;
        if (activeMenu) Game1.activeClickableMenu = cover = new ActionPumpCoverMenu();
        try
        {
            surface = (IUiSemanticReloadSession)(activeMenu
                ? api.CreateActiveMenuOverlay(model, new UiSemanticSurfaceOptions(id))
                : api.CreateSurface(model, UiSemanticHostKind.Hud, new UiSemanticSurfaceOptions(id)));
            surface.Show();
            string watchPath = Path.Combine(Environment.GetEnvironmentVariable("HATIFECT_TEST_ARTIFACTS")
                ?? throw new InvalidOperationException("The isolated artifact root is required."), "reload-" + kind + ".visual");
            File.WriteAllText(watchPath, "visual Reload\n\nName\n    surface = Surface.Hover\n");
            watch = surface.WatchAssets(id, null, watchPath);
            UiSemanticStardewHost host = CaptureReloadHost(surface);
            RequireAction(host.Session.Root.Actions.Invoke(action) && operation != null,
                "Failed cleanup fixture requires a real pending surface action.");
            int notifications = 0;
            int closed = 0;
            surface.AssetsReloaded += _ => notifications++;
            surface.Closed += () => closed++;
            fault.FailRemove = "UpdateTicked";
            UiSemanticReloadResult? result = null;
            Exception? failure = null;
            try { result = surface.Reload(id, null, "visual Reload\n\nName\n    surface = Surface.Hover\n"); }
            catch (Exception error) { failure = error; }
            bool retained = fault.RemovalFailures > 0 && fault.ActiveHandlers > 0 &&
                !host.Session.Root.IsActive && operation!.Token.IsCancellationRequested;
            // Deliver the actual still-registered callbacks once before retry. This deterministic
            // test delivery uses the same delegates as SMAPI, without claiming another game tick.
            IReadOnlyList<Exception> dispatchErrors = fault.ReplayRetained("UpdateTicked");
            bool fenced = retained && dispatchErrors.Count == 0 && failure == null && result?.Accepted == true && result.Changed &&
                !surface.Visible && notifications == 0;
            Record("semantic.actions.reload." + kind + ".failed-cleanup", fenced,
                "Failed subscription cleanup leaves the accepted surface retired, returns its result and suppresses notifications.");
            int removalFailures = fault.RemovalFailures;
            int retainedHandlers = fault.ActiveHandlers;
            fault.FailRemove = null;
            if (dispose) surface.Dispose(); else surface.Hide();
            operation!.Fault();
            bool retried = !surface.Visible && fault.ActiveHandlers == 0 && closed == 1 &&
                operation.Reads == 1 && operation.FaultObserved && results == 0;
            Record("semantic.actions.reload." + kind + ".retry", retried,
                "Retry removes every retained handler exactly through the owning surface and observes the late fault without effects.");
            _reloadFailedCleanup.Add(new { kind, retained, fenced, retried, removalFailures, retainedHandlers,
                finalHandlers = fault.ActiveHandlers, notifications, closed, dispatchErrors = dispatchErrors.Select(error => error.GetType().Name).ToArray(),
                failure = failure?.GetType().Name,
                accepted = result?.Accepted, cancelled = operation.Token.IsCancellationRequested,
                reads = operation.Reads, callbacks = results });
        }
        finally
        {
            fault.FailRemove = null;
            surface?.Dispose();
            watch?.Dispose();
            operation?.Fault();
            if (cover != null && ReferenceEquals(Game1.activeClickableMenu, cover)) Game1.activeClickableMenu = null;
        }
    }

    // Test-only inspection reaches the real owner produced by the public surface API. No
    // production accessor, substitute host, dispatcher, native Update or Pump is introduced.
    private static UiSemanticStardewHost CaptureReloadHost(IUiSemanticReloadSession surface)
    {
        if (surface is UiActiveMenuSemanticSurfaceSession)
            return ReloadField<UiSemanticStardewHost>(ReloadField<UiSemanticStardewOverlaySession>(surface, "_overlay"), "_host");
        var owner = (UiHostedSemanticSurfaceSession)surface;
        UiSemanticStardewMenu? menu = ReloadOptionalField<UiSemanticStardewMenu>(owner, "_menu");
        return menu != null ? ReloadField<UiSemanticStardewHost>(menu, "_host")
            : ReloadField<UiSemanticStardewHost>(ReloadField<UiSemanticStardewOverlaySession>(owner, "_overlay"), "_host");
    }
    private static T ReloadField<T>(object owner, string name) where T : class
        => ReloadOptionalField<T>(owner, name) ?? throw new InvalidOperationException("Missing reload owner field " + name);
    private static T? ReloadOptionalField<T>(object owner, string name) where T : class
    {
        for (Type? type = owner.GetType(); type != null; type = type.BaseType)
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } field)
                return field.GetValue(owner) as T;
        throw new InvalidOperationException("Unknown reload owner field " + name);
    }
    private sealed class ReloadCompletion : IValueTaskSource<UiActionResult<int>>
    {
        private ManualResetValueTaskSourceCore<UiActionResult<int>> _source;
        private bool _completed;
        internal ReloadCompletion(CancellationToken token) => Token = token;
        internal CancellationToken Token { get; }
        internal int Reads { get; private set; }
        internal bool FaultObserved { get; private set; }
        internal void Fault()
        {
            if (_completed) return;
            _completed = true;
            _source.SetException(new InvalidOperationException("Controlled reload completion after retirement."));
        }
        public UiActionResult<int> GetResult(short token)
        {
            try { return _source.GetResult(token); }
            catch { FaultObserved = true; throw; }
            finally { Reads++; }
        }
        public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _source.OnCompleted(continuation, state, token, flags);
    }
}
