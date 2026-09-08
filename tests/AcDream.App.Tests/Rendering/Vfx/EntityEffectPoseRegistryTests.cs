using System.Numerics;
using AcDream.App.Rendering.Vfx;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.Rendering.Vfx;

public sealed class EntityEffectPoseRegistryTests
{
    [Fact]
    public void Publish_PreservesIndexedParts_AndUpdateRootDoesNotReplaceThem()
    {
        var poses = new EntityEffectPoseRegistry();
        WorldEntity entity = Entity(7u, new Vector3(1, 2, 3));
        poses.Publish(entity, new[]
        {
            Matrix4x4.Identity,
            Matrix4x4.CreateTranslation(0, 0, 4),
        });

        entity.SetPosition(new Vector3(10, 20, 30));
        Assert.True(poses.UpdateRoot(entity));

        Assert.True(poses.TryGetRootPose(7u, out Matrix4x4 root));
        Assert.Equal(new Vector3(10, 20, 30), root.Translation);
        Assert.True(poses.TryGetPartPose(7u, 1, out Matrix4x4 part));
        Assert.Equal(new Vector3(0, 0, 4), part.Translation);
        Assert.False(poses.TryGetPartPose(7u, -1, out _));
    }

    [Fact]
    public void MovingLiveEntity_CarriesItsEffectsToTheNewCell()
    {
        var poses = new EntityEffectPoseRegistry();
        WorldEntity entity = Entity(11u, new Vector3(1, 2, 3));
        poses.Publish(entity, Array.Empty<Matrix4x4>());
        Assert.True(poses.TryGetCellId(11u, out uint published));
        Assert.Equal(0x01010001u, published);

        entity.SetPosition(new Vector3(30, 2, 3));
        entity.ParentCellId = 0x01010002u;
        Assert.True(poses.UpdateRoot(entity));

        Assert.True(poses.TryGetCellId(11u, out uint moved));
        Assert.Equal(0x01010002u, moved);
    }

    [Fact]
    public void OutdoorStabWithNoRenderParent_UsesItsAuthoredLandcell()
    {
        var poses = new EntityEffectPoseRegistry();
        WorldEntity stab = Entity(12u, new Vector3(4, 5, 6));
        stab.ParentCellId = null;
        stab.EffectCellId = 0x0101001Au;

        poses.Publish(stab, Array.Empty<Matrix4x4>());

        Assert.True(poses.TryGetCellId(12u, out uint cell));
        Assert.Equal(0x0101001Au, cell);
    }

    [Fact]
    public void PublishMeshRefs_ReplacesPartSnapshotImmediately()
    {
        var poses = new EntityEffectPoseRegistry();
        WorldEntity entity = Entity(8u, Vector3.Zero);
        poses.Publish(entity, new[] { Matrix4x4.Identity });
        entity.MeshRefs = new[]
        {
            new MeshRef(0x01000001u, Matrix4x4.CreateTranslation(1, 2, 3)),
            new MeshRef(0x01000002u, Matrix4x4.CreateTranslation(4, 5, 6)),
        };

        poses.PublishMeshRefs(entity);

        Assert.True(poses.TryGetPartPoses(8u, out var parts));
        Assert.Equal(2, parts.Count);
        Assert.Equal(new Vector3(4, 5, 6), parts[1].Translation);
    }

    [Fact]
    public void Publish_MissingMiddlePartRetainsIndicesButCannotBeResolved()
    {
        var poses = new EntityEffectPoseRegistry();
        WorldEntity entity = Entity(81u, Vector3.Zero);

        poses.Publish(
            entity,
            [
                Matrix4x4.CreateTranslation(1, 0, 0),
                Matrix4x4.CreateTranslation(2, 0, 0),
                Matrix4x4.CreateTranslation(3, 0, 0),
            ],
            [true, false, true]);

        Assert.False(poses.TryGetPartPose(81u, 1, out _));
        Assert.True(poses.TryGetPartPose(81u, 2, out Matrix4x4 third));
        Assert.Equal(3f, third.Translation.X);
    }

    [Fact]
    public void PublishMeshRefs_PrefersStableIndexedEntityPosesOverCompactedDrawables()
    {
        var poses = new EntityEffectPoseRegistry();
        WorldEntity entity = Entity(82u, Vector3.Zero);
        entity.MeshRefs =
        [
            new MeshRef(1u, Matrix4x4.CreateTranslation(1, 0, 0)),
            new MeshRef(3u, Matrix4x4.CreateTranslation(3, 0, 0)),
        ];
        entity.SetIndexedPartPoses(
            [
                Matrix4x4.CreateTranslation(1, 0, 0),
                Matrix4x4.CreateTranslation(2, 0, 0),
                Matrix4x4.CreateTranslation(3, 0, 0),
            ],
            [true, false, true]);

        poses.PublishMeshRefs(entity);

        Assert.False(poses.TryGetPartPose(82u, 1, out _));
        Assert.True(poses.TryGetPartPose(82u, 2, out Matrix4x4 third));
        Assert.Equal(3f, third.Translation.X);
    }

