using AcDream.Core.Net.Messages;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeAllegianceStateTests
{
    private const uint MonarchGuid = 0x50000001u;
    private const uint PatronGuid = 0x50000002u;
    private const uint SelfGuid = 0x50000003u;
    private const uint VassalAGuid = 0x50000004u;
    private const uint VassalBGuid = 0x50000005u;

    private static ClientCommandResponses.AllegianceMemberRecord Record(
        uint id,
        uint parent,
        string name,
        bool loggedIn = true) =>
        new(id, parent, loggedIn, name);

    private static ClientCommandResponses.AllegianceUpdate Update(uint rank = 3u) =>
        new(
            rank,
            TotalMembers: 5u,
            TotalVassals: 3u,
            RecordCount: 5,
            AllegianceName: "The Order",
            Monarch: Record(MonarchGuid, 0u, "Monarch"),
            Records:
            [
                Record(PatronGuid, MonarchGuid, "Patron"),
                Record(SelfGuid, PatronGuid, "Self"),
                Record(VassalAGuid, SelfGuid, "VassalA"),
                Record(VassalBGuid, SelfGuid, "VassalB"),
            ]);

    [Fact]
    public void ApplyUpdate_SeedsTheProfileAndArmsHasServerSeed()
    {
        var state = new RuntimeAllegianceState();
        Assert.False(state.HasServerSeed);

        state.ApplyUpdate(Update(rank: 7u));

        RuntimeAllegianceSnapshot snapshot = state.View.Snapshot;
        Assert.True(state.HasServerSeed);
        Assert.True(snapshot.HasServerSeed);
        Assert.True(snapshot.HasProfile);
        Assert.Equal(7u, snapshot.Rank);
        Assert.Equal("The Order", snapshot.AllegianceName);
        Assert.Equal(MonarchGuid, snapshot.MonarchGuid);
        Assert.True(snapshot.HasMonarch);
        Assert.Equal(4, snapshot.RecordCount);
    }

    [Fact]
    public void TryGetMonarchPatronAndVassals_WalkTheFlatRecordList()
    {
        var state = new RuntimeAllegianceState();
        state.ApplyUpdate(Update());

        Assert.True(state.View.TryGetMonarch(out RuntimeAllegianceMemberSnapshot monarch));
        Assert.Equal("Monarch", monarch.Name);

        Assert.True(state.View.TryGetPatron(SelfGuid, out RuntimeAllegianceMemberSnapshot patron));
        Assert.Equal(PatronGuid, patron.CharacterId);

        // The monarch has no patron.
        Assert.False(state.View.TryGetPatron(MonarchGuid, out _));

        var vassals = state.View.GetVassals(SelfGuid).ToList();
        Assert.Equal(2, vassals.Count);
        Assert.Contains(vassals, v => v.CharacterId == VassalAGuid);
        Assert.Contains(vassals, v => v.CharacterId == VassalBGuid);

        Assert.True(state.View.TryGetMember(VassalAGuid, out RuntimeAllegianceMemberSnapshot vassalA));
        Assert.Equal("VassalA", vassalA.Name);
        Assert.False(state.View.TryGetMember(0x99999999u, out _));
    }

    [Fact]
    public void GetVassals_VisitsSiblingsInReverseWireOrder()
    {
        // Lane C §4.4 point 3: each new record is PREPENDED to its parent's
        // vassal list on assembly — the record parsed LAST under a given
        // parent renders FIRST.
        var state = new RuntimeAllegianceState();
        state.ApplyUpdate(Update());

        var vassals = state.View.GetVassals(SelfGuid).ToList();
        Assert.Equal(VassalBGuid, vassals[0].CharacterId);
        Assert.Equal(VassalAGuid, vassals[1].CharacterId);
    }

    [Fact]
    public void Revision_IsMonotonicAcrossEveryEventKind()
    {
        var state = new RuntimeAllegianceState();
        long r0 = state.View.Snapshot.Revision;

        state.ApplyUpdate(Update());
        long r1 = state.View.Snapshot.Revision;
        Assert.True(r1 > r0);

        state.ApplyLoginNotification(VassalAGuid, isLoggedIn: true);
        long r2 = state.View.Snapshot.Revision;
        Assert.True(r2 > r1);

        state.ApplyUpdateDone(weenieError: 0u);
        long r3 = state.View.Snapshot.Revision;
        Assert.True(r3 > r2);

        state.ApplyUpdateAborted(weenieError: 0u);
        long r4 = state.View.Snapshot.Revision;
        Assert.True(r4 > r3);
    }

    [Fact]
    public void CaptureOwnership_ConvergesOnlyAfterDispose()
    {
        var state = new RuntimeAllegianceState();
        state.ApplyUpdate(Update());

        Assert.False(state.CaptureOwnership().IsConverged);

        state.Dispose();

        RuntimeAllegianceOwnershipSnapshot retired = state.CaptureOwnership();
        Assert.True(retired.IsConverged);
        Assert.True(retired.IsDisposed);
        Assert.False(retired.HasProfile);
        Assert.Equal(0, retired.RecordCount);
        Assert.False(state.HasServerSeed);
    }

    [Fact]
    public void MutatingAfterDispose_Throws()
    {
        var state = new RuntimeAllegianceState();
        state.Dispose();

        Assert.Throws<ObjectDisposedException>(() => state.ApplyUpdate(Update()));
        Assert.Throws<ObjectDisposedException>(
            () => state.ApplyLoginNotification(SelfGuid, true));
    }

    [Fact]
    public void ResetSession_ClearsProfileAndDropsHasServerSeed_MatchingOnEndCharacterSession()
    {
        var state = new RuntimeAllegianceState();
        state.ApplyUpdate(Update(rank: 7u));
        Assert.True(state.HasServerSeed);
        Assert.True(state.View.Snapshot.HasProfile);

        state.ResetSession();

        RuntimeAllegianceSnapshot snapshot = state.View.Snapshot;
        Assert.False(state.HasServerSeed);
        Assert.False(snapshot.HasServerSeed);
        Assert.False(snapshot.HasProfile);
        Assert.Equal(0u, snapshot.Rank);
        Assert.Equal(string.Empty, snapshot.AllegianceName);
        Assert.Equal(0u, snapshot.MonarchGuid);
        Assert.Equal(0, snapshot.RecordCount);
        Assert.False(state.IsDisposed);
    }

    [Fact]
    public void ResetSession_OnAnUnseededOwner_IsANoOpThatDoesNotBumpRevision()
    {
        var state = new RuntimeAllegianceState();
        long before = state.View.Snapshot.Revision;

        state.ResetSession();

        Assert.Equal(before, state.View.Snapshot.Revision);
    }

    [Fact]
    public void ResetSession_AfterDispose_IsANoOpAndDoesNotThrow()
    {
        var state = new RuntimeAllegianceState();
        state.ApplyUpdate(Update());
        state.Dispose();

        Exception? thrown = Xunit.Record.Exception(state.ResetSession);

        Assert.Null(thrown);
        Assert.False(state.View.Snapshot.HasProfile);
    }
}
