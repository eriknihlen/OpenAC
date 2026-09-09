using System.Numerics;
using AcDream.App.Physics;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.World;

namespace AcDream.App.Tests.World;

public sealed class LiveEntityPhysicsHostOwnershipTests
{
    [Fact]
    public void Host_IsOwnedByExactRecordAndStaleRemoteReaderCannotSeeReplacement()
    {
        const uint guid = 0x70000010u;
        var runtime = Runtime();
        LiveEntityRecord first = runtime.RegisterAndMaterializeProjection(Spawn(guid, 1));
        var firstMotion = new HostConsumerMotion();
        runtime.SetRemoteMotionRuntime(guid, firstMotion);
        EntityPhysicsHost firstHost = Host(guid);

        runtime.InstallPhysicsHost(first, firstHost);
        Assert.Same(firstHost, firstMotion.Host);
        Assert.True(runtime.TryGetPhysicsHost(guid, out IPhysicsObjHost resolved));
        Assert.Same(firstHost, resolved);

        LiveEntityRecord second = runtime.RegisterAndMaterializeProjection(Spawn(guid, 2));
        var secondMotion = new HostConsumerMotion();
        runtime.SetRemoteMotionRuntime(guid, secondMotion);
        EntityPhysicsHost replacement = Host(guid);
        runtime.InstallPhysicsHost(second, replacement);

        Assert.Null(firstMotion.Host);
        Assert.Same(replacement, secondMotion.Host);
        Assert.Same(replacement, second.PhysicsHost);
    }

    [Fact]
    public void Install_RejectsWrongGuidDuplicateAndStaleRecord()
    {
        const uint guid = 0x70000011u;
        var runtime = Runtime();
        LiveEntityRecord first = runtime.RegisterAndMaterializeProjection(Spawn(guid, 1));
        EntityPhysicsHost original = Host(guid);
        runtime.InstallPhysicsHost(first, original);

        Assert.Throws<ArgumentException>(() =>
            runtime.InstallPhysicsHost(first, Host(guid + 1)));
        Assert.Throws<InvalidOperationException>(() =>
            runtime.InstallPhysicsHost(first, Host(guid)));

        LiveEntityRecord second = runtime.RegisterAndMaterializeProjection(Spawn(guid, 2));
        Assert.Throws<InvalidOperationException>(() =>
            runtime.InstallPhysicsHost(first, Host(guid)));
        Assert.Null(second.PhysicsHost);
    }

    [Fact]
    public void InstallOrRebind_PreservesHostManagersAndLiveTrackingState()
    {
        const uint watcherGuid = 0x70000020u;
        const uint targetGuid = 0x70000021u;
        var runtime = Runtime();
        LiveEntityRecord watcherRecord = runtime.RegisterAndMaterializeProjection(Spawn(watcherGuid, 1));
        LiveEntityRecord targetRecord = runtime.RegisterAndMaterializeProjection(Spawn(targetGuid, 1));
        IPhysicsObjHost? Resolve(uint id) =>
            runtime.TryGetPhysicsHost(id, out IPhysicsObjHost host)
                ? host
                : null;

        EntityPhysicsHost watcher = Host(
            watcherGuid,
            resolve: Resolve,
            position: new Vector3(1f, 0f, 0f));
        EntityPhysicsHost target = Host(
            targetGuid,
            resolve: Resolve,
            position: new Vector3(4f, 0f, 0f));
        runtime.InstallPhysicsHost(watcherRecord, watcher);
        runtime.InstallPhysicsHost(targetRecord, target);

        watcher.PositionManager.StickTo(targetGuid, 0.5f, 1f);
        watcher.PositionManager.ConstrainTo(
            new Position(0x01010001u, Vector3.Zero, Quaternion.Identity),
            1f,
            5f);
        TargetManager targetManager = watcher.TargetManager;
        PositionManager positionManager = watcher.PositionManager;
        StickyManager sticky = Assert.IsType<StickyManager>(positionManager.Sticky);
        ConstraintManager constraint = Assert.IsType<ConstraintManager>(positionManager.Constraint);
        Assert.Equal(targetGuid, sticky.TargetId);
        Assert.NotNull(watcher.TargetManager.TargetInfo);
        Assert.True(target.TargetManager.VoyeurTable!.ContainsKey(watcherGuid));

        EntityPhysicsHost configured = Host(
            watcherGuid,
            resolve: Resolve,
            position: new Vector3(9f, 0f, 0f));
        EntityPhysicsHost rebound = EntityPhysicsHostComposition.InstallOrRebind(
            runtime,
            watcherRecord,
            configured);

        Assert.Same(watcher, rebound);
        Assert.Same(targetManager, rebound.TargetManager);
        Assert.Same(positionManager, rebound.PositionManager);
        Assert.Same(sticky, rebound.PositionManager.Sticky);
        Assert.Same(constraint, rebound.PositionManager.Constraint);
        Assert.Equal(targetGuid, rebound.PositionManager.GetStickyObjectId());
        Assert.Equal(9f, rebound.Position.Frame.Origin.X);
        Assert.True(target.TargetManager.VoyeurTable!.ContainsKey(watcherGuid));

        EntityPhysicsHost secondConfiguration = Host(
            watcherGuid,
            resolve: Resolve,
            position: new Vector3(12f, 0f, 0f));
        Assert.Same(
            watcher,
            EntityPhysicsHostComposition.InstallOrRebind(runtime, watcherRecord, secondConfiguration));
        Assert.Same(targetManager, watcher.TargetManager);
        Assert.Same(positionManager, watcher.PositionManager);
        Assert.Equal(12f, watcher.Position.Frame.Origin.X);
    }

