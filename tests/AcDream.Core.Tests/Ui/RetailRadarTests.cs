using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Ui;

namespace AcDream.Core.Tests.Ui;

public sealed class RetailRadarTests
{
    [Theory]
    [InlineData(RadarBehavior.Undef, true, false)]
    [InlineData(RadarBehavior.ShowNever, true, false)]
    [InlineData(RadarBehavior.ShowMovement, true, true)]
    [InlineData(RadarBehavior.ShowAttacking, true, true)]
    [InlineData(RadarBehavior.ShowAlways, true, true)]
    [InlineData(RadarBehavior.ShowAlways, false, false)]
    public void Showability_AcceptsRetailEnumValuesWithoutLiveStateChecks(
        RadarBehavior behavior,
        bool hasPhysics,
        bool expected)
        => Assert.Equal(expected, RetailRadar.IsShowable(behavior, hasPhysics));

    [Fact]
    public void ShapeClassification_UsesFellowshipAllegianceAndMutualPkPriority()
    {
        var pk = new RadarObjectTraits(IsPlayerKiller: true);
        var pkLite = new RadarObjectTraits(IsPkLite: true);

        Assert.Equal(RadarBlipShape.Default, RetailRadar.GetBlipShape(pk));
        Assert.Equal(
            RadarBlipShape.Threat,
            RetailRadar.GetBlipShape(pk, new RadarRelationshipTraits(PlayerIsPlayerKiller: true)));
        Assert.Equal(
            RadarBlipShape.Threat,
            RetailRadar.GetBlipShape(pkLite, new RadarRelationshipTraits(PlayerIsPkLite: true)));
        Assert.Equal(
            RadarBlipShape.AllegianceMember,
            RetailRadar.GetBlipShape(pk, new RadarRelationshipTraits(
                IsAllegianceMember: true,
                PlayerIsPlayerKiller: true)));
        Assert.Equal(
            RadarBlipShape.Fellowship,
            RetailRadar.GetBlipShape(pk, new RadarRelationshipTraits(
                IsFellowshipMember: true,
                IsAllegianceMember: true,
                PlayerIsPlayerKiller: true)));
        Assert.Equal(
            RadarBlipShape.FellowshipLeader,
            RetailRadar.GetBlipShape(pk, new RadarRelationshipTraits(
                IsFellowshipMember: true,
                IsFellowshipLeader: true)));
        Assert.Equal(
            RadarBlipShape.Undef,
            RetailRadar.GetBlipShape(new RadarObjectTraits(IsValid: false)));
    }

    [Fact]
    public void BlipPixelShapes_MatchRetailDrawRoutines()
    {
        AssertOffsets(RadarBlipShape.Circle, [(0, 0)]);
        AssertOffsets(RadarBlipShape.Plus, [(0, 0), (0, -1), (0, 1), (-1, 0), (1, 0)]);
        AssertOffsets(RadarBlipShape.X, [(0, 0), (1, 1), (-1, -1), (-1, 1), (1, -1)]);
        AssertOffsets(
            RadarBlipShape.Box,
            [(0, -1), (0, 1), (-1, 0), (1, 0), (1, 1), (-1, -1), (-1, 1), (1, -1)]);
        AssertOffsets(RadarBlipShape.Triangle, [(0, 0), (-1, 1), (0, 1), (1, 1)]);
        AssertOffsets(RadarBlipShape.InvertedTriangle, [(0, 0), (-1, -1), (0, -1), (1, -1)]);
        AssertOffsets(
            RadarBlipShape.XBox,
            [
                (0, -1), (0, 1), (-1, 0), (1, 0),
                (1, 1), (-1, -1), (-1, 1), (1, -1),
                (-2, -2), (2, -2), (-2, 2), (2, 2),
            ]);
        Assert.Empty(RetailRadar.GetBlipPixels(RadarBlipShape.Undef).ToArray());
    }

    [Fact]
    public void CenterMarkerAndSelectionOutline_MatchRetailPixelCountsAndExtents()
    {
        var player = RetailRadar.PlayerMarkerPixels.ToArray();
        Assert.Equal(
            new[]
            {
                new RadarPixelOffset(0, 0),
                new RadarPixelOffset(0, -1),
                new RadarPixelOffset(0, 1),
                new RadarPixelOffset(-1, 0),
                new RadarPixelOffset(1, 0),
                new RadarPixelOffset(-2, 0),
                new RadarPixelOffset(2, 0),
                new RadarPixelOffset(0, -2),
                new RadarPixelOffset(0, 2),
            },
            player);

        var selected = RetailRadar.SelectionPixels.ToArray();
        Assert.Equal(20, selected.Length);
        Assert.All(selected, p => Assert.True(Math.Abs(p.X) == 3 || Math.Abs(p.Y) == 3));
        for (int coordinate = -2; coordinate <= 2; coordinate++)
        {
            Assert.Contains(new RadarPixelOffset(coordinate, -3), selected);
            Assert.Contains(new RadarPixelOffset(coordinate, 3), selected);
            Assert.Contains(new RadarPixelOffset(-3, coordinate), selected);
            Assert.Contains(new RadarPixelOffset(3, coordinate), selected);
        }
    }

