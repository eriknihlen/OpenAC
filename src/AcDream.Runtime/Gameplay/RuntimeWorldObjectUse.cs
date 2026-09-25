using System.Numerics;
using AcDream.Core.Items;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Gameplay;

/// <summary>
/// What it takes to get within reach of a world object: which object, how
/// close the character has to be, whether it is already that close, and the
/// size of the thing it is reaching for.
/// </summary>
/// <param name="TargetGuid">The object being reached for.</param>
/// <param name="TargetLocalEntityId">That object's identity in the world, or zero when it has none.</param>
/// <param name="TargetPosition">Where that object is.</param>
/// <param name="PlayerCellId">The cell the character stands in.</param>
/// <param name="UseRadius">How close the character has to be, in meters.</param>
/// <param name="IsWithinReach">Whether the character is already that close.</param>
/// <param name="CanCharge">Whether the object is far enough off to run at rather than walk to.</param>
/// <param name="TargetRadius">How wide the object is, in meters.</param>
/// <param name="TargetHeight">How tall the object is, in meters.</param>
internal readonly record struct RuntimeApproachPlan(
    uint TargetGuid,
    uint TargetLocalEntityId,
    Vector3 TargetPosition,
    uint PlayerCellId,
    float UseRadius,
    bool IsWithinReach,
    bool CanCharge,
    float TargetRadius,
    float TargetHeight);

/// <summary>
/// Walking the character to within reach of a world object, and saying how
/// that walk is going. A client can only do this once it has a body; before
/// that everything here answers no.
/// </summary>
internal interface IRuntimeApproachSource
{
    /// <summary>Works out what getting within reach of an object would take.</summary>
    /// <param name="serverGuid">The object to reach.</param>
    /// <param name="plan">What that would take.</param>
    /// <returns>False when the object cannot be reached for at all.</returns>
    bool TryPlanApproach(uint serverGuid, out RuntimeApproachPlan plan);

    /// <summary>Sends the character walking, and names the walk.</summary>
    /// <param name="plan">What <see cref="TryPlanApproach"/> worked out.</param>
    /// <param name="arm">
    /// Run once the walk has a name and the body's previous walk has been
    /// called off, to arm whatever should happen on arrival.
    /// </param>
    /// <returns>False when the character has no body to walk, or refused to.</returns>
    bool BeginApproach(
        in RuntimeApproachPlan plan,
        Action<RuntimeInteractionApproachToken>? arm = null);

    /// <summary>
    /// How many ticks in a row the walk in progress has failed to get any
    /// closer, or null when no walk is in progress to ask about. It goes back
    /// to zero the instant the walk advances again, so a slow walk never reads
    /// as a stuck one.
    /// </summary>
    /// <returns>The count of stalled ticks, or null.</returns>
    uint? StalledTicks();

    /// <summary>Calls off the walk in progress.</summary>
    void CancelApproach();
}

/// <summary>
/// The one walk-then-use route: work out whether an object can be used and how
/// far away it is, walk to it when it is out of reach, and send the use once
/// the character is there. Every client goes through this, so a plugin asking
/// to open a corpse several meters off gets the same walk, the same use and
/// the same refusals whether or not there is a window.
/// </summary>
/// <remarks>
/// This is the automation route. A person clicking an object takes the same
/// steps but is allowed to interrupt whatever they armed a moment ago, and is
/// told in words when it does not work out; an automation call can arrive at
/// any cadence, so it is held to every gate instead and reports a code.
/// </remarks>
internal sealed class RuntimeWorldObjectUse
{
    /// <summary>
    /// How many ticks in a row an armed walk may fail to get closer before the
    /// route gives up on it. Each tick is at least a thirtieth of a second, so
    /// this is a floor of about five seconds of genuine standstill -- an
    /// obstruction such as a closed door across the straight-line path -- on
    /// top of the grace the walk itself gives before it counts a stall at all.
    /// Without a give-up, the reservation stayed held and every later use in
    /// the session reported busy for the rest of it.
    /// </summary>
    public const uint StalledApproachGiveUpTicks = 150;

    private readonly RuntimeItemInteraction _items;
    private readonly RuntimeInteractionTransactionState _transactions;
    private readonly IRuntimeInteractionTransport _transport;
    private readonly IRuntimeApproachSource _approach;
    private readonly Func<uint, uint?> _worldUseability;
    private readonly Action<string>? _log;

    // Whether the walk currently armed for a use was a person's own doing,
    // and so should be spoken about when it is given up on. An automation
    // call is answered with a code and says nothing to the character.
    private bool _armedUseSpokenFor;

