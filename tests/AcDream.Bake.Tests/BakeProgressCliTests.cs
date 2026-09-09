using System.Text.Json;
using AcDream.Content.Pak;

namespace AcDream.Bake.Tests;

public sealed class BakeProgressCliTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void HelpIsAZeroDatArgumentProbe(string argument)
    {
        Assert.True(BakeCommandLine.IsHelpRequest([argument]));
        Assert.False(BakeCommandLine.IsHelpRequest([argument, "extra"]));
        Assert.Contains("--help", BakeCommandLine.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressJsonFlagIsOptInAndDefaultOutputRemainsInTheDatDirectory()
    {
        using var errors = new StringWriter();
        Assert.True(BakeCommandLine.TryParse(
            ["--dat-dir", "retail-dats"],
            errors,
            out BakeCommandLineOptions? defaults));

        Assert.NotNull(defaults);
        Assert.False(defaults.ProgressJson);
        Assert.Equal(
            Path.Combine("retail-dats", "acdream.pak"),
            defaults.OutputPath);

        Assert.True(BakeCommandLine.TryParse(
            [
                "--dat-dir", "retail-dats",
                "--out", "prepared/acdream.pak",
                "--threads", "7",
                "--progress-json",
            ],
            errors,
            out BakeCommandLineOptions? machine));

        Assert.NotNull(machine);
        Assert.True(machine.ProgressJson);
        Assert.Equal("prepared/acdream.pak", machine.OutputPath);
        Assert.Equal(7, machine.Threads);
        Assert.Contains("--progress-json", BakeCommandLine.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void HumanFiveSecondLineIsAlwaysWrittenAndJsonIsOnlyWrittenWhenEnabled()
    {
        using var humanOnly = new StringWriter();
        BakeProgressReporter.Write(
            humanOnly,
            machineOutput: null,
            phase: "mesh",
            completed: 1250,
            total: 5000,
            failures: 2,
            elapsed: TimeSpan.FromSeconds(5),
            etaSeconds: 15,
            privateBytes: 64L * 1024 * 1024,
            managedBytes: 16L * 1024 * 1024);

        string defaultText = humanOnly.ToString();
        Assert.Contains("[00:00:05] extracted", defaultText, StringComparison.Ordinal);
        Assert.Contains("failures=2", defaultText, StringComparison.Ordinal);
        Assert.DoesNotContain("\"v\":", defaultText, StringComparison.Ordinal);

        using var combined = new StringWriter();
        var json = new BakeProgressJsonWriter(combined);
        BakeProgressReporter.Write(
            combined,
            json,
            phase: "collision",
            completed: 5,
            total: 10,
            failures: 0,
            elapsed: TimeSpan.FromSeconds(10),
            etaSeconds: 10,
            privateBytes: 1,
            managedBytes: 2);

        string[] lines = combined.ToString().Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("[00:00:10] extracted", lines[0], StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(lines[1]);
        Assert.Equal(1, document.RootElement.GetProperty("v").GetInt32());
        Assert.Equal("progress", document.RootElement.GetProperty("e").GetString());
        Assert.Equal("collision", document.RootElement.GetProperty("phase").GetString());
    }

    [Fact]
    public void VersionedWriterCarriesCurrentBakeVersionOnTerminalRecords()
    {
        using var output = new StringWriter();
        var writer = new BakeProgressJsonWriter(output);

        writer.Started(PakFormat.CurrentBakeToolVersion, "prepared/acdream.pak");
        writer.Completed(PakFormat.CurrentBakeToolVersion, 1234, failures: 0);

        JsonElement[] events = output.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();
        Assert.Equal(["started", "completed"], events.Select(value =>
            value.GetProperty("e").GetString()));
        Assert.All(events, value => Assert.Equal(
            PakFormat.CurrentBakeToolVersion,
            value.GetProperty("bakeToolVersion").GetUInt32()));
    }
}
