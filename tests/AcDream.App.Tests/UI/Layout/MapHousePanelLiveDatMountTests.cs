using System.IO;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Net.Messages;
using DatReaderWriter;
using DatReaderWriter.Options;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "InstalledDat")]
public sealed class MapHousePanelLiveDatMountTests
{
    private static string DatDirectory =>
        Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents",
            "Asheron's Call");

    [InstalledDatFact]
    public void MountRecipe_ResolvesPlayerAndHouseIcons_UnderTheMapWidget()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);

        Assert.Null(LayoutImporter.ImportInfos(
            dats,
            MapHousePanelController.HostLayoutId,
            MapPageController.PlayerIconId));
        Assert.Null(LayoutImporter.ImportInfos(
            dats,
            MapHousePanelController.HostLayoutId,
            MapPageController.HouseIconId));

        // 2) The full panel-slot resolve — what MountMapHousePanel actually
        //    imports — DOES materialize both icons, nested under m_pMap.
        ElementInfo? rootInfo = LayoutImporter.ImportInfos(
            dats,
            MapHousePanelController.HostLayoutId,
            MapHousePanelController.SlotElementId);
        Assert.NotNull(rootInfo);

        // ── The production mount recipe (sprites/fonts stubbed) ──────────
        ImportedLayout layout = LayoutImporter.Build(rootInfo!, static _ => (0u, 0, 0), null);

        UiElement? ResolveTemplate(uint layoutId, uint elementId)
        {
            ElementInfo? info = LayoutImporter.ImportInfos(dats, layoutId, elementId);
            return info is null
                ? null
                : LayoutImporter.Build(info, static _ => (0u, 0, 0), null).Root;
        }

        UiElement? BuildIcon(ElementInfo info)
            => LayoutImporter.Build(info, static _ => (0u, 0, 0), null).Root;

        uint playerCell = 0u;
        var callbacks = new MapHousePanelController.Callbacks(
            Toggle: static () => { },
            Map: new MapPageController.Bindings(
                CurrentCalendar: static () => default,
                PlayerCellId: () => playerCell,
                HousePosition: static () => (CreateObject.ServerPosition?)null,
                TemplateResolver: ResolveTemplate,
                IconBuilder: BuildIcon),
            House: new HousePageController.Bindings(
                Lines: static () => Array.Empty<string>(),
                TemplateResolver: ResolveTemplate));

        MapHousePanelController? controller =
            MapHousePanelController.Bind(rootInfo!, layout, callbacks);
        Assert.NotNull(controller);

        // ── Pin 1: both icons resolve as BUILT elements ──────────────────
        UiElement? map = UiElement.FindDescendant(
            controller!.Root, MapPageController.MapWidgetId);
        Assert.NotNull(map);

        UiElement? playerIcon = UiElement.FindDescendant(
            controller.Root, MapPageController.PlayerIconId);
        UiElement? houseIcon = UiElement.FindDescendant(
            controller.Root, MapPageController.HouseIconId);
        Assert.NotNull(playerIcon);
        Assert.NotNull(houseIcon);

        Assert.Same(map, playerIcon!.Parent);
        Assert.Same(map, houseIcon!.Parent);
        Assert.True(playerIcon.Width > 0 && playerIcon.Height > 0,
            $"player icon built with degenerate extent {playerIcon.Width}x{playerIcon.Height}");
        Assert.True(houseIcon.Width > 0 && houseIcon.Height > 0,
            $"house icon built with degenerate extent {houseIcon.Width}x{houseIcon.Height}");

        playerIcon.ApplyAnchor(map!.Width, map.Height);
        houseIcon.ApplyAnchor(map.Width, map.Height);
        Assert.False(playerIcon.Visible);

        playerCell = 0x11CE0001u;                          // Arwic (independently pinned:
        controller.Tick(MapPageController.RefreshIntervalSeconds + 0.01);
        Assert.True(playerIcon.Visible);

        (float expectedLeft, float expectedTop) = MapPageController.ComputeMarkerPosition(
            markerX0: 6, markerX1: 247, markerY0: 8, markerY1: 258,
            (int)playerIcon.Width, (int)playerIcon.Height, -88.30000000000001, 62.900000000000006);
        Assert.Equal(expectedLeft, playerIcon.Left);
        Assert.Equal(expectedTop, playerIcon.Top);

        playerIcon.ApplyAnchor(map.Width, map.Height);     // the next frame's pass
        Assert.Equal(expectedLeft, playerIcon.Left);
        Assert.Equal(expectedTop, playerIcon.Top);
    }
}
