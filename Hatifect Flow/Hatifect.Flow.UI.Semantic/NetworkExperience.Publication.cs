using Hatifect.Flow.Application;
using Hatifect.UI;
using Hatifect.UI.Experience;

namespace Hatifect.Flow.UI.Semantic;

internal sealed partial class NetworkExperience
{
    private bool Project(FlowNetworkSnapshot snapshot, UiPublicationBatch batch, bool refreshInventory = false, Projection? settings = null, bool resetPage = false)
    {
        if (Retired) return false;
        refreshInventory |= _refreshInventoryPending;
        _projecting = true;
        _preparingSnapshot = snapshot;
        long epoch = _notificationEpoch;
        try
        {
            Projection projection = (settings ?? _projection.Value) with { Snapshot = snapshot };
            if (resetPage) projection = projection with { Page = 0 };
            else if (_pendingPage is { } page) projection = projection with { Page = page };
            foreach (var edit in _pendingEdits) batch.Set(edit.Key, edit.Value);
            batch.Replace(_sources.Source, snapshot.Stations).Replace(_destinations.Source, snapshot.Stations)
                .Replace(_links.Source, snapshot.Transport.Links);
            IReadOnlyList<FlowRecoveryIssue> recovery = _application.ReadRecovery();
            if (Retired) return false;
            batch.Replace(_recovery.Source, recovery);
            FlowStationDetails? source = Selected(batch, _sources);
            FlowStationDetails? destination = Selected(batch, _destinations);
            if (refreshInventory)
            {
                FlowInventorySlot? selectedInventory = Selected(batch, _inventory);
                IReadOnlyList<FlowInventorySlot> inventory = source is null ? Array.Empty<FlowInventorySlot>() : _application.ReadInventory(source.Id);
                if (Retired) return false;
                batch.Replace(_inventory.Source, inventory);
                if (selectedInventory is not null && !inventory.Any(slot => slot.Index == selectedInventory.Index
                    && string.Equals(slot.Fingerprint, selectedInventory.Fingerprint, StringComparison.Ordinal)))
                    batch.Select(_inventory.Source, null);
                if (Retired) return false;
                projection = projection with { InventorySource = source?.Id };
            }
            else if (source?.Id != projection.InventorySource)
            {
                // An ordinary domain notification never serializes physical inventories.
                batch.Replace(_inventory.Source, Array.Empty<FlowInventorySlot>());
                projection = projection with { InventorySource = null };
            }
            FlowRoutePreview route = _application.PreviewRoute(source?.Id ?? Guid.Empty, destination?.Id ?? Guid.Empty);
            if (Retired) return false;
            projection = ProjectHistory(batch, projection);
            if (Retired) return false;
            string name = batch.Read(_name.Source);
            string capacity = batch.Read(_capacity.Source);
            string ticks = batch.Read(_ticks.Source);
            batch.Set(_nameError, name.Length is < 1 or > 32 || !name.All(character => char.IsLetterOrDigit(character) || character is '_' or '-')
                    ? Text("Use 1–32 letters, digits, _ or -", "Допустимы 1–32 буквы, цифры, _ и -")
                    : snapshot.Stations.Any(station => station.Id != source?.Id && string.Equals(station.Name, name, StringComparison.OrdinalIgnoreCase))
                        ? Text("Name already exists", "Имя уже занято") : null)
                .Set(_capacityError, Number(capacity, 999) ? null : Text("Enter 1–999", "Введите 1–999"))
                .Set(_ticksError, Number(ticks, 36000) ? null : Text("Enter 1–36000", "Введите 1–36000"));
            ValidateQuantity(batch);
            batch.Set(_target, snapshot.TargetDescription.Length > 0 ? snapshot.TargetDescription
                    : Text("Close this window, point at a chest, then open Flowline again", "Закройте окно, укажите на сундук и снова откройте Flowline"))
                .Set(_status, TransportStatus(snapshot.Transport.State, recovery.Count))
                .Set(_route, route.Found ? $"{route.LinkCount} " + Text("links", "связей") + $" · {route.TransitTicks} " + Text("ticks", "тиков")
                    + $" · {route.AvailableUnits} " + Text("units available", "ед. свободно") : Text("No route selected", "Маршрут не выбран или недоступен"))
                .Set(_result, _pendingResult ?? _result.Value).Set(_projection, projection);
            // Supplementary owner reads have no independent revision envelope. Fence their complete candidate.
            FlowSnapshot current = _application.ReadSnapshot();
            if (Retired) return false;
            if (epoch != _notificationEpoch || current.SessionId != snapshot.Transport.SessionId || current.Revision != snapshot.Transport.Revision
                || current.State != snapshot.Transport.State)
            { _dirty = true; return false; }
            _preparingSnapshot = null;
            UiPublicationResult result = batch.Commit();
            if (!result.Succeeded) throw new InvalidOperationException("Flow network publication failed: " + result.Status
                + "; " + string.Join("; ", result.Diagnostics.Select(error => error.Message)));
            _pendingResult = null;
            _refreshInventoryPending = false;
            _pendingEdits.Clear();
            _pendingPage = null;
            return true;
        }
        catch (ObjectDisposedException) when (Retired) { return false; }
        finally { _preparingSnapshot = null; _projecting = false; }
    }

