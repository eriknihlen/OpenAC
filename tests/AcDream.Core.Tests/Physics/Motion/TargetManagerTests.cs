using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class TargetManagerTests
{
    private static (PhysicsObjHostStub self, PhysicsObjHostStub target, Dictionary<uint, PhysicsObjHostStub> world) TwoHosts()
    {
        var world = new Dictionary<uint, PhysicsObjHostStub>();
        var self = new PhysicsObjHostStub(10u, world);
        var target = new PhysicsObjHostStub(20u, world);
        return (self, target, world);
    }

    // ── watcher role ───────────────────────────────────────────────────────

    [Fact]
    public void SetTarget_SubscribesOnTarget_AndReceivesImmediateSnapshot()
    {
        var (self, target, _) = TwoHosts();
        target.SetOrigin(new Vector3(3f, 0f, 0f));

        self.TargetManager.SetTarget(0, target.Id, radius: 1.0f, quantum: 0.0);

        // Watcher subscribed on the target.
        Assert.True(target.TargetManager.VoyeurTable!.ContainsKey(self.Id));
        // Immediate Ok snapshot fanned to the watcher's host.
        Assert.Single(self.HandleUpdateTargetCalls);
        Assert.Equal(TargetStatus.Ok, self.HandleUpdateTargetCalls[0].Status);
        Assert.Equal(target.Id, self.HandleUpdateTargetCalls[0].ObjectId);
        Assert.Equal(new Vector3(3f, 0f, 0f),
            self.HandleUpdateTargetCalls[0].InterpolatedPosition.Frame.Origin);
        Assert.Equal(TargetStatus.Ok, self.TargetManager.TargetInfo!.Value.Status);
    }

    [Fact]
    public void SetTarget_Zero_SynthesizesTimedOut_LeavesTargetInfoNull()
    {
        var (self, _, _) = TwoHosts();
        self.TargetManager.SetTarget(contextId: 7, objectId: 0, radius: 1.0f, quantum: 0.0);

        Assert.Null(self.TargetManager.TargetInfo);
        Assert.Single(self.HandleUpdateTargetCalls);
        Assert.Equal(TargetStatus.TimedOut, self.HandleUpdateTargetCalls[0].Status);
        Assert.Equal(7u, self.HandleUpdateTargetCalls[0].ContextId);
    }

    [Fact]
    public void ClearTarget_UnsubscribesFromTarget()
    {
        var (self, target, _) = TwoHosts();
        self.TargetManager.SetTarget(0, target.Id, 1.0f, 0.0);
        Assert.True(target.TargetManager.VoyeurTable!.ContainsKey(self.Id));

        self.TargetManager.ClearTarget();

        Assert.Null(self.TargetManager.TargetInfo);
        Assert.False(target.TargetManager.VoyeurTable!.ContainsKey(self.Id));
    }

    [Fact]
    public void ReceiveUpdate_ForWrongObject_Ignored()
    {
        var (self, target, _) = TwoHosts();
        self.TargetManager.SetTarget(0, target.Id, 1.0f, 0.0);
        int before = self.HandleUpdateTargetCalls.Count;

        var p = new Position(1u, Vector3.Zero, Quaternion.Identity);
        self.TargetManager.ReceiveUpdate(
            new TargetInfo(999u, TargetStatus.Ok, p, p),
            target);

        Assert.Equal(before, self.HandleUpdateTargetCalls.Count);
    }

    [Fact]
    public void ReceiveUpdate_ExitWorld_ClearsTarget()
    {
        var (self, target, _) = TwoHosts();
        self.TargetManager.SetTarget(0, target.Id, 1.0f, 0.0);

        var p = new Position(1u, Vector3.Zero, Quaternion.Identity);
        self.TargetManager.ReceiveUpdate(
            new TargetInfo(target.Id, TargetStatus.ExitWorld, p, p),
            target);

        Assert.Null(self.TargetManager.TargetInfo);
        Assert.False(target.TargetManager.VoyeurTable!.ContainsKey(self.Id)); // unsubscribed
    }

    [Fact]
    public void ReceiveUpdate_ComputesInterpolatedHeadingTowardTarget()
    {
        var (self, target, _) = TwoHosts();
        self.SetOrigin(Vector3.Zero);
        self.TargetManager.SetTarget(0, target.Id, 1.0f, 0.0);

        var tp = new Position(1u, new Vector3(0f, 5f, 0f), Quaternion.Identity);
        self.TargetManager.ReceiveUpdate(
            new TargetInfo(target.Id, TargetStatus.Ok, tp, tp),
            target);

        // self→target interp position (0,5,0) normalized = +Y.
        var heading = self.TargetManager.TargetInfo!.Value.InterpolatedHeading;
        Assert.Equal(0f, heading.X, 3);
        Assert.Equal(1f, heading.Y, 3);
    }

    [Fact]
    public void ReceiveUpdate_SameGuidReplacementSender_IsIgnored()
    {
        var (self, originalTarget, world) = TwoHosts();
        self.TargetManager.SetTarget(0, originalTarget.Id, 1.0f, 0.0);
        self.HandleUpdateTargetCalls.Clear();
        var replacement = new PhysicsObjHostStub(originalTarget.Id, world);
        var p = new Position(1u, new Vector3(99f, 0f, 0f), Quaternion.Identity);

        self.ReceiveTargetUpdate(
            new TargetInfo(originalTarget.Id, TargetStatus.ExitWorld, p, p),
            replacement);

        Assert.Empty(self.HandleUpdateTargetCalls);
        Assert.NotNull(self.TargetManager.TargetInfo);
        Assert.Same(
            originalTarget,
            self.TargetManager.GetRelationshipTarget(originalTarget.Id));
    }

    [Fact]
    public void StickyAdjustOffset_UsesExactTargetInsteadOfSameGuidReplacement()
    {
        var (self, originalTarget, world) = TwoHosts();
        self.SetOrigin(Vector3.Zero);
        originalTarget.SetOrigin(new Vector3(5f, 0f, 0f));
        self.PositionManager.StickTo(originalTarget.Id, 0.5f, 1f);
        var replacement = new PhysicsObjHostStub(originalTarget.Id, world);
        replacement.SetOrigin(new Vector3(10f, 0f, 0f));
        originalTarget.SetOrigin(new Vector3(-5f, 0f, 0f));
        var delta = new MotionDeltaFrame();

        self.PositionManager.AdjustOffset(delta, 0.1);

        Assert.True(delta.Origin.X < 0f);
        Assert.Same(
            originalTarget,
            self.TargetManager.GetRelationshipTarget(originalTarget.Id));
    }

    [Fact]
    public void HandleTargetting_UndefinedTarget_TimesOutAfter10Seconds()
    {
        var (self, target, _) = TwoHosts();
        target.Resolvable = false; // no immediate snapshot → status stays Undefined
        self.CurTime = 0;
        self.PhysicsTimerTime = 0;
        self.TargetManager.SetTarget(0, target.Id, 1.0f, 0.0);
        Assert.Equal(TargetStatus.Undefined, self.TargetManager.TargetInfo!.Value.Status);

        self.AdvanceClocks(11.0);
        self.TargetManager.HandleTargetting();

        Assert.Equal(TargetStatus.TimedOut, self.TargetManager.TargetInfo!.Value.Status);
        Assert.Contains(self.HandleUpdateTargetCalls, c => c.Status == TargetStatus.TimedOut);
    }

    // ── watched role + gates ────────────────────────────────────────────────

    [Fact]
    public void HandleTargetting_Throttles_Within500ms()
    {
        var (self, target, _) = TwoHosts();
        // seed a voyeur directly + advance so a sweep WOULD send if not throttled.
        target.TargetManager.AddVoyeur(self, 0.1f, 0.0);
        target.SetOrigin(new Vector3(10f, 0f, 0f)); // far past radius

        target.PhysicsTimerTime = 0.3; // < 0.5 throttle
        int before = self.HandleUpdateTargetCalls.Count;
        target.TargetManager.HandleTargetting();

        Assert.Equal(before, self.HandleUpdateTargetCalls.Count); // throttled, no sweep
    }

    [Fact]
    public void CheckAndUpdateVoyeur_SendsOnlyPastRadius()
    {
        var (self, target, _) = TwoHosts();
        // self must have a matching target_info for ReceiveUpdate to record.
        self.TargetManager.SetTarget(0, target.Id, radius: 1.0f, quantum: 0.0);
        int afterSubscribe = self.HandleUpdateTargetCalls.Count; // includes immediate snapshot

        // small move → within radius → no send
        target.SetOrigin(new Vector3(0.5f, 0f, 0f));
        target.AdvanceClocks(1.0);
        target.TargetManager.HandleTargetting();
        Assert.Equal(afterSubscribe, self.HandleUpdateTargetCalls.Count);

        // large move → past radius → send
        target.SetOrigin(new Vector3(2f, 0f, 0f));
        target.AdvanceClocks(1.0);
        target.TargetManager.HandleTargetting();
        Assert.Equal(afterSubscribe + 1, self.HandleUpdateTargetCalls.Count);
        var last = self.HandleUpdateTargetCalls[^1];
        Assert.Equal(TargetStatus.Ok, last.Status);
        Assert.Equal(2f, last.InterpolatedPosition.Frame.Origin.X, 3);
    }

    [Fact]
    public void GetInterpolatedPosition_DeadReckonsWithVelocity()
    {
        var (_, target, _) = TwoHosts();
        target.SetOrigin(new Vector3(1f, 2f, 0f));
        target.Velocity = new Vector3(10f, 0f, 0f);

        var p = target.TargetManager.GetInterpolatedPosition(quantum: 0.5);

        Assert.Equal(new Vector3(6f, 2f, 0f), p.Frame.Origin); // 1 + 10*0.5 = 6
    }

    [Fact]
    public void AddVoyeur_Existing_UpdatesInPlace_NoResend()
    {
        var (self, target, _) = TwoHosts();
        target.SetOrigin(Vector3.Zero);
        target.TargetManager.AddVoyeur(self, radius: 1.0f, quantum: 0.0);
        var voyeur = target.TargetManager.VoyeurTable![self.Id];
        Assert.Equal(Vector3.Zero, voyeur.LastSentPosition.Frame.Origin);

        target.SetOrigin(new Vector3(5f, 0f, 0f));
        target.TargetManager.AddVoyeur(self, radius: 2.5f, quantum: 0.3); // existing → update only

        Assert.Equal(2.5f, voyeur.Radius);
        Assert.Equal(0.3, voyeur.Quantum);
        Assert.Equal(Vector3.Zero, voyeur.LastSentPosition.Frame.Origin); // NOT re-sent
    }

    [Fact]
    public void RemoveVoyeur_RemovesEntry()
    {
        var (self, target, _) = TwoHosts();
        target.TargetManager.AddVoyeur(self, 1.0f, 0.0);
        Assert.True(target.TargetManager.RemoveVoyeur(self.Id, self));
        Assert.False(target.TargetManager.VoyeurTable!.ContainsKey(self.Id));
        Assert.False(target.TargetManager.RemoveVoyeur(self.Id, self)); // already gone
    }

    [Fact]
    public void HiddenWatcher_GuidReuseCannotRedirectOrRemoveExactSubscription()
    {
        var (watcher, target, world) = TwoHosts();
        watcher.TargetManager.SetTarget(0, target.Id, 1.0f, 0.0);
        watcher.HandleUpdateTargetCalls.Clear();
        watcher.Resolvable = false;
        var replacement = new PhysicsObjHostStub(watcher.Id, world);

        target.TargetManager.NotifyVoyeurOfEvent(TargetStatus.Teleported);

        Assert.Single(watcher.HandleUpdateTargetCalls);
        Assert.Empty(replacement.HandleUpdateTargetCalls);
        Assert.False(target.TargetManager.RemoveVoyeur(watcher.Id, replacement));
        Assert.True(target.TargetManager.RemoveVoyeur(watcher.Id, watcher));
    }

    [Fact]
    public void NotifyVoyeurOfEvent_BroadcastsToAll_NoDistanceGate()
    {
        var world = new Dictionary<uint, PhysicsObjHostStub>();
        var target = new PhysicsObjHostStub(20u, world);
        var w1 = new PhysicsObjHostStub(10u, world);
        var w2 = new PhysicsObjHostStub(11u, world);
        // both watch the target (each gets a target_info via SetTarget)
        w1.TargetManager.SetTarget(0, target.Id, 1.0f, 0.0);
        w2.TargetManager.SetTarget(0, target.Id, 1.0f, 0.0);
        int w1Before = w1.HandleUpdateTargetCalls.Count;
        int w2Before = w2.HandleUpdateTargetCalls.Count;

        target.TargetManager.NotifyVoyeurOfEvent(TargetStatus.Teleported);

        Assert.Equal(w1Before + 1, w1.HandleUpdateTargetCalls.Count);
        Assert.Equal(w2Before + 1, w2.HandleUpdateTargetCalls.Count);
        Assert.Equal(TargetStatus.Teleported, w1.HandleUpdateTargetCalls[^1].Status);
    }

    // ── full round-trip ─────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_WatcherTracksMovingTarget()
    {
        var (self, target, _) = TwoHosts();
        target.SetOrigin(Vector3.Zero);

        // Watcher sets target with a 1.0 radius (moveto-style, quantum 0).
        self.TargetManager.SetTarget(0, target.Id, radius: 1.0f, quantum: 0.0);
        Assert.Single(self.HandleUpdateTargetCalls); // immediate snapshot at (0,0,0)

        // Target walks to (3,0,0); its per-tick HandleTargetting notices the
        // drift and pushes an update the watcher receives.
        target.SetOrigin(new Vector3(3f, 0f, 0f));
        target.AdvanceClocks(1.0);
        target.TargetManager.HandleTargetting();

        Assert.Equal(2, self.HandleUpdateTargetCalls.Count);
        Assert.Equal(new Vector3(3f, 0f, 0f),
            self.HandleUpdateTargetCalls[^1].InterpolatedPosition.Frame.Origin);
        Assert.Equal(3f, self.TargetManager.TargetInfo!.Value.InterpolatedPosition.Frame.Origin.X, 3);
    }
}
