using System;
using System.Buffers.Binary;
using AcDream.Core.Chess;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class ChessMessagesTests
{
    [Fact]
    public void BuildJoin_WritesBoardAndAsksForAnyTeam()
    {
        byte[] body = ChessRequests.BuildJoin(7, 0x7A9B4001u);

        Assert.Equal(20, body.Length);
        Assert.Equal(ChessRequests.GameActionEnvelope,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0)));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
        Assert.Equal(0x0269u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(0x7A9B4001u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(16)));
    }

    [Theory]
    [InlineData(0x026Au)] // Quit
    [InlineData(0x026Du)] // MovePass
    public void EmptyBodiedActions_WriteEnvelopeSequenceOpcodeOnly(uint opcode)
    {
        byte[] body = opcode == 0x026Au
            ? ChessRequests.BuildQuit(3)
            : ChessRequests.BuildMovePass(3);

        Assert.Equal(12, body.Length);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
        Assert.Equal(opcode, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
    }

    [Fact]
    public void BuildMove_WritesFromThenToSquares()
    {
        byte[] body = ChessRequests.BuildMove(9, 4, 1, 4, 3);

        Assert.Equal(28, body.Length);
        Assert.Equal(0x026Bu, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(12)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(16)));
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(20)));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(24)));
    }

    [Theory]
    [InlineData(true, 1u)]
    [InlineData(false, 0u)]
    public void BuildStalemate_WritesOfferAsOneOrZero(bool offer, uint wire)
    {
        byte[] body = ChessRequests.BuildStalemate(2, offer);

        Assert.Equal(16, body.Length);
        Assert.Equal(0x026Eu, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(wire, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
    }

    [Fact]
    public void ParseJoinGameResponse_ReadsBoardAndTeam_IncludingRefusal()
    {
        byte[] joined = new AceWireWriter().Write(0x7A9B4001u).Write(1).ToArray();
        byte[] refused = new AceWireWriter().Write(0x7A9B4001u).Write(-1).ToArray();

        Assert.Equal(new GameEvents.ChessJoinGameResponse(0x7A9B4001u, 1),
            GameEvents.ParseChessJoinGameResponse(joined));
        Assert.Equal(-1, GameEvents.ParseChessJoinGameResponse(refused)!.Value.Team);
        Assert.Null(GameEvents.ParseChessJoinGameResponse(joined.AsSpan(0, 4)));
    }

    [Fact]
    public void ParseStartGameAndMoveResponse_ReadBoardAndValue()
    {
        byte[] start = new AceWireWriter().Write(0x7A9B4001u).Write(0).ToArray();
        byte[] refused = new AceWireWriter().Write(0x7A9B4001u).Write(-106).ToArray();

        Assert.Equal(new GameEvents.ChessStartGame(0x7A9B4001u, 0),
            GameEvents.ParseChessStartGame(start));
        Assert.Equal(new GameEvents.ChessMoveResponse(0x7A9B4001u, ChessMoveResult.WouldSelfCheck),
            GameEvents.ParseChessMoveResponse(refused));
        Assert.Null(GameEvents.ParseChessStartGame(start.AsSpan(0, 7)));
        Assert.Null(GameEvents.ParseChessMoveResponse(refused.AsSpan(0, 7)));
    }

    [Fact]
    public void ParseOpponentTurn_ReadsAFromToMove()
    {
        // The opponent-turn event: board, team, then the move
        // (type, player, from x/y, to x/y).
        byte[] wire = new AceWireWriter()
            .Write(0x7A9B4001u).Write(1)
            .Write((uint)ChessMoveType.FromTo).Write(0x50000002u)
            .Write(6).Write(7).Write(5).Write(5)
            .ToArray();

        var parsed = GameEvents.ParseChessOpponentTurn(wire);

        Assert.NotNull(parsed);
        Assert.Equal(0x7A9B4001u, parsed!.Value.BoardGuid);
        Assert.Equal(1, parsed.Value.Team);
        Assert.Equal(new GameEvents.ChessMove(
            ChessMoveType.FromTo, 0x50000002u, 6, 7, 5, 5, 0u), parsed.Value.Move);
    }

    [Fact]
    public void ParseOpponentTurn_ReadsGridAndSelectedPieceMoves()
    {
        byte[] grid = new AceWireWriter()
            .Write(0x7A9B4001u).Write(0)
            .Write((uint)ChessMoveType.Grid).Write(0x50000002u)
            .Write(3).Write(4)
            .ToArray();
        byte[] piece = new AceWireWriter()
            .Write(0x7A9B4001u).Write(0)
            .Write((uint)ChessMoveType.SelectedPiece).Write(0x50000002u)
            .Write(0x80000123u)
            .ToArray();

        var gridMove = GameEvents.ParseChessOpponentTurn(grid)!.Value.Move;
        var pieceMove = GameEvents.ParseChessOpponentTurn(piece)!.Value.Move;

        Assert.Equal((3, 4), (gridMove.FromX, gridMove.FromY));
        Assert.Equal(0x80000123u, pieceMove.PieceGuid);
    }

    [Fact]
    public void ParseOpponentTurn_AcceptsABareMoveAndRejectsATruncatedOne()
    {
        byte[] pass = new AceWireWriter()
            .Write(0x7A9B4001u).Write(0)
            .Write((uint)ChessMoveType.Pass).Write(0x50000002u)
            .ToArray();
        byte[] fromTo = new AceWireWriter()
            .Write(0x7A9B4001u).Write(0)
            .Write((uint)ChessMoveType.FromTo).Write(0x50000002u)
            .Write(6).Write(7).Write(5).Write(5)
            .ToArray();

        Assert.Equal(ChessMoveType.Pass, GameEvents.ParseChessOpponentTurn(pass)!.Value.Move.Type);
        Assert.Null(GameEvents.ParseChessOpponentTurn(fromTo.AsSpan(0, fromTo.Length - 4)));
        Assert.Null(GameEvents.ParseChessOpponentTurn(pass.AsSpan(0, 12)));
    }

    [Fact]
    public void ParseOpponentStalemate_ReadsOfferAndRetraction()
    {
        byte[] offer = new AceWireWriter().Write(0x7A9B4001u).Write(1).Write(1).ToArray();
        byte[] retract = new AceWireWriter().Write(0x7A9B4001u).Write(1).Write(0).ToArray();

        Assert.Equal(new GameEvents.ChessOpponentStalemate(0x7A9B4001u, 1, true),
            GameEvents.ParseChessOpponentStalemate(offer));
        Assert.False(GameEvents.ParseChessOpponentStalemate(retract)!.Value.Offered);
        Assert.Null(GameEvents.ParseChessOpponentStalemate(offer.AsSpan(0, 8)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(GameEvents.ChessStalemateWinner)]
    [InlineData(GameEvents.ChessNoResultWinner)]
    public void ParseGameOver_ReadsBoardAndWinner(int winner)
    {
        byte[] wire = new AceWireWriter().Write(0x7A9B4001u).Write(winner).ToArray();

        Assert.Equal(new GameEvents.ChessGameOver(0x7A9B4001u, winner),
            GameEvents.ParseChessGameOver(wire));
        Assert.Null(GameEvents.ParseChessGameOver(wire.AsSpan(0, 4)));
    }
}
