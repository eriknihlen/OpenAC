using System.Numerics;
using AcDream.App.Physics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;

namespace AcDream.App.Tests.Rendering;

public sealed class EntityPhysicsHostTests
{
    [Fact]
    public void NotifyHidden_FansNonOkStatusAndClearsWatcherSubscription()
    {
        const uint watcherId = 0x50000001u;
        const uint targetId = 0x70000001u;
        var hosts = new Dictionary<uint, IPhysicsObjHost>();
        var watcherUpdates = new List<TargetInfo>();

        EntityPhysicsHost watcher = CreateHost(
            watcherId,
            hosts,
            watcherUpdates.Add);
        EntityPhysicsHost target = CreateHost(targetId, hosts, _ => { });
        hosts.Add(watcherId, watcher);
        hosts.Add(targetId, target);
        watcher.SetTarget(0u, targetId, 1f, 0.1);
        watcherUpdates.Clear();

        target.NotifyHidden();

        TargetInfo hidden = Assert.Single(watcherUpdates);
        Assert.Equal(TargetStatus.ExitWorld, hidden.Status);
        Assert.Null(watcher.TargetManager.TargetInfo);
        Assert.False(target.TargetManager.VoyeurTable!.ContainsKey(watcherId));

        watcherUpdates.Clear();
        watcher.SetTarget(0u, targetId, 1f, 0.1);

        TargetInfo restored = Assert.Single(watcherUpdates);
        Assert.Equal(TargetStatus.Ok, restored.Status);
        Assert.Equal(targetId, restored.ObjectId);
        Assert.True(target.TargetManager.VoyeurTable.ContainsKey(watcherId));
    }

    private static EntityPhysicsHost CreateHost(
        uint id,
        IReadOnlyDictionary<uint, IPhysicsObjHost> hosts,
        Action<TargetInfo> update)
    {
        return new EntityPhysicsHost(
            id,
            getPosition: () => new Position(
                0x01010001u,
                new Vector3(id == 0x50000001u ? 0f : 2f, 0f, 0f),
                Quaternion.Identity),
            getVelocity: () => Vector3.Zero,
            getRadius: () => 0.5f,
            inContact: () => true,
            minterpMaxSpeed: () => 1f,
            curTime: () => 1.0,
            physicsTimerTime: () => 1.0,
            getObjectA: objectId => hosts.GetValueOrDefault(objectId),
            handleUpdateTarget: update,
            interruptCurrentMovement: () => { });
    }
}
