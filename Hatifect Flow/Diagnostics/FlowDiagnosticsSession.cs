using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Hatifect.Flow.Application;
using Hatifect.Flow.Infrastructure.Persistence;
using Hatifect.Flow.Sessions;
using Hatifect.Flow.UI.Semantic;
using Hatifect.UI;
using Hatifect.UI.Experience;
using StardewModdingAPI;
using StardewValley;

namespace Hatifect.Flow.Diagnostics;

/// <summary>Owns opt-in runtime probes without owning the player's transport session.</summary>
internal sealed class FlowDiagnosticsSession : IDisposable
{
    private DurableFlowHost? _host;
    private FlowHostAcceptance? _acceptance;
    private FlowChestRoundtripAcceptance? _chestAcceptance;
    private FlowChestCancellationAcceptance? _cancellationAcceptance;
    private FlowChestReturnAcceptance? _returnAcceptance;
    private FlowChestIsolationAcceptance? _isolationAcceptance;
    private FlowGamePerformanceAcceptance? _performanceAcceptance;
    private FlowGameResourceAcceptance? _resourceAcceptance;
    private FlowItemNamesAcceptance? _itemNamesAcceptance;
    private FlowUiAcceptance? _uiAcceptance;
    private FlowUiActionsAcceptance? _uiActionsAcceptance;
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly Func<FlowGameSession?> _currentSession;
    private readonly Func<bool> _hasSurface;
    private bool _startupFailed;

    private FlowDiagnosticsSession(IModHelper helper, IMonitor monitor, Func<FlowGameSession?> currentSession, Func<bool> hasSurface)
    {
        _helper = helper;
        _monitor = monitor;
        _currentSession = currentSession;
        _hasSurface = hasSurface;
    }

    internal bool UsesDiagnosticTransport => _acceptance is not null;

    internal static FlowDiagnosticsSession? TryCreate(
        IModHelper helper, IMonitor monitor, Func<FlowGameSession?> currentSession, Func<bool> hasSurface)
    {
        if (Environment.GetEnvironmentVariable("HATIFECT_TEST_MODE") != "1"
            || Environment.GetEnvironmentVariable("HATIFECT_TEST_AUTOMATED") != "1")
            return null;

        return new FlowDiagnosticsSession(helper, monitor, currentSession, hasSurface);
    }

    internal void Start(
        Func<IFlowApplication, Guid?, Func<Guid, string>, IUiSemanticSurfaceApi, ParcelExperience> showParcel,
        Func<IFlowNetworkApplication, IUiSemanticSurfaceApi, NetworkExperience> showNetwork,
        Func<FlowGameSession, IUiSemanticHostApi, NetworkExperience> openPlayerNetwork,
        Action closeSurface)
    {
        try
        {
            _itemNamesAcceptance = FlowItemNamesAcceptance.TryCreate(_helper, _monitor);
            _uiAcceptance = FlowUiAcceptance.TryCreate(_helper, _monitor,
                showParcel, closeSurface);
            _uiActionsAcceptance = FlowUiActionsAcceptance.TryCreate(_helper, _monitor,
                showNetwork,
                showParcel, closeSurface);
            _acceptance = FlowHostAcceptance.TryCreate(_helper, _monitor);
            _chestAcceptance = FlowChestRoundtripAcceptance.TryCreate(_helper, _monitor, _currentSession, openPlayerNetwork, closeSurface);
            _cancellationAcceptance = FlowChestCancellationAcceptance.TryCreate(_helper, _monitor, _currentSession);
            _returnAcceptance = FlowChestReturnAcceptance.TryCreate(_helper, _monitor, _currentSession);
            _isolationAcceptance = FlowChestIsolationAcceptance.TryCreate(_helper, _monitor, _currentSession);
            _performanceAcceptance = FlowGamePerformanceAcceptance.TryCreate(_helper, _monitor, _currentSession);
            _resourceAcceptance = FlowGameResourceAcceptance.TryCreate(_helper, _monitor, _currentSession);
        }
        catch (Exception error)
        {
            _startupFailed = true;
            ReportFailure(error);
        }
    }

