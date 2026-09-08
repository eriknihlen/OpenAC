using System;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class FellowshipEventsTests
{
    // ── 0x02BE FellowshipFullUpdate ──────────────────────────────────────

    [Fact]
    public void ParseFellowshipFullUpdate_RoundTrips_TwoMembersOneDeparted()
    {
        byte[] wire = new AceWireWriter()
            .Write((ushort)2)   // memberCount
            .Write((ushort)16)
            // member 1
            .Write(0x50000001u)
            .Write((uint)100)
            .Write((uint)50)    // lumCache
            .Write((uint)20)    // level
            .Write((uint)100)   // maxHealth
            .Write((uint)100)   // maxStamina
            .Write((uint)100)   // maxMana
            .Write((uint)80)
            .Write((uint)90)
            .Write((uint)70)
            .Write((uint)0x10)
            .WriteString16L("Leader")
            // member 2
            .Write(0x50000002u)
            .Write((uint)0).Write((uint)0).Write((uint)15)
            .Write((uint)90).Write((uint)90).Write((uint)90)
            .Write((uint)90).Write((uint)90).Write((uint)90)
            .Write((uint)0)
            .WriteString16L("Second")
            // fellowship-level fields
            .WriteString16L("TestFellowship")
            .Write(0x50000001u) // leaderGuid
            .Write((uint)1)     // shareXp
            .Write((uint)1)     // evenXpSplit
            .Write((uint)0)     // openFellow
            .Write((uint)0)     // locked
            .Write((ushort)1)   // departedCount
            .Write((ushort)32)  // numBuckets
            .Write(0x50000099u)
            .Write(1700000000)
            .ToArray();

        var update = GameEvents.ParseFellowshipFullUpdate(wire);

        Assert.NotNull(update);
        Assert.Equal(2, update.Value.Members.Count);
        Assert.Equal(0x50000001u, update.Value.Members[0].Guid);
        Assert.Equal("Leader", update.Value.Members[0].Name);
        Assert.Equal(100u, update.Value.Members[0].CpCache);
        Assert.Equal(50u, update.Value.Members[0].LumCache);
        Assert.Equal(20u, update.Value.Members[0].Level);
        Assert.Equal(80u, update.Value.Members[0].CurrentHealth);
        Assert.Equal(0x10u, update.Value.Members[0].ShareLoot);
        Assert.Equal("Second", update.Value.Members[1].Name);
        Assert.Equal("TestFellowship", update.Value.Name);
        Assert.Equal(0x50000001u, update.Value.LeaderGuid);
        Assert.True(update.Value.ShareXp);
        Assert.True(update.Value.EvenXpSplit);
        Assert.False(update.Value.OpenFellow);
        Assert.False(update.Value.Locked);
        Assert.Single(update.Value.Departed);
        Assert.Equal(0x50000099u, update.Value.Departed[0].Guid);
        Assert.Equal(1700000000, update.Value.Departed[0].DepartedTimestamp);
    }

    [Fact]
    public void ParseFellowshipFullUpdate_ShareLootIsRawNotBool_D5()
    {
        byte[] wire = BuildSingleMemberFullUpdate(shareLoot: 2u);

        var update = GameEvents.ParseFellowshipFullUpdate(wire);

        Assert.NotNull(update);
        Assert.Equal(2u, update.Value.Members[0].ShareLoot);
        Assert.NotEqual(1u, update.Value.Members[0].ShareLoot);
    }

    [Fact]
    public void ParseFellowshipFullUpdate_NoMembersNoDeparted_ParsesEmpty()
    {
        byte[] wire = new AceWireWriter()
            .Write((ushort)0).Write((ushort)16)
            .WriteString16L("Empty")
            .Write((uint)0).Write((uint)0).Write((uint)0).Write((uint)0).Write((uint)0)
            .Write((ushort)0).Write((ushort)32)
            .ToArray();

        var update = GameEvents.ParseFellowshipFullUpdate(wire);

        Assert.NotNull(update);
        Assert.Empty(update.Value.Members);
        Assert.Empty(update.Value.Departed);
        Assert.Equal("Empty", update.Value.Name);
    }

    [Fact]
    public void ParseFellowshipFullUpdate_TruncatedPayload_ReturnsNull()
    {
        byte[] wire = new AceWireWriter().Write((ushort)1).ToArray(); // missing everything else
        Assert.Null(GameEvents.ParseFellowshipFullUpdate(wire));
    }

    private static byte[] BuildSingleMemberFullUpdate(uint shareLoot)
    {
        return new AceWireWriter()
            .Write((ushort)1).Write((ushort)16)
            .Write(0x50000001u)
            .Write((uint)0).Write((uint)0).Write((uint)1)
            .Write((uint)100).Write((uint)100).Write((uint)100)
            .Write((uint)100).Write((uint)100).Write((uint)100)
            .Write(shareLoot)
            .WriteString16L("Solo")
            .WriteString16L("Solo Fellowship")
            .Write(0x50000001u)
            .Write((uint)0).Write((uint)0).Write((uint)0).Write((uint)0)
            .Write((ushort)0).Write((ushort)32)
            .ToArray();
    }

    // ── 0x02C0 FellowshipUpdateFellow ────────────────────────────────────

    [Fact]
    public void ParseFellowshipUpdateFellow_GuidFirst_RoundTrips()
    {
        byte[] wire = new AceWireWriter()
            .Write(0x50000005u) // guid FIRST
            .Write((uint)10).Write((uint)5).Write((uint)3)
            .Write((uint)100).Write((uint)80).Write((uint)60)
            .Write((uint)90).Write((uint)70).Write((uint)50)
            .Write((uint)0)
            .WriteString16L("Vitals")
            .Write((uint)3) // updateType = 3 UpdateVitals
            .ToArray();

        var update = GameEvents.ParseFellowshipUpdateFellow(wire);

        Assert.NotNull(update);
        Assert.Equal(0x50000005u, update.Value.MemberGuid);
        Assert.Equal(0x50000005u, update.Value.Member.Guid);
        Assert.Equal("Vitals", update.Value.Member.Name);
        Assert.Equal(90u, update.Value.Member.CurrentHealth);
        Assert.Equal(3u, update.Value.UpdateType);
    }

    [Fact]
    public void ParseFellowshipUpdateFellow_ShareLootIsRawNotBool_D5()
    {
        byte[] wire = new AceWireWriter()
            .Write(0x50000005u) // guid FIRST
            .Write((uint)10).Write((uint)5).Write((uint)3)
            .Write((uint)100).Write((uint)80).Write((uint)60)
            .Write((uint)90).Write((uint)70).Write((uint)50)
            .Write((uint)2)
            .WriteString16L("Vitals")
            .Write((uint)3) // updateType = 3 UpdateVitals
            .ToArray();

        var update = GameEvents.ParseFellowshipUpdateFellow(wire);

        Assert.NotNull(update);
        Assert.Equal(2u, update.Value.Member.ShareLoot);
        Assert.NotEqual(1u, update.Value.Member.ShareLoot);
    }

    // ── 0x00A3/0x00A4 S→C ─────────────────────────────────────────────────

    [Fact]
    public void ParseFellowshipQuit_ReadsQuitterGuid()
    {
        byte[] wire = new AceWireWriter().Write(0x50000009u).ToArray();
        var notice = GameEvents.ParseFellowshipQuit(wire);
        Assert.NotNull(notice);
        Assert.Equal(0x50000009u, notice.Value.QuitterGuid);
    }

    [Fact]
    public void ParseFellowshipDismiss_ReadsDismissedGuid()
    {
        byte[] wire = new AceWireWriter().Write(0x5000000Au).ToArray();
        var notice = GameEvents.ParseFellowshipDismiss(wire);
        Assert.NotNull(notice);
        Assert.Equal(0x5000000Au, notice.Value.DismissedGuid);
    }

    // ── 0x02BF FellowshipDisband ──────────────────────────────────────────

    [Fact]
    public void ParseFellowshipDisband_EmptyBody_ReturnsTrue()
    {
        Assert.True(GameEvents.ParseFellowshipDisband(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void ParseFellowshipDisband_NonEmptyBody_StillReturnsTrue()
    {
        Assert.True(GameEvents.ParseFellowshipDisband(new byte[] { 1 }));
    }

    // ── 0x01C9/0x01CA dead events — parse-and-ignore, must never fail ──────

    [Fact]
    public void ParseFellowshipFellowUpdateDone_EmptyPayload_ToleratedNullRaw()
    {
        var done = GameEvents.ParseFellowshipFellowUpdateDone(ReadOnlySpan<byte>.Empty);
        Assert.Null(done.RawValue);
    }

    [Fact]
    public void ParseFellowshipFellowUpdateDone_TrailingU32_CapturesRawValue()
    {
        byte[] wire = new AceWireWriter().Write(42u).ToArray();
        var done = GameEvents.ParseFellowshipFellowUpdateDone(wire);
        Assert.Equal(42u, done.RawValue);
    }

    [Fact]
    public void ParseFellowshipFellowStatsDone_EmptyPayload_ToleratedNullRaw()
    {
        var done = GameEvents.ParseFellowshipFellowStatsDone(ReadOnlySpan<byte>.Empty);
        Assert.Null(done.RawValue);
    }

    [Fact]
    public void ParseFellowshipFellowStatsDone_TrailingU32_CapturesRawValue()
    {
        byte[] wire = new AceWireWriter().Write(7u).ToArray();
        var done = GameEvents.ParseFellowshipFellowStatsDone(wire);
        Assert.Equal(7u, done.RawValue);
    }
}