    [Fact]
    public void PreparedRebind_DoesNotPublishDelegatesUntilCommit()
    {
        const uint guid = 0x70000025u;
        var runtime = Runtime();
        LiveEntityRecord record = runtime.RegisterAndMaterializeProjection(Spawn(guid, 1));
        EntityPhysicsHost original = Host(
            guid,
            position: new Vector3(1f, 0f, 0f));
        runtime.InstallPhysicsHost(record, original);
        EntityPhysicsHost configuration = Host(
            guid,
            position: new Vector3(9f, 0f, 0f));

        EntityPhysicsHost stable = EntityPhysicsHostComposition.SelectStableHostWithoutRebind(
            runtime,
            record,
            configuration);

        Assert.Same(original, stable);
        Assert.Same(original, record.PhysicsHost);
        Assert.Equal(1f, original.Position.Frame.Origin.X);

        EntityPhysicsHost committed = EntityPhysicsHostComposition.InstallOrRebind(
            runtime,
            record,
            configuration);
        Assert.Same(original, committed);
        Assert.Equal(9f, committed.Position.Frame.Origin.X);
    }

    [Fact]
    public void SameGuidReplacement_HostCallbacksRemainIncarnationLocal()
    {
        const uint guid = 0x70000022u;
        var runtime = Runtime();
        LiveEntityRecord first = runtime.RegisterAndMaterializeProjection(Spawn(guid, 1));
        int firstUpdates = 0;
        int firstInterrupts = 0;
        var firstHost = CallbackHost(
            guid,
            _ => firstUpdates++,
            () => firstInterrupts++);
        runtime.InstallPhysicsHost(first, firstHost);

        LiveEntityRecord replacement = runtime.RegisterAndMaterializeProjection(Spawn(guid, 2));
        int replacementUpdates = 0;
        int replacementInterrupts = 0;
        var replacementHost = CallbackHost(
            guid,
            _ => replacementUpdates++,
            () => replacementInterrupts++);
        runtime.InstallPhysicsHost(replacement, replacementHost);

        firstHost.HandleUpdateTarget(default);
        firstHost.InterruptCurrentMovement();

        Assert.Equal(1, firstUpdates);
        Assert.Equal(1, firstInterrupts);
        Assert.Equal(0, replacementUpdates);
        Assert.Equal(0, replacementInterrupts);
    }

    [Fact]
    public void RemoteMotion_FullHostGateUsesCanonicalStableHost()
    {
        const uint guid = 0x70000030u;
        var runtime = Runtime();
        LiveEntityRecord record = runtime.RegisterAndMaterializeProjection(Spawn(guid, 1));
        var motion = new RemoteMotion(new PhysicsBody());
        runtime.SetRemoteMotionRuntime(guid, motion);
        EntityPhysicsHost minimal = Host(guid);
        runtime.InstallPhysicsHost(record, minimal);

        Assert.Null(motion.Host);
        motion.MarkFullPhysicsHostBound();
        Assert.Same(minimal, motion.Host);

        EntityPhysicsHost configured = Host(guid, position: new Vector3(7f, 0f, 0f));
        Assert.Same(
            minimal,
            EntityPhysicsHostComposition.InstallOrRebind(runtime, record, configured));
        Assert.Same(minimal, motion.Host);
        Assert.Equal(7f, motion.Host!.Position.Frame.Origin.X);

        runtime.SetRemoteMotionRuntime(guid, motion);
        Assert.Same(minimal, motion.Host);
    }

