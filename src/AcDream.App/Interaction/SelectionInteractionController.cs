using AcDream.App.UI;
using AcDream.App.World;
using AcDream.Core.Items;
using AcDream.Core.Physics;
using AcDream.Core.Selection;
using AcDream.Core.Ui;
using AcDream.Runtime.Gameplay;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.Interaction;

internal sealed class SelectionInteractionController
{
    private readonly SelectionState _selection;
    private readonly IWorldSelectionQuery _query;
    private readonly ItemInteractionController _items;
    private readonly RuntimeInteractionTransactionState _transactions;
    private readonly IRuntimeInteractionTransport _transport;
    private readonly IPlayerInteractionMovementSink _movement;
    private readonly PlayerApproachCompletionState _approachCompletions;
    private readonly Action<string>? _toast;
    private readonly Func<uint, bool>? _splitStack;
    private readonly Func<IEnumerable<uint>> _fellowshipMembers;

    public SelectionInteractionController(
        SelectionState selection,
        IWorldSelectionQuery query,
        ItemInteractionController items,
        IRuntimeInteractionTransport transport,
        IPlayerInteractionMovementSink movement,
        Action<string>? toast = null,
        PlayerApproachCompletionState? approachCompletions = null,
        Func<uint, bool>? splitStack = null,
        Func<IEnumerable<uint>>? fellowshipMembers = null)
    {
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _transactions = _items.RuntimeTransactions;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _movement = movement ?? throw new ArgumentNullException(nameof(movement));
        _toast = toast;
        _approachCompletions = approachCompletions
            ?? new PlayerApproachCompletionState();
        _splitStack = splitStack;
        _fellowshipMembers = fellowshipMembers ?? (() => Array.Empty<uint>());
    }

