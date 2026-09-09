namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed class VulkanGpuFrame : IGpuFrame
{
    private readonly VulkanGpuDevice _device;
    private IGpuPassEncoder? _openPass;
    private bool _ended;

    internal VulkanGpuFrame(VulkanGpuDevice device, int slotIndex, long serial)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        SlotIndex = slotIndex;
        Serial = serial;
    }

    public int SlotIndex { get; }

    public long Serial { get; }

    public GpuRingAllocation AllocateRing(int byteCount, GpuRingUsage usage) =>
        _device.AllocateRing(SlotIndex, byteCount, usage);

    public void PublishHostStorageWrites(IGpuBuffer buffer) =>
        _device.PublishHostStorageWrites(this, buffer);

    public IGpuPassEncoder BeginPass(GpuPassDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        if (_openPass is not null)
        {
            throw new InvalidOperationException(
                "A pass is already open on this frame; dispose it before beginning another.");
        }

        IGpuPassEncoder encoder = _device.BeginPass(this, description);
        _openPass = encoder;
        return encoder;
    }

    internal void ClosePass(IGpuPassEncoder encoder)
    {
        if (ReferenceEquals(_openPass, encoder))
            _openPass = null;
    }

    public void End()
    {
        if (_ended)
            return;
        _ended = true;
        _device.EndFrame(this);
    }

    public void Dispose() => End();
}