    [Fact]
    public void Projection_UsesRetailRangeScaleAndScreenYAxis()
    {
        bool visible = RetailRadar.TryProject(
            new Vector3(10f, 20f, 4.999f),
            new Vector2(60f, 60f),
            radarRadiusPixels: 55,
            radarRangeMeters: RetailRadar.OutdoorRangeMeters,
            out var projection);

        Assert.True(visible);
        Assert.Equal(new Vector2(67f, 45f), projection.Pixel);
        Assert.Equal(1f, projection.RgbMultiplier);
    }

    [Fact]
    public void Projection_ExcludesExactRangeMinusOneBoundary()
    {
        Assert.True(RetailRadar.TryProject(
            new Vector3(73.999f, 0f, 0f),
            Vector2.Zero,
            55,
            RetailRadar.OutdoorRangeMeters,
            out _));
        Assert.False(RetailRadar.TryProject(
            new Vector3(74f, 0f, 0f),
            Vector2.Zero,
            55,
            RetailRadar.OutdoorRangeMeters,
            out _));
    }

    [Theory]
    [InlineData(4.999f, 1f)]
    [InlineData(-4.999f, 1f)]
    [InlineData(5f, 0.65f)]
    [InlineData(-5f, 0.65f)]
    public void AltitudeDim_UsesStrictFiveMeterThreshold(float z, float expected)
        => Assert.Equal(expected, RetailRadar.GetAltitudeRgbMultiplier(z));

    [Fact]
    public void CompassTokens_OrbitUsingPhysicalHeading()
    {
        Vector2 center = new(60f, 60f);
        Vector2 size = new(10f, 10f);

        Assert.Equal(
            new Vector2(55f, 0f),
            RetailRadar.GetCompassTokenTopLeft(0f, RadarCompassPoint.North, center, 55f, size));
        Assert.Equal(
            new Vector2(110f, 54f),
            RetailRadar.GetCompassTokenTopLeft(0f, RadarCompassPoint.East, center, 55f, size));
        Assert.Equal(
            new Vector2(55f, 110f),
            RetailRadar.GetCompassTokenTopLeft(0f, RadarCompassPoint.South, center, 55f, size));
        Assert.Equal(
            new Vector2(0f, 55f),
            RetailRadar.GetCompassTokenTopLeft(0f, RadarCompassPoint.West, center, 55f, size));

        Assert.Equal(
            new Vector2(0f, 55f),
            RetailRadar.GetCompassTokenTopLeft(90f, RadarCompassPoint.North, center, 55f, size));
    }

    [Fact]
    public void Coordinates_UseOutdoorLandcellAndRetailAxisOrder()
    {
        uint cellId = LandDefs.LcoordToGid(1358, 1440);

        Assert.True(RadarCoordinates.TryFromCell(cellId, out var coordinates));
        Assert.Equal(33.9, coordinates.X, precision: 10);
        Assert.Equal(42.1, coordinates.Y, precision: 10);
        Assert.Equal("33.9E", coordinates.XText);
        Assert.Equal("42.1N", coordinates.YText);
        Assert.Equal("42.1N,33.9E", coordinates.CombinedText);
    }

    [Fact]
    public void Coordinates_FormatWestSouthAndZeroWithoutDirection()
    {
        uint cellId = LandDefs.LcoordToGid(900, 800);
        Assert.True(RadarCoordinates.TryFromCell(cellId, out var coordinates));
        Assert.Equal("11.9W", coordinates.XText);
        Assert.Equal("21.9S", coordinates.YText);

        cellId = LandDefs.LcoordToGid(1019, 1019);
        Assert.True(RadarCoordinates.TryFromCell(cellId, out coordinates));
        Assert.Equal("0.0", coordinates.XText);
        Assert.Equal("0.0,0.0", coordinates.CombinedText);
    }

    [Fact]
    public void Coordinates_AreUnavailableIndoors()
        => Assert.False(RadarCoordinates.TryFromCell(0xA9B40164u, out _));

    private static void AssertOffsets(RadarBlipShape shape, (int X, int Y)[] expected)
        => Assert.Equal(
            expected.Select(p => new RadarPixelOffset(p.X, p.Y)),
            RetailRadar.GetBlipPixels(shape).ToArray());
}
