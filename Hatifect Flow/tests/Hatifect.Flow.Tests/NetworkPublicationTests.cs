using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.Flow.Application;
using Hatifect.Flow.UI.Semantic;
using Hatifect.UI;
using Hatifect.UI.Experience;
using Xunit;

namespace Hatifect.Flow.Tests;

[Trait("Category", "flow")]
public sealed class NetworkPublicationTests
{
    [Fact]
    public void DomainProjectionPublishesCandidateNamesStatusAndHistoryTogether()
    {
        using var app = new ObservedNetwork();
        using var view = Create(app);
        var observations = new List<(string Status, string Station, string Link, string History)>();
        foreach (var element in view.Experience.Elements)
            element.Source.Changed += () => observations.Add((Text(view, "Transport"),
                Collection<FlowStationDetails>(view, "Source station").GetItem(0).Label,
                Collection<FlowLinkSnapshot>(view, "Links").GetItem(0).Label,
                Collection<FlowParcelSnapshot>(view, "Shipments").GetItem(0).SupportingText!));
        UiPublicationView before = view.Publication.Capture();
        app.Prefix = "New ";
        app.Inner.Application.SetAvailability(FlowApplicationState.Paused);

        Assert.True(view.Pump());

        Assert.NotEmpty(observations);
        Assert.All(observations, item =>
        {
            Assert.Equal("Paused", item.Status);
            Assert.Equal("New Mine", item.Station);
            Assert.Equal("New Mine → New Farm", item.Link);
            Assert.StartsWith("New Mine → New Farm", item.History);
        });
        Assert.Equal(before.Version + 1, view.Publication.Version);
        Assert.Empty(view.Publication.LastResult.ObserverErrors);
    }

    [Fact]
    public void QuerySelectionPayloadAndDetailsAreConsistentFromTheFirstCallback()
    {
        using var app = new ObservedNetwork();
        using var view = Create(app);
        var history = Collection<FlowParcelSnapshot>(view, "Shipments");
        Assert.True(history.TrySelect(history.GetItem(0).Id));
        var query = Source<FlowTextSource>(view, "Find shipments");
        var payload = Assert.IsType<UiPublishedState<FlowParcelSnapshot?>>(view.Experience.Sources.Single(source => source.Alias == "ShipmentPayload").Source);
        var observations = new List<(string Query, int Count, UiSymbolId? Selection, FlowParcelSnapshot? Payload, string Details)>();
        foreach (var source in view.Experience.Sources)
            source.Source.Changed += () => observations.Add((query.Value, history.Count, history.SelectedItemId, payload.Value, Text(view, "Selected shipment")));

        query.Value = "no matching cargo";

        Assert.NotEmpty(observations);
        Assert.All(observations, item =>
        {
            Assert.Equal("no matching cargo", item.Query);
            Assert.Equal(0, item.Count);
            Assert.Null(item.Selection);
            Assert.Null(item.Payload);
            Assert.Equal("Select a shipment", item.Details);
        });
        query.Value = "Mine";
        Assert.NotNull(history.SelectedItemId);
        Assert.Same(history.Value[0], payload.Value);
        Assert.Empty(view.Publication.LastResult.ObserverErrors);
    }

    [Fact]
    public void SelectionAndFormEditsPublishTheirDependenciesBeforeCallbacks()
    {
        using var app = new ObservedNetwork();
        using var view = Create(app);
        var source = Collection<FlowStationDetails>(view, "Source station");
        var inventory = Collection<FlowInventorySlot>(view, "Source cargo");
        var form = Source<UiFormState>(view, "Station details");
        var inventoryObservations = new List<(UiSymbolId? Source, int Count)>();
        source.Changed += () => inventoryObservations.Add((source.SelectedItemId, inventory.Count));
        Assert.True(source.TrySelect(source.GetItem(0).Id));
        Assert.Equal((source.SelectedItemId, 1), Assert.Single(inventoryObservations));
        var formObservations = new List<(string Value, string? Error, bool Valid)>();
        form.Changed += () => formObservations.Add((form.Fields[0].Value.Value, form.Fields[0].ValidationMessage!.Value, form.IsValid));

        form.Fields[0].Value.Value = "Orchard";

        Assert.NotEmpty(formObservations);
        Assert.All(formObservations, item => Assert.Equal(("Orchard", (string?)null, true), item));
        Assert.True(Action(view, "Bind new station").CanExecute);
        Assert.Empty(view.Publication.LastResult.ObserverErrors);
    }

