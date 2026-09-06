using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Semantics;
using Xunit;

namespace Hatifect.UI.Planning.Tests;

public sealed class TypedExperienceGraphTests
{
    private static readonly UiSymbolId Owner = new("Graph.Tests", "experience");
    private static UiSymbolId Id(string name) => Owner.Child(name);

    [Fact]
    public void FailedRequiredSourceBuildCanBeRetriedWithoutSubscriptionsOrSealing()
    {
        var source = new ObservedSource<string?>(null);
        var builder = new UiExperienceBuilder(Owner, "View").Element(Id("label"), "Label", "Label", source,
            new UiSourceType<string?>(UiDataTypes.String), UiCapabilities.Inspect);
        var error = Assert.Throws<UiGraphValidationException>(() => builder.Build());
        Assert.Contains(error.Diagnostics, item => item.Code == "UIG022" && item.Subject == Id("label"));
        Assert.Equal(0, source.Subscriptions);
        source.Value = "Now ready";
        var experience = builder.Build();
        Assert.Single(experience.Graph.Nodes);
        Assert.Equal(0, source.Subscriptions);
        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Theory]
    [InlineData("Browse")]
    [InlineData("Navigate")]
    [InlineData("Actions")]
    [InlineData("Configure")]
    public void IncompatiblePresentedSourceFailsBeforeSceneActivation(string capability)
    {
        var builder = new UiExperienceBuilder(Owner, "View").Element(Id("invalid"), "Invalid", "Invalid",
            new UiConstantSource<string>("value"), UiSourceTypes.String,
            new UiCapability(new("Hatifect.UI", "capability/" + capability), capability));
        var error = Assert.Throws<UiGraphValidationException>(() => builder.Build());
        Assert.Contains(error.Diagnostics, item => item.Subject == Id("invalid") && item.Code is "UIG005" or "UIG024");
    }

