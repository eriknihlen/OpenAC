using AcDream.UI.Abstractions.Panels.Settings;
using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

[Collection(ThreadSchedulingCollection.Name)]
public sealed class BuildingDegradeControllerTests
{
    [Fact]
    public void FpsUsesPriorTwentyFramesThenInsertsJustFinishedFrame()
    {
        DisplaySettings settings = DisplaySettings.Default;
        var controller = new BuildingDegradeController(() => settings);

        for (int i = 0; i < 20; i++)
            controller.Tick(0.05);

        Assert.Equal(20f / 0.95f, controller.Fps, 4);
        controller.Tick(0.05);
        Assert.Equal(20f, controller.Fps, 4);
    }

    [Fact]
    public void FpsWideSumNarrowsOnceAtStoreAndMatchesRetailBits()
    {
        DisplaySettings settings = DisplaySettings.Default;
        var controller = new BuildingDegradeController(() => settings);
        for (int i = 0; i < 10; i++)
        {
            controller.Tick(0.001d);
            controller.Tick(0.005d);
        }

        controller.Tick(0d);

        Assert.Equal(0x43A6AAABu, BitConverter.SingleToUInt32Bits(controller.Fps));
    }

    [Fact]
    public void FpsHistoryMovesOldSlotsUpAndStoresCurrentAtSlotZero()
    {
        float[] history = Enumerable.Range(1, 20).Select(static value => (float)value).ToArray();

        BuildingDegradeController.AdvanceFrameHistory(history, 99f);

        Assert.Equal(99f, history[0]);
        for (int i = 1; i < history.Length; i++)
            Assert.Equal((float)i, history[i]);
    }

    [Fact]
    public void ManualAndAutomaticModesUseTheSamePersistedSettingsWithoutResettingAutoHistory()
    {
        DisplaySettings settings = DisplaySettings.Default with
        {
            AutomaticDegrades = true,
            GraphicsPerformance = -0.4f,
            DegradeDistance = 73f,
        };
        var controller = new BuildingDegradeController(() => settings);

        for (int i = 0; i < 51; i++)
            controller.Tick(1d / 14d);
        float automatic = controller.AutomaticMultiplier;
        Assert.True(automatic > 0f);
        Assert.Equal(automatic, controller.ActiveMultiplier);
        Assert.Equal(73f, controller.DegradeDistance);

        settings = settings with { AutomaticDegrades = false };
        controller.Tick(0.05);
        Assert.Equal(-0.4f, controller.ActiveMultiplier);
        Assert.Equal(automatic, controller.AutomaticMultiplier);

        settings = settings with { AutomaticDegrades = true };
        Assert.Equal(automatic, controller.ActiveMultiplier);
    }

    [Fact]
    public void WarmedTickIsAllocationFreeAndExceptionalDeltasStayDefined()
    {
        DisplaySettings settings = DisplaySettings.Default with
        {
            AutomaticDegrades = true,
        };
        var controller = new BuildingDegradeController(() => settings);
        for (int i = 0; i < 100; i++)
            controller.Tick(1d / 60d);

        controller.Tick(0d);
        controller.Tick(double.NaN);
        controller.Tick(double.PositiveInfinity);
        controller.Tick(double.NegativeInfinity);

        long allocated = ZeroAllocationProbe.MeasureWarmed(
            () => controller.Tick(1d / 60d),
            batchSize: 10_000,
            warmupBatches: 2,
            samples: 5);

        Assert.Equal(0, allocated);
        Assert.InRange(controller.AutomaticMultiplier, -1f, 1f);
    }

    [Theory]
    [InlineData(0d, 0x41A86BCAu)]
    [InlineData(0.00001d, 0x41A86B56u)]
    [InlineData(double.NaN, 0u)]
    [InlineData(double.PositiveInfinity, 0u)]
    [InlineData(double.NegativeInfinity, 0u)]
    public void ZeroTinyAndExceptionalDeltasKeepRetailFloatHistoryWithoutSanitizing(
        double exceptionalDelta,
        uint expectedFollowingFpsBits)
    {
        DisplaySettings settings = DisplaySettings.Default with
        {
            AutomaticDegrades = true,
        };
        var controller = new BuildingDegradeController(() => settings);
        for (int i = 0; i < 20; i++)
            controller.Tick(0.05d);

        controller.Tick(exceptionalDelta);
        Assert.Equal(0x41A00000u, BitConverter.SingleToUInt32Bits(controller.Fps));
        Assert.Equal(0u, BitConverter.SingleToUInt32Bits(controller.AutomaticMultiplier));

        controller.Tick(0.05d);
        Assert.Equal(expectedFollowingFpsBits, BitConverter.SingleToUInt32Bits(controller.Fps));
        Assert.Equal(0u, BitConverter.SingleToUInt32Bits(controller.AutomaticMultiplier));
    }

