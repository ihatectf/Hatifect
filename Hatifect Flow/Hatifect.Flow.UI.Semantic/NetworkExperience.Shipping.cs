using System.Globalization;
using Hatifect.Flow.Application;
using Hatifect.UI;
using Hatifect.UI.Experience;

namespace Hatifect.Flow.UI.Semantic;

internal sealed partial class NetworkExperience
{
    private readonly FlowSelectionSource<FlowInventorySlot> _inventory;
    private readonly FlowTextSource _quantity;
    private readonly UiPublishedState<string?> _quantityError;
    private readonly UiFormState _shipmentForm;

    private void AppendShipping(UiExperienceBuilder builder, UiSymbolId id)
    {
        builder.Element(id.Child("element/source-cargo"), "SourceCargo", Text("Source cargo", "Груз в источнике"), _inventory, UiSourceTypes.Collection(FlowUiDataTypes.InventorySlot), UiCapabilities.Select)
            .Element(id.Child("element/shipment-quantity"), "ShipmentQuantity", Text("Quantity", "Количество"), _shipmentForm, FlowUiDataTypes.ShipmentForm, UiCapabilities.Configure)
            .Actions(id.Child("element/shipment-actions"), "ShipmentActions", Text("Shipment actions", "Действия с грузом"),
                Action(id, "send", Text("Send whole stack", "Отправить весь стек"), () => Send(null),
                    () => Source is not null && Destination is not null && Source.Id != Destination.Id && Selected(_inventory) is not null),
                Action(id, "send-quantity", Text("Send selected quantity", "Отправить указанное количество"),
                    () => Send(int.Parse(_quantity.Value, CultureInfo.InvariantCulture)),
                    () => Source is not null && Destination is not null && Source.Id != Destination.Id && _shipmentForm.IsValid),
                new UiActionDefinition(id.Child("action/inventory"), Text("Refresh cargo", "Обновить груз"), RefreshInventory, () => CanRequest && IsActive));
    }

    private void RefreshInventory() => Refresh();

    private void ValidateQuantity(UiPublicationBatch batch)
    {
        FlowInventorySlot? slot = Selected(batch, _inventory);
        batch.Set(_quantityError, slot is null ? Text("Select source cargo", "Выберите груз в источнике")
            : Number(batch.Read(_quantity.Source), slot.Quantity) ? null
            : Text("Enter 1–", "Введите 1–") + slot.Quantity.ToString(CultureInfo.InvariantCulture));
    }

    private void Send(int? quantity)
    {
        FlowInventorySlot? slot = Selected(_inventory);
        if (slot is null || Source is null || Destination is null) return;
        FlowCommandResult result = _application.Execute(new FlowSendCommand(_snapshot.Transport.SessionId, _snapshot.Transport.Revision,
            Source.Id, Destination.Id, slot.Index, slot.Fingerprint) { Quantity = quantity });
        Complete(result, Text("Shipment created", "Отправление создано"), refreshInventory: true);
    }
}
