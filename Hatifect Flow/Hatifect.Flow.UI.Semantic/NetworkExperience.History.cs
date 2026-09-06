using Hatifect.Flow.Application;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.UI;
using Hatifect.UI.Experience;
using Hatifect.UI.Semantics;

namespace Hatifect.Flow.UI.Semantic;

internal sealed partial class NetworkExperience
{
    private const int HistoryPageSize = 12;
    private FlowTextSource _historyQuery = null!;
    private UiPublishedState<string> _historyPage = null!, _historyAvailability = null!, _historyDetails = null!;
    private UiPublishedState<FlowParcelSnapshot?> _historySelectedParcel = null!;
    private FlowSelectionSource<string> _historyFilter = null!;
    private FlowSelectionSource<FlowParcelSnapshot> _history = null!;
    private Func<string, string> _historyItemName = null!;
    private int _page => _projection.Value.Page;
    private int _historyCount => _projection.Value.HistoryCount;

    private void InitializeHistory(UiSymbolId id, Func<string, string> itemName)
    {
        _historyItemName = value => Format(itemName, value);
        UiPublishedState<string> State(string key) => _publication.State(id.Child("source/" + key), "", UiSourceTypes.String);
        _historyQuery = new(State("history-query"), RequestEdit);
        _historyPage = State("history-page"); _historyAvailability = State("history-availability"); _historyDetails = State("history-details");
        _historySelectedParcel = _publication.State<FlowParcelSnapshot?>(id.Child("source/history-selected"), null,
            new(FlowUiDataTypes.Parcel.Descriptor with { Nullable = true }));
        _historyFilter = Selection(id.Child("source/history-filter-values"), new[] { "all", "active", "attention" }, UiSourceTypes.String, value => id.Child("history-filter/" + value),
            value => value == "all" ? Text("All shipments", "Все отправления") : value == "active" ? Text("Active shipments", "Активные отправления") : Text("Needs attention", "Требуют внимания"),
            selectedItemId: id.Child("history-filter/all"));
        _history = Selection(id.Child("source/history-values"), Array.Empty<FlowParcelSnapshot>(), FlowUiDataTypes.Parcel, value => id.Child("history/" + value.Id.ToString("N")),
            value => _historyItemName(value.ItemKey) + " × " + value.Quantity,
            supportingText: value => StationName(value.Origin) + " → " + StationName(value.Destination) + " · " + Describe(value.State));
    }

    private void AppendHistory(UiExperienceBuilder builder, UiSymbolId id)
    {
        UiSymbolId history = id.Child("element/history");
        UiSymbolId selected = id.Child("source/history-selection");
        UiSymbolId filter = id.Child("source/history-filter");
        UiSymbolId details = id.Child("source/history-payload");
        builder.Source(selected, "SelectedShipment", "Selected shipment ID", new UiSelectionSource(_history),
                UiSourceTypes.Selection(FlowUiDataTypes.Parcel), UiCapabilities.Select)
            .Source(filter, "SelectedHistoryFilter", "Selected filter", new UiSelectionSource(_historyFilter),
                UiSourceTypes.Selection(UiSourceTypes.String), UiCapabilities.Select, UiCapabilities.Filter)
            .Source(details, "ShipmentPayload", "Shipment payload", _historySelectedParcel,
                new UiSourceType<FlowParcelSnapshot?>(FlowUiDataTypes.Parcel.Descriptor with { Nullable = true }), UiCapabilities.Inspect);
        builder.Element(id.Child("element/history-search"), "HistorySearch", Text("Find shipments", "Поиск отправлений"), _historyQuery, UiSourceTypes.String, UiCapabilities.Search)
            .Element(id.Child("element/history-filter"), "HistoryFilter", Text("Shipment filter", "Фильтр отправлений"), _historyFilter, UiSourceTypes.Collection(UiSourceTypes.String), UiCapabilities.Select)
            .Element(id.Child("element/history"), "History", Text("Shipments", "Отправления"), _history, UiSourceTypes.Collection(FlowUiDataTypes.Parcel), UiCapabilities.Select, UiCapabilities.Browse)
            .Element(id.Child("element/history-page"), "HistoryPage", Text("Page", "Страница"), _historyPage, UiSourceTypes.String, UiCapabilities.Monitor)
            .Element(id.Child("element/history-detail"), "HistoryDetail", Text("Selected shipment", "Выбранное отправление"), _historyDetails, UiSourceTypes.String, UiCapabilities.Inspect)
            .Element(id.Child("element/history-availability"), "HistoryAvailability", Text("Availability", "Доступность"), _historyAvailability, UiSourceTypes.String, UiCapabilities.Monitor)
            .Actions(id.Child("element/history-navigation"), "HistoryNavigation", Text("History navigation", "Навигация истории"),
                new UiActionDefinition(id.Child("action/previous-page"), Text("Previous page", "Предыдущая страница"), () => ChangePage(-1), () => CanRequest && IsActive && _page > 0),
                new UiActionDefinition(id.Child("action/next-page"), Text("Next page", "Следующая страница"), () => ChangePage(1), () => CanRequest && IsActive && (_page + 1) * HistoryPageSize < _historyCount))
            .Actions(id.Child("element/history-actions"), "HistoryActions", Text("Selected shipment actions", "Действия с отправлением"),
                HistoryAction(id, "reserve", Text("Dispatch selected", "Отправить выбранное"), FlowParcelAction.Reserve),
                HistoryAction(id, "cancel", Text("Cancel selected", "Отменить выбранное"), FlowParcelAction.Cancel),
                HistoryAction(id, "retry", Text("Retry selected delivery", "Повторить выбранную доставку"), FlowParcelAction.RetryDelivery),
                HistoryAction(id, "return", Text("Return selected cargo to source", "Вернуть выбранный груз в источник"), FlowParcelAction.ReturnToSource))
            .Input(history, new(history.Child("input/query"), "Query", UiSourceTypes.String.Descriptor, true))
            .Input(history, new(history.Child("input/filter"), "Filter", UiSourceTypes.Selection(UiSourceTypes.String).Descriptor, true))
            .Relation(new(id.Child("relation/history-selection"), UiRelationKind.Selection, history, selected))
            .Relation(new(id.Child("relation/history-details"), UiRelationKind.Details, selected, details))
            .Relation(new(id.Child("relation/history-query"), UiRelationKind.Query, id.Child("element/history-search"), history, history.Child("input/query")))
            .Relation(new(id.Child("relation/history-filter"), UiRelationKind.Filter, filter, history, history.Child("input/filter")))
            .Relation(new(id.Child("relation/history-filter-selection"), UiRelationKind.Selection, id.Child("element/history-filter"), filter));
    }

