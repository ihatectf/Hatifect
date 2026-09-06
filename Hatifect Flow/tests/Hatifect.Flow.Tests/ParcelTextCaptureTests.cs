using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.Flow.Application;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.UI.Semantic;
using Hatifect.UI;
using Hatifect.UI.Experience;
using Xunit;

namespace Hatifect.Flow.Tests;

[Trait("Category", "flow")]
public sealed class ParcelTextCaptureTests
{
    [Fact]
    public void FailedLocalizedNameCaptureRetainsTheWholePublicationAndRawCommandResultForRetry()
    {
        var fixture = new CheckpointFixture();
        int commands = 0, captures = 0;
        bool fail = false;
        using var app = new FlowApplication(fixture.Runtime, action => { commands++; action(fixture.Runtime); }, _ => { });
        using var view = new ParcelExperience(Id, app, CheckpointFixture.Parcel.Value,
            localizedItemName: _ => { captures++; return fail ? null! : Name(); });
        long before = view.Publication.Version;
        object?[] values = view.Experience.Elements.Select(element => element.Source.UntypedValue).ToArray();
        Assert.True(view.Experience.Actions[0].TryExecute());
        fail = true;

        Assert.Throws<InvalidOperationException>(() => view.Pump());

        Assert.Equal(before, view.Publication.Version);
        var unchanged = view.Experience.Elements.Select(element => element.Source.UntypedValue).ToArray();
        for (int i = 0; i < values.Length; i++) Assert.Same(values[i], unchanged[i]);
        Assert.Equal(1, commands);
        Assert.Equal(ParcelState.Reserved, fixture.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        fail = false;
        Assert.True(view.Pump());
        Assert.Equal(before + 1, view.Publication.Version);
        Assert.Equal("Медная руда × 7", Value(view, "Cargo").Format("ru-RU"));
        Assert.Equal("Команда выполнена", Value(view, "Result").Format("ru-RU"));
        Assert.Equal(FlowCommandStatus.Applied, Value(view, "Result").Facts.Result!.Status);
        Assert.Equal(1, commands);
        Assert.Equal(3, captures);
        Assert.False(view.Pump());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RetirementDuringLocalizedNameCaptureStopsTheRemainingPreparation(bool publicationOnly, bool returnNull)
    {
        var fixture = new CheckpointFixture();
        using var app = new FlowApplication(fixture.Runtime, action => action(fixture.Runtime), _ => { });
        Action? retire = null;
        int stationCalls = 0, itemCalls = 0;
        using var view = new ParcelExperience(Id, app, CheckpointFixture.Parcel.Value,
            stationName: _ => { stationCalls++; return "Station"; },
            localizedItemName: _ =>
            {
                itemCalls++;
                retire?.Invoke();
                return retire is not null && returnNull ? null! : Name();
            });
        Assert.True(view.Experience.Actions[0].TryExecute());
        long before = view.Publication.Version;
        retire = publicationOnly ? view.Publication.Dispose : view.Dispose;

        Assert.False(view.Pump());

        Assert.Equal(before, view.Publication.Version);
        Assert.Equal(2, stationCalls);
        Assert.Equal(2, itemCalls);
        Assert.Equal("Ready to dispatch", Value(view, "State").Format("en"));
        Assert.Equal(string.Empty, Value(view, "Result").Format("ru-RU"));
        Assert.False(view.IsActive);
        Assert.All(view.Experience.Actions, action => Assert.False(action.TryExecute()));
        Assert.Equal(ParcelState.Reserved, fixture.Runtime.GetParcel(CheckpointFixture.Parcel).State);
    }

    private static readonly UiSymbolId Id = new("Hatifect.Flow", "parcel");
    private static UiLocalizedText Name() => new("Copper ore", new Dictionary<string, string> { ["ru-RU"] = "Медная руда" });
    private static ParcelTextValue Value(ParcelExperience view, string alias)
        => Assert.IsType<ParcelTextValue>(view.Experience.Elements.Single(element => element.Alias == alias).Source.UntypedValue);
}
