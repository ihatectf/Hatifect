using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Hatifect.Flow.Application;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Inventory;
using Hatifect.Flow.Sessions;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Objects;
using static Hatifect.Flow.Diagnostics.FlowHostAcceptance;

namespace Hatifect.Flow.Diagnostics;

// Prepares physical fixture data only. Every station, route and shipment command must
// arrive through the application handed to the ordinary production keybind opening.
internal sealed class FlowPlayerNativeSequence : IDisposable
{
    private readonly IModHelper _helper;
    private readonly AcceptanceRequest _request;
    private readonly Vector2 _source, _destination, _standing;
    private readonly List<object> _generations = new(3);
    private readonly List<FlowPlayerNativeInputCapture> _retiredCaptures = new(2);
    private readonly string _initialState;
    private FlowGameSession _session;
    private FlowPlayerNativeInputCapture _capture;
    private readonly FlowPlayerInputUiEvidence _ui;
    private FlowPlayerCommandTrace[] _admissionCommands = Array.Empty<FlowPlayerCommandTrace>();
    private int _settled, _ticks;
    private bool _admitted, _disposed;
    private string? _lastStage;
    internal Guid WholeParcel { get; private set; }
    internal Guid PartialParcel { get; private set; }

    internal FlowPlayerNativeSequence(IModHelper helper, AcceptanceRequest request, FlowGameSession session,
        Vector2 source, Vector2 destination, Vector2 standing)
    {
        _helper = helper; _request = request; _session = session;
        _source = source; _destination = destination; _standing = standing;
        FlowSnapshot initial = session.ReadSnapshot();
        Require(initial.Stations.Count == 0 && initial.Links.Count == 0 && initial.Parcels.Count == 0
            && session.ReadResources().Payloads.Used == 0, "Ordinary input requires an initially empty Flow network.");
        _ui = new(request);
        _initialState = CaptureState();
        _capture = new(helper, session, request, CaptureCommandState);
        DelayedAction.warpAfterDelay("Farm", new Point((int)standing.X, (int)standing.Y), 10);
        Publish("await-fixture-position");
    }

    internal static (Chest Source, Chest Destination, Vector2 SourceTile, Vector2 DestinationTile, Vector2 Standing) CreateFixture()
    {
        GameLocation farm = Game1.getFarm();
        for (int y = 10; y < 50; y++)
        for (int x = 10; x < 50; x++)
        {
            var source = new Vector2(x, y);
            var destination = new Vector2(x + 2, y);
            var standing = new Vector2(x + 1, y + 1);
            bool open = true;
            // A continuous three-tile approach row keeps both chests locally accessible.
            for (int offset = 0; offset < 3; offset++)
            {
                var tile = new Vector2(x + offset, y + 1);
                if (!farm.isTileLocationOpen(tile) || farm.IsTileBlockedBy(tile)) { open = false; break; }
            }
            if (!open || !farm.isTileLocationOpen(source) || !farm.isTileLocationOpen(destination)
                || !farm.CanItemBePlacedHere(source, false) || !farm.CanItemBePlacedHere(destination, false)) continue;
            var first = new Chest(playerChest: true, tileLocation: source, itemId: "130");
            var second = new Chest(playerChest: true, tileLocation: destination, itemId: "130");
            farm.Objects.Add(source, first);
            farm.Objects.Add(destination, second);
            return (first, second, source, destination, standing);
        }
        throw new InvalidOperationException("No locally accessible ordinary-input chest fixture was found.");
    }

    internal void ObserveEntry(bool free, bool authority, bool menu, bool session)
        => _capture.ObserveEntry(free, authority, menu, session);

    internal void ConfirmOpened() => _capture.ConfirmOpened(_session.ReadNetwork().Target);

    internal IFlowNetworkApplication ForOrdinaryEntry(FlowGameSession session)
    {
        Require(!_disposed, "Ordinary input sequence is retired.");
        return _capture.ForOrdinaryEntry(session);
    }

    internal void OnSaveLoaded(FlowGameSession session)
    {
        if (!ReferenceEquals(_session, session))
        {
            Require(_admitted && _generations.Count < 2, "Unexpected ordinary-input session generation.");
            Archive();
            _session = session;
            _capture = new(_helper, session, _request, CaptureCommandState);
        }
    }

