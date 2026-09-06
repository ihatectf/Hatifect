using Hatifect.Flow.Application;
using Hatifect.UI;
using Hatifect.UI.Experience;

namespace Hatifect.Flow.UI.Semantic;

/// <summary>Flow-owned nominal identities do not depend on assembly names, labels or localization.</summary>
internal static class FlowUiDataTypes
{
    internal static readonly UiSourceType<FlowParcelSnapshot> Parcel = UiSourceTypes.Scalar<FlowParcelSnapshot>(Id("parcel"), false);
    internal static readonly UiSourceType<FlowStationDetails> Station = UiSourceTypes.Scalar<FlowStationDetails>(Id("station"), false);
    internal static readonly UiSourceType<FlowLinkSnapshot> Link = UiSourceTypes.Scalar<FlowLinkSnapshot>(Id("link"), false);
    internal static readonly UiSourceType<FlowInventorySlot> InventorySlot = UiSourceTypes.Scalar<FlowInventorySlot>(Id("inventory-slot"), false);
    internal static readonly UiSourceType<FlowRecoveryIssue> Recovery = UiSourceTypes.Scalar<FlowRecoveryIssue>(Id("recovery"), false);
    internal static readonly UiSourceType<IReadOnlyList<UiSemanticFormField>> StationForm = UiSourceTypes.Form(Id("station-form"));
    internal static readonly UiSourceType<IReadOnlyList<UiSemanticFormField>> LinkForm = UiSourceTypes.Form(Id("link-form"));
    private static UiSymbolId Id(string name) => new("Hatifect.Flow", "data/" + name);
}
