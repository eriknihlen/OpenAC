using AcDream.App.Rendering.Gpu;

namespace AcDream.App.Rendering;

internal interface IRenderFrameGlState
{
    void RestoreFrameDefaults();
}
internal interface IWorldPassSurface
{
    void PrepareClipFrame();

    void EnableClipDistances();

    void DisableClipDistances();

    void ClearInteriorDepth();
}

internal sealed class RhiWorldPassSurface : IWorldPassSurface
{
    private readonly IWorldPassScope _scope;
    private readonly ICurrentGpuFrameSource _frames;
    private readonly ClipFrame _clipFrame;

    public RhiWorldPassSurface(
        IWorldPassScope scope,
        ICurrentGpuFrameSource frames,
        ClipFrame clipFrame)
    {
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _clipFrame = clipFrame ?? throw new ArgumentNullException(nameof(clipFrame));
    }

    public void PrepareClipFrame()
    {
        _scope.Sections.ClipRegions = Publish(
            _clipFrame.RegionBytes,
            GpuRingUsage.Storage);
    }

    public void EnableClipDistances()
    {
    }

    public void DisableClipDistances()
    {
    }

    public void ClearInteriorDepth()
    {
        _scope.ClearInteriorDepth();
    }

    private GpuBufferSection Publish(ReadOnlySpan<byte> data, GpuRingUsage usage)
    {
        IGpuFrame frame = _frames.CurrentFrame
            ?? throw new InvalidOperationException(
                "The world clip frame requires an open IGpuFrame (see GpuDeviceFrameLifetime).");
        // A logically empty table still reserves one slot so the bound range is
        // never zero-length — the same rule the light buffers already state.
        int byteCount = Math.Max(data.Length, ClipFrame.CellClipStrideBytes);
        GpuRingAllocation allocation = frame.AllocateRing(byteCount, usage);
        allocation.Data.Clear();
        if (!data.IsEmpty)
            data.CopyTo(allocation.Data);
        return new GpuBufferSection(
            allocation.Buffer,
            allocation.OffsetBytes,
            (uint)byteCount);
    }
}

internal sealed class NullRenderFrameGlState : IRenderFrameGlState
{
    public static NullRenderFrameGlState Instance { get; } = new();

    private NullRenderFrameGlState()
    {
    }

    public void RestoreFrameDefaults()
    {
    }
}
