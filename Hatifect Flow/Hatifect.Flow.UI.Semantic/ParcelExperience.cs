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
    private readonly UiState<string> _availability = new(string.Empty);
    private readonly UiState<string> _result = new(string.Empty);
    private FlowSnapshot _snapshot;
    private FlowParcelSnapshot? _parcel;
    private bool _dirty;
    private bool _disposed;
    private bool _subscribed;
    private bool _pumping;

    internal ParcelExperience(UiSymbolId id, IFlowApplication application, Guid parcelId,
        bool russian = false, Func<Guid, string>? stationName = null, Func<string, string>? itemName = null)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        if (parcelId == Guid.Empty) throw new ArgumentException("A parcel identity is required.", nameof(parcelId));
        _parcelId = parcelId;
        _russian = russian;
        _stationName = stationName ?? (_ => Text("Station", "Станция"));
        _itemName = itemName ?? (key => key);
        try
        {
            // Subscribe before the first read; source callbacks may publish while projecting.
            _subscribed = true;
            application.RevisionChanged += OnRevision;
            _dirty = false;
            _snapshot = application.ReadSnapshot();
            Project();
            Experience = new UiExperienceBuilder(id, Text("Flowline shipment", "Отправление Flowline")
                + (_snapshot.ProviderMode == FlowProviderMode.DiagnosticFake ? Text(" · diagnostic", " · диагностика") : ""))
                .Element(id.Child("element/cargo"), "Cargo", Text("Cargo", "Груз"), _cargo, UiSourceTypes.String, UiCapabilities.Inspect)
                .Element(id.Child("element/route"), "Route", Text("Route", "Маршрут"), _route, UiSourceTypes.String, UiCapabilities.Inspect)
                .Element(id.Child("element/state"), "State", Text("State", "Состояние"), _state, UiSourceTypes.String, UiCapabilities.Monitor)
                .Element(id.Child("element/result"), "Result", Text("Result", "Результат"), _result, UiSourceTypes.String, UiCapabilities.Monitor)
                .Element(id.Child("element/availability"), "Availability", Text("Availability", "Доступность"), _availability, UiSourceTypes.String, UiCapabilities.Monitor)
                .Actions(id.Child("element/actions"), "Actions", Text("Actions", "Действия"),
                    Action(id, "reserve", FlowParcelAction.Reserve, Text("Dispatch", "Отправить")),
                    Action(id, "cancel", FlowParcelAction.Cancel, Text("Cancel", "Отменить")),
                    Action(id, "retry", FlowParcelAction.RetryDelivery, Text("Retry delivery", "Повторить доставку")),
                    Action(id, "reconcile", FlowParcelAction.ReconcileTransfer, Text("Check transfer", "Проверить передачу")),
                    Action(id, "return", FlowParcelAction.ReturnToSource, Text("Return cargo to source", "Вернуть груз в источник")))
                .Build();
            if (_dirty) Pump();
        }
        catch
        {
            _disposed = true;
            Unsubscribe();
            throw;
        }
    }

    public UiExperienceDefinition Experience { get; }
    public bool IsActive => !_disposed && _snapshot.State is not (FlowApplicationState.Closed or FlowApplicationState.Faulted) && _parcel is not null;

    // Coalesces domain notifications into one complete projection per pump.
    public bool Pump()
    {
        if (_disposed || !_dirty || _pumping) return false;
        _dirty = false;
        _pumping = true;
        try
        {
            _snapshot = _application.ReadSnapshot();
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
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _application.RevisionChanged -= OnRevision;
        _subscribed = false;
    }
    private void OnRevision(long revision) { if (!_disposed) _dirty = true; }

    private UiActionDefinition Action(UiSymbolId id, string key, FlowParcelAction action, string title)
        => new(id.Child("action/" + key), title, () => Execute(action), () => Can(action));

    private bool Can(FlowParcelAction action)
    {
        if (!IsActive || !_parcel!.Availability[action].Available) return false;
        FlowSnapshot current = _application.ReadSnapshot();
        return current.State == FlowApplicationState.Active && current.SessionId == _snapshot.SessionId
            && current.Revision == _snapshot.Revision;
    }

    private void Execute(FlowParcelAction action)
    {
        FlowCommandResult result = _application.Execute(new FlowParcelCommand(_snapshot.SessionId, _snapshot.Revision, _parcelId, action));
        _result.Value = result.Status == FlowCommandStatus.Applied ? Text("Command completed", "Команда выполнена")
            : FlowReasonText.Describe(result.Code, result.ReasonKey, _russian);
        _dirty = true;
    }

    private void Project()
    {
        _parcel = _snapshot.Parcels.FirstOrDefault(parcel => parcel.Id == _parcelId);
        if (_snapshot.State is FlowApplicationState.Closed or FlowApplicationState.Faulted || _parcel is null)
        {
            _availability.Value = FlowReasonText.Describe(_snapshot.Code, _snapshot.ReasonKey, _russian);
            _cargo.Value = string.Empty;
            _route.Value = string.Empty;
            _state.Value = Text("Session closed", "Сессия закрыта");
            return;
        }
        _availability.Value = FlowReasonText.UnavailableActions(_parcel.Availability, _russian);
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
