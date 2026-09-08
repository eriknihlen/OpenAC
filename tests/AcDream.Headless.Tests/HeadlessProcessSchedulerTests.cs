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
using AcDream.Headless.Policies;
using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.Headless.Tests;

public sealed class HeadlessProcessSchedulerTests
{
    [Fact]
    public void AbsoluteDeadlinesTickSessionsInStableElapsedTime()
    {
        var time = new ManualTimeProvider();
        var operations = new FixtureSessionOperations();
        using HeadlessSessionHost first =
            CreateSession("first", time, operations);
        using HeadlessSessionHost second =
            CreateSession("second", time, operations);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            first.Start().Status);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            second.Start().Status);
        var scheduler = new HeadlessProcessScheduler(
            [first, second],
            time);

        Assert.False(scheduler.DispatchDue(time.GetTimestamp()));
        time.Advance(TimeSpan.FromMilliseconds(15));

        Assert.True(scheduler.DispatchDue(time.GetTimestamp()));
        Assert.Equal(1UL, first.Runtime.Clock.FrameNumber);
        Assert.Equal(1UL, second.Runtime.Clock.FrameNumber);
        Assert.Equal(
            0.015d,
            first.Runtime.Clock.SimulationTimeSeconds,
            precision: 9);
        HeadlessSchedulerSnapshot snapshot =
            scheduler.CaptureSnapshot();
        Assert.Equal(2, snapshot.TurnCount);
        Assert.Equal(0, snapshot.CatchUpCollapseCount);
    }

    [Fact]
    public void LongPauseUsesBoundedCatchUpAndPreservesElapsedTime()
    {
        var time = new ManualTimeProvider();
        var operations = new FixtureSessionOperations();
        using HeadlessSessionHost session =
            CreateSession("catch-up", time, operations);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            session.Start().Status);
        var scheduler = new HeadlessProcessScheduler(
            [session],
            time,
            maximumCatchUpTurns: 4);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(scheduler.DispatchDue(time.GetTimestamp()));

        Assert.Equal(4UL, session.Runtime.Clock.FrameNumber);
        Assert.Equal(
            1d,
            session.Runtime.Clock.SimulationTimeSeconds,
            precision: 9);
        Assert.Equal(
            1,
            scheduler.CaptureSnapshot().CatchUpCollapseCount);
    }

    [Fact]
    public void SchedulerMeasuresDeadlineLatenessWithoutAllocatingPerTurn()
    {
        var time = new ManualTimeProvider();
        var operations = new FixtureSessionOperations();
        using HeadlessSessionHost first =
            CreateSession("late-first", time, operations);
        using HeadlessSessionHost second =
            CreateSession("late-second", time, operations);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            first.Start().Status);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            second.Start().Status);
        var scheduler = new HeadlessProcessScheduler(
            [first, second],
            time);

        time.Advance(TimeSpan.FromMilliseconds(20));
        Assert.True(scheduler.DispatchDue(time.GetTimestamp()));

        HeadlessSchedulerSnapshot snapshot =
            scheduler.CaptureSnapshot();
        Assert.Equal(2L, snapshot.LateDeadlineCount);
        Assert.Equal(5d, snapshot.MeanLatenessMilliseconds);
        Assert.Equal(5d, snapshot.MaximumLatenessMilliseconds);
    }

    [Fact]
    public void ProcessObservationUsesOneAbsoluteDeadlineAndCollapsesPauses()
    {
        var time = new ManualTimeProvider();
        var operations = new FixtureSessionOperations();
        using HeadlessSessionHost session =
            CreateSession("observation", time, operations);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            session.Start().Status);
        int observationCount = 0;
        var scheduler = new HeadlessProcessScheduler(
            [session],
            time,
            observation: _ => observationCount++,
            observationPeriod: TimeSpan.FromSeconds(1));

        time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(
            scheduler.DispatchObservationDue(time.GetTimestamp()));
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(
            scheduler.DispatchObservationDue(time.GetTimestamp()));
        Assert.False(
            scheduler.DispatchObservationDue(time.GetTimestamp()));
        time.Advance(TimeSpan.FromMilliseconds(2500));
        Assert.True(
            scheduler.DispatchObservationDue(time.GetTimestamp()));
        Assert.False(
            scheduler.DispatchObservationDue(time.GetTimestamp()));
        Assert.Equal(2, observationCount);
    }

    [Fact]
    public void SessionStartupRebaseExcludesSequentialConnectTime()
    {
        var time = new ManualTimeProvider();
        var operations = new FixtureSessionOperations();
        using HeadlessSessionHost first =
            CreateSession("rebase-first", time, operations);
        using HeadlessSessionHost second =
            CreateSession("rebase-second", time, operations);
        var scheduler = new HeadlessProcessScheduler(
            [first, second],
            time);

        time.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            first.Start().Status);
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            second.Start().Status);
        scheduler.RebaseDeadlinesAfterSessionStart();

        Assert.False(scheduler.DispatchDue(time.GetTimestamp()));
        time.Advance(TimeSpan.FromMilliseconds(15));
        Assert.True(scheduler.DispatchDue(time.GetTimestamp()));
        HeadlessSchedulerSnapshot snapshot =
            scheduler.CaptureSnapshot();
        Assert.Equal(2L, snapshot.TurnCount);
        Assert.Equal(0L, snapshot.LateDeadlineCount);
        Assert.Equal(0L, snapshot.CatchUpCollapseCount);
        Assert.Throws<InvalidOperationException>(
            scheduler.RebaseDeadlinesAfterSessionStart);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, TimeSpan.TicksPerMillisecond)]
    [InlineData(
        TimeSpan.TicksPerMillisecond - 1,
        TimeSpan.TicksPerMillisecond)]
    [InlineData(
        TimeSpan.TicksPerMillisecond,
        TimeSpan.TicksPerMillisecond)]
    [InlineData(
        TimeSpan.TicksPerMillisecond + 1,
        TimeSpan.TicksPerMillisecond + 1)]
    public void SchedulerNeverBusyLoopsOnSubMillisecondTimerDelay(
        long sourceTicks,
        long expectedTicks)
    {
        TimeSpan normalized =
            HeadlessProcessScheduler.NormalizeTimerDelay(
                TimeSpan.FromTicks(sourceTicks));

        Assert.Equal(expectedTicks, normalized.Ticks);
    }

    [Fact]
    public void SystemTimerCadenceDoesNotBusyLoopBetweenTurns()
    {
        var operations = new FixtureSessionOperations();
        using HeadlessSessionHost session =
            CreateSession(
                "system-timer",
                TimeProvider.System,
                operations);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            session.Start().Status);
        var scheduler = new HeadlessProcessScheduler([session]);
        scheduler.RebaseDeadlinesAfterSessionStart();
        using var cancellation =
            new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        scheduler.Run(cancellation.Token);

        HeadlessSchedulerSnapshot snapshot =
            scheduler.CaptureSnapshot();
        Assert.InRange(snapshot.WaitCount, 1L, 1000L);
        Assert.True(snapshot.TurnCount > 0L);
    }

    [Fact]
    public void ReconnectQuiescenceIsAMonotonicDeadlineNotABlockingWait()
    {
        var time = new ManualTimeProvider();
        var operations = new FixtureSessionOperations();
        using HeadlessSessionHost session = CreateSession(
            "reconnect",
            time,
            operations,
            TimeSpan.FromMilliseconds(2500));
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            session.Start().Status);
        var scheduler = new HeadlessProcessScheduler(
            [session],
            time);

        RuntimeSessionStartResult reconnect = session.Reconnect();

        Assert.Equal(
            RuntimeSessionStartStatus.Deferred,
            reconnect.Status);
        Assert.True(session.IsReconnectPending);
        Assert.Equal(1, operations.CreatedSessionCount);
        Assert.Equal(1, operations.DisposedSessionCount);

        time.Advance(TimeSpan.FromMilliseconds(2499));
        Assert.False(scheduler.DispatchDue(time.GetTimestamp()));
        Assert.Equal(1, operations.CreatedSessionCount);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(scheduler.DispatchDue(time.GetTimestamp()));
        Assert.False(session.IsReconnectPending);
        Assert.Equal(2, operations.CreatedSessionCount);
        Assert.True(session.Runtime.Session.IsInWorld);
    }

    [Fact]
    public void IdleDispatchBeforeDeadlineAllocatesNothingAndDoesNotTick()
    {
        var time = new ManualTimeProvider();
        var operations = new FixtureSessionOperations();
        using HeadlessSessionHost session =
            CreateSession("idle", time, operations);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            session.Start().Status);
        var scheduler = new HeadlessProcessScheduler([session], time);

        for (int index = 0; index < 32; index++)
            Assert.False(scheduler.DispatchDue(time.GetTimestamp()));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 100_000; index++)
        {
            if (scheduler.DispatchDue(time.GetTimestamp()))
                throw new InvalidOperationException("Idle session ticked.");
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
        Assert.Equal(0UL, session.Runtime.Clock.FrameNumber);
        Assert.Equal(0L, scheduler.CaptureSnapshot().TurnCount);
    }

    [Fact]
    public async Task ProcessHostRunsMultipleSessionsOnOneScheduler()
    {
        var configuration = new HeadlessConfiguration
        {
            Version = 1,
            Sessions =
            [
                Descriptor(
                    "first",
                    "first-stdin"),
                Descriptor(
                    "second",
                    "second-stdin"),
            ],
        };
        var operations = new FixtureSessionOperations();
        using var diagnostics = new StringWriter();
        using var host = new HeadlessProcessHost(
            configuration,
            HeadlessPathSet.Resolve(new HeadlessPathOverrides()),
            new System.IO.StringReader(
                "first-password"
                + Environment.NewLine
                + "second-password"
                + Environment.NewLine),
            diagnostics,
            operations);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        HeadlessExitCode result =
            await host.RunAsync(cancellation.Token);

        Assert.Equal(HeadlessExitCode.Success, result);
        Assert.Equal(2, host.Sessions.Count);
        Assert.All(
            host.Sessions,
            session => Assert.True(session.Runtime.Session.IsInWorld));
        Assert.Equal(2, operations.CreatedSessionCount);
        Assert.DoesNotContain(
            "password",
            diagnostics.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "\"kind\":\"resources\"",
            diagnostics.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "\"configuredCount\":2",
            diagnostics.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "\"resourceEnvelope\":",
            diagnostics.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "\"profile\":\"k4-linux-30-session\"",
            diagnostics.ToString(),
            StringComparison.Ordinal);

        host.Dispose();

        Assert.Contains(
            "\"state\":\"disposed\"",
            diagnostics.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "\"convergedRuntimeCount\":2",
            diagnostics.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "\"violations\":",
            diagnostics.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThirtySessionsConvergeAcrossRandomizedCancellationEdges()
    {
        const int sessionCount = 30;
        var random = new Random(0xAC2013);
        for (int iteration = 0; iteration < 5; iteration++)
        {
            var configuration = new HeadlessConfiguration
            {
                Version = 1,
                Sessions = Enumerable.Range(0, sessionCount)
                    .Select(index => Descriptor(
                        $"cancel-{iteration}-{index}",
                        $"cancel-{iteration}-{index}-stdin"))
                    .Cast<HeadlessSessionDescriptor?>()
                    .ToList(),
            };
            var operations = new FixtureSessionOperations();
            using var diagnostics = new StringWriter();
            using var host = new HeadlessProcessHost(
                configuration,
                HeadlessPathSet.Resolve(
                    new HeadlessPathOverrides()),
                new System.IO.StringReader(string.Concat(
                    Enumerable.Repeat(
                        "random-cancel-password"
                        + Environment.NewLine,
                        sessionCount))),
                diagnostics,
                operations);
            using var cancellation = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(random.Next(37, 144)));

            HeadlessExitCode result =
                await host.RunAsync(cancellation.Token);

            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(HeadlessExitCode.Success, result);
            Assert.Equal(sessionCount, operations.CreatedSessionCount);
            Assert.All(
                host.Sessions,
                static session =>
                    Assert.False(session.IsFaulted));

            host.Dispose();

            Assert.Equal(sessionCount, operations.DisposedSessionCount);
            Assert.All(
                host.Sessions,
                static session =>
                    Assert.True(
                        session.Runtime
                            .CaptureOwnership()
                            .IsConverged));
            string output = diagnostics.ToString();
            Assert.Contains(
                "\"state\":\"disposed\"",
                output,
                StringComparison.Ordinal);
            Assert.Contains(
                $"\"convergedRuntimeCount\":{sessionCount}",
                output,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "random-cancel-password",
                output,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ProcessHostRunsStartAndEveryTickOnOneDedicatedUpdateThread()
    {
        var configuration = new HeadlessConfiguration
        {
            Version = 1,
            Sessions =
            [
                Descriptor(
                    "update-thread",
                    "update-thread-stdin"),
            ],
        };
        var operations = new ThreadRecordingSessionOperations();
        using var diagnostics = new StringWriter();
        using var host = new HeadlessProcessHost(
            configuration,
            HeadlessPathSet.Resolve(new HeadlessPathOverrides()),
            new System.IO.StringReader(
                "update-thread-password" + Environment.NewLine),
            diagnostics,
            operations);
        using var cancellation = new CancellationTokenSource();

        int callerThread = Environment.CurrentManagedThreadId;
        Task<HeadlessExitCode> run = host.RunAsync(cancellation.Token);
        var stopwatch = Stopwatch.StartNew();
        while (operations.TickCount < 5
            && stopwatch.Elapsed < TimeSpan.FromSeconds(10))
        {
            Thread.Sleep(1);
        }
        cancellation.Cancel();
        HeadlessExitCode result = await run;

        Assert.Equal(HeadlessExitCode.Success, result);
        Assert.True(
            operations.TickCount >= 5,
            $"Expected at least 5 ticks, observed {operations.TickCount}.");
        int updateThread = operations.ConnectThreadId;
        Assert.NotEqual(0, updateThread);
        Assert.NotEqual(callerThread, updateThread);
        Assert.All(
            operations.TickThreadIds,
            id => Assert.Equal(updateThread, id));
    }

    [Fact]
    public void ThrowingPolicyQuarantinesOnlyItsOwnSession()
    {
        var time = new ManualTimeProvider();
        var operations = new FixtureSessionOperations();
        var throwing = new ThrowingPolicy();
        var healthy = new CountingPolicy();
        using HeadlessSessionHost first =
            CreateSession(
                "fault",
                time,
                operations,
                policyOverride: throwing);
        using HeadlessSessionHost second =
            CreateSession(
                "healthy",
                time,
                operations,
                policyOverride: healthy);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            first.Start().Status);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            second.Start().Status);
        var scheduler = new HeadlessProcessScheduler(
            [first, second],
            time);

        time.Advance(TimeSpan.FromMilliseconds(15));
        Assert.True(scheduler.DispatchDue(time.GetTimestamp()));

        Assert.True(first.IsFaulted);
        Assert.IsType<InvalidOperationException>(first.Fault);
        Assert.False(second.IsFaulted);
        Assert.Equal(1, healthy.TickCount);
        HeadlessSchedulerSnapshot snapshot =
            scheduler.CaptureSnapshot();
        Assert.Equal(1, snapshot.FaultedSessionCount);
        Assert.Equal(1, snapshot.ActiveSessionCount);

        time.Advance(TimeSpan.FromMilliseconds(15));
        Assert.True(scheduler.DispatchDue(time.GetTimestamp()));
        Assert.Equal(2, healthy.TickCount);
        Assert.Equal(1UL, first.Runtime.Clock.FrameNumber);
        Assert.Equal(2UL, second.Runtime.Clock.FrameNumber);
    }

    private static HeadlessSessionHost CreateSession(
        string id,
        TimeProvider time,
        ILiveSessionOperations operations,
        TimeSpan? reconnectQuiescence = null,
        IHeadlessBotPolicy? policyOverride = null)
    {
        var credential = new HeadlessCredentialSecret(
            $"{id}-credential",
            "password");
        try
        {
            return new HeadlessSessionHost(
                Descriptor(id, $"{id}-credential"),
                credential,
                new HeadlessDiagnosticWriter(TextWriter.Null),
                operations,
                time,
                reconnectQuiescence,
                policyOverride: policyOverride);
        }
        catch
        {
            credential.Dispose();
            throw;
        }
    }

    private static HeadlessSessionDescriptor Descriptor(
        string id,
        string credentialReference) => new()
    {
        Id = id,
        Endpoint = new HeadlessEndpointDescriptor
        {
            Host = "127.0.0.1",
            Port = 9000,
        },
        Account = $"{id}-account",
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
            Provider = HeadlessCredentialProviderKind.StandardInput,
            Reference = credentialReference,
        },
    };

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency =>
            TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        internal void Advance(TimeSpan duration) =>
            _timestamp = checked(_timestamp + duration.Ticks);
    }

    private sealed class FixtureSessionOperations : ILiveSessionOperations
    {
        public int CreatedSessionCount { get; private set; }
        public int DisposedSessionCount { get; private set; }

        public IPEndPoint ResolveEndpoint(string host, int port) =>
            new(IPAddress.Loopback, port);

        public WorldSession CreateSession(IPEndPoint endpoint)
        {
            CreatedSessionCount++;
            return new WorldSession(endpoint);
        }

        public void Connect(
            WorldSession session,
            string user,
            string password)
        {
        }

        public CharacterList.Parsed GetCharacters(
            WorldSession session) =>
            new(
                0u,
                [
                    new CharacterList.Character(
                        0x50000001u,
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

    private sealed class ThreadRecordingSessionOperations
        : ILiveSessionOperations
    {
        private int _connectThreadId;
        private readonly ConcurrentQueue<int> _tickThreadIds = new();

        internal int ConnectThreadId =>
            Volatile.Read(ref _connectThreadId);
        internal int TickCount => _tickThreadIds.Count;
        internal IReadOnlyCollection<int> TickThreadIds => _tickThreadIds;

        public IPEndPoint ResolveEndpoint(string host, int port) =>
            new(IPAddress.Loopback, port);

        public WorldSession CreateSession(IPEndPoint endpoint) =>
            new(endpoint);

        public void Connect(
            WorldSession session,
            string user,
            string password) =>
            Volatile.Write(
                ref _connectThreadId,
                Environment.CurrentManagedThreadId);

        public CharacterList.Parsed GetCharacters(
            WorldSession session) =>
            new(
                0u,
                [
                    new CharacterList.Character(
                        0x50000001u,
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

        public void Tick(WorldSession session) =>
            _tickThreadIds.Enqueue(Environment.CurrentManagedThreadId);

        public void DisposeSession(WorldSession session) =>
            session.Dispose();
    }

    private abstract class FixturePolicy : IHeadlessBotPolicy
    {
        public virtual bool IsComplete => false;

        public abstract void Tick(
            IGameRuntimeView view,
            IGameRuntimeCommands commands);

        public void OnLifecycle(in RuntimeLifecycleDelta delta)
        {
        }

        public void OnCommand(in RuntimeCommandDelta delta)
        {
        }

        public void OnEntity(in RuntimeEntityDelta delta)
        {
        }

        public void OnInventory(in RuntimeInventoryDelta delta)
        {
        }

        public void OnChat(in RuntimeChatDelta delta)
        {
        }

        public void OnMovement(in RuntimeMovementDelta delta)
        {
        }

        public void OnPortal(in RuntimePortalDelta delta)
        {
        }

        public void OnCombat(in RuntimeCombatDelta delta)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingPolicy : FixturePolicy
    {
        public override void Tick(
            IGameRuntimeView view,
            IGameRuntimeCommands commands) =>
            throw new InvalidOperationException("injected policy fault");
    }

    private sealed class CountingPolicy : FixturePolicy
    {
        public int TickCount { get; private set; }

        public override void Tick(
            IGameRuntimeView view,
            IGameRuntimeCommands commands) =>
            TickCount++;
    }
}
