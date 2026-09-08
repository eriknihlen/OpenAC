using AcDream.Core.Chat;

namespace AcDream.Core.Tests.Chat;

public sealed class ChatWindowStateTests
{

    [Theory]
    [InlineData(0, 0xFBFFFFFFu)]
    [InlineData(1, 0x0000101Cu)]
    [InlineData(2, 0x00040C00u)]
    [InlineData(3, 0x00080000u)]
    [InlineData(4, 0x78000000u)]
    public void Defaults_MatchRetailPostInitTable(int windowId, ulong expectedFilter)
    {
        var state = new ChatWindowState();

        Assert.Equal(expectedFilter, state.GetFilter(windowId));
    }

    [Fact]
    public void NamedDefaultConstants_MatchTheSameTable()
    {
        Assert.Equal(0xFBFFFFFFu, ChatWindowState.MainWindowDefaultFilter);
        Assert.Equal(0x0000101Cu, ChatWindowState.Floaty1DefaultFilter);
        Assert.Equal(0x00040C00u, ChatWindowState.Floaty2DefaultFilter);
        Assert.Equal(0x00080000u, ChatWindowState.Floaty3DefaultFilter);
        Assert.Equal(0x78000000u, ChatWindowState.Floaty4DefaultFilter);
    }

    [Fact]
    public void Defaults_MainWindowIsAlwaysOpen_FloatingWindowsStartClosed()
    {
        var state = new ChatWindowState();

        Assert.True(state.IsOpen(0));
        for (int windowId = 1; windowId <= 4; windowId++)
            Assert.False(state.IsOpen(windowId));
    }

    [Fact]
    public void Window1_DefaultFilter_MatchesSpeechTellDirectSendEmote()
    {
        var state = new ChatWindowState();
        Assert.True(state.TypeIsActive(1, 0x02u));  // Speech
        Assert.True(state.TypeIsActive(1, 0x03u));  // Tell
        Assert.True(state.TypeIsActive(1, 0x04u));  // Speech_Direct_Send
        Assert.True(state.TypeIsActive(1, 0x0Cu));  // Emote
        Assert.False(state.TypeIsActive(1, 0x0Au)); // Social — not in window 1's default
    }

    [Fact]
    public void Window2_DefaultFilter_MatchesSocialSocialSendAllegiance()
    {
        var state = new ChatWindowState();
        Assert.True(state.TypeIsActive(2, 0x0Au));  // Social
        Assert.True(state.TypeIsActive(2, 0x0Bu));  // Social_Send
        Assert.True(state.TypeIsActive(2, 0x12u));  // Allegiance
        Assert.False(state.TypeIsActive(2, 0x13u)); // Fellowship
    }

    [Fact]
    public void Window3_DefaultFilter_MatchesFellowshipOnly()
    {
        var state = new ChatWindowState();
        Assert.True(state.TypeIsActive(3, 0x13u));   // Fellowship
        Assert.False(state.TypeIsActive(3, 0x0Au));  // Social
        Assert.False(state.TypeIsActive(3, 0x02u));  // Speech
    }

    [Fact]
    public void Window4_DefaultFilter_MatchesTurbineGeneralTradeLfgRoleplay()
    {
        var state = new ChatWindowState();
        Assert.True(state.TypeIsActive(4, 0x1Bu));   // TurbineGeneral
        Assert.True(state.TypeIsActive(4, 0x1Cu));   // TurbineTrade
        Assert.True(state.TypeIsActive(4, 0x1Du));   // TurbineLFG
        Assert.True(state.TypeIsActive(4, 0x1Eu));   // TurbineRoleplay
        Assert.False(state.TypeIsActive(4, 0x20u));  // TurbineSociety — opt-in only
        Assert.False(state.TypeIsActive(4, 0x12u));  // Allegiance
    }

    [Fact]
    public void EveryWindow_NeverActivatesSocietyOrReservedByDefault()
    {
        var state = new ChatWindowState();
        for (int windowId = 0; windowId <= 4; windowId++)
        {
            Assert.False(state.TypeIsActive(windowId, 0x20u)); // Society
            Assert.False(state.TypeIsActive(windowId, 0x21u)); // Reserved
        }
    }


    [Fact]
    public void TypeIsActive_TypeAtOrAbove64_IsNeverActive()
    {
        var state = new ChatWindowState();
        state.SetFilter(1, ulong.MaxValue);

        Assert.False(state.TypeIsActive(1, 64u));
        Assert.False(state.TypeIsActive(1, 1000u));
    }

    // ── Display rule matrix (windowId-addressed vs broadcast × filter hit/miss) ──

    [Fact]
    public void ShouldDisplay_ExplicitlyAddressed_AlwaysShowsRegardlessOfFilter()
    {
        var state = new ChatWindowState();
        Assert.True(state.ShouldDisplay(windowId: 3, targetWindowId: 3u, logTextType: 0x02u));
    }

