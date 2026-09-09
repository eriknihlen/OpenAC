using AcDream.App.Rendering;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Rendering.Wb;

namespace AcDream.App.Tests.Rendering.Wb;

public sealed class DirectionalShadowTerrainPreparedDrawTests
{
    [Fact]
    public void Product_ContainsEveryResidentRangeWithoutVisibilityClassification()
    {
        var product = new DirectionalShadowTerrainPreparedDraws();
        Assert.True(product.TryBegin(frameSequence: 10, estimatedCommands: 3));
        DirectionalShadowTerrainRange[] resident =
        [
            new(FirstIndex: 0, IndexCount: 384),
            new(FirstIndex: 384, IndexCount: 384),
            new(FirstIndex: 768, IndexCount: 384),
        ];
        foreach (DirectionalShadowTerrainRange range in resident)
            product.Add(in range);

        product.Complete(frameSequence: 10);

        Assert.Equal(3, product.Commands.Length);
        Assert.Equal(0u, product.Commands[0].FirstIndex);
        Assert.Equal(384u, product.Commands[1].FirstIndex);
        Assert.Equal(768u, product.Commands[2].FirstIndex);
        Assert.All(
            product.Commands.ToArray(),
            static command =>
            {
                Assert.Equal(384u, command.Count);
                Assert.Equal(1u, command.InstanceCount);
                Assert.Equal(0, command.BaseVertex);
            });
    }

    [Fact]
    public void SameFrame_AllCascadesReuseOneTerrainBuild()
    {
        var product = new DirectionalShadowTerrainPreparedDraws();
        Assert.True(product.TryBegin(frameSequence: 1, estimatedCommands: 1));
        var range = new DirectionalShadowTerrainRange(100, 24);
        product.Add(in range);
        product.Complete(frameSequence: 1);
        long retained = product.RetainedScratchBytes;

        Assert.False(product.TryBegin(frameSequence: 1, estimatedCommands: 500));

        Assert.Equal(1ul, product.BuildSequence);
        Assert.Equal(retained, product.RetainedScratchBytes);
        Assert.Single(product.Commands.ToArray());
    }

    [Fact]
    public void NextFrame_RebuildsIntoRetainedStorage()
    {
        var product = new DirectionalShadowTerrainPreparedDraws();
        Assert.True(product.TryBegin(frameSequence: 1, estimatedCommands: 2));
        var first = new DirectionalShadowTerrainRange(10, 3);
        var second = new DirectionalShadowTerrainRange(20, 6);
        product.Add(in first);
        product.Add(in second);
        product.Complete(frameSequence: 1);
        long retained = product.RetainedScratchBytes;

        Assert.True(product.TryBegin(frameSequence: 2, estimatedCommands: 1));
        product.Add(in second);
        product.Complete(frameSequence: 2);

        Assert.Equal(2ul, product.BuildSequence);
        Assert.Equal(retained, product.RetainedScratchBytes);
        Assert.Single(product.Commands.ToArray());
        Assert.Equal(20u, product.Commands[0].FirstIndex);
    }

    [Fact]
    public void PriorLandscapeSelection_ScansExactAuthoredEightByEightCells()
    {
        var product = new DirectionalShadowTerrainPreparedDraws();
        Assert.True(product.TryBegin(frameSequence: 1, estimatedCommands: 3));
        DirectionalShadowTerrainRange[] resident =
        [
            new(FirstIndex: 0, IndexCount: 384, LandblockId: 0x1111FFFFu),
            new(FirstIndex: 384, IndexCount: 384, LandblockId: 0x2222FFFFu),
            new(FirstIndex: 768, IndexCount: 384, LandblockId: 0x3333FFFFu),
        ];
        foreach (DirectionalShadowTerrainRange range in resident)
            product.Add(in range);
        product.Complete(frameSequence: 1);
        ulong topologySequence = product.BuildSequence;
        var visible = new HashSet<uint>
        {
            0x11110040u,
            0x22220041u,
        };
        var visibility = new RetailLandscapeVisibilityFrame(
            visible,
            HasCompletedWorldView: true);

        product.ApplySelection(in visibility);

        DrawElementsIndirectCommand selected = Assert.Single(product.Commands.ToArray());
        Assert.Equal(0u, selected.FirstIndex);
        Assert.Equal(3, product.ResidentRanges.Length);
        Assert.Equal(topologySequence, product.BuildSequence);

        var empty = new RetailLandscapeVisibilityFrame(
            new HashSet<uint>(),
            HasCompletedWorldView: true);
        product.ApplySelection(in empty);
        Assert.Empty(product.Commands.ToArray());
        Assert.Equal(topologySequence, product.BuildSequence);

        product.ApplySelection(in visibility);
        ZeroAllocationProbe.AssertAllocatesNothing(
            "DirectionalShadowTerrainPreparedDraws.ApplySelection",
            () => product.ApplySelection(in visibility),
            batchSize: 256);
        Assert.Equal(topologySequence, product.BuildSequence);
    }
}
