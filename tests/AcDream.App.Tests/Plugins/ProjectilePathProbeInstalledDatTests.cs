using System.Collections.Immutable;
using System.Numerics;
using AcDream.App.Plugins;
using AcDream.App.Tests.Rendering;
using AcDream.Core.Physics;
using AcDream.Plugin.Abstractions;
using DatReaderWriter;
using DatReaderWriter.Options;
using DatReaderWriter.Types;
using Xunit.Abstractions;
using DatEnvCell = DatReaderWriter.DBObjs.EnvCell;
using DatEnvironment = DatReaderWriter.DBObjs.Environment;
using AcDream.Automation;

namespace AcDream.App.Tests.Plugins;

/// <summary>
/// The projectile sweep against real dungeon walls from the installed cell
/// dat. Skips silently when no dat directory is installed (see
/// <see cref="InstalledDatTestPath"/>). The dungeon is whichever landblock
/// in a fixed band first offers a room, so the test does not depend on any
/// particular dungeon surviving a dat update.
/// </summary>
[Trait("Lane", "InstalledDat")]
public sealed class ProjectilePathProbeInstalledDatTests(ITestOutputHelper output)
{
    private const uint ShooterId = 1_000_001u;
    private const uint TargetId = 1_000_002u;

    private sealed record Dungeon(
        PhysicsEngine Engine,
        uint LandblockId,
        Dictionary<uint, (Vector3 Min, Vector3 Max)> Rooms);

    [Fact]
    public void InsideOneRoomAShortShotIsClear()
    {
        if (Load() is not { } dungeon)
            return;
        (uint cellId, Vector3 center, float halfWidth) = LargestRoom(dungeon);
        Vector3 feet = center with { Z = FloorBelow(dungeon.Engine, cellId, center) };
        Vector3 targetFeet = feet + new Vector3(MathF.Min(1.5f, halfWidth * 0.5f), 0f, 0f);
        output.WriteLine($"room 0x{cellId:X8} feet={feet} target={targetFeet}");

        PluginProjectilePathResult result = ProjectilePathProbe.Evaluate(
            dungeon.Engine,
            new ProjectilePathEndpoint(ShooterId, feet, cellId, 1.8f),
            new ProjectilePathEndpoint(TargetId, targetFeet, cellId, 1.8f),
            PluginProjectilePathKind.Straight,
            PluginAttackHeight.Medium,
            radius: 0.25f,
            stepDistance: 0.5f,
            maximumChecks: 32,
            captureDiagnostics: true);

        output.WriteLine($"{result.Status} checks={result.CollisionChecks} notice={result.Notice}");
        Assert.Equal(PluginProjectilePathStatus.Clear, result.Status);
    }

    [Fact]
    public void AShotAimedThroughTheCeilingIsBlockedByTheEnvironment()
    {
        if (Load() is not { } dungeon)
            return;
        (uint cellId, Vector3 center, _) = LargestRoom(dungeon);
        Vector3 feet = center with { Z = FloorBelow(dungeon.Engine, cellId, center) };
        // Same room, but the aim point is far above any dungeon ceiling.
        Vector3 targetFeet = feet + new Vector3(0.5f, 0f, 60f);

        PluginProjectilePathResult result = ProjectilePathProbe.Evaluate(
            dungeon.Engine,
            new ProjectilePathEndpoint(ShooterId, feet, cellId, 1.8f),
            new ProjectilePathEndpoint(TargetId, targetFeet, cellId, 1.8f),
            PluginProjectilePathKind.Straight,
            PluginAttackHeight.Low,
            radius: 0.25f,
            stepDistance: 0.5f,
            maximumChecks: 256,
            captureDiagnostics: true);

        output.WriteLine($"{result.Status} checks={result.CollisionChecks} notice={result.Notice} stop={(result.DebugSamples.Count > 0 ? result.DebugSamples[^1].WorldPosition : default)}");
        Assert.Equal(PluginProjectilePathStatus.Blocked, result.Status);
        Assert.Equal("environment", result.Notice);
        Assert.True(result.DebugSamples[^1].WorldPosition.Z < targetFeet.Z, "the sweep stopped at the ceiling");
    }

