using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Ui;

namespace AcDream.Core.Tests.Ui;

public sealed class RetailPositionFormatterTests
{
    [Fact]
    public void Format_MatchesPositionToStringFieldOrderAndPrecision()
    {
        var position = new Position(
            0xA9B40001u,
            new CellFrame(
                new Vector3(1.25f, -2.5f, 3f),
                new Quaternion(0.1f, 0.2f, 0.3f, 0.9f)));

        Assert.Equal(
            "0xA9B40001 [1.250000 -2.500000 3.000000] 0.900000 0.100000 0.200000 0.300000",
            RetailPositionFormatter.Format(position));
    }

    [Fact]
    public void FormatOutdoorCell_UsesRetailRadarCoordinateOrder()
    {
        Assert.True(RadarCoordinates.TryFromCell(0xA9B40001u, out var expected));
        Assert.Equal(expected.CombinedText,
            RetailPositionFormatter.FormatOutdoorCell(0xA9B40001u));
        Assert.Null(RetailPositionFormatter.FormatOutdoorCell(0xA9B40164u));
    }
}
