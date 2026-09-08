using System.IO;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using DatReaderWriter;
using DatReaderWriter.Options;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "Manual")]
[Trait("ManualTask", "LiveMountProbe")]
public sealed class KeyboardConfigLiveMountProbeTests
{
    [Fact]
    public void ProbeKeyboardLiveMount()
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

        ElementInfo? info = LayoutImporter.ImportInfos(
            dats, KeyboardConfigController.LayoutId);
        Assert.NotNull(info);

        // Production shape A — what MountKeyboardConfig actually built at the
        // gate (NO string resolver on the main Build; the template build DOES
        // pass one — the asymmetry under investigation).
        ImportedLayout withoutStrings = LayoutImporter.Build(
            info!, _ => (0u, 0, 0), null);
        // Shape B — the same build WITH the resolver every sibling mount passes.
        ImportedLayout withStrings = LayoutImporter.Build(
            info!, _ => (0u, 0, 0), null, null, strings.Resolve);

        Console.WriteLine(
            $"[kbprobe] layout root id=0x{info!.Id:X8} rect=({withoutStrings.Root.Left},{withoutStrings.Root.Top} "
            + $"{withoutStrings.Root.Width}x{withoutStrings.Root.Height}) children={withoutStrings.Root.Children.Count}");

        DumpTreeRects(withoutStrings.Root, 0, maxDepth: 3);
        UiElement? windowRoot = withoutStrings.FindElement(
            KeyboardConfigController.WindowRootElementId);
        Console.WriteLine(windowRoot is null
            ? "[kbprobe] window root 0x1000001F: MISSING from flat index"
            : $"[kbprobe] window root 0x1000001F rect=({windowRoot.Left},{windowRoot.Top} "
              + $"{windowRoot.Width}x{windowRoot.Height}) children={windowRoot.Children.Count} "
              + $"type={windowRoot.GetType().Name}");

        DumpCaptions("without-strings", withoutStrings.Root);
        DumpCaptions("with-strings", withStrings.Root);

        // Observation 2 — "some buttons were outside of the window": every
        // element whose screen rect escapes the root's own extent.
        DumpOutOfBounds(withoutStrings.Root);

        Assert.Null(withStrings.FindElement(0x1000002Eu));
        Assert.Null(withStrings.FindElement(0x1000002Fu));
        foreach ((uint id, string expected) in new[]
        {
            (0x1000002Au, "Defaults"),
            (0x1000002Bu, "Revert"),
            (0x1000002Cu, "OK"),
            (0x1000002Du, "Cancel"),
        })
        {
            UiElement? el = withStrings.FindElement(id);
            UiButton button = Assert.IsType<UiButton>(el);
            Assert.Equal(expected, button.Label);
        }