    internal bool TryHandleSaveLoaded()
    {
        if (_uiActionsAcceptance is not null)
        {
            try { RequireReadOnlyUiSession(); _uiActionsAcceptance.OnSaveLoaded(); }
            catch (Exception error) { ReportFailure(error); }
            return true;
        }
        if (_uiAcceptance is not null)
        {
            try { RequireReadOnlyUiSession(); _uiAcceptance.OnSaveLoaded(); }
            catch (Exception error) { ReportFailure(error); }
            return true;
        }
        if (_itemNamesAcceptance is not null)
        {
            try
            {
                RequireReadOnlyNamesSession();
                _itemNamesAcceptance.OnSaveLoaded();
            }
            catch (Exception error) { ReportFailure(error); }
            return true;
        }
        if (_acceptance is null)
            return false;
        try
        {
            if (!Context.IsMainPlayer || !Context.IsWorldReady)
                throw new InvalidOperationException("Flow host startup requires the authoritative loaded world.");
            _host ??= new DurableFlowHost();
            bool started = _host.Start(_acceptance.SessionIdentity, _acceptance.OpenSession);
            _acceptance.OnSaveLoaded(_host, started);
        }
        catch (Exception error) { ReportFailure(error); }
        return true;
    }

    internal void OnGameSessionLoaded()
    {
        _chestAcceptance?.OnSaveLoaded();
        _cancellationAcceptance?.OnSaveLoaded();
        _returnAcceptance?.OnSaveLoaded();
        _isolationAcceptance?.OnSaveLoaded();
        _performanceAcceptance?.OnSaveLoaded();
        _resourceAcceptance?.OnSaveLoaded();
    }

    internal bool ExitAfterStartupFailure()
    {
        if (!_startupFailed)
            return false;
        Game1.game1.Exit();
        return true;
    }

    internal bool PrepareGameTick()
    {
        if (_uiActionsAcceptance is not null || _uiAcceptance is not null)
        {
            RequireReadOnlyUiSession();
            return true;
        }
        if (_itemNamesAcceptance is not null)
        {
            RequireReadOnlyNamesSession();
            _itemNamesAcceptance.Tick();
            return false;
        }
        return true;
    }

    internal void Tick()
    {
        if (_uiActionsAcceptance is not null)
        {
            _uiActionsAcceptance.Tick();
            return;
        }
        if (_uiAcceptance is not null)
        {
            _uiAcceptance.Tick();
            return;
        }
        if (_host?.State == DurableFlowHostState.Active)
            _host.Tick(!Context.IsWorldReady || !Context.IsMainPlayer || !Game1.shouldTimePass());
        _acceptance?.Tick(_host);
        _chestAcceptance?.Tick();
        _cancellationAcceptance?.Tick();
        _returnAcceptance?.Tick();
        _isolationAcceptance?.Tick();
        _performanceAcceptance?.Tick();
        _resourceAcceptance?.Tick();
    }

    internal void OnReturnedToTitle()
    {
        CloseHost();
        _uiAcceptance?.OnReturnedToTitle();
        _uiActionsAcceptance?.OnReturnedToTitle();
        _acceptance?.OnReturnedToTitle();
        _chestAcceptance?.OnReturnedToTitle();
        _cancellationAcceptance?.OnReturnedToTitle();
        _returnAcceptance?.OnReturnedToTitle();
        _isolationAcceptance?.OnReturnedToTitle();
        _performanceAcceptance?.OnReturnedToTitle();
        _resourceAcceptance?.OnReturnedToTitle();
    }

    internal FlowGameSession? OpenGameSession(FlowSessionOwner owner,
        Func<FlowGameSave?> readSave, Func<FlowGameSave?, FlowGameSession> restoreSession)
    {
        FlowGameSave? saved = null;
        FlowResourceCost read = default, restore = default;
        FlowGameSession? session = owner.Open(Context.ScreenId, Context.IsMainPlayer, () =>
        {
            if (_resourceAcceptance is null)
                return restoreSession(readSave());
            saved = FlowResourceCost.Measure(readSave, out read);
            return FlowResourceCost.Measure(() => restoreSession(saved), out restore);
        });
        // The observer verifies that the session owner has already published this session.
        if (session is not null)
            _resourceAcceptance?.ObserveLoad(session, saved, read, restore);
        return session;
    }

