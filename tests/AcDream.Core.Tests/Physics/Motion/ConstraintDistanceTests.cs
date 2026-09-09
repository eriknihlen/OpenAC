using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class ConstraintDistanceTests
{
    private const uint OutdoorCell = 0x12340007u;
    // EnvCell (indoor) indices are 0x0100+ .
    private const uint IndoorCell = 0x12340105u;

    [Fact]
    public void GetStartConstraintDistance_Outdoor_Is10NotAcesInverted5()
    {
        Assert.Equal(10.0f, ConstraintDistance.GetStartConstraintDistance(OutdoorCell));
    }

    [Fact]
    public void GetStartConstraintDistance_Indoor_Is5()
    {
        Assert.Equal(5.0f, ConstraintDistance.GetStartConstraintDistance(IndoorCell));
    }

    [Fact]
    public void GetMaxConstraintDistance_Outdoor_Is50()
    {
        Assert.Equal(50.0f, ConstraintDistance.GetMaxConstraintDistance(OutdoorCell));
    }

    [Fact]
    public void GetMaxConstraintDistance_Indoor_Is20()
    {
        Assert.Equal(20.0f, ConstraintDistance.GetMaxConstraintDistance(IndoorCell));
    }

    [Theory]
    [InlineData(0x00FFu, false)]
    [InlineData(0x0100u, true)]
    [InlineData(0x0000u, false)]
    [InlineData(0xFFFFu, true)]
    public void IsIndoorCell_BoundaryIsLow16Ox0100(uint objCellId, bool expectedIndoor)
    {
        Assert.Equal(expectedIndoor, ConstraintDistance.IsIndoorCell(objCellId));
    }

    [Fact]
    public void NoPlayerVsRemoteSplit_SameCellAlwaysYieldsSameBand()
    {
        float startA = ConstraintDistance.GetStartConstraintDistance(OutdoorCell);
        float startB = ConstraintDistance.GetStartConstraintDistance(OutdoorCell);
        float maxA = ConstraintDistance.GetMaxConstraintDistance(OutdoorCell);
        float maxB = ConstraintDistance.GetMaxConstraintDistance(OutdoorCell);

        Assert.Equal(startA, startB);
        Assert.Equal(maxA, maxB);
    }
}