    internal RuntimeWorldObjectUse(
        RuntimeItemInteraction items,
        IRuntimeInteractionTransport transport,
        IRuntimeApproachSource approach,
        Func<uint, uint?> worldUseability,
        Action<string>? log = null)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _transactions = _items.RuntimeTransactions;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _approach = approach ?? throw new ArgumentNullException(nameof(approach));
        _worldUseability = worldUseability
            ?? throw new ArgumentNullException(nameof(worldUseability));
        _log = log;
    }

    /// <summary>
    /// Uses a world object the character does not own -- a corpse, a chest, a
    /// vendor, a townsfolk, a door -- walking to it first when it is out of
    /// reach.
    /// </summary>
    /// <param name="serverGuid">The object to use.</param>
    /// <returns>
    /// Whether the use went out, a walk was begun that will send it on
    /// arrival, or why neither happened.
    /// </returns>
    public AutomationUseOutcome TryUse(uint serverGuid)
    {
        // Every exit is logged: this is the only automation entry point for
        // using an object the character does not own, it can fire at any
        // cadence, and a walk that is begun and silently never completes would
        // otherwise leave no trace at all.
        AutomationUseOutcome outcome = TryUseCore(serverGuid);
        _log?.Invoke(
            $"[interaction] automation use guid=0x{serverGuid:X8} outcome={outcome}");
        return outcome;
    }

    private AutomationUseOutcome TryUseCore(uint serverGuid)
    {
        if (serverGuid == 0u)
            return AutomationUseOutcome.NotUseable;
        // Using another character would otherwise silently open a secure
        // trade; a character-to-character exchange goes through the trade
        // route, not this one.
        if (_items.IsPlayerTarget(serverGuid))
            return AutomationUseOutcome.NotUseable;
        if (!_items.TryConsumeUseThrottleForAutomation())
            return AutomationUseOutcome.Busy;
        if (!_items.EnsureInventoryRequestReady())
            return AutomationUseOutcome.Busy;

        // A plain use of a loose item lying on the ground, or of an item
        // inside the container the character has open, means picking it up
        // into the pack. That is decided before a use request is considered
        // at all, so an item with no use of its own -- a dropped book, a
        // corpse's loot -- is collected rather than refused as unusable.
        if (_items.ClassifyPrimaryUse(serverGuid)
            == ItemPrimaryUseResult.PlaceInBackpack)
        {
            return PickUp(serverGuid);
        }

        ItemUseRequestReservation reservation =
            _items.BeginAutomationUseReservation();
        // The reservation holds the busy count from here on; a throw
        // downstream has to give it back or the one-request-at-a-time gate
        // stays wedged for the session.
        try
        {
            return PerformUse(serverGuid, reservation);
        }
        catch
        {
            reservation.CancelBeforeDispatch();
            throw;
        }
    }

    private AutomationUseOutcome PickUp(uint serverGuid)
    {
        // The same rule as a use: never cut in front of a walk already armed.
        if (_transactions.HasPendingUse || _transactions.HasPendingPickup)
            return AutomationUseOutcome.Busy;

        return _items.TryPlaceWorldItemInBackpack(serverGuid) switch
        {
            RuntimeBackpackPlacementOutcome.Sent => AutomationUseOutcome.Started,
            RuntimeBackpackPlacementOutcome.AlreadyPending => AutomationUseOutcome.Busy,
            RuntimeBackpackPlacementOutcome.NoRoom => AutomationUseOutcome.NoRoom,
            _ => AutomationUseOutcome.Unavailable,
        };
    }

    private AutomationUseOutcome PerformUse(
        uint serverGuid,
        ItemUseRequestReservation reservation)
    {
        if (_transactions.HasPendingUse || _transactions.HasPendingPickup)
        {
            // This route never cuts in front of a walk already armed -- a
            // person's own pending click, or an earlier automation call -- it
            // reports busy and leaves it alone.
            reservation.CancelBeforeDispatch();
            return AutomationUseOutcome.Busy;
        }

        if (_items.TryOpenSecureTradeWithPlayer(serverGuid))
        {
            reservation.CancelBeforeDispatch();
            return AutomationUseOutcome.Started;
        }

        bool ownedByPlayer = _items.IsOwnedByPlayer(serverGuid);
        bool useable = ownedByPlayer || IsUseable(serverGuid);

        if (useable
            && _approach.TryPlanApproach(serverGuid, out RuntimeApproachPlan plan)
            && !plan.IsWithinReach)
        {
            bool armed = false;
            bool started = _approach.BeginApproach(
                plan,
                token =>
                {
                    armed = _transactions.TryArmPostArrivalUse(
                        serverGuid,
                        ownedByPlayer,
                        useable,
                        reservation,
                        token,
                        out _);
                    if (armed)
                        _armedUseSpokenFor = false;
                });
            if (!started || !armed)
            {
                if (_transactions.TryCancelPendingUse(
                        serverGuid,
                        out RuntimePendingUse cancelled))
                {
                    cancelled.Reservation?.CancelBeforeDispatch();
                }
                else
                {
                    reservation.CancelBeforeDispatch();
                }
                return AutomationUseOutcome.Busy;
            }
            return AutomationUseOutcome.Started;
        }

        RuntimeInteractionDispatchResult result =
            _transactions.TryDispatchUse(
                serverGuid,
                ownedByPlayer,
                useable,
                reservation,
                _transport,
                out _);
        if (result == RuntimeInteractionDispatchResult.NotInWorld)
            return AutomationUseOutcome.NotInWorld;
        if (result == RuntimeInteractionDispatchResult.Dispatched)
        {
            // Arm the container or vendor open only now that the use has
            // actually gone out. Arming on a call that never dispatched would
            // close whatever container is already open and point the client at
            // one whose use went nowhere, dropping the real answer in flight.
            _items.ArmLandscapeContainerRequest(serverGuid);
            return AutomationUseOutcome.Started;
        }
        if (result == RuntimeInteractionDispatchResult.NotUseable)
            return AutomationUseOutcome.NotUseable;
        // The transport itself refused the send -- distinct from the busy
        // gates above, so a caller does not read it as "try again shortly".
        return AutomationUseOutcome.Unavailable;
    }

    /// <summary>
    /// Whether an object out in the world is one that can be used at all.
    /// </summary>
    /// <param name="serverGuid">The object to judge.</param>
    /// <returns>False when nothing known about it says it can be used.</returns>
    /// <summary>
    /// Records that the walk just armed was a person's own doing, so giving
    /// up on it is said in words rather than only reported as a code.
    /// </summary>
    internal void NoteArmedUseIsSpokenFor() => _armedUseSpokenFor = true;

    public bool IsUseable(uint serverGuid) =>
        _worldUseability(serverGuid) is { } useability
        && ItemUseability.IsUseable(useability);

    /// <summary>
    /// Sends the use that was armed for arrival, now that the walk has ended.
    /// </summary>
    /// <param name="pending">What was armed.</param>
    /// <param name="accepted">Whether the walk arrived rather than being called off.</param>
    /// <returns>What came of the use, or nothing when the walk never arrived.</returns>
    public RuntimeInteractionDispatchResult? CompleteArmedUse(
        RuntimePendingUse pending,
        bool accepted)
    {
        if (!accepted)
        {
            pending.Reservation?.CancelBeforeDispatch();
            return null;
        }

        // Range is deliberately NOT re-checked here. A centre-to-centre
        // distance against the use radius makes no allowance for how wide the
        // bodies are, while the walk's own arrival rule does, so re-deriving
        // it refused targets the character had genuinely just reached -- seen
        // live on a corpse still within melee range right after the kill. An
        // arrival means the walk's own rule was satisfied; trust it.
        RuntimeInteractionDispatchResult result =
            _transactions.TryDispatchUse(
                pending.ServerGuid,
                pending.OwnedByPlayer,
                pending.Useable,
                pending.Reservation,
                _transport,
                out _);
        if (result == RuntimeInteractionDispatchResult.Dispatched)
        {
            // Same rule as the immediate branch: arm only once the use has
            // actually gone out, so a walk that arrives and then fails to
            // dispatch does not touch the container already open.
            _items.ArmLandscapeContainerRequest(pending.ServerGuid);
            _log?.Invoke(
                $"[interaction] use guid=0x{pending.ServerGuid:X8} (arrival-gated)");
        }
        return result;
    }

    /// <summary>
    /// Gives up on an armed use whose walk has stopped getting anywhere for
    /// <see cref="StalledApproachGiveUpTicks"/> ticks in a row, so the
    /// character stops walking into whatever is in the way and the next use is
    /// not refused as busy forever.
    /// </summary>
    /// <param name="stalledTicks">How long the walk has been stalled.</param>
    /// <param name="serverGuid">The object the abandoned use was for.</param>
    /// <param name="spokenFor">Whether the person who asked should be told in words.</param>
    /// <returns>False when nothing was given up on.</returns>
    public bool TryGiveUpOnStalledUse(
        uint stalledTicks,
        out uint serverGuid,
        out bool spokenFor)
    {
        serverGuid = 0u;
        spokenFor = false;
        if (stalledTicks < StalledApproachGiveUpTicks)
            return false;
        if (!_transactions.TryCancelPendingUse(out RuntimePendingUse pending))
            return false;
        spokenFor = _armedUseSpokenFor;

        _approach.CancelApproach();
        pending.Reservation?.CancelBeforeDispatch();
        serverGuid = pending.ServerGuid;
        _log?.Invoke(
            $"[interaction] use guid=0x{serverGuid:X8} approach stalled for "
            + $"{stalledTicks} tick(s) -- refused");
        return true;
    }
}

