using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Items;
using AcDream.Runtime.Entities;

namespace AcDream.Runtime.Tests.Entities;

public sealed class RuntimeEntityObjectLifetimeTests
{
    [Fact]
    public void Owner_ConstructsOneExactDirectoryAndObjectTablePerInstance()
    {
        var first = new RuntimeEntityObjectLifetime();
        var second = new RuntimeEntityObjectLifetime();

        Assert.NotSame(first.Entities, second.Entities);
        Assert.NotSame(first.Objects, second.Objects);
        Assert.Same(first.Entities, first.Entities);
        Assert.Same(first.Objects, first.Objects);
    }

    [Fact]
    public void Owner_ConstructionLoadsNoPresentationOrBackendAssembly()
    {
        _ = new RuntimeEntityObjectLifetime();

        string[] loaded = AppDomain.CurrentDomain
            .GetAssemblies()
            .Select(assembly => assembly.GetName().Name ?? string.Empty)
            .ToArray();
        Assert.DoesNotContain(loaded, name =>
            name.StartsWith("AcDream.App", StringComparison.Ordinal)
            || name.StartsWith("AcDream.UI.", StringComparison.Ordinal)
            || name.StartsWith("Silk.NET", StringComparison.Ordinal)
            || name.StartsWith("OpenAL", StringComparison.Ordinal)
            || name.StartsWith("ImGui", StringComparison.Ordinal));
    }

    [Fact]
    public void SeparateOwners_IsolateConflictingGuidsAndContents()
    {
        const uint guid = 0x70000001u;
        var first = new RuntimeEntityObjectLifetime();
        var second = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord firstRecord = Accept(first, Spawn(guid, 1, "first"));
        RuntimeEntityRecord secondRecord = Accept(second, Spawn(guid, 9, "second"));

        Assert.True(first.ApplyAcceptedSpawn(
            firstRecord,
            firstRecord.CreateIntegrationVersion,
            firstRecord.Snapshot,
            replaceGeneration: false));
        Assert.True(second.ApplyAcceptedSpawn(
            secondRecord,
            secondRecord.CreateIntegrationVersion,
            secondRecord.Snapshot,
            replaceGeneration: false));

        Assert.Equal("first", first.Objects.Get(guid)!.Name);
        Assert.Equal("second", second.Objects.Get(guid)!.Name);
        Assert.Equal((ushort)1, firstRecord.Incarnation);
        Assert.Equal((ushort)9, secondRecord.Incarnation);
    }

    [Fact]
    public void AcceptedSpawn_RejectsWrongOrSupersededCanonicalIncarnation()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        WorldSession.EntitySpawn spawn = Spawn(0x70000002u, 3, "current");
        RuntimeEntityRecord canonical = Accept(lifetime, spawn);
        var wrong = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord wrongCanonical = Accept(wrong, spawn);

        Assert.False(lifetime.ApplyAcceptedSpawn(
            wrongCanonical,
            wrongCanonical.CreateIntegrationVersion,
            spawn,
            replaceGeneration: false));