    public bool HandleInputAction(InputAction action)
    {
        switch (action)
        {
            case InputAction.SelectionSelf:
                SelectSelf();
                return true;
            case InputAction.SelectionPlaceInInventory:
                PlaceSelectionInBackpack(mainPack: false);
                return true;
            case InputAction.SelectionPlaceInMainPack:
                PlaceSelectionInBackpack(mainPack: true);
                return true;
            case InputAction.SelectionSplitStack:
                if (_selection.SelectedObjectId is { } stack)
                    _splitStack?.Invoke(stack);
                return true;
            case InputAction.SelectionClosestCompassItem:
                SelectRetailTarget(RetailSelectionKind.CompassItem, RetailSelectionDirection.Closest);
                return true;
            case InputAction.SelectionPreviousCompassItem:
                SelectRetailTarget(RetailSelectionKind.CompassItem, RetailSelectionDirection.Previous);
                return true;
            case InputAction.SelectionNextCompassItem:
                SelectRetailTarget(RetailSelectionKind.CompassItem, RetailSelectionDirection.Next);
                return true;
            case InputAction.SelectionClosestItem:
                SelectRetailTarget(
                    RetailSelectionKind.Item,
                    RetailSelectionDirection.Closest,
                    excludeOwnedByPlayer: true);
                return true;
            case InputAction.SelectionPreviousItem:
                SelectRetailTarget(RetailSelectionKind.Item, RetailSelectionDirection.Previous);
                return true;
            case InputAction.SelectionNextItem:
                SelectRetailTarget(RetailSelectionKind.Item, RetailSelectionDirection.Next);
                return true;
            case InputAction.SelectionClosestMonster:
                SelectRetailTarget(
                    RetailSelectionKind.Monster,
                    RetailSelectionDirection.Closest,
                    showToast: true);
                return true;
            case InputAction.SelectionPreviousMonster:
                SelectRetailTarget(RetailSelectionKind.Monster, RetailSelectionDirection.Previous);
                return true;
            case InputAction.SelectionNextMonster:
                SelectRetailTarget(RetailSelectionKind.Monster, RetailSelectionDirection.Next);
                return true;
            case InputAction.SelectionLastAttacker:
                if (_query.FindLastAttacker() is { } attacker)
                    _selection.Select(attacker, SelectionChangeSource.Keyboard);
                return true;
            case InputAction.SelectionClosestPlayer:
                SelectRetailTarget(RetailSelectionKind.Player, RetailSelectionDirection.Closest);
                return true;
            case InputAction.SelectionPreviousPlayer:
                SelectRetailTarget(RetailSelectionKind.Player, RetailSelectionDirection.Previous);
                return true;
            case InputAction.SelectionNextPlayer:
                SelectRetailTarget(RetailSelectionKind.Player, RetailSelectionDirection.Next);
                return true;
            case InputAction.SelectionPreviousFellow:
                SelectFellow(previous: true);
                return true;
            case InputAction.SelectionNextFellow:
                SelectFellow(previous: false);
                return true;
            case InputAction.SelectionClosestUnopenedCorpse:
                SelectRetailTarget(RetailSelectionKind.UnopenedCorpse, RetailSelectionDirection.Closest);
                return true;
            case InputAction.SelectionNextUnopenedCorpse:
                SelectRetailTarget(RetailSelectionKind.UnopenedCorpse, RetailSelectionDirection.Next);
                return true;
            case InputAction.SelectionUseClosestUnopenedCorpse:
                SelectAndUseCorpse(RetailSelectionDirection.Closest);
                return true;
            case InputAction.SelectionUseNextUnopenedCorpse:
                SelectAndUseCorpse(RetailSelectionDirection.Next);
                return true;
            case InputAction.SelectionGiveToTarget:
                GiveSelectionToPreviousTarget();
                return true;
            case InputAction.SelectionDrop:
                DropSelection();
                return true;
            case InputAction.SelectionPreviousSelection:
                _selection.SelectPrevious();
                return true;
            case InputAction.SelectLeft:
                PickAndStoreSelection(useImmediately: false);
                return true;
            case InputAction.SelectRight:
                PickSelectAndExamine();
                return true;
            case InputAction.SelectDblLeft:
                PickAndStoreSelection(useImmediately: true);
                return true;
            case InputAction.SelectionExamine:
                _items.ExamineSelectedOrEnterMode(
                    _selection.SelectedObjectId ?? 0u);
                return true;
            case InputAction.UseSelected:
                UseCurrentSelection();
                return true;
            case InputAction.SelectionPickUp:
                if (_selection.SelectedObjectId is uint pickupTarget)
                {
                    EnqueueIdentityBound(
                        RuntimeQueuedInteractionKind.Pickup,
                        pickupTarget,
                        requireLiveEntity: true);
                }
                else
                {
                    _toast?.Invoke("Nothing selected");
                }
                return true;
            case InputAction.EscapeKey when _items.IsAnyTargetModeActive:
                _items.CancelTargetMode();
                return true;
            case InputAction.EscapeKey when _selection.SelectedObjectId is not null:
                _selection.Clear(SelectionChangeSource.Keyboard);
                return true;
            default:
                return false;
        }
    }

    private void SelectSelf()
    {
        uint playerGuid = _query.PlayerGuid;
        if (playerGuid == 0u)
            return;
        if (_items.OfferPrimaryClick(playerGuid) is not ItemPrimaryClickResult.NotActive)
            return;
        _selection.Select(playerGuid, SelectionChangeSource.Keyboard);
    }

    private void PlaceSelectionInBackpack(bool mainPack)
    {
        if (_selection.SelectedObjectId is { } selected)
            _items.PlaceWorldItemInBackpack(selected, mainPack);
    }

    private void SelectRetailTarget(
        RetailSelectionKind kind,
        RetailSelectionDirection direction,
        bool excludeOwnedByPlayer = false,
        bool showToast = false)
    {
        uint? anchor = _selection.SelectedObjectId ?? _selection.PreviousObjectId;
        uint? target = _query.FindSelectionTarget(
            kind,
            direction,
            anchor,
            excludeOwnedByPlayer);
        if (target is { } guid)
        {
            _selection.Select(guid, SelectionChangeSource.Keyboard);
            if (showToast)
                _toast?.Invoke(_query.Describe(guid));
        }
    }

