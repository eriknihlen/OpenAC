using System.IO;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Items;
using AcDream.Core.Selection;
using AcDream.Automation.Items;

namespace AcDream.App.Tests.UI.Layout;

/// <summary>
/// The doll mask is authored, so its widget class and its extent come from the
/// installed data, not from a fixture. #64 shipped once against a fixture that
/// guessed the class wrong and did nothing at all in the live client; this lane
/// is what would have caught that.
/// </summary>
[Trait("Lane", "InstalledDat")]
public sealed class PaperdollMaskInstalledDatTests
{
    private const uint InventoryLayoutId = 0x21000023u;
    private const uint Player = 0x50000001u;

    [Fact]
    public void AuthoredDollMask_IsInteractive_AndMatchesTheClickMapExtent()
    {
        string? datDir = ResolveDatDir();
        if (datDir is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new AcDream.App.Tests.BoundedTestDatCollection(datDir!);
        ElementInfo? rootInfo = LayoutImporter.ImportInfos(dats, InventoryLayoutId);
        Assert.NotNull(rootInfo);

        ImportedLayout layout = LayoutImporter.Build(
            rootInfo!, resolve: _ => (1u, 16, 16), datFont: null);

        UiElement? mask = layout.FindElement(PaperdollController.DollDragMaskId);
        Assert.True(
            mask is not null,
            $"the authored doll mask 0x{PaperdollController.DollDragMaskId:X8} is not in the imported layout.");
        Assert.False(mask!.ClickThrough);

        PaperdollClickMap? clickMap = PaperdollClickMap.Load(dats);
        Assert.True(clickMap is not null, "the authored paperdoll click map did not load.");

        // The hit test samples the map one pixel per pixel with no scaling, so
        // the mask and the map have to be the same size for the point the mask
        // reports to name the region the player aimed at.
        Assert.Equal(clickMap!.Width, (int)mask.Width);
        Assert.Equal(clickMap.Height, (int)mask.Height);

        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = Player });
        objects.AddOrUpdate(new ClientObject { ObjectId = 0xA01u, WielderId = Player });
        objects.MoveItem(0xA01u, Player, newSlot: -1, newEquipLocation: EquipMask.ChestArmor);

        var selection = new SelectionState();
        var examines = new List<uint>();
        var interaction = new ItemInteractionController(
            objects,
            new AcDream.Runtime.Gameplay.RuntimeInteractionTransactionState(
                new InventoryTransactionState(objects)),
            new InteractionState(),
            () => Player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null,
            sendExamine: examines.Add,
            sendPutItemInContainer: null,
            systemMessage: null);

        PaperdollController.Bind(
            layout, objects, () => Player,
            iconIds: (_, _, _, _, _) => 0x1234u,
            selection: selection,
            itemInteraction: interaction,
            clickMap: clickMap,
            resolveAppropriateName: ItemTooltipCaptionNames.Resolve);

        Assert.True(
            mask.PointerRegion is not null,
            $"the doll mask imported as {mask.GetType().Name} and the panel left it without a "
            + "pointer region, so it is inert in the live client whatever the fixtures say.");
        Assert.True(mask.IsDragSource);

        (int x, int y) = FindRegionPixel(clickMap, EquipMask.ChestArmor);

        // Drive the mask through a real root the way the window does, so the
        // press has to survive hit-testing against the viewport underneath it.
        var root = new UiRoot { Width = 1024, Height = 768 };
        root.AddChild(layout.Root);
        var origin = mask.ScreenPosition;
        int sx = (int)origin.X + x, sy = (int)origin.Y + y;
        Assert.Same(mask, root.Pick(sx, sy));

        root.OnMouseDown(UiMouseButton.Left, sx, sy);
        root.OnMouseUp(UiMouseButton.Left, sx, sy);
        Assert.Equal(0xA01u, selection.SelectedObjectId);

        root.OnMouseDown(UiMouseButton.Right, sx, sy);
        root.OnMouseUp(UiMouseButton.Right, sx, sy);
        Assert.Equal(new uint[] { 0xA01u }, examines);

        root.OnMouseDown(UiMouseButton.Left, sx, sy);
        root.OnMouseMove(sx, sy + 20);
        var lifted = Assert.IsType<ItemDragPayload>(root.DragPayload);
        Assert.Equal(0xA01u, lifted.ObjId);
        Assert.Equal(ItemDragSource.Equipment, lifted.SourceKind);
        root.OnMouseUp(UiMouseButton.Left, sx, sy + 20);
    }

    private static (int X, int Y) FindRegionPixel(PaperdollClickMap map, EquipMask region)
    {
        for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++)
                if ((map.GetBodyLocation(x, y) & region) != EquipMask.None)
                    return (x, y);
        Assert.Fail($"the authored click map has no {region} region.");
        return (0, 0);
    }

    private static string? ResolveDatDir()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && Directory.Exists(fromEnvironment))
            return fromEnvironment;

        string installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents",
            "Asheron's Call");
        return Directory.Exists(installed) ? installed : null;
    }
}
