using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.Flow.Application;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Application.Dispatch;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Policies;
using Hatifect.Flow.Domain.Ports;
using Hatifect.Flow.UI.Semantic;
using Hatifect.UI;
using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Tests;
using Xunit;

namespace Hatifect.Flow.Tests;

[Trait("Category", "flow")]
public sealed class ParcelLocaleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedParcelReformatsLabelsAndCommittedResultWithoutPublishingOrRepeatingCommand(bool russianFallback)
    {
        var runtime = CreateRuntime();
        int commands = 0, names = 0;
        using var app = new FlowApplication(runtime, action => { commands++; action(runtime); }, _ => { });
        using var view = new ParcelExperience(Id, app, Parcel.Value, russianFallback,
            stationName: station => { names++; return station == Origin.Value ? "Mine" : "Farm"; },
            itemName: _ => { names++; return "Copper ore"; });
        var experience = view.Experience;
        var sources = experience.Elements.Select(element => element.Source).ToArray();
        var actions = experience.Actions.ToArray();
        var host = new ExperienceTextProbe(experience);
        host.Compose("en");
        try
        {
            Assert.True(actions[0].TryExecute());
            Assert.True(view.Pump());
            long publication = view.Publication.Version;
            FlowSnapshot snapshot = app.ReadSnapshot();
            int capturedNames = names;
            foreach (string locale in new[] { "en", "ru-RU", "en" })
            {
                ExperienceTextSnapshot scene = host.Compose(locale);
                bool ru = locale == "ru-RU";
                Assert.Same(experience, host.ActiveExperience);
                Assert.Equal(ru ? "Отправление Flowline · диагностика" : "Flowline shipment · diagnostic", scene.DisplayName);
                AssertValue(scene, "cargo", ru ? "Груз" : "Cargo", "Copper ore × 7");
                AssertValue(scene, "route", ru ? "Маршрут" : "Route", "Mine → Farm");
                AssertValue(scene, "state", ru ? "Состояние" : "State", ru ? "Запланировано" : "Scheduled");
                AssertValue(scene, "result", ru ? "Результат" : "Result", ru ? "Команда выполнена" : "Command completed");
                var availability = Value(scene, "availability");
                Assert.Equal(ru ? "Доступность" : "Availability", availability.SemanticName);
                Assert.Contains(ru ? "Отправка: Недоступно на этой стадии отправления" : "Dispatch: Unavailable at this shipment stage", availability.DisplayText);
                string[] titles = ru
                    ? new[] { "Отправить", "Отменить", "Повторить доставку", "Проверить передачу", "Вернуть груз в источник" }
                    : new[] { "Dispatch", "Cancel", "Retry delivery", "Check transfer", "Return cargo to source" };
                var buttons = scene.Actions;
                Assert.Equal(titles, buttons.Select(button => button.Label));
                for (int i = 0; i < actions.Length; i++) Assert.Same(actions[i], buttons[i].Action);
                var accessible = scene.Accessibility;
                foreach (var value in scene.Values)
                    Assert.Contains(accessible, node => node.Id == value.Id && node.Name == value.SemanticName && node.Value == value.DisplayText);
                Assert.Equal(scene.DisplayName, scene.Accessibility[0].Name);
                Assert.Equal(publication, view.Publication.Version);
                Assert.Same(snapshot, app.ReadSnapshot());
                Assert.Equal(capturedNames, names);
                Assert.Equal(1, commands);
                Assert.False(view.Pump());
                Assert.Equal(sources, experience.Elements.Select(element => element.Source));
            }
            Assert.Equal(1, host.Activations);
            Assert.Equal(ParcelState.Reserved, runtime.GetParcel(Parcel).State);
            Assert.True(actions[1].CanExecute);
        }
        finally { host.Dispose(); }
    }

    [Theory]
    [InlineData(false, "ru", true)]
    [InlineData(true, "en-US", false)]
    [InlineData(false, "RU-ru", false)]
    [InlineData(true, "RU-ru", true)]
    [InlineData(false, "custom-locale", false)]
    [InlineData(true, "custom-locale", true)]
    public void ExplicitLocaleAliasesAndUnknownLocaleFallbackApplyToTheWholeParcel(bool fallback, string locale, bool russian)
    {
        var runtime = CreateRuntime();
        using var app = new FlowApplication(runtime, action => action(runtime), _ => { });
        using var view = new ParcelExperience(Id, app, Parcel.Value, fallback);
        using var probe = new ExperienceTextProbe(view.Experience);
        var scene = probe.Compose(locale);
        Assert.Equal(russian ? "Отправление Flowline · диагностика" : "Flowline shipment · diagnostic", scene.DisplayName);
        AssertValue(scene, "state", russian ? "Состояние" : "State", russian ? "Готово к отправке" : "Ready to dispatch");
        AssertValue(scene, "route", russian ? "Маршрут" : "Route", russian ? "Станция → Станция" : "Station → Station");
        Assert.Equal(russian ? "Отправить" : "Dispatch", scene.Actions.First().Label);
        Assert.False(view.Pump());
    }

    [Fact]
    public void LocaleChangesUseTheCapturedItemTranslationsWithoutCallingTheNameAdapterAgain()
    {
        var runtime = CreateRuntime();
        using var app = new FlowApplication(runtime, action => action(runtime), _ => { });
        var translations = new Dictionary<string, string> { ["ru-RU"] = "Медная руда" };
        int captures = 0;
        using var view = new ParcelExperience(Id, app, Parcel.Value,
            localizedItemName: _ => { captures++; return new UiLocalizedText("Copper ore", translations); });
        using var probe = new ExperienceTextProbe(view.Experience);
        var compose = probe.Compose;
        long version = view.Publication.Version;
        FlowSnapshot snapshot = app.ReadSnapshot();
        translations["ru-RU"] = "Changed external dictionary";
        foreach (string locale in new[] { "en", "ru-RU", "en" })
        {
            var scene = compose(locale);
            bool russian = locale == "ru-RU";
            AssertValue(scene, "cargo", russian ? "Груз" : "Cargo", russian ? "Медная руда × 7" : "Copper ore × 7");
            AssertValue(scene, "route", russian ? "Маршрут" : "Route", russian ? "Станция → Станция" : "Station → Station");
            Assert.Equal(1, captures);
            Assert.Equal(version, view.Publication.Version);
            Assert.Same(snapshot, app.ReadSnapshot());
        }
    }

    [Fact]
    public void RejectedCommandReformatsItsOriginalReasonWithoutAnotherProviderAttempt()
    {
        var runtime = CreateRuntime();
        int effects = 0, admissionChecks = 0;
        using var app = new FlowApplication(runtime, action => { effects++; action(runtime); }, _ => { },
            () => { admissionChecks++; return false; });
        using var view = new ParcelExperience(Id, app, Parcel.Value);
        using var probe = new ExperienceTextProbe(view.Experience);
        var compose = probe.Compose;
        var before = compose("en");
        var snapshot = app.ReadSnapshot();
        Assert.True(view.Experience.Actions[0].TryExecute());
        Assert.True(view.Pump());
        long publication = view.Publication.Version;
        foreach (string locale in new[] { "en", "ru-RU", "en" })
        {
            bool russian = locale == "ru-RU";
            var scene = compose(locale);
            AssertValue(scene, "result", russian ? "Результат" : "Result", russian
                ? "Владелец инвентаря временно недоступен" : "The inventory owner is temporarily unavailable");
            AssertValue(scene, "state", russian ? "Состояние" : "State", russian ? "Готово к отправке" : "Ready to dispatch");
            Assert.Equal(publication, view.Publication.Version);
            Assert.Same(snapshot, app.ReadSnapshot());
            Assert.Equal(1, admissionChecks);
            Assert.Equal(0, effects);
            Assert.False(view.Pump());
        }
        AssertValue(before, "result", "Result", string.Empty);
        AssertValue(before, "state", "State", "Ready to dispatch");
        Assert.Equal(ParcelState.Created, runtime.GetParcel(Parcel).State);
    }

    [Fact]
    public void CapturedParcelScenesKeepTheirOwnCompletePublicationAfterALaterCommand()
    {
        var runtime = CreateRuntime();
        using var app = new FlowApplication(runtime, action => action(runtime), _ => { });
        using var view = new ParcelExperience(Id, app, Parcel.Value);
        using var probe = new ExperienceTextProbe(view.Experience);
        var compose = probe.Compose;
        var englishBefore = compose("en");
        var russianBefore = compose("ru-RU");
        Assert.True(view.Experience.Actions[0].TryExecute());
        Assert.True(view.Pump());
        var russianAfter = compose("ru-RU");
        AssertValue(englishBefore, "state", "State", "Ready to dispatch");
        AssertValue(englishBefore, "result", "Result", string.Empty);
        AssertValue(russianBefore, "state", "Состояние", "Готово к отправке");
        AssertValue(russianBefore, "result", "Результат", string.Empty);
        AssertValue(russianAfter, "state", "Состояние", "Запланировано");
        AssertValue(russianAfter, "result", "Результат", "Команда выполнена");
        Assert.Equal("Flowline shipment · diagnostic", englishBefore.DisplayName);
    }

    private static readonly UiSymbolId Id = new("Hatifect.Flow", "parcel");
    private static ExperienceTextValue Value(ExperienceTextSnapshot scene, string key)
        => Assert.Single(scene.Values, value => value.Id == Id.Child("element/" + key).Child("scene/component"));
    private static void AssertValue(ExperienceTextSnapshot scene, string key, string label, string text)
    {
        var value = Value(scene, key);
        Assert.Equal(label, value.SemanticName);
        Assert.Equal(text, value.DisplayText);
    }
    private static readonly StationId Origin = new(new Guid("00000000-0000-0000-0000-000000000011"));
    private static readonly StationId Destination = new(new Guid("00000000-0000-0000-0000-000000000022"));
    private static readonly ParcelId Parcel = new(new Guid("00000000-0000-0000-0000-000000000033"));
    private static FlowRuntime CreateRuntime()
    {
        var runtime = new FlowRuntime(new NetworkId(new Guid("00000000-0000-0000-0000-000000000044")));
        var source = new InMemoryCargoPort();
        var cargo = new CargoId(new Guid("00000000-0000-0000-0000-000000000055"));
        var shipment = new ShipmentId(new Guid("00000000-0000-0000-0000-000000000066"));
        var manifest = new CargoManifest("ore", 7);
        source.Seed(cargo, manifest);
        runtime.AddStation(Origin, source);
        runtime.AddStation(Destination, new InMemoryCargoPort());
        runtime.AddLink(new LinkId(new Guid("00000000-0000-0000-0000-000000000077")), Origin, Destination, 20, 3);
        runtime.RegisterCargo(cargo, Origin, manifest);
        runtime.CreateShipment(shipment, Origin, Destination, manifest, new ServicePolicy(ServiceClass.Standard, DeliveryGuarantee.Flexible));
        runtime.SplitShipment(shipment, Parcel, cargo);
        return runtime;
    }
}
