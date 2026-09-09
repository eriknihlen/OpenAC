using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Wb;
using AcDream.Core.World;

namespace AcDream.App.Tests.Rendering;

public sealed class PrivateEntityViewportRendererResidencyTests
{
    [Fact]
    public void FirstEntityIsNotPublishedUntilEveryDrawableMeshIsReady()
    {
        const ulong first = 0x0100_0001u;
        const ulong second = 0x0100_0002u;
        var adapter = new RecordingMeshAdapter { ReadyIds = { first } };
        var textures = new RecordingTextureLifetime();
        var slot = CreateSlot(adapter, textures);
        WorldEntity entity = Entity(first, second);

        slot.Set(entity);

        Assert.True(slot.HasPending);
        Assert.Null(slot.Entity);
        Assert.False(slot.PrepareForDraw());
        Assert.Null(slot.Entity);
        Assert.Equal(1, adapter.ReferenceCount(first));
        Assert.Equal(1, adapter.ReferenceCount(second));

        adapter.ReadyIds.Add(second);

        Assert.True(slot.PrepareForDraw());
        Assert.False(slot.HasPending);
        Assert.Same(entity, slot.Entity);

        slot.Dispose();
        Assert.Equal(0, adapter.TotalReferences);
        Assert.Equal(1, textures.ReleaseCount);
    }

    [Fact]
    public void ReplacementKeepsActiveEntityUntilCandidateIsReady()
    {
        const ulong first = 0x0100_0011u;
        const ulong second = 0x0100_0012u;
        var adapter = new RecordingMeshAdapter { ReadyIds = { first } };
        var textures = new RecordingTextureLifetime();
        var slot = CreateSlot(adapter, textures);
        WorldEntity active = Entity(first);
        WorldEntity candidate = Entity(second);
        slot.Set(active);
        Assert.True(slot.PrepareForDraw());

        slot.Set(candidate);

        Assert.True(slot.HasPending);
        Assert.Same(active, slot.Entity);
        Assert.False(slot.PrepareForDraw());
        Assert.Same(active, slot.Entity);
        Assert.Equal(1, adapter.ReferenceCount(first));
        Assert.Equal(1, adapter.ReferenceCount(second));

        adapter.ReadyIds.Add(second);

        Assert.True(slot.PrepareForDraw());
        Assert.Same(candidate, slot.Entity);
        Assert.Equal(0, adapter.ReferenceCount(first));
        Assert.Equal(1, adapter.ReferenceCount(second));
        Assert.Equal(1, textures.ReleaseCount);

        slot.Dispose();
        Assert.Equal(0, adapter.TotalReferences);
    }

    [Fact]
    public void InPlaceMeshChangeIsDetectedAtTheDrawBarrier()
    {
        const ulong first = 0x0100_0021u;
        const ulong second = 0x0100_0022u;
        var adapter = new RecordingMeshAdapter { ReadyIds = { first } };
        var slot = CreateSlot(adapter, new RecordingTextureLifetime());
        WorldEntity entity = Entity(first);
        slot.Set(entity);
        Assert.True(slot.PrepareForDraw());

        entity.MeshRefs = [new MeshRef((uint)second, Matrix4x4.Identity)];

        Assert.False(slot.PrepareForDraw());
        Assert.True(slot.HasPending);
        Assert.Same(entity, slot.Entity);
        Assert.Equal(1, adapter.ReferenceCount(first));
        Assert.Equal(1, adapter.ReferenceCount(second));

        adapter.ReadyIds.Add(second);

        Assert.True(slot.PrepareForDraw());
        Assert.False(slot.HasPending);
        Assert.Equal(0, adapter.ReferenceCount(first));
        Assert.Equal(1, adapter.ReferenceCount(second));

        slot.Dispose();
    }

