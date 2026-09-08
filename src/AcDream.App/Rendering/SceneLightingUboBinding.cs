using System;
using AcDream.App.Rendering.Gpu;
using AcDream.Core.Lighting;

namespace AcDream.App.Rendering;

public sealed unsafe class SceneLightingUboBinding : IDisposable
{
    private readonly ICurrentGpuFrameSource _frames;
    private readonly WorldFrameSections _sections;
    private bool _frameStarted;

    internal int DynamicBufferCount => 0;

    internal SceneLightingUboBinding(
        ICurrentGpuFrameSource frames,
        WorldFrameSections sections)
    {
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _sections = sections ?? throw new ArgumentNullException(nameof(sections));
    }

    public void BeginFrame(int frameSlot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameSlot);
        _frameStarted = true;
    }

    public void Upload(SceneLightingUbo data)
    {
        if (!_frameStarted)
            throw new InvalidOperationException("BeginFrame must be called before uploading scene lighting.");

        IGpuFrame frame = _frames.CurrentFrame
            ?? throw new InvalidOperationException(
                "Scene lighting requires an open IGpuFrame (see GpuDeviceFrameLifetime).");
        GpuRingAllocation allocation = frame.AllocateRing(
            SceneLightingUbo.SizeInBytes,
            GpuRingUsage.Uniform);
        new ReadOnlySpan<byte>(&data, SceneLightingUbo.SizeInBytes)
            .CopyTo(allocation.Data);
        _sections.SceneLighting = new GpuBufferSection(
            allocation.Buffer,
            allocation.OffsetBytes,
            (uint)SceneLightingUbo.SizeInBytes);
    }

    public void Dispose()
    {
    }
}
