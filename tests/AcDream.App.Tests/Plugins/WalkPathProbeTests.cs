using System.Collections.Immutable;
using System.Numerics;
using AcDream.App.Plugins;
using AcDream.Core.Physics;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.Tests.Plugins;

/// <summary>The walk probe over a synthetic flat landblock with placed obstacles.</summary>
public sealed class WalkPathProbeTests
{
    private const uint Landblock = 0xA9B40000u;
    private const float Ground = 50f;
    private const uint WalkerId = 1_000_001u;
    private const uint TargetId = 1_000_002u;
    private const uint WallId = 0x7000_0001u;

    private static readonly Vector3 Feet = new(96f, 80f, Ground);

    private static readonly ImmutableArray<FlatCollisionSphere> Human =
    [
        new FlatCollisionSphere(new Vector3(0f, 0f, 0.48f), 0.48f),
        new FlatCollisionSphere(new Vector3(0f, 0f, 1.355f), 0.48f),
    ];

    private static PhysicsEngine FlatField(byte height = (byte)Ground)
    {
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        var heights = new byte[81];
        Array.Fill(heights, height);
        var table = new float[256];
        for (int index = 0; index < table.Length; index++)
            table[index] = index;
        engine.AddLandblock(
            Landblock | 0xFFFFu,
            new TerrainSurface(heights, table),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        return engine;
    }

    private static WalkProbeMover Walker(Vector3? feet = null) => new(
        WalkerId,
        feet ?? Feet,
        TerrainSurface.ComputeOutdoorCellId(Landblock, (feet ?? Feet).X, (feet ?? Feet).Y),
        Human,
        Scale: 1f,
        StepUpHeight: 0.4f,
        StepDownHeight: 0.4f);

    private static void Pillar(PhysicsEngine engine, uint id, Vector3 feet, float radius, float height) =>
        engine.ShadowObjects.Register(
            id, 0u, feet, Quaternion.Identity, radius, 0f, 0f, Landblock,
            ShadowCollisionType.Cylinder, cylHeight: height, isStatic: true);

    private static void Creature(PhysicsEngine engine, uint id, Vector3 feet) =>
        engine.ShadowObjects.Register(
            id, 0u, feet, Quaternion.Identity, 0.5f, 0f, 0f, Landblock,
            ShadowCollisionType.Cylinder, cylHeight: 2f,
            flags: EntityCollisionFlags.HasWeenie | EntityCollisionFlags.IsCreature,
            isStatic: false);

    private static PluginWalkProbeResult Walk(
        PhysicsEngine engine,
        float heading,
        float distance = 20f,
        int maximumChecks = 64,
        uint target = 0u,
        bool diagnostics = false) => WalkPathProbe.Evaluate(
            engine, Walker(), heading, distance, stepDistance: 1f, maximumChecks, target, diagnostics);

    [Fact]
    public void HeadingsFollowTheCompass()
    {
        Assert.InRange(Vector2.Distance(WalkPathProbe.HeadingDirection(0f), new Vector2(0f, 1f)), 0f, 1e-5f);
        Assert.InRange(Vector2.Distance(WalkPathProbe.HeadingDirection(90f), new Vector2(1f, 0f)), 0f, 1e-5f);
        Assert.InRange(Vector2.Distance(WalkPathProbe.HeadingDirection(180f), new Vector2(0f, -1f)), 0f, 1e-5f);
        Assert.InRange(Vector2.Distance(WalkPathProbe.HeadingDirection(270f), new Vector2(-1f, 0f)), 0f, 1e-5f);
    }

    [Fact]
    public void OpenGroundIsWalkedToTheEnd()
    {
        PhysicsEngine engine = FlatField();

        PluginWalkProbeResult result = Walk(engine, heading: 0f, diagnostics: true);

        Assert.Equal(PluginWalkProbeStatus.Clear, result.Status);
        Assert.Equal(20f, result.ClearDistanceMeters);
        Assert.Equal(20, result.CollisionChecks);
        // The body stayed on the ground the whole way.
        Assert.All(result.DebugSamples, sample => Assert.InRange(sample.WorldPosition.Z, Ground - 0.1f, Ground + 0.6f));
        Assert.InRange(result.DebugSamples[^1].WorldPosition.Y, 99.9f, 100.1f);
    }

    [Fact]
    public void AWallStopsTheWalkAndReportsHowFarItGot()
    {
        PhysicsEngine engine = FlatField();
        Pillar(engine, WallId, new Vector3(96f, 90f, Ground), radius: 2f, height: 3f);

        PluginWalkProbeResult north = Walk(engine, heading: 0f);
        PluginWalkProbeResult east = Walk(engine, heading: 90f);

        Assert.Equal(PluginWalkProbeStatus.Blocked, north.Status);
        Assert.Equal(WallId, north.BlockingObjectId);
        Assert.InRange(north.ClearDistanceMeters, 5f, 8.5f);
        Assert.Equal(PluginWalkProbeStatus.Clear, east.Status);
    }

    [Fact]
    public void CreaturesInTheWayAreIgnored()
    {
        PhysicsEngine engine = FlatField();
        Creature(engine, TargetId, new Vector3(96f, 90f, Ground));

        PluginWalkProbeResult result = Walk(engine, heading: 0f, target: TargetId);

        Assert.Equal(PluginWalkProbeStatus.Clear, result.Status);
    }

    [Fact]
    public void ALowStepIsClimbedButAWaistHighLedgeIsNot()
    {
        PhysicsEngine engine = FlatField();
        // A knee-high block to step onto, then a chest-high one.
        Pillar(engine, WallId, new Vector3(96f, 88f, Ground), radius: 1.5f, height: 0.3f);
        Pillar(engine, WallId + 1u, new Vector3(96f, 95f, Ground), radius: 1.5f, height: 1.2f);

        PluginWalkProbeResult result = Walk(engine, heading: 0f, diagnostics: true);

        Assert.Equal(PluginWalkProbeStatus.Blocked, result.Status);
        Assert.Equal(WallId + 1u, result.BlockingObjectId);
        Assert.InRange(result.ClearDistanceMeters, 10f, 14f);
        Assert.Contains(result.DebugSamples, sample => sample.WorldPosition.Z > Ground + 0.2f);
    }

    [Fact]
    public void ACliffEdgeIsNotWalkedOff()
    {
        PhysicsEngine engine = FlatField();
        // Standing on a five-meter plinth: the far edge is a fall, not a step.
        Pillar(engine, WallId, new Vector3(96f, 80f, Ground), radius: 3f, height: 5f);
        WalkProbeMover onTop = Walker(new Vector3(96f, 80f, Ground + 5f));

        PluginWalkProbeResult result = WalkPathProbe.Evaluate(
            engine, onTop, 0f, 20f, stepDistance: 1f, maximumChecks: 64, captureDiagnostics: true);

        Assert.Equal(PluginWalkProbeStatus.Blocked, result.Status);
        Assert.InRange(result.ClearDistanceMeters, 1.5f, 3.5f);
        Assert.All(result.DebugSamples, sample => Assert.InRange(sample.WorldPosition.Z, Ground + 4.9f, Ground + 5.1f));
    }

    [Fact]
    public void BudgetAndBadRequestsAreReported()
    {
        PhysicsEngine engine = FlatField();

        PluginWalkProbeResult budget = Walk(engine, heading: 0f, distance: 20f, maximumChecks: 3);
        Assert.Equal(PluginWalkProbeStatus.BudgetExceeded, budget.Status);
        Assert.Equal(3f, budget.ClearDistanceMeters);

        Assert.Equal(PluginWalkProbeStatus.Error, Walk(engine, heading: float.NaN).Status);
        Assert.Equal(PluginWalkProbeStatus.Error, Walk(engine, heading: 0f, distance: 0f).Status);
        Assert.Equal(
            PluginWalkProbeStatus.Error,
            WalkPathProbe.Evaluate(engine, Walker() with { CellId = 0u }, 0f, 5f, 1f, 8).Status);
    }
}
