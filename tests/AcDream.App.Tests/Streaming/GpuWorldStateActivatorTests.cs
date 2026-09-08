using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Streaming;
using AcDream.Core.Physics;
using AcDream.Core.Vfx;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using Xunit;
using DatPhysicsScript = DatReaderWriter.DBObjs.PhysicsScript;

namespace AcDream.App.Tests.Streaming;

public sealed class GpuWorldStateActivatorTests
{
    private sealed class RecordingSink : IAnimationHookSink
    {
        public List<(uint EntityId, Vector3 Pos, AnimationHook Hook)> Calls = new();
        public void OnHook(uint entityId, Vector3 worldPos, AnimationHook hook)
            => Calls.Add((entityId, worldPos, hook));
    }

    private sealed record Pipeline(
        GpuWorldState State,
        PhysicsScriptRunner Runner,
        ParticleHookSink Sink,
        EntityEffectPoseRegistry Poses,
        RecordingSink Recording);

    private static Pipeline BuildPipeline(uint scriptId)
    {
        var script = new DatPhysicsScript();
        script.ScriptData.Add(new PhysicsScriptData
        {
            StartTime = 0.0,
            Hook = new CreateParticleHook { EmitterInfoId = 100u, Offset = new Frame() },
        });
        var table = new Dictionary<uint, DatPhysicsScript> { [scriptId] = script };

        var registry = new EmitterDescRegistry();
        var system = new ParticleSystem(registry);
        var poses = new EntityEffectPoseRegistry();
        var sink = new ParticleHookSink(system, poses);
        var recording = new RecordingSink();
        var runner = new PhysicsScriptRunner(id => table.TryGetValue(id, out var s) ? s : null, recording);
        var activator = new EntityScriptActivator(runner, sink, poses,
            _ => new ScriptActivationInfo(scriptId, Array.Empty<Matrix4x4>()));

        var state = new GpuWorldState(entityScriptActivator: activator);
        return new Pipeline(state, runner, sink, poses, recording);
    }

    private static LoadedLandblock MakeStubLandblock(uint canonicalId, params WorldEntity[] entities)
        => new(canonicalId, new LandBlock(), entities);

    private static WorldEntity DatHydrated(uint id, Vector3 pos) => new()
    {
        Id = id,
        ServerGuid = 0u,
        SourceGfxObjOrSetupId = 0x02000001u,
        Position = pos,
        Rotation = Quaternion.Identity,
        MeshRefs = Array.Empty<MeshRef>(),
    };

    private static WorldEntity Live(uint serverGuid, Vector3 pos) => new()
    {
        Id = serverGuid,
        ServerGuid = serverGuid,
        SourceGfxObjOrSetupId = 0x02000001u,
        Position = pos,
        Rotation = Quaternion.Identity,
        MeshRefs = Array.Empty<MeshRef>(),
    };

    [Fact]
    public void AddLandblock_FiresActivatorForDatHydratedEntity()
    {
        var p = BuildPipeline(scriptId: 0xAAu);
        var entity = DatHydrated(id: 0x40A9B401u, pos: new Vector3(1, 2, 3));
        var lb = MakeStubLandblock(0xA9B4FFFFu, entity);

        p.State.AddLandblock(lb);

        p.Runner.Tick(0.001f);
        Assert.Single(p.Recording.Calls);
        Assert.Equal(entity.Id, p.Recording.Calls[0].EntityId);
        Assert.Equal(new Vector3(1, 2, 3), p.Recording.Calls[0].Pos);
    }

    [Fact]
    public void PendingLiveProjection_DoesNotFireLogicalActivator()
    {
        var p = BuildPipeline(scriptId: 0xAAu);
        var live = Live(serverGuid: 0xCAFEu, pos: Vector3.Zero);

        p.State.PlaceLiveEntityProjection(0xA9B4FFFFu, live);
        var emptyLb = MakeStubLandblock(0xA9B4FFFFu);
        p.State.AddLandblock(emptyLb);

        p.Runner.Tick(0.001f);
        Assert.Empty(p.Recording.Calls);
    }

