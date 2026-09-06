using Hatifect.Flow.Application;
using Hatifect.UI;
using Hatifect.UI.Experience;

namespace Hatifect.Flow.UI.Semantic;

internal sealed partial class NetworkExperience
{
    private readonly UiSelectableCollectionState<FlowInventorySlot> _inventory;
    private Guid? _inventorySource;

    private void AppendShipping(UiExperienceBuilder builder, UiSymbolId id)
    {
        builder.Element(id.Child("element/source-cargo"), "SourceCargo", Text("Source cargo", "Груз в источнике"), _inventory, UiSourceTypes.Collection(FlowUiDataTypes.InventorySlot), UiCapabilities.Select)
            .Actions(id.Child("element/shipment-actions"), "ShipmentActions", Text("Shipment actions", "Действия с грузом"),
                Action(id, "send", Text("Send whole stack", "Отправить весь стек"), Send,
                    () => Source is not null && Destination is not null && Source.Id != Destination.Id && Selected(_inventory) is not null),
                new UiActionDefinition(id.Child("action/inventory"), Text("Refresh cargo", "Обновить груз"), RefreshInventory, () => IsActive));
    }

    private void RefreshInventory()
    {
        _inventorySource = Source?.Id;
        _inventory.Replace(_inventorySource.HasValue ? _application.ReadInventory(_inventorySource.Value) : Array.Empty<FlowInventorySlot>());
    }

    private void Send()
    {
        FlowInventorySlot? slot = Selected(_inventory);
        if (slot is null || Source is null || Destination is null) return;
        FlowCommandResult result = _application.Execute(new FlowSendCommand(_snapshot.Transport.SessionId, _snapshot.Transport.Revision,
            Source.Id, Destination.Id, slot.Index, slot.Fingerprint));
        _result.Value = result.Status == FlowCommandStatus.Applied ? Text("Shipment created", "Отправление создано")
            : FlowReasonText.Describe(result.Code, result.ReasonKey, _russian);
        _dirty = true;
        Pump();
        RefreshInventory();
    }
}
