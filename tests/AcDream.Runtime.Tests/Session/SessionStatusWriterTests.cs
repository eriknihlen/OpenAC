using System.Text.Json;
using AcDream.Runtime.Session;

namespace AcDream.Runtime.Tests.Session;

public sealed class SessionStatusWriterTests
{
    [Fact]
    public void EachEventWritesTheExactPinnedShapeInOrder()
    {
        using TemporaryFile file = TemporaryFile.Create();
        var writer = new SessionStatusWriter(file.Path);

        writer.Started("s1");
        writer.Connected("s1");
        writer.CharacterList(
            "s1",
            new LiveSessionRosterReport(
                "account",
                11,
                [
                    new LiveSessionRosterEntry(0x50000001u, "Ready", 0u),
                    new LiveSessionRosterEntry(0x50000002u, "Grey", 10u),
                ]));
        writer.EnteredWorld("s1", 0x50000001u, "Ready");
        writer.PluginLoaded("s1", "acdream.good");
        writer.PluginFailed("s1", "acdream.bad", "enable failed");
        writer.LoginCommandFailed("s1", 2, "/version", "unsupported headless command");
        writer.Disconnected("s1", "stopped");
        writer.Exited("s1", 0, "disposed");

        string[] lines = File.ReadAllLines(file.Path);
        Assert.Equal(9, lines.Length);

        JsonElement started = Parse(lines[0]);
        Assert.Equal(1, started.GetProperty("v").GetInt32());
        Assert.Equal("started", started.GetProperty("e").GetString());
        Assert.True(started.TryGetProperty("t", out _));
        Assert.Equal("s1", started.GetProperty("sessionId").GetString());

        JsonElement connected = Parse(lines[1]);
        Assert.Equal("connected", connected.GetProperty("e").GetString());
        Assert.Equal("s1", connected.GetProperty("sessionId").GetString());

        JsonElement characterList = Parse(lines[2]);
        Assert.Equal("characterList", characterList.GetProperty("e").GetString());
        Assert.Equal("account", characterList.GetProperty("accountName").GetString());
        Assert.Equal(11, characterList.GetProperty("slotCount").GetInt32());
        JsonElement characters = characterList.GetProperty("characters");
        Assert.Equal(2, characters.GetArrayLength());
        JsonElement first = characters[0];
        Assert.Equal(0x50000001u, first.GetProperty("id").GetUInt32());
        Assert.Equal("Ready", first.GetProperty("name").GetString());
        Assert.Equal(0u, first.GetProperty("secondsGreyedOut").GetUInt32());

        JsonElement enteredWorld = Parse(lines[3]);
        Assert.Equal("enteredWorld", enteredWorld.GetProperty("e").GetString());
        Assert.Equal(0x50000001u, enteredWorld.GetProperty("characterId").GetUInt32());
        Assert.Equal("Ready", enteredWorld.GetProperty("characterName").GetString());

        JsonElement pluginLoaded = Parse(lines[4]);
        Assert.Equal("pluginLoaded", pluginLoaded.GetProperty("e").GetString());
        Assert.Equal("acdream.good", pluginLoaded.GetProperty("plugin").GetString());

        JsonElement pluginFailed = Parse(lines[5]);
        Assert.Equal("pluginFailed", pluginFailed.GetProperty("e").GetString());
        Assert.Equal("acdream.bad", pluginFailed.GetProperty("plugin").GetString());
        Assert.Equal("enable failed", pluginFailed.GetProperty("error").GetString());

        JsonElement loginCommandFailed = Parse(lines[6]);
        Assert.Equal(1, loginCommandFailed.GetProperty("v").GetInt32());
        Assert.Equal("loginCommandFailed", loginCommandFailed.GetProperty("e").GetString());
        Assert.Equal("s1", loginCommandFailed.GetProperty("sessionId").GetString());
        Assert.Equal(2, loginCommandFailed.GetProperty("commandIndex").GetInt32());
        Assert.Equal("/version", loginCommandFailed.GetProperty("command").GetString());
        Assert.Equal(
            "unsupported headless command",
            loginCommandFailed.GetProperty("error").GetString());
        Assert.Equal(
            ["v", "e", "t", "sessionId", "commandIndex", "command", "error"],
            loginCommandFailed.EnumerateObject()
                .Select(static property => property.Name));

        JsonElement disconnected = Parse(lines[7]);
        Assert.Equal("disconnected", disconnected.GetProperty("e").GetString());
        Assert.Equal("stopped", disconnected.GetProperty("reason").GetString());

        JsonElement exited = Parse(lines[8]);
        Assert.Equal("exited", exited.GetProperty("e").GetString());
        Assert.Equal(0, exited.GetProperty("code").GetInt32());
        Assert.Equal("disposed", exited.GetProperty("reason").GetString());
    }

