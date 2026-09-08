namespace AcDream.App.Rendering.Gpu;

internal interface IGpuPipelineFormatVariantHost
{
    IDisposable AcquirePipelineColorFormat(GpuTextureFormat format);
}
