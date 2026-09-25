using AcDream.Plugin.Abstractions;

namespace AcDream.Plugin.Tests;

/// <summary>
/// The move that may join a stack, on a host that only implements the plain
/// move. Not joining is the plain move, so it is passed on; joining is
/// something that host cannot do, so it says so instead of quietly moving
/// the item beside the stack.
/// </summary>
public sealed class ItemAutomationDefaultsTests
{
    private sealed class PlainMoveOnly : IItemAutomation
    {
        public List<(uint Item, uint Container, uint Amount, int Placement)> Moves { get; } = [];

        public PluginItemCommandResult MoveToContainer(
            uint objectId,
            uint containerObjectId,
            uint amount = 0u,
            int placement = 0)
        {
            Moves.Add((objectId, containerObjectId, amount, placement));
            return new(PluginItemCommandStatus.Started);
        }
    }

    [Fact]
    public void NotJoiningIsThePlainMove()
    {
        var host = new PlainMoveOnly();
        IItemAutomation items = host;

        PluginItemCommandResult result =
            items.MoveToContainer(7u, 9u, 3u, 2, joinStack: false);

        Assert.Equal(PluginItemCommandStatus.Started, result.Status);
        Assert.Equal([(7u, 9u, 3u, 2)], host.Moves);
    }

    [Fact]
    public void JoiningOnAHostWithoutItIsUnavailableAndSendsNothing()
    {
        var host = new PlainMoveOnly();
        IItemAutomation items = host;

        PluginItemCommandResult result =
            items.MoveToContainer(7u, 9u, 0u, 0, joinStack: true);

        Assert.Equal(PluginItemCommandStatus.Unavailable, result.Status);
        Assert.Empty(host.Moves);
    }
}
