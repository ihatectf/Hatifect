using System;
using System.Collections.Generic;
using Hatifect.UI.Experience;
using Xunit;

namespace Hatifect.UI.Planning.Tests;

public sealed class ExperienceTextTests
{
    [Fact]
    public void ExactLocaleLookupCopiesTranslationsAndUsesOnlyExplicitFallback()
    {
        var translations = new Dictionary<string, string> { ["ru-RU"] = "Груз", ["custom-A"] = "A", ["custom-B"] = "B" };
        var text = new UiLocalizedText("Cargo", translations);
        translations["ru-RU"] = "changed";
        translations.Clear();

        Assert.Equal("Груз", text.Resolve("ru-RU"));
        Assert.Equal("A", text.Resolve("custom-A"));
        Assert.Equal("B", text.Resolve("custom-B"));
        foreach (string locale in new[] { "ru", "RU-ru", "", "fr-FR", "custom-C" })
            Assert.Equal("Cargo", text.Resolve(locale));
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)text.Translations).Clear());
        Assert.Throws<ArgumentNullException>(() => text.Resolve(null!));
    }

    [Theory]
    [InlineData(null, "ru-RU", "Груз")]
    [InlineData(" ", "ru-RU", "Груз")]
    [InlineData("Cargo", null, "Груз")]
    [InlineData("Cargo", " ", "Груз")]
    [InlineData("Cargo", "ru-RU", null)]
    [InlineData("Cargo", "ru-RU", " ")]
    public void InvalidLabelCatalogRejects(string? fallback, string? locale, string? label)
        => Assert.Throws<ArgumentException>(() => new UiLocalizedText(fallback!, new[] { KeyValuePair.Create(locale!, label!) }));

    [Fact]
    public void DuplicateLocaleAndNullTranslationsReject()
    {
        Assert.Throws<ArgumentException>(() => new UiLocalizedText("Cargo", new[]
        {
            KeyValuePair.Create("ru-RU", "Груз"), KeyValuePair.Create("ru-RU", "Другой")
        }));
        Assert.Throws<ArgumentNullException>(() => new UiLocalizedText("Cargo", null!));
    }

    [Fact]
    public void LocalizedMetadataPreservesGraphFallbacksAliasesAndActionObject()
    {
        UiSymbolId element = Id.Child("element/cargo"), actionId = Id.Child("action/send");
        var action = new UiActionDefinition(actionId, "Send", () => { });
        var source = new UiState<int>(12);
        var builder = new UiExperienceBuilder(Id, "Shipment")
            .Element(element, "Cargo", "Cargo", source, UiCapabilities.Inspect)
            .Actions("Actions", action)
            .LocalizeDisplayName(Text("Shipment", "Отправление"))
            .LocalizeElement(element, Text("Cargo", "Груз"))
            .LocalizeAction(actionId, Text("Send", "Отправить"))
            .FormatText<int>(element, (value, locale) => locale + ":" + value);

        var experience = builder.Build();

        Assert.Equal("Shipment", experience.DisplayName);
        Assert.Equal("Отправление", experience.LocalizedDisplayName!.Resolve("ru-RU"));
        Assert.Equal("Cargo", experience.Elements[0].Label);
        Assert.Equal("Cargo", experience.Elements[0].Alias);
        Assert.Same(source, experience.Elements[0].Source);
        Assert.Same(action, Assert.Single(experience.Actions));
        Assert.Equal("Send", action.Title);
        Assert.Equal("Груз", experience.LocalizedElementLabels[element].Resolve("ru-RU"));
        Assert.Equal("Отправить", experience.LocalizedActionTitles[actionId].Resolve("ru-RU"));
        Assert.Throws<NotSupportedException>(() => ((IDictionary<UiSymbolId, UiLocalizedText>)experience.LocalizedElementLabels).Clear());
        Assert.Throws<InvalidOperationException>(() => builder.LocalizeElement(element, Text("Cargo", "Груз")));
        Assert.Throws<InvalidOperationException>(() => builder.FormatText<int>(element, (_, _) => "changed"));
    }

    [Fact]
    public void InvalidDeclarationsDoNotPartiallyChangeTheBuilder()
    {
        UiSymbolId element = Id.Child("element/cargo"), actionId = Id.Child("action/send");
        var builder = new UiExperienceBuilder(Id, "Shipment")
            .Element(element, "Cargo", "Cargo", new UiState<int>(12), UiCapabilities.Inspect)
            .Actions("Actions", new UiActionDefinition(actionId, "Send", () => { }));
        Assert.Throws<ArgumentException>(() => builder.LocalizeElement(Id.Child("missing"), Text("Cargo", "Груз")));
        Assert.Throws<ArgumentException>(() => builder.LocalizeAction(element, Text("Send", "Отправить")));
        Assert.Throws<ArgumentException>(() => builder.LocalizeDisplayName(Text("Wrong fallback", "Отправление")));
        Assert.Throws<ArgumentException>(() => builder.LocalizeElement(element, Text("Wrong fallback", "Груз")));
        Assert.Throws<ArgumentException>(() => builder.LocalizeAction(actionId, Text("Wrong fallback", "Отправить")));
        Assert.Throws<ArgumentNullException>(() => builder.LocalizeElement(element, null!));
        Assert.Throws<ArgumentException>(() => builder.FormatText<string>(element, (value, _) => value));
        Assert.Throws<ArgumentException>(() => builder.FormatText<int>(Id.Child("missing"), (_, _) => ""));
        Assert.Throws<ArgumentNullException>(() => builder.FormatText<int>(element, null!));

        builder.LocalizeDisplayName(Text("Shipment", "Отправление"))
            .LocalizeElement(element, Text("Cargo", "Груз"))
            .LocalizeAction(actionId, Text("Send", "Отправить"))
            .FormatText<int>(element, (_, _) => "");
        Assert.Throws<InvalidOperationException>(() => builder.LocalizeDisplayName(Text("Shipment", "Другой")));
        Assert.Throws<InvalidOperationException>(() => builder.LocalizeElement(element, Text("Cargo", "Другой")));
        Assert.Throws<InvalidOperationException>(() => builder.LocalizeAction(actionId, Text("Send", "Другой")));
        Assert.Throws<InvalidOperationException>(() => builder.FormatText<int>(element, (_, _) => "other"));
        var experience = builder.Build();
        Assert.Equal("Груз", Assert.Single(experience.LocalizedElementLabels).Value.Resolve("ru-RU"));
        Assert.Equal("Отправить", Assert.Single(experience.LocalizedActionTitles).Value.Resolve("ru-RU"));
    }

    [Fact]
    public void TooltipsRetainStableTargetsAndResolveExactLocales()
    {
        UiSymbolId element = Id.Child("element/cargo"), actionId = Id.Child("action/send");
        var action = new UiActionDefinition(actionId, "Send", () => { });
        var builder = new UiExperienceBuilder(Id, "Shipment")
            .Element(element, "Cargo", "Cargo", new UiState<int>(12), UiCapabilities.Inspect)
            .Actions("Actions", action)
            .Tooltip(element, Text("Available cargo", "Доступный груз"))
            .Tooltip(actionId, Text("Send selected cargo", "Отправить выбранный груз"));

        UiExperienceDefinition experience = builder.Build();

        Assert.Equal("Available cargo", experience.Tooltips[element].Fallback);
        Assert.Equal("Доступный груз", experience.Tooltips[element].Resolve("ru-RU"));
        Assert.Equal("Отправить выбранный груз", experience.Tooltips[actionId].Resolve("ru-RU"));
        Assert.Throws<InvalidOperationException>(() => builder.Tooltip(element, Text("Other", "Другое")));
    }

    [Fact]
    public void InvalidTooltipTargetsAndDuplicatesDoNotPartiallyChangeBuilder()
    {
        UiSymbolId element = Id.Child("element/cargo");
        var builder = new UiExperienceBuilder(Id, "Shipment")
            .Element(element, "Cargo", "Cargo", new UiState<int>(12), UiCapabilities.Inspect);
        UiLocalizedText tooltip = Text("Available cargo", "Доступный груз");

        Assert.Throws<ArgumentException>(() => builder.Tooltip(Id.Child("missing"), tooltip));
        builder.Tooltip(element, tooltip);
        Assert.Throws<InvalidOperationException>(() => builder.Tooltip(element, tooltip));

        UiExperienceDefinition experience = builder.Build();
        Assert.Single(experience.Tooltips);
        Assert.Same(tooltip, experience.Tooltips[element]);
    }

    [Fact]
    public void DeclaredFormFieldsAreStableLocalizedTooltipTargets()
    {
        UiSymbolId fieldId = Id.Child("field/name");
        using var form = new UiFormState(UiFormFields.Text(fieldId, "Name", string.Empty));
        UiLocalizedText tooltip = Text("Enter a station name", "Введите название станции");
        var builder = new UiExperienceBuilder(Id, "Station");

        Assert.Throws<ArgumentException>(() => builder.Tooltip(fieldId, tooltip));

        UiExperienceDefinition experience = builder
            .Configure("Details", form)
            .Tooltip(fieldId, tooltip)
            .Build();

        Assert.Same(tooltip, experience.Tooltips[fieldId]);
        Assert.Equal("Введите название станции", experience.Tooltips[fieldId].Resolve("ru-RU"));
    }

    [Theory]
    [InlineData("Search")]
    [InlineData("Filter")]
    [InlineData("Configure")]
    [InlineData("Actions")]
    public void EditableAndCommandCapabilitiesRejectValueFormatters(string capability)
    {
        UiCapability selected = capability switch
        {
            "Search" => UiCapabilities.Search, "Filter" => UiCapabilities.Filter,
            "Configure" => UiCapabilities.Configure, _ => UiCapabilities.Actions
        };
        UiSymbolId element = Id.Child("element/value");
        var source = new UiState<string>("draft");
        var builder = new UiExperienceBuilder(Id, "Shipment").Element(element, "Value", "Value", source, selected);
        Assert.Throws<ArgumentException>(() => builder.FormatText<string>(element, (_, _) => "translated"));
        Assert.Equal("draft", source.Value);
    }

    [Fact]
    public void CollectionAndAuxiliarySourcesCannotSilentlyIgnoreFormatters()
    {
        UiSymbolId element = Id.Child("element/rows"), auxiliary = Id.Child("source/count");
        var rows = new UiCollectionSource<string>(new[] { "ore" }, _ => Id.Child("item/ore"));
        var builder = new UiExperienceBuilder(Id, "Shipment")
            .Element(element, "Rows", "Rows", rows, UiCapabilities.Browse)
            .Source(auxiliary, "Count", "Count", new UiState<int>(1),
                UiSourceTypes.Scalar<int>(Id.Child("type/count"), false), UiCapabilities.Monitor);
        Assert.Throws<ArgumentException>(() => builder.FormatText<IReadOnlyList<string>>(element, (_, _) => "ignored"));
        Assert.Throws<ArgumentException>(() => builder.FormatText<int>(auxiliary, (_, _) => "ignored"));
        Assert.Throws<ArgumentException>(() => builder.LocalizeElement(auxiliary, Text("Count", "Количество")));
    }

    private static readonly UiSymbolId Id = new("Hatifect.Tests", "text/experience");
    private static UiLocalizedText Text(string fallback, string russian)
        => new(fallback, new Dictionary<string, string> { ["ru-RU"] = russian });
}