    [Fact]
    public void RemoteMotionBindingFailure_DoesNotPublishPartialCanonicalState()
    {
        const uint guid = 0x70000031u;
        var runtime = Runtime();
        LiveEntityRecord record = runtime.RegisterAndMaterializeProjection(Spawn(guid, 1));
        var failing = new ThrowingCellConsumerMotion();

        Assert.Throws<InvalidOperationException>(() =>
            runtime.SetRemoteMotionRuntime(guid, failing));
        Assert.Null(record.RemoteMotionRuntime);
        Assert.Null(record.PhysicsBody);
        Assert.False(runtime.TryGetRemoteMotionRuntime(guid, out _));

        runtime.SetRemoteMotionRuntime(guid, failing);
        Assert.Same(failing, record.RemoteMotionRuntime);
        Assert.Same(failing.Body, record.PhysicsBody);
    }

    [Fact]
    public void AlreadyBoundRemoteFromOldGeneration_CannotPoisonReplacement()
    {
        const uint guid = 0x70000032u;
        var runtime = Runtime();
        runtime.RegisterAndMaterializeProjection(Spawn(guid, 1));
        var stale = new RemoteMotion(new PhysicsBody());
        runtime.SetRemoteMotionRuntime(guid, stale);

        LiveEntityRecord replacement = runtime.RegisterAndMaterializeProjection(Spawn(guid, 2));
        Assert.Throws<InvalidOperationException>(() =>
            runtime.SetRemoteMotionRuntime(guid, stale));
        Assert.Null(replacement.RemoteMotionRuntime);
        Assert.Null(replacement.PhysicsBody);
        Assert.False(runtime.TryGetRemoteMotionRuntime(guid, out _));

        var fresh = new RemoteMotion(new PhysicsBody());
        runtime.SetRemoteMotionRuntime(guid, fresh);
        Assert.Same(fresh, replacement.RemoteMotionRuntime);
    }

    [Fact]
    public void SameRemoteRuntime_CannotChangeCanonicalBodyOnIdempotentBind()
    {
        const uint guid = 0x70000033u;
        var runtime = Runtime();
        LiveEntityRecord record = runtime.RegisterAndMaterializeProjection(Spawn(guid, 1));
        var firstBody = new PhysicsBody();
        var secondBody = new PhysicsBody();
        var alternating = new AlternatingBodyMotion(firstBody, secondBody);

        runtime.SetRemoteMotionRuntime(guid, alternating);
        Assert.Same(firstBody, record.PhysicsBody);

        Assert.Throws<InvalidOperationException>(() =>
            runtime.SetRemoteMotionRuntime(guid, alternating));
        Assert.Same(firstBody, record.PhysicsBody);
        Assert.Same(alternating, record.RemoteMotionRuntime);
        Assert.True(runtime.TryGetRemoteMotionRuntime(guid, out var indexed));
        Assert.Same(alternating, indexed);
    }

    [Fact]
    public void IdempotentRemoteBind_ReadsBodyOnceAndMutatesOnlyCanonicalBody()
    {
        const uint guid = 0x70000036u;
        var runtime = Runtime();
        LiveEntityRecord record = runtime.RegisterAndMaterializeProjection(Spawn(guid, 1));
        var canonical = new PhysicsBody();
        var unrelated = new PhysicsBody();
        PhysicsStateFlags unrelatedInitialState = unrelated.State;
        var scripted = new SequenceBodyMotion(canonical, canonical, unrelated);

        runtime.SetRemoteMotionRuntime(guid, scripted);
        runtime.SetRemoteMotionRuntime(guid, scripted);

        Assert.Same(canonical, record.PhysicsBody);
        Assert.Equal(record.FinalPhysicsState, canonical.State);
        Assert.Equal(unrelatedInitialState, unrelated.State);
        Assert.Equal(2, scripted.ReadCount);
    }

