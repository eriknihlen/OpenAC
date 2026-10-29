using System;
using System.Collections.Generic;
using AcDream.Core.Chess;
using AcDream.Core.Items;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.UI.Layout;

/// <summary>
/// The Game Center window: the chess board, plus the Resign, Pass and
/// Stalemate buttons. Using a board in the world joins its game and opens it.
/// The board is drawn from the side the local player sits on; clicking one
/// of their pieces on their turn selects it and clicking another square
/// tries the move.
/// </summary>
public sealed class MiniGameUiController : IRetainedPanelController
{
    public const uint LayoutId = 0x2100001Eu;
    public const uint RootId = 0x1000016Au;
    public const uint CloseId = 0x1000016Bu;
    public const uint ResignId = 0x10000175u;
    public const uint PassId = 0x10000176u;
    public const uint StalemateId = 0x10000177u;
    public const uint BoardId = 0x10000174u;
    public const uint SquareTemplateId = 0x10000178u;
    public const uint SelectionId = 0x10000179u;

    /// <summary>The board's list of piece images: White's six, then
    /// Black's, each running pawn, bishop, knight, rook, queen, king.</summary>
    public const uint PieceImagesPropertyId = 0x10000021u;
    private const int PiecesPerSide = 6;

    public const string ResignConfirmation =
        "If a game is in progress, resigning will be recorded as your loss. Are you sure you want to resign?";

    private const int SquareSize = 32;
    private const int SquareCount = ChessBoard.Size * ChessBoard.Size;

    /// <summary>What the board needs beyond its layout.</summary>
    public sealed record ChessBindings(
        RuntimeChessState Chess,
        Func<UiElement?> CreateSquare,
        Func<uint, uint> SpriteTexture,
        Action<ChessSquare, ChessSquare> SendMove,
        Action SendPass,
        Action<bool> SendStalemate,
        Action SendQuit,
        Action<string, Action<bool>> Confirm,
        Action ShowWindow,
        Action HideWindow,
        Action<bool> SetIndicatorActive);

    private readonly UiButton _close;
    private readonly ChessBindings? _chess;
    private readonly uint[] _pieceImages;
    private readonly RuntimeItemInteraction? _items;
    private readonly UiButton? _resign;
    private readonly UiButton? _pass;
    private readonly UiButton? _stalemate;
    private readonly UiDatElement?[] _squares = new UiDatElement?[SquareCount];
    private readonly UiElement?[] _selections = new UiElement?[SquareCount];
    private ChessSquare? _selected;
    private long _drawnRevision = -1;
    private long _gamesEnded;
    private bool _indicatorActive;
    private bool _confirmingResign;

    private MiniGameUiController(
        ImportedLayout layout,
        UiButton close,
        Action? closeWindow,
        ChessBindings? chess,
        IReadOnlyList<uint>? pieceImages,
        RuntimeItemInteraction? items)
    {
        _close = close;
        _close.OnClick = closeWindow;
        _chess = chess;
        _pieceImages = new uint[2 * PiecesPerSide];
        if (pieceImages is not null)
        {
            for (int i = 0; i < _pieceImages.Length && i < pieceImages.Count; i++)
                _pieceImages[i] = pieceImages[i];
        }
        if (chess is null)
            return;

        _gamesEnded = chess.Chess.Snapshot.GamesEnded;
        _resign = layout.FindElement(ResignId) as UiButton;
        _pass = layout.FindElement(PassId) as UiButton;
        _stalemate = layout.FindElement(StalemateId) as UiButton;
        if (_resign is not null) _resign.OnClick = OnResign;
        if (_stalemate is not null) _stalemate.OnClick = OnStalemate;
        if (_pass is not null)
        {
            // The game has a pass button but keeps it out of sight.
            _pass.OnClick = OnPass;
            _pass.Visible = false;
        }

        if (layout.FindElement(BoardId) is UiTemplateListBox board)
            BuildSquares(board, chess.CreateSquare);

        // The item owner outlives this window, so the window takes its
        // handler back off when it is disposed.
        _items = items;
        if (_items is not null)
            _items.PolicyActionRequested += HandlePolicyAction;
    }