    private void SelectAndUseCorpse(RetailSelectionDirection direction)
    {
        SelectRetailTarget(RetailSelectionKind.UnopenedCorpse, direction);
        if (_selection.SelectedObjectId is { } corpse)
            EnqueueIdentityBound(
                RuntimeQueuedInteractionKind.Use,
                corpse,
                requireLiveEntity: false);
    }

    private void SelectFellow(bool previous)
    {
        uint[] fellows = _fellowshipMembers()
            .Where(static guid => guid != 0u)
            .Distinct()
            .ToArray();
        if (fellows.Length == 0)
            return;

        int current = _selection.SelectedObjectId is { } selected
            ? Array.IndexOf(fellows, selected)
            : -1;
        int next = previous
            ? (current > 0 ? current - 1 : fellows.Length - 1)
            : (current >= 0 && current + 1 < fellows.Length ? current + 1 : 0);
        _selection.Select(fellows[next], SelectionChangeSource.Keyboard);
    }

    private void GiveSelectionToPreviousTarget()
    {
        if (_selection.SelectedObjectId is not { } selected
            || _selection.PreviousObjectId is not { } target
            || selected == target
            || !_query.IsCreature(target))
        {
            _toast?.Invoke(
                "You must select a creature or a character to give that to.\n");
            return;
        }

        if (_items.PlaceSelectedIn3D(selected, target))
            _selection.Select(target, SelectionChangeSource.Keyboard);
    }

    private void DropSelection()
    {
        if (_selection.SelectedObjectId is not { } selected)
            return;
        if (!_items.IsOwnedByPlayer(selected))
        {
            _toast?.Invoke("You must pick that up first");
            return;
        }
        _items.PlaceSelectedIn3D(selected, targetGuid: 0u);
    }

    public uint? PickAtCursor(bool includeSelf)
        => _query.PickAtCursor(includeSelf);

    public void PlaceDraggedItem(ItemDragPayload payload, float mouseX, float mouseY)
    {
        ArgumentNullException.ThrowIfNull(payload);
        uint target = _query.PickAt(mouseX, mouseY, includeSelf: true) ?? 0u;
        if (target != 0u)
            _query.BeginLightingPulse(target);
        _items.PlaceIn3D(payload, target);
    }

    public uint? GetSelectedOrClosestCombatTarget(bool autoTarget)
    {
        if (_selection.SelectedObjectId is { } selected
            && _query.IsAttackableTarget(selected))
        {
            return selected;
        }
        return autoTarget ? SelectClosestCombatTarget(showToast: false) : null;
    }

    public uint? SelectClosestCombatTarget(bool showToast)
    {
        ClosestCombatTarget? closest = _query.FindClosestHostileMonster();
        uint? bestGuid = closest?.ServerGuid;
        if (bestGuid is { } selected)
            _selection.Select(selected, SelectionChangeSource.Keyboard);
        else
            _selection.Clear(SelectionChangeSource.Keyboard);

        if (bestGuid is { } guid)
        {
            string label = _query.Describe(guid);
            float distance = MathF.Sqrt(closest!.Value.DistanceSquared);
            Console.WriteLine($"combat: selected target 0x{guid:X8} {label} dist={distance:F1}");
            if (showToast)
                _toast?.Invoke($"Target {label}");
        }
        else if (showToast)
        {
            _toast?.Invoke("No monster target");
            Console.WriteLine("combat: no creature target found");
        }
        return bestGuid;
    }

    public void PickAndStoreSelection(bool useImmediately)
    {
        uint? picked = _query.PickAtCursor(includeSelf: true);
        if (picked is not uint guid)
        {
            if (!_items.IsAnyTargetModeActive)
                _toast?.Invoke("Nothing to select");
            return;
        }

        _query.BeginLightingPulse(guid);
        if (_items.OfferPrimaryClick(guid) is not ItemPrimaryClickResult.NotActive)
            return;

        _selection.Select(guid, SelectionChangeSource.World);
        string label = _query.Describe(guid);
        Console.WriteLine($"[B.4b] pick guid=0x{guid:X8} name={label}");
        _toast?.Invoke($"Selected: {label}");
        if (useImmediately && !_query.IsWieldedByPlayer(guid))
        {
            EnqueueIdentityBound(
                RuntimeQueuedInteractionKind.Activate,
                guid,
                requireLiveEntity: true);
        }
    }

