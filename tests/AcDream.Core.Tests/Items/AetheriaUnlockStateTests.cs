using AcDream.Core.Items;
using Xunit;

namespace AcDream.Core.Tests.Items;

public sealed class AetheriaUnlockStateTests
{
    [Fact]
    public void Missing_property_means_all_slots_locked()
    {
        var player = new ClientObject { ObjectId = 1 };

        Assert.Equal(AetheriaUnlockState.None, AetheriaUnlocks.Read(player));
    }

    [Fact]
    public void Property_322_preserves_the_three_independent_retail_bits()
    {
        var player = new ClientObject { ObjectId = 1 };
        player.Properties.Ints[AetheriaUnlocks.PropertyId] =
            (int)(AetheriaUnlockState.Blue | AetheriaUnlockState.Red);

        AetheriaUnlockState actual = AetheriaUnlocks.Read(player);

        Assert.True((actual & AetheriaUnlockState.Blue) != 0);
        Assert.False((actual & AetheriaUnlockState.Yellow) != 0);
        Assert.True((actual & AetheriaUnlockState.Red) != 0);
    }
}
