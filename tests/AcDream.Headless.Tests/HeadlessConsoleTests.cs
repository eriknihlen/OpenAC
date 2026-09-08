using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Text;
using AcDream.Core.Chat;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Headless.Configuration;
using AcDream.Headless.Credentials;
using AcDream.Headless.Diagnostics;
using AcDream.Headless.Hosting;
using AcDream.Headless.Platform;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Chat;
using AcDream.Runtime.Session;

namespace AcDream.Headless.Tests;

public sealed class HeadlessConsoleTests
{
    // ── HeadlessConsoleOptions (typed option resolution) ─────────────────

    [Theory]
    [InlineData(true, "0", false, true)]    // CLI flag always wins, even over env "0"
    [InlineData(false, "1", false, true)]   // env var "1" wins over terminal default
    [InlineData(false, "yes", false, true)]
    [InlineData(false, "0", true, false)]   // env var "0" disables even when stdin is a terminal
    [InlineData(false, "0", false, false)]  // env var "0" disables when stdin is redirected too
    [InlineData(false, null, true, true)]   // no flag/env -> terminal-shaped default (on)
    [InlineData(false, null, false, false)] // no flag/env -> terminal-shaped default (off)
    public void ResolvePrefersFlagThenEnvironmentThenTerminalDefault(
        bool commandLineFlag,
        string? environmentValue,
        bool standardInputIsTerminal,
        bool expected)
    {
        bool resolved = HeadlessConsoleOptions.Resolve(
            commandLineFlag,
            _ => environmentValue,
            standardInputIsTerminal);

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void CommandLineParsesTheBareConsoleFlag()
    {
        HeadlessCommandLine parsed = HeadlessCommandLine.Parse(
            ["run", "--config", "bot.json", "--console"]);

        Assert.True(parsed.ConsoleEnabled);
        Assert.Equal("bot.json", parsed.ConfigurationPath);
    }

    [Fact]
    public void CommandLineWithoutTheFlagDefaultsConsoleOff()
    {
        HeadlessCommandLine parsed = HeadlessCommandLine.Parse(
            ["run", "--config", "bot.json"]);

        Assert.False(parsed.ConsoleEnabled);
    }

    [Fact]
    public void ValidateModeRejectsTheConsoleFlag()
    {
        Assert.Throws<HeadlessCommandLineException>(() =>
            HeadlessCommandLine.Parse(
                ["validate", "--config", "bot.json", "--console"]));
    }

    // ── HeadlessConsoleInputReader: reader-thread/ordering ───────────────

    [Fact]
    public void LinesQueuedByTheReaderThreadDrainInOrderOnTheCallingThread()
    {
        using var input = new System.IO.StringReader(
            "one" + Environment.NewLine
            + "two" + Environment.NewLine
            + "three" + Environment.NewLine);
        using var reader = new HeadlessConsoleInputReader(input);

        Assert.True(
            reader.EndOfInput.Wait(TimeSpan.FromSeconds(5)),
            "the reader thread never reached EOF");

        int callingThread = Environment.CurrentManagedThreadId;
        var drained = new List<string>();
        while (reader.TryDequeue(out string line))
        {
            drained.Add(line);
            Assert.Equal(callingThread, Environment.CurrentManagedThreadId);
        }

        Assert.Equal(["one", "two", "three"], drained);
    }

    [Fact]
    public void SubmitRunsOnTheDrainCallersThreadNeverTheReaderThread()
    {
        using var fixture = new ThreadIdRecordingTextReader(
            new System.IO.StringReader("hello" + Environment.NewLine));
        int? observedSubmitThreadId = null;
        using var quit = new CancellationTokenSource();
        using var controller = new HeadlessConsoleController(
            fixture,
            TextWriter.Null,
            line =>
            {
                observedSubmitThreadId = Environment.CurrentManagedThreadId;
                return SubmitOutcome.Sent;
            },
            () => string.Empty,
            quit);

        Assert.True(WaitForEndOfInput(controller));
        int drainCallerThreadId = Environment.CurrentManagedThreadId;
        controller.DrainDue();

        Assert.NotNull(fixture.ReadLineThreadId);
        Assert.NotNull(observedSubmitThreadId);
        Assert.NotEqual(fixture.ReadLineThreadId, observedSubmitThreadId);
        Assert.Equal(drainCallerThreadId, observedSubmitThreadId);
    }

    // ── HeadlessConsoleController: /quit, /status, dispatch ordering ─────

    [Fact]
    public void ControllerDrainsEveryLineQueuedSinceTheLastTickInOrderOnOneCall()
    {
        using var input = new System.IO.StringReader(
            "alpha" + Environment.NewLine
            + "beta" + Environment.NewLine
            + "gamma" + Environment.NewLine);
        var handled = new List<string>();
        using var quit = new CancellationTokenSource();
        using var controller = new HeadlessConsoleController(
            input,
            TextWriter.Null,
            line =>
            {
                handled.Add(line);
                return SubmitOutcome.Sent;
            },
            () => string.Empty,
            quit);

        Assert.True(
            WaitForEndOfInput(controller),
            "the reader thread never reached EOF");
        controller.DrainDue();

        Assert.Equal(["alpha", "beta", "gamma"], handled);
        Assert.Equal(3, controller.LastDrainCount);

        // A second drain with nothing queued does nothing — proves DrainDue
        // does not re-process already-handled lines.
        controller.DrainDue();
        Assert.Equal(["alpha", "beta", "gamma"], handled);
        Assert.Equal(0, controller.LastDrainCount);
    }

    [Fact]
    public void QuitRequestsCancellationAndNeverReachesSubmit()
    {
        using var input = new System.IO.StringReader("/quit" + Environment.NewLine);
        var submitted = new List<string>();
        var output = new StringWriter();
        using var quit = new CancellationTokenSource();
        using var controller = new HeadlessConsoleController(
            input,
            output,
            line =>
            {
                submitted.Add(line);
                return SubmitOutcome.Sent;
            },
            () => string.Empty,
            quit);

        Assert.True(WaitForEndOfInput(controller));
        controller.DrainDue();

        Assert.True(quit.IsCancellationRequested);
        Assert.Empty(submitted);
        Assert.Contains("quitting", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StatusPrintsTheProvidedStatusTextAndNeverReachesSubmit()
    {
        using var input = new System.IO.StringReader("/status" + Environment.NewLine);
        var submitted = new List<string>();
        var output = new StringWriter();
        using var quit = new CancellationTokenSource();
        using var controller = new HeadlessConsoleController(
            input,
            output,
            line =>
            {
                submitted.Add(line);
                return SubmitOutcome.Sent;
            },
            () => "generation=1 position=unknown plugins=0 loaded",
            quit);

        Assert.True(WaitForEndOfInput(controller));
        controller.DrainDue();

        Assert.Empty(submitted);
        Assert.Contains(
            "generation=1 position=unknown plugins=0 loaded",
            output.ToString());
    }

    [Theory]
    [InlineData(SubmitOutcome.UnknownCommand)]
    [InlineData(SubmitOutcome.Dropped)]
    public void UnknownOrDroppedOutcomePrintsAVisibleLine(SubmitOutcome outcome)
    {
        using var input = new System.IO.StringReader("garbage" + Environment.NewLine);
        var output = new StringWriter();
        using var quit = new CancellationTokenSource();
        using var controller = new HeadlessConsoleController(
            input,
            output,
            _ => outcome,
            () => string.Empty,
            quit);

        Assert.True(WaitForEndOfInput(controller));
        controller.DrainDue();

        Assert.Contains("garbage", output.ToString());
        Assert.Contains(outcome.ToString(), output.ToString());
    }

    [Fact]
    public void SubmitFailurePrintsALineAndNeverEscapesDrainDue()
    {
        using var input = new System.IO.StringReader("boom" + Environment.NewLine);
        var output = new StringWriter();
        using var quit = new CancellationTokenSource();
        using var controller = new HeadlessConsoleController(
            input,
            output,
            _ => throw new InvalidOperationException("fixture failure"),
            () => string.Empty,
            quit);

        Assert.True(WaitForEndOfInput(controller));
        controller.DrainDue();

        Assert.Contains("fixture failure", output.ToString());
    }


    [Theory]
    [InlineData("Bob", 0x50000010u, "hi", "[Local] Bob: hi")]
    [InlineData("", 0u, "hi", "[Local] You: hi")]
    public void FormatsLocalSpeechWithTheLocalLabel(
        string sender, uint senderGuid, string text, string expected)
    {
        var entry = new RuntimeChatEntry(
            Revision: 1,
            SenderGuid: senderGuid,
            Kind: (int)ChatKind.LocalSpeech,
            Sender: sender,
            Text: text,
            ChannelName: string.Empty);

        Assert.Equal(expected, HeadlessConsoleChatFormatter.Format(entry));
    }

    [Fact]
    public void FormatsChannelBroadcastWithItsFriendlyName()
    {
        var entry = new RuntimeChatEntry(
            Revision: 1,
            SenderGuid: 0x50000010u,
            Kind: (int)ChatKind.Channel,
            Sender: "Bob",
            Text: "group up",
            ChannelName: "Fellowship");

        Assert.Equal(
            "[Fellowship] Bob: group up",
            HeadlessConsoleChatFormatter.Format(entry));
    }

    [Theory]
    [InlineData(0x50000010u, "Bob", "hi", "[Tell] Bob: hi")]
    [InlineData(0u, "Bob", "hi", "[Tell] You -> Bob: hi")]
    public void FormatsTellWithDirection(
        uint senderGuid, string sender, string text, string expected)
    {
        var entry = new RuntimeChatEntry(
            Revision: 1,
            SenderGuid: senderGuid,
            Kind: (int)ChatKind.Tell,
            Sender: sender,
            Text: text,
            ChannelName: string.Empty);

        Assert.Equal(expected, HeadlessConsoleChatFormatter.Format(entry));
    }

    // ── HeadlessConsoleRenderer: N5 dim-weight rules ─────────────────────

    [Fact]
    public void ChatAndInterfaceTextPrintAtDefaultWeightNeverDimmed()
    {
        var output = new StringWriter();
        var renderer = new HeadlessConsoleRenderer(output, useColor: true);
        var entry = new RuntimeChatEntry(
            Revision: 1,
            SenderGuid: 0x50000010u,
            Kind: (int)ChatKind.LocalSpeech,
            Sender: "Bob",
            Text: "hi",
            ChannelName: string.Empty);

        renderer.OnChat(new RuntimeChatDelta(default, entry));
        renderer.WriteInterfaceText("Unknown command: /x");

        string text = output.ToString();
        Assert.DoesNotContain("[2m", text);
        Assert.Contains("[Local] Bob: hi", text);
        Assert.Contains("Unknown command: /x", text);
    }

    [Fact]
    public void LifecycleCommandAndPortalLinesAreDimmedWhenColorIsEnabled()
    {
        var output = new StringWriter();
        var renderer = new HeadlessConsoleRenderer(output, useColor: true);

        renderer.OnLifecycle(new RuntimeLifecycleDelta(
            default, RuntimeLifecycleState.Starting, RuntimeLifecycleState.InWorld));
        renderer.OnCommand(new RuntimeCommandDelta(
            default, RuntimeCommandDomain.Chat, 0, RuntimeCommandStatus.Rejected, Text: "boom"));
        renderer.OnPortal(new RuntimePortalDelta(
            default,
            new RuntimePortalSnapshot(
                Generation: 1,
                RuntimePortalKind.Portal,
                Readiness: new RuntimeDestinationReadiness(
                    1, 0x12345678u, false, false, 0, true, true, true),
                Materialized: true,
                Completed: false,
                Cancelled: false,
                WorldViewportObserved: true,
                WorldSimulationAvailable: true,
                InvariantFailureCount: 0,
                WaitCueShown: false,
                PortalMaterializationCount: 1)));

        string[] lines = output.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.All(lines, line => Assert.Contains("[2m", line));
    }

    // ── HeadlessSessionHost.SubmitConsoleLine: the real dispatch pipeline ─

    [Fact]
    public void SlashSayProducesTheSameOutboundTalkActionTheGraphicalRouteSends()
    {
        var captured = new List<byte[]>();
        var operations = new FixtureSessionOperations
        {
            GameActionCapture = body => captured.Add(body),
        };
        using var credential = new HeadlessCredentialSecret("fixture", "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);

        SubmitOutcome outcome = host.SubmitConsoleLine("/say hello");

        Assert.Equal(SubmitOutcome.Sent, outcome);
        byte[] body = Assert.Single(captured);
        Assert.Equal(ChatRequests.TalkOpcode, ActionOpcode(body));
        Assert.Equal("hello", TalkText(body));
    }

    [Fact]
    public void PlainTextProducesTheSameOutboundTalkActionAsSlashSay()
    {
        var captured = new List<byte[]>();
        var operations = new FixtureSessionOperations
        {
            GameActionCapture = body => captured.Add(body),
        };
        using var credential = new HeadlessCredentialSecret("fixture", "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);

        SubmitOutcome outcome = host.SubmitConsoleLine("hello");

        Assert.Equal(SubmitOutcome.Sent, outcome);
        byte[] body = Assert.Single(captured);
        Assert.Equal(ChatRequests.TalkOpcode, ActionOpcode(body));
        Assert.Equal("hello", TalkText(body));
    }

    [Fact]
    public void PluginVerbReachesTheRegisteredPluginCommandWithoutTouchingTheWire()
    {
        var captured = new List<byte[]>();
        var operations = new FixtureSessionOperations
        {
            GameActionCapture = body => captured.Add(body),
        };
        using var credential = new HeadlessCredentialSecret("fixture", "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);
        var received = new List<PluginCommand>();
        using IDisposable registration = host.PluginCommands.Register(
            "vt",
            command => received.Add(command));

        SubmitOutcome outcome = host.SubmitConsoleLine("/vt start");

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        PluginCommand command = Assert.Single(received);
        Assert.Equal("vt", command.Verb);
        Assert.Equal("start", command.Arguments);
        Assert.Empty(captured);
    }

    [Fact]
    public void UnknownVerbProducesTheSameChatLineTheChatBoxShows()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret("fixture", "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);

        SubmitOutcome outcome = host.SubmitConsoleLine("/");

        Assert.Equal(SubmitOutcome.UnknownCommand, outcome);
        var chatEntry = Assert.Single(host.Runtime.CommunicationOwner.Chat.Snapshot());
        Assert.Contains("Unknown command:", chatEntry.Text);
        SpewBoxState spewBox = host.Runtime.CommunicationOwner.SpewBox;
        spewBox.Tick(host.Runtime.Clock.SimulationTimeSeconds);
        Assert.Empty(spewBox.Snapshot());
    }

    // ── HeadlessConsoleSpewBoxPump: server/plugin-driven interface text ──

    [Fact]
    public void PumpPrintsInterfaceTextNotOriginatingFromTheConsole()
    {
        var spewBox = new SpewBoxState();
        var printed = new List<string>();
        double now = 0d;
        var pump = new HeadlessConsoleSpewBoxPump(spewBox, () => now, printed.Add);

        spewBox.Enqueue("[vt] navigation route loaded");

        pump.Pump();

        Assert.Equal(["[vt] navigation route loaded"], printed);

        // A second pump with nothing new enqueued must not reprint the
        // still-visible entry.
        now += 0.1d;
        pump.Pump();
        Assert.Equal(["[vt] navigation route loaded"], printed);
    }


    [Fact]
    public async Task ConsoleLineReachesTheSessionAndQuitEndsTheProcessGracefully()
    {
        var captured = new List<byte[]>();
        var operations = new FixtureSessionOperations
        {
            GameActionCapture = body => captured.Add(body),
        };
        var configuration = new HeadlessConfiguration
        {
            Version = 1,
            Sessions = [Descriptor()],
        };
        using var diagnostics = new StringWriter();
        using var input = new System.IO.StringReader(
            "hello" + Environment.NewLine + "/quit" + Environment.NewLine);
        using var host = new HeadlessProcessHost(
            configuration,
            HeadlessPathSet.Resolve(new HeadlessPathOverrides()),
            input,
            diagnostics,
            operations,
            new FakeTimeProvider(),
            directCredentials: new HeadlessDirectCredentials("account", "password"),
            consoleEnabled: true);

        HeadlessExitCode exitCode = await host.RunAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(HeadlessExitCode.Success, exitCode);
        byte[] body = Assert.Single(captured);
        Assert.Equal(ChatRequests.TalkOpcode, ActionOpcode(body));
        Assert.Equal("hello", TalkText(body));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task StandardOutputIsTerminalParameterControlsColorNotTheRealConsole(
        bool standardOutputIsTerminal, bool expectDimmed)
    {
        var operations = new FixtureSessionOperations();
        var configuration = new HeadlessConfiguration
        {
            Version = 1,
            Sessions = [Descriptor()],
        };
        using var diagnostics = new StringWriter();
        using var input = new System.IO.StringReader("/quit" + Environment.NewLine);
        using var host = new HeadlessProcessHost(
            configuration,
            HeadlessPathSet.Resolve(new HeadlessPathOverrides()),
            input,
            diagnostics,
            operations,
            new FakeTimeProvider(),
            directCredentials: new HeadlessDirectCredentials("account", "password"),
            consoleEnabled: true,
            standardOutputIsTerminal: standardOutputIsTerminal);

        await host.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        string text = diagnostics.ToString();
        Assert.Contains("entered world", text);
        Assert.Equal(expectDimmed, text.Contains("[2m"));
    }

    [Fact]
    public void TwoSessionsWithConsoleFlagReportsSingleSessionOnly()
    {
        HeadlessSessionDescriptor StandardInputDescriptor(string id) =>
            Descriptor() with
            {
                Id = id,
                Credential = new HeadlessCredentialReference
                {
                    Provider = HeadlessCredentialProviderKind.StandardInput,
                    Reference = "fixture",
                },
            };
        var configuration = new HeadlessConfiguration
        {
            Version = 1,
            Sessions =
            [
                StandardInputDescriptor("one"),
                StandardInputDescriptor("two"),
            ],
        };
        var operations = new FixtureSessionOperations();
        using var diagnostics = new StringWriter();
        using var input = new System.IO.StringReader(
            "password-one" + Environment.NewLine
            + "password-two" + Environment.NewLine);
        using var host = new HeadlessProcessHost(
            configuration,
            HeadlessPathSet.Resolve(new HeadlessPathOverrides()),
            input,
            diagnostics,
            operations,
            new FakeTimeProvider(),
            directCredentials: null,
            consoleEnabled: true);

        Assert.Contains("single-session only", diagnostics.ToString());
    }

    private static bool WaitForEndOfInput(HeadlessConsoleController controller) =>
        controller.Reader.EndOfInput.Wait(TimeSpan.FromSeconds(5));

    private static uint ActionOpcode(byte[] body) =>
        BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8, sizeof(uint)));

    private static string TalkText(byte[] body)
    {
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(
            body.AsSpan(12, sizeof(ushort)));
        return Encoding.ASCII.GetString(body, 14, length);
    }

    private static HeadlessSessionDescriptor Descriptor() => new()
    {
        Id = "console-bot",
        Endpoint = new HeadlessEndpointDescriptor
        {
            Host = "127.0.0.1",
            Port = 9000,
        },
        Account = "account",
        Character = new HeadlessCharacterSelector
        {
            Name = "headless",
        },
        Policy = new HeadlessBotPolicyDescriptor
        {
            Id = "idle",
        },
        Credential = new HeadlessCredentialReference
        {
            Provider = HeadlessCredentialProviderKind.Environment,
            Reference = "CONSOLE_BOT_PASSWORD",
        },
    };

    private sealed class FixtureSessionOperations : ILiveSessionOperations
    {
        public Action<byte[]>? GameActionCapture { get; init; }

        public CharacterList.Parsed? Characters { get; init; } = new(
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

        public IPEndPoint ResolveEndpoint(string host, int port) =>
            new(IPAddress.Loopback, port);

        public WorldSession CreateSession(IPEndPoint endpoint)
        {
            var session = new WorldSession(endpoint);
            session.GameActionCapture = GameActionCapture;
            return session;
        }

        public void Connect(WorldSession session, string user, string password)
        {
        }

        public CharacterList.Parsed? GetCharacters(WorldSession session) =>
            Characters;

        public void EnterWorld(WorldSession session, int activeCharacterIndex)
        {
        }

        public void Tick(WorldSession session)
        {
        }

        public void DisposeSession(WorldSession session) => session.Dispose();
    }

    private sealed class ThreadIdRecordingTextReader(TextReader inner)
        : TextReader
    {
        internal int? ReadLineThreadId { get; private set; }

        public override string? ReadLine()
        {
            ReadLineThreadId = Environment.CurrentManagedThreadId;
            return inner.ReadLine();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        public override long GetTimestamp() => Stopwatch.GetTimestamp();

        public override long TimestampFrequency => Stopwatch.Frequency;
    }
}
