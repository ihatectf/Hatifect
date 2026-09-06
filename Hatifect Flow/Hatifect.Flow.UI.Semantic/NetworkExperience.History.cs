using Hatifect.Flow.Application;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.UI;
using Hatifect.UI.Experience;
using Hatifect.UI.Semantics;

namespace Hatifect.Flow.UI.Semantic;

internal sealed partial class NetworkExperience
{
    private const int HistoryPageSize = 12;
    private readonly UiState<string> _historyQuery = new("");
    private readonly UiState<string> _historyPage = new("");
    private readonly UiState<string> _historyAvailability = new("");
    private readonly UiState<string> _historyDetails = new("");
    private readonly UiState<FlowParcelSnapshot?> _historySelectedParcel = new(null);
    private UiSelectableCollectionState<string> _historyFilter = null!;
    private UiSelectableCollectionState<FlowParcelSnapshot> _history = null!;
    private Func<string, string> _historyItemName = null!;
    private Guid? _historySelection;
    private int _page;
    private int _historyCount;
    private bool _projectingHistory;

    private void InitializeHistory(UiSymbolId id, Func<string, string> itemName)
    {
        _historyItemName = itemName;
        _historyFilter = new(new[] { "all", "active", "attention" }, value => id.Child("history-filter/" + value),
            value => value == "all" ? Text("All shipments", "Все отправления") : value == "active" ? Text("Active shipments", "Активные отправления") : Text("Needs attention", "Требуют внимания"),
            selectedItemId: id.Child("history-filter/all"));
        _history = new(Array.Empty<FlowParcelSnapshot>(), value => id.Child("history/" + value.Id.ToString("N")),
            value => itemName(value.ItemKey) + " × " + value.Quantity,
            supportingText: value => StationName(value.Origin) + " → " + StationName(value.Destination) + " · " + Describe(value.State));
        _historyQuery.Changed += OnHistoryQuery;
        _historyFilter.Changed += OnHistoryQuery;
        _history.Changed += OnHistorySelection;
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
                new UiActionDefinition(id.Child("action/previous-page"), Text("Previous page", "Предыдущая страница"), () => { _page--; ProjectHistory(); }, () => IsActive && _page > 0),
                new UiActionDefinition(id.Child("action/next-page"), Text("Next page", "Следующая страница"), () => { _page++; ProjectHistory(); }, () => IsActive && (_page + 1) * HistoryPageSize < _historyCount))
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
            _result.Value = result.Status == FlowCommandStatus.Applied ? Text("Command completed", "Команда выполнена") : FlowReasonText.Describe(result.Code, result.ReasonKey, _russian);
            _dirty = true;
            Pump();
        }, () => Selected(_history) is { } parcel && parcel.Availability[action].Available);

    private void OnHistoryQuery()
    {
        if (_disposed || _projectingHistory) return;
        _page = 0;
        ProjectHistory();
    }
    private void OnHistorySelection()
    {
        if (_disposed || _projectingHistory) return;
        _historySelection = Selected(_history)?.Id;
        ProjectHistoryDetails();
    }
    private void ProjectHistory()
    {
        _projectingHistory = true;
        try
        {
            string query = _historyQuery.Value.Trim();
            if (query.Length > 128) query = query[..128];
            string? filter = Selected(_historyFilter);
            // The host retains at most 256 parcels; the visible collection is a detached twelve-row page.
            FlowParcelSnapshot[] matches = _snapshot.Transport.Parcels.Where(parcel =>
                (filter != "active" || parcel.State is not (ParcelState.Delivered or ParcelState.Cancelled or ParcelState.Returned))
                && (filter != "attention" || parcel.State is ParcelState.DeliveryRejected or ParcelState.DeliveryFaulted
                    or ParcelState.ExtractionUncertain or ParcelState.DeliveryUncertain or ParcelState.ReturnRejected or ParcelState.ReturnFaulted or ParcelState.ReturnUncertain)
                && (query.Length == 0 || _historyItemName(parcel.ItemKey).Contains(query, StringComparison.OrdinalIgnoreCase)
                    || StationName(parcel.Origin).Contains(query, StringComparison.OrdinalIgnoreCase)
                    || StationName(parcel.Destination).Contains(query, StringComparison.OrdinalIgnoreCase)
                    || parcel.Id.ToString("D").Contains(query, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(parcel => parcel.Id).ToArray();
            _historyCount = matches.Length;
            _page = Math.Min(_page, Math.Max(0, (matches.Length - 1) / HistoryPageSize));
            _history.Replace(matches.Skip(_page * HistoryPageSize).Take(HistoryPageSize).ToArray());
            for (int index = 0; index < _history.Count; index++)
                if (_history.Value[index].Id == _historySelection) _history.TrySelect(_history.GetItem(index).Id);
            _historyPage.Value = $"{_page + 1} / {Math.Max(1, (matches.Length + HistoryPageSize - 1) / HistoryPageSize)} · {matches.Length}";
            ProjectHistoryDetails();
        }
        finally { _projectingHistory = false; }
    }
    private void ProjectHistoryDetails()
    {
        FlowParcelSnapshot? parcel = Selected(_history);
        _historySelectedParcel.Value = parcel;
        _historyAvailability.Value = parcel is null ? "" : FlowReasonText.UnavailableActions(parcel.Availability, _russian);
        _historyDetails.Value = parcel is null ? Text("Select a shipment", "Выберите отправление")
            : $"{parcel.Id} · {Describe(parcel.State)} · " + Text("attempts: ", "попыток: ") + parcel.DeliveryAttempts;
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
