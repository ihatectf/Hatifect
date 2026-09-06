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
    private readonly UiPublication _publication;
    private readonly UiPublishedState<string> _cargo;
    private readonly UiPublishedState<string> _route;
    private readonly UiPublishedState<string> _state;
    private readonly UiPublishedState<string> _availability;
    private readonly UiPublishedState<string> _result;
    private readonly UiPublishedState<Projection> _projection;
    private string? _pendingResult;
    private bool _requesting;
    private long _notificationEpoch;
    private FlowSnapshot Snapshot => _projection.Value.Snapshot;
    private FlowParcelSnapshot? Parcel => _projection.Value.Parcel;
    private sealed record Projection(FlowSnapshot Snapshot, FlowParcelSnapshot? Parcel);
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
        _publication = new(id);
        try
        {
            // Subscribe before the first read; source callbacks may publish while projecting.
            _subscribed = true;
            application.RevisionChanged += OnRevision;
            _dirty = false;
            FlowSnapshot snapshot = application.ReadSnapshot();
            _cargo = _publication.State(id.Child("source/cargo"), string.Empty, UiSourceTypes.String);
            _route = _publication.State(id.Child("source/route"), string.Empty, UiSourceTypes.String);
            _state = _publication.State(id.Child("source/state"), string.Empty, UiSourceTypes.String);
            _availability = _publication.State(id.Child("source/availability"), string.Empty, UiSourceTypes.String);
            _result = _publication.State(id.Child("source/result"), string.Empty, UiSourceTypes.String);
            _projection = _publication.State(id.Child("source/projection"), new Projection(snapshot, null),
                UiSourceTypes.Scalar<Projection>(new("Hatifect.Flow", "data/parcel-projection"), false));
            Project(snapshot);
            Experience = new UiExperienceBuilder(id, Text("Flowline shipment", "Отправление Flowline")
                + (Snapshot.ProviderMode == FlowProviderMode.DiagnosticFake ? Text(" · diagnostic", " · диагностика") : ""))
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
            try { Unsubscribe(); }
            finally { _publication.Dispose(); }
            throw;
        }
    }

    public UiExperienceDefinition Experience { get; }
    internal UiPublication Publication => _publication;
    public bool IsActive => !_disposed && !_publication.IsDisposed
        && Snapshot.State is not (FlowApplicationState.Closed or FlowApplicationState.Faulted) && Parcel is not null;
    private bool CanRequest => !_disposed && !_publication.IsDisposed && !_publication.IsPublishing && !_pumping && !_requesting;

    // Coalesces domain notifications into one complete projection per pump.
    public bool Pump()
    {
        if (!CanRequest || !_dirty) return false;
        _publication.Capture();
        _dirty = false;
        _pumping = true;
        try
        {
            FlowSnapshot snapshot = _application.ReadSnapshot();
            return !_disposed && !_publication.IsDisposed && Project(snapshot);
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
        finally { _publication.Dispose(); }
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

    private UiActionDefinition Action(UiSymbolId id, string key, FlowParcelAction action, string title)
        => new(id.Child("action/" + key), title, () => Execute(action), () => Can(action));

    private bool Can(FlowParcelAction action)
    {
        if (!CanRequest) return false;
        _publication.Capture();
        _requesting = true;
        try { return CurrentAllows(action); }
        finally { _requesting = false; }
    }

    private bool CurrentAllows(FlowParcelAction action)
    {
        if (!IsActive || !Parcel!.Availability[action].Available) return false;
        FlowSnapshot expected = Snapshot;
        long epoch = _notificationEpoch;
        FlowSnapshot current = _application.ReadSnapshot();
        return !_disposed && !_publication.IsDisposed && epoch == _notificationEpoch && current.State == FlowApplicationState.Active
            && current.SessionId == expected.SessionId && current.Revision == expected.Revision;
    }

    private void Execute(FlowParcelAction action)
    {
        if (!CanRequest) return;
        _publication.Capture();
        _requesting = true;
        try
        {
            if (!CurrentAllows(action)) return;
            FlowSnapshot snapshot = Snapshot;
            FlowCommandResult result = _application.Execute(new FlowParcelCommand(snapshot.SessionId, snapshot.Revision, _parcelId, action));
            if (_disposed || _publication.IsDisposed) return;
            _pendingResult = result.Status == FlowCommandStatus.Applied ? Text("Command completed", "Команда выполнена")
                : FlowReasonText.Describe(result.Code, result.ReasonKey, _russian);
            // Preserve the existing coalesced contract: the next Pump publishes the result and model together.
            _dirty = true;
        }
        finally { _requesting = false; }
    }

    private bool Project(FlowSnapshot snapshot)
    {
        FlowParcelSnapshot? parcel = snapshot.Parcels.FirstOrDefault(value => value.Id == _parcelId);
        bool unavailable = snapshot.State is FlowApplicationState.Closed or FlowApplicationState.Faulted || parcel is null;
        string availability = unavailable ? FlowReasonText.Describe(snapshot.Code, snapshot.ReasonKey, _russian)
            : FlowReasonText.UnavailableActions(parcel!.Availability, _russian);
        string cargo = unavailable ? string.Empty : _itemName(parcel!.ItemKey) + " × " + parcel.Quantity;
        if (_disposed || _publication.IsDisposed) return false;
        string origin = unavailable ? string.Empty : _stationName(parcel!.Origin);
        if (_disposed || _publication.IsDisposed) return false;
        string destination = unavailable ? string.Empty : _stationName(parcel!.Destination);
        if (_disposed || _publication.IsDisposed) return false;
        string route = unavailable ? string.Empty : origin + " → " + destination;
        string state = unavailable ? Text("Session closed", "Сессия закрыта") : DescribeState(snapshot, parcel!);
        UiPublicationResult result = _publication.BeginUpdate()
            .Set(_projection, new Projection(snapshot, parcel))
            .Set(_cargo, cargo).Set(_route, route).Set(_state, state).Set(_availability, availability)
            .Set(_result, _pendingResult ?? _result.Value).Commit();
        if (!result.Succeeded) throw new InvalidOperationException("Flow parcel publication failed: " + result.Status);
        _pendingResult = null;
        return true;
    }

    private string DescribeState(FlowSnapshot snapshot, FlowParcelSnapshot parcel)
        => snapshot.State == FlowApplicationState.Paused ? Text("Transport paused", "Перевозки приостановлены")
            : snapshot.State == FlowApplicationState.RecoveryRequired ? Text("Recovery required; cargo retained", "Требуется восстановление; груз сохранён")
            : parcel.State switch
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

    private string Text(string english, string russian) => _russian ? russian : english;
}