    [Fact]
    public void NewControllerStartsAtRetailZeroWithoutSharingHistory()
    {
        DisplaySettings settings = DisplaySettings.Default with
        {
            AutomaticDegrades = true,
        };
        var first = new BuildingDegradeController(() => settings);
        for (int i = 0; i < 51; i++)
            first.Tick(1d / 14d);

        var restarted = new BuildingDegradeController(() => settings);
        Assert.NotEqual(0f, first.AutomaticMultiplier);
        Assert.Equal(0f, restarted.AutomaticMultiplier);
        Assert.Equal(0f, restarted.Fps);
    }

    [Theory]
    [InlineData(0f, -0.150000006f)]
    [InlineData(10f, 0f)]
    [InlineData(14f, 0.0054545454f)]
    [InlineData(20f, 0.1f)]
    [InlineData(25f, 0.1f)]
    public void ExactFiveWeightFormulaMatchesFixedRetailVectors(
        float fps,
        float expected)
    {
        Assert.Equal(
            expected,
            BuildingDegradeController.CalculateCandidate(fps, 0f),
            7);
    }

    [Fact]
    public void FiveWeightFormulaKeepsX87IntermediatesUntilCandidateStore()
    {
        float candidate = BuildingDegradeController.CalculateCandidate(14f, 0f);

        Assert.Equal(0x3BB2BC0Au, BitConverter.SingleToUInt32Bits(candidate));
    }

    [Fact]
    public void FiveWeightNumeratorPreservesAllFourRetailFloatStores()
    {
        float candidate = BuildingDegradeController.CalculateCandidate(16.25f, 0f);

        Assert.Equal(0x3CA3D70Au, BitConverter.SingleToUInt32Bits(candidate));
    }

    [Fact]
    public void StabilityUsesQwordPointZeroOneRatherThanPromotedFloatConstant()
    {
        float candidate = BitConverter.UInt32BitsToSingle(0x3C13D70Au);
        float prior = -BitConverter.UInt32BitsToSingle(0x3A800001u);
        float[] history = Enumerable.Repeat(prior, 30).ToArray();

        double difference = Math.Abs((double)prior - candidate);
        Assert.True(difference > (double)0.01f);
        Assert.True(difference < 0.01);
        Assert.True(BuildingDegradeController.IsCandidateStable(history, candidate));
    }

    [Fact]
    public void AutomaticCommitRequiresAllThirtyPriorSlotsWithinStrictBand()
    {
        float[] history = Enumerable.Repeat(0.1f, 30).ToArray();
        history[15] = 0f;

        float rejected = BuildingDegradeController.AdvanceAutomaticMultiplier(
            history, automatic: true, fps: 20f, current: 0f);

        Assert.Equal(0f, rejected);
        Assert.Equal(0f, history[^1]);

        Array.Fill(history, 0.1f);
        float committed = BuildingDegradeController.AdvanceAutomaticMultiplier(
            history, automatic: true, fps: 20f, current: 0f);

        Assert.Equal(0.1f, committed, 7);
        Assert.All(history, value => Assert.Equal(0.1f, value, 7));
    }

    [Fact]
    public void AutomaticClampAndManualArmMatchFixedExpectedTransitions()
    {
        float[] history = Enumerable.Repeat(1f, 30).ToArray();
        float clamped = BuildingDegradeController.AdvanceAutomaticMultiplier(
            history, automatic: true, fps: 25f, current: 0.95f);
        Assert.Equal(1f, clamped);

        history[10] = -1f;
        float manual = BuildingDegradeController.AdvanceAutomaticMultiplier(
            history, automatic: false, fps: 0f, current: 0.35f);

        Assert.Equal(0.35f, manual);
        Assert.Equal(0.35f, history[^1]);
    }
}
