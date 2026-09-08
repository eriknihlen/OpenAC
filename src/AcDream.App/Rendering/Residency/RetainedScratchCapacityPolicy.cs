namespace AcDream.App.Rendering.Residency;

internal readonly record struct AlphaScratchBudgetProfile(
    long QueueBytes,
    long DispatcherBytes,
    long ParticleBytes)
{
    public long TotalBytes => checked(
        QueueBytes + DispatcherBytes + ParticleBytes);

    public static AlphaScratchBudgetProfile Create(long totalBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalBytes);
        long queue = totalBytes / 4;
        long particles = totalBytes / 4;
        long dispatcher = checked(totalBytes - queue - particles);
        return new AlphaScratchBudgetProfile(queue, dispatcher, particles);
    }
}

internal sealed class RetainedScratchCapacityPolicy(
    long budgetBytes,
    int lowDemandSamplesBeforeShrink = 3)
{
    private readonly long _budgetBytes =
        budgetBytes > 0
            ? budgetBytes
            : throw new ArgumentOutOfRangeException(nameof(budgetBytes));
    private readonly int _lowDemandSamplesBeforeShrink =
        lowDemandSamplesBeforeShrink > 0
            ? lowDemandSamplesBeforeShrink
            : throw new ArgumentOutOfRangeException(
                nameof(lowDemandSamplesBeforeShrink));
    private int _lowDemandSamples;

    public long BudgetBytes => _budgetBytes;

    public int ObserveAndSelectCapacity(
        int currentCapacity,
        int requiredCapacity,
        int bytesPerUnit,
        int minimumCapacity,
        int growthQuantum)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(currentCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(requiredCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerUnit);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(growthQuantum);

        if (requiredCapacity > currentCapacity)
        {
            _lowDemandSamples = 0;
            return RoundUp(requiredCapacity, growthQuantum);
        }

        int budgetCapacity = checked((int)Math.Min(
            int.MaxValue,
            Math.Max(minimumCapacity, _budgetBytes / bytesPerUnit)));
        bool demandHasFallen =
            currentCapacity > budgetCapacity
            && requiredCapacity <= budgetCapacity
            && (requiredCapacity == 0
                || (long)currentCapacity >= (long)requiredCapacity * 4);
        if (!demandHasFallen)
        {
            _lowDemandSamples = 0;
            return currentCapacity;
        }

        _lowDemandSamples++;
        if (_lowDemandSamples < _lowDemandSamplesBeforeShrink)
            return currentCapacity;

        _lowDemandSamples = 0;
        long warmedDemand = Math.Max(
            minimumCapacity,
            Math.Min(int.MaxValue, (long)requiredCapacity * 2));
        int target = RoundUp(
            checked((int)Math.Min(warmedDemand, budgetCapacity)),
            growthQuantum);
        target = Math.Max(requiredCapacity, Math.Min(target, budgetCapacity));
        return Math.Min(currentCapacity, target);
    }

    private static int RoundUp(int value, int quantum)
    {
        if (value == 0)
            return 0;
        long rounded = ((long)value + quantum - 1) / quantum * quantum;
        return checked((int)Math.Min(int.MaxValue, rounded));
    }
}
