using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Stardew.Semantic;

namespace Hatifect.UI.Stardew;

internal sealed partial class UiAutomatedAcceptanceController
{
    private static readonly string[] SaveSwitchKinds = { "window", "terminal", "hud", "active-menu" };
    private readonly List<object> _saveSwitchObservations = new();
    private readonly List<SaveSwitchSurface> _saveSwitchSurfaces = new();
    private readonly List<SaveSwitchRequest> _saveSwitchEffects = new();
    private SaveSwitchStage _saveSwitchStage;
    private string[] _saveSwitchPaths = Array.Empty<string>();
    private string[] _saveSwitchRunIds = Array.Empty<string>();
    private string _saveSwitchRuntimeId = string.Empty;
    private SaveSwitchSurface? _saveSwitchOld, _saveSwitchCurrent;
    private int _saveSwitchCase, _saveSwitchWorld, _saveSwitchTitles, _saveSwitchLoads;
    private int _saveSwitchTicks, _saveSwitchOwnerThread, _saveSwitchSettleTicks;
    private bool _advancingSaveSwitch, _saveSwitchCompleted;
    private enum SaveSwitchStage { None, AwaitTitle, LoadNext, AwaitWorld, Delivery }

    private void BeginSaveSwitch()
    {
        _dogfood.CloseOverlay();
        _dogfood.Close();
        ReadSaveSwitchCopies();
        ValidateSaveSwitchWorld();
        _saveSwitchLoads = 1;
        _saveSwitchOwnerThread = Environment.CurrentManagedThreadId;
        Record("semantic.actions.save-switch.copies", true,
            "Two named companions are bound to the accepted request, runtime and distinct synthetic worlds.");
        _advancingSaveSwitch = true;
        try { BeginSaveSwitchCase(); }
        finally { _advancingSaveSwitch = false; }
    }

    private void BeginSaveSwitchCase()
    {
        ValidateSaveSwitchWorld();
        _saveSwitchOld = OpenSaveSwitchSurface();
        RequireAction(_saveSwitchOld.Operations.Count == 2
            && _saveSwitchOld.Operations.All(operation => !operation.Cancelled && operation.SourceReads == 0)
            && _saveSwitchOld.Host!.Session.Accessibility.Portals.Count == 1,
            "The old world must own pending root and popup work before the native transition.");
        Record(SaveSwitchCheck("pending"), true,
            "The public surface owns pending typed root/popup actions with committed pre-await effects.");
        _saveSwitchStage = SaveSwitchStage.AwaitTitle;
        _saveSwitchTicks = 0;
        _awaitingReturnedToTitle = true;
        RequestReturnToTitle();
    }

    private bool AdvanceSaveSwitch()
    {
        if (_saveSwitchStage == SaveSwitchStage.None) return false;
        _advancingSaveSwitch = true;
        try
        {
            RequireAction(++_saveSwitchTicks <= 3600,
                "Native save-switch phase exceeded its update bound: " + _saveSwitchStage);
            if (_saveSwitchStage == SaveSwitchStage.AwaitTitle) return true;
            if (_saveSwitchStage == SaveSwitchStage.LoadNext) return AdvanceLoadNextStage();
            if (_saveSwitchStage == SaveSwitchStage.AwaitWorld) return AdvanceAwaitWorldStage();
            return AdvanceDeliverySettlementStage();
        }
        finally { _advancingSaveSwitch = false; }
    }

    private bool AdvanceLoadNextStage()
    {
        // This is a later native Update, after every ReturnedToTitle subscriber ran.
        RequireAction(!Context.IsWorldReady, "The native title event did not retire the old world.");
        RequireSaveSwitchRetired(_saveSwitchOld!);
        Record(SaveSwitchCheck("retirement"), true,
            "Native title/menu lifecycle cancels old work and releases its source and SMAPI subscriptions.");
        _saveSwitchWorld = 1 - _saveSwitchWorld;
        ValidateSaveSwitchCopy(_saveSwitchWorld);
        _saveSwitchStage = SaveSwitchStage.AwaitWorld;
        _saveSwitchTicks = 0;
        BeginLoadSave(_saveSwitchPaths[_saveSwitchWorld]);
        return true;
    }

