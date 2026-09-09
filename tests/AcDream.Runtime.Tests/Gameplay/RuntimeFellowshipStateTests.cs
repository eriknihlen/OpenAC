using AcDream.Core.Net.Messages;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeFellowshipStateTests
{
    private const uint SelfGuid = 0x50000001u;
    private const uint LeaderGuid = 0x50000002u;
    private const uint OtherGuid = 0x50000003u;

    private static GameEvents.FellowMember Member(
        uint guid,
        string name = "Fellow",
        uint currentHealth = 100u,
        uint shareLoot = 0u) =>
        new(
            guid,
            CpCache: 0u,
            LumCache: 0u,
            Level: 10u,
            MaxHealth: 100u,
            MaxStamina: 100u,
            MaxMana: 100u,
            CurrentHealth: currentHealth,
            CurrentStamina: 100u,
            CurrentMana: 100u,
            ShareLoot: shareLoot,
            Name: name);

    private static GameEvents.FellowshipFullUpdate FullUpdate(
        params GameEvents.FellowMember[] members) =>
        new(
            members,
            Name: "The Fellows",
            LeaderGuid: LeaderGuid,
            ShareXp: true,
            EvenXpSplit: false,
            OpenFellow: true,
            Locked: false,
            Departed: []);

    [Fact]
    public void ApplyFullUpdate_ReplacesTheWholeRosterAndFlags()
    {
        var state = new RuntimeFellowshipState();

        state.ApplyFullUpdate(FullUpdate(
            Member(SelfGuid, "Self"),
            Member(LeaderGuid, "Leader"),
            Member(OtherGuid, "Other")));

        RuntimeFellowshipSnapshot snapshot = state.View.Snapshot;
        Assert.True(snapshot.IsInFellowship);
        Assert.Equal("The Fellows", snapshot.Name);
        Assert.Equal(LeaderGuid, snapshot.LeaderGuid);
        Assert.True(snapshot.ShareXp);
        Assert.False(snapshot.EvenXpSplit);
        Assert.True(snapshot.IsOpen);
        Assert.False(snapshot.Locked);
        Assert.Equal(3, snapshot.MemberCount);
        Assert.True(state.View.TryGetMember(OtherGuid, out RuntimeFellowMemberSnapshot other));
        Assert.Equal("Other", other.Name);

        // A SECOND full update REPLACES, not merges — the departed member
        // must disappear.
        state.ApplyFullUpdate(FullUpdate(Member(SelfGuid, "Self")));
        Assert.Equal(1, state.View.Snapshot.MemberCount);
        Assert.False(state.View.TryGetMember(OtherGuid, out _));
    }

    [Fact]
    public void ApplyUpdateFellow_UpsertsExactlyOneMemberByGuid()
    {
        var state = new RuntimeFellowshipState();
        state.ApplyFullUpdate(FullUpdate(
            Member(SelfGuid, "Self", currentHealth: 100u),
            Member(LeaderGuid, "Leader")));

        state.ApplyUpdateFellow(new GameEvents.FellowshipUpdateFellow(
            SelfGuid,
            Member(SelfGuid, "Self", currentHealth: 42u),
            UpdateType: 3u));

        Assert.True(state.View.TryGetMember(SelfGuid, out RuntimeFellowMemberSnapshot self));
        Assert.Equal(42u, self.CurrentHealth);
        // The other member is untouched.
        Assert.True(state.View.TryGetMember(LeaderGuid, out RuntimeFellowMemberSnapshot leader));
        Assert.Equal("Leader", leader.Name);
        Assert.Equal(2, state.View.Snapshot.MemberCount);
    }

    [Fact]
    public void ApplyUpdateFellow_BeforeAnyFullUpdate_IsANoOp()
    {
        var state = new RuntimeFellowshipState();
        long before = state.View.Snapshot.Revision;

        state.ApplyUpdateFellow(new GameEvents.FellowshipUpdateFellow(
            SelfGuid, Member(SelfGuid), UpdateType: 1u));

        Assert.Equal(before, state.View.Snapshot.Revision);
        Assert.False(state.View.Snapshot.IsInFellowship);
    }

    [Theory]
    [InlineData(false)] // Quit
    [InlineData(true)]  // Dismiss
    public void SelfRemoval_ClearsTheWholeSnapshot(bool viaDismiss)
    {
        var state = new RuntimeFellowshipState();
        state.ApplyFullUpdate(FullUpdate(
            Member(SelfGuid),
            Member(LeaderGuid),
            Member(OtherGuid)));

        if (viaDismiss)
            state.ApplyDismiss(SelfGuid, SelfGuid);
        else
            state.ApplyQuit(SelfGuid, SelfGuid);

        RuntimeFellowshipSnapshot snapshot = state.View.Snapshot;
        Assert.False(snapshot.IsInFellowship);
        Assert.Equal(0, snapshot.MemberCount);
        Assert.Equal(string.Empty, snapshot.Name);
        Assert.Equal(0u, snapshot.LeaderGuid);
    }

    [Theory]
    [InlineData(false)] // Quit
    [InlineData(true)]  // Dismiss
    public void OtherMemberRemoval_RemovesOnlyThatMember(bool viaDismiss)
    {
        var state = new RuntimeFellowshipState();
        state.ApplyFullUpdate(FullUpdate(
            Member(SelfGuid),
            Member(LeaderGuid),
            Member(OtherGuid)));

        if (viaDismiss)
            state.ApplyDismiss(OtherGuid, SelfGuid);
        else
            state.ApplyQuit(OtherGuid, SelfGuid);

        RuntimeFellowshipSnapshot snapshot = state.View.Snapshot;
        Assert.True(snapshot.IsInFellowship);
        Assert.Equal(2, snapshot.MemberCount);
        Assert.False(state.View.TryGetMember(OtherGuid, out _));
        Assert.True(state.View.TryGetMember(SelfGuid, out _));
    }

    [Fact]
    public void ApplyDisband_AlwaysClears()
    {
        var state = new RuntimeFellowshipState();
        state.ApplyFullUpdate(FullUpdate(Member(SelfGuid), Member(LeaderGuid)));

        state.ApplyDisband();

        Assert.False(state.View.Snapshot.IsInFellowship);
        Assert.Equal(0, state.View.Snapshot.MemberCount);
    }

    [Fact]
    public void RequiresLeaderHandoffBeforeQuit_OnlyWhenSelfIsLeaderAndNotDisbanding()
    {
        var state = new RuntimeFellowshipState();
        state.ApplyFullUpdate(FullUpdate(
            Member(SelfGuid),
            Member(LeaderGuid),
            Member(OtherGuid)));

        // Not the leader (SelfGuid isn't the leader here) — no handoff.
        Assert.False(state.RequiresLeaderHandoffBeforeQuit(SelfGuid, disband: false, out _));

        // The leader disbanding — no handoff needed (the fellowship dissolves).
        Assert.False(state.RequiresLeaderHandoffBeforeQuit(LeaderGuid, disband: true, out _));

        // The leader quitting WITHOUT disbanding — hand off to a non-leader fellow.
        Assert.True(state.RequiresLeaderHandoffBeforeQuit(LeaderGuid, disband: false, out uint newLeader));
        Assert.NotEqual(LeaderGuid, newLeader);
        Assert.True(newLeader is SelfGuid or OtherGuid);

        // Sole member (no one else to hand off to) — no handoff, even as leader.
        var solo = new RuntimeFellowshipState();
        solo.ApplyFullUpdate(new GameEvents.FellowshipFullUpdate(
            [Member(LeaderGuid)],
            "Solo",
            LeaderGuid,
            ShareXp: false,
            EvenXpSplit: false,
            OpenFellow: false,
            Locked: false,
            Departed: []));
        Assert.False(solo.RequiresLeaderHandoffBeforeQuit(LeaderGuid, disband: false, out _));
    }

    [Fact]
    public void Revision_IsMonotonicAcrossEveryMutationKind()
    {
        var state = new RuntimeFellowshipState();
        long r0 = state.View.Snapshot.Revision;

        state.ApplyFullUpdate(FullUpdate(Member(SelfGuid), Member(LeaderGuid)));
        long r1 = state.View.Snapshot.Revision;
        Assert.True(r1 > r0);

        state.ApplyUpdateFellow(new GameEvents.FellowshipUpdateFellow(
            SelfGuid, Member(SelfGuid, currentHealth: 1u), UpdateType: 3u));
        long r2 = state.View.Snapshot.Revision;
        Assert.True(r2 > r1);

        state.ApplyQuit(LeaderGuid, SelfGuid);
        long r3 = state.View.Snapshot.Revision;
        Assert.True(r3 > r2);

        state.ApplyDisband();
        long r4 = state.View.Snapshot.Revision;
        Assert.True(r4 > r3);
    }

    [Fact]
    public void ShareLoot_ReadsTheRawWireBitAsNonZero_NeverEqualsOne()
    {
        var state = new RuntimeFellowshipState();
        state.ApplyFullUpdate(FullUpdate(Member(SelfGuid, shareLoot: 0x10u)));

        Assert.True(state.View.TryGetMember(SelfGuid, out RuntimeFellowMemberSnapshot member));
        Assert.True(member.ShareLoot);
    }

    [Fact]
    public void CaptureOwnership_ConvergesOnlyAfterDisposeAndEmptyRoster()
    {
        var state = new RuntimeFellowshipState();
        state.ApplyFullUpdate(FullUpdate(Member(SelfGuid), Member(LeaderGuid)));

        Assert.False(state.CaptureOwnership().IsConverged);

        state.Dispose();

        RuntimeFellowshipOwnershipSnapshot retired = state.CaptureOwnership();
        Assert.True(retired.IsConverged);
        Assert.True(retired.IsDisposed);
        Assert.False(retired.IsInFellowship);
        Assert.Equal(0, retired.MemberCount);
    }

    [Fact]
    public void ResetSession_ClearsSessionScopedRoster_MatchingTheExternalContainerPrecedent()
    {
        var state = new RuntimeFellowshipState();
        state.ApplyFullUpdate(FullUpdate(Member(SelfGuid), Member(LeaderGuid)));

        state.ResetSession();

        RuntimeFellowshipSnapshot snapshot = state.View.Snapshot;
        Assert.False(snapshot.IsInFellowship);
        Assert.Equal(0, snapshot.MemberCount);
        Assert.False(state.IsDisposed);
    }

    [Fact]
    public void MutatingAfterDispose_Throws()
    {
        var state = new RuntimeFellowshipState();
        state.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => state.ApplyFullUpdate(FullUpdate(Member(SelfGuid))));
        Assert.Throws<ObjectDisposedException>(
            () => state.ApplyQuit(SelfGuid, SelfGuid));
        Assert.Throws<ObjectDisposedException>(state.ApplyDisband);
    }

    [Fact]
    public void ResetSession_AfterDispose_IsANoOpAndDoesNotThrow()
    {
        var state = new RuntimeFellowshipState();
        state.ApplyFullUpdate(FullUpdate(Member(SelfGuid)));
        state.Dispose();

        Exception? thrown = Record.Exception(state.ResetSession);

        Assert.Null(thrown);
        Assert.False(state.View.Snapshot.IsInFellowship);
    }

    // ── SHOULD-FIX 3 (mechanism review): RecalculateEvenXPSplitting ────────

    [Theory]
    [InlineData(10u, 10u, true)]  // within +/-5 of the leader -> stays even
    [InlineData(10u, 20u, false)] // 10 spread, minLevel<50 -> not even
    [InlineData(60u, 200u, true)]
    public void ApplyUpdateFellow_RecalculatesEvenXpSplit_MatchingRetailWideSpreadRule(
        uint memberLevel,
        uint leaderLevel,
        bool expectedEvenXpSplit)
    {
        var state = new RuntimeFellowshipState();
        state.ApplyFullUpdate(new GameEvents.FellowshipFullUpdate(
            [Member(LeaderGuid)],
            "The Fellows",
            LeaderGuid,
            ShareXp: true,
            EvenXpSplit: true,
            OpenFellow: true,
            Locked: false,
            Departed: []));
        // Overwrite the leader's own level to the test's value via a
        // same-guid upsert (an existing-member refresh, never gated).
        state.ApplyUpdateFellow(new GameEvents.FellowshipUpdateFellow(
            LeaderGuid,
            Member(LeaderGuid) with { Level = leaderLevel },
            UpdateType: 3u));

        state.ApplyUpdateFellow(new GameEvents.FellowshipUpdateFellow(
            SelfGuid,
            Member(SelfGuid) with { Level = memberLevel },
            UpdateType: 1u));

        Assert.Equal(expectedEvenXpSplit, state.View.Snapshot.EvenXpSplit);
    }

    [Fact]
    public void ApplyUpdateFellow_ShareXpOff_LeavesEvenXpSplitUntouched()
    {
        var state = new RuntimeFellowshipState();
        state.ApplyFullUpdate(new GameEvents.FellowshipFullUpdate(
            [Member(LeaderGuid)],
            "The Fellows",
            LeaderGuid,
            ShareXp: false,
            EvenXpSplit: true, // deliberately mismatched with ShareXp: false
            OpenFellow: true,
            Locked: false,
            Departed: []));

        state.ApplyUpdateFellow(new GameEvents.FellowshipUpdateFellow(
            SelfGuid, Member(SelfGuid) with { Level = 999u }, UpdateType: 1u));

        Assert.True(state.View.Snapshot.EvenXpSplit);
    }

    [Fact]
    public void ApplyFullUpdate_NeverRecomputesEvenXpSplit_StoresTheWireFlagVerbatim()
    {
        var state = new RuntimeFellowshipState();

        state.ApplyFullUpdate(new GameEvents.FellowshipFullUpdate(
            [
                Member(LeaderGuid) with { Level = 5u },
                Member(SelfGuid) with { Level = 500u },
            ],
            "The Fellows",
            LeaderGuid,
            ShareXp: true,
            EvenXpSplit: true, // server says even despite the huge spread
            OpenFellow: true,
            Locked: false,
            Departed: []));

        Assert.True(state.View.Snapshot.EvenXpSplit);
    }

    // ── SHOULD-FIX 4 (mechanism review): locked/departed admission gate ───

    [Fact]
    public void ApplyUpdateFellow_LockedFellowship_RefusesABrandNewGuidNotInDeparted()
    {
        var state = new RuntimeFellowshipState();
        state.ApplyFullUpdate(new GameEvents.FellowshipFullUpdate(
            [Member(LeaderGuid)],
            "The Fellows",
            LeaderGuid,
            ShareXp: false,
            EvenXpSplit: false,
            OpenFellow: false,
            Locked: true,
            Departed: []));
        long before = state.View.Snapshot.Revision;

        state.ApplyUpdateFellow(new GameEvents.FellowshipUpdateFellow(
            OtherGuid, Member(OtherGuid), UpdateType: 1u));

        Assert.False(state.View.TryGetMember(OtherGuid, out _));
        Assert.Equal(1, state.View.Snapshot.MemberCount);
        Assert.Equal(before, state.View.Snapshot.Revision);
    }

    [Fact]
    public void ApplyUpdateFellow_LockedFellowship_AdmitsAGuidThatDepartedWithinTheGraceWindow()
    {
        var clock = new ManualTimeProvider();
        var state = new RuntimeFellowshipState(clock);
        state.ApplyFullUpdate(new GameEvents.FellowshipFullUpdate(
            [Member(LeaderGuid)],
            "The Fellows",
            LeaderGuid,
            ShareXp: false,
            EvenXpSplit: false,
            OpenFellow: false,
            Locked: true,
            Departed: [new GameEvents.FellowshipDepartedMember(
                OtherGuid, (int)clock.GetUtcNow().ToUnixTimeSeconds())]));
        clock.Advance(TimeSpan.FromSeconds(899));

        state.ApplyUpdateFellow(new GameEvents.FellowshipUpdateFellow(
            OtherGuid, Member(OtherGuid), UpdateType: 1u));

        Assert.True(state.View.TryGetMember(OtherGuid, out _));
        Assert.Equal(2, state.View.Snapshot.MemberCount);
    }

    [Fact]
    public void ApplyUpdateFellow_LockedFellowship_RefusesAGuidThatDepartedOutsideTheGraceWindow()
    {
        var clock = new ManualTimeProvider();
        var state = new RuntimeFellowshipState(clock);
        state.ApplyFullUpdate(new GameEvents.FellowshipFullUpdate(
            [Member(LeaderGuid)],
            "The Fellows",
            LeaderGuid,
            ShareXp: false,
            EvenXpSplit: false,
            OpenFellow: false,
            Locked: true,
            Departed: [new GameEvents.FellowshipDepartedMember(
                OtherGuid, (int)clock.GetUtcNow().ToUnixTimeSeconds())]));
        clock.Advance(TimeSpan.FromSeconds(901));

        state.ApplyUpdateFellow(new GameEvents.FellowshipUpdateFellow(
            OtherGuid, Member(OtherGuid), UpdateType: 1u));

        Assert.False(state.View.TryGetMember(OtherGuid, out _));
        Assert.Equal(1, state.View.Snapshot.MemberCount);
    }

    [Fact]
    public void ApplyUpdateFellow_LockedFellowship_NeverGatesAnExistingMembersOwnRefresh()
    {
        var state = new RuntimeFellowshipState();
        state.ApplyFullUpdate(new GameEvents.FellowshipFullUpdate(
            [Member(LeaderGuid), Member(SelfGuid, currentHealth: 100u)],
            "The Fellows",
            LeaderGuid,
            ShareXp: false,
            EvenXpSplit: false,
            OpenFellow: false,
            Locked: true,
            Departed: []));

        state.ApplyUpdateFellow(new GameEvents.FellowshipUpdateFellow(
            SelfGuid, Member(SelfGuid, currentHealth: 42u), UpdateType: 3u));

        Assert.True(state.View.TryGetMember(SelfGuid, out RuntimeFellowMemberSnapshot self));
        Assert.Equal(42u, self.CurrentHealth);
    }


    [Fact]
    public void GetMembers_ReturnsEveryCurrentMember()
    {
        var state = new RuntimeFellowshipState();
        state.ApplyFullUpdate(FullUpdate(
            Member(SelfGuid, "Self"), Member(LeaderGuid, "Leader"), Member(OtherGuid, "Other")));

        var guids = state.View.GetMembers().Select(m => m.Guid).ToHashSet();

        Assert.Equal(3, guids.Count);
        Assert.Contains(SelfGuid, guids);
        Assert.Contains(LeaderGuid, guids);
        Assert.Contains(OtherGuid, guids);
    }

    [Fact]
    public void GetMembers_EmptyWhenNotInAFellowship()
    {
        var state = new RuntimeFellowshipState();

        Assert.Empty(state.View.GetMembers());
    }

    [Fact]
    public void GetMembers_ReflectsAnIncrementalUpsert()
    {
        var state = new RuntimeFellowshipState();
        state.ApplyFullUpdate(FullUpdate(Member(SelfGuid), Member(LeaderGuid)));

        state.ApplyUpdateFellow(new GameEvents.FellowshipUpdateFellow(
            OtherGuid, Member(OtherGuid, "Other"), UpdateType: 1u));

        Assert.Equal(3, state.View.GetMembers().Count());
        Assert.Contains(state.View.GetMembers(), m => m.Guid == OtherGuid && m.Name == "Other");
    }

    [Fact]
    public void GetMembers_ReflectsRemovalAfterDismiss()
    {
        var state = new RuntimeFellowshipState();
        state.ApplyFullUpdate(FullUpdate(Member(SelfGuid), Member(LeaderGuid), Member(OtherGuid)));

        state.ApplyDismiss(OtherGuid, SelfGuid);

        Assert.DoesNotContain(state.View.GetMembers(), m => m.Guid == OtherGuid);
        Assert.Equal(2, state.View.GetMembers().Count());
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 8, 12, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
