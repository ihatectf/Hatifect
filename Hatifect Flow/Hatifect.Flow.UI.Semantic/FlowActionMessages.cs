using Hatifect.Flow.Application;
using Hatifect.UI.Experience;

namespace Hatifect.Flow.UI.Semantic;

// Fixed enum-sized caches keep availability reads free of text/dictionary allocations.
internal static class FlowActionMessages
{
    private static readonly IReadOnlyDictionary<FlowRejectionCode, UiActionAvailability> English = Create(false);
    private static readonly IReadOnlyDictionary<FlowRejectionCode, UiActionAvailability> Russian = Create(true);

    internal static UiActionAvailability Disabled(FlowRejectionCode code, bool russian)
        => (russian ? Russian : English).TryGetValue(code, out var reason)
            ? reason : (russian ? Russian : English)[FlowRejectionCode.ActionUnavailable];
    internal static UiActionMessage Message(FlowRejectionCode code, bool russian)
        => Disabled(code, russian).Reason!.LocalizedMessage!;

    private static IReadOnlyDictionary<FlowRejectionCode, UiActionAvailability> Create(bool russian)
    {
        var result = new Dictionary<FlowRejectionCode, UiActionAvailability>();
        foreach (FlowRejectionCode code in Enum.GetValues<FlowRejectionCode>())
        {
            FlowRejectionCode reason = code == FlowRejectionCode.None ? FlowRejectionCode.ActionUnavailable : code;
            string english = FlowReasonText.Describe(reason, string.Empty, false);
            string translated = FlowReasonText.Describe(reason, string.Empty, true);
            var message = new UiActionMessage("flow.reason." + reason,
                new UiLocalizedText(russian ? translated : english, new Dictionary<string, string>
                { ["en"] = english, ["en-US"] = english, ["ru"] = translated, ["ru-RU"] = translated }));
            result.Add(code, UiActionAvailability.Disabled(UiActionRejection.Localized(message)));
        }
        return result;
    }
}
