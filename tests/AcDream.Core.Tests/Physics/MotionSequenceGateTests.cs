using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class PhysicsTimestampGateTests
{
    [Theory]
    [InlineData((ushort)0, (ushort)1, true)]        // simple newer
    [InlineData((ushort)1, (ushort)0, false)]       // simple older
    [InlineData((ushort)5, (ushort)5, false)]       // equal is not newer
    [InlineData((ushort)0, (ushort)0x7FFF, true)]
    [InlineData((ushort)0, (ushort)0x8000, false)]  // abs diff 0x8000 → wraparound branch
    [InlineData((ushort)0xFFFF, (ushort)0, true)]   // 0 is newer than 0xFFFF (wrap)
    [InlineData((ushort)0xFFFE, (ushort)2, true)]   // newer across the seam
    [InlineData((ushort)2, (ushort)0xFFFE, false)]  // older across the seam
    public void IsNewer_MatchesRetailWraparoundCompare(ushort oldStamp, ushort newStamp, bool expected)
    {
        Assert.Equal(expected, PhysicsTimestampGate.IsNewer(oldStamp, newStamp));
    }

    [Fact]
    public void EventBeforeCreateObjectSeed_IsRejected()
    {
        var gate = new PhysicsTimestampGate();
        Assert.False(gate.TryAcceptMovementEvent(instance: 0, movement: 1, serverControlledMove: 0));
        Assert.False(gate.TryAcceptStateEvent(instance: 0, state: 1));
        Assert.False(gate.TryAcceptVectorEvent(instance: 0, vector: 1));
        Assert.False(gate.TryAcceptObjDescEvent(instance: 0, objDesc: 1));
    }

    [Fact]
    public void DuplicateMovementSequence_IsDropped()
    {
        var gate = new PhysicsTimestampGate();
        SeedMovement(gate, instance: 0, movement: 0, serverControl: 0);
        Assert.True(gate.TryAcceptMovementEvent(0, 5, 0));
        Assert.False(gate.TryAcceptMovementEvent(0, 5, 0));
    }

    [Fact]
    public void StaleMovementSequence_IsDropped_ThenNewerAccepted()
    {
        var gate = new PhysicsTimestampGate();
        SeedMovement(gate, instance: 0, movement: 0, serverControl: 0);
        Assert.True(gate.TryAcceptMovementEvent(0, 5, 0));
        Assert.False(gate.TryAcceptMovementEvent(0, 4, 0)); // reordered straggler
        Assert.True(gate.TryAcceptMovementEvent(0, 6, 0));
    }

    [Fact]
    public void MovementSequence_WrapsAroundTheU16Seam()
    {
        var gate = new PhysicsTimestampGate();
        SeedMovement(gate, instance: 0, movement: 0xFFFD, serverControl: 0);
        Assert.True(gate.TryAcceptMovementEvent(0, 0xFFFE, 0));
        Assert.True(gate.TryAcceptMovementEvent(0, 2, 0)); // 2 is newer than 0xFFFE by wrap rule
        Assert.False(gate.TryAcceptMovementEvent(0, 0xFFFE, 0)); // and the old stamp is now stale
    }

    [Fact]
    public void SeededGate_AcceptsNextSequence_RejectsThePast()
    {
        var gate = new PhysicsTimestampGate();
        SeedMovement(gate, instance: 3, movement: 0x9000, serverControl: 2);
        Assert.True(gate.TryAcceptMovementEvent(3, 0x9001, 2));
        Assert.False(gate.TryAcceptMovementEvent(3, 0x8FFF, 2)); // pre-spawn straggler
    }

    [Fact]
    public void SameGenerationCreateObject_DoesNotConsumeIndividualChannels()
    {
        var gate = new PhysicsTimestampGate();
        SeedMovement(gate, instance: 3, movement: 0x9000, serverControl: 2);
        Assert.True(gate.TryAcceptMovementEvent(3, 0x9001, 2));

        var disposition = gate.SeedForCreateObject(
            0, 0x9002, 0, 0, 0, 2, 0, 0, 3);
        Assert.Equal(CreateObjectTimestampDisposition.ExistingGeneration, disposition);
        Assert.True(gate.TryAcceptMovementEvent(3, 0x9002, 2));

        SeedMovement(gate, instance: 4, movement: 0x9010, serverControl: 2);
        Assert.False(gate.TryAcceptMovementEvent(3, 0x9011, 2)); // old incarnation stale
        Assert.True(gate.TryAcceptMovementEvent(4, 0x9011, 2));
    }

    [Fact]
    public void PreviewCreateObject_MatchesSeedDispositionWithoutMutation()
    {
        var gate = new PhysicsTimestampGate();

        Assert.Equal(CreateObjectTimestampDisposition.InitialGeneration,
            gate.PreviewCreateObject(instance: 7));
        Assert.Equal(CreateObjectTimestampDisposition.InitialGeneration,
            gate.SeedForCreateObject(10, 20, 30, 40, 50, 60, 70, 80, 7));

        Assert.Equal(CreateObjectTimestampDisposition.ExistingGeneration,
            gate.PreviewCreateObject(instance: 7));
        Assert.Equal(CreateObjectTimestampDisposition.NewGeneration,
            gate.PreviewCreateObject(instance: 8));
        Assert.Equal(CreateObjectTimestampDisposition.StaleGeneration,
            gate.PreviewCreateObject(instance: 6));

        Assert.True(gate.TryAcceptMovementEvent(
            instance: 7,
            movement: 21,
            serverControlledMove: 60));
        Assert.False(gate.TryAcceptMovementEvent(
            instance: 8,
            movement: 22,
            serverControlledMove: 60));
    }

    [Fact]
    public void SameGenerationCreateObject_MixedChannelsAreAcceptedIndependently()
    {
        var gate = new PhysicsTimestampGate();
        gate.SeedForCreateObject(10, 10, 10, 10, 20, 5, 0, 10, 3);

        Assert.Equal(CreateObjectTimestampDisposition.ExistingGeneration,
            gate.SeedForCreateObject(9, 11, 11, 9, 20, 5, 0, 11, 3));

        Assert.False(gate.TryAcceptPositionChannelEvent(3, 9));
        Assert.True(gate.TryAcceptMovementEvent(3, 11, 5));
        Assert.True(gate.TryAcceptStateEvent(3, 11));
        Assert.False(gate.TryAcceptVectorEvent(3, 9));
        Assert.True(gate.TryAcceptObjDescEvent(3, 11));
    }

    [Fact]
    public void StaleServerControl_Drops_ButMovementStampStillAdvances()
    {
        var gate = new PhysicsTimestampGate();
        SeedMovement(gate, instance: 0, movement: 0, serverControl: 0);
        Assert.True(gate.TryAcceptMovementEvent(0, 1, 5));
        Assert.False(gate.TryAcceptMovementEvent(0, 2, 4)); // sc=4 older than stored 5 → drop
        Assert.False(gate.TryAcceptMovementEvent(0, 2, 5));
        Assert.True(gate.TryAcceptMovementEvent(0, 3, 5));  // next movementSeq proceeds
    }

    [Fact]
    public void EqualServerControl_Passes()
    {
        var gate = new PhysicsTimestampGate();
        SeedMovement(gate, instance: 0, movement: 0, serverControl: 0);
        Assert.True(gate.TryAcceptMovementEvent(0, 1, 7));
        Assert.True(gate.TryAcceptMovementEvent(0, 2, 7));
    }

    [Fact]
    public void StaleInstance_Drops_WithoutConsumingMovementSequence()
    {
        var gate = new PhysicsTimestampGate();
        SeedMovement(gate, instance: 2, movement: 0, serverControl: 0);
        Assert.True(gate.TryAcceptMovementEvent(2, 1, 0));
        Assert.False(gate.TryAcceptMovementEvent(1, 2, 0)); // stale incarnation
        Assert.True(gate.TryAcceptMovementEvent(2, 2, 0));
    }

    [Fact]
    public void NewerInstance_IsDroppedUntilCreateObjectStartsGeneration()
    {
        var gate = new PhysicsTimestampGate();
        SeedMovement(gate, instance: 1, movement: 0, serverControl: 0);
        Assert.True(gate.TryAcceptMovementEvent(1, 1, 0));
        Assert.False(gate.TryAcceptMovementEvent(2, 2, 0));
        Assert.True(gate.TryAcceptMovementEvent(1, 2, 0));
    }

    [Fact]
    public void InstanceCompare_WrapsAroundTheU16Seam()
    {
        var gate = new PhysicsTimestampGate();
        SeedMovement(gate, instance: 0xFFFE, movement: 0, serverControl: 0);
        Assert.True(gate.TryAcceptMovementEvent(0xFFFE, 1, 0));
        Assert.False(gate.TryAcceptMovementEvent(2, 2, 0));
        Assert.Equal(CreateObjectTimestampDisposition.NewGeneration,
            gate.SeedForCreateObject(0, 1, 0, 0, 0, 0, 0, 0, 2));
        Assert.True(gate.TryAcceptMovementEvent(2, 2, 0));
    }

    [Fact]
    public void StateAndVectorChannelsAdvanceIndependently()
    {
        var gate = new PhysicsTimestampGate();
        gate.SeedForCreateObject(10, 20, 30, 40, 50, 60, 70, 80, 90);

        Assert.True(gate.TryAcceptStateEvent(90, 31));
        Assert.False(gate.TryAcceptStateEvent(90, 31));
        Assert.True(gate.TryAcceptVectorEvent(90, 41));
        Assert.False(gate.TryAcceptVectorEvent(90, 40));
    }

    [Fact]
    public void PositionRollsBackWhenNewerTeleportAlreadyExists()
    {
        var gate = new PhysicsTimestampGate();
        gate.SeedForCreateObject(10, 0, 0, 0, 20, 0, 0, 0, 1);

        Assert.Equal(PositionTimestampDisposition.Rejected,
            gate.TryAcceptPositionEvent(1, 11, 19, 0, isLocalPlayer: false));
        Assert.Equal(PositionTimestampDisposition.Apply,
            gate.TryAcceptPositionEvent(1, 11, 20, 0, isLocalPlayer: false));
    }

    [Fact]
    public void FreshTeleportAndForcePositionAdvanceTheirOwnChannels()
    {
        var gate = new PhysicsTimestampGate();
        gate.SeedForCreateObject(10, 0, 0, 0, 20, 0, 30, 0, 1);

        Assert.Equal(PositionTimestampDisposition.ForcePosition,
            gate.TryAcceptPositionEvent(1, 9, 20, 31, isLocalPlayer: true));
        Assert.Equal((ushort)9, gate.PositionTimestamp);
        Assert.Equal((ushort)20, gate.TeleportTimestamp);
        Assert.Equal(PositionTimestampDisposition.Rejected,
            gate.TryAcceptPositionEvent(1, 9, 20, 31, isLocalPlayer: true));
    }

    [Fact]
    public void FreshForceWithNewerTeleport_UsesForcePathWithoutConsumingTeleport()
    {
        var gate = new PhysicsTimestampGate();
        gate.SeedForCreateObject(10, 0, 0, 0, 20, 0, 30, 0, 1);

        Assert.Equal(PositionTimestampDisposition.ForcePosition,
            gate.TryAcceptPositionEvent(1, 11, 21, 31, isLocalPlayer: true));
        Assert.Equal((ushort)11, gate.PositionTimestamp);
        Assert.Equal((ushort)20, gate.TeleportTimestamp);
        Assert.Equal((ushort)31, gate.ForcePositionTimestamp);
    }

    [Fact]
    public void FreshForceWithWrappedNewerTeleport_UsesForcePathWithoutConsumingTeleport()
    {
        var gate = new PhysicsTimestampGate();
        gate.SeedForCreateObject(10, 0, 0, 0, ushort.MaxValue, 0, 30, 0, 1);

        Assert.Equal(PositionTimestampDisposition.ForcePosition,
            gate.TryAcceptPositionEvent(1, 11, 0, 31, isLocalPlayer: true));
        Assert.Equal((ushort)11, gate.PositionTimestamp);
        Assert.Equal(ushort.MaxValue, gate.TeleportTimestamp);
        Assert.Equal((ushort)31, gate.ForcePositionTimestamp);
    }

    [Fact]
    public void FreshForceIsConsumedWhenOlderTeleportRejectsPose()
    {
        var gate = new PhysicsTimestampGate();
        gate.SeedForCreateObject(10, 0, 0, 0, 20, 0, 30, 0, 1);

        Assert.Equal(PositionTimestampDisposition.Rejected,
            gate.TryAcceptPositionEvent(1, 11, 19, 31, isLocalPlayer: true));
        Assert.Equal((ushort)10, gate.PositionTimestamp);
        Assert.Equal((ushort)20, gate.TeleportTimestamp);
        Assert.Equal((ushort)31, gate.ForcePositionTimestamp);
    }

    [Fact]
    public void ParentPickupAndPositionShareOnePositionChannel()
    {
        var gate = new PhysicsTimestampGate();
        gate.SeedForCreateObject(10, 0, 0, 0, 20, 0, 0, 0, 1);

        Assert.True(gate.TryAcceptPositionChannelEvent(1, 11));
        Assert.Equal(PositionTimestampDisposition.Rejected,
            gate.TryAcceptPositionEvent(1, 11, 20, 0, isLocalPlayer: false));
        Assert.False(gate.TryAcceptPositionChannelEvent(1, 10));
        Assert.True(gate.TryAcceptPositionChannelEvent(1, 12));
    }

    [Fact]
    public void NewCreateObjectGenerationReplacesLowerChannelStampsWholesale()
    {
        var gate = new PhysicsTimestampGate();
        gate.SeedForCreateObject(1000, 1000, 1000, 1000, 1000, 1000, 1000, 1000, 1);

        var disposition = gate.SeedForCreateObject(1, 1, 1, 1, 1, 1, 1, 1, 2);

        Assert.Equal(CreateObjectTimestampDisposition.NewGeneration, disposition);
        Assert.True(gate.TryAcceptStateEvent(2, 2));
        Assert.True(gate.TryAcceptVectorEvent(2, 2));
        Assert.Equal(PositionTimestampDisposition.Apply,
            gate.TryAcceptPositionEvent(2, 2, 1, 1, isLocalPlayer: false));
    }

    [Fact]
    public void StaleCreateObjectAndDeleteCannotAffectCurrentGeneration()
    {
        var gate = new PhysicsTimestampGate();
        gate.SeedForCreateObject(1, 1, 1, 1, 1, 1, 1, 1, 2);

        Assert.Equal(CreateObjectTimestampDisposition.StaleGeneration,
            gate.SeedForCreateObject(500, 500, 500, 500, 500, 500, 500, 500, 1));
        Assert.False(gate.TryAcceptDeleteEvent(1));
        Assert.True(gate.TryAcceptDeleteEvent(2));
        Assert.False(gate.TryAcceptDeleteEvent(2, isLocalPlayer: true));
    }

    [Fact]
    public void ObjDescRequiresCurrentInstanceAndStrictlyNewerSequence()
    {
        var gate = new PhysicsTimestampGate();
        gate.SeedForCreateObject(0, 0, 0, 0, 0, 0, 0, 0xFFFE, 7);

        Assert.False(gate.TryAcceptObjDescEvent(6, 0xFFFF));
        Assert.True(gate.TryAcceptObjDescEvent(7, 0xFFFF));
        Assert.True(gate.TryAcceptObjDescEvent(7, 0));
        Assert.False(gate.TryAcceptObjDescEvent(7, 0));
    }

    [Fact]
    public void PlayerTeleportStartChecksButDoesNotAdvanceTeleportChannel()
    {
        var gate = new PhysicsTimestampGate();
        gate.SeedForCreateObject(0, 0, 0, 0, 10, 0, 0, 0, 1);

        Assert.True(gate.IsFreshTeleportStart(10));
        Assert.False(gate.IsFreshTeleportStart(9));
        Assert.True(gate.IsFreshTeleportStart(11));
        Assert.True(gate.IsFreshTeleportStart(11));
        Assert.Equal((ushort)10, gate.TeleportTimestamp);
    }

    private static void SeedMovement(
        PhysicsTimestampGate gate,
        ushort instance,
        ushort movement,
        ushort serverControl) =>
        gate.SeedForCreateObject(0, movement, 0, 0, 0, serverControl, 0, 0, instance);
}
