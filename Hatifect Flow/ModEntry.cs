using System;
using Hatifect.Flow.Diagnostics;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace Hatifect.Flow;

/// <summary>SMAPI lifecycle and update boundary for Hatifect Flow.</summary>
public sealed partial class ModEntry : Mod
{
    private FlowDiagnosticsSession? _diagnostics;
    private bool _attached;

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
        _diagnostics = FlowDiagnosticsSession.TryCreate(Helper, Monitor, () => _gameSession,
            () => _parcelSurface is not null);
        // Retain the owner before starting probes: even failure reporting can throw during startup.
        _diagnostics?.Start(ShowParcel, ShowNetwork, OpenPlayerNetwork, CloseParcelSurface);
        try { ResolveFlowUi(); }
        catch (Exception error) { ReportGameFailure(error); }
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        if (_diagnostics?.TryHandleSaveLoaded() == true)
            return;
        OpenGameSession();
        _diagnostics?.OnGameSessionLoaded();
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (_diagnostics?.ExitAfterStartupFailure() == true)
            return;
        try
        {
            if (_diagnostics?.PrepareGameTick() == false)
                return;
            TickGameSession(e);
            _diagnostics?.Tick();
        }
        catch (Exception error) { ReportFailure(error); }
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        if (_diagnostics?.RejectNamesLifecycle("ReturnedToTitle") == true)
            return;
        try
        {
            CloseGameSession();
            _diagnostics?.OnReturnedToTitle();
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
                finally { _diagnostics?.CloseHost(); }
            }
            finally
            {
                _diagnostics?.DisposeTransport();
            }
        }
        finally
        {
            try
            {
                if (disposing)
                    _diagnostics?.DisposeReadOnly();
            }
            finally { base.Dispose(disposing); }
        }
    }

    private void ReportFailure(Exception error)
    {
        if (_diagnostics is not null)
            _diagnostics.ReportFailure(error);
        else
            Monitor.Log("Hatifect Flow host failed: " + error, LogLevel.Error);
    }
}
