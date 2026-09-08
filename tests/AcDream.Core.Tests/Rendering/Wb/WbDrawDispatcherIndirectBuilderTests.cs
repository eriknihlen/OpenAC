using System.Numerics;
using AcDream.App.Rendering.Wb;
using AcDream.Core.Meshing;
using DatReaderWriter.Enums;
using Xunit;

namespace AcDream.Core.Tests.Rendering.Wb;

public sealed class WbDrawDispatcherIndirectBuilderTests
{
    [Fact]
    public void TwoOpaqueGroupsAndOneTransparent_LaysOutContiguouslyOpaqueFirst()
    {
        // Arrange — three groups: 2 opaque (12+1 instances) + 1 transparent (12 instances)
        var groups = new List<WbDrawDispatcher.IndirectGroupInput>
        {
            new(IndexCount: 100, FirstIndex: 0,   BaseVertex: 0,   InstanceCount: 12, FirstInstance: 0,  TextureIndex: 0xAA, TextureLayer: 0, Translucency: TranslucencyKind.Opaque, MaterialState: RetailSetSurfaceMaterialState.Opaque, SurfaceOpacity: 0.75f),
            new(IndexCount: 200, FirstIndex: 100, BaseVertex: 0,   InstanceCount: 12, FirstInstance: 12, TextureIndex: 0xBB, TextureLayer: 0, Translucency: TranslucencyKind.AlphaBlend, MaterialState: RetailSetSurfaceMaterialState.Opaque, SurfaceOpacity: 0.25f),
            new(IndexCount: 50,  FirstIndex: 300, BaseVertex: 100, InstanceCount: 1,  FirstInstance: 24, TextureIndex: 0xCC, TextureLayer: 0, Translucency: TranslucencyKind.Opaque, MaterialState: RetailSetSurfaceMaterialState.Opaque, SurfaceOpacity: 0.5f),
        };

        var indirect = new DrawElementsIndirectCommand[16];
        var batch = new WbDrawDispatcher.BatchDataPublic[16];
        var cull = new CullMode[16];

        // Act
        var result = WbDrawDispatcher.BuildIndirectArrays(groups, indirect, batch, cull);

        // Assert layout
        Assert.Equal(2, result.OpaqueCount);
        Assert.Equal(1, result.TransparentCount);
        Assert.Equal(2 * 20, result.TransparentByteOffset);  // sizeof(DEIC) = 20

        Assert.Equal(100u, indirect[0].Count);
        Assert.Equal(0u,   indirect[0].FirstIndex);
        Assert.Equal(0,    indirect[0].BaseVertex);
        Assert.Equal(12u,  indirect[0].InstanceCount);
        Assert.Equal(0u,   indirect[0].BaseInstance);

        Assert.Equal(50u,  indirect[1].Count);
        Assert.Equal(300u, indirect[1].FirstIndex);
        Assert.Equal(100,  indirect[1].BaseVertex);
        Assert.Equal(1u,   indirect[1].InstanceCount);
        Assert.Equal(24u,  indirect[1].BaseInstance);

        // Transparent section
        Assert.Equal(200u, indirect[2].Count);
        Assert.Equal(100u, indirect[2].FirstIndex);
        Assert.Equal(12u,  indirect[2].InstanceCount);
        Assert.Equal(12u,  indirect[2].BaseInstance);

        // BatchData parallel — same indices as indirect
        Assert.Equal(0xAAu, batch[0].TextureIndex);
        Assert.Equal(0xCCu, batch[1].TextureIndex);
        Assert.Equal(0xBBu, batch[2].TextureIndex);
        Assert.Equal(0.75f, batch[0].SurfaceOpacity);
        Assert.Equal(0.5f, batch[1].SurfaceOpacity);
        Assert.Equal(0.25f, batch[2].SurfaceOpacity);
        Assert.Equal(CullMode.CounterClockwise, cull[0]);
        Assert.Equal(CullMode.CounterClockwise, cull[1]);
        Assert.Equal(CullMode.CounterClockwise, cull[2]);
    }

