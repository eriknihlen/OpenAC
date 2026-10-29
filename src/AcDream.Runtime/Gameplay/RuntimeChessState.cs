using AcDream.Core.Chat;
using AcDream.Core.Chess;
using AcDream.Core.Net.Messages;

namespace AcDream.Runtime.Gameplay;

/// <summary>Where the local player is in a chess game.</summary>
public enum RuntimeChessStage
{
    NotPlaying = 0,

    /// <summary>A join was sent for a board; no answer yet.</summary>
    Joining = 1,

    /// <summary>Seated, waiting for the other side to sit down.</summary>
    WaitingForStart = 2,

    /// <summary>The local player's move.</summary>
    MyTurn = 3,

    /// <summary>A move was shown and sent; waiting for the server to accept
    /// or refuse it.</summary>
    MoveSent = 4,

    OpponentsTurn = 5,
}

public readonly record struct RuntimeChessSnapshot(
    long Revision,
    RuntimeChessStage Stage,
    uint BoardGuid,
    ChessTeam? Team,
    bool OfferingStalemate,
    long GamesEnded);

public interface IRuntimeChessView
{
    RuntimeChessSnapshot Snapshot { get; }

    /// <summary>A copy of the position the board shows.</summary>
    ChessBoard Board { get; }
}

/// <summary>
/// The local player's chess game. The board window asks it what to send:
/// using a board answers whether to send a join, a move is checked and
/// shown straight away and answers whether to send it, and so on. The
/// server's game events move it along. Every step reports a status line
/// the way the game always has, through <see cref="OnInterfaceText"/>.
/// </summary>
/// <remarks>
/// A move shown before the server confirms it is put back if the server
/// refuses it. When a game ends the final position stays on the board until
/// the next game; <see cref="RuntimeChessSnapshot.GamesEnded"/> counts the
/// games that ended by resigning or with no result, which close the window.
/// </remarks>
public sealed class RuntimeChessState
{
    public const string JoinPrompt = "To join a game, select and use the game board.";
    public const string NotPlayingText = "You are not currently playing a game.";

    private readonly object _gate = new();
    private ChessBoard _board = ChessBoard.CreateStandard();
    private ChessBoard? _beforeMove;
    private RuntimeChessStage _stage;
    private uint _boardGuid;
    private ChessTeam? _team;
    private bool _offeringStalemate;
    private long _gamesEnded;
    private long _revision;

    public RuntimeChessState() => View = new ChessView(this);

    public IRuntimeChessView View { get; }

    public Action<string, RetailLogTextType>? OnInterfaceText { get; set; }

    public RuntimeChessSnapshot Snapshot
    {
        get { lock (_gate) return SnapshotLocked(); }
    }

    /// <summary>The local player used a board. True means send a join for it.</summary>
    public bool Begin(uint boardGuid)
    {
        string text;
        bool join;
        lock (_gate)
        {
            if (_boardGuid == boardGuid)
            {
                (text, join) = ("You are already playing this game.", false);
            }
            else if (_boardGuid != 0)
            {
                (text, join) = ("You are already playing another game.", false);
            }
            else
            {
                _boardGuid = boardGuid;
                _stage = RuntimeChessStage.Joining;
                _revision++;
                (text, join) = ("Attempting to join game, please wait...", true);
            }
        }
        Say(text, join ? RetailLogTextType.Default : RetailLogTextType.ClientLocal);
        return join;
    }

    /// <summary>
    /// Try a move on the local player's turn. An accepted move is shown at
    /// once and should be sent; anything else leaves the board alone. Off
    /// turn the board does not move and nothing is said.
    /// </summary>
    public ChessMoveResult TryMove(ChessSquare from, ChessSquare to)
    {
        ChessMoveResult result;
        lock (_gate)
        {
            if (_stage != RuntimeChessStage.MyTurn)
                return ChessMoveResult.None;

            ChessBoard before = _board.Clone();
            result = _board.Move(from, to);
            if (result.IsAccepted())
            {
                _beforeMove = before;
                _stage = RuntimeChessStage.MoveSent;
                _revision++;
            }
        }
        Say(OwnMoveText(result));
        return result;
    }

