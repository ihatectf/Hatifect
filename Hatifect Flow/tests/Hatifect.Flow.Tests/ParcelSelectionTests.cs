using System;
using System.Linq;
using Hatifect.Flow.Application;
using Hatifect.Flow.Application.Dispatch;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.UI.Semantic;
using Hatifect.UI;
using Hatifect.UI.Runtime.Tests;
using Xunit;

namespace Hatifect.Flow.Tests;

[Trait("Category", "flow")]
public sealed class ParcelSelectionTests
{
    [Theory]
    [InlineData("en", "No shipment selected", "Select a shipment to inspect")]
    [InlineData("ru-RU", "Отправление не выбрано", "Выберите отправление для просмотра")]
    public void EmptySessionCanPresentAnUnselectedScreenWithoutInventingCargoOrRunningCommands(
        string locale, string state, string availability)
    {
        var runtime = new FlowRuntime(new NetworkId(Guid.NewGuid()));
        int commands = 0, captures = 0;
        using var app = new FlowApplication(runtime, _ => commands++, _ => { });
        using var view = new ParcelExperience(Id, app,
            stationName: _ => { captures++; return "Unexpected"; },
            itemName: _ => { captures++; return "Unexpected"; });
        using var actionHost = new ExperienceTextProbe(view.Experience);
        actionHost.Compose("en");
        using var probe = new ExperienceTextProbe(view.Experience);
        var snapshot = app.ReadSnapshot();
        var scene = probe.Compose(locale);
        Assert.True(view.IsActive);
        AssertText(scene, "state", state);
        AssertText(scene, "availability", availability);
        AssertText(scene, "cargo", string.Empty);
        AssertText(scene, "route", string.Empty);
        Assert.All(view.Experience.Actions, action => Assert.False(actionHost.Invoke(action)));
        Assert.Equal(0, commands);
        Assert.Equal(0, captures);
        Assert.Same(snapshot, app.ReadSnapshot());
        Assert.Empty(snapshot.Parcels);
        app.Dispose();
        Assert.True(view.Pump());
        Assert.False(view.IsActive);
        Assert.All(view.Experience.Actions, action => Assert.False(actionHost.Invoke(action)));
        AssertText(scene, "state", state);
    }

    [Fact]
    public void UnselectedViewNeverAdoptsAnUnrequestedParcelAcrossOwnerRevisions()
    {
        var fixture = new CheckpointFixture();
        int effects = 0, captures = 0;
        using var app = new FlowApplication(fixture.Runtime,
            action => { effects++; action(fixture.Runtime); }, _ => { });
        using var view = new ParcelExperience(Id, app,
            itemName: _ => { captures++; return "Unexpected"; });
        using var actionHost = new ExperienceTextProbe(view.Experience);
        actionHost.Compose("en");
        using var probe = new ExperienceTextProbe(view.Experience);
        var before = probe.Compose("en");
        var actions = view.Experience.Actions.ToArray();
        var sources = view.Experience.Elements.Select(element => element.Source).ToArray();
        Assert.All(actions, action => Assert.False(actionHost.Invoke(action)));
        Assert.Equal(0, effects);
        Assert.Equal(FlowCommandStatus.Applied,
            app.Execute(FlowApplicationTests.Command(app.ReadSnapshot(), FlowParcelAction.Reserve)).Status);
        Assert.True(view.Pump());
        var after = probe.Compose("ru-RU");
        AssertText(before, "state", "No shipment selected");
        AssertText(after, "state", "Отправление не выбрано");
        AssertText(after, "cargo", string.Empty);
        Assert.Equal(actions, view.Experience.Actions);
        Assert.Equal(sources, view.Experience.Elements.Select(element => element.Source));
        Assert.All(actions, action => Assert.False(actionHost.Invoke(action)));
        Assert.Equal(1, effects);
        Assert.Equal(0, captures);
        Assert.False(view.Pump());
    }

    [Fact]
    public void ExplicitEmptyIdentityStillRejectsBeforeNameCapture()
    {
        var fixture = new CheckpointFixture();
        using var app = new FlowApplication(fixture.Runtime, action => action(fixture.Runtime), _ => { });
        int captures = 0;
        Assert.Throws<ArgumentException>(() => new ParcelExperience(Id, app, Guid.Empty,
            itemName: _ => { captures++; return "Unexpected"; }));
        Assert.Equal(0, captures);
    }

    [Theory]
    [InlineData(FlowApplicationState.Paused, "Transport is paused", "Перевозки приостановлены")]
    [InlineData(FlowApplicationState.RecoveryRequired, "Recovery required; cargo retained", "Требуется восстановление; груз сохранён")]
    public void UnselectedViewPreservesOwnerAvailabilityAndTerminalReasons(
        FlowApplicationState availability, string englishReason, string russianReason)
    {
        var fixture = new CheckpointFixture();
        int attempts = 0, reports = 0, captures = 0;
        using var app = new FlowApplication(fixture.Runtime,
            _ => { attempts++; throw new InvalidOperationException("provider failed"); }, _ => reports++);
        app.SetAvailability(availability);
        using var view = new ParcelExperience(Id, app,
            itemName: _ => { captures++; return "Unexpected"; });
        using var actionHost = new ExperienceTextProbe(view.Experience);
        actionHost.Compose("en");
        using var probe = new ExperienceTextProbe(view.Experience);
        var english = probe.Compose("en");
        var russian = probe.Compose("ru-RU");
        AssertText(english, "state", "No shipment selected");
        AssertText(english, "availability", englishReason);
        AssertText(russian, "availability", russianReason);
        Assert.All(view.Experience.Actions, action => Assert.False(actionHost.Invoke(action)));

        app.SetAvailability(FlowApplicationState.Active);
        Assert.True(view.Pump());
        var resumed = probe.Compose("en");
        AssertText(resumed, "availability", "Select a shipment to inspect");
        Assert.All(view.Experience.Actions, action => Assert.False(actionHost.Invoke(action)));
        Assert.Equal(0, attempts);

        var command = FlowApplicationTests.Command(app.ReadSnapshot(), FlowParcelAction.Reserve);
        Assert.Equal(FlowCommandStatus.Faulted, app.Execute(command).Status);
        Assert.True(view.Pump());
        Assert.True(view.IsActive);
        var faulted = probe.Compose("ru-RU");
        AssertText(faulted, "state", "Ошибка перевозки; см. журнал диагностики");
        AssertText(faulted, "availability", "Ошибка перевозки; см. журнал диагностики");
        AssertText(faulted, "result", string.Empty);
        Assert.All(view.Experience.Actions, action => Assert.False(actionHost.Invoke(action)));
        app.Dispose();
        Assert.True(view.Pump());
        Assert.False(view.IsActive);
        AssertText(probe.Compose("en"), "state", "Session closed");
        AssertText(english, "availability", englishReason);
        AssertText(russian, "availability", russianReason);
        AssertText(resumed, "availability", "Select a shipment to inspect");
        AssertText(faulted, "state", "Ошибка перевозки; см. журнал диагностики");
        Assert.Equal(1, attempts);
        Assert.Equal(1, reports);
        Assert.Equal(0, captures);
    }

    private static readonly UiSymbolId Id = new("Hatifect.Flow", "parcel");
    private static void AssertText(ExperienceTextSnapshot scene, string key, string expected)
        => Assert.Equal(expected, Assert.Single(scene.Values,
            value => value.Id == Id.Child("element/" + key).Child("scene/component")).DisplayText);
}
