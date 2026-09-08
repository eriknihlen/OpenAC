using AcDream.App.Rendering.Gpu;

namespace AcDream.App.Rendering;

internal static class UiTextureTableHandle
{
    /// <summary>No texture. What every widget's <c>tex == 0</c> guard tests for.</summary>
    public const uint None = 0;

    public static uint FromSlot(GpuTextureSlot slot) =>
        slot.IsAssigned ? slot.Index + 1 : None;

    public static GpuTextureSlot ToSlot(uint handle) =>
        handle == None ? GpuTextureSlot.Unassigned : new GpuTextureSlot(handle - 1);
}