    public static MiniGameUiController? Bind(
        ImportedLayout layout,
        Action? close = null,
        ChessBindings? chess = null,
        IReadOnlyList<uint>? pieceImages = null,
        RuntimeItemInteraction? items = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return layout.FindElement(CloseId) is UiButton button
            ? new MiniGameUiController(layout, button, close, chess, pieceImages, items)
            : null;
    }

    /// <summary>The square the local player has picked up a piece from.</summary>
    public ChessSquare? Selected => _selected;

    /// <summary>Using a game board opens the window. Joining its game is
    /// the runtime's part and has already been done.</summary>
    public void HandlePolicyAction(ItemPolicyAction action)
    {
        if (_chess is not null && action.Kind == ItemPolicyActionKind.BeginGame)
            _chess.ShowWindow();
    }

    /// <summary>Clicks on the board's squares, numbered left to right and
    /// top to bottom as the window shows them.</summary>
    public void ClickSquare(int index)
    {
        if (_chess is null || (uint)index >= SquareCount)
            return;
        RuntimeChessSnapshot snapshot = _chess.Chess.Snapshot;
        if (snapshot.Stage != RuntimeChessStage.MyTurn || snapshot.Team is not ChessTeam team)
            return;

        ChessSquare square = SquareAt(index, team);
        if (_selected is ChessSquare from)
        {
            _selected = null;
            if (from != square && _chess.Chess.TryMove(from, square).IsAccepted())
                _chess.SendMove(from, square);
        }
        else if (_chess.Chess.View.Board[square] is { } piece && piece.Team == team)
        {
            _selected = square;
        }
        Redraw(_chess.Chess.Snapshot);
    }

    public void Tick()
    {
        if (_chess is null)
            return;
        RuntimeChessSnapshot snapshot = _chess.Chess.Snapshot;

        if (snapshot.GamesEnded != _gamesEnded)
        {
            _gamesEnded = snapshot.GamesEnded;
            _chess.HideWindow();
        }

        bool active = snapshot.BoardGuid != 0;
        if (active != _indicatorActive)
        {
            _indicatorActive = active;
            _chess.SetIndicatorActive(active);
        }

        if (snapshot.Revision == _drawnRevision)
            return;
        if (snapshot.Stage != RuntimeChessStage.MyTurn)
            _selected = null;
        Redraw(snapshot);
    }

    public void Dispose()
    {
        if (_items is not null)
            _items.PolicyActionRequested -= HandlePolicyAction;
        _close.OnClick = null;
        if (_resign is not null) _resign.OnClick = null;
        if (_pass is not null) _pass.OnClick = null;
        if (_stalemate is not null) _stalemate.OnClick = null;
        foreach (UiDatElement? square in _squares)
        {
            if (square is not null)
                square.OnClick = null;
        }
    }

    /// <summary>
    /// The board square under window square <paramref name="index"/>. White
    /// sees its own back rank at the bottom; anyone else sees the board
    /// turned round.
    /// </summary>
    public static ChessSquare SquareAt(int index, ChessTeam? team)
    {
        int column = index % ChessBoard.Size;
        int row = index / ChessBoard.Size;
        return team == ChessTeam.White
            ? new ChessSquare(column, ChessBoard.Size - 1 - row)
            : new ChessSquare(ChessBoard.Size - 1 - column, row);
    }

    /// <summary>The image the board's layout gives <paramref name="piece"/>,
    /// or zero when it gives none.</summary>
    public uint PieceSprite(ChessPiece piece)
    {
        int side = piece.Team == ChessTeam.White ? 0 : PiecesPerSide;
        return _pieceImages[side + piece.Type switch
        {
            ChessPieceType.Pawn => 0,
            ChessPieceType.Bishop => 1,
            ChessPieceType.Knight => 2,
            ChessPieceType.Rook => 3,
            ChessPieceType.Queen => 4,
            _ => 5,
        }];
    }

