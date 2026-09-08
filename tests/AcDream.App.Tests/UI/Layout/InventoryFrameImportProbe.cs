using System;
using System.IO;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Items;
using DatReaderWriter;
using DatReaderWriter.Options;
using Xunit;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "InstalledDat")]
public class InventoryFrameImportProbe
{
    private const uint Frame          = 0x21000023u;
    private const uint Backdrop       = 0x100001D0u;   // full-window Alphablend backdrop (ZLevel 100)
    private const uint BackpackPanel  = 0x100001CEu;
    private const uint ItemsPanel     = 0x100001CFu;
    private const uint PaperdollPanel = 0x100001CDu;
    private const uint BurdenMeter    = 0x100001D9u;
    private const uint ContentsGrid   = 0x100001C6u;

    private static string? DatDir()
    {
        var d = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            "Documents", "Asheron's Call");
        return Directory.Exists(d) ? d : null;
    }

    [Fact]
    public void Paperdoll_equip_slots_resolve_to_item_lists()
    {
        var datDir = DatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");   // CI: no live dat — skip

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var layout = LayoutImporter.Import(dats, Frame, _ => (0u, 0, 0), null);
        Assert.NotNull(layout);

        foreach (uint id in new[] { 0x100005ABu, 0x100001E1u, 0x100001DFu, 0x100005E9u, 0x1000058Eu, 0x100001DCu })
        {
            var el = layout!.FindElement(id);
            Assert.True(el is UiItemList,
                $"equip slot 0x{id:X8} resolved to {el?.GetType().Name ?? "null"}, expected UiItemList");
        }
    }

    [Fact]
    public void Mounted_panels_sit_in_front_of_the_backdrop()
    {
        var datDir = DatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");   // CI: no live dat — skip (this is a smoke test)

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var layout = LayoutImporter.Import(dats, Frame, _ => (0u, 0, 0), null);
        Assert.NotNull(layout);

        var backdrop = layout!.FindElement(Backdrop);
        Assert.NotNull(backdrop);

        Assert.NotNull(layout.FindElement(BurdenMeter));    // backpack burden meter
        Assert.NotNull(layout.FindElement(ContentsGrid));

        foreach (var (id, name) in new[]
        {
            (BackpackPanel, "backpack"), (ItemsPanel, "3D-items"), (PaperdollPanel, "paperdoll"),
        })
        {
            var panel = layout.FindElement(id);
            Assert.True(panel is not null, $"{name} panel 0x{id:X8} missing from the imported tree");
            Assert.True(panel!.ZOrder > backdrop!.ZOrder,
                $"{name} panel ZOrder {panel.ZOrder} must be > backdrop ZOrder {backdrop.ZOrder} " +
                "(else the Alphablend backdrop overpaints/washes out the panel content)");
        }
    }

    [Fact]
    public void Close_button_resolves_and_invokes_controller_close_callback()
    {
        var datDir = DatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");   // CI: no live dat - skip

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var layout = LayoutImporter.Import(dats, Frame, _ => (0u, 0, 0), null);
        Assert.NotNull(layout);

        var close = layout!.FindElement(WindowChromeController.InventoryCloseButtonId);
        Assert.NotNull(close);
        Assert.True(close is UiButton or UiDatElement,
            $"inventory close button resolved to {close?.GetType().Name ?? "null"}");

        int closes = 0;
        InventoryController.Bind(
            layout,
            new ClientObjectTable(),
            playerGuid: static () => 0u,
            iconIds: static (_, _, _, _, _) => 0u,
            strength: static () => 100,
            selection: new AcDream.Core.Selection.SelectionState(),
            datFont: null,
            onClose: () => closes++);

        close!.OnEvent(new UiEvent(0u, close, UiEventType.Click));

        Assert.Equal(1, closes);
    }
}
