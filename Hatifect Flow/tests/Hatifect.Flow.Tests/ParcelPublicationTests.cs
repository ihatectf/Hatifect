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
public sealed class ParcelPublicationTests
{
    [Fact]
    public void CommandResultAndCompleteReadModelAppearInOnePublicationBeforeEveryObserver()
    {
        using var app = new ObservedApplication();
        using var view = Create(app);
        UiPublicationView before = view.Publication.Capture();
        var observations = new List<(string State, string Result, bool Nested)>();
        foreach (var element in view.Experience.Elements)
            element.Source.Changed += () => observations.Add((Value(view, "State"), Value(view, "Result"),
                view.Experience.Actions[1].TryExecute()));

        Assert.True(view.Experience.Actions[0].TryExecute());
        Assert.Equal(before.Version, view.Publication.Version);
        Assert.Empty(observations);
        Assert.True(view.Pump());

        Assert.NotEmpty(observations);
        Assert.All(observations, value =>
        {
            Assert.Equal("Scheduled", value.State);
            Assert.Equal("Command completed", value.Result);
            Assert.False(value.Nested);
        });
        Assert.Equal(1, app.Commands);
        Assert.Equal(before.Version + 1, view.Publication.Version);
        Assert.Equal("Ready to dispatch", Read(before, Source(view, "State")));
        Assert.Equal(string.Empty, Read(before, Source(view, "Result")));
        Assert.True(view.Experience.Actions[1].CanExecute);
        Assert.Empty(view.Publication.LastResult.ObserverErrors);
        Assert.False(view.Pump());
    }

