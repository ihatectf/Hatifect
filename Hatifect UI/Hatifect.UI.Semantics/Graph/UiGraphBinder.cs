namespace Hatifect.UI.Semantics;

/// <summary>Validates declarations before source subscription or host activation. Work is O(nodes + edges + slots).</summary>
public static class UiGraphBinder
{
    public static IReadOnlyList<UiGraphDiagnostic> Validate(UiSemanticGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var errors = new List<UiGraphDiagnostic>();
        var ids = new HashSet<UiSymbolId>();
        var nodes = new Dictionary<UiSymbolId, UiSemanticNode>();
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var slots = new Dictionary<UiSymbolId, (UiSymbolId Owner, UiProjectionInput Input)>();
        var producers = new Dictionary<UiSymbolId, int>();
        var selectionOwners = new HashSet<UiSymbolId>();
        var relationKeys = new HashSet<(UiRelationKind Kind, UiSymbolId Source, UiSymbolId Target, UiSymbolId? Input)>();
        var fixedInputs = new HashSet<(UiRelationKind Kind, UiSymbolId Target)>();
        var actionTargets = new HashSet<UiSymbolId>();
        var edges = new Dictionary<UiSymbolId, List<UiSymbolId>>();
        var indegrees = new Dictionary<UiSymbolId, int>();
        var invalidTypes = new HashSet<UiSymbolId>();
        if (!graph.OwnerId.IsValid) Error("UIG001", "A valid graph owner is required.", graph.OwnerId);

        ValidateNodesAndInputs();
        ValidateRolesAndPresentedNodes();
        ValidateRelations();
        ValidateFinalGraphConstraints();
        return errors.AsReadOnly();

        void ValidateNodesAndInputs()
        {
            foreach (UiSemanticNode node in graph.Nodes)
            {
                Register(node.Id, graph.OwnerId);
                bool legacyName = node.DataType is null && node.Inputs.Count == 0 && node.Alias == node.Label
                    && IsCanonicalLegacyName(graph.OwnerId, node.Id, "element", node.Alias);
                if ((!IsAlias(node.Alias) && !legacyName) || !aliases.Add(node.Alias))
                    Error("UIG002", "Node alias must be a unique authoring identifier.", node.Id);
                if (string.IsNullOrWhiteSpace(node.Label)) Error("UIG003", "Node label is required.", node.Id);
                if (!nodes.TryAdd(node.Id, node)) continue;
                indegrees.Add(node.Id, 0);
                edges.Add(node.Id, new List<UiSymbolId>());
                var caps = new HashSet<UiSymbolId>();
                foreach (UiSymbolId capability in node.Capabilities)
                    if (!capability.IsValid || !caps.Add(capability)) Error("UIG004", "Invalid or duplicate capability.", node.Id);
                if (node.DataType is { } type)
                {
                    CheckType(type, node.Id);
                    if ((Has(node, "Browse") || Has(node, "Navigate")) && type.Shape != UiDataShape.Collection)
                        Error("UIG005", "Browse/Navigate requires a collection data source.", node.Id);
                    if (Has(node, "Select") && type.Shape is not (UiDataShape.Collection or UiDataShape.Selection))
                        Error("UIG005", "Select requires a collection or stable-ID selection source.", node.Id);
                    if (Has(node, "Search") && type != UiDataTypes.String)
                        Error("UIG005", "Search requires a required string source.", node.Id);
                }
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (UiProjectionInput input in node.Inputs)
                {
                    Register(input.Id, node.Id);
                    if (!IsAlias(input.Name) || !names.Add(input.Name)) Error("UIG006", "Invalid or duplicate input name.", input.Id);
                    CheckType(input.AcceptedType, input.Id);
                    slots.TryAdd(input.Id, (node.Id, input));
                    if (node.DataType is null) Error("UIG007", "Opaque declarations cannot have typed inputs.", node.Id, input: input.Id);
                }
            }
        }
        void ValidateRolesAndPresentedNodes()
        {
            aliases.Clear();
            foreach (UiGraphRole role in graph.Roles)
            {
                Register(role.Id, graph.OwnerId);
                if ((!IsAlias(role.Alias) && !IsCanonicalLegacyName(graph.OwnerId, role.Id, "role", role.Alias)) || !aliases.Add(role.Alias))
                    Error("UIG002", "Role alias must be a unique authoring identifier.", role.Id);
            }
            var presented = new HashSet<UiSymbolId>();
            foreach (UiSymbolId id in graph.PresentedNodes)
                if (!nodes.ContainsKey(id) || !presented.Add(id)) Error("UIG008", "Presented node is missing or duplicated.", id);
        }
        void ValidateRelations()
        {
            foreach (UiSemanticRelation relation in graph.Relations)
            {
                Register(relation.Id, graph.OwnerId);
                if (relation.Provenance is { } provenance
                    && (string.IsNullOrWhiteSpace(provenance.SourceName) || provenance.DeclarationId is { IsValid: false }
                        || provenance.Span.Start < 0 || provenance.Span.Length < 0 || provenance.Span.Line < 0 || provenance.Span.Column < 0
                        || (long)provenance.Span.Start + provenance.Span.Length > int.MaxValue))
                    RelationError("UIG028", "Provenance requires a source name, valid optional declaration ID and nonnegative bounded span.", relation);
                if (!relationKeys.Add((relation.Kind, relation.Source, relation.Target, relation.TargetInput)))
                    RelationError("UIG025", "The same semantic relation is already declared.", relation);
                if (relation.Kind is UiRelationKind.Details or UiRelationKind.Validation or UiRelationKind.Submission
                    && !fixedInputs.Add((relation.Kind, relation.Target)))
                    RelationError("UIG026", "This target accepts a single producer for this relation kind.", relation);
                if (relation.Kind == UiRelationKind.ActionTarget && !actionTargets.Add(relation.Source))
                    RelationError("UIG026", "An action accepts a single target binding.", relation);
                if (!Enum.IsDefined(typeof(UiRelationKind), relation.Kind)) { RelationError("UIG010", "Unknown relation kind.", relation); continue; }
                if (!nodes.TryGetValue(relation.Source, out UiSemanticNode? source) || !nodes.TryGetValue(relation.Target, out UiSemanticNode? target))
                { RelationError("UIG011", "Relation endpoint does not exist.", relation); continue; }
                if (source.DataType is not { } from || target.DataType is not { } to)
                { RelationError("UIG012", "Typed relations require explicit descriptors at both endpoints.", relation); continue; }
                // Invalid/deep descriptors must never reach recursive record equality or assignability.
                if (invalidTypes.Contains(source.Id) || invalidTypes.Contains(target.Id)) continue;
                if (relation.Mapping is { } mapped) CheckType(mapped, relation.Id);
                if (invalidTypes.Contains(relation.Id)) continue;

                bool inputRelation = relation.Kind is UiRelationKind.Query or UiRelationKind.Filter;
                if (inputRelation)
                {
                    if (relation.TargetInput is not { } input || !slots.TryGetValue(input, out var slot) || slot.Owner != target.Id)
                        RelationError("UIG013", "Query/filter must address an input owned by its target.", relation);
                    else
                    {
                        producers.TryGetValue(input, out int count);
                        producers[input] = count + 1;
                        if (!invalidTypes.Contains(input) && !slot.Input.AcceptedType.Accepts(from))
                            RelationError("UIG014", "Source type is incompatible with the addressed input.", relation);
                    }
                }
                else if (relation.TargetInput is not null) RelationError("UIG013", "This relation kind does not accept a projection input.", relation);
                if (relation.Kind != UiRelationKind.Submission && relation.Mapping is not null)
                    RelationError("UIG015", "Only Submission accepts a form mapping declaration.", relation);

                bool valid = relation.Kind switch
                {
                    UiRelationKind.Selection => from.Shape == UiDataShape.Collection && to.Shape == UiDataShape.Selection
                        && to.Nullable && from.ItemType == to.ItemType && Has(target, "Select"),
                    UiRelationKind.Details => from.Shape == UiDataShape.Selection && to.Shape == UiDataShape.Scalar
                        && to.Nullable && from.ItemType is { } item && to.Accepts(item) && Has(target, "Inspect"),
                    UiRelationKind.Query => from == UiDataTypes.String && Has(source, "Search")
                        && to.Shape == UiDataShape.Collection && Has(target, "Browse"),
                    UiRelationKind.Filter => from.Shape != UiDataShape.Action && Has(source, "Filter") && to.Shape == UiDataShape.Collection && Has(target, "Browse"),
                    UiRelationKind.Validation => from.Shape == UiDataShape.Form && to.Shape == UiDataShape.Validation && from.TypeId == to.TypeId,
                    UiRelationKind.Submission => from.Shape == UiDataShape.Form && to.Shape == UiDataShape.Action
                        && relation.Mapping is { } mapping && to.InputType is { } accepted && accepted.Accepts(mapping),
                    UiRelationKind.ActionTarget => from.Shape == UiDataShape.Action && from.TargetType is { } expected
                        && expected.Accepts(to) && from.TargetCapability is { } required && target.Capabilities.Contains(required),
                    _ => false
                };
                if (!valid) RelationError("UIG016", "Relation types, nullability or required capabilities do not match.", relation);
                if (relation.Kind == UiRelationKind.Selection && !selectionOwners.Add(target.Id))
                    RelationError("UIG017", "A selection must have a single collection owner.", relation);
                if (relation.Kind is not (UiRelationKind.Submission or UiRelationKind.ActionTarget))
                {
                    edges[source.Id].Add(target.Id);
                    indegrees[target.Id]++;
                }
            }
        }
        void ValidateFinalGraphConstraints()
        {
            foreach (var (id, slot) in slots)
            {
                producers.TryGetValue(id, out int count);
                if (count > 1 || (slot.Input.Required && count != 1))
                    Error("UIG018", slot.Input.Required ? "Required input needs exactly one producer." : "Optional input accepts at most one producer.", slot.Owner, input: id);
            }
            foreach (UiSemanticNode node in graph.Nodes)
                if (node.DataType is { Shape: UiDataShape.Action, TargetType: not null } && !actionTargets.Contains(node.Id))
                    Error("UIG027", "An action with a target contract requires an ActionTarget binding.", node.Id);

            // Kahn's traversal avoids recursive stack growth on large authored graphs.
            var ready = new Queue<UiSymbolId>(indegrees.Where(pair => pair.Value == 0).Select(pair => pair.Key));
            while (ready.TryDequeue(out UiSymbolId id))
                foreach (UiSymbolId target in edges[id])
                    if (--indegrees[target] == 0) ready.Enqueue(target);
            foreach (UiSemanticRelation relation in graph.Relations)
                if (relation.Kind is not (UiRelationKind.Submission or UiRelationKind.ActionTarget)
                    && indegrees.TryGetValue(relation.Source, out int left) && left > 0
                    && indegrees.TryGetValue(relation.Target, out int right) && right > 0)
                    RelationError("UIG019", "Data dependency belongs to or is blocked by a cycle.", relation);
        }

        void Register(UiSymbolId id, UiSymbolId owner)
        {
            if (!IsChild(owner, id)) Error("UIG001", $"Identity must be a child of '{owner}'.", id);
            if (!ids.Add(id)) Error("UIG009", "Identity is already declared in this graph.", id);
        }
        void CheckType(UiDataType type, UiSymbolId id, int depth = 0, Dictionary<UiDataType, int>? visited = null)
        {
            if (depth > 16) { Error("UIG020", "Data type nesting exceeds 16 levels.", id); return; }
            visited ??= new(ReferenceEqualityComparer.Instance);
            if (visited.TryGetValue(type, out int priorDepth) && priorDepth >= depth) return;
            visited[type] = depth;
            if (!type.TypeId.IsValid || !Enum.IsDefined(typeof(UiDataShape), type.Shape)) Error("UIG020", "Invalid data type identity or shape.", id);
            bool itemShape = type.Shape is UiDataShape.Collection or UiDataShape.Selection;
            if (itemShape != (type.ItemType is not null) || (itemShape && type.ItemType!.Shape != UiDataShape.Scalar))
                Error("UIG020", "Collection/selection requires a scalar nominal item descriptor.", id);
            if (type.Shape == UiDataShape.Selection && (!type.Nullable || type.TypeId != UiDataTypes.SymbolId))
                Error("UIG020", "Selection is a nullable stable symbol ID.", id);
            if (type.Shape == UiDataShape.Collection && (type.Nullable || type.ItemType?.TypeId != type.TypeId))
                Error("UIG020", "Collection is required and carries its nominal item type.", id);
            bool action = type.Shape == UiDataShape.Action;
            if (action ? type.Nullable || type.InputType is null || type.ResultType is null
                    || (type.TargetType is null) != (type.TargetCapability is null) || type.TargetCapability is { IsValid: false }
                : type.InputType is not null || type.ResultType is not null || type.TargetType is not null || type.TargetCapability is not null)
                Error("UIG020", "Invalid action input/result/target contract.", id);
            if (type.ItemType is { } item) CheckType(item, id, depth + 1, visited);
            if (type.InputType is { } input) CheckType(input, id, depth + 1, visited);
            if (type.ResultType is { } result) CheckType(result, id, depth + 1, visited);
            if (type.TargetType is { } target) CheckType(target, id, depth + 1, visited);
        }
        void Error(string code, string message, UiSymbolId subject, UiSymbolId? input = null)
        {
            if (code == "UIG020") invalidTypes.Add(subject);
            errors.Add(new(code, message, subject, Input: input));
        }
        void RelationError(string code, string message, UiSemanticRelation relation)
            => errors.Add(new(code, message, relation.Id, relation.Source, relation.Target, relation.TargetInput, relation.Provenance));
    }

    public static bool IsChild(UiSymbolId owner, UiSymbolId id)
        => owner.IsValid && id.IsValid && owner.Scope == id.Scope && id.LocalId.StartsWith(owner.LocalId + "/", StringComparison.Ordinal);

    public static bool IsAlias(string? alias)
        => !string.IsNullOrEmpty(alias) && alias.Split('.').All(part => part.Length > 0
            && (char.IsLetter(part[0]) || part[0] == '_')
            && part.All(character => char.IsLetterOrDigit(character) || character is '_' or '-'));

    public static bool IsCanonicalLegacyName(UiSymbolId owner, UiSymbolId id, string kind, string name)
        => UiSymbolId.TryParse($"{owner}/{kind}/{name}", out UiSymbolId canonical) && id == canonical;

    private static bool Has(UiSemanticNode node, string capability)
        => node.Capabilities.Contains(new UiSymbolId("Hatifect.UI", "capability/" + capability));
}
