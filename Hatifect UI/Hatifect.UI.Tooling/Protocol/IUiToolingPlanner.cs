using System.Collections.Generic;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Tooling.Protocol;

internal sealed record UiToolingPlanningElement(UiSymbolId Id, IReadOnlyList<UiSymbolId> Capabilities, bool IsCollection);
internal sealed record UiToolingPlanningInput(UiSymbolId Owner, IReadOnlyList<UiToolingPlanningElement> Elements);
internal sealed record UiToolingPlanResult(string Status, object? Plan, object[] Decisions);

internal interface IUiToolingPlanner
{
    UiToolingPlanResult Plan(UiToolingPlanningInput input, UiPresentationDefinition presentation,
        UiEnvironment environment, string hostKind);
}
