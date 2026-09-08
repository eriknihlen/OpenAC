using System.Net;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.Runtime.Tests.Session;

public sealed class LiveSessionControllerTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed) =>
            _timestamp += elapsed.Ticks;
    }

    private sealed class TestTransport : IWorldSessionTransport
    {
        public void Send(ReadOnlySpan<byte> datagram) { }

        public void Send(IPEndPoint remote, ReadOnlySpan<byte> datagram) { }

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
            throw new OperationCanceledException(cancellationToken);

        public void Dispose() { }
    }

    private sealed class TestCommandBus
    {
        public bool Active { get; set; }
        public int PublishCount { get; private set; }

        public void Publish<T>(T command) where T : notnull
        {
            if (Active)
                PublishCount++;
        }
    }

    private sealed class TestOperations(List<string> calls) : ILiveSessionOperations
    {
        public CharacterList.Parsed? Characters { get; set; } = AvailableCharacters();
        public Action? OnResolve { get; set; }
        public Action? OnCreate { get; set; }
        public Action? OnConnect { get; set; }
        public Action? OnCharacters { get; set; }
        public Action? OnEnterWorld { get; set; }
        public Action? OnTick { get; set; }
        public Action? OnDispose { get; set; }
        public bool ThrowOnConnect { get; set; }
        public bool ThrowOnCreate { get; set; }
        public bool ThrowOnCharacters { get; set; }
        public bool ThrowOnEnterWorld { get; set; }
        public CharacterError.Parsed? EnterWorldRejection { get; set; }
        public bool ThrowOnTick { get; set; }
        public bool FailDisposeOnce { get; set; }
        public List<WorldSession> Sessions { get; } = [];
        public Dictionary<WorldSession, int> DisposeCounts { get; } = [];
        public List<(string Account, int ActiveIndex)> DeleteRequests { get; } = [];
        public List<uint> RestoreRequests { get; } = [];
        public int EnterWorldCount { get; private set; }
        public int TickCount { get; private set; }

        public IPEndPoint ResolveEndpoint(string host, int port)
        {
            calls.Add("resolve");
            OnResolve?.Invoke();
            return new IPEndPoint(IPAddress.Loopback, port);
        }

        public WorldSession CreateSession(IPEndPoint endpoint)
        {
            calls.Add("create");
            OnCreate?.Invoke();
            if (ThrowOnCreate)
                throw new InvalidOperationException("create failure");
            var session = new WorldSession(endpoint, new TestTransport());
            Sessions.Add(session);
            return session;
        }

        public void Connect(WorldSession session, string user, string password)
        {
            calls.Add("connect");
            OnConnect?.Invoke();
            if (ThrowOnConnect)
                throw new InvalidOperationException("connect failure");
        }

        public CharacterList.Parsed? GetCharacters(WorldSession session)
        {
            OnCharacters?.Invoke();
            if (ThrowOnCharacters)
                throw new InvalidOperationException("characters failure");
            return Characters;
        }

        public ServerName.Parsed? ServerInfo { get; set; }

        public ServerName.Parsed? GetServerInfo(WorldSession session) => ServerInfo;

        public void StartCharacterSelectionReceive(WorldSession session) =>
            calls.Add("start-character-selection-receive");

        public void EnterWorld(WorldSession session, int activeCharacterIndex)
        {
            calls.Add($"enter:{activeCharacterIndex}");
            EnterWorldCount++;
            OnEnterWorld?.Invoke();
            if (EnterWorldRejection is { } rejection)
                throw new CharacterSelectionRejectedException(rejection);
            if (ThrowOnEnterWorld)
                throw new InvalidOperationException("enter failure");
        }

        public void DeleteCharacter(
            WorldSession session,
            string accountName,
            int activeCharacterIndex)
        {
            calls.Add($"delete:{activeCharacterIndex}");
            DeleteRequests.Add((accountName, activeCharacterIndex));
        }

        public void RestoreCharacter(WorldSession session, uint characterId)
        {
            calls.Add($"restore:{characterId:X8}");
            RestoreRequests.Add(characterId);
        }

        public void Tick(WorldSession session)
        {
            calls.Add("tick");
            TickCount++;
            OnTick?.Invoke();
            if (ThrowOnTick)
                throw new InvalidOperationException("tick failure");
        }

        public void DisposeSession(WorldSession session)
        {
            calls.Add("dispose-session");
            OnDispose?.Invoke();
            if (FailDisposeOnce)
            {
                FailDisposeOnce = false;
                throw new InvalidOperationException("dispose failure");
            }
            DisposeCounts[session] = DisposeCounts.GetValueOrDefault(session) + 1;
        }

        public int RequestCharacterLogOffCount { get; private set; }
        public int ReturnToCharacterSelectCount { get; private set; }
        public bool ThrowOnRequestCharacterLogOff { get; set; }
        public bool ThrowOnReturnToCharacterSelect { get; set; }

        public void RequestCharacterLogOff(WorldSession session)
        {
            calls.Add("request-character-logoff");
            RequestCharacterLogOffCount++;
            if (ThrowOnRequestCharacterLogOff)
                throw new InvalidOperationException("logoff request failure");
        }

        public void ReturnToCharacterSelect(WorldSession session)
        {
            calls.Add("return-to-character-select");
            ReturnToCharacterSelectCount++;
            if (ThrowOnReturnToCharacterSelect)
                throw new InvalidOperationException("return failure");
        }
    }

    private sealed class TestHost(List<string> calls) : ILiveSessionLifecycleHost
    {
        public Action? OnBind { get; set; }
        public Action? OnReset { get; set; }
        public Action? OnConnecting { get; set; }
        public Action? OnConnected { get; set; }
        public Action? OnRoster { get; set; }
        public Action? OnSelected { get; set; }
        public Action? OnActivate { get; set; }
        public Action? OnEntered { get; set; }
        public Action? OnDetach { get; set; }
        public bool FailResetOnce { get; set; }
        public bool FailDetachOnce { get; set; }
        public bool FailDeactivateOnce { get; set; }
        public bool FailEventDetachOnce { get; set; }
        public bool ThrowOnBind { get; set; }
        public bool ThrowOnConnecting { get; set; }
        public bool ThrowOnConnected { get; set; }
        public bool ThrowOnRoster { get; set; }
        public bool ThrowOnSelected { get; set; }
        public bool ThrowOnActivate { get; set; }
        public bool ThrowOnEntered { get; set; }
        public WorldSession? BindingSessionOverride { get; set; }
        public int ResetCount { get; private set; }
        public int DetachCount { get; private set; }
        public int DeactivateCount { get; private set; }
        public int EventDetachCount { get; private set; }
        public int ActivateCount { get; private set; }
        public List<TestCommandBus> CommandBuses { get; } = [];
        public List<LiveSessionCharacterSelection> Selections { get; } = [];
        public List<RuntimeGenerationToken> ResetGenerations { get; } = [];
        public List<LiveSessionRosterReport> Rosters { get; } = [];

        public LiveSessionBinding BindSession(WorldSession session)
        {
            calls.Add("bind");
            OnBind?.Invoke();
            if (ThrowOnBind)
                throw new InvalidOperationException("bind failure");
            var commands = new TestCommandBus();
            CommandBuses.Add(commands);
            return new LiveSessionBinding(
                BindingSessionOverride ?? session,
                activateCommands: () =>
                {
                    calls.Add("activate");
                    ActivateCount++;
                    commands.Active = true;
                    OnActivate?.Invoke();
                    if (ThrowOnActivate)
                        throw new InvalidOperationException("activate failure");
                },
                deactivateCommands: () =>
                {
                    calls.Add("deactivate");
                    DeactivateCount++;
                    commands.Active = false;
                    if (FailDeactivateOnce)
                    {
                        FailDeactivateOnce = false;
                        throw new InvalidOperationException("deactivate failure");
                    }
                },
                detachEvents: () =>
                {
                    calls.Add("detach-events");
                    EventDetachCount++;
                    if (FailEventDetachOnce)
                    {
                        FailEventDetachOnce = false;
                        throw new InvalidOperationException("event detach failure");
                    }
                });
        }

        public void ResetSessionState(
            RuntimeGenerationToken retiringGeneration)
        {
            calls.Add("reset");
            ResetCount++;
            ResetGenerations.Add(retiringGeneration);
            OnReset?.Invoke();
            if (FailResetOnce)
            {
                FailResetOnce = false;
                throw new InvalidOperationException("reset failure");
            }
        }

        public void ReportConnecting(string host, int port, string user)
        {
            calls.Add("report-connecting");
            OnConnecting?.Invoke();
            if (ThrowOnConnecting)
                throw new InvalidOperationException("connecting failure");
        }

        public void ReportConnected()
        {
            calls.Add("report-connected");
            OnConnected?.Invoke();
            if (ThrowOnConnected)
                throw new InvalidOperationException("connected failure");
        }

        public void ReportRoster(LiveSessionRosterReport roster)
        {
            calls.Add("roster");
            Rosters.Add(roster);
            OnRoster?.Invoke();
            if (ThrowOnRoster)
                throw new InvalidOperationException("roster failure");
        }

        public void ApplySelectedCharacter(LiveSessionCharacterSelection selection)
        {
            calls.Add("selected");
            Selections.Add(selection);
            OnSelected?.Invoke();
            if (ThrowOnSelected)
                throw new InvalidOperationException("selected failure");
        }

        public void ApplyEnteredWorld(LiveSessionCharacterSelection selection)
        {
            calls.Add("entered");
            OnEntered?.Invoke();
            if (ThrowOnEntered)
                throw new InvalidOperationException("entered failure");
        }

        public void DetachSession(WorldSession session)
        {
            calls.Add("detach-session");
            DetachCount++;
            OnDetach?.Invoke();
            if (FailDetachOnce)
            {
                FailDetachOnce = false;
                throw new InvalidOperationException("detach failure");
            }
        }
    }

    public enum ReentrantStopPoint
    {
        Reset,
        Resolve,
        Create,
        Bind,
        Connecting,
        Connect,
        Connected,
        Selected,
        EnterWorld,
        Activate,
        Entered,
    }

    [Fact]
    public void Start_BindsBeforeConnectAndPublishesCanonicalSelectionAfterEnterWorld()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);

        LiveSessionStartResult result = controller.Start(LiveOptions(), host);

        Assert.Equal(LiveSessionStartStatus.Connected, result.Status);
        Assert.Equal(
            [
                "reset", "resolve", "create", "bind", "report-connecting",
                "connect", "report-connected", "roster", "selected", "enter:1",
                "activate", "entered",
            ],
            calls);
        Assert.Equal(
            new LiveSessionCharacterSelection(1, 0x50000002u, "Ready", "Canonical"),
            result.Selection);
        Assert.True(controller.IsInWorld);
        Assert.Same(operations.Sessions[0], controller.CurrentSession);
        Assert.True(host.CommandBuses[0].Active);
    }

    [Fact]
    public void Start_AppliesWorldNameFromDurableServerInfoAfterConnect()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls)
        {
            ServerInfo = new ServerName.Parsed(1, 128, "sawato"),
        };
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);

        LiveSessionStartResult result = controller.Start(LiveOptions(), host);

        Assert.Equal(LiveSessionStartStatus.Connected, result.Status);
        Assert.Equal(
            "sawato",
            controller.CharacterSelectionState.View.Snapshot.WorldName);
    }

    [Fact]
    public void Start_ReportsRosterFromCharacterListBeforeSelection()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);

        LiveSessionStartResult result = controller.Start(LiveOptions(), host);

        Assert.Equal(LiveSessionStartStatus.Connected, result.Status);
        LiveSessionRosterReport roster = Assert.Single(host.Rosters);
        Assert.Equal("Canonical", roster.AccountName);
        Assert.Equal(11, roster.SlotCount);
        Assert.Equal(
            [
                new LiveSessionRosterEntry(0x50000001u, "Grey", 10u),
                new LiveSessionRosterEntry(0x50000002u, "Ready", 0u),
            ],
            roster.Entries);
        Assert.True(calls.IndexOf("roster") < calls.IndexOf("selected"));
    }

    [Fact]
    public void Start_GraphicalNoSelectorStopsBeforeEnterWorldThenTypedEnterContinuesSameScope()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);

        LiveSessionStartResult result = controller.Start(
            LiveOptions(awaitSelection: true),
            host);

        Assert.Equal(
            LiveSessionStartStatus.AwaitingCharacterSelection,
            result.Status);
        Assert.False(controller.IsInWorld);
        Assert.NotNull(controller.CurrentSession);
        Assert.Equal(0, operations.EnterWorldCount);
        Assert.False(host.CommandBuses[0].Active);
        RuntimeGenerationToken generation = controller.Generation;
        Assert.Equal(
            RuntimeCharacterSelectionLifecycle.AwaitingSelection,
            controller.CharacterSelection.Snapshot.Lifecycle);

        Assert.True(controller.Highlight(generation, 0x50000001u).Accepted);
        Assert.Equal(
            RuntimeCommandStatus.Rejected,
            controller.Enter(generation).Status);
        Assert.True(controller.Highlight(generation, 0x50000002u).Accepted);
        Assert.True(controller.Enter(generation).Accepted);

        Assert.True(controller.IsInWorld);
        Assert.Equal(1, operations.EnterWorldCount);
        Assert.Contains("enter:1", calls);
        Assert.True(host.CommandBuses[0].Active);
        Assert.Same(operations.Sessions[0], controller.CurrentSession);
    }

    [Fact]
    public void Start_ExplicitSelectorStillEntersWhenGraphicalPauseFlagIsSet()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);

        LiveSessionStartResult result = controller.Start(
            LiveOptions(
                selector: new LiveSessionCharacterSelector(
                    CharacterId: 0x50000002u),
                awaitSelection: true),
            host);

        Assert.Equal(LiveSessionStartStatus.Connected, result.Status);
        Assert.True(controller.IsInWorld);
        Assert.Equal(1, operations.EnterWorldCount);
    }

    [Fact]
    public void EnterRejection_SurfacesErrorAndKeepsExactPreWorldScopeRetryable()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls)
        {
            EnterWorldRejection = new CharacterError.Parsed(
                (uint)CharacterError.Code.EnterGameCharacterLocked),
        };
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);
        Assert.Equal(
            LiveSessionStartStatus.AwaitingCharacterSelection,
            controller.Start(LiveOptions(awaitSelection: true), host).Status);
        RuntimeGenerationToken generation = controller.Generation;
        WorldSession session = controller.CurrentSession!;

        Assert.Equal(
            RuntimeCommandStatus.Rejected,
            controller.Enter(generation).Status);
        Assert.Same(session, controller.CurrentSession);
        Assert.Equal(generation, controller.Generation);
        Assert.False(controller.IsInWorld);
        RuntimeCharacterSelectionSnapshot rejected =
            controller.CharacterSelection.Snapshot;
        Assert.Equal(
            RuntimeCharacterSelectionLifecycle.AwaitingSelection,
            rejected.Lifecycle);
        Assert.Equal(
            CharacterError.Code.EnterGameCharacterLocked,
            rejected.Error!.Value.Code);

        operations.EnterWorldRejection = null;
        Assert.True(controller.Enter(generation).Accepted);
        Assert.True(controller.IsInWorld);
        Assert.Same(session, controller.CurrentSession);
    }

    [Fact]
    public async Task CharacterCommands_UseWireSlotAndRestoreNeverWaitsForReply()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var time = new ManualTimeProvider();
        var controller = new LiveSessionController(operations, time);
        Assert.Equal(
            LiveSessionStartStatus.AwaitingCharacterSelection,
            controller.Start(LiveOptions(awaitSelection: true), host).Status);
        RuntimeGenerationToken generation = controller.Generation;

        Assert.True(controller.Highlight(generation, 0x50000002u).Accepted);
        Assert.True(controller.RequestDelete(generation).Accepted);
        Assert.Equal(0x50000002u,
            controller.CharacterSelection.Snapshot.PendingDeleteCharacterId);
        Assert.True(controller.Cancel(generation).Accepted);
        Assert.True(controller.RequestDelete(generation).Accepted);
        Assert.True(controller.ConfirmDelete(generation).Accepted);
        Assert.Equal([("Canonical", 1)], operations.DeleteRequests);
        Assert.False(controller.CharacterSelection.Snapshot.Buttons.CanEnter);
        controller.CharacterSelectionState.ApplyDeleteAcknowledged();
        controller.CharacterSelectionState.ApplyRoster(new LiveSessionRosterReport(
            "Canonical",
            11,
            [
                new(0x50000001u, "Grey", 10u),
                new(0x50000002u, "Ready", 1u),
            ]));

        Assert.True(controller.Highlight(generation, 0x50000001u).Accepted);
        RuntimeCommandResult restore = await Task.Run(
                () => controller.Restore(generation))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(restore.Accepted);
        Assert.Equal([0x50000001u], operations.RestoreRequests);
        Assert.True(controller.Highlight(generation, 0x50000002u).Accepted);
        Assert.Equal(
            RuntimeCommandStatus.Rejected,
            controller.Restore(generation).Status);
        controller.Tick();
        Assert.Equal(1, operations.TickCount);
        time.Advance(RuntimeCharacterSelectionState.RestoreCorrelationTimeout);
        controller.Tick();
        Assert.Equal(
            RuntimeCharacterSelectionOperation.None,
            controller.CharacterSelection.Snapshot.Operation);
        Assert.True(controller.CharacterSelection.Snapshot.Buttons.CanRestore);
        Assert.True(controller.Restore(generation).Accepted);
        Assert.Equal(
            [0x50000001u, 0x50000002u],
            operations.RestoreRequests);
        Assert.False(controller.IsInWorld);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoreCompletionDuringConfirmedDelete_PreservesDeleteUntilAck(
        bool acknowledgeBeforeCompletion)
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);
        Assert.Equal(
            LiveSessionStartStatus.AwaitingCharacterSelection,
            controller.Start(LiveOptions(awaitSelection: true), host).Status);
        RuntimeGenerationToken generation = controller.Generation;

        Assert.True(controller.Highlight(generation, 0x50000001u).Accepted);
        Assert.True(controller.Restore(generation).Accepted);
        Assert.True(controller.Highlight(generation, 0x50000002u).Accepted);
        Assert.True(controller.RequestDelete(generation).Accepted);
        Assert.True(controller.ConfirmDelete(generation).Accepted);
        Assert.Equal([("Canonical", 1)], operations.DeleteRequests);
        Assert.Equal(
            RuntimeCharacterSelectionOperation.DeleteRequested,
            controller.CharacterSelection.Snapshot.Operation);
        Assert.Equal(
            RuntimeCharacterSelectionButtons.None with { CanCreate = true },
            controller.CharacterSelection.Snapshot.Buttons);

        if (acknowledgeBeforeCompletion)
            controller.CharacterSelectionState.ApplyDeleteAcknowledged();

        controller.CharacterSelectionState.ApplyRestore(
            new CharacterRestore.Parsed(
                VerificationFlag: 1u,
                Guid: 0x50000001u,
                Name: "Restored",
                SecondsGreyedOut: 0u));

        Assert.Equal(
            acknowledgeBeforeCompletion
                ? RuntimeCharacterSelectionOperation.DeleteAcknowledged
                : RuntimeCharacterSelectionOperation.DeleteRequested,
            controller.CharacterSelection.Snapshot.Operation);
        Assert.Equal(
            RuntimeCharacterSelectionButtons.None with { CanCreate = true },
            controller.CharacterSelection.Snapshot.Buttons);
        Assert.True(controller.CharacterSelection.TryGet(
            0x50000001u,
            out RuntimeCharacterSelectionEntry restored));
        Assert.False(restored.IsPendingDelete);
        Assert.Equal(
            RuntimeCommandStatus.Rejected,
            controller.ConfirmDelete(generation).Status);
        Assert.Equal([("Canonical", 1)], operations.DeleteRequests);

        if (!acknowledgeBeforeCompletion)
            controller.CharacterSelectionState.ApplyDeleteAcknowledged();

        Assert.Equal(
            RuntimeCharacterSelectionOperation.DeleteAcknowledged,
            controller.CharacterSelection.Snapshot.Operation);
        Assert.Equal(
            RuntimeCharacterSelectionButtons.None with { CanCreate = true },
            controller.CharacterSelection.Snapshot.Buttons);
        Assert.Equal([("Canonical", 1)], operations.DeleteRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoreTimeoutDuringConfirmedDelete_PreservesDeleteUntilAck(
        bool acknowledgeBeforeTimeout)
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var time = new ManualTimeProvider();
        var controller = new LiveSessionController(operations, time);
        Assert.Equal(
            LiveSessionStartStatus.AwaitingCharacterSelection,
            controller.Start(LiveOptions(awaitSelection: true), host).Status);
        RuntimeGenerationToken generation = controller.Generation;

        Assert.True(controller.Highlight(generation, 0x50000001u).Accepted);
        Assert.True(controller.Restore(generation).Accepted);
        Assert.True(controller.Highlight(generation, 0x50000002u).Accepted);
        Assert.True(controller.RequestDelete(generation).Accepted);
        Assert.True(controller.ConfirmDelete(generation).Accepted);
        Assert.Equal([("Canonical", 1)], operations.DeleteRequests);

        if (acknowledgeBeforeTimeout)
            controller.CharacterSelectionState.ApplyDeleteAcknowledged();

        time.Advance(RuntimeCharacterSelectionState.RestoreCorrelationTimeout);
        controller.Tick();

        Assert.Equal(
            acknowledgeBeforeTimeout
                ? RuntimeCharacterSelectionOperation.DeleteAcknowledged
                : RuntimeCharacterSelectionOperation.DeleteRequested,
            controller.CharacterSelection.Snapshot.Operation);
        Assert.Equal(
            RuntimeCharacterSelectionButtons.None with { CanCreate = true },
            controller.CharacterSelection.Snapshot.Buttons);
        Assert.Equal(
            RuntimeCommandStatus.Rejected,
            controller.ConfirmDelete(generation).Status);
        Assert.Equal([("Canonical", 1)], operations.DeleteRequests);

        if (!acknowledgeBeforeTimeout)
            controller.CharacterSelectionState.ApplyDeleteAcknowledged();

        Assert.Equal(
            RuntimeCharacterSelectionOperation.DeleteAcknowledged,
            controller.CharacterSelection.Snapshot.Operation);
        Assert.Equal(
            RuntimeCharacterSelectionButtons.None with { CanCreate = true },
            controller.CharacterSelection.Snapshot.Buttons);
        Assert.Equal([("Canonical", 1)], operations.DeleteRequests);
    }

    [Fact]
    public void CharacterCommands_AreGenerationGatedAcrossReconnectAndStop()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);
        controller.Start(LiveOptions(awaitSelection: true), host);
        RuntimeGenerationToken first = controller.Generation;

        LiveSessionStartResult reconnect = controller.Reconnect(
            LiveOptions(awaitSelection: true),
            host);

        Assert.Equal(
            LiveSessionStartStatus.AwaitingCharacterSelection,
            reconnect.Status);
        Assert.Equal(
            RuntimeCommandStatus.StaleGeneration,
            controller.Highlight(first, 0x50000002u).Status);
        RuntimeGenerationToken second = controller.Generation;
        Assert.True(controller.Highlight(second, 0x50000002u).Accepted);
        controller.Stop();
        Assert.Equal(
            RuntimeCommandStatus.StaleGeneration,
            controller.Enter(second).Status);
        Assert.Equal(
            RuntimeCharacterSelectionLifecycle.Inactive,
            controller.CharacterSelection.Snapshot.Lifecycle);
        Assert.Equal(0, controller.CharacterSelection.Snapshot.RosterCount);
    }

    [Fact]
    public void Start_DisabledAndMissingCredentialsResetButNeverConstructSession()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);

        Assert.Equal(
            LiveSessionStartStatus.Disabled,
            controller.Start(LiveOptions(live: false), host).Status);
        Assert.Equal(
            LiveSessionStartStatus.MissingCredentials,
            controller.Start(LiveOptions(user: null), host).Status);

        Assert.Equal(2, host.ResetCount);
        Assert.Empty(operations.Sessions);
    }

    [Fact]
    public void Start_NoAvailableCharacterTearsDownExactScope()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls)
        {
            Characters = new CharacterList.Parsed(
                0u,
                [new CharacterList.Character(0x50000001u, "Grey", 5u)],
                [],
                11,
                "Canonical",
                true,
                true),
        };
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);

        LiveSessionStartResult result = controller.Start(LiveOptions(), host);

        Assert.Equal(LiveSessionStartStatus.NoCharacters, result.Status);
        Assert.False(controller.IsInWorld);
        Assert.Null(controller.CurrentSession);
        Assert.Equal(1, host.DeactivateCount);
        Assert.Equal(1, host.EventDetachCount);
        Assert.Equal(1, host.DetachCount);
        Assert.Equal(2, host.ResetCount);
        Assert.Equal(1, operations.DisposeCounts[operations.Sessions[0]]);
    }

    [Fact]
    public void Start_ProbeReportsRosterThenGracefullyDisconnectsWithoutSelectionOrEnterWorld()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);

        LiveSessionStartResult result = controller.Start(
            LiveOptions(probe: true),
            host);

        Assert.Equal(LiveSessionStartStatus.ProbeComplete, result.Status);
        Assert.Null(result.Selection);
        Assert.Equal(
            [
                "reset", "resolve", "create", "bind", "report-connecting",
                "connect", "report-connected", "roster", "deactivate",
                "detach-events", "dispose-session", "detach-session", "reset",
            ],
            calls);
        Assert.DoesNotContain("selected", calls);
        Assert.DoesNotContain("activate", calls);
        Assert.DoesNotContain("entered", calls);
        Assert.Equal(0, operations.EnterWorldCount);
        LiveSessionRosterReport roster = Assert.Single(host.Rosters);
        Assert.Equal("Canonical", roster.AccountName);
        Assert.False(controller.IsInWorld);
        Assert.Null(controller.CurrentSession);
        Assert.Equal(1, operations.DisposeCounts[operations.Sessions[0]]);

        LiveSessionOwnershipSnapshot ownership = controller.CaptureOwnership();
        Assert.Equal(RuntimeTeardownStage.Complete, ownership.LastTeardownStages);
        Assert.False(ownership.HasActiveSession);
        Assert.False(ownership.HasRetiredSession);
        Assert.False(ownership.HasPendingOperation);
    }

    [Fact]
    public void Start_ProbeWithoutCharacterListIsNonSuccessAndTearsDownGracefully()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls) { Characters = null };
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);

        LiveSessionStartResult result = controller.Start(
            LiveOptions(probe: true),
            host);

        Assert.Equal(LiveSessionStartStatus.NoCharacters, result.Status);
        Assert.Empty(host.Rosters);
        Assert.Equal(
            [
                "reset", "resolve", "create", "bind", "report-connecting",
                "connect", "report-connected", "deactivate",
                "detach-events", "dispose-session", "detach-session", "reset",
            ],
            calls);
        Assert.Equal(0, operations.EnterWorldCount);
        Assert.False(controller.IsInWorld);
        Assert.Null(controller.CurrentSession);
        Assert.Equal(1, operations.DisposeCounts[operations.Sessions[0]]);

        LiveSessionOwnershipSnapshot ownership = controller.CaptureOwnership();
        Assert.Equal(RuntimeTeardownStage.Complete, ownership.LastTeardownStages);
        Assert.False(ownership.HasActiveSession);
        Assert.False(ownership.HasRetiredSession);
        Assert.False(ownership.HasPendingOperation);
    }

    [Theory]
    [InlineData("index")]
    [InlineData("id")]
    [InlineData("name")]
    public void Start_SelectsConfiguredAvailableCharacter(string selectorKind)
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);
        LiveSessionCharacterSelector selector = selectorKind switch
        {
            "index" => new(ActiveIndex: 1),
            "id" => new(CharacterId: 0x50000002u),
            "name" => new(CharacterName: "ready"),
            _ => throw new ArgumentOutOfRangeException(nameof(selectorKind)),
        };

        LiveSessionStartResult result = controller.Start(
            LiveOptions(selector: selector),
            host);

        Assert.Equal(LiveSessionStartStatus.Connected, result.Status);
        Assert.Equal(0x50000002u, result.Selection!.CharacterId);
        Assert.Contains("enter:1", calls);
    }

    [Fact]
    public void Start_UnavailableConfiguredCharacterConvergesOffline()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);

        LiveSessionStartResult result = controller.Start(
            LiveOptions(
                selector: new LiveSessionCharacterSelector(
                    CharacterId: 0x50000001u)),
            host);

        Assert.Equal(LiveSessionStartStatus.NoCharacters, result.Status);
        Assert.False(controller.IsInWorld);
        Assert.Null(controller.CurrentSession);
        Assert.Equal(1, operations.DisposeCounts[operations.Sessions[0]]);
    }

    [Fact]
    public void Start_ConnectFailureConvergesOffline()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls) { ThrowOnConnect = true };
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);

        LiveSessionStartResult result = controller.Start(LiveOptions(), host);

        Assert.Equal(LiveSessionStartStatus.Failed, result.Status);
        Assert.Contains("connect failure", result.Error!.ToString());
        Assert.False(controller.IsInWorld);
        Assert.Null(controller.CurrentSession);
        Assert.Equal(1, operations.DisposeCounts[operations.Sessions[0]]);
        Assert.Equal(1, host.DetachCount);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("bind")]
    [InlineData("enter")]
    public void Start_OtherConstructionAndEntryFailuresConvergeOffline(string phase)
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls)
        {
            ThrowOnCreate = phase == "create",
            ThrowOnEnterWorld = phase == "enter",
        };
        var host = new TestHost(calls) { ThrowOnBind = phase == "bind" };
        var controller = new LiveSessionController(operations);

        LiveSessionStartResult result = controller.Start(LiveOptions(), host);

        Assert.Equal(LiveSessionStartStatus.Failed, result.Status);
        Assert.Contains($"{phase} failure", result.Error!.ToString());
        Assert.False(controller.IsInWorld);
        Assert.Null(controller.CurrentSession);
        if (phase == "create")
        {
            Assert.Empty(operations.Sessions);
            Assert.Equal(1, host.ResetCount);
        }
        else
        {
            Assert.Single(operations.Sessions);
            Assert.Equal(1, operations.DisposeCounts[operations.Sessions[0]]);
            Assert.Equal(2, host.ResetCount);
        }
        Assert.Equal(phase == "enter" ? 1 : 0, host.DetachCount);
    }

    [Fact]
    public void Start_HealthyDuplicateIsIdempotent()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);
        LiveSessionStartResult first = controller.Start(LiveOptions(), host);

        LiveSessionStartResult duplicate = controller.Start(LiveOptions(), host);

        Assert.Equal(LiveSessionStartStatus.Connected, duplicate.Status);
        Assert.Equal(first.Selection, duplicate.Selection);
        Assert.Single(operations.Sessions);
        Assert.Equal(1, host.ResetCount);
        Assert.Equal(1, host.ActivateCount);
    }

    [Fact]
    public void Reconnect_QuiescesAThenDisposesDetachesResetsBeforeConstructingB()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);
        controller.Start(LiveOptions(), host);
        WorldSession sessionA = operations.Sessions[0];
        TestCommandBus commandsA = host.CommandBuses[0];
        calls.Clear();

        LiveSessionStartResult result = controller.Reconnect(LiveOptions(), host);

        Assert.Equal(LiveSessionStartStatus.Connected, result.Status);
        Assert.Equal(
            [
                "deactivate", "detach-events", "dispose-session", "detach-session",
                "reset", "resolve", "create", "bind", "report-connecting",
                "connect", "report-connected", "roster", "selected", "enter:1",
                "activate", "entered",
            ],
            calls);
        commandsA.Publish(new object());
        Assert.Equal(0, commandsA.PublishCount);
        Assert.Equal(1, operations.DisposeCounts[sessionA]);
        Assert.Equal(2, operations.Sessions.Count);
        Assert.Same(operations.Sessions[1], controller.CurrentSession);
    }

    [Fact]
    public void ResetCallbacksCarryTheExactRetiringScopeGeneration()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);

        LiveSessionStartResult first =
            controller.Start(LiveOptions(), host);
        RuntimeGenerationToken firstGeneration = controller.Generation;
        LiveSessionStartResult second =
            controller.Reconnect(LiveOptions(), host);
        RuntimeGenerationToken secondGeneration = controller.Generation;
        controller.Stop();

        Assert.Equal(LiveSessionStartStatus.Connected, first.Status);
        Assert.Equal(LiveSessionStartStatus.Connected, second.Status);
        Assert.Equal(
            [
                RuntimeGenerationToken.Initial,
                firstGeneration,
                secondGeneration,
            ],
            host.ResetGenerations);
        Assert.NotEqual(firstGeneration, secondGeneration);
    }

    [Theory]
    [InlineData("detach", "stop")]
    [InlineData("detach", "dispose")]
    [InlineData("detach", "reconnect")]
    [InlineData("reset", "stop")]
    [InlineData("reset", "dispose")]
    [InlineData("reset", "reconnect")]
    public void Reconnect_ReentrantTeardownRequestNeverPublishesSupersededB(
        string callbackPhase,
        string request)
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);
        controller.Start(LiveOptions(), host);
        bool requested = false;
        Action callback = () =>
        {
            if (requested)
                return;
            requested = true;
            switch (request)
            {
                case "stop": controller.Stop(); break;
                case "dispose": controller.Dispose(); break;
                case "reconnect":
                    Assert.Equal(
                        LiveSessionStartStatus.Deferred,
                        controller.Reconnect(LiveOptions(), host).Status);
                    break;
            }
        };
        if (callbackPhase == "detach")
            host.OnDetach = callback;
        else
            host.OnReset = callback;

        LiveSessionStartResult outer = controller.Reconnect(LiveOptions(), host);

        Assert.Equal(LiveSessionStartStatus.Deferred, outer.Status);
        Assert.Equal(request == "reconnect" ? 2 : 1, operations.Sessions.Count);
        Assert.Equal(request == "reconnect", controller.IsInWorld);
        if (request == "dispose")
            Assert.Throws<ObjectDisposedException>(() => controller.Start(LiveOptions(), host));
        controller.Dispose();
    }

    [Fact]
    public void MismatchedBindingWithInterruptedDetachRetainsCleanupOwnershipForRetry()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        using var mismatchedSession = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9001),
            new TestTransport());
        var host = new TestHost(calls)
        {
            BindingSessionOverride = mismatchedSession,
            FailEventDetachOnce = true,
        };
        var controller = new LiveSessionController(operations);

        LiveSessionStartResult result = controller.Start(LiveOptions(), host);

        Assert.Equal(LiveSessionStartStatus.Failed, result.Status);
        Assert.Contains("different session", result.Error!.ToString());
        Assert.Equal(0, operations.DisposeCounts.GetValueOrDefault(operations.Sessions[0]));
        Assert.Equal(1, host.DeactivateCount);
        Assert.Equal(1, host.EventDetachCount);

        controller.Stop();

        Assert.Equal(1, operations.DisposeCounts[operations.Sessions[0]]);
        Assert.Equal(1, host.DeactivateCount);
        Assert.Equal(2, host.EventDetachCount);
        Assert.Equal(1, host.DetachCount);
        controller.Dispose();
    }

    [Theory]
    [InlineData(ReentrantStopPoint.Reset)]
    [InlineData(ReentrantStopPoint.Resolve)]
    [InlineData(ReentrantStopPoint.Create)]
    [InlineData(ReentrantStopPoint.Bind)]
    [InlineData(ReentrantStopPoint.Connecting)]
    [InlineData(ReentrantStopPoint.Connect)]
    [InlineData(ReentrantStopPoint.Connected)]
    [InlineData(ReentrantStopPoint.Selected)]
    [InlineData(ReentrantStopPoint.EnterWorld)]
    [InlineData(ReentrantStopPoint.Activate)]
    [InlineData(ReentrantStopPoint.Entered)]
    public void ReentrantStop_InvalidatesOuterGenerationAndCannotResurrect(
        ReentrantStopPoint point)
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);
        Action stop = controller.Stop;
        switch (point)
        {
            case ReentrantStopPoint.Reset: host.OnReset = stop; break;
            case ReentrantStopPoint.Resolve: operations.OnResolve = stop; break;
            case ReentrantStopPoint.Create: operations.OnCreate = stop; break;
            case ReentrantStopPoint.Bind: host.OnBind = stop; break;
            case ReentrantStopPoint.Connecting: host.OnConnecting = stop; break;
            case ReentrantStopPoint.Connect: operations.OnConnect = stop; break;
            case ReentrantStopPoint.Connected: host.OnConnected = stop; break;
            case ReentrantStopPoint.Selected: host.OnSelected = stop; break;
            case ReentrantStopPoint.EnterWorld: operations.OnEnterWorld = stop; break;
            case ReentrantStopPoint.Activate: host.OnActivate = stop; break;
            case ReentrantStopPoint.Entered: host.OnEntered = stop; break;
        }

        LiveSessionStartResult result = controller.Start(LiveOptions(), host);

        Assert.Equal(LiveSessionStartStatus.Deferred, result.Status);
        Assert.False(controller.IsInWorld);
        Assert.Null(controller.CurrentSession);
        if (operations.Sessions.Count == 0)
        {
            Assert.Contains(
                point,
                new[] { ReentrantStopPoint.Reset, ReentrantStopPoint.Resolve });
        }
        else
        {
            Assert.Equal(1, operations.DisposeCounts[operations.Sessions[0]]);
        }
        Assert.Equal(
            point is ReentrantStopPoint.Reset
                or ReentrantStopPoint.Resolve
                or ReentrantStopPoint.Create
                ? 0
                : 1,
            host.DetachCount);
    }

    [Fact]
    public void ReentrantDuplicateStartIsDeferredWithoutDisturbingOuterAttempt()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);
        LiveSessionStartResult? nested = null;
        host.OnSelected = () => nested = controller.Start(LiveOptions(), host);

        LiveSessionStartResult outer = controller.Start(LiveOptions(), host);

        Assert.Equal(LiveSessionStartStatus.Deferred, nested!.Status);
        Assert.Equal(LiveSessionStartStatus.Connected, outer.Status);
        Assert.True(controller.IsInWorld);
        Assert.Single(operations.Sessions);
    }

    [Fact]
    public void ReentrantStartFromFailingEnteredCallbackCannotObserveUncommittedSession()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);
        LiveSessionStartResult? nested = null;
        host.OnEntered = () =>
        {
            nested = controller.Start(LiveOptions(), host);
            throw new InvalidOperationException("entered failure");
        };

        LiveSessionStartResult outer = controller.Start(LiveOptions(), host);

        Assert.Equal(LiveSessionStartStatus.Deferred, nested!.Status);
        Assert.Equal(LiveSessionStartStatus.Failed, outer.Status);
        Assert.False(controller.IsInWorld);
        Assert.Null(controller.CurrentSession);
        controller.Dispose();
    }

    [Theory]
    [InlineData("reconnect")]
    [InlineData("dispose")]
    public void ReentrantLifecycleRequestDuringActivationDoesNotLeakActiveCommands(
        string request)
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);
        bool requested = false;
        host.OnActivate = () =>
        {
            if (requested)
                return;
            requested = true;
            if (request == "reconnect")
                controller.Reconnect(LiveOptions(), host);
            else
                controller.Dispose();
        };

        LiveSessionStartResult outer = controller.Start(LiveOptions(), host);

        Assert.Equal(LiveSessionStartStatus.Deferred, outer.Status);
        Assert.False(host.CommandBuses[0].Active);
        Assert.Equal(request == "reconnect" ? 2 : 1, operations.Sessions.Count);
        Assert.Equal(request == "reconnect", controller.IsInWorld);
        controller.Dispose();
    }

    [Theory]
    [InlineData("connecting")]
    [InlineData("connected")]
    [InlineData("characters")]
    [InlineData("roster")]
    [InlineData("selected")]
    [InlineData("activate")]
    [InlineData("entered")]
    public void Start_CallbackFailureConvergesOffline(string phase)
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        switch (phase)
        {
            case "connecting": host.ThrowOnConnecting = true; break;
            case "connected": host.ThrowOnConnected = true; break;
            case "characters": operations.ThrowOnCharacters = true; break;
            case "roster": host.ThrowOnRoster = true; break;
            case "selected": host.ThrowOnSelected = true; break;
            case "activate": host.ThrowOnActivate = true; break;
            case "entered": host.ThrowOnEntered = true; break;
        }
        var controller = new LiveSessionController(operations);

        LiveSessionStartResult result = controller.Start(LiveOptions(), host);

        Assert.Equal(LiveSessionStartStatus.Failed, result.Status);
        Assert.Contains($"{phase} failure", result.Error!.ToString());
        Assert.False(controller.IsInWorld);
        Assert.Null(controller.CurrentSession);
        Assert.Equal(1, operations.DisposeCounts[operations.Sessions[0]]);
        controller.Dispose();
    }

    [Theory]
    [InlineData("deactivate")]
    [InlineData("events")]
    [InlineData("session")]
    [InlineData("detach")]
    [InlineData("reset")]
    public void Stop_RetriesExactFailedTeardownStageWithoutRepeatingCompletedWork(
        string phase)
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);
        controller.Start(LiveOptions(), host);
        WorldSession session = operations.Sessions[0];
        switch (phase)
        {
            case "deactivate": host.FailDeactivateOnce = true; break;
            case "events": host.FailEventDetachOnce = true; break;
            case "session": operations.FailDisposeOnce = true; break;
            case "detach": host.FailDetachOnce = true; break;
            case "reset": host.FailResetOnce = true; break;
        }

        Assert.Throws<InvalidOperationException>(controller.Stop);
        controller.Stop();

        Assert.False(controller.IsInWorld);
        Assert.Null(controller.CurrentSession);
        Assert.Equal(1, operations.DisposeCounts[session]);
        Assert.Equal(phase == "detach" ? 2 : 1, host.DetachCount);
        Assert.Equal(phase == "reset" ? 3 : 2, host.ResetCount);
        controller.Dispose();
    }

    [Fact]
    public void ReconnectRequestedDuringTickRunsAfterTickAndReplacesExactGeneration()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);
        controller.Start(LiveOptions(), host);
        WorldSession sessionA = operations.Sessions[0];
        bool requested = false;
        operations.OnTick = () =>
        {
            if (requested)
                return;
            requested = true;
            LiveSessionStartResult nested = controller.Reconnect(LiveOptions(), host);
            Assert.Equal(LiveSessionStartStatus.Deferred, nested.Status);
        };

        controller.Tick();

        Assert.True(controller.IsInWorld);
        Assert.Equal(2, operations.Sessions.Count);
        Assert.Equal(1, operations.DisposeCounts[sessionA]);
        Assert.Same(operations.Sessions[1], controller.CurrentSession);
    }

    [Fact]
    public void ResetFailureBlocksConstructionUntilRetryConverges()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls) { FailResetOnce = true };
        var controller = new LiveSessionController(operations);

        LiveSessionStartResult first = controller.Start(LiveOptions(), host);
        LiveSessionStartResult second = controller.Start(LiveOptions(), host);

        Assert.Equal(LiveSessionStartStatus.Failed, first.Status);
        Assert.Equal(LiveSessionStartStatus.Connected, second.Status);
        Assert.Equal(2, host.ResetCount);
        Assert.Single(operations.Sessions);
    }

    [Fact]
    public void FailedDetachRetainsRetiredScopeAndBlocksBFactoryUntilRetry()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls) { FailDetachOnce = true };
        var controller = new LiveSessionController(operations);
        controller.Start(LiveOptions(), host);
        WorldSession sessionA = operations.Sessions[0];

        LiveSessionStartResult first = controller.Reconnect(LiveOptions(), host);

        Assert.Equal(LiveSessionStartStatus.Failed, first.Status);
        Assert.Single(operations.Sessions);
        Assert.Equal(1, operations.DisposeCounts[sessionA]);
        Assert.False(controller.IsInWorld);

        LiveSessionStartResult retry = controller.Reconnect(LiveOptions(), host);

        Assert.Equal(LiveSessionStartStatus.Connected, retry.Status);
        Assert.Equal(2, operations.Sessions.Count);
        Assert.Equal(1, operations.DisposeCounts[sessionA]);
        Assert.Equal(2, host.DetachCount);
    }

    [Fact]
    public void TickFailureCleansScopeBeforeRethrowing()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls) { ThrowOnTick = true };
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);
        controller.Start(LiveOptions(), host);
        WorldSession session = operations.Sessions[0];

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(controller.Tick);

        Assert.False(controller.IsInWorld);
        Assert.Null(controller.CurrentSession);
        Assert.Equal(1, operations.DisposeCounts[session]);
        Assert.Contains(
            $"{nameof(TestOperations)}.{nameof(TestOperations.Tick)}",
            error.StackTrace,
            StringComparison.Ordinal);
    }


    [Fact]
    public void Tick_InvokesConfiguredAutoSaveHook_WithTheCurrentSessionWhileInWorld()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);
        controller.Start(LiveOptions(), host);
        WorldSession session = operations.Sessions[0];
        WorldSession? seen = null;
        controller.ConfigureAutoSaveTick(s => seen = s);

        controller.Tick();

        Assert.Same(session, seen);
    }

    [Fact]
    public void Tick_DoesNotInvokeAutoSaveHook_WhenNotInWorld()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var controller = new LiveSessionController(operations);
        bool invoked = false;
        controller.ConfigureAutoSaveTick(_ => invoked = true);

        // Never started — Tick() early-returns before any hook can fire.
        controller.Tick();

        Assert.False(invoked);
        Assert.Equal(0, operations.TickCount);
    }

    [Fact]
    public void Tick_AutoSaveHookThrowing_DoesNotFailTheTickOrTearDownTheSession()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);
        controller.Start(LiveOptions(), host);
        WorldSession session = operations.Sessions[0];
        controller.ConfigureAutoSaveTick(
            _ => throw new InvalidOperationException("send failed"));

        // Must not throw — a transient send error on a background auto-save
        // must not tear down the whole live session.
        controller.Tick();

        Assert.True(controller.IsInWorld);
        Assert.Same(session, controller.CurrentSession);
        Assert.False(operations.DisposeCounts.ContainsKey(session));
    }

    [Fact]
    public void Stop_InvokesConfiguredPreLogoffFlushHook_BeforeSessionDisposed()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);
        controller.Start(LiveOptions(), host);
        WorldSession session = operations.Sessions[0];
        WorldSession? seen = null;
        controller.ConfigurePreLogoffFlush(s =>
        {
            seen = s;
            calls.Add("pre-logoff-flush");
        });

        controller.Stop();

        Assert.Same(session, seen);
        int flushIndex = calls.IndexOf("pre-logoff-flush");
        int disposeIndex = calls.IndexOf("dispose-session");
        Assert.True(flushIndex >= 0);
        Assert.True(disposeIndex >= 0);
        Assert.True(flushIndex < disposeIndex);
    }

    [Fact]
    public void Stop_DoesNotInvokePreLogoffFlushHook_WhenNeverEnteredWorld()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls) { ThrowOnEnterWorld = true };
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);
        bool invoked = false;
        controller.ConfigurePreLogoffFlush(_ => invoked = true);

        controller.Start(LiveOptions(), host);

        Assert.False(invoked);
        Assert.False(controller.IsInWorld);
    }

    [Fact]
    public void Stop_PreLogoffFlushHookThrowing_DoesNotBlockGracefulTeardown()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);
        controller.Start(LiveOptions(), host);
        WorldSession session = operations.Sessions[0];
        controller.ConfigurePreLogoffFlush(
            _ => throw new InvalidOperationException("flush failed"));

        // Must not throw — a failed flush must not block the graceful-
        // shutdown sequence.
        controller.Stop();

        Assert.False(controller.IsInWorld);
        Assert.Null(controller.CurrentSession);
        Assert.Equal(1, operations.DisposeCounts[session]);
    }

    [Fact]
    public void DisposeIsIdempotentAndMakesOldCommandsInert()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);
        controller.Start(LiveOptions(), host);
        WorldSession session = operations.Sessions[0];
        TestCommandBus commands = host.CommandBuses[0];

        controller.Dispose();
        controller.Dispose();
        commands.Publish(new object());

        Assert.Equal(1, operations.DisposeCounts[session]);
        Assert.Equal(0, commands.PublishCount);
        Assert.False(controller.IsInWorld);
        Assert.Throws<ObjectDisposedException>(() => controller.Start(LiveOptions(), host));
    }

    [Fact]
    public void GenerationScopedStopAcknowledgesTheCompleteTeardownTransaction()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);
        controller.Start(LiveOptions(), host);
        RuntimeGenerationToken retired = controller.Generation;

        RuntimeTeardownAcknowledgement acknowledgement =
            controller.Stop(retired);

        Assert.True(acknowledgement.IsComplete);
        Assert.Equal(retired, acknowledgement.RetiredGeneration);
        Assert.Equal(controller.Generation, acknowledgement.CurrentGeneration);
        Assert.Equal(
            RuntimeTeardownStage.Complete,
            acknowledgement.CompletedStages);
    }

    [Fact]
    public void FailedStopAcknowledgesOnlyCompletedPrefixAndRetryDrainsSuffix()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls)
        {
            FailEventDetachOnce = true,
        };
        var controller = new LiveSessionController(operations);
        controller.Start(LiveOptions(), host);

        RuntimeTeardownAcknowledgement failed =
            controller.Stop(controller.Generation);

        Assert.Equal(RuntimeCommandStatus.Rejected, failed.Status);
        Assert.True(
            (failed.CompletedStages & RuntimeTeardownStage.CommandsInert) != 0);
        Assert.True(
            (failed.CompletedStages & RuntimeTeardownStage.InboundDetached) == 0);
        Assert.True(
            (failed.CompletedStages & RuntimeTeardownStage.TransportDisposed) == 0);

        RuntimeTeardownAcknowledgement retry =
            controller.Stop(failed.CurrentGeneration);

        Assert.True(retry.IsComplete);
        Assert.Equal(1, host.DeactivateCount);
        Assert.Equal(2, host.EventDetachCount);
        Assert.Single(operations.DisposeCounts);
    }


    [Fact]
    public void BeginCharacterLogOff_FlushesFirstThenSendsTheRequest()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);
        controller.ConfigurePreLogoffFlush(_ => calls.Add("flush"));
        Assert.Equal(
            LiveSessionStartStatus.Connected,
            controller.Start(LiveOptions(), host).Status);

        RuntimeCommandResult result =
            controller.BeginCharacterLogOff(controller.Generation);

        Assert.True(result.Accepted);
        Assert.Equal(1, operations.RequestCharacterLogOffCount);
        Assert.True(
            calls.IndexOf("flush") < calls.IndexOf("request-character-logoff"));
        // No teardown of any kind at request time (the single reset on
        // record is Start's own initial host reset).
        Assert.True(controller.IsInWorld);
        Assert.Equal(1, host.ResetCount);
    }

    [Fact]
    public void BeginCharacterLogOff_RefusesOutsideTheWorld()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);

        Assert.Equal(
            RuntimeCommandStatus.Inactive,
            controller.BeginCharacterLogOff(controller.Generation).Status);
        Assert.Equal(0, operations.RequestCharacterLogOffCount);

        Assert.Equal(
            LiveSessionStartStatus.Connected,
            controller.Start(LiveOptions(), host).Status);
        Assert.Equal(
            RuntimeCommandStatus.StaleGeneration,
            controller.BeginCharacterLogOff(default).Status);
    }

    [Fact]
    public void CompleteCharacterLogOff_ReturnsToSelectionOnTheLiveSession()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);
        Assert.Equal(
            LiveSessionStartStatus.Connected,
            controller.Start(LiveOptions(), host).Status);
        WorldSession session = operations.Sessions[0];
        RuntimeGenerationToken worldGeneration = controller.Generation;
        calls.Clear();

        RuntimeCommandResult result =
            controller.CompleteCharacterLogOff(worldGeneration);

        Assert.True(result.Accepted);
        // The retiring world generation's routes died first, then the host
        // reset THAT generation, then the same live session flipped back —
        // no transport disposal anywhere.
        Assert.Equal(
            [
                "deactivate", "detach-events", "detach-session", "reset",
                "return-to-character-select", "bind", "roster",
            ],
            calls);
        // ResetGenerations[0] is Start's own initial host reset; the
        // transaction's reset targets exactly the retiring world generation.
        Assert.Equal(2, host.ResetCount);
        Assert.Equal(worldGeneration, host.ResetGenerations[^1]);
        Assert.Empty(operations.DisposeCounts);
        Assert.False(controller.IsInWorld);
        Assert.Same(session, controller.CurrentSession);
        Assert.NotEqual(worldGeneration, controller.Generation);
        Assert.Equal(result.Generation, controller.Generation);

        RuntimeCharacterSelectionSnapshot snapshot =
            controller.CharacterSelectionState.View.Snapshot;
        Assert.Equal(
            RuntimeCharacterSelectionLifecycle.AwaitingSelection,
            snapshot.Lifecycle);
        Assert.Equal(controller.Generation, snapshot.Generation);
        Assert.Equal(2, host.Rosters.Count);

        RuntimeCommandResult enter = controller.Enter(controller.Generation);
        Assert.True(enter.Accepted);
        Assert.True(controller.IsInWorld);
        Assert.Equal(2, operations.EnterWorldCount);
        Assert.True(host.CommandBuses[^1].Active);
    }

    [Fact]
    public void NextLoginAutomaticallyEntersTheRememberedRosterCharacterOnce()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls)
        {
            Characters = new CharacterList.Parsed(
                0u,
                [
                    new CharacterList.Character(0x50000010u, "Alpha", 0u),
                    new CharacterList.Character(0x50000020u, "Beta", 0u),
                ],
                [],
                11,
                "Canonical",
                true,
                true),
        };
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);
        Assert.Equal(
            LiveSessionStartStatus.Connected,
            controller.Start(LiveOptions(), host).Status);
        Assert.Equal(0x50000010u, host.Selections[^1].CharacterId);

        Assert.True(controller.TrySetNextLogin(0x50000020u));
        Assert.Equal(0x50000020u, controller.NextLoginCharacterId);
        RuntimeCommandResult result =
            controller.CompleteCharacterLogOff(controller.Generation);

        Assert.True(result.Accepted);
        Assert.True(controller.IsInWorld);
        Assert.Equal(0u, controller.NextLoginCharacterId);
        Assert.Equal(2, operations.EnterWorldCount);
        Assert.Equal(0x50000020u, host.Selections[^1].CharacterId);
        Assert.Contains("enter:1", calls);
        Assert.True(controller.ClearNextLogin());
    }

    [Fact]
    public void CompleteCharacterLogOff_RefusalsAndFailureDegradeToStop()
    {
        var calls = new List<string>();
        var operations = new TestOperations(calls);
        var host = new TestHost(calls);
        var controller = new LiveSessionController(operations);

        Assert.Equal(
            RuntimeCommandStatus.Inactive,
            controller.CompleteCharacterLogOff(controller.Generation).Status);

        Assert.Equal(
            LiveSessionStartStatus.Connected,
            controller.Start(LiveOptions(), host).Status);
        Assert.Equal(
            RuntimeCommandStatus.StaleGeneration,
            controller.CompleteCharacterLogOff(default).Status);

        // A mid-transaction failure must not leave a half-reset session:
        // the transaction degrades to the full StopCore teardown.
        operations.ThrowOnReturnToCharacterSelect = true;
        WorldSession session = operations.Sessions[0];
        RuntimeCommandResult result =
            controller.CompleteCharacterLogOff(controller.Generation);
        Assert.Equal(RuntimeCommandStatus.Rejected, result.Status);
        Assert.False(controller.IsInWorld);
        Assert.Null(controller.CurrentSession);
        Assert.Equal(1, operations.DisposeCounts[session]);
    }

    private static LiveSessionConnectOptions LiveOptions(
        bool live = true,
        string? user = "user",
        LiveSessionCharacterSelector? selector = null,
        bool probe = false,
        bool awaitSelection = false) =>
        new(
            live,
            "127.0.0.1",
            9000,
            user ?? string.Empty,
            "password",
            selector,
            probe,
            awaitSelection);

    private static CharacterList.Parsed AvailableCharacters() => new(
        0u,
        [
            new CharacterList.Character(0x50000001u, "Grey", 10u),
            new CharacterList.Character(0x50000002u, "Ready", 0u),
        ],
        [new CharacterList.Character(0x50000003u, "Deleted", 0u)],
        11,
        "Canonical",
        true,
        true);
}
