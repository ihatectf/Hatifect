using System.Globalization;
using Hatifect.Flow.Application;
using Hatifect.UI;
using Hatifect.UI.Experience;

namespace Hatifect.Flow.UI.Semantic;

internal sealed partial class NetworkExperience : IFlowExperience
{
    private readonly IFlowNetworkApplication _application;
    private readonly bool _russian;
    private readonly UiPublication _publication;
    private readonly UiPublishedState<Projection> _projection;
    private readonly UiPublishedState<string> _status, _target, _route, _result;
    private readonly FlowTextSource _name, _capacity, _ticks;
    private readonly UiPublishedState<string?> _nameError, _capacityError, _ticksError;
    private readonly UiFormState _stationForm, _linkForm;
    private readonly FlowSelectionSource<FlowStationDetails> _sources, _destinations;
    private readonly FlowSelectionSource<FlowLinkSnapshot> _links;
    private FlowNetworkSnapshot _snapshot => _projection.Value.Snapshot;
    private FlowNetworkSnapshot? _preparingSnapshot;
    private bool _dirty, _disposed, _subscribed, _pumping, _projecting, _requesting;
    private long _notificationEpoch;
    private string? _pendingResult;
    private bool _refreshInventoryPending;
    // One latest value per five declared text fields, never an event queue.
    private readonly Dictionary<UiPublishedState<string>, string> _pendingEdits = new();
    private int? _pendingPage;
    private sealed record Projection(FlowNetworkSnapshot Snapshot, int Page = 0, int HistoryCount = 0,
        Guid? HistorySelection = null, Guid? InventorySource = null);