    [Fact]
    public void CullModes_FollowOpaqueTransparentLayout()
    {
        var groups = new List<WbDrawDispatcher.IndirectGroupInput>
        {
            new(IndexCount: 10, FirstIndex: 0, BaseVertex: 0, InstanceCount: 1, FirstInstance: 0,
                TextureIndex: 0x1, TextureLayer: 0, Translucency: TranslucencyKind.Opaque,
                MaterialState: RetailSetSurfaceMaterialState.Opaque,
                CullMode: CullMode.Clockwise),
            new(IndexCount: 20, FirstIndex: 10, BaseVertex: 0, InstanceCount: 1, FirstInstance: 1,
                TextureIndex: 0x2, TextureLayer: 0, Translucency: TranslucencyKind.AlphaBlend,
                MaterialState: RetailSetSurfaceMaterialState.Opaque,
                CullMode: CullMode.None),
            new(IndexCount: 30, FirstIndex: 30, BaseVertex: 0, InstanceCount: 1, FirstInstance: 2,
                TextureIndex: 0x3, TextureLayer: 0, Translucency: TranslucencyKind.ClipMap,
                MaterialState: RetailSetSurfaceMaterialState.Opaque,
                CullMode: CullMode.Landblock),
        };
        var indirect = new DrawElementsIndirectCommand[4];
        var batch = new WbDrawDispatcher.BatchDataPublic[4];
        var cull = new CullMode[4];

        var result = WbDrawDispatcher.BuildIndirectArrays(groups, indirect, batch, cull);

        Assert.Equal(2, result.OpaqueCount);
        Assert.Equal(CullMode.Clockwise, cull[0]);
        Assert.Equal(CullMode.Landblock, cull[1]);
        Assert.Equal(CullMode.None, cull[2]);
    }

    [Fact]
    public void EmptyGroupList_ProducesZeroCounts()
    {
        var groups = new List<WbDrawDispatcher.IndirectGroupInput>();
        var indirect = new DrawElementsIndirectCommand[0];
        var batch = new WbDrawDispatcher.BatchDataPublic[0];

        var result = WbDrawDispatcher.BuildIndirectArrays(groups, indirect, batch);

        Assert.Equal(0, result.OpaqueCount);
        Assert.Equal(0, result.TransparentCount);
        Assert.Equal(0, result.TransparentByteOffset);
    }

    [Fact]
    public void ClipMapTreatedAsOpaque()
    {
        var groups = new List<WbDrawDispatcher.IndirectGroupInput>
        {
            new(IndexCount: 10, FirstIndex: 0, BaseVertex: 0, InstanceCount: 1, FirstInstance: 0, TextureIndex: 0x1, TextureLayer: 0, Translucency: TranslucencyKind.ClipMap, MaterialState: RetailSetSurfaceMaterialState.Opaque),
        };
        var indirect = new DrawElementsIndirectCommand[4];
        var batch = new WbDrawDispatcher.BatchDataPublic[4];

        var result = WbDrawDispatcher.BuildIndirectArrays(groups, indirect, batch);

        Assert.Equal(1, result.OpaqueCount);
        Assert.Equal(0, result.TransparentCount);
    }

    [Fact]
    public void EveryBuiltMeshMaterialSubsetIsDetailEligible()
    {
        TranslucencyKind[] kinds =
        [
            TranslucencyKind.Opaque,
            TranslucencyKind.ClipMap,
            TranslucencyKind.AlphaBlend,
            TranslucencyKind.Additive,
            TranslucencyKind.InvAlpha,
        ];
        var groups = kinds.Select((kind, index) => new WbDrawDispatcher.IndirectGroupInput(
            IndexCount: 3,
            FirstIndex: (uint)(index * 3),
            BaseVertex: 0,
            InstanceCount: 1,
            FirstInstance: index,
            TextureIndex: (uint)index,
            TextureLayer: 0,
            Translucency: kind,
            MaterialState: RetailSetSurfaceMaterialState.Opaque)).ToList();
        var indirect = new DrawElementsIndirectCommand[kinds.Length];
        var batches = new WbDrawDispatcher.BatchDataPublic[kinds.Length];

        WbDrawDispatcher.BuildIndirectArrays(groups, indirect, batches);

        Assert.All(batches, batch => Assert.Equal(1u, batch.Flags));
    }

