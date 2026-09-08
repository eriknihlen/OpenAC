using AcDream.App.World;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;

namespace AcDream.App.Tests.World;

public sealed class LiveEntityRuntimeTeardownControllerTests
{
    [Fact]
    public void Retry_ReusesPlanAndDoesNotReplayCompletedOwners()
    {
        LiveEntityRecord record =
            LiveEntityTestFixture.CreateExactProjectionRecord(Spawn());
        int factoryCalls = 0;
        int completedOwnerCalls = 0;
        int retryingOwnerCalls = 0;
        bool failOnce = true;
        var controller = new LiveEntityRuntimeTeardownController(
            _ =>
            {
                factoryCalls++;
                return new LiveEntityTeardownPlan([
                    () => completedOwnerCalls++,
                    () =>
                    {
                        retryingOwnerCalls++;
                        if (failOnce)
                        {
                            failOnce = false;
                            throw new InvalidOperationException("fixture failure");
                        }
                    },
                ]);
            },
            _ => { });

        Assert.Throws<AggregateException>(() => controller.TearDown(record));
        controller.TearDown(record);

        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, completedOwnerCalls);
        Assert.Equal(2, retryingOwnerCalls);
        Assert.True(record.RuntimeComponentTeardownPlan!.IsComplete);
    }

    [Fact]
    public void UnknownOwner_IsForwardedWithoutCreatingATeardownPlan()
    {
        uint forgotten = 0u;
        int factoryCalls = 0;
        var controller = new LiveEntityRuntimeTeardownController(
            _ =>
            {
                factoryCalls++;
                return new LiveEntityTeardownPlan([]);
            },
            guid => forgotten = guid);

        controller.ForgetUnknownOwner(Guid);

        Assert.Equal(Guid, forgotten);
        Assert.Equal(0, factoryCalls);
    }

    private const uint Guid = 0x70000001u;
    private const uint Cell = 0x01010001u;

    private static WorldSession.EntitySpawn Spawn()
    {
        var position = new CreateObject.ServerPosition(
            Cell, 10f, 10f, 5f, 1f, 0f, 0f, 0f);
        var timestamps = new PhysicsTimestamps(1, 1, 1, 1, 0, 1, 0, 1, 1);
        var physics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.ReportCollisions,
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
            Guid,
            position,
            0x02000001u,
            [],
            [],
            [],
            null,
            null,
            "fixture",
            (uint)ItemType.Creature,
            null,
            0x09000001u,
            PhysicsState: (uint)PhysicsStateFlags.ReportCollisions,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }
}
