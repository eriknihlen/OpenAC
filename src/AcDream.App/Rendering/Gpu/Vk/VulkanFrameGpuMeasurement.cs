using AcDream.App.Diagnostics;

namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed class VulkanFrameGpuMeasurement : IRenderFrameGpuMeasurement
{
    private readonly FrameProfiler _profiler;
    private readonly VulkanGpuDevice _device;

    internal VulkanFrameGpuMeasurement(FrameProfiler profiler, VulkanGpuDevice device)
    {
        _profiler = profiler ?? throw new ArgumentNullException(nameof(profiler));
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    public void BeginFrame()
    {
        _profiler.FrameBoundary();

        while (_device.TryTakeFrameGpuSample(out int frameIndex, out long elapsedUs))
            _profiler.RecordGpuSample(frameIndex, elapsedUs);

        _device.BeginFrameTimerScope(_profiler.CurrentFrameIndex);
    }

    public void EndFrame() => _device.EndFrameTimerScope();
}
