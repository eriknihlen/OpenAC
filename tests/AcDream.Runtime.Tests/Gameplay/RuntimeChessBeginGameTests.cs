using System.Buffers.Binary;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Tests.Support;

namespace AcDream.Runtime.Tests.Gameplay;

/// <summary>
/// Using a game board out in the world joins its game in the runtime, so a
/// client with no window joins exactly as one with a window does. Before,
/// only the board window sent the join, and a windowless use of a board was
/// answered as handled while nothing went out.
/// </summary>
public sealed class RuntimeChessBeginGameTests
{
    private const uint Board = 0x7A9B4001u;

    private static void PlaceBoard(NoWindowGameRuntimeHost host)
        => host.Runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Board,
            Name = "Chess Board",
            Type = ItemType.Gameboard,
            PublicWeenieBitfield = (uint)PublicWeenieFlags.Stuck,
        });

    private static List<byte[]> CaptureGameActions(NoWindowGameRuntimeHost host)
    {
        var captured = new List<byte[]>();
        host.Runtime.Session.CurrentSession!.GameActionCapture = captured.Add;
        return captured;
    }

    private static void AssertSentOneJoin(List<byte[]> captured)
    {
        byte[] join = Assert.Single(captured, body =>
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)) == ChessRequests.JoinOpcode);
        Assert.Equal(Board, BinaryPrimitives.ReadUInt32LittleEndian(join.AsSpan(12)));
    }

    [Fact]
    public void UsingABoardWithNoWindow_SendsTheJoinAndWaitsForTheAnswer()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        PlaceBoard(host);
        List<byte[]> captured = CaptureGameActions(host);

        Assert.True(host.Runtime.ItemInteractionOwner.TryBeginGame(Board));

        AssertSentOneJoin(captured);
        Assert.Equal(RuntimeChessStage.Joining, host.Runtime.Chess.Snapshot.Stage);
        Assert.Equal(Board, host.Runtime.Chess.Snapshot.BoardGuid);
    }

    [Fact]
    public void UsingTheSameBoardAgain_SendsNoSecondJoin()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        PlaceBoard(host);
        List<byte[]> captured = CaptureGameActions(host);

        Assert.True(host.Runtime.ItemInteractionOwner.TryBeginGame(Board));
        Assert.True(host.Runtime.ItemInteractionOwner.TryBeginGame(Board));

        AssertSentOneJoin(captured);
    }

    [Fact]
    public void AnAutomationUseOfABoard_JoinsItsGameInsteadOfUsingIt()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        PlaceBoard(host);
        List<byte[]> captured = CaptureGameActions(host);

        Assert.Equal(
            AutomationUseOutcome.Started,
            host.Runtime.WorldObjectUseOwner.TryUse(Board));

        AssertSentOneJoin(captured);
        Assert.Equal(RuntimeChessStage.Joining, host.Runtime.Chess.Snapshot.Stage);
    }
}
