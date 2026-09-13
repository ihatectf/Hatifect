using Hatifect.Flow.Inventory;
using Hatifect.Flow.Sessions;
using Xunit;

namespace Hatifect.Flow.Stardew.Tests;

[Trait("Category", "flow")]
public sealed class FlowIdentityTests
{
    [Fact]
    public void PersistenceKeysUseTheCurrentHatifectIdentity()
    {
        Assert.Equal("flowline-v1", FlowGameSession.SaveKey);
        Assert.Equal("Hatifect.Flow/Station", FlowGameSession.StationKey);
        Assert.Equal("Hatifect.Flow/Cargo", ChestInventoryAccess.CargoKey);
    }
}
