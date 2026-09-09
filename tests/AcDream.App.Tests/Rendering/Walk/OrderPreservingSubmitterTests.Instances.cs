using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Walk;
using AcDream.App.Rendering.Wb;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.Core.Meshing;
using DatReaderWriter.Enums;

namespace AcDream.App.Tests.Rendering.Walk;

public sealed partial class OrderPreservingSubmitterTests
{
    [Fact]
    public void InstancedRanges_CompactDrawsAndBatchesButKeepAllInstanceSidecars()
    {
        using var fx = new DispatcherFixture();
        using DrawScope draw = fx.BeginDraw();
        fx.Dispatcher.BeginDirectionalShadowTransformFrame(
            draw.Frame, new[] { Matrix4x4.Identity, Matrix4x4.Identity });
        OrderedDrawCommand first = MakeCommand(3) with { AllowInstanceMerge = true };
        OrderedDrawCommand secondMesh = MakeCommand(7) with { AllowInstanceMerge = true };
        OrderedDrawStream stream = StreamOf(
            first with { ClipSlot = 11, Alpha = 0.1f },
            first with { Transform = Matrix4x4.CreateTranslation(9, 8, 7), ClipSlot = 22, Alpha = 0.2f },
            secondMesh with { ClipSlot = 33, Alpha = 0.3f },
            secondMesh with { ClipSlot = 44, Alpha = 0.4f });

        fx.Dispatcher.PrepareOrderedStream(draw.Frame, stream, Matrix4x4.Identity, new[] { 2 });
        fx.Dispatcher.DrawOrderedRange(draw.Pass, 0, 2);
        fx.Dispatcher.DrawOrderedRange(draw.Pass, 2, 2);

        Assert.Equal(new[] { (0, 1), (1, 1) }, DecodeDrawRanges(fx.Device));
        var runs = DecodeRuns(fx.Device);
        Assert.Equal(new[] { 0, 1 }, runs.Select(run => run.Constants.DrawIdOffset));
        var calls = fx.Device.Calls.OfType<GpuRecordedMultiDrawIndirect>().ToArray();
        var commands = MemoryMarshal.Cast<byte, DrawElementsIndirectCommand>(
            fx.Device.RingBytes.Slice((int)calls[0].OffsetBytes, 2 * WbDrawDispatcher.DrawCommandStride)).ToArray();
        Assert.Equal(new[] { 2u, 2u }, commands.Select(command => command.InstanceCount));
        Assert.Equal(new[] { 2u, 4u }, commands.Select(command => command.BaseInstance));
        Assert.Equal(new[] { first.Key.FirstIndex, secondMesh.Key.FirstIndex }, commands.Select(command => command.FirstIndex));
        Assert.Equal(new[] { first.Key.BaseVertex, secondMesh.Key.BaseVertex }, commands.Select(command => command.BaseVertex));
        Assert.Equal(new[] { 3u, 7u }, ReadSection<WbDrawDispatcher.BatchDataPublic>(GpuBindingModel.StorageBatches).Select(batch => batch.TextureIndex));
        Assert.Equal(new[] { 11u, 22u, 33u, 44u }, ReadSection<uint>(GpuBindingModel.StorageClipSlots));
        Assert.Equal(new[] { 0.1f, 0.2f, 0.3f, 0.4f }, ReadSection<float>(GpuBindingModel.StorageInstanceAlpha));
        Matrix4x4[] transforms = ReadSection<Matrix4x4>(GpuBindingModel.StorageInstances);
        Assert.Equal(stream.Transforms, transforms.Skip(2).Take(4));

        T[] ReadSection<T>(uint binding) where T : unmanaged
        {
            GpuRecordedStorageBind bind = fx.Device.Calls.OfType<GpuRecordedStorageBind>()
                .Last(call => call.Binding == binding);
            return MemoryMarshal.Cast<byte, T>(fx.Device.RingBytes.Slice((int)bind.OffsetBytes, (int)bind.SizeBytes)).ToArray();
        }
    }

    [Fact]
    public void InstanceRuns_RequireBothCommandsToOptInAndNeverReorderMeshes()
    {
        OrderedDrawCommand a = MakeCommand(0) with { AllowInstanceMerge = true };
        OrderedDrawCommand b = MakeCommand(1) with { AllowInstanceMerge = true };
        OrderedDrawStream stream = StreamOf(a, a, a with { AllowInstanceMerge = false }, a, b, b, a);
        var instances = new List<WbDrawDispatcher.OrderedMergeRun>();
        WbDrawDispatcher.BuildOrderedInstanceRuns(stream, WbDrawDispatcher.BuildOrderedMergeRuns(stream), instances);
        Assert.Equal(new[] { (0, 2), (2, 1), (3, 1), (4, 2), (6, 1) },
            instances.Select(run => (run.FirstCommand, run.CommandCount)));
        stream.Reset();
        stream.Append(MakeCommand(0));
        Assert.Equal(new[] { false }, stream.AllowInstanceMerges);
    }

    [Theory]
    [InlineData("stage")]
    [InlineData("detail")]
    [InlineData("pipeline")]
    [InlineData("cull")]
    [InlineData("break")]
    [InlineData("texture")]
    public void InstanceRuns_PreserveEverySubmissionBoundary(string boundary)
    {
        OrderedDrawCommand a = MakeCommand(0) with { AllowInstanceMerge = true };
        OrderedDrawCommand b = boundary switch
        {
            "stage" => a with { Stage = WalkDrawStage.OutdoorStatic },
            "detail" => a with { DetailCategory = 1 },
            "pipeline" => a with { Key = a.Key with { Translucency = TranslucencyKind.AlphaBlend } },
            "cull" => a with { Key = a.Key with { CullMode = CullMode.None } },
            "texture" => a with { Key = a.Key with { TextureSlot = new GpuTextureSlot(17) } },
            _ => a,
        };
        OrderedDrawStream stream = StreamOf(a, a, b, b);
        var instances = new List<WbDrawDispatcher.OrderedMergeRun>();
        var runs = WbDrawDispatcher.BuildOrderedMergeRuns(stream, boundary == "break" ? new[] { 2 } : null);
        WbDrawDispatcher.BuildOrderedInstanceRuns(stream, runs, instances);
        Assert.Equal(boundary == "detail" ? new[] { (0, 2), (2, 1), (3, 1) } : new[] { (0, 2), (2, 2) },
            instances.Select(run => (run.FirstCommand, run.CommandCount)));
    }
}
