using AcDream.Core.Net.Messages;
using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.Runtime.Tests.Session;

public sealed class RuntimeCharacterSelectionStateTests
{
    private sealed class Collector : IRuntimeCharacterSelectionVisitor
    {
        public List<RuntimeCharacterSelectionEntry> Entries { get; } = [];

        public void Visit(in RuntimeCharacterSelectionEntry character) =>
            Entries.Add(character);
    }

    private sealed class Observer(
        Action<RuntimeCharacterSelectionDelta> onDelta)
        : IRuntimeCharacterSelectionObserver
    {
        public void OnCharacterSelectionChanged(
            in RuntimeCharacterSelectionDelta delta) =>
            onDelta(delta);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed) =>
            _timestamp += elapsed.Ticks;
    }

    [Fact]
    public void ApplyRoster_PortsRetailOrderFallbackAndDisabledButtonMatrix()
    {
        using var state = new RuntimeCharacterSelectionState();
        state.Begin(new RuntimeGenerationToken(7));

        state.ApplyRoster(Roster(
            new(0x50000001u, "Zulu", 1u),
            new(0x50000002u, "Bravo", 0u),
            new(0x50000003u, "Alpha", 0u),
            new(0x50000004u, "Able", 9u)));

        RuntimeCharacterSelectionSnapshot snapshot = state.Snapshot;
        Assert.Equal(RuntimeCharacterSelectionLifecycle.AwaitingSelection, snapshot.Lifecycle);
        Assert.Equal(0x50000002u, snapshot.HighlightedCharacterId);
        Assert.True(snapshot.Buttons.CanEnter);
        Assert.True(snapshot.Buttons.CanDelete);
        Assert.False(snapshot.Buttons.CanRestore);
        Assert.True(snapshot.Buttons.DeleteVisible);
        Assert.False(snapshot.Buttons.RestoreVisible);

        var collector = new Collector();
        state.View.Visit(collector);
        Assert.Equal(
            [
                (0x50000003u, 2, false),
                (0x50000002u, 1, false),
                (0x50000004u, 3, true),
                (0x50000001u, 0, true),
            ],
            collector.Entries.Select(entry =>
                (entry.CharacterId, entry.ActiveIndex, entry.IsPendingDelete)));

        Assert.True(state.TryHighlight(0x50000004u));
        snapshot = state.Snapshot;
        Assert.False(snapshot.Buttons.CanEnter);
        Assert.False(snapshot.Buttons.CanDelete);
        Assert.True(snapshot.Buttons.CanRestore);
        Assert.False(snapshot.Buttons.DeleteVisible);
        Assert.True(snapshot.Buttons.RestoreVisible);
    }

    [Fact]
    public void ApplyRoster_PreservesHighlightedGuidAcrossDeleteRefresh()
    {
        using var state = new RuntimeCharacterSelectionState();
        state.Begin(new RuntimeGenerationToken(3));
        state.ApplyRoster(Roster(
            new(0x50000001u, "One", 0u),
            new(0x50000002u, "Two", 0u)));
        Assert.True(state.TryHighlight(0x50000002u));
        Assert.True(state.TryRequestDelete(out uint requested));
        Assert.Equal(0x50000002u, requested);
        Assert.True(state.TryTakeDeleteConfirmation(
            out RuntimeCharacterSelectionEntry deleted,
            out string account));
        Assert.Equal(1, deleted.ActiveIndex);
        Assert.Equal("Canonical", account);

        state.ApplyDeleteAcknowledged();
        Assert.Equal(
            RuntimeCharacterSelectionOperation.DeleteAcknowledged,
            state.Snapshot.Operation);
        state.ApplyRoster(Roster(
            new(0x50000001u, "One", 0u),
            new(0x50000002u, "Two", 1u)));

        RuntimeCharacterSelectionSnapshot snapshot = state.Snapshot;
        Assert.Equal(0x50000002u, snapshot.HighlightedCharacterId);
        Assert.Equal(RuntimeCharacterSelectionOperation.None, snapshot.Operation);
        Assert.True(snapshot.Buttons.CanRestore);
        Assert.False(snapshot.Buttons.CanEnter);
    }

    [Fact]
    public void DeleteConfirmation_CancelIsIdempotentlyGenerationLocal()
    {
        using var state = new RuntimeCharacterSelectionState();
        state.Begin(new RuntimeGenerationToken(4));
        state.ApplyRoster(Roster(
            new LiveSessionRosterEntry(0x50000001u, "One", 0u)));

        Assert.True(state.TryRequestDelete(out _));
        Assert.Equal(0x50000001u, state.Snapshot.PendingDeleteCharacterId);
        Assert.False(state.TryHighlight(0x50000001u));
        Assert.False(state.BeginEnter(out _));
        Assert.True(state.Cancel());
        Assert.Equal(0u, state.Snapshot.PendingDeleteCharacterId);
        Assert.False(state.Cancel());

        state.Reset(new RuntimeGenerationToken(5));
        Assert.Equal(RuntimeCharacterSelectionLifecycle.Inactive, state.Snapshot.Lifecycle);
        Assert.Equal(0, state.Snapshot.RosterCount);
        Assert.Equal(new RuntimeGenerationToken(5), state.Snapshot.Generation);
    }

    [Fact]
    public void Restore_IsFireAndObserveAndSuccessUpdatesTheCanonicalEntry()
    {
        using var state = new RuntimeCharacterSelectionState();
        state.Begin(new RuntimeGenerationToken(11));
        state.ApplyRoster(Roster(
            new LiveSessionRosterEntry(0x50000001u, "Grey", 1u)));

        Assert.True(state.TryBeginRestore(out RuntimeCharacterSelectionEntry request));
        Assert.Equal(0x50000001u, request.CharacterId);
        Assert.Equal(
            RuntimeCharacterSelectionOperation.RestoreRequested,
            state.Snapshot.Operation);
        Assert.True(state.TryHighlight(0x50000001u));

        state.ApplyRestore(new CharacterRestore.Parsed(
            VerificationFlag: 1u,
            Guid: 0x50000001u,
            Name: "Restored",
            SecondsGreyedOut: 0u));

        Assert.True(state.View.TryGet(0x50000001u, out var restored));
        Assert.Equal("Restored", restored.Name);
        Assert.False(restored.IsPendingDelete);
        Assert.True(state.Snapshot.Buttons.CanEnter);
        Assert.Equal(
            RuntimeCharacterSelectionOperation.RestoreSucceeded,
            state.Snapshot.Operation);
    }

    [Fact]
    public void DelayedFlagOnlyRestoreResponse_CannotCompleteNewerRequest()
    {
        var time = new ManualTimeProvider();
        using var state = new RuntimeCharacterSelectionState(time);
        state.Begin(new RuntimeGenerationToken(12));
        state.ApplyRoster(Roster(
            new(0x50000001u, "First", 1u),
            new(0x50000002u, "Second", 1u)));

        Assert.True(state.TryBeginRestore(out _));
        Assert.True(state.TryHighlight(0x50000002u));
        Assert.False(state.TryBeginRestore(out _));
        time.Advance(RuntimeCharacterSelectionState.RestoreCorrelationTimeout);
        Assert.True(state.SweepRestoreCorrelation());
        Assert.True(state.TryBeginRestore(out _));
        long revision = state.Snapshot.Revision;
        state.ApplyRestore(new CharacterRestore.Parsed(
            VerificationFlag: 2u,
            Guid: null,
            Name: null,
            SecondsGreyedOut: null));

        Assert.Equal(
            RuntimeCharacterSelectionOperation.RestoreRequested,
            state.Snapshot.Operation);
        Assert.Equal(0x50000002u, state.Snapshot.LastRestoreRequestedCharacterId);
        Assert.Equal(revision, state.Snapshot.Revision);
        Assert.True(state.View.TryGet(0x50000001u, out var first));
        Assert.True(first.IsPendingDelete);
        Assert.True(state.View.TryGet(0x50000002u, out var second));
        Assert.True(second.IsPendingDelete);
        Assert.False(state.Snapshot.Buttons.CanRestore);
    }

    [Fact]
    public void NoReplyRestoreTimeout_ReleasesOnlyRestoreGate()
    {
        var time = new ManualTimeProvider();
        using var state = new RuntimeCharacterSelectionState(time);
        state.Begin(new RuntimeGenerationToken(13));
        state.ApplyRoster(Roster(
            new(0x50000001u, "Pending", 1u),
            new(0x50000002u, "Ready", 0u)));

        Assert.True(state.TryHighlight(0x50000001u));
        Assert.True(state.TryBeginRestore(out _));
        Assert.False(state.Snapshot.Buttons.CanRestore);
        Assert.True(state.TryHighlight(0x50000002u));
        Assert.True(state.BeginEnter(out var ready));
        Assert.Equal(0x50000002u, ready.CharacterId);

        state.ReturnToSelection();
        Assert.True(state.TryHighlight(0x50000001u));
        Assert.True(state.TryBeginRestore(out _));
        time.Advance(RuntimeCharacterSelectionState.RestoreCorrelationTimeout);
        Assert.True(state.SweepRestoreCorrelation());
        Assert.Equal(
            RuntimeCharacterSelectionOperation.None,
            state.Snapshot.Operation);
        Assert.True(state.Snapshot.Buttons.CanRestore);
    }

    [Fact]
    public void CharacterErrors_MapKnownAndUnknownButNeverPublishNumErrorsSentinel()
    {
        using var state = new RuntimeCharacterSelectionState();
        state.Begin(new RuntimeGenerationToken(2));
        state.ApplyRoster(Roster(
            new LiveSessionRosterEntry(0x50000001u, "One", 0u)));
        var deltas = new List<RuntimeCharacterSelectionDelta>();
        using IDisposable subscription = state.View.Subscribe(
            new Observer(deltas.Add));

        state.ApplyError(new CharacterError.Parsed(
            (uint)CharacterError.Code.Delete));
        RuntimeCharacterSelectionError known =
            Assert.IsType<RuntimeCharacterSelectionError>(state.Snapshot.Error);
        Assert.Equal(CharacterError.Code.Delete, known.Code);
        Assert.Contains("delete", known.Message, StringComparison.OrdinalIgnoreCase);

        Assert.True(state.Cancel());
        state.ApplyError(new CharacterError.Parsed(0xDEADBEEFu));
        RuntimeCharacterSelectionError unknown =
            Assert.IsType<RuntimeCharacterSelectionError>(state.Snapshot.Error);
        Assert.Contains("0xDEADBEEF", unknown.Message, StringComparison.Ordinal);

        long revision = state.Snapshot.Revision;
        int count = deltas.Count;
        state.ApplyError(new CharacterError.Parsed(
            (uint)CharacterError.Code.NumErrors));
        Assert.Equal(revision, state.Snapshot.Revision);
        Assert.Equal(count, deltas.Count);
        Assert.Equal(0xDEADBEEFu, state.Snapshot.Error!.Value.RawCode);
    }

    [Fact]
    public void Deltas_AreMonotonicAndReentrantMutationsStayOrdered()
    {
        using var state = new RuntimeCharacterSelectionState();
        state.Begin(new RuntimeGenerationToken(9));
        var deltas = new List<RuntimeCharacterSelectionDelta>();
        var observer = new Observer(delta =>
        {
            deltas.Add(delta);
            if (delta.Kind == RuntimeCharacterSelectionDeltaKind.RosterChanged)
                Assert.True(state.TryHighlight(0x50000002u));
        });
        using IDisposable subscription = state.View.Subscribe(observer);

        state.ApplyRoster(Roster(
            new(0x50000001u, "Ready", 0u),
            new(0x50000002u, "Grey", 1u)));

        Assert.Equal(
            [
                RuntimeCharacterSelectionDeltaKind.RosterChanged,
                RuntimeCharacterSelectionDeltaKind.HighlightChanged,
            ],
            deltas.Select(delta => delta.Kind));
        Assert.True(deltas[0].Sequence < deltas[1].Sequence);
        Assert.All(
            deltas,
            delta => Assert.Equal(new RuntimeGenerationToken(9), delta.Generation));
    }

    [Fact]
    public void ApplyWorldName_PopulatesSnapshot_IndependentOfRoster()
    {
        using var state = new RuntimeCharacterSelectionState();
        state.Begin(new RuntimeGenerationToken(5));

        Assert.Equal(string.Empty, state.Snapshot.WorldName);

        state.ApplyWorldName("sawato");
        Assert.Equal("sawato", state.Snapshot.WorldName);

        state.ApplyRoster(Roster(
            new LiveSessionRosterEntry(0x50000001u, "One", 0u)));
        Assert.Equal("sawato", state.Snapshot.WorldName);
        Assert.Equal(0x50000001u, state.Snapshot.HighlightedCharacterId);
    }

    [Fact]
    public void ApplyWorldName_UnchangedValue_DoesNotBumpRevisionOrPublish()
    {
        using var state = new RuntimeCharacterSelectionState();
        state.Begin(new RuntimeGenerationToken(6));
        state.ApplyWorldName("sawato");
        var deltas = new List<RuntimeCharacterSelectionDelta>();
        using IDisposable subscription = state.View.Subscribe(
            new Observer(deltas.Add));
        long revision = state.Snapshot.Revision;

        state.ApplyWorldName("sawato");

        Assert.Equal(revision, state.Snapshot.Revision);
        Assert.Empty(deltas);
    }

    [Fact]
    public void Reset_ClearsWorldName()
    {
        using var state = new RuntimeCharacterSelectionState();
        state.Begin(new RuntimeGenerationToken(8));
        state.ApplyWorldName("sawato");

        state.Reset(new RuntimeGenerationToken(9));

        Assert.Equal(string.Empty, state.Snapshot.WorldName);
    }

    private static LiveSessionRosterReport Roster(
        params LiveSessionRosterEntry[] entries) =>
        new("Canonical", 11, entries);
}
