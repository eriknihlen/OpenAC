using AcDream.App.Rendering.Gpu;

namespace AcDream.App.Rendering.Wb;

public sealed partial class WbDrawDispatcher
{
    internal sealed class DirectionalShadowReceiverPipelineState : IDisposable
    {
        private readonly MeshPipelineSet _backbuffer;
        private readonly MeshPipelineSet _offscreen;

        internal DirectionalShadowReceiverPipelineState(
            IDirectionalShadowReceiverSource source,
            MeshPipelineSet backbuffer,
            MeshPipelineSet offscreen)
        {
            Source = source;
            _backbuffer = backbuffer;
            _offscreen = offscreen;
        }

        internal IDirectionalShadowReceiverSource Source { get; }

        internal MeshPipelineSet ForSampleCount(int sampleCount) =>
            sampleCount > 1 ? _backbuffer : _offscreen;

        public void Dispose()
        {
            DisposeMeshPipelineSet(_backbuffer);
            if (!ReferenceEquals(_offscreen, _backbuffer))
                DisposeMeshPipelineSet(_offscreen);
        }
    }

    private DirectionalShadowReceiverPipelineState? _directionalShadowReceiver;

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

        MeshPipelineSet? backbuffer = null;
        MeshPipelineSet? offscreen = null;
        try
        {
            backbuffer = CreateMeshPipelineSet(
                device,
                sampleCount,
                baseShaders: source.PipelineShaders.WorldReceiver,
                namePrefix: "wb-mesh-atmospheric",
                usesRenderPackShaderAbi: true);
            offscreen = sampleCount == 1
                ? backbuffer
                : CreateMeshPipelineSet(
                    device,
                    1,
                    baseShaders: source.PipelineShaders.WorldReceiver,
                    namePrefix: "wb-mesh-atmospheric",
                    usesRenderPackShaderAbi: true);
            return new DirectionalShadowReceiverPipelineState(source, backbuffer, offscreen);
        }
        catch
        {
            DisposeMeshPipelineSet(backbuffer);
            if (!ReferenceEquals(offscreen, backbuffer))
                DisposeMeshPipelineSet(offscreen);
            throw;
        }
    }

    internal DirectionalShadowReceiverPipelineState? SwapDirectionalShadowReceiver(
        DirectionalShadowReceiverPipelineState? candidate)
    {
        DirectionalShadowReceiverPipelineState? prior = _directionalShadowReceiver;
        _directionalShadowReceiver = candidate;
        return prior;
    }

    private MeshPipelineSet PipelinesFor(
        IGpuPassEncoder encoder,
        IGpuFrame frame,
        out DirectionalShadowFrameBinding shadowBinding)
    {
        DirectionalShadowReceiverPipelineState? receiver = _directionalShadowReceiver;
        IDirectionalShadowReceiverSource? source = receiver?.Source;
        shadowBinding = DirectionalShadowFrameBinding.Disabled;
        bool bindingValid = source is not null
            && source.TryGetCurrentFrameBinding(frame, out shadowBinding);
        if (DirectionalShadowReceiverPolicy.ShouldSelectReceiverPipeline(
                encoder.Pass.Name,
                source is not null,
                bindingValid))
        {
            return receiver!.ForSampleCount(encoder.Pass.SampleCount);
        }
        return PipelinesFor(encoder);
    }

    internal static void BindDirectionalShadowReceiver(
        IGpuPassEncoder encoder,
        in DirectionalShadowFrameBinding binding)
    {
        if (binding.Buffer is null)
            return;
        encoder.BindUniformBuffer(
            GpuBindingModel.UniformDirectionalShadow,
            binding.Buffer,
            binding.OffsetBytes,
            binding.SizeBytes);
        if (binding.AtmosphericFrame.IsBound)
        {
            encoder.BindUniformBuffer(
                GpuBindingModel.UniformAtmosphericFrame,
                binding.AtmosphericFrame.Buffer!,
                binding.AtmosphericFrame.OffsetBytes,
                binding.AtmosphericFrame.SizeBytes);
        }
    }

    private void DisposeDirectionalShadowReceiverPipelines()
    {
        DirectionalShadowReceiverPipelineState? state =
            SwapDirectionalShadowReceiver(null);
        state?.Dispose();
    }
}