    [Fact]
    public void DetailCategoryPredicateCoversEveryInstanceInAnIndirectCommand()
    {
        var command = new DrawElementsIndirectCommand
        {
            BaseInstance = 2,
            InstanceCount = 3,
        };

        Assert.True(WbDrawDispatcher.CommandContainsDetailCategory(
            command,
            [0u, 0u, 0u, 1u, 0u]));
        Assert.False(WbDrawDispatcher.CommandContainsDetailCategory(
            command,
            [1u, 1u, 0u, 0u, 0u]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WbDrawDispatcher.CommandContainsDetailCategory(
                command,
                [0u, 0u, 0u, 1u]));
    }

    [Fact]
    public void OpaqueDetailRunsSkipNonbuildingCommandsAndKeepMixedCommands()
    {
        DrawElementsIndirectCommand[] commands =
        [
            new() { BaseInstance = 0, InstanceCount = 2 },
            new() { BaseInstance = 2, InstanceCount = 2 },
            new() { BaseInstance = 4, InstanceCount = 1 },
        ];

        uint[] mixedCategories = [0u, 0u, 0u, 1u, 0u];
        Assert.True(WbDrawDispatcher.TryGetNextDetailCommandRun(
            commands,
            mixedCategories,
            searchStart: 0,
            exclusiveEnd: commands.Length,
            out WbDrawDispatcher.DetailCommandRun mixedRun));
        Assert.Equal(new WbDrawDispatcher.DetailCommandRun(1, 1), mixedRun);
        Assert.False(WbDrawDispatcher.TryGetNextDetailCommandRun(
            commands,
            mixedCategories,
            searchStart: mixedRun.FirstCommand + mixedRun.CommandCount,
            exclusiveEnd: commands.Length,
            out _));

        Assert.False(WbDrawDispatcher.TryGetNextDetailCommandRun(
            commands,
            new uint[5],
            searchStart: 0,
            exclusiveEnd: commands.Length,
            out _));
    }

    [Fact]
    public void OpaqueDetailRunsCoalesceConsecutiveEligibleCommands()
    {
        DrawElementsIndirectCommand[] commands =
        [
            new() { BaseInstance = 0, InstanceCount = 1 },
            new() { BaseInstance = 1, InstanceCount = 1 },
            new() { BaseInstance = 2, InstanceCount = 1 },
            new() { BaseInstance = 3, InstanceCount = 1 },
        ];
        uint[] categories = [0u, 1u, 1u, 0u];

        Assert.True(WbDrawDispatcher.TryGetNextDetailCommandRun(
            commands,
            categories,
            searchStart: 0,
            exclusiveEnd: commands.Length,
            out WbDrawDispatcher.DetailCommandRun run));
        Assert.Equal(new WbDrawDispatcher.DetailCommandRun(1, 2), run);
    }

    [Fact]
    public void BatchDataPublic_LayoutMatchesPrivateBatchData()
    {
        Assert.Equal(16, System.Runtime.CompilerServices.Unsafe.SizeOf<WbDrawDispatcher.BatchDataPublic>());
        Assert.Equal(0,  (int)System.Runtime.InteropServices.Marshal.OffsetOf<WbDrawDispatcher.BatchDataPublic>(nameof(WbDrawDispatcher.BatchDataPublic.TextureIndex)));
        Assert.Equal(4,  (int)System.Runtime.InteropServices.Marshal.OffsetOf<WbDrawDispatcher.BatchDataPublic>(nameof(WbDrawDispatcher.BatchDataPublic.SurfaceOpacity)));
        Assert.Equal(8,  (int)System.Runtime.InteropServices.Marshal.OffsetOf<WbDrawDispatcher.BatchDataPublic>(nameof(WbDrawDispatcher.BatchDataPublic.TextureLayer)));
        Assert.Equal(12, (int)System.Runtime.InteropServices.Marshal.OffsetOf<WbDrawDispatcher.BatchDataPublic>(nameof(WbDrawDispatcher.BatchDataPublic.Flags)));
    }

    [Fact]
    public void DrawCommandStride_MatchesStructSize()
    {
        Assert.Equal(WbDrawDispatcher.DrawCommandStride, System.Runtime.CompilerServices.Unsafe.SizeOf<DrawElementsIndirectCommand>());
    }
}
