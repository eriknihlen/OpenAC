using System.Numerics;
using AcDream.App.Plugins;
using AcDream.Core.Physics;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.Tests.Plugins;

/// <summary>
/// Drives the projectile sweep over a synthetic flat landblock so every
/// obstacle and body is placed by the test. Real geometry is covered by
/// <see cref="ProjectilePathProbeInstalledDatTests"/>.
/// </summary>
public sealed class ProjectilePathProbeTests
{
    private const uint Landblock = 0xA9B40000u;
    private const float Ground = 50f;
    private const uint ShooterId = 1_000_001u;
    private const uint TargetId = 1_000_002u;
    private const uint WallId = 0x7000_0001u;
    private const uint BystanderId = 1_000_003u;

    private static readonly Vector3 ShooterFeet = new(96f, 80f, Ground);
    private static readonly Vector3 TargetFeet = new(96f, 100f, Ground);

    private static PhysicsEngine FlatField()
    {
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        var heights = new byte[81];
        Array.Fill(heights, (byte)Ground);
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

    private static uint CellOf(Vector3 world) =>
        TerrainSurface.ComputeOutdoorCellId(Landblock, world.X, world.Y);

    private static ProjectilePathEndpoint Endpoint(uint id, Vector3 feet, float height = 1.8f) =>
        new(id, feet, CellOf(feet), height);

    private static void Creature(PhysicsEngine engine, uint id, Vector3 feet, float radius = 0.4f, float height = 1.8f) =>
        engine.ShadowObjects.Register(
            id, 0u, feet, Quaternion.Identity, radius, 0f, 0f, Landblock,
            ShadowCollisionType.Cylinder, cylHeight: height,
            flags: EntityCollisionFlags.HasWeenie | EntityCollisionFlags.IsCreature,
            isStatic: false);

    private static void Pillar(PhysicsEngine engine, uint id, Vector3 feet, float radius, float height) =>
        engine.ShadowObjects.Register(
            id, 0u, feet, Quaternion.Identity, radius, 0f, 0f, Landblock,
            ShadowCollisionType.Cylinder, cylHeight: height, isStatic: true);

    private static PluginProjectilePathResult Evaluate(
        PhysicsEngine engine,
        PluginProjectilePathKind kind = PluginProjectilePathKind.Straight,
        PluginAttackHeight height = PluginAttackHeight.Medium,
        float launchSpeed = 0f,
        int maximumChecks = 128,
        float step = 1f,
        bool diagnostics = false) => ProjectilePathProbe.Evaluate(
            engine,
            Endpoint(ShooterId, ShooterFeet),
            Endpoint(TargetId, TargetFeet),
            kind,
            height,
            radius: 0.25f,
            stepDistance: step,
            maximumChecks,
            launchSpeed,
            diagnostics);

    [Fact]
    public void OpenFieldIsClearAndCountsItsChecks()
    {
        PhysicsEngine engine = FlatField();

        PluginProjectilePathResult result = Evaluate(engine);

        Assert.Equal(PluginProjectilePathStatus.Clear, result.Status);
        Assert.InRange(result.CollisionChecks, 18, 22);
        Assert.Equal(0u, result.BlockingObjectId);
    }

    [Fact]
    public void ReachingTheTargetBodyCountsAsClearAndNamesIt()
    {
        PhysicsEngine engine = FlatField();
        Creature(engine, TargetId, TargetFeet);

        PluginProjectilePathResult result = Evaluate(engine, diagnostics: true);

        Assert.Equal(PluginProjectilePathStatus.Clear, result.Status);
        Assert.Equal(TargetId, result.BlockingObjectId);
        // The sweep stopped on the body, short of the aim point inside it.
        Assert.InRange(result.CollisionChecks, 1, 20);
        Assert.NotEmpty(result.DebugSamples);
        Assert.All(result.DebugSamples, static sample => Assert.True(sample.IsClear));
    }

    [Fact]
    public void AStaticObstacleBlocksAndIsReported()
    {
        PhysicsEngine engine = FlatField();
        Creature(engine, TargetId, TargetFeet);
        Pillar(engine, WallId, new Vector3(96f, 90f, Ground), radius: 1.5f, height: 4f);

        PluginProjectilePathResult result = Evaluate(engine, diagnostics: true);

        Assert.Equal(PluginProjectilePathStatus.Blocked, result.Status);
        Assert.Equal(WallId, result.BlockingObjectId);
        Assert.InRange(result.DebugSamples[^1].WorldPosition.Y, 80f, 90f);
        Assert.False(result.DebugSamples[^1].IsClear);
    }

    [Fact]
    public void AnotherCreatureInTheWayIsIgnoredForADesignatedTarget()
    {
        PhysicsEngine engine = FlatField();
        Creature(engine, TargetId, TargetFeet);
        Creature(engine, BystanderId, new Vector3(96f, 90f, Ground), radius: 1f, height: 3f);

        PluginProjectilePathResult result = Evaluate(engine);

        Assert.Equal(PluginProjectilePathStatus.Clear, result.Status);
        Assert.Equal(TargetId, result.BlockingObjectId);
    }

    [Fact]
    public void TheShooterOwnBodyDoesNotBlockTheLaunch()
    {
        PhysicsEngine engine = FlatField();
        Creature(engine, ShooterId, ShooterFeet, radius: 1.2f, height: 2.5f);
        Creature(engine, TargetId, TargetFeet);

        PluginProjectilePathResult result = Evaluate(engine);

        Assert.Equal(PluginProjectilePathStatus.Clear, result.Status);
        Assert.Equal(TargetId, result.BlockingObjectId);
    }

    [Fact]
    public void ASlowArcLobsOverAWallThatBlocksTheStraightShot()
    {
        PhysicsEngine engine = FlatField();
        Creature(engine, TargetId, TargetFeet);
        Pillar(engine, WallId, new Vector3(96f, 90f, Ground), radius: 0.5f, height: 3f);

        PluginProjectilePathResult straight = Evaluate(engine, PluginProjectilePathKind.Straight);
        PluginProjectilePathResult fastArc = Evaluate(engine, PluginProjectilePathKind.Arc);
        PluginProjectilePathResult slowArc = Evaluate(
            engine, PluginProjectilePathKind.Arc, launchSpeed: 8f, diagnostics: true);

        Assert.Equal(PluginProjectilePathStatus.Blocked, straight.Status);
        Assert.Equal(WallId, straight.BlockingObjectId);
        // The default arc is nearly flat over twenty meters.
        Assert.Equal(PluginProjectilePathStatus.Blocked, fastArc.Status);
        Assert.Equal(PluginProjectilePathStatus.Clear, slowArc.Status);
        float apex = slowArc.DebugSamples.Max(static sample => sample.WorldPosition.Z);
        Assert.True(apex > Ground + 3f, $"apex {apex} should clear the wall");
        // ...and it still comes back down onto the target.
        Assert.Equal(TargetId, slowArc.BlockingObjectId);
    }

    [Fact]
    public void AimHeightsLandOnTheBodyNotAboveOrBelowIt()
    {
        PhysicsEngine engine = FlatField();
        Creature(engine, TargetId, TargetFeet, radius: 0.4f, height: 1.8f);

        foreach (PluginAttackHeight height in new[]
                 {
                     PluginAttackHeight.Low, PluginAttackHeight.Medium, PluginAttackHeight.High,
                 })
        {
            PluginProjectilePathResult result = Evaluate(engine, height: height, diagnostics: true);
            Assert.Equal(PluginProjectilePathStatus.Clear, result.Status);
            Assert.Equal(TargetId, result.BlockingObjectId);
            float z = result.DebugSamples[^1].WorldPosition.Z;
            Assert.InRange(z, Ground + 0.2f, Ground + 1.8f);
        }
    }

    [Fact]
    public void ExhaustingTheBudgetIsReportedNotMistakenForClear()
    {
        PhysicsEngine engine = FlatField();

        PluginProjectilePathResult result = Evaluate(engine, maximumChecks: 3, step: 1f);

        Assert.Equal(PluginProjectilePathStatus.BudgetExceeded, result.Status);
        Assert.Equal(3, result.CollisionChecks);
        Assert.False(result.IsClear);
    }

    [Fact]
    public void DegenerateInputsAreInvalid()
    {
        PhysicsEngine engine = FlatField();
        ProjectilePathEndpoint shooter = Endpoint(ShooterId, ShooterFeet);

        Assert.Equal(
            PluginProjectilePathStatus.InvalidTarget,
            ProjectilePathProbe.Evaluate(
                engine, shooter, shooter with { EntityId = TargetId },
                PluginProjectilePathKind.Straight, PluginAttackHeight.Medium, 0.25f, 1f, 16).Status);
        Assert.Equal(
            PluginProjectilePathStatus.InvalidTarget,
            ProjectilePathProbe.Evaluate(
                engine, shooter with { CellId = 0u }, Endpoint(TargetId, TargetFeet),
                PluginProjectilePathKind.Straight, PluginAttackHeight.Medium, 0.25f, 1f, 16).Status);
        Assert.Equal(
            PluginProjectilePathStatus.InvalidTarget,
            ProjectilePathProbe.Evaluate(
                engine, shooter, Endpoint(TargetId, TargetFeet),
                PluginProjectilePathKind.Straight, PluginAttackHeight.Medium, 0f, 1f, 16).Status);
    }

    [Fact]
    public void HeightIsMeasuredFromTheRegisteredBody()
    {
        PhysicsEngine engine = FlatField();
        Creature(engine, TargetId, TargetFeet, radius: 0.6f, height: 2.4f);

        Assert.True(ProjectilePathProbe.TryMeasureHeight(engine, TargetId, TargetFeet, out float height));
        Assert.InRange(height, 2.39f, 2.41f);
        Assert.False(ProjectilePathProbe.TryMeasureHeight(engine, BystanderId, TargetFeet, out _));
    }
}