    public void PickSelectAndExamine()
    {
        uint? picked = _query.PickAtCursor(includeSelf: true);
        if (picked is not uint guid)
            return;

        _query.BeginLightingPulse(guid);
        _selection.Select(guid, SelectionChangeSource.World);
        _items.ExamineSelectedOrEnterMode(guid);
    }

    public void UseCurrentSelection()
    {
        if (_selection.SelectedObjectId is not uint selected)
        {
            _toast?.Invoke("Nothing selected");
            return;
        }
        EnqueueIdentityBound(
            RuntimeQueuedInteractionKind.Use,
            selected,
            requireLiveEntity: false);
    }

    public void SendUse(uint serverGuid)
        => RequestUse(serverGuid, reservation: null);

    public void RequestUse(
        uint serverGuid,
        ItemUseRequestReservation? reservation)
    {
        CancelPendingApproach();

        if (_items.TryOpenSecureTradeWithPlayer(serverGuid))
        {
            reservation?.CancelBeforeDispatch();
            return;
        }

        bool ownedByPlayer = _items.IsOwnedByPlayer(serverGuid);
        bool useable = ownedByPlayer || _query.IsUseable(serverGuid);

        if (useable
            && _query.TryGetApproach(serverGuid, out InteractionApproach approach)
            && !approach.IsCloseRange)
        {
            bool armed = false;
            bool started = _movement.BeginApproach(
                approach,
                token =>
                {
                    armed = _transactions.TryArmPostArrivalUse(
                        serverGuid,
                        ownedByPlayer,
                        useable,
                        reservation,
                        new RuntimeInteractionApproachToken(
                            token.ControllerLifetime,
                            token.ApproachGeneration),
                        out _);
                });
            if (!started || !armed)
            {
                if (_transactions.TryCancelPendingUse(
                        serverGuid, out RuntimePendingUse cancelled))
                {
                    cancelled.Reservation?.CancelBeforeDispatch();
                }
                else
                {
                    reservation?.CancelBeforeDispatch();
                }
            }
            return;
        }

        RuntimeInteractionDispatchResult result =
            _transactions.TryDispatchUse(
                serverGuid,
                ownedByPlayer,
                useable,
                reservation,
                _transport,
                out uint sequence);
        if (result == RuntimeInteractionDispatchResult.NotInWorld)
            _toast?.Invoke("Not in world");
        if (result == RuntimeInteractionDispatchResult.Dispatched)
            Console.WriteLine($"[B.4b] use guid=0x{serverGuid:X8} seq={sequence}");
    }