    [Fact]
    public void ShouldDisplay_ExplicitlyAddressedToAnotherWindow_NeverShowsHereEvenOnBroadcastFilterHit()
    {
        var state = new ChatWindowState();
        // Addressed to window 2, evaluated from window 1's perspective: not a
        // broadcast (targetWindowId is a real window id, not
        // BroadcastTargetWindow) and not addressed to window 1.
        Assert.False(state.ShouldDisplay(windowId: 1, targetWindowId: 2u, logTextType: 0x02u));
    }

    [Fact]
    public void ShouldDisplay_Broadcast_FilterHit_Shows()
    {
        var state = new ChatWindowState();
        Assert.True(state.ShouldDisplay(
            windowId: 1, targetWindowId: ChatWindowState.BroadcastTargetWindow, logTextType: 0x02u)); // Speech
    }

    [Fact]
    public void ShouldDisplay_Broadcast_FilterMiss_DoesNotShow()
    {
        var state = new ChatWindowState();
        Assert.False(state.ShouldDisplay(
            windowId: 1, targetWindowId: ChatWindowState.BroadcastTargetWindow, logTextType: 0x0Au)); // Social
    }

    [Fact]
    public void ShouldDisplay_MainWindow_BroadcastRespectsItsOwnFilter()
    {
        var state = new ChatWindowState();
        Assert.False(state.ShouldDisplay(
            windowId: 0, targetWindowId: ChatWindowState.BroadcastTargetWindow, logTextType: 0x1Au)); // excluded
        Assert.False(state.ShouldDisplay(
            windowId: 0, targetWindowId: ChatWindowState.BroadcastTargetWindow, logTextType: 0x20u)); // Society, opt-in
        Assert.True(state.ShouldDisplay(
            windowId: 0, targetWindowId: ChatWindowState.BroadcastTargetWindow, logTextType: 0x02u)); // Speech
    }

    [Fact]
    public void ShouldDisplay_MainWindow_ExplicitlyAddressed_AlwaysShows()
    {
        var state = new ChatWindowState();
        Assert.True(state.ShouldDisplay(windowId: 0, targetWindowId: 0u, logTextType: 0x1Au));
    }

    // ── SetFilter / SetOpen / Toggle ─────────────────────────────────────────

    [Fact]
    public void SetFilter_MainWindow_Persists()
    {
        var state = new ChatWindowState();

        state.SetFilter(0, 0x1u);

        Assert.Equal(0x1u, state.GetFilter(0));
        Assert.True(state.TypeIsActive(0, 0x00u));
        Assert.False(state.TypeIsActive(0, 0x02u));
    }

    [Fact]
    public void SetFilter_FloatingWindow_Persists()
    {
        var state = new ChatWindowState();

        state.SetFilter(2, 0x1u);

        Assert.Equal(0x1u, state.GetFilter(2));
        Assert.True(state.TypeIsActive(2, 0x00u));
    }

    [Fact]
    public void SetOpen_MainWindow_IsANoOp_AlwaysOpen()
    {
        var state = new ChatWindowState();

        state.SetOpen(0, false);

        Assert.True(state.IsOpen(0));
    }

    [Fact]
    public void Toggle_FloatingWindow_FlipsOpenState_AndReturnsNewValue()
    {
        var state = new ChatWindowState();
        Assert.False(state.IsOpen(1));

        bool afterFirst = state.Toggle(1);
        Assert.True(afterFirst);
        Assert.True(state.IsOpen(1));

        bool afterSecond = state.Toggle(1);
        Assert.False(afterSecond);
        Assert.False(state.IsOpen(1));
    }

    [Fact]
    public void Toggle_MainWindow_AlwaysReturnsTrue_NeverCloses()
    {
        var state = new ChatWindowState();

        bool result = state.Toggle(0);

        Assert.True(result);
        Assert.True(state.IsOpen(0));
    }

    [Fact]
    public void ResetToDefaults_RestoresSeededFiltersAndOpenState()
    {
        var state = new ChatWindowState();
        state.SetFilter(1, 0u);
        state.SetOpen(1, true);

        state.ResetToDefaults();

        Assert.Equal(0x0000101Cu, state.GetFilter(1));
        Assert.False(state.IsOpen(1));
    }

    // ── Argument validation ───────────────────────────────────────────────

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void OutOfRangeWindowId_Throws(int windowId)
    {
        var state = new ChatWindowState();
        Assert.Throws<ArgumentOutOfRangeException>(() => state.GetFilter(windowId));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.IsOpen(windowId));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.TypeIsActive(windowId, 0u));
    }


    [Fact]
    public void Revision_AdvancesOnFilterAndOpenChange_NotOnNoOpWrites()
    {
        var state = new ChatWindowState();
        long baseline = state.Revision;

        state.SetFilter(1, 0x1u);
        Assert.True(state.Revision > baseline);
        long afterFilter = state.Revision;

        // No-op: same value.
        state.SetFilter(1, 0x1u);
        Assert.Equal(afterFilter, state.Revision);

        state.SetOpen(1, true);
        Assert.True(state.Revision > afterFilter);
    }
}