    [Fact]
    public void ReentrantBind_SameGuidReplacementPreventsOuterPublication()
    {
        const uint guid = 0x70000034u;
        var runtime = Runtime();
        LiveEntityRecord retired = runtime.RegisterAndMaterializeProjection(Spawn(guid, 1));
        var motion = new CallbackCanonicalMotion(() =>
            runtime.RegisterLiveEntity(Spawn(guid, 2)));

        Assert.Throws<InvalidOperationException>(() =>
            runtime.SetRemoteMotionRuntime(guid, motion));

        Assert.True(runtime.TryGetCanonical(guid, out var replacement));
        Assert.Equal((ushort)2, replacement.Incarnation);
        Assert.NotNull(replacement.LocalEntityId);
        Assert.Null(replacement.PhysicsBody);
        Assert.False(runtime.TryGetRecord(guid, out _));
        Assert.Null(retired.RemoteMotionRuntime);
        Assert.Null(retired.PhysicsBody);
        Assert.False(runtime.TryGetRemoteMotionRuntime(guid, out _));
    }

    [Fact]
    public void ReentrantBind_SessionClearPreventsOuterPublication()
    {
        const uint guid = 0x70000035u;
        var runtime = Runtime();
        LiveEntityRecord retired = runtime.RegisterAndMaterializeProjection(Spawn(guid, 1));
        var motion = new CallbackCanonicalMotion(runtime.Clear);

        Assert.Throws<InvalidOperationException>(() =>
            runtime.SetRemoteMotionRuntime(guid, motion));

        Assert.Equal(0, runtime.Count);
        Assert.Equal(0, runtime.PendingTeardownCount);
        Assert.Null(retired.RemoteMotionRuntime);
        Assert.Null(retired.PhysicsBody);
        Assert.False(runtime.TryGetRemoteMotionRuntime(guid, out _));
    }

    [Fact]
    public void Delete_TeardownHostsRemainPeerResolvableUntilExitWorldConverges()
    {
        const uint leftGuid = 0x70000040u;
        const uint rightGuid = 0x70000041u;
        LiveEntityRuntime runtime = null!;
        runtime = Runtime(record => CleanPhysicsHost(record));
        LiveEntityRecord leftRecord = runtime.RegisterAndMaterializeProjection(Spawn(leftGuid, 1));
        LiveEntityRecord rightRecord = runtime.RegisterAndMaterializeProjection(Spawn(rightGuid, 1));
        IPhysicsObjHost? Resolve(uint id) =>
            runtime.TryGetPhysicsHost(id, out IPhysicsObjHost host)
                ? host
                : null;
        EntityPhysicsHost left = Host(leftGuid, Resolve, new Vector3(0f, 0f, 0f));
        EntityPhysicsHost right = Host(rightGuid, Resolve, new Vector3(2f, 0f, 0f));
        runtime.InstallPhysicsHost(leftRecord, left);
        runtime.InstallPhysicsHost(rightRecord, right);
        left.PositionManager.StickTo(rightGuid, 0.5f, 1f);
        right.PositionManager.StickTo(leftGuid, 0.5f, 1f);

        Assert.True(runtime.UnregisterLiveEntity(
            new DeleteObject.Parsed(leftGuid, InstanceSequence: 1),
            isLocalPlayer: false));

        Assert.Null(leftRecord.PhysicsHost);
        Assert.Equal(0u, right.PositionManager.GetStickyObjectId());
        Assert.Null(right.TargetManager.TargetInfo);
        Assert.False(right.TargetManager.VoyeurTable?.ContainsKey(leftGuid) ?? false);
        Assert.False(runtime.TryGetPhysicsHost(leftGuid, out _));
    }

