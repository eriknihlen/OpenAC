using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Headless.Configuration;
using AcDream.Headless.Credentials;
using AcDream.Headless.Diagnostics;
using AcDream.Headless.Hosting;
using AcDream.Headless.Platform;
using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.Headless.Tests;

public sealed class HeadlessCharacterOptionsSeederWiringTests
{
    [Fact]
    public void DeclaredOptionSendsOnceBothLoginCompleteAndTheRealPlayerDescriptionEventLand()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);

        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);
        Assert.NotNull(host.OptionsSeeder);

        var sent = new List<byte[]>();
        WorldSession session = operations.Sessions[^1];
        session.GameActionCapture = body => sent.Add(body);

        // LoginComplete lands first (a legitimate production ordering —
        // see the type doc); no PlayerDescription has arrived yet, so
        // HasServerSeed is still false and nothing can send.
        host.OptionsSeeder!.NoteLoginCompleteSent();
        Assert.Empty(sent);

        session.GameEvents.Dispatch(
            GameEventEnvelope.TryParse(
                WrapPlayerDescriptionEnvelope(options1: 0u, options2: 0u))!
                .Value);

        byte[] action = Assert.Single(sent);
        Assert.Equal(
            SocialActions.SetSingleCharacterOptionOpcode,
            ActionOpcode(action));
        Assert.Equal(
            (uint)CharacterOptionId.IgnoreAllegianceRequests,
            BinaryPrimitives.ReadUInt32LittleEndian(action.AsSpan(12, 4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(action.AsSpan(16, 4)));
        Assert.True(
            host.Runtime.CharacterOwner.Options.HasServerSeed);
    }

    [Fact]
    public void ReconnectAgainstAServerThatNowAgreesSendsNothing()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret(
            "fixture",
            "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);
        WorldSession firstSession = operations.Sessions[^1];
        var firstSent = new List<byte[]>();
        firstSession.GameActionCapture = body => firstSent.Add(body);
        host.OptionsSeeder!.NoteLoginCompleteSent();
        firstSession.GameEvents.Dispatch(
            GameEventEnvelope.TryParse(
                WrapPlayerDescriptionEnvelope(options1: 0u, options2: 0u))!
                .Value);
        Assert.Single(firstSent);

        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Reconnect().Status);
        Assert.NotSame(firstSession, operations.Sessions[^1]);
        WorldSession secondSession = operations.Sessions[^1];
        var secondSent = new List<byte[]>();
        secondSession.GameActionCapture = body => secondSent.Add(body);

        host.OptionsSeeder!.NoteLoginCompleteSent();
        secondSession.GameEvents.Dispatch(
            GameEventEnvelope.TryParse(
                WrapPlayerDescriptionEnvelope(
                    options1: 0x00000004u,
                    options2: 0u))!
                .Value);

        Assert.Empty(secondSent);
    }

    [Fact]
    public async Task DiffAndSendRunsOnTheSameDedicatedUpdateThreadAsEveryTick()
    {
        var configuration = new HeadlessConfiguration
        {
            Version = 1,
            Sessions = [Descriptor()],
        };
        var operations = new SeedTriggeringSessionOperations();
        using var diagnostics = new StringWriter();
        using var host = new HeadlessProcessHost(
            configuration,
            HeadlessPathSet.Resolve(new HeadlessPathOverrides()),
            new System.IO.StringReader("fixture-password" + Environment.NewLine),
            diagnostics,
            operations);
        operations.Host = host.Sessions[0];
        using var cancellation = new CancellationTokenSource();

        int callerThread = Environment.CurrentManagedThreadId;
        Task<HeadlessExitCode> run = host.RunAsync(cancellation.Token);
        var stopwatch = Stopwatch.StartNew();
        while (operations.SentActions.IsEmpty
            && stopwatch.Elapsed < TimeSpan.FromSeconds(10))
        {
            Thread.Sleep(1);
        }
        cancellation.Cancel();
        HeadlessExitCode result = await run;

        Assert.Equal(HeadlessExitCode.Success, result);
        (byte[] Body, int ThreadId) sent = Assert.Single(operations.SentActions);
        Assert.Equal(
            SocialActions.SetSingleCharacterOptionOpcode,
            ActionOpcode(sent.Body));
        Assert.NotEqual(0, sent.ThreadId);
        Assert.NotEqual(callerThread, sent.ThreadId);
        Assert.Equal(operations.ConnectThreadId, sent.ThreadId);
    }

    private static uint ActionOpcode(byte[] body) =>
        BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8, sizeof(uint)));

    private static byte[] WrapPlayerDescriptionEnvelope(
        uint options1,
        uint options2)
    {
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(
            stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0u);       // property flags
            writer.Write(0x52u);    // player weenie type
            writer.Write(0u);       // vector flags
            writer.Write(0u);       // has health
            writer.Write(0x40u);
            writer.Write(options1);
            writer.Write(0u);
            writer.Write(0u);       // spellbook filters
            writer.Write(options2);
            writer.Write(0u);
            writer.Write(0u);
        }

        byte[] payload = stream.ToArray();
        byte[] body = new byte[GameEventEnvelope.HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameEventEnvelope.Opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  0u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  0u);
        BinaryPrimitives.WriteUInt32LittleEndian(
            body.AsSpan(12), (uint)GameEventType.PlayerDescription);
        Array.Copy(payload, 0, body, GameEventEnvelope.HeaderSize, payload.Length);
        return body;
    }

    private static HeadlessSessionDescriptor Descriptor() => new()
    {
        Id = "bot",
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
            Provider = HeadlessCredentialProviderKind.StandardInput,
            Reference = "fixture-password",
        },
        CharacterOptions = new Dictionary<string, bool>
        {
            ["IgnoreAllegianceRequests"] = true,
        },
    };

    private sealed class FixtureSessionOperations : ILiveSessionOperations
    {
        public List<WorldSession> Sessions { get; } = [];

        public IPEndPoint ResolveEndpoint(string host, int port) =>
            new(IPAddress.Loopback, port);

        public WorldSession CreateSession(IPEndPoint endpoint)
        {
            var session = new WorldSession(endpoint);
            Sessions.Add(session);
            return session;
        }

        public void Connect(WorldSession session, string user, string password)
        {
        }

        public CharacterList.Parsed GetCharacters(WorldSession session) =>
            new(
                0u,
                [new CharacterList.Character(0x50000001u, "Headless", 0u)],
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

    private sealed class SeedTriggeringSessionOperations : ILiveSessionOperations
    {
        private int _connectThreadId;
        private int _dispatched;

        internal HeadlessSessionHost? Host { get; set; }
        internal int ConnectThreadId => Volatile.Read(ref _connectThreadId);
        internal ConcurrentQueue<(byte[] Body, int ThreadId)> SentActions { get; } = new();

        public IPEndPoint ResolveEndpoint(string host, int port) =>
            new(IPAddress.Loopback, port);

        public WorldSession CreateSession(IPEndPoint endpoint)
        {
            var session = new WorldSession(endpoint);
            session.GameActionCapture = body => SentActions.Enqueue(
                (body, Environment.CurrentManagedThreadId));
            return session;
        }

        public void Connect(WorldSession session, string user, string password) =>
            Volatile.Write(ref _connectThreadId, Environment.CurrentManagedThreadId);

        public CharacterList.Parsed GetCharacters(WorldSession session) =>
            new(
                0u,
                [new CharacterList.Character(0x50000001u, "Headless", 0u)],
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
            if (Interlocked.Exchange(ref _dispatched, 1) != 0)
                return;
            Host?.OptionsSeeder?.NoteLoginCompleteSent();
            session.GameEvents.Dispatch(
                GameEventEnvelope.TryParse(
                    WrapPlayerDescriptionEnvelope(options1: 0u, options2: 0u))!
                    .Value);
        }

        public void DisposeSession(WorldSession session) => session.Dispose();
    }
}
