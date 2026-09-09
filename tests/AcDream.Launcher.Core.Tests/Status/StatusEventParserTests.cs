using AcDream.Launcher.Core.Status;

namespace AcDream.Launcher.Core.Tests.Status;

public sealed class StatusEventParserTests
{
    [Fact]
    public void ParsesStarted()
    {
        var e = StatusEventParser.Parse(
            """{"v":1,"e":"started","t":"2026-08-14T12:00:00Z","sessionId":"s1"}""");

        var started = Assert.IsType<StartedStatusEvent>(e);
        Assert.Equal(1, started.V);
        Assert.Equal("started", started.E);
        Assert.Equal("s1", started.SessionId);
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-14T12:00:00Z"),
            started.T);
    }

    [Fact]
    public void ParsesConnected()
    {
        var e = StatusEventParser.Parse(
            """{"v":1,"e":"connected","t":"2026-08-14T12:00:01Z","sessionId":"s1"}""");
        Assert.IsType<ConnectedStatusEvent>(e);
    }

    [Fact]
    public void ParsesCharacterListWithMultipleCharacters()
    {
        var e = StatusEventParser.Parse(
            """
            {"v":1,"e":"characterList","t":"2026-08-14T12:00:02Z","sessionId":"s1",
             "accountName":"testaccount","slotCount":6,
             "characters":[
               {"id":1342177290,"name":"+Acdream","secondsGreyedOut":0},
               {"id":1342177291,"name":"+Second","secondsGreyedOut":1}
             ]}
            """);

        var list = Assert.IsType<CharacterListStatusEvent>(e);
        Assert.Equal("testaccount", list.AccountName);
        Assert.Equal(6, list.SlotCount);
        Assert.Equal(2, list.Characters.Count);
        Assert.Equal(1342177290u, list.Characters[0].Id);
        Assert.Equal("+Acdream", list.Characters[0].Name);
        Assert.Equal(0u, list.Characters[0].SecondsGreyedOut);
        Assert.Equal(1342177291u, list.Characters[1].Id);
        Assert.Equal(1u, list.Characters[1].SecondsGreyedOut);
    }

    [Fact]
    public void ParsesEnteredWorld()
    {
        var e = StatusEventParser.Parse(
            """{"v":1,"e":"enteredWorld","t":"2026-08-14T12:00:03Z","sessionId":"s1","characterId":1342177290,"characterName":"+Acdream"}""");

        var entered = Assert.IsType<EnteredWorldStatusEvent>(e);
        Assert.Equal(1342177290u, entered.CharacterId);
        Assert.Equal("+Acdream", entered.CharacterName);
    }

    [Fact]
    public void ParsesPluginLoadedAndPluginFailed()
    {
        var loaded = Assert.IsType<PluginLoadedStatusEvent>(
            StatusEventParser.Parse(
                """{"v":1,"e":"pluginLoaded","t":"2026-08-14T12:00:04Z","sessionId":"s1","plugin":"ExamplePlugin"}"""));
        Assert.Equal("ExamplePlugin", loaded.Plugin);

        var failed = Assert.IsType<PluginFailedStatusEvent>(
            StatusEventParser.Parse(
                """{"v":1,"e":"pluginFailed","t":"2026-08-14T12:00:05Z","sessionId":"s1","plugin":"BadPlugin","error":"boom"}"""));
        Assert.Equal("BadPlugin", failed.Plugin);
        Assert.Equal("boom", failed.Error);
    }

    [Fact]
    public void ParsesCharacterCreatedAndCreationFailed()
    {
        var created = Assert.IsType<CharacterCreatedStatusEvent>(
            StatusEventParser.Parse(
                """{"v":1,"e":"characterCreated","t":"2026-08-15T12:00:00Z","sessionId":"s1","guid":1342177296,"name":"NewChar"}"""));
        Assert.Equal(1342177296u, created.Guid);
        Assert.Equal("NewChar", created.Name);

        var failed = Assert.IsType<CreationFailedStatusEvent>(
            StatusEventParser.Parse(
                """{"v":1,"e":"creationFailed","t":"2026-08-15T12:00:01Z","sessionId":"s1","code":3,"reason":"NameInUse","name":"Bob"}"""));
        Assert.Equal(3u, failed.Code);
        Assert.Equal("NameInUse", failed.Reason);
        Assert.Equal("Bob", failed.Name);
    }

    [Theory]
    [InlineData("{\"v\":1,\"e\":\"characterCreated\",\"t\":\"2026-08-15T12:00:00Z\",\"sessionId\":\"s1\",\"name\":\"NewChar\"}")]
    [InlineData("{\"v\":1,\"e\":\"characterCreated\",\"t\":\"2026-08-15T12:00:00Z\",\"sessionId\":\"s1\",\"guid\":1342177296}")]
    [InlineData("{\"v\":1,\"e\":\"creationFailed\",\"t\":\"2026-08-15T12:00:00Z\",\"sessionId\":\"s1\",\"reason\":\"NameInUse\",\"name\":\"Bob\"}")]
    [InlineData("{\"v\":1,\"e\":\"creationFailed\",\"t\":\"2026-08-15T12:00:00Z\",\"sessionId\":\"s1\",\"code\":3,\"name\":\"Bob\"}")]
    [InlineData("{\"v\":1,\"e\":\"creationFailed\",\"t\":\"2026-08-15T12:00:00Z\",\"sessionId\":\"s1\",\"code\":3,\"reason\":\"NameInUse\"}")]
    public void MalformedCharacterCreationEventsUseTheKnownEventFailurePath(string line)
    {
        var malformed = Assert.IsType<MalformedStatusEvent>(StatusEventParser.Parse(line));

        Assert.Equal("s1", malformed.SessionId);
        Assert.False(string.IsNullOrWhiteSpace(malformed.Error));
    }

    [Fact]
    public void ParsesLoginCommandFailed()
    {
        var failed = Assert.IsType<LoginCommandFailedStatusEvent>(
            StatusEventParser.Parse(
                """{"v":1,"e":"loginCommandFailed","t":"2026-08-14T12:00:05Z","sessionId":"s1","commandIndex":2,"command":"/version","error":"unsupported headless command"}"""));

        Assert.Equal(2, failed.CommandIndex);
        Assert.Equal("/version", failed.Command);
        Assert.Equal("unsupported headless command", failed.Error);
    }

    [Theory]
    [InlineData("{\"v\":1,\"e\":\"loginCommandFailed\",\"t\":\"2026-08-14T12:00:05Z\",\"sessionId\":\"s1\",\"commandIndex\":-1,\"command\":\"/version\",\"error\":\"unsupported\"}")]
    [InlineData("{\"v\":1,\"e\":\"loginCommandFailed\",\"t\":\"2026-08-14T12:00:05Z\",\"sessionId\":\"s1\",\"commandIndex\":0,\"error\":\"unsupported\"}")]
    [InlineData("{\"v\":1,\"e\":\"loginCommandFailed\",\"t\":\"2026-08-14T12:00:05Z\",\"sessionId\":\"s1\",\"commandIndex\":0,\"command\":\"/version\"}")]
    public void MalformedLoginCommandFailureUsesTheKnownEventFailurePath(string line)
    {
        var malformed = Assert.IsType<MalformedStatusEvent>(
            StatusEventParser.Parse(line));

        Assert.Equal("loginCommandFailed", malformed.E);
        Assert.Equal("s1", malformed.SessionId);
        Assert.False(string.IsNullOrWhiteSpace(malformed.Error));
    }

    [Fact]
    public void ParsesDisconnectedAndExited()
    {
        var disconnected = Assert.IsType<DisconnectedStatusEvent>(
            StatusEventParser.Parse(
                """{"v":1,"e":"disconnected","t":"2026-08-14T12:00:06Z","sessionId":"s1","reason":"serverClosed"}"""));
        Assert.Equal("serverClosed", disconnected.Reason);

        var exited = Assert.IsType<ExitedStatusEvent>(
            StatusEventParser.Parse(
                """{"v":1,"e":"exited","t":"2026-08-14T12:00:07Z","sessionId":"s1","code":0,"reason":"graceful"}"""));
        Assert.Equal(0, exited.Code);
        Assert.Equal("graceful", exited.Reason);
    }

    [Fact]
    public void UnknownEValueSurfacesAsUnknownEventRatherThanThrowing()
    {
        var e = StatusEventParser.Parse(
            """{"v":1,"e":"someFutureEvent","t":"2026-08-14T12:00:08Z","sessionId":"s1","extra":true}""");

        var unknown = Assert.IsType<UnknownStatusEvent>(e);
        Assert.Equal("someFutureEvent", unknown.E);
        Assert.Equal("s1", unknown.SessionId);
        Assert.Contains("someFutureEvent", unknown.RawJson);
    }

    [Fact]
    public void MalformedJsonSurfacesAsUnknownEventRatherThanThrowing()
    {
        var e = StatusEventParser.Parse("{not json");

        Assert.IsType<UnknownStatusEvent>(e);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    public void CompleteJsonWithANonObjectRootSurfacesAsMalformedEvent(string line)
    {
        var e = StatusEventParser.Parse(line);

        var malformed = Assert.IsType<MalformedStatusEvent>(e);
        Assert.Contains("root", malformed.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void WhitespaceOrEmptyLineSurfacesAsUnknownEventRatherThanThrowing(string line)
    {
        var e = StatusEventParser.Parse(line);

        Assert.IsType<UnknownStatusEvent>(e);
    }

    [Fact]
    public void NullLineSurfacesAsUnknownEventRatherThanThrowing()
    {
        var e = StatusEventParser.Parse(null!);

        Assert.IsType<UnknownStatusEvent>(e);
    }

    [Theory]
    [InlineData("{\"e\":\"started\",\"t\":\"2026-08-14T12:00:00Z\",\"sessionId\":\"s1\"}")]
    [InlineData("{\"v\":\"1\",\"e\":\"connected\",\"t\":\"2026-08-14T12:00:00Z\",\"sessionId\":\"s1\"}")]
    [InlineData("{\"v\":2,\"e\":\"started\",\"t\":\"2026-08-14T12:00:00Z\",\"sessionId\":\"s1\"}")]
    [InlineData("{\"v\":1,\"e\":\"connected\",\"sessionId\":\"s1\"}")]
    [InlineData("{\"v\":1,\"e\":\"started\",\"t\":42,\"sessionId\":\"s1\"}")]
    [InlineData("{\"v\":1,\"e\":\"connected\",\"t\":\"not-a-time\",\"sessionId\":\"s1\"}")]
    [InlineData("{\"v\":1,\"e\":\"started\",\"t\":\"2026-08-14T12:00:00+02:00\",\"sessionId\":\"s1\"}")]
    [InlineData("{\"v\":1,\"e\":\"connected\",\"t\":\"2026-08-14T12:00:00Z\"}")]
    [InlineData("{\"v\":1,\"e\":\"started\",\"t\":\"2026-08-14T12:00:00Z\",\"sessionId\":42}")]
    [InlineData("{\"v\":1,\"e\":\"connected\",\"t\":\"2026-08-14T12:00:00Z\",\"sessionId\":\"\"}")]
    public void PayloadFreeKnownEventsRequireTheFullPinnedV1Envelope(string line)
    {
        var e = StatusEventParser.Parse(line);

        var malformed = Assert.IsType<MalformedStatusEvent>(e);
        Assert.False(string.IsNullOrWhiteSpace(malformed.Error));
    }

    [Theory]
    [InlineData("{\"v\":1,\"t\":\"2026-08-14T12:00:00Z\",\"sessionId\":\"s1\"}")]
    [InlineData("{\"v\":1,\"e\":42,\"t\":\"2026-08-14T12:00:00Z\",\"sessionId\":\"s1\"}")]
    public void MissingOrWrongKindEventNameSurfacesAsMalformedEvent(string line)
    {
        Assert.IsType<MalformedStatusEvent>(StatusEventParser.Parse(line));
    }

    [Fact]
    public void KnownEValueWithMissingRequiredFieldSurfacesAsMalformedEventRatherThanThrowing()
    {
        var e = StatusEventParser.Parse(
            """{"v":1,"e":"characterList","t":"2026-08-14T12:00:09Z","sessionId":"s1","accountName":"a","slotCount":6}""");

        var malformed = Assert.IsType<MalformedStatusEvent>(e);
        Assert.Equal("characterList", malformed.E);
        Assert.Equal("s1", malformed.SessionId);
        Assert.False(string.IsNullOrWhiteSpace(malformed.Error));
    }

    [Fact]
    public void KnownEValueWithAFieldOfTheWrongJsonKindSurfacesAsMalformedEvent()
    {
        var e = StatusEventParser.Parse(
            """{"v":1,"e":"characterList","t":"2026-08-14T12:00:10Z","sessionId":"s1","accountName":"a","slotCount":6,"characters":"not-an-array"}""");

        var malformed = Assert.IsType<MalformedStatusEvent>(e);
        Assert.Equal("characterList", malformed.E);
        Assert.False(string.IsNullOrWhiteSpace(malformed.Error));
    }
}
