using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

/// <summary>
/// OpenAC #43: the open sea is a wall. A landblock whose every cell is under
/// water refuses any mover except the camera and missiles.
/// </summary>
public sealed class OpenSeaBarrierTests
{
    private const uint Landblock = 0xA9B4FFFFu;
    private const byte WaterVertex = 0x10 << 2;   // a water terrain type in the surface bits
    private const byte LandVertex = 0x01 << 2;    // any dry terrain type

    [Fact]
    public void Player_CannotWalkOntoTheOpenSea()
    {
        PhysicsEngine engine = Engine(Surface(WaterVertex));

        ResolveResult result = Step(engine, ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide);

        // The step is refused at the shore: the mover is held where it stood.
        Assert.Equal(Start.X, result.Position.X, 3);
        Assert.Equal(Start.Y, result.Position.Y, 3);
    }

    [Fact]
    public void CameraAndMissiles_CrossTheOpenSea()
    {
        PhysicsEngine engine = Engine(Surface(WaterVertex));

        ResolveResult viewer = Step(engine, ObjectInfoState.IsViewer | ObjectInfoState.PathClipped);
        Assert.True(viewer.Ok);
        Assert.True(viewer.Position.X > Start.X + 0.9f, $"camera held at x={viewer.Position.X:F3}");

        var missile = new PhysicsBody { State = PhysicsStateFlags.Missile | PhysicsStateFlags.Gravity };
        missile.SnapToCell(Cell, Start, Start);
        ResolveResult arrow = Step(engine, ObjectInfoState.PathClipped, missile);
        Assert.True(arrow.Ok);
        Assert.True(arrow.Position.X > Start.X + 0.9f, $"missile held at x={arrow.Position.X:F3}");
    }

    [Fact]
    public void Player_CanWadeWhereTheBlockIsOnlyPartlyWater()
    {
        byte[] shore = FilledTerrain(WaterVertex);
        shore[0] = LandVertex;
        PhysicsEngine engine = Engine(Surface(shore));

        ResolveResult result = Step(engine, ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide);

        Assert.True(result.Ok);
        Assert.True(result.Position.X > Start.X + 0.3f, $"wader held at x={result.Position.X:F3}");
    }

    private static readonly Vector3 Start = new(100f, 100f, 0f);
    private const uint Cell = 0xA9B40025u;          // (100, 100) → column 4, row 4

    private static ResolveResult Step(
        PhysicsEngine engine, ObjectInfoState moverFlags, PhysicsBody? body = null) =>
        engine.ResolveWithTransition(
            currentPos: Start,
            targetPos: Start + new Vector3(1f, 0f, 0f),
            cellId: Cell,
            sphereRadius: 0.48f,
            sphereHeight: 1.835f,
            stepUpHeight: 0.4f,
            stepDownHeight: 0.4f,
            isOnGround: true,
            body: body,
            moverFlags: moverFlags);

    private static PhysicsEngine Engine(TerrainSurface terrain)
    {
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        engine.AddLandblock(
            Landblock, terrain, Array.Empty<CellSurface>(), Array.Empty<PortalPlane>(),
            worldOffsetX: 0f, worldOffsetY: 0f);
        return engine;
    }

    private static byte[] FilledTerrain(byte vertex)
    {
        var terrain = new byte[81];
        Array.Fill(terrain, vertex);
        return terrain;
    }

    private static TerrainSurface Surface(byte vertex) => Surface(FilledTerrain(vertex));

    private static TerrainSurface Surface(byte[] terrain) =>
        new(new byte[81], new float[256], 0xA9u, 0xB4u, terrain);
}
