using System.Numerics;
using AcDream.App.Interaction;
using AcDream.App.UI;
using AcDream.App.World;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.Selection;
using AcDream.Core.World;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;

namespace AcDream.App.Tests.Interaction;

public sealed class ProductionUseApproachWiringTests
{
    private const uint Player = 0x5000_0001u;
    private const uint Vendor = 0x7C95_B01Cu;
    private const uint OtherVendor = 0x7C95_B01Du;
    private const uint Cell = 0x0101_0001u;

    private sealed class Query : IWorldSelectionQuery
    {
        public Dictionary<uint, InteractionApproach> Approaches { get; } = new();

        public uint? PickAtCursor(bool includeSelf) => null;
        public uint? PickAt(float mouseX, float mouseY, bool includeSelf) => null;
        public void BeginLightingPulse(uint serverGuid) { }
        public bool TryCaptureIdentity(uint serverGuid, out uint localEntityId)
        {
            localEntityId = 101u;
            return true;
        }
        public bool IsCurrent(uint serverGuid, uint localEntityId) => true;
        public string Describe(uint serverGuid) => $"Target {serverGuid:X8}";
        public bool IsCreature(uint serverGuid) => true;
        public bool IsHostileMonster(uint serverGuid) => false;
        public bool IsAttackableTarget(uint serverGuid) => false;
        public ClosestCombatTarget? FindClosestHostileMonster() => null;
        public bool IsUseable(uint serverGuid) => true;
        public bool IsPickupable(uint serverGuid) => false;
        public bool IsStuckInWorld(uint serverGuid) => false;
        public bool IsWieldedByPlayer(uint serverGuid) => false;
        public bool IsWieldedPositionState(uint serverGuid) => false;
        public Vector3? GetCombatCameraTargetPoint(uint serverGuid) => null;
        public bool TryGetApproach(uint serverGuid, out InteractionApproach approach)
            => Approaches.TryGetValue(serverGuid, out approach);
    }

    private sealed class Transport : IRuntimeInteractionTransport
    {
        private uint _sequence;
        public bool IsInWorld => true;
        public List<uint> Uses { get; } = new();

        public bool TrySendUse(uint serverGuid, out uint sequence)
        {
            sequence = ++_sequence;
            Uses.Add(serverGuid);
            return true;
        }

        public bool TrySendPickup(
            uint itemGuid,
            uint destinationContainerId,
            int placement,
            out uint sequence)
        {
            sequence = ++_sequence;
            return true;
        }
    }

    private sealed class NoAutomaticTarget : IRuntimeCombatTargetOperations
    {
        public bool AutoTarget => false;
        public uint? SelectClosestTarget() => null;
    }

    private sealed class Harness : IDisposable
    {
        public readonly RuntimeEntityObjectLifetime RuntimeLifetime = new();
        public readonly Query Query = new();
        public readonly Transport Transport = new();
        public readonly PlayerApproachCompletionState Completions = new();
        public readonly IPlayerApproachCompletionSink CompletionLifetime;
        public readonly SelectionState Selection = new();
        public readonly CombatState Combat = new();
        public readonly RuntimeCombatTargetState CombatTarget;
        public readonly ClientObjectTable Objects = new();
        public readonly InventoryTransactionState Inventory;
        public readonly RuntimeInteractionTransactionState Transactions;
        public readonly ItemInteractionController Items;
        public readonly SelectionInteractionController Controller;
        public readonly PlayerMovementController MovementController;
        public readonly MoveToManager MoveTo;
        public readonly Dictionary<uint, EntityPhysicsHost> TargetHosts = new();