    private bool RequestSelection<T>(UiPublishedSelectableCollection<T> source, UiSymbolId item)
    {
        if (!CanRequest || !IsActive) return false;
        _publication.Capture();
        if (!source.TryGetIndex(item, out int index)) return false;
        if (source.SelectedItemId == item) return true;
        _requesting = true;
        try
        {
            Projection settings = _projection.Value;
            if (ReferenceEquals(source, _historyFilter.Source)) settings = settings with { Page = 0 };
            if (ReferenceEquals(source, _history.Source)) settings = settings with { HistorySelection = ((FlowParcelSnapshot)(object)source.Value[index]!).Id };
            bool inventory = ReferenceEquals(source, _sources.Source) && settings.InventorySource != ((FlowStationDetails)(object)source.Value[index]!).Id;
            return Project(_snapshot, _publication.BeginUpdate().Select(source, item), inventory, settings,
                resetPage: ReferenceEquals(source, _historyFilter.Source));
        }
        finally { _requesting = false; }
    }

    private void RequestEdit(UiPublishedState<string> source, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!CanRequest || !IsActive) return;
        _publication.Capture();
        if ((_pendingEdits.TryGetValue(source, out string? pending) ? pending : source.Value) == value) return;
        _pendingEdits[source] = value;
        if (ReferenceEquals(source, _historyQuery.Source)) _pendingPage = 0;
        _dirty = true;
        // Overlay local intent on a fresh domain model; failed preparation retains the latest draft for retry.
        Pump();
    }

    private void ChangePage(int delta)
    {
        if (!CanRequest || !IsActive) return;
        _publication.Capture();
        _pendingPage = Math.Clamp((_pendingPage ?? _page) + delta, 0, Math.Max(0, (_historyCount - 1) / HistoryPageSize));
        _dirty = true;
        Pump();
    }

    private void Refresh()
    {
        if (!CanRequest || !IsActive) return;
        _publication.Capture();
        _refreshInventoryPending = true;
        _dirty = true;
        Pump();
    }

    private void Complete(FlowCommandResult result, string success, bool refreshInventory = false)
    {
        if (Retired) return;
        _pendingResult = result.Status == FlowCommandStatus.Applied ? success : FlowReasonText.Describe(result.Code, result.ReasonKey, _russian);
        _refreshInventoryPending |= refreshInventory;
        _dirty = true;
    }

    private UiStatus TransportStatus(FlowApplicationState state, int recoveryCount)
        => state switch
        {
            FlowApplicationState.Active => _activeStatus,
            FlowApplicationState.Paused => _pausedStatus,
            FlowApplicationState.RecoveryRequired when recoveryCount == 0 => _recoveryUnknownStatus,
            FlowApplicationState.RecoveryRequired => _recoveryStatus,
            FlowApplicationState.Faulted => _faultedStatus,
            _ => _closedStatus
        };
}