    public void SendPickup(uint itemGuid, uint destinationContainerId, int placement)
    {
        CancelPendingApproach();
        if (!_transport.IsInWorld)
        {
            _toast?.Invoke("Not in world");
            CancelPickupPresentation(itemGuid);
            return;
        }

        ulong pendingPlacementToken = _items.TryGetPendingBackpackPlacement(
            itemGuid,
            out PendingBackpackPlacement pendingPlacement)
                ? pendingPlacement.Token
                : 0u;
        if (!IsCurrentPickupPresentation(
                itemGuid,
                destinationContainerId,
                placement,
                pendingPlacementToken))
        {
            return;
        }

        if (_items.IsInCurrentGroundObject(itemGuid)
            || (_query.IsWieldedPositionState(itemGuid)
                && _items.IsOwnedByPlayer(itemGuid)))
        {
            var contained = new RuntimePendingPickup(
                Token: 0u,
                itemGuid,
                LocalEntityId: 0u,
                destinationContainerId,
                placement,
                pendingPlacementToken,
                ApproachToken: default);
            if (_transactions.TryDispatchPickup(
                    contained,
                    _transport,
                    out uint containedSequence))
            {
                Console.WriteLine(
                    $"[B.5] contained pickup item=0x{itemGuid:X8} container=0x{destinationContainerId:X8} placement={placement} seq={containedSequence}");
            }
            else
            {
                CancelPickupPresentation(itemGuid, pendingPlacementToken);
            }
            return;
        }

        if (!ValidatePickupTarget(itemGuid, showToast: true)
            || !_query.TryGetApproach(itemGuid, out InteractionApproach approach))
        {
            CancelPickupPresentation(itemGuid, pendingPlacementToken);
            return;
        }

        if (approach.IsCloseRange)
        {
            bool armed = false;
            bool started = _movement.BeginApproach(
                approach,
                token =>
                {
                    armed = _transactions.TryArmPostArrivalPickup(
                        itemGuid,
                        approach.Target.LocalEntityId,
                        destinationContainerId,
                        placement,
                        pendingPlacementToken,
                        new RuntimeInteractionApproachToken(
                            token.ControllerLifetime,
                            token.ApproachGeneration),
                        out _);
                });
            if (!started || !armed)
            {
                if (_transactions.TryCancelPendingPickup(
                        itemGuid,
                        approach.Target.LocalEntityId,
                        out RuntimePendingPickup cancelled))
                {
                    CancelPickupPresentation(
                        cancelled.ServerGuid,
                        cancelled.PendingPlacementToken);
                }
                else
                {
                    CancelPickupPresentation(itemGuid, pendingPlacementToken);
                }
            }
            return;
        }

        _movement.BeginApproach(approach);
        if (!IsCurrentPickupPresentation(
                itemGuid,
                destinationContainerId,
                placement,
                pendingPlacementToken))
        {
            return;
        }
        var immediate = new RuntimePendingPickup(
            Token: 0u,
            itemGuid,
            approach.Target.LocalEntityId,
            destinationContainerId,
            placement,
            pendingPlacementToken,
            ApproachToken: default);
        if (_transactions.TryDispatchPickup(
                immediate,
                _transport,
                out uint sequence))
        {
            Console.WriteLine(
                $"[B.5] pickup item=0x{itemGuid:X8} container=0x{destinationContainerId:X8} placement={placement} seq={sequence}");
        }
        else
        {
            CancelPickupPresentation(itemGuid, pendingPlacementToken);
        }
    }

    public void OnNaturalMoveToComplete()
    {
        if (_transactions.TryGetPendingPickup(out RuntimePendingPickup pendingPickup))
        {
            HandleApproachCompletion(pendingPickup.ApproachToken, natural: true);
            return;
        }
        if (_transactions.TryGetPendingUse(out RuntimePendingUse pendingUse))
            HandleApproachCompletion(pendingUse.ApproachToken, natural: true);
    }

    private void HandleApproachCompletion(
        RuntimeInteractionApproachToken approachToken,
        bool natural)
    {
        bool pickupAccepted = _transactions.TryResolveApproachCompletion(
            approachToken,
            natural,
            out RuntimePendingPickup pendingPickup);
        if (pendingPickup.Token != 0u)
        {
            HandlePickupApproachCompletion(pendingPickup, pickupAccepted);
            return;
        }

        bool useAccepted = _transactions.TryResolveUseApproachCompletion(
            approachToken,
            natural,
            out RuntimePendingUse pendingUse);
        if (pendingUse.Token != 0u)
            HandleUseApproachCompletion(pendingUse, useAccepted);
    }

    private void HandlePickupApproachCompletion(
        RuntimePendingPickup pending,
        bool accepted)
    {
        if (!accepted)
        {
            CancelPickupPresentation(
                pending.ServerGuid,
                pending.PendingPlacementToken);
            return;
        }
        if (!_query.IsCurrent(pending.ServerGuid, pending.LocalEntityId))
        {
            CancelPickupPresentation(
                pending.ServerGuid,
                pending.PendingPlacementToken);
            return;
        }

        if (!IsCurrentPickupPresentation(
                pending.ServerGuid,
                pending.DestinationContainerId,
                pending.Placement,
                pending.PendingPlacementToken))
        {
            return;
        }
        if (!_transactions.TryDispatchPickup(
                pending,
                _transport,
                out _))
        {
            CancelPickupPresentation(
                pending.ServerGuid,
                pending.PendingPlacementToken);
        }
    }