    private bool AdvanceAwaitWorldStage()
    {
        if (!Context.IsWorldReady || Game1.fadeToBlackAlpha > 0f) return true;
        _awaitingWorld = false;
        _worldWaitTicks = 0;
        ValidateSaveSwitchWorld();
        _saveSwitchLoads++;
        RequireSaveSwitchRetired(_saveSwitchOld!);
        _saveSwitchCurrent = OpenSaveSwitchSurface();
        RequireAction(!ReferenceEquals(_saveSwitchOld!.Host, _saveSwitchCurrent.Host)
            && _saveSwitchOld.Id == _saveSwitchCurrent.Id
            && _saveSwitchOld.World != _saveSwitchCurrent.World,
            "The successor must use stable semantic IDs in a distinct native world and host.");
        // Neither operation is released until the successor in the other world is active.
        for (int index = 0; index < 2; index++)
        {
            _saveSwitchOld.Operations[index].CompleteFromWorker(61 + index,
                fault: index == (_saveSwitchCase % 2));
            _saveSwitchCurrent.Operations[index].CompleteFromWorker(71 + index);
        }
        _saveSwitchStage = SaveSwitchStage.Delivery;
        _saveSwitchTicks = 0;
        _saveSwitchSettleTicks = 0;
        return true;
    }

    private bool AdvanceDeliverySettlementStage()
    {
        if (!SaveSwitchResultsReady()) return true;
        // Observe additional real Updates to catch repeated delivery or retained subscriptions.
        if (++_saveSwitchSettleTicks < 30) return true;
        RequireSaveSwitchRetired(_saveSwitchOld!);
        int oldReads = _saveSwitchOld!.Source.Reads;
        _saveSwitchOld.Source.Publish();
        RequireAction(_saveSwitchOld.Source.Reads == oldReads && _saveSwitchOld.CommittedEffects == 2
            && _saveSwitchCurrent!.CommittedEffects == 2,
            "Retirement must release publication observers without undoing pre-await effects.");
        Record(SaveSwitchCheck("late"), true,
            "Old success and fault are consumed once after the switch; neither calls an observer in the new session.");
        Record(SaveSwitchCheck("delivery"), true,
            "Native Update delivers both fresh results exactly once on the owner thread with captured requests.");
        _saveSwitchObservations.Add(new { kind = SaveSwitchKinds[_saveSwitchCase],
            fromWorld = _saveSwitchOld.World, toWorld = _saveSwitchCurrent!.World,
            oldClosed = _saveSwitchOld.Closed, oldEventHandlers = _saveSwitchOld.ActiveHandlers,
            oldSourceHandlers = _saveSwitchOld.Source.ActiveHandlers,
            oldCommittedEffects = _saveSwitchOld.CommittedEffects, newCommittedEffects = _saveSwitchCurrent.CommittedEffects,
            oldOperations = _saveSwitchOld.Operations.ToArray(), newOperations = _saveSwitchCurrent.Operations.ToArray(),
            oldSourceReadsAfterPublication = _saveSwitchOld.Source.Reads - oldReads, settlingUpdates = _saveSwitchSettleTicks });
        CloseSaveSwitchSurface(_saveSwitchOld);
        CloseSaveSwitchSurface(_saveSwitchCurrent);
        if (++_saveSwitchCase < SaveSwitchKinds.Length) BeginSaveSwitchCase();
        else
        {
            RequireAction(_saveSwitchTitles == 4 && _saveSwitchLoads == 5 && _saveSwitchEffects.Count == 8
                && _saveSwitchSurfaces.All(surface => surface.ActiveHandlers == 0 && surface.Source.ActiveHandlers == 0),
                "The save-switch matrix did not complete every real lifecycle and release every owner.");
            _saveSwitchCompleted = true;
            Record("semantic.actions.save-switch.lifecycle", true,
                "Four real title transitions and five native loads completed with eight fresh callbacks and all owners released.");
            StopSaveSwitch();
            _capturePending = true;
        }
        return true;
    }

    private bool SaveSwitchResultsReady()
    {
        SaveSwitchSurface old = _saveSwitchOld!, current = _saveSwitchCurrent!;
        ValidateSaveSwitchWorld();
        RequireSaveSwitchRetired(old);
        RequireAction(current.Surface!.Visible && current.Host!.Session.Root.IsActive,
            "The successor surface retired before its native action delivery.");
        for (int index = 0; index < 2; index++)
        {
            var retired = old.Operations[index];
            var fresh = current.Operations[index];
            if (!retired.WorkerFinished || retired.SourceReads == 0 || !fresh.WorkerFinished || fresh.Callbacks == 0)
                return false;
            RequireAction(retired.SourceReads == 1 && retired.Callbacks == 0
                && retired.FaultObserved == (index == (_saveSwitchCase % 2))
                && retired.WorkerThread != _saveSwitchOwnerThread,
                "A retired result was lost, read twice or delivered across the native save switch.");
            RequireAction(fresh.SourceReads == 1 && fresh.Callbacks == 1 && fresh.Result == 71 + index
                && fresh.CapturedValue == 7 && !fresh.Cancelled && !fresh.ObserverDuringDriver
                && fresh.CallbackThread == _saveSwitchOwnerThread && fresh.WorkerThread != _saveSwitchOwnerThread,
                "Fresh action delivery violated immutable capture, single result or owning-thread behavior.");
        }
        RequireAction(_saveSwitchEffects.Count == (_saveSwitchCase + 1) * 2
            && _saveSwitchEffects.TakeLast(2).All(request => request.World == current.World && request.Value == 7),
            "An old observer changed the shared successor effect sink.");
        return true;
    }

