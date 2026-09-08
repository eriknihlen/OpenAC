using System;
using System.Numerics;
using AcDream.App.Rendering.Wb;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using Xunit;

namespace AcDream.Core.Tests.Rendering.Wb;

public sealed class EntitySpawnAdapterTests
{
    // ── Happy-path: server-spawned entity ─────────────────────────────────

    [Fact]
    public void OnCreate_ServerSpawnedEntity_RegistersAnimatedEntityState()
    {
        var lifetime = new RecordingTextureLifetime();
        var adapter = MakeAdapter(lifetime);
        var entity = MakeEntity(id: 1, serverGuid: 0xDEAD0001u);

        var state = adapter.OnCreate(entity);

        Assert.NotNull(state);
        Assert.Same(state, adapter.GetState(0xDEAD0001u));
    }

    [Fact]
    public void OnCreate_ServerSpawnedEntity_SequencerIsNotNull()
    {
        var adapter = MakeAdapter();
        var entity = MakeEntity(id: 1, serverGuid: 0xDEAD0002u);

        var state = adapter.OnCreate(entity);

        Assert.NotNull(state!.Sequencer);
    }

    // ── Atlas-tier filter ─────────────────────────────────────────────────

    [Fact]
    public void OnCreate_ProceduralEntity_ReturnsNullAndRegistersNothing()
    {
        var lifetime = new RecordingTextureLifetime();
        var adapter = MakeAdapter(lifetime);
        // ServerGuid == 0 → atlas-tier, must not be processed here.
        var entity = MakeEntity(id: 2, serverGuid: 0u);

        var state = adapter.OnCreate(entity);

        Assert.Null(state);
        Assert.Null(adapter.GetState(0u));
        Assert.Empty(lifetime.ReleasedOwnerIds);
    }

    [Fact]
    public void OnCreate_DoesNotAcquireTexturesBeforeTheSurfaceIsDrawn()
    {
        var lifetime = new RecordingTextureLifetime();
        var adapter = MakeAdapter(lifetime);
        var entity = MakeEntity(id: 10, serverGuid: 0xBEEF0001u);

        adapter.OnCreate(entity);

        Assert.Empty(lifetime.ReleasedOwnerIds);
    }

    // ── OnRemove ─────────────────────────────────────────────────────────

    [Fact]
    public void OnRemove_ReleasesPerEntityState()
    {
        var lifetime = new RecordingTextureLifetime();
        var adapter = MakeAdapter(lifetime);
        var entity = MakeEntity(id: 20, serverGuid: 0xCAFE0001u);

        adapter.OnCreate(entity);
        adapter.SetPresentationResident(entity, resident: true);
        Assert.NotNull(adapter.GetState(0xCAFE0001u));

        adapter.OnRemove(0xCAFE0001u);
        Assert.Null(adapter.GetState(0xCAFE0001u));
        Assert.Equal([20u], lifetime.ReleasedOwnerIds);
    }

    [Fact]
    public void OnRemove_UnknownGuid_NoOps()
    {
        var adapter = MakeAdapter();

        // Must not throw.
        adapter.OnRemove(0xDEADBEEFu);
    }

    // ── Multiple entities ─────────────────────────────────────────────────

    [Fact]
    public void OnCreate_MultipleEntities_EachGetsOwnState()
    {
        var adapter = MakeAdapter();
        var e1 = MakeEntity(id: 30, serverGuid: 0x11110001u);
        var e2 = MakeEntity(id: 31, serverGuid: 0x11110002u);

        var s1 = adapter.OnCreate(e1);
        var s2 = adapter.OnCreate(e2);

        Assert.NotNull(s1);
        Assert.NotNull(s2);
        Assert.NotSame(s1, s2);
        Assert.Same(s1, adapter.GetState(0x11110001u));
        Assert.Same(s2, adapter.GetState(0x11110002u));
    }

    [Fact]
    public void OnRemove_OnlyReleasesTargetGuid()
    {
        var adapter = MakeAdapter();
        var e1 = MakeEntity(id: 40, serverGuid: 0x22220001u);
        var e2 = MakeEntity(id: 41, serverGuid: 0x22220002u);

        adapter.OnCreate(e1);
        adapter.OnCreate(e2);
        adapter.OnRemove(0x22220001u);

        Assert.Null(adapter.GetState(0x22220001u));
        Assert.NotNull(adapter.GetState(0x22220002u));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static EntitySpawnAdapter MakeAdapter(IEntityTextureLifetime? lifetime = null)
    {
        lifetime ??= new RecordingTextureLifetime();
        return new EntitySpawnAdapter(lifetime, _ => MakeSequencer());
    }

    private static WorldEntity MakeEntity(uint id, uint serverGuid)
        => new WorldEntity
        {
            Id = id,
            ServerGuid = serverGuid,
            SourceGfxObjOrSetupId = 0x02000001u,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs = new[] { new MeshRef(0x01000001u, Matrix4x4.Identity) },
        };

    private static AnimationSequencer MakeSequencer()
        => new AnimationSequencer(new Setup(), new MotionTable(), new NullAnimationLoader());

    // ── Mocks / stubs ─────────────────────────────────────────────────────

    private sealed class RecordingTextureLifetime : IEntityTextureLifetime
    {
        public List<uint> ReleasedOwnerIds { get; } = new();

        public void ReleaseOwner(uint localEntityId) => ReleasedOwnerIds.Add(localEntityId);
    }

    private sealed class NullAnimationLoader : IAnimationLoader
    {
        public Animation? LoadAnimation(uint id) => null;
    }
}
