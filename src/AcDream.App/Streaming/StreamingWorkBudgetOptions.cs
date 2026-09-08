using System.Globalization;

namespace AcDream.App.Streaming;

public sealed record StreamingWorkBudgetOptions(
    double MaxUpdateMilliseconds,
    int MaxCompletionAdmissions,
    long MaxAdoptedCpuBytes,
    int MaxEntityOperations,
    long MaxGpuUploadBytes,
    int MaxGlRetireOperations,
    float DestinationReserveFraction,
    double HoldDestinationCeilingMilliseconds = 8.0)
{
    public const long MiB = 1024L * 1024L;

    public static StreamingWorkBudgetOptions Default { get; } = new(
        MaxUpdateMilliseconds: 2.0,
        MaxCompletionAdmissions: 64,
        MaxAdoptedCpuBytes: 8 * MiB,
        MaxEntityOperations: 4_096,
        MaxGpuUploadBytes: 8 * MiB,
        MaxGlRetireOperations: 64,
        DestinationReserveFraction: 0.75f,
        HoldDestinationCeilingMilliseconds: 8.0);

    internal static StreamingWorkBudgetOptions Parse(
        Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(env);
        StreamingWorkBudgetOptions defaults = Default;
        return new StreamingWorkBudgetOptions(
            MaxUpdateMilliseconds: ParsePositiveDouble(
                env("ACDREAM_STREAM_WORK_MS"),
                defaults.MaxUpdateMilliseconds),
            MaxCompletionAdmissions: ParsePositiveInt(
                env("ACDREAM_STREAM_WORK_COMPLETIONS"),
                defaults.MaxCompletionAdmissions),
            MaxAdoptedCpuBytes: ParseMiB(
                env("ACDREAM_STREAM_WORK_CPU_MIB"),
                defaults.MaxAdoptedCpuBytes),
            MaxEntityOperations: ParsePositiveInt(
                env("ACDREAM_STREAM_WORK_ENTITY_OPS"),
                defaults.MaxEntityOperations),
            MaxGpuUploadBytes: ParseMiB(
                env("ACDREAM_STREAM_WORK_GPU_MIB"),
                defaults.MaxGpuUploadBytes),
            MaxGlRetireOperations: ParsePositiveInt(
                env("ACDREAM_STREAM_WORK_GL_RETIRE_OPS"),
                defaults.MaxGlRetireOperations),
            DestinationReserveFraction: ParseReservePercent(
                env("ACDREAM_STREAM_WORK_DEST_RESERVE_PERCENT"),
                defaults.DestinationReserveFraction),
            HoldDestinationCeilingMilliseconds: ParsePositiveDouble(
                env("ACDREAM_STREAM_WORK_HOLD_DEST_MS"),
                defaults.HoldDestinationCeilingMilliseconds));
    }

    public StreamingWorkBudget ToBudget() => new(
        TimeSpan.FromMilliseconds(MaxUpdateMilliseconds),
        MaxCompletionAdmissions,
        MaxAdoptedCpuBytes,
        MaxEntityOperations,
        MaxGpuUploadBytes,
        MaxGlRetireOperations,
        DestinationReserveFraction);

    public StreamingWorkBudgetOptions ScaleForLegacyCompletionCount(int count)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        double scale = count / 4.0;
        return this with
        {
            MaxUpdateMilliseconds = Math.Max(
                0.25,
                MaxUpdateMilliseconds * scale),
            MaxCompletionAdmissions = Scale(MaxCompletionAdmissions, scale),
            MaxAdoptedCpuBytes = Scale(MaxAdoptedCpuBytes, scale),
            MaxEntityOperations = Scale(MaxEntityOperations, scale),
            MaxGpuUploadBytes = Scale(MaxGpuUploadBytes, scale),
            MaxGlRetireOperations = Scale(MaxGlRetireOperations, scale),
        };
    }

    private static double ParsePositiveDouble(string? value, double fallback) =>
        double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double parsed)
        && double.IsFinite(parsed)
        && parsed > 0
            ? parsed
            : fallback;

    private static int ParsePositiveInt(string? value, int fallback) =>
        int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int parsed)
        && parsed > 0
            ? parsed
            : fallback;

    private static long ParseMiB(string? value, long fallback)
    {
        if (!long.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long mebibytes)
            || mebibytes <= 0
            || mebibytes > long.MaxValue / MiB)
        {
            return fallback;
        }

        return checked(mebibytes * MiB);
    }

    private static float ParseReservePercent(string? value, float fallback)
    {
        if (!float.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float percent)
            || !float.IsFinite(percent)
            || percent <= 0f
            || percent >= 100f)
        {
            return fallback;
        }

        return percent / 100f;
    }

    internal static int Scale(int value, double scale)
    {
        if (scale >= int.MaxValue / (double)value)
            return int.MaxValue;
        return Math.Max(
            1,
            (int)Math.Round(value * scale, MidpointRounding.AwayFromZero));
    }

    internal static long Scale(long value, double scale)
    {
        if (scale >= long.MaxValue / (double)value)
            return long.MaxValue;
        return Math.Max(
            1L,
            (long)Math.Round(value * scale, MidpointRounding.AwayFromZero));
    }
}
