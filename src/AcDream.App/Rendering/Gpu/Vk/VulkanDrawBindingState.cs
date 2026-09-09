namespace AcDream.App.Rendering.Gpu.Vk;

internal struct VulkanDrawBindingState
{
    private ulong _pipelineLayout;
    private int _packGeneration;
    private bool _hasBinding;
    private bool _dirty;

    internal readonly bool RequiresBind(
        ulong pipelineLayout,
        int packGeneration) =>
        !_hasBinding
        || _dirty
        || _pipelineLayout != pipelineLayout
        || _packGeneration != packGeneration;

    internal void MarkDirty() => _dirty = true;

    internal void MarkBound(ulong pipelineLayout, int packGeneration)
    {
        _pipelineLayout = pipelineLayout;
        _packGeneration = packGeneration;
        _hasBinding = true;
        _dirty = false;
    }
}
