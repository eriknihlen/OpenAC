using AcDream.Plugin.Abstractions;
using AcDream.Plugin.Tests.Fixtures;

namespace AcDream.Plugin.Tests;

public sealed class PluginChatCoordinateWorkflowTests
{
    [Theory]
    [InlineData("42.1N, 33.6E", 33.6, 42.1)]
    [InlineData("42.1 S / 33.6 W", -33.6, -42.1)]
    [InlineData("33.6E 42.1N", 33.6, 42.1)]
    public void CoordinateParserSupportsCompassOrdersAndSeparators(string text, double eastWest, double northSouth)
    {
        Assert.True(PluginChatCoordinateParser.TryParse(text, out var coordinate));
        Assert.Equal(eastWest, coordinate.EastWest, 10);
        Assert.Equal(northSouth, coordinate.NorthSouth, 10);
    }

    [Fact]
    public void MultipleCoordinatesAreReturnedAndSingleParseRejectsAmbiguity()
    {
        var values = PluginChatCoordinateParser.ParseAll("A 1N, 2E then 3S; 4W");
        Assert.Equal(2, values.Count);
        Assert.False(PluginChatCoordinateParser.TryParse("1N, 2E and 3N, 4E", out _));
    }

    [Fact]
    public void LinkRouterForwardsCoordinatesUntilDisposed()
    {
        var chat = new FakePluginChat();
        var received = new List<PluginChatCoordinate>();
        using var router = new PluginChatCoordinateLinkRouter(chat, received.Add);
        chat.RaiseLinkClicked(new(PluginChatLinkKind.Coordinate, "1N, 2E", new(2, 1)));
        Assert.Single(received);
        router.Dispose();
        chat.RaiseLinkClicked(new(PluginChatLinkKind.Coordinate, "3N, 4E", new(4, 3)));
        Assert.Single(received);
    }
}