/// <summary>
/// Walks the character's own body to within reach of a world object, reading
/// where everything is from the runtime's own record of the world. This is the
/// same body and the same walk on every client.
/// </summary>
internal sealed class RuntimeApproachSource : IRuntimeApproachSource
{
    /// <summary>
    /// How close the character has to be to an object that never said, in
    /// meters.
    /// </summary>
    private const float DefaultUseRadius = 0.6f;

    /// <summary>
    /// Beyond this many meters the character is allowed to run at what it is
    /// going to rather than walk.
    /// </summary>
    private const float ChargeDistance = 7.5f;

    private readonly GameRuntime _runtime;
    private readonly IRuntimeApproachTokenSource _tokens;

    internal RuntimeApproachSource(
        GameRuntime runtime,
        IRuntimeApproachTokenSource tokens)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    }

    public bool TryPlanApproach(uint serverGuid, out RuntimeApproachPlan plan)
    {
        plan = default;
        if (serverGuid == 0u
            || _runtime.MovementOwner.Controller is not { } controller)
        {
            return false;
        }

        RuntimeEntityDirectory entities = _runtime.EntityObjects.Entities;
        // Something being carried has no place of its own to walk to.
        if (entities.ParentAttachments.TryGetProjection(serverGuid, out _))
            return false;
        if (!entities.TryGetActive(serverGuid, out RuntimeEntityRecord target)
            || !RuntimePhysicsState.TryGetAbsoluteWorldPosition(
                target,
                out Vector3 targetPosition))
        {
            return false;
        }

        uint playerGuid = _runtime.PlayerIdentity.ServerGuid;
        if (playerGuid == 0u
            || !entities.TryGetActive(playerGuid, out RuntimeEntityRecord player)
            || !RuntimePhysicsState.TryGetAbsoluteWorldPosition(
                player,
                out Vector3 playerPosition))
        {
            return false;
        }

        float useRadius = target.Snapshot.UseRadius is > 0f
            ? target.Snapshot.UseRadius!.Value
            : DefaultUseRadius;
        float dx = targetPosition.X - playerPosition.X;
        float dy = targetPosition.Y - playerPosition.Y;
        float distanceSquared = (dx * dx) + (dy * dy);
        // How wide and tall the thing is, so the walk stops short of it
        // rather than inside it. The shape the installed data files describe
        // is the better answer; a client that has not opened them falls back
        // to the width the body it is simulating reports, and no height.
        (float Radius, float Height) shape =
            _runtime.EntityObjects.Physics.EntityBodyShape(serverGuid)
            ?? (_runtime.EntityObjects.Physics
                    .ResolveObjectTableHost(serverGuid)?.Radius ?? 0f,
                0f);
        plan = new RuntimeApproachPlan(
            serverGuid,
            target.LocalEntityId ?? 0u,
            targetPosition,
            controller.CurrentCellPosition.ObjCellId,
            useRadius,
            distanceSquared <= useRadius * useRadius,
            distanceSquared >= ChargeDistance * ChargeDistance,
            shape.Radius,
            shape.Height);
        return true;
    }

    public bool BeginApproach(
        in RuntimeApproachPlan plan,
        Action<RuntimeInteractionApproachToken>? arm = null)
    {
        if (_runtime.MovementOwner.Controller is not { MoveTo: not null } controller)
            return false;

        var movement = new MovementStruct
        {
            ObjectId = plan.TargetGuid,
            TopLevelId = plan.TargetGuid,
            Pos = new Position(
                plan.PlayerCellId,
                plan.TargetPosition,
                Quaternion.Identity),
            Params = new MovementParameters
            {
                DistanceToObject = plan.UseRadius,
                CanCharge = plan.CanCharge,
            },
            Type = plan.IsWithinReach
                ? MovementType.TurnToObject
                : MovementType.MoveToObject,
            Radius = plan.TargetRadius,
            Height = plan.TargetHeight,
        };

        controller.Movement.CancelMoveTo(WeenieError.ActionCancelled);
        if (!_tokens.TryBeginApproach(out RuntimeInteractionApproachToken token))
            return false;
        arm?.Invoke(token);

        controller.SetLastMoveWasAutonomous(false);
        return controller.Movement.PerformMovement(movement) == WeenieError.None;
    }

    public uint? StalledTicks()
    {
        MoveToManager? moveTo = _runtime.MovementOwner.Controller?.MoveTo;
        return moveTo is { } manager && manager.IsMovingTo()
            ? manager.FailProgressCount
            : null;
    }

    public void CancelApproach() =>
        _runtime.MovementOwner.Controller?.Movement.CancelMoveTo(
            WeenieError.ActionCancelled);
}