    [Theory]
    [InlineData("recovery")]
    [InlineData("route")]
    public void FailedSupplementaryReadPreservesCompleteViewAndRetriesWithoutNewEvent(string read)
    {
        using var app = new ObservedNetwork();
        using var view = Create(app);
        UiPublicationView before = view.Publication.Capture();
        app.Inner.Application.SetAvailability(FlowApplicationState.Paused);
        app.Callback(read, () => throw new InvalidOperationException("read failed"));

        Assert.Throws<InvalidOperationException>(() => view.Pump());
        Assert.Same(before, view.Publication.Capture());
        Assert.True(view.Pump());
        Assert.Equal("Paused", Text(view, "Transport"));
        Assert.False(view.Pump());
    }

    [Theory]
    [InlineData("recovery")]
    [InlineData("route")]
    [InlineData("snapshot")]
    public void RevisionDuringSupplementaryReadRejectsMixedCandidateAndRemainsPending(string read)
    {
        using var app = new ObservedNetwork();
        using var view = Create(app);
        UiPublicationView before = view.Publication.Capture();
        app.Inner.Application.Refresh(force: true);
        app.Callback(read, () => app.Inner.Application.SetAvailability(FlowApplicationState.Paused));

        Assert.False(view.Pump());
        Assert.Same(before, view.Publication.Capture());
        Assert.True(view.Pump());
        Assert.Equal("Paused", Text(view, "Transport"));
        Assert.False(view.Pump());
    }

    [Fact]
    public void SendResultInventorySelectionAndQuantityValidationCommitTogether()
    {
        using var app = new ObservedNetwork();
        using var view = Create(app);
        SelectRoute(view);
        var inventory = Collection<FlowInventorySlot>(view, "Source cargo");
        inventory.TrySelect(inventory.GetItem(0).Id);
        var form = Source<UiFormState>(view, "Quantity");
        form.Fields[0].Value.Value = "99";
        Assert.False(form.IsValid);
        var observations = new List<(string Result, int Count, UiSymbolId? Selected, string? Error, bool Nested)>();
        foreach (var element in view.Experience.Elements)
            element.Source.Changed += () => observations.Add((Text(view, "Result"), inventory.Count, inventory.SelectedItemId,
                form.Fields[0].ValidationMessage!.Value, Action(view, "Send whole stack").TryExecute()));

        Assert.True(Action(view, "Send whole stack").TryExecute());

        Assert.Equal(1, app.Commands);
        Assert.Null(app.Inner.SendCommand!.Quantity);
        Assert.Equal(new string('A', 64), app.Inner.SendCommand.Fingerprint);
        Assert.NotEmpty(observations);
        Assert.All(observations, item => Assert.Equal(("Shipment created", 0, (UiSymbolId?)null, (string?)"Select source cargo", false), item));
        Assert.Empty(view.Publication.LastResult.ObserverErrors);
    }

    [Fact]
    public void ExplicitInventoryRefreshFailureKeepsIntentForNextPump()
    {
        using var app = new ObservedNetwork();
        using var view = Create(app);
        SelectRoute(view);
        UiPublicationView before = view.Publication.Capture();
        app.InventoryQuantity = 5;
        app.Callback("inventory", () => throw new InvalidOperationException("inventory failed"));

        Assert.Throws<InvalidOperationException>(() => Action(view, "Refresh cargo").TryExecute());
        Assert.Same(before, view.Publication.Capture());
        Assert.True(view.Pump());
        Assert.Equal(5, Collection<FlowInventorySlot>(view, "Source cargo").Value[0].Quantity);
        Assert.False(view.Pump());
    }