    [Fact]
    public void AShotAtAPointBeyondEveryRoomIsBlockedByAWall()
    {
        if (Load() is not { } dungeon)
            return;
        (uint cellId, Vector3 center, _) = LargestRoom(dungeon);
        Vector3 feet = center with { Z = FloorBelow(dungeon.Engine, cellId, center) };
        float farthestX = dungeon.Rooms.Values.Max(static room => room.Max.X);
        Vector3 targetFeet = feet with { X = farthestX + 10f };

        PluginProjectilePathResult result = ProjectilePathProbe.Evaluate(
            dungeon.Engine,
            new ProjectilePathEndpoint(ShooterId, feet, cellId, 1.8f),
            new ProjectilePathEndpoint(TargetId, targetFeet, cellId, 1.8f),
            PluginProjectilePathKind.Straight,
            PluginAttackHeight.Medium,
            radius: 0.25f,
            stepDistance: 0.5f,
            maximumChecks: 1024);

        output.WriteLine($"{result.Status} checks={result.CollisionChecks} notice={result.Notice}");
        Assert.Equal(PluginProjectilePathStatus.Blocked, result.Status);
        Assert.Equal("environment", result.Notice);
    }

    [Fact]
    public void WalkingAcrossTheRoomIsClearButWalkingIntoTheWallIsNot()
    {
        if (Load() is not { } dungeon)
            return;
        (uint cellId, Vector3 center, float halfWidth) = LargestRoom(dungeon);
        Vector3 feet = center with { Z = FloorBelow(dungeon.Engine, cellId, center) };
        var walker = new WalkProbeMover(
            ShooterId, feet, cellId,
            [
                new FlatCollisionSphere(new Vector3(0f, 0f, 0.48f), 0.48f),
                new FlatCollisionSphere(new Vector3(0f, 0f, 1.355f), 0.48f),
            ],
            Scale: 1f, StepUpHeight: 0.4f, StepDownHeight: 0.4f);
        float farthestX = dungeon.Rooms.Values.Max(static room => room.Max.X);

        PluginWalkProbeResult across = WalkPathProbe.Evaluate(
            dungeon.Engine, walker, 90f, MathF.Max(0.5f, halfWidth * 0.5f), 0.5f, 32, captureDiagnostics: true);
        PluginWalkProbeResult intoTheWall = WalkPathProbe.Evaluate(
            dungeon.Engine, walker, 90f, farthestX - feet.X + 10f, 0.5f, 1024, captureDiagnostics: true);

        output.WriteLine($"across: {across.Status} clear={across.ClearDistanceMeters}");
        output.WriteLine($"wall: {intoTheWall.Status} clear={intoTheWall.ClearDistanceMeters} notice={intoTheWall.Notice}");
        Assert.Equal(PluginWalkProbeStatus.Clear, across.Status);
        Assert.Equal(PluginWalkProbeStatus.Blocked, intoTheWall.Status);
        Assert.True(intoTheWall.ClearDistanceMeters < farthestX - feet.X, "stopped inside the dungeon");
    }

