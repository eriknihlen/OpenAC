using System.IO;
using AcDream.App.UI.Layout;
using DatReaderWriter;
using DatReaderWriter.Options;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "Manual")]
[Trait("ManualTask", "PowerbarProbe")]
public sealed class PowerbarLayoutProbeTests
{
    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void ProbePowerbarAuthoredStrings()
    {
        if (Environment.GetEnvironmentVariable("ACDREAM_PROBE_POWERBAR") != "1")
            Assert.Fail("Lane=Manual powerbar probe requires ACDREAM_PROBE_POWERBAR=1.");

        var datDir = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var strings = new DatStringResolver(dats);

        Dump(dats, strings, JumpPowerbarController.LayoutId, "floaty-powerbar");
        Dump(dats, strings, CombatUiController.LayoutId, "combat-panel");
    }

    [Fact]
    public void ProbeFriendsRowTemplate()
    {
        if (Environment.GetEnvironmentVariable("ACDREAM_PROBE_POWERBAR") != "1")
            Assert.Fail("Lane=Manual powerbar probe requires ACDREAM_PROBE_POWERBAR=1.");

        var datDir = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var strings = new DatStringResolver(dats);

        ElementInfo? root = LayoutImporter.ImportInfos(
            dats, SocialPanelController.HostLayoutId, SocialPanelController.SlotElementId);
        Assert.NotNull(root);
        ElementInfo? listBox = FindById(root!, 0x10000517u);
        Assert.NotNull(listBox);
        Console.WriteLine(
            $"[pbprobe] friends listbox 0x10000517 templates={listBox!.TemplateList.Count}");
        foreach (UiTemplateListEntry template in listBox.TemplateList)
        {
            Console.WriteLine(
                $"[pbprobe] template layout=0x{template.TemplateLayoutId:X8} "
                + $"element=0x{template.TemplateElementId:X8}");
            ElementInfo? row = LayoutImporter.ImportInfos(
                dats, template.TemplateLayoutId, template.TemplateElementId);
            if (row is not null)
                DumpElement(strings, row, 1);
        }
    }

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void ProbeSecureTradeLayout()
    {
        if (Environment.GetEnvironmentVariable("ACDREAM_PROBE_POWERBAR") != "1")
            Assert.Fail("Lane=Manual powerbar probe requires ACDREAM_PROBE_POWERBAR=1.");

        var datDir = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var strings = new DatStringResolver(dats);

        int scanned = 0;
        for (uint layoutId = 0x21000000u; layoutId <= 0x210001FFu; layoutId++)
        {
            ElementInfo? root = LayoutImporter.ImportInfos(dats, layoutId);
            if (root is null)
                continue;
            scanned++;
            if (FindById(root, 0x10000088u) is null)
                continue;
            Console.WriteLine($"[pbprobe] SECURE TRADE LAYOUT = 0x{layoutId:X8}");
            DumpElement(strings, root, 0);
        }
        Console.WriteLine($"[pbprobe] scanned {scanned} layouts");
    }

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void ProbeTotalItemsTemplate()
    {
        if (Environment.GetEnvironmentVariable("ACDREAM_PROBE_POWERBAR") != "1")
            Assert.Fail("Lane=Manual powerbar probe requires ACDREAM_PROBE_POWERBAR=1.");

        var datDir = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        uint hash = DatStringResolver.ComputeHash("ID_SecureTrade_TotalItemsLabel");
        var table = dats.Get<DatReaderWriter.DBObjs.StringTable>(0x23000001u);
        Console.WriteLine($"[pbprobe] key hash=0x{hash:X8} tableFound={table is not null}");
        if (table is not null && table.Strings.TryGetValue(hash, out var entry))
        {
            for (int i = 0; i < entry.Strings.Count; i++)
                Console.WriteLine($"[pbprobe] fragment[{i}]='{entry.Strings[i].Value}'");
            for (int i = 0; i < entry.Variables.Count; i++)
                Console.WriteLine($"[pbprobe] variable[{i}]=0x{entry.Variables[i]:X8}");
            foreach (string candidate in new[]
                { "COUNT", "NUM", "NUMBER", "ITEMS", "TOTAL", "AMOUNT", "N" })
            {
                Console.WriteLine(
                    $"[pbprobe] hash('{candidate}')=0x{DatStringResolver.ComputeHash(candidate):X8}");
            }
        }
        else
        {
            Console.WriteLine("[pbprobe] entry NOT FOUND in 0x23000001");
        }
    }