    /// <summary>True means send a pass.</summary>
    public bool TryPass()
    {
        lock (_gate)
        {
            if (_boardGuid != 0)
                return true;
        }
        Say(NotPlayingText, RetailLogTextType.ClientLocal);
        return false;
    }

    /// <summary>Offer a draw, or take the offer back. True means send
    /// <paramref name="offer"/>.</summary>
    public bool TryToggleStalemate(out bool offer)
    {
        lock (_gate)
        {
            if (_boardGuid != 0)
            {
                _offeringStalemate = !_offeringStalemate;
                _revision++;
                offer = _offeringStalemate;
                return true;
            }
        }
        offer = false;
        Say(NotPlayingText, RetailLogTextType.ClientLocal);
        return false;
    }

    /// <summary>
    /// The local player confirmed resigning. The caller sends the quit
    /// either way; a game in progress also ends here.
    /// </summary>
    public void Resign()
    {
        lock (_gate)
        {
            if (_boardGuid == 0)
                return;
            EndGameLocked();
            _gamesEnded++;
        }
        Say(JoinPrompt);
    }

    public void ApplyJoinResponse(GameEvents.ChessJoinGameResponse response)
    {
        string text;
        lock (_gate)
        {
            if (response.BoardGuid != _boardGuid || _stage != RuntimeChessStage.Joining)
                return;
            if (response.Team is not (0 or 1))
            {
                _boardGuid = 0;
                _stage = RuntimeChessStage.NotPlaying;
                text = "You could not join that game.";
            }
            else
            {
                _team = (ChessTeam)response.Team;
                _stage = RuntimeChessStage.WaitingForStart;
                _board = ChessBoard.CreateStandard();
                _beforeMove = null;
                _offeringStalemate = false;
                text = "You have joined the game, waiting for all players to be ready.";
            }
            _revision++;
        }
        Say(text);
    }

    public void ApplyStartGame(GameEvents.ChessStartGame start)
    {
        string text;
        lock (_gate)
        {
            if (start.BoardGuid != _boardGuid || _stage != RuntimeChessStage.WaitingForStart)
                return;
            if (start.FirstTeam == (int?)_team)
            {
                _stage = RuntimeChessStage.MyTurn;
                text = "The game has begun, it is your turn to move.";
            }
            else
            {
                _stage = RuntimeChessStage.OpponentsTurn;
                text = "The game has begun, waiting for your opponent to move.";
            }
            _revision++;
        }
        Say(text);
    }

    /// <summary>The server's answer to the move the local player sent.</summary>
    public void ApplyMoveResponse(GameEvents.ChessMoveResponse response)
    {
        string text;
        lock (_gate)
        {
            if (response.BoardGuid != _boardGuid || _stage != RuntimeChessStage.MoveSent)
                return;
            if (response.Result.IsAccepted())
            {
                _stage = RuntimeChessStage.OpponentsTurn;
                text = "Waiting for your opponent to move.";
            }
            else
            {
                if (_beforeMove is not null)
                    _board = _beforeMove;
                _stage = RuntimeChessStage.MyTurn;
                text = OwnMoveText(response.Result);
            }
            _beforeMove = null;
            _revision++;
        }
        Say(text);
    }

    public void ApplyOpponentTurn(GameEvents.ChessOpponentTurn turn)
    {
        string? text = null;
        lock (_gate)
        {
            if (turn.BoardGuid != _boardGuid || _stage != RuntimeChessStage.OpponentsTurn)
                return;
            if (turn.Move.Type == ChessMoveType.FromTo)
            {
                ChessMoveResult result = _board.Move(
                    new ChessSquare(turn.Move.FromX, turn.Move.FromY),
                    new ChessSquare(turn.Move.ToX, turn.Move.ToY));
                if (result.IsAccepted())
                    text = OpponentMoveText(result);
            }
            // The turn passes even when the board's rules refuse a move the
            // server accepted, and nothing is said. That is how the game
            // behaves: the board keeps the opponent to move, so every move
            // the player then tries is refused as not theirs until the game
            // ends. Matching it keeps both clients in the same state.
            _stage = RuntimeChessStage.MyTurn;
            _revision++;
        }
        if (text is not null)
            Say(text);
    }

