using System;
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
        public bool IsStuckInWorld(uint serverGuid) => false;
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
        public bool RefuseSend { get; set; }
        public Exception? ThrowOnSend { get; set; }
        public List<uint> Uses { get; } = new();
        public List<(uint Item, uint Container, int Placement)> Pickups { get; } = new();

        public bool TrySendUse(uint serverGuid, out uint sequence)
        {
            if (ThrowOnSend is not null)
                throw ThrowOnSend;
            if (!IsInWorld || RefuseSend)
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

    private sealed class Movement(IRuntimeApproachTokenSource approachTokens)
        : IPlayerInteractionMovementSink
    {
        public List<InteractionApproach> Approaches { get; } = new();
        public bool Starts { get; set; } = true;
        public Action? AfterArm { get; set; }
        public uint? FailProgressCount { get; set; }
        public int CancelCount { get; private set; }

        public bool BeginApproach(
            InteractionApproach approach,
            Action<RuntimeInteractionApproachToken>? armAfterCancel = null)
        {
            Approaches.Add(approach);
            if (!Starts || !approachTokens.TryBeginApproach(out RuntimeInteractionApproachToken token))
                return false;
            armAfterCancel?.Invoke(token);
            AfterArm?.Invoke();
            return true;
        }

        public uint? CurrentApproachFailProgressCount() => FailProgressCount;

        public void CancelApproach() => CancelCount++;
    }

    /// <summary>
    /// Lets the shared walk-then-use route reach this suite's own world and
    /// its own body, so the route under test is the production one.
    /// </summary>
    private sealed class ApproachAdapter(Query query, Movement movement)
        : IRuntimeApproachSource
    {
        private InteractionApproach _planned;

        public bool TryPlanApproach(uint serverGuid, out RuntimeApproachPlan plan)
        {
            if (!query.TryGetApproach(serverGuid, out InteractionApproach approach))
            {
                plan = default;
                return false;
            }
            _planned = approach;
            plan = new RuntimeApproachPlan(
                approach.Target.ServerGuid,
                approach.Target.LocalEntityId,
                approach.Target.Entity.Position,
                approach.Player.CellId,
                approach.UseRadius,
                approach.IsCloseRange,
                approach.CanCharge,
                approach.TargetRadius,
                approach.TargetHeight);
            return true;
        }

        public bool BeginApproach(
            in RuntimeApproachPlan plan,
            Action<RuntimeInteractionApproachToken>? arm = null) =>
            movement.BeginApproach(_planned, arm);

        public uint? StalledTicks() =>
            movement.CurrentApproachFailProgressCount();

        public void CancelApproach() => movement.CancelApproach();
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
        public readonly RuntimeApproachCompletionState Completions = new();
        public readonly IRuntimeApproachCompletionSink CompletionLifetime;
        public readonly Movement Movement;
        public readonly SelectionState Selection = new();
        public readonly ClientObjectTable Objects = new();
        public readonly List<string> Toasts = new();
        public readonly List<uint> Examines = new();
        public readonly List<PendingBackpackPlacement> PendingPlacements = new();
        public readonly List<PendingBackpackPlacement> CancelledPlacements = new();
        public readonly RuntimeItemInteraction Items;
        public readonly CombatState Combat = new();
        public readonly CombatTargetOperations CombatTargetOperations;
        public readonly RuntimeCombatTargetState CombatTarget;
        public readonly SelectionInteractionController Controller;
        public readonly RuntimeWorldObjectUse WorldObjectUse;

        /// <summary>
        /// The shared per-frame step that ends an armed walk. The client that
        /// draws no longer takes it itself, so the harness takes it where the
        /// per-frame local-player step would.
        /// </summary>
        public readonly RuntimeInteractionApproachDriver ArmedApproaches;

        public uint GroundObjectId { get; set; }
        public uint? RequestedExternalContainerId { get; private set; }

        /// <summary>
        /// One frame's worth of interaction work in the order a client does
        /// it: the shared drive ends the walks that have ended, then this
        /// client sends what its own clicks have queued.
        /// </summary>
        public void DriveFrame()
        {
            ArmedApproaches.DriveArmedApproaches();
            Controller.DrainOutbound();
        }

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
            Items = new RuntimeItemInteraction(
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
                    controller!.RequestUse(guid, reservation),
                requestExternalContainer: guid => RequestedExternalContainerId = guid);
            CombatTargetOperations = new CombatTargetOperations(Selection);
            CombatTarget = new RuntimeCombatTargetState(
                Combat,
                Selection,
                CombatTargetOperations);
            WorldObjectUse = new RuntimeWorldObjectUse(
                Items,
                Transport,
                new ApproachAdapter(Query, Movement),
                guid => Query.IsUseable(guid) ? ItemUseability.Remote : null);
            ArmedApproaches = new RuntimeInteractionApproachDriver(
                Completions,
                Items.RuntimeTransactions,
                WorldObjectUse,
                new ApproachAdapter(Query, Movement));
            Controller = controller = new SelectionInteractionController(
                Selection,
                Query,
                Items,
                Transport,
                Movement,
                CombatTarget,
                WorldObjectUse,
                ArmedApproaches,
                Toasts.Add,
                Completions);
            _ = ArmedApproaches.BindPresentationOwned(controller);
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
        h.DriveFrame();

        Assert.Equal(1, h.Items.BusyCount);
        Assert.Equal(new[] { Target }, h.Transport.Uses);

        h.CompletionLifetime.PublishCancellation(WeenieError.ActionCancelled);
        h.DriveFrame();

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
        h.DriveFrame();

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
        h.DriveFrame();

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
        h.DriveFrame();

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
        h.DriveFrame();

        Assert.Equal(new[] { Target }, h.Transport.Uses);
        Assert.Equal(1, h.Items.BusyCount);

        h.Items.CompleteUse(0u);

        Assert.Equal(0, h.Items.BusyCount);
    }

    [Fact]
    public void AutomationUseOfAFarWorldObjectWalksThenDispatchesOnArrival()
    {
        var h = new Harness();
        h.SetApproach(closeRange: false);

        AutomationUseOutcome outcome = h.WorldObjectUse.TryUse(Target);

        Assert.Equal(AutomationUseOutcome.Started, outcome);
        PlayerInteractionMovementSinkAssertSingleApproach(h, Target);
        // Armed, not yet sent -- the plugin surface reports Started for the
        // walk, and the actual use dispatches once the player arrives.
        Assert.Empty(h.Transport.Uses);

        h.Controller.OnNaturalMoveToComplete();

        Assert.Equal(new[] { Target }, h.Transport.Uses);
    }


    [Fact]
    public void StalledApproachExpiresTheReservationOnceTheFailCounterCrossesTheThreshold()
    {
        // A live-verified root cause: an obstruction (a closed door, a
        // wall) in the straight-line approach path can stop the local
        // physics from ever calling MoveToComplete or MoveToCancelled at
        // all -- not just report a bad arrival. A live repro against a
        // real ACE vendor left the character frozen at a closed door for
        // 40+ seconds with zero HandleUseApproachCompletion calls.
        // Without a bound, the reservation and HasPendingUse would stay
        // held for the rest of the session; every later Use, from a
        // click or a plugin, would report Busy forever. The give-up
        // reads the move-to's own per-tick progress-failure counter
        // (see StalledApproachGiveUpTicks) rather than a wall clock.
        var h = new Harness();
        h.SetApproach(closeRange: false);

        AutomationUseOutcome outcome = h.WorldObjectUse.TryUse(Target);
        Assert.Equal(AutomationUseOutcome.Started, outcome);
        Assert.Equal(1, h.Items.BusyCount);
        Assert.True(h.Items.RuntimeTransactions.HasPendingUse);

        // Short of the threshold: the fail counter is climbing (the move
        // is stalled) but hasn't crossed the line yet, so nothing changes.
        h.Movement.FailProgressCount = SelectionInteractionController.StalledApproachGiveUpTicks - 1;
        h.DriveFrame();

        Assert.True(h.Items.RuntimeTransactions.HasPendingUse);
        Assert.Equal(1, h.Items.BusyCount);
        Assert.Equal(0, h.Movement.CancelCount);

        // At the threshold: the host gives up, frees the gate, and cancels
        // the underlying move-to so the player stops walking into it.
        h.Movement.FailProgressCount = SelectionInteractionController.StalledApproachGiveUpTicks;
        h.DriveFrame();

        Assert.False(h.Items.RuntimeTransactions.HasPendingUse);
        Assert.Equal(0, h.Items.BusyCount);
        Assert.True(h.Items.EnsureInventoryRequestReady());
        Assert.Empty(h.Transport.Uses);
        Assert.Equal(1, h.Movement.CancelCount);
    }

    [Fact]
    public void AutomationRouteDoesNotToastWhenAnApproachStalls()
    {
        // The automation route's toast: false contract must survive to
        // the expiry path too -- a plugin's Use should not pop a message
        // in the user's chat window the same way a click's would.
        var h = new Harness();
        h.SetApproach(closeRange: false);

        h.WorldObjectUse.TryUse(Target);
        h.Movement.FailProgressCount = SelectionInteractionController.StalledApproachGiveUpTicks;
        h.DriveFrame();

        Assert.Empty(h.Toasts);
    }

    [Fact]
    public void ClickRouteDoesToastWhenAnApproachStalls()
    {
        var h = new Harness();
        h.SetApproach(closeRange: false);

        h.Controller.SendUse(Target);
        h.Movement.FailProgressCount = SelectionInteractionController.StalledApproachGiveUpTicks;
        h.DriveFrame();

        Assert.Single(h.Toasts);
    }

    [Fact]
    public void ASlowButProgressingApproachIsNeverCutOff()
    {
        // The give-up must never punish a walk that is merely slow. The
        // move-to's fail counter resets to 0 the instant it makes
        // progress (MoveToManager.CheckProgressMade); simulate that by
        // never letting the counter reach the threshold, no matter how
        // many drain cycles pass.
        var h = new Harness();
        h.SetApproach(closeRange: false);

        h.WorldObjectUse.TryUse(Target);

        for (int i = 0; i < 500; i++)
        {
            // Climbs partway, then progress resets it, over and over --
            // it never accumulates to the threshold.
            h.Movement.FailProgressCount = SelectionInteractionController.StalledApproachGiveUpTicks - 1;
            h.DriveFrame();
            h.Movement.FailProgressCount = 0;
            h.DriveFrame();
        }

        Assert.True(h.Items.RuntimeTransactions.HasPendingUse);
        Assert.Equal(0, h.Movement.CancelCount);
    }

    [Fact]
    public void StalledPickupApproachExpiresTheSameWay()
    {
        // MEDIUM: walk-then-pickup has the identical unbounded wedge as
        // walk-then-use -- SendPickup arms TryArmPostArrivalPickup the
        // same way PerformUse arms TryArmPostArrivalUse, and nothing
        // released it if the move-to never completed or cancelled.
        var h = new Harness();
        h.SetApproach(closeRange: true);

        Assert.True(h.Items.PlaceWorldItemInBackpack(Target));
        Assert.True(h.Items.RuntimeTransactions.HasPendingPickup);

        h.Movement.FailProgressCount = SelectionInteractionController.StalledApproachGiveUpTicks;
        h.DriveFrame();

        Assert.False(h.Items.RuntimeTransactions.HasPendingPickup);
        Assert.Equal(1, h.Movement.CancelCount);
    }

    [Fact]
    public void AutomationUseOfACloseWorldObjectDispatchesImmediately()
    {
        var h = new Harness();
        h.SetApproach(closeRange: true);

        AutomationUseOutcome outcome = h.WorldObjectUse.TryUse(Target);

        Assert.Equal(AutomationUseOutcome.Started, outcome);
        Assert.Empty(h.Movement.Approaches);
        Assert.Equal(new[] { Target }, h.Transport.Uses);
    }

    [Fact]
    public void AutomationUseOfANonOwnedContainerArmsTheExternalContainerRequest()
    {
        // HIGH root-cause fix: a click on a landscape container arms
        // ExternalContainers.RequestOpen as one of the SAME policy
        // actions that decides to send Use (SetGroundObject, alongside
        // SendUse, both produced by ItemInteractionPolicy.DecideUse). The
        // automation route dispatches Use through a different seam
        // (PerformUse -> TryDispatchUse) that bypassed that policy
        // entirely, so RequestedContainerId stayed 0 and the server's
        // ViewContents response for a freshly opened corpse/chest was
        // silently dropped -- Started, and nothing opened. Live-verified
        // on a real ACE chest.
        var h = new Harness();
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Target,
            Name = "Chest",
            Type = ItemType.Container,
            Useability = ItemUseability.Remote,
            ItemsCapacity = 6,
        });
        h.SetApproach(closeRange: true);

        AutomationUseOutcome outcome = h.WorldObjectUse.TryUse(Target);

        Assert.Equal(AutomationUseOutcome.Started, outcome);
        Assert.Equal(Target, h.RequestedExternalContainerId);
    }

    [Fact]
    public void AutomationUseOfAnOwnedItemDoesNotArmTheExternalContainerRequest()
    {
        var h = new Harness();
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Target,
            Name = "MyBag",
            Type = ItemType.Container,
            ContainerId = Player,
            Useability = ItemUseability.Remote,
            ItemsCapacity = 6,
        });
        h.SetApproach(closeRange: true);

        h.WorldObjectUse.TryUse(Target);

        Assert.Null(h.RequestedExternalContainerId);
    }

    [Fact]
    public void AutomationUseOfATargetedContainerDoesNotArmTheExternalContainerRequest()
    {
        var h = new Harness();
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Target,
            Name = "LockedChest",
            Type = ItemType.Container,
            // A target-mode bit (Contained, shifted into the target half)
            // means this needs a key/tool used ON it, not a bare Use --
            // the same reason ItemInteractionPolicy never emits
            // SetGroundObject for it.
            Useability = ItemUseability.Contained << 16,
            ItemsCapacity = 6,
        });
        h.SetApproach(closeRange: true);

        h.WorldObjectUse.TryUse(Target);

        Assert.Null(h.RequestedExternalContainerId);
    }

    [Fact]
    public void ABusyRefusedAutomationUseDoesNotArmTheExternalContainerRequest()
    {
        // MEDIUM-1: arming must happen only where Use is actually
        // dispatched (immediately, or on arrival), never earlier where a
        // Busy refusal would still return without ever sending anything.
        // A plugin Use(B) refused Busy while A's own walk-then-use is
        // still in flight must not call ExternalContainers.RequestOpen
        // for B -- that would close whatever container the user already
        // has open (ExternalContainerState's ReplacementRequested
        // transition) and repoint RequestedContainerId at a container
        // whose Use never actually went anywhere.
        const uint otherContainer = 0x7000_0099u;
        var h = new Harness();
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = otherContainer,
            Name = "OtherChest",
            Type = ItemType.Container,
            Useability = ItemUseability.Remote,
            ItemsCapacity = 6,
        });
        h.SetApproach(closeRange: false);
        h.Controller.SendUse(Target);
        Assert.True(h.Items.RuntimeTransactions.HasPendingUse);

        h.SetApproach(closeRange: true, serverGuid: otherContainer);
        AutomationUseOutcome outcome = h.WorldObjectUse.TryUse(otherContainer);

        Assert.Equal(AutomationUseOutcome.Busy, outcome);
        Assert.Null(h.RequestedExternalContainerId);
        // The original approach is still the one armed.
        Assert.True(h.Items.RuntimeTransactions.HasPendingUse);
    }

    [Fact]
    public void AutomationUseOfANotUseableFarTargetIsRejectedWithoutApproaching()
    {
        var h = new Harness();
        h.Query.Useable = false;
        h.SetApproach(closeRange: false);

        AutomationUseOutcome outcome = h.WorldObjectUse.TryUse(Target);

        Assert.Equal(AutomationUseOutcome.NotUseable, outcome);
        Assert.Empty(h.Movement.Approaches);
        Assert.Empty(h.Transport.Uses);
    }

    [Fact]
    public void AutomationUseOfALooseGroundItemWithNoUsePicksItUp()
    {
        // A dropped book with no use of its own: a plain use of a loose item
        // on the ground is a pick-up into the pack, not a refusal.
        const uint tome = 0x8000_E5AEu;
        var h = new Harness();
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = tome,
            Name = "Damaged Tome",
            Type = ItemType.Writable,
            Useability = ItemUseability.No,
        });
        h.Query.Useable = false;
        h.SetApproach(closeRange: false, serverGuid: tome);

        AutomationUseOutcome outcome = h.WorldObjectUse.TryUse(tome);

        Assert.Equal(AutomationUseOutcome.Started, outcome);
        Assert.Empty(h.Transport.Uses);
        Assert.Equal(new[] { (tome, Player, 0) }, h.Transport.Pickups);
        Assert.Single(h.PendingPlacements);
        Assert.Equal(0, h.Items.BusyCount);
    }

    [Fact]
    public void AutomationUseOfAnItemInTheOpenContainerPicksItUp()
    {
        const uint loot = 0x7000_0011u;
        var h = new Harness
        {
            GroundObjectId = GroundContainer,
        };
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = loot,
            Name = "Insatiable Eater Jaw",
            Type = ItemType.Misc,
            ContainerId = GroundContainer,
        });
        h.Query.Useable = false;
        h.Query.Approach = null;

        AutomationUseOutcome outcome = h.WorldObjectUse.TryUse(loot);

        Assert.Equal(AutomationUseOutcome.Started, outcome);
        Assert.Empty(h.Movement.Approaches);
        Assert.Empty(h.Transport.Uses);
        Assert.Equal(new[] { (loot, Player, 0) }, h.Transport.Pickups);
    }

    [Fact]
    public void AutomationUseOfAUseableLooseGroundItemPicksItUpRatherThanUsingIt()
    {
        // The pick-up is decided before the item's own use is looked at, so a
        // potion lying on the ground is collected, not drunk where it lies.
        const uint potion = 0x8000_E5AFu;
        var h = new Harness();
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = potion,
            Name = "Health Potion",
            Type = ItemType.Food,
            Useability = ItemUseability.Contained,
        });
        h.SetApproach(closeRange: true, serverGuid: potion);

        AutomationUseOutcome outcome = h.WorldObjectUse.TryUse(potion);

        Assert.Equal(AutomationUseOutcome.Started, outcome);
        Assert.Empty(h.Transport.Uses);
        Assert.Single(h.PendingPlacements);
    }

    [Fact]
    public void AutomationUseOfAnItemInAContainerThatIsNotOpenIsStillRefused()
    {
        // Only the container the character has open lends its contents to a
        // pick-up; an item inside some other container is not loose.
        const uint loot = 0x7000_0012u;
        const uint closedChest = 0x7000_0013u;
        var h = new Harness
        {
            GroundObjectId = GroundContainer,
        };
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = loot,
            Name = "Loot",
            Type = ItemType.Misc,
            ContainerId = closedChest,
        });
        h.Query.Useable = false;
        h.SetApproach(closeRange: true, serverGuid: loot);

        AutomationUseOutcome outcome = h.WorldObjectUse.TryUse(loot);

        Assert.Equal(AutomationUseOutcome.NotUseable, outcome);
        Assert.Empty(h.Transport.Pickups);
        Assert.Empty(h.PendingPlacements);
    }

    [Fact]
    public void AutomationUseThrottleRefusesASecondCallWithinTheWindow()
    {
        var h = new Harness();
        h.SetApproach(closeRange: true);

        AutomationUseOutcome first = h.WorldObjectUse.TryUse(Target);
        Assert.Equal(AutomationUseOutcome.Started, first);
        Assert.Single(h.Transport.Uses);

        // A second automation call made immediately after (well inside
        // RuntimeInteractionTransactionState.RetailUseThrottleMs) must be
        // refused by the same throttle a click is held to, not bypass it.
        AutomationUseOutcome second = h.WorldObjectUse.TryUse(Target);

        Assert.Equal(AutomationUseOutcome.Busy, second);
        Assert.Single(h.Transport.Uses);
    }

    [Fact]
    public void AutomationUseIsBusyWhenAnInventoryRequestIsAlreadyInFlight()
    {
        var h = new Harness();
        h.SetApproach(closeRange: true);
        // Occupy the one-request-at-a-time inventory gate directly (not
        // through TryUseForAutomation), so this isolates the inventory
        // gate from the use-throttle gate above.
        ItemUseRequestReservation blocking =
            h.Items.RuntimeTransactions.BeginUseRequestReservation();

        AutomationUseOutcome outcome = h.WorldObjectUse.TryUse(Target);

        Assert.Equal(AutomationUseOutcome.Busy, outcome);
        Assert.Empty(h.Transport.Uses);

        blocking.CancelBeforeDispatch();
    }

    [Fact]
    public void AutomationUseReleasesTheReservationWhenTheSendThrows()
    {
        // The reservation increments the busy count the moment it is
        // taken; a throw anywhere downstream (a transport fault, a reset
        // mid-call) must give it back or the one-request-at-a-time gate is
        // wedged for the rest of the session.
        var h = new Harness();
        h.SetApproach(closeRange: true);
        h.Transport.ThrowOnSend = new InvalidOperationException("transport fault");

        Assert.Throws<InvalidOperationException>(() => h.WorldObjectUse.TryUse(Target));

        Assert.Equal(0, h.Items.BusyCount);
        Assert.True(h.Items.EnsureInventoryRequestReady());
    }

    [Fact]
    public void AutomationUseIncrementsBusyCountUntilTheServerConfirmsCompletion()
    {
        var h = new Harness();
        h.SetApproach(closeRange: true);

        AutomationUseOutcome outcome = h.WorldObjectUse.TryUse(Target);

        Assert.Equal(AutomationUseOutcome.Started, outcome);
        Assert.Equal(1, h.Items.BusyCount);

        h.Items.CompleteUse(0u);

        Assert.Equal(0, h.Items.BusyCount);
    }

    [Fact]
    public void AutomationUseReportsBusyInsteadOfCancellingAnInFlightApproach()
    {
        var h = new Harness();
        h.SetApproach(closeRange: false);
        h.Controller.SendUse(Target);
        PlayerInteractionMovementSinkAssertSingleApproach(h, Target);
        Assert.True(h.Items.RuntimeTransactions.HasPendingUse);

        AutomationUseOutcome outcome = h.WorldObjectUse.TryUse(Target);

        Assert.Equal(AutomationUseOutcome.Busy, outcome);
        // The original click-driven approach is still armed, not
        // cancelled by the automation call.
        Assert.True(h.Items.RuntimeTransactions.HasPendingUse);
        Assert.Single(h.Movement.Approaches);
    }

    [Fact]
    public void AutomationUseOfAnotherPlayerIsRefusedInsteadOfOpeningASecureTrade()
    {
        var h = new Harness();
        const uint otherPlayer = 0x5000_0099u;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = otherPlayer,
            Type = ItemType.Creature,
            PublicWeenieBitfield = (uint)PublicWeenieFlags.Player,
        });
        var tradesRequested = new List<uint>();
        h.Items.SecureTradeRequested += (guid, _) => tradesRequested.Add(guid);

        AutomationUseOutcome outcome = h.WorldObjectUse.TryUse(otherPlayer);

        Assert.Equal(AutomationUseOutcome.NotUseable, outcome);
        Assert.Empty(tradesRequested);
        Assert.Empty(h.Transport.Uses);
    }

    [Fact]
    public void AutomationUseMapsATransportSendRejectionToUnavailableNotBusy()
    {
        var h = new Harness();
        h.SetApproach(closeRange: true);
        // In-world, but the transport itself refuses to send -- distinct
        // from NotInWorld and from the busy gates above.
        h.Transport.RefuseSend = true;

        AutomationUseOutcome outcome = h.WorldObjectUse.TryUse(Target);

        Assert.Equal(AutomationUseOutcome.Unavailable, outcome);
        Assert.Empty(h.Transport.Uses);
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
        h.DriveFrame();

        // Letting the selection go is the runtime's now, for every client;
        // what this controller still owes is the cancel.
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

        h.DriveFrame();

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
        h.DriveFrame();
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
        h.DriveFrame();

        Assert.Equal(new[] { Target }, h.Transport.Uses);
        Assert.Empty(h.Transport.Pickups);

        h.CompletionLifetime.PublishNaturalCompletion();
        h.DriveFrame();

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
        h.DriveFrame();

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
        h.DriveFrame();

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
        h.DriveFrame();

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
        control.DriveFrame();
        Assert.NotEmpty(control.Transport.Uses);

        var h = new Harness();
        h.Query.Picked = Target;
        h.Query.WieldedByPlayer = true;

        h.Controller.HandleInputAction(InputAction.SelectDblLeft);
        h.DriveFrame();

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
        h.DriveFrame();

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
        h.DriveFrame();

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
        h.DriveFrame();

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
        h.DriveFrame();

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
        h.DriveFrame();

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
