using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Language.Text;
using Hatifect.UI.Semantics;
using Xunit;

namespace Hatifect.UI.Semantics.Tests;

public sealed class SemanticGraphTests
{
    private static readonly UiSymbolId Owner = new("Graph.Tests", "experience");
    private static readonly UiDataType Item = UiDataTypes.Scalar(new("Graph.Tests", "types/item"), false);
    private static UiSymbolId Id(string name) => Owner.Child(name);
    private static UiSymbolId Cap(string name) => new("Hatifect.UI", "capability/" + name);

    [Fact]
    public void AllSevenRelationsBindWithoutExecutingSourcesOrActions()
    {
        UiSemanticGraph graph = Graph();
        Assert.Empty(UiGraphBinder.Validate(graph));
        Assert.Equal(Enum.GetValues<UiRelationKind>(), graph.Relations.Select(relation => relation.Kind));
        UiBindingContext context = new(graph);
        Assert.True(context.TryGetElement("Items", out var items));
        Assert.Equal(Id("items"), items!.Id);
        Assert.False(context.TryGetElement("Selection", out _));
        Assert.True(context.TryGetGraphNode("Selection", out _));
    }

    [Theory]
    [InlineData("missing-endpoint", "UIG011")]
    [InlineData("opaque-endpoint", "UIG012")]
    [InlineData("missing-input", "UIG013")]
    [InlineData("foreign-input", "UIG013")]
    [InlineData("slot-type", "UIG014")]
    [InlineData("query-capability", "UIG016")]
    [InlineData("browse-capability", "UIG016")]
    [InlineData("selection-type", "UIG016")]
    [InlineData("details-required", "UIG016")]
    [InlineData("validation-schema", "UIG016")]
    [InlineData("submission-mapping", "UIG016")]
    [InlineData("action-target", "UIG016")]
    [InlineData("selection-owner", "UIG017")]
    [InlineData("required-input", "UIG018")]
    [InlineData("duplicate-producer", "UIG018")]
    [InlineData("duplicate-id", "UIG009")]
    [InlineData("duplicate-alias", "UIG002")]
    [InlineData("cross-owner", "UIG001")]
    [InlineData("duplicate-slot", "UIG009")]
    [InlineData("role-node-id", "UIG009")]
    [InlineData("unexpected-input", "UIG013")]
    [InlineData("unexpected-mapping", "UIG015")]
    [InlineData("duplicate-relation", "UIG025")]
    [InlineData("details-producers", "UIG026")]
    [InlineData("missing-action-target", "UIG027")]
    public void InvalidGraphIsRejectedWithAddressedDiagnostics(string mutation, string expected)
    {
        UiSemanticGraph graph = Graph();
        var nodes = graph.Nodes.ToList();
        var relations = graph.Relations.ToList();
        var roles = graph.Roles.ToList();
        switch (mutation)
        {
            case "missing-endpoint": relations[0] = relations[0] with { Source = Id("missing") }; break;
            case "opaque-endpoint": Replace("items", type: null); break;
            case "missing-input": relations[2] = relations[2] with { TargetInput = null }; break;
            case "foreign-input": relations[2] = relations[2] with { TargetInput = Id("other/input/query") }; break;
            case "slot-type": Replace("query", UiDataTypes.Boolean); break;
            case "query-capability": Replace("query", UiDataTypes.String, Array.Empty<UiSymbolId>()); break;
            case "browse-capability": Replace("items", UiDataTypes.Collection(Item), new[] { Cap("Select") }); break;
            case "selection-type": Replace("selection", UiDataTypes.Selection(UiDataTypes.String)); break;
            case "details-required": Replace("details", Item); break;
            case "validation-schema": Replace("validation", UiDataTypes.Validation(Id("other-schema"))); break;
            case "submission-mapping": relations[5] = relations[5] with { Mapping = null }; break;
            case "action-target": relations[6] = relations[6] with { Target = Id("details") }; break;
            case "selection-owner": relations.Add(relations[0] with { Id = Id("relations/second-owner") }); break;
            case "required-input": relations.RemoveAt(2); break;
            case "duplicate-producer": relations.Add(relations[2] with { Id = Id("relations/second-query") }); break;
            case "duplicate-id": relations.Add(relations[2]); break;
            case "duplicate-alias": nodes.Add(new(Id("another-query"), "Query", "Another", UiDataTypes.String, new[] { Cap("Search") })); break;
            case "cross-owner": nodes.Add(new(new("Foreign", "node"), "Foreign", "Foreign", UiDataTypes.String, Array.Empty<UiSymbolId>())); break;
            case "duplicate-slot":
                var old = nodes[0]; nodes[0] = new(old.Id, old.Alias, old.Label, old.DataType, old.Capabilities, old.Inputs.Concat(old.Inputs)); break;
            case "role-node-id": roles.Add(new(Id("items"), "Role")); break;
            case "unexpected-input": relations[0] = relations[0] with { TargetInput = Id("items/input/query") }; break;
            case "unexpected-mapping": relations[0] = relations[0] with { Mapping = Item }; break;
            case "duplicate-relation": relations.Add(relations[1] with { Id = Id("relations/duplicate-details") }); break;
            case "details-producers":
                nodes.Add(new(Id("other-selection"), "OtherSelection", "Other", UiDataTypes.Selection(Item), new[] { Cap("Select") }));
                relations.Add(relations[1] with { Id = Id("relations/other-details"), Source = Id("other-selection") }); break;
            case "missing-action-target": relations.RemoveAt(6); break;
        }
        var invalid = new UiSemanticGraph(Owner, nodes, relations, graph.PresentedNodes, roles);
        IReadOnlyList<UiGraphDiagnostic> diagnostics = UiGraphBinder.Validate(invalid);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == expected && diagnostic.Subject.IsValid);
        UiGraphValidationException error = Assert.Throws<UiGraphValidationException>(() => new UiBindingContext(invalid));
        Assert.Equal(diagnostics, error.Diagnostics);
        if (mutation == "missing-input")
        {
            UiGraphDiagnostic addressed = Assert.Single(diagnostics, diagnostic => diagnostic.Code == "UIG013");
            Assert.Equal(Id("query"), addressed.Source);
            Assert.Equal(Id("items"), addressed.Target);
            Assert.Equal("graph.cs", addressed.Provenance!.SourceName);
        }

