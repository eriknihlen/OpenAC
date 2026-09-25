using System.Net;
using System.Text.Json;
using AcDream.Core.Chat;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Combat;
using AcDream.Headless.Configuration;
using AcDream.Headless.Credentials;
using AcDream.Headless.Hosting;
using AcDream.Headless.Platform;
using AcDream.Runtime.Chat;
using AcDream.Runtime.Session;

namespace AcDream.Headless.Tests;

/// <summary>
/// The console is the chat box: the same feed, the same words. The chat box's
/// own wording is pinned line for line where the panel reads it, and the panel
/// reads it from the same feed these tests drive, so asserting the console
/// against the feed pins the whole chain.
/// </summary>
public sealed class HeadlessConsoleChatParityTests
{
    private const uint OtherPlayerGuid = 0x5000000Au;

    private static string[] Lines(StringWriter output) =>
        output.ToString().Split(
            Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Every kind of line a session can put in the chat box.</summary>
    private static void ScriptEveryKind(ChatLog log)
    {
        log.OnLocalSpeech(
            "Bob", "hi there", OtherPlayerGuid, isRanged: false,
            logTextType: (uint)RetailLogTextType.Speech);
        log.OnLocalSpeech(
            "", "hi yourself", 0u, isRanged: false,
            logTextType: (uint)RetailLogTextType.SpeechDirectSend);
        log.OnLocalSpeech(
            "Bob", "over here", OtherPlayerGuid, isRanged: true,
            logTextType: (uint)RetailLogTextType.Speech);
        log.OnTellReceived(
            "Bob", "meet me", OtherPlayerGuid, (uint)RetailLogTextType.Tell);
        log.OnSelfSent(
            ChatKind.Tell, "on my way", (uint)RetailLogTextType.Tell, "Bob");
        log.OnChannelBroadcast(
            7u, "Bob", "group up",
            (uint)RetailLogTextType.Fellowship, "Fellowship");
        log.OnEmote("Bob", "waves.", OtherPlayerGuid);
        log.OnSoulEmote("Bob", "bows deeply.", OtherPlayerGuid);
        log.OnSystemMessage(
            "Your Cooking skill is now trained!",
            (uint)RetailLogTextType.Advancement);
        log.OnCombatLine(
            "A Drudge Slinker slashes you for 9 points of damage!",
            (uint)RetailLogTextType.CombatEnemy,
            CombatLineKind.Warning);
        log.OnPopup("You have died.");
    }

    [Fact]
    public void EveryChatLineReachesTheConsoleWithTheSameWordsTheChatBoxShows()
    {
        // Before this, the console reworded every line of its own accord:
        // local speech read "[Local] Bob: hi there" instead of
        // Bob says, "hi there", and a shout was collapsed into local speech.
        var log = new ChatLog();
        var windows = new ChatWindowState();
        using var feed = new RuntimeChatFeed(log, windows);
        var output = new StringWriter();
        using var renderer = new HeadlessConsoleRenderer(
            output, useColor: false, chat: feed);

        ScriptEveryKind(log);

        IReadOnlyList<RuntimeChatLine> shown = feed.SnapshotForWindow(0, limit: 100);
        Assert.Equal(
            shown
                .Select(line => RuntimeChatLineTags.For(line) + line.Text)
                .ToArray(),
            Lines(output));

        // And the words themselves, with the tag taken back off, are the chat
        // box's words unchanged.
        Assert.Equal(
            shown.Select(line => line.Text).ToArray(),
            Lines(output)
                .Zip(shown, (printed, line) =>
                    printed[RuntimeChatLineTags.For(line).Length..])
                .ToArray());
    }

    [Fact]
    public void TheWordsAreTheOnesAPlayerReadsNotAConsoleShorthand()
    {
        var log = new ChatLog();
        using var feed = new RuntimeChatFeed(log, new ChatWindowState());
        var output = new StringWriter();
        using var renderer = new HeadlessConsoleRenderer(
            output, useColor: false, chat: feed);

        ScriptEveryKind(log);

        Assert.Equal(
            [
                "[say] Bob says, \"hi there\"",
                "[say] You say, \"hi yourself\"",
                "[say] Bob says, \"over here\"",
                "[tell] Bob tells you, \"meet me\"",
                "[tell] You tell Bob, \"on my way\"",
                // No "[fellowship] " in front: the line already names its
                // channel, and printing the label twice reads badly.
                "[Fellowship] Bob says, \"group up\"",
                "[emote] Bob waves.",
                "[emote] Bob bows deeply.",
                "[advance] Your Cooking skill is now trained!",
                "[combat] A Drudge Slinker slashes you for 9 points of damage!",
                "[msg] [Popup] You have died.",
            ],
            Lines(output));
    }

    [Fact]
    public void ATextClassTheMainWindowFiltersOutIsShownOnNeitherFrontEnd()
    {
        var log = new ChatLog();
        var windows = new ChatWindowState();
        using var feed = new RuntimeChatFeed(log, windows);
        var output = new StringWriter();
        using var renderer = new HeadlessConsoleRenderer(
            output, useColor: false, chat: feed);

        // Speech only; an advancement line now belongs in no open window.
        windows.SetFilter(
            ChatWindowState.MainWindowId, 1UL << (int)RetailLogTextType.Speech);
        log.OnLocalSpeech(
            "Bob", "hi there", OtherPlayerGuid, false,
            (uint)RetailLogTextType.Speech);
        log.OnSystemMessage(
            "Your Cooking skill is now trained!",
            (uint)RetailLogTextType.Advancement);

        Assert.Equal(
            ["Bob says, \"hi there\""],
            feed.SnapshotForWindow(0, limit: 100)
                .Select(line => line.Text)
                .ToArray());
        Assert.Equal(["[say] Bob says, \"hi there\""], Lines(output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheConsoleShowsTimestampsExactlyWhenTheChatBoxWould(
        bool timestamps)
    {
        var log = new ChatLog { DisplayTimestampsSource = () => timestamps };
        using var feed = new RuntimeChatFeed(log, new ChatWindowState());
        var output = new StringWriter();
        using var renderer = new HeadlessConsoleRenderer(
            output, useColor: false, chat: feed);

        log.OnLocalSpeech(
            "Bob", "hi there", OtherPlayerGuid, false,
            (uint)RetailLogTextType.Speech);

        string prefix = timestamps
            ? ChatLog.FormatTimestampPrefix(log.Snapshot()[0].Received)
            : string.Empty;
        Assert.Equal(
            [$"[say] {prefix}Bob says, \"hi there\""], Lines(output));
        Assert.Equal(prefix + "Bob says, \"hi there\"", feed.Snapshot()[0].Text);
    }

    [Fact]
    public void ClientLocalTextCarriesItsOwnMarker()
    {
        var output = new StringWriter();
        using var renderer = new HeadlessConsoleRenderer(output, useColor: false);

        renderer.WriteInterfaceText("navigation route loaded");

        Assert.Equal(["[client] navigation route loaded"], Lines(output));
    }

    [Fact]
    public void WhatTheConsoleSaysAboutItselfCannotBeReadAsChat()
    {
        var output = new StringWriter();
        using var renderer = new HeadlessConsoleRenderer(output, useColor: false);

        renderer.WriteNotice("entered world");

        Assert.Equal(["-- entered world"], Lines(output));
    }

    // -- The two streams --------------------------------------------------

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("stdout", null, true)]
    [InlineData("stderr", "stdout", false)]
    [InlineData(null, "stdout", true)]
    [InlineData(null, "STDOUT", true)]
    [InlineData(null, "stderr", false)]
    public void TheConsoleStreamPrefersTheFlagThenTheEnvironmentThenStandardError(
        string? commandLineValue,
        string? environmentValue,
        bool expectStandardOutput)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        TextWriter chosen = HeadlessConsoleOptions.SelectWriter(
            HeadlessConsoleOptions.ResolveStream(
                commandLineValue, _ => environmentValue),
            output,
            error);

        Assert.Same(expectStandardOutput ? output : error, chosen);
    }

    [Fact]
    public void AnUnusableConsoleStreamSettingIsReportedNotGuessedAt()
        => Assert.Throws<HeadlessConfigurationException>(() =>
            HeadlessConsoleOptions.ResolveStream(null, _ => "syslog"));

    [Fact]
    public void CommandLineParsesTheConsoleStreamOption()
    {
        HeadlessCommandLine parsed = HeadlessCommandLine.Parse(
            ["run", "--config", "bot.json", "--console", "--console-stream", "stdout"]);

        Assert.True(parsed.ConsoleEnabled);
        Assert.Equal("stdout", parsed.ConsoleStream);
    }

    [Fact]
    public void CommandLineWithoutTheConsoleStreamOptionLeavesItUnset()
        => Assert.Null(
            HeadlessCommandLine.Parse(["run", "--config", "bot.json"]).ConsoleStream);

    [Fact]
    public void CommandLineRejectsAConsoleStreamItCannotWriteTo()
        => Assert.Throws<HeadlessCommandLineException>(() =>
            HeadlessCommandLine.Parse(
                ["run", "--config", "bot.json", "--console-stream", "syslog"]));

    [Fact]
    public void ValidateModeRejectsTheConsoleStreamOption()
        => Assert.Throws<HeadlessCommandLineException>(() =>
            HeadlessCommandLine.Parse(
                ["validate", "--config", "bot.json", "--console-stream", "stdout"]));

    [Fact]
    public async Task TheDiagnosticStreamStaysMachineReadableWithTheConsoleOn()
    {
        // The console's chat and notices go to their own writer, so a harness
        // reading the diagnostic stream sees exactly the JSON lines it saw
        // before the console existed.
        var configuration = new HeadlessConfiguration
        {
            Version = 1,
            Sessions = [Descriptor()],
        };
        using var diagnostics = new StringWriter();
        using var console = new StringWriter();
        using var input = new System.IO.StringReader(
            "/" + Environment.NewLine + "/quit" + Environment.NewLine);
        using var host = new HeadlessProcessHost(
            configuration,
            IsolatedHeadlessPaths.Create(),
            input,
            diagnostics,
            new IdleSessionOperations(),
            timeProvider: null,
            contentFactory: null,
            directCredentials: new HeadlessDirectCredentials("account", "password"),
            consoleEnabled: true,
            standardOutputIsTerminal: false,
            consoleOutput: console);

        HeadlessExitCode exitCode = await host.RunAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(HeadlessExitCode.Success, exitCode);

        string[] diagnosticLines = Lines(diagnostics);
        Assert.NotEmpty(diagnosticLines);
        foreach (string line in diagnosticLines)
        {
            // Parsing is the contract: one JSON document per line, nothing else.
            using JsonDocument document = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        }
        Assert.DoesNotContain("Unknown command:", diagnostics.ToString());
        Assert.DoesNotContain(
            HeadlessConsoleRenderer.NoticePrefix, diagnostics.ToString());

        // And the chat line the typed command produced is on the console.
        Assert.Contains("Unknown command:", console.ToString());
        Assert.Contains("-- quitting", console.ToString());
    }

    private static HeadlessSessionDescriptor Descriptor() => new()
    {
        Id = "console-parity-bot",
        Endpoint = new HeadlessEndpointDescriptor
        {
            Host = "127.0.0.1",
            Port = 9000,
        },
        Account = "account",
        Character = new HeadlessCharacterSelector { Name = "headless" },
        Policy = new HeadlessBotPolicyDescriptor { Id = "idle" },
        Credential = new HeadlessCredentialReference
        {
            Provider = HeadlessCredentialProviderKind.Environment,
            Reference = "CONSOLE_PARITY_BOT_PASSWORD",
        },
    };

    /// <summary>A session that connects, enters the world and sends nothing.</summary>
    private sealed class IdleSessionOperations : ILiveSessionOperations
    {
        public IPEndPoint ResolveEndpoint(string host, int port) =>
            new(IPAddress.Loopback, port);

        public WorldSession CreateSession(IPEndPoint endpoint) =>
            new WorldSession(endpoint).TakingItsSends();

        public void Connect(WorldSession session, string user, string password)
        {
        }

        public CharacterList.Parsed? GetCharacters(WorldSession session) =>
            new(
                0u,
                [
                    new CharacterList.Character(0x50000001u, "Other", 0u),
                    new CharacterList.Character(0x50000002u, "Headless", 0u),
                ],
                [],
                11,
                "account",
                true,
                true);

        public void EnterWorld(WorldSession session, int activeCharacterIndex)
        {
        }

        public void Tick(WorldSession session)
        {
        }

        public void DisposeSession(WorldSession session) => session.Dispose();
    }
}