        ulong expectedVersion = canonical.CreateIntegrationVersion;
        lifetime.Entities.AdvanceCreateAuthority(canonical);
        Assert.False(lifetime.ApplyAcceptedSpawn(
            canonical,
            expectedVersion,
            spawn,
            replaceGeneration: false));
        Assert.Null(lifetime.Objects.Get(spawn.Guid));
    }

    [Fact]
    public void AcceptedSpawn_RevalidatesAfterSynchronousObjectCallbacks()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        WorldSession.EntitySpawn spawn = Spawn(0x70000003u, 4, "reentrant");
        RuntimeEntityRecord canonical = Accept(lifetime, spawn);
        ulong expectedVersion = canonical.CreateIntegrationVersion;
        lifetime.Objects.ObjectAdded += _ =>
            lifetime.Entities.AdvanceCreateAuthority(canonical);

        Assert.False(lifetime.ApplyAcceptedSpawn(
            canonical,
            expectedVersion,
            spawn,
            replaceGeneration: false));
        Assert.NotNull(lifetime.Objects.Get(spawn.Guid));
    }

    [Fact]
    public void AcceptedDelete_RemovesOnlyMatchingLogicalGeneration()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        WorldSession.EntitySpawn spawn = Spawn(0x70000004u, 6, "retained");
        RuntimeEntityRecord canonical = Accept(lifetime, spawn);
        Assert.True(lifetime.ApplyAcceptedSpawn(
            canonical,
            canonical.CreateIntegrationVersion,
            spawn,
            replaceGeneration: true));

        Assert.False(lifetime.TryAcceptDelete(
            new DeleteObject.Parsed(spawn.Guid, 7),
            isLocalPlayer: false,
            removeRetainedObject: true,
            out _));
        Assert.NotNull(lifetime.Objects.Get(spawn.Guid));

        Assert.True(lifetime.TryAcceptDelete(
            new DeleteObject.Parsed(spawn.Guid, 6),
            isLocalPlayer: false,
            removeRetainedObject: true,
            out RuntimeEntityDeleteAcceptance accepted));
        Assert.Same(canonical, accepted.RetiredCanonical);
        Assert.False(lifetime.Entities.TryGetActive(spawn.Guid, out _));
        lifetime.CompleteAcceptedDelete(accepted);
        Assert.Null(lifetime.RetireCanonicalOnly(canonical));
        Assert.Null(lifetime.Objects.Get(spawn.Guid));
    }

    [Fact]
    public void DeleteAcceptance_CannotCrossOwnersOrReplay()
    {
        var owner = new RuntimeEntityObjectLifetime();
        var other = new RuntimeEntityObjectLifetime();
        WorldSession.EntitySpawn spawn = Spawn(0x70000006u, 8, "token");
        RuntimeEntityRecord canonical = Accept(owner, spawn);
        Assert.True(owner.ApplyAcceptedSpawn(
            canonical,
            canonical.CreateIntegrationVersion,
            spawn,
            replaceGeneration: true));
        Assert.True(owner.TryAcceptDelete(
            new DeleteObject.Parsed(spawn.Guid, spawn.InstanceSequence),
            isLocalPlayer: false,
            removeRetainedObject: true,
            out RuntimeEntityDeleteAcceptance acceptance));

        Assert.Throws<InvalidOperationException>(
            () => other.CompleteAcceptedDelete(acceptance));
        owner.CompleteAcceptedDelete(acceptance);
        Assert.Throws<InvalidOperationException>(
            () => owner.CompleteAcceptedDelete(acceptance));
        Assert.Null(owner.Objects.Get(spawn.Guid));
    }

    [Fact]
    public void ClearObjects_DoesNotResetCanonicalEntityIdentity()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        WorldSession.EntitySpawn spawn = Spawn(0x70000005u, 2, "clear");
        RuntimeEntityRecord canonical = Accept(lifetime, spawn);
        Assert.True(lifetime.ApplyAcceptedSpawn(
            canonical,
            canonical.CreateIntegrationVersion,
            spawn,
            replaceGeneration: false));

        lifetime.ClearObjects();

        Assert.Null(lifetime.Objects.Get(spawn.Guid));
        Assert.True(lifetime.Entities.TryGetActive(spawn.Guid, out var retained));
        Assert.Same(canonical, retained);
    }

    [Fact]
    public void CanonicalRegistration_IssuesIdentityBeforeAnyProjection()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        WorldSession.EntitySpawn spawn = Spawn(
            0x70000010u,
            4,
            "direct");
        RuntimeEntityRecord canonical = Accept(lifetime, spawn);

        Assert.Equal(
            RuntimeEntityDirectory.FirstLocalEntityId,
            canonical.LocalEntityId);
        Assert.True(lifetime.EntityView.TryGet(
            spawn.Guid,
            out RuntimeEntitySnapshot snapshot));
        Assert.Equal(canonical.LocalEntityId, snapshot.Identity.LocalEntityId);
        Assert.Equal(spawn.InstanceSequence, snapshot.Identity.Incarnation);
        Assert.Equal(spawn.Position!.Value.LandblockId, snapshot.CellId);
    }

    [Fact]
    public void EntityAndInventoryCommits_ShareOnePerGenerationSequence()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeGenerationToken generation = new(8);
        ulong frame = 20;
        lifetime.BindEventContext(() => generation, () => frame);
        var observer = new RecordingObserver();
        using IDisposable subscription = lifetime.Events.Subscribe(observer);
        WorldSession.EntitySpawn spawn = Spawn(
            0x70000011u,
            2,
            "ordered");
        RuntimeEntityRecord canonical =
            lifetime.RegisterEntity(spawn).Canonical!;

        Assert.True(lifetime.ApplyAcceptedSpawn(
            canonical,
            canonical.CreateIntegrationVersion,
            spawn,
            replaceGeneration: false));
        frame++;
        lifetime.Objects.MoveItem(
            spawn.Guid,
            newContainerId: 0x50000001u,
            newSlot: 3);
        _ = lifetime.RegisterEntity(spawn);

        Assert.Equal(
            [
                (RuntimeTraceKind.Entity, 1UL),
                (RuntimeTraceKind.Inventory, 2UL),
                (RuntimeTraceKind.Inventory, 3UL),
                (RuntimeTraceKind.Entity, 4UL),
            ],
            observer.Order);
        Assert.All(
            observer.Stamps,
            stamp => Assert.Equal(generation, stamp.Generation));
        Assert.Equal(20UL, observer.Stamps[0].FrameNumber);
        Assert.Equal(21UL, observer.Stamps[^1].FrameNumber);

        generation = new RuntimeGenerationToken(9);
        lifetime.ClearObjects();
        Assert.Equal(1UL, observer.Stamps[^1].Sequence);
        Assert.Equal(generation, observer.Stamps[^1].Generation);
    }

    [Fact]
    public void InventoryView_UsesCanonicalRuntimeIncarnationWithoutApp()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        WorldSession.EntitySpawn spawn = Spawn(
            0x70000012u,
            12,
            "inventory");
        RuntimeEntityRecord canonical = Accept(lifetime, spawn);
        Assert.True(lifetime.ApplyAcceptedSpawn(
            canonical,
            canonical.CreateIntegrationVersion,
            spawn,
            replaceGeneration: false));

        Assert.True(lifetime.InventoryView.TryGet(
            spawn.Guid,
            out RuntimeInventoryItemSnapshot item));
        Assert.Equal((ushort)12, item.Incarnation);
        Assert.Equal("inventory", item.Name);
    }

    [Fact]
    public void UnknownConfirmedMove_RetainsIdentityAndPlacementInStream()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        var observer = new RecordingObserver();
        using IDisposable subscription = lifetime.Events.Subscribe(observer);

        Assert.False(lifetime.Objects.ApplyConfirmedServerMove(
            0x7000DEADu,
            newContainerId: 0x50000001u,
            newWielderId: 0u,
            newSlot: 7));

        RuntimeInventoryDelta moved = Assert.Single(observer.Inventory);
        Assert.Equal(RuntimeInventoryChange.Moved, moved.Change);
        Assert.Equal(0x7000DEADu, moved.Item.ObjectId);
        Assert.Equal(0x50000001u, moved.Item.ContainerId);
        Assert.Equal(7, moved.Item.ContainerSlot);
    }

    [Fact]
    public void EventSubscriptions_AreExactAndDisposeWithoutRetention()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        var observer = new RecordingObserver();
        IDisposable subscription = lifetime.Events.Subscribe(observer);

        Assert.Equal(1, lifetime.Events.SubscriberCount);
        Assert.Throws<InvalidOperationException>(
            () => lifetime.Events.Subscribe(observer));

        subscription.Dispose();
        subscription.Dispose();

        Assert.Equal(0, lifetime.Events.SubscriberCount);
    }

    [Fact]
    public void Dispose_DetachesEveryObserverAndRejectsLaterDispatch()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        var observer = new RecordingObserver();
        _ = lifetime.Events.Subscribe(observer);

        lifetime.Dispose();
        lifetime.Dispose();

        Assert.Equal(0, lifetime.Events.SubscriberCount);
        Assert.Throws<ObjectDisposedException>(
            () => lifetime.RegisterEntity(
                Spawn(0x70000018u, 1, "disposed")));
        lifetime.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x70000019u,
            Name = "detached",
        });
        Assert.Empty(observer.Order);
    }

    [Fact]
    public void ObserverFailure_DoesNotInterruptCommitOrLaterObservers()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        var throwing = new CallbackObserver(
            onEntity: static _ => throw new InvalidOperationException("observer"));
        var recording = new RecordingObserver();
        using IDisposable first = lifetime.Events.Subscribe(throwing);
        using IDisposable second = lifetime.Events.Subscribe(recording);

        RuntimeEntityRegistrationResult result =
            lifetime.RegisterEntity(Spawn(0x70000015u, 1, "committed"));

        Assert.True(result.LogicalRegistrationCreated);
        Assert.NotNull(result.Canonical);
        Assert.True(lifetime.Entities.IsCurrent(result.Canonical));
        Assert.Single(recording.Entities);
        Assert.Equal(RuntimeEntityChange.Registered, recording.Entities[0].Change);
        Assert.Equal(1, lifetime.Events.DispatchFailureCount);
        Assert.IsType<InvalidOperationException>(
            lifetime.Events.LastDispatchFailure);
    }

    [Fact]
    public void ObserverMutationDuringDispatch_AffectsOnlyLaterCommits()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        var removed = new RecordingObserver();
        var added = new RecordingObserver();
        IDisposable? removedSubscription = null;
        IDisposable? addedSubscription = null;
        bool mutated = false;
        var mutating = new CallbackObserver(
            onEntity: _ =>
            {
                if (mutated)
                    return;
                mutated = true;
                removedSubscription!.Dispose();
                addedSubscription = lifetime.Events.Subscribe(added);
            });
        using IDisposable first = lifetime.Events.Subscribe(mutating);
        removedSubscription = lifetime.Events.Subscribe(removed);

        RuntimeEntityRegistrationResult registration =
            lifetime.RegisterEntity(Spawn(0x70000016u, 1, "first"));
        _ = lifetime.RegisterEntity(registration.Canonical!.Snapshot);

        Assert.Single(removed.Entities);
        Assert.Single(added.Entities);
        Assert.Equal(
            RuntimeEntityChange.Registered,
            removed.Entities[0].Change);
        Assert.Equal(
            RuntimeEntityChange.Updated,
            added.Entities[0].Change);

        addedSubscription!.Dispose();
        Assert.Equal(1, lifetime.Events.SubscriberCount);
    }

    [Fact]
    public void ReentrantCrossDomainCommit_PreservesSequenceForEveryObserver()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        lifetime.BindEventContext(static () => new(6), static () => 12UL);
        bool nested = false;
        var mutating = new CallbackObserver(
            onEntity: _ =>
            {
                if (nested)
                    return;
                nested = true;
                lifetime.Objects.AddOrUpdate(new ClientObject
                {
                    ObjectId = 0x71000001u,
                    Name = "nested",
                });
            });
        var recording = new RecordingObserver();
        using IDisposable first = lifetime.Events.Subscribe(mutating);
        using IDisposable second = lifetime.Events.Subscribe(recording);

        _ = lifetime.RegisterEntity(
            Spawn(0x7000001Au, 1, "outer"));

        Assert.Equal(
            [
                (RuntimeTraceKind.Entity, 1UL),
                (RuntimeTraceKind.Inventory, 2UL),
            ],
            recording.Order);
        Assert.Equal(0, lifetime.Events.PendingDispatchCount);
        Assert.False(lifetime.Events.IsDispatching);
    }

    [Fact]
    public void ObjectTransactions_PublishCommittedBorrowedViewInExactOrder()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        lifetime.BindEventContext(static () => new(7), static () => 13UL);
        const uint itemId = 0x71000002u;
        var observedViews =
            new List<RuntimeInventoryItemSnapshot>();
        var observer = new CallbackObserver(
            onInventory: delta =>
            {
                if (delta.Change
                    is RuntimeInventoryChange.Removed
                    or RuntimeInventoryChange.Cleared)
                {
                    return;
                }

                Assert.True(lifetime.InventoryView.TryGet(
                    delta.Item.ObjectId,
                    out RuntimeInventoryItemSnapshot current));
                observedViews.Add(current);
            });
        var recording = new RecordingObserver();
        using IDisposable first = lifetime.Events.Subscribe(observer);
        using IDisposable second = lifetime.Events.Subscribe(recording);

        lifetime.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = itemId,
            Name = "transaction",
            ContainerId = 0x50000001u,
            ContainerSlot = 2,
            StackSize = 3,
            Value = 30,
        });
        Assert.True(lifetime.Objects.MoveItemOptimistic(
            itemId,
            0x50000002u,
            newSlot: 0));
        Assert.True(lifetime.Objects.RollbackMove(itemId));
        Assert.True(lifetime.Objects.UpdateStackSize(
            itemId,
            stackSize: 2,
            value: 20));
        Assert.True(lifetime.Objects.RemoveLogicalGeneration(
            itemId,
            generation: 9));
        lifetime.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = itemId,
            Name = "replacement",
        });
        lifetime.ClearObjects();

        Assert.Equal(
            [
                RuntimeInventoryChange.Added,
                RuntimeInventoryChange.Moved,
                RuntimeInventoryChange.Moved,
                RuntimeInventoryChange.Updated,
                RuntimeInventoryChange.Removed,
                RuntimeInventoryChange.Added,
                RuntimeInventoryChange.Cleared,
            ],
            recording.Inventory
                .Select(delta => delta.Change)
                .ToArray());
        Assert.Equal(
            Enumerable.Range(1, 7).Select(value => (ulong)value),
            recording.Inventory.Select(delta => delta.Stamp.Sequence));
        Assert.Equal(
            (ushort)9,
            recording.Inventory[4].Item.Incarnation);
        Assert.Equal(5, observedViews.Count);
        Assert.Equal(0x50000002u, observedViews[1].ContainerId);
        Assert.Equal(0x50000001u, observedViews[2].ContainerId);
        Assert.Equal(2, observedViews[3].StackSize);
        Assert.Equal("replacement", observedViews[4].Name);
        Assert.Equal(0, lifetime.InventoryView.ObjectCount);
        Assert.Equal(0, lifetime.Events.PendingDispatchCount);
    }

    [Fact]
    public void DirectDelete_PublishesTerminalEntityBeforeInventoryRemoval()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        lifetime.BindEventContext(static () => new(4), static () => 11);
        var observer = new RecordingObserver();
        using IDisposable subscription = lifetime.Events.Subscribe(observer);
        WorldSession.EntitySpawn spawn =
            Spawn(0x70000017u, 5, "delete");
        RuntimeEntityRegistrationResult registration =
            lifetime.RegisterEntity(spawn);
        RuntimeEntityRecord canonical = registration.Canonical!;
        Assert.True(lifetime.ApplyAcceptedSpawn(
            canonical,
            canonical.CreateIntegrationVersion,
            spawn,
            replaceGeneration: true));

        Assert.True(lifetime.TryAcceptDelete(
            new DeleteObject.Parsed(spawn.Guid, spawn.InstanceSequence),
            isLocalPlayer: false,
            removeRetainedObject: true,
            out RuntimeEntityDeleteAcceptance acceptance));
        lifetime.CompleteAcceptedDelete(acceptance);
        Assert.Null(lifetime.RetireCanonicalOnly(canonical));

        Assert.Equal(
            [
                (RuntimeTraceKind.Entity, 1UL),
                (RuntimeTraceKind.Inventory, 2UL),
                (RuntimeTraceKind.Entity, 3UL),
                (RuntimeTraceKind.Inventory, 4UL),
            ],
            observer.Order);
        Assert.Equal(
            RuntimeEntityChange.Deleted,
            observer.Entities[^1].Change);
        Assert.False(lifetime.EntityView.TryGet(spawn.Guid, out _));
        Assert.False(lifetime.InventoryView.TryGet(spawn.Guid, out _));
        Assert.Equal(0, lifetime.Entities.ClaimedLocalIdCount);
    }

    [Fact]
    public void SessionClear_SealsOldGenerationAndNextStartsAtOne()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeGenerationToken generation = new(12);
        lifetime.BindEventContext(() => generation, static () => 30UL);
        var observer = new RecordingObserver();
        bool rejectedReentrantCreate = false;
        var reentrant = new CallbackObserver(
            onEntity: delta =>
            {
                if (delta.Change is not RuntimeEntityChange.Deleted)
                    return;
                rejectedReentrantCreate = Assert.Throws<InvalidOperationException>(
                    () => lifetime.RegisterEntity(
                        Spawn(delta.Entity.Identity.ServerGuid, 2, "stale")))
                    is not null;
            });
        using IDisposable first = lifetime.Events.Subscribe(reentrant);
        using IDisposable second = lifetime.Events.Subscribe(observer);
        WorldSession.EntitySpawn spawn =
            Spawn(0x70000020u, 1, "old");
        RuntimeEntityRecord canonical =
            lifetime.RegisterEntity(spawn).Canonical!;
        Assert.True(lifetime.ApplyAcceptedSpawn(
            canonical,
            canonical.CreateIntegrationVersion,
            spawn,
            replaceGeneration: true));

        IReadOnlyList<RuntimeEntityRecord> retired =
            lifetime.BeginSessionClear();
        lifetime.ClearObjects();
        Assert.Null(lifetime.RetireCanonicalOnly(Assert.Single(retired)));
        Assert.True(lifetime.CompleteSessionClearIfConverged());

        generation = new RuntimeGenerationToken(13);
        RuntimeEntityRecord replacement =
            lifetime.RegisterEntity(Spawn(spawn.Guid, 2, "new")).Canonical!;

        Assert.True(rejectedReentrantCreate);
        Assert.Equal((ushort)2, replacement.Incarnation);
        Assert.Equal(
            [
                (new RuntimeGenerationToken(12), 1UL),
                (new RuntimeGenerationToken(12), 2UL),
                (new RuntimeGenerationToken(12), 3UL),
                (new RuntimeGenerationToken(12), 4UL),
                (new RuntimeGenerationToken(13), 1UL),
            ],
            observer.Stamps
                .Select(stamp => (stamp.Generation, stamp.Sequence))
                .ToArray());
    }

    [Fact]
    public void RegisterEntity_DirectHostPublishesCompleteGenerationOrder()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        lifetime.BindEventContext(static () => new(3), static () => 9);
        var observer = new RecordingObserver();
        using IDisposable subscription = lifetime.Events.Subscribe(observer);
        const uint guid = 0x70000013u;

        RuntimeEntityRegistrationResult first =
            lifetime.RegisterEntity(Spawn(guid, 1, "first"));
        RuntimeEntityRegistrationResult refresh =
            lifetime.RegisterEntity(Spawn(guid, 1, "refresh"));
        RuntimeEntityRegistrationResult replacement =
            lifetime.RegisterEntity(Spawn(guid, 2, "replacement"));

        Assert.True(first.LogicalRegistrationCreated);
        Assert.False(refresh.LogicalRegistrationCreated);
        Assert.True(replacement.ReplacedExistingGeneration);
        Assert.Equal(
            [
                RuntimeEntityChange.Registered,
                RuntimeEntityChange.Updated,
                RuntimeEntityChange.Deleted,
                RuntimeEntityChange.Registered,
            ],
            observer.Entities.Select(delta => delta.Change).ToArray());
        Assert.Equal(
            [1UL, 2UL, 3UL, 4UL],
            observer.Entities
                .Select(delta => delta.Stamp.Sequence)
                .ToArray());
        Assert.Equal(
            (ushort)2,
            lifetime.EntityView.TryGet(guid, out RuntimeEntitySnapshot current)
                ? current.Identity.Incarnation
                : (ushort)0);
        Assert.Equal(0, lifetime.Entities.PendingTeardownCount);
    }

    [Fact]
    public void GenerationReplacement_RetainsExactReceiptBeforeTerminalCallback()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        const uint guid = 0x70000030u;
        RuntimeEntityRecord prior =
            lifetime.RegisterEntity(Spawn(guid, 1, "prior")).Canonical!;
        uint priorLocalId = prior.LocalEntityId!.Value;
        bool observedRetainedReceipt = false;
        var observer = new CallbackObserver(
            onEntity: delta =>
            {
                if (delta.Change is not RuntimeEntityChange.Deleted)
                    return;
                observedRetainedReceipt = lifetime.Entities.TryGetTeardown(
                    guid,
                    1,
                    out RuntimeEntityRecord retained)
                    && ReferenceEquals(prior, retained)
                    && lifetime.Entities.TryGetByLocalId(
                        priorLocalId,
                        out RuntimeEntityRecord identity)
                    && ReferenceEquals(prior, identity);
            });
        using IDisposable subscription = lifetime.Events.Subscribe(observer);

        RuntimeEntityRegistrationResult replacement =
            lifetime.RegisterEntity(Spawn(guid, 2, "replacement"));

        Assert.True(observedRetainedReceipt);
        Assert.Equal((ushort)2, replacement.Canonical!.Incarnation);
        Assert.Equal(0, lifetime.Entities.PendingTeardownCount);
        Assert.Equal(1, lifetime.Entities.ClaimedLocalIdCount);
        Assert.False(lifetime.Entities.TryGetByLocalId(
            priorLocalId,
            out _));
    }

    [Fact]
    public void DeleteAndSessionClear_ExposeExactReceiptsUntilAcknowledged()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        const uint deletedGuid = 0x70000031u;
        const uint clearedGuid = 0x70000032u;
        RuntimeEntityRecord deleted = lifetime.RegisterEntity(
            Spawn(deletedGuid, 1, "deleted")).Canonical!;
        RuntimeEntityRecord cleared = lifetime.RegisterEntity(
            Spawn(clearedGuid, 1, "cleared")).Canonical!;
        var retainedDuringCallbacks = new HashSet<uint>();
        var observer = new CallbackObserver(
            onEntity: delta =>
            {
                if (delta.Change
                        is RuntimeEntityChange.Deleted
                        or RuntimeEntityChange.Withdrawn
                    && lifetime.Entities.TryGetTeardown(
                        delta.Entity.Identity.ServerGuid,
                        delta.Entity.Identity.Incarnation,
                        out _))
                {
                    retainedDuringCallbacks.Add(
                        delta.Entity.Identity.ServerGuid);
                }
            });
        using IDisposable subscription = lifetime.Events.Subscribe(observer);

        Assert.True(lifetime.TryAcceptDelete(
            new DeleteObject.Parsed(deletedGuid, 1),
            isLocalPlayer: false,
            removeRetainedObject: false,
            out RuntimeEntityDeleteAcceptance acceptance));
        Assert.Contains(deletedGuid, retainedDuringCallbacks);
        Assert.Equal(1, lifetime.Entities.PendingTeardownCount);
        lifetime.CompleteAcceptedDelete(acceptance);
        Assert.Null(lifetime.RetireCanonicalOnly(deleted));

        IReadOnlyList<RuntimeEntityRecord> retired =
            lifetime.BeginSessionClear();
        Assert.Same(cleared, Assert.Single(retired));
        Assert.Contains(clearedGuid, retainedDuringCallbacks);
        Assert.Equal(1, lifetime.Entities.PendingTeardownCount);
        Assert.Null(lifetime.RetireCanonicalOnly(cleared));
        Assert.True(lifetime.CompleteSessionClearIfConverged());
        Assert.Equal(
            default(RuntimeEntityObjectOwnershipSnapshot) with
            {
                StreamSubscriberCount = 1,
            },
            lifetime.CaptureOwnership());
    }

    [Fact]
    public void RegisterEntity_ReentrantNewerGenerationSupersedesOuterCreate()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        const uint guid = 0x70000014u;
        _ = lifetime.RegisterEntity(Spawn(guid, 1, "first"));
        bool nested = false;
        var observer = new CallbackObserver(
            onEntity: delta =>
            {
                if (nested
                    || delta.Change is not RuntimeEntityChange.Deleted)
                {
                    return;
                }

                nested = true;
                _ = lifetime.RegisterEntity(Spawn(guid, 3, "nested"));
            });
        using IDisposable subscription = lifetime.Events.Subscribe(observer);

        RuntimeEntityRegistrationResult outer =
            lifetime.RegisterEntity(Spawn(guid, 2, "outer"));

        Assert.Equal(
            CreateObjectTimestampDisposition.StaleGeneration,
            outer.Inbound.Disposition);
        Assert.False(outer.LogicalRegistrationCreated);
        Assert.True(lifetime.Entities.TryGetActive(
            guid,
            out RuntimeEntityRecord current));
        Assert.Equal((ushort)3, current.Incarnation);
        Assert.NotNull(current.LocalEntityId);
    }

    [Fact]
    public void RegisterEntity_ReentrantDeleteSupersedesSameGenerationRefresh()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        const uint guid = 0x70000021u;
        RuntimeEntityRecord first =
            lifetime.RegisterEntity(Spawn(guid, 1, "first")).Canonical!;
        bool deleted = false;
        var observer = new CallbackObserver(
            onEntity: delta =>
            {
                if (deleted
                    || delta.Change is not RuntimeEntityChange.Updated)
                {
                    return;
                }

                deleted = lifetime.TryAcceptDelete(
                    new DeleteObject.Parsed(guid, 1),
                    isLocalPlayer: false,
                    removeRetainedObject: false,
                    out RuntimeEntityDeleteAcceptance acceptance);
                if (deleted)
                {
                    lifetime.CompleteAcceptedDelete(acceptance);
                    Assert.Null(lifetime.RetireCanonicalOnly(first));
                }
            });
        using IDisposable subscription = lifetime.Events.Subscribe(observer);

        RuntimeEntityRegistrationResult refresh =
            lifetime.RegisterEntity(Spawn(guid, 1, "refresh"));

        Assert.True(deleted);
        Assert.Equal(
            CreateObjectTimestampDisposition.StaleGeneration,
            refresh.Inbound.Disposition);
        Assert.Null(refresh.Canonical);
        Assert.False(lifetime.Entities.TryGetActive(guid, out _));
    }

    [Fact]
    public void RegisterEntity_ReentrantReplacementSupersedesInitialCommit()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        const uint guid = 0x70000022u;
        bool replaced = false;
        var observer = new CallbackObserver(
            onEntity: delta =>
            {
                if (replaced
                    || delta.Change is not RuntimeEntityChange.Registered
                    || delta.Entity.Identity.Incarnation != 1)
                {
                    return;
                }

                replaced = true;
                _ = lifetime.RegisterEntity(Spawn(guid, 2, "replacement"));
            });
        using IDisposable subscription = lifetime.Events.Subscribe(observer);

        RuntimeEntityRegistrationResult outer =
            lifetime.RegisterEntity(Spawn(guid, 1, "outer"));

        Assert.True(replaced);
        Assert.Equal(
            CreateObjectTimestampDisposition.StaleGeneration,
            outer.Inbound.Disposition);
        Assert.False(outer.LogicalRegistrationCreated);
        Assert.Equal(
            (ushort)2,
            Assert.IsType<RuntimeEntityRecord>(outer.Canonical).Incarnation);
    }

    [Fact]
    public void AcceptedUpdate_RevalidatesAfterReentrantTerminalObserver()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        const uint guid = 0x70000023u;
        RuntimeEntityRecord canonical =
            lifetime.RegisterEntity(Spawn(guid, 1, "vector")).Canonical!;
        bool deleted = false;
        var observer = new CallbackObserver(
            onEntity: delta =>
            {
                if (deleted
                    || delta.Change is not RuntimeEntityChange.Updated)
                {
                    return;
                }

                deleted = lifetime.TryAcceptDelete(
                    new DeleteObject.Parsed(guid, 1),
                    isLocalPlayer: false,
                    removeRetainedObject: false,
                    out RuntimeEntityDeleteAcceptance acceptance);
                if (deleted)
                {
                    lifetime.CompleteAcceptedDelete(acceptance);
                    Assert.Null(lifetime.RetireCanonicalOnly(canonical));
                }
            });
        using IDisposable subscription = lifetime.Events.Subscribe(observer);

        bool remainedCurrent = lifetime.TryApplyVector(
            new VectorUpdate.Parsed(
                guid,
                new System.Numerics.Vector3(1f, 0f, 0f),
                System.Numerics.Vector3.Zero,
                InstanceSequence: 1,
                VectorSequence: 2),
            acknowledgeProjection: null,
            out _);

        Assert.True(deleted);
        Assert.False(remainedCurrent);
        Assert.False(lifetime.Entities.TryGetActive(guid, out _));
    }

    [Fact]
    public void AcceptedVector_ProjectionFailureStillPublishesCommittedFact()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        lifetime.BindEventContext(static () => new(21), static () => 90UL);
        var observer = new RecordingObserver();
        using IDisposable subscription = lifetime.Events.Subscribe(observer);
        const uint guid = 0x70000024u;
        RuntimeEntityRecord canonical =
            lifetime.RegisterEntity(Spawn(guid, 1, "vector")).Canonical!;
        var velocity = new System.Numerics.Vector3(3f, 4f, 5f);

        Assert.Throws<InvalidOperationException>(() =>
            lifetime.TryApplyVector(
                new VectorUpdate.Parsed(
                    guid,
                    velocity,
                    System.Numerics.Vector3.Zero,
                    InstanceSequence: 1,
                    VectorSequence: 2),
                _ => throw new InvalidOperationException(
                    "projection failed after canonical commit"),
                out _));

        Assert.True(lifetime.Entities.IsCurrent(canonical));
        Assert.Equal(velocity, canonical.Snapshot.Physics!.Value.Velocity);
        Assert.Equal(
            [
                RuntimeEntityChange.Registered,
                RuntimeEntityChange.Updated,
            ],
            observer.Entities.Select(delta => delta.Change).ToArray());
        Assert.Equal([1UL, 2UL], observer.Stamps
            .Select(stamp => stamp.Sequence)
            .ToArray());
    }

    [Fact]
    public void ReentrantNewerVector_SupersedesOuterCommitPublication()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        lifetime.BindEventContext(static () => new(22), static () => 91UL);
        var observer = new RecordingObserver();
        using IDisposable subscription = lifetime.Events.Subscribe(observer);
        const uint guid = 0x70000025u;
        _ = lifetime.RegisterEntity(Spawn(guid, 1, "vector"));
        var newest = new System.Numerics.Vector3(9f, 8f, 7f);

        bool outerRemainedCurrent = lifetime.TryApplyVector(
            new VectorUpdate.Parsed(
                guid,
                new System.Numerics.Vector3(1f, 2f, 3f),
                System.Numerics.Vector3.Zero,
                InstanceSequence: 1,
                VectorSequence: 2),
            ignoredCanonical => Assert.True(lifetime.TryApplyVector(
                new VectorUpdate.Parsed(
                    guid,
                    newest,
                    System.Numerics.Vector3.Zero,
                    InstanceSequence: 1,
                    VectorSequence: 3),
                acknowledgeProjection: null,
                out _)),
            out _);

        Assert.False(outerRemainedCurrent);
        Assert.True(lifetime.Entities.TryGetActive(
            guid,
            out RuntimeEntityRecord current));
        Assert.Equal(newest, current.Snapshot.Physics!.Value.Velocity);
        Assert.Equal(
            [
                RuntimeEntityChange.Registered,
                RuntimeEntityChange.Updated,
            ],
            observer.Entities.Select(delta => delta.Change).ToArray());
        Assert.Equal(2UL, lifetime.Events.LastSequence);
    }

    [Fact]
    public void ReentrantTimestampOnlyMotion_SupersedesOuterPublication()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        lifetime.BindEventContext(static () => new(26), static () => 95UL);
        var observer = new RecordingObserver();
        using IDisposable subscription = lifetime.Events.Subscribe(observer);
        const uint guid = 0x7000002Bu;
        _ = lifetime.RegisterEntity(Spawn(guid, 1, "motion"));
        var newestMotion =
            new CreateObject.ServerMotionState(0x3D, 0x12);

        bool outerRemainedCurrent = lifetime.TryApplyMotion(
            new WorldSession.EntityMotionUpdate(
                guid,
                new CreateObject.ServerMotionState(0x3D, 0x11),
                InstanceSequence: 1,
                MovementSequence: 2,
                ServerControlSequence: 1,
                IsAutonomous: true),
            retainPayload: false,
            ignoredCanonical => Assert.True(lifetime.TryApplyMotion(
                new WorldSession.EntityMotionUpdate(
                    guid,
                    newestMotion,
                    InstanceSequence: 1,
                    MovementSequence: 3,
                    ServerControlSequence: 1,
                    IsAutonomous: true),
                retainPayload: false,
                acknowledgeProjection: null,
                out _,
                out _)),
            out _,
            out _);

        Assert.False(outerRemainedCurrent);
        Assert.True(lifetime.Entities.TryGetActive(
            guid,
            out RuntimeEntityRecord current));
        Assert.NotEqual(
            newestMotion,
            current.Snapshot.MotionState);
        Assert.Equal(
            [
                RuntimeEntityChange.Registered,
                RuntimeEntityChange.Updated,
            ],
            observer.Entities.Select(delta => delta.Change).ToArray());
        Assert.Equal(2UL, lifetime.Events.LastSequence);
    }

    [Fact]
    public void Rebucket_ProjectionFailureStillPublishesCommittedCell()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        lifetime.BindEventContext(static () => new(23), static () => 92UL);
        var observer = new RecordingObserver();
        using IDisposable subscription = lifetime.Events.Subscribe(observer);
        RuntimeEntityRecord canonical = lifetime.RegisterEntity(
            Spawn(0x70000026u, 1, "rebucket")).Canonical!;

        Assert.Throws<InvalidOperationException>(() =>
            lifetime.CommitRebucket(
                canonical,
                0x02020022u,
                0x0202FFFFu,
                _ => throw new InvalidOperationException(
                    "projection failed after spatial commit")));

        Assert.Equal(0x02020022u, canonical.FullCellId);
        RuntimeEntityDelta committed = observer.Entities[^1];
        Assert.Equal(RuntimeEntityChange.Rebucketed, committed.Change);
        Assert.Equal(0x02020022u, committed.Entity.CellId);
        Assert.Equal(2UL, committed.Stamp.Sequence);
    }

    [Fact]
    public void ReentrantChildStateMutation_SupersedesOuterPublication()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        lifetime.BindEventContext(static () => new(24), static () => 93UL);
        var observer = new RecordingObserver();
        using IDisposable subscription = lifetime.Events.Subscribe(observer);
        RuntimeEntityRecord canonical = lifetime.RegisterEntity(
            Spawn(0x70000027u, 1, "child")).Canonical!;

        bool outerRemainedCurrent = lifetime.CommitChildNoDraw(
            canonical,
            noDraw: true,
            _ => Assert.True(lifetime.CommitChildNoDraw(
                canonical,
                noDraw: false)));

        Assert.False(outerRemainedCurrent);
        Assert.Equal(
            PhysicsStateFlags.None,
            canonical.FinalPhysicsState & PhysicsStateFlags.NoDraw);
        Assert.Equal(
            [
                RuntimeEntityChange.Registered,
                RuntimeEntityChange.Updated,
            ],
            observer.Entities.Select(delta => delta.Change).ToArray());
        Assert.Equal(2UL, lifetime.Events.LastSequence);
    }

    [Fact]
    public void RejectedUpdate_ConsumesNoSharedSequence()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        lifetime.BindEventContext(static () => new(25), static () => 94UL);
        var observer = new RecordingObserver();
        using IDisposable subscription = lifetime.Events.Subscribe(observer);
        const uint guid = 0x70000028u;
        _ = lifetime.RegisterEntity(Spawn(guid, 1, "stale"));

        Assert.False(lifetime.TryApplyVector(
            new VectorUpdate.Parsed(
                guid,
                new System.Numerics.Vector3(1f, 0f, 0f),
                System.Numerics.Vector3.Zero,
                InstanceSequence: 1,
                VectorSequence: 1),
            acknowledgeProjection: null,
            out _));

        Assert.Single(observer.Entities);
        Assert.Equal(1UL, lifetime.Events.LastSequence);
    }

    [Fact]
    public void DisposeWithLiveDirectState_ConvergesCompleteOwnershipLedger()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        const uint guid = 0x70000029u;
        RuntimeEntityRecord canonical =
            lifetime.RegisterEntity(Spawn(guid, 1, "dispose")).Canonical!;
        Assert.True(lifetime.ApplyAcceptedSpawn(
            canonical,
            canonical.CreateIntegrationVersion,
            canonical.Snapshot,
            replaceGeneration: false));
        _ = lifetime.Events.Subscribe(new RecordingObserver());

        lifetime.Dispose();
        lifetime.Dispose();

        Assert.Equal(
            default(RuntimeEntityObjectOwnershipSnapshot) with
            {
                IsDisposed = true,
            },
            lifetime.CaptureOwnership());
    }

    [Fact]
    public void DisposeAfterAcceptedDelete_ConvergesUnfinishedReceiptAndRelations()
    {
        var lifetime = new RuntimeEntityObjectLifetime();
        const uint guid = 0x7000002Au;
        RuntimeEntityRecord canonical =
            lifetime.RegisterEntity(Spawn(guid, 1, "pending")).Canonical!;
        Assert.True(lifetime.ApplyAcceptedSpawn(
            canonical,
            canonical.CreateIntegrationVersion,
            canonical.Snapshot,
            replaceGeneration: false));
        lifetime.Entities.ParentAttachments.Enqueue(
            new ParentEvent.Parsed(
                ParentGuid: 0x7000F001u,
                ChildGuid: guid,
                ParentLocation: 1,
                PlacementId: 2,
                ParentInstanceSequence: 4,
                ChildPositionSequence: 3));
        Assert.True(lifetime.TryAcceptDelete(
            new DeleteObject.Parsed(guid, 1),
            isLocalPlayer: false,
            removeRetainedObject: true,
            out _));
        Assert.Equal(1, lifetime.Entities.PendingTeardownCount);
        Assert.Equal(1, lifetime.Objects.ObjectCount);

        lifetime.Dispose();

        Assert.Equal(
            default(RuntimeEntityObjectOwnershipSnapshot) with
            {
                IsDisposed = true,
            },
            lifetime.CaptureOwnership());
    }

    private static RuntimeEntityRecord Accept(
        RuntimeEntityObjectLifetime lifetime,
        WorldSession.EntitySpawn spawn)
    {
        InboundCreateResult accepted = lifetime.Entities.AcceptCreate(spawn);
        return lifetime.Entities.AddActive(accepted.Snapshot);
    }

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        ushort instance,
        string name)
    {
        var position = new CreateObject.ServerPosition(
            0x0101FFFFu,
            10f,
            20f,
            5f,
            1f,
            0f,
            0f,
            0f);
        var timestamps = new PhysicsTimestamps(
            Position: 1,
            Movement: 1,
            State: 1,
            Vector: 1,
            Teleport: 0,
            ServerControlledMove: 1,
            ForcePosition: 0,
            ObjDesc: 1,
            Instance: instance);
        var physics = new PhysicsSpawnData(
            RawState: 0x408u,
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
            name,
            null,
            null,
            0x09000001u,
            PhysicsState: 0x408u,
            InstanceSequence: instance,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    private sealed class RecordingObserver : IRuntimeEntityObjectObserver
    {
        public List<(RuntimeTraceKind Kind, ulong Sequence)> Order { get; } = [];
        public List<RuntimeEventStamp> Stamps { get; } = [];
        public List<RuntimeEntityDelta> Entities { get; } = [];
        public List<RuntimeInventoryDelta> Inventory { get; } = [];

        public void OnEntity(in RuntimeEntityDelta delta)
        {
            Order.Add((RuntimeTraceKind.Entity, delta.Stamp.Sequence));
            Stamps.Add(delta.Stamp);
            Entities.Add(delta);
        }

        public void OnInventory(in RuntimeInventoryDelta delta)
        {
            Order.Add((RuntimeTraceKind.Inventory, delta.Stamp.Sequence));
            Stamps.Add(delta.Stamp);
            Inventory.Add(delta);
        }
    }

    private sealed class CallbackObserver(
        Action<RuntimeEntityDelta>? onEntity = null,
        Action<RuntimeInventoryDelta>? onInventory = null)
        : IRuntimeEntityObjectObserver
    {
        public void OnEntity(in RuntimeEntityDelta delta) =>
            onEntity?.Invoke(delta);

        public void OnInventory(in RuntimeInventoryDelta delta) =>
            onInventory?.Invoke(delta);
    }
}
