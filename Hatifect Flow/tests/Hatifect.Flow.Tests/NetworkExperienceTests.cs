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
public sealed class NetworkExperienceTests
{
    [Fact]
    public void HistoryPagesSearchAndFilterRemainBoundedAndRestoreSelectionByIdentity()
    {
        using var app = new NetworkTestApplication();
        for (int i = 2; i <= 27; i++) app.Fixture.AddBatch(i);
        app.Application.Refresh();
        using var view = new NetworkExperience(new UiSymbolId("Hatifect.Flow", "network"), app);
        var history = Source<UiSelectableCollectionState<FlowParcelSnapshot>>(view, "Shipments");
        Assert.Equal(12, history.Count);
        history.TrySelect(history.GetItem(0).Id);
        UiSymbolId selected = history.SelectedItemId!.Value;
        Assert.True(Action(view, "Next page").TryExecute());
        Assert.Equal(12, history.Count);
        Assert.Null(history.SelectedItemId);
        Assert.True(Action(view, "Next page").TryExecute());
        Assert.Equal(3, history.Count);
        Assert.False(Action(view, "Next page").CanExecute);
        Action(view, "Previous page").TryExecute();
        Action(view, "Previous page").TryExecute();
        Assert.Equal(selected, history.SelectedItemId);
        var query = Source<UiState<string>>(view, "Find shipments");
        query.Value = "no matching cargo";
        Assert.Empty(history.Value);
        Assert.False(Action(view, "Cancel selected").CanExecute);
        query.Value = "Mine";
        Assert.Equal(12, history.Count);
        Assert.Equal(selected, history.SelectedItemId);
        Assert.True(Action(view, "Cancel selected").TryExecute());
        Assert.Equal(Hatifect.Flow.Domain.Shipments.ParcelState.Cancelled, history.Value[0].State);
        var filter = Source<UiSelectableCollectionState<string>>(view, "Shipment filter");
        filter.TrySelect(filter.GetItem(1).Id);
        Assert.DoesNotContain(history.Value, parcel => parcel.State == Hatifect.Flow.Domain.Shipments.ParcelState.Cancelled);
        filter.TrySelect(filter.GetItem(2).Id);
        Assert.Empty(history.Value);
    }

    [Fact]
    public void LocalizedNetworkPreservesElementIdsAndAllowsMultiwordLabels()
    {
        using var app = new NetworkTestApplication();
        var id = new UiSymbolId("Hatifect.Flow", "network");
        using var english = new NetworkExperience(id, app);
        using var russian = new NetworkExperience(id, app, russian: true);
        Assert.Equal(english.Experience.Elements.Select(element => element.Id), russian.Experience.Elements.Select(element => element.Id));
        Assert.Contains(russian.Experience.Elements, element => element.Name == "Станция отправления");
    }

    [Fact]
    public void StationAuthoringUsesEditableValidatedFieldsAndDistinctSelectionIdentities()
    {
        using var app = new NetworkTestApplication();
        using var view = new NetworkExperience(new UiSymbolId("Hatifect.Flow", "network"), app);
        var form = Source<UiFormState>(view, "Station details");
        UiActionDefinition register = Action(view, "Bind new station");
        Assert.False(register.CanExecute);
        Assert.False(form.IsValid);
        form.Fields[0].Value.Value = "Orchard";
        Assert.True(form.IsValid);
        Assert.True(register.TryExecute());
        Assert.Equal(FlowNetworkAction.RegisterStation, app.NetworkCommand!.Action);
        Assert.Equal("Orchard", app.NetworkCommand.Name);
        Assert.Equal(app.Target, app.NetworkCommand.Target);
        var source = Source<UiSelectableCollectionState<FlowStationDetails>>(view, "Source station");
        var destination = Source<UiSelectableCollectionState<FlowStationDetails>>(view, "Destination station");
        Assert.DoesNotContain(source.GetItem(0).Id, destination.Value.Select((_, index) => destination.GetItem(index).Id));
        source.TrySelect(source.GetItem(0).Id);
        destination.TrySelect(destination.GetItem(1).Id);
        Assert.True(Action(view, "Add directed link").TryExecute());
        Assert.Equal(CheckpointFixture.Origin.Value, app.NetworkCommand.Station);
        Assert.Equal(CheckpointFixture.Destination.Value, app.NetworkCommand.Destination);
        Assert.Equal(999, app.NetworkCommand.Capacity);
        Assert.Equal(180, app.NetworkCommand.TransitTicks);
    }