    [Fact]
    public void FailedPreparationKeepsEveryOldSourceAndPendingCommandResultForRetry()
    {
        using var app = new ObservedApplication();
        bool fail = false;
        using var view = Create(app, stationName: _ => fail ? throw new InvalidOperationException("station unavailable") : "Station");
        UiPublicationView before = view.Publication.Capture();
        object?[] values = view.Experience.Elements.Select(element => element.Source.UntypedValue).ToArray();
        int notifications = 0;
        view.Publication.Changed += () => notifications++;
        Assert.True(view.Experience.Actions[0].TryExecute());
        fail = true;

        Assert.Throws<InvalidOperationException>(() => view.Pump());

        Assert.Equal(before.Version, view.Publication.Version);
        Assert.Equal(values, view.Experience.Elements.Select(element => element.Source.UntypedValue).ToArray());
        Assert.Equal(0, notifications);
        Assert.Equal(ParcelState.Reserved, app.Inner.ReadSnapshot().Parcels.Single().State);
        fail = false;
        Assert.True(view.Pump());
        Assert.Equal("Scheduled", Value(view, "State"));
        Assert.Equal("Command completed", Value(view, "Result"));
        Assert.Equal(1, notifications);
        Assert.Equal(1, app.Commands);
        Assert.False(view.Pump());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnerReadAndCommandCallbacksCannotReenterBeforeTheEffect(bool duringExecute)
    {
        using var app = new ObservedApplication();
        using var view = Create(app);
        bool? nested = null;
        Action callback = () => nested = view.Experience.Actions[0].TryExecute();
        if (duringExecute) app.BeforeExecute = callback;
        else app.AfterRead = callback;

        Assert.True(view.Experience.Actions[0].TryExecute());

        Assert.False(nested);
        Assert.Equal(1, app.Commands);
        Assert.True(view.Pump());
        Assert.Equal("Scheduled", Value(view, "State"));
    }

    [Fact]
    public void ProjectionFormattingCannotDispatchEvenAnOtherwiseAvailableAction()
    {
        using var app = new ObservedApplication();
        ParcelExperience? current = null;
        bool? nested = null;
        using var view = Create(app, itemName: key =>
        {
            if (current is not null) nested = current.Experience.Actions[1].TryExecute();
            return key;
        });
        current = view;
        Assert.True(view.Experience.Actions[0].TryExecute());

        Assert.True(view.Pump());

        Assert.False(nested);
        Assert.Equal(1, app.Commands);
        Assert.Equal("Scheduled", Value(view, "State"));
        Assert.True(view.Experience.Actions[1].CanExecute);
    }

    [Fact]
    public void CloseDuringCommittedCommandRetainsTheDomainEffectWithoutPublishingLateUiResult()
    {
        using var app = new ObservedApplication();
        using var view = Create(app);
        UiPublicationView before = view.Publication.Capture();
        app.AfterExecute = view.Dispose;

        Assert.True(view.Experience.Actions[0].TryExecute());

        Assert.Equal(ParcelState.Reserved, app.Inner.ReadSnapshot().Parcels.Single().State);
        Assert.Equal(1, app.Commands);
        Assert.False(view.IsActive);
        Assert.False(view.Pump());
        Assert.Equal(before.Version, view.Publication.Version);
        Assert.Equal(string.Empty, Value(view, "Result"));
        Assert.All(view.Experience.Actions, action => Assert.False(action.TryExecute()));
    }

    [Fact]
    public void RetiringThePublicationDirectlyPreventsProviderReadsAndCommands()
    {
        using var app = new ObservedApplication();
        using var view = Create(app);
        int reads = app.Reads;
        view.Publication.Dispose();

        Assert.All(view.Experience.Actions, action => Assert.False(action.TryExecute()));
        Assert.False(view.IsActive);
        Assert.False(view.Pump());
        Assert.Equal(reads, app.Reads);
        Assert.Equal(0, app.Commands);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void RetirementDuringReadOrFormattingStopsRemainingCallbacksAndPublication(int phase, bool publicationOnly)
    {
        using var app = new ObservedApplication();
        Action? retire = null;
        int calls = 0;
        string Format(string value)
        {
            if (retire is not null && ++calls == phase) retire();
            return value;
        }
        using var view = Create(app, stationName: _ => Format("Station"), itemName: Format);
        UiPublicationView before = view.Publication.Capture();
        Assert.True(view.Experience.Actions[0].TryExecute());
        retire = publicationOnly ? view.Publication.Dispose : view.Dispose;
        if (phase == 0) app.AfterRead = retire;

        Assert.False(view.Pump());

        Assert.Equal(phase, calls);
        Assert.Equal(before.Version, view.Publication.Version);
        Assert.Equal("Ready to dispatch", Value(view, "State"));
        Assert.Equal(string.Empty, Value(view, "Result"));
        Assert.False(view.IsActive);
        Assert.False(view.Pump());
        Assert.Equal(1, app.Commands);
        Assert.Equal(ParcelState.Reserved, app.Inner.ReadSnapshot().Parcels.Single().State);
    }

    private static ParcelExperience Create(ObservedApplication app, Func<Guid, string>? stationName = null,
        Func<string, string>? itemName = null)
        => new(new UiSymbolId("Hatifect.Flow", "parcel"), app, CheckpointFixture.Parcel.Value,
            stationName: stationName, itemName: itemName);
    private static UiPublishedState<string> Source(ParcelExperience view, string name)
        => Assert.IsType<UiPublishedState<string>>(view.Experience.Elements.Single(element => element.Name == name).Source);
    private static string Value(ParcelExperience view, string name) => Source(view, name).Value;
    private static string Read(UiPublicationView view, UiPublishedState<string> source)
        => Assert.IsAssignableFrom<IUiSemanticSource<string>>(view.Read(source)).Value;

    private sealed class ObservedApplication : IFlowApplication, IDisposable
    {
        internal readonly CheckpointFixture Fixture = new();
        internal readonly FlowApplication Inner;
        internal int Reads, Commands;
        internal Action? AfterRead, BeforeExecute, AfterExecute;
        internal ObservedApplication() => Inner = new(Fixture.Runtime, action => action(Fixture.Runtime), _ => { });
        public event Action<long>? RevisionChanged { add => Inner.RevisionChanged += value; remove => Inner.RevisionChanged -= value; }
        public FlowSnapshot ReadSnapshot()
        {
            Reads++;
            FlowSnapshot snapshot = Inner.ReadSnapshot();
            Action? callback = AfterRead;
            AfterRead = null;
            callback?.Invoke();
            return snapshot;
        }
        public FlowCommandResult Execute(FlowParcelCommand command)
        {
            Commands++;
            BeforeExecute?.Invoke();
            FlowCommandResult result = Inner.Execute(command);
            AfterExecute?.Invoke();
            return result;
        }
        public void Dispose() => Inner.Dispose();
    }
}
