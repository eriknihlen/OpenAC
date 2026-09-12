using AcDream.Core.Items;

namespace AcDream.Core.Tests.Items;

public sealed class InventoryTransactionStateTests
{
    private const uint Player = 0x50000001u;
    private const uint Pack = 0x50000002u;
    private const uint First = 0x60000001u;
    private const uint Second = 0x60000002u;

    [Fact]
    public void ReserveClosesReentrantDispatchWindowAndSuccessfulSendCommits()
    {
        var objects = CreateTable();
        using var state = new InventoryTransactionState(objects);
        bool secondDispatched = true;

        Assert.True(state.TryDispatch(
            InventoryRequestKind.PutInContainer,
            First,
            () =>
            {
                secondDispatched = state.TryDispatch(
                    InventoryRequestKind.PutInContainer,
                    Second,
                    static () => true);
                return true;
            }));

        Assert.False(secondDispatched);
        Assert.True(state.TryGetPending(out PendingInventoryRequest pending));
        Assert.Equal(First, pending.ItemId);
        Assert.True(pending.Dispatched);
    }

    [Fact]
    public void DispatchFailureOrExceptionReleasesExactProvisionalOwner()
    {
        var objects = CreateTable();
        using var state = new InventoryTransactionState(objects);

        Assert.False(state.TryDispatch(
            InventoryRequestKind.PutInContainer,
            First,
            static () => false));
        Assert.False(state.HasPendingRequest);

        Assert.Throws<InvalidOperationException>(() => state.TryDispatch(
            InventoryRequestKind.PutInContainer,
            First,
            static () => throw new InvalidOperationException("transport")));
        Assert.False(state.HasPendingRequest);
        Assert.True(state.CanBeginRequest);
    }

    [Fact]
    public void FailedPrePublicationCallbackRollsBackOnlyItsReservation()
    {
        var objects = CreateTable();
        using var state = new InventoryTransactionState(objects);

        Assert.Throws<InvalidOperationException>(() =>
            state.TryReserve(
                InventoryRequestKind.Pickup,
                First,
                out _,
                static _ => throw new InvalidOperationException("projection")));

        Assert.False(state.HasPendingRequest);
        Assert.True(state.CanBeginRequest);
    }

    [Fact]
    public void AuthoritativeResponseClearsBeforeReentrantCompletionObserver()
    {
        var objects = CreateTable();
        using var state = new InventoryTransactionState(objects);
        bool replacementDispatched = false;
        state.RequestCompleted += request =>
        {
            Assert.Equal(First, request.ItemId);
            Assert.False(state.HasPendingRequest);
            replacementDispatched = state.TryDispatch(
                InventoryRequestKind.PutInContainer,
                Second,
                static () => true);
        };

        Assert.True(state.TryDispatch(
            InventoryRequestKind.PutInContainer,
            First,
            static () => true));
        Assert.True(objects.ApplyConfirmedServerMove(First, Player, 0u, 0));

        Assert.True(replacementDispatched);
        Assert.True(state.TryGetPending(out PendingInventoryRequest pending));
        Assert.Equal(Second, pending.ItemId);
    }

    [Fact]
    public void OptimisticMoveAndUnrelatedResponsesDoNotCompleteRequest()
    {
        var objects = CreateTable();
        using var state = new InventoryTransactionState(objects);

        Assert.True(state.TryDispatch(
            InventoryRequestKind.PutInContainer,
            First,
            () => objects.MoveItemOptimistic(First, Player, 0)));
        Assert.True(objects.ApplyConfirmedServerMove(Second, Player, 1u, 1));
        Assert.True(state.HasPendingRequest);

        Assert.True(objects.ApplyConfirmedServerMove(First, Player, 0u, 0));
        Assert.False(state.HasPendingRequest);
    }

    [Fact]
    public void ReusedGuidResponseCannotCompletePriorObjectIdentity()
    {
        var objects = CreateTable();
        using var state = new InventoryTransactionState(objects);
        ClientObject original = objects.Get(First)!;

        Assert.True(state.TryDispatch(
            InventoryRequestKind.PutInContainer,
            First,
            static () => true));

        var replacement = new ClientObject
        {
            ObjectId = First,
            Name = "replacement",
            Type = ItemType.Misc,
        };
        objects.AddOrUpdate(replacement);
        objects.MoveItem(First, Pack, 0);
        Assert.NotSame(original, replacement);

        Assert.True(objects.ApplyConfirmedServerMove(First, Player, 0u, 0));
        Assert.True(state.HasPendingRequest);
    }

    [Fact]
    public void UseReservationTransfersToUseDoneOrCancelsBeforeDispatch()
    {
        var objects = CreateTable();
        using var state = new InventoryTransactionState(objects);

        ItemUseRequestReservation cancelled = state.BeginUseRequestReservation();
        Assert.Equal(1, state.BusyCount);
        cancelled.CancelBeforeDispatch();
        cancelled.CancelBeforeDispatch();
        Assert.Equal(0, state.BusyCount);

        ItemUseRequestReservation dispatched = state.BeginUseRequestReservation();
        dispatched.MarkDispatched();
        dispatched.CancelBeforeDispatch();
        Assert.Equal(1, state.BusyCount);
        state.CompleteUse(0u);
        Assert.Equal(0, state.BusyCount);
    }

