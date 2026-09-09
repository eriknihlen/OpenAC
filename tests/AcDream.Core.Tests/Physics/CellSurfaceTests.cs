using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.Enums;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class CellSurfaceTests
{
    private static CellSurface MakeFlatFloor(
        uint cellId, float originX, float originY, float z,
        float halfSize = 10f)
    {
        // 4 vertices forming a square floor at the given Z, in WORLD space.
        var vertices = new Dictionary<ushort, Vector3>
        {
            [0] = new(originX - halfSize, originY - halfSize, z),
            [1] = new(originX + halfSize, originY - halfSize, z),
            [2] = new(originX + halfSize, originY + halfSize, z),
            [3] = new(originX - halfSize, originY + halfSize, z),
        };

        // One quad polygon with 4 vertex IDs.
        var polygonVertexIds = new List<List<short>>
        {
            new() { 0, 1, 2, 3 },
        };

        return new CellSurface(cellId, vertices, polygonVertexIds);
    }

    [Fact]
    public void SampleFloorZ_InsideFlat_ReturnsZ()
    {
        var surface = MakeFlatFloor(0x0100, originX: 50f, originY: 50f, z: 10f);

        float? z = surface.SampleFloorZ(50f, 50f);

        Assert.NotNull(z);
        Assert.Equal(10f, z!.Value, precision: 2);
    }

    [Fact]
    public void SampleFloorZ_OutsideFloor_ReturnsNull()
    {
        var surface = MakeFlatFloor(0x0100, originX: 50f, originY: 50f, z: 10f, halfSize: 5f);

        float? z = surface.SampleFloorZ(100f, 100f);

        Assert.Null(z);
    }

    [Fact]
    public void SampleFloorZ_AtEdge_ReturnsZ()
    {
        var surface = MakeFlatFloor(0x0100, originX: 50f, originY: 50f, z: 10f, halfSize: 10f);

        // Right at the edge of the polygon.
        float? z = surface.SampleFloorZ(60f, 50f);

        Assert.NotNull(z);
        Assert.Equal(10f, z!.Value, precision: 2);
    }

    [Fact]
    public void SampleFloorZ_SlopedFloor_InterpolatesZ()
    {
        // A triangular floor that slopes from Z=0 to Z=20.
        var vertices = new Dictionary<ushort, Vector3>
        {
            [0] = new(0f, 0f, 0f),
            [1] = new(20f, 0f, 0f),
            [2] = new(10f, 20f, 20f),
        };
        var polygons = new List<List<short>> { new() { 0, 1, 2 } };
        var surface = new CellSurface(0x0100, vertices, polygons);

        float? z = surface.SampleFloorZ(10f, 6.67f);

        Assert.NotNull(z);
        Assert.InRange(z!.Value, 5f, 8f);  // approximate
    }

    [Fact]
    public void PreparedPolygonTable_MatchesLegacyWorldSurface()
    {
        var vertices = new Dictionary<ushort, Vector3>
        {
            [0] = new(-2f, -2f, 1f),
            [1] = new(2f, -2f, 2f),
            [2] = new(2f, 2f, 4f),
            [3] = new(-2f, 2f, 3f),
        };
        var table = new FlatPolygonTable(
            ImmutableArray.Create(
                new FlatCollisionPolygon(
                    0,
                    default,
                    CullMode.None,
                    4,
                    new FlatIndexRange(0, 4))),
            ImmutableArray.Create(
                vertices[0],
                vertices[1],
                vertices[2],
                vertices[3]));
        Quaternion rotation = Quaternion.CreateFromAxisAngle(
            Vector3.UnitZ,
            0.37f);
        Vector3 translation = new(17f, -9f, 6f);
        var prepared = new CellSurface(
            0x0100,
            table,
            rotation,
            translation);

        Vector3 localSample = new(0.25f, -0.5f, 0f);
        Vector3 worldSample =
            Vector3.Transform(localSample, rotation) + translation;
        var transformedVertices = vertices.ToDictionary(
            static pair => pair.Key,
            pair => Vector3.Transform(pair.Value, rotation) + translation);
        var legacy = new CellSurface(
            0x0100,
            transformedVertices,
            [new List<short> { 0, 1, 2, 3 }]);

        float? expected = legacy.SampleFloorZ(worldSample.X, worldSample.Y);
        float? actual = prepared.SampleFloorZ(worldSample.X, worldSample.Y);

        Assert.Equal(expected.HasValue, actual.HasValue);
        Assert.Equal(
            BitConverter.SingleToInt32Bits(expected!.Value),
            BitConverter.SingleToInt32Bits(actual!.Value));
    }
}
