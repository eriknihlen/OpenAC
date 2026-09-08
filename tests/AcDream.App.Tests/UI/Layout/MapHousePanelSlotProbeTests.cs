using System.IO;
using AcDream.App.UI.Layout;
using DatReaderWriter;
using DatReaderWriter.Options;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Purpose", "Diagnostic")]
[Trait("Lane", "Manual")]
[Trait("ManualTask", "LiveMountProbe")]
public sealed class MapHousePanelSlotProbeTests
{
    private const uint HostLayoutId = 0x2100006Eu;
    private const uint SlotElementId = 0x1000018Cu;
    private const uint ToolbarLayoutId = 0x21000016u;
    private const uint MapHouseToolbarButtonId = 0x1000019Au;

    private const uint MapDateTimeTextId = 0x100001EBu;
    private const uint MapWidgetId = 0x100001ECu;
    private const uint MapPlayerIconId = 0x100001EDu;
    private const uint MapHouseIconId = 0x100001EEu;
    private const uint MapCoordinateTextId = 0x100001EFu;

    private const uint HouseTextBoxId = 0x100001E6u;

    [Fact]
    public void ProbeMapHousePanelSlot()
    {
        if (Environment.GetEnvironmentVariable("ACDREAM_PROBE_LIVE_MOUNT") != "1")
            Assert.Fail("Lane=Manual live-mount probe requires ACDREAM_PROBE_LIVE_MOUNT=1.");

        var datDir = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        // 1) The slot itself — panel id, type, tab table.
        ElementInfo? slot = LayoutImporter.ImportInfos(dats, HostLayoutId, SlotElementId);
        if (slot is null)
        {
            Console.WriteLine($"[maphouse] slot 0x{SlotElementId:X8} -> IMPORT NULL");
            return;
        }

        string panelId = slot.TryGetEffectiveProperty(0x10000029u, out var p)
            ? $"{p.UnsignedValue} (kind={p.Kind})"
            : "ABSENT";
        Console.WriteLine(
            $"[maphouse] slot 0x{SlotElementId:X8} panelId={panelId} type={slot.Type} "
            + $"({slot.X},{slot.Y} {slot.Width}x{slot.Height}) children={slot.Children.Count} "
            + $"tabTableCount={slot.TabTable.Count}");
        foreach (var tab in slot.TabTable)
            Console.WriteLine(
                $"[maphouse]     tab button=0x{tab.ButtonElementId:X8} page=0x{tab.PageElementId:X8} "
                + $"default={tab.IsDefault}");

        bool hasMapSignature = FindInfo(slot, MapDateTimeTextId) || FindInfo(slot, MapWidgetId);
        bool hasHouseSignature = FindInfo(slot, HouseTextBoxId);
        Console.WriteLine(
            $"[maphouse] hasMapSignature={hasMapSignature} hasHouseSignature={hasHouseSignature}");

        foreach (ElementInfo c in slot.Children)
        {
            Console.WriteLine(
                $"[maphouse]     child 0x{c.Id:X8} type={c.Type} ({c.X},{c.Y} {c.Width}x{c.Height}) "
                + $"kids={c.Children.Count}");
            foreach (ElementInfo g in c.Children)
                Console.WriteLine(
                    $"[maphouse]         g 0x{g.Id:X8} type={g.Type} ({g.X},{g.Y} {g.Width}x{g.Height}) "
                    + $"kids={g.Children.Count}");
        }

        // 2) The toolbar button that should open panel 16.
        ElementInfo? button = LayoutImporter.ImportInfos(dats, ToolbarLayoutId, MapHouseToolbarButtonId);
        string buttonPanelId = button is not null && button.TryGetEffectiveProperty(0x10000029u, out var bp)
            ? $"{bp.UnsignedValue} (kind={bp.Kind})"
            : "ABSENT/NULL";
        Console.WriteLine($"[maphouse] toolbar button 0x{MapHouseToolbarButtonId:X8} panelId={buttonPanelId}");

        // 3) m_pMap's own marker-area rect + hotspot template attrs.
        ElementInfo? map = FindDescendant(slot, MapWidgetId);
        if (map is null)
        {
            Console.WriteLine($"[maphouse] m_pMap 0x{MapWidgetId:X8} NOT FOUND under slot");
        }
        else
        {
            string x0 = map.TryGetEffectiveProperty(0x1000004Eu, out var vx0) ? vx0.IntegerValue.ToString() : "ABSENT";
            string x1 = map.TryGetEffectiveProperty(0x1000004Fu, out var vx1) ? vx1.IntegerValue.ToString() : "ABSENT";
            string y0 = map.TryGetEffectiveProperty(0x10000050u, out var vy0) ? vy0.IntegerValue.ToString() : "ABSENT";
            string y1 = map.TryGetEffectiveProperty(0x10000051u, out var vy1) ? vy1.IntegerValue.ToString() : "ABSENT";
            bool hasTemplateElement = map.TryGetEffectiveProperty(0x47u, out var te);
            bool hasTemplateLayoutDid = map.TryGetEffectiveProperty(0x48u, out var tl);
            string templateElement = hasTemplateElement
                ? $"0x{te.UnsignedValue:X8} (kind={te.Kind})" : "ABSENT";
            string templateLayoutDid = hasTemplateLayoutDid
                ? $"0x{tl.UnsignedValue:X8} (kind={tl.Kind})" : "ABSENT";
            Console.WriteLine(
                $"[maphouse] m_pMap 0x{MapWidgetId:X8} markerArea=({x0},{y0})-({x1},{y1}) "
                + $"templateElement(attr 0x47)={templateElement} templateLayoutDid(attr 0x48)={templateLayoutDid} "
                + $"type={map.Type} children={map.Children.Count}");

            if (hasTemplateElement && hasTemplateLayoutDid && tl.UnsignedValue != 0)
            {
                ElementInfo? template = LayoutImporter.ImportInfos(dats, (uint)tl.UnsignedValue, (uint)te.UnsignedValue);
                if (template is null)
                {
                    Console.WriteLine("[maphouse] hotspot template IMPORT NULL");
                }
                else
                {
                    string tp47 = template.TryGetEffectiveProperty(0x47u, out var tpe)
                        ? $"0x{tpe.UnsignedValue:X8} (kind={tpe.Kind})" : "ABSENT";
                    string tp48 = template.TryGetEffectiveProperty(0x48u, out var tpl)
                        ? $"0x{tpl.UnsignedValue:X8} (kind={tpl.Kind})" : "ABSENT";
                    string tp4b = template.TryGetEffectiveProperty(0x4Bu, out var tpb)
                        ? tpb.UnsignedValue.ToString() : "ABSENT";
                    Console.WriteLine(
                        $"[maphouse] hotspot template 0x{template.Id:X8} type={template.Type} "
                        + $"({template.Width}x{template.Height}) states={template.States.Count} "
                        + $"stateMedia={template.StateMedia.Count} defaultState='{template.DefaultStateName}' "
                        + $"popupRoot(P0x47)={tp47} popupLayout(P0x48)={tp48} tooltipOn(P0x4B)={tp4b} "
                        + $"fontDid=0x{template.FontDid:X8} kids={template.Children.Count}");
                    foreach (var s in template.States)
                    {
                        Console.WriteLine(
                            $"[maphouse]     template state id=0x{s.Key:X} name='{s.Value.Name}' "
                            + $"props={s.Value.Properties.Values.Count} "
                            + $"image={(s.Value.Image is { } img ? $"0x{img.File:X8}/{img.DrawMode}" : "none")}");
                        foreach (var pv in s.Value.Properties.Values)
                            Console.WriteLine(
                                $"[maphouse]         t-prop 0x{pv.Key:X} kind={pv.Value.Kind} "
                                + $"u=0x{pv.Value.UnsignedValue:X} i={pv.Value.IntegerValue} b={pv.Value.BoolValue} "
                                + $"color={pv.Value.ColorValue}");
                    }
                    foreach (var m in template.StateMedia)
                        Console.WriteLine(
                            $"[maphouse]     template stateMedia '{m.Key}' file=0x{m.Value.File:X8} drawMode={m.Value.DrawMode}");
                    foreach (ElementInfo tc in template.Children)
                    {
                        Console.WriteLine(
                            $"[maphouse]     template kid 0x{tc.Id:X8} type={tc.Type} "
                            + $"({tc.X},{tc.Y} {tc.Width}x{tc.Height}) stateMedia={tc.StateMedia.Count} "
                            + $"states={tc.States.Count} defaultState='{tc.DefaultStateName}' kids={tc.Children.Count} "
                            + $"origParent={(tc.HasOriginalParentSize ? $"{tc.OriginalParentWidth}x{tc.OriginalParentHeight}" : "none")} "
                            + $"edges=({tc.Left},{tc.Top},{tc.Right},{tc.Bottom})");
                        foreach (ElementInfo gk in tc.Children)
                            Console.WriteLine(
                                $"[maphouse]         kid-kid 0x{gk.Id:X8} type={gk.Type} "
                                + $"({gk.X},{gk.Y} {gk.Width}x{gk.Height}) stateMedia={gk.StateMedia.Count} "
                                + $"origParent={(gk.HasOriginalParentSize ? $"{gk.OriginalParentWidth}x{gk.OriginalParentHeight}" : "none")} "
                                + $"edges=({gk.Left},{gk.Top},{gk.Right},{gk.Bottom})");
                        foreach (var m in tc.StateMedia)
                            Console.WriteLine(
                                $"[maphouse]         kid stateMedia '{m.Key}' file=0x{m.Value.File:X8} drawMode={m.Value.DrawMode}");
                        foreach (var s in tc.States)
                        {
                            Console.WriteLine(
                                $"[maphouse]         kid state id=0x{s.Key:X} name='{s.Value.Name}' "
                                + $"props={s.Value.Properties.Values.Count} "
                                + $"image={(s.Value.Image is { } kimg ? $"0x{kimg.File:X8}/{kimg.DrawMode}" : "none")}");
                            foreach (var pv in s.Value.Properties.Values)
                                Console.WriteLine(
                                    $"[maphouse]             prop 0x{pv.Key:X} kind={pv.Value.Kind} "
                                    + $"u=0x{pv.Value.UnsignedValue:X} i={pv.Value.IntegerValue} f={pv.Value.FloatValue} "
                                    + $"b={pv.Value.BoolValue} color={pv.Value.ColorValue}");
                        }
                    }

                    foreach (uint skinId in new[] { 0x10000487u, 0x10000395u, 0x10000397u, 0x10000398u })
                    {
                        ElementInfo? s41 = LayoutImporter.ImportInfos(dats, 0x21000041u, skinId);
                        if (s41 is null)
                        {
                            Console.WriteLine($"[maphouse] skin-sweep 0x{skinId:X8} IMPORT NULL");
                            continue;
                        }
                        string rootMedia = s41.StateMedia.TryGetValue("", out var rm)
                            ? $"0x{rm.File:X8}/{rm.DrawMode}" : "none";
                        ElementInfo? text = null;
                        foreach (ElementInfo k in s41.Children)
                            if (k.Id == s41.TooltipTextChildElementId) { text = k; break; }
                        Console.WriteLine(
                            $"[maphouse] skin-sweep 0x{skinId:X8} ({s41.Width}x{s41.Height}) rootMedia={rootMedia} "
                            + $"textChild=0x{s41.TooltipTextChildElementId:X8} "
                            + $"textFontDid=0x{text?.FontDid ?? 0u:X8} "
                            + $"textColor={(text?.FontColor is { } fc ? fc.ToString() : "none")} "
                            + $"textSize={text?.Width}x{text?.Height}");
                    }

                    uint popupLayout = template.TryGetEffectiveProperty(0x48u, out var pl) && pl.UnsignedValue != 0
                        ? (uint)pl.UnsignedValue
                        : (uint)tl.UnsignedValue;
                    uint popupRoot = template.TryGetEffectiveProperty(0x47u, out var pr)
                        ? (uint)pr.UnsignedValue : 0u;
                    if (popupRoot != 0u)
                    {
                        ElementInfo? skin = LayoutImporter.ImportInfos(dats, popupLayout, popupRoot);
                        if (skin is null)
                        {
                            Console.WriteLine(
                                $"[maphouse] popup skin 0x{popupRoot:X8} in 0x{popupLayout:X8} IMPORT NULL");
                        }
                        else
                        {
                            Console.WriteLine(
                                $"[maphouse] popup skin 0x{skin.Id:X8} in 0x{popupLayout:X8} type={skin.Type} "
                                + $"({skin.Width}x{skin.Height}) textChild(P0x4A)=0x{skin.TooltipTextChildElementId:X8} "
                                + $"stateMedia={skin.StateMedia.Count} kids={skin.Children.Count}");
                            foreach (var m in skin.StateMedia)
                                Console.WriteLine(
                                    $"[maphouse]     skin stateMedia '{m.Key}' file=0x{m.Value.File:X8} drawMode={m.Value.DrawMode}");
                            DumpSkinTree(skin, "    ");
                        }
                    }
                }
            }
        }

        ElementInfo? playerIcon = FindDescendant(slot, MapPlayerIconId);
        ElementInfo? houseIcon = FindDescendant(slot, MapHouseIconId);
        Console.WriteLine(
            $"[maphouse] playerIcon found={playerIcon is not null} type={playerIcon?.Type} "
            + $"stateMedia={playerIcon?.StateMedia.Count}");
        Console.WriteLine(
            $"[maphouse] houseIcon found={houseIcon is not null} type={houseIcon?.Type} "
            + $"stateMedia={houseIcon?.StateMedia.Count}");

        ElementInfo? houseBox = FindDescendant(slot, HouseTextBoxId);
        if (houseBox is null)
        {
            Console.WriteLine($"[maphouse] house listbox 0x{HouseTextBoxId:X8} NOT FOUND under slot");
        }
        else
        {
            Console.WriteLine(
                $"[maphouse] house listbox 0x{HouseTextBoxId:X8} type={houseBox.Type} "
                + $"children={houseBox.Children.Count} templateListCount={houseBox.TemplateList.Count} "
                + $"scrollbar=0x{houseBox.ScrollbarElementId:X8}");
            foreach (var t in houseBox.TemplateList)
            {
                Console.WriteLine(
                    $"[maphouse]     template layoutId=0x{t.TemplateLayoutId:X8} elementId=0x{t.TemplateElementId:X8}");
                ElementInfo? rowTemplate = LayoutImporter.ImportInfos(dats, t.TemplateLayoutId, t.TemplateElementId);
                if (rowTemplate is null)
                {
                    Console.WriteLine("[maphouse]         row template IMPORT NULL");
                    continue;
                }
                Console.WriteLine(
                    $"[maphouse]         row template type={rowTemplate.Type} "
                    + $"({rowTemplate.Width}x{rowTemplate.Height}) kids={rowTemplate.Children.Count}");
                foreach (ElementInfo rc in rowTemplate.Children)
                    Console.WriteLine(
                        $"[maphouse]             rc 0x{rc.Id:X8} type={rc.Type} ({rc.X},{rc.Y} {rc.Width}x{rc.Height})");
            }
            foreach (ElementInfo row in houseBox.Children)
            {
                string caption = row.TryGetEffectiveProperty(0x17u, out var cap)
                    ? $"StringInfo(table={cap.StringInfoValue.TableId:X},id={cap.StringInfoValue.StringId:X},lit={cap.StringInfoValue.English != 0})"
                    : "NO 0x17";
                Console.WriteLine(
                    $"[maphouse]     row 0x{row.Id:X8} type={row.Type} caption={caption} kids={row.Children.Count}");
                foreach (ElementInfo g in row.Children)
                {
                    string gcaption = g.TryGetEffectiveProperty(0x17u, out var gcap)
                        ? $"StringInfo(table={gcap.StringInfoValue.TableId:X},id={gcap.StringInfoValue.StringId:X})"
                        : "NO 0x17";
                    Console.WriteLine($"[maphouse]         g 0x{g.Id:X8} type={g.Type} caption={gcaption}");
                }
            }
        }
    }

    private static void DumpSkinTree(ElementInfo info, string indent)
    {
        foreach (ElementInfo c in info.Children)
        {
            Console.WriteLine(
                $"[maphouse] {indent}skin kid 0x{c.Id:X8} type={c.Type} ({c.X},{c.Y} {c.Width}x{c.Height}) "
                + $"fontDid=0x{c.FontDid:X8} stateMedia={c.StateMedia.Count} kids={c.Children.Count}");
            foreach (var m in c.StateMedia)
                Console.WriteLine(
                    $"[maphouse] {indent}    media '{m.Key}' file=0x{m.Value.File:X8} drawMode={m.Value.DrawMode}");
            DumpSkinTree(c, indent + "    ");
        }
    }

    private static bool FindInfo(ElementInfo info, uint id)
    {
        if (info.Id == id) return true;
        foreach (ElementInfo c in info.Children)
            if (FindInfo(c, id)) return true;
        return false;
    }

    private static ElementInfo? FindDescendant(ElementInfo info, uint id)
    {
        if (info.Id == id) return info;
        foreach (ElementInfo c in info.Children)
        {
            ElementInfo? found = FindDescendant(c, id);
            if (found is not null) return found;
        }
        return null;
    }
}
