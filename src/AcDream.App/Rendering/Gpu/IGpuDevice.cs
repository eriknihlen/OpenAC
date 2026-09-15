namespace AcDream.App.Rendering.Gpu;

internal interface IGpuDevice : IDisposable
{
    GpuBackendKind Backend { get; }

    GpuCapabilityRecord Capabilities { get; }

    /// <summary>
    /// The device-memory sizing the backend was built with (pool blocks, the
    /// staging and per-frame rings, the mesh arena's starting size). Fixed for
    /// the device's lifetime; a stand-in device reports the default.
    /// </summary>
    GpuMemoryProfile MemoryProfile => GpuMemoryProfile.Default;

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

    /// <summary>
    /// Points an already-published slot at a new (texture, sampler) pair and
    /// returns the slot to keep using. Draw data that baked the slot's index
    /// (terrain tiles, cached draws) stays valid, which a release followed by a
    /// fresh registration would not give: the freed index is scrubbed and can
    /// be handed to the next texture. A stand-in may fall back to that shape.
    /// </summary>
    GpuTextureSlot ReplaceTextureSlot(GpuTextureSlot slot, IGpuTexture texture, IGpuSampler sampler)
    {
        if (slot.IsAssigned)
            ReleaseTextureSlot(slot);
        return RegisterTexture(texture, sampler);
    }

    /// <summary>Opens the next frame, waiting for its flight slot to retire first.</summary>
    IGpuFrame BeginFrame();

    void QueueDeviceAction(Action action);

    void ProcessDeviceActions();

    byte[] CaptureBackbuffer(int width, int height);

    /// <summary>
    /// Keeps a host-readable copy of every presented frame (what
    /// <see cref="CaptureBackbuffer"/> reads), or stops keeping one. A
    /// device built to retain captures ignores the request to stop. From
    /// the frame thread, between frames.
    /// </summary>
    void RetainBackbufferCapture(bool retain) { }

    /// <summary>Whether a retained copy of the frame just presented is ready for <see cref="CaptureBackbuffer"/>.</summary>
    bool HasRetainedCapture => false;

    void WaitIdle();
}