    [Fact]
    public void ResetInvalidatesLateUseReservationAndClearsPendingRequest()
    {
        var objects = CreateTable();
        using var state = new InventoryTransactionState(objects);
        Assert.True(state.TryReserve(
            InventoryRequestKind.Pickup,
            First,
            out _));
        ItemUseRequestReservation stale = state.BeginUseRequestReservation();

        state.ResetSession();
        stale.CancelBeforeDispatch();

        Assert.Equal(0, state.BusyCount);
        Assert.False(state.HasPendingRequest);
        Assert.True(state.CanBeginRequest);
    }

    [Fact]
    public void ObserverFailureIsIsolatedAndRecorded()
    {
        var objects = CreateTable();
        using var state = new InventoryTransactionState(objects);
        int delivered = 0;
        state.StateChanged += () =>
            throw new InvalidOperationException("observer");
        state.StateChanged += () => delivered++;

        state.IncrementBusyCount();

        Assert.Equal(1, delivered);
        Assert.Equal(1, state.DispatchFailureCount);
        Assert.IsType<InvalidOperationException>(state.LastDispatchFailure);
    }

    [Fact]
    public void ObjectTableClearReleasesRequestAndNotifiesProjectionOwner()
    {
        var objects = CreateTable();
        using var state = new InventoryTransactionState(objects);
        int cleared = 0;
        state.ObjectTableCleared += () => cleared++;
        Assert.True(state.TryDispatch(
            InventoryRequestKind.PutInContainer,
            First,
            static () => true));

        objects.Clear();

        Assert.Equal(1, cleared);
        Assert.False(state.HasPendingRequest);
    }

    [Fact]
    public void DisposeDetachesFromBorrowedObjectTable()
    {
        var objects = CreateTable();
        var state = new InventoryTransactionState(objects);
        int completed = 0;
        state.RequestCompleted += _ => completed++;
        Assert.True(state.TryDispatch(
            InventoryRequestKind.PutInContainer,
            First,
            static () => true));

        state.Dispose();
        state.Dispose();
        objects.ApplyConfirmedServerMove(First, Player, 0u, 0);

        Assert.True(state.IsDisposed);
        Assert.Equal(0, completed);
        Assert.Throws<ObjectDisposedException>(() =>
            state.TryReserve(
                InventoryRequestKind.Pickup,
                First,
                out _));
    }

    [Fact]
    public void RejectMoveFiresRequestFailedWithLatchedKindAndWireError()
    {
        var objects = CreateTable();
        using var state = new InventoryTransactionState(objects);
        var failures = new List<(PendingInventoryRequest Request, uint Error)>();
        state.RequestFailed += (request, error) => failures.Add((request, error));

        Assert.True(state.TryDispatch(
            InventoryRequestKind.DropToWorld, First, static () => true));
        objects.RejectMove(First, 0x426u);

        (PendingInventoryRequest failed, uint error) = Assert.Single(failures);
        Assert.Equal(InventoryRequestKind.DropToWorld, failed.Kind);
        Assert.Equal(First, failed.ItemId);
        Assert.Equal(0x426u, error);
        Assert.False(state.HasPendingRequest);
    }

    // OpenAC #53: while a request is pending, a move failure resolves THAT
    // request whatever guid the wire carries; with nothing pending it is
    // ignored.
    [Fact]
    public void RequestFailedResolvesTheLatchedRequestWhateverGuidTheWireCarries()
    {
        var objects = CreateTable();
        using var state = new InventoryTransactionState(objects);
        var failures = new List<(PendingInventoryRequest Request, uint Error)>();
        state.RequestFailed += (request, error) => failures.Add((request, error));

        Assert.True(state.TryDispatch(
            InventoryRequestKind.DropToWorld, First, static () => true));

        objects.RejectMove(Second, 0x426u);
        (PendingInventoryRequest failed, uint error) = Assert.Single(failures);
        Assert.Equal(First, failed.ItemId);
        Assert.Equal(0x426u, error);
        Assert.False(state.HasPendingRequest);

        objects.RejectMove(First, 0x1Du);
        Assert.Single(failures);
        Assert.False(state.HasPendingRequest);
    }

    [Fact]
    public void RejectMoveWithNoGuidResolvesTheSoleOutstandingPendingRequest()
    {
        var objects = CreateTable();
        using var state = new InventoryTransactionState(objects);
        var failures = new List<(PendingInventoryRequest Request, uint Error)>();
        state.RequestFailed += (request, error) => failures.Add((request, error));

        Assert.True(state.TryDispatch(InventoryRequestKind.Pickup, First, static () => true));

        // Observed: InventoryServerSaveFailed guid=0x00000000 on a quest-cooldown pickup refusal.
        objects.RejectMove(0u, 0x43Eu);

        (PendingInventoryRequest failed, uint error) = Assert.Single(failures);
        Assert.Equal(First, failed.ItemId);
        Assert.Equal(0x43Eu, error);
        Assert.False(state.HasPendingRequest);

        // The gate must reopen: the next request (standing in for the next corpse's "use") must dispatch.
        Assert.True(state.TryDispatch(InventoryRequestKind.Pickup, Second, static () => true));
    }

    private static ClientObjectTable CreateTable()
    {
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = First,
            Name = "first",
            Type = ItemType.Misc,
        });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Second,
            Name = "second",
            Type = ItemType.Misc,
        });
        objects.MoveItem(First, Pack, 0);
        objects.MoveItem(Second, Pack, 1);
        return objects;
    }
}
