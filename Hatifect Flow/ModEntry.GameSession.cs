using System;
using System.Globalization;
using System.Diagnostics;
using System.Linq;
using Microsoft.Xna.Framework;
using Hatifect.Flow.Application;
using Hatifect.Flow.Diagnostics;
using Hatifect.Flow.Inventory;
using Hatifect.Flow.Sessions;
using Hatifect.Flow.UI.Semantic;
using Hatifect.UI;
using Hatifect.UI.Experience;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Objects;

namespace Hatifect.Flow;

public sealed partial class ModEntry
{
    private readonly FlowSessionOwner _sessionOwner = new();
    private FlowGameSession? _gameSession => _sessionOwner.ForScreen(Context.ScreenId);
    private IUiSemanticSurfaceApi? _flowUi;
    private readonly PerScreen<FlowScreenState> _screens = new(() => new FlowScreenState());
    private ParcelSurface? _parcelSurface { get => _screens.Value.Surface; set => _screens.Value.Surface = value; }
    private Guid? _selectedParcel { get => _screens.Value.SelectedParcel; set => _screens.Value.SelectedParcel = value; }
    private sealed class FlowScreenState
    {
        internal ParcelSurface? Surface;
        internal Guid? SelectedParcel;
    }

    private void AttachGameSessionEvents()
    {
        _config = Helper.ReadConfig<FlowConfig>();
        Helper.Events.Input.ButtonsChanged += OnFlowButtonsChanged;
        Helper.Events.GameLoop.Saving += OnGameSaving;
        Helper.Events.GameLoop.Saved += OnGameSaved;
        Helper.Events.GameLoop.SaveCreated += OnGameCreated;
        Helper.ConsoleCommands.Add("hatifect_flow", "Flowline transport. Usage: hatifect_flow help", OnFlowCommand);
    }

    private void DetachGameSessionEvents()
    {
        Helper.Events.Input.ButtonsChanged -= OnFlowButtonsChanged;
        Helper.Events.GameLoop.Saving -= OnGameSaving;
        Helper.Events.GameLoop.Saved -= OnGameSaved;
        Helper.Events.GameLoop.SaveCreated -= OnGameCreated;
    }

    private void ResolveFlowUi()
    {
        _flowHostUi = Helper.ModRegistry.GetApi<IUiSemanticHostApi>("Hatifect.UI");
        _flowUi = Helper.ModRegistry.GetApi<IUiSemanticSurfaceApi>("Hatifect.UI");
        if (_flowUi is null || _flowUi.ApiVersion < 1)
            Monitor.Log("Flowline shipment surfaces need Hatifect UI API v1.", LogLevel.Warn);
    }

    private static bool IsGameAuthority() => Context.IsWorldReady && Context.IsMainPlayer && !Context.IsMultiplayer;

    private void OpenGameSession()
    {
        try
        {
            CloseGameSession();
            if (!IsGameAuthority())
            {
                Monitor.Log("Flowline chest transport is enabled only in single-player saves.", LogLevel.Info);
                return;
            }
            FlowGameSave? saved = null;
            FlowResourceCost read = default, restore = default;
            FlowGameSession? session = _sessionOwner.Open(Context.ScreenId, Context.IsMainPlayer, () =>
            {
                if (_resourceAcceptance is null)
                    return new FlowGameSession(Game1.uniqueIDForThisGame, Game1.player.UniqueMultiplayerID,
                        IsGameAuthority, ResolveStationChest, ReportGameFailure, ReadGameSaveData(),
                        new FlowChestLocks(() => Context.IsMultiplayer, ReportGameFailure));
                saved = FlowResourceCost.Measure(ReadGameSaveData, out read);
                return FlowResourceCost.Measure(() => new FlowGameSession(Game1.uniqueIDForThisGame, Game1.player.UniqueMultiplayerID,
                    IsGameAuthority, ResolveStationChest, ReportGameFailure, saved,
                    new FlowChestLocks(() => Context.IsMultiplayer, ReportGameFailure)), out restore);
            });
            if (session is not null) _resourceAcceptance?.ObserveLoad(session, saved, read, restore);
            if (session?.IsFaulted == true)
                Monitor.Log("Flowline transport is paused: a previous inventory error requires recovery. Saved cargo is retained; automatic replay is disabled.", LogLevel.Error);
        }
        catch (Exception error) { ReportGameFailure(error); }
    }

