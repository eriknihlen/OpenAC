using AcDream.App.Rendering.Residency;

namespace AcDream.App.Tests.Rendering.Residency;

public sealed class RetainedScratchCapacityPolicyTests
{
    [Fact]
    public void AlphaBudgetProfilePartitionsTheAggregateExactly()
    {
        AlphaScratchBudgetProfile profile =
            AlphaScratchBudgetProfile.Create(16 * 1024 * 1024);

        Assert.Equal(4 * 1024 * 1024, profile.QueueBytes);
        Assert.Equal(8 * 1024 * 1024, profile.DispatcherBytes);
        Assert.Equal(4 * 1024 * 1024, profile.ParticleBytes);
        Assert.Equal(16 * 1024 * 1024, profile.TotalBytes);
    }

    [Fact]
    public void SpikeRetainsWarmCapacityUntilDemandFallsThenConverges()
    {
        var policy = new RetainedScratchCapacityPolicy(
            budgetBytes: 1024,
            lowDemandSamplesBeforeShrink: 3);

        Assert.Equal(
            4096,
            policy.ObserveAndSelectCapacity(
                currentCapacity: 4096,
                requiredCapacity: 4096,
                bytesPerUnit: 4,
                minimumCapacity: 64,
                growthQuantum: 64));
        Assert.Equal(4096, ObserveLowDemand(policy));
        Assert.Equal(4096, ObserveLowDemand(policy));

        int converged = ObserveLowDemand(policy);

        Assert.InRange(converged, 64, 256);
        Assert.True((long)converged * 4 <= 1024);
    }

    [Fact]
    public void SustainedLegitimateDemandIsNeverTrimmedBelowRequirement()
    {
        var policy = new RetainedScratchCapacityPolicy(
            budgetBytes: 1024,
            lowDemandSamplesBeforeShrink: 2);

        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(
                4096,
                policy.ObserveAndSelectCapacity(
                    currentCapacity: 4096,
                    requiredCapacity: 4096,
                    bytesPerUnit: 4,
                    minimumCapacity: 64,
                    growthQuantum: 64));
        }
    }

    [Fact]
    public void RequiredGrowthIsImmediateAndQuantumRounded()
    {
        var policy = new RetainedScratchCapacityPolicy(1024);

        Assert.Equal(
            320,
            policy.ObserveAndSelectCapacity(
                currentCapacity: 64,
                requiredCapacity: 257,
                bytesPerUnit: 4,
                minimumCapacity: 64,
                growthQuantum: 64));
    }

    private static int ObserveLowDemand(
        RetainedScratchCapacityPolicy policy) =>
        policy.ObserveAndSelectCapacity(
            currentCapacity: 4096,
            requiredCapacity: 32,
            bytesPerUnit: 4,
            minimumCapacity: 64,
            growthQuantum: 64);
}
