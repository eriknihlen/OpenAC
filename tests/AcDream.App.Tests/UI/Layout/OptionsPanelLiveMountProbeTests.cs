using System.IO;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using DatReaderWriter;
using DatReaderWriter.Options;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "Manual")]
[Trait("ManualTask", "LiveMountProbe")]
public sealed class OptionsPanelLiveMountProbeTests
{
    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void ProbeLiveMountShapes()
    {
        if (Environment.GetEnvironmentVariable("ACDREAM_PROBE_LIVE_MOUNT") != "1")
            Assert.Fail("Lane=Manual live-mount probe requires ACDREAM_PROBE_LIVE_MOUNT=1.");

        var datDir = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        ElementInfo root = Assert.IsType<ElementInfo>(
            LayoutImporter.ImportInfos(dats, 0x2100006Eu, 0x1000018Du));
        ImportedLayout layout = LayoutImporter.Build(root, _ => (0u, 0, 0), null);

        Console.WriteLine($"[probe] root id=0x{root.Id:X8} type={root.Type} children={root.Children.Count}");
        DumpTree(root, 0, maxDepth: 3);

        // What does the flat index hold for the load-bearing ids?
        foreach (uint id in new[]
        {
            0x10000208u,
            0x10000212u, 0x10000211u, 0x1000050Cu, 0x10000213u, // page slots
            0x100001FAu,
            0x1000050Du,
            0x10000200u,
            0x10000203u, 0x10000617u, 0x100005CCu,
            0x10000206u, 0x10000207u, // UA / RA
        })
        {
            UiElement? el = layout.FindElement(id);
            Console.WriteLine($"[probe] flat 0x{id:X8} -> {(el is null ? "MISSING" : el.GetType().Name)}");
        }

        // The tab panel + its authored table, as production sees it.
        if (layout.FindElement(0x10000208u) is UiTabPanel tabs)
        {
            Console.WriteLine($"[probe] tab table entries={tabs.Tabs.Count}");
            foreach (UiTabTableEntry t in tabs.Tabs)
                Console.WriteLine($"[probe]   button=0x{t.ButtonElementId:X8} page=0x{t.PageElementId:X8} default={t.IsDefault}");
        }

        foreach ((uint slot, uint listBox, string name) in new[]
        {
            (0x10000211u, 0x100001FAu, "Character"),
            (0x1000050Cu, 0x1000050Du, "Chat"),
            (0x10000213u, 0x10000200u, "Config"),
        })
        {
            UiElement? slotEl = layout.FindElement(slot);
            if (slotEl is null)
            {
                Console.WriteLine($"[probe] {name}: SLOT 0x{slot:X8} MISSING from flat index");
                continue;
            }
            UiElement? scoped = UiElement.FindDescendant(slotEl, listBox);
            Console.WriteLine(
                $"[probe] {name}: slot=0x{slot:X8}({slotEl.GetType().Name}, children={slotEl.Children.Count}) "
                + $"scoped-listbox 0x{listBox:X8} -> {(scoped is null ? "MISSING" : scoped.GetType().Name)}");
            if (scoped is UiTemplateListBox tlb)
                Console.WriteLine($"[probe]   templates={tlb.Templates.Count} scrollbarId=0x{tlb.ScrollbarElementId:X8}");
        }
    }

