using Hatifect.Flow.Application;
using Hatifect.UI;
using Hatifect.UI.Experience;

namespace Hatifect.Flow.UI.Semantic;

internal sealed class ParcelExperience : IFlowExperience
{
    private readonly IFlowApplication _application;
    private readonly Guid? _parcelId;
    private readonly bool _russianFallback;
    private readonly Func<Guid, string>? _stationName;
    private readonly Func<string, string> _itemName;
    private readonly Func<string, UiLocalizedText>? _localizedItemName;
    private readonly UiPublication _publication;
    private readonly UiPublishedState<ParcelTextValue> _cargo, _route, _state, _availability, _result;
    private readonly UiPublishedState<ParcelPresentationSnapshot> _projection;
    private FlowCommandResult? _pendingResult;
    private bool _requesting;
    private long _notificationEpoch;
    private FlowSnapshot Snapshot => _projection.Value.Snapshot;
    private FlowParcelSnapshot? Parcel => _projection.Value.Parcel;
    private bool _dirty;
    private bool _disposed;
    private bool _subscribed;
    private bool _pumping;

    internal ParcelExperience(UiSymbolId id, IFlowApplication application, Guid? parcelId = null,
        bool russian = false, Func<Guid, string>? stationName = null, Func<string, string>? itemName = null,
        Func<string, UiLocalizedText>? localizedItemName = null)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        if (parcelId == Guid.Empty) throw new ArgumentException("A parcel identity is required.", nameof(parcelId));
        _parcelId = parcelId;
        if (itemName is not null && localizedItemName is not null)
            throw new ArgumentException("Supply either a captured item name or localized item names, not both.", nameof(localizedItemName));
        _russianFallback = russian;
        _stationName = stationName;
        _itemName = itemName ?? (key => key);
        _localizedItemName = localizedItemName;
        _publication = new(id);
        try
        {
            // Subscribe before the first read; source callbacks may publish while projecting.
            _subscribed = true;
            application.RevisionChanged += OnRevision;
            _dirty = false;
            FlowSnapshot snapshot = application.ReadSnapshot();
            var initial = new ParcelPresentationSnapshot(snapshot, null, HasSelection: parcelId.HasValue);
            var textType = UiSourceTypes.Scalar<ParcelTextValue>(new("Hatifect.Flow", "data/parcel-text"), false);
            UiPublishedState<ParcelTextValue> State(string key, ParcelTextPart part)
                => _publication.State(id.Child("source/" + key), new ParcelTextValue(initial, part, russian), textType);
            _cargo = State("cargo", ParcelTextPart.Cargo);
            _route = State("route", ParcelTextPart.Route);
            _state = State("state", ParcelTextPart.State);
            _availability = State("availability", ParcelTextPart.Availability);
            _result = State("result", ParcelTextPart.Result);
            _projection = _publication.State(id.Child("source/projection"), initial,
                UiSourceTypes.Scalar<ParcelPresentationSnapshot>(new("Hatifect.Flow", "data/parcel-projection"), false));
            Project(snapshot);
            bool diagnostic = Snapshot.ProviderMode == FlowProviderMode.DiagnosticFake;
            UiLocalizedText title = Localized("Flowline shipment" + (diagnostic ? " · diagnostic" : ""),
                "Отправление Flowline" + (diagnostic ? " · диагностика" : ""));
            var builder = new UiExperienceBuilder(id, title.Fallback)
                .LocalizeDisplayName(title)
                .Element(id.Child("element/cargo"), "Cargo", Text("Cargo", "Груз"), _cargo, textType, UiCapabilities.Inspect)
                .Element(id.Child("element/route"), "Route", Text("Route", "Маршрут"), _route, textType, UiCapabilities.Inspect)
                .Element(id.Child("element/state"), "State", Text("State", "Состояние"), _state, textType, UiCapabilities.Monitor)
                .Element(id.Child("element/result"), "Result", Text("Result", "Результат"), _result, textType, UiCapabilities.Monitor)
                .Element(id.Child("element/availability"), "Availability", Text("Availability", "Доступность"), _availability, textType, UiCapabilities.Monitor)
                .Actions(id.Child("element/actions"), "Actions", Text("Actions", "Действия"),
                    Action(id, "reserve", FlowParcelAction.Reserve, Text("Dispatch", "Отправить")),
                    Action(id, "cancel", FlowParcelAction.Cancel, Text("Cancel", "Отменить")),
                    Action(id, "retry", FlowParcelAction.RetryDelivery, Text("Retry delivery", "Повторить доставку")),
                    Action(id, "reconcile", FlowParcelAction.ReconcileTransfer, Text("Check transfer", "Проверить передачу")),
                    Action(id, "return", FlowParcelAction.ReturnToSource, Text("Return cargo to source", "Вернуть груз в источник")));
            Present("cargo", "Cargo", "Груз");
            Present("route", "Route", "Маршрут");
            Present("state", "State", "Состояние");
            Present("result", "Result", "Результат");
            Present("availability", "Availability", "Доступность");
            builder.LocalizeElement(id.Child("element/actions"), Localized("Actions", "Действия"))
                .LocalizeAction(id.Child("action/reserve"), Localized("Dispatch", "Отправить"))
                .LocalizeAction(id.Child("action/cancel"), Localized("Cancel", "Отменить"))
                .LocalizeAction(id.Child("action/retry"), Localized("Retry delivery", "Повторить доставку"))
                .LocalizeAction(id.Child("action/reconcile"), Localized("Check transfer", "Проверить передачу"))
                .LocalizeAction(id.Child("action/return"), Localized("Return cargo to source", "Вернуть груз в источник"));
            Experience = builder.Build();
            if (_dirty) Pump();

            void Present(string key, string english, string translated)
                => builder.LocalizeElement(id.Child("element/" + key), Localized(english, translated))
                    .FormatText<ParcelTextValue>(id.Child("element/" + key), static (value, locale) => value.Format(locale));
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
    // Empty or faulted data remains visible until the owning session/surface retires.
    public bool IsActive => !_disposed && !_publication.IsDisposed
        && Snapshot.State != FlowApplicationState.Closed;
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
        if (!IsActive || Snapshot.State != FlowApplicationState.Active || Parcel is not { } parcel
            || !parcel.Availability[action].Available) return false;
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
            if (_parcelId is not { } parcelId || !CurrentAllows(action)) return;
            FlowSnapshot snapshot = Snapshot;
            FlowCommandResult result = _application.Execute(new FlowParcelCommand(snapshot.SessionId, snapshot.Revision, parcelId, action));
            if (_disposed || _publication.IsDisposed) return;
            _pendingResult = result;
            // Preserve the existing coalesced contract: the next Pump publishes the result and model together.
            _dirty = true;
        }
        finally { _requesting = false; }
    }

    private bool Project(FlowSnapshot snapshot)
    {
        FlowParcelSnapshot? parcel = _parcelId is { } parcelId
            ? snapshot.Parcels.FirstOrDefault(value => value.Id == parcelId) : null;
        bool unavailable = snapshot.State is FlowApplicationState.Closed or FlowApplicationState.Faulted || parcel is null;
        UiLocalizedText? localizedItem = null;
        string itemName = string.Empty;
        if (!unavailable)
        {
            if (_localizedItemName is null) itemName = _itemName(parcel!.ItemKey);
            else
            {
                localizedItem = _localizedItemName(parcel!.ItemKey);
                if (_disposed || _publication.IsDisposed) return false;
                itemName = (localizedItem ?? throw new InvalidOperationException("Item name capture returned no localized text.")).Fallback;
            }
        }
        if (_disposed || _publication.IsDisposed) return false;
        string? origin = unavailable ? string.Empty : _stationName?.Invoke(parcel!.Origin);
        if (_disposed || _publication.IsDisposed) return false;
        string? destination = unavailable ? string.Empty : _stationName?.Invoke(parcel!.Destination);
        if (_disposed || _publication.IsDisposed) return false;
        var facts = new ParcelPresentationSnapshot(snapshot, parcel, itemName, origin, destination,
            _pendingResult ?? _projection.Value.Result, localizedItem, _parcelId.HasValue);
        UiPublicationResult result = _publication.BeginUpdate()
            .Set(_projection, facts)
            .Set(_cargo, Value(ParcelTextPart.Cargo)).Set(_route, Value(ParcelTextPart.Route))
            .Set(_state, Value(ParcelTextPart.State)).Set(_availability, Value(ParcelTextPart.Availability))
            .Set(_result, Value(ParcelTextPart.Result)).Commit();
        if (!result.Succeeded) throw new InvalidOperationException("Flow parcel publication failed: " + result.Status);
        _pendingResult = null;
        return true;
        ParcelTextValue Value(ParcelTextPart part) => new(facts, part, _russianFallback);
    }

    private string Text(string english, string russian) => _russianFallback ? russian : english;
    private UiLocalizedText Localized(string english, string russian)
        => new(Text(english, russian), new Dictionary<string, string>
        { ["en"] = english, ["en-US"] = english, ["ru"] = russian, ["ru-RU"] = russian });
}