    [Fact]
    public void ProbeUiItemCatalog()
    {
        if (Environment.GetEnvironmentVariable("ACDREAM_PROBE_POWERBAR") != "1")
            Assert.Fail("Lane=Manual powerbar probe requires ACDREAM_PROBE_POWERBAR=1.");

        var datDir = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var strings = new DatStringResolver(dats);

        ElementInfo? root = LayoutImporter.ImportInfos(dats, 0x21000037u);
        Assert.NotNull(root);
        DumpElement(strings, root!, 0);
    }

    [Fact]
    public void ProbeCharacterSelectRowTemplate()
    {
        if (Environment.GetEnvironmentVariable("ACDREAM_PROBE_POWERBAR") != "1")
            Assert.Fail("Lane=Manual powerbar probe requires ACDREAM_PROBE_POWERBAR=1.");

        var datDir = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var strings = new DatStringResolver(dats);

        ElementInfo? row = LayoutImporter.ImportInfos(dats, 0x21000004u, 0x100003A5u);
        Assert.NotNull(row);
        Console.WriteLine("[pbprobe] === char-select roster row 0x21000004/0x100003A5 ===");
        DumpElement(strings, row!, 0);
    }

    private static ElementInfo? FindById(ElementInfo element, uint id)
    {
        if (element.Id == id) return element;
        foreach (ElementInfo child in element.Children)
            if (FindById(child, id) is { } found)
                return found;
        return null;
    }

    private static void Dump(
        DatCollection dats,
        DatStringResolver strings,
        uint layoutId,
        string label)
    {
        ElementInfo? root = LayoutImporter.ImportInfos(dats, layoutId);
        if (root is null)
        {
            Console.WriteLine($"[pbprobe] {label} 0x{layoutId:X8}: import FAILED");
            return;
        }
        Console.WriteLine($"[pbprobe] === {label} 0x{layoutId:X8} ===");
        DumpElement(strings, root, 0);
    }

    private static void DumpElement(DatStringResolver strings, ElementInfo e, int depth)
    {
        string indent = new(' ', depth * 2);
        string media = string.Join(
            " ",
            e.StateMedia
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => $"'{pair.Key}'=0x{pair.Value.File:X8}"));
        Console.WriteLine(
            $"[pbprobe] {indent}0x{e.Id:X8} type={e.Type} ({e.X},{e.Y} {e.Width}x{e.Height}) "
            + $"defaultState=0x{e.DefaultStateId:X8}('{e.DefaultStateName}') states={e.States.Count} "
            + $"hjustify={e.HJustify} fontColor={(e.FontColor is { } fc ? fc.ToString() : "none")} "
            + $"media[{media}]");
        foreach (var (stateId, state) in e.States)
        {
            string text = "";
            if (state.Properties.Values.TryGetValue(0x17u, out var p)
                && p.Kind == UiPropertyKind.StringInfo)
            {
                text = strings.Resolve(p.StringInfoValue) ?? "<unresolved>";
                text = $" text='{text}'";
            }
            string properties = string.Join(
                " ",
                state.Properties.Values
                    .OrderBy(static pair => pair.Key)
                    .Select(static pair =>
                        $"0x{pair.Key:X2}:{pair.Value.Kind}=0x{pair.Value.UnsignedValue:X}"));
            Console.WriteLine(
                $"[pbprobe] {indent}  state 0x{stateId:X8} '{state.Name}'"
                + $" passToChildren={state.PassToChildren} mediaCount={state.MediaCount}"
                + $"{text} props[{properties}]");
        }
        foreach (ElementInfo child in e.Children)
            DumpElement(strings, child, depth + 1);
    }
}
