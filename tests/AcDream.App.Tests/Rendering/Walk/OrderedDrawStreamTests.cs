using System.Numerics;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Wb;
using AcDream.App.Rendering.Walk;
using AcDream.Core.Meshing;
using DatReaderWriter.Enums;

namespace AcDream.App.Tests.Rendering.Walk;

public sealed class OrderedDrawStreamTests
{
    private static OrderedDrawCommand MakeCommand(
        int index,
        WalkDrawStage stage = WalkDrawStage.Terrain,
        TranslucencyKind translucency = TranslucencyKind.Opaque,
        CullMode cullMode = CullMode.CounterClockwise,
        uint detailCategory = 0) =>
        new(
            Key: new GroupKey(
                FirstIndex: (uint)index * 3,
                BaseVertex: index * 4,
                IndexCount: 3,
                TextureSlot: new GpuTextureSlot((uint)index),
                TextureLayer: 0,
                Translucency: translucency,
                MaterialState: RetailSetSurfaceMaterialState.Opaque,
                FoliageFlags: 0,
                CullMode: cullMode),
            Transform: Matrix4x4.CreateTranslation(index, index * 2, index * 3),
            Stage: stage,
            CellId: 0x8C040100u + (uint)index,
            ClipSlot: (uint)index + 1,
            Lights: WbDrawDispatcher.InstanceLightSet.Disabled,
            IndoorFlag: (uint)(index % 2),
            Alpha: 1f - index * 0.01f,
            SelectionLighting: new Vector2(index, index + 1),
            DetailCategory: detailCategory);

    [Fact]
    public void EmptyStream_HasZeroCount()
    {
        var stream = new OrderedDrawStream();

        Assert.Equal(0, stream.Count);
        Assert.Empty(stream.Keys);
        Assert.Empty(stream.Transforms);
        Assert.Empty(stream.Stages);
        Assert.Empty(stream.CellIds);
        Assert.Empty(stream.ClipSlots);
        Assert.Empty(stream.Lights);
        Assert.Empty(stream.IndoorFlags);
        Assert.Empty(stream.Alphas);
        Assert.Empty(stream.SelectionLighting);
        Assert.Empty(stream.DetailCategories);
    }

    [Fact]
    public void Append_GrowsCountAndEveryParallelListInLockstep()
    {
        var stream = new OrderedDrawStream();

        for (int i = 0; i < 5; i++)
            stream.Append(MakeCommand(i));

        Assert.Equal(5, stream.Count);
        Assert.Equal(5, stream.Keys.Count);
        Assert.Equal(5, stream.Transforms.Count);
        Assert.Equal(5, stream.Stages.Count);
        Assert.Equal(5, stream.CellIds.Count);
        Assert.Equal(5, stream.ClipSlots.Count);
        Assert.Equal(5, stream.Lights.Count);
        Assert.Equal(5, stream.IndoorFlags.Count);
        Assert.Equal(5, stream.Alphas.Count);
        Assert.Equal(5, stream.SelectionLighting.Count);
        Assert.Equal(5, stream.DetailCategories.Count);
    }

    [Fact]
    public void Append_PreservesEveryFieldAtItsIndex()
    {
        var stream = new OrderedDrawStream();
        OrderedDrawCommand[] commands =
        [
            MakeCommand(0, WalkDrawStage.Terrain),
            MakeCommand(1, WalkDrawStage.CellStatic),
            MakeCommand(2, WalkDrawStage.BuildingShell),
        ];

        foreach (OrderedDrawCommand command in commands)
            stream.Append(command);

        for (int i = 0; i < commands.Length; i++)
        {
            Assert.Equal(commands[i].Key, stream.Keys[i]);
            Assert.Equal(commands[i].Transform, stream.Transforms[i]);
            Assert.Equal(commands[i].Stage, stream.Stages[i]);
            Assert.Equal(commands[i].CellId, stream.CellIds[i]);
            Assert.Equal(commands[i].ClipSlot, stream.ClipSlots[i]);
            Assert.Equal(commands[i].Lights, stream.Lights[i]);
            Assert.Equal(commands[i].IndoorFlag, stream.IndoorFlags[i]);
            Assert.Equal(commands[i].Alpha, stream.Alphas[i]);
            Assert.Equal(commands[i].SelectionLighting, stream.SelectionLighting[i]);
            Assert.Equal(commands[i].DetailCategory, stream.DetailCategories[i]);
        }
    }

    [Theory]
    [InlineData(WalkDrawStage.Terrain)]
    [InlineData(WalkDrawStage.CellStatic)]
    [InlineData(WalkDrawStage.BuildingShell)]
    [InlineData(WalkDrawStage.PortalPunch)]
    [InlineData(WalkDrawStage.LookInStatic)]
    [InlineData(WalkDrawStage.Dynamic)]
    internal void Append_AcceptsEveryStageIncludingPortalPunch(WalkDrawStage stage)
    {
        // The stream itself is a dumb data structure — it accepts every stage
        // unconditionally. Only the SUBMITTER rejects PortalPunch (see
        // OrderPreservingSubmitterTests), so stage-separation gates can
        // exercise the boundary at the stream level too.
        var stream = new OrderedDrawStream();

        stream.Append(MakeCommand(0, stage));

        Assert.Equal(1, stream.Count);
        Assert.Equal(stage, stream.Stages[0]);
    }

    [Fact]
    public void Reset_ClearsEveryParallelListToZeroCount()
    {
        var stream = new OrderedDrawStream();
        for (int i = 0; i < 7; i++)
            stream.Append(MakeCommand(i));
        Assert.Equal(7, stream.Count);

        stream.Reset();

        Assert.Equal(0, stream.Count);
        Assert.Empty(stream.Keys);
        Assert.Empty(stream.Transforms);
        Assert.Empty(stream.Stages);
        Assert.Empty(stream.CellIds);
        Assert.Empty(stream.ClipSlots);
        Assert.Empty(stream.Lights);
        Assert.Empty(stream.IndoorFlags);
        Assert.Empty(stream.Alphas);
        Assert.Empty(stream.SelectionLighting);
        Assert.Empty(stream.DetailCategories);
    }

    [Fact]
    public void Reset_ThenAppend_StartsAFreshInOrderSequence()
    {
        var stream = new OrderedDrawStream();
        stream.Append(MakeCommand(0, WalkDrawStage.Terrain));
        stream.Append(MakeCommand(1, WalkDrawStage.Terrain));
        stream.Reset();

        stream.Append(MakeCommand(9, WalkDrawStage.Dynamic));

        Assert.Equal(1, stream.Count);
        Assert.Equal(WalkDrawStage.Dynamic, stream.Stages[0]);
        Assert.Equal(0x8C040100u + 9u, stream.CellIds[0]);
    }
}
