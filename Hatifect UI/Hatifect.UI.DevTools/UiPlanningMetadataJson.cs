using System.Linq;
using System.Text.Json;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;

namespace Hatifect.UI.DevTools;

/// <summary>Exports ordered source-kind facts for a tooling planner request without reading source values.</summary>
public static class UiPlanningMetadataJson
{
    public static byte[] Export(UiExperienceDefinition experience)
    {
        UiPlanningInput input = UiPlanningInput.Capture(experience);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, ownerId = input.Id.ToString(),
            elements = input.Elements.Select(element => new
            {
                id = element.Id.ToString(), isCollection = element.IsCollection
            }).ToArray()
        });
    }
}