    [Fact]
    public void NoOpWriterNeverCreatesAFile()
    {
        using TemporaryFile file = TemporaryFile.Reserve();
        var writer = new SessionStatusWriter(null);

        writer.Started("s1");
        writer.Connected("s1");
        writer.PluginLoaded("s1", "acdream.good");
        writer.PluginFailed("s1", "acdream.bad", "failed");
        writer.LoginCommandFailed("s1", 0, "", "unknown command");
        writer.CharacterCreated("s1", 0x50000001u, "NewChar");
        writer.CreationFailed("s1", 3u, "NameInUse", "Bob");
        writer.Disconnected("s1", "stopped");
        writer.Exited("s1", 0, "disposed");

        Assert.False(writer.IsEnabled);
        Assert.False(File.Exists(file.Path));
    }

    [Fact]
    public void CharacterCreatedAndCreationFailed_WriteThePinnedShape()
    {
        using TemporaryFile file = TemporaryFile.Create();
        var writer = new SessionStatusWriter(file.Path);

        writer.CharacterCreated("s1", 0x50000010u, "NewChar");
        writer.CreationFailed("s1", 3u, "NameInUse", "Bob");

        string[] lines = File.ReadAllLines(file.Path);
        Assert.Equal(2, lines.Length);

        JsonElement created = Parse(lines[0]);
        Assert.Equal(1, created.GetProperty("v").GetInt32());
        Assert.Equal("characterCreated", created.GetProperty("e").GetString());
        Assert.Equal("s1", created.GetProperty("sessionId").GetString());
        Assert.Equal(0x50000010u, created.GetProperty("guid").GetUInt32());
        Assert.Equal("NewChar", created.GetProperty("name").GetString());
        AssertExactProperties(lines[0], "v", "e", "t", "sessionId", "guid", "name");

        JsonElement failed = Parse(lines[1]);
        Assert.Equal("creationFailed", failed.GetProperty("e").GetString());
        Assert.Equal("s1", failed.GetProperty("sessionId").GetString());
        Assert.Equal(3u, failed.GetProperty("code").GetUInt32());
        Assert.Equal("NameInUse", failed.GetProperty("reason").GetString());
        Assert.Equal("Bob", failed.GetProperty("name").GetString());
        AssertExactProperties(
            lines[1], "v", "e", "t", "sessionId", "code", "reason", "name");
    }

    [Fact]
    public void BlankPathIsTreatedAsAbsent()
    {
        var writer = new SessionStatusWriter("   ");

        Assert.False(writer.IsEnabled);
        // Must not throw even though there is no real path behind it.
        writer.Started("s1");
    }

    [Fact]
    public void SecondConnectedEdgeFirstClosesTheRetiringConnection()
    {
        using TemporaryFile file = TemporaryFile.Create();
        var writer = new SessionStatusWriter(file.Path);

        writer.Connected("s1");
        writer.Connected("s1");

        JsonElement[] events = File.ReadAllLines(file.Path)
            .Select(Parse)
            .ToArray();
        Assert.Equal(
            ["connected", "disconnected", "connected"],
            events.Select(static item => item.GetProperty("e").GetString()));
        Assert.Equal(
            "reconnect",
            events[1].GetProperty("reason").GetString());
    }

    [Fact]
    public void ExitedIsIdempotentTerminalAndClosesAnOpenConnection()
    {
        using TemporaryFile file = TemporaryFile.Create();
        var writer = new SessionStatusWriter(file.Path);

        writer.Connected("s1");
        writer.Exited("s1", 0, "graceful");
        writer.Exited("s1", 1, "duplicate-must-not-win");
        writer.Connected("s1");

        JsonElement[] events = File.ReadAllLines(file.Path)
            .Select(Parse)
            .ToArray();
        Assert.Equal(
            ["connected", "disconnected", "exited"],
            events.Select(static item => item.GetProperty("e").GetString()));
        Assert.Equal(
            "process-exit",
            events[1].GetProperty("reason").GetString());
        Assert.Equal(0, events[2].GetProperty("code").GetInt32());
        Assert.Equal("graceful", events[2].GetProperty("reason").GetString());
    }

