using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Editor;

namespace Hatifect.UI.Tooling.Protocol;

internal sealed partial class UiToolingProtocolSession
{
    private UiJsonRpcDispatchResult PlannerTrace(UiJsonRpcRequest request)
    {
        RequireRequest(request, request.Method);
        if (_planner is null) return Error(UiJsonRpcErrorCodes.MethodNotFound, "Planner trace is not supported by this server.");
        JsonElement parameters = RequiredObject(request.Parameters, "planner trace params");
        UiEditorDocumentSnapshot snapshot = CurrentCompilationSnapshot(parameters);
        UiToolingPlanningInput input = ReadPlanningInput(Property(parameters, "planningMetadata"), snapshot);
        UiEnvironment environment = ReadPlanningEnvironment(Property(parameters, "environment"));
        string hostKind = RequiredString(parameters, "hostKind");
        if (hostKind is not ("Window" or "Popup" or "Sheet" or "Context" or "Terminal" or "Fullscreen" or "Overlay"))
            throw new InvalidDataException("Unsupported planning host kind.");
        if (!snapshot.IsValid)
            return Success(new { uri = snapshot.SourceName, version = snapshot.Version, bindingRevision = _bindingRevision,
                resultId = snapshot.ResultId, status = "compilation-invalid", plan = (object?)null,
                diagnostics = DiagnosticItems(snapshot), decisions = Array.Empty<object>() });
        if (snapshot.Definition is not UiPresentationDefinition presentation)
            throw new InvalidDataException("Planner trace requires a Presentation document.");
        UiToolingPlanResult result = _planner.Plan(input, presentation, environment, hostKind);
        return Success(new { uri = snapshot.SourceName, version = snapshot.Version, bindingRevision = _bindingRevision,
            resultId = snapshot.ResultId, status = result.Status, plan = result.Plan,
            diagnostics = DiagnosticItems(snapshot), decisions = result.Decisions });
    }

    private static UiToolingPlanningInput ReadPlanningInput(JsonElement value, UiEditorDocumentSnapshot snapshot)
    {
        JsonElement metadata = RequiredObject(value, "planning metadata");
        if (RequiredNonNegativeInt32(metadata, "schemaVersion") != 1)
            throw new InvalidDataException("Unsupported planning metadata schema version.");
        UiSymbolId owner = UiSymbolId.Parse(RequiredString(metadata, "ownerId"));
        var bindings = snapshot.Analysis.Bindings;
        if (owner != bindings.OwnerId) throw new InvalidDataException("Planning metadata owner does not match bindings.");
        JsonElement elements = Property(metadata, "elements");
        if (elements.ValueKind != JsonValueKind.Array || elements.GetArrayLength() is < 1 or > 4096
            || elements.GetArrayLength() != bindings.Elements.Count)
            throw new InvalidDataException("Planning metadata must contain every presented element exactly once (1–4096).");
        var declared = bindings.Elements.ToDictionary(element => element.Id);
        var result = new List<UiToolingPlanningElement>(elements.GetArrayLength());
        foreach (JsonElement item in elements.EnumerateArray())
        {
            JsonElement element = RequiredObject(item, "planning element");
            UiSymbolId id = UiSymbolId.Parse(RequiredString(element, "id"));
            if (!declared.Remove(id, out var binding))
                throw new InvalidDataException("Planning element is duplicate, auxiliary or absent from bindings.");
            result.Add(new UiToolingPlanningElement(id, binding.Capabilities, RequiredBoolean(element, "isCollection")));
        }
        if (bindings.Graph is { } graph && !graph.PresentedNodes.SequenceEqual(result.Select(element => element.Id)))
            throw new InvalidDataException("Planning element order contradicts the semantic graph.");
        return new UiToolingPlanningInput(owner, result.AsReadOnly());
    }

    private static UiEnvironment ReadPlanningEnvironment(JsonElement value)
    {
        JsonElement environment = RequiredObject(value, "planning environment");
        string input = RequiredString(environment, "inputMode");
        if (!Enum.TryParse(input, out UiInputMode mode) || !Enum.IsDefined(typeof(UiInputMode), mode) || mode.ToString() != input)
            throw new InvalidDataException("Unsupported planning input mode.");
        string locale = RequiredString(environment, "locale");
        if (locale.Length > 128) throw new InvalidDataException("Planning locale exceeds 128 characters.");
        return new UiEnvironment(new UiEnvironmentViewport(RequiredPositiveSingle(environment, "width"),
            RequiredPositiveSingle(environment, "height")), RequiredPositiveSingle(environment, "scale"), mode, locale,
            UiSymbolId.Parse(RequiredString(environment, "theme")),
            new UiAccessibilityPreferences(RequiredBoolean(environment, "reducedMotion"), RequiredBoolean(environment, "highContrast")),
            new UiEnvironmentOrigins("Tooling request", "Tooling request", "Tooling request", "Tooling request", "Tooling request", "Tooling request"));
    }

    private static bool RequiredBoolean(JsonElement value, string name)
        => Property(value, name).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"Property '{name}' must be a boolean.")
        };

    private static float RequiredPositiveSingle(JsonElement value, string name)
    {
        JsonElement property = Property(value, name);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetSingle(out float number) || !float.IsFinite(number) || number <= 0)
            throw new InvalidDataException($"Property '{name}' must be a finite positive number.");
        return number;
    }
}
