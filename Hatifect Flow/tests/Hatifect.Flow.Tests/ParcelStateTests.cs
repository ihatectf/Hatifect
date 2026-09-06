using System;
using System.Linq;
using Hatifect.Flow.Application;
using Hatifect.Flow.Application.Dispatch;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.UI.Semantic;
using Hatifect.UI;
using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Tests;
using Xunit;

namespace Hatifect.Flow.Tests;

[Trait("Category", "flow")]
public sealed class ParcelStateTests
{
    [Theory]
    [InlineData("en", "Shipment is no longer available")]
    [InlineData("ru-RU", "Отправление больше недоступно")]
    public void EmptySessionRemainsVisibleWithLocalizedReasonUntilItsOwnerCloses(string locale, string reason)
    {
        var runtime = new FlowRuntime(new NetworkId(Guid.NewGuid()));
        int commands = 0, names = 0;
        using var owner = new FlowApplication(runtime, _ => commands++, _ => { });
        var app = new ObservedApplication(owner);
        using var view = new ParcelExperience(Id, app, CheckpointFixture.Parcel.Value,
            stationName: _ => { names++; return "Unexpected station"; },
            itemName: _ => { names++; return "Unexpected item"; });
        using var api = new SurfaceProbe(locale);
        using var surface = new ParcelSurface(view);
        surface.Show(api);

        surface.Pump();

        Assert.False(surface.IsClosed);
        Assert.True(view.IsActive);
        Assert.True(api.Visible);
        Assert.Equal(1, app.Subscribers);
        AssertText(api.Current!, "state", reason);
        AssertText(api.Current!, "availability", reason);
        AssertText(api.Current!, "cargo", string.Empty);
        AssertText(api.Current!, "route", string.Empty);
        Assert.All(view.Experience.Actions, action => Assert.False(action.TryExecute()));
        Assert.Equal(0, commands);
        Assert.Equal(0, names);
        var oldScene = api.Current!;
        owner.Dispose();
        surface.Pump();
        Assert.True(surface.IsClosed);
        Assert.False(api.Visible);
        Assert.Equal(0, app.Subscribers);
        Assert.Equal(1, api.Disposals);
        AssertText(oldScene, "state", reason);
    }

    [Theory]
    [InlineData(FlowApplicationState.Paused, "Transport is paused", "Перевозки приостановлены")]
    [InlineData(FlowApplicationState.RecoveryRequired, "Recovery required; cargo retained", "Требуется восстановление; груз сохранён")]
    public void EmptyParcelRetainsTheOwningPauseOrRecoveryReasonUntilTheOwnerResumes(
        FlowApplicationState state, string englishReason, string russianReason)
    {
        var runtime = new FlowRuntime(new NetworkId(Guid.NewGuid()));
        int commands = 0;
        using var owner = new FlowApplication(runtime, _ => commands++, _ => { });
        owner.SetAvailability(state);
        using var view = new ParcelExperience(Id, owner, CheckpointFixture.Parcel.Value);
        using var api = new SurfaceProbe("en");
        using var surface = new ParcelSurface(view);
        surface.Show(api);
        foreach (string locale in new[] { "en", "ru-RU" })
        {
            api.Locale = locale;
            surface.Pump();
            Assert.False(surface.IsClosed);
            AssertText(api.Current!, "state", locale == "en" ? "Shipment is no longer available" : "Отправление больше недоступно");
            AssertText(api.Current!, "availability", locale == "en" ? englishReason : russianReason);
            Assert.All(view.Experience.Actions, action => Assert.False(action.TryExecute()));
        }
        owner.SetAvailability(FlowApplicationState.Active);
        surface.Pump();
        AssertText(api.Current!, "availability", "Отправление больше недоступно");
        Assert.All(view.Experience.Actions, action => Assert.False(action.TryExecute()));
        Assert.Equal(0, commands);
        Assert.False(surface.IsClosed);
    }

