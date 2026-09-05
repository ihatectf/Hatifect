using System.Globalization;
using Hatifect.Flow.Application;
using Hatifect.UI;
using Hatifect.UI.Experience;

namespace Hatifect.Flow.UI.Semantic;

internal sealed partial class NetworkExperience : IFlowExperience
{
    private readonly IFlowNetworkApplication _application;
    private readonly bool _russian;
    private readonly UiState<string> _status = new("");
    private readonly UiState<string> _target = new("");
    private readonly UiState<string> _route = new("");
    private readonly UiState<string> _result = new("");
    private readonly UiState<string> _name = new("");
    private readonly UiState<string> _capacity = new("999");
    private readonly UiState<string> _ticks = new("180");
    private readonly UiState<string?> _nameError = new(null);
    private readonly UiState<string?> _capacityError = new(null);
    private readonly UiState<string?> _ticksError = new(null);
    private readonly UiFormState _stationForm;
    private readonly UiFormState _linkForm;
    private readonly UiSelectableCollectionState<FlowStationDetails> _sources;
    private readonly UiSelectableCollectionState<FlowStationDetails> _destinations;
    private readonly UiSelectableCollectionState<FlowLinkSnapshot> _links;
    private FlowNetworkSnapshot _snapshot;
    private bool _dirty;
    private bool _projecting;
    private bool _disposed;
    private bool _subscribed;
    private bool _pumping;