    [Fact]
    public void ProbeConfigMenuChrome()
    {
        if (Environment.GetEnvironmentVariable("ACDREAM_PROBE_LIVE_MOUNT") != "1")
            Assert.Fail("Lane=Manual live-mount probe requires ACDREAM_PROBE_LIVE_MOUNT=1.");

        var datDir = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var strings = new DatStringResolver(dats);

        ElementInfo root = Assert.IsType<ElementInfo>(
            LayoutImporter.ImportInfos(dats, 0x2100006Eu, 0x1000018Du));
        ImportedLayout layout = LayoutImporter.Build(
            root, _ => (1u, 8, 8), null, null, strings.Resolve);

        UiElement? configSlot = layout.FindElement(0x10000213u);
        Assert.NotNull(configSlot);
        UiElement? lbEl = UiElement.FindDescendant(configSlot!, 0x10000200u);
        UiTemplateListBox lb = Assert.IsType<UiTemplateListBox>(lbEl);

        Console.WriteLine($"[menuprobe] Config ListBox templates={lb.Templates.Count}");
        for (int i = 0; i < lb.Templates.Count; i++)
            Console.WriteLine(
                $"[menuprobe]   template[{i}] 0x{lb.Templates[i].TemplateLayoutId:X8}/0x{lb.Templates[i].TemplateElementId:X8}");

        (uint tLayout, uint tElement) =
            (lb.Templates[4].TemplateLayoutId, lb.Templates[4].TemplateElementId);
        ElementInfo? tInfo = LayoutImporter.ImportInfos(dats, tLayout, tElement);
        Assert.NotNull(tInfo);
        DumpInfoTree(tInfo!, 0);
        UiElement rowBuilt = LayoutImporter.Build(
            tInfo!, _ => (1u, 8, 8), null, null, strings.Resolve).Root;

        UiElement? menuEl = UiElement.FindDescendant(rowBuilt, 0x10000224u);
        Console.WriteLine(
            $"[menuprobe] menu leaf 0x10000224 -> {(menuEl is null ? "MISSING" : menuEl.GetType().Name)} "
            + (menuEl is null ? "" : $"({menuEl.Left},{menuEl.Top} {menuEl.Width}x{menuEl.Height})"));
        if (menuEl is UiMenu m)
        {
            Console.WriteLine(
                $"[menuprobe] sprites: normal=0x{m.NormalSprite:X8} pressed=0x{m.PressedSprite:X8} "
                + $"popupBg=0x{m.PopupBgSprite:X8} itemNormal=0x{m.ItemNormalSprite:X8} "
                + $"itemHighlight=0x{m.ItemHighlightSprite:X8} arrowClosed=0x{m.ArrowCapClosedSprite:X8} "
                + $"arrowOpen=0x{m.ArrowCapOpenSprite:X8} spriteResolve={(m.SpriteResolve is null ? "NULL" : "set")} "
                + $"openUpward={m.OpenUpward} rows={m.RowsPerColumn} rowH={m.RowHeight} colW={m.ColumnWidth}");
        }
    }

    private static void DumpInfoTree(ElementInfo info, int depth)
    {
        Console.WriteLine(
            $"[menuprobe] info {new string(' ', depth * 2)}0x{info.Id:X8} type={info.Type} "
            + $"({info.X},{info.Y} {info.Width}x{info.Height}) children={info.Children.Count}");
        if (depth >= 3) return;
        foreach (ElementInfo c in info.Children)
            DumpInfoTree(c, depth + 1);
    }

    [Fact]
    public void ProbeConfigMenuPopupChrome()
    {
        if (Environment.GetEnvironmentVariable("ACDREAM_PROBE_LIVE_MOUNT") != "1")
            Assert.Fail("Lane=Manual live-mount probe requires ACDREAM_PROBE_LIVE_MOUNT=1.");

        var datDir = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        Console.WriteLine("[menuprobe2] === Config tab menu row (0x2100002B template idx4) ===");
        ElementInfo? configRow = LayoutImporter.ImportInfos(dats, 0x2100002Bu, 0x10000222u);
        Assert.NotNull(configRow);
        DumpMenuAttributes(configRow!, 0x10000224u, "Config 0x10000224");
        DumpStateMedia(configRow!, 0x10000355u, "Config face 0x10000355");
        DumpStateMedia(configRow!, 0x10000356u, "Config arrow 0x10000356");

        Console.WriteLine("[menuprobe2] === CONTROL: chat channel menu (0x21000006/0x10000014) ===");
        ElementInfo? chatRoot = LayoutImporter.ImportInfos(dats, 0x21000006u);
        Assert.NotNull(chatRoot);
        DumpMenuAttributes(chatRoot!, 0x10000014u, "Chat 0x10000014");

        Console.WriteLine("[menuprobe2] === CONTROL: vendor category menu (0x21000012/0x100000BF) ===");
        ElementInfo? vendorRoot = LayoutImporter.ImportInfos(dats, 0x21000012u);
        Assert.NotNull(vendorRoot);
        DumpMenuAttributes(vendorRoot!, 0x100000BFu, "Vendor 0x100000BF");

        if (FindInfo(configRow!, 0x10000224u) is { } configMenu
            && configMenu.TryGetEffectiveProperty(7u, out UiPropertyValue popupLayout)
            && popupLayout.Kind == UiPropertyKind.DataId)
        {
            uint popupLayoutId = (uint)popupLayout.UnsignedValue;
            Console.WriteLine($"[menuprobe2] Config popup catalog layout = 0x{popupLayoutId:X8} — dumping tree");
            ElementInfo? popupRoot = LayoutImporter.ImportInfos(dats, popupLayoutId);
            if (popupRoot is not null)
                DumpInfoTree(popupRoot, 0);
            else
                Console.WriteLine($"[menuprobe2] popup layout 0x{popupLayoutId:X8} failed to import");
        }
    }

