using AcDream.App.Input;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.Runtime.Entities;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.World;

/// <summary>
/// A kill in a dungeon: the creature dies in view and the server deletes it;
/// its corpse is created in view; the player walks on. Once the corpse's
/// cell has left the player's PVS the server forgets the corpse after 25 s
/// and never sends its decay, so the client's own 25-second destruction is
/// what returns the entity and animation counts to baseline. Before the fix
/// the client judged visibility by landblock, and a dungeon is one
/// landblock, so every such corpse stayed forever.
/// </summary>
public sealed class DungeonCorpseExpiryTests
{
    private const uint Landblock = 0x6145FFFFu;
    private const uint PlayerGuid = 0x50000003u;
    private const uint CreatureGuid = 0x80000CD4u;
    private const uint CorpseGuid = 0x800090A7u;

    /// <summary>The player's fighting spot.</summary>
    private const uint FightCell = 0x6145031Du;
    /// <summary>On the fighting spot's PVS list.</summary>
    private const uint NearCell = 0x61450317u;
    /// <summary>Same dungeon, not on the fighting spot's list.</summary>
    private const uint FarCell = 0x614502B6u;

    private static readonly Dictionary<uint, LiveEntityEnvCellVisibility> Cells = new()
    {
        [FightCell] = new(SeenOutside: false, new HashSet<uint> { NearCell }),
        [NearCell] = new(SeenOutside: false, new HashSet<uint> { FightCell }),
        [FarCell] = new(SeenOutside: false, new HashSet<uint>()),
    };

    [Fact]
    public void CorpseLeftBehindInTheDungeon_ExpiresLikeTheServerForgetsIt()
    {
        var world = new World();
        int baselineEntities = world.Spatial.Entities.Count;
        int baselineAnimations = world.Runtime.SpatialAnimationRuntimeCount;
        int baselineRecords = world.Runtime.Count;

        world.SpawnAnimated(CreatureGuid, FightCell);
        Assert.Equal(baselineEntities + 1, world.Spatial.Entities.Count);
        Assert.Equal(baselineAnimations + 1, world.Runtime.SpatialAnimationRuntimeCount);

        // The creature dies in view: its delete arrives and completes.
        Assert.True(world.Deletion.Delete(new DeleteObject.Parsed(CreatureGuid, 0)));
        Assert.Equal(baselineEntities, world.Spatial.Entities.Count);
        Assert.Equal(baselineAnimations, world.Runtime.SpatialAnimationRuntimeCount);

        // Its corpse is created where it fell, in the Dead pose.
        world.SpawnAnimated(CorpseGuid, FightCell);
        Assert.Equal(baselineEntities + 1, world.Spatial.Entities.Count);
        Assert.Equal(baselineAnimations + 1, world.Runtime.SpatialAnimationRuntimeCount);

        // The player walks deeper into the dungeon; the corpse's cell is no
        // longer on the player's PVS list. The server queues the corpse for
        // destruction and no DeleteObject will ever come for it.
        Assert.True(world.Runtime.RebucketLiveEntity(PlayerGuid, FarCell));

        world.Liveness.Tick(100.0);
        world.Liveness.Tick(124.0);
        Assert.Equal(baselineEntities + 1, world.Spatial.Entities.Count);
        Assert.Equal(baselineAnimations + 1, world.Runtime.SpatialAnimationRuntimeCount);

        world.Liveness.Tick(126.0);

        Assert.Equal(baselineEntities, world.Spatial.Entities.Count);
        Assert.Equal(baselineAnimations, world.Runtime.SpatialAnimationRuntimeCount);
        Assert.Equal(baselineRecords, world.Runtime.Count);
        Assert.False(world.Runtime.TryGetRecord(CorpseGuid, out _));
        Assert.Null(world.Objects.Get(CorpseGuid));
    }

    [Fact]
    public void CorpseStillOnThePlayersPvsList_IsKept()
    {
        var world = new World();
        int baselineEntities = world.Spatial.Entities.Count;

        world.SpawnAnimated(CorpseGuid, FightCell);
        Assert.True(world.Runtime.RebucketLiveEntity(PlayerGuid, NearCell));

        world.Liveness.Tick(100.0);
        world.Liveness.Tick(130.0);
        world.Liveness.Tick(160.0);

        Assert.Equal(baselineEntities + 1, world.Spatial.Entities.Count);
        Assert.True(world.Runtime.TryGetRecord(CorpseGuid, out _));
        Assert.NotNull(world.Objects.Get(CorpseGuid));
    }

