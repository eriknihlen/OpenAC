using AcDream.App.Rendering.Wb;

namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed class VulkanMeshPipelineDevice : IMeshPipelineDevice
{
    public VulkanMeshPipelineDevice(IGpuResourceRetirementQueue resourceRetirement)
    {
        ResourceRetirement = resourceRetirement
            ?? throw new ArgumentNullException(nameof(resourceRetirement));
    }

    /// <inheritdoc />
    public IGpuResourceRetirementQueue ResourceRetirement { get; }

    /// <inheritdoc />
    public uint InstanceVBO => 0;

    /// <inheritdoc />
    public bool HasBindless => true;

    /// <inheritdoc />
    public bool HasOpenGL43 => true;

    public bool HasPendingWork => false;

    public void ProcessQueue()
    {
    }

    public void Dispose()
    {
    }
}
