using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Chess;
using AcDream.Core.Items;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Tests.UI.Layout;

public sealed class MiniGameUiControllerTests
{
    private const uint Board = 0x7A9B4001u;

    // The first white and first black piece image the window's layout lists.
    private const uint WhitePieceSprites = 0x06004CFDu;
    private const uint BlackPieceSprites = 0x06004D03u;

    private readonly RuntimeChessState _chess = new();
    private readonly List<(ChessSquare From, ChessSquare To)> _moves = [];
    private readonly List<bool> _stalemates = [];
    private readonly List<bool> _indicator = [];
    private int _quits;
    private int _shows;
    private int _hides;
    private string? _confirmMessage;
    private Action<bool>? _confirm;

    private static ChessSquare Sq(string name) => new(name[0] - 'a', name[1] - '1');

    /// <summary>A square shaped like the game's square template: a 32 by 32
    /// image holding a hidden selection frame.</summary>
    private static UiElement? CreateSquare()
    {
        var info = new ElementInfo { Id = MiniGameUiController.SquareTemplateId, Type = 3, Width = 32, Height = 32 };
        info.Children.Add(new ElementInfo
        {
            Id = MiniGameUiController.SelectionId,
            Type = 3,
            Width = 32,
            Height = 32,
            Invisible = true,
        });
        return LayoutImporter.Build(info, _ => (0u, 0, 0), null).Root;
    }

    private MiniGameUiController.ChessBindings Bindings() => new(
        _chess,
        CreateSquare,
        SpriteTexture: sprite => sprite,
        SendMove: (from, to) => _moves.Add((from, to)),
        SendPass: () => { },
        SendStalemate: _stalemates.Add,
        SendQuit: () => _quits++,
        Confirm: (message, completed) => (_confirmMessage, _confirm) = (message, completed),
        ShowWindow: () => _shows++,
        HideWindow: () => _hides++,
        SetIndicatorActive: _indicator.Add);

    private (MiniGameUiController Controller, ImportedLayout Layout) Bind()
    {
        ImportedLayout layout = FixtureLoader.LoadMiniGame();
        MiniGameUiController controller = MiniGameUiController.Bind(
            layout,
            close: null,
            Bindings(),
            MiniGameUiController.ReadPieceImages(FixtureLoader.LoadMiniGameInfos()))!;
        return (controller, layout);
    }

    private static UiDatElement[] Squares(ImportedLayout layout)
    {
        var board = Assert.IsType<UiTemplateListBox>(layout.FindElement(MiniGameUiController.BoardId));
        Assert.Equal(1, board.ItemCount);
        UiElement grid = Assert.Single(board.Children).Children[0];
        return grid.Children.Cast<UiDatElement>().ToArray();
    }

    private static bool Highlighted(UiDatElement square) => square.Children[0].Visible;

    private void Seat(MiniGameUiController controller, ChessTeam team)
    {
        _chess.Begin(Board);
        controller.HandlePolicyAction(new ItemPolicyAction(ItemPolicyActionKind.BeginGame, Board));
        _chess.ApplyJoinResponse(new(Board, (int)team));
        _chess.ApplyStartGame(new(Board, (int)ChessTeam.White));
        controller.Tick();
    }

    [Fact]
    public void Bind_LaysOutSixtyFourSquaresAndHidesThePassButton()
    {
        var (controller, layout) = Bind();
        using var _ = controller;

        UiDatElement[] squares = Squares(layout);

        Assert.Equal(64, squares.Length);
        Assert.Equal((32f, 0f), (squares[1].Left, squares[1].Top));
        Assert.Equal((0f, 32f), (squares[8].Left, squares[8].Top));
        Assert.All(squares, square => Assert.False(square.ClickThrough));
        Assert.False(layout.FindElement(MiniGameUiController.PassId)!.Visible);
        Assert.True(layout.FindElement(MiniGameUiController.ResignId)!.Visible);
    }

    [Fact]
    public void UsingABoard_OnlyOpensTheWindow()
    {
        var (controller, _) = Bind();
        using var __ = controller;
        var use = new ItemPolicyAction(ItemPolicyActionKind.BeginGame, Board);

        controller.HandlePolicyAction(use);
        controller.HandlePolicyAction(use);
        controller.HandlePolicyAction(new ItemPolicyAction(ItemPolicyActionKind.OpenSalvage, Board));

        // Joining is the runtime's part; the window never begins a game.
        Assert.Equal(2, _shows);
        Assert.Equal(RuntimeChessStage.NotPlaying, _chess.Snapshot.Stage);
    }

