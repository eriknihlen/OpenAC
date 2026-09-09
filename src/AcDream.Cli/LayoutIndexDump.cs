using System.Reflection;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using DatReaderWriter.Types;

namespace AcDream.Cli;

public static class LayoutIndexDump
{
    public static int Run(string datDir, string? rootTypeText)
    {
        if (!Directory.Exists(datDir)) { Console.Error.WriteLine($"error: dir not found: {datDir}"); return 2; }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        uint? filter = null;
        if (!string.IsNullOrWhiteSpace(rootTypeText))
        {
            var t = rootTypeText.Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
            if (uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var f)) filter = f;
        }

        Console.WriteLine(filter is { } ff
            ? $"=== LayoutDescs with a root element of Type 0x{ff:X8} ==="
            : "=== All LayoutDescs (id : root element Type : size : #elements : type histogram) ===");

        int total = 0, shown = 0;
        foreach (var id in dats.GetAllIdsOfType<LayoutDesc>().OrderBy(x => x))
        {
            var l = dats.Get<LayoutDesc>(id);
            if (l is null) continue;
            total++;

            // The root is the single top-level element (or, if several, the largest).
            ElementDesc? root = null;
            foreach (var kv in l.Elements)
                if (root is null || Area(kv.Value) > Area(root)) root = kv.Value;
            if (root is null) continue;

            if (filter is { } want && root.Type != want) continue;
            shown++;

            var hist = new SortedDictionary<uint, int>();
            int count = 0;
            CountTypes(root, hist, ref count);
            string h = string.Join(" ", hist.Select(kv => $"{TypeName(kv.Key)}×{kv.Value}"));
            Console.WriteLine(
                $"  0x{id:X8}  root=0x{root.ElementId:X8} type=0x{root.Type:X8}({TypeName(root.Type)})  " +
                $"{root.Width}x{root.Height}  n={count}  [{h}]");
        }

        Console.WriteLine();
        Console.WriteLine($"shown {shown} / {total} LayoutDescs.");
        return 0;
    }

    private static long Area(ElementDesc e) => (long)e.Width * e.Height;

    private static void CountTypes(ElementDesc e, SortedDictionary<uint, int> hist, ref int count)
    {
        count++;
        hist[e.Type] = hist.TryGetValue(e.Type, out var c) ? c + 1 : 1;
        foreach (var kv in e.Children)
            CountTypes(kv.Value, hist, ref count);
    }

    private static string TypeName(uint t) => t switch
    {
        0 => "Text0",
        1 => "Button",
        2 => "Dragbar",
        3 => "Field",
        5 => "ListBox",
        6 => "Menu",
        7 => "Meter",
        8 => "Panel",
        9 => "Resizebar",
        0xB => "Scrollbar",
        0xC => "Text",
        0xD => "Viewport",
        0xE => "Browser",
        0x10 => "ColorPicker",
        0x11 => "GroupBox",
        0x12 => "Proto",
        0x10000041 => "gmMainChatUI",
        0x10000040 => "gmFloatyChatUI",
        0x10000050 => "gmFloatyMainChatUI",
        0x10000042 => "gmChatOptionsUI",
        0x10000009 => "gmVitalsUI",
        _ => $"0x{t:X}",
    };
}
