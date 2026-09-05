using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Hatifect.Flow.Application.Dispatch;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Infrastructure.Persistence;
using Xunit;

namespace Hatifect.Flow.Tests;

[Trait("Category", "flow")]
public sealed class DurableSaveSessionStoreTests
{
    private static readonly FlowSaveIdentity SaveA = new(1);
    private static readonly FlowSaveIdentity SaveB = new(ulong.MaxValue);

    [Fact]
    public void Identity_RejectsZeroAndDefaultBeforeFilesystemChanges()
    {
        using var directory = new SaveTestDirectory();
        string root = Path.Combine(directory.Root, "absent");
        var store = new DurableSaveSessionStore(root);
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlowSaveIdentity(0));
        Assert.ThrowsAny<ArgumentException>(() => store.GetSaveDirectory(default));
        Assert.ThrowsAny<ArgumentException>(() => store.Open(default));
        Assert.ThrowsAny<ArgumentException>(() => store.Create(default, new CheckpointFixture().Runtime));
        Assert.Throws<ArgumentNullException>(() => store.Create(SaveA, null!));
        Assert.False(Directory.Exists(root));
        Assert.Equal(1UL, SaveA.Value); Assert.Equal(ulong.MaxValue, SaveB.Value);
        Assert.Equal(Path.Combine(root, "save-0000000000000001"), store.GetSaveDirectory(SaveA));
        Assert.Equal(Path.Combine(root, "save-ffffffffffffffff"), store.GetSaveDirectory(SaveB));
    }

    [Fact]
    public void Store_SaveAToSaveBToSaveARestoresIndependentDeliveryAndFencesPreviousRuntime()
    {
        using var directory = new SaveTestDirectory();
        var store = new DurableSaveSessionStore(directory.Root);
        using var host = new DurableFlowHost();
        host.Start("a", () => store.Create(SaveA, new CheckpointFixture().Runtime));
        FlowRuntime retired = host.Runtime;
        host.Execute(runtime => Assert.True(runtime.TryReserve(CheckpointFixture.Parcel)));
        for (int tick = 0; tick < 4; tick++) host.Tick();
        Assert.Equal(ParcelState.Delivered, host.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(2, host.ProviderRevision); Assert.Empty(host.GetPortSnapshot(CheckpointFixture.Origin).Inventory);
        Assert.Equal(CheckpointFixture.Cargo.Value, Assert.Single(host.GetPortSnapshot(CheckpointFixture.Destination).Inventory).CargoId);
        host.Stop();
        var stateA = Images(store, SaveA);
        host.Start("b", () => store.Create(SaveB, new CheckpointFixture().Runtime));
        Assert.Throws<InvalidOperationException>(() => retired.Cancel(CheckpointFixture.Parcel));
        host.Execute(runtime => Assert.True(runtime.TryReserve(CheckpointFixture.Parcel)));
        Assert.Equal(ParcelState.Reserved, host.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(0, host.ProviderRevision); Assert.Single(host.GetPortSnapshot(CheckpointFixture.Origin).Inventory);
        host.Stop(); var stateB = Images(store, SaveB);
        host.Start("a", () => store.Open(SaveA));
        Assert.Equal(ParcelState.Delivered, host.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(4, host.LogicalTick); Assert.Equal(2, host.ProviderRevision);
        Assert.Single(host.GetPortSnapshot(CheckpointFixture.Origin).Receipts);
        Assert.Single(host.GetPortSnapshot(CheckpointFixture.Destination).Receipts);
        Assert.Null(host.Runtime.GetCargo(CheckpointFixture.Cargo).ClaimedBy);
        AssertImages(stateA, Images(store, SaveA)); AssertImages(stateB, Images(store, SaveB));
        host.Stop();
        using var b = store.Open(SaveB);
        Assert.Equal(ParcelState.Reserved, b.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(0, b.ProviderRevision);
        AssertImages(stateA, Images(store, SaveA)); AssertImages(stateB, Images(store, SaveB));
    }

    [Fact]
    public void Store_ReopenUsesSamePairAndDuplicateWriterCannotModifyFiles()
    {
        using var directory = new SaveTestDirectory(); var store = new DurableSaveSessionStore(directory.Root);
        Guid pair;
        using (var session = store.Create(SaveA, new CheckpointFixture().Runtime))
        {
            pair = session.PairId; Assert.NotEqual(Guid.Empty, pair);
            var before = Images(store, SaveA);
            Assert.Throws<IOException>(() => store.Open(SaveA));
            Assert.Throws<IOException>(() => store.Create(SaveA, new CheckpointFixture().Runtime));
            AssertImages(before, Images(store, SaveA));
        }
        using var reopened = new DurableSaveSessionStore(directory.Root).Open(SaveA);
        Assert.Equal(pair, reopened.PairId); Assert.Equal(0, reopened.Revision);
    }

    [Theory]
    [InlineData("core")] [InlineData("provider")] [InlineData("both")] [InlineData("binding")]
    public void Store_SwappedSaveMembersRejectWithoutMutatingEitherPair(string member)
    {
        using var directory = new SaveTestDirectory(); var store = new DurableSaveSessionStore(directory.Root);
        using (store.Create(SaveA, new CheckpointFixture().Runtime)) { }
        using (store.Create(SaveB, new CheckpointFixture().Runtime)) { }
        var original = Images(store, SaveA);
        string[] members = member == "both" ? new[] { "core", "provider" } : new[] { member };
        foreach (string selected in members) File.Copy(MemberPath(store, SaveB, selected), MemberPath(store, SaveA, selected), true);
        var corrupted = Images(store, SaveA); var other = Images(store, SaveB);
        Assert.Throws<InvalidDataException>(() => store.Open(SaveA));
        AssertImages(corrupted, Images(store, SaveA)); AssertImages(other, Images(store, SaveB));
        foreach (string selected in members) File.WriteAllBytes(MemberPath(store, SaveA, selected), original[selected]);
        using var repaired = store.Open(SaveA);
        Assert.Equal(ParcelState.Created, repaired.Runtime.GetParcel(CheckpointFixture.Parcel).State);
    }

    [Theory]
    [InlineData("empty")] [InlineData("truncated")] [InlineData("trailing")] [InlineData("magic")]
    [InlineData("checksum")] [InlineData("save")] [InlineData("pair")] [InlineData("network")]
    [InlineData("empty-pair")] [InlineData("empty-network")]
    public void Store_InvalidBindingRejectsBeforePairAccessAndReleasesBindingLease(string defect)
    {
        using var directory = new SaveTestDirectory(); var store = new DurableSaveSessionStore(directory.Root);
        using (store.Create(SaveA, new CheckpointFixture().Runtime)) { }
        string path = MemberPath(store, SaveA, "binding"); byte[] valid = File.ReadAllBytes(path); byte[] bytes = (byte[])valid.Clone();
        switch (defect)
        {
            case "empty": bytes = Array.Empty<byte>(); break;
            case "truncated": bytes = bytes[..79]; break;
            case "trailing": bytes = bytes.Concat(new byte[] { 0 }).ToArray(); break;
            case "magic": bytes[0] ^= 1; break;
            case "checksum": bytes[79] ^= 1; break;
            case "save": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), SaveB.Value); break;
            case "pair": Guid.NewGuid().TryWriteBytes(bytes.AsSpan(16, 16)); break;
            case "network": Guid.NewGuid().TryWriteBytes(bytes.AsSpan(32, 16)); break;
            case "empty-pair": bytes.AsSpan(16, 16).Clear(); break;
            case "empty-network": bytes.AsSpan(32, 16).Clear(); break;
        }
        if (defect is "save" or "pair" or "network" or "empty-pair" or "empty-network") SHA256.HashData(bytes.AsSpan(0, 48)).CopyTo(bytes, 48);
        File.WriteAllBytes(path, bytes); var before = Images(store, SaveA);
        Assert.Throws<InvalidDataException>(() => store.Open(SaveA));
        AssertImages(before, Images(store, SaveA));
        File.WriteAllBytes(path, valid);
        using var restored = store.Open(SaveA); Assert.Equal(0, restored.ProviderRevision);
    }

    [Theory]
    [InlineData("binding")] [InlineData("core")] [InlineData("provider")]
    public void Store_IncompletePairCannotBeOpenedOrAdopted(string missing)
    {
        using var directory = new SaveTestDirectory(); var store = new DurableSaveSessionStore(directory.Root);
        using (store.Create(SaveA, new CheckpointFixture().Runtime)) { }
        string path = MemberPath(store, SaveA, missing); byte[] bytes = File.ReadAllBytes(path); File.Delete(path);
        var before = Images(store, SaveA);
        Assert.ThrowsAny<IOException>(() => store.Open(SaveA));
        Assert.ThrowsAny<IOException>(() => store.Create(SaveA, new CheckpointFixture().Runtime));
        AssertImages(before, Images(store, SaveA)); Assert.False(File.Exists(path));
        File.WriteAllBytes(path, bytes);
        using var restored = store.Open(SaveA); Assert.Equal(0, restored.Revision);
    }

    [Fact]
    public void Store_AbsentSaveCannotOpenAndFailedCreateDoesNotConsumeFixture()
    {
        using var directory = new SaveTestDirectory(); var store = new DurableSaveSessionStore(directory.Root);
        Assert.ThrowsAny<IOException>(() => store.Open(SaveA));
        using (store.Create(SaveA, new CheckpointFixture().Runtime)) { }
        var fixture = new CheckpointFixture();
        Assert.Throws<IOException>(() => store.Create(SaveA, fixture.Runtime));
        Assert.True(fixture.Runtime.TryReserve(CheckpointFixture.Parcel));
        Assert.Equal(ParcelState.Reserved, fixture.Runtime.GetParcel(CheckpointFixture.Parcel).State);
    }

    [Theory]
    [InlineData("root")] [InlineData("save")] [InlineData("core-directory")] [InlineData("provider-directory")]
    [InlineData("binding")] [InlineData("core")] [InlineData("provider")]
    [InlineData("save-lock")] [InlineData("core-lock")] [InlineData("provider-lock")]
    public void Store_LinkedOwnershipPathsRejectWithoutWritingThroughLink(string selected)
    {
        using var directory = new SaveTestDirectory();
        string root = Path.Combine(directory.Root, "store"); var store = new DurableSaveSessionStore(root);
        using (store.Create(SaveA, new CheckpointFixture().Runtime)) { }
        string save = store.GetSaveDirectory(SaveA);
        string path = selected switch
        {
            "root" => root,
            "save" => save,
            "core-directory" => Path.Combine(save, "core"),
            "provider-directory" => Path.Combine(save, "provider"),
            "save-lock" => Path.Combine(save, DurableSaveSessionStore.LockFileName),
            "core-lock" => Path.Combine(save, "core", DurableFlowSession.LockFileName),
            "provider-lock" => Path.Combine(save, "provider", DurableCargoProvider.LockFileName),
            _ => MemberPath(store, SaveA, selected),
        };
        string target = Path.Combine(directory.Root, "displaced");
        bool isDirectory = Directory.Exists(path);
        if (isDirectory) { Directory.Move(path, target); Directory.CreateSymbolicLink(path, target); }
        else { File.Move(path, target); File.CreateSymbolicLink(path, target); }
        var targetBefore = Directory.Exists(target)
            ? Directory.GetFiles(target, "*", SearchOption.AllDirectories).ToDictionary(file => Path.GetRelativePath(target, file), File.ReadAllBytes)
            : new Dictionary<string, byte[]> { ["file"] = File.ReadAllBytes(target) };
        Assert.ThrowsAny<IOException>(() => new DurableSaveSessionStore(root).Open(SaveA));
        Assert.ThrowsAny<IOException>(() => new DurableSaveSessionStore(root).Create(SaveA, new CheckpointFixture().Runtime));
        var targetAfter = Directory.Exists(target)
            ? Directory.GetFiles(target, "*", SearchOption.AllDirectories).ToDictionary(file => Path.GetRelativePath(target, file), File.ReadAllBytes)
            : new Dictionary<string, byte[]> { ["file"] = File.ReadAllBytes(target) };
        AssertImages(targetBefore, targetAfter);
        if (isDirectory) { Directory.Delete(path); Directory.Move(target, path); }
        else { File.Delete(path); File.Move(target, path); }
        using var restored = store.Open(SaveA); Assert.Equal(0, restored.Revision);
    }

    [Fact]
    public void Store_UnknownPriorContentIsPreservedAndStableLockAloneAllowsCreation()
    {
        using var directory = new SaveTestDirectory(); var store = new DurableSaveSessionStore(directory.Root);
        string save = store.GetSaveDirectory(SaveA); Directory.CreateDirectory(save);
        string unrelated = Path.Combine(save, "unrelated.txt"); File.WriteAllText(unrelated, "owned elsewhere");
        var fixture = new CheckpointFixture();
        Assert.Throws<IOException>(() => store.Create(SaveA, fixture.Runtime));
        Assert.Equal("owned elsewhere", File.ReadAllText(unrelated));
        Assert.False(File.Exists(MemberPath(store, SaveA, "binding")));
        Assert.True(fixture.Runtime.TryReserve(CheckpointFixture.Parcel));
        File.Delete(unrelated);
        Assert.True(File.Exists(Path.Combine(save, DurableSaveSessionStore.LockFileName)));
        using var created = store.Create(SaveA, new CheckpointFixture().Runtime);
        Assert.Equal(ParcelState.Created, created.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(0, created.ProviderRevision);
    }

    private sealed class SaveTestDirectory : IDisposable
    {
        internal string Root { get; } = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
            "hatifect-save-store-test-" + Guid.NewGuid().ToString("N"));
        internal SaveTestDirectory() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, true);
    }

    private static string MemberPath(DurableSaveSessionStore store, FlowSaveIdentity identity, string member)
    {
        string root = store.GetSaveDirectory(identity);
        return member switch
        {
            "core" => Path.Combine(root, "core", DurableFlowSession.CheckpointFileName),
            "provider" => Path.Combine(root, "provider", DurableCargoProvider.ImageFileName),
            _ => Path.Combine(root, DurableSaveSessionStore.BindingFileName),
        };
    }
    private static Dictionary<string, byte[]> Images(DurableSaveSessionStore store, FlowSaveIdentity identity)
        => new[] { "binding", "core", "provider" }.Where(member => File.Exists(MemberPath(store, identity, member)))
            .ToDictionary(member => member, member => File.ReadAllBytes(MemberPath(store, identity, member)));
    private static void AssertImages(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.OrderBy(key => key), actual.Keys.OrderBy(key => key));
        foreach (var entry in expected) Assert.Equal(entry.Value, actual[entry.Key]);
    }
}
