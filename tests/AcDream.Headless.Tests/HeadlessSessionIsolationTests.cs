using System.Net;
using System.Reflection;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Headless.Configuration;
using AcDream.Headless.Credentials;
using AcDream.Headless.Diagnostics;
using AcDream.Headless.Hosting;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;

namespace AcDream.Headless.Tests;

[CollectionDefinition(
    HeadlessEnduranceCollection.Name,
    DisableParallelization = true)]
public sealed class HeadlessEnduranceCollection
{
    public const string Name = "Headless endurance";
}

[Collection(HeadlessEnduranceCollection.Name)]
public sealed class HeadlessSessionIsolationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(30)]
    public void SameServerIdentityStaysIsolatedAcrossActivityPortalAndReconnect(
        int sessionCount)
    {
        const uint sharedPlayerGuid = 0x50000001u;
        var operations = new FixtureOperations(sharedPlayerGuid);
        HeadlessSessionHost[] hosts = Enumerable.Range(0, sessionCount)
            .Select(index => CreateHost(index, operations))
            .ToArray();
        try
        {
            foreach (HeadlessSessionHost host in hosts)
            {
                Assert.Equal(
                    RuntimeSessionStartStatus.Connected,
                    host.Start().Status);
            }

            var firstRecords = new RuntimeEntityRecord[sessionCount];
            for (int index = 0; index < hosts.Length; index++)
            {
                HeadlessSessionHost host = hosts[index];
                WorldSession session =
                    operations.ActiveSession(host.SessionId);
                session.GameActionCapture = _ => { };
                SpawnInto(session, sharedPlayerGuid, index + 1f);
                RuntimeCommandResult selected =
                    host.Commands.Selection.SelectObject(
                        host.Runtime.Generation,
                        sharedPlayerGuid);
                Assert.True(selected.Accepted);
                Assert.True(
                    host.Runtime.EntityObjects.Entities.TryGetActive(
                        sharedPlayerGuid,
                        out RuntimeEntityRecord record));
                firstRecords[index] = record;
                Assert.Equal(
                    index + 1f,
                    record.Snapshot.Position!.Value.PositionX);

                Portal(
                    session,
                    sharedPlayerGuid,
                    index + 20f);
                Assert.True(host.Runtime.Portal.Snapshot.Completed);
                Assert.True(
                    host.Runtime.TransitOwner
                        .CaptureOwnership()
                        .IsSessionIdle);
            }

            Assert.Equal(
                sessionCount,
                firstRecords
                    .Distinct(ReferenceEqualityComparer.Instance)
                    .Count());
            Assert.Equal(
                sessionCount,
                hosts
                    .Select(static host => host.Runtime.Entities)
                    .Distinct(ReferenceEqualityComparer.Instance)
                    .Count());

            for (int index = 0; index < hosts.Length; index++)
            {
                HeadlessSessionHost host = hosts[index];
                ulong priorGeneration = host.Runtime.Generation.Value;
                Assert.Equal(
                    RuntimeSessionStartStatus.Connected,
                    host.Reconnect().Status);
                Assert.True(
                    host.Runtime.Generation.Value > priorGeneration);
                Assert.Equal(0, host.Runtime.Entities.Count);

                WorldSession replacement =
                    operations.ActiveSession(host.SessionId);
                replacement.GameActionCapture = _ => { };
                SpawnInto(
                    replacement,
                    sharedPlayerGuid,
                    index + 101f);
                Assert.True(
                    host.Runtime.EntityObjects.Entities.TryGetActive(
                        sharedPlayerGuid,
                        out RuntimeEntityRecord replacementRecord));
                Assert.NotSame(firstRecords[index], replacementRecord);
                Assert.Equal(
                    index + 101f,
                    replacementRecord.Snapshot.Position!.Value.PositionX);
            }
        }
        finally
        {
            for (int index = hosts.Length - 1; index >= 0; index--)
                hosts[index].Dispose();
        }

        Assert.Equal(sessionCount * 2, operations.CreatedSessionCount);
        Assert.Equal(sessionCount * 2, operations.DisposedSessionCount);
        Assert.All(
            hosts,
            static host =>
                Assert.True(host.Runtime.CaptureOwnership().IsConverged));
    }

    [Fact]
    public void ThirtySessionMixedWorkloadMaintainsIsolationAndConverges()
    {
        const int sessionCount = 30;
        const int turnPeriodMilliseconds = 15;
        const int simulatedSeconds = 2 * 60 * 60;
        const int frameCount =
            simulatedSeconds * 1000 / turnPeriodMilliseconds;
        const long maximumProcessAllocationBytesPerSecond =
            384L * 1024L;
        const uint sharedPlayerGuid = 0x50000001u;
        var time = new ManualTimeProvider();
        var operations = new FixtureOperations(sharedPlayerGuid);
        HeadlessSessionHost[] hosts = Enumerable.Range(0, sessionCount)
            .Select(index => CreateHost(index, operations, time))
            .ToArray();
        var reconnectCounts = new int[sessionCount];
        var deathCounts = new int[sessionCount];
        var random = new Random(0xAC2013);
        try
        {
            foreach (HeadlessSessionHost host in hosts)
            {
                Assert.Equal(
                    RuntimeSessionStartStatus.Connected,
                    host.Start().Status);
                WorldSession session =
                    operations.ActiveSession(host.SessionId);
                session.GameActionCapture = _ => { };
                SpawnInto(session, sharedPlayerGuid, 1f);
                Assert.True(
                    host.Commands.Selection.SelectObject(
                        host.Runtime.Generation,
                        sharedPlayerGuid).Accepted);
                Assert.True(
                    host.Commands.Movement.SetIntent(
                        host.Runtime.Generation,
                        new MovementInput(
                            Forward: true,
                            Run: true)).Accepted);
            }

            var scheduler = new HeadlessProcessScheduler(
                hosts,
                time);
            scheduler.RebaseDeadlinesAfterSessionStart();
            long retainedBefore = GC.GetTotalMemory(
                forceFullCollection: true);
            long allocatedBefore =
                GC.GetAllocatedBytesForCurrentThread();
            for (int frame = 1; frame <= frameCount; frame++)
            {
                if (frame % 211 == 0)
                {
                    int index = random.Next(sessionCount);
                    Portal(
                        operations.ActiveSession(
                            hosts[index].SessionId),
                        sharedPlayerGuid,
                        index + frame / 100f,
                        checked((ushort)(
                            2 + reconnectCounts[index]
                            + (frame / 211))));
                }
                if (frame % 5003 == 0)
                {
                    int index = (frame / 5003) % sessionCount;
                    Death(
                        operations.ActiveSession(
                            hosts[index].SessionId),
                        sharedPlayerGuid,
                        frame);
                    deathCounts[index]++;
                }
                if (frame % 353 == 0)
                {
                    int index = random.Next(sessionCount);
                    HeadlessSessionHost host = hosts[index];
                    Assert.True(
                        host.Commands.Chat.Execute(
                            host.Runtime.Generation,
                            new RuntimeChatCommand(
                                RuntimeChatChannel.Say,
                                $"stress-{frame}-{index}")).Accepted);
                    _ = host.Commands.Selection.Execute(
                        host.Runtime.Generation,
                        RuntimeSelectionCommand.UseSelected);
                    _ = host.Commands.Combat.Execute(
                        host.Runtime.Generation,
                        RuntimeCombatCommand.ToggleMode);
                    _ = host.Commands.Magic.Execute(
                        host.Runtime.Generation,
                        new RuntimeMagicCommand(1u));
                }
                if (frame % 997 == 0)
                {
                    int index = random.Next(sessionCount);
                    HeadlessSessionHost host = hosts[index];
                    ulong previousGeneration =
                        host.Runtime.Generation.Value;
                    Assert.Equal(
                        RuntimeSessionStartStatus.Connected,
                        host.Reconnect().Status);
                    reconnectCounts[index]++;
                    Assert.True(
                        host.Runtime.Generation.Value
                        > previousGeneration);
                    WorldSession replacement =
                        operations.ActiveSession(host.SessionId);
                    replacement.GameActionCapture = _ => { };
                    SpawnInto(
                        replacement,
                        sharedPlayerGuid,
                        index + frame);
                    Assert.True(
                        host.Commands.Selection.SelectObject(
                            host.Runtime.Generation,
                            sharedPlayerGuid).Accepted);
                    Assert.True(
                        host.Commands.Movement.SetIntent(
                            host.Runtime.Generation,
                            new MovementInput(
                                Forward: (frame & 1) == 0,
                                TurnRight: (frame & 1) != 0,
                                Run: true)).Accepted);
                }

                time.Advance(
                    TimeSpan.FromMilliseconds(
                        turnPeriodMilliseconds));
                if (!scheduler.DispatchDue(time.GetTimestamp()))
                {
                    throw new InvalidOperationException(
                        $"No session dispatched at stress frame {frame}.");
                }
            }
            long allocatedBytes =
                GC.GetAllocatedBytesForCurrentThread()
                - allocatedBefore;
            long retainedAfter = GC.GetTotalMemory(
                forceFullCollection: true);
            long retainedGrowth = Math.Max(
                0L,
                retainedAfter - retainedBefore);

            HeadlessSchedulerSnapshot snapshot =
                scheduler.CaptureSnapshot();
            Assert.Equal(
                (long)sessionCount * frameCount,
                snapshot.TurnCount);
            Assert.Equal(0L, snapshot.CatchUpCollapseCount);
            Assert.Equal(0L, snapshot.LateDeadlineCount);
            Assert.Equal(0, snapshot.FaultedSessionCount);
            Assert.Equal(sessionCount, snapshot.ActiveSessionCount);
            Assert.InRange(
                allocatedBytes,
                0L,
                maximumProcessAllocationBytesPerSecond
                * simulatedSeconds);
            Assert.InRange(
                retainedGrowth,
                0L,
                16L * 1024L * 1024L);
            Assert.All(
                reconnectCounts,
                static count => Assert.True(count > 0));
            Assert.All(
                deathCounts,
                static count => Assert.True(count > 0));
            var finalRecords = new List<RuntimeEntityRecord>(
                sessionCount);
            for (int index = 0; index < hosts.Length; index++)
            {
                HeadlessSessionHost host = hosts[index];
                Assert.Equal(
                    (ulong)frameCount,
                    host.Runtime.Clock.FrameNumber);
                Assert.True(
                    host.Runtime.EntityObjects.Entities.TryGetActive(
                        sharedPlayerGuid,
                        out RuntimeEntityRecord finalRecord));
                finalRecords.Add(finalRecord);
                Assert.True(
                    host.Runtime.TransitOwner
                        .CaptureOwnership()
                        .IsSessionIdle);
                Assert.True(
                    host.Runtime.CommunicationOwner.View.Count
                    >= deathCounts[index]);
            }
            Assert.Equal(
                sessionCount,
                finalRecords
                    .Distinct(
                        ReferenceEqualityComparer.Instance)
                    .Count());
        }
        finally
        {
            for (int index = hosts.Length - 1; index >= 0; index--)
                hosts[index].Dispose();
        }

        int reconnectCount = reconnectCounts.Sum();
        Assert.Equal(
            sessionCount + reconnectCount,
            operations.CreatedSessionCount);
        Assert.Equal(
            sessionCount + reconnectCount,
            operations.DisposedSessionCount);
        Assert.All(
            hosts,
            static host =>
                Assert.True(
                    host.Runtime.CaptureOwnership().IsConverged));
    }

    [Fact]
    public void RemoteSteadyStatePositionAdvancesTheBotVisibleCell()
    {
        const uint playerGuid = 0x50000001u;
        const uint remoteGuid = 0x70000031u;
        var operations = new FixtureOperations(playerGuid);
        using HeadlessSessionHost host = CreateHost(0, operations);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);
        WorldSession session = operations.ActiveSession(host.SessionId);
        session.GameActionCapture = _ => { };

        SpawnInto(session, remoteGuid, 1f);
        Assert.True(host.Runtime.Entities.TryGet(
            remoteGuid,
            out RuntimeEntitySnapshot created));
        Assert.Equal(0x01010001u, created.CellId);

        const uint movedCell = 0x01010025u;
        EventDelegate<Action<WorldSession.EntityPositionUpdate>>(
            session,
            nameof(session.PositionUpdated))(
                new WorldSession.EntityPositionUpdate(
                    remoteGuid,
                    Position(2f) with { LandblockId = movedCell },
                    Velocity: null,
                    PlacementId: null,
                    IsGrounded: true,
                    InstanceSequence: 1,
                    PositionSequence: 2,
                    TeleportSequence: 0,
                    ForcePositionSequence: 0));

        Assert.True(host.Runtime.Entities.TryGet(
            remoteGuid,
            out RuntimeEntitySnapshot moved));
        Assert.Equal(movedCell, moved.CellId);
        Assert.Equal(movedCell, moved.Position!.Value.ObjCellId);
    }

    private static HeadlessSessionHost CreateHost(
        int index,
        FixtureOperations operations,
        TimeProvider? timeProvider = null)
    {
        string id = $"bot-{index}";
        var credential = new HeadlessCredentialSecret(
            $"{id}-credential",
            "fixture-password");
        try
        {
            return new HeadlessSessionHost(
                new HeadlessSessionDescriptor
                {
                    Id = id,
                    Endpoint = new HeadlessEndpointDescriptor
                    {
                        Host = "127.0.0.1",
                        Port = 9000,
                    },
                    Account = id,
                    Character = new HeadlessCharacterSelector
                    {
                        Index = 0,
                    },
                    Policy = new HeadlessBotPolicyDescriptor
                    {
                        Id = "idle",
                    },
                    Credential = new HeadlessCredentialReference
                    {
                        Provider =
                            HeadlessCredentialProviderKind.StandardInput,
                        Reference = $"{id}-credential",
                    },
                },
                credential,
                new HeadlessDiagnosticWriter(TextWriter.Null),
                operations,
                timeProvider);
        }
        catch
        {
            credential.Dispose();
            throw;
        }
    }

    private static void SpawnInto(
        WorldSession session,
        uint guid,
        float positionX) =>
        EventDelegate<Action<WorldSession.EntitySpawn>>(
            session,
            nameof(session.EntitySpawned))(
                Spawn(guid, positionX));

    private static void Portal(
        WorldSession session,
        uint guid,
        float positionX,
        ushort teleportSequence = 1)
    {
        EventDelegate<Action<uint>>(
            session,
            nameof(session.TeleportStarted))(teleportSequence);
        EventDelegate<Action<WorldSession.EntityPositionUpdate>>(
            session,
            nameof(session.PositionUpdated))(
                new WorldSession.EntityPositionUpdate(
                    guid,
                    Position(positionX) with
                    {
                        LandblockId = 0x01020001u,
                    },
                    Velocity: null,
                    PlacementId: null,
                    IsGrounded: true,
                    InstanceSequence: 1,
                    PositionSequence: checked(
                        (ushort)(teleportSequence + 1)),
                    TeleportSequence: teleportSequence,
                    ForcePositionSequence: 0));
    }

    private static void Death(
        WorldSession session,
        uint victimGuid,
        int sequence) =>
        EventDelegate<Action<PlayerKilled.Parsed>>(
            session,
            nameof(session.PlayerKilledReceived))(
                new PlayerKilled.Parsed(
                    $"Headless death {sequence}.",
                    victimGuid,
                    0x5000FFFFu));

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        float positionX)
    {
        CreateObject.ServerPosition position = Position(positionX);
        var timestamps = new PhysicsTimestamps(
            Position: 1,
            Movement: 1,
            State: 1,
            Vector: 1,
            Teleport: 0,
            ServerControlledMove: 1,
            ForcePosition: 0,
            ObjDesc: 1,
            Instance: 1);
        var physics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.ReportCollisions,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x02000001u,
            MotionTableId: null,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: null,
            Friction: null,
            Elasticity: null,
            Translucency: null,
            Velocity: null,
            Acceleration: null,
            AngularVelocity: null,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            guid,
            position,
            0x02000001u,
            [],
            [],
            [],
            null,
            null,
            "Headless",
            null,
            null,
            null,
            PhysicsState: physics.RawState,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    private static CreateObject.ServerPosition Position(float positionX) =>
        new(
            0x01010001u,
            positionX,
            10f,
            5f,
            1f,
            0f,
            0f,
            0f);

    private static TDelegate EventDelegate<TDelegate>(
        WorldSession session,
        string eventName)
        where TDelegate : Delegate =>
        Assert.IsType<TDelegate>(
            typeof(WorldSession).GetField(
                eventName,
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(session));

    private sealed class FixtureOperations(uint sharedPlayerGuid)
        : ILiveSessionOperations
    {
        private readonly Dictionary<string, WorldSession> _active =
            new(StringComparer.Ordinal);

        public int CreatedSessionCount { get; private set; }
        public int DisposedSessionCount { get; private set; }

        public WorldSession ActiveSession(string account) =>
            _active[account];

        public IPEndPoint ResolveEndpoint(string host, int port) =>
            new(IPAddress.Loopback, port);

        public WorldSession CreateSession(IPEndPoint endpoint)
        {
            CreatedSessionCount++;
            return new WorldSession(endpoint, new FixtureTransport());
        }

        public void Connect(
            WorldSession session,
            string user,
            string password) =>
            _active[user] = session;

        public CharacterList.Parsed GetCharacters(
            WorldSession session) =>
            new(
                0u,
                [
                    new CharacterList.Character(
                        sharedPlayerGuid,
                        "Headless",
                        0u),
                ],
                [],
                11,
                "account",
                true,
                true);

        public void EnterWorld(
            WorldSession session,
            int activeCharacterIndex)
        {
        }

        public void Tick(WorldSession session)
        {
        }

        public void DisposeSession(WorldSession session)
        {
            DisposedSessionCount++;
            session.Dispose();
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency =>
            TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        internal void Advance(TimeSpan duration) =>
            _timestamp = checked(_timestamp + duration.Ticks);
    }

    private sealed class FixtureTransport : IWorldSessionTransport
    {
        public void Send(ReadOnlySpan<byte> datagram)
        {
        }

        public void Send(
            IPEndPoint remote,
            ReadOnlySpan<byte> datagram)
        {
        }

        public int Receive(
            Span<byte> destination,
            TimeSpan timeout,
            out IPEndPoint? from)
        {
            from = null;
            return -1;
        }

        public ValueTask<NetReceiveResult> ReceiveAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<NetReceiveResult>(
                new OperationCanceledException(cancellationToken));

        public void Dispose()
        {
        }
    }
}
