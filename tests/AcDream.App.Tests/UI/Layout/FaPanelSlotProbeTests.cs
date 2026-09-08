using System.IO;
using AcDream.App.UI.Layout;
using DatReaderWriter;
using DatReaderWriter.Options;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Purpose", "Diagnostic")]
[Trait("Lane", "Manual")]
[Trait("ManualTask", "LiveMountProbe")]
public sealed class FaPanelSlotProbeTests
{
    [Fact]
    public void ProbePanelSlotTable()
    {
        if (Environment.GetEnvironmentVariable("ACDREAM_PROBE_LIVE_MOUNT") != "1")
            Assert.Fail("Lane=Manual live-mount probe requires ACDREAM_PROBE_LIVE_MOUNT=1.");

        var datDir = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        foreach (uint slotId in new[]
        {
            0x1000018Bu, 0x1000018Fu, 0x1000018Eu, 0x10000559u,
            0x1000018Cu, 0x10000182u, 0x1000018Du, 0x10000190u,
            0x10000184u, 0x10000185u, 0x10000181u, 0x10000189u,
            0x10000183u, 0x1000018Au, 0x10000187u, 0x10000188u,
            0x10000186u,
        })
        {
            ElementInfo? slot = LayoutImporter.ImportInfos(dats, 0x2100006Eu, slotId);
            if (slot is null)
            {
                Console.WriteLine($"[faslot] 0x{slotId:X8} -> IMPORT NULL");
                continue;
            }

            string panelId = slot.TryGetEffectiveProperty(0x10000029u, out var p)
                ? $"{p.UnsignedValue} (kind={p.Kind})"
                : "ABSENT";

            bool hasFellowshipName = FindInfo(slot, 0x1000026Fu);
            bool hasAllegiance = FindInfo(slot, 0x10000263u) || FindInfo(slot, 0x10000264u)
                || FindInfo(slot, 0x10000265u) || FindInfo(slot, 0x10000268u)
                || FindInfo(slot, 0x10000269u) || FindInfo(slot, 0x10000492u);
            string family = hasFellowshipName ? " <= FELLOWSHIP"
                : hasAllegiance ? " <= ALLEGIANCE"
                : string.Empty;

            Console.WriteLine(
                $"[faslot] 0x{slotId:X8} panelId={panelId} type={slot.Type} "
                + $"({slot.X},{slot.Y} {slot.Width}x{slot.Height}) "
                + $"children={slot.Children.Count}{family}");

            bool unidentified = slotId is 0x1000018Fu;
            if (unidentified)
            {
                foreach (ElementInfo c in slot.Children)
                {
                    Console.WriteLine(
                        $"[faslot]     child 0x{c.Id:X8} type={c.Type} ({c.X},{c.Y} {c.Width}x{c.Height}) kids={c.Children.Count}");
                    foreach (ElementInfo g in c.Children)
                        Console.WriteLine(
                            $"[faslot]         g 0x{g.Id:X8} type={g.Type} ({g.X},{g.Y} {g.Width}x{g.Height})");
                }
            }
        }
    }

    private static bool FindInfo(ElementInfo info, uint id)
    {
        if (info.Id == id) return true;
        foreach (ElementInfo c in info.Children)
            if (FindInfo(c, id)) return true;
        return false;
    }
}