    /// <summary>
    /// The twelve piece images listed on the board element of the window's
    /// layout, in the order <see cref="PieceSprite"/> reads them. An entry
    /// the layout does not give as an image, or gives as a value too wide
    /// to be one, is zero.
    /// </summary>
    public static uint[] ReadPieceImages(ElementInfo rootInfo)
    {
        ArgumentNullException.ThrowIfNull(rootInfo);
        var images = new uint[2 * PiecesPerSide];
        if (FindInfo(rootInfo, BoardId) is not { } board
            || !board.TryGetEffectiveProperty(PieceImagesPropertyId, out UiPropertyValue list)
            || list.Kind != UiPropertyKind.Array)
            return images;

        for (int i = 0; i < images.Length && i < list.ArrayValue.Count; i++)
        {
            UiPropertyValue entry = list.ArrayValue[i];
            // An image id is 32 bits; anything wider is not one, and draws
            // no piece rather than stopping the window from mounting.
            if (entry.Kind == UiPropertyKind.DataId && entry.UnsignedValue <= uint.MaxValue)
                images[i] = (uint)entry.UnsignedValue;
        }
        return images;
    }

    private static ElementInfo? FindInfo(ElementInfo info, uint id)
    {
        if (info.Id == id)
            return info;
        foreach (ElementInfo child in info.Children)
        {
            if (FindInfo(child, id) is { } found)
                return found;
        }
        return null;
    }

    private void BuildSquares(UiTemplateListBox board, Func<UiElement?> createSquare)
    {
        var grid = new BoardGrid
        {
            Width = ChessBoard.Size * SquareSize,
            Height = ChessBoard.Size * SquareSize,
        };
        for (int index = 0; index < SquareCount; index++)
        {
            if (createSquare() is not UiDatElement square)
                return;
            square.Left = index % ChessBoard.Size * SquareSize;
            square.Top = index / ChessBoard.Size * SquareSize;
            square.ClickThrough = false;
            square.RuntimeImageTexture = 0u;
            int clicked = index;
            square.OnClick = () => ClickSquare(clicked);
            grid.AddChild(square);
            _squares[index] = square;
            _selections[index] = FindChild(square, SelectionId);
        }
        board.AddPrebuiltRow(grid);
    }

    private void Redraw(RuntimeChessSnapshot snapshot)
    {
        _drawnRevision = snapshot.Revision;
        ChessBoard board = _chess!.Chess.View.Board;
        for (int index = 0; index < SquareCount; index++)
        {
            ChessSquare square = SquareAt(index, snapshot.Team);
            if (_squares[index] is { } element)
            {
                element.RuntimeImageTexture = board[square] is { } piece
                    && PieceSprite(piece) is uint image && image != 0u
                        ? _chess.SpriteTexture(image)
                        : 0u;
            }
            if (_selections[index] is { } selection)
                selection.Visible = _selected == square;
        }
    }

    private void OnResign()
    {
        if (_chess is null || _confirmingResign)
            return;
        _confirmingResign = true;
        _chess.Confirm(ResignConfirmation, confirmed =>
        {
            _confirmingResign = false;
            if (!confirmed)
                return;
            _chess.SendQuit();
            _chess.Chess.Resign();
        });
    }

    private void OnStalemate()
    {
        if (_chess is not null && _chess.Chess.TryToggleStalemate(out bool offer))
            _chess.SendStalemate(offer);
    }

    private void OnPass()
    {
        if (_chess is not null && _chess.Chess.TryPass())
            _chess.SendPass();
    }

    private static UiElement? FindChild(UiElement element, uint id)
    {
        foreach (UiElement child in element.Children)
        {
            if (child is UiDatElement dat && dat.ElementId == id)
                return child;
            if (FindChild(child, id) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>Holds the 64 squares as one row of the board list.</summary>
    private sealed class BoardGrid : UiElement
    {
    }
}