        foreach ((string name, uint pageId) in new[]
        {
            ("Movement", 0x1000049Du),
            ("Camera", 0x1000049Fu),
            ("Combat", 0x100004A1u),
            ("UI", 0x100004A3u),
            ("CharacterSettings", 0x10000211u),
            ("Emote", 0x100004A5u),
        })
        {
            UiElement? page = UiElement.FindDescendant(withStrings.Root, pageId);
            UiElement? lb = page is null ? null : UiElement.FindDescendant(page, 0x10000025u);
            Console.WriteLine(
                $"[kbprobe] page {name} 0x{pageId:X8} -> {(page is null ? "MISSING" : $"({page.Left},{page.Top} {page.Width}x{page.Height})")} "
                + $"listbox -> {(lb is null ? "MISSING" : $"{lb.GetType().Name} ({lb.Left},{lb.Top} {lb.Width}x{lb.Height})")}");
            if (lb is UiTemplateListBox tlb)
            {
                for (int i = 0; i < tlb.Templates.Count; i++)
                {
                    (uint layoutId, uint elementId) =
                        (tlb.Templates[i].TemplateLayoutId, tlb.Templates[i].TemplateElementId);
                    ElementInfo? tInfo = LayoutImporter.ImportInfos(dats, layoutId, elementId);
                    if (tInfo is null)
                    {
                        Console.WriteLine($"[kbprobe]   template[{i}] 0x{layoutId:X8}/0x{elementId:X8} -> IMPORT MISSING");
                        continue;
                    }
                    UiElement built = LayoutImporter.Build(
                        tInfo, _ => (0u, 0, 0), null, null, strings.Resolve).Root;
                    Console.WriteLine(
                        $"[kbprobe]   template[{i}] 0x{layoutId:X8}/0x{elementId:X8} -> {built.GetType().Name} "
                        + $"({built.Left},{built.Top} {built.Width}x{built.Height}) children={built.Children.Count}");
                    foreach (uint keyBtn in new[] { 0x10000030u, 0x10000031u, 0x10000032u })
                    {
                        UiElement? b = UiElement.FindDescendant(built, keyBtn);
                        if (b is not null)
                            Console.WriteLine(
                                $"[kbprobe]     key-button 0x{keyBtn:X8} ({b.Left},{b.Top} {b.Width}x{b.Height})");
                    }
                }
            }
            // Only the first page's templates matter for geometry (all six share
            // the same authored list) — stop after one full dump.
            if (lb is not null) break;
        }
    }

    [Fact]
    public void ProbeKeyboardFontsAndKeyNameStrings()
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

        foreach (uint did in new[] { 0x40000000u, 0x40000001u, 0x4000000Au, 0x4000000Fu })
        {
            if (dats.TryGet<DatReaderWriter.DBObjs.Font>(did, out var font) && font is not null)
                Console.WriteLine(
                    $"[kbfont] font 0x{did:X8} MaxCharHeight={font.MaxCharHeight} "
                    + $"glyphs={font.CharDescs.Count} fg=0x{font.ForegroundSurfaceDataId:X8} "
                    + $"bg=0x{font.BackgroundSurfaceDataId:X8}");
            else
                Console.WriteLine($"[kbfont] font 0x{did:X8} -> MISSING from dat");
        }

        // Authored FontDids on the live-imported templates.
        foreach (uint templateId in new[] { 0x1000002Eu, 0x1000002Fu })
        {
            ElementInfo? tInfo = LayoutImporter.ImportInfos(
                dats, KeyboardConfigController.LayoutId, templateId);
            if (tInfo is null)
            {
                Console.WriteLine($"[kbfont] template 0x{templateId:X8} -> IMPORT MISSING");
                continue;
            }
            DumpFontDids(tInfo, 0);
        }

        // (b) Which font DIDs the production-shaped template build requests.
        {
            ElementInfo? rowInfo = LayoutImporter.ImportInfos(
                dats, KeyboardConfigController.LayoutId, 0x1000002Fu);
            Assert.NotNull(rowInfo);
            var requested = new List<uint>();
            UiElement built = LayoutImporter.Build(
                rowInfo!, _ => (0u, 0, 0), null,
                did => { requested.Add(did); return null; },
                strings.Resolve).Root;
            Console.WriteLine(
                "[kbfont] row-template build requested fonts: "
                + string.Join(", ", requested.Select(d => $"0x{d:X8}")));
            foreach (uint keyBtn in new[] { 0x10000030u, 0x10000031u, 0x10000032u })
            {
                if (UiElement.FindDescendant(built, keyBtn) is UiButton b)
                    Console.WriteLine(
                        $"[kbfont] key-button 0x{keyBtn:X8} LabelFont={(b.LabelFont is null ? "<null>" : "set")}");
            }
        }

        string[] keys =
        {
            "ID_ActionKeyMap_MapInstructions",
            "ID_ActionKeyMap_Binding",
            "ID_ActionKeyMap_ButtonLabel",
            "ID_ActionKeyMap_NonUserBindableBinding",
            "ID_ActionKeyMap_OverwriteExistingBinding",
            "ID_ActionKeyMap_OverwriteExistingBindings",
            "ID_ActionKeyMap_TT_ExistingBinding",
            "ID_ActionKeyMap_TT_NewBinding",
            "ID_KeyDescDelimiter",
            "ID_KeyNameWithSubControl",
            "ID_KeyMapCantOverwriteReadOnlyKeymap_Label",
            "DIK_W", "DIK_X", "DIK_S", "DIK_LSHIFT", "DIK_UP", "DIK_LCONTROL",
            "DIK_LMENU", "DIK_RSHIFT", "DIK_RCONTROL", "DIK_RMENU",
            "DIK_NUMPADENTER", "DIK_DELETE", "DIK_INSERT", "DIK_PRIOR", "DIK_NEXT",
            "MOUSE_B1", "SHIFT", "CTRL", "ALT",
        };
        for (uint table = 0x23000001u; table <= 0x2300000Cu; table++)
        {
            DatReaderWriter.DBObjs.StringTable? st = null;
            try { st = dats.Get<DatReaderWriter.DBObjs.StringTable>(table); }
            catch { }
            if (st is null) continue;
            foreach (string key in keys)
            {
                if (!st.Strings.TryGetValue(DatStringResolver.ComputeHash(key), out var entry)
                    || entry.Strings.Count == 0)
                    continue;
                string fragments = string.Join(
                    "¦", entry.Strings.Select(s => s.Value));
                string variables = entry.Variables.Count == 0
                    ? ""
                    : " vars=[" + string.Join(",", entry.Variables.Select(v => $"0x{v:X8}")) + "]";
                Console.WriteLine(
                    $"[kbstr] table 0x{table:X8} '{key}' -> '{fragments}'{variables}");
            }
        }

        foreach (string candidate in new[]
                 {
                     "ACTION", "BINDINGS", "KEY", "LABEL", "VALUE",
                     "NAME", "SUBCONTROL", "PLAYER", "COMMAND",
                 })
            Console.WriteLine(
                $"[kbstr] hash('{candidate}') = 0x{DatStringResolver.ComputeHash(candidate):X8}");

        foreach (uint category in new uint[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })
        {
            foreach (uint enumValue in new uint[] { 3, 4, 5, 0x10000004 })
            {
                uint did = AcDream.Content.RetailDataIdResolver.Resolve(dats, enumValue, category);
                if (did != 0)
                    Console.WriteLine(
                        $"[kbenum] category={category} enum=0x{enumValue:X} -> DID 0x{did:X8}");
            }
        }

        {
            ElementInfo? waitInfo = LayoutImporter.ImportInfos(dats, 0x2100003Cu, 0x31u);
            if (waitInfo is null)
                Console.WriteLine("[kbwait] wait root 0x31 -> IMPORT MISSING from 0x2100003C");
            else
            {
                DumpFontDids(waitInfo, 0);
                ImportedLayout waitBuilt = LayoutImporter.Build(
                    waitInfo, _ => (0u, 0, 0), null, null, strings.Resolve);
                Assert.IsType<UiDialogRoot>(waitBuilt.Root);
                Assert.NotNull(waitBuilt.FindElement(0x3Du));
                Assert.IsType<UiText>(waitBuilt.FindElement(0x3Eu));
            }
        }
    }

    private static void DumpFontDids(ElementInfo info, int depth)
    {
        Console.WriteLine(
            $"[kbfont] {new string(' ', depth * 2)}0x{info.Id:X8} type={info.Type} "
            + $"FontDid=0x{info.FontDid:X8} rect=({info.X},{info.Y} {info.Width}x{info.Height})");
        foreach (ElementInfo child in info.Children)
            DumpFontDids(child, depth + 1);
    }

    private static void DumpCaptions(string tag, UiElement root)
    {
        Walk(root, el =>
        {
            string? caption = el switch
            {
                UiButton b => b.Label,
                UiText t => string.Join(
                    " / ", t.LinesProvider().Select(static l => l.Text)),
                _ => null,
            };
            if (el is UiButton or UiText)
                Console.WriteLine(
                    $"[kbprobe] {tag} 0x{el.EventId:X8} {el.GetType().Name} "
                    + $"({el.Left},{el.Top} {el.Width}x{el.Height}) caption='{caption ?? "<null>"}'");
        });
    }

    private static void DumpOutOfBounds(UiElement root)
    {
        Walk(root, el =>
        {
            var p = el.ScreenPosition;
            bool outside = p.X < root.Left - 0.5f || p.Y < root.Top - 0.5f
                || p.X + el.Width > root.Left + root.Width + 0.5f
                || p.Y + el.Height > root.Top + root.Height + 0.5f;
            if (outside)
                Console.WriteLine(
                    $"[kbprobe] OUT-OF-BOUNDS 0x{el.EventId:X8} {el.GetType().Name} "
                    + $"screen=({p.X},{p.Y} {el.Width}x{el.Height}) rootExtent={root.Width}x{root.Height}");
        });
    }

    private static void Walk(UiElement el, Action<UiElement> visit)
    {
        visit(el);
        foreach (UiElement c in el.Children)
            Walk(c, visit);
    }

    private static void DumpTreeRects(UiElement el, int depth, int maxDepth)
    {
        Console.WriteLine(
            $"[kbprobe] tree {new string(' ', depth * 2)}{el.GetType().Name} "
            + $"({el.Left},{el.Top} {el.Width}x{el.Height}) children={el.Children.Count}");
        if (depth >= maxDepth) return;
        foreach (UiElement c in el.Children)
            DumpTreeRects(c, depth + 1, maxDepth);
    }
}