    [Fact]
    public void SessionClear_TeardownHostsResolveAcrossTombstones()
    {
        const uint leftGuid = 0x70000042u;
        const uint rightGuid = 0x70000043u;
        LiveEntityRuntime runtime = null!;
        runtime = Runtime(record => CleanPhysicsHost(record));
        LiveEntityRecord leftRecord = runtime.RegisterAndMaterializeProjection(Spawn(leftGuid, 1));
        LiveEntityRecord rightRecord = runtime.RegisterAndMaterializeProjection(Spawn(rightGuid, 1));
        IPhysicsObjHost? Resolve(uint id) =>
            runtime.TryGetPhysicsHost(id, out IPhysicsObjHost host)
                ? host
                : null;
        EntityPhysicsHost left = Host(leftGuid, Resolve, Vector3.Zero);
        EntityPhysicsHost right = Host(rightGuid, Resolve, new Vector3(2f, 0f, 0f));
        runtime.InstallPhysicsHost(leftRecord, left);
        runtime.InstallPhysicsHost(rightRecord, right);
        left.PositionManager.StickTo(rightGuid, 0.5f, 1f);
        right.PositionManager.StickTo(leftGuid, 0.5f, 1f);

        runtime.Clear();

        Assert.Equal(0, runtime.PendingTeardownCount);
        Assert.Null(leftRecord.PhysicsHost);
        Assert.Null(rightRecord.PhysicsHost);
        Assert.Equal(0u, left.PositionManager.GetStickyObjectId());
        Assert.Equal(0u, right.PositionManager.GetStickyObjectId());
    }

    [Fact]
    public void FailedTeardown_RetryCannotMutateReplacementRelationship()
    {
        const uint guid = 0x70000044u;
        const uint targetGuid = 0x70000045u;
        bool failOnce = true;
        EntityPhysicsHost? replacementHost = null;
        LiveEntityRecord? replacementRecord = null;
        LiveEntityRuntime runtime = null!;
        runtime = Runtime(record =>
        {
            if (failOnce)
            {
                failOnce = false;
                runtime.RegisterLiveEntity(Spawn(guid, 2));
                throw new InvalidOperationException("fixture failure before old host cleanup");
            }

            CleanPhysicsHost(record);
        });
        LiveEntityRecord oldRecord = runtime.RegisterAndMaterializeProjection(Spawn(guid, 1));
        LiveEntityRecord targetRecord = runtime.RegisterAndMaterializeProjection(Spawn(targetGuid, 1));
        IPhysicsObjHost? InitialResolve(uint id) =>
            runtime.TryGetPhysicsHost(id, out IPhysicsObjHost host)
                ? host
                : null;
        EntityPhysicsHost oldHost = Host(guid, InitialResolve);
        EntityPhysicsHost targetHost = Host(targetGuid, InitialResolve, new Vector3(2f, 0f, 0f));
        runtime.InstallPhysicsHost(oldRecord, oldHost);
        runtime.InstallPhysicsHost(targetRecord, targetHost);
        oldHost.PositionManager.StickTo(targetGuid, 0.5f, 1f);

        Assert.Throws<AggregateException>(() => runtime.UnregisterLiveEntity(
            new DeleteObject.Parsed(guid, InstanceSequence: 1),
            isLocalPlayer: false));
        replacementRecord = runtime.RegisterAndMaterializeProjection(Spawn(guid, 2));
        IPhysicsObjHost? ResolveReplacement(uint id) =>
            runtime.TryGetPhysicsHost(id, out IPhysicsObjHost host)
                ? host
                : null;
        replacementHost = Host(guid, ResolveReplacement);
        runtime.InstallPhysicsHost(replacementRecord, replacementHost);
        replacementHost.PositionManager.StickTo(targetGuid, 0.5f, 1f);
        Assert.Same(oldHost, oldRecord.PhysicsHost);
        Assert.Same(replacementHost, replacementRecord!.PhysicsHost);
        Assert.Equal(targetGuid, replacementHost!.PositionManager.GetStickyObjectId());
        Assert.True(targetHost.TargetManager.VoyeurTable!.ContainsKey(guid));

        Assert.Equal(1, runtime.RetryPendingTeardowns());
        Assert.Null(oldRecord.PhysicsHost);
        Assert.Same(replacementHost, replacementRecord.PhysicsHost);
        Assert.Equal(targetGuid, replacementHost.PositionManager.GetStickyObjectId());
        Assert.True(targetHost.TargetManager.VoyeurTable!.ContainsKey(guid));
    }

