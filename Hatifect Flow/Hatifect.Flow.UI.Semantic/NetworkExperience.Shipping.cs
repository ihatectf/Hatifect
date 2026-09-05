using System.Globalization;
using Hatifect.Flow.Application;
using Hatifect.UI;
using Hatifect.UI.Experience;

namespace Hatifect.Flow.UI.Semantic;

internal sealed partial class NetworkExperience
{
    private readonly UiSelectableCollectionState<FlowInventorySlot> _inventory;
    private readonly UiState<string> _quantity = new("1");
    private readonly UiState<string?> _quantityError = new(null);
    private readonly UiFormState _shipmentForm;
    private Guid? _inventorySource;

    private void AppendShipping(UiExperienceBuilder builder, UiSymbolId id)
    {
        builder.Select(id.Child("element/source-cargo"), Text("Source cargo", "Груз в источнике"), _inventory)
            .Configure(id.Child("element/shipment-quantity"), Text("Quantity", "Количество"), _shipmentForm)
            .Actions(id.Child("element/shipment-actions"), Text("Shipment actions", "Действия с грузом"),
                Action(id, "send", Text("Send whole stack", "Отправить весь стек"), () => Send(null),
                    () => Source is not null && Destination is not null && Source.Id != Destination.Id && Selected(_inventory) is not null),
                Action(id, "send-quantity", Text("Send selected quantity", "Отправить указанное количество"),
                    () => Send(int.Parse(_quantity.Value, CultureInfo.InvariantCulture)),
                    () => Source is not null && Destination is not null && Source.Id != Destination.Id && _shipmentForm.IsValid),
                new UiActionDefinition(id.Child("action/inventory"), Text("Refresh cargo", "Обновить груз"), RefreshInventory, () => IsActive));
    }

    private void RefreshInventory()
    {
        _inventorySource = Source?.Id;
        _inventory.Replace(_inventorySource.HasValue ? _application.ReadInventory(_inventorySource.Value) : Array.Empty<FlowInventorySlot>());
    }

    private void ValidateQuantity()
    {
        FlowInventorySlot? slot = Selected(_inventory);
        _quantityError.Value = slot is null ? Text("Select source cargo", "Выберите груз в источнике")
            : Number(_quantity.Value, slot.Quantity) ? null
            : Text("Enter 1–", "Введите 1–") + slot.Quantity.ToString(CultureInfo.InvariantCulture);
    }

    private void Send(int? quantity)
    {
        FlowInventorySlot? slot = Selected(_inventory);
        if (slot is null || Source is null || Destination is null) return;
        FlowCommandResult result = _application.Execute(new FlowSendCommand(_snapshot.Transport.SessionId, _snapshot.Transport.Revision,
            Source.Id, Destination.Id, slot.Index, slot.Fingerprint) { Quantity = quantity });
        _result.Value = result.Status == FlowCommandStatus.Applied ? Text("Shipment created", "Отправление создано")
            : FlowReasonText.Describe(result.Code, result.ReasonKey, _russian);
        _dirty = true;
        Pump();
        RefreshInventory();
    }
}