    [Fact]
    public void ClosingTheInterface_TakesTheBoardHandlerOffTheItemOwner()
    {
        // Mounted and torn down the way the interface does it: the window
        // is registered with its controller, and shutting the window manager
        // down disposes that controller. The item owner lives on, and a
        // later use of a board must no longer reach the old window.
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Board,
            Type = ItemType.Gameboard,
            PublicWeenieBitfield = (uint)PublicWeenieFlags.Stuck,
        });
        using var items = new RuntimeItemInteraction(
            objects,
            new RuntimeInteractionTransactionState(
                new InventoryTransactionState(objects, new VendorState())),
            new InteractionState(),
            playerGuid: () => 0x50000001u,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null);
        ImportedLayout layout = FixtureLoader.LoadMiniGame();
        MiniGameUiController controller = MiniGameUiController.Bind(
            layout,
            close: null,
            Bindings(),
            MiniGameUiController.ReadPieceImages(FixtureLoader.LoadMiniGameInfos()),
            items)!;
        var root = new UiRoot { Width = 1280, Height = 1024 };
        RetailWindowFrame.Mount(
            root,
            layout.Root,
            id => (id, 16, 16),
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.MiniGame,
                Visible = false,
                Controller = controller,
            });
        Assert.True(items.TryBeginGame(Board));
        Assert.Equal(1, _shows);

        root.WindowManager.Dispose();
        items.TryBeginGame(Board);

        Assert.Equal(1, _shows);
    }

    [Fact]
    public void APieceImageTooWideToBeAnId_DrawsNoPieceInsteadOfFailing()
    {
        ElementInfo info = FixtureLoader.LoadMiniGameInfos();
        Assert.True(FindBoardInfo(info)!.TryGetEffectiveProperty(
            MiniGameUiController.PieceImagesPropertyId, out UiPropertyValue list));
        list.ArrayValue[0].UnsignedValue = 1UL << 40;

        uint[] images = MiniGameUiController.ReadPieceImages(info);

        Assert.Equal(0u, images[0]);
        Assert.Equal(WhitePieceSprites + 1u, images[1]);
    }

    private static ElementInfo? FindBoardInfo(ElementInfo info)
    {
        if (info.Id == MiniGameUiController.BoardId)
            return info;
        foreach (ElementInfo child in info.Children)
        {
            if (FindBoardInfo(child) is { } found)
                return found;
        }
        return null;
    }

    [Fact]
    public void White_SeesItsBackRankAlongTheBottom()
    {
        var (controller, layout) = Bind();
        using var _ = controller;
        Seat(controller, ChessTeam.White);

        UiDatElement[] squares = Squares(layout);

        Assert.Equal(WhitePieceSprites + 5u, squares[60].RuntimeImageTexture); // e1 king
        Assert.Equal(BlackPieceSprites + 3u, squares[0].RuntimeImageTexture);  // a8 rook
        Assert.Equal(0u, squares[36].RuntimeImageTexture);                                         // e4 empty
    }

    [Fact]
    public void Black_SeesTheBoardTurnedRound()
    {
        var (controller, layout) = Bind();
        using var _ = controller;
        Seat(controller, ChessTeam.Black);

        UiDatElement[] squares = Squares(layout);

        Assert.Equal(WhitePieceSprites + 5u, squares[3].RuntimeImageTexture);  // e1 king
        Assert.Equal(BlackPieceSprites + 5u, squares[59].RuntimeImageTexture); // e8 king
    }

    [Theory]
    [InlineData(ChessPieceType.Pawn, 0u)]
    [InlineData(ChessPieceType.Bishop, 1u)]
    [InlineData(ChessPieceType.Knight, 2u)]
    [InlineData(ChessPieceType.Rook, 3u)]
    [InlineData(ChessPieceType.Queen, 4u)]
    [InlineData(ChessPieceType.King, 5u)]
    public void PieceSprites_AreReadFromTheLayoutInTheGamesOrder(ChessPieceType type, uint offset)
    {
        var (controller, _) = Bind();
        using var __ = controller;

        Assert.Equal(WhitePieceSprites + offset, controller.PieceSprite(new(type, ChessTeam.White)));
        Assert.Equal(BlackPieceSprites + offset, controller.PieceSprite(new(type, ChessTeam.Black)));
    }

    [Fact]
    public void ALayoutWithoutPieceImages_DrawsNoPieces()
    {
        ElementInfo info = FixtureLoader.LoadMiniGameInfos();
        Assert.All(MiniGameUiController.ReadPieceImages(
            new ElementInfo { Id = info.Id }), image => Assert.Equal(0u, image));

        MiniGameUiController controller = MiniGameUiController.Bind(
            FixtureLoader.LoadMiniGame(),
            close: null,
            new MiniGameUiController.ChessBindings(
                _chess, CreateSquare, sprite => sprite,
                (_, _) => { }, () => { }, _ => { }, () => { },
                (_, _) => { }, () => { }, () => { }, _ => { }))!;
        using var _ = controller;

        Assert.Equal(0u, controller.PieceSprite(new(ChessPieceType.King, ChessTeam.White)));
    }

    [Fact]
    public void ClickingAPieceThenASquare_MovesItAndSendsTheMove()
    {
        var (controller, layout) = Bind();
        using var _ = controller;
        Seat(controller, ChessTeam.White);
        UiDatElement[] squares = Squares(layout);

        squares[52].OnEvent(new UiEvent(0, squares[52], UiEventType.Click)); // e2
        Assert.Equal(Sq("e2"), controller.Selected);
        Assert.True(Highlighted(squares[52]));

        controller.ClickSquare(36); // e4
        controller.Tick();

        Assert.Equal([(Sq("e2"), Sq("e4"))], _moves);
        Assert.Null(controller.Selected);
        Assert.False(Highlighted(squares[52]));
        Assert.Equal(WhitePieceSprites, squares[36].RuntimeImageTexture);
        Assert.Equal(0u, squares[52].RuntimeImageTexture);
    }

    [Fact]
    public void ClickingTheSelectedSquareAgain_PutsThePieceDown()
    {
        var (controller, _) = Bind();
        using var __ = controller;
        Seat(controller, ChessTeam.White);

        controller.ClickSquare(52);
        controller.ClickSquare(52);

        Assert.Null(controller.Selected);
        Assert.Empty(_moves);
    }

    [Fact]
    public void RefusedMove_SendsNothingAndDropsTheSelection()
    {
        var (controller, _) = Bind();
        using var __ = controller;
        Seat(controller, ChessTeam.White);

        controller.ClickSquare(52); // e2
        controller.ClickSquare(28); // e5

        Assert.Empty(_moves);
        Assert.Null(controller.Selected);
    }

    [Fact]
    public void OpponentsPiecesAndOffTurnClicks_DoNothing()
    {
        var (controller, _) = Bind();
        using var __ = controller;
        Seat(controller, ChessTeam.Black);

        controller.ClickSquare(51); // e7 from Black's side, but it is White's turn
        Assert.Null(controller.Selected);

        _chess.ApplyOpponentTurn(new(Board, 0, new(
            AcDream.Core.Net.Messages.ChessMoveType.FromTo, 0x50000002u, 4, 1, 4, 3, 0u)));
        controller.Tick();
        controller.ClickSquare(11); // e2, a White pawn
        Assert.Null(controller.Selected);
        controller.ClickSquare(51); // e7
        Assert.Equal(Sq("e7"), controller.Selected);
    }

    [Fact]
    public void Resign_AsksFirstThenQuitsAndClosesTheWindow()
    {
        var (controller, layout) = Bind();
        using var _ = controller;
        Seat(controller, ChessTeam.White);
        var resign = (UiButton)layout.FindElement(MiniGameUiController.ResignId)!;

        resign.OnEvent(new UiEvent(0, resign, UiEventType.Click));
        Assert.Equal(MiniGameUiController.ResignConfirmation, _confirmMessage);
        _confirm!(false);
        Assert.Equal(0, _quits);

        resign.OnEvent(new UiEvent(0, resign, UiEventType.Click));
        _confirm!(true);
        controller.Tick();

        Assert.Equal(1, _quits);
        Assert.Equal(RuntimeChessStage.NotPlaying, _chess.Snapshot.Stage);
        Assert.Equal(1, _hides);
    }

    [Fact]
    public void Stalemate_SendsTheOfferThenTheRetraction()
    {
        var (controller, layout) = Bind();
        using var _ = controller;
        Seat(controller, ChessTeam.White);
        var stalemate = (UiButton)layout.FindElement(MiniGameUiController.StalemateId)!;

        stalemate.OnEvent(new UiEvent(0, stalemate, UiEventType.Click));
        stalemate.OnEvent(new UiEvent(0, stalemate, UiEventType.Click));

        Assert.Equal([true, false], _stalemates);
    }

    [Fact]
    public void Indicator_LightsWhileAGameIsOn()
    {
        var (controller, _) = Bind();
        using var __ = controller;

        Seat(controller, ChessTeam.White);
        _chess.ApplyGameOver(new(Board, 0));
        controller.Tick();

        Assert.Equal([true, false], _indicator);
        Assert.Equal(0, _hides);
    }
}
