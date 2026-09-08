using System;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class CharacterTitleEventsTests
{

    [Fact]
    public void ParseCharacterTitleTable_RoundTrips_DiscardsLeadingVersionTag()
    {
        byte[] wire = new AceWireWriter()
            .Write(1u)
            .Write(13u)         // displayTitleId
            .Write(3u)
            .Write(1u)
            .Write(5u)
            .Write(13u)
            .ToArray();

        GameEvents.CharacterTitleTable? table = GameEvents.ParseCharacterTitleTable(wire);

        Assert.NotNull(table);
        Assert.Equal(13u, table!.Value.DisplayTitleId);
        Assert.Equal(new uint[] { 1u, 5u, 13u }, table.Value.TitleIds);
    }

    [Fact]
    public void ParseCharacterTitleTable_IgnoresNonOneLeadingTag()
    {
        byte[] wire = new AceWireWriter()
            .Write(0xDEADBEEFu)
            .Write(7u)
            .Write(0u) // empty title list
            .ToArray();

        GameEvents.CharacterTitleTable? table = GameEvents.ParseCharacterTitleTable(wire);

        Assert.NotNull(table);
        Assert.Equal(7u, table!.Value.DisplayTitleId);
        Assert.Empty(table.Value.TitleIds);
    }

    [Fact]
    public void ParseCharacterTitleTable_EmptyTitleList_RoundTrips()
    {
        byte[] wire = new AceWireWriter()
            .Write(1u)
            .Write(0u)  // displayTitleId — no display title yet
            .Write(0u)
            .ToArray();

        GameEvents.CharacterTitleTable? table = GameEvents.ParseCharacterTitleTable(wire);

        Assert.NotNull(table);
        Assert.Equal(0u, table!.Value.DisplayTitleId);
        Assert.Empty(table.Value.TitleIds);
    }

    [Theory]
    [InlineData(0)]  // empty payload
    [InlineData(4)]  // only the leading tag
    [InlineData(8)]
    public void ParseCharacterTitleTable_TruncatedHeader_ReturnsNull(int length)
    {
        byte[] wire = new byte[length];
        Assert.Null(GameEvents.ParseCharacterTitleTable(wire));
    }

    [Fact]
    public void ParseCharacterTitleTable_CountExceedsAvailableTitleIds_ReturnsNull()
    {
        byte[] wire = new AceWireWriter()
            .Write(1u)
            .Write(1u)
            .Write(2u)
            .Write(1u)  // only 1 is actually present
            .ToArray();

        Assert.Null(GameEvents.ParseCharacterTitleTable(wire));
    }

    // ── 0x002B UpdateTitle ───────────────────────────────────────────────

    [Theory]
    [InlineData(0u, false)]
    [InlineData(13u, true)]
    public void ParseUpdateTitle_RoundTrips(uint titleId, bool setAsDisplay)
    {
        byte[] wire = new AceWireWriter()
            .Write(titleId)
            .Write(setAsDisplay ? 1u : 0u)
            .ToArray();

        GameEvents.UpdateTitle? update = GameEvents.ParseUpdateTitle(wire);

        Assert.NotNull(update);
        Assert.Equal(titleId, update!.Value.TitleId);
        Assert.Equal(setAsDisplay, update.Value.SetAsDisplay);
    }

    [Fact]
    public void ParseUpdateTitle_NonZeroSetAsDisplay_IsTrue()
    {
        byte[] wire = new AceWireWriter().Write(5u).Write(0xFFu).ToArray();

        GameEvents.UpdateTitle? update = GameEvents.ParseUpdateTitle(wire);

        Assert.NotNull(update);
        Assert.True(update!.Value.SetAsDisplay);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)] // only titleId, missing setAsDisplay
    public void ParseUpdateTitle_Truncated_ReturnsNull(int length)
    {
        byte[] wire = new byte[length];
        Assert.Null(GameEvents.ParseUpdateTitle(wire));
    }
}