    [Fact]
    public void EachEventSerializesExactlyItsPinnedPropertySetAndNothingElse()
    {
        using TemporaryFile file = TemporaryFile.Create();
        var writer = new SessionStatusWriter(file.Path);

        writer.Started("bot");
        writer.Connected("bot");
        writer.CharacterList(
            "bot",
            new LiveSessionRosterReport(
                "account-name",
                11,
                [new LiveSessionRosterEntry(0x50000001u, "Ready", 0u)]));
        writer.EnteredWorld("bot", 0x50000001u, "Ready");
        writer.PluginLoaded("bot", "acdream.good");
        writer.PluginFailed("bot", "acdream.bad", "enable failed");
        writer.LoginCommandFailed("bot", 1, "/version", "unsupported");
        writer.Disconnected("bot", "stopped");
        writer.Exited("bot", 0, "disposed");

        string[] lines = File.ReadAllLines(file.Path);
        Assert.Equal(9, lines.Length);

        AssertExactProperties(lines[0], "v", "e", "t", "sessionId");
        AssertExactProperties(lines[1], "v", "e", "t", "sessionId");
        AssertExactProperties(
            lines[2],
            "v", "e", "t", "sessionId", "accountName", "slotCount", "characters");
        AssertExactProperties(
            lines[3], "v", "e", "t", "sessionId", "characterId", "characterName");
        AssertExactProperties(lines[4], "v", "e", "t", "sessionId", "plugin");
        AssertExactProperties(
            lines[5], "v", "e", "t", "sessionId", "plugin", "error");
        AssertExactProperties(
            lines[6],
            "v", "e", "t", "sessionId", "commandIndex", "command", "error");
        AssertExactProperties(lines[7], "v", "e", "t", "sessionId", "reason");
        AssertExactProperties(lines[8], "v", "e", "t", "sessionId", "code", "reason");

        JsonElement character = Parse(lines[2]).GetProperty("characters")[0];
        AssertExactProperties(character, "id", "name", "secondsGreyedOut");
    }

    private static void AssertExactProperties(string line, params string[] expected) =>
        AssertExactProperties(Parse(line), expected);

    private static void AssertExactProperties(JsonElement element, params string[] expected)
    {
        string[] actual = element.EnumerateObject()
            .Select(static property => property.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        string[] sortedExpected = expected
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(sortedExpected, actual);
    }

    [Fact]
    public void MissingParentDirectoryIsCreatedAndEventsFlow()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"acdream-status-root-{Guid.NewGuid():N}");
        string path = Path.Combine(root, "nested", "sessions", "s1", "status.jsonl");
        try
        {
            Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
            var writer = new SessionStatusWriter(path);

            writer.Started("s1");
            writer.Connected("s1");

            Assert.True(writer.IsEnabled);
            string[] lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.Contains("\"started\"", lines[0]);
            Assert.Contains("\"connected\"", lines[1]);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ParentSegmentIsAFileLatchesTheWriterInsteadOfThrowing()
    {
        string blocker = Path.Combine(
            Path.GetTempPath(),
            $"acdream-status-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "not a directory");
        string path = Path.Combine(blocker, "status.jsonl");
        try
        {
            var writer = new SessionStatusWriter(path);
            Assert.True(writer.IsEnabled);

            writer.Started("s1");
            Assert.False(writer.IsEnabled);

            writer.Connected("s1");
            writer.Exited("s1", 0, "disposed");
        }
        finally
        {
            if (File.Exists(blocker))
                File.Delete(blocker);
        }
    }

    [Fact]
    public void FileIsOpenedShareReadSoAConcurrentTailerCanReadWhileAppending()
    {
        using TemporaryFile file = TemporaryFile.Create();
        var writer = new SessionStatusWriter(file.Path);
        writer.Started("s1");

        using FileStream tailer = new(
            file.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        using var tailerReader = new StreamReader(tailer);
        string? firstLine = tailerReader.ReadLine();
        Assert.NotNull(firstLine);
        Assert.Contains("\"started\"", firstLine);

        // The writer keeps working while the tailer's handle is still open.
        writer.Connected("s1");
        string? secondLine = tailerReader.ReadLine();
        Assert.NotNull(secondLine);
        Assert.Contains("\"connected\"", secondLine);
    }

    private static JsonElement Parse(string line) =>
        JsonDocument.Parse(line).RootElement;

    private sealed class TemporaryFile : IDisposable
    {
        private TemporaryFile(string path) => Path = path;

        internal string Path { get; }

        internal static TemporaryFile Create()
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"acdream-status-{Guid.NewGuid():N}.jsonl");
            return new TemporaryFile(path);
        }

        internal static TemporaryFile Reserve() => Create();

        public void Dispose()
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
    }
}
