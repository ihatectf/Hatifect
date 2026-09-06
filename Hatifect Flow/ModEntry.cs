using System;
using Hatifect.Flow.Diagnostics;
using Hatifect.Flow.Infrastructure.Persistence;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace Hatifect.Flow;

/// <summary>SMAPI lifecycle and update boundary for Hatifect Flow.</summary>
public sealed partial class ModEntry : Mod
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
    private bool _attached;
    private bool _startupFailed;

    public override void Entry(IModHelper helper)
    {
        if (_attached) throw new InvalidOperationException("Hatifect Flow host is already attached.");
        _attached = true;
        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        AttachGameSessionEvents();
        Monitor.Log("Hatifect Flow loaded. Use hatifect_flow help for single-player chest transport.", LogLevel.Info);
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        try
        {
            _itemNamesAcceptance = FlowItemNamesAcceptance.TryCreate(Helper, Monitor);
            _acceptance = FlowHostAcceptance.TryCreate(Helper, Monitor);
            _chestAcceptance = FlowChestRoundtripAcceptance.TryCreate(Helper, Monitor, () => _gameSession);
            _cancellationAcceptance = FlowChestCancellationAcceptance.TryCreate(Helper, Monitor, () => _gameSession);
            _returnAcceptance = FlowChestReturnAcceptance.TryCreate(Helper, Monitor, () => _gameSession);
            _isolationAcceptance = FlowChestIsolationAcceptance.TryCreate(Helper, Monitor, () => _gameSession);
            _performanceAcceptance = FlowGamePerformanceAcceptance.TryCreate(Helper, Monitor, () => _gameSession);
            _resourceAcceptance = FlowGameResourceAcceptance.TryCreate(Helper, Monitor, () => _gameSession);
        }
        catch (Exception error)
        {
            _startupFailed = true;
            ReportFailure(error);
        }
        try { ResolveFlowUi(); }
        catch (Exception error) { ReportGameFailure(error); }
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        if (_itemNamesAcceptance is not null)
        {
            try
            {
                RequireReadOnlyNamesSession();
                _itemNamesAcceptance.OnSaveLoaded();
            }
            catch (Exception error) { ReportFailure(error); }
            return;
        }
        if (_acceptance is null)
        {
            OpenGameSession();
            _chestAcceptance?.OnSaveLoaded();
            _cancellationAcceptance?.OnSaveLoaded();
            _returnAcceptance?.OnSaveLoaded();
            _isolationAcceptance?.OnSaveLoaded();
            _performanceAcceptance?.OnSaveLoaded();
            _resourceAcceptance?.OnSaveLoaded();
            return;
        }
        try
        {
            if (!Context.IsMainPlayer || !Context.IsWorldReady)
                throw new InvalidOperationException("Flow host startup requires the authoritative loaded world.");
            _host ??= new DurableFlowHost();
            bool started = _host.Start(_acceptance.SessionIdentity, _acceptance.OpenSession);
            _acceptance.OnSaveLoaded(_host, started);
        }
        catch (Exception error) { ReportFailure(error); }
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (_startupFailed)
        {
            // A rejected isolated harness configuration cannot produce acceptance
            // evidence. End this requested process instead of waiting indefinitely.
            Game1.game1.Exit();
            return;
        }
        try
        {
            if (_itemNamesAcceptance is not null)
            {
                RequireReadOnlyNamesSession();
                _itemNamesAcceptance.Tick();
                return;
            }
            TickGameSession(e);
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
        catch (Exception error) { ReportFailure(error); }
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        if (RejectNamesLifecycle("ReturnedToTitle")) return;
        try
        {
            CloseGameSession();
            CloseHost();
            _acceptance?.OnReturnedToTitle();
            _chestAcceptance?.OnReturnedToTitle();
            _cancellationAcceptance?.OnReturnedToTitle();
            _returnAcceptance?.OnReturnedToTitle();
            _isolationAcceptance?.OnReturnedToTitle();
            _performanceAcceptance?.OnReturnedToTitle();
            _resourceAcceptance?.OnReturnedToTitle();
        }
        catch (Exception error) { ReportFailure(error); }
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            if (!disposing) return;
            if (_attached)
            {
                Helper.Events.GameLoop.GameLaunched -= OnGameLaunched;
                Helper.Events.GameLoop.SaveLoaded -= OnSaveLoaded;
                Helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
                Helper.Events.GameLoop.ReturnedToTitle -= OnReturnedToTitle;
                _attached = false;
                DetachGameSessionEvents();
            }
            try
            {
                try { DisposeGameSessions(); }
                finally { CloseHost(); }
            }
            finally
            {
                try { _acceptance?.Dispose(); }
                finally
                {
                    try { _chestAcceptance?.Dispose(); }
                    finally
                    {
                        try { _cancellationAcceptance?.Dispose(); }
                        finally { try { _returnAcceptance?.Dispose(); } finally { try { _isolationAcceptance?.Dispose(); } finally { try { _performanceAcceptance?.Dispose(); } finally { _resourceAcceptance?.Dispose(); } } } }
                    }
                }
            }
        }
        finally
        {
            try { if (disposing) _itemNamesAcceptance?.Dispose(); }
            finally { base.Dispose(disposing); }
        }
    }

    private void CloseHost()
    {
        try { _host?.Dispose(); }
        finally { _host = null; }
    }

    private void RequireReadOnlyNamesSession()
        => FlowHostAcceptance.Require(_host is null && _gameSession is null && _parcelSurface is null,
            "Read-only name acceptance unexpectedly retained transport or a Flow surface.");

    private bool RejectNamesLifecycle(string lifecycle)
    {
        if (_itemNamesAcceptance is null) return false;
        _itemNamesAcceptance.Fail(new InvalidOperationException("Unexpected read-only name acceptance lifecycle: " + lifecycle));
        return true;
    }

    private void ReportFailure(Exception error)
    {
        try { CloseHost(); }
        catch (Exception cleanup) { error = new AggregateException("Flow host operation and cleanup failed.", error, cleanup); }
        Monitor.Log("Hatifect Flow host failed: " + error, LogLevel.Error);
        _acceptance?.Fail(error);
        _chestAcceptance?.Fail(error);
        _cancellationAcceptance?.Fail(error);
        _returnAcceptance?.Fail(error);
        _isolationAcceptance?.Fail(error);
        _performanceAcceptance?.Fail(error);
        _resourceAcceptance?.Fail(error);
        _itemNamesAcceptance?.Fail(error);
    }
}