    [Fact]
    public void MissingParcelClearsCurrentFactsAndRefillRetainsIdentityWithoutReplayingActions()
    {
        using var owner = CreateOwner();
        var app = new SnapshotApplication(owner.ReadSnapshot());
        int names = 0;
        using var view = new ParcelExperience(Id, app, CheckpointFixture.Parcel.Value,
            itemName: _ => { names++; return "Copper ore"; });
        using var api = new SurfaceProbe("en");
        using var surface = new ParcelSurface(view);
        surface.Show(api);
        var initial = api.Current!;
        var sources = view.Experience.Elements.Select(element => element.Source).ToArray();
        var actions = view.Experience.Actions.ToArray();
        var full = app.ReadSnapshot();
        app.Publish(new FlowSnapshot(full.SessionId, full.NetworkId, full.Revision + 1,
            FlowApplicationState.Active, full.Stations.ToArray(), full.Links.ToArray(), Array.Empty<FlowParcelSnapshot>()));

        surface.Pump();

        Assert.False(surface.IsClosed);
        Assert.Equal(1, api.Refreshes);
        Assert.Equal(1, names);
        var empty = api.Current!;
        AssertText(empty, "state", "Shipment is no longer available");
        AssertText(empty, "availability", "Shipment is no longer available");
        AssertText(empty, "cargo", string.Empty);
        AssertText(empty, "route", string.Empty);
        Assert.All(actions, action => Assert.False(action.TryExecute()));
        Assert.Equal(0, app.Commands);
        AssertText(initial, "cargo", "Copper ore × 7");
        app.Publish(new FlowSnapshot(full.SessionId, full.NetworkId, full.Revision + 2,
            FlowApplicationState.Active, full.Stations.ToArray(), full.Links.ToArray(), full.Parcels.ToArray()));
        surface.Pump();
        Assert.Equal(2, api.Refreshes);
        AssertText(api.Current!, "cargo", "Copper ore × 7");
        AssertText(api.Current!, "state", "Ready to dispatch");
        AssertText(empty, "cargo", string.Empty);
        Assert.Equal(sources, view.Experience.Elements.Select(element => element.Source));
        Assert.Equal(actions, view.Experience.Actions);
        Assert.True(actions[0].CanExecute);
        Assert.Equal(0, app.Commands);
        Assert.Equal(2, names);
        surface.Dispose();
        Assert.Equal(0, app.Subscribers);
        Assert.All(actions, action => Assert.False(action.TryExecute()));
    }

    [Fact]
    public void FaultedCommandRemainsVisibleUntilOwnerDisposalAndTranslatesWithoutAnotherAttempt()
    {
        var fixture = new CheckpointFixture();
        int commands = 0, errors = 0;
        using var owner = new FlowApplication(fixture.Runtime,
            _ => { commands++; throw new InvalidOperationException("provider failed"); }, _ => errors++);
        var app = new ObservedApplication(owner);
        using var view = new ParcelExperience(Id, app, CheckpointFixture.Parcel.Value);
        using var api = new SurfaceProbe("en");
        using var surface = new ParcelSurface(view);
        surface.Show(api);
        Assert.True(view.Experience.Actions[0].TryExecute());
        surface.Pump();

        Assert.Equal(FlowApplicationState.Faulted, owner.ReadSnapshot().State);
        Assert.False(surface.IsClosed);
        Assert.True(view.IsActive);
        long version = view.Publication.Version;
        foreach (string locale in new[] { "en", "ru-RU", "en" })
        {
            api.Locale = locale;
            surface.Pump();
            string reason = locale == "en" ? "Transport failed; see its diagnostic log" : "Ошибка перевозки; см. журнал диагностики";
            AssertText(api.Current!, "state", reason);
            AssertText(api.Current!, "availability", reason);
            AssertText(api.Current!, "result", reason);
            AssertText(api.Current!, "cargo", string.Empty);
            AssertText(api.Current!, "route", string.Empty);
            Assert.All(view.Experience.Actions, action => Assert.False(action.TryExecute()));
            Assert.Equal(version, view.Publication.Version);
            Assert.Equal(1, commands);
            Assert.Equal(1, errors);
            Assert.True(api.Visible);
        }
        owner.Dispose();
        surface.Pump();
        Assert.True(surface.IsClosed);
        Assert.False(view.IsActive);
        Assert.Equal(FlowApplicationState.Closed, owner.ReadSnapshot().State);
        Assert.Equal(0, app.Subscribers);
        Assert.False(api.Visible);
        Assert.All(view.Experience.Actions, action => Assert.False(action.TryExecute()));
        Assert.Equal(1, commands);
    }

