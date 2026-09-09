using System;
using System.Collections.Generic;
using System.Linq;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using DatReaderWriter.Types;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.App.Tests.UI;

[Trait("Purpose", "Diagnostic")]
[Trait("Lane", "InstalledDat")]
public sealed class SpewBoxLayoutDumpDiagnostic
{
    private readonly ITestOutputHelper _out;
    public SpewBoxLayoutDumpDiagnostic(ITestOutputHelper output) => _out = output;

    private const uint SpewBoxElementClass = 0x10000016u;
    private const uint ListBoxElementClass = 0x10000049u;
    private const uint LineTemplateLayoutEnum = 0x10000012u;
    private const uint LineTemplateElementId = 0x1000004Au;
    private const uint ListBoxMaxItemsProperty = 0x10000028u;

    private static string? ResolveDatDir()
    {
        var fromEnv = System.Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv) && System.IO.Directory.Exists(fromEnv))
            return fromEnv;
        var def = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "Documents", "Asheron's Call");
        return System.IO.Directory.Exists(def) ? def : null;
    }

    [Fact]
    public void SweepInstalledLayoutDescs_ForSpewBoxElementClass()
    {
        string? datDir = ResolveDatDir();
        if (datDir is null)
        {
            _out.WriteLine("SKIP: installed retail DAT directory is unavailable.");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);

        int scanned = 0;
        var hits = new List<(uint LayoutId, ElementDesc Element)>();
        var allIds = dats.GetAllIdsOfType<LayoutDesc>().ToList();
        _out.WriteLine($"Portal.GetAllIdsOfType<LayoutDesc> count: {dats.Portal.GetAllIdsOfType<LayoutDesc>().Count()}");
        _out.WriteLine($"HighRes.GetAllIdsOfType<LayoutDesc> count: {dats.HighRes.GetAllIdsOfType<LayoutDesc>().Count()}");
        _out.WriteLine($"Contains known char-window 0x2100002E: {allIds.Contains(0x2100002Eu)}");
        _out.WriteLine($"Contains known inventory 0x21000023: {allIds.Contains(0x21000023u)}");
        _out.WriteLine($"Contains known toolbar 0x21000016: {allIds.Contains(0x21000016u)}");
        _out.WriteLine($"Min id: 0x{allIds.Min():X8}  Max id: 0x{allIds.Max():X8}");

        foreach (uint layoutId in allIds)
        {
            scanned++;
            if (!dats.Portal.TryGet<LayoutDesc>(layoutId, out LayoutDesc? ld) || ld is null)
                continue;

            foreach (var kv in ld.Elements)
            {
                var found = FindByType(kv.Value, SpewBoxElementClass);
                if (found is not null)
                    hits.Add((layoutId, found));
            }
        }

        _out.WriteLine($"Scanned {scanned} installed LayoutDescs.");
        _out.WriteLine($"Elements of class 0x{SpewBoxElementClass:X8} (gmSpewBoxUI): {hits.Count}");

        foreach (var (layoutId, element) in hits)
        {
            _out.WriteLine(
                $"  LayoutDesc 0x{layoutId:X8} -> element 0x{element.ElementId:X8} "
                + $"pos=({element.X},{element.Y}) size=({element.Width}x{element.Height}) "
                + $"zLevel={element.ZLevel} readOrder={element.ReadOrder} "
                + $"children={element.Children.Count}");

            var listBox = FindByType(element, ListBoxElementClass);
            if (listBox is not null)
            {
                _out.WriteLine(
                    $"    ListBox child 0x{listBox.ElementId:X8} "
                    + $"pos=({listBox.X},{listBox.Y}) size=({listBox.Width}x{listBox.Height})");
                DumpProperties(listBox, "      ", _out.WriteLine);
            }
            else
            {
                _out.WriteLine("    (no ListBox child of class 0x10000049 found)");
            }
        }

        if (dats.Portal.TryGet<LayoutDesc>(LineTemplateLayoutEnum, out LayoutDesc? templateLd)
            && templateLd is not null)
        {
            _out.WriteLine($"LayoutDesc 0x{LineTemplateLayoutEnum:X8} exists ({templateLd.Elements.Count} top-level elements).");
            if (templateLd.Elements.TryGetValue(LineTemplateElementId, out ElementDesc? lineTemplate))
            {
                _out.WriteLine(
                    $"  Line template 0x{LineTemplateElementId:X8}: type=0x{lineTemplate.Type:X8} "
                    + $"pos=({lineTemplate.X},{lineTemplate.Y}) size=({lineTemplate.Width}x{lineTemplate.Height})");
                DumpProperties(lineTemplate, "    ", _out.WriteLine);
            }
            else
            {
                _out.WriteLine($"  No top-level element 0x{LineTemplateElementId:X8} in that LayoutDesc.");
            }
        }
        else
        {
            _out.WriteLine($"LayoutDesc 0x{LineTemplateLayoutEnum:X8} does not exist in the installed DAT.");
        }

        var localHits = new List<(uint LayoutId, ElementDesc Element)>();
        int localScanned = 0;
        List<uint> localIds = dats.Local.GetAllIdsOfType<LayoutDesc>().ToList();
        _out.WriteLine($"Local.GetAllIdsOfType<LayoutDesc> count: {localIds.Count}");
        if (localIds.Count > 0)
            _out.WriteLine($"Local min id: 0x{localIds.Min():X8}  Local max id: 0x{localIds.Max():X8}");

        foreach (uint layoutId in localIds)
        {
            localScanned++;
            if (!dats.Local.TryGet<LayoutDesc>(layoutId, out LayoutDesc? ld) || ld is null)
                continue;

            foreach (var kv in ld.Elements)
            {
                var found = FindByType(kv.Value, SpewBoxElementClass);
                if (found is not null)
                    localHits.Add((layoutId, found));
            }
        }

        _out.WriteLine($"Scanned {localScanned} dats.Local LayoutDescs.");
        _out.WriteLine(
            $"dats.Local elements of class 0x{SpewBoxElementClass:X8} (gmSpewBoxUI): {localHits.Count}");

        foreach (var (layoutId, element) in localHits)
        {
            _out.WriteLine(
                $"  [Local] LayoutDesc 0x{layoutId:X8} -> element 0x{element.ElementId:X8} "
                + $"pos=({element.X},{element.Y}) size=({element.Width}x{element.Height}) "
                + $"zLevel={element.ZLevel} readOrder={element.ReadOrder} "
                + $"leftEdge={element.LeftEdge} topEdge={element.TopEdge} "
                + $"rightEdge={element.RightEdge} bottomEdge={element.BottomEdge} "
                + $"baseElement=0x{element.BaseElement:X8} baseLayoutId=0x{element.BaseLayoutId:X8} "
                + $"children={element.Children.Count}");
            DumpProperties(element, "    ", _out.WriteLine);
            foreach (var (childId, child) in element.Children)
            {
                _out.WriteLine(
                    $"    child 0x{childId:X8}: type=0x{child.Type:X8} "
                    + $"pos=({child.X},{child.Y}) size=({child.Width}x{child.Height})");
                DumpProperties(child, "      ", _out.WriteLine);
            }

            if (element.Children.TryGetValue(ListBoxElementClass, out ElementDesc? listBoxById))
            {
                _out.WriteLine(
                    $"    [Local] ListBox-by-ElementId 0x{listBoxById.ElementId:X8} "
                    + $"(Type=0x{listBoxById.Type:X8}) "
                    + $"pos=({listBoxById.X},{listBoxById.Y}) size=({listBoxById.Width}x{listBoxById.Height})");
            }

            var listBox = FindByType(element, ListBoxElementClass);
            if (listBox is not null)
            {
                _out.WriteLine(
                    $"    [Local] ListBox-by-Type child 0x{listBox.ElementId:X8} "
                    + $"pos=({listBox.X},{listBox.Y}) size=({listBox.Width}x{listBox.Height})");
                DumpProperties(listBox, "      ", _out.WriteLine);
            }
        }

        if (dats.Local.TryGet<LayoutDesc>(LineTemplateLayoutEnum, out LayoutDesc? localTemplateLd)
            && localTemplateLd is not null)
        {
            _out.WriteLine(
                $"[Local] LayoutDesc 0x{LineTemplateLayoutEnum:X8} exists "
                + $"({localTemplateLd.Elements.Count} top-level elements).");
        }
        else
        {
            _out.WriteLine($"[Local] LayoutDesc 0x{LineTemplateLayoutEnum:X8} does not exist.");
        }

    }

    private static ElementDesc? FindByType(ElementDesc d, uint type)
    {
        if (d.Type == type)
            return d;
        foreach (var kv in d.Children)
        {
            var found = FindByType(kv.Value, type);
            if (found is not null)
                return found;
        }
        return null;
    }

    private static void DumpProperties(ElementDesc d, string indent, Action<string> write)
    {
        if (d.StateDesc?.Properties is null)
        {
            write($"{indent}(no direct-state properties)");
            return;
        }

        foreach (var (propertyId, property) in d.StateDesc.Properties)
        {
            write($"{indent}property 0x{propertyId:X8} = {Describe(property)}"
                + (propertyId == ListBoxMaxItemsProperty ? "  <-- MaxConcurrentItems" : ""));
        }
    }

    private static string Describe(object property) => property switch
    {
        DatReaderWriter.Types.EnumBaseProperty e => $"Enum({e.Value})",
        DatReaderWriter.Types.DataIdBaseProperty did => $"DataId(0x{did.Value:X8})",
        DatReaderWriter.Types.ArrayBaseProperty arr => $"Array[{arr.Value.Count}]({string.Join(", ", arr.Value.Select(Describe))})",
        DatReaderWriter.Types.IntegerBaseProperty i => $"Integer({i.Value})",
        DatReaderWriter.Types.BoolBaseProperty b => $"Bool({b.Value})",
        _ => property.ToString() ?? "?",
    };
}
