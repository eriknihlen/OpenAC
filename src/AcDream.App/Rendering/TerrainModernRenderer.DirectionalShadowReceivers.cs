using AcDream.App.Rendering.Gpu;

namespace AcDream.App.Rendering;

public sealed partial class TerrainModernRenderer
{
    internal sealed class DirectionalShadowReceiverPipelineState(
        IDirectionalShadowReceiverSource source,
        IGpuPipeline pipeline) : IDisposable
    {
        internal IDirectionalShadowReceiverSource Source { get; } = source;

        internal IGpuPipeline Pipeline { get; } = pipeline;

        public void Dispose() => Pipeline.Dispose();
    }

    internal DirectionalShadowReceiverPipelineState? PrepareDirectionalShadowReceiver(
        IDirectionalShadowReceiverSource? source,
        int sampleCount)
    {
        if (source is null)
            return null;
        IGpuDevice device = _device
            ?? throw new InvalidOperationException("Directional receivers require the modern RHI device.");
        if (_scope is null || sampleCount != _scope.SampleCount)
            throw new InvalidOperationException("Receiver and world-pass sample counts must match.");

        return new DirectionalShadowReceiverPipelineState(
            source,
            device.CreatePipeline(
            new GpuPipelineDescription
            {
                Name = "terrain-atmospheric",
                Shaders = source.PipelineShaders.TerrainReceiver,
                VertexLayout = TerrainVertexLayout,
                Topology = GpuPrimitiveTopology.TriangleList,
                Blend = GpuBlendMode.None,
                Depth = new GpuDepthState(true, true, WorldDepthContract.WorldCompare),
                Cull = GpuCullMode.Back,
                FrontFace = GpuFrontFace.CounterClockwise,
                AlphaToCoverage = false,
                ColorWrite = true,
                UsesRenderPackShaderAbi = true,
                SampleCount = sampleCount,
            }));
    }

    internal DirectionalShadowReceiverPipelineState? SwapDirectionalShadowReceiver(
        DirectionalShadowReceiverPipelineState? candidate)
    {
        DirectionalShadowReceiverPipelineState? prior = _directionalShadowReceiver;
        _directionalShadowReceiver = candidate;
        return prior;
    }
}
