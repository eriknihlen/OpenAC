using AcDream.App.Rendering.Gpu;

namespace AcDream.App.Rendering;

internal interface ICurrentGpuFrameSource
{
    IGpuFrame? CurrentFrame { get; }
}

internal sealed class GpuDeviceFrameLifetime : IRenderFrameLifetime, ICurrentGpuFrameSource
{
    private readonly IGpuDevice _device;

    public GpuDeviceFrameLifetime(IGpuDevice device) =>
        _device = device ?? throw new ArgumentNullException(nameof(device));

    public IGpuFrame? CurrentFrame { get; private set; }

    public void BeginFrame() => CurrentFrame = _device.BeginFrame();

    public void EndFrame()
    {
        IGpuFrame? frame = CurrentFrame;
        CurrentFrame = null;
        frame?.End();
    }
}