    private FlowGameSave? ReadGameSaveData()
        => Helper.Data.ReadSaveData<FlowGameSave>(FlowGameSession.SaveKey);

    private static Chest? ResolveStationChest(StationBinding binding)
    {
        GameLocation? location = Game1.getLocationFromName(binding.Location);
        return location is not null && location.Objects.TryGetValue(new Vector2(binding.X, binding.Y), out StardewValley.Object? item)
            ? item as Chest : null;
    }

    private void TickGameSession(UpdateTickedEventArgs e)
    {
        FlowGameSession? session = _gameSession;
        if (session is not null && (_performanceAcceptance is not null || _resourceAcceptance is not null))
        {
            bool timePasses = Game1.shouldTimePass();
            long allocated = GC.GetAllocatedBytesForCurrentThread(), started = Stopwatch.GetTimestamp();
            session.Tick(timePasses);
            long elapsed = Stopwatch.GetTimestamp() - started;
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            _performanceAcceptance?.ObserveTick(session, timePasses, elapsed, allocated);
            _resourceAcceptance?.ObserveTick(session, timePasses, elapsed, allocated);
        }
        else session?.Tick(Game1.shouldTimePass());
        if (_parcelSurface is not null && (!_parcelSurface.IsClosed || e.IsOneSecond))
        {
            try { _parcelSurface.Pump(); }
            catch (Exception error) { ReportGameFailure(error); }
        }
    }

    private void OnGameSaving(object? sender, SavingEventArgs e)
    {
        if (RejectReadOnlySaveLifecycle("Saving")) return;
        if (_gameSession is null) return;
        try { CloseParcelSurface(); }
        catch (Exception error) { ReportGameFailure(error); }
        try
        {
            FlowGameSession session = _gameSession;
            if (_resourceAcceptance is null) Helper.Data.WriteSaveData(FlowGameSession.SaveKey, session.BeginSave());
            else
            {
                FlowGameSave saved = FlowResourceCost.Measure(session.BeginSave, out FlowResourceCost capture);
                FlowResourceCost write = FlowResourceCost.Measure(() => Helper.Data.WriteSaveData(FlowGameSession.SaveKey, saved));
                _resourceAcceptance.ObserveSave(session, saved, capture, write);
            }
        }
        catch (Exception error) { ReportGameFailure(error); }
    }

    private void OnGameSaved(object? sender, SavedEventArgs e)
    {
        if (RejectReadOnlySaveLifecycle("Saved")) return;
        _gameSession?.EndSave();
    }
    private void OnGameCreated(object? sender, SaveCreatedEventArgs e)
    {
        if (RejectReadOnlySaveLifecycle("SaveCreated")) return;
        if (_acceptance is null && _gameSession is null) OpenGameSession();
    }

    private void CloseGameSession()
    {
        try { CloseParcelSurface(); }
        finally
        {
            _sessionOwner.Close(Context.ScreenId);
            _selectedParcel = null;
        }
    }

    private void CloseParcelSurface()
    {
        _parcelSurface?.Dispose();
        _parcelSurface = null;
    }

    private void DisposeGameSessions()
    {
        foreach (var screen in _screens.GetActiveValues()) screen.Value.Surface?.Dispose();
        _sessionOwner.Dispose();
        _screens.ResetAllScreens();
    }

    private void ReportGameFailure(Exception error)
    {
        Monitor.Log("Flowline game session: " + error, LogLevel.Error);
        _uiAcceptance?.Fail(error);
        _chestAcceptance?.Fail(error);
    }

