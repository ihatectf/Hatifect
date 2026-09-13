using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Hatifect.Flow.Application;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Inventory;
using Hatifect.Flow.Sessions;
using StardewValley;
using StardewValley.Objects;
using static Hatifect.Flow.Diagnostics.FlowHostAcceptance;

namespace Hatifect.Flow.Diagnostics;

internal sealed partial class FlowGameResourceAcceptance
{
    private const string ChestMarker = "Hatifect.Flow/ResourceChest";
    private static string Name(int index) => "resource_" + index;
    private static int Source(int group) => group == 0 ? 0 : group + 1;
    private static Item[] Items(Chest chest) => chest.GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Where(item => item is not null).ToArray();
    private static string[] Xml(Chest chest) => Items(chest).Select(FlowItemCodec.Encode).OrderBy(value => value, StringComparer.Ordinal).ToArray();

    private void CreateFixtureAndProfileRoutes()
    {
        var farm = Game1.getFarm();
        for (int index = 0; index < 32; index++)
        {
            bool placed = false;
            for (int y = 5; y < 20 && !placed; y++)
            for (int x = 5; x < 20 && !placed; x++)
            {
                var tile = new Vector2(x, y);
                if (farm.Objects.ContainsKey(tile)) continue;
                var chest = new Chest(playerChest: true, tileLocation: tile, itemId: "130");
                chest.modData[ChestMarker] = _request.RunId + ":" + index;
                Require(chest.GetActualCapacity() is >= 33 and <= 128, "Unsupported resource chest capacity.");
                farm.Objects.Add(tile, chest); _chests[index] = chest; _tiles[index] = tile; placed = true;
                _stations[index] = Session().RegisterStation(Name(index), "Farm", x, y, chest).Id;
            }
            Require(placed, "No empty resource fixture tile is available.");
            if (index > 0) Session().Link(Name(index - 1), Name(index), transitTicks: 1);
            if (index is 1 or 15 or 31)
            {
                ProfileRoute("cold", 0, index, index, 1);
                ProfileRoute("hit", 0, index, index, 0);
            }
        }
        int churn = 0;
        for (int origin = 0; origin < 32 && churn < 65; origin++)
        for (int destination = origin + 1; destination < 32 && churn < 65; destination++)
        {
            if (origin == 0 && destination is 1 or 15 or 31) continue;
            ProfileRoute("churn", origin, destination, destination - origin, 1); churn++;
        }
        ProfileRoute("evicted", 0, 2, 2, 1);
        ProfileRoute("anchor", 0, 31, 31, 1);
        Session().Link(Name(31), Name(30), transitTicks: 1);
        ProfileRoute("independent", 0, 31, 31, 0);
        Session().Link(Name(0), Name(31), transitTicks: 1);
        ProfileRoute("dependent", 0, 31, 1, 1);
        Require(_routes.Count == 75, "The fixed route profile was incomplete."); _passed.Add("routing");
        // Finish every topology change before reserving cargo: route revisions are part of departure authority.
        for (int group = 1; group < 8; group++) Session().Link(Name(Source(group)), Name(1), transitTicks: 1);
        for (int group = 0; group < 8; group++)
        {
            _cargoXml[group] = new string[32];
            for (int index = 0; index < 32; index++)
            {
                var wine = (StardewValley.Object)ItemRegistry.Create("(O)348", 1, 4);
                wine.preserve.Value = StardewValley.Object.PreserveType.Wine;
                wine.preservedParentSheetIndex.Value = "613";
                wine.modData["Hatifect.Flow/ResourceCargo"] = _request.RunId + ":" + group + ":" + index;
                _chests[Source(group)].GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Add(wine);
                _cargoXml[group][index] = FlowItemCodec.Encode(wine);
            }
        }
        Item overflow = ItemRegistry.Create("(O)388", 1);
        overflow.modData["Hatifect.Flow/ResourceOverflow"] = _request.RunId;
        _chests[0].GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Add(overflow);
        _overflowXml = FlowItemCodec.Encode(overflow);
        for (int index = 0; index < _chests[1].GetActualCapacity(); index++)
        {
            Item filler = ItemRegistry.Create("(O)390", 999);
            filler.modData["Hatifect.Flow/ResourceFiller"] = _request.RunId + ":" + index;
            _chests[1].GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Add(filler);
        }
        _fillers = Xml(_chests[1]);
    }

