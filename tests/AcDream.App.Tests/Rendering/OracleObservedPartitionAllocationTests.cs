using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Scene;
using AcDream.Core.World;

namespace AcDream.App.Tests.Rendering;

public sealed class OracleObservedPartitionAllocationTests
{
    [Fact]
    internal void AWarmedObservedPartitionAllocatesNearZero()
    {
        const int EntityCount = 20_000;
        var entities = new List<WorldEntity>(EntityCount);
        for (int i = 0; i < EntityCount; i++)
        {
            uint id = unchecked((uint)i * 2654435761u);
            entities.Add(new WorldEntity
            {
                Id = id,
                SourceGfxObjOrSetupId = 0x01000000u + (id & 0xFFFu),
                Position = new Vector3(i % 192, i / 192f, 0f),
                Rotation = Quaternion.Identity,
                MeshRefs = new[] { new MeshRef(0x01000001u, Matrix4x4.Identity) },
            });
        }

        var landblockEntries = new[]
        {
            (LandblockId: 0xA9B4FFFFu,
             AabbMin: Vector3.Zero,
             AabbMax: new Vector3(192f, 192f, 100f),
             Entities: (IReadOnlyList<WorldEntity>)entities,
             AnimatedById: (IReadOnlyDictionary<uint, WorldEntity>?)null),
        };
        var visibleCells = new HashSet<uint>();
        var result = new InteriorEntityPartition.Result();
        var oracle = new CurrentRenderSceneOracle();

        for (int warm = 0; warm < 3; warm++)
        {
            InteriorEntityPartition.Partition(
                result, visibleCells, landblockEntries, oracle);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        InteriorEntityPartition.Partition(
            result, visibleCells, landblockEntries, oracle);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            allocated < 64 * 1024,
            $"warmed observed partition allocated {allocated} bytes for "
            + $"{EntityCount} entities");
    }
}
