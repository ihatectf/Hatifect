using System.Globalization;
using Hatifect.Flow.Application;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.UI.Experience;

namespace Hatifect.Flow.UI.Semantic;

// One immutable set of domain facts and captured names shared by the five presented values.
internal sealed record ParcelPresentationSnapshot(FlowSnapshot Snapshot, FlowParcelSnapshot? Parcel,
    string ItemName = "", string? Origin = null, string? Destination = null,
    FlowCommandResult? Result = null, UiLocalizedText? LocalizedItemName = null, bool HasSelection = true);

internal enum ParcelTextPart { Cargo, Route, State, Availability, Result }

internal sealed record ParcelTextValue(ParcelPresentationSnapshot Facts, ParcelTextPart Part, bool RussianFallback)
{
    internal string Format(string locale)
    {
        bool russian = UsesRussian(locale, RussianFallback);
        FlowSnapshot snapshot = Facts.Snapshot;
        FlowParcelSnapshot? parcel = Facts.Parcel;
        bool unavailable = snapshot.State is FlowApplicationState.Closed or FlowApplicationState.Faulted || parcel is null;
        bool unselected = !Facts.HasSelection && snapshot.State is not (FlowApplicationState.Closed or FlowApplicationState.Faulted);
        FlowRejectionCode missingCode = snapshot.State is FlowApplicationState.Closed or FlowApplicationState.Faulted
            ? snapshot.Code : FlowRejectionCode.ParcelNotFound;
        return Part switch
        {
            ParcelTextPart.Cargo => unavailable ? string.Empty
                : (Facts.LocalizedItemName?.Resolve(locale) ?? Facts.ItemName) + " × " + parcel!.Quantity.ToString(CultureInfo.InvariantCulture),
            ParcelTextPart.Route => unavailable ? string.Empty
                : (Facts.Origin ?? Text("Station", "Станция")) + " → " + (Facts.Destination ?? Text("Station", "Станция")),
            ParcelTextPart.State => unselected ? Text("No shipment selected", "Отправление не выбрано")
                : unavailable ? FlowReasonText.Describe(missingCode, string.Empty, russian)
                : DescribeState(snapshot, parcel!, russian),
            ParcelTextPart.Availability => unselected && snapshot.Code == FlowRejectionCode.None
                ? Text("Select a shipment to inspect", "Выберите отправление для просмотра")
                : unavailable ? FlowReasonText.Describe(
                    snapshot.Code == FlowRejectionCode.None ? missingCode : snapshot.Code, string.Empty, russian)
                : FlowReasonText.UnavailableActions(parcel!.Availability, russian),
            ParcelTextPart.Result => Facts.Result is null ? string.Empty
                : Facts.Result.Status == FlowCommandStatus.Applied ? Text("Command completed", "Команда выполнена")
                : FlowReasonText.Describe(Facts.Result.Code, Facts.Result.ReasonKey, russian),
            _ => throw new InvalidOperationException("Unknown parcel text part.")
        };
        string Text(string english, string translated) => russian ? translated : english;
    }

    // Preserve useful fallback text for existing source inspection; scene formatting supplies its captured locale.
    public override string ToString() => Format(string.Empty);

    internal static bool UsesRussian(string locale, bool fallback)
        => locale switch { "ru" or "ru-RU" => true, "en" or "en-US" => false, _ => fallback };

    private static string DescribeState(FlowSnapshot snapshot, FlowParcelSnapshot parcel, bool russian)
    {
        return snapshot.State == FlowApplicationState.Paused ? Text("Transport paused", "Перевозки приостановлены")
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
        string Text(string english, string translated) => russian ? translated : english;
    }
}