    private void HandleUseApproachCompletion(
        RuntimePendingUse pending,
        bool accepted)
    {
        if (!accepted)
        {
            pending.Reservation?.CancelBeforeDispatch();
            return;
        }

        RuntimeInteractionDispatchResult result =
            _transactions.TryDispatchUse(
                pending.ServerGuid,
                pending.OwnedByPlayer,
                pending.Useable,
                pending.Reservation,
                _transport,
                out uint sequence);
        if (result == RuntimeInteractionDispatchResult.NotInWorld)
            _toast?.Invoke("Not in world");
        if (result == RuntimeInteractionDispatchResult.Dispatched)
        {
            Console.WriteLine(
                $"[B.4b] use guid=0x{pending.ServerGuid:X8} seq={sequence} (arrival-gated)");
        }
    }

    public void DrainOutbound()
    {
        while (_approachCompletions.TryTake(out PlayerApproachCompletion completion))
        {
            HandleApproachCompletion(
                new RuntimeInteractionApproachToken(
                    completion.Token.ControllerLifetime,
                    completion.Token.ApproachGeneration),
                completion.IsNatural);
        }
        _transactions.DrainOutbound(DispatchQueuedInteraction);
    }

    public void OnMoveToCancelled(WeenieError _) => CancelPendingApproach();

    public void OnEntityHidden(uint serverGuid)
    {
        _transactions.CancelQueuedInteractions(serverGuid);
        if (_transactions.TryCancelPendingPickup(
                serverGuid,
                localEntityId: null,
                out RuntimePendingPickup cancelled))
        {
            CancelPickupPresentation(
                cancelled.ServerGuid,
                cancelled.PendingPlacementToken);
        }
        if (_transactions.TryCancelPendingUse(serverGuid, out RuntimePendingUse cancelledUse))
            cancelledUse.Reservation?.CancelBeforeDispatch();
        if (_selection.SelectedObjectId == serverGuid)
        {
            _selection.Clear(
                SelectionChangeSource.System,
                SelectionChangeReason.Cleared);
        }
    }

    public void OnEntityRemoved(LiveEntityRecord record, bool replacementExists)
    {
        ArgumentNullException.ThrowIfNull(record);
        _transactions.CancelQueuedInteractions(
            record.ServerGuid,
            record.LocalEntityId);
        if (_transactions.TryCancelPendingPickup(
                record.ServerGuid,
                record.LocalEntityId,
                out RuntimePendingPickup cancelled))
        {
            CancelPickupPresentation(
                cancelled.ServerGuid,
                cancelled.PendingPlacementToken);
        }
        if (_transactions.TryCancelPendingUse(record.ServerGuid, out RuntimePendingUse cancelledUse))
            cancelledUse.Reservation?.CancelBeforeDispatch();
        if (!replacementExists && _selection.SelectedObjectId == record.ServerGuid)
        {
            _selection.Clear(
                SelectionChangeSource.System,
                SelectionChangeReason.SelectedObjectRemoved);
        }
    }

    public void ResetSession()
    {
        List<Exception> failures = [];
        try { CancelPendingApproach(); }
        catch (Exception error) { failures.Add(error); }
        try { _items.ResetSession(); }
        catch (Exception error) { failures.Add(error); }
        try { _selection.Reset(); }
        catch (Exception error) { failures.Add(error); }
        try { _approachCompletions.Clear(); }
        catch (Exception error) { failures.Add(error); }

        if (failures.Count != 0)
            throw new AggregateException(
                "One or more selection-interaction reset stages failed.",
                failures);
    }