    [Fact]
    public void FailedTeardown_TombstoneCannotAcceptNewRelationshipsOutsideTeardown()
    {
        const uint retiredGuid = 0x70000046u;
        const uint watcherGuid = 0x70000047u;
        bool fail = true;
        LiveEntityRuntime runtime = null!;
        runtime = Runtime(record =>
        {
            CleanPhysicsHost(record);
            if (fail)
            {
                fail = false;
                throw new InvalidOperationException("fixture post-exit failure");
            }
        });
        LiveEntityRecord retiredRecord = runtime.RegisterAndMaterializeProjection(Spawn(retiredGuid, 1));
        LiveEntityRecord watcherRecord = runtime.RegisterAndMaterializeProjection(Spawn(watcherGuid, 1));
        IPhysicsObjHost? Resolve(uint id) =>
            runtime.TryGetPhysicsHost(id, out IPhysicsObjHost host)
                ? host
                : null;
        EntityPhysicsHost retired = Host(retiredGuid, Resolve);
        EntityPhysicsHost watcher = Host(watcherGuid, Resolve);
        runtime.InstallPhysicsHost(retiredRecord, retired);
        runtime.InstallPhysicsHost(watcherRecord, watcher);

        Assert.Throws<AggregateException>(() => runtime.UnregisterLiveEntity(
            new DeleteObject.Parsed(retiredGuid, InstanceSequence: 1),
            isLocalPlayer: false));
        watcher.SetTarget(0, retiredGuid, 0.5f, 0.5);

        Assert.Null(retired.TargetManager.VoyeurTable);
        Assert.Equal(TargetStatus.Undefined, watcher.TargetManager.TargetInfo!.Value.Status);
        Assert.False(runtime.TryGetPhysicsHost(retiredGuid, out _));
        Assert.Equal(1, runtime.RetryPendingTeardowns());
    }

    [Fact]
    public void MinimalHost_ExitPositionRetainsExactRecordFrame()
    {
        const uint guid = 0x70000048u;
        var runtime = Runtime();
        LiveEntityRecord record = runtime.RegisterAndMaterializeProjection(Spawn(guid, 1));
        record.FullCellId = 0x01010123u;
        record.WorldEntity = new AcDream.Core.World.WorldEntity
        {
            Id = 1_000_123u,
            ServerGuid = guid,
            SourceGfxObjOrSetupId = 0x02000001u,
            Position = new Vector3(12f, 34f, 56f),
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f),
            MeshRefs = Array.Empty<AcDream.Core.World.MeshRef>(),
        };
        EntityPhysicsHost host = EntityPhysicsHostComposition.CreateMinimal(record, _ => null, () => 0);