    private void ProfileRoute(string kind, int origin, int destination, int hops, long searches)
    {
        Require(_routes.Count < 75, "Resource route profile exceeded its bound.");
        FlowGameSession session = Session();
        long before = session.TickCounters.RouteSearches;
        FlowRoutePreview route = FlowResourceCost.Measure(() => session.PreviewRoute(_stations[origin], _stations[destination]), out FlowResourceCost cost);
        FlowGameResources resources = session.ReadResources();
        Require(route.Found && route.LinkCount == hops && route.TransitTicks == hops && route.AvailableUnits == 999
            && resources.Runtime.RouteSearches - before == searches && resources.Runtime.RouteCache.Used <= 64,
            "The actual route query violated its cache/dependency or search bound.");
        _routes.Add(new(kind, (int)resources.Runtime.Stations.Used, origin, destination, hops, before,
            resources.Runtime.RouteSearches, resources.Runtime.RouteCache.Used, cost));
    }

    private void RebindFixture()
    {
        var farm = Game1.getFarm();
        for (int index = 0; index < 32; index++)
        {
            Require(farm.Objects.TryGetValue(_tiles[index], out StardewValley.Object? value) && value is Chest,
                "A saved resource chest disappeared.");
            Chest chest = (Chest)farm.Objects[_tiles[index]];
            Require(chest.modData.TryGetValue(ChestMarker, out string marker) && marker == _request.RunId + ":" + index
                && chest.modData.TryGetValue(FlowGameSession.StationKey, out string station) && station == _stations[index].ToString("D"),
                "A saved resource chest changed ownership or station identity.");
            _chests[index] = chest;
        }
    }

    private void AdmitTo(int count)
    {
        Require(_attempt == 0 && count is 32 or 128 or 256 && _admitted < count && _ownsPause, "Unexpected resource admission scale.");
        for (; _admitted < count; _admitted++)
        {
            int group = _admitted / 32, index = _admitted % 32;
            _parcels[_admitted] = Session().Send(Name(Source(group)), Name(1), index);
            Item item = _chests[Source(group)].GetItemsForPlayer(Game1.player.UniqueMultiplayerID)[index];
            _cargoXml[group][index] = FlowItemCodec.Encode(item);
            Guid cargo = Guid.Parse(item.modData[ChestInventoryAccess.CargoKey]);
            _payloadXml.Add(cargo, _cargoXml[group][index]);
        }
        VerifyState();
    }

