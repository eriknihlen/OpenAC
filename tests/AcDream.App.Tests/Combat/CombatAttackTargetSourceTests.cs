using System.Numerics;
using AcDream.App.Combat;
using AcDream.App.Input;
using AcDream.App.Rendering;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Selection;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Combat;

public sealed class CombatAttackTargetSourceTests
{
    private const uint Player = 0x5000_0001u;

    [Fact]
    public void ExplicitSelectedHostileIsAcceptedWithoutAutoTarget()
    {
        var harness = new Harness();
        const uint target = 0x7000_0001u;
        harness.Add(target, new Vector3(2f, 0f, 0f), attackable: true);
        harness.Selection.Select(target, SelectionChangeSource.Keyboard);

        Assert.Equal(
            target,
            harness.Targets.GetSelectedOrClosestCombatTarget(autoTarget: false));
    }

    [Fact]
    public void ExplicitSelectedPkLitePlayerIsAcceptedWithoutAutoTarget()
    {
        var harness = new Harness();
        harness.SetLocalPlayerPvpFlags(SelectedObjectHealthPolicy.BfPkLiteStatus);
        const uint target = 0x7000_0002u;
        harness.Add(
            target,
            new Vector3(2f, 0f, 0f),
            attackable: false,
            isPlayer: true,
            extraFlags: SelectedObjectHealthPolicy.BfPkLiteStatus);
        harness.Selection.Select(target, SelectionChangeSource.Keyboard);

        Assert.Equal(
            target,
            harness.Targets.GetSelectedOrClosestCombatTarget(autoTarget: false));
    }

    [Fact]
    public void ExplicitSelectedNonPkPlayerIsRefused()
    {
        var harness = new Harness();
        const uint target = 0x7000_0003u;
        harness.Add(
            target,
            new Vector3(2f, 0f, 0f),
            attackable: false,
            isPlayer: true);
        harness.Selection.Select(target, SelectionChangeSource.Keyboard);

        Assert.Null(
            harness.Targets.GetSelectedOrClosestCombatTarget(autoTarget: false));
    }

    [Fact]
    public void ExplicitSelectedAttackablePetIsRefused()
    {
        var harness = new Harness();
        const uint pet = 0x7000_0007u;
        harness.Add(
            pet,
            new Vector3(2f, 0f, 0f),
            attackable: true,
            petOwnerId: Player);
        harness.Selection.Select(pet, SelectionChangeSource.Keyboard);

        Assert.Null(
            harness.Targets.GetSelectedOrClosestCombatTarget(autoTarget: false));
    }

    [Fact]
    public void AutoTargetNeverAcquiresAPlayerOverAMonster()
    {
        var harness = new Harness();
        harness.SetLocalPlayerPvpFlags(SelectedObjectHealthPolicy.BfPkLiteStatus);
        const uint monster = 0x7000_0004u;
        const uint pkLitePlayer = 0x7000_0005u;
        harness.Add(monster, new Vector3(8f, 0f, 0f), attackable: true);
        harness.Add(
            pkLitePlayer,
            new Vector3(1f, 0f, 0f),
            attackable: false,
            isPlayer: true,
            extraFlags: SelectedObjectHealthPolicy.BfPkLiteStatus);

        uint? selected = harness.Targets.GetSelectedOrClosestCombatTarget(
            autoTarget: true);

        Assert.Equal(monster, selected);
        Assert.Equal(monster, harness.Selection.SelectedObjectId);
    }

    [Fact]
    public void AutoTargetNeverAcquiresAPlayerWhenNoMonsterIsInRange()
    {
        var harness = new Harness();
        harness.SetLocalPlayerPvpFlags(SelectedObjectHealthPolicy.BfPkLiteStatus);
        const uint pkLitePlayer = 0x7000_0006u;
        harness.Add(
            pkLitePlayer,
            new Vector3(1f, 0f, 0f),
            attackable: false,
            isPlayer: true,
            extraFlags: SelectedObjectHealthPolicy.BfPkLiteStatus);

        uint? selected = harness.Targets.GetSelectedOrClosestCombatTarget(
            autoTarget: true);

        Assert.Null(selected);
        Assert.Null(harness.Selection.SelectedObjectId);
    }

