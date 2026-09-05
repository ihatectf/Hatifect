using Hatifect.Flow.Application;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.UI;
using Hatifect.UI.Experience;

namespace Hatifect.Flow.UI.Semantic;

internal sealed class ParcelExperience : IFlowExperience
{
    private readonly IFlowApplication _application;
    private readonly Guid _parcelId;
    private readonly bool _russian;
    private readonly Func<Guid, string> _stationName;
    private readonly Func<string, string> _itemName;
    private readonly UiState<string> _cargo = new(string.Empty);
    private readonly UiState<string> _route = new(string.Empty);
    private readonly UiState<string> _state = new(string.Empty);
    private readonly UiState<string> _result = new(string.Empty);
    private FlowSnapshot _snapshot;
    private FlowParcelSnapshot? _parcel;
    private bool _dirty;
    private bool _disposed;

    internal ParcelExperience(UiSymbolId id, IFlowApplication application, Guid parcelId,
        bool russian = false, Func<Guid, string>? stationName = null, Func<string, string>? itemName = null)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        if (parcelId == Guid.Empty) throw new ArgumentException("A parcel identity is required.", nameof(parcelId));
        _parcelId = parcelId;
        _russian = russian;
        _stationName = stationName ?? (_ => Text("Station", "Станция"));
        _itemName = itemName ?? (key => key);
        _snapshot = application.ReadSnapshot();
        Project();
        Experience = new UiExperienceBuilder(id, Text("Flowline shipment", "Отправление Flowline"))
            .Inspect(Text("Cargo", "Груз"), _cargo)
            .Inspect(Text("Route", "Маршрут"), _route)
            .Monitor(Text("State", "Состояние"), _state)
            .Monitor(Text("Result", "Результат"), _result)
            .Actions(Text("Actions", "Действия"),
                Action(id, "reserve", FlowParcelAction.Reserve, FlowParcelActions.Reserve, Text("Dispatch", "Отправить")),
                Action(id, "cancel", FlowParcelAction.Cancel, FlowParcelActions.Cancel, Text("Cancel", "Отменить")),
                Action(id, "retry", FlowParcelAction.RetryDelivery, FlowParcelActions.RetryDelivery, Text("Retry delivery", "Повторить доставку")),
                Action(id, "reconcile", FlowParcelAction.ReconcileTransfer, FlowParcelActions.ReconcileTransfer, Text("Check transfer", "Проверить передачу")),
                Action(id, "return", FlowParcelAction.ReturnToSource, FlowParcelActions.ReturnToSource, Text("Return cargo to source", "Вернуть груз в источник")))
            .Build();
        application.RevisionChanged += OnRevision;
    }

    public UiExperienceDefinition Experience { get; }
    public bool IsActive => !_disposed && _snapshot.State is not (FlowApplicationState.Closed or FlowApplicationState.Faulted) && _parcel is not null;

    // Coalesces domain notifications into one complete projection per pump.
    public bool Pump()
    {
        if (_disposed || !_dirty) return false;
        _snapshot = _application.ReadSnapshot();
        Project();
        _dirty = false;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _application.RevisionChanged -= OnRevision;
    }

    private void OnRevision(long revision) => _dirty = true;

    private UiActionDefinition Action(UiSymbolId id, string key, FlowParcelAction action, FlowParcelActions capability, string title)
        => new(id.Child("action/" + key), title, () => Execute(action), () => Can(capability));

    private bool Can(FlowParcelActions action)
    {
        if (!IsActive || (_parcel!.Actions & action) == 0) return false;
        FlowSnapshot current = _application.ReadSnapshot();
        return current.State == FlowApplicationState.Active && current.SessionId == _snapshot.SessionId
            && current.Revision == _snapshot.Revision;
    }

    private void Execute(FlowParcelAction action)
    {
        FlowCommandResult result = _application.Execute(new FlowParcelCommand(_snapshot.SessionId, _snapshot.Revision, _parcelId, action));
        _result.Value = result.Status switch
        {
            FlowCommandStatus.Applied => Text("Command completed", "Команда выполнена"),
            FlowCommandStatus.Rejected => Text("Cannot perform this action now", "Сейчас это действие недоступно"),
            FlowCommandStatus.Conflict => Text("State changed; review the updated shipment", "Состояние изменилось; проверьте отправление"),
            FlowCommandStatus.SessionClosed => Text("Session closed", "Сессия закрыта"),
            _ => Text("Action failed; see the transport log", "Ошибка действия; см. журнал транспорта")
        };
        _dirty = true;
    }

    private void Project()
    {
        _parcel = _snapshot.Parcels.FirstOrDefault(parcel => parcel.Id == _parcelId);
        if (_snapshot.State is FlowApplicationState.Closed or FlowApplicationState.Faulted || _parcel is null)
        {
            _cargo.Value = string.Empty;
            _route.Value = string.Empty;
            _state.Value = Text("Session closed", "Сессия закрыта");
            return;
        }
        _cargo.Value = _itemName(_parcel.ItemKey) + " × " + _parcel.Quantity;
        _route.Value = _stationName(_parcel.Origin) + " → " + _stationName(_parcel.Destination);
        _state.Value = _snapshot.State == FlowApplicationState.Paused ? Text("Transport paused", "Перевозки приостановлены")
            : _snapshot.State == FlowApplicationState.RecoveryRequired ? Text("Recovery required; cargo retained", "Требуется восстановление; груз сохранён")
            : _parcel.State switch
        {
            ParcelState.Created => Text("Ready to dispatch", "Готово к отправке"),
            ParcelState.Reserved => Text("Scheduled", "Запланировано"),
            ParcelState.InTransit => Text("In transit", "В пути"),
            ParcelState.Arrived => Text("Arrived", "Прибыло"),
            ParcelState.Delivered => Text("Delivered", "Доставлено"),
            ParcelState.ReturnRequested => Text("Return to source scheduled", "Запланирован возврат в источник"),
            ParcelState.Returned => Text("Returned to source", "Возвращено в источник"),
            ParcelState.ReturnRejected or ParcelState.ReturnFaulted => Text("Source could not accept the return", "Источник не смог принять возврат"),
            ParcelState.Cancelled => Text("Cancelled", "Отменено"),
            ParcelState.DeliveryRejected => Text("Destination could not accept the cargo", "Получатель не смог принять груз"),
            ParcelState.ExtractionUncertain or ParcelState.DeliveryUncertain or ParcelState.ReturnUncertain => Text("Transfer needs verification", "Нужно проверить передачу"),
            _ => Text("Delivery needs attention", "Доставка требует внимания")
        };
    }

    private string Text(string english, string russian) => _russian ? russian : english;
}
