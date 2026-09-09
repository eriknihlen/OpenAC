using AcDream.App.Rendering.Gpu;
using AcDream.Core.Lighting;

namespace AcDream.App.Rendering;

internal readonly record struct GpuBufferSection(
    IGpuBuffer? Buffer,
    uint OffsetBytes,
    uint SizeBytes)
{
    public bool IsValid => Buffer is not null && SizeBytes > 0;
}

internal sealed class WorldFrameSections
{
    /// <summary>Set 1 binding 1 — <c>SceneLighting</c>.</summary>
    public GpuBufferSection SceneLighting { get; set; }

    public GpuBufferSection ClipRegions { get; set; }

    public void Reset()
    {
        SceneLighting = default;
        ClipRegions = default;
    }
}

internal interface IWorldPassScope
{
    int SampleCount { get; }

    /// <summary>The open encoder, or null outside the world phase.</summary>
    IGpuPassEncoder? CurrentEncoder { get; }

    IGpuPassEncoder RequireEncoder();

    int AttachmentWidth { get; }

    int AttachmentHeight { get; }

    void ClearInteriorDepth();

    /// <summary>The frame-global sections every renderer in this pass rebinds.</summary>
    WorldFrameSections Sections { get; }

    IDisposable Publish(IGpuPassEncoder encoder);
}

internal static class WorldFrameSectionBinding
{
    internal static void BindSceneLighting(
        IGpuPassEncoder encoder,
        WorldFrameSections sections,
        IGpuFrame frame)
    {
        GpuBufferSection section = sections.SceneLighting;
        if (!section.IsValid)
        {
            section = Zeroed(
                frame,
                SceneLightingUbo.SizeInBytes,
                GpuRingUsage.Uniform);
        }

        encoder.BindUniformBuffer(
            (uint)SceneLightingUbo.BindingPoint,
            section.Buffer!,
            section.OffsetBytes,
            section.SizeBytes);
    }

    internal static void BindClipRegions(
        IGpuPassEncoder encoder,
        WorldFrameSections sections,
        IGpuFrame frame)
    {
        GpuBufferSection section = sections.ClipRegions;
        if (!section.IsValid)
        {
            section = Zeroed(
                frame,
                ClipFrame.CellClipStrideBytes,
                GpuRingUsage.Storage);
        }

        encoder.BindStorageBuffer(
            GpuBindingModel.StorageClipRegions,
            section.Buffer!,
            section.OffsetBytes,
            section.SizeBytes);
    }

    private static GpuBufferSection Zeroed(
        IGpuFrame frame,
        int byteCount,
        GpuRingUsage usage)
    {
        GpuRingAllocation allocation = frame.AllocateRing(byteCount, usage);
        allocation.Data.Clear();
        return new GpuBufferSection(
            allocation.Buffer,
            allocation.OffsetBytes,
            (uint)byteCount);
    }
}
