namespace AcDream.App.Rendering.Packs;

using AcDream.Plugin.Abstractions.Rendering;

internal enum AtmosphericQualityLevel : byte
{
    Low,
    Medium,
    High,
}

internal readonly record struct AtmosphericQualityMeasurement(
    double InclusivePackGpuMillisecondsP99,
    double IncrementalCpuMillisecondsP99,
    long ResidentGpuBytes,
    bool StableFrameBoundary);

internal readonly record struct AtmosphericAutoQualitySnapshot(
    AtmosphericQualityLevel Current,
    int ConsecutiveOverBudgetFrames,
    int ConsecutiveHeadroomFrames,
    int CooldownFramesRemaining,
    long ChangeGeneration,
    bool SafeFallbackToRetailRequested);

internal readonly record struct AtmosphericQualityBudget(
    double GpuMillisecondsP99,
    double CpuMillisecondsP99,
    long ResidentGpuBytes)
{
    internal static AtmosphericQualityBudget FromPreset(RenderQualityPreset preset) => new(
        preset.MaxIncrementalGpuMillisecondsP99,
        preset.MaxIncrementalCpuMillisecondsP99,
        preset.MaxResidentGpuBytes);
}

internal sealed class AtmosphericAutoQualityController
{
    internal const int DowngradeHysteresisFrames = 180;
    internal const int UpgradeHysteresisFrames = 900;
    internal const int ChangeCooldownFrames = 300;

    private AtmosphericQualityLevel _current;
    private readonly AtmosphericQualityLevel _minimum;
    private readonly AtmosphericQualityLevel _maximum;
    private readonly AtmosphericQualityBudget[] _budgets;
    private int _overBudget;
    private int _headroom;
    private int _cooldown;
    private long _generation;
    private bool _safeFallbackToRetailRequested;

    internal AtmosphericAutoQualityController(
        AtmosphericQualityLevel initial = AtmosphericQualityLevel.Medium,
        AtmosphericQualityLevel minimum = AtmosphericQualityLevel.Low,
        AtmosphericQualityLevel maximum = AtmosphericQualityLevel.High)
        : this(DefaultBudgets(), initial, minimum, maximum)
    {
    }

    internal AtmosphericAutoQualityController(
        IReadOnlyList<AtmosphericQualityBudget> budgets,
        AtmosphericQualityLevel initial = AtmosphericQualityLevel.Medium,
        AtmosphericQualityLevel minimum = AtmosphericQualityLevel.Low,
        AtmosphericQualityLevel maximum = AtmosphericQualityLevel.High)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        if (budgets.Count != 3)
            throw new ArgumentException("Auto quality requires Low, Medium, and High budgets.", nameof(budgets));
        if (minimum > initial || initial > maximum)
            throw new ArgumentOutOfRangeException(nameof(initial));
        _budgets = budgets.ToArray();
        foreach (AtmosphericQualityBudget budget in _budgets)
        {
            if (!double.IsFinite(budget.GpuMillisecondsP99)
                || budget.GpuMillisecondsP99 < 0d
                || !double.IsFinite(budget.CpuMillisecondsP99)
                || budget.CpuMillisecondsP99 < 0d
                || budget.ResidentGpuBytes < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(budgets),
                    "Automatic-quality budgets must be finite and non-negative.");
            }
        }
        _minimum = minimum;
        _maximum = maximum;
        _current = initial;
    }

    internal AtmosphericAutoQualitySnapshot Snapshot => new(
        _current,
        _overBudget,
        _headroom,
        _cooldown,
        _generation,
        _safeFallbackToRetailRequested);

    internal AtmosphericQualityBudget CurrentBudget => _budgets[(int)_current];

    internal AtmosphericAutoQualitySnapshot Observe(
        in AtmosphericQualityMeasurement measurement)
    {
        Validate(in measurement);
        if (!measurement.StableFrameBoundary)
            return Snapshot;
        if (_safeFallbackToRetailRequested)
            return Snapshot;
        if (_cooldown > 0)
        {
            _cooldown--;
            _overBudget = 0;
            _headroom = 0;
            return Snapshot;
        }

        AtmosphericQualityBudget budget = _budgets[(int)_current];
        bool over = measurement.InclusivePackGpuMillisecondsP99
                > budget.GpuMillisecondsP99
            || measurement.IncrementalCpuMillisecondsP99
                > budget.CpuMillisecondsP99
            || measurement.ResidentGpuBytes > budget.ResidentGpuBytes;
        if (over)
        {
            _overBudget++;
            _headroom = 0;
            if (_overBudget >= DowngradeHysteresisFrames)
            {
                if (_current != _minimum)
                    Change((AtmosphericQualityLevel)((int)_current - 1));
                else
                    RequestSafeFallback();
            }
            return Snapshot;
        }

        _overBudget = 0;
        if (_current == _maximum)
        {
            _headroom = 0;
            return Snapshot;
        }

        AtmosphericQualityLevel next =
            (AtmosphericQualityLevel)((int)_current + 1);
        AtmosphericQualityBudget nextBudget = _budgets[(int)next];
        bool hasHeadroom = measurement.InclusivePackGpuMillisecondsP99
                <= nextBudget.GpuMillisecondsP99 * 0.70
            && measurement.IncrementalCpuMillisecondsP99
                <= nextBudget.CpuMillisecondsP99 * 0.70
            && measurement.ResidentGpuBytes
                <= (long)(nextBudget.ResidentGpuBytes * 0.70);
        if (!hasHeadroom)
        {
            _headroom = 0;
            return Snapshot;
        }

        _headroom++;
        if (_headroom >= UpgradeHysteresisFrames)
            Change(next);
        return Snapshot;
    }

    internal void Reset(AtmosphericQualityLevel level)
    {
        _current = level;
        _overBudget = 0;
        _headroom = 0;
        _cooldown = 0;
        _safeFallbackToRetailRequested = false;
        _generation = checked(_generation + 1);
    }

    private void Change(AtmosphericQualityLevel value)
    {
        _current = value;
        _overBudget = 0;
        _headroom = 0;
        _cooldown = ChangeCooldownFrames;
        _generation = checked(_generation + 1);
    }

    private void RequestSafeFallback()
    {
        _overBudget = DowngradeHysteresisFrames;
        _headroom = 0;
        _cooldown = 0;
        _safeFallbackToRetailRequested = true;
        _generation = checked(_generation + 1);
    }

    private static AtmosphericQualityBudget[] DefaultBudgets() =>
    [
        From(DirectionalShadowPreset.Low),
        From(DirectionalShadowPreset.Medium),
        From(DirectionalShadowPreset.High),
    ];

    private static AtmosphericQualityBudget From(DirectionalShadowPreset preset)
    {
        DirectionalShadowQuality quality = DirectionalShadowQuality.For(preset);
        return new AtmosphericQualityBudget(
            quality.IncrementalGpuP99BudgetMilliseconds,
            quality.IncrementalCpuP99BudgetMilliseconds,
            quality.PackResidentGpuByteBudget);
    }

    private static void Validate(in AtmosphericQualityMeasurement value)
    {
        if (!double.IsFinite(value.InclusivePackGpuMillisecondsP99)
            || value.InclusivePackGpuMillisecondsP99 < 0
            || !double.IsFinite(value.IncrementalCpuMillisecondsP99)
            || value.IncrementalCpuMillisecondsP99 < 0
            || value.ResidentGpuBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Atmospheric quality measurements must be finite and non-negative.");
        }
    }
}