        void Replace(string id, UiDataType? type, IReadOnlyList<UiSymbolId>? capabilities = null)
        {
            int index = nodes.FindIndex(node => node.Id == Id(id));
            UiSemanticNode old = nodes[index];
            nodes[index] = new(old.Id, old.Alias, old.Label, type, capabilities ?? old.Capabilities, old.Inputs);
        }
    }

    [Fact]
    public void DiagnosticsPreserveDeclarationRelationAndRequiredBindingOrder()
    {
        UiSemanticGraph graph = Graph();
        var nodes = graph.Nodes.ToArray();
        UiSemanticNode items = nodes[0];
        nodes[0] = new(items.Id, items.Alias, " ", items.DataType, items.Capabilities, items.Inputs);
        var relations = graph.Relations.ToList();
        UiSemanticRelation query = relations[2] with { Source = Id("missing-source") };
        relations[2] = query;
        relations.RemoveAt(6);
        UiSymbolId invalidRole = Id("role/invalid");
        var invalid = new UiSemanticGraph(Owner, nodes, relations,
            graph.PresentedNodes.Concat(new[] { Id("items"), Id("missing-presented") }),
            new[] { new UiGraphRole(invalidRole, "Invalid Role") });
        var expected = new UiGraphDiagnostic[]
        {
            new("UIG003", "Node label is required.", Id("items")),
            new("UIG002", "Role alias must be a unique authoring identifier.", invalidRole),
            new("UIG008", "Presented node is missing or duplicated.", Id("items")),
            new("UIG008", "Presented node is missing or duplicated.", Id("missing-presented")),
            new("UIG011", "Relation endpoint does not exist.", query.Id,
                query.Source, query.Target, query.TargetInput, query.Provenance),
            new("UIG018", "Required input needs exactly one producer.", Id("items"), Input: Id("items/input/query")),
            new("UIG027", "An action with a target contract requires an ActionTarget binding.", Id("action"))
        };

        IReadOnlyList<UiGraphDiagnostic> diagnostics = UiGraphBinder.Validate(invalid);

        Assert.Equal(expected, diagnostics);
        UiGraphValidationException error = Assert.Throws<UiGraphValidationException>(() => new UiBindingContext(invalid));
        Assert.Equal(expected, error.Diagnostics);
        Assert.Equal(string.Join(Environment.NewLine, expected.Select(item => $"{item.Code} {item.Subject}: {item.Message}")), error.Message);
    }

    [Fact]
    public void DuplicateRelationDiagnosticsPrecedeEndpointErrorsAndCycleDiagnostics()
    {
        UiDataType collection = UiDataTypes.Collection(Item);
        UiSemanticNode Node(string name) => new(Id(name), name, name, collection, new[] { Cap("Browse"), Cap("Filter") },
            new[] { new UiProjectionInput(Id(name + "/input"), "Input", collection, true) });
        var provenance = new UiGraphProvenance("cycle.cs", new UiTextSpan(4, 6, 0, 4), Id("declaration/cycle"));
        var missing = new UiSemanticRelation(Id("relations/missing"), UiRelationKind.Details,
            Id("missing-source"), Id("missing-target"), Provenance: provenance);
        UiSemanticRelation duplicate = missing with { Id = Id("relations/duplicate") };
        var forward = new UiSemanticRelation(Id("relations/forward"), UiRelationKind.Filter,
            Id("A"), Id("B"), Id("B/input"), Provenance: provenance);
        var backward = new UiSemanticRelation(Id("relations/backward"), UiRelationKind.Filter,
            Id("B"), Id("A"), Id("A/input"), Provenance: provenance);
        var graph = new UiSemanticGraph(Owner, new[] { Node("A"), Node("B") },
            new[] { missing, duplicate, forward, backward });
        var expected = new UiGraphDiagnostic[]
        {
            new("UIG011", "Relation endpoint does not exist.", missing.Id,
                missing.Source, missing.Target, Provenance: provenance),
            new("UIG025", "The same semantic relation is already declared.", duplicate.Id,
                duplicate.Source, duplicate.Target, Provenance: provenance),
            new("UIG026", "This target accepts a single producer for this relation kind.", duplicate.Id,
                duplicate.Source, duplicate.Target, Provenance: provenance),
            new("UIG011", "Relation endpoint does not exist.", duplicate.Id,
                duplicate.Source, duplicate.Target, Provenance: provenance),
            new("UIG019", "Data dependency belongs to or is blocked by a cycle.", forward.Id,
                forward.Source, forward.Target, forward.TargetInput, provenance),
            new("UIG019", "Data dependency belongs to or is blocked by a cycle.", backward.Id,
                backward.Source, backward.Target, backward.TargetInput, provenance)
        };

        IReadOnlyList<UiGraphDiagnostic> diagnostics = UiGraphBinder.Validate(graph);

        Assert.Equal(expected, diagnostics);
        Assert.All(diagnostics, diagnostic => Assert.Same(provenance, diagnostic.Provenance));
        UiGraphValidationException error = Assert.Throws<UiGraphValidationException>(() => new UiBindingContext(graph));
        Assert.Equal(expected, error.Diagnostics);
    }

    [Fact]
    public void RequiredValuesMayFeedOptionalSlotsButOptionalValuesCannotFeedRequiredSlots()
    {
        Assert.True((UiDataTypes.String with { Nullable = true }).Accepts(UiDataTypes.String));
        Assert.False(UiDataTypes.String.Accepts(UiDataTypes.String with { Nullable = true }));
        Assert.False(UiDataTypes.String.Accepts(Item));
    }

    [Theory]
    [InlineData("empty-source")]
    [InlineData("start")]
    [InlineData("length")]
    [InlineData("line")]
    [InlineData("column")]
    [InlineData("overflow")]
    [InlineData("declaration")]
    public void InvalidProvenanceIsRejectedBeforeExportOrActivation(string mutation)
    {
        var graph = Graph();
        var provenance = mutation switch
        {
            "empty-source" => new UiGraphProvenance(" ", new(0, 0, 0, 0)),
            "start" => new("graph.cs", new(-1, 0, 0, 0)),
            "length" => new("graph.cs", new(0, -1, 0, 0)),
            "line" => new("graph.cs", new(0, 0, -1, 0)),
            "column" => new("graph.cs", new(0, 0, 0, -1)),
            "overflow" => new("graph.cs", new(int.MaxValue, 1, 0, 0)),
            _ => new("graph.cs", new(0, 0, 0, 0), default(UiSymbolId))
        };
        var relations = graph.Relations.ToArray();
        relations[0] = relations[0] with { Provenance = provenance };
        var invalid = new UiSemanticGraph(Owner, graph.Nodes, relations, graph.PresentedNodes, graph.Roles);
        var error = Assert.Throws<UiGraphValidationException>(() => new UiBindingContext(invalid));
        var diagnostic = Assert.Single(error.Diagnostics);
        Assert.Equal("UIG028", diagnostic.Code);
        Assert.Equal(relations[0].Id, diagnostic.Subject);
        Assert.Same(provenance, diagnostic.Provenance);
    }

    [Fact]
    public void ValidTypedFilterCycleIsRejectedWithoutRecursiveTraversal()
    {
        UiDataType collection = UiDataTypes.Collection(Item);
        UiSemanticNode Node(string name) => new(Id(name), name, name, collection, new[] { Cap("Browse"), Cap("Filter") },
            new[] { new UiProjectionInput(Id(name + "/input"), "Input", collection, true) });
        var graph = new UiSemanticGraph(Owner, new[] { Node("A"), Node("B") }, new[]
        {
            new UiSemanticRelation(Id("relations/a"), UiRelationKind.Filter, Id("A"), Id("B"), Id("B/input")),
            new UiSemanticRelation(Id("relations/b"), UiRelationKind.Filter, Id("B"), Id("A"), Id("A/input"))
        });
        var errors = UiGraphBinder.Validate(graph);
        Assert.Equal(2, errors.Count);
        Assert.All(errors, error => Assert.Equal("UIG019", error.Code));
    }

    [Fact]
    public void AuxiliarySourceCannotBePlacedEvenWhenUndeclaredTargetsAreAllowed()
    {
        var context = new UiBindingContext(Graph()) { RequireDeclaredElements = false };
        var result = new UiCompiler().Compile("presentation Test\nSelection -> Primary\n", context);
        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "UIG021" && diagnostic.Message.Contains(Id("selection").ToString()));
    }

    [Fact]
    public void GraphCopiesCallerCollectionsAndAllowsSeparateRoleAliasNamespace()
    {
        var capabilities = new[] { Cap("Monitor") };
        var node = new UiSemanticNode(Id("a"), "A", "Label", UiDataTypes.String, capabilities);
        var nodes = new[] { node };
        var roles = new[] { new UiGraphRole(Id("role/a"), "A") };
        var graph = new UiSemanticGraph(Owner, nodes, roles: roles);
        capabilities[0] = Cap("Browse");
        nodes[0] = new(Id("b"), "B", "Other", UiDataTypes.String, capabilities);
        roles[0] = new(Id("a"), "A");
        Assert.Same(node, Assert.Single(graph.Nodes));
        Assert.Equal(Cap("Monitor"), Assert.Single(node.Capabilities));
        Assert.Empty(UiGraphBinder.Validate(graph));
    }

    [Fact]
    public void RejectedDeepDescriptorsNeverReachRecursiveAssignability()
    {
        UiDataType Chain()
        {
            UiDataType type = UiDataTypes.String;
            for (int index = 0; index < 10000; index++)
                type = UiDataTypes.Action(Id("type/action"), type, UiDataTypes.Unit);
            return type;
        }
        var graph = new UiSemanticGraph(Owner, new[]
        {
            new UiSemanticNode(Id("source"), "Source", "Source", Chain(), new[] { Cap("Filter") }),
            new UiSemanticNode(Id("target"), "Target", "Target", UiDataTypes.Collection(Item), new[] { Cap("Browse") },
                new[] { new UiProjectionInput(Id("target/input"), "Input", Chain(), true) })
        }, new[] { new UiSemanticRelation(Id("relation"), UiRelationKind.Filter, Id("source"), Id("target"), Id("target/input")) });
        var diagnostics = UiGraphBinder.Validate(graph);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "UIG020" && diagnostic.Subject == Id("source"));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "UIG020" && diagnostic.Subject == Id("target/input"));
        Assert.Throws<UiGraphValidationException>(() => new UiBindingContext(graph));
    }

    [Fact]
    public void SharedDescriptorBranchesHaveBoundedValidationEqualityAndHashing()
    {
        UiDataType Tree()
        {
            UiDataType type = UiDataTypes.String;
            for (int depth = 0; depth < 16; depth++)
                type = UiDataTypes.Action(Id("type/action"), type, type, type, Cap("Actions"));
            return type;
        }
        UiDataType first = Tree(), second = Tree();
        Assert.True(first.Accepts(second));
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        // The missing target is reported after type validation visits the shared DAG.
        var graph = new UiSemanticGraph(Owner, new[] { new UiSemanticNode(Id("action"), "Action", "Action", first, new[] { Cap("Actions") }) });
        Assert.Equal("UIG027", Assert.Single(UiGraphBinder.Validate(graph)).Code);
    }

    private static UiSemanticGraph Graph()
    {
        UiDataType form = UiDataTypes.Form(Id("schema"));
        UiDataType request = UiDataTypes.Scalar(Id("request"), false);
        var nodes = new[]
        {
            new UiSemanticNode(Id("items"), "Items", "Items", UiDataTypes.Collection(Item), new[] { Cap("Browse") }, new[]
            {
                new UiProjectionInput(Id("items/input/query"), "Query", UiDataTypes.String, true),
                new UiProjectionInput(Id("items/input/mode"), "Mode", UiDataTypes.Boolean, false)
            }),
            new UiSemanticNode(Id("selection"), "Selection", "Selection", UiDataTypes.Selection(Item), new[] { Cap("Select") }),
            new UiSemanticNode(Id("details"), "Details", "Details", Item with { Nullable = true }, new[] { Cap("Inspect") }),
            new UiSemanticNode(Id("query"), "Query", "Query", UiDataTypes.String, new[] { Cap("Search") }),
            new UiSemanticNode(Id("filter"), "Filter", "Filter", UiDataTypes.Boolean, new[] { Cap("Filter") }),
            new UiSemanticNode(Id("form"), "Form", "Form", form, new[] { Cap("Configure") }),
            new UiSemanticNode(Id("validation"), "Validation", "Validation", UiDataTypes.Validation(form.TypeId), Array.Empty<UiSymbolId>()),
            new UiSemanticNode(Id("action"), "Submit", "Submit", UiDataTypes.Action(Id("action-type"), request, UiDataTypes.Unit,
                UiDataTypes.Selection(Item), Cap("Select")), new[] { Cap("Actions") })
        };
        UiSemanticRelation Relation(string id, UiRelationKind kind, string from, string to, UiSymbolId? input = null, UiDataType? mapping = null)
            => new(Id("relations/" + id), kind, Id(from), Id(to), input, mapping, new("graph.cs", new UiTextSpan(4, 6, 0, 4), Id("declaration/" + id)));
        return new(Owner, nodes, new[]
        {
            Relation("selection", UiRelationKind.Selection, "items", "selection"),
            Relation("details", UiRelationKind.Details, "selection", "details"),
            Relation("query", UiRelationKind.Query, "query", "items", Id("items/input/query")),
            Relation("filter", UiRelationKind.Filter, "filter", "items", Id("items/input/mode")),
            Relation("validation", UiRelationKind.Validation, "form", "validation"),
            Relation("submit", UiRelationKind.Submission, "form", "action", mapping: request),
            Relation("target", UiRelationKind.ActionTarget, "action", "selection")
        }, new[] { Id("items"), Id("query"), Id("details"), Id("form") });
    }
}