    private static void DumpMenuAttributes(ElementInfo root, uint menuId, string label)
    {
        ElementInfo? menu = FindInfo(root, menuId);
        if (menu is null)
        {
            Console.WriteLine($"[menuprobe2] {label}: MISSING from imported tree");
            return;
        }

        Console.WriteLine(
            $"[menuprobe2] {label}: type=0x{menu.Type:X8} ({menu.X},{menu.Y} {menu.Width}x{menu.Height}) "
            + $"children={menu.Children.Count} [{string.Join(",", menu.Children.ConvertAll(c => $"0x{c.Id:X8}"))}]");

        foreach (uint attr in new[] { 2u, 5u, 6u, 7u })
        {
            if (menu.TryGetEffectiveProperty(attr, out UiPropertyValue value))
            {
                string rendered = value.Kind switch
                {
                    UiPropertyKind.Bool => value.BoolValue.ToString(),
                    UiPropertyKind.DataId or UiPropertyKind.Enum
                        => $"0x{value.UnsignedValue:X8}",
                    UiPropertyKind.Integer => value.IntegerValue.ToString(),
                    _ => value.Kind.ToString(),
                };
                Console.WriteLine($"[menuprobe2]   attr[{attr}] kind={value.Kind} value={rendered}");
            }
            else
            {
                Console.WriteLine($"[menuprobe2]   attr[{attr}] MISSING");
            }
        }
    }

    private static void DumpStateMedia(ElementInfo root, uint childId, string label)
    {
        ElementInfo? child = FindInfo(root, childId);
        if (child is null)
        {
            Console.WriteLine($"[menuprobe2] {label}: MISSING from imported tree");
            return;
        }

        Console.WriteLine(
            $"[menuprobe2] {label}: type=0x{child.Type:X8} ({child.X},{child.Y} {child.Width}x{child.Height}) "
            + $"defaultState='{child.DefaultStateName}' states={child.States.Count}");
        foreach ((string stateName, var media) in child.StateMedia)
            Console.WriteLine($"[menuprobe2]   state='{stateName}' -> file=0x{media.File:X8} drawMode={media.DrawMode}");
    }

