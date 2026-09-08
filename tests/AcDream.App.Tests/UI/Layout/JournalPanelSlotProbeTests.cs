using System;
using System.IO;
using System.Linq;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Content;
using DatReaderWriter;
using DatReaderWriter.Options;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "InstalledDat")]
public sealed class JournalPanelSlotProbeTests
{
    private const uint SlotKeyPropertyId = 0x10000029u;
    private const uint TabTablePropertyId = 0x2Eu;
    private const uint TabButtonPropertyId = 0x30u;
    private const uint TabPagePropertyId = 0x31u;
    private const uint TabDefaultPropertyId = 0x32u;

    private const uint ToolbarLayoutId = 0x21000016u;
    private const uint JournalToolbarButtonId = 0x1000055Au;

    private static ElementInfo Import(uint layoutId, uint rootElementId = 0u)
    {
        string? datDir = ContentConformanceDatDir();
        if (datDir is null)
        {
            Assert.Fail(
                "Lane=InstalledDat requires an installed retail DAT directory; "
                + "see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        ElementInfo? info = rootElementId == 0u
            ? LayoutImporter.ImportInfos(adapter, layoutId)
            : LayoutImporter.ImportInfos(adapter, layoutId, rootElementId);
        Assert.NotNull(info);
        return info!;
    }

    private static string? ContentConformanceDatDir()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv))
            return fromEnv;

        string def = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents", "Asheron's Call");
        return Directory.Exists(def) ? def : null;
    }

    private static UiPropertyValue? Property(ElementInfo info, uint propertyId)
    {
        foreach (UiStateInfo state in info.States.Values)
        {
            if (state.Properties.Values.TryGetValue(propertyId, out UiPropertyValue? value))
                return value;
        }
        return null;
    }

    [Fact]
    public void TheSlotAuthorsTheCatalogPanelId()
    {
        ElementInfo slot = Import(
            JournalPanelController.HostLayoutId,
            JournalPanelController.SlotElementId);

        UiPropertyValue? key = Property(slot, SlotKeyPropertyId);

        Assert.NotNull(key);
        Assert.Equal(RetailPanelCatalog.Journal, (uint)key!.UnsignedValue);
    }

    [Fact]
    public void TheAuthoredTabTablePairsContractsWithTheGmContractsUiPage()
    {
        // Read from the table, never inferred from x-order.
        ElementInfo slot = Import(
            JournalPanelController.HostLayoutId,
            JournalPanelController.SlotElementId);

        UiPropertyValue? tabs = Property(slot, TabTablePropertyId);
        Assert.NotNull(tabs);
        Assert.Equal(3, tabs!.ArrayValue.Count);

        UiPropertyValue first = tabs.ArrayValue[0];
        Assert.Equal(
            0x100005D3u,
            (uint)first.StructValue[TabButtonPropertyId].UnsignedValue);
        Assert.Equal(
            JournalPanelController.ContractsPageId,
            (uint)first.StructValue[TabPagePropertyId].UnsignedValue);
    }

    [Fact]
    public void ContractsIsTheAuthoredDefaultTab()
    {
        // Opening on the wrong tab would look like the panel is empty.
        ElementInfo slot = Import(
            JournalPanelController.HostLayoutId,
            JournalPanelController.SlotElementId);

        UiPropertyValue tabs = Property(slot, TabTablePropertyId)!;

        UiPropertyValue defaultTab = Assert.Single(
            tabs.ArrayValue,
            t => t.StructValue.TryGetValue(TabDefaultPropertyId, out UiPropertyValue? d)
                && d.BoolValue);

        Assert.Equal(
            JournalPanelController.ContractsPageId,
            (uint)defaultTab.StructValue[TabPagePropertyId].UnsignedValue);
    }

    [Fact]
    public void TheToolbarButtonAuthorsTheSamePanelId()
    {
        ElementInfo toolbar = Import(ToolbarLayoutId);

        ElementInfo? button = Find(toolbar, JournalToolbarButtonId);
        Assert.NotNull(button);

        UiPropertyValue? key = Property(button!, SlotKeyPropertyId);
        Assert.NotNull(key);
        Assert.Equal(RetailPanelCatalog.Journal, (uint)key!.UnsignedValue);
    }

    [Fact]
    public void TheContractsPageCarriesEveryChildTheControllerResolves()
    {
        ElementInfo slot = Import(
            JournalPanelController.HostLayoutId,
            JournalPanelController.SlotElementId);

        ElementInfo page = Assert.IsType<ElementInfo>(
            Find(slot, JournalPanelController.ContractsPageId));

        foreach (uint childId in new[]
        {
            0x100005CFu,   // the list
            0x100005DEu,   // description
            0x100005DFu,   // status value
            0x100005E0u,
            0x100005E1u,
            0x100005E2u,   // quest location
            0x100005E3u,   // timed value
            0x100005DCu,   // Abandon
        })
        {
            Assert.True(
                Find(page, childId) is not null,
                $"contracts page is missing authored child 0x{childId:X8}");
        }
    }

    private static ElementInfo? Find(ElementInfo root, uint id)
    {
        if (root.Id == id) return root;
        foreach (ElementInfo child in root.Children)
        {
            ElementInfo? hit = Find(child, id);
            if (hit is not null) return hit;
        }
        return null;
    }
}