    [Fact]
    public void PlayerInACellTheClientHasNotLoaded_CannotExpireAnything()
    {
        var world = new World();
        int baselineEntities = world.Spatial.Entities.Count;

        world.SpawnAnimated(CorpseGuid, FarCell);
        Assert.True(world.Runtime.RebucketLiveEntity(PlayerGuid, 0x614503F0u));

        world.Liveness.Tick(100.0);
        world.Liveness.Tick(130.0);
        world.Liveness.Tick(160.0);

        Assert.Equal(baselineEntities + 1, world.Spatial.Entities.Count);
        Assert.True(world.Runtime.TryGetRecord(CorpseGuid, out _));
    }

    private sealed class World
    {
        public readonly GpuWorldState Spatial = new();
        public readonly RuntimeEntityObjectLifetime EntityObjects = new();
        public readonly LiveEntityRuntime Runtime;
        public readonly LiveEntityDeletionController Deletion;
        public readonly LiveEntityLivenessController Liveness;

        public ClientObjectTable Objects => EntityObjects.Objects;

        public World()
        {
            EntityObjects.BindEventContext(
                static () => new AcDream.Runtime.RuntimeGenerationToken(1UL),
                static () => 1UL);
            Spatial.AddLandblock(new LoadedLandblock(
                Landblock,
                new LandBlock(),
                Array.Empty<WorldEntity>()));
            var teardown = new Teardown();
            Runtime = new LiveEntityRuntime(
                Spatial,
                new Resources(),
                teardown,
                EntityObjects);
            var identity = new LocalPlayerIdentityState
            {
                ServerGuid = PlayerGuid,
            };
            Deletion = new LiveEntityDeletionController(
                Runtime,
                EntityObjects,
                teardown,
                identity);
            Liveness = new LiveEntityLivenessController(
                Runtime,
                identity,
                Deletion,
                new EnvCellSource());

            Runtime.RegisterLiveEntity(Spawn(PlayerGuid, FightCell), isLocalPlayer: true);
            Assert.NotNull(Runtime.MaterializeLiveEntity(
                PlayerGuid,
                FightCell,
                id => Entity(id, PlayerGuid)));
        }

        public void SpawnAnimated(uint guid, uint cell)
        {
            WorldSession.EntitySpawn spawn = Spawn(guid, cell);
            LiveEntityRegistrationResult registration =
                Runtime.RegisterLiveEntity(spawn);
            RuntimeEntityRecord canonical = registration.Canonical!;
            Assert.True(EntityObjects.ApplyAcceptedSpawn(
                canonical,
                canonical.CreateIntegrationVersion,
                spawn,
                replaceGeneration: false));
            Assert.NotNull(Objects.Get(guid));
            WorldEntity entity = Runtime.MaterializeLiveEntity(
                guid,
                cell,
                id => Entity(id, guid))!;
            Runtime.SetAnimationRuntime(guid, new Animation(entity));
        }

        private sealed class Resources : ILiveEntityResourceLifecycle
        {
            public void Register(WorldEntity entity)
            {
            }

            public void Unregister(WorldEntity entity)
            {
            }
        }

        private sealed class Teardown : ILiveEntityTeardownCoordinator
        {
            public void TearDown(LiveEntityRecord record)
            {
            }

            public void ForgetUnknownOwner(uint serverGuid)
            {
            }
        }

        private sealed class EnvCellSource : ILiveEntityEnvCellSource
        {
            public LiveEntityEnvCellVisibility? GetEnvCell(uint cellId) =>
                Cells.TryGetValue(cellId, out LiveEntityEnvCellVisibility cell) ? cell : null;
        }

        private sealed class Animation(WorldEntity entity) : ILiveEntityAnimationRuntime
        {
            public WorldEntity Entity { get; } = entity;
            public uint CurrentMotion => 0x40000004u;
        }
    }

    private static WorldEntity Entity(uint id, uint guid) => new()
    {
        Id = id,
        ServerGuid = guid,
        SourceGfxObjOrSetupId = 0x02000001u,
        Position = System.Numerics.Vector3.Zero,
        Rotation = System.Numerics.Quaternion.Identity,
        MeshRefs = Array.Empty<MeshRef>(),
    };

    private static WorldSession.EntitySpawn Spawn(uint guid, uint cell)
    {
        var position = new CreateObject.ServerPosition(
            cell, 10f, 10f, 5f, 1f, 0f, 0f, 0f);
        var timestamps = new PhysicsTimestamps(1, 1, 1, 1, 0, 1, 0, 1, 0);
        var physics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.ReportCollisions,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x02000001u,
            MotionTableId: 0x09000001u,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: null,
            Friction: null,
            Elasticity: null,
            Translucency: null,
            Velocity: null,
            Acceleration: null,
            AngularVelocity: null,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            guid,
            position,
            0x02000001u,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            null,
            null,
            "fixture",
            null,
            null,
            0x09000001u,
            PhysicsState: (uint)PhysicsStateFlags.ReportCollisions,
            InstanceSequence: 0,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }
}