        public Harness(bool bindObjectTableResolver)
        {
            CompletionLifetime = Completions.BeginControllerLifetime();
            Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = Player,
                Type = ItemType.Creature,
            });
            Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = Vendor,
                Name = "Archmage",
                Type = ItemType.Creature,
                Useability = ItemUseability.Remote,
            });
            Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = OtherVendor,
                Name = "Other Archmage",
                Type = ItemType.Creature,
                Useability = ItemUseability.Remote,
            });

            var controller = new PlayerMovementController(new PhysicsEngine());
            controller.SeedPlacementForTest(Vector3.Zero, Cell, Vector3.Zero);
            MovementController = controller;
            EntityPhysicsHost playerHost = null!;
            MoveTo = new MoveToManager(
                controller.Motion,
                stopCompletely: () =>
                    _ = controller.StopCompletelyAtPhysicsObjectBoundary(),
                getPosition: () => controller.CellPosition,
                getHeading: () => MoveToMath.HeadingFromYaw(controller.Yaw),
                setHeading: (heading, _) =>
                    controller.Yaw = MoveToMath.YawFromHeading(heading),
                getOwnRadius: static () => 0.48f,
                getOwnHeight: static () => 1.835f,
                contact: static () => true,
                isInterpolating: static () => false,
                getVelocity: static () => Vector3.Zero,
                getSelfId: static () => Player,
                setTarget: (context, target, radius, quantum) =>
                    playerHost.SetTarget(context, target, radius, quantum),
                clearTarget: () => playerHost.ClearTarget(),
                getTargetQuantum: () =>
                    playerHost.TargetManager.GetTargetQuantum(),
                setTargetQuantum: quantum =>
                    playerHost.TargetManager.SetTargetQuantum(quantum));
            playerHost = new EntityPhysicsHost(
                Player,
                getPosition: () => controller.CellPosition,
                getVelocity: static () => Vector3.Zero,
                getRadius: static () => 0.48f,
                inContact: static () => true,
                minterpMaxSpeed: static () => null,
                curTime: static () => 0d,
                physicsTimerTime: static () => 0d,
                // THE seam under test — the production publication binding.
                getObjectA: RuntimeLifetime.Physics.ResolveObjectTableHost,
                handleUpdateTarget: info =>
                    controller.Movement.HandleUpdateTarget(info),
                interruptCurrentMovement: () =>
                    controller.Movement.CancelMoveTo(
                        WeenieError.ActionCancelled));
            MoveTo.StickTo = (target, radius, height) =>
                playerHost.PositionManager.StickTo(target, radius, height);
            MoveTo.Unstick = playerHost.PositionManager.UnStick;
            controller.MoveTo = MoveTo;

            MoveTo.MoveToComplete = error =>
            {
                if (error == WeenieError.None)
                    CompletionLifetime.PublishNaturalCompletion();
                else
                    CompletionLifetime.PublishCancellation(error);
            };
            MoveTo.MoveToCancelled = error =>
                CompletionLifetime.PublishCancellation(error);

            if (bindObjectTableResolver)
            {
                // SessionPlayerComposition's bind, with the lazy App
                // resolver stood in by a per-guid minimal-host table (the
                // shape LiveEntityMotionRuntimeController.ResolvePhysicsHost
                // produces for a never-animated entity).
                RuntimeLifetime.Physics.BindObjectTableHostResolver(
                    guid => TargetHosts.GetValueOrDefault(guid));
            }

            CombatTarget = new RuntimeCombatTargetState(
                Combat,
                Selection,
                new NoAutomaticTarget());
            Inventory = new InventoryTransactionState(Objects);
            Transactions = new RuntimeInteractionTransactionState(Inventory);
            SelectionInteractionController? selectionController = null;
            Items = new ItemInteractionController(
                Objects,
                Transactions,
                new InteractionState(),
                () => Player,
                sendUse: null,
                sendUseWithTarget: null,
                sendWield: null,
                sendDrop: null,
                sendExamine: _ => { },
                groundObjectId: () => 0u,
                placeInBackpack: (item, container, placement) =>
                    selectionController!.SendPickup(item, container, placement),
                requestUse: (guid, reservation) =>
                    selectionController!.RequestUse(guid, reservation));
            Controller = selectionController = new SelectionInteractionController(
                Selection,
                Query,
                Items,
                Transport,
                new PlayerInteractionMovementSink(
                    () => MovementController,
                    Completions),
                CombatTarget,
                toast: null,
                Completions);
        }

        public void AddFarTarget(uint serverGuid, Vector3 position)
        {
            var entity = new WorldEntity
            {
                Id = 101u,
                ServerGuid = serverGuid,
                SourceGfxObjOrSetupId = 0x0200_0001u,
                Position = position,
                Rotation = Quaternion.Identity,
                MeshRefs = [],
            };
            Query.Approaches[serverGuid] = new InteractionApproach(
                new WorldInteractionTarget(serverGuid, 101u, entity),
                new PlayerInteractionPose(Cell, Vector3.Zero),
                UseRadius: 3f,
                IsCloseRange: false,
                CanCharge: true,
                TargetRadius: 0.5f,
                TargetHeight: 2f);
            TargetHosts[serverGuid] = new EntityPhysicsHost(
                serverGuid,
                getPosition: () => new Position(
                    Cell, position, Quaternion.Identity),
                getVelocity: static () => Vector3.Zero,
                getRadius: static () => 0.5f,
                inContact: static () => true,
                minterpMaxSpeed: static () => null,
                curTime: static () => 0d,
                physicsTimerTime: static () => 0d,
                getObjectA: static _ => null,
                handleUpdateTarget: static _ => { },
                interruptCurrentMovement: static () => { });
        }

        public void Dispose()
        {
            CombatTarget.Dispose();
            RuntimeLifetime.Dispose();
        }
    }

    [Fact]
    public void FarUseDispatchesOnNaturalArrivalThroughTheRealMovementChain()
    {
        using var h = new Harness(bindObjectTableResolver: true);
        h.AddFarTarget(Vendor, new Vector3(3.5f, 0f, 0f));
        ItemUseRequestReservation reservation =
            h.Transactions.BeginUseRequestReservation();
        Assert.Equal(1, h.Inventory.BusyCount);

        h.Controller.RequestUse(Vendor, reservation);

        Assert.Empty(h.Transport.Uses);

        h.Controller.DrainOutbound();

        Assert.Equal(new[] { Vendor }, h.Transport.Uses);
        Assert.Equal(1, h.Inventory.BusyCount);
        h.Transactions.CompleteUse(0u);
        Assert.Equal(0, h.Inventory.BusyCount);
    }

    [Fact]
    public void CancelledApproachReleasesTheArmedReservationInTheSameDrain()
    {
        using var h = new Harness(bindObjectTableResolver: true);
        h.AddFarTarget(Vendor, new Vector3(10f, 0f, 0f));
        ItemUseRequestReservation reservation =
            h.Transactions.BeginUseRequestReservation();

        h.Controller.RequestUse(Vendor, reservation);

        // Genuinely walking: the far target initialized a real node plan.
        Assert.True(h.MoveTo.IsMovingTo());
        Assert.True(h.MoveTo.Initialized);
        Assert.NotEmpty(h.MoveTo.PendingActions);
        Assert.Equal(1, h.Inventory.BusyCount);

        h.MovementController.Movement.CancelMoveTo(
            WeenieError.ActionCancelled);
        Assert.Equal(1, h.Inventory.BusyCount);

        h.Controller.DrainOutbound();

        Assert.Empty(h.Transport.Uses);
        Assert.Equal(0, h.Inventory.BusyCount);
        Assert.True(h.Transactions.TryGetPendingUse(out _) == false);
    }

    [Fact]
    public void NewFarUseSupersedesThePriorApproachAndReleasesItsReservation()
    {
        using var h = new Harness(bindObjectTableResolver: true);
        h.AddFarTarget(Vendor, new Vector3(10f, 0f, 0f));
        h.AddFarTarget(OtherVendor, new Vector3(3.5f, 0f, 0f));
        ItemUseRequestReservation first =
            h.Transactions.BeginUseRequestReservation();
        h.Controller.RequestUse(Vendor, first);
        Assert.Equal(1, h.Inventory.BusyCount);

        ItemUseRequestReservation second =
            h.Transactions.BeginUseRequestReservation();
        Assert.Equal(2, h.Inventory.BusyCount);
        h.Controller.RequestUse(OtherVendor, second);

        Assert.Equal(1, h.Inventory.BusyCount);

        h.Controller.DrainOutbound();

        Assert.Equal(new[] { OtherVendor }, h.Transport.Uses);
        Assert.Equal(1, h.Inventory.BusyCount);
        h.Transactions.CompleteUse(0u);
        Assert.Equal(0, h.Inventory.BusyCount);
    }

    [Fact]
    public void UnresolvableTargetLeavesTheApproachInertUntilCancelReleasesTheReservation()
    {
        using var h = new Harness(bindObjectTableResolver: false);
        h.AddFarTarget(Vendor, new Vector3(3.5f, 0f, 0f));
        ItemUseRequestReservation reservation =
            h.Transactions.BeginUseRequestReservation();

        h.Controller.RequestUse(Vendor, reservation);
        h.Controller.DrainOutbound();

        // Armed but inert — the live vendor-diag pathology.
        Assert.True(h.MoveTo.IsMovingTo());
        Assert.False(h.MoveTo.Initialized);
        Assert.Empty(h.MoveTo.PendingActions);
        Assert.Empty(h.Transport.Uses);
        Assert.Equal(1, h.Inventory.BusyCount);

        h.MovementController.Movement.CancelMoveTo(
            WeenieError.ActionCancelled);
        h.Controller.DrainOutbound();

        Assert.Empty(h.Transport.Uses);
        Assert.Equal(0, h.Inventory.BusyCount);
    }
}