    [Theory]
    [InlineData("inventory")]
    [InlineData("route")]
    [InlineData("recovery")]
    public void ReadCallbacksCannotDispatchOrChangeAnotherSelection(string read)
    {
        using var app = new ObservedNetwork();
        using var view = Create(app);
        SelectRoute(view);
        var inventory = Collection<FlowInventorySlot>(view, "Source cargo");
        inventory.TrySelect(inventory.GetItem(0).Id);
        var destinations = Collection<FlowStationDetails>(view, "Destination station");
        UiSymbolId? selected = destinations.SelectedItemId;
        bool? command = null, selection = null;
        app.Callback(read, () =>
        {
            command = Action(view, "Send whole stack").TryExecute();
            selection = destinations.TrySelect(destinations.GetItem(0).Id);
        });

        Assert.True(Action(view, "Refresh cargo").TryExecute());

        Assert.False(command);
        Assert.False(selection);
        Assert.Equal(0, app.Commands);
        Assert.Equal(selected, destinations.SelectedItemId);
    }

    [Fact]
    public void RemovedSourceClearsCachedInventoryWithoutReadingPhysicalSlots()
    {
        using var app = new ObservedNetwork();
        using var view = Create(app);
        SelectRoute(view);
        var inventory = Collection<FlowInventorySlot>(view, "Source cargo");
        inventory.TrySelect(inventory.GetItem(0).Id);
        int reads = app.InventoryReads;
        app.RemoveOrigin = true;
        app.Inner.Application.Refresh(force: true);

        Assert.True(view.Pump());

        Assert.Null(Collection<FlowStationDetails>(view, "Source station").SelectedItemId);
        Assert.Empty(inventory.Value);
        Assert.Null(inventory.SelectedItemId);
        Assert.False(Source<UiFormState>(view, "Quantity").IsValid);
        Assert.Equal(reads, app.InventoryReads);
    }

