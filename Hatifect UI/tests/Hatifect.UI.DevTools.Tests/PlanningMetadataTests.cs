using System;
using System.Linq;
using System.Text.Json;
using Hatifect.UI.Experience;
using Hatifect.UI.Semantics;
using Xunit;

namespace Hatifect.UI.DevTools.Tests;

public sealed class PlanningMetadataTests
{
    [Fact]
    public void ExportPreservesPresentedOrderAndActualCollectionKindWithoutReadingValues()
    {
        var owner = new UiSymbolId("External.Author", "storage");
        var experience = new UiExperienceBuilder(owner, "Storage")
            .Element("ZSelection", new UiCollectionSource<int>(new[] { 1 }, n => owner.Child("item/" + n)), UiCapabilities.Select)
            .Element("AStatus", new UnreadableSource(), UiCapabilities.Monitor).Build();

        using var metadata = JsonDocument.Parse(UiPlanningMetadataJson.Export(experience));

        Assert.Equal(1, metadata.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(owner.ToString(), metadata.RootElement.GetProperty("ownerId").GetString());
        var elements = metadata.RootElement.GetProperty("elements").EnumerateArray().ToArray();
        Assert.Equal(experience.Elements.Select(e => e.Id.ToString()), elements.Select(e => e.GetProperty("id").GetString()));
        Assert.True(elements[0].GetProperty("isCollection").GetBoolean());
        Assert.False(elements[1].GetProperty("isCollection").GetBoolean());
        Assert.Null(experience.Elements[0].DataType);
    }

    private sealed class UnreadableSource : IUiSemanticSource<string>
    {
        public Type ValueType => typeof(string);
        public string Value => throw new InvalidOperationException("Must not read source.");
        public object UntypedValue => throw new InvalidOperationException("Must not read source.");
        public event Action? Changed
        {
            add => throw new InvalidOperationException("Must not subscribe.");
            remove => throw new InvalidOperationException("Must not unsubscribe.");
        }
    }
}