    private void OnFlowCommand(string command, string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] == "help")
            {
                Monitor.Log("Flowline (single player, whole ordinary object stacks):\n"
                    + "  hatifect_flow station <name> — bind the chest under the cursor\n"
                    + "  hatifect_flow rename <name> <new-name> | rebind <name> — use the chest under the cursor\n"
                    + "  hatifect_flow link <source> <destination> [capacity=999] [ticks=180] — one-way link\n"
                    + "  hatifect_flow send <source> <destination> <slot=1-based> — dispatch a whole stack\n"
                    + "  hatifect_flow list — stations and shipments\n"
                    + "  hatifect_flow diagnostics — bounded resource usage, remaining capacity and retained limits\n"
                    + "  hatifect_flow target — remember the chest under the cursor; network — open network controls over a game menu\n"
                    + "  hatifect_flow show [parcel-id] — open shipment controls over an open game menu\n"
                    + "  hatifect_flow cancel|reserve|retry|return <parcel-id> — shipment action\n"
                    + "  hatifect_flow recovery | recover <parcel-id> — inspect recovery or reconcile a known saved receipt", LogLevel.Info);
                return;
            }
            FlowGameSession session = _gameSession ?? throw new InvalidOperationException("Load a single-player save first.");
            switch (args[0])
            {
                case "diagnostics" when args.Length == 1:
                    Monitor.Log(session.ReadResources().Format(), LogLevel.Info);
                    break;
                case "recovery" when args.Length == 1:
                    Monitor.Log(string.Join("\n", session.ReadRecovery().Select(value => $"{value.ParcelId}: {value.Phase}, receipt={value.Receipt}, can reconcile={value.CanReconcile}")), LogLevel.Info);
                    break;
                case "recover" when args.Length == 2:
                    FlowSnapshot recovering = session.ReadSnapshot();
                    Monitor.Log("Recovery: " + session.Execute(new FlowRecoveryCommand(recovering.SessionId, recovering.Revision, Guid.Parse(args[1]))).Status, LogLevel.Info);
                    break;
                case "target" when args.Length == 1:
                    Vector2 targetTile = Helper.Input.GetCursorPosition().GrabTile;
                    if (!Game1.currentLocation.Objects.TryGetValue(targetTile, out StardewValley.Object? targetItem) || targetItem is not Chest targetChest)
                        throw new ArgumentException("Point at an ordinary player chest.");
                    session.CaptureTarget(Game1.currentLocation.NameOrUniqueName, (int)targetTile.X, (int)targetTile.Y, targetChest);
                    Monitor.Log("Chest captured for Flowline network controls.", LogLevel.Info);
                    break;
                case "network" when args.Length == 1:
                    ShowNetwork(session);
                    break;
                case "rename" when args.Length == 3:
                    session.RenameStation(args[1], args[2]);
                    Monitor.Log("Station renamed.", LogLevel.Info);
                    break;
                case "station" or "rebind" when args.Length == 2:
                    Vector2 tile = Helper.Input.GetCursorPosition().GrabTile;
                    if (!Game1.currentLocation.Objects.TryGetValue(tile, out StardewValley.Object? item) || item is not Chest chest)
                        throw new ArgumentException("Point at an ordinary player chest before binding a station.");
                    if (args[0] == "station") session.RegisterStation(args[1], Game1.currentLocation.NameOrUniqueName, (int)tile.X, (int)tile.Y, chest);
                    else session.RebindStation(args[1], Game1.currentLocation.NameOrUniqueName, (int)tile.X, (int)tile.Y, chest);
                    Monitor.Log("Station bound: " + args[1], LogLevel.Info);
                    break;
                case "link" when args.Length is >= 3 and <= 5:
                    session.Link(args[1], args[2], args.Length > 3 ? ParseNumber(args[3]) : 999, args.Length > 4 ? ParseNumber(args[4]) : 180);
                    Monitor.Log("Route link registered.", LogLevel.Info);
                    break;
                case "send" when args.Length == 4:
                    _selectedParcel = session.Send(args[1], args[2], checked(ParseNumber(args[3]) - 1));
                    Monitor.Log("Shipment: " + _selectedParcel + ". Use hatifect_flow show with a game menu open.", LogLevel.Info);
                    break;
                case "list" when args.Length == 1:
                    FlowSnapshot snapshot = session.Application.ReadSnapshot();
                    Monitor.Log("Stations: " + string.Join(", ", snapshot.Stations.Select(value => session.StationName(value.Id)))
                        + "\n" + string.Join("\n", snapshot.Parcels.Select(value => $"{value.Id}: {value.ItemKey} × {value.Quantity}, {value.State}")), LogLevel.Info);
                    break;
                case "show" when args.Length is 1 or 2:
                    Guid? selected = args.Length == 2 ? Guid.Parse(args[1])
                        : _selectedParcel ?? session.Application.ReadSnapshot().Parcels.FirstOrDefault()?.Id;
                    ShowParcel(session.Application, selected, session.StationName);
                    break;
                case "cancel" or "reserve" or "retry" or "reconcile" or "return" when args.Length == 2:
                    FlowParcelAction action = args[0] switch
                    {
                        "cancel" => FlowParcelAction.Cancel, "reserve" => FlowParcelAction.Reserve,
                        "retry" => FlowParcelAction.RetryDelivery, "return" => FlowParcelAction.ReturnToSource, _ => FlowParcelAction.ReconcileTransfer
                    };
                    FlowSnapshot current = session.Application.ReadSnapshot();
                    FlowCommandResult result = session.Application.Execute(new FlowParcelCommand(current.SessionId, current.Revision, Guid.Parse(args[1]), action));
                    Monitor.Log("Shipment command: " + result.Status, LogLevel.Info);
                    break;
                default: throw new ArgumentException("Invalid command. Use hatifect_flow help.");
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException or OverflowException)
        { Monitor.Log("Flowline: " + error.Message, LogLevel.Warn); }
        catch (Exception error) { ReportGameFailure(error); }
    }

    // Production and exact native acceptance share the same consumer opening/retirement path.
    private NetworkExperience ShowNetwork(IFlowNetworkApplication application, IUiSemanticSurfaceApi? api = null)
    {
        api ??= _flowUi;
        if (api is null || api.ApiVersion < 1 || Game1.activeClickableMenu is null)
            throw new InvalidOperationException("Open a game menu; the Hatifect UI surface API must be available.");
        var experience = new NetworkExperience(new UiSymbolId("Hatifect.Flow", "network"), application,
            LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.ru,
            key => ItemRegistry.GetDataOrErrorItem(key).DisplayName);
        try { CloseParcelSurface(); }
        catch { experience.Dispose(); throw; }
        _parcelSurface = new ParcelSurface(experience);
        _parcelSurface.Show(api);
        return experience;
    }

    private ParcelExperience ShowParcel(IFlowApplication application, Guid? selected, Func<Guid, string> stationName,
        IUiSemanticSurfaceApi? api = null)
    {
        api ??= _flowUi;
        if (api is null || api.ApiVersion < 1 || Game1.activeClickableMenu is null)
            throw new InvalidOperationException("Open a game menu; the Hatifect UI surface API must be available.");
        var experience = new ParcelExperience(new UiSymbolId("Hatifect.Flow", "parcel"), application, selected,
            LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.ru,
            stationName, localizedItemName: FlowItemNames.Capture);
        try { CloseParcelSurface(); }
        catch { experience.Dispose(); throw; }
        _parcelSurface = new ParcelSurface(experience);
        _parcelSurface.Show(api);
        return experience;
    }

    private static int ParseNumber(string text) => int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
}