    private static ElementInfo? FindInfo(ElementInfo root, uint id)
    {
        if (root.Id == id) return root;
        foreach (ElementInfo c in root.Children)
        {
            ElementInfo? found = FindInfo(c, id);
            if (found is not null) return found;
        }
        return null;
    }

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void ProbeMenuPopupSizingAndTextStyle()
    {
        if (Environment.GetEnvironmentVariable("ACDREAM_PROBE_LIVE_MOUNT") != "1")
            Assert.Fail("Lane=Manual live-mount probe requires ACDREAM_PROBE_LIVE_MOUNT=1.");

        var datDir = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        Console.WriteLine("[menuprobe3] === Config option-menu base 0x21000043/0x10000353 ===");
        ElementInfo? menuBase = LayoutImporter.ImportInfos(dats, 0x21000043u, 0x10000353u);
        if (menuBase is null)
        {
            Console.WriteLine("[menuprobe3] 0x10000353 FAILED to import");
        }
        else
        {
            DumpMenuAttrs3(menuBase, "base 0x10000353", new[] { 2u, 3u, 5u, 6u, 7u, 8u, 9u });
            foreach (ElementInfo c in menuBase.Children)
                DumpTextStyle(c, $"base child 0x{c.Id:X8}");
        }

        Console.WriteLine("[menuprobe3] === Config leaf 0x2100002B/0x10000224 (attrs 3+8) ===");
        ElementInfo? configRow = LayoutImporter.ImportInfos(dats, 0x2100002Bu, 0x10000222u);
        if (configRow is not null && FindInfo(configRow, 0x10000224u) is { } leaf)
        {
            DumpMenuAttrs3(leaf, "leaf 0x10000224", new[] { 3u, 8u });
            foreach (ElementInfo c in leaf.Children)
                DumpTextStyle(c, $"leaf child 0x{c.Id:X8}");
        }
        else
        {
            Console.WriteLine("[menuprobe3] leaf 0x10000224 MISSING");
        }

        Console.WriteLine("[menuprobe3] === Config popup root/ListBox/row template ===");
        DumpDockAndSize(dats, 0x21000043u, 0x10000357u, "Config popup root");
        DumpDockAndSize(dats, 0x21000043u, 0x10000358u, "Config popup ListBox");
        ElementInfo? rowTemplate = LayoutImporter.ImportInfos(dats, 0x21000043u, 0x1000035Au);
        if (rowTemplate is not null)
        {
            DumpTextStyle(rowTemplate, "Config row template 0x1000035A");
            foreach (ElementInfo c in rowTemplate.Children)
                DumpTextStyle(c, $"row-template child 0x{c.Id:X8}");
        }
        else
        {
            Console.WriteLine("[menuprobe3] row template 0x1000035A FAILED to import");
        }

        Console.WriteLine("[menuprobe3] === CONTROL: vendor chain (content-sized; scrollbar 0x79 hides when disabled) ===");
        DumpDockAndSize(dats, 0x21000043u, 0x1000034Fu, "Vendor popup root");
        DumpDockAndSize(dats, 0x21000043u, 0x10000350u, "Vendor popup ListBox");
        ElementInfo? vendorBase = LayoutImporter.ImportInfos(dats, 0x21000043u, 0x1000034Bu);
        if (vendorBase is not null)
        {
            DumpMenuAttrs3(vendorBase, "vendor base 0x1000034B", new[] { 2u, 3u, 5u, 6u, 7u, 8u, 9u });
            foreach (ElementInfo c in vendorBase.Children)
                DumpTextStyle(c, $"vendor base child 0x{c.Id:X8}");
        }
        ElementInfo? vendorRowTemplate = LayoutImporter.ImportInfos(dats, 0x21000043u, 0x10000352u);
        if (vendorRowTemplate is not null)
        {
            DumpTextStyle(vendorRowTemplate, "Vendor row template 0x10000352");
            foreach (ElementInfo c in vendorRowTemplate.Children)
                DumpTextStyle(c, $"vendor row-template child 0x{c.Id:X8}");
        }
    }

    private static void DumpMenuAttrs3(ElementInfo el, string label, uint[] attrs)
    {
        Console.WriteLine(
            $"[menuprobe3] {label}: type=0x{el.Type:X8} ({el.X},{el.Y} {el.Width}x{el.Height}) "
            + $"edges L={el.Left} T={el.Top} R={el.Right} B={el.Bottom} "
            + $"children=[{string.Join(",", el.Children.ConvertAll(c => $"0x{c.Id:X8}"))}]");
        foreach (uint attr in attrs)
        {
            if (el.TryGetEffectiveProperty(attr, out UiPropertyValue value))
            {
                string rendered = value.Kind switch
                {
                    UiPropertyKind.Bool => value.BoolValue.ToString(),
                    UiPropertyKind.DataId or UiPropertyKind.Enum => $"0x{value.UnsignedValue:X8}",
                    UiPropertyKind.Integer => value.IntegerValue.ToString(),
                    _ => value.Kind.ToString(),
                };
                Console.WriteLine($"[menuprobe3]   attr[{attr}] kind={value.Kind} value={rendered}");
            }
            else
            {
                Console.WriteLine($"[menuprobe3]   attr[{attr}] MISSING");
            }
        }
    }

    private static void DumpDockAndSize(DatCollection dats, uint layoutId, uint elementId, string label)
    {
        ElementInfo? el = LayoutImporter.ImportInfos(dats, layoutId, elementId);
        if (el is null)
        {
            Console.WriteLine($"[menuprobe3] {label} 0x{elementId:X8}: FAILED to import");
            return;
        }
        Console.WriteLine(
            $"[menuprobe3] {label} 0x{elementId:X8}: type=0x{el.Type:X8} "
            + $"({el.X},{el.Y} {el.Width}x{el.Height}) "
            + $"edges L={el.Left} T={el.Top} R={el.Right} B={el.Bottom} "
            + $"children=[{string.Join(",", el.Children.ConvertAll(c => $"0x{c.Id:X8}"))}]");
    }