    private UiActionDefinition HistoryAction(UiSymbolId id, string key, string title, FlowParcelAction action)
        => Action(id, "history-" + key, title, () =>
        {
            FlowParcelSnapshot? parcel = Selected(_history);
            if (parcel is null) return;
            FlowCommandResult result = _application.Execute(new FlowParcelCommand(_snapshot.Transport.SessionId, _snapshot.Transport.Revision, parcel.Id, action));
            Complete(result, Text("Command completed", "Команда выполнена"));
        }, () => Selected(_history) is { } parcel && parcel.Availability[action].Available);

    private Projection ProjectHistory(UiPublicationBatch batch, Projection projection)
    {
        string query = batch.Read(_historyQuery.Source).Trim();
        if (query.Length > 128) query = query[..128];
        string? filter = Selected(batch, _historyFilter);
        // The host retains at most 256 parcels; the visible collection is a detached twelve-row page.
        FlowParcelSnapshot[] matches = projection.Snapshot.Transport.Parcels.Where(parcel =>
            (filter != "active" || parcel.State is not (ParcelState.Delivered or ParcelState.Cancelled or ParcelState.Returned))
            && (filter != "attention" || parcel.State is ParcelState.DeliveryRejected or ParcelState.DeliveryFaulted
                or ParcelState.ExtractionUncertain or ParcelState.DeliveryUncertain or ParcelState.ReturnRejected or ParcelState.ReturnFaulted or ParcelState.ReturnUncertain)
            && (query.Length == 0 || _historyItemName(parcel.ItemKey).Contains(query, StringComparison.OrdinalIgnoreCase)
                || StationName(parcel.Origin).Contains(query, StringComparison.OrdinalIgnoreCase)
                || StationName(parcel.Destination).Contains(query, StringComparison.OrdinalIgnoreCase)
                || parcel.Id.ToString("D").Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(parcel => parcel.Id).ToArray();
        int page = Math.Min(projection.Page, Math.Max(0, (matches.Length - 1) / HistoryPageSize));
        FlowParcelSnapshot[] visible = matches.Skip(page * HistoryPageSize).Take(HistoryPageSize).ToArray();
        batch.Replace(_history.Source, visible);
        if (Retired) return projection;
        IUiSemanticCollectionSnapshot snapshot = batch.Read(_history.Source);
        UiSymbolId? selected = null;
        for (int index = 0; index < visible.Length; index++)
            if (visible[index].Id == projection.HistorySelection) { selected = snapshot.GetItem(index).Id; break; }
        batch.Select(_history.Source, selected);
        FlowParcelSnapshot? parcel = Selected(batch, _history);
        batch.Set(_historyPage, $"{page + 1} / {Math.Max(1, (matches.Length + HistoryPageSize - 1) / HistoryPageSize)} · {matches.Length}")
            .Set(_historySelectedParcel, parcel)
            .Set(_historyAvailability, parcel is null ? "" : FlowReasonText.UnavailableActions(parcel.Availability, _russian))
            .Set(_historyDetails, parcel is null ? Text("Select a shipment", "Выберите отправление")
                : $"{parcel.Id} · {Describe(parcel.State)} · " + Text("attempts: ", "попыток: ") + parcel.DeliveryAttempts);
        return projection with { Page = page, HistoryCount = matches.Length };
    }
    private string Describe(ParcelState state) => state switch
    {
        ParcelState.Created => Text("Ready", "Готово"), ParcelState.Reserved => Text("Scheduled", "Запланировано"),
        ParcelState.InTransit => Text("In transit", "В пути"), ParcelState.Arrived => Text("Arrived", "Прибыло"),
        ParcelState.Delivered => Text("Delivered", "Доставлено"), ParcelState.Cancelled => Text("Cancelled", "Отменено"),
        ParcelState.ReturnRequested => Text("Return scheduled", "Возврат запланирован"), ParcelState.Returned => Text("Returned", "Возвращено"),
        ParcelState.DeliveryRejected => Text("Delivery rejected", "Доставка отклонена"), ParcelState.ReturnRejected => Text("Return rejected", "Возврат отклонён"),
        ParcelState.DeliveryFaulted or ParcelState.ReturnFaulted => Text("Needs attention", "Требует внимания"),
        _ => Text("Transfer uncertain", "Результат передачи неизвестен")
    };
}