    [Fact]
    public void RemoveLandblock_FiresOnRemoveForDatHydratedEntity()
    {
        var p = BuildPipeline(scriptId: 0xAAu);
        var entity = DatHydrated(id: 0x40A9B401u, pos: Vector3.Zero);
        var lb = MakeStubLandblock(0xA9B4FFFFu, entity);

        p.State.AddLandblock(lb);
        Assert.Equal(1, p.Runner.ActiveScriptCount);

        p.State.RemoveLandblock(0xA9B4FFFFu);
        Assert.Equal(0, p.Runner.ActiveScriptCount);
    }

    [Fact]
    public void SameIdLandblockRehydrate_RebindsOwnerWithoutReplayingDefaultScript()
    {
        var p = BuildPipeline(scriptId: 0xAAu);
        WorldEntity first = DatHydrated(
            id: 0x40A9B401u,
            pos: new Vector3(1f, 2f, 3f));
        WorldEntity replacement = DatHydrated(
            id: first.Id,
            pos: new Vector3(8f, 9f, 10f));

        p.State.AddLandblock(MakeStubLandblock(0xA9B4FFFFu, first));
        Assert.Equal(1, p.Runner.ActiveScriptCount);

        p.State.AddLandblock(MakeStubLandblock(0xA9B4FFFFu, replacement));

        Assert.Equal(1, p.Runner.ActiveScriptCount);
        Assert.True(p.Poses.TryGetRootPose(first.Id, out Matrix4x4 root));
        Assert.Equal(replacement.Position, root.Translation);
        p.Runner.Tick(0.001f);
        Assert.Single(p.Recording.Calls);
        Assert.Equal(replacement.Position, p.Recording.Calls[0].Pos);
    }

    [Fact]
    public void SameLandblockRehydrate_OmittedStaticRetiresScriptAndPose()
    {
        var p = BuildPipeline(scriptId: 0xAAu);
        WorldEntity entity = DatHydrated(0x40A9B401u, Vector3.Zero);
        p.State.AddLandblock(MakeStubLandblock(0xA9B4FFFFu, entity));
        Assert.Equal(1, p.Runner.ActiveScriptCount);
        Assert.True(p.Poses.TryGetRootPose(entity.Id, out _));

        p.State.AddLandblock(MakeStubLandblock(0xA9B4FFFFu));

        Assert.Equal(0, p.Runner.ActiveScriptCount);
        Assert.False(p.Poses.TryGetRootPose(entity.Id, out _));
    }

    [Fact]
    public void AddEntitiesToExistingLandblock_FiresActivatorForEachPromoted()
    {
        var p = BuildPipeline(scriptId: 0xAAu);
        var emptyLb = MakeStubLandblock(0xA9B4FFFFu);
        p.State.AddLandblock(emptyLb);

        var promoted = new[]
        {
            DatHydrated(id: 0x40A9B401u, pos: Vector3.Zero),
            DatHydrated(id: 0x40A9B402u, pos: Vector3.UnitX),
        };
        p.State.AddEntitiesToExistingLandblock(0xA9B4FFFFu, promoted);

        p.Runner.Tick(0.001f);
        Assert.Equal(2, p.Recording.Calls.Count);
    }

    [Fact]
    public void RemoveEntitiesFromLandblock_FiresOnRemoveForDatHydratedEntities()
    {
        var p = BuildPipeline(scriptId: 0xAAu);
        var entity = DatHydrated(id: 0x40A9B401u, pos: Vector3.Zero);
        var lb = MakeStubLandblock(0xA9B4FFFFu, entity);
        p.State.AddLandblock(lb);
        Assert.Equal(1, p.Runner.ActiveScriptCount);

        p.State.RemoveEntitiesFromLandblock(0xA9B4FFFFu);
        Assert.Equal(0, p.Runner.ActiveScriptCount);
    }
}
