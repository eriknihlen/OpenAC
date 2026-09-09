using AcDream.Launcher.Core.Installation;

namespace AcDream.Launcher.Core.Tests.Installation;

public sealed class BakeProgressJsonlParserTests
{
    [Fact]
    public void PartialChunksAreBufferedUntilTheJsonLineIsComplete()
    {
        var parser = new BakeProgressJsonlParser();

        Assert.IsType<BakeHumanOutputEvent>(Assert.Single(
            parser.Append("human startup text\n{\"v\":1,\"e\":\"pro")));
        IReadOnlyList<BakeProgressEvent> events = parser.Append(
            "gress\",\"phase\":\"mesh\",\"completed\":4,\"total\":10,"
            + "\"failures\":0,\"elapsedSeconds\":5,\"etaSeconds\":7}\n");

        Assert.Single(events);
        BakeWorkProgressEvent progress =
            Assert.IsType<BakeWorkProgressEvent>(events[0]);
        Assert.Equal("mesh", progress.Phase);
        Assert.Equal(4, progress.Completed);
        Assert.Equal(10, progress.Total);
    }

    [Fact]
    public void MalformedKnownPayloadAndTruncatedFinalLineNeverThrow()
    {
        var parser = new BakeProgressJsonlParser();
        IReadOnlyList<BakeProgressEvent> first = parser.Append(
            "{\"v\":1,\"e\":\"progress\",\"phase\":\"mesh\"}\n"
            + "{not-json");
        Assert.IsType<MalformedBakeProgressEvent>(Assert.Single(first));

        MalformedBakeProgressEvent final = Assert.IsType<MalformedBakeProgressEvent>(
            Assert.Single(parser.Complete()));
        Assert.False(string.IsNullOrWhiteSpace(final.Reason));
    }

    [Fact]
    public void UnknownKindsAndFutureVersionsRemainTypedAndFutureSafe()
    {
        var parser = new BakeProgressJsonlParser();
        IReadOnlyList<BakeProgressEvent> events = parser.Append(
            "{\"v\":1,\"e\":\"newMetric\",\"value\":9}\n"
            + "{\"v\":2,\"e\":\"progress\",\"newShape\":true}\n"
            + "{\"v\":1,\"e\":\"started\",\"bakeToolVersion\":4,"
            + "\"outputPath\":\"pak\",\"futureField\":42}\n");

        Assert.IsType<UnknownBakeProgressEvent>(events[0]);
        FutureBakeProgressEvent future =
            Assert.IsType<FutureBakeProgressEvent>(events[1]);
        Assert.Equal(2, future.Version);
        BakeStartedEvent started = Assert.IsType<BakeStartedEvent>(events[2]);
        Assert.Equal(4u, started.BakeToolVersion);
    }
}
