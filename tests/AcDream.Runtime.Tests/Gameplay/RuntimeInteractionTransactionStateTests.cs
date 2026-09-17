using AcDream.Core.Items;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeInteractionTransactionStateTests
{
    private const uint Item = 0x70000001u;
    private const uint Container = 0x50000001u;

    [Fact]
    public void UseThrottleMatchesRetailStrictBoundaryAndResets()
    {
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);

        Assert.True(state.TryConsumeUseThrottle(1_000));
        Assert.False(state.TryConsumeUseThrottle(1_199));
        Assert.True(state.TryConsumeUseThrottle(1_200));

        state.ResetSession();

        Assert.True(state.TryConsumeUseThrottle(1_200));
    }

    [Fact]
    public void OrdinaryUseTransfersBusyReferenceToAuthoritativeUseDone()
    {
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);
        var transport = new Transport();
        ItemUseRequestReservation reservation =
            state.BeginUseRequestReservation();

        RuntimeInteractionDispatchResult result = state.TryDispatchUse(
            Item,
            ownedByPlayer: false,
            useable: true,
            reservation,
            transport,
            out uint sequence);

        Assert.Equal(RuntimeInteractionDispatchResult.Dispatched, result);
        Assert.Equal(1u, sequence);
        Assert.Equal(1, inventory.BusyCount);
        Assert.Equal(new[] { Item }, transport.Uses);
        RuntimeInteractionTransactionSnapshot snapshot =
            state.CaptureOwnership();
        Assert.Equal(Item, snapshot.LastUseSourceId);
        Assert.Equal(0u, snapshot.LastUseTargetId);

        reservation.CancelBeforeDispatch();
        Assert.Equal(1, inventory.BusyCount);

        state.CompleteUse(0u);
        Assert.Equal(0, inventory.BusyCount);
        Assert.Equal(new RuntimeItemUseCompletion(1, Item, 0u, 0u),
            state.LastItemUseCompletion);
        Assert.False(state.CaptureOwnership().AwaitingItemUseCompletion);
    }

    [Fact]
    public void TargetedItemUsePublishesExactAuthoritativeFailureReceipt()
    {
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);

        Assert.True(state.TryDispatchTargetedUse(
            Item,
            Container,
            static (_, _) => { },
            incrementBusy: true));
        Assert.True(state.CaptureOwnership().AwaitingItemUseCompletion);

        state.CompleteUse(0x0402u);

        Assert.Equal(
            new RuntimeItemUseCompletion(1, Item, Container, 0x0402u),
            state.LastItemUseCompletion);
        Assert.False(state.LastItemUseCompletion.IsSuccess);
        Assert.False(state.CaptureOwnership().AwaitingItemUseCompletion);
    }

    [Fact]
    public void UseDoneWithoutPluginItemRequestCannotFabricateItemReceipt()
    {
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);
        state.IncrementBusyCount();

        state.CompleteUse(0u);

        Assert.Equal(default, state.LastItemUseCompletion);
        Assert.Equal(0, inventory.BusyCount);
    }

    [Fact]
    public void ItemUseReceiptIsGenerationScoped()
    {
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);
        Assert.True(state.TryDispatchTargetedUse(
            Item,
            Container,
            static (_, _) => { },
            incrementBusy: true));
        state.CompleteUse(0u);
        Assert.True(state.LastItemUseCompletion.IsSuccess);

        state.ResetSession();

        Assert.Equal(default, state.LastItemUseCompletion);
        Assert.True(state.TryDispatchTargetedUse(
            Container,
            Item,
            static (_, _) => { },
            incrementBusy: true));
        state.CompleteUse(0u);
        Assert.Equal(1, state.LastItemUseCompletion.Revision);
        Assert.Equal(Container, state.LastItemUseCompletion.SourceObjectId);
    }

    [Theory]
    [InlineData(false, true, RuntimeInteractionDispatchResult.NotInWorld)]
    [InlineData(true, false, RuntimeInteractionDispatchResult.NotUseable)]
    public void RejectedUseReleasesOnlyItsReservation(
        bool inWorld,
        bool useable,
        RuntimeInteractionDispatchResult expected)
    {
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);
        var transport = new Transport { IsInWorld = inWorld };
        ItemUseRequestReservation reservation =
            state.BeginUseRequestReservation();

        RuntimeInteractionDispatchResult result = state.TryDispatchUse(
            Item,
            ownedByPlayer: false,
            useable,
            reservation,
            transport,
            out _);

        Assert.Equal(expected, result);
        Assert.Equal(0, inventory.BusyCount);
        Assert.Empty(transport.Uses);
    }

    [Fact]
    public void TargetedUseRecordsExactSourceAndTarget()
    {
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);
        var sent = new List<(uint Source, uint Target)>();

        Assert.True(state.TryDispatchTargetedUse(
            Item,
            Container,
            (source, target) => sent.Add((source, target)),
            incrementBusy: true));

        Assert.Equal(new[] { (Item, Container) }, sent);
        Assert.Equal(1, inventory.BusyCount);
        RuntimeInteractionTransactionSnapshot snapshot =
            state.CaptureOwnership();
        Assert.Equal(Item, snapshot.LastUseSourceId);
        Assert.Equal(Container, snapshot.LastUseTargetId);
    }

    [Fact]
    public void ReentrantResetPreventsTargetedUseStateResurrection()
    {
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);
        var sent = new List<(uint Source, uint Target)>();

        Assert.False(state.TryDispatchTargetedUse(
            Item,
            Container,
            (source, target) =>
            {
                sent.Add((source, target));
                state.ResetSession();
            },
            incrementBusy: true));

        Assert.Equal(new[] { (Item, Container) }, sent);
        Assert.Equal(0, inventory.BusyCount);
        RuntimeInteractionTransactionSnapshot snapshot =
            state.CaptureOwnership();
        Assert.Equal(0u, snapshot.LastUseSourceId);
        Assert.Equal(0u, snapshot.LastUseTargetId);
    }

    [Fact]
    public void AppraisalReplacementKeepsOneBusyReferenceAndExactCurrentId()
    {
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);
        var sent = new List<uint>();

        Assert.True(state.TryRequestAppraisal(Item, sent.Add));
        Assert.True(state.TryRequestAppraisal(Container, sent.Add));
        Assert.Equal(1, inventory.BusyCount);
        Assert.False(state.AcceptAppraisalResponse(Item).Accepted);

        RuntimeAppraisalResponseAcceptance first =
            state.AcceptAppraisalResponse(Container);
        Assert.True(first.Accepted);
        Assert.True(first.FirstResponse);
        Assert.Equal(Container, state.CurrentAppraisalId);
        Assert.Equal(0, inventory.BusyCount);

        Assert.True(state.RefreshCurrentAppraisal(sent.Add));
        Assert.Equal(new[] { Item, Container, Container }, sent);

        Assert.True(state.CancelObjectAppraisalForSpell(sent.Add));
        Assert.Equal(0u, state.CurrentAppraisalId);
        Assert.Equal(0u, sent[^1]);
    }

    /// <summary>
    /// A walk asks whether a door is locked. The answer must reach the object without
    /// becoming the appraisal the character is looking at, so nothing opens on screen.
    /// </summary>
    [Fact]
    public void AQuietAppraisalAnswersWithoutBecomingTheCurrentOne()
    {
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);
        var sent = new List<uint>();

        Assert.True(state.TryRequestAppraisal(Item, sent.Add, AppraisalRequestOrigin.Automation));
        Assert.Equal(new[] { Item }, sent);
        Assert.Equal(Item, state.AwaitingAppraisalId);

        RuntimeAppraisalResponseAcceptance answer = state.AcceptAppraisalResponse(Item);

        Assert.True(answer.Accepted);
        Assert.True(answer.FirstResponse);
        Assert.Equal(AppraisalRequestOrigin.Automation, answer.Origin);
        Assert.False(answer.PresentInUi);
        Assert.Equal(0u, state.CurrentAppraisalId);
        Assert.Equal(0, inventory.BusyCount);
    }

    [Fact]
    public void AnAppraisalTheCharacterAskedForBecomesTheCurrentOne()
    {
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);
        var sent = new List<uint>();

        Assert.True(state.TryRequestAppraisal(Item, sent.Add));
        RuntimeAppraisalResponseAcceptance answer = state.AcceptAppraisalResponse(Item);

        Assert.True(answer.FirstResponse);
        Assert.True(answer.PresentInUi);
        Assert.Equal(Item, state.CurrentAppraisalId);
    }

    [Fact]
    public void AppraisalReceivedFiresOnBothTheFirstResponseAndARefresh()
    {
        // AcceptAppraisalResponse used to only raise AppraisalReceived on
        // the first response (objectId == _awaitingAppraisalId). A
        // RefreshCurrentAppraisal re-request lands on the same objectId as
        // _currentAppraisalId, so it was silently swallowed: Accepted=true
        // came back to the caller but no observer ever heard about it.
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);
        var received = new List<uint>();
        state.AppraisalReceived += received.Add;

        Assert.True(state.TryRequestAppraisal(Item, _ => { }));
        RuntimeAppraisalResponseAcceptance first =
            state.AcceptAppraisalResponse(Item);
        Assert.True(first.Accepted);
        Assert.True(first.FirstResponse);
        Assert.Equal(new[] { Item }, received);

        Assert.True(state.RefreshCurrentAppraisal(_ => { }));
        RuntimeAppraisalResponseAcceptance refresh =
            state.AcceptAppraisalResponse(Item);
        Assert.True(refresh.Accepted);
        Assert.False(refresh.FirstResponse);
        Assert.Equal(new[] { Item, Item }, received);
    }

    [Fact]
    public void AppraisalTransportFailureRollsBackOnlyItsBusyReference()
    {
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);

        Assert.Throws<InvalidOperationException>(() =>
            state.TryRequestAppraisal(
                Item,
                static _ => throw new InvalidOperationException("transport")));

        RuntimeInteractionTransactionSnapshot snapshot =
            state.CaptureOwnership();
        Assert.Equal(0u, snapshot.AwaitingAppraisalId);
        Assert.Equal(0u, snapshot.CurrentAppraisalId);
        Assert.Equal(0, inventory.BusyCount);
    }

    [Fact]
    public void AutomationAppraisalOfADifferentObjectDoesNotRetargetCurrent()
    {
        // Live finding: a plugin's background Identify(objectId) used to
        // hijack the single appraisal slot and, once its response landed,
        // silently swap the examination window's target object out from
        // under the user (observed live: a periodic gauntlet re-identify
        // popped the "Leather Gauntlets" assess window). A response tagged
        // Automation must leave CurrentAppraisalId alone when it lands on
        // an object other than the one already current.
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);

        Assert.True(state.TryRequestAppraisal(Item, _ => { }));
        RuntimeAppraisalResponseAcceptance userFirst =
            state.AcceptAppraisalResponse(Item);
        Assert.True(userFirst.Accepted);
        Assert.True(userFirst.PresentInUi);
        Assert.Equal(Item, state.CurrentAppraisalId);

        Assert.True(state.TryRequestAppraisal(
            Container,
            _ => { },
            AppraisalRequestOrigin.Automation));
        RuntimeAppraisalResponseAcceptance automation =
            state.AcceptAppraisalResponse(Container);

        Assert.True(automation.Accepted);
        Assert.True(automation.FirstResponse);
        Assert.Equal(AppraisalRequestOrigin.Automation, automation.Origin);
        Assert.False(automation.PresentInUi);
        Assert.Equal(Item, state.CurrentAppraisalId);

        // The presentation target did not move -- but the plugin-facing
        // completion signal must, regardless: this response is the one a
        // plugin's Identify(Container) was waiting on, and it completed.
        Assert.Equal(Container, state.LastCompletedAppraisalId);
    }

    [Fact]
    public void AutomationResponseAdvancesCompletionSignalNotPresentationTarget()
    {
        // HIGH-1: CurrentAppraisalId (presentation) and
        // LastCompletedAppraisalId (the plugin-facing completion signal
        // mapped by AppAutomationSurface into
        // ILootAutomation.Appraisal.CurrentObjectId) are two different
        // things. Before this split, MossTank's corpse-identify wait
        // polled CurrentAppraisalId and never observed it change for a
        // corpse the window was not showing, so corpse looting stalled
        // forever. LastCompletedAppraisalId must advance for every
        // completed response, of either origin, independent of whatever
        // the window is presenting.
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);

        Assert.Equal(0u, state.LastCompletedAppraisalId);

        Assert.True(state.TryRequestAppraisal(
            Container,
            _ => { },
            AppraisalRequestOrigin.Automation));
        RuntimeAppraisalResponseAcceptance automation =
            state.AcceptAppraisalResponse(Container);

        Assert.True(automation.Accepted);
        Assert.False(automation.PresentInUi);
        Assert.Equal(0u, state.CurrentAppraisalId);
        Assert.Equal(Container, state.LastCompletedAppraisalId);
    }

    [Fact]
    public void AutomationAppraisalOfTheCurrentObjectStillPresents()
    {
        // The flip side: a plugin re-identifying the object the user is
        // already looking at is a legitimate background data refresh
        // (durability ticked, a stack count moved) and must still reach
        // the UI so the open window's content stays accurate -- it just
        // must not reopen or refocus the window (that is
        // AppraisalUiController's job via the Origin field, not Runtime's).
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);

        Assert.True(state.TryRequestAppraisal(Item, _ => { }));
        state.AcceptAppraisalResponse(Item);
        Assert.Equal(Item, state.CurrentAppraisalId);

        Assert.True(state.TryRequestAppraisal(
            Item,
            _ => { },
            AppraisalRequestOrigin.Automation));
        RuntimeAppraisalResponseAcceptance refresh =
            state.AcceptAppraisalResponse(Item);

        Assert.True(refresh.Accepted);
        Assert.True(refresh.FirstResponse);
        Assert.Equal(AppraisalRequestOrigin.Automation, refresh.Origin);
        Assert.True(refresh.PresentInUi);
        Assert.Equal(Item, state.CurrentAppraisalId);
    }

    [Fact]
    public void UserAppraisalDuringAnInFlightAutomationAppraisalStillRetargets()
    {
        // A deliberate user assess issued while a plugin's Identify is
        // still awaiting its response must win the single appraisal slot
        // and open the window for the user's object -- exactly like today,
        // just now carrying an explicit User tag instead of an implicit
        // default.
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);

        Assert.True(state.TryRequestAppraisal(
            Container,
            _ => { },
            AppraisalRequestOrigin.Automation));
        Assert.Equal(
            AppraisalRequestOrigin.Automation,
            state.AwaitingAppraisalOrigin);

        Assert.True(state.TryRequestAppraisal(Item, _ => { }));
        Assert.Equal(
            AppraisalRequestOrigin.User,
            state.AwaitingAppraisalOrigin);

        RuntimeAppraisalResponseAcceptance userResponse =
            state.AcceptAppraisalResponse(Item);
        Assert.True(userResponse.Accepted);
        Assert.Equal(AppraisalRequestOrigin.User, userResponse.Origin);
        Assert.True(userResponse.PresentInUi);
        Assert.Equal(Item, state.CurrentAppraisalId);

        // The superseded Automation request's late response is dropped by
        // the existing single-slot semantics (unchanged by this change) --
        // it is neither the awaiting id nor the current one any more.
        Assert.False(state.AcceptAppraisalResponse(Container).Accepted);
    }

    [Fact]
    public void AutomationAppraisalRefusesToEvictAnAwaitingUserAppraisal()
    {
        // HIGH-2: the reverse of the test above. Before this fix, a
        // plugin's background Identify could win the single awaiting slot
        // away from a user's own assess that was already in flight, so the
        // user's assess would silently produce nothing once its response
        // arrived (it was no longer the awaiting id, so
        // AcceptAppraisalResponse would refuse it). An Automation request
        // must instead be refused outright while a User request awaits,
        // leaving the user's request completely untouched.
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);
        var userSent = new List<uint>();
        var automationSent = new List<uint>();

        Assert.True(state.TryRequestAppraisal(Item, userSent.Add));
        Assert.Equal(
            AppraisalRequestOrigin.User,
            state.AwaitingAppraisalOrigin);

        bool accepted = state.TryRequestAppraisal(
            Container,
            automationSent.Add,
            AppraisalRequestOrigin.Automation);

        Assert.False(accepted);
        Assert.Empty(automationSent);
        Assert.Equal(Item, state.AwaitingAppraisalId);
        Assert.Equal(
            AppraisalRequestOrigin.User,
            state.AwaitingAppraisalOrigin);
        Assert.Equal(1, inventory.BusyCount);

        // The user's own assess still completes normally -- it was never
        // touched by the refused Automation request.
        RuntimeAppraisalResponseAcceptance userResponse =
            state.AcceptAppraisalResponse(Item);
        Assert.True(userResponse.Accepted);
        Assert.True(userResponse.PresentInUi);
        Assert.Equal(AppraisalRequestOrigin.User, userResponse.Origin);
        Assert.Equal(Item, state.CurrentAppraisalId);
        Assert.Equal(0, inventory.BusyCount);
    }

    [Fact]
    public void ReidentifyOfACompletedObjectThenCancelledLeavesNoFalseCompletion()
    {
        // HIGH-3: LastCompletedAppraisalId is a one-shot signal for the
        // request that produced it, not a sticky history. A plugin
        // re-identifying an object it already saw complete, whose new
        // request is then cancelled (CancelObjectAppraisalForSpell) before
        // it ever completes again, must not see the OLD completion
        // misread as an answer to the new request -- a poll of
        // "CurrentObjectId == myId && AwaitingObjectId != myId" would be
        // trivially (and wrongly) true the instant the new request was
        // accepted, since both halves were already true from the first
        // completion.
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);
        var sent = new List<uint>();

        Assert.True(state.TryRequestAppraisal(
            Container, sent.Add, AppraisalRequestOrigin.Automation));
        state.AcceptAppraisalResponse(Container);
        Assert.Equal(Container, state.LastCompletedAppraisalId);

        Assert.True(state.TryRequestAppraisal(
            Container, sent.Add, AppraisalRequestOrigin.Automation));
        Assert.Equal(0u, state.LastCompletedAppraisalId);

        Assert.True(state.CancelObjectAppraisalForSpell(sent.Add));

        Assert.Equal(0u, state.LastCompletedAppraisalId);
        Assert.Equal(0u, state.AwaitingAppraisalId);
    }

    [Fact]
    public void ReidentifyOfACompletedObjectThenDisplacedByAnotherRequestLeavesNoFalseCompletion()
    {
        // HIGH-3, the other half: instead of a cancel, the re-identify's
        // awaiting slot gets displaced by an unrelated request before it
        // completes again. The stale completion from the FIRST identify
        // must not survive to be misread as an answer to the second.
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);
        var sent = new List<uint>();

        Assert.True(state.TryRequestAppraisal(
            Container, sent.Add, AppraisalRequestOrigin.Automation));
        state.AcceptAppraisalResponse(Container);
        Assert.Equal(Container, state.LastCompletedAppraisalId);

        Assert.True(state.TryRequestAppraisal(
            Container, sent.Add, AppraisalRequestOrigin.Automation));
        Assert.Equal(0u, state.LastCompletedAppraisalId);

        // A user assess for a different object displaces Container's
        // still-in-flight re-identify out of the single awaiting slot.
        Assert.True(state.TryRequestAppraisal(Item, sent.Add));
        Assert.Equal(Item, state.AwaitingAppraisalId);

        Assert.Equal(0u, state.LastCompletedAppraisalId);
    }

    [Fact]
    public void AppraisalReceivedFiresForAnAutomationOriginResponseThatDoesNotPresent()
    {
        // Plugin-facing observers (IEvents.ObjectChanged(IdentReceived),
        // ILootAutomation.Appraisal) must still see every accepted
        // appraisal response regardless of whether the UI presents it.
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);
        var received = new List<uint>();
        state.AppraisalReceived += received.Add;

        Assert.True(state.TryRequestAppraisal(Item, _ => { }));
        state.AcceptAppraisalResponse(Item);

        Assert.True(state.TryRequestAppraisal(
            Container,
            _ => { },
            AppraisalRequestOrigin.Automation));
        RuntimeAppraisalResponseAcceptance automation =
            state.AcceptAppraisalResponse(Container);

        Assert.False(automation.PresentInUi);
        Assert.Equal(new[] { Item, Container }, received);
    }

    [Fact]
    public void TryRequestAppraisalDefaultsToUserOrigin()
    {
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);

        Assert.True(state.TryRequestAppraisal(Item, _ => { }));
        Assert.Equal(AppraisalRequestOrigin.User, state.AwaitingAppraisalOrigin);
    }

    [Fact]
    public void OutboundQueueIsTypedFifoAndReentrantWorkWaitsForNextDrain()
    {
        using var inventory = NewInventory(out ClientObjectTable objects);
        using var state = new RuntimeInteractionTransactionState(inventory);
        var item = new ClientObject { ObjectId = Item };
        objects.AddOrUpdate(item);
        RuntimeInteractionIdentity identity = new(Item, 12u, item);
        state.Enqueue(new RuntimeQueuedInteraction(
            RuntimeQueuedInteractionKind.Activate,
            identity));
        state.Enqueue(new RuntimeQueuedInteraction(
            RuntimeQueuedInteractionKind.Use,
            identity));
        var seen = new List<RuntimeQueuedInteractionKind>();

        state.DrainOutbound(value =>
        {
            seen.Add(value.Kind);
            if (value.Kind == RuntimeQueuedInteractionKind.Activate)
            {
                state.Enqueue(new RuntimeQueuedInteraction(
                    RuntimeQueuedInteractionKind.Pickup,
                    identity));
            }
        });

        Assert.Equal(
            new[]
            {
                RuntimeQueuedInteractionKind.Activate,
                RuntimeQueuedInteractionKind.Use,
            },
            seen);
        Assert.Equal(1, state.OutboundCount);

        state.DrainOutbound(value => seen.Add(value.Kind));
        Assert.Equal(RuntimeQueuedInteractionKind.Pickup, seen[^1]);
        Assert.Equal(0, state.OutboundCount);
    }

    [Fact]
    public void ReentrantSessionResetStopsTheCapturedFrameSuffix()
    {
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);
        RuntimeInteractionIdentity identity = new(Item, 12u, null);
        state.Enqueue(new RuntimeQueuedInteraction(
            RuntimeQueuedInteractionKind.Activate,
            identity));
        state.Enqueue(new RuntimeQueuedInteraction(
            RuntimeQueuedInteractionKind.Use,
            identity));
        var seen = new List<RuntimeQueuedInteractionKind>();

        state.DrainOutbound(interaction =>
        {
            seen.Add(interaction.Kind);
            state.ResetSession();
        });

        Assert.Equal(
            new[] { RuntimeQueuedInteractionKind.Activate },
            seen);
        Assert.Equal(0, state.OutboundCount);
        Assert.Equal(0, inventory.BusyCount);
    }

    [Fact]
    public void DispatchFailureIsRecordedWithoutLosingLaterQueuedWork()
    {
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);
        RuntimeInteractionIdentity identity = new(Item, 12u, null);
        state.Enqueue(new RuntimeQueuedInteraction(
            RuntimeQueuedInteractionKind.Activate,
            identity));
        state.Enqueue(new RuntimeQueuedInteraction(
            RuntimeQueuedInteractionKind.Use,
            identity));

        Assert.Throws<InvalidOperationException>(() =>
            state.DrainOutbound(static _ =>
                throw new InvalidOperationException("observer")));

        Assert.Equal(1, state.DispatchFailureCount);
        Assert.Equal(1, state.OutboundCount);
        var seen = new List<RuntimeQueuedInteractionKind>();
        state.DrainOutbound(value => seen.Add(value.Kind));
        Assert.Equal(new[] { RuntimeQueuedInteractionKind.Use }, seen);
    }

    [Fact]
    public void HiddenAndDeletedCancellationPreservesOtherIdentityOrder()
    {
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);
        const uint reusedGuid = Item;
        const uint otherGuid = 0x70000002u;
        state.Enqueue(new RuntimeQueuedInteraction(
            RuntimeQueuedInteractionKind.Activate,
            new RuntimeInteractionIdentity(reusedGuid, 11u, null)));
        state.Enqueue(new RuntimeQueuedInteraction(
            RuntimeQueuedInteractionKind.Use,
            new RuntimeInteractionIdentity(otherGuid, 21u, null)));
        state.Enqueue(new RuntimeQueuedInteraction(
            RuntimeQueuedInteractionKind.Pickup,
            new RuntimeInteractionIdentity(reusedGuid, 12u, null)));
        state.Enqueue(new RuntimeQueuedInteraction(
            RuntimeQueuedInteractionKind.Activate,
            new RuntimeInteractionIdentity(otherGuid, 22u, null)));

        Assert.Equal(1, state.CancelQueuedInteractions(reusedGuid, 11u));
        Assert.Equal(3, state.OutboundCount);
        Assert.Equal(1, state.CancelQueuedInteractions(reusedGuid));
        Assert.Equal(2, state.OutboundCount);

        var seen = new List<(uint Guid, uint? LocalId)>();
        state.DrainOutbound(interaction => seen.Add(
            (interaction.Identity.ServerGuid,
                interaction.Identity.LocalEntityId)));

        Assert.Equal(
            new[]
            {
                (otherGuid, (uint?)21u),
                (otherGuid, (uint?)22u),
            },
            seen);
    }

    [Fact]
    public void PostArrivalPickupRequiresItsExactApproachAndReservationTokens()
    {
        using var inventory = NewInventory(out ClientObjectTable objects);
        using var state = new RuntimeInteractionTransactionState(inventory);
        objects.AddOrUpdate(new ClientObject { ObjectId = Item });
        Assert.True(inventory.TryReserve(
            InventoryRequestKind.Pickup,
            Item,
            out PendingInventoryRequest request));
        var approach = new RuntimeInteractionApproachToken(3u, 7u);

        Assert.True(state.TryArmPostArrivalPickup(
            Item,
            localEntityId: 12u,
            Container,
            placement: 0,
            request.Token,
            approach,
            out RuntimePendingPickup pending));
        Assert.False(state.TryResolveApproachCompletion(
            new RuntimeInteractionApproachToken(3u, 8u),
            natural: true,
            out _));
        Assert.True(state.TryResolveApproachCompletion(
            approach,
            natural: true,
            out RuntimePendingPickup ready));
        Assert.Equal(pending.Token, ready.Token);

        var transport = new Transport();
        Assert.True(state.TryDispatchPickup(ready, transport, out uint sequence));
        Assert.Equal(1u, sequence);
        Assert.Equal(new[] { (Item, Container, 0) }, transport.Pickups);
        Assert.True(inventory.TryGetPending(out PendingInventoryRequest sent));
        Assert.True(sent.Dispatched);
    }

    [Fact]
    public void CancellationResetAndDisposalConvergeTheCompleteLedger()
    {
        using var inventory = NewInventory(out ClientObjectTable objects);
        var state = new RuntimeInteractionTransactionState(inventory);
        objects.AddOrUpdate(new ClientObject { ObjectId = Item });
        Assert.True(inventory.TryReserve(
            InventoryRequestKind.Pickup,
            Item,
            out PendingInventoryRequest request));
        Assert.True(state.TryArmPostArrivalPickup(
            Item,
            localEntityId: 12u,
            Container,
            placement: 0,
            request.Token,
            new RuntimeInteractionApproachToken(1u, 1u),
            out _));
        state.Enqueue(new RuntimeQueuedInteraction(
            RuntimeQueuedInteractionKind.Pickup,
            new RuntimeInteractionIdentity(Item, 12u, objects.Get(Item))));
        state.IncrementBusyCount();

        state.ResetSession();

        Assert.Equal(0, inventory.BusyCount);
        Assert.False(inventory.HasPendingRequest);
        Assert.Equal(0, state.OutboundCount);
        Assert.False(state.HasPendingPickup);

        state.Dispose();
        Assert.True(state.CaptureOwnership().IsConverged);
        state.Dispose();
    }


    [Fact]
    public void PostArrivalUseRequiresItsExactApproachTokenAndDispatchesTheHeldReservation()
    {
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);
        ItemUseRequestReservation reservation = state.BeginUseRequestReservation();
        var approach = new RuntimeInteractionApproachToken(3u, 7u);

        Assert.True(state.TryArmPostArrivalUse(
            Item,
            ownedByPlayer: false,
            useable: true,
            reservation,
            approach,
            out RuntimePendingUse pending));
        Assert.True(state.HasPendingUse);
        // Nothing sent yet — armed, not dispatched.
        Assert.Equal(1, inventory.BusyCount);

        // The wrong token must not resolve it.
        Assert.False(state.TryResolveUseApproachCompletion(
            new RuntimeInteractionApproachToken(3u, 8u),
            natural: true,
            out _));
        Assert.True(state.HasPendingUse);

        Assert.True(state.TryResolveUseApproachCompletion(
            approach,
            natural: true,
            out RuntimePendingUse ready));
        Assert.Equal(pending.Token, ready.Token);
        Assert.False(state.HasPendingUse);

        var transport = new Transport();
        RuntimeInteractionDispatchResult result = state.TryDispatchUse(
            ready.ServerGuid,
            ready.OwnedByPlayer,
            ready.Useable,
            ready.Reservation,
            transport,
            out uint sequence);

        Assert.Equal(RuntimeInteractionDispatchResult.Dispatched, result);
        Assert.Equal(1u, sequence);
        Assert.Equal(new[] { Item }, transport.Uses);
        // The reservation transferred to the authoritative UseDone wait,
        // exactly like an ordinary immediate Use.
        Assert.Equal(1, inventory.BusyCount);
    }

    [Fact]
    public void UseApproachCompletionCancellationReleasesTheHeldReservationWithoutSending()
    {
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);
        ItemUseRequestReservation reservation = state.BeginUseRequestReservation();
        var approach = new RuntimeInteractionApproachToken(1u, 1u);
        Assert.True(state.TryArmPostArrivalUse(
            Item, ownedByPlayer: false, useable: true, reservation, approach, out _));
        Assert.Equal(1, inventory.BusyCount);

        Assert.False(state.TryResolveUseApproachCompletion(
            approach, natural: false, out RuntimePendingUse cancelled));
        Assert.False(state.HasPendingUse);
        cancelled.Reservation?.CancelBeforeDispatch();

        Assert.Equal(0, inventory.BusyCount);
    }

    [Fact]
    public void CancelPendingUseByGuidOnlyMatchesTheArmedTarget()
    {
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);
        ItemUseRequestReservation reservation = state.BeginUseRequestReservation();
        Assert.True(state.TryArmPostArrivalUse(
            Item, ownedByPlayer: false, useable: true, reservation,
            new RuntimeInteractionApproachToken(1u, 1u), out _));

        Assert.False(state.TryCancelPendingUse(Container, out _));
        Assert.True(state.HasPendingUse);

        Assert.True(state.TryCancelPendingUse(Item, out RuntimePendingUse cancelled));
        Assert.False(state.HasPendingUse);
        cancelled.Reservation?.CancelBeforeDispatch();
        Assert.Equal(0, inventory.BusyCount);
    }

    [Fact]
    public void ArmingASecondUseWhileOneIsAlreadyArmedFails()
    {
        using var inventory = NewInventory(out _);
        using var state = new RuntimeInteractionTransactionState(inventory);
        Assert.True(state.TryArmPostArrivalUse(
            Item, ownedByPlayer: false, useable: true, reservation: null,
            new RuntimeInteractionApproachToken(1u, 1u), out _));

        Assert.False(state.TryArmPostArrivalUse(
            Container, ownedByPlayer: false, useable: true, reservation: null,
            new RuntimeInteractionApproachToken(2u, 2u), out _));
        Assert.Equal(Item, state.TryGetPendingUse(out RuntimePendingUse stillArmed)
            ? stillArmed.ServerGuid
            : 0u);
    }

    [Fact]
    public void PendingUseReservationIsReleasedByResetSessionAndDisposal()
    {
        using var inventory = NewInventory(out _);
        var state = new RuntimeInteractionTransactionState(inventory);
        ItemUseRequestReservation reservation = state.BeginUseRequestReservation();
        Assert.True(state.TryArmPostArrivalUse(
            Item, ownedByPlayer: false, useable: true, reservation,
            new RuntimeInteractionApproachToken(1u, 1u), out _));
        Assert.Equal(1, inventory.BusyCount);

        state.ResetSession();

        Assert.Equal(0, inventory.BusyCount);
        Assert.False(state.HasPendingUse);

        state.Dispose();
        Assert.True(state.CaptureOwnership().IsConverged);
        state.Dispose();
    }

    [Fact]
    public void InstancesDoNotShareThrottleQueueAppraisalOrPickupState()
    {
        using var firstInventory = NewInventory(out _);
        using var secondInventory = NewInventory(out _);
        using var first =
            new RuntimeInteractionTransactionState(firstInventory);
        using var second =
            new RuntimeInteractionTransactionState(secondInventory);

        Assert.True(first.TryConsumeUseThrottle(100));
        first.TryRequestAppraisal(Item, static _ => { });
        first.Enqueue(new RuntimeQueuedInteraction(
            RuntimeQueuedInteractionKind.Use,
            new RuntimeInteractionIdentity(Item, 1u, null)));

        RuntimeInteractionTransactionSnapshot snapshot =
            second.CaptureOwnership();
        Assert.Equal(0u, snapshot.AwaitingAppraisalId);
        Assert.Equal(0, snapshot.OutboundCount);
        Assert.Equal(0, secondInventory.BusyCount);
        Assert.True(second.TryConsumeUseThrottle(100));
    }

    private static InventoryTransactionState NewInventory(
        out ClientObjectTable objects)
    {
        objects = new ClientObjectTable();
        return new InventoryTransactionState(objects);
    }

    private sealed class Transport : IRuntimeInteractionTransport
    {
        private uint _sequence;
        public bool IsInWorld { get; set; } = true;
        public List<uint> Uses { get; } = [];
        public List<(uint Item, uint Container, int Placement)> Pickups { get; } = [];

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
}
