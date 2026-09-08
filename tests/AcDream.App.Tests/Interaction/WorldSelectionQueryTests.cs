using System.Numerics;
using AcDream.App.Interaction;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Selection;
using AcDream.App.Streaming;
using AcDream.App.UI.Layout;
using AcDream.App.World;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Selection;
using AcDream.Core.Ui;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Interaction;

public sealed class WorldSelectionQueryTests
{
    private const uint Player = 0x5000_0001u;
    private const uint Target = 0x7000_0001u;
    private const uint Wielder = 0x7000_0100u;
    private const uint RemoteWeapon = 0x7000_0101u;
    private const uint OwnWeapon = 0x7000_0102u;

    private sealed class Resources : ILiveEntityResourceLifecycle
    {
        public void Register(WorldEntity entity) { }
        public void Unregister(WorldEntity entity) { }
    }

    private sealed class GeometrySource(RetailSelectionMesh mesh)
        : IRetailSelectionGeometrySource
    {
        public RetailSelectionMesh? Resolve(uint gfxObjId) => mesh;
    }

    private sealed record Animation(WorldEntity Entity, uint CurrentMotion)
        : ILiveEntityAnimationRuntime;

    private sealed class Harness
    {
        public readonly ClientObjectTable Objects = new();
        public readonly LiveEntityRuntime Runtime;
        public readonly RetailSelectionScene Scene;
        public readonly WorldSelectionQuery Query;
        public readonly ExternalContainerState ExternalContainers = new();
        public readonly HashSet<uint> Fellows = [];
        public PlayerInteractionPose? PlayerPose = new(0x0101_0001u, Vector3.Zero);
        public CombatMode CurrentCombatMode = CombatMode.NonCombat;

        public readonly Dictionary<uint, Matrix4x4> ChildRoots = new();

        public Harness()
        {
            var spatial = new GpuWorldState();
            spatial.AddLandblock(new LoadedLandblock(
                0x0101_FFFFu,
                new LandBlock(),
                Array.Empty<WorldEntity>()));
            Runtime = LiveEntityRuntimeFixture.Create(spatial, new Resources());
            Scene = new RetailSelectionScene(new GeometrySource(Mesh()));
            Query = new WorldSelectionQuery(
                Runtime,
                Objects,
                Scene,
                () => Player,
                Camera,
                () => new Vector2(400f, 300f),
                () => PlayerPose,
                (_, _) => (0.5f, 2f),
                _ => (new Vector3(1f, 0f, 0f), 2f),
                localEntityId => ChildRoots.TryGetValue(localEntityId, out Matrix4x4 root)
                    ? root
                    : null,
                ExternalContainers.HasCorpseBeenOpened,
                () => CurrentCombatMode,
                Fellows.Contains);

            Add(Player, Vector3.Zero, ItemType.Creature, SelectedObjectHealthPolicy.BfPlayer);
        }

