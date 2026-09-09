using System.Text;
using AcDream.Launcher.Core.Status;

namespace AcDream.Launcher.Core.Tests.Status;

public sealed class StatusFileTailerTests : IDisposable
{
    private readonly string _root;
    private readonly string _path;

    public StatusFileTailerTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "acdream-launcher-tailer-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "status.jsonl");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ReturnsNoEventsWhenTheFileDoesNotExistYet()
    {
        var tailer = new StatusFileTailer(_path);

        IReadOnlyList<StatusEvent> events = tailer.ReadNewEvents();

        Assert.Empty(events);
    }

    [Fact]
    public void ReturnsNoEventsWhenNothingHasBeenAppendedSinceTheLastPoll()
    {
        AppendShared(Line("started", "s1"));
        var tailer = new StatusFileTailer(_path);
        Assert.Single(tailer.ReadNewEvents());

        IReadOnlyList<StatusEvent> events = tailer.ReadNewEvents();

        Assert.Empty(events);
    }

    [Fact]
    public void ReadsMultipleCompleteLinesInOnePoll()
    {
        AppendShared(Line("started", "s1") + Line("connected", "s1"));
        var tailer = new StatusFileTailer(_path);

        IReadOnlyList<StatusEvent> events = tailer.ReadNewEvents();

        Assert.Equal(2, events.Count);
        Assert.IsType<StartedStatusEvent>(events[0]);
        Assert.IsType<ConnectedStatusEvent>(events[1]);
    }

    [Fact]
    public void ContinuesPastCompleteNonObjectJsonValuesToTheFollowingValidLine()
    {
        AppendShared(
            "[]\nnull\n42\n\"text\"\n"
            + Line("connected", "s1"));
        var tailer = new StatusFileTailer(_path);

        IReadOnlyList<StatusEvent> events = tailer.ReadNewEvents();

        Assert.Equal(5, events.Count);
        Assert.All(events.Take(4), e => Assert.IsType<MalformedStatusEvent>(e));
        Assert.IsType<ConnectedStatusEvent>(events[4]);
    }

    [Fact]
    public void TolerateAPartialLastLineAndCompletesItOnALaterPoll()
    {
        string full = Line("started", "s1");
        int splitAt = full.Length - 10;
        AppendShared(full[..splitAt]);
        var tailer = new StatusFileTailer(_path);

        IReadOnlyList<StatusEvent> firstPoll = tailer.ReadNewEvents();
        Assert.Empty(firstPoll);

        AppendShared(full[splitAt..]);
        IReadOnlyList<StatusEvent> secondPoll = tailer.ReadNewEvents();

        StatusEvent onlyEvent = Assert.Single(secondPoll);
        Assert.IsType<StartedStatusEvent>(onlyEvent);
    }

    [Fact]
    public void APartialLineFollowedByAFullLineOnlyEmitsTheCompleteOne()
    {
        AppendShared(Line("started", "s1"));
        string partial = """{"v":1,"e":"connected","t":"2026-08-14T12:00:00Z","sessionId":"s1""";
        AppendShared(partial);
        var tailer = new StatusFileTailer(_path);

        IReadOnlyList<StatusEvent> events = tailer.ReadNewEvents();

        StatusEvent onlyEvent = Assert.Single(events);
        Assert.IsType<StartedStatusEvent>(onlyEvent);

        AppendShared("\"}\n");
        IReadOnlyList<StatusEvent> secondPoll = tailer.ReadNewEvents();
        StatusEvent completed = Assert.Single(secondPoll);
        Assert.IsType<ConnectedStatusEvent>(completed);
    }