    internal bool Admit()
    {
        RequireHealthy();
        Require(!_disposed && ++_ticks <= 72000, "Ordinary input exceeded its bounded admission period.");
        if (_settled < 2)
        {
            bool positioned = Context.IsPlayerFree && Game1.currentLocation.NameOrUniqueName == "Farm"
                && (int)(Game1.player.Position.X / 64) == (int)_standing.X
                && (int)(Game1.player.Position.Y / 64) == (int)_standing.Y;
            _settled = positioned ? _settled + 1 : 0;
            Publish(_settled == 2 ? "ordinary-entry-source" : "await-fixture-position");
            return false;
        }
        FlowSnapshot snapshot = _session.ReadSnapshot();
        Require(snapshot.Stations.Count <= 2 && snapshot.Links.Count <= 1 && snapshot.Parcels.Count <= 2,
            "Ordinary input created unexpected stations, links or parcels.");
        string stage = snapshot.Stations.Count switch
        {
            0 => "ordinary-entry-source", 1 => "ordinary-entry-destination",
            _ => snapshot.Links.Count == 0 ? "author-route" : snapshot.Parcels.Count == 0 ? "send-whole"
                : snapshot.Parcels.Count == 1 ? "send-five" : "close-window-for-transport"
        };
        if (_ticks % 60 == 0 || snapshot.Parcels.Count == 2) Publish(stage);
        if (snapshot.Parcels.Count != 2) return false;
        if (_ticks % 6 != 0) return false;
        if (!_ui.HasInitialProbe() || !_ui.HasCommandResults(_capture.Commands.ToArray(),
            LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.ru))
        {
            Publish("await-native-visible-result-evidence");
            return false;
        }
        FlowPlayerCommandTrace[] applied = _capture.Commands.Where(value => value.Result?.Status == FlowCommandStatus.Applied).ToArray();
        Require(_capture.OpenedEntries >= 3 && _capture.HasRejectedBusyEntry && _capture.HasEmptyTargetOpening && applied.Length == 5
            && applied.Count(value => value.Command is FlowNetworkCommand { Action: FlowNetworkAction.RegisterStation }) == 2
            && applied.Count(value => value.Command is FlowNetworkCommand { Action: FlowNetworkAction.AddLink, Capacity: 999, TransitTicks: 180 }) == 1
            && applied.Count(value => value.Command is FlowSendCommand { Quantity: null }) == 1
            && applied.Count(value => value.Command is FlowSendCommand { Quantity: 5 }) == 1,
            "Ordinary entry did not author exactly two stations, one route and whole/partial sends through the journal.");
        FlowParcelSnapshot whole = snapshot.Parcels.Single(value => value.Quantity == 8);
        FlowParcelSnapshot partial = snapshot.Parcels.Single(value => value.Quantity == 5);
        Require(whole.State == ParcelState.Reserved && partial.State == ParcelState.Reserved,
            "Ordinary admission must be observed before physical transport.");
        Require(Items(_source).Sum(value => value.Stack) == 21 && Items(_destination).Length == 0,
            "Ordinary admission moved physical inventory before the scheduled effects.");
        WholeParcel = whole.Id; PartialParcel = partial.Id; _admitted = true;
        _admissionCommands = _capture.Commands.ToArray();
        Publish("close-window-for-transport");
        return true;
    }

    internal void Publish(string stage)
    {
        if (_lastStage == stage && _ticks % 60 != 0) return;
        _lastStage = stage;
        _capture.Publish(stage, new
        {
            location = "Farm", source = new { x = _source.X, y = _source.Y },
            destination = new { x = _destination.X, y = _destination.Y },
            standing = new { x = _standing.X, y = _standing.Y },
            sourceScreen = Screen(_source), destinationScreen = Screen(_destination), emptyScreen = Screen(_standing),
            sourceName = "src_" + _request.RunId.Replace("-", "")[..6],
            destinationName = "dst_" + _request.RunId.Replace("-", "")[..6],
            probeText = "native-" + _request.RunId.Replace("-", "")[..8],
            wholeParcelId = WholeParcel, partialParcelId = PartialParcel,
            state = JsonSerializer.Deserialize<JsonElement>(CaptureState())
        });
    }

    private static object Screen(Vector2 tile)
    {
        Vector2 local = Game1.GlobalToLocal(Game1.viewport, tile * 64f + new Vector2(32f, 32f));
        return new { x = local.X, y = local.Y };
    }

    private string CaptureState() => JsonSerializer.Serialize(new
    {
        snapshot = _session.ReadSnapshot(), network = _session.ReadNetwork(), resources = _session.ReadResources(),
        source = Items(_source).Select(FlowItemCodec.Encode).ToArray(),
        destination = Items(_destination).Select(FlowItemCodec.Encode).ToArray(), ui = _ui.Stamp()
    });

    private string CaptureCommandState()
    {
        Require(_ui.Stamp() is not null, "A command requires an observed ordinary Window before dispatch.");
        return CaptureState();
    }

    internal bool ObserveDelivered()
    {
        RequireHealthy();
        Require(++_ticks <= 72000, "Ordinary input exceeded its final reopening period.");
        if (_ticks % 60 == 0) Publish("ordinary-reopen-delivered-history");
        if (_ticks % 6 != 0) return false;
        return _capture.OpenedEntries > 0 && _ui.HasInitialProbe()
            && _ui.HasCommandResults(_admissionCommands,
                LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.ru)
            && _ui.HasDeliveredHistory(PartialParcel, LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.ru);
    }

    internal void RequireHealthy()
    {
        Exception? failure = _capture.Failure;
        for (int index = 0; failure is null && index < _retiredCaptures.Count; index++)
            failure = _retiredCaptures[index].Failure;
        if (failure is not null) throw new InvalidOperationException("An ordinary Window command failed unexpectedly.", failure);
    }

    internal void RequireCompletedObserver()
    {
        RequireHealthy();
        _ui.RequireCompletedObserver();
    }

    private static Item[] Items(Vector2 tile) => ((Chest)Game1.getFarm().Objects[tile])
        .GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Where(value => value is not null).ToArray();

    private void Archive()
    {
        _generations.Add(new { session = _session.ReadSnapshot().SessionId, evidence = _capture.Evidence });
        _retiredCaptures.Add(_capture);
        _capture.Dispose();
    }

    internal object Evidence => new
    {
        initial = JsonSerializer.Deserialize<JsonElement>(_initialState),
        generations = _generations.ToArray(), current = _capture.Evidence
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _capture.Dispose();
    }
}
