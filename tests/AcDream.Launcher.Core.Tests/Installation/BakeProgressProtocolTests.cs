using AcDream.Launcher.Core.Installation;

namespace AcDream.Launcher.Core.Tests.Installation;

public sealed class BakeProgressProtocolTests
{
    [Fact]
    public void OneStartedProgressAndCompletedSequenceIsAccepted()
    {
        var protocol = new BakeProgressProtocol();

        Assert.True(protocol.Observe(new BakeHumanOutputEvent("human")));
        Assert.True(protocol.Observe(new UnknownBakeProgressEvent(
            1,
            "newMetric",
            "{}")));
        Assert.True(protocol.Observe(new FutureBakeProgressEvent(
            2,
            "started",
            "{}")));
        Assert.True(protocol.Observe(new BakeStartedEvent(1, 4, "pak")));
        Assert.True(protocol.Observe(new BakeWorkProgressEvent(
            1,
            "mesh",
            1,
            2,
            0,
            1,
            1)));
        Assert.True(protocol.Observe(new BakeCompletedEvent(1, 4, 100, 0)));

        protocol.CompleteInput();

        Assert.Null(protocol.Violation);
        Assert.NotNull(protocol.Started);
        Assert.NotNull(protocol.Completed);
        Assert.Null(protocol.Error);
    }

    [Theory]
    [MemberData(nameof(InvalidKnownSequences))]
    public void OutOfOrderDuplicateAndPostTerminalKnownEventsAreRejected(
        BakeProgressEvent[] events)
    {
        var protocol = new BakeProgressProtocol();

        foreach (BakeProgressEvent progressEvent in events)
        {
            protocol.Observe(progressEvent);
        }

        protocol.CompleteInput();

        Assert.NotNull(protocol.Violation);
    }

    [Fact]
    public void ErrorTerminalCannotBeOverwrittenByContradictoryCompletion()
    {
        var protocol = new BakeProgressProtocol();
        var failure = new BakeErrorEvent(1, "first failure");

        Assert.True(protocol.Observe(new BakeStartedEvent(1, 4, null)));
        Assert.True(protocol.Observe(failure));
        Assert.False(protocol.Observe(new BakeCompletedEvent(1, 4, 10, 0)));
        protocol.CompleteInput();

        Assert.Same(failure, protocol.Error);
        Assert.Null(protocol.Completed);
        Assert.Contains("after", protocol.Violation, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<BakeProgressEvent[]> InvalidKnownSequences => new()
    {
        new BakeProgressEvent[]
        {
            new BakeWorkProgressEvent(1, "mesh", 0, 1, 0, 0, 0),
        },
        new BakeProgressEvent[]
        {
            new BakeCompletedEvent(1, 4, 10, 0),
        },
        new BakeProgressEvent[]
        {
            new BakeErrorEvent(1, "before start"),
        },
        new BakeProgressEvent[]
        {
            new BakeStartedEvent(1, 4, null),
            new BakeStartedEvent(1, 4, null),
        },
        new BakeProgressEvent[]
        {
            new BakeStartedEvent(1, 4, null),
            new BakeCompletedEvent(1, 4, 10, 0),
            new BakeCompletedEvent(1, 4, 10, 0),
        },
        new BakeProgressEvent[]
        {
            new BakeStartedEvent(1, 4, null),
            new BakeCompletedEvent(1, 4, 10, 0),
            new BakeWorkProgressEvent(1, "mesh", 1, 1, 0, 1, 0),
        },
        new BakeProgressEvent[]
        {
            new BakeStartedEvent(1, 4, null),
        },
    };
}