    public void ApplyOpponentStalemate(GameEvents.ChessOpponentStalemate stalemate)
    {
        lock (_gate)
        {
            if (stalemate.BoardGuid != _boardGuid)
                return;
        }
        Say(stalemate.Offered
            ? "Your opponent has declared a stalemate.  To agree, press your stalemate button."
            : "Your opponent has retracted their offer of stalemate.");
    }

    public void ApplyGameOver(GameEvents.ChessGameOver over)
    {
        string text;
        lock (_gate)
        {
            if (over.BoardGuid != _boardGuid)
                return;
            string result = over.Winner switch
            {
                GameEvents.ChessNoResultWinner => string.Empty,
                GameEvents.ChessStalemateWinner => "The game has ended in a stalemate.  ",
                _ when over.Winner == (int?)_team => "You are victorious!  ",
                _ => "You have been defeated!  ",
            };
            if (over.Winner == GameEvents.ChessNoResultWinner)
                _gamesEnded++;
            EndGameLocked();
            text = result + JoinPrompt;
        }
        Say(text);
    }

    public void ResetSession()
    {
        lock (_gate)
        {
            EndGameLocked();
            _board = ChessBoard.CreateStandard();
        }
    }

    /// <summary>The line for the local player's own move: the server's
    /// verdict or the board's.</summary>
    public static string OwnMoveText(ChessMoveResult result)
    {
        if (result.GivesCheckmate())
            return "You have checkmated your opponent!";
        if (result.IsAccepted())
            return result.GivesCheck()
                ? "Move in progress.  Your opponent is in Check."
                : "Move in progress.";
        if (result == ChessMoveResult.NotYourTurn)
            return "Its not your turn, please wait for your opponents move.";

        string reason = result switch
        {
            ChessMoveResult.CantCastleAfterMoving => "You cannot castle after moving the King or Rook",
            ChessMoveResult.CantCastleThroughCheck => "You cannot castle through check",
            ChessMoveResult.CantCastleOutOfCheck => "You cannot castle out of check",
            ChessMoveResult.PathBlocked => "You can only move through empty squares",
            ChessMoveResult.WouldSelfCheck => "That move would put you in check",
            ChessMoveResult.WouldCaptureOwnPiece => "You cannot attack your own pieces",
            ChessMoveResult.OffBoard => "You cannot move off the board",
            ChessMoveResult.NotYours => "The selected piece is not yours",
            ChessMoveResult.NoPiece => "You tried to move an empty square",
            ChessMoveResult.BadDistance => "The selected piece cannot move that far",
            ChessMoveResult.BadDirection => "The selected piece cannot move that direction",
            _ => "That move is invalid",
        };
        return reason + ", try again.  It is your turn to move.";
    }

    public static string OpponentMoveText(ChessMoveResult result)
    {
        if (result.GivesCheckmate())
            return "You have been checkmated!";
        return result.GivesCheck()
            ? "You are in check!  It is your turn to move."
            : "It is your turn to move.";
    }

    private void EndGameLocked()
    {
        _boardGuid = 0;
        _stage = RuntimeChessStage.NotPlaying;
        _team = null;
        _offeringStalemate = false;
        _beforeMove = null;
        _revision++;
    }

    private void Say(string text, RetailLogTextType type = RetailLogTextType.Default)
        => OnInterfaceText?.Invoke(text, type);

    private RuntimeChessSnapshot SnapshotLocked() => new(
        _revision, _stage, _boardGuid, _team, _offeringStalemate, _gamesEnded);

    private sealed class ChessView(RuntimeChessState owner) : IRuntimeChessView
    {
        public RuntimeChessSnapshot Snapshot => owner.Snapshot;

        public ChessBoard Board
        {
            get { lock (owner._gate) return owner._board.Clone(); }
        }
    }
}
