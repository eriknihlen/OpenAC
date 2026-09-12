using System.Numerics;
using AcDream.App.Interaction;
using AcDream.App.UI;
using AcDream.App.World;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Selection;
using AcDream.Core.World;
using AcDream.Runtime.Gameplay;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.Tests.Interaction;

public sealed class SelectionInteractionControllerTests
{
    private const uint Player = 0x5000_0001u;
    private const uint Target = 0x7000_0001u;
    private const uint GroundContainer = 0x7000_0010u;
    private const uint Wielder = 0x7000_0100u;
    private const uint RemoteWeapon = 0x7000_0101u;
    private const uint OwnWeapon = 0x7000_0102u;
    private const uint GroundWeapon = 0x7000_0103u;

    private sealed class Query : IWorldSelectionQuery
    {
        public uint? Picked { get; set; }
        public bool LastIncludeSelf { get; private set; }
        public bool Current { get; set; } = true;
        public bool Creature { get; set; }
        public bool Hostile { get; set; }
        public bool Attackable { get; set; }
        public bool Useable { get; set; } = true;
        public bool Pickupable { get; set; } = true;
        public bool WieldedByPlayer { get; set; }
        public HashSet<uint> WieldedPositionStates { get; } = new();
        public bool CaptureIdentity { get; set; } = true;
        public uint LocalEntityId { get; set; } = 101u;
        public ClosestCombatTarget? Closest { get; set; }
        public InteractionApproach? Approach { get; set; }
        public List<string> Events { get; } = new();

        public uint? PickAtCursor(bool includeSelf)
        {
            LastIncludeSelf = includeSelf;
            Events.Add("pick");
            return Picked;
        }

        public uint? PickAt(float mouseX, float mouseY, bool includeSelf)
            => PickAtCursor(includeSelf);

        public void BeginLightingPulse(uint serverGuid) => Events.Add("pulse");
        public bool TryCaptureIdentity(uint serverGuid, out uint localEntityId)
        {
            localEntityId = LocalEntityId;
            return CaptureIdentity;
        }
        public bool IsCurrent(uint serverGuid, uint localEntityId) => Current;
        public string Describe(uint serverGuid) => $"Target {serverGuid:X8}";
        public bool IsCreature(uint serverGuid) => Creature;
        public bool IsHostileMonster(uint serverGuid) => Hostile;
        public bool IsAttackableTarget(uint serverGuid) => Attackable;
        public ClosestCombatTarget? FindClosestHostileMonster() => Closest;
        public bool IsUseable(uint serverGuid) => Useable;
        public bool IsPickupable(uint serverGuid) => Pickupable;
        public bool IsWieldedByPlayer(uint serverGuid) => WieldedByPlayer;
        public bool IsWieldedPositionState(uint serverGuid)
            => WieldedPositionStates.Contains(serverGuid);
        public Vector3? GetCombatCameraTargetPoint(uint serverGuid) => null;

        public bool TryGetApproach(uint serverGuid, out InteractionApproach approach)
        {
            if (Approach is { } found)
            {
                approach = found;
                return true;
            }
            approach = default;
            return false;
        }
    }

    private sealed class Transport : IRuntimeInteractionTransport
    {
        private uint _sequence;
        public bool IsInWorld { get; set; } = true;
        public List<uint> Uses { get; } = new();
        public List<(uint Item, uint Container, int Placement)> Pickups { get; } = new();

        public bool TrySendUse(uint serverGuid, out uint sequence)
        {
            if (!IsInWorld)
            {
                sequence = 0u;
                return false;
            }
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
            if (!IsInWorld)
            {
                sequence = 0u;
                return false;
            }
            sequence = ++_sequence;
            Pickups.Add((itemGuid, destinationContainerId, placement));
            return true;
        }
    }

    private sealed class Movement(IPlayerApproachTokenSource approachTokens)
        : IPlayerInteractionMovementSink
    {
        public List<InteractionApproach> Approaches { get; } = new();
        public bool Starts { get; set; } = true;
        public Action? AfterArm { get; set; }
        public bool BeginApproach(
            InteractionApproach approach,
            Action<PlayerApproachToken>? armAfterCancel = null)
        {
            Approaches.Add(approach);
            if (!Starts || !approachTokens.TryBeginApproach(out PlayerApproachToken token))
                return false;
            armAfterCancel?.Invoke(token);
            AfterArm?.Invoke();
            return true;
        }
    }

    private sealed class CombatTargetOperations(SelectionState selection)
        : IRuntimeCombatTargetOperations
    {
        public bool AutoTarget { get; set; }
        public uint? Reacquires { get; set; }
        public int Calls { get; private set; }

        public uint? SelectClosestTarget()
        {
            Calls++;
            if (Reacquires is not { } guid)
                return null;
            selection.Select(guid, SelectionChangeSource.System);
            return guid;
        }
    }

