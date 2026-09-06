using System.Text;
using Hatifect.Flow.Application;

namespace Hatifect.Flow.UI.Semantic;

internal static class FlowReasonText
{
    private static readonly FlowParcelAction[] Actions = Enum.GetValues<FlowParcelAction>();

    internal static string Describe(FlowRejectionCode code, string reasonKey, bool russian)
    {
        const string prefix = "flow.reason.";
        if (reasonKey.StartsWith(prefix, StringComparison.Ordinal)
            && Enum.TryParse(reasonKey[prefix.Length..], out FlowRejectionCode known)
            && Enum.IsDefined(typeof(FlowRejectionCode), known)) code = known;
        return code switch
        {
            FlowRejectionCode.None => "",
            FlowRejectionCode.UnsupportedAction => Text("This provider does not support this action", "Этот провайдер не поддерживает действие"),
            FlowRejectionCode.Paused => Text("Transport is paused", "Перевозки приостановлены"),
            FlowRejectionCode.RecoveryRequired => Text("Recovery required; cargo retained", "Требуется восстановление; груз сохранён"),
            FlowRejectionCode.SessionClosed => Text("Session closed", "Сессия закрыта"),
            FlowRejectionCode.Faulted => Text("Transport failed; see its diagnostic log", "Ошибка перевозки; см. журнал диагностики"),
            FlowRejectionCode.StaleSession => Text("This screen belongs to a previous session", "Этот экран относится к предыдущей сессии"),
            FlowRejectionCode.StaleRevision or FlowRejectionCode.StateChanged => Text("State changed; review the updated data", "Состояние изменилось; проверьте обновлённые данные"),
            FlowRejectionCode.InvalidCommand => Text("Invalid command", "Некорректная команда"),
            FlowRejectionCode.ParcelNotFound => Text("Shipment is no longer available", "Отправление больше недоступно"),
            FlowRejectionCode.InvalidState => Text("Unavailable at this shipment stage", "Недоступно на этой стадии отправления"),
            FlowRejectionCode.WorkLimit => Text("Transport work queue is full", "Очередь перевозок заполнена"),
            FlowRejectionCode.RouteUnavailable => Text("No route connects these stations", "Между станциями нет маршрута"),
            FlowRejectionCode.RouteSearchLimit => Text("Route search reached its supported limit", "Поиск маршрута достиг поддерживаемого предела"),
            FlowRejectionCode.CapacityUnavailable => Text("The route has insufficient free capacity", "На маршруте недостаточно свободной ёмкости"),
            FlowRejectionCode.RetryLimit => Text("The retry limit has been reached", "Достигнут предел повторных попыток"),
            FlowRejectionCode.OperationPending => Text("An operation is already scheduled", "Операция уже запланирована"),
            FlowRejectionCode.ProviderUnavailable => Text("The inventory owner is temporarily unavailable", "Владелец инвентаря временно недоступен"),
            FlowRejectionCode.UnknownOutcome => Text("No settled transfer outcome is available; cargo retained", "Нет подтверждённого результата передачи; груз сохранён"),
            _ => Text("Cannot perform this action now", "Сейчас это действие недоступно")
        };
        string Text(string en, string ru) => russian ? ru : en;
    }

    internal static string UnavailableActions(FlowActionAvailabilitySet availability, bool russian)
    {
        var text = new StringBuilder();
        foreach (FlowParcelAction action in Actions)
        {
            FlowActionAvailability value = availability[action];
            if (value.Available) continue;
            if (text.Length > 0) text.Append('\n');
            text.Append(Title(action, russian)).Append(": ").Append(Describe(value.Code, value.ReasonKey, russian));
        }
        return text.ToString();
    }

    private static string Title(FlowParcelAction action, bool ru) => action switch
    {
        FlowParcelAction.Reserve => ru ? "Отправка" : "Dispatch",
        FlowParcelAction.Cancel => ru ? "Отмена" : "Cancel",
        FlowParcelAction.RetryDelivery => ru ? "Повторная доставка" : "Retry delivery",
        FlowParcelAction.ReconcileTransfer => ru ? "Проверка передачи" : "Check transfer",
        _ => ru ? "Возврат" : "Return"
    };
}
