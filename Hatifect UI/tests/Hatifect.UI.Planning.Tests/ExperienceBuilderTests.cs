using System;
using System.Collections.Generic;
using Hatifect.UI.Experience;
using Xunit;

namespace Hatifect.UI.Planning.Tests;

public sealed class ExperienceBuilderTests
{
    [Fact]
    public void BuildsSemanticElementsWithoutPresentationGeometry()
    {
        UiExperienceDefinition experience = CreateExperience();

        Assert.Equal("Storage", experience.DisplayName);
        Assert.Contains(experience.Elements, element => element.Name == "Items" && element.Capabilities[0] == UiCapabilities.Browse);
        Assert.Contains(experience.Elements, element => element.Name == "Inspector" && element.Capabilities[0] == UiCapabilities.Inspect);
        Assert.Contains(experience.VisualRoles, role => role.Name == "Item");
        Assert.DoesNotContain(
            typeof(UiSemanticElementDefinition).GetProperties(),
            property => property.Name is "X" or "Y" or "Width" or "Padding" or "Color");
    }

    [Fact]
    public void DuplicateSemanticElementFailsExplicitly()
    {
        var builder = new UiExperienceBuilder(new UiSymbolId("Author.Mod", "storage"), "Storage")
            .Browse("Items", new UiCollectionSource<string>(
                Array.Empty<string>(), item => new UiSymbolId("Author.Mod", $"item/{item}")));

        Assert.Throws<InvalidOperationException>(() =>
            builder.Search("Items", new UiState<string>(string.Empty)));
    }

    [Fact]
    public void FailedBuildDoesNotSealBuilder()
    {
        var builder = new UiExperienceBuilder(new UiSymbolId("Author.Mod", "storage"), "Storage");

        Assert.Throws<InvalidOperationException>(() => builder.Build());

        UiExperienceDefinition experience = builder
            .Search("Search", new UiState<string>(string.Empty))
            .Build();
        Assert.Single(experience.Elements);
    }

    [Fact]
    public void DuplicateActionDoesNotPartiallyAddElement()
    {
        var action = new UiActionDefinition(new UiSymbolId("Author.Mod", "storage/action/take"), "Take", () => { });
        var builder = new UiExperienceBuilder(new UiSymbolId("Author.Mod", "storage"), "Storage")
            .Actions("PrimaryActions", action);

        Assert.Throws<InvalidOperationException>(() => builder.Actions("DuplicateActions", action));

        UiExperienceDefinition experience = builder.Build();
        Assert.Single(experience.Elements);
        Assert.Single(experience.Actions);
    }

    internal static UiExperienceDefinition CreateExperience()
    {
        var take = new UiActionDefinition(new UiSymbolId("Author.Mod", "storage/action/take"), "Take", () => { });
        return new UiExperienceBuilder(new UiSymbolId("Author.Mod", "storage"), "Storage")
            .Browse("Items", new UiCollectionSource<string>(
                new[] { "Wood", "Stone" }, item => new UiSymbolId("Author.Mod", $"item/{item}")))
            .Search("Search", new UiState<string>(string.Empty))
            .Inspect("Inspector", new UiState<string?>(null))
            .Actions("Actions", take)
            .VisualRole("Item")
            .VisualRole("Inspector")
            .Build();
    }
}