    internal NetworkExperience(UiSymbolId id, IFlowNetworkApplication application, bool russian = false, Func<string, string>? itemName = null)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _russian = russian;
        _publication = new(id);
        try
        {
            // Subscribe before the first read; source callbacks may publish while projecting.
            _subscribed = true;
            application.RevisionChanged += OnRevision;
            _dirty = false;
            FlowNetworkSnapshot initial = application.ReadNetwork();
            _projection = _publication.State(id.Child("source/projection"), new Projection(initial),
                UiSourceTypes.Scalar<Projection>(new("Hatifect.Flow", "data/network-projection"), false));
            UiPublishedState<string> State(string key, string value = "") => _publication.State(id.Child("source/" + key), value, UiSourceTypes.String);
            UiPublishedState<string?> Error(string key) => _publication.State<string?>(id.Child("source/" + key), null,
                new(UiSourceTypes.String.Descriptor with { Nullable = true }));
            _status = State("status"); _target = State("target"); _route = State("route"); _result = State("result");
            _name = new(State("name"), RequestEdit); _capacity = new(State("capacity", "999"), RequestEdit); _ticks = new(State("ticks", "180"), RequestEdit);
            _nameError = Error("name-error"); _capacityError = Error("capacity-error"); _ticksError = Error("ticks-error");
            _quantity = new(State("quantity", "1"), RequestEdit); _quantityError = Error("quantity-error");
            _sources = Stations(id, "source");
            _destinations = Stations(id, "destination");
            InitializeHistory(id, itemName ?? (key => key));
            _recovery = Selection(id.Child("source/recovery"), Array.Empty<FlowRecoveryIssue>(), FlowUiDataTypes.Recovery, value => id.Child("recovery/" + value.ParcelId.ToString("N")),
                value => value.ItemKey + " × " + value.Quantity,
                supportingText: value => value.CanReconcile ? Text("Retained outcome available", "Сохранён результат передачи")
                    : Text("Outcome unknown; restore the complete game save from a known good backup", "Результат неизвестен; восстановите целый игровой сейв из исправной резервной копии"));
            _inventory = Selection(id.Child("source/inventory"), Array.Empty<FlowInventorySlot>(), FlowUiDataTypes.InventorySlot, value => id.Child("slot/" + value.Index),
                value => (itemName is null ? value.ItemKey : Format(itemName, value.ItemKey)) + " × " + value.Quantity,
                supportingText: value => Text("Slot ", "Слот ") + (value.Index + 1) + Text(" · quality ", " · качество ") + value.Detail);
            _links = Selection(id.Child("source/links"), Array.Empty<FlowLinkSnapshot>(), FlowUiDataTypes.Link, value => id.Child("link/" + value.Id.ToString("N")),
                value => StationName(value.Origin) + " → " + StationName(value.Destination),
                supportingText: value => $"{value.Capacity} · {value.TransitTicks} " + Text("ticks", "тиков"));
            _stationForm = new(new UiSemanticFormField(id.Child("field/name"), Text("Station name", "Имя станции"), _name, _nameError));
            _linkForm = new(new UiSemanticFormField(id.Child("field/capacity"), Text("Capacity", "Ёмкость"), _capacity, _capacityError),
                new UiSemanticFormField(id.Child("field/ticks"), Text("Travel ticks", "Время в тиках"), _ticks, _ticksError));
            _shipmentForm = new(new UiSemanticFormField(id.Child("field/quantity"), Text("Quantity", "Количество"), _quantity, _quantityError));
            var builder = new UiExperienceBuilder(id, Text("Flowline network", "Сеть Flowline")
                + (_snapshot.Transport.ProviderMode == FlowProviderMode.DiagnosticFake ? Text(" · diagnostic", " · диагностика") : ""))
                .Element(id.Child("element/transport"), "Transport", Text("Transport", "Перевозки"), _status, UiSourceTypes.String, UiCapabilities.Monitor)
                .Element(id.Child("element/captured-chest"), "CapturedChest", Text("Captured chest", "Выбранный сундук"), _target, UiSourceTypes.String, UiCapabilities.Inspect)
                .Element(id.Child("element/station-details"), "StationDetails", Text("Station details", "Параметры станции"), _stationForm, FlowUiDataTypes.StationForm, UiCapabilities.Configure)
                .Element(id.Child("element/source-station"), "SourceStation", Text("Source station", "Станция отправления"), _sources, UiSourceTypes.Collection(FlowUiDataTypes.Station), UiCapabilities.Select)
                .Actions(id.Child("element/station-actions"), "StationActions", Text("Station actions", "Действия со станцией"),
                    Action(id, "register", Text("Bind new station", "Создать станцию"), () => Command(FlowNetworkAction.RegisterStation), () => _stationForm.IsValid && _snapshot.Target != Guid.Empty
                        && !_snapshot.Stations.Any(station => string.Equals(station.Name, _name.Value, StringComparison.OrdinalIgnoreCase))),
                    Action(id, "rename", Text("Rename source", "Переименовать источник"), () => Command(FlowNetworkAction.RenameStation), () => _stationForm.IsValid && Source is not null),
                    Action(id, "rebind", Text("Rebind source to captured chest", "Привязать источник к выбранному сундуку"), () => Command(FlowNetworkAction.RebindStation), () => Source is not null && _snapshot.Target != Guid.Empty))
                .Element(id.Child("element/destination-station"), "DestinationStation", Text("Destination station", "Станция назначения"), _destinations, UiSourceTypes.Collection(FlowUiDataTypes.Station), UiCapabilities.Select)
                .Element(id.Child("element/route-preview"), "RoutePreview", Text("Route preview", "Предпросмотр маршрута"), _route, UiSourceTypes.String, UiCapabilities.Inspect)
                .Element(id.Child("element/link-settings"), "LinkSettings", Text("Link settings", "Параметры связи"), _linkForm, FlowUiDataTypes.LinkForm, UiCapabilities.Configure)
                .Actions(id.Child("element/route-actions"), "RouteActions", Text("Route actions", "Действия с маршрутом"),
                    Action(id, "link", Text("Add directed link", "Добавить направленную связь"), () => Command(FlowNetworkAction.AddLink), () => _linkForm.IsValid && Source is not null && Destination is not null && Source.Id != Destination.Id))
                .Element(id.Child("element/links"), "Links", Text("Links", "Связи"), _links, UiSourceTypes.Collection(FlowUiDataTypes.Link), UiCapabilities.Select)
                .Actions(id.Child("element/link-actions"), "LinkActions", Text("Link actions", "Действия со связью"),
                    Action(id, "remove", Text("Remove selected link", "Удалить выбранную связь"), () => Command(FlowNetworkAction.RemoveLink), () => Selected(_links) is not null),
                    new UiActionDefinition(id.Child("action/refresh"), Text("Refresh", "Обновить"), Refresh, () => CanRequest && IsActive));
            AppendShipping(builder, id);
            AppendHistory(builder, id);
            AppendRecovery(builder, id);
            Experience = builder.Element(id.Child("element/result"), "Result", Text("Result", "Результат"), _result, UiSourceTypes.String, UiCapabilities.Monitor).Build();
            Project(initial, _publication.BeginUpdate());
            if (_dirty) Pump();
        }
        catch
        {
            _disposed = true;
            try { Unsubscribe(); }
            finally
            {
                _stationForm?.Dispose(); _linkForm?.Dispose(); _shipmentForm?.Dispose();
                _publication.Dispose();
            }
            throw;
        }
    }

    public UiExperienceDefinition Experience { get; }
    internal UiPublication Publication => _publication;
    private bool Retired => _disposed || _publication.IsDisposed;
    public bool IsActive => !Retired && _snapshot.Transport.State is not (FlowApplicationState.Closed or FlowApplicationState.Faulted);
    private bool CanRequest => !Retired && !_publication.IsPublishing && !_pumping && !_projecting && !_requesting;
    private FlowStationDetails? Source => Selected(_sources);
    private FlowStationDetails? Destination => Selected(_destinations);

    public bool Pump()
    {
        if (!CanRequest || !_dirty) return false;
        _publication.Capture();
        _dirty = false;
        _pumping = true;
        try
        {
            FlowNetworkSnapshot snapshot = _application.ReadNetwork();
            return !Retired && Project(snapshot, _publication.BeginUpdate(), _refreshInventoryPending);
        }
        catch { _dirty = true; throw; }
        finally { _pumping = false; }
    }

    public void Dispose()
    {
        if (_disposed && !_subscribed) return;
        _publication.Capture();
        _disposed = true;
        try { Unsubscribe(); }
        finally
        {
            _pendingEdits.Clear(); _pendingPage = null; _pendingResult = null;
            _stationForm.Dispose(); _linkForm.Dispose(); _shipmentForm.Dispose();
            _publication.Dispose();
        }
    }

    private FlowSelectionSource<FlowStationDetails> Stations(UiSymbolId id, string role)
        => Selection(id.Child("source/" + role), Array.Empty<FlowStationDetails>(), FlowUiDataTypes.Station,
            value => id.Child(role + "/" + value.Id.ToString("N")), value => value.Name,
            supportingText: value => value.Location + (value.Available ? "" : Text(" · unavailable", " · недоступна")));
    private FlowSelectionSource<T> Selection<T>(UiSymbolId id, IReadOnlyList<T> values, UiSourceType<T> type,
        Func<T, UiSymbolId> identify, Func<T, string> label, UiSymbolId? selectedItemId = null, Func<T, string?>? supportingText = null)
    {
        var source = _publication.SelectableCollection(id, values, type, identify, label, selectedItemId, supportingText);
        return new(source, item => RequestSelection(source, item));
    }
    private static T? Selected<T>(FlowSelectionSource<T> source) where T : class
        => source.SelectedItemId is { } item && source.TryGetIndex(item, out int index) ? source.Value[index] : null;
    private static T? Selected<T>(UiPublicationBatch batch, FlowSelectionSource<T> source) where T : class
    {
        IUiSemanticCollectionSnapshot snapshot = batch.Read(source.Source);
        return snapshot.SelectedItemId is { } item && snapshot.TryGetIndex(item, out int index)
            ? (T?)snapshot.GetItem(index).Value : null;
    }
    private UiActionDefinition Action(UiSymbolId id, string key, string label, Action execute, Func<bool> applicable)
        => new(id.Child("action/" + key), label, () => RequestCommand(execute, applicable), () => Available(applicable));
    private bool Available(Func<bool> applicable, FlowApplicationState state = FlowApplicationState.Active)
    {
        if (!CanRequest) return false;
        _publication.Capture();
        _requesting = true;
        try { return CurrentAllows(state) && applicable(); }
        finally { _requesting = false; }
    }
    private bool CurrentAllows(FlowApplicationState state)
    {
        if (!IsActive || _snapshot.Transport.State != state) return false;
        FlowSnapshot expected = _snapshot.Transport;
        long epoch = _notificationEpoch;
        FlowSnapshot current = _application.ReadSnapshot();
        return !Retired && epoch == _notificationEpoch && current.State == state && current.SessionId == expected.SessionId && current.Revision == expected.Revision;
    }
    private void RequestCommand(Action execute, Func<bool> applicable, FlowApplicationState state = FlowApplicationState.Active)
    {
        if (!CanRequest) return;
        _publication.Capture();
        _requesting = true;
        try { if (CurrentAllows(state) && applicable()) execute(); }
        finally { _requesting = false; }
        if (!Retired) Pump();
    }
    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _application.RevisionChanged -= OnRevision;
        _subscribed = false;
    }
    private void OnRevision(long revision)
    {
        if (_disposed) return;
        _notificationEpoch++;
        _dirty = true;
    }
    private string Format(Func<string, string> formatter, string value)
    {
        if (Retired) throw new ObjectDisposedException(nameof(NetworkExperience));
        string result = formatter(value);
        if (Retired) throw new ObjectDisposedException(nameof(NetworkExperience));
        return result;
    }
    private static bool Number(string value, int maximum) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed > 0 && parsed <= maximum;
    private string StationName(Guid id) => (_preparingSnapshot ?? _snapshot).Stations.FirstOrDefault(station => station.Id == id)?.Name ?? "?";
    private void Command(FlowNetworkAction action)
    {
        FlowCommandResult result = _application.Execute(new FlowNetworkCommand(_snapshot.Transport.SessionId, _snapshot.Transport.Revision, action,
            Source?.Id ?? Guid.Empty, Destination?.Id ?? Guid.Empty,
            action == FlowNetworkAction.RemoveLink ? Selected(_links)?.Id ?? Guid.Empty : _snapshot.Target, _name.Value,
            Number(_capacity.Value, 999) ? int.Parse(_capacity.Value, CultureInfo.InvariantCulture) : 0,
            Number(_ticks.Value, 36000) ? int.Parse(_ticks.Value, CultureInfo.InvariantCulture) : 0));
        Complete(result, Text("Command completed", "Команда выполнена"));
    }
    private string Text(string english, string russian) => _russian ? russian : english;
}
