using AcDream.Core.World;
using Xunit;

namespace AcDream.Core.Tests.World;

public class PhysicsDatBundleTests
{
    [Fact]
    public void Empty_ReturnsNullInfoAndEmptyMaps()
    {
        var b = PhysicsDatBundle.Empty;
        Assert.Null(b.Info);
        Assert.Empty(b.EnvCells);
        Assert.Empty(b.Environments);
        Assert.Empty(b.Setups);
        Assert.Empty(b.GfxObjs);
    }
}