    private static void RequireSaveSwitchRetired(SaveSwitchSurface surface)
        => RequireAction(!surface.Surface!.Visible && !surface.Host!.Session.Root.IsActive && surface.Closed == 1
            && surface.ActiveHandlers == 0 && surface.Source.ActiveHandlers == 0
            && surface.Operations.All(operation => operation.Cancelled && operation.Callbacks == 0),
            "Native retirement left an active host, subscription, uncancelled action or observer effect.");

    private string SaveSwitchCheck(string check) => "semantic.actions.save-switch." + SaveSwitchKinds[_saveSwitchCase] + "." + check;

    private SaveSwitchSurface OpenSaveSwitchSurface()
    {
        RequireAction(Game1.activeClickableMenu == null, "Save-switch requires a free native menu slot.");
        string kind = SaveSwitchKinds[_saveSwitchCase];
        var state = new SaveSwitchSurface(ActionId("save-switch/" + kind), Game1.uniqueIDForThisGame);
        _saveSwitchSurfaces.Add(state); // Acquire cleanup responsibility before creating any native resource.
        IGameLoopEvents loop = RetiredInputForwarder.Wrap(_helper.Events.GameLoop);
        IDisplayEvents display = RetiredInputForwarder.Wrap(_helper.Events.Display);
        IInputEvents input = RetiredInputForwarder.Wrap(_helper.Events.Input);
        state.Events.Add((RetiredInputForwarder)loop);
        state.Events.Add((RetiredInputForwarder)display);
        state.Events.Add((RetiredInputForwarder)input);
        IModEvents events = RetiredInputForwarder.Wrap(_helper.Events);
        var properties = ((RetiredInputForwarder)events).Properties;
        properties.Add("GameLoop", loop); properties.Add("Display", display); properties.Add("Input", input);
        IModHelper helper = RetiredInputForwarder.Wrap(_helper);
        ((RetiredInputForwarder)helper).Properties.Add("Events", events);
        var api = new UiSemanticSurfaceService(helper);
        UiActionDefinition root = SaveSwitchAction(state, "run");
        var experience = new UiExperienceBuilder(state.Id, "Save switch acceptance")
            .Monitor("Status", state.Source).Actions("Actions", root).Build();
        if (kind == "active-menu") Game1.activeClickableMenu = state.Cover = new ActionPumpCoverMenu();
        IUiSemanticSurfaceSession surface = kind == "terminal"
            ? api.CreateTerminal(new UiSemanticTerminalDefinition(state.Id.Child("terminal"),
                new[] { new UiSemanticTerminalSection(experience) }), new UiSemanticSurfaceOptions(state.Id.Child("terminal")))
            : kind == "active-menu" ? api.CreateActiveMenuOverlay(experience, new UiSemanticSurfaceOptions(state.Id))
            : api.CreateSurface(experience, kind == "hud" ? UiSemanticHostKind.Hud : UiSemanticHostKind.Window,
                new UiSemanticSurfaceOptions(state.Id));
        state.Surface = (IUiSemanticReloadSession)surface;
        surface.Closed += () => state.Closed++;
        surface.Show();
        state.Host = CaptureReloadHost(state.Surface);
        RequireAction(state.Host.Session.Root.Actions.Invoke(root), "Save-switch root rejected its action.");
        var policy = new UiHostPolicy(UiHostKind.Popup, UiWindowChrome.Tool, UiDismissPolicy.Escape,
            UiModalPolicy.Modeless, UiFocusScopePolicy.Contained, UiPopupPlacement.Anchor);
        state.Portal = state.Host.Present(new UiPortalRequest(state.Id.Child("popup"),
            new UiPortalOwner(state.Host.Session.Root.Scene.Root.Id), ActionScene("save-switch-popup", policy,
                SaveSwitchAction(state, "popup-run")), new UiHostPlacementContext(UiSemanticStardewMenu.CaptureViewport(),
                anchor: new UiRect(400, 300, 30, 30))));
        RequireAction(state.Host.Session.Submit().Interaction?.ActionInvoked == true && state.Operations.Count == 2,
            "Save-switch popup rejected its action.");
        state.CaptureValue = 99;
        RequireAction(state.ActiveHandlers > 0 && (kind == "active-menu" || state.Source.ActiveHandlers > 0),
            "The public surface did not acquire its expected native/source subscriptions.");
        return state;
    }