    private void VerifyState()
    {
        FlowGameSession session = Session(); FlowSnapshot snapshot = session.ReadSnapshot();
        FlowGameResources resources = session.ReadResources();
        Require(snapshot.Stations.Count == 32 && snapshot.Links.Count == 40 && snapshot.Parcels.Count == _admitted
            && snapshot.Parcels.Select(parcel => parcel.Id).ToHashSet().SetEquals(_parcels.Take(_admitted))
            && snapshot.Parcels.All(parcel => parcel.Quantity == 1 && _payloadXml.ContainsKey(parcel.CargoId)
                && parcel.DeliveryAttempts == _attempt && parcel.State == (_attempt == 0 ? ParcelState.Reserved : ParcelState.DeliveryRejected)),
            "The retained resource parcels lost identity, quantity, state or attempts.");
        Require(resources.Runtime.Cargo.Used == _admitted && resources.Payloads.Used == _admitted
            && resources.Runtime.PendingOperations.Used == (_attempt == 0 ? _admitted : 0)
            && resources.Runtime.IssuedTransfers.Used == (_attempt == 0 ? 0 : _admitted * (_attempt + 1))
            && resources.Runtime.RetiredTransfers == resources.Runtime.IssuedTransfers.Used
            && resources.Ports.Sum(port => port.Port.Receipts.Used) == resources.Runtime.RetiredTransfers,
            "Resource authority, queue or receipt counts disagree.");
        for (int group = 0; group < 8; group++)
        {
            string[] expected = _attempt == 0 ? _cargoXml[group] : Array.Empty<string>();
            if (group == 0) expected = expected.Append(_overflowXml).ToArray();
            Require(Xml(_chests[Source(group)]).SequenceEqual(expected.OrderBy(value => value, StringComparer.Ordinal)),
                "The physical source changed its exact saved item XML or quantity.");
            FlowPortResources port = resources.Ports.Single(port => port.StationId == _stations[Source(group)]).Port;
            int admittedHere = Math.Clamp(_admitted - group * 32, 0, 32);
            Require(port.Custody.Used == (_attempt == 0 ? admittedHere : 0) && port.Receipts.Used == (_attempt == 0 ? 0 : admittedHere),
                "Source custody or extraction receipts changed.");
        }
        Require(Xml(_chests[1]).SequenceEqual(_fillers), "The full destination lost or gained a physical item.");
        FlowPortResources destination = resources.Ports.Single(port => port.StationId == _stations[1]).Port;
        Require(destination.Custody.Used == 0 && destination.Receipts.Used == _admitted * _attempt, "Rejected destination receipts are incomplete.");
        for (int index = 9; index < 32; index++) Require(Items(_chests[index]).Length == 0, "Routing chest gained physical cargo.");
    }

    private void VerifyRefusals()
    {
        FlowGameSession session = Session(); FlowSnapshot before = session.ReadSnapshot();
        FlowGameResources resources = session.ReadResources();
        FlowInventorySlot overflow = session.ReadInventory(_stations[0]).Single();
        try
        {
            session.Send(Name(0), Name(1), overflow.Index);
            throw new InvalidOperationException("The 257th retained cargo was admitted.");
        }
        catch (FlowResourceLimitException error)
        {
            Require(error.Resource == FlowAdmissionResource.RetainedCargo && error.Used == 256 && error.Limit == 256,
                "The send was rejected for the wrong resource."); _sendRefusals++;
        }
        Require(session.Execute(new FlowSendCommand(before.SessionId, before.Revision, _stations[0], _stations[1], overflow.Index, overflow.Fingerprint)).Status
            == FlowCommandStatus.Rejected, "The typed send bypassed retained capacity."); _sendRefusals++;
        foreach (Guid parcel in _parcels)
        {
            foreach (FlowParcelAction action in new[] { FlowParcelAction.RetryDelivery, FlowParcelAction.ReturnToSource })
            {
                FlowCommandResult result = session.Execute(new FlowParcelCommand(before.SessionId, before.Revision, parcel, action));
                Require(result.Status == FlowCommandStatus.Rejected && result.Code == FlowRejectionCode.RetryLimit && result.Revision == before.Revision,
                    "Attempt seventeen changed authority or returned the wrong refusal.");
                if (action == FlowParcelAction.RetryDelivery) _retryRefusals++; else _returnRefusals++;
            }
        }
        FlowGameResources after = session.ReadResources();
        Require(ReferenceEquals(before, session.ReadSnapshot()) && after.Runtime == resources.Runtime && after.Ports.SequenceEqual(resources.Ports)
            && after.TickCounters == resources.TickCounters && after.AdmissionRejections == new FlowAdmissionRejections(0, 0, 2)
            && !Items(_chests[0]).Single().modData.ContainsKey(ChestInventoryAccess.CargoKey), "Refusal changed inventory, revision, tickets, receipts or host health.");
        VerifyState(); _passed.Add("limits");
    }
}
