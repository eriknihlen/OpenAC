using AcDream.Runtime.Chat;
using AcDream.Runtime.Session;

namespace AcDream.Runtime.Tests.Chat;

public sealed class LoginCommandSequenceTests
{
    [Fact]
    public void EnteredWorldExecutesImmediatelyThenHonorsMonotonicDelay()
    {
        var time = new ManualTimeProvider();
        var sent = new List<string>();
        var bus = TalkBus(sent);
        var sequence = new LoginCommandSequence(
            ["one", "two", "three"],
            TimeSpan.FromMilliseconds(500),
            new RecordingFeedback(),
            bus,
            timeProvider: time);
        var generation = new RuntimeGenerationToken(7);

        sequence.EnteredWorld(generation);
        Assert.Equal(["one"], sent);

        time.Advance(TimeSpan.FromMilliseconds(499));
        sequence.Tick(generation, isInWorld: true);
        Assert.Equal(["one"], sent);

        time.Advance(TimeSpan.FromMilliseconds(1));
        sequence.Tick(generation, isInWorld: true);
        Assert.Equal(["one", "two"], sent);

        time.Advance(TimeSpan.FromMilliseconds(500));
        sequence.Tick(generation, isInWorld: true);
        Assert.Equal(["one", "two", "three"], sent);
        Assert.False(sequence.IsActive);
    }

    [Fact]
    public void HandlerRuntimeDoesNotConsumeTheInterCommandDelay()
    {
        var time = new ManualTimeProvider();
        var sent = new List<string>();
        var bus = new LiveCommandBus();
        bus.Register<SendChatCmd>(command =>
        {
            sent.Add(command.Text);
            time.Advance(TimeSpan.FromSeconds(1));
        });
        var sequence = new LoginCommandSequence(
            ["one", "two"],
            TimeSpan.FromMilliseconds(500),
            new RecordingFeedback(),
            bus,
            timeProvider: time);
        var generation = new RuntimeGenerationToken(7);

        sequence.EnteredWorld(generation);
        Assert.Equal(["one"], sent);

        time.Advance(TimeSpan.FromMilliseconds(499));
        sequence.Tick(generation, isInWorld: true);
        Assert.Equal(["one"], sent);

        time.Advance(TimeSpan.FromMilliseconds(1));
        sequence.Tick(generation, isInWorld: true);
        Assert.Equal(["one", "two"], sent);
    }

    [Fact]
    public void ParseAndHandlerFailuresAndStatusFailureAllContinue()
    {
        var sent = new List<string>();
        var bus = TalkBus(sent);
        bus.Register<ExecuteClientCommandCmd>(_ =>
            throw new InvalidOperationException("client boom"));
        int reports = 0;
        var sequence = new LoginCommandSequence(
            ["/", "/version", "after"],
            TimeSpan.Zero,
            new RecordingFeedback(),
            bus,
            _ =>
            {
                reports++;
                throw new IOException("status unavailable");
            });

        sequence.EnteredWorld(new RuntimeGenerationToken(1));

        Assert.Equal(2, reports);
        Assert.Equal(["after"], sent);
        Assert.False(sequence.IsActive);
    }

    [Fact]
    public void CancelAndGenerationReplacementPreventLeaksAndReconnectRestarts()
    {
        var time = new ManualTimeProvider();
        var sent = new List<string>();
        var sequence = new LoginCommandSequence(
            ["one", "two"],
            TimeSpan.FromMilliseconds(500),
            new RecordingFeedback(),
            TalkBus(sent),
            timeProvider: time);
        var first = new RuntimeGenerationToken(1);
        var second = new RuntimeGenerationToken(2);

        sequence.EnteredWorld(first);
        sequence.Cancel(first);
        time.Advance(TimeSpan.FromSeconds(1));
        sequence.Tick(first, isInWorld: true);
        sequence.EnteredWorld(first); // exact-once per generation
        Assert.Equal(["one"], sent);

        sequence.EnteredWorld(second);
        sequence.Tick(first, isInWorld: true); // stale frame is inert
        time.Advance(TimeSpan.FromMilliseconds(500));
        sequence.Tick(second, isInWorld: true);

        Assert.Equal(["one", "one", "two"], sent);
    }

    [Fact]
    public void NullAndEmptyCollectionsAreNoOpsAndNullEntriesTypeAsEmpty()
    {
        var feedback = new RecordingFeedback();
        var bus = TalkBus([]);
        var absent = new LoginCommandSequence(
            null,
            TimeSpan.Zero,
            feedback,
            bus);
        var empty = new LoginCommandSequence(
            [],
            TimeSpan.Zero,
            feedback,
            bus);
        var nullEntry = new LoginCommandSequence(
            [null],
            TimeSpan.Zero,
            feedback,
            bus);

        absent.EnteredWorld(new RuntimeGenerationToken(1));
        empty.EnteredWorld(new RuntimeGenerationToken(1));
        nullEntry.EnteredWorld(new RuntimeGenerationToken(1));

        Assert.False(absent.IsActive);
        Assert.False(empty.IsActive);
        Assert.False(nullEntry.IsActive);
    }

    private static LiveCommandBus TalkBus(List<string> sent)
    {
        var bus = new LiveCommandBus();
        bus.Register<SendChatCmd>(command => sent.Add(command.Text));
        bus.Register<SendServerCommandCmd>(command => sent.Add(command.Text));
        bus.Register<SendRawChannelCmd>(_ => { });
        return bus;
    }

    private sealed class RecordingFeedback : IChatCommandFeedback
    {
        public string? LastIncomingTellSender { get; set; }
        public string? LastOutgoingTellTarget { get; set; }
        public List<string> Interface { get; } = [];
        public List<string> System { get; } = [];
        public void ShowInterfaceText(string text) => Interface.Add(text);
        public void ShowSystemMessage(string text) => System.Add(text);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => 1_000;
        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration) =>
            _timestamp += checked((long)duration.TotalMilliseconds);
    }
}
