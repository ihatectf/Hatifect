using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Hatifect.Flow.Application;
using Hatifect.Flow.Diagnostics;
using Hatifect.Flow.Inventory;
using Hatifect.Flow.Sessions;
using Xunit;

namespace Hatifect.Flow.Stardew.Tests;

[Trait("Category", "flow")]
public sealed class PlayerInputJournalTests
{
    [Fact]
    public void RealPartialCommandAndStaleRepeatRetainExactImmutableBeforeAndAfterEvidence()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        var stamp = new FlowPlayerInputStamp("os-injected", 42, 2, 2, 3, 1);
        var journal = new FlowPlayerInputJournal(session, () => stamp, () => Capture(session, world));
        FlowSendCommand command = Send(session) with { Quantity = 3 };
        FlowCommandResult applied = journal.Execute(command);
        FlowPlayerCommandTrace first = Assert.Single(journal.Entries);
        Assert.Same(command, first.Command);
        Assert.Same(applied, first.Result);
        Assert.Same(stamp, first.Input);
        Assert.Equal("send", first.Kind);
        Assert.Equal(1, first.Sequence);
        Assert.Equal(FlowCommandStatus.Applied, applied.Status);
        Assert.Null(first.Error);
        Assert.Equal(0, Parcels(first.Before));
        Assert.Equal(1, Parcels(first.After!));
        Assert.Equal(8, Assert.Single(world.Source.Items).Stack);
        Assert.Empty(world.Destination.Items);
        string retained = JsonSerializer.Serialize(first);

        FlowCommandResult stale = journal.Execute(command);
        Assert.Equal(FlowCommandStatus.Conflict, stale.Status);
        Assert.Equal(FlowRejectionCode.StaleRevision, stale.Code);
        FlowPlayerCommandTrace second = journal.Entries[1];
        Assert.Equal(2, second.Sequence);
        Assert.Same(stale, second.Result);
        Assert.Equal(second.Before, second.After);
        Assert.Equal(1, Parcels(second.After!));
        for (int tick = 0; tick < 8; tick++) session.Tick(true);
        Assert.Equal(5, Assert.Single(world.Source.Items).Stack);
        Assert.Equal(3, Assert.Single(world.Destination.Items).Stack);
        Assert.Equal(retained, JsonSerializer.Serialize(first));
        Assert.Empty(world.Errors);
    }

    [Fact]
    public void EvidenceCapacityRejectsBeforeTheNextOtherwiseValidOwnerCommand()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        FlowSendCommand valid = Send(session);
        int captures = 0;
        var journal = new FlowPlayerInputJournal(session,
            () => new("undeclared", 0, 0, 0, 0, 0), () => { captures++; return Capture(session, world); });
        for (int index = 0; index < FlowPlayerInputJournal.MaximumCommands; index++)
            Assert.Equal(FlowCommandStatus.Conflict, journal.Execute(valid with { ExpectedRevision = -1 }).Status);
        string before = Capture(session, world);
        Assert.Throws<InvalidOperationException>(() => journal.Execute(valid));
        Assert.Equal(FlowPlayerInputJournal.MaximumCommands, journal.Entries.Count);
        Assert.Equal(FlowPlayerInputJournal.MaximumCommands * 2, captures);
        Assert.Equal(before, Capture(session, world));
        Assert.Empty(session.ReadSnapshot().Parcels);
        Assert.Equal(0, session.ReadResources().Payloads.Used);
    }

    [Fact]
    public void PostCommandCaptureFailureRetainsTheAppliedResultAndPropagatesFailureWithoutReplay()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        var failure = new InvalidOperationException("Expected diagnostic capture failure.");
        int captures = 0;
        var journal = new FlowPlayerInputJournal(session,
            () => new("undeclared", 0, 0, 0, 0, 0),
            () => ++captures == 2 ? throw failure : Capture(session, world));
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => journal.Execute(Send(session))));
        FlowPlayerCommandTrace entry = Assert.Single(journal.Entries);
        Assert.Equal(FlowCommandStatus.Applied, entry.Result!.Status);
        Assert.Null(entry.After);
        Assert.Contains(failure.Message, entry.Error!);
        Assert.Single(session.ReadSnapshot().Parcels);
        Assert.Equal(1, session.ReadResources().Payloads.Used);
        Assert.Equal(8, Assert.Single(world.Source.Items).Stack);
        Assert.Empty(world.Destination.Items);
        Assert.Equal(2, captures);
    }

    [Fact]
    public void CaptureCannotReenterOwnerCommandAndOversizedEvidencePreventsAdmission()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        FlowSendCommand command = Send(session);
        FlowPlayerInputJournal? journal = null;
        journal = new(session, () => new("undeclared", 0, 0, 0, 0, 0), () =>
        {
            Assert.Throws<InvalidOperationException>(() => journal!.Execute(command));
            return new string('x', 262145);
        });
        Assert.Throws<InvalidOperationException>(() => journal.Execute(command));
        Assert.Empty(journal.Entries);
        Assert.Empty(session.ReadSnapshot().Parcels);
        Assert.Equal(0, session.ReadResources().Payloads.Used);
        Assert.Equal(8, Assert.Single(world.Source.Items).Stack);
        Assert.Empty(world.Destination.Items);
    }

    [Fact]
    public void OwnerThreadFailureIsRecordedAndPropagatedWithoutAnAppliedResult()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        FlowSendCommand command = Send(session);
        var journal = new FlowPlayerInputJournal(session,
            () => new("undeclared", 0, 0, 0, 0, 0), () => "{}");
        Exception? observed = null;
        var thread = new Thread(() => observed = Record.Exception(() => journal.Execute(command)));
        thread.Start();
        thread.Join();
        Assert.IsType<InvalidOperationException>(observed);
        FlowPlayerCommandTrace entry = Assert.Single(journal.Entries);
        Assert.Null(entry.Result);
        Assert.Null(entry.After);
        Assert.Equal("Flow application access requires its owning thread.", observed!.Message);
        Assert.NotNull(entry.Error);
        Assert.StartsWith($"{typeof(InvalidOperationException).FullName}: {observed.Message}", entry.Error);
        Assert.Contains("FlowApplication.RequireThread()", entry.Error);
        // Propagation adds caller frames after the journal captures the exception.
        Assert.StartsWith(entry.Error, observed.ToString());
        Assert.Empty(session.ReadSnapshot().Parcels);
        Assert.Equal(0, session.ReadResources().Payloads.Used);
        Assert.Equal(8, Assert.Single(world.Source.Items).Stack);
        Assert.Empty(world.Destination.Items);
    }

    private static FlowSendCommand Send(FlowGameSession session)
    {
        FlowSnapshot snapshot = session.ReadSnapshot();
        FlowLinkSnapshot link = Assert.Single(snapshot.Links);
        FlowInventorySlot slot = Assert.Single(session.ReadInventory(link.Origin));
        return new(snapshot.SessionId, snapshot.Revision, link.Origin, link.Destination, slot.Index, slot.Fingerprint);
    }

    private static string Capture(FlowGameSession session, GameSessionWorld world)
        => JsonSerializer.Serialize(new
        {
            snapshot = session.ReadSnapshot(), resources = session.ReadResources(),
            source = world.Source.Items.Where(item => item is not null).Select(FlowItemCodec.Encode).ToArray(),
            destination = world.Destination.Items.Where(item => item is not null).Select(FlowItemCodec.Encode).ToArray()
        });

    private static int Parcels(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("snapshot").GetProperty("Parcels").GetArrayLength();
    }
}