    [Fact]
    public void ALocalEditCannotConsumeAPendingInventoryRefreshWithoutReadingTheInventory()
    {
        using var app = new ObservedNetwork();
        using var view = Create(app);
        SelectRoute(view);
        app.InventoryQuantity = 5;
        app.Callback("inventory", () => throw new InvalidOperationException("inventory failed"));
        Assert.Throws<InvalidOperationException>(() => Action(view, "Refresh cargo").TryExecute());
        int reads = app.InventoryReads;

        Source<UiFormState>(view, "Station details").Fields[0].Value.Value = "Orchard";

        Assert.Equal(reads + 1, app.InventoryReads);
        Assert.Equal(5, Collection<FlowInventorySlot>(view, "Source cargo").Value[0].Quantity);
        Assert.True(Source<UiFormState>(view, "Station details").IsValid);
        Assert.False(view.Pump());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NotificationWithinActionReadCannotReachExecuteWithTheCapturedStaleSnapshot(bool secondRead)
    {
        using var app = new ObservedNetwork();
        using var view = Create(app);
        SelectRoute(view);
        var inventory = Collection<FlowInventorySlot>(view, "Source cargo");
        inventory.TrySelect(inventory.GetItem(0).Id);
        Action invalidate = () => app.Inner.Application.Refresh(force: true);
        app.Callback("snapshot", secondRead ? () => app.Callback("snapshot", invalidate) : invalidate);

        Assert.Equal(secondRead, Action(view, "Send whole stack").TryExecute());

        Assert.Equal(0, app.Commands);
        Assert.Null(app.Inner.SendCommand);
        Assert.Equal(!secondRead, view.Pump());
        Assert.Equal(8, inventory.Value[0].Quantity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextIntentSurvivesPendingDomainRevisionAndAnotherRevisionDuringPreparation(bool duringRead)
    {
        using var app = new ObservedNetwork();
        using var view = Create(app);
        app.Inner.Application.Refresh(force: true);
        var query = Source<FlowTextSource>(view, "Find shipments");
        if (duringRead) app.Callback("route", () => app.Inner.Application.Refresh(force: true));

        query.Value = "no matching cargo";
        if (duringRead)
        {
            Assert.Equal("", query.Value);
            Assert.True(view.Pump());
        }

        Assert.Equal("no matching cargo", query.Value);
        Assert.Empty(Collection<FlowParcelSnapshot>(view, "Shipments").Value);
        Assert.False(view.Pump());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANewFilterResetsAnEarlierPendingPageOnlyWhenItsCandidateCommits(bool rejected)
    {
        using var app = new ObservedNetwork();
        for (int index = 2; index <= 27; index++) app.Inner.Fixture.AddBatch(index);
        app.Inner.Application.Refresh();
        using var view = Create(app);
        var history = Collection<FlowParcelSnapshot>(view, "Shipments");
        UiSymbolId first = history.GetItem(0).Id;
        app.Callback("recovery", () => throw new InvalidOperationException("read failed"));
        Assert.Throws<InvalidOperationException>(() => Action(view, "Next page").TryExecute());
        var filter = Collection<string>(view, "Shipment filter");

        if (rejected) app.Callback("route", () => app.Inner.Application.Refresh(force: true));
        Assert.Equal(!rejected, filter.TrySelect(filter.GetItem(1).Id));

        Assert.StartsWith("1 /", Text(view, "Page"));
        Assert.Equal(first, history.GetItem(0).Id);
        Assert.True(view.Pump());
        Assert.StartsWith(rejected ? "2 /" : "1 /", Text(view, "Page"));
        if (rejected) Assert.NotEqual(first, history.GetItem(0).Id);
    }

    [Theory]
    [InlineData("network", false)]
    [InlineData("recovery", false)]
    [InlineData("inventory", false)]
    [InlineData("route", false)]
    [InlineData("snapshot", false)]
    [InlineData("network", true)]
    [InlineData("recovery", true)]
    [InlineData("inventory", true)]
    [InlineData("route", true)]
    [InlineData("snapshot", true)]
    public void RetirementDuringOwnerReadStopsRemainingReadsAndPublication(string phase, bool publicationOnly)
    {
        using var app = new ObservedNetwork();
        using var view = Create(app);
        SelectRoute(view);
        UiPublicationView before = view.Publication.Capture();
        int atRetirement = -1;
        app.ReadLog.Clear();
        app.Callback(phase, () =>
        {
            atRetirement = app.ReadLog.Count;
            if (publicationOnly) view.Publication.Dispose(); else view.Dispose();
        });

        Assert.True(Action(view, "Refresh cargo").TryExecute());

        Assert.True(atRetirement > 0);
        Assert.Equal(atRetirement, app.ReadLog.Count);
        Assert.Same(before, view.Publication.Capture());
        Assert.False(view.IsActive);
        Assert.False(view.Pump());
        Assert.All(view.Experience.Actions, action => Assert.False(action.TryExecute()));
        Assert.False(Collection<FlowStationDetails>(view, "Source station").TrySelect(Collection<FlowStationDetails>(view, "Source station").GetItem(0).Id));
        Source<FlowTextSource>(view, "Find shipments").Value = "retired";
        Assert.Equal("", Source<FlowTextSource>(view, "Find shipments").Value);
        Assert.Equal(atRetirement, app.ReadLog.Count);
        Assert.Equal(0, app.Commands);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void RetirementInInventoryOrHistoryFormatterStopsRemainingWork(int phase, bool publicationOnly)
    {
        using var app = new ObservedNetwork();
        Action? retire = null;
        int calls = 0;
        using var view = Create(app, value =>
        {
            if (retire is not null && ++calls == phase) retire();
            return value;
        });
        SelectRoute(view);
        UiPublicationView before = view.Publication.Capture();
        retire = publicationOnly ? view.Publication.Dispose : view.Dispose;

        Assert.True(Action(view, "Refresh cargo").TryExecute());

        Assert.Equal(phase, calls);
        Assert.Same(before, view.Publication.Capture());
        Assert.False(view.IsActive);
        Assert.False(view.Pump());
        Assert.Equal(0, app.Commands);
    }

    [Fact]
    public void AThrowingResultObserverCannotRollbackOrReplayTheShipment()
    {
        using var app = new ObservedNetwork();
        using var view = Create(app);
        SelectRoute(view);
        var inventory = Collection<FlowInventorySlot>(view, "Source cargo");
        inventory.TrySelect(inventory.GetItem(0).Id);
        var result = Source<UiPublishedState<string>>(view, "Result");
        result.Changed += () => throw new InvalidOperationException("observer failed");
        int later = 0;
        result.Changed += () => later++;

        Assert.True(Action(view, "Send whole stack").TryExecute());

        Assert.Equal(1, app.Commands);
        Assert.Equal(1, later);
        Assert.Equal("Shipment created", result.Value);
        Assert.Empty(inventory.Value);
        Assert.Single(view.Publication.LastResult.ObserverErrors);
        Assert.False(view.Pump());
    }

    [Fact]
    public void PostSendInventoryFailureAndLocalEditRetainOneCommandAndOneConsistentResult()
    {
        using var app = new ObservedNetwork();
        using var view = Create(app);
        SelectRoute(view);
        var inventory = Collection<FlowInventorySlot>(view, "Source cargo");
        inventory.TrySelect(inventory.GetItem(0).Id);
        UiPublicationView before = view.Publication.Capture();
        app.Callback("inventory", () => throw new InvalidOperationException("inventory failed"));

        Assert.Throws<InvalidOperationException>(() => Action(view, "Send whole stack").TryExecute());
        Assert.Same(before, view.Publication.Capture());
        Assert.Equal(1, app.Commands);
        Assert.Equal(0, app.InventoryQuantity);
        Source<UiFormState>(view, "Station details").Fields[0].Value.Value = "Orchard";

        Assert.Equal("Shipment created", Text(view, "Result"));
        Assert.Empty(inventory.Value);
        Assert.Null(inventory.SelectedItemId);
        Assert.Equal("Select source cargo", Source<UiFormState>(view, "Quantity").Fields[0].ValidationMessage!.Value);
        Assert.True(Source<UiFormState>(view, "Station details").IsValid);
        Assert.Equal(1, app.Commands);
        Assert.False(view.Pump());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetirementInQueryFormatterStopsPreparationBeforeCollectionReplacement(bool publicationOnly)
    {
        using var app = new ObservedNetwork();
        Action? retire = null;
        int calls = 0;
        using var view = Create(app, value => { if (retire is not null) { calls++; retire(); } return value; });
        UiPublicationView before = view.Publication.Capture();
        retire = publicationOnly ? view.Publication.Dispose : view.Dispose;
        app.ReadLog.Clear();

        Source<FlowTextSource>(view, "Find shipments").Value = "Mine";

        Assert.Equal(1, calls);
        Assert.Equal(new[] { "network", "recovery", "route" }, app.ReadLog);
        Assert.Same(before, view.Publication.Capture());
        Assert.Equal("", Source<FlowTextSource>(view, "Find shipments").Value);
        Assert.False(view.IsActive);
        Assert.False(view.Pump());
        Assert.Equal(0, app.Commands);
    }

    [Theory]
    [InlineData("inventory")]
    [InlineData("history")]
    [InlineData("query")]
    public void FormatterFailurePreservesTheWholeViewAndRetriesThePendingIntent(string phase)
    {
        using var app = new ObservedNetwork();
        bool fail = false;
        using var view = Create(app, value => fail ? throw new InvalidOperationException("formatter failed") : value);
        SelectRoute(view);
        UiPublicationView before = view.Publication.Capture();
        fail = true;
        if (phase == "inventory") Assert.Throws<InvalidOperationException>(() => Action(view, "Refresh cargo").TryExecute());
        else if (phase == "query") Assert.Throws<InvalidOperationException>(() => Source<FlowTextSource>(view, "Find shipments").Value = "Mine");
        else
        {
            app.Inner.Application.Refresh(force: true);
            Assert.Throws<InvalidOperationException>(() => view.Pump());
        }

        Assert.Same(before, view.Publication.Capture());
        fail = false;
        Assert.True(view.Pump());
        Assert.Equal(phase == "query" ? "Mine" : "", Source<FlowTextSource>(view, "Find shipments").Value);
        Assert.NotEmpty(Collection<FlowParcelSnapshot>(view, "Shipments").Value);
        Assert.False(view.Pump());
        Assert.Equal(0, app.Commands);
    }

    private static NetworkExperience Create(ObservedNetwork app, Func<string, string>? itemName = null)
        => new(new UiSymbolId("Hatifect.Flow", "network"), app, itemName: itemName);
    private static T Source<T>(NetworkExperience view, string name)
        => Assert.IsType<T>(view.Experience.Elements.Single(element => element.Name == name).Source);
    private static FlowSelectionSource<T> Collection<T>(NetworkExperience view, string name) => Source<FlowSelectionSource<T>>(view, name);
    private static string Text(NetworkExperience view, string name)
    {
        IUiSemanticSource source = view.Experience.Elements.Single(element => element.Name == name).Source;
        return source is IUiSemanticSource<UiStatus> status
            ? status.Value.Message
            : Assert.IsType<UiPublishedState<string>>(source).Value;
    }
    private static UiActionDefinition Action(NetworkExperience view, string title) => Assert.Single(view.Experience.Actions, action => action.Title == title);
    private static void SelectRoute(NetworkExperience view)
    {
        var source = Collection<FlowStationDetails>(view, "Source station");
        var destination = Collection<FlowStationDetails>(view, "Destination station");
        Assert.True(source.TrySelect(source.GetItem(0).Id));
        Assert.True(destination.TrySelect(destination.GetItem(1).Id));
    }

    private sealed class ObservedNetwork : IFlowNetworkApplication, IDisposable
    {
        internal readonly NetworkTestApplication Inner = new();
        internal string Prefix = "";
        internal bool RemoveOrigin;
        internal int InventoryQuantity = 8, Commands, InventoryReads;
        internal readonly List<string> ReadLog = new();
        private readonly Dictionary<string, Action> _callbacks = new();
        internal void Callback(string phase, Action callback) => _callbacks[phase] = callback;
        private void Invoke(string phase)
        {
            ReadLog.Add(phase);
            if (_callbacks.Remove(phase, out Action? callback)) callback();
        }
        public event Action<long>? RevisionChanged { add => Inner.RevisionChanged += value; remove => Inner.RevisionChanged -= value; }
        public FlowSnapshot ReadSnapshot() { FlowSnapshot snapshot = Inner.ReadSnapshot(); Invoke("snapshot"); return snapshot; }
        public FlowNetworkSnapshot ReadNetwork()
        {
            var snapshot = Inner.ReadNetwork();
            var result = new FlowNetworkSnapshot(snapshot.Transport, snapshot.Stations.Where(station => !RemoveOrigin || station.Id != CheckpointFixture.Origin.Value)
                .Select(station => station with { Name = Prefix + station.Name }).ToArray(), snapshot.Target, snapshot.TargetDescription);
            Invoke("network"); return result;
        }
        public FlowRoutePreview PreviewRoute(Guid source, Guid destination) { var result = Inner.PreviewRoute(source, destination); Invoke("route"); return result; }
        public IReadOnlyList<FlowInventorySlot> ReadInventory(Guid station)
        {
            InventoryReads++;
            IReadOnlyList<FlowInventorySlot> result = InventoryQuantity == 0 ? Array.Empty<FlowInventorySlot>()
                : new[] { new FlowInventorySlot(3, "ore", InventoryQuantity, new string('A', 64), "0") };
            Invoke("inventory"); return result;
        }
        public IReadOnlyList<FlowRecoveryIssue> ReadRecovery() { Invoke("recovery"); return Inner.ReadRecovery(); }
        public FlowCommandResult Execute(FlowParcelCommand command) { Commands++; Invoke("execute"); return Inner.Execute(command); }
        public FlowCommandResult Execute(FlowNetworkCommand command) { Commands++; Invoke("execute"); return Inner.Execute(command); }
        public FlowCommandResult Execute(FlowSendCommand command)
        {
            Commands++; Invoke("execute");
            var result = Inner.Execute(command);
            InventoryQuantity -= command.Quantity ?? InventoryQuantity;
            Inner.Application.Refresh(force: true);
            return result;
        }
        public FlowCommandResult Execute(FlowRecoveryCommand command) { Commands++; Invoke("execute"); return Inner.Execute(command); }
        public void Dispose() => Inner.Dispose();
    }
}
