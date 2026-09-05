using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Policies;
using Hatifect.Flow.Domain.Shipments;
using Xunit;

namespace Hatifect.Flow.Tests;

[Trait("Category", "flow")]
public sealed class IdentityContractTests
{
    [Fact]
    public void TypedIds_RoundTripAndKindsRemainDistinct()
    {
        const string canonical = "abcdef01-2345-6789-abcd-ef0123456789";
        const string alternate = "{ABCDEF01-2345-6789-ABCD-EF0123456789}";
        Guid value = Guid.Parse(canonical);
        object[] identities =
        {
            NetworkId.Parse(alternate), StationId.Parse(alternate), LinkId.Parse(alternate),
            ShipmentId.Parse(alternate), ParcelId.Parse(alternate), CargoId.Parse(alternate)
        };

        Assert.All(identities, id => Assert.Equal(canonical, id.ToString()));
        Assert.Equal(new NetworkId(value), NetworkId.Parse(canonical));
        Assert.Equal(new StationId(value), StationId.Parse(canonical));
        Assert.Equal(new LinkId(value), LinkId.Parse(canonical));
        Assert.Equal(new ShipmentId(value), ShipmentId.Parse(canonical));
        Assert.Equal(new ParcelId(value), ParcelId.Parse(canonical));
        Assert.Equal(new CargoId(value), CargoId.Parse(canonical));
        Assert.Equal(6, new HashSet<object>(identities).Count);
        Guid other = Guid.Parse("abcdef02-2345-6789-abcd-ef0123456789");
        Assert.NotEqual(new NetworkId(value), new NetworkId(other));
        Assert.NotEqual(new StationId(value), new StationId(other));
        Assert.NotEqual(new LinkId(value), new LinkId(other));
        Assert.NotEqual(new ShipmentId(value), new ShipmentId(other));
        Assert.NotEqual(new ParcelId(value), new ParcelId(other));
        Assert.NotEqual(new CargoId(value), new CargoId(other));
    }

    [Fact]
    public void TypedIds_RejectEmptyAndMalformedValues()
    {
        Assert.Throws<ArgumentException>(() => new NetworkId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new StationId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new LinkId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new ShipmentId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new ParcelId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new CargoId(Guid.Empty));
        Assert.Throws<FormatException>(() => NetworkId.Parse("farm"));
        Assert.Throws<FormatException>(() => StationId.Parse("farm"));
        Assert.Throws<FormatException>(() => LinkId.Parse("farm"));
        Assert.Throws<FormatException>(() => ShipmentId.Parse("farm"));
        Assert.Throws<FormatException>(() => ParcelId.Parse("farm"));
        Assert.Throws<FormatException>(() => CargoId.Parse("farm"));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    public void ServicePolicy_ServiceAndGuaranteeAreIndependent(int service, int guarantee)
    {
        var policy = new ServicePolicy((ServiceClass)service, (DeliveryGuarantee)guarantee);

        Assert.Equal((ServiceClass)service, policy.ServiceClass);
        Assert.Equal((DeliveryGuarantee)guarantee, policy.Guarantee);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(3, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 3)]
    public void ServicePolicy_RejectsUnknownDiscriminators(int service, int guarantee)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ServicePolicy((ServiceClass)service, (DeliveryGuarantee)guarantee));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Manifest_RejectsMissingItemIdentity(string? itemKey)
    {
        Assert.Throws<ArgumentException>(() => new CargoManifest(itemKey!, 5));
    }

    [Fact]
    public void Manifest_EnforcesPositiveQuantityAndBoundedItemIdentity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CargoManifest("ore", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CargoManifest("ore", -1));
        var atLimit = new CargoManifest(new string('x', 256), 1);
        Assert.Equal(256, atLimit.ItemKey.Length);
        Assert.Equal(1, atLimit.Quantity);
        Assert.Throws<ArgumentException>(() => new CargoManifest(new string('x', 257), 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void FlowLimits_RejectNonPositiveResourceBounds(int invalid)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlowLimits(maxStations: invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlowLimits(maxLinks: invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlowLimits(maxParcels: invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlowLimits(maxRouteVisits: invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlowLimits(maxRoutePlans: invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlowLimits(maxDeliveryAttempts: invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlowLimits(maxEvents: invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlowLimits(maxOperationsPerAdvance: invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlowLimits(maxCargoUnits: invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlowLimits(maxPendingOperations: invalid));
    }

    [Fact]
    public void CoreAssembly_HasNoPlatformOrPresentationReferencesAndExportsOnlyApplicationContract()
    {
        var core = typeof(NetworkId).Assembly;
        string[] forbidden = { "Stardew", "SMAPI", "MonoGame", "Microsoft.Xna", "Hatifect.UI" };
        Assert.DoesNotContain(core.GetReferencedAssemblies(), reference =>
            forbidden.Any(prefix => reference.Name!.Contains(prefix, StringComparison.OrdinalIgnoreCase)));
        string[] platform = { "Stardew", "SMAPI", "MonoGame", "Microsoft.Xna" };
        Assert.DoesNotContain(typeof(IdentityContractTests).Assembly.GetReferencedAssemblies(), reference =>
            platform.Any(prefix => reference.Name!.Contains(prefix, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("Hatifect.Flow.Core", core.GetName().Name);
        string[] contract =
        {
            "Hatifect.Flow.Application.IFlowApplication",
            "Hatifect.Flow.Application.IFlowNetworkApplication",
            "Hatifect.Flow.Application.FlowNetworkAction",
            "Hatifect.Flow.Application.FlowNetworkCommand",
            "Hatifect.Flow.Application.FlowStationDetails",
            "Hatifect.Flow.Application.FlowRoutePreview",
            "Hatifect.Flow.Application.FlowNetworkSnapshot",
            "Hatifect.Flow.Application.FlowInventorySlot",
            "Hatifect.Flow.Application.FlowSendCommand",
            "Hatifect.Flow.Application.FlowRecoveryIssue",
            "Hatifect.Flow.Application.FlowRecoveryCommand",
            "Hatifect.Flow.Application.FlowApplicationState",
            "Hatifect.Flow.Application.FlowParcelAction",
            "Hatifect.Flow.Application.FlowCommandStatus",
            "Hatifect.Flow.Application.FlowParcelActions",
            "Hatifect.Flow.Application.FlowParcelCommand",
            "Hatifect.Flow.Application.FlowCommandResult",
            "Hatifect.Flow.Application.FlowStationSnapshot",
            "Hatifect.Flow.Application.FlowLinkSnapshot",
            "Hatifect.Flow.Application.FlowParcelSnapshot",
            "Hatifect.Flow.Application.FlowSnapshot",
            "Hatifect.Flow.Domain.Shipments.ParcelState"
        };
        Assert.Equal(contract.OrderBy(name => name), core.GetExportedTypes().Select(type => type.FullName).OrderBy(name => name));
    }
}