    private UiActionDefinition SaveSwitchAction(SaveSwitchSurface state, string name)
    {
        TerminalGenerationCompletion? operation = null;
        return new UiAction<SaveSwitchRequest, int>(state.Id.Child(name), "Run async", (request, token) =>
        {
            RequireAction(request.World == state.World && request.Value == 7, "The typed request was not captured at invocation.");
            state.CommittedEffects++;
            operation = new TerminalGenerationCompletion(token);
            state.Operations.Add(operation);
            return new ValueTask<UiActionResult<int>>(operation, 0);
        }, UiActionConcurrency.RejectWhileRunning).Bind(() => new SaveSwitchRequest(state.World, state.CaptureValue), (request, result) =>
        {
            operation!.Callbacks++;
            operation.Result = result.Value;
            operation.CapturedValue = request.Value;
            operation.CallbackThread = Environment.CurrentManagedThreadId;
            operation.ObserverDuringDriver = _advancingSaveSwitch;
            _saveSwitchEffects.Add(request);
        });
    }

    private void StopSaveSwitch()
    {
        _saveSwitchStage = SaveSwitchStage.None;
        foreach (SaveSwitchSurface surface in _saveSwitchSurfaces)
        {
            try { CloseSaveSwitchSurface(surface); }
            catch (Exception error) { RetainTerminalFailure("HARNESS-SAVE-SWITCH-CLEANUP", error); }
            foreach (var operation in surface.Operations) operation.CompleteFromWorker(0, fault: true);
        }
    }

    private static void CloseSaveSwitchSurface(SaveSwitchSurface surface)
    {
        try { surface.Surface?.Dispose(); }
        finally
        {
            surface.Portal?.Dispose();
            if (surface.Cover != null && ReferenceEquals(Game1.activeClickableMenu, surface.Cover))
                Game1.activeClickableMenu = null;
        }
    }

