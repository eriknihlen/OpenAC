using AcDream.Core.Chat;
using AcDream.Core.Chess;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeChessStateTests
{
    private const uint Board = 0x7A9B4001u;
    private const uint OtherBoard = 0x7A9B4002u;

    private readonly List<(string Text, RetailLogTextType Type)> _lines = [];

    private RuntimeChessState NewState()
        => new() { OnInterfaceText = (text, type) => _lines.Add((text, type)) };

    private static ChessSquare Sq(string name) => new(name[0] - 'a', name[1] - '1');

    private static GameEvents.ChessOpponentTurn OpponentMove(int team, string from, string to)
        => new(Board, team, new GameEvents.ChessMove(
            ChessMoveType.FromTo, 0x50000002u,
            Sq(from).X, Sq(from).Y, Sq(to).X, Sq(to).Y, 0u));

    /// <summary>Begins, joins as <paramref name="team"/> and starts with
    /// White to move.</summary>
    private RuntimeChessState Seated(ChessTeam team)
    {
        RuntimeChessState chess = NewState();
        chess.Begin(Board);
        chess.ApplyJoinResponse(new(Board, (int)team));
        chess.ApplyStartGame(new(Board, (int)ChessTeam.White));
        _lines.Clear();
        return chess;
    }

    private string LastLine => _lines[^1].Text;

    [Fact]
    public void Begin_AsksToJoinAndSaysSo()
    {
        RuntimeChessState chess = NewState();

        Assert.True(chess.Begin(Board));

        Assert.Equal(RuntimeChessStage.Joining, chess.Snapshot.Stage);
        Assert.Equal(Board, chess.Snapshot.BoardGuid);
        Assert.Equal(("Attempting to join game, please wait...", RetailLogTextType.Default), _lines[^1]);
    }

    [Fact]
    public void Begin_WhilePlaying_IsRefusedWithAMessage()
    {
        RuntimeChessState chess = NewState();
        chess.Begin(Board);

        Assert.False(chess.Begin(Board));
        Assert.Equal(("You are already playing this game.", RetailLogTextType.ClientLocal), _lines[^1]);
        Assert.False(chess.Begin(OtherBoard));
        Assert.Equal(("You are already playing another game.", RetailLogTextType.ClientLocal), _lines[^1]);
        Assert.Equal(Board, chess.Snapshot.BoardGuid);
    }

    [Fact]
    public void JoinRefused_GoesBackToNotPlaying()
    {
        RuntimeChessState chess = NewState();
        chess.Begin(Board);

        chess.ApplyJoinResponse(new(Board, -1));

        Assert.Equal(RuntimeChessStage.NotPlaying, chess.Snapshot.Stage);
        Assert.Equal(0u, chess.Snapshot.BoardGuid);
        Assert.Equal("You could not join that game.", LastLine);
        Assert.True(chess.Begin(Board));
    }

    [Fact]
    public void JoinAndStart_TellTheTeamWhetherItMovesFirst()
    {
        RuntimeChessState white = NewState();
        white.Begin(Board);
        white.ApplyJoinResponse(new(Board, 0));
        Assert.Equal(RuntimeChessStage.WaitingForStart, white.Snapshot.Stage);
        Assert.Equal("You have joined the game, waiting for all players to be ready.", LastLine);
        white.ApplyStartGame(new(Board, 0));
        Assert.Equal(RuntimeChessStage.MyTurn, white.Snapshot.Stage);
        Assert.Equal(ChessTeam.White, white.Snapshot.Team);
        Assert.Equal("The game has begun, it is your turn to move.", LastLine);

        RuntimeChessState black = NewState();
        black.Begin(Board);
        black.ApplyJoinResponse(new(Board, 1));
        black.ApplyStartGame(new(Board, 0));
        Assert.Equal(RuntimeChessStage.OpponentsTurn, black.Snapshot.Stage);
        Assert.Equal("The game has begun, waiting for your opponent to move.", LastLine);
    }

    [Fact]
    public void EventsForAnotherBoard_AreIgnored()
    {
        RuntimeChessState chess = NewState();
        chess.Begin(Board);
        _lines.Clear();

        chess.ApplyJoinResponse(new(OtherBoard, 0));
        chess.ApplyGameOver(new(OtherBoard, 0));
        chess.ApplyOpponentStalemate(new(OtherBoard, 1, true));

        Assert.Equal(RuntimeChessStage.Joining, chess.Snapshot.Stage);
        Assert.Empty(_lines);
    }

    [Fact]
    public void AcceptedMove_IsShownAtOnceAndWaitsForTheServer()
    {
        RuntimeChessState chess = Seated(ChessTeam.White);

        ChessMoveResult result = chess.TryMove(Sq("e2"), Sq("e4"));

        Assert.True(result.IsAccepted());
        Assert.Equal(RuntimeChessStage.MoveSent, chess.Snapshot.Stage);
        Assert.Equal(ChessPieceType.Pawn, chess.View.Board[Sq("e4")]?.Type);
        Assert.Equal("Move in progress.", LastLine);

        chess.ApplyMoveResponse(new(Board, ChessMoveResult.MovedToEmptySquare));

        Assert.Equal(RuntimeChessStage.OpponentsTurn, chess.Snapshot.Stage);
        Assert.Equal("Waiting for your opponent to move.", LastLine);
    }

    [Fact]
    public void RefusedMove_SaysWhyAndKeepsTheTurn()
    {
        RuntimeChessState chess = Seated(ChessTeam.White);

        ChessMoveResult result = chess.TryMove(Sq("e2"), Sq("e5"));

        Assert.Equal(ChessMoveResult.BadDirection, result);
        Assert.Equal(RuntimeChessStage.MyTurn, chess.Snapshot.Stage);
        Assert.Equal(
            "The selected piece cannot move that direction, try again.  It is your turn to move.",
            LastLine);
    }

    [Fact]
    public void MoveTheServerRefuses_IsPutBack()
    {
        RuntimeChessState chess = Seated(ChessTeam.White);
        chess.TryMove(Sq("e2"), Sq("e4"));

        chess.ApplyMoveResponse(new(Board, ChessMoveResult.InvalidCommand));

        ChessBoard board = chess.View.Board;
        Assert.Equal(RuntimeChessStage.MyTurn, chess.Snapshot.Stage);
        Assert.Null(board[Sq("e4")]);
        Assert.Equal(new ChessPiece(ChessPieceType.Pawn, ChessTeam.White), board[Sq("e2")]);
        Assert.Equal(ChessTeam.White, board.Turn);
        Assert.Equal("That move is invalid, try again.  It is your turn to move.", LastLine);
    }

    [Fact]
    public void MovesOffTurn_AreIgnored()
    {
        RuntimeChessState chess = Seated(ChessTeam.Black);

        Assert.Equal(ChessMoveResult.None, chess.TryMove(Sq("e7"), Sq("e5")));
        Assert.Empty(_lines);
    }

    [Fact]
    public void OpponentsMove_IsPlayedAndHandsOverTheTurn()
    {
        RuntimeChessState chess = Seated(ChessTeam.Black);

        chess.ApplyOpponentTurn(OpponentMove(0, "e2", "e4"));

        Assert.Equal(RuntimeChessStage.MyTurn, chess.Snapshot.Stage);
        Assert.Equal(ChessPieceType.Pawn, chess.View.Board[Sq("e4")]?.Type);
        Assert.Equal("It is your turn to move.", LastLine);
        Assert.True(chess.TryMove(Sq("e7"), Sq("e5")).IsAccepted());
    }

    [Fact]
    public void OpponentsMoveTheRulesRefuse_LeavesTheBoardAndLocksTheTurn()
    {
        // The game behaves this way too: the board stays as it was, nothing
        // is said, the turn still passes to the local player, and since the
        // board still has the opponent to move every move after it is
        // refused as not the player's piece.
        RuntimeChessState chess = Seated(ChessTeam.Black);
        ChessBoard before = chess.View.Board;

        chess.ApplyOpponentTurn(OpponentMove(0, "e2", "e5"));

        Assert.Equal(RuntimeChessStage.MyTurn, chess.Snapshot.Stage);
        Assert.Empty(_lines);
        Assert.Equal(before.Pieces, chess.View.Board.Pieces);
        Assert.Equal(ChessTeam.White, chess.View.Board.Turn);

        Assert.Equal(ChessMoveResult.NotYours, chess.TryMove(Sq("e7"), Sq("e5")));
        Assert.Equal(RuntimeChessState.OwnMoveText(ChessMoveResult.NotYours), LastLine);
        Assert.Equal(RuntimeChessStage.MyTurn, chess.Snapshot.Stage);
    }

    [Fact]
    public void CheckAndCheckmate_AreAnnouncedToBothSides()
    {
        RuntimeChessState white = Seated(ChessTeam.White);
        white.TryMove(Sq("f2"), Sq("f3"));
        white.ApplyMoveResponse(new(Board, ChessMoveResult.MovedToEmptySquare));
        white.ApplyOpponentTurn(OpponentMove(1, "e7", "e5"));
        white.TryMove(Sq("g2"), Sq("g4"));
        white.ApplyMoveResponse(new(Board, ChessMoveResult.MovedToEmptySquare));

        white.ApplyOpponentTurn(OpponentMove(1, "d8", "h4"));

        Assert.Equal("You have been checkmated!", LastLine);
        Assert.Equal("You have checkmated your opponent!",
            RuntimeChessState.OwnMoveText(ChessMoveResult.MovedToEmptySquare | ChessMoveResult.Checkmate));
        Assert.Equal("Move in progress.  Your opponent is in Check.",
            RuntimeChessState.OwnMoveText(ChessMoveResult.Captured | ChessMoveResult.Check));
        Assert.Equal("You are in check!  It is your turn to move.",
            RuntimeChessState.OpponentMoveText(ChessMoveResult.MovedToEmptySquare | ChessMoveResult.Check));
    }

    [Fact]
    public void NotYourTurnFromTheServer_HasItsOwnLine()
    {
        Assert.Equal("Its not your turn, please wait for your opponents move.",
            RuntimeChessState.OwnMoveText(ChessMoveResult.NotYourTurn));
    }

    [Theory]
    [InlineData(0, "You are victorious!  To join a game, select and use the game board.")]
    [InlineData(1, "You have been defeated!  To join a game, select and use the game board.")]
    [InlineData(-1, "The game has ended in a stalemate.  To join a game, select and use the game board.")]
    [InlineData(-2, "To join a game, select and use the game board.")]
    public void GameOver_SaysHowItEndedAndFreesThePlayer(int winner, string expected)
    {
        RuntimeChessState chess = Seated(ChessTeam.White);

        chess.ApplyGameOver(new(Board, winner));

        Assert.Equal(expected, LastLine);
        Assert.Equal(RuntimeChessStage.NotPlaying, chess.Snapshot.Stage);
        Assert.Null(chess.Snapshot.Team);
        Assert.True(chess.Begin(Board));
    }

    [Fact]
    public void GameOver_KeepsTheFinalPositionAndOnlyANoResultEndClosesTheWindow()
    {
        RuntimeChessState chess = Seated(ChessTeam.White);
        chess.TryMove(Sq("e2"), Sq("e4"));

        chess.ApplyGameOver(new(Board, 1));
        Assert.Equal(ChessPieceType.Pawn, chess.View.Board[Sq("e4")]?.Type);
        Assert.Equal(0, chess.Snapshot.GamesEnded);

        chess.Begin(Board);
        chess.ApplyJoinResponse(new(Board, 0));
        Assert.Null(chess.View.Board[Sq("e4")]);
        chess.ApplyGameOver(new(Board, GameEvents.ChessNoResultWinner));
        Assert.Equal(1, chess.Snapshot.GamesEnded);
    }

    [Fact]
    public void Resign_EndsTheGameAndClosesTheWindow()
    {
        RuntimeChessState chess = Seated(ChessTeam.White);

        chess.Resign();

        Assert.Equal(RuntimeChessStage.NotPlaying, chess.Snapshot.Stage);
        Assert.Equal(1, chess.Snapshot.GamesEnded);
        Assert.Equal(RuntimeChessState.JoinPrompt, LastLine);
    }

    [Fact]
    public void Resign_WhenNotPlaying_ChangesNothing()
    {
        RuntimeChessState chess = NewState();

        chess.Resign();

        Assert.Equal(0, chess.Snapshot.GamesEnded);
        Assert.Empty(_lines);
    }

    [Fact]
    public void Stalemate_TogglesTheOfferAndAnnouncesTheOpponents()
    {
        RuntimeChessState chess = Seated(ChessTeam.White);

        Assert.True(chess.TryToggleStalemate(out bool offer));
        Assert.True(offer);
        Assert.True(chess.Snapshot.OfferingStalemate);
        Assert.True(chess.TryToggleStalemate(out offer));
        Assert.False(offer);

        chess.ApplyOpponentStalemate(new(Board, 1, true));
        Assert.Equal("Your opponent has declared a stalemate.  To agree, press your stalemate button.", LastLine);
        chess.ApplyOpponentStalemate(new(Board, 1, false));
        Assert.Equal("Your opponent has retracted their offer of stalemate.", LastLine);
    }

    [Fact]
    public void StalemateAndPass_WhenNotPlaying_SaySo()
    {
        RuntimeChessState chess = NewState();

        Assert.False(chess.TryToggleStalemate(out _));
        Assert.Equal((RuntimeChessState.NotPlayingText, RetailLogTextType.ClientLocal), _lines[^1]);
        Assert.False(chess.TryPass());
        Assert.Equal(2, _lines.Count);
    }

    [Fact]
    public void ResetSession_EndsAnyGameAndClearsTheBoard()
    {
        RuntimeChessState chess = Seated(ChessTeam.White);
        chess.TryMove(Sq("e2"), Sq("e4"));

        chess.ResetSession();

        Assert.Equal(RuntimeChessStage.NotPlaying, chess.Snapshot.Stage);
        Assert.Null(chess.View.Board[Sq("e4")]);
        Assert.True(chess.Begin(Board));
    }

    [Fact]
    public void GameRuntime_OwnsTheChessStateAndSendsItsLinesToChat()
    {
        using GameRuntime runtime = UseRangeTestWorld.Create();

        runtime.ChessOwner.Begin(Board);

        Assert.Equal(RuntimeChessStage.Joining, runtime.Chess.Snapshot.Stage);
        Assert.Contains(runtime.CommunicationOwner.Chat.Snapshot(),
            entry => entry.Text.Contains("Attempting to join game", StringComparison.Ordinal));
    }
}