    private static readonly UiSymbolId Id = new("Hatifect.Flow", "parcel");
    private static FlowApplication CreateOwner()
    {
        var fixture = new CheckpointFixture();
        return new FlowApplication(fixture.Runtime, action => action(fixture.Runtime), _ => { });
    }
    private static void AssertText(ExperienceTextSnapshot scene, string key, string expected)
        => Assert.Equal(expected, Assert.Single(scene.Values,
            value => value.Id == Id.Child("element/" + key).Child("scene/component")).DisplayText);

    private sealed class ObservedApplication : IFlowApplication
    {
        private readonly IFlowApplication _inner;
        internal ObservedApplication(IFlowApplication inner) => _inner = inner;
        internal int Subscribers { get; private set; }
        public event Action<long>? RevisionChanged
        {
            add { _inner.RevisionChanged += value; Subscribers++; }
            remove { _inner.RevisionChanged -= value; Subscribers--; }
        }
        public FlowSnapshot ReadSnapshot() => _inner.ReadSnapshot();
        public FlowCommandResult Execute(FlowParcelCommand command) => _inner.Execute(command);
    }

    private sealed class SnapshotApplication : IFlowApplication
    {
        private FlowSnapshot _snapshot;
        private Action<long>? _changed;
        internal SnapshotApplication(FlowSnapshot snapshot) => _snapshot = snapshot;
        internal int Subscribers { get; private set; }
        internal int Commands { get; private set; }
        public event Action<long>? RevisionChanged
        {
            add { _changed += value; Subscribers++; }
            remove { _changed -= value; Subscribers--; }
        }
        public FlowSnapshot ReadSnapshot() => _snapshot;
        public FlowCommandResult Execute(FlowParcelCommand command)
        { Commands++; throw new InvalidOperationException("No command should be executed by this fixture."); }
        internal void Publish(FlowSnapshot snapshot) { _snapshot = snapshot; _changed?.Invoke(snapshot.Revision); }
    }

    // The consumer owns the lifetime. Each refresh/synchronization still observes the real compositor and host.
    private sealed class SurfaceProbe : IUiSemanticSurfaceApi, IUiSemanticSurfaceSession
    {
        private ExperienceTextProbe? _probe;
        private bool _disposed;
        internal SurfaceProbe(string locale) => Locale = locale;
        internal string Locale { get; set; }
        internal ExperienceTextSnapshot? Current { get; private set; }
        internal int Refreshes { get; private set; }
        internal int Disposals { get; private set; }
        public int ApiVersion => 1;
        public IUiSemanticSurfaceAutomation Automation => throw new NotSupportedException();
        public bool Visible { get; private set; }
        public event Action? Closed;
        public event Action? Rendered { add { } remove { } }
        public IUiSemanticSurfaceSession CreateActiveMenuOverlay(UiExperienceDefinition experience, UiSemanticSurfaceOptions options)
        { _probe = new ExperienceTextProbe(experience); return this; }
        public void Show() { Visible = true; Compose(); }
        public void Hide() { Visible = false; Closed?.Invoke(); }
        public void Configure(UiSemanticSurfaceOptions options) { }
        public void Refresh() { Refreshes++; Compose(); }
        public void Synchronize() => Compose();
        private void Compose() => Current = _probe!.Compose(Locale);
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disposals++;
            _probe?.Dispose();
            Hide();
        }
    }
}
