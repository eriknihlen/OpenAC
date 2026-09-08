using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.App.Rendering.Packs;

internal static class AtmosphericGpuTimerSampling
{
    internal const int LowIntervalFrames = 4;

    internal static bool ShouldMeasure(
        RenderQualitySemantic quality,
        long frameSerial)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameSerial);
        return quality is not RenderQualitySemantic.Low
            || frameSerial % LowIntervalFrames == 0;
    }
}
