using AcDream.Core.Net;
using Xunit;

namespace AcDream.Core.Net.Tests;

public sealed class WorldSessionInboundBudgetTests
{
    [Fact]
    public void InWorld_overBudget_stops()
        => Assert.True(WorldSession.InboundBudgetExceeded(
            WorldSession.State.InWorld, startTicks: 100, nowTicks: 250, budgetTicks: 100));

    [Fact]
    public void InWorld_atBudgetBoundary_stops()
        => Assert.True(WorldSession.InboundBudgetExceeded(
            WorldSession.State.InWorld, startTicks: 0, nowTicks: 100, budgetTicks: 100));

    [Fact]
    public void InWorld_underBudget_continues()
        => Assert.False(WorldSession.InboundBudgetExceeded(
            WorldSession.State.InWorld, startTicks: 0, nowTicks: 99, budgetTicks: 100));

    [Fact]
    public void NotInWorld_neverBounds_soTheHandshakeDrainsFully()
        => Assert.False(WorldSession.InboundBudgetExceeded(
            WorldSession.State.Disconnected, startTicks: 0, nowTicks: 1_000_000, budgetTicks: 100));
}