    internal void ResetGenerationPresentation()
    {
        List<Exception> failures = [];
        try { CancelPendingApproach(); }
        catch (Exception error) { failures.Add(error); }
        try { _items.ResetGenerationPresentation(); }
        catch (Exception error) { failures.Add(error); }
        try { _approachCompletions.Clear(); }
        catch (Exception error) { failures.Add(error); }

        if (failures.Count != 0)
        {
            throw new AggregateException(
                "One or more selection-interaction presentation reset stages failed.",
                failures);
        }
    }

    private bool ValidatePickupTarget(uint serverGuid, bool showToast)
    {
        if (_query.IsCreature(serverGuid))
        {
            if (showToast)
                _toast?.Invoke(RetailMessages.CannotPickUpCreatures);
            return false;
        }
        if (_query.IsWieldedPositionState(serverGuid)
            && !_items.IsOwnedByPlayer(serverGuid))
        {
            if (showToast)
            {
                _toast?.Invoke(RetailMessages.BeingWieldedBySomeoneElse(
                    _query.Describe(serverGuid)));
            }
            return false;
        }
        if (_query.IsPickupable(serverGuid))
            return true;
        if (showToast)
            _toast?.Invoke(RetailMessages.CantBePickedUp(_query.Describe(serverGuid)));
        return false;
    }

    private bool EnqueueIdentityBound(
        RuntimeQueuedInteractionKind kind,
        uint serverGuid,
        bool requireLiveEntity)
    {
        uint? localEntityId = _query.TryCaptureIdentity(serverGuid, out uint localId)
            ? localId
            : null;
        ClientObject? item = _items.TryCaptureObjectIdentity(serverGuid, out ClientObject captured)
            ? captured
            : null;
        if ((requireLiveEntity && localEntityId is null)
            || (localEntityId is null && item is null))
        {
            return false;
        }

        var identity = new RuntimeInteractionIdentity(
            serverGuid,
            localEntityId,
            item);
        _transactions.Enqueue(new RuntimeQueuedInteraction(kind, identity));
        return true;
    }

    private bool IsCurrent(RuntimeInteractionIdentity identity)
        => (identity.LocalEntityId is not uint localId
                || _query.IsCurrent(identity.ServerGuid, localId))
            && (identity.ClientObject is not { } item
                || _items.IsCurrentObjectIdentity(identity.ServerGuid, item));

    private void CancelPendingApproach()
    {
        if (_transactions.TryCancelPendingPickup(
                out RuntimePendingPickup pending))
        {
            CancelPickupPresentation(
                pending.ServerGuid,
                pending.PendingPlacementToken);
        }
        // G3: a new SendPickup/RequestUse supersedes whatever approach was
        // previously armed — release an in-flight Use's reservation too, not
        // just pickup's presentation token.
        if (_transactions.TryCancelPendingUse(out RuntimePendingUse pendingUse))
            pendingUse.Reservation?.CancelBeforeDispatch();
    }

    private void DispatchQueuedInteraction(
        RuntimeQueuedInteraction interaction)
    {
        RuntimeInteractionIdentity identity = interaction.Identity;
        if (!IsCurrent(identity))
            return;

        switch (interaction.Kind)
        {
            case RuntimeQueuedInteractionKind.Activate:
                _items.ActivateItem(identity.ServerGuid);
                break;
            case RuntimeQueuedInteractionKind.Use:
                _items.UseSelectedOrEnterMode(identity.ServerGuid);
                break;
            case RuntimeQueuedInteractionKind.Pickup:
                if (ValidatePickupTarget(identity.ServerGuid, showToast: true))
                    _items.PlaceWorldItemInBackpack(identity.ServerGuid);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown queued interaction kind {interaction.Kind}.");
        }
    }

    private void CancelPickupPresentation(uint itemGuid, ulong token = 0u)
        => _items.CancelPendingBackpackPlacement(itemGuid, token);

    private bool IsCurrentPickupPresentation(
        uint itemGuid,
        uint destinationContainerId,
        int placement,
        ulong token)
        => token != 0u
            && _items.TryGetPendingBackpackPlacement(
                itemGuid,
                out PendingBackpackPlacement pending)
            && pending.Token == token
            && pending.ContainerId == destinationContainerId
            && pending.Placement == placement;
}