    internal void TickGameSession(FlowGameSession session, bool timePasses)
    {
        if (_performanceAcceptance is null && _resourceAcceptance is null)
        {
            session.Tick(timePasses);
            return;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread(), started = Stopwatch.GetTimestamp();
        session.Tick(timePasses);
        long elapsed = Stopwatch.GetTimestamp() - started;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        _performanceAcceptance?.ObserveTick(session, timePasses, elapsed, allocated);
        _resourceAcceptance?.ObserveTick(session, timePasses, elapsed, allocated);
    }

    internal void SaveGameSession(FlowGameSession session)
    {
        if (_resourceAcceptance is null)
        {
            _helper.Data.WriteSaveData(FlowGameSession.SaveKey, session.BeginSave());
            return;
        }
        FlowGameSave saved = FlowResourceCost.Measure(session.BeginSave, out FlowResourceCost capture);
        FlowResourceCost write = FlowResourceCost.Measure(() => _helper.Data.WriteSaveData(FlowGameSession.SaveKey, saved));
        _resourceAcceptance.ObserveSave(session, saved, capture, write);
    }

    internal void ObserveOrdinaryEntry(bool free, bool authority, bool menuOpen, bool hasSession)
        => _chestAcceptance?.ObserveOrdinaryEntry(free, authority, menuOpen, hasSession);

    internal void ConfirmOrdinaryOpening() => _chestAcceptance?.ConfirmOrdinaryOpening();

    internal IFlowNetworkApplication ForOrdinaryEntry(FlowGameSession session)
        => _chestAcceptance?.ForOrdinaryEntry(session) ?? session;

    internal void ReportGameFailure(Exception error)
    {
        _uiAcceptance?.Fail(error);
        _chestAcceptance?.Fail(error);
    }

    // Separate phases retain the host's existing cleanup order, including failures during event detachment.
    internal void DisposeTransport()
        => DisposeAll(_acceptance, _chestAcceptance, _cancellationAcceptance, _returnAcceptance,
            _isolationAcceptance, _performanceAcceptance, _resourceAcceptance);

    internal void DisposeReadOnly()
        => DisposeAll(_itemNamesAcceptance, _uiAcceptance, _uiActionsAcceptance);

    public void Dispose()
    {
        try
        {
            try { CloseHost(); }
            finally { DisposeTransport(); }
        }
        finally { DisposeReadOnly(); }
    }

    internal static void DisposeAll(params IDisposable?[] resources)
    {
        ExceptionDispatchInfo? failure = null;
        foreach (IDisposable? resource in resources)
        {
            try { resource?.Dispose(); }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        }
        // Match nested finally: attempt every cleanup and propagate the last failure.
        failure?.Throw();
    }

    internal void CloseHost()
    {
        try { _host?.Dispose(); }
        finally { _host = null; }
    }

    private void RequireReadOnlyNamesSession()
        => FlowHostAcceptance.Require(_host is null && _currentSession() is null && !_hasSurface(),
            "Read-only name acceptance unexpectedly retained transport or a Flow surface.");

    private void RequireReadOnlyUiSession()
        => FlowHostAcceptance.Require(_host is null && _currentSession() is null,
            "Read-only Flow UI acceptance unexpectedly retained a production transport session.");

    internal bool RejectReadOnlySaveLifecycle(string lifecycle)
    {
        if (RejectNamesLifecycle(lifecycle)) return true;
        if (_uiAcceptance is null && _uiActionsAcceptance is null) return false;
        var error = new InvalidOperationException("Unexpected read-only Flow UI lifecycle: " + lifecycle);
        _uiAcceptance?.Fail(error);
        _uiActionsAcceptance?.Fail(error);
        return true;
    }

    internal bool RejectNamesLifecycle(string lifecycle)
    {
        if (_itemNamesAcceptance is null) return false;
        _itemNamesAcceptance.Fail(new InvalidOperationException("Unexpected read-only name acceptance lifecycle: " + lifecycle));
        return true;
    }

    internal void ReportFailure(Exception error)
    {
        try { CloseHost(); }
        catch (Exception cleanup) { error = new AggregateException("Flow host operation and cleanup failed.", error, cleanup); }
        _monitor.Log("Hatifect Flow host failed: " + error, LogLevel.Error);
        _acceptance?.Fail(error);
        _chestAcceptance?.Fail(error);
        _cancellationAcceptance?.Fail(error);
        _returnAcceptance?.Fail(error);
        _isolationAcceptance?.Fail(error);
        _performanceAcceptance?.Fail(error);
        _resourceAcceptance?.Fail(error);
        _itemNamesAcceptance?.Fail(error);
        _uiAcceptance?.Fail(error);
        _uiActionsAcceptance?.Fail(error);
    }
}
