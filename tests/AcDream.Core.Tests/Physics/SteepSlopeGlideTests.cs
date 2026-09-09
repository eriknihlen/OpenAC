using System;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public sealed class SteepSlopeGlideTests
{
    private const float DxyPerTick = 0.23f;
    private const int Ticks = 30;

    private const float StartX = 80.4f;
    private const float StartY = 79.8f;

    private static readonly Vector2 Lateral = new(0.70710678f, 0.70710678f);
    private static readonly Vector2 IntoFace = new(-0.70710678f, 0.70710678f);

    [Fact]
    public void Angled45Approach_GlidesAlongTheDiagonal()
    {
        var (finalPos, stuckTicks) = RunApproach(angleFromPerpendicularDeg: 45f);

        float lateral = LateralAdvance(finalPos);
        Assert.True(lateral > 1.0f,
            $"expected the lateral component to survive against the " +
            $"too-steep face (the retail glide), got only {lateral:F3} m " +
            $"along the face over {Ticks} ticks (final=" +
            $"{finalPos.X:F3},{finalPos.Y:F3},{finalPos.Z:F3})");

        Assert.True(finalPos.Z < 1.0f,
            $"expected the mover to stay at the base of the too-steep " +
            $"face, but Z climbed to {finalPos.Z:F3}");
        Assert.True(finalPos.Z > -0.05f,
            $"expected the mover to stay on the flat surface (z=0), but " +
            $"it sank to Z={finalPos.Z:F3}");

        Assert.InRange(stuckTicks, 1, Ticks / 2 + 2);
    }

    [Fact]
    public void SteeperApproachAngle_YieldsMoreLateralAdvance()
    {
        var (pos30, _) = RunApproach(angleFromPerpendicularDeg: 30f);
        var (pos60, _) = RunApproach(angleFromPerpendicularDeg: 60f);

        float lat30 = LateralAdvance(pos30);
        float lat60 = LateralAdvance(pos60);
        Assert.True(lat60 > lat30,
            $"expected the more-angled approach to glide farther " +
            $"(lat60={lat60:F3} m vs lat30={lat30:F3} m)");
    }

    [Fact]
    public void PerpendicularApproach_Stops()
    {
        var (finalPos, _) = RunApproach(angleFromPerpendicularDeg: 0f);

        float lateral = MathF.Abs(LateralAdvance(finalPos));
        Assert.True(lateral < 0.15f,
            $"expected no lateral drift on a perpendicular approach, got " +
            $"{lateral:F3} m");

        float dx = finalPos.X - StartX;
        float dy = finalPos.Y - StartY;
        float xyTravel = MathF.Sqrt(dx * dx + dy * dy);
        Assert.True(xyTravel < 1.2f,
            $"expected the too-steep face to stop the perpendicular " +
            $"approach at its base (~0.4 m away), got {xyTravel:F3} m of " +
            $"travel");
        Assert.True(finalPos.Z < 1.0f,
            $"expected no climb on a perpendicular approach, got " +
            $"Z={finalPos.Z:F3}");
        Assert.True(finalPos.Z > -0.05f,
            $"expected no sink on a perpendicular approach, got " +
            $"Z={finalPos.Z:F3}");
    }

    [Fact]
    public void CellBoundaryFace_Angled45_AlsoGlides()
    {
        var engine = BuildBoundaryFaceEngine();
        var body = NewGroundedBody();

        var position = new Vector3(91f, 36f, 0f);
        uint cell = TerrainSurface.ComputeOutdoorCellId(0xA9B4FFFFu, 91f, 36f);
        float d = DxyPerTick * 0.70710678f;

        for (int tick = 0; tick < 40; tick++)
        {
            var result = engine.ResolveWithTransition(
                currentPos: position,
                targetPos: new Vector3(position.X + d, position.Y + d, position.Z),
                cellId: cell,
                sphereRadius: 0.47f,
                sphereHeight: 1.20f,
                stepUpHeight: 0.60f,
                stepDownHeight: 1.50f,
                isOnGround: true,
                body: body,
                moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: 0x5000000Au);
            position = result.Position;
            cell = result.CellId;
        }

        Assert.True(position.Y - 36f > 0.5f,
            $"expected lateral advance along the boundary face, got " +
            $"{position.Y - 36f:F3} m");
        Assert.True(position.Z < 1.0f,
            $"expected no climb up the boundary face, got Z={position.Z:F3}");
    }

    private static float LateralAdvance(Vector3 finalPos)
        => (finalPos.X - StartX) * Lateral.X + (finalPos.Y - StartY) * Lateral.Y;

    private static (Vector3 FinalPos, int StuckTicks) RunApproach(
        float angleFromPerpendicularDeg)
    {
        var engine = BuildDiagonalFaceEngine();
        var body = NewGroundedBody();

        float rad = angleFromPerpendicularDeg * MathF.PI / 180f;
        Vector2 dir = MathF.Cos(rad) * IntoFace + MathF.Sin(rad) * Lateral;
        float dx = DxyPerTick * dir.X;
        float dy = DxyPerTick * dir.Y;

        var position = new Vector3(StartX, StartY, 0f);
        uint cell = TerrainSurface.ComputeOutdoorCellId(0xA9B4FFFFu, StartX, StartY);
        int stuckTicks = 0;

        for (int tick = 0; tick < Ticks; tick++)
        {
            var result = engine.ResolveWithTransition(
                currentPos: position,
                targetPos: new Vector3(position.X + dx, position.Y + dy, position.Z),
                cellId: cell,
                sphereRadius: 0.47f,
                sphereHeight: 1.20f,
                stepUpHeight: 0.60f,
                stepDownHeight: 1.50f,
                isOnGround: true,
                body: body,
                moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: 0x5000000Au);

            // The stuck-tick predicate, from positions: nonzero XY request,
            // zero XY delivered.
            if (result.Position.X == position.X && result.Position.Y == position.Y)
                stuckTicks++;

            position = result.Position;
            cell = result.CellId;
        }

        return (position, stuckTicks);
    }

    private static PhysicsBody NewGroundedBody() => new()
    {
        State = PhysicsStateFlags.Gravity,
        TransientState = TransientStateFlags.Active | TransientStateFlags.Contact | TransientStateFlags.OnWalkable,
    };

    private static PhysicsEngine BuildDiagonalFaceEngine()
    {
        var heights = new byte[81];
        heights[3 * 9 + 4] = 32;
        return BuildEngine(heights);
    }

    private static PhysicsEngine BuildBoundaryFaceEngine()
    {
        var heights = new byte[81];
        for (int x = 5; x < 9; x++)
            for (int y = 0; y < 9; y++)
                heights[x * 9 + y] = 32;
        return BuildEngine(heights);
    }

    private static PhysicsEngine BuildEngine(byte[] heights)
    {
        var heightTable = new float[256];
        for (int i = 0; i < 256; i++) heightTable[i] = i;

        var engine = new PhysicsEngine();
        engine.AddLandblock(
            0xA9B4FFFFu,
            new TerrainSurface(heights, heightTable),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        return engine;
    }
}