    private void ReadSaveSwitchCopies()
    {
        string Required(string name) => Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException("Missing save-switch setting: " + name);
        RequireAction(Guid.TryParseExact(_runId, "D", out Guid run) && run.ToString("D") == _runId,
            "The save-switch request must have a canonical UUID.");
        int last = int.Parse(_runId[^1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) ^ 1;
        string secondRun = _runId[..^1] + last.ToString("x", CultureInfo.InvariantCulture);
        RequireAction(Required("HATIFECT_TEST_SECONDARY_RUN_ID") == secondRun, "The secondary identity is not this request's companion.");
        string isolated = Path.GetFullPath(Required("HATIFECT_TEST_ISOLATED_ROOT"));
        string saves = Path.Combine(isolated, "config", "StardewValley", "Saves");
        _saveSwitchRunIds = new[] { _runId, secondRun };
        _saveSwitchPaths = new[] { Required("HATIFECT_SMAPI_TEST_SAVE"), Required("HATIFECT_SMAPI_TEST_SECONDARY_SAVE") }
            .Select(Path.GetFullPath).ToArray();
        RequireAction(Path.GetFullPath(_helper.DirectoryPath) == Path.Combine(isolated, "Mods", "Hatifect", "Hatifect UI"),
            "Save-switch must run in the isolated UI module.");
        using JsonDocument request = ReadSaveSwitchJson(Path.Combine(_artifactDirectory, "request.json"));
        JsonElement r = request.RootElement;
        RequireAction(r.GetProperty("protocolVersion").GetInt32() == 2 && r.GetProperty("requestId").GetString() == _runId
            && r.GetProperty("scenarioId").GetString() == "semantic.actions.save-switch"
            && Path.GetFullPath(r.GetProperty("isolatedRoot").GetString()!) == isolated
            && Path.GetFullPath(r.GetProperty("savePath").GetString()!) == _saveSwitchPaths[0]
            && Path.GetFullPath(r.GetProperty("artifactDirectory").GetString()!) == Path.GetFullPath(_artifactDirectory)
            && Path.GetFullPath(_artifactDirectory) == Path.Combine(Path.GetFullPath(r.GetProperty("repositoryRoot").GetString()!),
                "artifacts", "runtime", _runId), "The named save companions do not match the accepted request.");
        string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        _saveSwitchRuntimeId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            HashFile(typeof(Game1).Assembly.Location) + "\n" + HashFile(typeof(IModHelper).Assembly.Location) + "\n"))).ToLowerInvariant()[..24];
        for (int index = 0; index < 2; index++)
        {
            string expected = "HatifectHarness" + Guid.Parse(_saveSwitchRunIds[index]).ToString("N") + "_" + (4242424242UL + (ulong)index);
            RequireAction(_saveSwitchPaths[index] == Path.Combine(saves, expected), "A save companion is outside its exact owned path.");
            ValidateSaveSwitchCopy(index);
        }
    }

    private void ValidateSaveSwitchCopy(int index)
    {
        string path = _saveSwitchPaths[index];
        RequireAction(Directory.Exists(path), "The requested save companion disappeared.");
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((path, 0));
        int entries = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            RejectSaveSwitchLinks(directory.Path);
            foreach (string child in Directory.EnumerateFileSystemEntries(directory.Path))
            {
                RequireAction(++entries <= 4096, "The save companion exceeds its entry bound.");
                RejectSaveSwitchLinks(child);
                if (!Directory.Exists(child)) continue;
                RequireAction(directory.Depth < 16, "The save companion exceeds its depth bound.");
                pending.Push((child, directory.Depth + 1));
            }
        }
        using JsonDocument owner = ReadSaveSwitchJson(Path.Combine(path, ".hatifect-save-owner.json"));
        JsonElement o = owner.RootElement;
        RequireAction(o.EnumerateObject().Count() == 6 && o.GetProperty("fixtureSchemaVersion").GetInt32() == 2
            && o.GetProperty("fixtureId").GetString() == "hatifect-golden-save"
            && o.GetProperty("runtimeId").GetString() == _saveSwitchRuntimeId
            && o.GetProperty("runId").GetString() == _saveSwitchRunIds[index]
            && o.GetProperty("state").GetString() == "Ready" && o.GetProperty("createdAtUtc").ValueKind == JsonValueKind.String,
            "The save companion ownership differs from this runtime/request.");
    }

    private void ValidateSaveSwitchWorld()
    {
        RequireAction(Context.IsWorldReady && Context.IsMainPlayer && !Context.IsMultiplayer
            && Game1.uniqueIDForThisGame == 4242424242UL + (ulong)_saveSwitchWorld
            && Constants.SaveFolderName == Path.GetFileName(_saveSwitchPaths[_saveSwitchWorld])
            && Constants.CurrentSavePath != null && Path.GetFullPath(Constants.CurrentSavePath) == _saveSwitchPaths[_saveSwitchWorld],
            "The loaded native world does not match the exact owned companion.");
    }

    private static JsonDocument ReadSaveSwitchJson(string path)
    {
        RejectSaveSwitchLinks(path);
        var info = new FileInfo(path);
        RequireAction(info.Exists && info.Length <= 64 * 1024, "The save-switch document is missing or oversized.");
        return JsonDocument.Parse(File.ReadAllBytes(path));
    }

    private static void RejectSaveSwitchLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            RequireAction(info.LinkTarget == null && (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) == 0),
                "Save-switch paths must not traverse symbolic links.");
        }
    }

    private sealed record SaveSwitchRequest(ulong World, int Value);
    private sealed class SaveSwitchSurface
    {
        internal SaveSwitchSurface(UiSymbolId id, ulong world) { Id = id; World = world; }
        internal UiSymbolId Id { get; }
        internal ulong World { get; }
        internal SaveSwitchSource Source { get; } = new();
        internal List<TerminalGenerationCompletion> Operations { get; } = new();
        internal List<RetiredInputForwarder> Events { get; } = new();
        internal int ActiveHandlers => Events.Sum(events => events.ActiveHandlers);
        internal IUiSemanticReloadSession? Surface;
        internal UiSemanticStardewHost? Host;
        internal UiPortalHandle? Portal;
        internal ActionPumpCoverMenu? Cover;
        internal int CaptureValue = 7, CommittedEffects, Closed;
    }
    private sealed class SaveSwitchSource : IUiSemanticSource<string>
    {
        private Action? _changed;
        internal int ActiveHandlers { get; private set; }
        internal int Reads { get; private set; }
        internal void Publish() => _changed?.Invoke();
        public Type ValueType => typeof(string);
        public string Value { get { Reads++; return "Save switch"; } }
        public object UntypedValue => Value;
        public event Action? Changed
        {
            add { _changed += value; ActiveHandlers++; }
            remove { _changed -= value; ActiveHandlers--; }
        }
    }
}
