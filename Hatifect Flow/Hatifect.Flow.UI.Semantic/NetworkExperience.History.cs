using Hatifect.Flow.Application;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.UI;
using Hatifect.UI.Experience;

namespace Hatifect.Flow.UI.Semantic;

internal sealed partial class NetworkExperience
{
    private const int HistoryPageSize = 12;
    private readonly UiState<string> _historyQuery = new("");
    private readonly UiState<string> _historyPage = new("");
    private readonly UiState<string> _historyDetails = new("");
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
        builder.Element(id.Child("element/history-search"), Text("Find shipments", "Поиск отправлений"), _historyQuery, UiCapabilities.Search)
            .Select(id.Child("element/history-filter"), Text("Shipment filter", "Фильтр отправлений"), _historyFilter)
            .Select(id.Child("element/history"), Text("Shipments", "Отправления"), _history)
            .Monitor(id.Child("element/history-page"), Text("Page", "Страница"), _historyPage)
            .Inspect(id.Child("element/history-detail"), Text("Selected shipment", "Выбранное отправление"), _historyDetails)
            .Actions(id.Child("element/history-navigation"), Text("History navigation", "Навигация истории"),
                new UiActionDefinition(id.Child("action/previous-page"), Text("Previous page", "Предыдущая страница"), () => { _page--; ProjectHistory(); }, () => IsActive && _page > 0),
                new UiActionDefinition(id.Child("action/next-page"), Text("Next page", "Следующая страница"), () => { _page++; ProjectHistory(); }, () => IsActive && (_page + 1) * HistoryPageSize < _historyCount))
            .Actions(id.Child("element/history-actions"), Text("Selected shipment actions", "Действия с отправлением"),
                HistoryAction(id, "reserve", Text("Dispatch selected", "Отправить выбранное"), FlowParcelAction.Reserve, FlowParcelActions.Reserve),
                HistoryAction(id, "cancel", Text("Cancel selected", "Отменить выбранное"), FlowParcelAction.Cancel, FlowParcelActions.Cancel),
                HistoryAction(id, "retry", Text("Retry selected delivery", "Повторить выбранную доставку"), FlowParcelAction.RetryDelivery, FlowParcelActions.RetryDelivery),
                HistoryAction(id, "return", Text("Return selected cargo to source", "Вернуть выбранный груз в источник"), FlowParcelAction.ReturnToSource, FlowParcelActions.ReturnToSource));
    }

    private UiActionDefinition HistoryAction(UiSymbolId id, string key, string title, FlowParcelAction action, FlowParcelActions capability)
        => Action(id, "history-" + key, title, () =>
        {
            FlowParcelSnapshot? parcel = Selected(_history);
            if (parcel is null) return;
            FlowCommandResult result = _application.Execute(new FlowParcelCommand(_snapshot.Transport.SessionId, _snapshot.Transport.Revision, parcel.Id, action));
            _result.Value = result.Status == FlowCommandStatus.Applied ? Text("Command completed", "Команда выполнена") : Text("Shipment changed or action unavailable", "Отправление изменилось или действие недоступно");
            _dirty = true;
            Pump();
        }, () => Selected(_history) is { } parcel && (parcel.Actions & capability) != 0);

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