    private sealed class Harness
    {
        public readonly Query Query = new();
        public readonly Transport Transport = new();
        public readonly PlayerApproachCompletionState Completions = new();
        public readonly IPlayerApproachCompletionSink CompletionLifetime;
        public readonly Movement Movement;
        public readonly SelectionState Selection = new();
        public readonly ClientObjectTable Objects = new();
        public readonly List<string> Toasts = new();
        public readonly List<uint> Examines = new();
        public readonly List<PendingBackpackPlacement> PendingPlacements = new();
        public readonly List<PendingBackpackPlacement> CancelledPlacements = new();
        public readonly ItemInteractionController Items;
        public readonly CombatState Combat = new();
        public readonly CombatTargetOperations CombatTargetOperations;
        public readonly RuntimeCombatTargetState CombatTarget;
        public readonly SelectionInteractionController Controller;
        public uint GroundObjectId { get; set; }

        public Harness()
        {
            CompletionLifetime = Completions.BeginControllerLifetime();
            Movement = new Movement(Completions);
            SelectionInteractionController? controller = null;
            Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = Player,
                Type = ItemType.Creature,
            });
            Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = Target,
                Name = "Target",
                Type = ItemType.Creature,
                Useability = ItemUseability.Remote,
                PublicWeenieBitfield = (uint)PublicWeenieFlags.Stuck,
            });
            Items = new ItemInteractionController(
                Objects,
                new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(new InventoryTransactionState(Objects)),
                new InteractionState(),
                () => Player,
                sendUse: null,
                sendUseWithTarget: null,
                sendWield: null,
                sendDrop: null,
                sendExamine: guid =>
                {
                    Query.Events.Add("examine");
                    Examines.Add(guid);
                },
                groundObjectId: () => GroundObjectId,
                placeInBackpack: (item, container, placement) =>
                    controller!.SendPickup(item, container, placement),
                requestUse: (guid, reservation) =>
                    controller!.RequestUse(guid, reservation));
            CombatTargetOperations = new CombatTargetOperations(Selection);
            CombatTarget = new RuntimeCombatTargetState(
                Combat,
                Selection,
                CombatTargetOperations);
            Controller = controller = new SelectionInteractionController(
                Selection,
                Query,
                Items,
                Transport,
                Movement,
                Toasts.Add,
                Completions,
                combatTarget: CombatTarget);
            Items.PendingBackpackPlacementRequested += PendingPlacements.Add;
            Items.PendingBackpackPlacementCancelled += CancelledPlacements.Add;
        }

        public void SetApproach(
            bool closeRange,
            uint localEntityId = 101u,
            uint serverGuid = Target)
        {
            var entity = new WorldEntity
            {
                Id = localEntityId,
                ServerGuid = serverGuid,
                SourceGfxObjOrSetupId = 0x0200_0001u,
                Position = new Vector3(5f, 0f, 0f),
                Rotation = Quaternion.Identity,
                MeshRefs = [],
            };
            Query.Approach = new InteractionApproach(
                new WorldInteractionTarget(serverGuid, localEntityId, entity),
                new PlayerInteractionPose(0x0101_0001u, Vector3.Zero),
                0.6f,
                closeRange,
                CanCharge: !closeRange,
                TargetRadius: 0.5f,
                TargetHeight: 2f);
        }
    }

    [Fact]
    public void Escape_ClearsCurrentSelectionBeforeTheOptionsFallback()
    {
        var h = new Harness();
        h.Selection.Select(Target, SelectionChangeSource.World);

        Assert.True(h.Controller.HandleInputAction(InputAction.EscapeKey));

        Assert.Null(h.Selection.SelectedObjectId);
    }

    [Fact]
    public void Escape_WithAutomaticTargeting_DropsTheTargetForGood()
    {
        // OpenAC #40: one press used to empty the selection and have automatic
        // targeting pick the same creature straight back up, so the target
        // only flickered and the press appeared to do nothing.
        var h = new Harness();
        h.Combat.SetCombatMode(CombatMode.Melee);
        h.CombatTargetOperations.AutoTarget = true;
        h.CombatTargetOperations.Reacquires = Target;
        h.Selection.Select(Target, SelectionChangeSource.World);

        Assert.True(h.Controller.HandleInputAction(InputAction.EscapeKey));

        Assert.Null(h.Selection.SelectedObjectId);
        Assert.Equal(0, h.CombatTargetOperations.Calls);
    }

    [Fact]
    public void Escape_WithNoTargetModeOrSelection_FallsThrough()
    {
        var h = new Harness();

        Assert.False(h.Controller.HandleInputAction(InputAction.EscapeKey));
    }

    [Fact]
    public void TargetModeClickPulsesBeforeItIsConsumedAndIncludesSelf()
    {
        var h = new Harness();
        h.Query.Picked = Target;
        h.Items.InteractionState.EnterExamine();

        Assert.True(h.Controller.HandleInputAction(InputAction.SelectLeft));

        Assert.True(h.Query.LastIncludeSelf);
        Assert.Equal(new[] { "pick", "pulse", "examine" }, h.Query.Events);
        Assert.Equal(new[] { Target }, h.Examines);
        Assert.Null(h.Selection.SelectedObjectId);
    }

    [Fact]
    public void RightClickPulsesSelectsAndExaminesPickedWorldObject()
    {
        var h = new Harness();
        h.Query.Picked = Target;

        Assert.True(h.Controller.HandleInputAction(InputAction.SelectRight));

        Assert.True(h.Query.LastIncludeSelf);
        Assert.Equal(Target, h.Selection.SelectedObjectId);
        Assert.Equal(new[] { "pick", "pulse", "examine" }, h.Query.Events);
        Assert.Equal(new[] { Target }, h.Examines);
    }

    [Fact]
    public void RightClickEmptyWorldIsANoOp()
    {
        var h = new Harness();

        Assert.True(h.Controller.HandleInputAction(InputAction.SelectRight));

        Assert.Equal(new[] { "pick" }, h.Query.Events);
        Assert.Null(h.Selection.SelectedObjectId);
        Assert.Empty(h.Examines);
        Assert.Empty(h.Toasts);
    }

    [Fact]
    public void RightClickExaminesWithoutConsumingLeftClickTargetMode()
    {
        var h = new Harness();
        h.Query.Picked = Target;
        h.Items.InteractionState.EnterUse();

        h.Controller.HandleInputAction(InputAction.SelectRight);

        Assert.Equal(InteractionModeKind.Use, h.Items.InteractionState.Current.Kind);
        Assert.Equal(Target, h.Selection.SelectedObjectId);
        Assert.Equal(new[] { Target }, h.Examines);
    }

    [Fact]
    public void ExamineActionUsesSelectionOrEntersTargetMode()
    {
        var selected = new Harness();
        selected.Selection.Select(Target, SelectionChangeSource.World);

        Assert.True(selected.Controller.HandleInputAction(
            InputAction.SelectionExamine));
        Assert.Equal(new[] { Target }, selected.Examines);

        var empty = new Harness();
        Assert.True(empty.Controller.HandleInputAction(
            InputAction.SelectionExamine));
        Assert.Equal(
            InteractionModeKind.Examine,
            empty.Items.InteractionState.Current.Kind);
    }

    [Fact]
    public void ClosestTargetInputMutatesSelectionOnce()
    {
        var h = new Harness();
        h.Query.Closest = new ClosestCombatTarget(Target, 25f);

        Assert.True(h.Controller.HandleInputAction(InputAction.SelectionClosestMonster));

        Assert.Equal(Target, h.Selection.SelectedObjectId);
        Assert.Contains(h.Toasts, text => text.Contains("Target 70000001"));
    }

    [Fact]
    public void EveryRetailItemSelectionRowHasALiveControllerConsumer()
    {
        InputAction[] actions = RetailActionIdentityTable.Map
            .Where(entry => entry.Key.InputMapId == 0x10000007u)
            .OrderBy(entry => entry.Key.ActionId)
            .Select(entry => entry.Value)
            .ToArray();

        Assert.Equal(26, actions.Length);
        foreach (InputAction action in actions)
        {
            var harness = new Harness();
            Assert.True(
                harness.Controller.HandleInputAction(action),
                $"No selection consumer for {action}");
        }
    }

    [Fact]
    public void CloseUseSendsImmediatelyWithoutSpeculativeMovement()
    {
        var h = new Harness();
        h.SetApproach(closeRange: true);

        h.Controller.SendUse(Target);

        Assert.Empty(h.Movement.Approaches);
        Assert.Equal(new[] { Target }, h.Transport.Uses);

        h.Controller.OnNaturalMoveToComplete();
        h.Controller.OnNaturalMoveToComplete();

        Assert.Equal(new[] { Target }, h.Transport.Uses);
    }

    [Fact]
    public void AcceptedUseRemainsBusyUntilAuthoritativeUseDone()
    {
        var h = new Harness();
        h.SetApproach(closeRange: true);
        h.Selection.Select(Target, SelectionChangeSource.World);

        h.Controller.HandleInputAction(InputAction.UseSelected);
        h.Controller.DrainOutbound();

        Assert.Equal(1, h.Items.BusyCount);
        Assert.Equal(new[] { Target }, h.Transport.Uses);

        h.CompletionLifetime.PublishCancellation(WeenieError.ActionCancelled);
        h.Controller.DrainOutbound();

        Assert.Equal(1, h.Items.BusyCount);
        Assert.False(h.Items.CanMakeInventoryRequest);

        h.Items.CompleteUse(0u);

        Assert.Equal(0, h.Items.BusyCount);
        Assert.True(h.Items.CanMakeInventoryRequest);
    }

    [Fact]
    public void DispatchedWorldUseRemainsBusyUntilAuthoritativeUseDone()
    {
        var h = new Harness();
        h.SetApproach(closeRange: true);
        h.Selection.Select(Target, SelectionChangeSource.World);

        h.Controller.HandleInputAction(InputAction.UseSelected);
        h.Controller.DrainOutbound();

        Assert.Equal(new[] { Target }, h.Transport.Uses);
        Assert.Equal(1, h.Items.BusyCount);

        h.Items.CompleteUse(0u);

        Assert.Equal(0, h.Items.BusyCount);
        Assert.True(h.Items.CanMakeInventoryRequest);
    }

    [Fact]
    public void WorldUseDoesNotDependOnLocalApproachInstallation()
    {
        var h = new Harness();
        h.SetApproach(closeRange: true);
        h.Movement.Starts = false;
        h.Selection.Select(Target, SelectionChangeSource.World);

        h.Controller.HandleInputAction(InputAction.UseSelected);
        h.Controller.DrainOutbound();

        Assert.Equal(1, h.Items.BusyCount);
        Assert.Equal(new[] { Target }, h.Transport.Uses);
        Assert.Empty(h.Movement.Approaches);
    }

    [Fact]
    public void RejectedWorldUseReleasesItsBusyReservation()
    {
        var h = new Harness();
        h.Query.Useable = false;
        h.Selection.Select(Target, SelectionChangeSource.World);

        h.Controller.HandleInputAction(InputAction.UseSelected);
        h.Controller.DrainOutbound();

        Assert.Equal(0, h.Items.BusyCount);
        Assert.Empty(h.Transport.Uses);
    }

    [Fact]
    public void FarUnusableTargetIsRejectedWithoutApproaching()
    {
        var h = new Harness();
        h.Query.Useable = false;
        h.SetApproach(closeRange: false);

        h.Controller.SendUse(Target);

        Assert.Empty(h.Movement.Approaches);
        Assert.Empty(h.Transport.Uses);
    }

    [Fact]
    public void SynchronousMovementCallbackCannotDuplicateWorldUse()
    {
        var h = new Harness();
        h.SetApproach(closeRange: true);
        h.Movement.AfterArm = h.Controller.OnNaturalMoveToComplete;
        h.Selection.Select(Target, SelectionChangeSource.World);

        h.Controller.HandleInputAction(InputAction.UseSelected);
        h.Controller.DrainOutbound();

        Assert.Equal(new[] { Target }, h.Transport.Uses);
        Assert.Equal(1, h.Items.BusyCount);

        h.Items.CompleteUse(0u);

        Assert.Equal(0, h.Items.BusyCount);
    }

    [Fact]
    public void FarUseApproachesThenDispatchesOnNaturalArrival()
    {
        var h = new Harness();
        h.SetApproach(closeRange: false);

        h.Controller.SendUse(Target);

        PlayerInteractionMovementSinkAssertSingleApproach(h, Target);
        // Armed, not yet sent — the whole point of the fix.
        Assert.Empty(h.Transport.Uses);

        h.Controller.OnNaturalMoveToComplete();

        Assert.Equal(new[] { Target }, h.Transport.Uses);

        h.Controller.OnNaturalMoveToComplete();

        Assert.Equal(new[] { Target }, h.Transport.Uses);
    }

    [Fact]
    public void NewFarUseCommandSupersedesThePreviousApproachCleanly()
    {
        const uint otherTarget = 0x7000_0099u;
        var h = new Harness();
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = otherTarget,
            Name = "Other",
            Type = ItemType.Creature,
            Useability = ItemUseability.Remote,
        });
        h.SetApproach(closeRange: false);

        h.Controller.SendUse(Target);
        h.SetApproach(closeRange: false, serverGuid: otherTarget);
        h.Controller.SendUse(otherTarget);

        Assert.Equal(2, h.Movement.Approaches.Count);
        Assert.Equal(Target, h.Movement.Approaches[0].Target.ServerGuid);
        Assert.Equal(otherTarget, h.Movement.Approaches[1].Target.ServerGuid);
        // Neither has sent yet — both are armed/superseded, not dispatched.
        Assert.Empty(h.Transport.Uses);

        h.Controller.OnNaturalMoveToComplete();

        // Only the surviving (second) approach's Use goes out.
        Assert.Equal(new[] { otherTarget }, h.Transport.Uses);
    }

    [Fact]
    public void MovingAwayDuringAFarUseApproachCancelsTheArmedUse()
    {
        var h = new Harness();
        h.SetApproach(closeRange: false);

        h.Controller.SendUse(Target);
        h.Controller.OnMoveToCancelled(WeenieError.ActionCancelled);
        h.Controller.OnNaturalMoveToComplete();

        Assert.Empty(h.Transport.Uses);
    }

    private static void PlayerInteractionMovementSinkAssertSingleApproach(
        Harness h, uint expectedTarget)
    {
        InteractionApproach approach = Assert.Single(h.Movement.Approaches);
        Assert.Equal(expectedTarget, approach.Target.ServerGuid);
    }

    [Fact]
    public void CarriedDirectUseBypassesWorldApproachAndWaitsForUseDone()
    {
        const uint favor = 0x8000_0606u;
        var h = new Harness();
        h.Query.Useable = false;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = favor,
            Name = "Blackmoor's Favor",
            Type = ItemType.Gem,
            Useability = ItemUseability.Undef,
        });
        Assert.True(h.Objects.MoveItem(favor, Player, newSlot: 1));

        Assert.True(h.Items.ActivateItem(favor));

        Assert.Empty(h.Movement.Approaches);
        Assert.Equal(new[] { favor }, h.Transport.Uses);
        Assert.Equal(1, h.Items.BusyCount);

        h.Items.CompleteUse(0u);

        Assert.Equal(0, h.Items.BusyCount);
        Assert.True(h.Items.CanMakeInventoryRequest);
    }

    [Fact]
    public void HiddenOrStaleNotificationsCannotRetractOrDuplicateDispatchedUse()
    {
        var hidden = new Harness();
        hidden.SetApproach(closeRange: true);
        hidden.Controller.SendUse(Target);
        hidden.Controller.OnEntityHidden(Target);
        hidden.Controller.OnNaturalMoveToComplete();
        Assert.Equal(new[] { Target }, hidden.Transport.Uses);

        var stale = new Harness();
        stale.SetApproach(closeRange: true);
        stale.Controller.SendUse(Target);
        stale.Query.Current = false;
        stale.Controller.OnNaturalMoveToComplete();
        Assert.Equal(new[] { Target }, stale.Transport.Uses);
    }

    [Fact]
    public void HiddenTargetCancelsQueuedUseBeforeFrameDrain()
    {
        var h = new Harness();
        h.Selection.Select(Target, SelectionChangeSource.World);
        h.Controller.HandleInputAction(InputAction.UseSelected);

        h.Controller.OnEntityHidden(Target);
        h.Controller.DrainOutbound();

        Assert.Null(h.Selection.SelectedObjectId);
        Assert.Empty(h.Transport.Uses);
        Assert.Equal(0, h.Items.BusyCount);
    }

    [Fact]
    public void PickupInputWaitsForFrameDrainThenUsesPendingDestination()
    {
        var h = new Harness();
        h.SetApproach(closeRange: false);
        h.Selection.Select(Target, SelectionChangeSource.World);

        Assert.True(h.Controller.HandleInputAction(InputAction.SelectionPickUp));
        Assert.Empty(h.Transport.Pickups);

        h.Controller.DrainOutbound();

        Assert.Equal(new[] { (Target, Player, 0) }, h.Transport.Pickups);
    }

    [Fact]
    public void CorpseChildDoubleClickDispatchesWithoutAWorldProjection()
    {
        const uint loot = 0x7000_0011u;
        var h = new Harness
        {
            GroundObjectId = GroundContainer,
        };
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = loot,
            Name = "Corpse loot",
            Type = ItemType.Misc,
            ContainerId = GroundContainer,
        });
        h.Query.Pickupable = false;
        h.Query.Approach = null;

        Assert.True(h.Items.ActivateItem(loot));

        Assert.Empty(h.Movement.Approaches);
        Assert.Equal(new[] { (loot, Player, 0) }, h.Transport.Pickups);
        Assert.Single(h.PendingPlacements);
        Assert.Empty(h.CancelledPlacements);
    }

    [Fact]
    public void SessionResetClearsQueuedAndDeferredWork()
    {
        var h = new Harness();
        h.SetApproach(closeRange: true);
        h.Selection.Select(Target, SelectionChangeSource.World);
        h.Controller.HandleInputAction(InputAction.SelectionPickUp);
        h.Controller.HandleInputAction(InputAction.UseSelected);
        h.Items.InteractionState.EnterExamine();

        h.Controller.ResetSession();
        h.Controller.DrainOutbound();
        h.Controller.OnNaturalMoveToComplete();

        Assert.Null(h.Selection.SelectedObjectId);
        Assert.False(h.Items.IsAnyTargetModeActive);
        Assert.Empty(h.Transport.Pickups);
        Assert.Empty(h.Transport.Uses);
    }

    [Fact]
    public void OldRemovalClearsCapturedPickupButDoesNotClearReplacementSelection()
    {
        var h = new Harness();
        var oldRecord = LiveEntityTestFixture.CreateExactProjectionRecord(
            Spawn(Target, instance: 1));
        uint localEntityId = oldRecord.LocalEntityId!.Value;
        h.SetApproach(closeRange: true, localEntityId: localEntityId);
        h.Selection.Select(Target, SelectionChangeSource.World);
        Assert.True(h.Items.PlaceWorldItemInBackpack(Target));
        oldRecord.WorldEntity = new WorldEntity
        {
            Id = localEntityId,
            ServerGuid = Target,
            SourceGfxObjOrSetupId = 0x0200_0001u,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs = [],
        };

        h.Controller.OnEntityRemoved(oldRecord, replacementExists: true);
        h.Controller.OnNaturalMoveToComplete();

        Assert.Equal(Target, h.Selection.SelectedObjectId);
        Assert.Empty(h.Transport.Pickups);
        Assert.Equal(new[] { Target }, h.CancelledPlacements.Select(p => p.ItemId));
    }

    [Fact]
    public void SynchronousNaturalCompletionCannotDuplicateUse()
    {
        var h = new Harness();
        h.SetApproach(closeRange: true);
        h.Movement.AfterArm = h.Controller.OnNaturalMoveToComplete;

        h.Controller.SendUse(Target);
        h.Controller.OnNaturalMoveToComplete();

        Assert.Equal(new[] { Target }, h.Transport.Uses);
    }

    [Fact]
    public void SynchronousResetCannotResurrectACancelledPickup()
    {
        var h = new Harness();
        h.SetApproach(closeRange: true);
        h.Movement.AfterArm = h.Controller.ResetSession;

        Assert.True(h.Items.PlaceWorldItemInBackpack(Target));
        h.Controller.OnNaturalMoveToComplete();

        Assert.Empty(h.Transport.Pickups);
    }

    [Fact]
    public void CancelledClosePickupWithdrawsPresentationAndNeverFiresLater()
    {
        var h = new Harness();
        h.SetApproach(closeRange: true);

        Assert.True(h.Items.PlaceWorldItemInBackpack(Target));
        Assert.Single(h.PendingPlacements);
        Assert.Empty(h.Transport.Pickups);

        h.Controller.OnMoveToCancelled(WeenieError.ActionCancelled);
        h.Controller.OnNaturalMoveToComplete();

        Assert.Equal(new[] { Target }, h.CancelledPlacements.Select(p => p.ItemId));
        Assert.Empty(h.Transport.Pickups);
    }

    [Fact]
    public void ErrorCompletionCannotRetractOrDuplicateDispatchedUse()
    {
        var h = new Harness();
        h.SetApproach(closeRange: true);
        h.Controller.SendUse(Target);

        h.Controller.OnMoveToCancelled(WeenieError.NoObject);
        h.Controller.OnNaturalMoveToComplete();

        Assert.Equal(new[] { Target }, h.Transport.Uses);
    }

    [Fact]
    public void CompletionFromPriorPickupCannotAffectReplacement()
    {
        var h = new Harness();
        h.SetApproach(closeRange: true);
        Assert.True(h.Items.PlaceWorldItemInBackpack(Target));
        h.CompletionLifetime.PublishCancellation(WeenieError.ActionCancelled);

        h.Selection.Select(Target, SelectionChangeSource.World);
        h.Controller.HandleInputAction(InputAction.UseSelected);
        h.Controller.DrainOutbound();

        Assert.Equal(new[] { Target }, h.Transport.Uses);
        Assert.Empty(h.Transport.Pickups);

        h.CompletionLifetime.PublishNaturalCompletion();
        h.Controller.DrainOutbound();

        Assert.Equal(new[] { Target }, h.Transport.Uses);
        Assert.Empty(h.Transport.Pickups);
    }

    [Fact]
    public void RetiringControllerLifetimeInvalidatesQueuedPickupArrival()
    {
        var h = new Harness();
        h.SetApproach(closeRange: true);
        Assert.True(h.Items.PlaceWorldItemInBackpack(Target));
        h.CompletionLifetime.PublishNaturalCompletion();

        h.Completions.RetireControllerLifetime(h.CompletionLifetime);
        h.Controller.DrainOutbound();

        Assert.Empty(h.Transport.Pickups);
        h.Controller.OnNaturalMoveToComplete();
        Assert.Empty(h.Transport.Pickups);
    }

    [Fact]
    public void FailedClosePickupStartWithdrawsPresentation()
    {
        var h = new Harness();
        h.SetApproach(closeRange: true);
        h.Movement.Starts = false;

        Assert.True(h.Items.PlaceWorldItemInBackpack(Target));

        Assert.Single(h.PendingPlacements);
        Assert.Equal(new[] { Target }, h.CancelledPlacements.Select(p => p.ItemId));
        Assert.Empty(h.Transport.Pickups);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidKeyboardPickupNeverPublishesAWaitingSlot(bool creature)
    {
        var h = new Harness();
        h.Query.Creature = creature;
        h.Query.Pickupable = false;
        h.Selection.Select(Target, SelectionChangeSource.World);

        h.Controller.HandleInputAction(InputAction.SelectionPickUp);
        h.Controller.DrainOutbound();

        Assert.Empty(h.PendingPlacements);
        Assert.Empty(h.Transport.Pickups);
    }

    [Theory]
    [InlineData(InputAction.SelectDblLeft)]
    [InlineData(InputAction.UseSelected)]
    [InlineData(InputAction.SelectionPickUp)]
    public void QueuedWorldActionCannotCrossAnIncarnationBoundary(InputAction action)
    {
        var h = new Harness();
        h.Query.Picked = Target;
        h.SetApproach(closeRange: false);
        h.Selection.Select(Target, SelectionChangeSource.World);

        h.Controller.HandleInputAction(action);
        h.Query.Current = false;
        h.Controller.DrainOutbound();

        Assert.Empty(h.Transport.Uses);
        Assert.Empty(h.Transport.Pickups);
        Assert.Empty(h.PendingPlacements);
    }

    [Fact]
    public void DoubleClickOnTheOwnWieldedObjectSelectsAndPulsesWithoutUse()
    {
        var control = new Harness();
        control.Query.Picked = Target;
        control.Controller.HandleInputAction(InputAction.SelectDblLeft);
        control.Controller.DrainOutbound();
        Assert.NotEmpty(control.Transport.Uses);

        var h = new Harness();
        h.Query.Picked = Target;
        h.Query.WieldedByPlayer = true;

        h.Controller.HandleInputAction(InputAction.SelectDblLeft);
        h.Controller.DrainOutbound();

        Assert.Equal(Target, h.Selection.SelectedObjectId);
        Assert.Contains("pulse", h.Query.Events);
        Assert.Empty(h.Transport.Uses);
    }

    private static void AddWieldedWeapon(Harness h, uint guid, uint wielderId)
    {
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = guid,
            Name = $"Weapon {guid:X8}",
            Type = ItemType.MeleeWeapon,
            WielderId = wielderId,
            CurrentlyEquippedLocation = EquipMask.MeleeWeapon,
        });
        h.Query.WieldedPositionStates.Add(guid);
    }

    [Fact]
    public void PickupOfARemotesWieldedItemIsRejectedLocallyWithNoMovementOrRequest()
    {
        var h = new Harness();
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Wielder,
            Name = "Remote",
            Type = ItemType.Creature,
        });
        AddWieldedWeapon(h, RemoteWeapon, wielderId: Wielder);
        h.SetApproach(closeRange: false, serverGuid: RemoteWeapon);
        h.Selection.Select(RemoteWeapon, SelectionChangeSource.World);

        Assert.True(h.Controller.HandleInputAction(InputAction.SelectionPickUp));
        h.Controller.DrainOutbound();

        Assert.Contains(
            $"The Target {RemoteWeapon:X8} is being wielded by someone else!",
            h.Toasts);
        Assert.Empty(h.Transport.Pickups);
        Assert.Empty(h.Transport.Uses);
        Assert.Empty(h.Movement.Approaches);
        Assert.Empty(h.PendingPlacements);
    }

    [Fact]
    public void DirectBackpackPlacementOfARemotesWieldedItemWithdrawsItsWaitingSlot()
    {
        var h = new Harness();
        AddWieldedWeapon(h, RemoteWeapon, wielderId: Wielder);
        h.SetApproach(closeRange: true, serverGuid: RemoteWeapon);

        Assert.True(h.Items.PlaceWorldItemInBackpack(RemoteWeapon));

        Assert.Contains(
            $"The Target {RemoteWeapon:X8} is being wielded by someone else!",
            h.Toasts);
        Assert.Empty(h.Transport.Pickups);
        Assert.Empty(h.Movement.Approaches);
        Assert.Single(h.PendingPlacements);
        Assert.Equal(
            new[] { RemoteWeapon },
            h.CancelledPlacements.Select(p => p.ItemId));
    }

    [Fact]
    public void ARemotesWieldedItemStaysSelectableAndExaminable()
    {
        var h = new Harness();
        AddWieldedWeapon(h, RemoteWeapon, wielderId: Wielder);
        h.Query.Picked = RemoteWeapon;

        Assert.True(h.Controller.HandleInputAction(InputAction.SelectRight));

        Assert.Equal(RemoteWeapon, h.Selection.SelectedObjectId);
        Assert.Equal(new[] { "pick", "pulse", "examine" }, h.Query.Events);
        Assert.Equal(new[] { RemoteWeapon }, h.Examines);
    }

    [Fact]
    public void AGroundItemOfTheSameTypeStillPicksUpNormally()
    {
        var h = new Harness();
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = GroundWeapon,
            Name = "Ground weapon",
            Type = ItemType.MeleeWeapon,
        });
        h.SetApproach(closeRange: false, serverGuid: GroundWeapon);
        h.Selection.Select(GroundWeapon, SelectionChangeSource.World);

        Assert.True(h.Controller.HandleInputAction(InputAction.SelectionPickUp));
        h.Controller.DrainOutbound();

        Assert.Equal(new[] { (GroundWeapon, Player, 0) }, h.Transport.Pickups);
        Assert.Single(h.Movement.Approaches);
        Assert.Empty(h.CancelledPlacements);
        Assert.DoesNotContain(h.Toasts, text => text.Contains("wielded"));
    }

    [Fact]
    public void PickupOfThePlayersOwnWieldedItemTransfersImmediatelyWithoutApproach()
    {
        var h = new Harness();
        AddWieldedWeapon(h, OwnWeapon, wielderId: Player);
        h.SetApproach(closeRange: true, serverGuid: OwnWeapon);
        h.Selection.Select(OwnWeapon, SelectionChangeSource.World);

        Assert.True(h.Controller.HandleInputAction(InputAction.SelectionPickUp));
        h.Controller.DrainOutbound();

        Assert.Equal(new[] { (OwnWeapon, Player, 0) }, h.Transport.Pickups);
        Assert.Empty(h.Movement.Approaches);
        Assert.Empty(h.CancelledPlacements);
        Assert.DoesNotContain(h.Toasts, text => text.Contains("wielded"));
    }

    /// <summary>
    /// The WIELDED no-approach shortcut must never outrun the legality gate:
    /// ownership alone decides which of the two wielded items may transfer.
    /// </summary>
    [Fact]
    public void TheWieldedShortcutIsGatedOnOwnershipNotOnWieldedStateAlone()
    {
        var h = new Harness();
        AddWieldedWeapon(h, RemoteWeapon, wielderId: Wielder);
        AddWieldedWeapon(h, OwnWeapon, wielderId: Player);

        h.SetApproach(closeRange: true, serverGuid: RemoteWeapon);
        Assert.True(h.Items.PlaceWorldItemInBackpack(RemoteWeapon));
        Assert.Empty(h.Transport.Pickups);

        h.SetApproach(closeRange: true, serverGuid: OwnWeapon);
        Assert.True(h.Items.PlaceWorldItemInBackpack(OwnWeapon));

        Assert.Equal(new[] { (OwnWeapon, Player, 0) }, h.Transport.Pickups);
        Assert.Empty(h.Movement.Approaches);
    }

    [Fact]
    public void DragReleasePulsesTheDropTargetWithoutChangingSelection()
    {
        var h = new Harness();
        h.Query.Picked = Target;
        const uint item = 0x7000_0002u;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = item,
            Name = "Dragged",
            Type = ItemType.Misc,
        });
        var payload = new ItemDragPayload(
            item,
            ItemDragSource.Inventory,
            SourceSlot: 0,
            new UiItemSlot());

        h.Controller.PlaceDraggedItem(payload, 10f, 20f);

        Assert.Contains("pulse", h.Query.Events);
        Assert.Null(h.Selection.SelectedObjectId);
    }

    [Fact]
    public void RejectedTargetModeClickConsumesTheFollowingDoubleClick()
    {
        var h = new Harness();
        h.Query.Picked = Target;
        h.Objects.Remove(Target);
        h.Items.InteractionState.EnterUse();

        h.Controller.HandleInputAction(InputAction.SelectLeft);
        h.Controller.HandleInputAction(InputAction.SelectDblLeft);
        h.Controller.DrainOutbound();

        Assert.Null(h.Selection.SelectedObjectId);
        Assert.Empty(h.Transport.Uses);
        Assert.Empty(h.Transport.Pickups);
    }

    [Fact]
    public void TransportLossAtClosePickupCompletionWithdrawsPresentation()
    {
        var h = new Harness();
        h.SetApproach(closeRange: true);
        Assert.True(h.Items.PlaceWorldItemInBackpack(Target));

        h.Transport.IsInWorld = false;
        h.Controller.OnNaturalMoveToComplete();

        Assert.Equal(new[] { Target }, h.CancelledPlacements.Select(p => p.ItemId));
        Assert.Empty(h.Transport.Pickups);
    }

    [Fact]
    public void RepeatedPickupKeepsTheOriginalReservationAndDeferredAction()
    {
        var h = new Harness();
        h.SetApproach(closeRange: true);
        Assert.True(h.Items.PlaceWorldItemInBackpack(Target));
        h.Selection.Select(Target, SelectionChangeSource.World);

        h.Controller.HandleInputAction(InputAction.SelectionPickUp);
        h.Controller.DrainOutbound();

        Assert.Single(h.PendingPlacements);
        Assert.Empty(h.CancelledPlacements);

        h.Controller.OnNaturalMoveToComplete();
        Assert.Equal(new[] { (Target, Player, 0) }, h.Transport.Pickups);
    }

    [Fact]
    public void WithdrawnPickupReservationInvalidatesDeferredWireAction()
    {
        const uint replacement = 0x7000_0002u;
        var h = new Harness();
        h.SetApproach(closeRange: true);
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = replacement,
            Name = "Replacement loot",
            Type = ItemType.Misc,
        });

        Assert.True(h.Items.PlaceWorldItemInBackpack(Target));
        Assert.Empty(h.Transport.Pickups);
        PendingBackpackPlacement original = Assert.Single(h.PendingPlacements);
        h.Items.CancelPendingBackpackPlacement(Target, original.Token);
        Assert.True(h.Items.TryBeginPendingBackpackPlacement(
            replacement,
            Player,
            placement: 0,
            out _));

        h.Controller.OnNaturalMoveToComplete();

        Assert.Empty(h.Transport.Pickups);
        Assert.True(h.Items.TryGetPendingBackpackPlacement(replacement, out _));
        Assert.False(h.Items.TryGetPendingBackpackPlacement(Target, out _));
    }

    [Fact]
    public void GetSelectedOrClosestCombatTargetAcceptsExplicitAttackableEvenWhenNotHostileMonster()
    {
        var h = new Harness();
        h.Query.Attackable = true;
        h.Query.Hostile = false;
        h.Selection.Select(Target, SelectionChangeSource.Keyboard);

        Assert.Equal(
            Target,
            h.Controller.GetSelectedOrClosestCombatTarget(autoTarget: false));
    }

    [Fact]
    public void GetSelectedOrClosestCombatTargetFallsBackToAutoAcquisitionWhenExplicitTargetIsNotAttackable()
    {
        var h = new Harness();
        const uint monster = 0x7000_0099u;
        h.Query.Attackable = false;
        h.Query.Closest = new ClosestCombatTarget(monster, DistanceSquared: 4f);
        h.Selection.Select(Target, SelectionChangeSource.Keyboard);

        Assert.Equal(
            monster,
            h.Controller.GetSelectedOrClosestCombatTarget(autoTarget: true));
    }

    private static WorldSession.EntitySpawn Spawn(uint guid, ushort instance)
        => new(
            guid,
            Position: null,
            SetupTableId: null,
            AnimPartChanges: [],
            TextureChanges: [],
            SubPalettes: [],
            BasePaletteId: null,
            ObjScale: null,
            Name: null,
            ItemType: null,
            MotionState: null,
            MotionTableId: null,
            InstanceSequence: instance);
}
