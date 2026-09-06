using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Metadata;
using Xunit;

namespace Hatifect.UI.Tooling.Tests;

public sealed class SemanticGraphMetadataTests
{
    private static readonly UiSymbolId Owner = new("External", "navigator");
    private static UiSymbolId Id(string name) => Owner.Child(name);
    private static UiSymbolId Cap(string name) => new("Hatifect.UI", "capability/" + name);

    [Fact]
    public void CanonicalV1PreservesTheHistoricalUtf8Bytes()
    {
        var context = new UiBindingContext(Owner).DeclareElement("Items").DeclareRole("Item");
        const string expected = "{\"schemaVersion\":1,\"ownerId\":\"External/navigator\",\"requireDeclaredElements\":true,\"requireDeclaredRoles\":true,\"elements\":[{\"id\":\"External/navigator/element/Items\",\"name\":\"Items\",\"capabilities\":[]}],\"roles\":[{\"id\":\"External/navigator/role/Item\",\"name\":\"Item\"}]}";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), UiBindingContextJson.Export(context));
        Assert.Equal(Encoding.UTF8.GetBytes(expected), UiBindingContextJson.Export(UiBindingContextJson.Import(Encoding.UTF8.GetBytes(expected))));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("A/B")]
    [InlineData("A..B")]
    [InlineData("-")]
    [InlineData(".")]
    public void LegacyCanonicalNamesRoundTripWithoutApplyingTypedAliasGrammar(string name)
    {
        var context = new UiBindingContext(Owner).DeclareElement(name).DeclareRole(name);
        byte[] first = UiBindingContextJson.Export(context);
        UiBindingContext imported = UiBindingContextJson.Import(first);
        Assert.Equal(first, UiBindingContextJson.Export(imported));
        Assert.Null(imported.Graph);
        Assert.True(imported.TryGetElement(name, out var element));
        Assert.Equal(Id("element/" + name), element!.Id);
        Assert.True(imported.TryGetRole(name, out var role));
        Assert.Equal(Id("role/" + name), role);
        Assert.Throws<ArgumentException>(() => new UiBindingContext(Owner).DeclareElement(Id("element/" + name), name, name));
    }

    [Fact]
    public void SharedDescriptorExpansionFailsExplicitlyAtWireBudgetInsteadOfExhaustingMemory()
    {
        UiDataType type = UiDataTypes.String;
        var nodes = new System.Collections.Generic.List<UiSemanticNode>
            { new(Id("node0"), "Node0", "Leaf", type, new[] { Cap("Actions") }) };
        var relations = new System.Collections.Generic.List<UiSemanticRelation>();
        for (int index = 1; index <= 16; index++)
        {
            type = UiDataTypes.Action(Id("contract"), type, type, type, Cap("Actions"));
            nodes.Add(new(Id("node" + index), "Node" + index, "Action", type, new[] { Cap("Actions") }));
            relations.Add(new(Id("relation/" + index), UiRelationKind.ActionTarget, Id("node" + index), Id("node" + (index - 1))));
        }
        var context = new UiBindingContext(new UiSemanticGraph(Owner, nodes, relations, Array.Empty<UiSymbolId>()));
        Assert.Contains("65536", Assert.Throws<InvalidDataException>(() => UiBindingContextJson.Export(context)).Message);
    }

    [Theory]
    [InlineData("Items", "Item", "Storage list")]
    [InlineData("Shipments", "Parcel", "Список отправлений")]
    public void IndependentElementAndRoleIdentitySurvivesAliasLabelAndCompile(string alias, string roleAlias, string label)
    {
        var context = new UiBindingContext(Owner)
            .DeclareElement(Id("stable/items"), alias, label, Cap("Browse"))
            .DeclareRole(Id("stable/role"), roleAlias);
        byte[] bytes = UiBindingContextJson.Export(context);
        Assert.Contains("\"schemaVersion\":2", Encoding.UTF8.GetString(bytes));
        UiBindingContext imported = UiBindingContextJson.Import(bytes);
        Assert.Equal(bytes, UiBindingContextJson.Export(imported));
        Assert.True(imported.TryGetElement(alias, out var element));
        Assert.Equal(label, element!.Label);
        var presentation = new UiCompiler().Compile($"presentation View\n{alias} -> Primary\n", imported, "view.lui");
        var visual = new UiCompiler().Compile($"visual Style\n{roleAlias}\n    foreground = Text.Primary\n", imported, "style.lui");
        Assert.True(presentation.IsValid);
        Assert.True(visual.IsValid);
        Assert.Equal(Id("stable/items"), Assert.Single(Assert.IsType<UiPresentationDefinition>(presentation.Definition).Placements).Element);
        Assert.Equal(Id("stable/role"), Assert.Single(Assert.IsType<UiVisualDefinition>(visual.Definition).Recipes).Target);
    }

    [Fact]
    public void TwoDifferentFilterInputsAndAuxiliaryMembershipRoundTripLosslessly()
    {
        UiBindingContext original = Context();
        byte[] bytes = UiBindingContextJson.Export(original);
        UiBindingContext imported = UiBindingContextJson.Import(bytes);
        Assert.Equal(bytes, UiBindingContextJson.Export(imported));
        UiSemanticGraph graph = imported.Graph!;
        Assert.Equal(5, graph.Nodes.Count);
        Assert.Equal(new[] { Id("categories"), Id("storages") }, graph.PresentedNodes);
        Assert.False(imported.TryGetElement("Mode", out _));
        Assert.False(imported.TryGetElement("SelectedCategory", out _));
        Assert.True(imported.TryGetGraphNode("SelectedCategory", out var selection));
        Assert.Equal(UiDataShape.Selection, selection!.DataType!.Shape);
        var inputs = graph.Nodes.Single(node => node.Id == Id("storages")).Inputs;
        Assert.Equal(2, inputs.Count);
        Assert.NotEqual(inputs[0].AcceptedType, inputs[1].AcceptedType);
        Assert.Equal(inputs.Select(input => input.Id).OrderBy(id => id.ToString()),
            graph.Relations.Where(relation => relation.Kind == UiRelationKind.Filter).Select(relation => relation.TargetInput!.Value).OrderBy(id => id.ToString()));
        var provenance = graph.Relations.Single(relation => relation.Id == Id("relation/mode")).Provenance;
        Assert.Equal("navigator.cs", provenance!.SourceName);
        Assert.Equal(new Hatifect.UI.Language.Text.UiTextSpan(12, 8, 2, 4), provenance.Span);
        Assert.Equal(Id("declaration/mode-filter"), provenance.DeclarationId);
        Assert.Empty(UiGraphBinder.Validate(graph));
    }

    [Theory]
    [InlineData("slot", "UIG013")]
    [InlineData("type", "UIG014")]
    [InlineData("required", "UIG018")]
    [InlineData("duplicate", "UIG009")]
    [InlineData("foreign", "UIG001")]
    public void TamperedV2GraphIsRejectedBeforeReturningAContext(string mutation, string code)
    {
        JsonNode json = JsonNode.Parse(UiBindingContextJson.Export(Context()))!;
        JsonArray nodes = json["graph"]!["nodes"]!.AsArray();
        JsonArray relations = json["graph"]!["relations"]!.AsArray();
        JsonNode filter = relations.Single(node => node!["id"]!.GetValue<string>() == Id("relation/mode").ToString())!;
        JsonNode collection = nodes.Single(node => node!["alias"]!.GetValue<string>() == "Storages")!;
        switch (mutation)
        {
            case "slot": filter["targetInput"] = Id("missing/input").ToString(); break;
            case "type": collection["inputs"]!.AsArray().Single(input => input!["name"]!.GetValue<string>() == "Mode")!["acceptedType"]!["typeId"] = "External/type/other"; break;
            case "required": relations.Remove(filter); break;
            case "duplicate": relations.Add(JsonNode.Parse(filter.ToJsonString())); break;
            case "foreign": collection["id"] = "Foreign/node"; break;
        }
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => UiBindingContextJson.Import(Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.Contains(code, error.Message);
        Assert.IsType<UiGraphValidationException>(error.InnerException);
    }

    [Theory]
    [InlineData("schemaVersion", "99")]
    [InlineData("unknown", "true")]
    public void UnknownSchemaOrPropertiesAreExplicitErrors(string property, string value)
    {
        JsonNode json = JsonNode.Parse(UiBindingContextJson.Export(Context()))!;
        json[property] = JsonNode.Parse(value);
        Assert.Throws<InvalidDataException>(() => UiBindingContextJson.Import(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    internal static UiBindingContext Context()
    {
        var category = UiDataTypes.Scalar(new("External", "type/category"), false);
        var storage = UiDataTypes.Scalar(new("External", "type/storage"), false);
        var selection = UiDataTypes.Selection(category);
        return new UiBindingContext(new UiSemanticGraph(Owner, new[]
        {
            new UiSemanticNode(Id("mode"), "Mode", "Mode", UiDataTypes.Boolean, new[] { Cap("Filter") }),
            new UiSemanticNode(Id("categories"), "Categories", "Categories", UiDataTypes.Collection(category), new[] { Cap("Browse") }),
            new UiSemanticNode(Id("selected-category"), "SelectedCategory", "Selected category", selection, new[] { Cap("Select"), Cap("Filter") }),
            new UiSemanticNode(Id("storages"), "Storages", "Storages", UiDataTypes.Collection(storage), new[] { Cap("Browse") }, new[]
            {
                new UiProjectionInput(Id("storages/input/mode"), "Mode", UiDataTypes.Boolean, true),
                new UiProjectionInput(Id("storages/input/category"), "Category", selection, false)
            }),
            new UiSemanticNode(Id("selected-storage"), "SelectedStorage", "Selected storage", UiDataTypes.Selection(storage), new[] { Cap("Select") })
        }, new[]
        {
            new UiSemanticRelation(Id("relation/category"), UiRelationKind.Selection, Id("categories"), Id("selected-category")),
            new UiSemanticRelation(Id("relation/storage"), UiRelationKind.Selection, Id("storages"), Id("selected-storage")),
            new UiSemanticRelation(Id("relation/mode"), UiRelationKind.Filter, Id("mode"), Id("storages"), Id("storages/input/mode"),
                Provenance: new("navigator.cs", new(12, 8, 2, 4), Id("declaration/mode-filter"))),
            new UiSemanticRelation(Id("relation/category-filter"), UiRelationKind.Filter, Id("selected-category"), Id("storages"), Id("storages/input/category"))
        }, new[] { Id("categories"), Id("storages") }));
    }
}