    [Fact]
    public void SendCarriesSelectedSlotFingerprintAndRetainedActionsStopAtNewRevisionOrDisposal()
    {
        using var app = new NetworkTestApplication();
        using var view = new NetworkExperience(new UiSymbolId("Hatifect.Flow", "network"), app);
        var source = Source<UiSelectableCollectionState<FlowStationDetails>>(view, "Source station");
        var destination = Source<UiSelectableCollectionState<FlowStationDetails>>(view, "Destination station");
        source.TrySelect(source.GetItem(0).Id);
        destination.TrySelect(destination.GetItem(1).Id);
        var inventory = Source<UiSelectableCollectionState<FlowInventorySlot>>(view, "Source cargo");
        Assert.Equal(8, Assert.Single(inventory.Value).Quantity);
        inventory.TrySelect(inventory.GetItem(0).Id);
        UiActionDefinition send = Action(view, "Send whole stack");
        Assert.True(send.TryExecute());
        Assert.Equal(3, app.SendCommand!.Slot);
        Assert.Equal(new string('A', 64), app.SendCommand.Fingerprint);
        Assert.Null(app.SendCommand.Quantity);
        int reads = app.InventoryReads;
        app.Application.Refresh(force: true);
        Assert.False(send.CanExecute);
        Assert.True(view.Pump());
        Assert.Equal(reads, app.InventoryReads); // domain notifications do not serialize physical inventories
        Assert.True(send.CanExecute);
        view.Dispose();
        Assert.False(send.TryExecute());
        Assert.All(view.Experience.Actions, action => Assert.False(action.CanExecute));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedQuantityUsesValidatedFormAndKeepsCompleteSourceFingerprint(bool russian)
    {
        using var app = new NetworkTestApplication();
        using var view = new NetworkExperience(new UiSymbolId("Hatifect.Flow", "quantity"), app, russian);
        T Element<T>(string suffix) => Assert.IsType<T>(view.Experience.Elements.Single(element => element.Id.LocalId.EndsWith(suffix, StringComparison.Ordinal)).Source);
        var source = Element<UiSelectableCollectionState<FlowStationDetails>>("/source-station");
        var destination = Element<UiSelectableCollectionState<FlowStationDetails>>("/destination-station");
        source.TrySelect(source.GetItem(0).Id);
        destination.TrySelect(destination.GetItem(1).Id);
        var inventory = Element<UiSelectableCollectionState<FlowInventorySlot>>("/source-cargo");
        var form = Element<UiFormState>("/shipment-quantity");
        UiActionDefinition send = Assert.Single(view.Experience.Actions, action => action.Id.LocalId.EndsWith("/send-quantity", StringComparison.Ordinal));
        Assert.False(send.CanExecute);
        inventory.TrySelect(inventory.GetItem(0).Id);
        foreach (string invalid in new[] { "0", "9", "-1", "1.5", "", "999999999999" })
        {
            form.Fields[0].Value.Value = invalid;
            Assert.False(form.IsValid);
            Assert.False(send.TryExecute());
        }
        form.Fields[0].Value.Value = "3";
        Assert.True(form.IsValid);
        Assert.True(send.TryExecute());
        Assert.Equal(3, app.SendCommand!.Quantity);
        Assert.Equal(new string('A', 64), app.SendCommand.Fingerprint);
        view.Dispose();
        Assert.False(send.TryExecute());
    }

    private static T Source<T>(NetworkExperience view, string name) => Assert.IsType<T>(view.Experience.Elements.Single(element => element.Name == name).Source);
    private static UiActionDefinition Action(NetworkExperience view, string title) => Assert.Single(view.Experience.Actions, action => action.Title == title);
}

internal sealed class NetworkTestApplication : IFlowNetworkApplication, IDisposable
{
    internal CheckpointFixture Fixture { get; } = new();
    internal Guid Target { get; } = Guid.NewGuid();
    internal FlowApplication Application { get; }
    internal FlowNetworkCommand? NetworkCommand { get; private set; }
    internal FlowSendCommand? SendCommand { get; private set; }
    internal int InventoryReads { get; private set; }
    internal NetworkTestApplication()
    {
        Application = new FlowApplication(Fixture.Runtime, action => action(Fixture.Runtime), _ => { });
    }
    public event Action<long>? RevisionChanged { add => Application.RevisionChanged += value; remove => Application.RevisionChanged -= value; }
    public FlowSnapshot ReadSnapshot() => Application.ReadSnapshot();
    public FlowNetworkSnapshot ReadNetwork() => new(ReadSnapshot(), new[]
    {
        new FlowStationDetails(CheckpointFixture.Origin.Value, "Mine", "Farm (0, 0)", true),
        new FlowStationDetails(CheckpointFixture.Destination.Value, "Farm", "Farm (1, 0)", true)
    }, Target, "Farm (2, 0)");
    public FlowRoutePreview PreviewRoute(Guid source, Guid destination) => new(source != Guid.Empty && destination != Guid.Empty && source != destination, 1, 3, 999);
    public IReadOnlyList<FlowInventorySlot> ReadInventory(Guid station)
    {
        InventoryReads++;
        return new[] { new FlowInventorySlot(3, "ore", 8, new string('A', 64), "0") };
    }
    public FlowCommandResult Execute(FlowNetworkCommand command) { NetworkCommand = command; return new(FlowCommandStatus.Applied, ReadSnapshot().Revision); }
    public FlowCommandResult Execute(FlowSendCommand command) { SendCommand = command; return new(FlowCommandStatus.Applied, ReadSnapshot().Revision); }
    public FlowCommandResult Execute(FlowParcelCommand command) => Application.Execute(command);
    public IReadOnlyList<FlowRecoveryIssue> ReadRecovery() => Array.Empty<FlowRecoveryIssue>();
    public FlowCommandResult Execute(FlowRecoveryCommand command) => new(FlowCommandStatus.Rejected, ReadSnapshot().Revision);
    public void Dispose() => Application.Dispose();
}