    [Fact]
    public void NewerCandidateReleasesSupersededPendingOwner()
    {
        const ulong first = 0x0100_0031u;
        const ulong second = 0x0100_0032u;
        var adapter = new RecordingMeshAdapter();
        var slot = CreateSlot(adapter, new RecordingTextureLifetime());

        slot.Set(Entity(first));
        slot.Set(Entity(second));

        Assert.True(slot.HasPending);
        Assert.Equal(0, adapter.ReferenceCount(first));
        Assert.Equal(1, adapter.ReferenceCount(second));

        slot.Dispose();
        Assert.Equal(0, adapter.TotalReferences);
    }

    [Fact]
    public void UnresolvedPartOverrideDoesNotBlockResolvedDrawableMeshes()
    {
        const ulong drawable = 0x0100_0041u;
        const ulong overrideId = 0x0100_0042u;
        var adapter = new RecordingMeshAdapter { ReadyIds = { drawable } };
        var slot = CreateSlot(adapter, new RecordingTextureLifetime());
        WorldEntity entity = Entity(
            [drawable],
            [new PartOverride(3, (uint)overrideId)]);

        slot.Set(entity);

        Assert.True(slot.PrepareForDraw());
        Assert.Same(entity, slot.Entity);
        Assert.Equal(1, adapter.ReferenceCount(overrideId));

        slot.Dispose();
        Assert.Equal(0, adapter.TotalReferences);
    }

    [Fact]
    public void ClearReleasesBothActiveAndPendingOwners()
    {
        const ulong first = 0x0100_0051u;
        const ulong second = 0x0100_0052u;
        var adapter = new RecordingMeshAdapter { ReadyIds = { first } };
        var textures = new RecordingTextureLifetime();
        var slot = CreateSlot(adapter, textures);
        slot.Set(Entity(first));
        Assert.True(slot.PrepareForDraw());
        slot.Set(Entity(second));

        slot.Set(null);

        Assert.Null(slot.Entity);
        Assert.False(slot.HasPending);
        Assert.Equal(0, adapter.TotalReferences);
        Assert.Equal(1, textures.ReleaseCount);

        slot.Dispose();
        Assert.Equal(1, textures.ReleaseCount);
    }

    private static PrivateEntityViewportRenderer.EntitySlot CreateSlot(
        IWbMeshAdapter adapter,
        IEntityTextureLifetime textures) =>
        new(adapter, textures, ownerLocalId: 0xDA11_D012u, "test viewport");

    private static WorldEntity Entity(params ulong[] meshIds) =>
        Entity(meshIds, []);

    private static WorldEntity Entity(
        IReadOnlyList<ulong> meshIds,
        IReadOnlyList<PartOverride> partOverrides) => new()
        {
            Id = 0xDA11_D012u,
            ServerGuid = 0xDA11_D011u,
            SourceGfxObjOrSetupId = 0x0200_0001u,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs = meshIds
                .Select(static id => new MeshRef((uint)id, Matrix4x4.Identity))
                .ToArray(),
            PartOverrides = partOverrides,
        };

    private sealed class RecordingTextureLifetime : IEntityTextureLifetime
    {
        public int ReleaseCount { get; private set; }

        public void ReleaseOwner(uint localEntityId) => ReleaseCount++;
    }

    private sealed class RecordingMeshAdapter : IWbMeshAdapter
    {
        private readonly Dictionary<ulong, int> _references = [];

        public HashSet<ulong> ReadyIds { get; } = [];
        public int TotalReferences => _references.Values.Sum();

        public int ReferenceCount(ulong id) => _references.GetValueOrDefault(id);

        public bool IsRenderDataReady(ulong id) => ReadyIds.Contains(id);

        public void IncrementRefCount(ulong id) =>
            _references[id] = ReferenceCount(id) + 1;

        public void DecrementRefCount(ulong id)
        {
            int current = ReferenceCount(id);
            if (current <= 0)
                throw new InvalidOperationException("reference underflow");
            _references[id] = current - 1;
        }
    }
}
