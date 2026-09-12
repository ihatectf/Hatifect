using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Protocol;

namespace Hatifect.UI.Tooling.Server;

internal sealed class UiToolingPlanner : IUiToolingPlanner
{
    public UiToolingPlanResult Plan(UiToolingPlanningInput input, UiPresentationDefinition presentation,
        UiEnvironment environment, string hostKind)
    {
        var planningInput = new UiPlanningInput(input.Owner, input.Elements.Select(element =>
            new UiPlanningElement(element.Id, element.Capabilities.Select(id => new UiCapability(id, id.ToString())),
                element.IsCollection)));
        UiHostContext host = UiHostContext.InEnvironment(Enum.Parse<UiHostKind>(hostKind), environment);
        try
        {
            UiPresentationPlan plan = new UiPresentationPlanner().PlanInput(planningInput, host, presentation);
            return new UiToolingPlanResult("planned", new
            {
                experience = plan.Experience.ToString(), pattern = plan.Pattern.ToString(),
                hostKind = plan.Host.HostKind.ToString(), profile = plan.Host.Profile.ToString(),
                elements = plan.Elements.Select(element => new
                {
                    element = element.Element.ToString(), region = element.Region.ToString(),
                    presentation = element.Presentation.ToString(),
                    decisions = element.Decisions.Select(Decision).ToArray()
                }).ToArray()
            }, plan.Decisions.Select(Decision).ToArray());
        }
        catch (UiPlanningException rejected)
        {
            return new UiToolingPlanResult("planning-rejected", null, rejected.Decisions.Select(Decision).ToArray());
        }
    }

    private static object Decision(UiPlanDecision decision) => new
    {
        code = decision.Code.ToString(), element = decision.Element?.ToString(), message = decision.Message,
        candidate = decision.Candidate?.ToString(),
        source = decision.Source is { } source ? new
        {
            sourceName = source.SourceName, start = source.Span.Start, length = source.Span.Length,
            line = source.Span.Line, column = source.Span.Column
        } : null
    };
}