    private static void DumpTextStyle(ElementInfo el, string label)
    {
        string color = el.FontColor is { } fc
            ? $"({fc.X:F2},{fc.Y:F2},{fc.Z:F2},{fc.W:F2})"
            : "null(default-white)";
        Console.WriteLine(
            $"[menuprobe3] {label}: type=0x{el.Type:X8} ({el.X},{el.Y} {el.Width}x{el.Height}) "
            + $"hJustify={el.HJustify} vJustify={el.VJustify} fontColor={color} fontDid=0x{el.FontDid:X8} "
            + $"edges L={el.Left} T={el.Top} R={el.Right} B={el.Bottom}");
    }

    [Fact]
    public void ProbeChatOpacityCaptions()
    {
        if (Environment.GetEnvironmentVariable("ACDREAM_PROBE_LIVE_MOUNT") != "1")
            Assert.Fail("Lane=Manual live-mount probe requires ACDREAM_PROBE_LIVE_MOUNT=1.");

        var datDir = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var strings = new DatStringResolver(dats);

        bool ok = ChatOptionsDatCaptions.TryRead(
            dats, strings,
            out ChatOptionsDatCaptions.Caption defaultOpacity,
            out ChatOptionsDatCaptions.Caption activeOpacity);

        Console.WriteLine($"[probe380] TryRead ok={ok}");
        Console.WriteLine($"[probe380] Default: Name='{defaultOpacity.Name}' Tooltip='{defaultOpacity.Tooltip}'");
        Console.WriteLine($"[probe380] Active:  Name='{activeOpacity.Name}' Tooltip='{activeOpacity.Tooltip}'");

        Assert.True(ok);
        Assert.Equal("Inactive Opacity", defaultOpacity.Name);
        Assert.Equal("Active Opacity", activeOpacity.Name);
        Assert.False(string.IsNullOrEmpty(defaultOpacity.Tooltip));
        Assert.False(string.IsNullOrEmpty(activeOpacity.Tooltip));
    }

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void ProbeFilterLabelHome()
    {
        if (Environment.GetEnvironmentVariable("ACDREAM_PROBE_LIVE_MOUNT") != "1")
            Assert.Fail("Lane=Manual live-mount probe requires ACDREAM_PROBE_LIVE_MOUNT=1.");

        var datDir = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var strings = new DatStringResolver(dats);

        string[] keys =
        {
            "ID_ChatOption_TextFilter_Combat",
            "ID_ChatOption_TextFilter_Gameplay",
            "ID_ChatOptions_TextFilter_Combat",   // plural-Options variant
            "ID_Chat_TextFilter_Combat",
            "ID_TextFilter_Combat",
            "TextFilter_Combat",
        };
        for (uint table = 0x23000001u; table <= 0x2300000Au; table++)
        {
            foreach (string key in keys)
            {
                string? hit = strings.Resolve(table, DatStringResolver.ComputeHash(key));
                if (hit is not null)
                    Console.WriteLine($"[probe] table 0x{table:X8} key '{key}' -> '{hit}'");
            }
        }
        Console.WriteLine(
            "[probe] control ID_PlayerOption_AutoTarget in 0x23000003 -> "
            + $"'{strings.Resolve(0x23000003u, DatStringResolver.ComputeHash("ID_PlayerOption_AutoTarget"))}'");

        // Exhaustive: sweep EVERY string table in the local dat for the key.
        uint targetHash = DatStringResolver.ComputeHash("ID_ChatOption_TextFilter_Combat");
        foreach (uint tableId in dats.Local.GetAllIdsOfType<DatReaderWriter.DBObjs.StringTable>())
        {
            string? hit = strings.Resolve(tableId, targetHash);
            if (hit is not null)
                Console.WriteLine($"[probe] EXHAUSTIVE hit: table 0x{tableId:X8} -> '{hit}'");
        }
        Console.WriteLine("[probe] exhaustive sweep complete");
    }

    private static void DumpTree(ElementInfo node, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;
        Console.WriteLine(
            $"[probe] {new string(' ', depth * 2)}0x{node.Id:X8} T={node.Type} "
            + $"kids={node.Children.Count} tabTable={node.TabTable.Count} tmpl={node.TemplateList.Count}");
        foreach (ElementInfo child in node.Children)
            DumpTree(child, depth + 1, maxDepth);
    }
}
