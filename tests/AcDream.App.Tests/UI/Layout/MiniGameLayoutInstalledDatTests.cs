using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Chess;
using AcDream.Runtime.Gameplay;
using DatReaderWriter;
using DatReaderWriter.Options;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "InstalledDat")]
public sealed class MiniGameLayoutInstalledDatTests
{
    [InstalledDatFact]
    public void Layout2100001E_BuildsTheBoardFromItsSquareTemplate()
    {
        string datDirectory = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        using var dats = new DatCollection(datDirectory, DatAccessType.Read);
        ElementInfo rootInfo = Assert.IsType<ElementInfo>(LayoutImporter.ImportInfos(
            dats, MiniGameUiController.LayoutId, MiniGameUiController.RootId));
        ImportedLayout layout = Assert.IsType<ImportedLayout>(LayoutImporter.Import(
            dats, MiniGameUiController.LayoutId, MiniGameUiController.RootId, static _ => (0u, 0, 0), null));

        Assert.Equal("Resign", (layout.FindElement(MiniGameUiController.ResignId) as UiButton)?.Label);
        Assert.Equal("Pass", (layout.FindElement(MiniGameUiController.PassId) as UiButton)?.Label);
        Assert.Equal("Stalemate", (layout.FindElement(MiniGameUiController.StalemateId) as UiButton)?.Label);
        var board = Assert.IsType<UiTemplateListBox>(layout.FindElement(MiniGameUiController.BoardId));
        Assert.Equal(
            (MiniGameUiController.LayoutId, MiniGameUiController.SquareTemplateId),
            (board.Templates[0].TemplateLayoutId, board.Templates[0].TemplateElementId));

        ImportedLayout square = Assert.IsType<ImportedLayout>(LayoutImporter.Import(
            dats, MiniGameUiController.LayoutId, MiniGameUiController.SquareTemplateId,
            static _ => (0u, 0, 0), null));
        var root = Assert.IsType<UiDatElement>(square.Root);
        Assert.Equal((32f, 32f), (root.Width, root.Height));
        Assert.False(Assert.IsAssignableFrom<UiElement>(
            square.FindElement(MiniGameUiController.SelectionId)).Visible);

        using MiniGameUiController controller = MiniGameUiController.Bind(
            layout,
            close: null,
            new MiniGameUiController.ChessBindings(
                new RuntimeChessState(),
                () => LayoutImporter.Import(
                    dats, MiniGameUiController.LayoutId, MiniGameUiController.SquareTemplateId,
                    static _ => (0u, 0, 0), null)?.Root,
                static sprite => sprite,
                static (_, _) => { }, static () => { }, static _ => { }, static () => { },
                static (_, _) => { }, static () => { }, static () => { }, static _ => { }),
            MiniGameUiController.ReadPieceImages(rootInfo))!;
        Assert.Equal(64, Assert.Single(Assert.Single(board.Children).Children).Children.Count);

        // White's pawn, bishop, knight, rook, queen and king, then Black's.
        Assert.Equal(
            Enumerable.Range(0, 12).Select(index => 0x06004CFDu + (uint)index),
            MiniGameUiController.ReadPieceImages(rootInfo));
        Assert.Equal(0x06004D08u, controller.PieceSprite(new(ChessPieceType.King, ChessTeam.Black)));
    }
}
