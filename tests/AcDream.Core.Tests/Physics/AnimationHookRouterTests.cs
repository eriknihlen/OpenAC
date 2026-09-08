using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public sealed class AnimationHookRouterTests
{
    private sealed class RecordingSink : IAnimationHookSink
    {
        public readonly List<(uint EntityId, Vector3 Pos, AnimationHook Hook)> Events = new();
        public void OnHook(uint entityId, Vector3 entityWorldPosition, AnimationHook hook)
        {
            Events.Add((entityId, entityWorldPosition, hook));
        }
    }

    [Fact]
    public void Register_SingleSink_ReceivesHook()
    {
        var router = new AnimationHookRouter();
        var sink = new RecordingSink();
        router.Register(sink);

        var hook = new SoundHook { Direction = AnimationHookDir.Both };
        router.OnHook(entityId: 42, entityWorldPosition: new Vector3(1, 2, 3), hook);

        Assert.Single(sink.Events);
        Assert.Equal(42u, sink.Events[0].EntityId);
        Assert.Equal(new Vector3(1, 2, 3), sink.Events[0].Pos);
        Assert.Same(hook, sink.Events[0].Hook);
    }

    [Fact]
    public void Register_MultipleSinks_AllReceiveHook()
    {
        var router = new AnimationHookRouter();
        var sinkA = new RecordingSink();
        var sinkB = new RecordingSink();
        var sinkC = new RecordingSink();
        router.Register(sinkA);
        router.Register(sinkB);
        router.Register(sinkC);

        router.OnHook(1, Vector3.Zero, new AttackHook { Direction = AnimationHookDir.Forward });

        Assert.Single(sinkA.Events);
        Assert.Single(sinkB.Events);
        Assert.Single(sinkC.Events);
    }

    [Fact]
    public void Register_SameSinkTwice_IsIdempotent()
    {
        var router = new AnimationHookRouter();
        var sink = new RecordingSink();
        router.Register(sink);
        router.Register(sink);

        router.OnHook(1, Vector3.Zero, new SoundHook { Direction = AnimationHookDir.Both });

        Assert.Single(sink.Events);  // Not two events — registration deduped.
        Assert.Single(router.Sinks);
    }

    [Fact]
    public void Unregister_RemovesSink()
    {
        var router = new AnimationHookRouter();
        var sinkA = new RecordingSink();
        var sinkB = new RecordingSink();
        router.Register(sinkA);
        router.Register(sinkB);
        router.Unregister(sinkA);

        router.OnHook(1, Vector3.Zero, new SoundHook { Direction = AnimationHookDir.Both });

        Assert.Empty(sinkA.Events);
        Assert.Single(sinkB.Events);
    }

    [Fact]
    public void OnHook_SinkThrows_OtherSinksStillReceive()
    {
        // A misbehaving sink must not poison the dispatch.
        var router = new AnimationHookRouter();
        router.Register(new ThrowingSink());
        var recording = new RecordingSink();
        router.Register(recording);

        router.OnHook(1, Vector3.Zero, new SoundHook { Direction = AnimationHookDir.Both });

        Assert.Single(recording.Events);
    }

    [Fact]
    public void NullAnimationHookSink_AcceptsAnyHookWithoutCrashing()
    {
        // Trivially verify the null sink exists and swallows hooks.
        var sink = NullAnimationHookSink.Instance;
        sink.OnHook(1, Vector3.Zero, new SoundHook { Direction = AnimationHookDir.Both });
        sink.OnHook(2, new Vector3(1, 2, 3), new AttackHook { Direction = AnimationHookDir.Forward });
    }

    private sealed class ThrowingSink : IAnimationHookSink
    {
        public void OnHook(uint entityId, Vector3 entityWorldPosition, AnimationHook hook)
        {
            throw new System.Exception("bad sink");
        }
    }
}