    [Fact]
    public void SkipsBlankLines()
    {
        AppendShared("\n" + Line("started", "s1") + "\n" + Line("connected", "s1"));
        var tailer = new StatusFileTailer(_path);

        IReadOnlyList<StatusEvent> events = tailer.ReadNewEvents();

        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void ReadsWithAWriterHoldingTheFileOpenForAppend()
    {
        // Share-tolerant reads: the writer's handle stays open the whole
        // time (FileShare.ReadWrite on both sides), matching a live host
        // process appending status.jsonl while the launcher tails it.
        using var writer = new FileStream(
            _path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        var tailer = new StatusFileTailer(_path);

        byte[] first = Encoding.UTF8.GetBytes(Line("started", "s1"));
        writer.Write(first, 0, first.Length);
        writer.Flush();

        IReadOnlyList<StatusEvent> firstPoll = tailer.ReadNewEvents();
        Assert.Single(firstPoll);

        byte[] second = Encoding.UTF8.GetBytes(Line("connected", "s1"));
        writer.Write(second, 0, second.Length);
        writer.Flush();

        IReadOnlyList<StatusEvent> secondPoll = tailer.ReadNewEvents();
        Assert.Single(secondPoll);
        Assert.IsType<ConnectedStatusEvent>(secondPoll[0]);
    }

    [Fact]
    [Trait("Lane", "Windows")]
    public void ReadNewEventsReturnsEmptyRatherThanThrowingOnASharingViolation()
    {
        // A deterministic proxy for the File.Exists -> new FileStream
        // TOCTOU window: Windows enforces FileShare
        // at the OS level, so holding an exclusive (FileShare.None)
        // handle open while the tailer tries to open the same path
        // reliably reproduces the IOException the tailer must now
        // swallow instead of throwing out of a method documented never
        // to throw. (.NET's FileStream doesn't apply mandatory locking
        // on Linux by default, so this specific scenario isn't
        // reproducible there — the fix itself is platform-agnostic, only
        // this particular deterministic trigger is Windows-only.)
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Lane=Windows requires a Windows host.");

        AppendShared(Line("started", "s1"));
        using var exclusiveHandle = new FileStream(
            _path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var tailer = new StatusFileTailer(_path);

        IReadOnlyList<StatusEvent> events = tailer.ReadNewEvents();

        Assert.Empty(events);
    }

    [Fact]
    public void TailsCharacterCreatedAndCreationFailedEvents()
    {
        AppendShared(
            """{"v":1,"e":"characterCreated","t":"2026-08-15T12:00:00Z","sessionId":"s1","guid":1342177296,"name":"NewChar"}"""
            + "\n"
            + """{"v":1,"e":"creationFailed","t":"2026-08-15T12:00:01Z","sessionId":"s1","code":3,"reason":"NameInUse","name":"Bob"}"""
            + "\n");
        var tailer = new StatusFileTailer(_path);

        IReadOnlyList<StatusEvent> events = tailer.ReadNewEvents();

        Assert.Equal(2, events.Count);
        var created = Assert.IsType<CharacterCreatedStatusEvent>(events[0]);
        Assert.Equal(1342177296u, created.Guid);
        Assert.Equal("NewChar", created.Name);
        var failed = Assert.IsType<CreationFailedStatusEvent>(events[1]);
        Assert.Equal(3u, failed.Code);
        Assert.Equal("NameInUse", failed.Reason);
        Assert.Equal("Bob", failed.Name);
    }

    [Fact]
    public void RestartsFromTheTopWhenTheFileIsTruncatedOrReplaced()
    {
        AppendShared(Line("started", "s1") + Line("connected", "s1"));
        var tailer = new StatusFileTailer(_path);
        Assert.Equal(2, tailer.ReadNewEvents().Count);

        File.Delete(_path);
        AppendShared(Line("started", "s2"));

        IReadOnlyList<StatusEvent> events = tailer.ReadNewEvents();
        StatusEvent onlyEvent = Assert.Single(events);
        Assert.Equal("s2", onlyEvent.SessionId);
    }

    private static string Line(string e, string sessionId) =>
        $$"""{"v":1,"e":"{{e}}","t":"2026-08-14T12:00:00Z","sessionId":"{{sessionId}}"}""" + "\n";

    private void AppendShared(string text)
    {
        using var stream = new FileStream(
            _path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }
}