    internal NetworkExperience(UiSymbolId id, IFlowNetworkApplication application, bool russian = false, Func<string, string>? itemName = null)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _russian = russian;
        try
        {
            // Subscribe before the first read; source callbacks may publish while projecting.
            _subscribed = true;
            application.RevisionChanged += OnRevision;
            _dirty = false;
            _snapshot = application.ReadNetwork();
            _sources = Stations(id, "source");
            _destinations = Stations(id, "destination");
            InitializeHistory(id, itemName ?? (key => key));
            _recovery = new(Array.Empty<FlowRecoveryIssue>(), value => id.Child("recovery/" + value.ParcelId.ToString("N")),
                value => value.ItemKey + " × " + value.Quantity,
                supportingText: value => value.CanReconcile ? Text("Retained outcome available", "Сохранён результат передачи")
                    : Text("Outcome unknown; restore the complete game save from a known good backup", "Результат неизвестен; восстановите целый игровой сейв из исправной резервной копии"));
            _inventory = new(Array.Empty<FlowInventorySlot>(), value => id.Child("slot/" + value.Index),
                value => (itemName?.Invoke(value.ItemKey) ?? value.ItemKey) + " × " + value.Quantity,
                supportingText: value => Text("Slot ", "Слот ") + (value.Index + 1) + Text(" · quality ", " · качество ") + value.Detail);
            _links = new(Array.Empty<FlowLinkSnapshot>(), value => id.Child("link/" + value.Id.ToString("N")),
                value => StationName(value.Origin) + " → " + StationName(value.Destination),
                supportingText: value => $"{value.Capacity} · {value.TransitTicks} " + Text("ticks", "тиков"));
            _stationForm = new(new UiSemanticFormField(id.Child("field/name"), Text("Station name", "Имя станции"), _name, _nameError));
            _linkForm = new(new UiSemanticFormField(id.Child("field/capacity"), Text("Capacity", "Ёмкость"), _capacity, _capacityError),
                new UiSemanticFormField(id.Child("field/ticks"), Text("Travel ticks", "Время в тиках"), _ticks, _ticksError));
            var builder = new UiExperienceBuilder(id, Text("Flowline network", "Сеть Flowline"))
                .Monitor(id.Child("element/transport"), Text("Transport", "Перевозки"), _status)
                .Inspect(id.Child("element/captured-chest"), Text("Captured chest", "Выбранный сундук"), _target)
                .Configure(id.Child("element/station-details"), Text("Station details", "Параметры станции"), _stationForm)
                .Select(id.Child("element/source-station"), Text("Source station", "Станция отправления"), _sources)
                .Actions(id.Child("element/station-actions"), Text("Station actions", "Действия со станцией"),
                    Action(id, "register", Text("Bind new station", "Создать станцию"), () => Command(FlowNetworkAction.RegisterStation), () => _stationForm.IsValid && _snapshot.Target != Guid.Empty
                        && !_snapshot.Stations.Any(station => string.Equals(station.Name, _name.Value, StringComparison.OrdinalIgnoreCase))),
                    Action(id, "rename", Text("Rename source", "Переименовать источник"), () => Command(FlowNetworkAction.RenameStation), () => _stationForm.IsValid && Source is not null),
                    Action(id, "rebind", Text("Rebind source to captured chest", "Привязать источник к выбранному сундуку"), () => Command(FlowNetworkAction.RebindStation), () => Source is not null && _snapshot.Target != Guid.Empty))
                .Select(id.Child("element/destination-station"), Text("Destination station", "Станция назначения"), _destinations)
                .Inspect(id.Child("element/route-preview"), Text("Route preview", "Предпросмотр маршрута"), _route)
                .Configure(id.Child("element/link-settings"), Text("Link settings", "Параметры связи"), _linkForm)
                .Actions(id.Child("element/route-actions"), Text("Route actions", "Действия с маршрутом"),
                    Action(id, "link", Text("Add directed link", "Добавить направленную связь"), () => Command(FlowNetworkAction.AddLink), () => _linkForm.IsValid && Source is not null && Destination is not null && Source.Id != Destination.Id))
                .Select(id.Child("element/links"), Text("Links", "Связи"), _links)
                .Actions(id.Child("element/link-actions"), Text("Link actions", "Действия со связью"),
                    Action(id, "remove", Text("Remove selected link", "Удалить выбранную связь"), () => Command(FlowNetworkAction.RemoveLink), () => Selected(_links) is not null),
                    new UiActionDefinition(id.Child("action/refresh"), Text("Refresh", "Обновить"), () => { _dirty = true; Pump(); RefreshInventory(); }, () => IsActive));
            AppendShipping(builder, id);
            AppendHistory(builder, id);
            AppendRecovery(builder, id);
            Experience = builder.Monitor(id.Child("element/result"), Text("Result", "Результат"), _result).Build();
            _sources.Changed += OnSelection;
            _destinations.Changed += OnSelection;
            _name.Changed += Validate;
            _capacity.Changed += Validate;
            _ticks.Changed += Validate;
            Project();
            if (_dirty) Pump();
        }
        catch
        {
            _disposed = true;
            Unsubscribe();
            _stationForm?.Dispose();
            _linkForm?.Dispose();
            throw;
        }
    }

    public UiExperienceDefinition Experience { get; }
    public bool IsActive => !_disposed && _snapshot.Transport.State is not (FlowApplicationState.Closed or FlowApplicationState.Faulted);
    private FlowStationDetails? Source => Selected(_sources);
    private FlowStationDetails? Destination => Selected(_destinations);
    private bool CanMutate => IsActive && _snapshot.Transport.State == FlowApplicationState.Active
        && _application.ReadSnapshot().SessionId == _snapshot.Transport.SessionId && _application.ReadSnapshot().Revision == _snapshot.Transport.Revision;

    public bool Pump()
    {
        if (_disposed || !_dirty || _pumping) return false;
        _dirty = false;
        _pumping = true;
        try
        {
            _snapshot = _application.ReadNetwork();
            Project();
            return true;
        }
        catch { _dirty = true; throw; }
        finally { _pumping = false; }
    }

    public void Dispose()
    {
        if (_disposed && !_subscribed) return;
        _disposed = true;
        Unsubscribe();
        _sources.Changed -= OnSelection;
        _destinations.Changed -= OnSelection;
        _name.Changed -= Validate;
        _capacity.Changed -= Validate;
        _ticks.Changed -= Validate;
        _stationForm.Dispose();
        _linkForm.Dispose();
        _historyQuery.Changed -= OnHistoryQuery;
        _historyFilter.Changed -= OnHistoryQuery;
        _history.Changed -= OnHistorySelection;
    }

    private void Project()
    {
        _projecting = true;
        try
        {
            _sources.Replace(_snapshot.Stations);
            _destinations.Replace(_snapshot.Stations);
            _links.Replace(_snapshot.Transport.Links);
            _recovery.Replace(_application.ReadRecovery());
            ProjectHistory();
            _target.Value = _snapshot.TargetDescription.Length > 0 ? _snapshot.TargetDescription : Text("Use hatifect_flow target while pointing at a chest", "Укажите на сундук и выполните hatifect_flow target");
            _status.Value = _snapshot.Transport.State switch
            {
                FlowApplicationState.Active => Text("Ready", "Готово"),
                FlowApplicationState.Paused => Text("Paused", "Приостановлено"),
                FlowApplicationState.RecoveryRequired => Text("Recovery required; cargo retained", "Требуется восстановление; груз сохранён"),
                _ => Text("Session closed", "Сессия закрыта")
            };
            Validate();
            UpdateRoute();
        }
        finally { _projecting = false; }
    }

    private UiSelectableCollectionState<FlowStationDetails> Stations(UiSymbolId id, string role)
        => new(Array.Empty<FlowStationDetails>(), value => id.Child(role + "/" + value.Id.ToString("N")), value => value.Name,
            supportingText: value => value.Location + (value.Available ? "" : Text(" · unavailable", " · недоступна")));
    private static T? Selected<T>(UiSelectableCollectionState<T> source) where T : class
    {
        for (int index = 0; index < source.Count; index++)
            if (source.GetItem(index).Id == source.SelectedItemId) return source.Value[index];
        return null;
    }
    private UiActionDefinition Action(UiSymbolId id, string key, string label, Action execute, Func<bool> applicable)
        => new(id.Child("action/" + key), label, execute, () => CanMutate && applicable());
    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _application.RevisionChanged -= OnRevision;
        _subscribed = false;
    }
    private void OnRevision(long revision) { if (!_disposed) _dirty = true; }
    private void OnSelection()
    {
        if (_projecting) return;
        Validate();
        UpdateRoute();
        if (_inventorySource != Source?.Id) RefreshInventory();
    }
    private void UpdateRoute()
    {
        FlowRoutePreview preview = _application.PreviewRoute(Source?.Id ?? Guid.Empty, Destination?.Id ?? Guid.Empty);
        _route.Value = preview.Found ? $"{preview.LinkCount} " + Text("links", "связей") + $" · {preview.TransitTicks} " + Text("ticks", "тиков")
            + $" · {preview.AvailableUnits} " + Text("units available", "ед. свободно") : Text("No route selected", "Маршрут не выбран или недоступен");
    }
    private void Validate()
    {
        _nameError.Value = _name.Value.Length is < 1 or > 32 || !_name.Value.All(character => char.IsLetterOrDigit(character) || character is '_' or '-')
            ? Text("Use 1–32 letters, digits, _ or -", "Допустимы 1–32 буквы, цифры, _ и -")
            : _snapshot.Stations.Any(station => station.Id != Source?.Id && string.Equals(station.Name, _name.Value, StringComparison.OrdinalIgnoreCase))
                ? Text("Name already exists", "Имя уже занято") : null;
        _capacityError.Value = Number(_capacity.Value, 999) ? null : Text("Enter 1–999", "Введите 1–999");
        _ticksError.Value = Number(_ticks.Value, 36000) ? null : Text("Enter 1–36000", "Введите 1–36000");
    }
    private static bool Number(string value, int maximum) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed > 0 && parsed <= maximum;
    private string StationName(Guid id) => _snapshot.Stations.FirstOrDefault(station => station.Id == id)?.Name ?? "?";
    private void Command(FlowNetworkAction action)
    {
        FlowCommandResult result = _application.Execute(new FlowNetworkCommand(_snapshot.Transport.SessionId, _snapshot.Transport.Revision, action,
            Source?.Id ?? Guid.Empty, Destination?.Id ?? Guid.Empty,
            action == FlowNetworkAction.RemoveLink ? Selected(_links)?.Id ?? Guid.Empty : _snapshot.Target, _name.Value,
            Number(_capacity.Value, 999) ? int.Parse(_capacity.Value, CultureInfo.InvariantCulture) : 0,
            Number(_ticks.Value, 36000) ? int.Parse(_ticks.Value, CultureInfo.InvariantCulture) : 0));
        _result.Value = result.Status == FlowCommandStatus.Applied ? Text("Command completed", "Команда выполнена")
            : result.Status == FlowCommandStatus.Conflict ? Text("State changed; review the updated network", "Состояние изменилось; проверьте сеть")
            : Text("Action unavailable; check selections and cargo state", "Действие недоступно; проверьте выбор и состояние груза");
        _dirty = true;
        Pump();
    }
    private string Text(string english, string russian) => _russian ? russian : english;
}
