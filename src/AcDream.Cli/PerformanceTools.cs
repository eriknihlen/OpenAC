using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AcDream.Cli;

public static partial class PerformanceTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static int SummarizeFrameHistory(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine(
                "usage: AcDream.Cli summarize-frame-history "
                + "<frame-history.csv> <checkpoints.jsonl> <markers.log> <out.json>");
            return 2;
        }

        try
        {
            IReadOnlyList<FrameRow> frames = ReadFrames(args[0]);
            IReadOnlyList<CheckpointRow> checkpoints = ReadCheckpoints(args[1]);
            IReadOnlyList<PortalMarker> portals = ReadPortalMarkers(args[2]);
            var summary = new FrameHistoryReport(
                SourceFrameHistory: Path.GetFileName(args[0]),
                FrameCount: frames.Count,
                FirstFrameUtc: frames.Count == 0 ? null : frames[0].TimestampUtc,
                LastFrameUtc: frames.Count == 0 ? null : frames[^1].TimestampUtc,
                Route: SummarizeWindow(frames),
                CheckpointWindows: checkpoints.Select(checkpoint =>
                    new NamedWindowSummary(
                        checkpoint.Name,
                        checkpoint.TimestampUtc.AddSeconds(-5),
                        checkpoint.TimestampUtc,
                        SummarizeWindow(frames.Where(frame =>
                            frame.TimestampUtc >= checkpoint.TimestampUtc.AddSeconds(-5)
                            && frame.TimestampUtc <= checkpoint.TimestampUtc)),
                        checkpoint.ProcessAllocationRateBytesPerSecond))
                    .ToArray(),
                PortalWindows: portals.Select(portal =>
                    new PortalWindowSummary(
                        portal.Destination,
                        portal.TimestampUtc,
                        SummarizeWindow(frames.Where(frame =>
                            frame.TimestampUtc >= portal.TimestampUtc.AddSeconds(-10)
                            && frame.TimestampUtc <= portal.TimestampUtc.AddSeconds(5))),
                        frames.Where(frame =>
                                frame.TimestampUtc >= portal.TimestampUtc.AddSeconds(-10)
                                && frame.TimestampUtc <= portal.TimestampUtc.AddSeconds(5))
                            .OrderByDescending(frame => frame.CpuUs)
                            .Take(20)
                            .Select(frame => new WorstFrame(
                                frame.Frame,
                                Math.Round(
                                    (frame.TimestampUtc - portal.TimestampUtc).TotalMilliseconds,
                                    3),
                                frame.CpuUs,
                                frame.GpuUs,
                                frame.AllocBytes,
                                frame.UpdateUs,
                                frame.UploadUs,
                                frame.PacingUs))
                            .ToArray()))
                    .ToArray());

            string output = Path.GetFullPath(args[3]);
            Directory.CreateDirectory(
                Path.GetDirectoryName(output)
                ?? throw new InvalidOperationException("Output path has no directory."));
            File.WriteAllText(output, JsonSerializer.Serialize(summary, JsonOptions));
            Console.WriteLine($"wrote frame-history summary: {output}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"frame-history summary failed: {error.Message}");
            return 1;
        }
    }

    public static int CompareScreenshots(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine(
                "usage: AcDream.Cli compare-screenshots "
                + "<expected.png> <actual.png> <out.json> "
                + "[channelTolerance=2] [maxDifferentFraction=0.001] [mask.png]");
            return 2;
        }

        try
        {
            int tolerance = args.Length >= 4
                ? int.Parse(args[3], CultureInfo.InvariantCulture)
                : 2;
            double maxDifferentFraction = args.Length >= 5
                ? double.Parse(args[4], CultureInfo.InvariantCulture)
                : 0.001;
            string? maskPath = args.Length >= 6 ? args[5] : null;
            ScreenshotComparison comparison = CompareImages(
                args[0],
                args[1],
                tolerance,
                maxDifferentFraction,
                maskPath);

            string output = Path.GetFullPath(args[2]);
            Directory.CreateDirectory(
                Path.GetDirectoryName(output)
                ?? throw new InvalidOperationException("Output path has no directory."));
            File.WriteAllText(output, JsonSerializer.Serialize(comparison, JsonOptions));
            Console.WriteLine(
                $"screenshot comparison {(comparison.Passed ? "passed" : "failed")}: "
                + $"{comparison.DifferentPixelFraction:P6} different");
            return comparison.Passed ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"screenshot comparison failed: {error.Message}");
            return 1;
        }
    }

    public static ScreenshotComparison CompareImages(
        string expectedPath,
        string actualPath,
        int channelTolerance,
        double maxDifferentFraction,
        string? maskPath = null)
    {
        if ((uint)channelTolerance > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(channelTolerance));
        if (maxDifferentFraction is < 0 or > 1 || double.IsNaN(maxDifferentFraction))
            throw new ArgumentOutOfRangeException(nameof(maxDifferentFraction));

        using Image<Rgba32> expected = Image.Load<Rgba32>(expectedPath);
        using Image<Rgba32> actual = Image.Load<Rgba32>(actualPath);
        using Image<Rgba32>? mask = maskPath is null
            ? null
            : Image.Load<Rgba32>(maskPath);

        if (expected.Width != actual.Width || expected.Height != actual.Height)
        {
            return new ScreenshotComparison(
                Passed: false,
                Width: actual.Width,
                Height: actual.Height,
                ExpectedWidth: expected.Width,
                ExpectedHeight: expected.Height,
                ChannelTolerance: channelTolerance,
                MaxDifferentPixelFraction: maxDifferentFraction,
                ComparedPixels: 0,
                MaskedPixels: 0,
                DifferentPixels: 0,
                DifferentPixelFraction: 1,
                MaximumChannelDelta: byte.MaxValue,
                MeanAbsoluteChannelDelta: 0,
                RootMeanSquareChannelDelta: 0,
                ExpectedPath: Path.GetFileName(expectedPath),
                ActualPath: Path.GetFileName(actualPath),
                MaskPath: maskPath is null ? null : Path.GetFileName(maskPath));
        }

        if (mask is not null
            && (mask.Width != expected.Width || mask.Height != expected.Height))
        {
            throw new InvalidDataException(
                "Screenshot mask dimensions must match the compared images.");
        }

        long comparedPixels = 0;
        long maskedPixels = 0;
        long differentPixels = 0;
        long absoluteDeltaSum = 0;
        long squaredDeltaSum = 0;
        int maximumDelta = 0;

        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                if (mask is not null && mask[x, y].A != 0)
                {
                    maskedPixels++;
                    continue;
                }

                comparedPixels++;
                Rgba32 left = expected[x, y];
                Rgba32 right = actual[x, y];
                int redDelta = Math.Abs(left.R - right.R);
                int greenDelta = Math.Abs(left.G - right.G);
                int blueDelta = Math.Abs(left.B - right.B);
                int alphaDelta = Math.Abs(left.A - right.A);

                absoluteDeltaSum += redDelta + greenDelta + blueDelta + alphaDelta;
                squaredDeltaSum +=
                    redDelta * redDelta
                    + greenDelta * greenDelta
                    + blueDelta * blueDelta
                    + alphaDelta * alphaDelta;
                maximumDelta = Math.Max(
                    maximumDelta,
                    Math.Max(
                        Math.Max(redDelta, greenDelta),
                        Math.Max(blueDelta, alphaDelta)));
                bool different =
                    redDelta > channelTolerance
                    || greenDelta > channelTolerance
                    || blueDelta > channelTolerance
                    || alphaDelta > channelTolerance;

                if (different)
                    differentPixels++;
            }
        }

        double differingFraction = comparedPixels == 0
            ? throw new InvalidDataException(
                "Screenshot mask excludes every pixel; no comparison is possible.")
            : differentPixels / (double)comparedPixels;
        long comparedChannels = comparedPixels * 4;
        return new ScreenshotComparison(
            Passed: differingFraction <= maxDifferentFraction,
            Width: actual.Width,
            Height: actual.Height,
            ExpectedWidth: expected.Width,
            ExpectedHeight: expected.Height,
            ChannelTolerance: channelTolerance,
            MaxDifferentPixelFraction: maxDifferentFraction,
            ComparedPixels: comparedPixels,
            MaskedPixels: maskedPixels,
            DifferentPixels: differentPixels,
            DifferentPixelFraction: differingFraction,
            MaximumChannelDelta: maximumDelta,
            MeanAbsoluteChannelDelta: comparedChannels == 0
                ? 0
                : absoluteDeltaSum / (double)comparedChannels,
            RootMeanSquareChannelDelta: comparedChannels == 0
                ? 0
                : Math.Sqrt(squaredDeltaSum / (double)comparedChannels),
            ExpectedPath: Path.GetFileName(expectedPath),
            ActualPath: Path.GetFileName(actualPath),
            MaskPath: maskPath is null ? null : Path.GetFileName(maskPath));
    }

    private static IReadOnlyList<FrameRow> ReadFrames(string path)
    {
        string[] lines = File.ReadAllLines(path);
        if (lines.Length == 0)
            throw new InvalidDataException("Frame-history CSV is empty.");

        string[] header = lines[0].Split(',');
        var columns = header
            .Select((name, index) => (name, index))
            .ToDictionary(pair => pair.name, pair => pair.index, StringComparer.Ordinal);
        string[] required =
        [
            "frame", "timestamp_utc", "cpu_us", "gpu_us", "alloc_bytes",
            "update_us", "upload_us", "imgui_us", "pacing_us",
        ];
        foreach (string name in required)
        {
            if (!columns.ContainsKey(name))
                throw new InvalidDataException($"Frame-history CSV is missing '{name}'.");
        }

        var rows = new List<FrameRow>(lines.Length - 1);
        for (int lineNumber = 1; lineNumber < lines.Length; lineNumber++)
        {
            if (string.IsNullOrWhiteSpace(lines[lineNumber]))
                continue;
            string[] values = lines[lineNumber].Split(',');
            try
            {
                rows.Add(new FrameRow(
                    ParseInt(values, columns, "frame"),
                    DateTime.Parse(
                        values[columns["timestamp_utc"]],
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                    ParseLong(values, columns, "cpu_us"),
                    ParseLong(values, columns, "gpu_us"),
                    ParseLong(values, columns, "alloc_bytes"),
                    ParseLong(values, columns, "update_us"),
                    ParseLong(values, columns, "upload_us"),
                    ParseLong(values, columns, "imgui_us"),
                    ParseLong(values, columns, "pacing_us")));
            }
            catch (Exception error) when (
                error is FormatException
                or IndexOutOfRangeException
                or KeyNotFoundException)
            {
                throw new InvalidDataException(
                    $"Invalid frame-history row {lineNumber + 1}: {error.Message}",
                    error);
            }
        }

        return rows;
    }

    private static IReadOnlyList<CheckpointRow> ReadCheckpoints(string path)
    {
        var rows = new List<CheckpointRow>();
        CheckpointRaw? previous = null;
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            JsonElement resources = root.GetProperty("resources");
            var current = new CheckpointRaw(
                root.GetProperty("name").GetString()
                    ?? throw new InvalidDataException("Checkpoint name is null."),
                root.GetProperty("timestampUtc").GetDateTime().ToUniversalTime(),
                resources.GetProperty("processTotalAllocatedBytes").GetInt64());
            double? allocationRate = null;
            if (previous is not null)
            {
                double seconds = (current.TimestampUtc - previous.TimestampUtc).TotalSeconds;
                if (seconds > 0)
                {
                    allocationRate = Math.Max(
                        0,
                        current.ProcessAllocatedBytes - previous.ProcessAllocatedBytes)
                        / seconds;
                }
            }

            rows.Add(new CheckpointRow(
                current.Name,
                current.TimestampUtc,
                allocationRate));
            previous = current;
        }

        return rows;
    }

    private static IReadOnlyList<PortalMarker> ReadPortalMarkers(string path)
    {
        var markers = new List<PortalMarker>();
        foreach (string line in File.ReadLines(path))
        {
            Match match = PortalMarkerPattern().Match(line);
            if (!match.Success)
                continue;
            markers.Add(new PortalMarker(
                DateTime.Parse(
                    match.Groups["timestamp"].Value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                match.Groups["destination"].Value));
        }

        return markers;
    }

    private static WindowSummary SummarizeWindow(IEnumerable<FrameRow> source)
    {
        FrameRow[] rows = source.OrderBy(row => row.TimestampUtc).ToArray();
        return new WindowSummary(
            FrameCount: rows.Length,
            DurationSeconds: rows.Length < 2
                ? 0
                : (rows[^1].TimestampUtc - rows[0].TimestampUtc).TotalSeconds,
            CpuUs: SummarizeMetric(rows.Select(row => row.CpuUs)),
            GpuUs: SummarizeMetric(rows.Where(row => row.GpuUs >= 0).Select(row => row.GpuUs)),
            AllocationBytes: SummarizeMetric(rows.Select(row => row.AllocBytes)),
            UpdateUs: SummarizeMetric(rows.Select(row => row.UpdateUs)),
            UploadUs: SummarizeMetric(rows.Select(row => row.UploadUs)),
            ImGuiUs: SummarizeMetric(rows.Select(row => row.ImGuiUs)),
            PacingUs: SummarizeMetric(rows.Select(row => row.PacingUs)),
            TotalPositiveAllocationBytes: rows.Sum(row => Math.Max(0, row.AllocBytes)),
            CpuStandardDeviationUs: StandardDeviation(rows.Select(row => row.CpuUs)));
    }

    private static MetricSummary SummarizeMetric(IEnumerable<long> source)
    {
        long[] values = source.Order().ToArray();
        if (values.Length == 0)
            return new MetricSummary(0, 0, 0, 0, 0, 0, 0);
        return new MetricSummary(
            values.Length,
            Percentile(values, 0.50),
            Percentile(values, 0.95),
            Percentile(values, 0.99),
            Percentile(values, 0.999),
            values[^1],
            values.Average());
    }

    private static long Percentile(long[] sorted, double percentile)
    {
        int index = (int)Math.Ceiling(sorted.Length * percentile) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static double StandardDeviation(IEnumerable<long> source)
    {
        long[] values = source.ToArray();
        if (values.Length == 0)
            return 0;
        double average = values.Average();
        double variance = values.Sum(value => Math.Pow(value - average, 2)) / values.Length;
        return Math.Sqrt(variance);
    }

    private static int ParseInt(
        string[] values,
        IReadOnlyDictionary<string, int> columns,
        string name) =>
        int.Parse(values[columns[name]], CultureInfo.InvariantCulture);

    private static long ParseLong(
        string[] values,
        IReadOnlyDictionary<string, int> columns,
        string name) =>
        long.Parse(values[columns[name]], CultureInfo.InvariantCulture);

    [GeneratedRegex(
        "^(?<timestamp>\\S+) materialized destination='(?<destination>[^']+)'",
        RegexOptions.CultureInvariant)]
    private static partial Regex PortalMarkerPattern();

    private sealed record FrameRow(
        int Frame,
        DateTime TimestampUtc,
        long CpuUs,
        long GpuUs,
        long AllocBytes,
        long UpdateUs,
        long UploadUs,
        long ImGuiUs,
        long PacingUs);

    private sealed record CheckpointRaw(
        string Name,
        DateTime TimestampUtc,
        long ProcessAllocatedBytes);

    private sealed record CheckpointRow(
        string Name,
        DateTime TimestampUtc,
        double? ProcessAllocationRateBytesPerSecond);

    private sealed record PortalMarker(DateTime TimestampUtc, string Destination);
}

public sealed record MetricSummary(
    int Count,
    long P50,
    long P95,
    long P99,
    long P999,
    long Maximum,
    double Mean);

public sealed record WindowSummary(
    int FrameCount,
    double DurationSeconds,
    MetricSummary CpuUs,
    MetricSummary GpuUs,
    MetricSummary AllocationBytes,
    MetricSummary UpdateUs,
    MetricSummary UploadUs,
    MetricSummary ImGuiUs,
    MetricSummary PacingUs,
    long TotalPositiveAllocationBytes,
    double CpuStandardDeviationUs);

public sealed record NamedWindowSummary(
    string Name,
    DateTime StartUtc,
    DateTime EndUtc,
    WindowSummary Metrics,
    double? ProcessAllocationRateBytesPerSecond);

public sealed record WorstFrame(
    int Frame,
    double RelativeToMaterializationMs,
    long CpuUs,
    long GpuUs,
    long AllocationBytes,
    long UpdateUs,
    long UploadUs,
    long PacingUs);

public sealed record PortalWindowSummary(
    string Destination,
    DateTime MaterializedUtc,
    WindowSummary Metrics,
    IReadOnlyList<WorstFrame> WorstFrames);

public sealed record FrameHistoryReport(
    string SourceFrameHistory,
    int FrameCount,
    DateTime? FirstFrameUtc,
    DateTime? LastFrameUtc,
    WindowSummary Route,
    IReadOnlyList<NamedWindowSummary> CheckpointWindows,
    IReadOnlyList<PortalWindowSummary> PortalWindows);

public sealed record ScreenshotComparison(
    bool Passed,
    int Width,
    int Height,
    int ExpectedWidth,
    int ExpectedHeight,
    int ChannelTolerance,
    double MaxDifferentPixelFraction,
    long ComparedPixels,
    long MaskedPixels,
    long DifferentPixels,
    double DifferentPixelFraction,
    int MaximumChannelDelta,
    double MeanAbsoluteChannelDelta,
    double RootMeanSquareChannelDelta,
    string ExpectedPath,
    string ActualPath,
    string? MaskPath);
