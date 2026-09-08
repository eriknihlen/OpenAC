using System.Collections;
using System.Reflection;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using DatReaderWriter.Types;

namespace AcDream.Cli;

public static class VitalsLayoutDump
{
    public static int Run(string datDir, string? layoutIdText)
    {
        if (!Directory.Exists(datDir)) { Console.Error.WriteLine($"error: dir not found: {datDir}"); return 2; }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        // Default to the vitals layout dump-vitals-bars found; allow override.
        uint layoutId = 0x21000014u;
        if (!string.IsNullOrWhiteSpace(layoutIdText))
        {
            var t = layoutIdText.Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
            uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out layoutId);
        }

        Console.WriteLine("=== LayoutDescs containing a vitals meter element (0x100000E6/EC/EE) ===");
        foreach (var id in dats.GetAllIdsOfType<LayoutDesc>())
        {
            var l = dats.Get<LayoutDesc>(id);
            if (l is null) continue;
            if (!ContainsAny(l, 0x100000E6u, 0x100000ECu, 0x100000EEu)) continue;
            Console.WriteLine($"  0x{id:X8}  {RootSizeSummary(l)}");
        }
        Console.WriteLine();

        var ld = dats.Get<LayoutDesc>(layoutId);
        if (ld is null) { Console.Error.WriteLine($"layout 0x{layoutId:X8} not found"); return 1; }

        Console.WriteLine($"=== FULL DUMP layout 0x{layoutId:X8} ===");
        DumpScalars("LayoutDesc", ld, 0);
        foreach (var kv in ld.Elements)
            DumpElement(kv.Value, 1);
        return 0;
    }

    private static bool ContainsAny(LayoutDesc l, params uint[] ids)
    {
        foreach (var kv in l.Elements)
            if (ElemContains(kv.Value, ids)) return true;
        return false;
    }

    private static bool ElemContains(ElementDesc e, uint[] ids)
    {
        if (Array.IndexOf(ids, e.ElementId) >= 0) return true;
        foreach (var kv in e.Children)
            if (ElemContains(kv.Value, ids)) return true;
        return false;
    }

    private static string RootSizeSummary(LayoutDesc l)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var p in l.GetType().GetProperties())
        {
            if (p.GetIndexParameters().Length > 0) continue;
            if (p.Name is "Elements") continue;
            object? v; try { v = p.GetValue(l); } catch { continue; }
            if (v is null) continue;
            if (IsScalar(v)) sb.Append($"{p.Name}={v} ");
        }
        return sb.ToString().Trim();
    }

    private static void DumpElement(ElementDesc e, int depth)
    {
        string ind = new string(' ', depth * 2);
        Console.WriteLine($"{ind}element 0x{e.ElementId:X8}");
        DumpScalars(ind + "  ", e, depth);

        if (e.StateDesc is not null) DumpMedia(ind + "  [DirectState]", e.StateDesc);
        foreach (var s in e.States)
            DumpMedia($"{ind}  [state {s.Key}]", s.Value);

        foreach (var c in e.Children)
            DumpElement(c.Value, depth + 1);
    }

    private static readonly HashSet<string> Skip = new() { "Children", "States", "StateDesc", "Elements", "Media" };

    private static void DumpScalars(string label, object o, int depth)
    {
        foreach (var (name, val) in Members(o))
        {
            if (Skip.Contains(name)) continue;
            if (IsScalar(val))
                Console.WriteLine($"{label}  {name} = {Fmt(name, val)}");
        }
    }

    private static void DumpMedia(string label, StateDesc sd)
    {
        foreach (var m in sd.Media)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var (name, val) in Members(m))
                if (IsScalar(val)) sb.Append($"{name}={Fmt(name, val)} ");
            Console.WriteLine($"{label}  {m.GetType().Name}: {sb.ToString().Trim()}");
        }
    }

    /// <summary>Enumerate public properties AND public fields (the DatReaderWriter
    /// generated types expose geometry/file ids as fields, not properties).</summary>
    private static IEnumerable<(string name, object val)> Members(object o)
    {
        var t = o.GetType();
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            object? v; try { v = p.GetValue(o); } catch { continue; }
            if (v is not null) yield return (p.Name, v);
        }
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            object? v; try { v = f.GetValue(o); } catch { continue; }
            if (v is not null) yield return (f.Name, v);
        }
    }

    private static string Fmt(string name, object v) =>
        name.Contains("File", StringComparison.OrdinalIgnoreCase) && v is uint u ? $"0x{u:X8}" : v.ToString() ?? "";

    private static bool IsScalar(object v)
    {
        var t = v.GetType();
        if (v is string) return true;
        if (t.IsPrimitive || t.IsEnum) return true;
        if (v is IEnumerable) return false;
        // value-type structs (Rectangle/Point/etc.) — print via ToString
        return t.IsValueType;
    }
}
