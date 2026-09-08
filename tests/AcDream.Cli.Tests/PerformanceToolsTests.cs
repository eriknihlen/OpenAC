using System.Text.Json;
using AcDream.Cli;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AcDream.Cli.Tests;

public sealed class PerformanceToolsTests
{
    [Fact]
    public void CompareImages_AppliesChannelToleranceAndMask()
    {
        using var directory = new TemporaryDirectory();
        string expectedPath = Path.Combine(directory.Path, "expected.png");
        string actualPath = Path.Combine(directory.Path, "actual.png");
        string maskPath = Path.Combine(directory.Path, "mask.png");

        using (var expected = new Image<Rgba32>(2, 1))
        using (var actual = new Image<Rgba32>(2, 1))
        using (var mask = new Image<Rgba32>(2, 1))
        {
            expected[0, 0] = new Rgba32(10, 20, 30, 255);
            expected[1, 0] = new Rgba32(40, 50, 60, 255);
            actual[0, 0] = new Rgba32(12, 20, 30, 255);
            actual[1, 0] = new Rgba32(200, 50, 60, 255);
            mask[1, 0] = new Rgba32(0, 0, 0, 255);
            expected.SaveAsPng(expectedPath);
            actual.SaveAsPng(actualPath);
            mask.SaveAsPng(maskPath);
        }

        ScreenshotComparison unmasked = PerformanceTools.CompareImages(
            expectedPath,
            actualPath,
            channelTolerance: 2,
            maxDifferentFraction: 0);
        ScreenshotComparison masked = PerformanceTools.CompareImages(
            expectedPath,
            actualPath,
            channelTolerance: 2,
            maxDifferentFraction: 0,
            maskPath);

        Assert.False(unmasked.Passed);
        Assert.Equal(1, unmasked.DifferentPixels);
        Assert.True(masked.Passed);
        Assert.Equal(1, masked.ComparedPixels);
        Assert.Equal(1, masked.MaskedPixels);
        Assert.Equal(0, masked.DifferentPixels);
    }

    [Fact]
    public void CompareImages_RejectsAFullyMaskedComparison()
    {
        using var directory = new TemporaryDirectory();
        string expectedPath = Path.Combine(directory.Path, "expected.png");
        string actualPath = Path.Combine(directory.Path, "actual.png");
        string maskPath = Path.Combine(directory.Path, "mask.png");
        using (var image = new Image<Rgba32>(1, 1))
        using (var mask = new Image<Rgba32>(1, 1))
        {
            mask[0, 0] = new Rgba32(0, 0, 0, 255);
            image.SaveAsPng(expectedPath);
            image.SaveAsPng(actualPath);
            mask.SaveAsPng(maskPath);
        }

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            PerformanceTools.CompareImages(
                expectedPath,
                actualPath,
                channelTolerance: 2,
                maxDifferentFraction: 0.001,
                maskPath));

        Assert.Contains("excludes every pixel", error.Message);
    }

    [Fact]
    public void CompareScreenshots_WritesSerializableDimensionMismatch()
    {
        using var directory = new TemporaryDirectory();
        string expectedPath = Path.Combine(directory.Path, "expected.png");
        string actualPath = Path.Combine(directory.Path, "actual.png");
        string outputPath = Path.Combine(directory.Path, "comparison.json");
        using (var expected = new Image<Rgba32>(1, 1))
        using (var actual = new Image<Rgba32>(2, 1))
        {
            expected.SaveAsPng(expectedPath);
            actual.SaveAsPng(actualPath);
        }

        int exitCode = PerformanceTools.CompareScreenshots(
            [expectedPath, actualPath, outputPath]);

        Assert.Equal(1, exitCode);
        using JsonDocument report = JsonDocument.Parse(File.ReadAllText(outputPath));
        Assert.False(report.RootElement.GetProperty("passed").GetBoolean());
        Assert.Equal("expected.png", report.RootElement.GetProperty("expectedPath").GetString());
        Assert.Equal("actual.png", report.RootElement.GetProperty("actualPath").GetString());
    }

    [Fact]
    public void SummarizeFrameHistory_WritesRouteCheckpointAndPortalPopulations()
    {
        using var directory = new TemporaryDirectory();
        string framesPath = Path.Combine(directory.Path, "frames.csv");
        string checkpointsPath = Path.Combine(directory.Path, "checkpoints.jsonl");
        string markersPath = Path.Combine(directory.Path, "markers.log");
        string outputPath = Path.Combine(directory.Path, "summary.json");

        File.WriteAllLines(framesPath,
        [
            "frame,timestamp_ms,timestamp_utc,cpu_us,gpu_us,alloc_bytes,update_us,upload_us,imgui_us,pacing_us",
            "0,0.000,2026-07-24T10:00:00.0000000Z,1000,500,100,100,10,0,0",
            "1,1000.000,2026-07-24T10:00:01.0000000Z,2000,600,200,200,20,0,0",
            "2,2000.000,2026-07-24T10:00:02.0000000Z,9000,700,300,300,30,0,0",
        ]);
        File.WriteAllText(
            checkpointsPath,
            """
            {"name":"baseline","timestampUtc":"2026-07-24T10:00:01Z","resources":{"processTotalAllocatedBytes":1000}}
            {"name":"return","timestampUtc":"2026-07-24T10:00:02Z","resources":{"processTotalAllocatedBytes":5000}}

            """);
        File.WriteAllText(
            markersPath,
            "2026-07-24T10:00:01.0000000Z materialized destination='Arwic' occurrence=1 elapsed=1.0s");

        int exitCode = PerformanceTools.SummarizeFrameHistory(
            [framesPath, checkpointsPath, markersPath, outputPath]);

        Assert.Equal(0, exitCode);
        using JsonDocument report = JsonDocument.Parse(File.ReadAllText(outputPath));
        JsonElement root = report.RootElement;
        Assert.Equal(3, root.GetProperty("frameCount").GetInt32());
        Assert.Equal(
            9000,
            root.GetProperty("route").GetProperty("cpuUs").GetProperty("p99").GetInt64());
        Assert.Equal(
            2,
            root.GetProperty("checkpointWindows").GetArrayLength());
        Assert.Equal(
            4000,
            root.GetProperty("checkpointWindows")[1]
                .GetProperty("processAllocationRateBytesPerSecond")
                .GetDouble());
        Assert.Equal(
            "Arwic",
            root.GetProperty("portalWindows")[0].GetProperty("destination").GetString());
        Assert.Equal(
            9000,
            root.GetProperty("portalWindows")[0]
                .GetProperty("worstFrames")[0]
                .GetProperty("cpuUs")
                .GetInt64());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "acdream-cli-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