        public WorldEntity Add(
            uint guid,
            Vector3 position,
            ItemType type,
            uint publicFlags = 0u,
            uint? useability = null,
            uint objectDescriptionFlags = 0u,
            ushort instance = 1,
            float scale = 1f,
            Quaternion? rotation = null,
            float? useRadius = null,
            byte? radarBehavior = null)
        {
            WorldSession.EntitySpawn spawn = Spawn(guid, instance) with
            {
                ItemType = (uint)type,
                Useability = useability,
                ObjectDescriptionFlags = objectDescriptionFlags,
                UseRadius = useRadius,
            };
            Runtime.RegisterLiveEntity(spawn);
            WorldEntity entity = Runtime.MaterializeLiveEntity(
                guid,
                0x0101_0001u,
                id => Entity(id, guid, position, scale, rotation ?? Quaternion.Identity))!;
            Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = guid,
                Name = $"Object {guid:X8}",
                Type = type,
                PublicWeenieBitfield = publicFlags,
                RadarBehavior = radarBehavior,
            });
            return entity;
        }

        public WorldEntity AddAttached(
            uint guid,
            Vector3 parentWorldPosition,
            ItemType type,
            uint wielderId,
            Matrix4x4? childRoot = null,
            float? objScale = null,
            EquipMask equippedLocation = EquipMask.MeleeWeapon)
        {
            WorldSession.EntitySpawn spawn = Spawn(guid, 1) with
            {
                ItemType = (uint)type,
                ObjScale = objScale,
            };
            Runtime.RegisterLiveEntity(spawn);
            WorldEntity entity = Runtime.MaterializeLiveEntity(
                guid,
                0x0101_0001u,
                id => Entity(
                    id,
                    guid,
                    parentWorldPosition,
                    1f,
                    Quaternion.Identity),
                LiveEntityProjectionKind.Attached)!;
            Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = guid,
                Name = $"Object {guid:X8}",
                Type = type,
                WielderId = wielderId,
                CurrentlyEquippedLocation = equippedLocation,
            });
            if (childRoot is { } root)
                ChildRoots[entity.Id] = root;
            return entity;
        }

        public void Publish(WorldEntity entity)
            => PublishParts((entity, entity.Position));

        public void PublishParts(params (WorldEntity Entity, Vector3 PartWorld)[] parts)
        {
            SelectionCameraSnapshot camera = Camera();
            Scene.BeginFrame();
            Scene.SetViewFrustum(FrustumPlanes.FromViewProjection(
                camera.View * camera.Projection));
            foreach ((WorldEntity entity, Vector3 partWorld) in parts)
            {
                Scene.AddVisiblePart(
                    entity,
                    0,
                    0x0100_0001u,
                    Matrix4x4.CreateTranslation(partWorld));
            }
            Scene.CompleteFrame();
        }
    }

    [Fact]
    public void PickRejectsPublishedPartAfterGuidWasReused()
    {
        var h = new Harness();
        WorldEntity oldTarget = h.Add(Target, new Vector3(0f, 0f, -5f), ItemType.Misc);
        h.Publish(oldTarget);
        Assert.Equal(Target, h.Query.PickAtCursor(includeSelf: false));

        Assert.True(h.Runtime.UnregisterLiveEntity(
            new DeleteObject.Parsed(Target, 1),
            isLocalPlayer: false));
        WorldEntity replacement = h.Add(
            Target,
            new Vector3(0f, 0f, -5f),
            ItemType.Misc,
            instance: 1);

        Assert.NotEqual(oldTarget.Id, replacement.Id);
        Assert.Null(h.Query.PickAtCursor(includeSelf: false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void PickRejectsPublishedPartAfterVisibilityLifetimeEnds(int transition)
    {
        var h = new Harness();
        WorldEntity target = h.Add(Target, new Vector3(0f, 0f, -5f), ItemType.Misc);
        h.Publish(target);
        Assert.Equal(Target, h.Query.PickAtCursor(includeSelf: false));

        switch (transition)
        {
            case 0:
                Assert.True(h.Runtime.TryApplyState(
                    new SetState.Parsed(
                        Target,
                        (uint)(PhysicsStateFlags.ReportCollisions | PhysicsStateFlags.Hidden),
                        InstanceSequence: 1,
                        StateSequence: 2),
                    out _));
                break;
            case 1:
                Assert.True(h.Runtime.WithdrawLiveEntityProjection(Target));
                break;
            default:
                Assert.True(h.Runtime.UnregisterLiveEntity(
                    new DeleteObject.Parsed(Target, 1),
                    isLocalPlayer: false));
                break;
        }

        Assert.Null(h.Query.PickAtCursor(includeSelf: false));
    }

    [Fact]
    public void ClosestTargetIncludesOnlyVisibleLivingHostileMonsters()
    {
        var h = new Harness();
        h.Add(
            0x7000_0010u,
            new Vector3(8f, 0f, 0f),
            ItemType.Creature,
            SelectedObjectHealthPolicy.BfAttackable);
        h.Add(
            0x7000_0011u,
            new Vector3(2f, 0f, 0f),
            ItemType.Creature,
            publicFlags: 0u);
        h.Add(
            0x7000_0012u,
            new Vector3(1f, 0f, 0f),
            ItemType.Misc,
            SelectedObjectHealthPolicy.BfAttackable);
        h.Add(
            0x7000_0013u,
            new Vector3(3f, 0f, 0f),
            ItemType.Creature,
            SelectedObjectHealthPolicy.BfAttackable | SelectedObjectHealthPolicy.BfPlayer);
        WorldEntity pet = h.Add(
            0x7000_0014u,
            new Vector3(4f, 0f, 0f),
            ItemType.Creature,
            SelectedObjectHealthPolicy.BfAttackable);
        h.Objects.Get(pet.ServerGuid)!.PetOwnerId = Player;
        WorldEntity dead = h.Add(
            0x7000_0015u,
            new Vector3(5f, 0f, 0f),
            ItemType.Creature,
            SelectedObjectHealthPolicy.BfAttackable);
        h.Runtime.SetAnimationRuntime(
            dead.ServerGuid,
            new Animation(dead, MotionCommand.Dead));
        WorldEntity hidden = h.Add(
            0x7000_0016u,
            new Vector3(6f, 0f, 0f),
            ItemType.Creature,
            SelectedObjectHealthPolicy.BfAttackable);
        Assert.True(h.Runtime.TryApplyState(
            new SetState.Parsed(
                hidden.ServerGuid,
                (uint)(PhysicsStateFlags.ReportCollisions | PhysicsStateFlags.Hidden),
                InstanceSequence: 1,
                StateSequence: 2),
            out _));
        WorldEntity pending = h.Add(
            0x7000_0017u,
            new Vector3(7f, 0f, 0f),
            ItemType.Creature,
            SelectedObjectHealthPolicy.BfAttackable);
        Assert.True(h.Runtime.WithdrawLiveEntityProjection(pending.ServerGuid));

        ClosestCombatTarget? closest = h.Query.FindClosestHostileMonster();

        Assert.Equal(0x7000_0010u, closest?.ServerGuid);
        Assert.Equal(64f, closest?.DistanceSquared);
    }

    [Fact]
    public void RetailItemSelectionUsesRadarAndSpecialObjectRules()
    {
        var h = new Harness();
        const uint radarItem = 0x7000_0020u;
        const uint ordinaryItem = 0x7000_0021u;
        const uint portal = 0x7000_0022u;
        h.Add(
            radarItem,
            new Vector3(1f, 0f, 0f),
            ItemType.Misc,
            radarBehavior: (byte)RadarBehavior.ShowAlways);
        h.Add(ordinaryItem, new Vector3(2f, 0f, 0f), ItemType.Misc);
        h.Add(
            portal,
            new Vector3(3f, 0f, 0f),
            ItemType.Misc,
            publicFlags: (uint)PublicWeenieFlags.Portal,
            radarBehavior: (byte)RadarBehavior.ShowAlways);

        Assert.Equal(
            ordinaryItem,
            h.Query.FindSelectionTarget(
                RetailSelectionKind.Item,
                RetailSelectionDirection.Closest,
                anchor: null));

        h.Objects.Get(ordinaryItem)!.ContainerId = Player;
        Assert.Equal(
            portal,
            h.Query.FindSelectionTarget(
                RetailSelectionKind.Item,
                RetailSelectionDirection.Closest,
                anchor: null));
    }

    [Fact]
    public void RetailCompassSelectionChangesPredicateInPhysicalCombat()
    {
        var h = new Harness();
        const uint peaceful = 0x7000_0030u;
        const uint fellow = 0x7000_0031u;
        const uint vendor = 0x7000_0032u;
        const uint environment = 0x7000_0033u;
        const uint hostile = 0x7000_0034u;
        h.Add(
            peaceful,
            new Vector3(1f, 0f, 0f),
            ItemType.Misc,
            radarBehavior: (byte)RadarBehavior.ShowAlways);
        h.Add(
            fellow,
            new Vector3(2f, 0f, 0f),
            ItemType.Creature,
            publicFlags: (uint)PublicWeenieFlags.Attackable,
            radarBehavior: (byte)RadarBehavior.ShowAlways);
        h.Fellows.Add(fellow);
        h.Add(
            vendor,
            new Vector3(3f, 0f, 0f),
            ItemType.Creature,
            publicFlags: (uint)(PublicWeenieFlags.Attackable | PublicWeenieFlags.Vendor),
            radarBehavior: (byte)RadarBehavior.ShowAlways);
        h.Add(
            environment,
            new Vector3(4f, 0f, 0f),
            ItemType.Creature,
            publicFlags: (uint)PublicWeenieFlags.Attackable,
            radarBehavior: (byte)RadarBehavior.ShowAlways);
        Assert.True(h.Runtime.TryApplyState(
            new SetState.Parsed(
                environment,
                (uint)(PhysicsStateFlags.ReportCollisions
                    | PhysicsStateFlags.ReportAsEnvironment),
                InstanceSequence: 1,
                StateSequence: 2),
            out _));
        h.Add(
            hostile,
            new Vector3(5f, 0f, 0f),
            ItemType.Creature,
            publicFlags: (uint)PublicWeenieFlags.Attackable,
            radarBehavior: (byte)RadarBehavior.ShowAlways);

        Assert.Equal(
            peaceful,
            h.Query.FindSelectionTarget(
                RetailSelectionKind.CompassItem,
                RetailSelectionDirection.Closest,
                anchor: null));

        h.CurrentCombatMode = CombatMode.Melee;
        Assert.Equal(
            hostile,
            h.Query.FindSelectionTarget(
                RetailSelectionKind.CompassItem,
                RetailSelectionDirection.Closest,
                anchor: null));

        h.CurrentCombatMode = CombatMode.Magic;
        Assert.Equal(
            peaceful,
            h.Query.FindSelectionTarget(
                RetailSelectionKind.CompassItem,
                RetailSelectionDirection.Closest,
                anchor: null));
    }

    [Fact]
    public void RetailMonsterSelectionUsesObjectIsAttackableAndRejectsFellowsAndVendors()
    {
        var h = new Harness();
        const uint fellow = 0x7000_0040u;
        const uint vendor = 0x7000_0041u;
        const uint hostile = 0x7000_0042u;
        h.Add(
            fellow,
            new Vector3(1f, 0f, 0f),
            ItemType.Creature,
            publicFlags: (uint)PublicWeenieFlags.Attackable,
            radarBehavior: (byte)RadarBehavior.ShowAlways);
        h.Fellows.Add(fellow);
        h.Add(
            vendor,
            new Vector3(2f, 0f, 0f),
            ItemType.Creature,
            publicFlags: (uint)(PublicWeenieFlags.Attackable | PublicWeenieFlags.Vendor),
            radarBehavior: (byte)RadarBehavior.ShowAlways);
        h.Add(
            hostile,
            new Vector3(3f, 0f, 0f),
            ItemType.Creature,
            publicFlags: (uint)PublicWeenieFlags.Attackable,
            radarBehavior: (byte)RadarBehavior.ShowAlways);

        Assert.Equal(
            hostile,
            h.Query.FindSelectionTarget(
                RetailSelectionKind.Monster,
                RetailSelectionDirection.Closest,
                anchor: null));
    }

    [Fact]
    public void RetailUnopenedCorpseSelectionRemembersOpenUntilDelete()
    {
        var h = new Harness();
        const uint corpse = 0x7000_0050u;
        h.Add(
            corpse,
            new Vector3(1f, 0f, 0f),
            ItemType.Container,
            publicFlags: (uint)PublicWeenieFlags.Corpse);

        Assert.Equal(
            corpse,
            h.Query.FindSelectionTarget(
                RetailSelectionKind.UnopenedCorpse,
                RetailSelectionDirection.Closest,
                anchor: null));

        Assert.True(h.ExternalContainers.RequestOpen(corpse, isCorpse: true));
        Assert.Null(h.Query.FindSelectionTarget(
            RetailSelectionKind.UnopenedCorpse,
            RetailSelectionDirection.Closest,
            anchor: null));

        Assert.True(h.ExternalContainers.SetCorpseDeleted(corpse));
        Assert.Equal(
            corpse,
            h.Query.FindSelectionTarget(
                RetailSelectionKind.UnopenedCorpse,
                RetailSelectionDirection.Closest,
                anchor: null));
    }

    [Fact]
    public void CombatCameraTracksACompatiblePkPlayerButNotAnIncompatibleOne()
    {
        var h = new Harness();
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Player,
            Name = $"Object {Player:X8}",
            Type = ItemType.Creature,
            PublicWeenieBitfield = SelectedObjectHealthPolicy.BfPlayer
                | SelectedObjectHealthPolicy.BfPkLiteStatus,
        });
        const uint pkLiteOpponent = 0x7000_0050u;
        const uint nonPkPlayer = 0x7000_0051u;
        h.Add(
            pkLiteOpponent,
            new Vector3(2f, 0f, 0f),
            ItemType.Creature,
            SelectedObjectHealthPolicy.BfPlayer
                | SelectedObjectHealthPolicy.BfPkLiteStatus);
        h.Add(
            nonPkPlayer,
            new Vector3(3f, 0f, 0f),
            ItemType.Creature,
            SelectedObjectHealthPolicy.BfPlayer);

        Assert.NotNull(h.Query.GetCombatCameraTargetPoint(pkLiteOpponent));
        Assert.Null(h.Query.GetCombatCameraTargetPoint(nonPkPlayer));
    }

    [Theory]
    [InlineData(0.59f, true)]
    [InlineData(0.61f, false)]
    [InlineData(7.49f, false)]
    [InlineData(7.50f, true)]
    public void ApproachUsesRetailGroundItemRadiusAndAceChargeBoundary(
        float distance,
        bool expectedBoundary)
    {
        var h = new Harness();
        h.Add(Target, new Vector3(distance, 0f, 0f), ItemType.Misc);

        Assert.True(h.Query.TryGetApproach(Target, out InteractionApproach approach));

        if (distance < 1f)
            Assert.Equal(expectedBoundary, approach.IsCloseRange);
        else
            Assert.Equal(expectedBoundary, approach.CanCharge);
        Assert.Equal(0.6f, approach.UseRadius);
        Assert.Equal(0.5f, approach.TargetRadius);
        Assert.Equal(2f, approach.TargetHeight);
    }

    [Fact]
    public void ApproachUsesTheCreatureTargetsOwnWireAuthoredUseRadius()
    {
        var h = new Harness();
        h.Add(Target, new Vector3(2f, 0f, 0f), ItemType.Creature, useRadius: 1.5f);

        Assert.True(h.Query.TryGetApproach(Target, out InteractionApproach approach));

        Assert.Equal(1.5f, approach.UseRadius);
        Assert.False(approach.IsCloseRange);
    }

    [Fact]
    public void ApproachFallsBackToAceDefaultUseRadiusWhenTheWireOmitsIt()
    {
        var h = new Harness();
        h.Add(Target, new Vector3(2f, 0f, 0f), ItemType.Creature);

        Assert.True(h.Query.TryGetApproach(Target, out InteractionApproach approach));

        Assert.Equal(0.6f, approach.UseRadius);
        Assert.False(approach.IsCloseRange);
    }

    [Fact]
    public void PickupAndUseabilityPreserveIndependentRetailGates()
    {
        var h = new Harness();
        const uint stuckComponent = 0x7000_0020u;
        const uint looseComponent = 0x7000_0021u;
        h.Add(
            stuckComponent,
            Vector3.UnitX,
            ItemType.SpellComponents,
            useability: 1u,
            objectDescriptionFlags: 0x0004u);
        h.Add(
            looseComponent,
            Vector3.UnitX * 2f,
            ItemType.SpellComponents,
            useability: 1u);

        Assert.False(h.Query.IsUseable(stuckComponent));
        Assert.False(h.Query.IsPickupable(stuckComponent));
        Assert.False(h.Query.IsUseable(looseComponent));
        Assert.True(h.Query.IsPickupable(looseComponent));
    }

    [Fact]
    public void UseabilityUsesRetailLowNoBitForAbsentAndExplicitValues()
    {
        var h = new Harness();
        const uint absent = 0x7000_0022u;
        const uint zero = 0x7000_0023u;
        const uint no = 0x7000_0024u;
        const uint neverWalk = 0x7000_0025u;
        h.Add(absent, Vector3.UnitX, ItemType.Gem);
        h.Add(zero, Vector3.UnitX * 2f, ItemType.Gem, useability: 0u);
        h.Add(no, Vector3.UnitX * 3f, ItemType.Gem, useability: ItemUseability.No);
        h.Add(
            neverWalk,
            Vector3.UnitX * 4f,
            ItemType.Gem,
            useability: ItemUseability.NeverWalk);

        Assert.True(h.Query.IsUseable(absent));
        Assert.True(h.Query.IsUseable(zero));
        Assert.False(h.Query.IsUseable(no));
        Assert.True(h.Query.IsUseable(neverWalk));
    }

    [Fact]
    public void SelectionSphereAppliesSetupOffsetScaleAndRotation()
    {
        var h = new Harness();
        h.Add(
            Target,
            new Vector3(10f, 20f, 3f),
            ItemType.Misc,
            scale: 2f,
            rotation: Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f));

        Assert.True(h.Query.TryGetSelectionSphere(Target, out Vector3 center, out float radius));

        Assert.True(Vector3.Distance(new Vector3(10f, 22f, 3f), center) < 0.0001f);
        Assert.Equal(4f, radius);
    }

    [Fact]
    public void VividTargetMarkerWaitsForFreshProjectionWhenDropMovePrecedesPosition()
    {
        var h = new Harness();
        WorldEntity entity =
            h.Add(Target, new Vector3(3f, 4f, 0f), ItemType.Misc);

        Assert.NotNull(h.Query.ResolveVividTargetInfo(Target));

        Assert.True(h.Objects.MoveItem(Target, Player, newSlot: 0));
        Assert.True(h.Runtime.WithdrawLiveEntityProjection(Target));
        Assert.Null(h.Query.ResolveVividTargetInfo(Target));

        Assert.True(h.Objects.MoveItem(Target, 0u, newSlot: -1));
        Assert.Null(h.Query.ResolveVividTargetInfo(Target));

        var newPosition = new Vector3(30f, 40f, 2f);
        entity.SetPosition(newPosition);
        Assert.True(h.Runtime.RebucketLiveEntity(Target, 0x0101_0001u));

        var marker = h.Query.ResolveVividTargetInfo(Target);
        Assert.NotNull(marker);
        Assert.Equal(newPosition + Vector3.UnitX, marker.Value.SelectionSphereCenter);
    }

    [Fact]
    public void VividTargetMarkerUsesFreshPoseWhenPositionPrecedesDropMove()
    {
        var h = new Harness();
        WorldEntity entity =
            h.Add(Target, new Vector3(3f, 4f, 0f), ItemType.Misc);
        Assert.True(h.Objects.MoveItem(Target, Player, newSlot: 0));
        Assert.True(h.Runtime.WithdrawLiveEntityProjection(Target));

        var newPosition = new Vector3(30f, 40f, 2f);
        entity.SetPosition(newPosition);
        Assert.True(h.Runtime.RebucketLiveEntity(Target, 0x0101_0001u));

        Assert.Null(h.Query.ResolveVividTargetInfo(Target));

        Assert.True(h.Objects.MoveItem(Target, 0u, newSlot: -1));
        var marker = h.Query.ResolveVividTargetInfo(Target);
        Assert.NotNull(marker);
        Assert.Equal(newPosition + Vector3.UnitX, marker.Value.SelectionSphereCenter);
    }

    [Fact]
    public void VividTargetMarkerRejectsRetainedProjectionInsideExternalContainer()
    {
        var h = new Harness();
        const uint corpse = 0x7000_0040u;
        h.Add(Target, new Vector3(3f, 4f, 0f), ItemType.Misc);
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = corpse,
            Name = "Corpse",
            Type = ItemType.Container,
        });

        Assert.True(h.Objects.MoveItem(Target, corpse, newSlot: 0));

        Assert.False(h.Objects.IsOwnedByObject(Target, Player));
        Assert.Null(h.Query.ResolveVividTargetInfo(Target));
    }

    [Fact]
    public void PickResolvesTheEquippedChildRatherThanItsWielder()
    {
        var h = new Harness();
        WorldEntity wielder = h.Add(
            Wielder,
            new Vector3(0f, 0f, -10f),
            ItemType.Creature,
            SelectedObjectHealthPolicy.BfAttackable);
        WorldEntity weapon = h.AddAttached(
            RemoteWeapon,
            wielder.Position,
            ItemType.MeleeWeapon,
            wielderId: Wielder,
            childRoot: Matrix4x4.CreateTranslation(0f, 0f, -5f));
        h.PublishParts(
            (wielder, wielder.Position),
            (weapon, new Vector3(0f, 0f, -5f)));

        Assert.Equal(RemoteWeapon, h.Query.PickAtCursor(includeSelf: false));
        Assert.True(h.Query.TryCaptureIdentity(RemoteWeapon, out uint localId));
        Assert.Equal(weapon.Id, localId);
    }

    [Fact]
    public void PickRejectsAWithdrawnEquippedChildWithoutFallingBackToTheWielder()
    {
        var h = new Harness();
        WorldEntity wielder = h.Add(
            Wielder,
            new Vector3(0f, 0f, -10f),
            ItemType.Creature,
            SelectedObjectHealthPolicy.BfAttackable);
        WorldEntity weapon = h.AddAttached(
            RemoteWeapon,
            wielder.Position,
            ItemType.MeleeWeapon,
            wielderId: Wielder,
            childRoot: Matrix4x4.CreateTranslation(0f, 0f, -5f));
        h.PublishParts(
            (wielder, wielder.Position),
            (weapon, new Vector3(0f, 0f, -5f)));
        Assert.Equal(RemoteWeapon, h.Query.PickAtCursor(includeSelf: false));

        Assert.True(h.Runtime.WithdrawLiveEntityProjection(RemoteWeapon));

        Assert.Null(h.Query.PickAtCursor(includeSelf: false));
        Assert.False(h.Query.IsCurrent(RemoteWeapon, weapon.Id));
        Assert.False(h.Query.TryGetInteractionTarget(RemoteWeapon, out _));
    }

    [Fact]
    public void PickRejectsAnEquippedChildWhoseIncarnationWasReplaced()
    {
        var h = new Harness();
        WorldEntity wielder = h.Add(
            Wielder,
            new Vector3(0f, 0f, -10f),
            ItemType.Creature,
            SelectedObjectHealthPolicy.BfAttackable);
        WorldEntity weapon = h.AddAttached(
            RemoteWeapon,
            wielder.Position,
            ItemType.MeleeWeapon,
            wielderId: Wielder,
            childRoot: Matrix4x4.CreateTranslation(0f, 0f, -5f));
        h.PublishParts((weapon, new Vector3(0f, 0f, -5f)));
        Assert.Equal(RemoteWeapon, h.Query.PickAtCursor(includeSelf: false));

        Assert.True(h.Runtime.UnregisterLiveEntity(
            new DeleteObject.Parsed(RemoteWeapon, 1),
            isLocalPlayer: false));
        WorldEntity replacement = h.AddAttached(
            RemoteWeapon,
            wielder.Position,
            ItemType.MeleeWeapon,
            wielderId: Wielder,
            childRoot: Matrix4x4.CreateTranslation(0f, 0f, -5f));

        Assert.NotEqual(weapon.Id, replacement.Id);
        Assert.Null(h.Query.PickAtCursor(includeSelf: false));
    }

    [Fact]
    public void VividTargetMarkerAnchorsOnTheComposedChildRoot()
    {
        var h = new Harness();
        var wielderRoot = new Vector3(1f, 2f, 3f);
        h.Add(
            Wielder,
            wielderRoot,
            ItemType.Creature,
            SelectedObjectHealthPolicy.BfAttackable);
        h.AddAttached(
            RemoteWeapon,
            wielderRoot,
            ItemType.MeleeWeapon,
            wielderId: Wielder,
            childRoot: Matrix4x4.CreateTranslation(7f, 8f, 9f));

        VividTargetInfo? marker = h.Query.ResolveVividTargetInfo(RemoteWeapon);

        Assert.NotNull(marker);
        Assert.Equal(new Vector3(8f, 8f, 9f), marker!.Value.SelectionSphereCenter);
        Assert.Equal(2f, marker.Value.SelectionSphereRadius);
        // The parent-derived bookkeeping pose would have produced (2,2,3).
        Assert.NotEqual(
            wielderRoot + Vector3.UnitX,
            marker.Value.SelectionSphereCenter);
    }

    [Fact]
    public void VividTargetMarkerRefusesAnEquippedChildWithNoComposedRoot()
    {
        var h = new Harness();
        var wielderRoot = new Vector3(1f, 2f, 3f);
        h.Add(
            Wielder,
            wielderRoot,
            ItemType.Creature,
            SelectedObjectHealthPolicy.BfAttackable);
        WorldEntity weapon = h.AddAttached(
            RemoteWeapon,
            wielderRoot,
            ItemType.MeleeWeapon,
            wielderId: Wielder,
            childRoot: Matrix4x4.CreateTranslation(7f, 8f, 9f));

        Assert.True(h.ChildRoots.Remove(weapon.Id));

        Assert.Null(h.Query.ResolveVividTargetInfo(RemoteWeapon));
        Assert.False(h.Query.TryGetSelectionSphere(RemoteWeapon, out _, out _));
    }

    [Fact]
    public void VividTargetMarkerSuppressesOnlyThePlayersOwnWieldedChild()
    {
        var h = new Harness();
        h.Add(
            Wielder,
            new Vector3(1f, 2f, 3f),
            ItemType.Creature,
            SelectedObjectHealthPolicy.BfAttackable);
        h.AddAttached(
            RemoteWeapon,
            new Vector3(1f, 2f, 3f),
            ItemType.MeleeWeapon,
            wielderId: Wielder,
            childRoot: Matrix4x4.CreateTranslation(7f, 8f, 9f));
        h.AddAttached(
            OwnWeapon,
            Vector3.Zero,
            ItemType.MeleeWeapon,
            wielderId: Player,
            childRoot: Matrix4x4.CreateTranslation(0f, 0.5f, 1f));

        Assert.NotNull(h.Query.ResolveVividTargetInfo(RemoteWeapon));
        Assert.Null(h.Query.ResolveVividTargetInfo(OwnWeapon));
        Assert.True(h.Query.IsWieldedByPlayer(OwnWeapon));
        Assert.False(h.Query.IsWieldedByPlayer(RemoteWeapon));
    }

    [Fact]
    public void LightingPulseStartsOnTheEquippedChildIdentityOnly()
    {
        var h = new Harness();
        WorldEntity wielder = h.Add(
            Wielder,
            new Vector3(0f, 0f, -10f),
            ItemType.Creature,
            SelectedObjectHealthPolicy.BfAttackable);
        WorldEntity weapon = h.AddAttached(
            RemoteWeapon,
            wielder.Position,
            ItemType.MeleeWeapon,
            wielderId: Wielder,
            childRoot: Matrix4x4.CreateTranslation(0f, 0f, -5f));

        h.Query.BeginLightingPulse(RemoteWeapon);

        Assert.True(h.Scene.TryGetLighting(RemoteWeapon, weapon.Id, out _));
        Assert.False(h.Scene.TryGetLighting(Wielder, wielder.Id, out _));
    }

    [Fact]
    public void EquippedChildStaysOutOfTheInteractionAndRadarVisibleSet()
    {
        var h = new Harness();
        WorldEntity wielder = h.Add(
            Wielder,
            new Vector3(0f, 0f, -10f),
            ItemType.Creature,
            SelectedObjectHealthPolicy.BfAttackable);
        WorldEntity weapon = h.AddAttached(
            RemoteWeapon,
            wielder.Position,
            ItemType.MeleeWeapon,
            wielderId: Wielder,
            childRoot: Matrix4x4.CreateTranslation(0f, 0f, -5f));

        Assert.False(h.Runtime.TryGetInteractionEligibleEntity(RemoteWeapon, out _));
        Assert.False(h.Runtime.TryGetInteractionEligibleRecord(RemoteWeapon, out _));
        Assert.False(h.Runtime.TryGetInteractionEligibleRecord(
            RemoteWeapon,
            weapon.Id,
            out _));
        Assert.DoesNotContain(
            h.Runtime.VisibleRecords,
            record => record.ServerGuid == RemoteWeapon);
        Assert.Contains(
            h.Runtime.VisibleRecords,
            record => record.ServerGuid == Wielder);

        Assert.True(h.Runtime.TryGetPickEligibleRecord(RemoteWeapon, out _));
        Assert.True(h.Runtime.TryGetPickEligibleRecord(
            RemoteWeapon,
            weapon.Id,
            out _));
        Assert.False(h.Runtime.TryGetPickEligibleRecord(
            RemoteWeapon,
            weapon.Id + 1u,
            out _));
    }

    [Fact]
    public void EquippedChildSelectionSphereUsesItsOwnSpawnScale()
    {
        var h = new Harness();
        h.Add(
            Wielder,
            Vector3.Zero,
            ItemType.Creature,
            SelectedObjectHealthPolicy.BfAttackable);
        h.AddAttached(
            RemoteWeapon,
            Vector3.Zero,
            ItemType.MeleeWeapon,
            wielderId: Wielder,
            childRoot: Matrix4x4.CreateTranslation(7f, 8f, 9f),
            objScale: 2f);

        Assert.True(h.Query.TryGetSelectionSphere(
            RemoteWeapon,
            out Vector3 center,
            out float radius));

        Assert.Equal(new Vector3(9f, 8f, 9f), center);
        Assert.Equal(4f, radius);
    }

    [Fact]
    public void WieldedPositionStateFollowsRetailsContainerThenLocationOrder()
    {
        var h = new Harness();
        const uint ground = 0x7000_0030u;
        const uint stowed = 0x7000_0031u;
        h.Add(ground, Vector3.UnitX, ItemType.MeleeWeapon);
        h.Add(
            Wielder,
            new Vector3(0f, 0f, -10f),
            ItemType.Creature,
            SelectedObjectHealthPolicy.BfAttackable);
        h.AddAttached(
            RemoteWeapon,
            new Vector3(0f, 0f, -10f),
            ItemType.MeleeWeapon,
            wielderId: Wielder,
            childRoot: Matrix4x4.CreateTranslation(0f, 0f, -5f));
        h.AddAttached(
            OwnWeapon,
            Vector3.Zero,
            ItemType.MeleeWeapon,
            wielderId: Player,
            childRoot: Matrix4x4.CreateTranslation(0f, 0.5f, 1f));
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = stowed,
            Name = "Stowed weapon",
            Type = ItemType.MeleeWeapon,
            ContainerId = Player,
            CurrentlyEquippedLocation = EquipMask.MeleeWeapon,
        });

        Assert.True(h.Query.IsWieldedPositionState(RemoteWeapon));
        Assert.True(h.Query.IsWieldedPositionState(OwnWeapon));
        Assert.False(h.Query.IsWieldedPositionState(ground));
        // IN_CONTAINER wins over a stale wielded location.
        Assert.False(h.Query.IsWieldedPositionState(stowed));
        Assert.False(h.Query.IsWieldedPositionState(0x7000_00FFu));
    }

    [Fact]
    public void PickAndSelectionStayOpenOnARemotesWieldedItemTheGateWillRefuse()
    {
        var h = new Harness();
        WorldEntity wielder = h.Add(
            Wielder,
            new Vector3(0f, 0f, -10f),
            ItemType.Creature,
            SelectedObjectHealthPolicy.BfAttackable);
        WorldEntity weapon = h.AddAttached(
            RemoteWeapon,
            wielder.Position,
            ItemType.MeleeWeapon,
            wielderId: Wielder,
            childRoot: Matrix4x4.CreateTranslation(0f, 0f, -5f));
        h.PublishParts(
            (wielder, wielder.Position),
            (weapon, new Vector3(0f, 0f, -5f)));

        Assert.Equal(RemoteWeapon, h.Query.PickAtCursor(includeSelf: false));
        Assert.True(h.Query.TryCaptureIdentity(RemoteWeapon, out uint localId));
        Assert.Equal(weapon.Id, localId);
        Assert.True(h.Query.IsCurrent(RemoteWeapon, weapon.Id));
        Assert.True(h.Query.TryGetInteractionTarget(RemoteWeapon, out _));
        Assert.NotNull(h.Query.ResolveVividTargetInfo(RemoteWeapon));
        Assert.Equal($"Object {RemoteWeapon:X8}", h.Query.Describe(RemoteWeapon));

        Assert.True(h.Query.IsPickupable(RemoteWeapon));
        Assert.True(h.Query.IsWieldedPositionState(RemoteWeapon));
        Assert.False(h.Objects.IsOwnedByObject(RemoteWeapon, Player));
    }

    [Fact]
    public void ApproachRefusesAnAttachedChildRatherThanAnchoringOnItsWielder()
    {
        var h = new Harness();
        const uint ground = 0x7000_0032u;
        var wielderRoot = new Vector3(20f, 0f, 0f);
        h.Add(ground, new Vector3(3f, 0f, 0f), ItemType.MeleeWeapon);
        h.Add(
            Wielder,
            wielderRoot,
            ItemType.Creature,
            SelectedObjectHealthPolicy.BfAttackable);
        h.AddAttached(
            RemoteWeapon,
            wielderRoot,
            ItemType.MeleeWeapon,
            wielderId: Wielder,
            childRoot: Matrix4x4.CreateTranslation(20f, 0f, 1.2f));

        Assert.False(h.Query.TryGetApproach(RemoteWeapon, out _));
        Assert.True(h.Query.TryGetApproach(ground, out InteractionApproach loose));
        Assert.Equal(ground, loose.Target.ServerGuid);
    }

    private static SelectionCameraSnapshot Camera()
    {
        Matrix4x4 view = Matrix4x4.CreateLookAt(
            Vector3.Zero,
            -Vector3.UnitZ,
            Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f,
            4f / 3f,
            0.1f,
            100f);
        return new SelectionCameraSnapshot(view, projection, new Vector2(800f, 600f));
    }

    private static RetailSelectionMesh Mesh()
        => new(
            Vector3.Zero,
            2f,
            [new RetailSelectionPolygon(
                [
                    new(-1f, -1f, 0f),
                    new( 1f, -1f, 0f),
                    new( 1f,  1f, 0f),
                    new(-1f,  1f, 0f),
                ],
                SingleSided: false)]);

    private static WorldEntity Entity(
        uint id,
        uint guid,
        Vector3 position,
        float scale,
        Quaternion rotation)
        => new()
        {
            Id = id,
            ServerGuid = guid,
            SourceGfxObjOrSetupId = 0x0200_0001u,
            Position = position,
            Rotation = rotation,
            Scale = scale,
            MeshRefs = [],
        };

    private static WorldSession.EntitySpawn Spawn(uint guid, ushort instance)
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
            Timestamps: new PhysicsTimestamps(1, 1, 1, 1, 0, 1, 0, 1, instance));
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
            InstanceSequence: instance,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }
}