    [Fact]
    public void AutoTargetUsesNearestEligibleLiveHostile()
    {
        var harness = new Harness();
        const uint valid = 0x7000_0010u;
        harness.Add(valid, new Vector3(8f, 0f, 0f), attackable: true);
        harness.Add(0x7000_0011u, new Vector3(2f, 0f, 0f), attackable: false);
        WorldEntity dead = harness.Add(
            0x7000_0012u,
            new Vector3(3f, 0f, 0f),
            attackable: true);
        harness.Runtime.SetAnimationRuntime(
            dead.ServerGuid,
            new Animation(dead, MotionCommand.Dead));
        WorldEntity hidden = harness.Add(
            0x7000_0013u,
            new Vector3(4f, 0f, 0f),
            attackable: true);
        Assert.True(harness.Runtime.TryApplyState(
            new SetState.Parsed(
                hidden.ServerGuid,
                (uint)(PhysicsStateFlags.ReportCollisions | PhysicsStateFlags.Hidden),
                InstanceSequence: 1,
                StateSequence: 2),
            out _));
        WorldEntity pending = harness.Add(
            0x7000_0014u,
            new Vector3(5f, 0f, 0f),
            attackable: true);
        Assert.True(harness.Runtime.WithdrawLiveEntityProjection(pending.ServerGuid));

        uint? selected = harness.Targets.GetSelectedOrClosestCombatTarget(
            autoTarget: true);

        Assert.Equal(valid, selected);
        Assert.Equal(valid, harness.Selection.SelectedObjectId);
    }

    [Fact]
    public void MissingAutoTargetClearsAnInvalidSelection()
    {
        var harness = new Harness();
        const uint nonHostile = 0x7000_0020u;
        harness.Add(nonHostile, Vector3.UnitX, attackable: false);
        harness.Selection.Select(nonHostile, SelectionChangeSource.Keyboard);

        Assert.Null(harness.Targets.GetSelectedOrClosestCombatTarget(autoTarget: true));

        Assert.Null(harness.Selection.SelectedObjectId);
    }

    private sealed class Harness
    {
        public ClientObjectTable Objects { get; } = new();
        public SelectionState Selection { get; } = new();
        public LiveEntityRuntime Runtime { get; }
        public CombatAttackTargetSource Targets { get; }

        public Harness()
        {
            var spatial = new GpuWorldState();
            spatial.AddLandblock(new LoadedLandblock(
                0x0101_FFFFu,
                new LandBlock(),
                Array.Empty<WorldEntity>()));
            Runtime = LiveEntityRuntimeFixture.Create(spatial, new Resources());
            var identity = new LocalPlayerIdentityState { ServerGuid = Player };
            Targets = new CombatAttackTargetSource(
                Selection,
                Runtime,
                Objects,
                identity);
            Add(Player, Vector3.Zero, attackable: false, isPlayer: true);
        }

        public void SetLocalPlayerPvpFlags(uint extraFlags) =>
            Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = Player,
                Name = $"Object {Player:X8}",
                Type = ItemType.Creature,
                PublicWeenieBitfield = SelectedObjectHealthPolicy.BfPlayer | extraFlags,
            });

        public WorldEntity Add(
            uint guid,
            Vector3 position,
            bool attackable,
            bool isPlayer = false,
            uint extraFlags = 0u,
            uint petOwnerId = 0u)
        {
            Runtime.RegisterLiveEntity(Spawn(guid));
            WorldEntity entity = Runtime.MaterializeLiveEntity(
                guid,
                0x0101_0001u,
                id => new WorldEntity
                {
                    Id = id,
                    ServerGuid = guid,
                    SourceGfxObjOrSetupId = 0x0200_0001u,
                    Position = position,
                    Rotation = Quaternion.Identity,
                    Scale = 1f,
                    MeshRefs = [],
                })!;
            uint flags = attackable ? SelectedObjectHealthPolicy.BfAttackable : 0u;
            if (isPlayer)
                flags |= SelectedObjectHealthPolicy.BfPlayer;
            flags |= extraFlags;
            Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = guid,
                Name = $"Object {guid:X8}",
                Type = ItemType.Creature,
                PublicWeenieBitfield = flags,
                PetOwnerId = petOwnerId,
            });
            return entity;
        }
    }

    private sealed record Animation(WorldEntity Entity, uint CurrentMotion)
        : ILiveEntityAnimationRuntime;

    private sealed class Resources : ILiveEntityResourceLifecycle
    {
        public void Register(WorldEntity entity)
        {
        }
        public void Unregister(WorldEntity entity)
        {
        }
    }

    private static WorldSession.EntitySpawn Spawn(uint guid)
    {
        var position = new CreateObject.ServerPosition(
            0x0101_0001u,
            10f,
            10f,
            5f,
            1f,
            0f,
            0f,
            0f);
        var physics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.ReportCollisions,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x0200_0001u,
            MotionTableId: 0x0900_0001u,
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
            Timestamps: new PhysicsTimestamps(1, 1, 1, 1, 0, 1, 0, 1, 1));
        return new WorldSession.EntitySpawn(
            guid,
            position,
            0x0200_0001u,
            [],
            [],
            [],
            null,
            null,
            "fixture",
            null,
            null,
            0x0900_0001u,
            PhysicsState: (uint)PhysicsStateFlags.ReportCollisions,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }
}