    [Fact]
    public void TypedEnumFilterIsAuxiliaryUnlessAnEditablePresentationIsAvailable()
    {
        var source = new UiState<Mode>(Mode.All);
        var type = UiSourceTypes.Scalar<Mode>(Id("type/mode"), false);
        var rejected = new UiExperienceBuilder(Owner, "View").Element(Id("filter"), "Filter", "Filter", source, type, UiCapabilities.Filter);
        Assert.Contains(Assert.Throws<UiGraphValidationException>(() => rejected.Build()).Diagnostics, item => item.Code == "UIG024");
        var accepted = new UiExperienceBuilder(Owner, "View").Monitor("Status", new UiState<string>("Ready"))
            .Source(Id("filter"), "Filter", "Filter", source, type, UiCapabilities.Filter).Build();
        Assert.Single(accepted.Elements);
        Assert.Equal(2, accepted.Sources.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CollectionItemNullabilityIsCheckedAgainstTheActualPayload(bool nullable)
    {
        var source = new UiCollectionSource<string?>(new string?[] { null }, _ => Id("row"), _ => "Empty");
        var item = new UiSourceType<string?>(UiDataTypes.String with { Nullable = nullable });
        var builder = new UiExperienceBuilder(Owner, "View").Element(Id("items"), "Items", "Items", source,
            UiSourceTypes.Collection(item), UiCapabilities.Browse);
        if (nullable) Assert.Single(builder.Build().Elements);
        else Assert.Contains(Assert.Throws<UiGraphValidationException>(() => builder.Build()).Diagnostics,
            item => item.Code == "UIG022" && item.Message.Contains("item 0"));
    }

    [Fact]
    public void ClrMismatchNullableValueMismatchAndNominalReuseAreRejected()
    {
        var wrongFoundation = new UiExperienceBuilder(Owner, "View").Element(Id("a"), "A", "A",
            new UiConstantSource<int>(1), new UiSourceType<int>(UiDataTypes.String), UiCapabilities.Inspect);
        Assert.Throws<UiGraphValidationException>(() => wrongFoundation.Build());
        var wrongNullable = new UiExperienceBuilder(Owner, "View").Element(Id("a"), "A", "A",
            new UiConstantSource<bool?>(true), new UiSourceType<bool?>(UiDataTypes.Boolean), UiCapabilities.Inspect);
        Assert.Throws<UiGraphValidationException>(() => wrongNullable.Build());
        var nominal = new UiExperienceBuilder(Owner, "View")
            .Element(Id("a"), "A", "A", new UiConstantSource<int>(1), UiSourceTypes.Scalar<int>(Id("type"), false), UiCapabilities.Inspect)
            .Element(Id("b"), "B", "B", new UiConstantSource<string>("1"), UiSourceTypes.Scalar<string>(Id("type"), false), UiCapabilities.Inspect);
        Assert.Contains(Assert.Throws<UiGraphValidationException>(() => nominal.Build()).Diagnostics, item => item.Message.Contains("already declared for CLR"));
    }

    [Fact]
    public void CollectionSelectionDetailsUseOneOwnerAndOnlyThreePresentedElements()
    {
        var item = UiSourceTypes.Scalar<Parcel>(Id("type/parcel"), false);
        var row = new Parcel(Id("row"), "Cargo");
        var collection = new UiSelectableCollectionState<Parcel>(new[] { row }, value => value.Id, value => value.Label);
        var selection = new UiSelectionSource(collection);
        var details = new UiState<Parcel?>(null);
        collection.Changed += () => details.Value = collection.SelectedItemId is null ? null : row;
        var experience = new UiExperienceBuilder(Owner, "Parcels")
            .Element(Id("items"), "Items", "Shipments", collection, UiSourceTypes.Collection(item), UiCapabilities.Browse, UiCapabilities.Select)
            .Source(Id("selected"), "Selected", "Selected shipment", selection, UiSourceTypes.Selection(item), UiCapabilities.Select)
            .Element(Id("details"), "Details", "Shipment details", details, new UiSourceType<Parcel?>(item.Descriptor with { Nullable = true }), UiCapabilities.Inspect)
            .Element(Id("query"), "Query", "Find shipments", new UiState<string>(""), UiSourceTypes.String, UiCapabilities.Search)
            .Input(Id("items"), new(Id("items/input/query"), "Query", UiDataTypes.String, true))
            .Relation(new(Id("relation/selection"), UiRelationKind.Selection, Id("items"), Id("selected")))
            .Relation(new(Id("relation/details"), UiRelationKind.Details, Id("selected"), Id("details")))
            .Relation(new(Id("relation/query"), UiRelationKind.Query, Id("query"), Id("items"), Id("items/input/query")))
            .Build();
        UiPresentationPlan plan = new UiPresentationPlanner().Plan(experience, new(UiHostKind.Window, UiPresentationProfiles.Wide));
        Assert.Equal(4, experience.Graph.Nodes.Count);
        Assert.Equal(3, plan.Elements.Count);
        Assert.DoesNotContain(plan.Elements, element => element.Element == Id("selected"));
        Assert.True(collection.TrySelect(row.Id));
        Assert.Equal(row.Id, selection.Value);
        Assert.Same(row, details.Value);
        collection.Replace(Array.Empty<Parcel>());
        Assert.Null(selection.Value);
        Assert.Null(details.Value);
    }

    [Fact]
    public void ExplicitLegacyAliasRemainsAvailableAndLocalizedLabelUsesSeparateIdentity()
    {
        var experience = new UiExperienceBuilder(Owner, "View")
            .Inspect(Id("stable/counter"), "Count", new UiConstantSource<string>("3"))
            .Element(Id("stable/name"), "Name", "Название станции", new UiState<string>("Home"), UiCapabilities.Inspect)
            .Build();
        var context = experience.CreateBindingContext();
        Assert.True(context.TryGetElement("Count", out var count));
        Assert.Equal(Id("stable/counter"), count!.Id);
        Assert.True(context.TryGetElement("Name", out var name));
        Assert.Equal("Название станции", name!.Label);
        Assert.Equal(Id("stable/name"), name.Id);
    }

    [Theory]
    [InlineData("Actions")]
    [InlineData("Configure")]
    [InlineData("Filter")]
    public void RequiredSourceContractUsesCapabilityIdentityInsteadOfItsDisplayName(string name)
    {
        var capability = new UiCapability(new("Hatifect.UI", "capability/" + name), "Localized capability label");
        var builder = new UiExperienceBuilder(Owner, "View").Element(Id("value"), "Value", "Value",
            new UiConstantSource<bool>(true), UiSourceTypes.Boolean, capability);
        Assert.Contains(Assert.Throws<UiGraphValidationException>(() => builder.Build()).Diagnostics, item => item.Code == "UIG024");
    }

    [Fact]
    public void LegacyNamesRemainRawAndIndependentWhileNewTypedAliasesAreStrict()
    {
        var builder = new UiExperienceBuilder(Owner, "Legacy")
            .Inspect("A/B", new UiConstantSource<string>("first"))
            .Inspect("B", new UiConstantSource<string>("second"))
            .Actions("0", new UiActionDefinition(Id("action/run"), "Run", () => { }))
            .VisualRole("A..B");
        var experience = builder.Build();
        Assert.Equal(new[] { "A/B", "B", "0" }, experience.Elements.Select(element => element.Alias));
        var context = experience.CreateBindingContext();
        Assert.Null(context.Graph);
        Assert.True(context.TryGetElement("A/B", out var first));
        Assert.Equal(Id("element/A/B"), first!.Id);
        Assert.True(context.TryGetRole("A..B", out var role));
        Assert.Equal(Id("role/A..B"), role);
        Assert.Throws<ArgumentException>(() => new UiExperienceBuilder(Owner, "Typed").Element(Id("element/0"), "0", "0", new UiState<string>("value"), UiSourceTypes.String, UiCapabilities.Inspect));
        Assert.Throws<ArgumentException>(() => new UiExperienceBuilder(Owner, "Typed").VisualRole(Id("role/0"), "0"));
    }

    [Fact]
    public void FormValidationAndSubmissionBindTheActualActionWithoutInvokingIt()
    {
        var name = new UiState<string>("Station");
        using var form = new UiFormState(new UiSemanticFormField(Id("field/name"), "Name", name));
        var validation = new UiState<UiValidationResult>(new(Id("schema"), Array.Empty<UiValidationMessage>()));
        int invoked = 0;
        var action = new UiActionDefinition(Id("action/submit"), "Save", () => invoked++);
        UiDataType request = UiDataTypes.Scalar(Id("type/request"), false);
        var experience = new UiExperienceBuilder(Owner, "Form")
            .Element(Id("form"), "Form", "Station", form, UiSourceTypes.Form(Id("schema")), UiCapabilities.Configure)
            .Source(Id("validation"), "Validation", "Validation", validation, UiSourceTypes.Validation(Id("schema")), UiCapabilities.Inspect)
            .Actions(Id("commands"), "Commands", "Commands", action)
            .Action(action, "Submit", UiDataTypes.Action(Id("type/submit"), request, UiDataTypes.Unit))
            .Relation(new(Id("relation/validation"), UiRelationKind.Validation, Id("form"), Id("validation")))
            .Relation(new(Id("relation/submission"), UiRelationKind.Submission, Id("form"), action.Id, Mapping: request))
            .Build();

        Assert.Empty(UiGraphBinder.Validate(experience.Graph));
        Assert.Same(action, Assert.Single(experience.Actions));
        Assert.Equal(0, invoked);
        Assert.True(action.TryExecute());
        Assert.Equal(1, invoked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypedActionRequiresTheSameInstanceInItsActualGroup(bool declareReplacement)
    {
        var actual = new UiActionDefinition(Id("action/run"), "Run", () => { });
        var replacement = new UiActionDefinition(actual.Id, "Different", () => throw new InvalidOperationException());
        var builder = new UiExperienceBuilder(Owner, "Actions")
            .Monitor("Status", new UiState<string>("Ready"))
            .Action(actual, "Run", UiDataTypes.Action(Id("type/action"), UiDataTypes.Unit, UiDataTypes.Unit));
        if (declareReplacement) builder.Actions("Actions", replacement);
        Assert.Contains(Assert.Throws<UiGraphValidationException>(() => builder.Build()).Diagnostics,
            error => error.Code == "UIG023" && error.Subject == actual.Id);
    }

    private sealed record Parcel(UiSymbolId Id, string Label);
    private enum Mode { All }
    private sealed class ObservedSource<T> : IUiSemanticSource<T>
    {
        public ObservedSource(T value) => Value = value;
        public T Value { get; set; }
        public Type ValueType => typeof(T);
        public object? UntypedValue => Value;
        public int Subscriptions { get; private set; }
        public event Action? Changed { add => Subscriptions++; remove => Subscriptions--; }
    }
}
