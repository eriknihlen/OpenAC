namespace AcDream.App.Rendering.Gpu;

internal interface IGpuDevice : IDisposable
{
    GpuBackendKind Backend { get; }

    GpuCapabilityRecord Capabilities { get; }

    /// <summary>
    /// Frame-flight-gated resource release. Resource disposal routes through here
    /// so nothing is freed while a submitted frame may still reference it.
    /// </summary>
    IGpuResourceRetirementQueue Retirement { get; }

    /// <summary>GPU timing results from retired frames.</summary>
    IGpuTimerPool Timers { get; }

    GpuTextureSlot DefaultTextureSlot { get; }

    IGpuBuffer CreateBuffer(in GpuBufferDescription description);

    IGpuTexture CreateTexture(in GpuTextureDescription description);

    IGpuSampler CreateSampler(in GpuSamplerDescription description);

    IGpuPipeline CreatePipeline(GpuPipelineDescription description);

    IGpuRenderTarget CreateRenderTarget(in GpuRenderTargetDescription description);

    IGpuDirectionalDepthTarget CreateDirectionalDepthTarget(
        in GpuDirectionalDepthTargetDescription description);

    /// <summary>
    /// Publishes a (texture, sampler) pair into the global table and returns the
    /// slot shaders index it by. The same texture registered with two samplers
    /// occupies two slots — matching how it holds two bindless handles today.
    /// </summary>
    GpuTextureSlot RegisterTexture(IGpuTexture texture, IGpuSampler sampler);

    void ReleaseTextureSlot(GpuTextureSlot slot);

    /// <summary>Opens the next frame, waiting for its flight slot to retire first.</summary>
    IGpuFrame BeginFrame();

    void QueueDeviceAction(Action action);

    void ProcessDeviceActions();

    byte[] CaptureBackbuffer(int width, int height);

    void WaitIdle();
}
