using AcDream.App.Rendering.Packs;
using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.App.Tests.Rendering.Packs;

public sealed class AtmosphericGpuTimerSamplingTests
{
    [Fact]
    public void LowMeasuresOneCompleteFrameInFour()
    {
        bool[] measured = Enumerable.Range(1, 12)
            .Select(frame => AtmosphericGpuTimerSampling.ShouldMeasure(
                RenderQualitySemantic.Low,
                frame))
            .ToArray();

        Assert.Equal(
            [false, false, false, true, false, false, false, true, false, false, false, true],
            measured);
    }

    [Theory]
    [InlineData(RenderQualitySemantic.Medium)]
    [InlineData(RenderQualitySemantic.High)]
    public void OtherQualitiesMeasureEveryFrame(RenderQualitySemantic quality)
    {
        for (long frame = 1; frame <= 32; frame++)
            Assert.True(AtmosphericGpuTimerSampling.ShouldMeasure(quality, frame));
    }

    [Fact]
    public void RejectsNonPositiveFrameSerial()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AtmosphericGpuTimerSampling.ShouldMeasure(
                RenderQualitySemantic.Low,
                frameSerial: 0));
    }

    [Fact]
    public void WarmSamplingDecisionsAllocateZero()
    {
        _ = AtmosphericGpuTimerSampling.ShouldMeasure(
            RenderQualitySemantic.Low,
            frameSerial: 1);

        int measured = 0;
        for (long frame = 1; frame <= 10_000; frame++)
        {
            if (AtmosphericGpuTimerSampling.ShouldMeasure(
                    RenderQualitySemantic.Low,
                    frame))
            {
                measured++;
            }
        }
        Assert.Equal(2_500, measured);
        ZeroAllocationProbe.AssertAllocatesNothing(
            "AtmosphericGpuTimerSampling.ShouldMeasure",
            () => _ = AtmosphericGpuTimerSampling.ShouldMeasure(
                RenderQualitySemantic.Low,
                frameSerial: 4),
            batchSize: 10_000);
    }
}
