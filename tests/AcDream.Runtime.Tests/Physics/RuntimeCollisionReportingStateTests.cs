using System.Collections.Immutable;
using System.Numerics;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Tests.Physics;

public sealed class RuntimeCollisionReportingStateTests
{
    private const uint Landblock = 0xA9B40000u;
    private const uint Cell = Landblock | 0x0001u;

    [Fact]
    public void PreparedBatchDefersTrackingUntilOrderedDispatchAndDispatchesOnce()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x70001F01u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord target = Entity(
            lifetime,
            0x70001F02u,
            1,
            PhysicsStateFlags.ReportCollisions);
        target.PhysicsBody!.TransientState |= TransientStateFlags.Contact;
        RegisterDynamicShadow(lifetime, target);
        var observer = new CollisionObserver();
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);

        Assert.True(lifetime.Physics.CollisionReports.TryPrepareSetPositionBatch(
            owner,
            owner.PhysicsBody!,
            physicsTime: 10d,
            previousContact: false,
            previousOnWalkable: false,
            finalOnWalkable: false,
            collidedWithEnvironment: false,
            [target.Key!.Value.LocalEntityId],
            out var prepared));
        Assert.NotNull(prepared);
        Assert.Empty(observer.Reports);
        Assert.Equal(0, lifetime.Physics.CollisionReports.CaptureOwnership()
            .TrackedObjectCount);

        Assert.True(lifetime.Physics.CollisionReports.TryInstallSetPositionBatch(
            prepared!,
            out var receipt));
        Assert.Empty(observer.Reports);
        Assert.Equal(0, lifetime.Physics.CollisionReports.CaptureOwnership()
            .TrackedObjectCount);
        Assert.Equal(1, lifetime.Physics.CaptureOwnership()
            .PendingCollisionSetPositionDispatchCount);

        lifetime.Physics.CollisionReports.DispatchSetPositionBatch(receipt);
        Assert.Equal(2, observer.Reports.Count);
        Assert.Equal(1, lifetime.Physics.CollisionReports.CaptureOwnership()
            .TrackedObjectCount);
        Assert.False(observer.Reports[0].RecipientWasInContact);
        Assert.True(observer.Reports[0].OtherWasInContact);
        Assert.True(observer.Reports[1].RecipientWasInContact);
        Assert.False(observer.Reports[1].OtherWasInContact);
        Assert.Equal(0, lifetime.Physics.CaptureOwnership()
            .PendingCollisionSetPositionDispatchCount);
        lifetime.Physics.CollisionReports.DispatchSetPositionBatch(receipt);
        Assert.Equal(2, observer.Reports.Count);
    }

    [Fact]
    public void CombinedPhysicsLedgerIncludesPendingShadowSetPositionReceipt()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime, 0x70001F03u, 1, PhysicsStateFlags.None);
        RegisterDynamicShadow(lifetime, owner);
        ShadowObjectRegistry shadows = lifetime.Physics.Engine.ShadowObjects;
        Assert.True(shadows.TryPrepareSetPosition(
            owner.Key!.Value.LocalEntityId,
            new Vector3(13f, 12f, 7f),
            Quaternion.Identity,
            Cell,
            0f,
            0f,
            PhysicsShadowCommitAction.Replace,
            [Cell],
            provenShapeless: false,
            suspendOwner: false,
            out var prepared));
        Assert.True(shadows.TryApplySetPosition(prepared!, out var receipt));

        Assert.Equal(1, lifetime.Physics.CaptureOwnership()
            .PendingShadowSetPositionDispatchCount);

        Assert.True(shadows.DiscardSetPositionCommit(receipt));
        Assert.Equal(0, lifetime.Physics.CaptureOwnership()
            .PendingShadowSetPositionDispatchCount);
    }

    [Fact]
    public void TwoOwnerPreparedBatchesDispatchExactlyOnceInReverseOrder()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord first = Entity(
            lifetime, 0x70001E01u, 1, PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord second = Entity(
            lifetime, 0x70001E02u, 1, PhysicsStateFlags.ReportCollisions);
        const uint staticId = 0x00F01E01u;
        RegisterShadow(lifetime, staticId, PhysicsStateFlags.Static,
            isStatic: true);
        var observer = new CollisionObserver();
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);

        Assert.True(lifetime.Physics.CollisionReports.TryPrepareSetPositionBatch(
            first, first.PhysicsBody!, 10d, false, false, false, false,
            [staticId], out var firstPrepared));
        Assert.True(lifetime.Physics.CollisionReports.TryInstallSetPositionBatch(
            firstPrepared!, out var firstReceipt));
        Assert.True(lifetime.Physics.CollisionReports.TryPrepareSetPositionBatch(
            second, second.PhysicsBody!, 10d, false, false, false, false,
            [staticId], out var secondPrepared));
        Assert.True(lifetime.Physics.CollisionReports.TryInstallSetPositionBatch(
            secondPrepared!, out var secondReceipt));

        Assert.True(lifetime.Physics.CollisionReports.DispatchSetPositionBatch(
            secondReceipt));
        Assert.True(lifetime.Physics.CollisionReports.DispatchSetPositionBatch(
            firstReceipt));
        Assert.False(lifetime.Physics.CollisionReports.DispatchSetPositionBatch(
            secondReceipt));
        Assert.False(lifetime.Physics.CollisionReports.DispatchSetPositionBatch(
            firstReceipt));

        Assert.Equal(2, observer.Reports.Count);
        Assert.Equal(0, lifetime.Physics.CollisionReports.CaptureOwnership()
            .PendingSetPositionDispatchCount);
    }

    [Fact]
    public void OldBatchRetirementCannotRemoveBatchInstalledByItsCallback()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime, 0x70001E03u, 1, PhysicsStateFlags.ReportCollisions);
        const uint staticId = 0x00F01E03u;
        RegisterShadow(lifetime, staticId, PhysicsStateFlags.Static,
            isStatic: true);
        Assert.True(lifetime.Physics.CollisionReports.TryPrepareSetPositionBatch(
            owner, owner.PhysicsBody!, 10d, false, false, false, false,
            [staticId], out var oldPrepared));
        Assert.True(lifetime.Physics.CollisionReports.TryInstallSetPositionBatch(
            oldPrepared!, out var oldReceipt));
        RuntimeCollisionReportingState.SetPositionCollisionBatchReceipt
            newerReceipt = default;
        bool installedNewer = false;
        var observer = new CollisionObserver(ignoredReport =>
        {
            if (installedNewer)
                return;
            installedNewer = true;
            Assert.True(lifetime.Physics.CollisionReports
                .TryPrepareSetPositionBatch(
                    owner, owner.PhysicsBody!, 11d, false, false, false, true,
                    ImmutableArray<uint>.Empty, out var newerPrepared));
            Assert.True(lifetime.Physics.CollisionReports
                .TryInstallSetPositionBatch(newerPrepared!, out newerReceipt));
            _ = lifetime.Physics.CollisionReports.DispatchSetPositionBatch(
                newerReceipt);
            _ = ignoredReport;
        });
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);

        Assert.True(lifetime.Physics.CollisionReports.DispatchSetPositionBatch(
            oldReceipt));
        Assert.True(installedNewer);
        Assert.True(newerReceipt.IsValid);
        Assert.Equal(1, lifetime.Physics.CollisionReports.CaptureOwnership()
            .OwnerCount);

        lifetime.Physics.CollisionReports.RetireSetPositionBatchOwner(
            oldReceipt);
        Assert.Equal(1, lifetime.Physics.CollisionReports.CaptureOwnership()
            .OwnerCount);

        lifetime.Physics.CollisionReports.RetireSetPositionBatchOwner(
            newerReceipt);
        Assert.Equal(0, lifetime.Physics.CollisionReports.CaptureOwnership()
            .OwnerCount);
    }

    [Fact]
    public void PreparedBatchTracksHiddenIgnoredPeerButSuppressesCallbacks()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x70001F11u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord target = Entity(
            lifetime,
            0x70001F12u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RegisterDynamicShadow(lifetime, target);
        var observer = new CollisionObserver();
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);
        Assert.True(lifetime.Physics.CollisionReports.TryPrepareSetPositionBatch(
            owner,
            owner.PhysicsBody!,
            10d,
            previousContact: false,
            previousOnWalkable: false,
            finalOnWalkable: false,
            collidedWithEnvironment: false,
            [target.Key!.Value.LocalEntityId],
            out var prepared));
        Assert.True(lifetime.Physics.CollisionReports.TryInstallSetPositionBatch(
            prepared!,
            out var receipt));

        PhysicsStateFlags hidden = PhysicsStateFlags.Hidden
            | PhysicsStateFlags.IgnoreCollisions;
        lifetime.Entities.SetFinalPhysicsState(target, hidden);
        target.PhysicsBody!.State = hidden;
        lifetime.Physics.CollisionReports.DispatchSetPositionBatch(receipt);

        Assert.Empty(observer.Reports);
        Assert.Equal(1, lifetime.Physics.CollisionReports.CaptureOwnership()
            .TrackedObjectCount);
    }

    [Fact]
    public void PreparedBatchConvertsLiveReportAsEnvironmentAfterGroundEdge()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x70001F21u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord target = Entity(
            lifetime,
            0x70001F22u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RegisterDynamicShadow(lifetime, target);
        var observer = new CollisionObserver();
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);
        Assert.True(lifetime.Physics.CollisionReports.TryPrepareSetPositionBatch(
            owner,
            owner.PhysicsBody!,
            10d,
            previousContact: false,
            previousOnWalkable: false,
            finalOnWalkable: false,
            collidedWithEnvironment: false,
            [target.Key!.Value.LocalEntityId],
            out var prepared));
        Assert.True(lifetime.Physics.CollisionReports.TryInstallSetPositionBatch(
            prepared!,
            out var receipt));

        lifetime.Entities.SetFinalPhysicsState(
            target,
            PhysicsStateFlags.ReportAsEnvironment);
        target.PhysicsBody!.State = PhysicsStateFlags.ReportAsEnvironment;
        lifetime.Physics.CollisionReports.DispatchSetPositionBatch(receipt);

        Assert.Single(observer.Reports);
        Assert.Equal(RuntimeCollisionReportKind.EnvironmentCollision,
            observer.Reports[0].Kind);
        Assert.Equal(1, lifetime.Physics.CollisionReports.CaptureOwnership()
            .TrackedObjectCount);
    }

    [Fact]
    public void PreparedEnvironmentBatchClearsCapturedMissileWithoutReport()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x70001F31u,
            1,
            PhysicsStateFlags.Missile | PhysicsStateFlags.AlignPath);
        RegisterDynamicShadow(lifetime, owner);
        const uint staticId = 0x00F01F31u;
        RegisterShadow(
            lifetime,
            staticId,
            PhysicsStateFlags.Static,
            isStatic: true);
        var observer = new CollisionObserver();
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);
        Assert.True(lifetime.Physics.CollisionReports.TryPrepareSetPositionBatch(
            owner,
            owner.PhysicsBody!,
            10d,
            previousContact: false,
            previousOnWalkable: false,
            finalOnWalkable: false,
            collidedWithEnvironment: false,
            [staticId],
            out var prepared));
        Assert.True(lifetime.Physics.CollisionReports.TryInstallSetPositionBatch(
            prepared!,
            out var receipt));

        lifetime.Physics.CollisionReports.DispatchSetPositionBatch(receipt);

        Assert.Empty(observer.Reports);
        Assert.False(owner.FinalPhysicsState.HasFlag(PhysicsStateFlags.Missile));
        Assert.False(owner.PhysicsBody!.State.HasFlag(PhysicsStateFlags.Missile));
    }

    [Fact]
    public void PreparedExpiredEndDegradesToMissingTargetAfterInstall()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x70001F41u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord target = Entity(
            lifetime,
            0x70001F42u,
            1,
            PhysicsStateFlags.ReportCollisions);
        target.PhysicsBody!.TransientState |= TransientStateFlags.Contact;
        RegisterDynamicShadow(lifetime, target);
        var observer = new CollisionObserver();
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);
        Assert.True(lifetime.Physics.CollisionReports.TryPrepareSetPositionBatch(
            owner,
            owner.PhysicsBody!,
            10d,
            false,
            false,
            false,
            false,
            [target.Key!.Value.LocalEntityId],
            out var start));
        Assert.True(lifetime.Physics.CollisionReports.TryInstallSetPositionBatch(
            start!,
            out var startReceipt));
        lifetime.Physics.CollisionReports.DispatchSetPositionBatch(startReceipt);
        observer.Reports.Clear();

        Assert.True(lifetime.Physics.CollisionReports.TryPrepareSetPositionBatch(
            owner,
            owner.PhysicsBody!,
            11.0001d,
            false,
            false,
            false,
            false,
            ImmutableArray<uint>.Empty,
            out var end));
        Assert.True(lifetime.Physics.CollisionReports.TryInstallSetPositionBatch(
            end!,
            out var endReceipt));
        Assert.True(lifetime.TryAcceptDelete(
            new DeleteObject.Parsed(target.ServerGuid, target.Incarnation),
            isLocalPlayer: false,
            removeRetainedObject: false,
            out RuntimeEntityDeleteAcceptance acceptance));
        lifetime.CompleteAcceptedDelete(acceptance);
        Assert.Null(lifetime.RetireCanonicalOnly(target));

        lifetime.Physics.CollisionReports.DispatchSetPositionBatch(endReceipt);

        RuntimeCollisionReport report = Assert.Single(observer.Reports);
        Assert.Equal(RuntimeCollisionReportKind.ObjectCollisionEnd, report.Kind);
        Assert.Equal(owner.Key, report.Recipient);
        Assert.Equal(target.ServerGuid, report.OtherServerGuid);
        Assert.False(report.OtherWasInContact);
    }

    [Fact]
    public void PreparedBatchPreservesPreLoopEnvironmentLatchUntilFinalSuffix()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x70001F51u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord target = Entity(
            lifetime,
            0x70001F52u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RegisterDynamicShadow(lifetime, target);
        var observer = new CollisionObserver();
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);

        Assert.True(lifetime.Physics.CollisionReports.HandleReports(
            owner,
            owner.PhysicsBody!,
            9d,
            previousContact: false,
            previousOnWalkable: false,
            collidedWithEnvironment: true,
            ImmutableArray<uint>.Empty));
        observer.Reports.Clear();

        Assert.True(lifetime.Physics.CollisionReports.TryPrepareSetPositionBatch(
            owner,
            owner.PhysicsBody!,
            10d,
            previousContact: false,
            previousOnWalkable: false,
            finalOnWalkable: false,
            collidedWithEnvironment: false,
            [target.Key!.Value.LocalEntityId],
            out var prepared));
        Assert.True(lifetime.Physics.CollisionReports.TryInstallSetPositionBatch(
            prepared!,
            out var receipt));
        lifetime.Entities.SetFinalPhysicsState(
            target,
            PhysicsStateFlags.ReportAsEnvironment);
        target.PhysicsBody!.State = PhysicsStateFlags.ReportAsEnvironment;

        lifetime.Physics.CollisionReports.DispatchSetPositionBatch(receipt);

        Assert.Empty(observer.Reports);
        Assert.Equal(1, lifetime.Physics.CollisionReports.CaptureOwnership()
            .TrackedObjectCount);

        Assert.True(lifetime.Physics.CollisionReports.TryPrepareSetPositionBatch(
            owner,
            owner.PhysicsBody!,
            11d,
            previousContact: false,
            previousOnWalkable: false,
            finalOnWalkable: false,
            collidedWithEnvironment: true,
            ImmutableArray<uint>.Empty,
            out var next));
        Assert.True(lifetime.Physics.CollisionReports.TryInstallSetPositionBatch(
            next!,
            out var nextReceipt));
        lifetime.Physics.CollisionReports.DispatchSetPositionBatch(nextReceipt);

        RuntimeCollisionReport report = Assert.Single(observer.Reports);
        Assert.Equal(RuntimeCollisionReportKind.EnvironmentCollision,
            report.Kind);
    }

    [Fact]
    public void PreparedEnvironmentBatchStopsMissileAddedByCallback()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x70001F61u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RegisterDynamicShadow(lifetime, owner);
        const uint staticId = 0x00F01F61u;
        RegisterShadow(
            lifetime,
            staticId,
            PhysicsStateFlags.Static,
            isStatic: true);
        var observer = new CollisionObserver(_ =>
        {
            PhysicsStateFlags callbackState = owner.PhysicsBody!.State
                | PhysicsStateFlags.Missile
                | PhysicsStateFlags.AlignPath;
            lifetime.Entities.SetFinalPhysicsState(owner, callbackState);
            owner.PhysicsBody.State = callbackState;
        });
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);
        Assert.True(lifetime.Physics.CollisionReports.TryPrepareSetPositionBatch(
            owner,
            owner.PhysicsBody!,
            10d,
            previousContact: false,
            previousOnWalkable: false,
            finalOnWalkable: false,
            collidedWithEnvironment: false,
            [staticId],
            out var prepared));
        Assert.True(lifetime.Physics.CollisionReports.TryInstallSetPositionBatch(
            prepared!,
            out var receipt));

        lifetime.Physics.CollisionReports.DispatchSetPositionBatch(receipt);

        Assert.Single(observer.Reports);
        Assert.False(owner.FinalPhysicsState.HasFlag(PhysicsStateFlags.Missile));
        Assert.False(owner.PhysicsBody!.State.HasFlag(PhysicsStateFlags.Missile));
    }

    [Fact]
    public void PreparedExistingCollisionRefreshesLiveEtherealBeforeExpiry()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x70001F71u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord target = Entity(
            lifetime,
            0x70001F72u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RegisterDynamicShadow(lifetime, target);
        var observer = new CollisionObserver();
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);

        DispatchPrepared(
            lifetime,
            owner,
            physicsTime: 10d,
            [target.Key!.Value.LocalEntityId]);
        observer.Reports.Clear();
        PhysicsStateFlags ethereal = PhysicsStateFlags.ReportCollisions
            | PhysicsStateFlags.Ethereal;
        lifetime.Entities.SetFinalPhysicsState(target, ethereal);
        target.PhysicsBody!.State = ethereal;

        DispatchPrepared(
            lifetime,
            owner,
            physicsTime: 10.5d,
            [target.Key!.Value.LocalEntityId]);
        Assert.Empty(observer.Reports);
        DispatchPrepared(
            lifetime,
            owner,
            physicsTime: 10.5001d,
            ImmutableArray<uint>.Empty);

        Assert.Equal(2, observer.Reports.Count);
        Assert.All(observer.Reports, report => Assert.Equal(
            RuntimeCollisionReportKind.ObjectCollisionEnd,
            report.Kind));
    }

    [Fact]
    public void PreparedExistingCollisionBecomingStaticKeepsOldAgeForSamePassEnd()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x70001F81u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord target = Entity(
            lifetime,
            0x70001F82u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RegisterDynamicShadow(lifetime, target);
        var observer = new CollisionObserver();
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);
        DispatchPrepared(
            lifetime,
            owner,
            physicsTime: 10d,
            [target.Key!.Value.LocalEntityId]);
        observer.Reports.Clear();

        Assert.True(lifetime.Physics.CollisionReports.TryPrepareSetPositionBatch(
            owner,
            owner.PhysicsBody!,
            11.0001d,
            previousContact: false,
            previousOnWalkable: false,
            finalOnWalkable: false,
            collidedWithEnvironment: false,
            [target.Key!.Value.LocalEntityId],
            out var prepared));
        Assert.True(lifetime.Physics.CollisionReports.TryInstallSetPositionBatch(
            prepared!,
            out var receipt));
        PhysicsStateFlags becameStatic = PhysicsStateFlags.ReportCollisions
            | PhysicsStateFlags.Static;
        lifetime.Entities.SetFinalPhysicsState(target, becameStatic);
        target.PhysicsBody!.State = becameStatic;

        lifetime.Physics.CollisionReports.DispatchSetPositionBatch(receipt);

        Assert.Equal(3, observer.Reports.Count);
        Assert.Equal(RuntimeCollisionReportKind.EnvironmentCollision,
            observer.Reports[0].Kind);
        Assert.Equal(RuntimeCollisionReportKind.ObjectCollisionEnd,
            observer.Reports[1].Kind);
        Assert.Equal(RuntimeCollisionReportKind.ObjectCollisionEnd,
            observer.Reports[2].Kind);
        Assert.Equal(0, lifetime.Physics.CollisionReports.CaptureOwnership()
            .TrackedObjectCount);
    }

    [Fact]
    public void ExactObjectReportsAreOrderedDeduplicatedAndExpireAtRetailThreshold()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x70002001u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord first = Entity(
            lifetime,
            0x70002002u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord second = Entity(
            lifetime,
            0x70002003u,
            1,
            PhysicsStateFlags.None);
        RegisterDynamicShadow(lifetime, first);
        RegisterDynamicShadow(lifetime, second);
        var observer = new CollisionObserver();
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);

        Assert.True(Handle(
            lifetime,
            owner,
            time: 10d,
            Collisions(
                first.Key!.Value.LocalEntityId,
                first.Key.Value.LocalEntityId,
                second.Key!.Value.LocalEntityId)));

        Assert.Collection(
            observer.Reports,
            report => AssertReport(
                report,
                RuntimeCollisionReportKind.ObjectCollision,
                owner,
                first),
            report => AssertReport(
                report,
                RuntimeCollisionReportKind.ObjectCollision,
                first,
                owner),
            report => AssertReport(
                report,
                RuntimeCollisionReportKind.ObjectCollision,
                owner,
                second));
        RuntimeCollisionReportingOwnershipSnapshot tracked = lifetime.Physics
            .CollisionReports.CaptureOwnership();
        Assert.Equal(1, tracked.OwnerCount);
        Assert.Equal(2, tracked.TrackedObjectCount);
        Assert.Equal(2, tracked.ReversePeerCount);

        observer.Reports.Clear();
        Assert.False(Handle(lifetime, owner, time: 11d, Collisions()));
        Assert.Empty(observer.Reports);
        Assert.Equal(2, lifetime.Physics.CollisionReports.CaptureOwnership()
            .TrackedObjectCount);

        Assert.False(Handle(lifetime, owner, time: 11.0001d, Collisions()));
        Assert.Collection(
            observer.Reports,
            report => AssertReport(
                report,
                RuntimeCollisionReportKind.ObjectCollisionEnd,
                owner,
                first),
            report => AssertReport(
                report,
                RuntimeCollisionReportKind.ObjectCollisionEnd,
                first,
                owner),
            report => AssertReport(
                report,
                RuntimeCollisionReportKind.ObjectCollisionEnd,
                owner,
                second));
        RuntimeCollisionReportingOwnershipSnapshot ended = lifetime.Physics
            .CollisionReports.CaptureOwnership();
        Assert.Equal(0, ended.OwnerCount);
        Assert.Equal(0, ended.TrackedObjectCount);
        Assert.Equal(0, ended.ReversePeerCount);
    }

    [Fact]
    public void NoReportParticipantsTrackWithoutTurningPresenceIntoSuccess()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x70002010u,
            1,
            PhysicsStateFlags.None);
        RuntimeEntityRecord target = Entity(
            lifetime,
            0x70002011u,
            1,
            PhysicsStateFlags.None);
        RegisterDynamicShadow(lifetime, target);

        Assert.False(Handle(
            lifetime,
            owner,
            time: 3d,
            Collisions(target.Key!.Value.LocalEntityId)));
        RuntimeCollisionReportingOwnershipSnapshot ownership = lifetime.Physics
            .CollisionReports.CaptureOwnership();
        Assert.Equal(1, ownership.OwnerCount);
        Assert.Equal(1, ownership.TrackedObjectCount);
    }

    [Fact]
    public void ExactStaticAndReportAsEnvironmentObjectsUseEnvironmentLatch()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x70002020u,
            1,
            PhysicsStateFlags.ReportCollisions);
        uint staticId = 0x00F00001u;
        RegisterShadow(
            lifetime,
            staticId,
            PhysicsStateFlags.None,
            isStatic: true);
        RuntimeEntityRecord environmentObject = Entity(
            lifetime,
            0x70002021u,
            1,
            PhysicsStateFlags.ReportAsEnvironment);
        RegisterDynamicShadow(lifetime, environmentObject);
        var observer = new CollisionObserver();
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);

        Assert.True(Handle(
            lifetime,
            owner,
            time: 1d,
            Collisions(
                staticId,
                environmentObject.Key!.Value.LocalEntityId,
                environmentCollision: true)));
        Assert.Single(observer.Reports);
        AssertReport(
            observer.Reports[0],
            RuntimeCollisionReportKind.EnvironmentCollision,
            owner,
            other: null);
        Assert.Equal(1, lifetime.Physics.CollisionReports.CaptureOwnership()
            .TrackedObjectCount);

        observer.Reports.Clear();
        Assert.False(Handle(
            lifetime,
            owner,
            time: 1.1d,
            Collisions(staticId, environmentCollision: true)));
        Assert.Empty(observer.Reports);

        Assert.False(Handle(lifetime, owner, time: 1.2d, Collisions()));
        Assert.True(Handle(
            lifetime,
            owner,
            time: 1.3d,
            Collisions(staticId, environmentCollision: true)));
        Assert.Single(observer.Reports);
    }

    [Fact]
    public void UnknownObjectAndNonFiniteClockFailClosedWithoutTracking()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x70002030u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord target = Entity(
            lifetime,
            0x70002031u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RegisterDynamicShadow(lifetime, target);

        Assert.False(Handle(
            lifetime,
            owner,
            time: double.NaN,
            Collisions(target.Key!.Value.LocalEntityId)));
        Assert.False(Handle(
            lifetime,
            owner,
            time: 2d,
            Collisions(0x00ABCDEFu)));
        RuntimeCollisionReportingOwnershipSnapshot ownership = lifetime.Physics
            .CollisionReports.CaptureOwnership();
        Assert.Equal(0, ownership.OwnerCount);
        Assert.Equal(0, ownership.TrackedObjectCount);
    }

    [Fact]
    public void EtherealContactExpiresAfterItsTouchedQuantum()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x70002040u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord target = Entity(
            lifetime,
            0x70002041u,
            1,
            PhysicsStateFlags.ReportCollisions | PhysicsStateFlags.Ethereal);
        RegisterDynamicShadow(lifetime, target);

        Assert.True(Handle(
            lifetime,
            owner,
            time: 4d,
            Collisions(target.Key!.Value.LocalEntityId)));
        Assert.False(Handle(lifetime, owner, time: 4d, Collisions()));
        Assert.Equal(1, lifetime.Physics.CollisionReports.CaptureOwnership()
            .TrackedObjectCount);
        Assert.False(Handle(lifetime, owner, time: 4.0001d, Collisions()));
        Assert.Equal(0, lifetime.Physics.CollisionReports.CaptureOwnership()
            .TrackedObjectCount);
    }

    [Fact]
    public void IgnoreAsymmetryAndMissileStopFollowRetailStateReads()
    {
        using var lifetime = Lifetime();
        PhysicsStateFlags missile = PhysicsStateFlags.ReportCollisions
            | PhysicsStateFlags.Missile
            | PhysicsStateFlags.AlignPath
            | PhysicsStateFlags.PathClipped;
        RuntimeEntityRecord owner = Entity(
            lifetime, 0x70002042u, 1, missile);
        RuntimeEntityRecord ignored = Entity(
            lifetime,
            0x70002043u,
            1,
            PhysicsStateFlags.ReportCollisions
                | PhysicsStateFlags.IgnoreCollisions);
        RegisterDynamicShadow(lifetime, owner);
        RegisterDynamicShadow(lifetime, ignored);
        var observer = new CollisionObserver();
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);

        Assert.True(Handle(
            lifetime,
            owner,
            4d,
            Collisions(ignored.Key!.Value.LocalEntityId)));
        RuntimeCollisionReport reciprocal = Assert.Single(observer.Reports);
        Assert.Equal(ignored.Key, reciprocal.Recipient);
        Assert.True(owner.FinalPhysicsState.HasFlag(PhysicsStateFlags.Missile));

        RuntimeEntityRecord ordinary = Entity(
            lifetime, 0x70002044u, 1, PhysicsStateFlags.None);
        RegisterDynamicShadow(lifetime, ordinary);
        ulong mutation = owner.PhysicsStateMutationVersion;
        Assert.True(Handle(
            lifetime,
            owner,
            4.1d,
            Collisions(ordinary.Key!.Value.LocalEntityId)));
        Assert.Equal(mutation + 1UL, owner.PhysicsStateMutationVersion);
        PhysicsStateFlags cleared = PhysicsStateFlags.Missile
            | PhysicsStateFlags.AlignPath
            | PhysicsStateFlags.PathClipped;
        Assert.Equal(0u, (uint)(owner.FinalPhysicsState & cleared));
        Assert.Equal(owner.FinalPhysicsState, owner.PhysicsBody!.State);
        Assert.True(lifetime.Physics.Engine.ShadowObjects
            .TryGetCollisionOwner(
                owner.Key!.Value.LocalEntityId,
                out uint shadowState,
                out _));
        Assert.Equal((uint)owner.FinalPhysicsState, shadowState);

        RuntimeEntityRecord sourceIgnoring = Entity(
            lifetime,
            0x70002045u,
            1,
            PhysicsStateFlags.ReportCollisions
                | PhysicsStateFlags.IgnoreCollisions);
        RuntimeEntityRecord reportingTarget = Entity(
            lifetime,
            0x70002046u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RegisterDynamicShadow(lifetime, reportingTarget);
        observer.Reports.Clear();
        Assert.True(Handle(
            lifetime,
            sourceIgnoring,
            4.2d,
            Collisions(reportingTarget.Key!.Value.LocalEntityId)));
        RuntimeCollisionReport sourceOnly = Assert.Single(observer.Reports);
        Assert.Equal(sourceIgnoring.Key, sourceOnly.Recipient);
    }

    [Fact]
    public void ObjectReportDoesNotClearMissileAddedBySourceCallback()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x70002093u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord target = Entity(
            lifetime, 0x70002094u, 1, PhysicsStateFlags.None);
        RegisterDynamicShadow(lifetime, target);
        bool added = false;
        var observer = new CollisionObserver(report =>
        {
            if (added
                || report.Kind is not RuntimeCollisionReportKind.ObjectCollision
                || report.Recipient != owner.Key)
            {
                return;
            }
            PhysicsStateFlags state = PhysicsStateFlags.ReportCollisions
                | PhysicsStateFlags.Missile
                | PhysicsStateFlags.AlignPath
                | PhysicsStateFlags.PathClipped;
            added = lifetime.TryApplyState(
                new SetState.Parsed(
                    owner.ServerGuid,
                    (uint)state,
                    owner.Incarnation,
                    StateSequence: 2),
                acknowledgeProjection: null,
                out _,
                out _);
        });
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);

        Assert.True(Handle(
            lifetime,
            owner,
            4d,
            Collisions(target.Key!.Value.LocalEntityId)));

        Assert.True(added);
        PhysicsStateFlags retained = owner.FinalPhysicsState;
        Assert.NotEqual(0u, (uint)(retained & PhysicsStateFlags.Missile));
        Assert.NotEqual(0u, (uint)(retained & PhysicsStateFlags.AlignPath));
        Assert.NotEqual(0u, (uint)(retained & PhysicsStateFlags.PathClipped));
    }

    [Fact]
    public void PreCallbackMissileGateClearsCurrentPathBitsAfterCallback()
    {
        using var lifetime = Lifetime();
        PhysicsStateFlags initial = PhysicsStateFlags.ReportCollisions
            | PhysicsStateFlags.Missile
            | PhysicsStateFlags.AlignPath
            | PhysicsStateFlags.PathClipped;
        RuntimeEntityRecord owner = Entity(
            lifetime, 0x70002095u, 1, initial);
        RuntimeEntityRecord target = Entity(
            lifetime, 0x70002096u, 1, PhysicsStateFlags.None);
        RegisterDynamicShadow(lifetime, owner);
        RegisterDynamicShadow(lifetime, target);
        bool changed = false;
        var observer = new CollisionObserver(report =>
        {
            if (changed
                || report.Kind is not RuntimeCollisionReportKind.ObjectCollision
                || report.Recipient != owner.Key)
            {
                return;
            }
            PhysicsStateFlags callbackState =
                PhysicsStateFlags.ReportCollisions
                | PhysicsStateFlags.AlignPath
                | PhysicsStateFlags.PathClipped;
            changed = lifetime.TryApplyState(
                new SetState.Parsed(
                    owner.ServerGuid,
                    (uint)callbackState,
                    owner.Incarnation,
                    StateSequence: 2),
                acknowledgeProjection: null,
                out _,
                out _);
        });
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);

        Assert.True(Handle(
            lifetime,
            owner,
            4d,
            Collisions(target.Key!.Value.LocalEntityId)));

        Assert.True(changed);
        const PhysicsStateFlags stopped = PhysicsStateFlags.Missile
            | PhysicsStateFlags.AlignPath
            | PhysicsStateFlags.PathClipped;
        Assert.Equal(0u, (uint)(owner.FinalPhysicsState & stopped));
        Assert.Equal(owner.FinalPhysicsState, owner.PhysicsBody!.State);
        Assert.True(lifetime.Physics.Engine.ShadowObjects
            .TryGetCollisionOwner(
                owner.Key!.Value.LocalEntityId,
                out uint shadowState,
                out _));
        Assert.Equal((uint)owner.FinalPhysicsState, shadowState);
    }

    [Fact]
    public void CanonicalDynamicStateOverridesStaleShadowClassification()
    {
        using var lifetime = Lifetime();
        PhysicsStateFlags missile = PhysicsStateFlags.ReportCollisions
            | PhysicsStateFlags.Missile
            | PhysicsStateFlags.AlignPath
            | PhysicsStateFlags.PathClipped;
        RuntimeEntityRecord owner = Entity(
            lifetime, 0x7000208Bu, 1, missile);
        RuntimeEntityRecord ignored = Entity(
            lifetime, 0x7000208Cu, 1, PhysicsStateFlags.None);
        RegisterDynamicShadow(lifetime, ignored);
        ignored.PhysicsBody!.State = PhysicsStateFlags.IgnoreCollisions;
        lifetime.Entities.SetFinalPhysicsState(
            ignored,
            PhysicsStateFlags.IgnoreCollisions);
        var observer = new CollisionObserver();
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);

        Assert.False(Handle(
            lifetime,
            owner,
            4d,
            Collisions(ignored.Key!.Value.LocalEntityId)));
        Assert.Empty(observer.Reports);
        Assert.True(owner.FinalPhysicsState.HasFlag(PhysicsStateFlags.Missile));

        RuntimeEntityRecord environmentOwner = Entity(
            lifetime,
            0x7000208Du,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord newlyEnvironment = Entity(
            lifetime, 0x7000208Eu, 1, PhysicsStateFlags.None);
        RegisterDynamicShadow(lifetime, newlyEnvironment);
        newlyEnvironment.PhysicsBody!.State =
            PhysicsStateFlags.ReportAsEnvironment;
        lifetime.Entities.SetFinalPhysicsState(
            newlyEnvironment,
            PhysicsStateFlags.ReportAsEnvironment);
        observer.Reports.Clear();
        Assert.True(Handle(
            lifetime,
            environmentOwner,
            4.1d,
            Collisions(newlyEnvironment.Key!.Value.LocalEntityId)));
        Assert.Equal(
            RuntimeCollisionReportKind.EnvironmentCollision,
            Assert.Single(observer.Reports).Kind);

        RuntimeEntityRecord ordinaryOwner = Entity(
            lifetime,
            0x7000208Fu,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord noLongerEnvironment = Entity(
            lifetime,
            0x70002090u,
            1,
            PhysicsStateFlags.Static
                | PhysicsStateFlags.ReportAsEnvironment);
        RegisterDynamicShadow(lifetime, noLongerEnvironment);
        noLongerEnvironment.PhysicsBody!.State = PhysicsStateFlags.None;
        lifetime.Entities.SetFinalPhysicsState(
            noLongerEnvironment,
            PhysicsStateFlags.None);
        observer.Reports.Clear();
        Assert.True(Handle(
            lifetime,
            ordinaryOwner,
            4.2d,
            Collisions(noLongerEnvironment.Key!.Value.LocalEntityId)));
        Assert.Equal(
            RuntimeCollisionReportKind.ObjectCollision,
            Assert.Single(observer.Reports).Kind);

        RuntimeEntityRecord shadowOnlyOwner = Entity(
            lifetime,
            0x70002091u,
            1,
            PhysicsStateFlags.ReportCollisions);
        const uint shadowOnlyId = 0x00F02091u;
        RegisterShadow(
            lifetime,
            shadowOnlyId,
            PhysicsStateFlags.ReportAsEnvironment,
            isStatic: false);
        observer.Reports.Clear();
        Assert.False(Handle(
            lifetime,
            shadowOnlyOwner,
            4.3d,
            Collisions(shadowOnlyId)));
        Assert.Empty(observer.Reports);
    }

    [Fact]
    public void RegisteredStaticAndUnobservedEnvironmentUseExactLatch()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x70002047u,
            1,
            PhysicsStateFlags.ReportCollisions);
        const uint rawStaticId = 0x00F00047u;
        RegisterShadow(
            lifetime,
            rawStaticId,
            PhysicsStateFlags.Static,
            isStatic: true);
        var observer = new CollisionObserver();
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);

        Assert.True(Handle(
            lifetime,
            owner,
            1d,
            Collisions(rawStaticId)));
        Assert.Equal(
            RuntimeCollisionReportKind.EnvironmentCollision,
            Assert.Single(observer.Reports).Kind);

        RuntimeEntityRecord unobserved = Entity(
            lifetime, 0x70002092u, 1, PhysicsStateFlags.None);
        Assert.False(Handle(
            lifetime,
            unobserved,
            1.1d,
            Collisions([], environmentCollision: true)));
        Assert.Equal(1, lifetime.Physics.CollisionReports.CaptureOwnership()
            .OwnerCount);
        Assert.False(Handle(
            lifetime,
            unobserved,
            1.2d,
            Collisions([], environmentCollision: true)));
        Assert.False(Handle(lifetime, unobserved, 1.3d, Collisions()));
        Assert.Equal(0, lifetime.Physics.CollisionReports.CaptureOwnership()
            .OwnerCount);
    }

    [Fact]
    public void ReportAsEnvironmentSuppressesBothObjectEndCallbacks()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x70002048u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord target = Entity(
            lifetime,
            0x70002049u,
            1,
            PhysicsStateFlags.ReportCollisions
                | PhysicsStateFlags.ReportAsEnvironment);
        RegisterDynamicShadow(lifetime, target);
        var observer = new CollisionObserver();
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);

        Assert.True(Handle(
            lifetime,
            owner,
            1d,
            Collisions(target.Key!.Value.LocalEntityId)));
        observer.Reports.Clear();
        Assert.False(Handle(lifetime, owner, 2.0001d, Collisions()));
        Assert.Empty(observer.Reports);
        Assert.Equal(0, lifetime.Physics.CollisionReports.CaptureOwnership()
            .TrackedObjectCount);
    }

    [Fact]
    public void NestedReportsDrainFifoAndObserverFailureDoesNotCorruptQueue()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord firstOwner = Entity(
            lifetime,
            0x7000204Au,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord firstTarget = Entity(
            lifetime, 0x7000204Bu, 1, PhysicsStateFlags.None);
        RuntimeEntityRecord secondOwner = Entity(
            lifetime,
            0x7000204Cu,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord secondTarget = Entity(
            lifetime, 0x7000204Du, 1, PhysicsStateFlags.None);
        RegisterDynamicShadow(lifetime, firstTarget);
        RegisterDynamicShadow(lifetime, secondTarget);
        bool nested = false;
        var firstObserver = new CollisionObserver(_ =>
        {
            if (nested)
                return;
            nested = true;
            Assert.True(Handle(
                lifetime,
                secondOwner,
                2d,
                Collisions(secondTarget.Key!.Value.LocalEntityId)));
        });
        var secondObserver = new CollisionObserver();
        var failing = new ThrowingCollisionObserver();
        using IDisposable firstSubscription = lifetime.Physics.CollisionReports
            .Subscribe(firstObserver);
        using IDisposable failingSubscription = lifetime.Physics.CollisionReports
            .Subscribe(failing);
        using IDisposable secondSubscription = lifetime.Physics.CollisionReports
            .Subscribe(secondObserver);
        Assert.Throws<InvalidOperationException>(() =>
            lifetime.Physics.CollisionReports.Subscribe(secondObserver));

        Assert.True(Handle(
            lifetime,
            firstOwner,
            1d,
            Collisions(firstTarget.Key!.Value.LocalEntityId)));
        Assert.Collection(
            secondObserver.Reports,
            report => Assert.Equal(firstOwner.Key, report.Recipient),
            report => Assert.Equal(secondOwner.Key, report.Recipient));
        RuntimeCollisionReportingOwnershipSnapshot ownership = lifetime.Physics
            .CollisionReports.CaptureOwnership();
        Assert.Equal(2, ownership.DispatchFailureCount);
        Assert.Equal(0, ownership.PendingReportCount);
        Assert.False(ownership.IsDispatching);
    }

    [Fact]
    public void ReentrantResetDropsRemainingPriorEpochReports()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x7000204Eu,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord first = Entity(
            lifetime, 0x7000204Fu, 1, PhysicsStateFlags.None);
        RuntimeEntityRecord second = Entity(
            lifetime, 0x70002054u, 1, PhysicsStateFlags.None);
        RegisterDynamicShadow(lifetime, first);
        RegisterDynamicShadow(lifetime, second);
        Assert.True(Handle(
            lifetime,
            owner,
            1d,
            Collisions(
                first.Key!.Value.LocalEntityId,
                second.Key!.Value.LocalEntityId)));
        bool reset = false;
        var observer = new CollisionObserver(_ =>
        {
            if (reset)
                return;
            reset = true;
            lifetime.Physics.CollisionReports.ResetSession();
        });
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);

        lifetime.Physics.CollisionReports.LeaveWorld(owner);

        Assert.True(reset);
        Assert.Single(observer.Reports);
        RuntimeCollisionReportingOwnershipSnapshot ownership = lifetime.Physics
            .CollisionReports.CaptureOwnership();
        Assert.Equal(0, ownership.OwnerCount);
        Assert.Equal(0, ownership.PendingReportCount);
        Assert.False(ownership.IsDispatching);
    }

    [Fact]
    public void ReentrantTargetDeletionCannotDeliverSecondOrRetainStaleContact()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x70002050u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord target = Entity(
            lifetime,
            0x70002051u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RegisterDynamicShadow(lifetime, target);
        bool deleted = false;
        var observer = new CollisionObserver(report =>
        {
            if (deleted
                || report.Kind is not RuntimeCollisionReportKind.ObjectCollision
                || report.Recipient != owner.Key)
            {
                return;
            }
            deleted = true;
            Assert.True(lifetime.TryAcceptDelete(
                new DeleteObject.Parsed(target.ServerGuid, target.Incarnation),
                isLocalPlayer: false,
                removeRetainedObject: false,
                out RuntimeEntityDeleteAcceptance acceptance));
            lifetime.CompleteAcceptedDelete(acceptance);
            Assert.Null(lifetime.RetireCanonicalOnly(target));
        });
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);

        Assert.True(Handle(
            lifetime,
            owner,
            time: 5d,
            Collisions(target.Key!.Value.LocalEntityId)));
        Assert.True(deleted);
        Assert.Single(observer.Reports);
        RuntimeCollisionReportingOwnershipSnapshot ownership = lifetime.Physics
            .CollisionReports.CaptureOwnership();
        Assert.Equal(1, ownership.OwnerCount);
        Assert.Equal(1, ownership.TrackedObjectCount);
        Assert.Equal(1, ownership.ReversePeerCount);

        Assert.False(Handle(lifetime, owner, time: 6.0001d, Collisions()));
        Assert.Equal(2, observer.Reports.Count);
        RuntimeCollisionReport ended = observer.Reports[1];
        Assert.Equal(RuntimeCollisionReportKind.ObjectCollisionEnd, ended.Kind);
        Assert.Equal(target.ServerGuid, ended.OtherServerGuid);
        Assert.Equal(0, lifetime.Physics.CollisionReports.CaptureOwnership()
            .TrackedObjectCount);
    }

    [Fact]
    public void DeleteAndGuidReuseNeverTransferTrackedIncarnation()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x70002060u,
            1,
            PhysicsStateFlags.ReportCollisions);
        const uint reusedGuid = 0x70002061u;
        RuntimeEntityRecord retired = Entity(
            lifetime,
            reusedGuid,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityKey retiredKey = retired.Key!.Value;
        RegisterDynamicShadow(lifetime, retired);
        Assert.True(Handle(
            lifetime,
            owner,
            time: 6d,
            Collisions(retired.Key!.Value.LocalEntityId)));

        Assert.True(lifetime.TryAcceptDelete(
            new DeleteObject.Parsed(reusedGuid, 1),
            isLocalPlayer: false,
            removeRetainedObject: false,
            out RuntimeEntityDeleteAcceptance acceptance));
        lifetime.CompleteAcceptedDelete(acceptance);
        Assert.Null(lifetime.RetireCanonicalOnly(retired));
        Assert.Equal(1, lifetime.Physics.CollisionReports.CaptureOwnership()
            .TrackedObjectCount);

        RuntimeEntityRecord replacement = Entity(
            lifetime,
            reusedGuid,
            2,
            PhysicsStateFlags.ReportCollisions);
        RegisterDynamicShadow(lifetime, replacement);
        Assert.NotEqual(retiredKey, replacement.Key);
        Assert.True(Handle(
            lifetime,
            owner,
            time: 6.1d,
            Collisions(replacement.Key!.Value.LocalEntityId)));
        Assert.Equal(2, lifetime.Physics.CollisionReports.CaptureOwnership()
            .TrackedObjectCount);

        Assert.False(Handle(
            lifetime,
            owner,
            time: 7.0001d,
            Collisions(replacement.Key.Value.LocalEntityId)));
        Assert.Equal(1, lifetime.Physics.CollisionReports.CaptureOwnership()
            .TrackedObjectCount);
    }

    [Fact]
    public void HiddenLeaveWorldRejectsReentrantContactReaddition()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x70002068u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord target = Entity(
            lifetime, 0x70002069u, 1, PhysicsStateFlags.None);
        RegisterDynamicShadow(lifetime, target);
        Assert.True(Handle(
            lifetime,
            owner,
            1d,
            Collisions(target.Key!.Value.LocalEntityId)));
        bool readdResult = true;
        var observer = new CollisionObserver(report =>
        {
            if (report.Kind is RuntimeCollisionReportKind.ObjectCollisionEnd)
            {
                readdResult = Handle(
                    lifetime,
                    owner,
                    1.1d,
                    Collisions(target.Key!.Value.LocalEntityId));
            }
        });
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);

        Assert.True(lifetime.TryApplyState(
            new SetState.Parsed(
                owner.ServerGuid,
                (uint)(PhysicsStateFlags.ReportCollisions
                    | PhysicsStateFlags.Hidden),
                owner.Incarnation,
                StateSequence: 2),
            acknowledgeProjection: null,
            out _,
            out RetailPhysicsStateTransition transition));
        Assert.Equal(
            RetailHiddenTransition.BecameHidden,
            transition.HiddenTransition);
        Assert.False(readdResult);
        RuntimeCollisionReportingOwnershipSnapshot ownership = lifetime.Physics
            .CollisionReports.CaptureOwnership();
        Assert.Equal(0, ownership.TrackedObjectCount);
        Assert.Equal(0, ownership.LeavingOwnerCount);
    }

    [Fact]
    public void FirstForceEndCallbackDeletingOwnerStopsRemainingSourceSuffix()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x70002082u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord first = Entity(
            lifetime, 0x70002083u, 1, PhysicsStateFlags.None);
        RuntimeEntityRecord second = Entity(
            lifetime, 0x70002084u, 1, PhysicsStateFlags.None);
        RegisterDynamicShadow(lifetime, first);
        RegisterDynamicShadow(lifetime, second);
        Assert.True(Handle(
            lifetime,
            owner,
            1d,
            Collisions(
                first.Key!.Value.LocalEntityId,
                second.Key!.Value.LocalEntityId)));
        bool accepted = false;
        var observer = new CollisionObserver(report =>
        {
            if (accepted
                || report.Kind is not RuntimeCollisionReportKind
                    .ObjectCollisionEnd
                || report.Recipient != owner.Key)
            {
                return;
            }
            accepted = true;
            Assert.True(lifetime.TryAcceptDelete(
                new DeleteObject.Parsed(owner.ServerGuid, owner.Incarnation),
                isLocalPlayer: false,
                removeRetainedObject: false,
                out _));
        });
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);

        lifetime.Physics.CollisionReports.LeaveWorld(owner);

        Assert.True(accepted);
        RuntimeCollisionReport report = Assert.Single(observer.Reports);
        Assert.Equal(first.Key, report.Other);
        Assert.Equal(0, lifetime.Physics.CollisionReports.CaptureOwnership()
            .TrackedObjectCount);
    }

    [Fact]
    public void SessionClearForceEndsBeforeTerminalCollisionReset()
    {
        var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x7000206Au,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord target = Entity(
            lifetime,
            0x7000206Bu,
            1,
            PhysicsStateFlags.ReportCollisions);
        RegisterDynamicShadow(lifetime, target);
        var observer = new CollisionObserver();
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);
        Assert.True(Handle(
            lifetime,
            owner,
            1d,
            Collisions(target.Key!.Value.LocalEntityId)));
        observer.Reports.Clear();

        IReadOnlyList<RuntimeEntityRecord> retiring = lifetime.BeginSessionClear();
        Assert.Collection(
            observer.Reports,
            report => Assert.Equal(
                RuntimeCollisionReportKind.ObjectCollisionEnd,
                report.Kind),
            report => Assert.Equal(
                RuntimeCollisionReportKind.ObjectCollisionEnd,
                report.Kind));
        Assert.Equal(0, lifetime.Physics.CollisionReports.CaptureOwnership()
            .TrackedObjectCount);
        foreach (RuntimeEntityRecord record in retiring)
            lifetime.CompleteSessionEntityRetirement(record);
        Assert.True(lifetime.CompleteSessionClearIfConverged());
        lifetime.Dispose();
    }

    [Fact]
    public void SessionClearBlocksCrossOwnerReadditionForWholeBatch()
    {
        var lifetime = Lifetime();
        RuntimeEntityRecord firstOwner = Entity(
            lifetime,
            0x7000207Au,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord peer = Entity(
            lifetime, 0x7000207Bu, 1, PhysicsStateFlags.None);
        RuntimeEntityRecord laterOwner = Entity(
            lifetime,
            0x7000207Cu,
            1,
            PhysicsStateFlags.ReportCollisions);
        RegisterDynamicShadow(lifetime, peer);
        Assert.True(Handle(
            lifetime,
            firstOwner,
            1d,
            Collisions(peer.Key!.Value.LocalEntityId)));
        Assert.True(Handle(
            lifetime,
            laterOwner,
            1d,
            Collisions(peer.Key.Value.LocalEntityId)));
        bool attempted = false;
        bool readded = true;
        var observer = new CollisionObserver(report =>
        {
            if (attempted || report.Recipient != laterOwner.Key)
                return;
            attempted = true;
            readded = Handle(
                lifetime,
                firstOwner,
                1.1d,
                Collisions(peer.Key.Value.LocalEntityId));
        });
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);

        IReadOnlyList<RuntimeEntityRecord> retiring = lifetime.BeginSessionClear();

        Assert.True(attempted);
        Assert.False(readded);
        RuntimeCollisionReportingOwnershipSnapshot ownership = lifetime.Physics
            .CollisionReports.CaptureOwnership();
        Assert.Equal(0, ownership.OwnerCount);
        Assert.Equal(0, ownership.AdmissionBlockedOwnerCount);
        foreach (RuntimeEntityRecord record in retiring)
            lifetime.CompleteSessionEntityRetirement(record);
        Assert.True(lifetime.CompleteSessionClearIfConverged());
        lifetime.Dispose();
    }

    [Fact]
    public void SessionClearCallbackDeletingLaterBlockedOwnerStillForceEndsIt()
    {
        var lifetime = Lifetime();
        RuntimeEntityRecord firstOwner = Entity(
            lifetime,
            0x70002085u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord firstPeer = Entity(
            lifetime, 0x70002086u, 1, PhysicsStateFlags.None);
        RuntimeEntityRecord laterOwner = Entity(
            lifetime,
            0x70002087u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord laterPeer = Entity(
            lifetime, 0x70002088u, 1, PhysicsStateFlags.None);
        RegisterDynamicShadow(lifetime, firstPeer);
        RegisterDynamicShadow(lifetime, laterPeer);
        Assert.True(Handle(
            lifetime,
            firstOwner,
            1d,
            Collisions(firstPeer.Key!.Value.LocalEntityId)));
        Assert.True(Handle(
            lifetime,
            laterOwner,
            1d,
            Collisions(laterPeer.Key!.Value.LocalEntityId)));
        bool accepted = false;
        var observer = new CollisionObserver(report =>
        {
            if (accepted
                || report.Kind is not RuntimeCollisionReportKind
                    .ObjectCollisionEnd
                || report.Recipient != firstOwner.Key)
            {
                return;
            }
            accepted = true;
            Assert.True(lifetime.TryAcceptDelete(
                new DeleteObject.Parsed(
                    laterOwner.ServerGuid,
                    laterOwner.Incarnation),
                isLocalPlayer: false,
                removeRetainedObject: false,
                out _));
        });
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);

        IReadOnlyList<RuntimeEntityRecord> retiring = lifetime.BeginSessionClear();

        Assert.True(accepted);
        Assert.Contains(
            observer.Reports,
            report => report.Kind
                    is RuntimeCollisionReportKind.ObjectCollisionEnd
                && report.Recipient == laterOwner.Key
                && report.Other == laterPeer.Key);
        RuntimeCollisionReportingOwnershipSnapshot ownership = lifetime.Physics
            .CollisionReports.CaptureOwnership();
        Assert.Equal(0, ownership.OwnerCount);
        Assert.Equal(0, ownership.AdmissionBlockedOwnerCount);
        foreach (RuntimeEntityRecord record in retiring)
            lifetime.CompleteSessionEntityRetirement(record);
        Assert.True(lifetime.CompleteSessionClearIfConverged());
        lifetime.Dispose();
    }

    [Fact]
    public void InvalidSetPositionMapsNewReportThenRepeatToRetailErrors()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x7000206Cu,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord target = Entity(
            lifetime, 0x7000206Du, 1, PhysicsStateFlags.None);
        RegisterDynamicShadow(lifetime, target);
        uint targetId = target.Key!.Value.LocalEntityId;
        lifetime.Physics.Engine.TransitionCellCollisionTestHook =
            (transition, phase, _, _) =>
            {
                if (phase is TransitionCellCollisionPhase.Environment)
                {
                    transition.CollisionInfo.CollideObjectGuids.Add(targetId);
                    transition.CollisionInfo.LastCollidedObjectGuid = targetId;
                }
                return TransitionState.Collided;
            };

        RuntimeSetPositionCommand command = PlacementCommand(
            owner,
            new Vector3(13f, 12f, 7f));
        RuntimeSetPositionOutcome first = lifetime.Physics.SetPosition.Apply(
            owner,
            owner.PositionAuthorityVersion,
            command);
        Assert.Equal(RuntimeSetPositionStatus.Rejected, first.Status);
        Assert.Equal(PhysicsSetPositionError.Collided, first.Error);
        Assert.True(lifetime.Physics.SetPosition.TryGetAwaitingPreparationToken(
            owner,
            out RuntimeEntityPlacementToken retry));

        RuntimeSetPositionOutcome repeated = lifetime.Physics.SetPosition
            .SubmitPreparedPlacement(retry, command);
        Assert.Equal(RuntimeSetPositionStatus.Rejected, repeated.Status);
        Assert.Equal(PhysicsSetPositionError.NoValidPosition, repeated.Error);
    }

    [Fact]
    public void SuccessfulSetPositionReportsBeforeResponseAndShadowReflood()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x7000206Eu,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord target = Entity(
            lifetime, 0x7000206Fu, 1, PhysicsStateFlags.None);
        RegisterDynamicShadow(lifetime, owner);
        RegisterDynamicShadow(lifetime, target);
        uint targetId = target.Key!.Value.LocalEntityId;
        Vector3 initial = owner.PhysicsBody!.Position;
        Vector3 destination = new(14f, 12f, 7f);
        owner.PhysicsBody.set_velocity(-Vector3.UnitX);
        owner.PhysicsBody.FramesStationaryFall = 2;
        owner.PhysicsBody.TransientState |=
            TransientStateFlags.StationaryStop;
        lifetime.Physics.Engine.TransitionCellCollisionTestHook =
            (transition, phase, _, _) =>
            {
                if (phase is TransitionCellCollisionPhase.Environment)
                {
                    transition.CollisionInfo.CollideObjectGuids.Add(targetId);
                    transition.CollisionInfo.LastCollidedObjectGuid = targetId;
                    transition.CollisionInfo.SetCollisionNormal(Vector3.UnitX);
                    transition.CollisionInfo.FramesStationaryFall = 1;
                }
                return TransitionState.OK;
            };
        bool observed = false;
        var observer = new CollisionObserver(report =>
        {
            if (report.Kind is not RuntimeCollisionReportKind.ObjectCollision
                || report.Recipient != owner.Key)
            {
                return;
            }
            observed = true;
            Assert.Equal(destination, owner.PhysicsBody.Position);
            Assert.Equal(-Vector3.UnitX, owner.PhysicsBody.Velocity);
            Assert.Equal(2, owner.PhysicsBody.FramesStationaryFall);
            Assert.NotEqual(
                0u,
                (uint)(owner.PhysicsBody.TransientState
                    & TransientStateFlags.StationaryStop));
            Assert.Equal(
                0u,
                (uint)(owner.PhysicsBody.TransientState
                    & TransientStateFlags.StationaryFall));
            ShadowEntry shadow = Assert.Single(
                lifetime.Physics.Engine.ShadowObjects.AllEntriesForDebug(),
                entry => entry.EntityId == owner.Key!.Value.LocalEntityId);
            Assert.Equal(initial, shadow.Position);
        });
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);

        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition.Apply(
            owner,
            owner.PositionAuthorityVersion,
            PlacementCommand(owner, destination));

        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            outcome.Status);
        Assert.True(observed);
        ShadowEntry committedShadow = Assert.Single(
            lifetime.Physics.Engine.ShadowObjects.AllEntriesForDebug(),
            entry => entry.EntityId == owner.Key!.Value.LocalEntityId);
        Assert.Equal(destination, committedShadow.Position);
        Assert.True(owner.PhysicsBody.Velocity.X >= 0f);
        Assert.Equal(1, owner.PhysicsBody.FramesStationaryFall);
        Assert.NotEqual(
            0u,
            (uint)(owner.PhysicsBody.TransientState
                & TransientStateFlags.StationaryFall));
        Assert.Equal(
            0u,
            (uint)(owner.PhysicsBody.TransientState
                & TransientStateFlags.StationaryStop));
    }

    [Fact]
    public void ReportSurvivesNewVectorWhileStalePhysicalResponseDoesNot()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x70002072u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord target = Entity(
            lifetime, 0x70002073u, 1, PhysicsStateFlags.None);
        RegisterDynamicShadow(lifetime, owner);
        RegisterDynamicShadow(lifetime, target);
        uint targetId = target.Key!.Value.LocalEntityId;
        lifetime.Physics.Engine.TransitionCellCollisionTestHook =
            (transition, phase, _, _) =>
            {
                if (phase is TransitionCellCollisionPhase.Environment)
                {
                    transition.CollisionInfo.CollideObjectGuids.Add(targetId);
                    transition.CollisionInfo.SetCollisionNormal(Vector3.UnitX);
                }
                return TransitionState.OK;
            };
        bool observed = false;
        var observer = new CollisionObserver(report =>
        {
            if (observed || report.Recipient != owner.Key)
                return;
            observed = true;
            lifetime.Entities.AdvanceVectorAuthority(owner);
            owner.PhysicsBody!.set_velocity(new Vector3(-7f, 3f, 0f));
        });
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);
        RuntimeSetPositionCommand command = PlacementCommand(
            owner,
            new Vector3(15f, 12f, 7f)) with
        {
            ExpectedVelocityAuthorityVersion = owner.VelocityAuthorityVersion,
        };

        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition.Apply(
            owner,
            owner.PositionAuthorityVersion,
            command);

        Assert.True(observed);
        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            outcome.Status);
        Assert.Equal(new Vector3(-7f, 3f, 0f), owner.PhysicsBody!.Velocity);
    }

    [Fact]
    public void HitGroundHiddenTransitionCommitsPlacementWithoutResumingCollisionTracking()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x70002074u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord target = Entity(
            lifetime, 0x70002075u, 1, PhysicsStateFlags.None);
        RegisterDynamicShadow(lifetime, owner);
        RegisterDynamicShadow(lifetime, target);
        uint targetId = target.Key!.Value.LocalEntityId;
        lifetime.Physics.Engine.TransitionCellCollisionTestHook =
            (transition, phase, _, _) =>
            {
                if (phase is TransitionCellCollisionPhase.Environment)
                {
                    transition.CollisionInfo.SetContactPlane(
                        new Plane(Vector3.UnitZ, 0f),
                        Cell);
                    transition.CollisionInfo.CollideObjectGuids.Add(targetId);
                }
                return TransitionState.OK;
            };
        bool hidden = false;
        var remote = new ReentrantRemotePlacement(owner.PhysicsBody!)
        {
            CellId = Cell,
            OnHitGround = () =>
            {
                hidden = lifetime.TryApplyState(
                    new SetState.Parsed(
                        owner.ServerGuid,
                        (uint)(PhysicsStateFlags.ReportCollisions
                            | PhysicsStateFlags.Hidden),
                        owner.Incarnation,
                        StateSequence: 2),
                    acknowledgeProjection: null,
                    out _,
                    out _);
            },
        };
        lifetime.Entities.SetRemoteMotion(owner, remote);
        var observer = new CollisionObserver();
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);

        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition.Apply(
            owner,
            owner.PositionAuthorityVersion,
            PlacementCommand(owner, new Vector3(16f, 12f, 7f)));

        Assert.True(hidden);
        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            outcome.Status);
        Assert.True(lifetime.Physics.SetPosition.TryPeekProjection(
            out RuntimePlacementProjectionSnapshot projection));
        Assert.True(owner.FinalPhysicsState.HasFlag(PhysicsStateFlags.Hidden));
        Assert.Equal(Cell, owner.FullCellId);
        Assert.Equal(projection.WorldPosition, owner.PhysicsBody!.Position);
        Assert.True(owner.PhysicsBody.InWorld);
        Assert.Empty(observer.Reports);
        Assert.Equal(0, lifetime.Physics.CollisionReports.CaptureOwnership()
            .TrackedObjectCount);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            outcome.Projection));
    }

    [Fact]
    public void HitGroundHiddenPeerCannotBecomeAStaleTrackedTarget()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x70002076u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord target = Entity(
            lifetime,
            0x70002077u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RegisterDynamicShadow(lifetime, owner);
        RegisterDynamicShadow(lifetime, target);
        uint targetId = target.Key!.Value.LocalEntityId;
        lifetime.Physics.Engine.TransitionCellCollisionTestHook =
            (transition, phase, _, _) =>
            {
                if (phase is TransitionCellCollisionPhase.Environment)
                {
                    transition.CollisionInfo.SetContactPlane(
                        new Plane(Vector3.UnitZ, 0f),
                        Cell);
                    transition.CollisionInfo.CollideObjectGuids.Add(targetId);
                }
                return TransitionState.OK;
            };
        var remote = new ReentrantRemotePlacement(owner.PhysicsBody!)
        {
            CellId = Cell,
            OnHitGround = () => Assert.True(lifetime.TryApplyState(
                new SetState.Parsed(
                    target.ServerGuid,
                    (uint)(PhysicsStateFlags.ReportCollisions
                        | PhysicsStateFlags.Hidden),
                    target.Incarnation,
                    StateSequence: 2),
                acknowledgeProjection: null,
                out _,
                out _)),
        };
        lifetime.Entities.SetRemoteMotion(owner, remote);
        var observer = new CollisionObserver();
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);

        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition.Apply(
            owner,
            owner.PositionAuthorityVersion,
            PlacementCommand(owner, new Vector3(17f, 12f, 7f)));

        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            outcome.Status);
        Assert.True(target.FinalPhysicsState.HasFlag(PhysicsStateFlags.Hidden));
        Assert.DoesNotContain(
            observer.Reports,
            report => report.Kind
                is RuntimeCollisionReportKind.ObjectCollision);
        Assert.Equal(0, lifetime.Physics.CollisionReports.CaptureOwnership()
            .TrackedObjectCount);
    }

    [Fact]
    public void CollisionReportHiddenTransitionPreservesCommittedPlacement()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x70002078u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord target = Entity(
            lifetime, 0x70002079u, 1, PhysicsStateFlags.None);
        RegisterDynamicShadow(lifetime, owner);
        RegisterDynamicShadow(lifetime, target);
        uint targetId = target.Key!.Value.LocalEntityId;
        lifetime.Physics.Engine.TransitionCellCollisionTestHook =
            (transition, phase, _, _) =>
            {
                if (phase is TransitionCellCollisionPhase.Environment)
                {
                    transition.CollisionInfo.CollideObjectGuids.Add(targetId);
                    transition.CollisionInfo.SetCollisionNormal(Vector3.UnitX);
                }
                return TransitionState.OK;
            };
        bool hidden = false;
        var observer = new CollisionObserver(report =>
        {
            if (hidden
                || report.Kind is not RuntimeCollisionReportKind.ObjectCollision)
            {
                return;
            }
            hidden = lifetime.TryApplyState(
                new SetState.Parsed(
                    owner.ServerGuid,
                    (uint)(PhysicsStateFlags.ReportCollisions
                        | PhysicsStateFlags.Hidden),
                    owner.Incarnation,
                    StateSequence: 2),
                acknowledgeProjection: null,
                out _,
                out _);
        });
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);

        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition.Apply(
            owner,
            owner.PositionAuthorityVersion,
            PlacementCommand(owner, new Vector3(18f, 12f, 7f)));

        Assert.True(hidden);
        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            outcome.Status);
        Assert.True(owner.FinalPhysicsState.HasFlag(PhysicsStateFlags.Hidden));
        Assert.Equal(0, lifetime.Physics.CollisionReports.CaptureOwnership()
            .TrackedObjectCount);
        Assert.Equal(Cell, owner.FullCellId);
        Assert.Equal(new Vector3(18f, 12f, 7f), owner.PhysicsBody!.Position);
        ShadowObjectRegistry shadows = lifetime.Physics.Engine.ShadowObjects;
        uint ownerId = owner.Key!.Value.LocalEntityId;
        ShadowEntry hiddenShadow = Assert.Single(
            shadows.AllEntriesForDebug(),
            entry => entry.EntityId == ownerId);
        Assert.Equal(owner.PhysicsBody.Position, hiddenShadow.Position);
        Assert.Equal(0, shadows.SuspendedRegistrationCount);
        Assert.True(shadows.TryGetCollisionOwner(
            ownerId,
            out uint hiddenShadowState,
            out _));
        Assert.Equal((uint)owner.FinalPhysicsState, hiddenShadowState);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            outcome.Projection));

        Assert.True(lifetime.TryApplyState(
            new SetState.Parsed(
                owner.ServerGuid,
                (uint)PhysicsStateFlags.ReportCollisions,
                owner.Incarnation,
                StateSequence: 3),
            acknowledgeProjection: null,
            out _,
            out _));
        Assert.Equal(0, shadows.SuspendedRegistrationCount);
        ShadowEntry restored = Assert.Single(
            shadows.AllEntriesForDebug(),
            entry => entry.EntityId == ownerId);
        Assert.Equal(owner.PhysicsBody.Position, restored.Position);
        Assert.Equal((uint)owner.FinalPhysicsState, restored.State);
    }

    [Fact]
    public void PreparedCollisionBatchStopsAfterCallbackHidesOwner()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x7000207Eu,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord first = Entity(
            lifetime, 0x7000207Fu, 1, PhysicsStateFlags.None);
        RuntimeEntityRecord second = Entity(
            lifetime, 0x70002080u, 1, PhysicsStateFlags.None);
        RegisterDynamicShadow(lifetime, owner);
        RegisterDynamicShadow(lifetime, first);
        RegisterDynamicShadow(lifetime, second);
        bool hidden = false;
        var observer = new CollisionObserver(report =>
        {
            if (hidden
                || report.Kind is not RuntimeCollisionReportKind.ObjectCollision
                || report.Recipient != owner.Key)
            {
                return;
            }
            hidden = lifetime.TryApplyState(
                new SetState.Parsed(
                    owner.ServerGuid,
                    (uint)(PhysicsStateFlags.ReportCollisions
                        | PhysicsStateFlags.Hidden),
                    owner.Incarnation,
                    StateSequence: 2),
                acknowledgeProjection: null,
                out _,
                out _);
        });
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);
        Assert.True(lifetime.Physics.CollisionReports.TryPrepareSetPositionBatch(
            owner,
            owner.PhysicsBody!,
            physicsTime: 12d,
            previousContact: false,
            previousOnWalkable: false,
            finalOnWalkable: false,
            collidedWithEnvironment: false,
            [
                first.Key!.Value.LocalEntityId,
                second.Key!.Value.LocalEntityId,
            ],
            out var prepared));
        Assert.True(lifetime.Physics.CollisionReports.TryInstallSetPositionBatch(
            prepared!, out var receipt));

        SetPositionCollisionBatchDispatchResult result = lifetime.Physics
            .CollisionReports.DispatchSetPositionBatchResult(receipt);

        Assert.True(hidden);
        Assert.Equal(SetPositionCollisionBatchDispatchStatus.Completed,
            result.Status);
        RuntimeCollisionReport report = Assert.Single(
            observer.Reports,
            candidate => candidate.Kind
                is RuntimeCollisionReportKind.ObjectCollision);
        Assert.Equal(first.Key, report.Other);
        RuntimeCollisionReportingOwnershipSnapshot ownership = lifetime.Physics
            .CollisionReports.CaptureOwnership();
        Assert.Equal(0, ownership.OwnerCount);
        Assert.Equal(0, ownership.TrackedObjectCount);
        Assert.Equal(0, ownership.ReversePeerCount);
        Assert.Equal(0, ownership.PendingSetPositionDispatchCount);
        ShadowObjectRegistry shadows = lifetime.Physics.Engine.ShadowObjects;
        Assert.Equal(0, shadows.SuspendedRegistrationCount);
        Assert.Contains(
            shadows.AllEntriesForDebug(),
            entry => entry.EntityId == owner.Key!.Value.LocalEntityId);
        Assert.True(shadows.TryGetCollisionOwner(
            owner.Key!.Value.LocalEntityId,
            out uint shadowState,
            out _));
        Assert.Equal((uint)owner.FinalPhysicsState, shadowState);
    }

    [Fact]
    public void NoDrawOwnerStillPublishesItsCollisionReport()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x7000207Au,
            1,
            PhysicsStateFlags.ReportCollisions | PhysicsStateFlags.NoDraw);
        RuntimeEntityRecord target = Entity(
            lifetime, 0x7000207Bu, 1, PhysicsStateFlags.None);
        RegisterDynamicShadow(lifetime, owner);
        RegisterDynamicShadow(lifetime, target);
        uint targetId = target.Key!.Value.LocalEntityId;
        lifetime.Physics.Engine.TransitionCellCollisionTestHook =
            (transition, phase, _, _) =>
            {
                if (phase is TransitionCellCollisionPhase.Environment)
                {
                    transition.CollisionInfo.CollideObjectGuids.Add(targetId);
                    transition.CollisionInfo.SetCollisionNormal(Vector3.UnitX);
                }
                return TransitionState.OK;
            };
        var observer = new CollisionObserver();
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);

        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition.Apply(
            owner,
            owner.PositionAuthorityVersion,
            PlacementCommand(owner, new Vector3(19f, 12f, 7f)));

        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            outcome.Status);
        RuntimeCollisionReport report = Assert.Single(
            observer.Reports,
            report => report.Kind is RuntimeCollisionReportKind.ObjectCollision);
        Assert.Equal(owner.Key, report.Recipient);
        Assert.Equal(target.Key, report.Other);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            outcome.Projection));
    }

    [Fact]
    public void NoDrawOwnerStillAllowsReciprocalPeerCollisionReport()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime, 0x7000207Cu, 1, PhysicsStateFlags.NoDraw);
        RuntimeEntityRecord target = Entity(
            lifetime,
            0x7000207Du,
            1,
            PhysicsStateFlags.ReportCollisions);
        RegisterDynamicShadow(lifetime, owner);
        RegisterDynamicShadow(lifetime, target);
        uint targetId = target.Key!.Value.LocalEntityId;
        lifetime.Physics.Engine.TransitionCellCollisionTestHook =
            (transition, phase, _, _) =>
            {
                if (phase is TransitionCellCollisionPhase.Environment)
                {
                    transition.CollisionInfo.CollideObjectGuids.Add(targetId);
                    transition.CollisionInfo.SetCollisionNormal(Vector3.UnitX);
                }
                return TransitionState.OK;
            };
        var observer = new CollisionObserver();
        using IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);

        RuntimeSetPositionOutcome outcome = lifetime.Physics.SetPosition.Apply(
            owner,
            owner.PositionAuthorityVersion,
            PlacementCommand(owner, new Vector3(20f, 12f, 7f)));

        Assert.Equal(
            RuntimeSetPositionStatus.CommittedHostAcknowledgementPending,
            outcome.Status);
        RuntimeCollisionReport report = Assert.Single(
            observer.Reports,
            report => report.Kind is RuntimeCollisionReportKind.ObjectCollision);
        Assert.Equal(target.Key, report.Recipient);
        Assert.Equal(owner.Key, report.Other);
        Assert.True(lifetime.Physics.SetPosition.AcknowledgeProjection(
            outcome.Projection));
    }

    [Fact]
    public void SessionResetAndDisposalConvergeEveryCollisionOwner()
    {
        var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x70002070u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RuntimeEntityRecord target = Entity(
            lifetime,
            0x70002071u,
            1,
            PhysicsStateFlags.ReportCollisions);
        RegisterDynamicShadow(lifetime, target);
        var observer = new CollisionObserver();
        IDisposable subscription = lifetime.Physics.CollisionReports
            .Subscribe(observer);
        Assert.True(Handle(
            lifetime,
            owner,
            time: 7d,
            Collisions(target.Key!.Value.LocalEntityId)));

        IReadOnlyList<RuntimeEntityRecord> retiring = lifetime.BeginSessionClear();
        RuntimeCollisionReportingOwnershipSnapshot cleared = lifetime.Physics
            .CollisionReports.CaptureOwnership();
        Assert.Equal(0, cleared.OwnerCount);
        Assert.Equal(0, cleared.TrackedObjectCount);
        foreach (RuntimeEntityRecord record in retiring)
            lifetime.CompleteSessionEntityRetirement(record);
        Assert.True(lifetime.CompleteSessionClearIfConverged());
        subscription.Dispose();
        lifetime.Dispose();
        Assert.True(lifetime.Physics.CollisionReports.CaptureOwnership()
            .IsConverged);
        Assert.True(lifetime.Physics.CaptureOwnership().IsConverged);
    }

    [Fact]
    public void IndependentRuntimeRootsProduceDeterministicReportsAndResult()
    {
        static (bool Result, RuntimeCollisionReport[] Reports) Run()
        {
            using var lifetime = Lifetime();
            RuntimeEntityRecord owner = Entity(
                lifetime,
                0x70002080u,
                1,
                PhysicsStateFlags.ReportCollisions);
            RuntimeEntityRecord target = Entity(
                lifetime,
                0x70002081u,
                1,
                PhysicsStateFlags.ReportCollisions);
            RegisterDynamicShadow(lifetime, target);
            var observer = new CollisionObserver();
            using IDisposable subscription = lifetime.Physics.CollisionReports
                .Subscribe(observer);
            bool result = Handle(
                lifetime,
                owner,
                time: 8d,
                Collisions(target.Key!.Value.LocalEntityId));
            return (result, observer.Reports.ToArray());
        }

        (bool graphicalResult, RuntimeCollisionReport[] graphical) = Run();
        (bool noWindowResult, RuntimeCollisionReport[] noWindow) = Run();
        Assert.Equal(graphicalResult, noWindowResult);
        Assert.Equal(graphical, noWindow);
    }

    [Fact]
    public void WarmedSteadyContactRefreshDoesNotAllocate()
    {
        using var lifetime = Lifetime();
        RuntimeEntityRecord owner = Entity(
            lifetime,
            0x70002089u,
            1,
            PhysicsStateFlags.None);
        RuntimeEntityRecord target = Entity(
            lifetime, 0x7000208Au, 1, PhysicsStateFlags.None);
        RegisterDynamicShadow(lifetime, target);
        PhysicsSetPositionCollisionReport collision =
            Collisions(target.Key!.Value.LocalEntityId);
        Assert.False(Handle(lifetime, owner, 1d, collision));

        for (int index = 0; index < 10_000; index++)
            _ = Handle(lifetime, owner, 1.1d, collision);

        long minimumAllocated = long.MaxValue;
        for (int sample = 0; sample < 5; sample++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 10_000; index++)
                _ = Handle(lifetime, owner, 1.1d, collision);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            minimumAllocated = Math.Min(minimumAllocated, allocated);
        }

        Assert.Equal(0, minimumAllocated);
        Assert.Equal(1, lifetime.Physics.CollisionReports.CaptureOwnership()
            .TrackedObjectCount);
    }

    private static RuntimeEntityObjectLifetime Lifetime()
    {
        var engine = new PhysicsEngine
        {
            DataCache = new PhysicsDataCache(),
        };
        engine.AddLandblock(
            Landblock,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        return new RuntimeEntityObjectLifetime(engine);
    }

    private static RuntimeEntityRecord Entity(
        RuntimeEntityObjectLifetime lifetime,
        uint guid,
        ushort incarnation,
        PhysicsStateFlags state)
    {
        RuntimeEntityRecord record = lifetime.RegisterEntity(
            Spawn(guid, incarnation, state)).Canonical!;
        lifetime.Entities.SetFullCell(record, Cell, Landblock | 0xFFFFu);
        lifetime.Entities.SetFinalPhysicsState(record, state);
        var body = new PhysicsBody
        {
            Position = new Vector3(12f, 12f, 7f),
            Orientation = Quaternion.Identity,
            State = state,
            LastUpdateTime = 1d,
            TransientState = TransientStateFlags.Active,
        };
        body.SnapToCell(Cell, body.Position, body.Position);
        lifetime.Entities.SetPhysicsBody(record, body);
        record.ObjectClock.Activate();
        lifetime.Physics.AcknowledgeSpatialProjection(record, spatial: true);
        return record;
    }

    private static void RegisterDynamicShadow(
        RuntimeEntityObjectLifetime lifetime,
        RuntimeEntityRecord record) => RegisterShadow(
            lifetime,
            record.Key!.Value.LocalEntityId,
            record.PhysicsBody!.State,
            isStatic: false);

    private static void RegisterShadow(
        RuntimeEntityObjectLifetime lifetime,
        uint localId,
        PhysicsStateFlags state,
        bool isStatic) => lifetime.Physics.Engine.ShadowObjects.Register(
            localId,
            gfxObjId: 0u,
            new Vector3(12f, 12f, 7f),
            Quaternion.Identity,
            radius: 0.4f,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            Landblock,
            ShadowCollisionType.Sphere,
            state: (uint)state,
            seedCellId: Cell,
            isStatic: isStatic);

    private static bool Handle(
        RuntimeEntityObjectLifetime lifetime,
        RuntimeEntityRecord owner,
        double time,
        in PhysicsSetPositionCollisionReport collision) =>
        lifetime.Physics.HandleSetPositionCollisions(
            owner,
            owner.PositionAuthorityVersion,
            owner.SpatialAuthorityVersion,
            owner.VelocityAuthorityVersion,
            time,
            owner.PhysicsBody!.InContact,
            owner.PhysicsBody.OnWalkable,
            collision);

    private static void DispatchPrepared(
        RuntimeEntityObjectLifetime lifetime,
        RuntimeEntityRecord owner,
        double physicsTime,
        ImmutableArray<uint> collidedObjectIds)
    {
        Assert.True(lifetime.Physics.CollisionReports
            .TryPrepareSetPositionBatch(
                owner,
                owner.PhysicsBody!,
                physicsTime,
                previousContact: owner.PhysicsBody!.InContact,
                previousOnWalkable: owner.PhysicsBody.OnWalkable,
                finalOnWalkable: owner.PhysicsBody.OnWalkable,
                collidedWithEnvironment: false,
                collidedObjectIds,
                out var prepared));
        Assert.True(lifetime.Physics.CollisionReports
            .TryInstallSetPositionBatch(prepared!, out var receipt));
        lifetime.Physics.CollisionReports.DispatchSetPositionBatch(receipt);
    }

    private static RuntimeSetPositionCommand PlacementCommand(
        RuntimeEntityRecord owner,
        Vector3 position)
    {
        var request = new PhysicsSetPositionRequest(
            position,
            Quaternion.Identity,
            Cell,
            position,
            [new FlatCollisionSphere(Vector3.Zero, 0.4f)],
            Scale: 1f,
            StepUpHeight: 0.4f,
            StepDownHeight: 0.4f,
            MoverPhysicsState: owner.FinalPhysicsState,
            MovingEntityId: owner.Key!.Value.LocalEntityId,
            Flags: PhysicsSetPositionFlags.Placement
                | PhysicsSetPositionFlags.Slide,
            CurrentCellId: Cell);
        return new RuntimeSetPositionCommand(
            request,
            RuntimeSetPositionOperationKind.RemoteAuthoritative,
            GameTime: 10d,
            ExpectedVelocityAuthorityVersion:
                owner.VelocityAuthorityVersion,
            ShadowWorldOffsetX: 0f,
            ShadowWorldOffsetY: 0f);
    }

    private static PhysicsSetPositionCollisionReport Collisions(
        params uint[] objectIds) => Collisions(
            objectIds,
            environmentCollision: false);

    private static PhysicsSetPositionCollisionReport Collisions(
        uint objectId,
        bool environmentCollision) => Collisions(
            [objectId],
            environmentCollision);

    private static PhysicsSetPositionCollisionReport Collisions(
        uint first,
        uint second,
        bool environmentCollision) => Collisions(
            [first, second],
            environmentCollision);

    private static PhysicsSetPositionCollisionReport Collisions(
        uint[] objectIds,
        bool environmentCollision) => new(
            ContactPlaneValid: false,
            ContactPlane: default,
            ContactPlaneCellId: 0u,
            ContactPlaneIsWater: false,
            LastKnownContactPlaneValid: false,
            LastKnownContactPlane: default,
            LastKnownContactPlaneCellId: 0u,
            LastKnownContactPlaneIsWater: false,
            SlidingNormalValid: false,
            SlidingNormal: default,
            CollisionNormalValid: false,
            CollisionNormal: default,
            CollidedWithEnvironment: environmentCollision,
            FramesStationaryFall: 0,
            AdjustOffset: default,
            LastCollidedObjectId: objectIds.Length == 0 ? null : objectIds[^1],
            CollidedObjectIds: objectIds.ToImmutableArray());

    private static void AssertReport(
        RuntimeCollisionReport report,
        RuntimeCollisionReportKind kind,
        RuntimeEntityRecord recipient,
        RuntimeEntityRecord? other)
    {
        Assert.Equal(kind, report.Kind);
        Assert.Equal(recipient.Key, report.Recipient);
        Assert.Equal(recipient.ServerGuid, report.RecipientServerGuid);
        Assert.Equal(other?.Key, report.Other);
        Assert.Equal(other?.ServerGuid, report.OtherServerGuid);
    }

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        ushort instance,
        PhysicsStateFlags state)
    {
        var position = new CreateObject.ServerPosition(
            Cell,
            12f,
            12f,
            7f,
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
            RawState: (uint)state,
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
            "collision-report-fixture",
            null,
            null,
            0x09000001u,
            PhysicsState: (uint)state,
            InstanceSequence: instance,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    private sealed class CollisionObserver(
        Action<RuntimeCollisionReport>? onReport = null)
        : IRuntimeCollisionReportObserver
    {
        internal List<RuntimeCollisionReport> Reports { get; } = [];

        public void OnCollisionReport(in RuntimeCollisionReport report)
        {
            Reports.Add(report);
            onReport?.Invoke(report);
        }
    }

    private sealed class ThrowingCollisionObserver
        : IRuntimeCollisionReportObserver
    {
        public void OnCollisionReport(in RuntimeCollisionReport report) =>
            throw new InvalidOperationException(
                $"fixture failure {report.Sequence}");
    }

    private sealed class ReentrantRemotePlacement(PhysicsBody body)
        : IRuntimeRemotePlacement
    {
        private Func<uint>? _readCell;
        private Action<uint>? _writeCell;
        private uint _cellId;

        public PhysicsBody Body { get; } = body;
        public uint CellId
        {
            get => _readCell?.Invoke() ?? _cellId;
            set
            {
                _cellId = value;
                _writeCell?.Invoke(value);
            }
        }
        public bool Airborne { get; set; }
        public Vector3 LastServerPosition { get; set; }
        public double LastServerPositionTime { get; set; }
        public Vector3 LastShadowSyncPosition { get; set; }
        public Quaternion LastShadowSyncOrientation { get; set; }
        internal Action? OnHitGround { get; init; }
        internal Action? OnLeaveGround { get; init; }

        public void BindCanonicalCell(Func<uint> read, Action<uint> write)
        {
            _readCell = read;
            _writeCell = write;
        }

        public void HitGround() => OnHitGround?.Invoke();
        public void LeaveGround() => OnLeaveGround?.Invoke();
    }
}