        Assert.Equal(record.FullCellId, host.Position.ObjCellId);
        Assert.Equal(record.WorldEntity.Position, host.Position.Frame.Origin);
        Assert.Equal(record.WorldEntity.Rotation, host.Position.Frame.Orientation);
    }

    [Fact]
    public void FullHost_ExitSnapshotRetainsOldFrameAcrossSameGuidReplacement()
    {
        const uint departingGuid = 0x70000049u;
        const uint watcherGuid = 0x7000004Au;
        var delivered = new List<TargetInfo>();
        LiveEntityRuntime runtime = null!;
        runtime = Runtime(record =>
        {
            if (record.ServerGuid == departingGuid)
                runtime.RegisterLiveEntity(Spawn(departingGuid, 2));
            CleanPhysicsHost(record);
        });
        Quaternion departingRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.75f);
        LiveEntityRecord departing = runtime.RegisterAndMaterializeProjection(
            Spawn(departingGuid, 1),
            localId => Entity(
                localId,
                departingGuid,
                new Vector3(12f, 34f, 56f),
                departingRotation));
        departing.FullCellId = 0x01010123u;
        WorldEntity departingEntity = Assert.IsType<WorldEntity>(departing.WorldEntity);
        LiveEntityRecord watcherRecord = runtime.RegisterAndMaterializeProjection(
            Spawn(watcherGuid, 1),
            localId => Entity(
                localId,
                watcherGuid,
                Vector3.Zero,
                Quaternion.Identity));
        IPhysicsObjHost? Resolve(uint id) =>
            runtime.TryGetPhysicsHost(id, out IPhysicsObjHost host) ? host : null;
        var body = new PhysicsBody
        {
            Position = departingEntity.Position,
            Orientation = departingRotation,
        };
        var remote = new RemoteMotion(body);
        runtime.SetRemoteMotionRuntime(departingGuid, remote);
        var departingHost = new EntityPhysicsHost(
            departingGuid,
            getPosition: () => new Position(
                departing.FullCellId,
                departing.WorldEntity?.Position ?? body.Position,
                body.Orientation),
            getVelocity: () => body.Velocity,
            getRadius: () => 0.5f,
            inContact: () => true,
            minterpMaxSpeed: () => 1f,
            curTime: () => 0,
            physicsTimerTime: () => 0,
            getObjectA: Resolve,
            handleUpdateTarget: _ => { },
            interruptCurrentMovement: () => { });
        var watcher = new EntityPhysicsHost(
            watcherGuid,
            getPosition: () => new Position(0x01010001u, Vector3.Zero, Quaternion.Identity),
            getVelocity: () => Vector3.Zero,
            getRadius: () => 0.5f,
            inContact: () => true,
            minterpMaxSpeed: () => 1f,
            curTime: () => 0,
            physicsTimerTime: () => 0,
            getObjectA: Resolve,
            handleUpdateTarget: delivered.Add,
            interruptCurrentMovement: () => { });
        runtime.InstallPhysicsHost(departing, departingHost);
        runtime.InstallPhysicsHost(watcherRecord, watcher);
        watcher.SetTarget(0, departingGuid, 0.5f, 0.5);
        delivered.Clear();

        Assert.True(runtime.UnregisterLiveEntity(
            new DeleteObject.Parsed(departingGuid, InstanceSequence: 1),
            isLocalPlayer: false));
        LiveEntityRecord replacement = runtime.RegisterAndMaterializeProjection(
            Spawn(departingGuid, 2));
        replacement.FullCellId = 0x02020202u;
        runtime.InstallPhysicsHost(
            replacement,
            Host(
                departingGuid,
                position: new Vector3(999f, 999f, 999f)));

        TargetInfo exit = Assert.Single(delivered);
        Assert.Equal(TargetStatus.ExitWorld, exit.Status);
        Assert.Equal(0x01010123u, exit.TargetPosition.ObjCellId);
        Assert.Equal(new Vector3(12f, 34f, 56f), exit.TargetPosition.Frame.Origin);
        Assert.Equal(departingRotation, exit.TargetPosition.Frame.Orientation);
        Assert.Equal(0x01010123u, remote.CellId);
    }

    [Fact]
    public void ActiveNonVisibleRecord_CanOwnResolvablePhysicsHost()
    {
        const uint guid = 0x7000004Bu;
        var runtime = Runtime();
        LiveEntityRecord record = runtime.RegisterAndMaterializeProjection(Spawn(guid, 1));
        EntityPhysicsHost host = EntityPhysicsHostComposition.CreateMinimal(record, _ => null, () => 0);
        runtime.InstallPhysicsHost(record, host);

        Assert.Empty(runtime.VisibleRecords);
        Assert.True(runtime.TryGetPhysicsHost(guid, out IPhysicsObjHost resolved));
        Assert.Same(host, resolved);
    }

    private static void CleanPhysicsHost(LiveEntityRecord record)
    {
        if (record.PhysicsHost is not EntityPhysicsHost host)
            return;
        host.PositionManager.UnStick();
        host.ClearTarget();
        host.NotifyExitWorld();
    }

    private static LiveEntityRuntime Runtime(Action<LiveEntityRecord>? teardown = null) =>
        teardown is null
            ? LiveEntityRuntimeFixture.Create(
                new GpuWorldState(),
                new DelegateLiveEntityResourceLifecycle(_ => { }, _ => { }))
            : LiveEntityRuntimeFixture.Create(
                new GpuWorldState(),
                new DelegateLiveEntityResourceLifecycle(_ => { }, _ => { }),
                teardown);

    private static WorldSession.EntitySpawn Spawn(uint guid, ushort instance) =>
        new(
            Guid: guid,
            Position: null,
            SetupTableId: null,
            AnimPartChanges: Array.Empty<CreateObject.AnimPartChange>(),
            TextureChanges: Array.Empty<CreateObject.TextureChange>(),
            SubPalettes: Array.Empty<CreateObject.SubPaletteSwap>(),
            BasePaletteId: null,
            ObjScale: null,
            Name: "fixture",
            ItemType: null,
            MotionState: null,
            MotionTableId: null,
            InstanceSequence: instance,
            Physics: new PhysicsSpawnData(
                RawState: 0u,
                Position: null,
                Movement: null,
                AnimationFrame: null,
                SetupTableId: null,
                MotionTableId: null,
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
                Timestamps: new PhysicsTimestamps(
                    Position: 0,
                    Movement: 0,
                    State: 0,
                    Vector: 0,
                    Teleport: 0,
                    ServerControlledMove: 0,
                    ForcePosition: 0,
                    ObjDesc: 0,
                    Instance: instance)));

    private static AcDream.Core.World.WorldEntity Entity(
        uint localId,
        uint guid,
        Vector3 position,
        Quaternion rotation) =>
        new()
        {
            Id = localId,
            ServerGuid = guid,
            SourceGfxObjOrSetupId = 0x02000001u,
            Position = position,
            Rotation = rotation,
            MeshRefs = Array.Empty<AcDream.Core.World.MeshRef>(),
        };

    private static EntityPhysicsHost Host(
        uint guid,
        Func<uint, IPhysicsObjHost?>? resolve = null,
        Vector3? position = null) =>
        new(
            guid,
            getPosition: () => new Position(
                0x01010001u,
                position ?? Vector3.Zero,
                Quaternion.Identity),
            getVelocity: () => Vector3.Zero,
            getRadius: () => 0.5f,
            inContact: () => true,
            minterpMaxSpeed: () => 1f,
            curTime: () => 0,
            physicsTimerTime: () => 0,
            getObjectA: resolve ?? (_ => null),
            handleUpdateTarget: _ => { },
            interruptCurrentMovement: () => { });

    private static EntityPhysicsHost CallbackHost(
        uint guid,
        Action<TargetInfo> handleUpdateTarget,
        Action interruptCurrentMovement) =>
        new(
            guid,
            getPosition: () => new Position(
                0x01010001u,
                Vector3.Zero,
                Quaternion.Identity),
            getVelocity: () => Vector3.Zero,
            getRadius: () => 0.5f,
            inContact: () => true,
            minterpMaxSpeed: () => 1f,
            curTime: () => 0,
            physicsTimerTime: () => 0,
            getObjectA: _ => null,
            handleUpdateTarget,
            interruptCurrentMovement);

    private sealed class HostConsumerMotion :
        IRuntimeRemoteMotion,
        IRuntimePhysicsHostConsumer
    {
        private Func<IPhysicsObjHost?>? _read;
        public PhysicsBody Body { get; } = new();
        public IPhysicsObjHost? Host => _read?.Invoke();
        public void BindPhysicsHost(Func<IPhysicsObjHost?> read)
        {
            if (_read is not null)
                throw new InvalidOperationException("already bound");
            _read = read;
        }
    }

    private sealed class ThrowingCellConsumerMotion :
        IRuntimeRemoteMotion,
        IRuntimeCanonicalPhysicsConsumer
    {
        private bool _failOnce = true;
        public PhysicsBody Body { get; } = new();
        public void BindCanonicalRuntime(
            Func<IPhysicsObjHost?> readPhysicsHost,
            Func<uint> readCell,
            Action<uint> writeCell)
        {
            ArgumentNullException.ThrowIfNull(readPhysicsHost);
            ArgumentNullException.ThrowIfNull(readCell);
            ArgumentNullException.ThrowIfNull(writeCell);
            if (_failOnce)
            {
                _failOnce = false;
                throw new InvalidOperationException("fixture context bind failure");
            }
        }
    }

    private sealed class AlternatingBodyMotion(
        PhysicsBody first,
        PhysicsBody second) : IRuntimeRemoteMotion
    {
        private int _reads;
        public PhysicsBody Body => _reads++ == 0 ? first : second;
    }

    private sealed class SequenceBodyMotion(params PhysicsBody[] bodies) :
        IRuntimeRemoteMotion
    {
        public int ReadCount { get; private set; }

        public PhysicsBody Body
        {
            get
            {
                int index = Math.Min(ReadCount, bodies.Length - 1);
                ReadCount++;
                return bodies[index];
            }
        }
    }

    private sealed class CallbackCanonicalMotion(Action callback) :
        IRuntimeRemoteMotion,
        IRuntimeCanonicalPhysicsConsumer
    {
        public PhysicsBody Body { get; } = new();

        public void BindCanonicalRuntime(
            Func<IPhysicsObjHost?> readPhysicsHost,
            Func<uint> readCell,
            Action<uint> writeCell)
        {
            ArgumentNullException.ThrowIfNull(readPhysicsHost);
            ArgumentNullException.ThrowIfNull(readCell);
            ArgumentNullException.ThrowIfNull(writeCell);
            callback();
        }
    }
}
