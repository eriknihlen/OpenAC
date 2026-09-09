using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Content;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "InstalledDat")]
public sealed class TooltipLiveDatTests
{
    private static string DatDirectory =>
        System.Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
        ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "Documents",
            "Asheron's Call");

    private const uint TooltipCatalogLayoutId = 0x21000041u;

    private static readonly uint[] PopupSkinRootIds =
        [0x10000487u, 0x10000395u, 0x10000397u, 0x10000398u];

    private const uint TooltipTextChildId = 0x10000396u;

    [InstalledDatFact]
    public void TooltipCatalog_EveryPopupSkin_SharesTheSameTextChild()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);

        ElementInfo? root = LayoutImporter.ImportInfos(dats, TooltipCatalogLayoutId);
        Assert.NotNull(root);
        Assert.Equal(0u, root!.Id);
        Assert.Equal(800f, root.Width);
        Assert.Equal(600f, root.Height);

        foreach (uint skinRootId in PopupSkinRootIds)
        {
            ElementInfo skin = Assert.Single(root.Children, c => c.Id == skinRootId);
            Assert.Equal(TooltipTextChildId, skin.TooltipTextChildElementId);

            ElementInfo textChild = Assert.Single(
                AllDescendants(skin), e => e.Id == TooltipTextChildId);
            Assert.Equal(12u, textChild.Type); // UIElement_Text
        }
    }

    [InstalledDatFact]
    public void TooltipCatalog_ImportsThroughLayoutImporter_WithTextChildResolvable()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);

        ImportedLayout? popup = LayoutImporter.Import(
            dats,
            TooltipCatalogLayoutId,
            0x10000487u,
            _ => (0u, 0, 0),
            null);
        Assert.NotNull(popup);
        Assert.Equal(30f, popup!.Root.Width);
        Assert.Equal(30f, popup.Root.Height);
        Assert.Equal(TooltipTextChildId, popup.Root.AuthoredTooltipTextChildElementId);

        UiElement? textChild = popup.FindElement(TooltipTextChildId);
        Assert.IsType<UiText>(textChild);
    }

    [InstalledDatFact]
    public void KnownElement_AuthorsAllFiveTooltipProperties_WithResolvableText()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);

        ElementInfo? tree = LayoutImporter.ImportInfos(dats, 0x21000005u);
        Assert.NotNull(tree);
        ElementInfo rotateLeft = Assert.Single(AllDescendants(tree!), e => e.Id == 0x100005A4u);

        Assert.True(rotateLeft.TooltipEnabled);
        Assert.Equal(0x10000487u, rotateLeft.TooltipRootElementId);
        Assert.Equal(TooltipCatalogLayoutId, rotateLeft.TooltipLayoutDid);
        Assert.NotNull(rotateLeft.TooltipText);

        var strings = new DatStringResolver(dats);
        string? resolved = DatWidgetFactory.ResolveTooltipText(rotateLeft, strings.Resolve);
        Assert.Equal("Rotate left.", resolved);
    }

    [InstalledDatFact]
    public void ClientWideSweep_FindsKnownLandmarksAndAFloorCount()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);

        var withProperties = new List<(uint LayoutId, uint ElementId, bool HasText, bool Showable)>();
        foreach (uint layoutId in dats.GetAllIdsOfType<LayoutDesc>())
        {
            ElementInfo? tree;
            try { tree = LayoutImporter.ImportInfos(dats, layoutId); }
            catch { continue; }
            if (tree is null) continue;

            foreach (ElementInfo e in AllDescendants(tree))
            {
                bool any = e.TooltipRootElementId != 0 || e.TooltipLayoutDid != 0
                    || e.TooltipText.HasValue || e.TooltipEnabled
                    || e.TooltipDelaySeconds.HasValue;
                if (any)
                {
                    bool showable = e.TooltipEnabled && e.TooltipText.HasValue
                        && e.TooltipLayoutDid != 0 && e.TooltipRootElementId != 0;
                    withProperties.Add((layoutId, e.Id, e.TooltipText.HasValue, showable));
                }
            }
        }

        Console.WriteLine($"[409-DAT] {withProperties.Count} elements author >=1 tooltip property "
            + $"({withProperties.Count(f => f.HasText)} with literal StringInfo text, "
            + $"{withProperties.Count(f => f.Showable)} pass the full OnTooltipShow gate).");

        Assert.Contains(withProperties, f => f.LayoutId == 0x21000005u && f.ElementId == 0x100005A4u);
        Assert.True(withProperties.Count >= 400,
            $"expected at least 400 tooltip-property-authoring elements, found {withProperties.Count}.");
        Assert.True(withProperties.Count(f => f.HasText) >= 200,
            $"expected at least 200 elements with literal tooltip text, found {withProperties.Count(f => f.HasText)}.");
        Assert.True(withProperties.Count(f => f.Showable) >= 200,
            $"expected at least 200 fully showable elements, found {withProperties.Count(f => f.Showable)}.");
    }

    [InstalledDatFact]
    public void OptionsToggleRowTemplate_AuthorsThePopupLocatorButNoLiteralText()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);

        ImportedLayout? row = LayoutImporter.Import(
            dats, OptionsPanelLayoutId, OptionsToggleRowTemplateId, _ => (0u, 0, 0), null);
        Assert.NotNull(row);

        UiElement checkbox = Assert.Single(
            AllWidgets(row!.Root), w => w.DatElementId == OptionsToggleCheckboxId);

        Assert.Equal(0x10000397u, checkbox.AuthoredTooltipRootElementId);   // P0x47
        Assert.Equal(TooltipCatalogLayoutId, checkbox.AuthoredTooltipLayoutDid); // P0x48
        Assert.True(checkbox.AuthoredTooltipEnabled);                        // P0x4B
        Assert.True(string.IsNullOrEmpty(checkbox.AuthoredTooltipText));     // no P0x49
        // ...and it is a real hover target, so UiRoot.UpdateHover can select it.
        Assert.False(checkbox.ClickThrough);
        Assert.Equal(OptionsPanelLayoutId, checkbox.SourceLayoutDid);
    }

    private const uint OptionsPanelLayoutId = 0x2100002Bu;
    private const uint OptionsToggleRowTemplateId = 0x10000218u;
    private const uint OptionsToggleCheckboxId = 0x10000219u;

    private const uint UiItemElementType = 0x10000032u;

    [InstalledDatFact]
    public void UiItemCatalog_EveryPrototype_SharesTheSamePopupLocator()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);

        ElementInfo? catalog = LayoutImporter.ImportInfos(dats, ItemListCellTemplate.CatalogLayoutId);
        Assert.NotNull(catalog);
        Assert.True(catalog!.Children.Count >= 40,
            $"expected the shared UIItem catalog to hold dozens of prototypes, found {catalog.Children.Count}.");

        var prototypes = catalog.Children.Where(c => c.Type == UiItemElementType).ToList();
        Assert.True(prototypes.Count >= 30,
            $"expected the shared UIItem catalog to hold dozens of type-0x10000032 prototypes, found {prototypes.Count}.");

        foreach (ElementInfo prototype in prototypes)
        {
            Assert.True(prototype.TooltipRootElementId == UiItemTooltipRootElementId,
                $"prototype 0x{prototype.Id:X8} authors P0x47=0x{prototype.TooltipRootElementId:X8}, expected 0x{UiItemTooltipRootElementId:X8}.");
            Assert.True(prototype.TooltipLayoutDid == TooltipCatalogLayoutId,
                $"prototype 0x{prototype.Id:X8} authors P0x48=0x{prototype.TooltipLayoutDid:X8}, expected 0x{TooltipCatalogLayoutId:X8}.");
            Assert.False(prototype.TooltipText.HasValue,
                $"prototype 0x{prototype.Id:X8} unexpectedly authors literal P0x49 tooltip text.");
        }

        ElementInfo? inventoryTree = LayoutImporter.ImportInfos(dats, 0x21000023u);
        ElementInfo? contentsGrid = inventoryTree is null
            ? null : AllDescendants(inventoryTree).FirstOrDefault(x => x.Id == 0x100001C6u);
        Assert.NotNull(contentsGrid);
        Assert.True(contentsGrid!.TryGetEffectiveProperty(0x1000000Eu, out UiPropertyValue protoProp));
        Assert.NotEqual(0u, (uint)protoProp.UnsignedValue);

        ElementInfo? toolbarTree = LayoutImporter.ImportInfos(dats, 0x21000016u);
        ElementInfo? toolbarSlot = toolbarTree is null
            ? null : AllDescendants(toolbarTree).FirstOrDefault(x => x.Id == 0x100001A7u);
        Assert.NotNull(toolbarSlot);
        Assert.True(toolbarSlot!.TryGetEffectiveProperty(0x1000000Eu, out UiPropertyValue toolbarProtoProp));
        Assert.NotEqual(0u, (uint)toolbarProtoProp.UnsignedValue);
        // Different lists really do select different prototypes.
        Assert.NotEqual((uint)protoProp.UnsignedValue, (uint)toolbarProtoProp.UnsignedValue);
    }

    [InstalledDatFact]
    public void SmartBoxWrapper_HasNoAuthoredElementDesc_AnywhereInstalled()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);

        int found = 0;
        foreach (uint layoutId in dats.GetAllIdsOfType<LayoutDesc>())
        {
            ElementInfo? tree;
            try { tree = LayoutImporter.ImportInfos(dats, layoutId); }
            catch { continue; }
            if (tree is null) continue;

            found += AllDescendants(tree).Count(e => e.Type == SmartBoxWrapperElementType);
        }

        Assert.Equal(0, found);
    }

    private const uint SmartBoxWrapperElementType = 0x10000030u;

    private const uint UiItemTooltipRootElementId = 0x10000395u;

    private static IEnumerable<UiElement> AllWidgets(UiElement root)
    {
        yield return root;
        foreach (UiElement child in root.Children)
            foreach (UiElement descendant in AllWidgets(child))
                yield return descendant;
    }

    private static IEnumerable<ElementInfo> AllDescendants(ElementInfo root)
    {
        yield return root;
        foreach (ElementInfo child in root.Children)
            foreach (ElementInfo descendant in AllDescendants(child))
                yield return descendant;
    }
}