    [Fact]
    public void AnimationHookQueue_DrainsAgainstPosePublishedAfterCapture()
    {
        var poses = new EntityEffectPoseRegistry();
        WorldEntity entity = Entity(9u, Vector3.Zero);
        poses.Publish(entity, Array.Empty<Matrix4x4>());
        var router = new AnimationHookRouter();
        var sink = new RecordingSink();
        router.Register(sink);
        var queue = new AnimationHookFrameQueue(router, poses);
        var sequencer = new AnimationSequencer(
            new Setup(),
            new MotionTable(),
            new NullAnimationLoader());

        queue.Capture(9u, sequencer, new AnimationHook[] { new SoundHook() });
        entity.SetPosition(new Vector3(4, 5, 6));
        poses.UpdateRoot(entity);
        queue.Drain();

        Assert.Single(sink.Calls);
        Assert.Equal(new Vector3(4, 5, 6), sink.Calls[0].Position);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void AnimationHookQueue_DeleteAndLocalIdReuse_DropsOldIncarnationHooks()
    {
        var poses = new EntityEffectPoseRegistry();
        WorldEntity first = Entity(9u, Vector3.Zero);
        poses.Publish(first, Array.Empty<Matrix4x4>());
        var router = new AnimationHookRouter();
        var sink = new RecordingSink();
        router.Register(sink);
        var queue = new AnimationHookFrameQueue(router, poses);
        var sequencer = new AnimationSequencer(
            new Setup(),
            new MotionTable(),
            new NullAnimationLoader());

        queue.Capture(9u, sequencer, new AnimationHook[] { new SoundHook() });
        Assert.True(poses.Remove(9u));
        WorldEntity replacement = Entity(9u, new Vector3(40f, 50f, 60f));
        poses.Publish(replacement, Array.Empty<Matrix4x4>());

        queue.Drain();

        Assert.Empty(sink.Calls);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void AnimationDoneReuseDuringCapture_DropsOldIncarnationHooks()
    {
        var poses = new EntityEffectPoseRegistry();
        poses.Publish(Entity(9u, Vector3.Zero), Array.Empty<Matrix4x4>());
        var router = new AnimationHookRouter();
        var sink = new RecordingSink();
        router.Register(sink);
        var queue = new AnimationHookFrameQueue(router, poses);
        var sequencer = new AnimationSequencer(
            new Setup(),
            new MotionTable(),
            new NullAnimationLoader());
        sequencer.Manager.AddToQueue(0x10000001u, ticks: 1u);
        sequencer.MotionDoneTarget = (_, success) =>
        {
            Assert.True(success);
            Assert.True(poses.Remove(9u));
            poses.Publish(
                Entity(9u, new Vector3(40f, 50f, 60f)),
                Array.Empty<Matrix4x4>());
        };

        queue.Capture(
            9u,
            sequencer,
            new AnimationHook[]
            {
                new AnimationDoneHook(),
                new SoundHook(),
            });
        queue.Drain();

        Assert.Empty(sink.Calls);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void MultipleAnimationDoneHooks_StopAdvancingDisplacedSequencerAfterReuse()
    {
        var poses = new EntityEffectPoseRegistry();
        poses.Publish(Entity(9u, Vector3.Zero), Array.Empty<Matrix4x4>());
        var queue = new AnimationHookFrameQueue(
            new AnimationHookRouter(),
            poses);
        var sequencer = new AnimationSequencer(
            new Setup(),
            new MotionTable(),
            new NullAnimationLoader());
        sequencer.Manager.AddToQueue(0x10000001u, ticks: 1u);
        sequencer.Manager.AddToQueue(0x10000002u, ticks: 1u);
        int completions = 0;
        sequencer.MotionDoneTarget = (_, success) =>
        {
            Assert.True(success);
            completions++;
            Assert.True(poses.Remove(9u));
            poses.Publish(
                Entity(9u, new Vector3(40f, 50f, 60f)),
                Array.Empty<Matrix4x4>());
        };

        queue.Capture(
            9u,
            sequencer,
            new AnimationHook[]
            {
                new AnimationDoneHook(),
                new AnimationDoneHook(),
            });

        Assert.Equal(1, completions);
        Assert.Single(sequencer.Manager.PendingAnimations);
        queue.Drain();
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void HookSinkReuseDuringDrain_StopsRemainingOldIncarnationHooks()
    {
        var poses = new EntityEffectPoseRegistry();
        poses.Publish(Entity(9u, Vector3.Zero), Array.Empty<Matrix4x4>());
        var router = new AnimationHookRouter();
        bool replaced = false;
        var sink = new CallbackSink(() =>
        {
            if (replaced)
                return;
            replaced = true;
            Assert.True(poses.Remove(9u));
            poses.Publish(
                Entity(9u, new Vector3(40f, 50f, 60f)),
                Array.Empty<Matrix4x4>());
        });
        router.Register(sink);
        var queue = new AnimationHookFrameQueue(router, poses);
        var sequencer = new AnimationSequencer(
            new Setup(),
            new MotionTable(),
            new NullAnimationLoader());

        queue.Capture(
            9u,
            sequencer,
            new AnimationHook[] { new SoundHook(), new SoundHook() });
        queue.Drain();

        Assert.Equal(1, sink.Calls);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void AnimationHookQueue_CompletesMotionStateAtCaptureEvenWhenPoseWasRemoved()
    {
        var poses = new EntityEffectPoseRegistry();
        var queue = new AnimationHookFrameQueue(new AnimationHookRouter(), poses);
        var sequencer = new AnimationSequencer(
            new Setup(),
            new MotionTable(),
            new NullAnimationLoader());
        sequencer.Manager.AddToQueue(0x10000001u, ticks: 1u);

        queue.Capture(
            11u,
            sequencer,
            new AnimationHook[] { new AnimationDoneHook() });

        Assert.Empty(sequencer.Manager.PendingAnimations);
        queue.Drain();

        Assert.Empty(sequencer.Manager.PendingAnimations);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void AnimationHookQueue_DeferredDrainDoesNotOwnPartArrayUseTime()
    {
        var poses = new EntityEffectPoseRegistry();
        var queue = new AnimationHookFrameQueue(new AnimationHookRouter(), poses);
        var sequencer = new AnimationSequencer(
            new Setup(),
            new MotionTable(),
            new NullAnimationLoader());
        sequencer.Manager.AddToQueue(0x10000001u, ticks: 0u);

        queue.Capture(12u, sequencer, Array.Empty<AnimationHook>());
        queue.Drain();

        Assert.Single(sequencer.Manager.PendingAnimations);
    }

    [Fact]
    public void Publish_UsesOutdoorEffectCellWithoutChangingRenderParent()
    {
        var poses = new EntityEffectPoseRegistry();
        WorldEntity entity = Entity(12u, Vector3.Zero);
        entity.ParentCellId = null;
        entity.EffectCellId = 0xA9B4000Bu;

        poses.Publish(entity, Array.Empty<Matrix4x4>());

        Assert.True(poses.TryGetCellId(entity.Id, out uint cellId));
        Assert.Equal(0xA9B4000Bu, cellId);
    }

    [Fact]
    public void ChangeNotifications_SuppressEquivalentStaticPublishes()
    {
        var poses = new EntityEffectPoseRegistry();
        WorldEntity entity = Entity(13u, new Vector3(1, 2, 3));
        var changed = new List<uint>();
        poses.EffectPoseChanged += changed.Add;
        Matrix4x4 part = Matrix4x4.CreateTranslation(0, 0, 1);

        poses.Publish(entity, new[] { part });
        poses.Publish(entity, new[] { part });
        Assert.True(poses.UpdateRoot(entity));
        Assert.Equal(new[] { 13u }, changed);

        entity.SetPosition(new Vector3(4, 5, 6));
        Assert.True(poses.UpdateRoot(entity));
        Assert.Equal(new[] { 13u, 13u }, changed);

        poses.Publish(entity, new[] { Matrix4x4.CreateTranslation(0, 0, 2) });
        Assert.Equal(new[] { 13u, 13u, 13u }, changed);

        Assert.True(poses.Remove(entity.Id));
        Assert.Equal(new[] { 13u, 13u, 13u, 13u }, changed);
    }

    private static WorldEntity Entity(uint id, Vector3 position) => new()
    {
        Id = id,
        ServerGuid = id,
        SourceGfxObjOrSetupId = 0x02000001u,
        Position = position,
        Rotation = Quaternion.Identity,
        MeshRefs = Array.Empty<MeshRef>(),
        ParentCellId = 0x01010001u,
    };

    private sealed class RecordingSink : IAnimationHookSink
    {
        public List<(uint Id, Vector3 Position)> Calls { get; } = new();

        public void OnHook(uint entityId, Vector3 entityWorldPosition, AnimationHook hook) =>
            Calls.Add((entityId, entityWorldPosition));
    }

    private sealed class CallbackSink(Action callback) : IAnimationHookSink
    {
        public int Calls { get; private set; }

        public void OnHook(uint entityId, Vector3 entityWorldPosition, AnimationHook hook)
        {
            Calls++;
            callback();
        }
    }

    private sealed class NullAnimationLoader : IAnimationLoader
    {
        public Animation? LoadAnimation(uint animationId) => null;
    }
}