    private Dungeon? Load()
    {
        string? datDirectory = InstalledDatTestPath.Resolve();
        if (datDirectory is null
            || !File.Exists(Path.Combine(datDirectory, "client_cell_1.dat"))
            || !File.Exists(Path.Combine(datDirectory, "client_portal.dat")))
        {
            output.WriteLine("no installed dat directory with cell and portal dats; skipping");
            return null;
        }
        using var dats = new DatCollection(new DatCollectionOptions
        {
            DatDirectory = datDirectory,
            AccessType = DatAccessType.Read,
            IndexCachingStrategy = IndexCachingStrategy.OnDemand,
            FileCachingStrategy = FileCachingStrategy.Never,
        });

        // Dungeons live in the low landblock band; take the first one with
        // a handful of rooms.
        for (uint landblock = 0x0001u; landblock <= 0x02FFu; landblock++)
        {
            uint landblockId = landblock << 16;
            if (dats.Get<DatEnvCell>(landblockId | 0x0100u) is null)
                continue;
            Dungeon? dungeon = Build(dats, landblockId);
            if (dungeon is not null && dungeon.Rooms.Count >= 4)
                return dungeon;
        }
        output.WriteLine("no dungeon found in the scanned band; skipping");
        return null;
    }

    private static Dungeon? Build(DatCollection dats, uint landblockId)
    {
        var cache = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };
        var rooms = new Dictionary<uint, (Vector3, Vector3)>();
        for (uint low = 0x0100u; low <= 0x01FFu; low++)
        {
            uint id = landblockId | low;
            DatEnvCell? datCell = dats.Get<DatEnvCell>(id);
            if (datCell is null)
                break;
            DatEnvironment? environment = dats.Get<DatEnvironment>(0x0D000000u | datCell.EnvironmentId);
            if (environment is null
                || !environment.Cells.TryGetValue(datCell.CellStructure, out CellStruct? cellStruct)
                || cellStruct is null)
            {
                continue;
            }
            Matrix4x4 world = Matrix4x4.CreateFromQuaternion(datCell.Position.Orientation)
                * Matrix4x4.CreateTranslation(datCell.Position.Origin);
            cache.CacheCellStruct(id, datCell, cellStruct, world);

            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (SWVertex vertex in cellStruct.VertexArray.Vertices.Values)
            {
                Vector3 point = Vector3.Transform(vertex.Origin, world);
                min = Vector3.Min(min, point);
                max = Vector3.Max(max, point);
            }
            if (min.X != float.MaxValue)
                rooms[id] = (min, max);
        }
        if (rooms.Count == 0)
            return null;
        var heights = new byte[81];
        var heightTable = new float[256];
        Array.Fill(heightTable, -1000f);
        engine.AddLandblock(
            landblockId | 0xFFFFu,
            new TerrainSurface(heights, heightTable),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            0f,
            0f);
        return new Dungeon(engine, landblockId, rooms);
    }

    /// <summary>The room with the biggest floor, its center, and half its narrower side.</summary>
    private static (uint CellId, Vector3 Center, float HalfWidth) LargestRoom(Dungeon dungeon)
    {
        uint best = 0u;
        float bestArea = -1f;
        foreach ((uint id, (Vector3 min, Vector3 max)) in dungeon.Rooms)
        {
            float area = (max.X - min.X) * (max.Y - min.Y);
            if (area > bestArea)
            {
                bestArea = area;
                best = id;
            }
        }
        (Vector3 roomMin, Vector3 roomMax) = dungeon.Rooms[best];
        Vector3 center = (roomMin + roomMax) * 0.5f;
        float halfWidth = MathF.Min(roomMax.X - roomMin.X, roomMax.Y - roomMin.Y) * 0.5f;
        return (best, center, halfWidth);
    }

    /// <summary>Drop a probe from the room's mid-height to find the floor under a point.</summary>
    private static float FloorBelow(PhysicsEngine engine, uint cellId, Vector3 point)
    {
        ResolveResult drop = engine.ResolveWithTransition(
            point,
            point with { Z = point.Z - 40f },
            cellId,
            sphereRadius: 0.25f,
            sphereHeight: 0f,
            stepUpHeight: 0f,
            stepDownHeight: 0f,
            isOnGround: false,
            moverFlags: ObjectInfoState.PathClipped);
        // Feet sit on the contact, a little above the sphere's stop point.
        return drop.Position.Z - 0.25f + 0.05f;
    }
}
