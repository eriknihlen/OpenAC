using System.IO;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Content;
using AcDream.Core.Textures;
using DatReaderWriter;
using DatReaderWriter.Options;
using Palette = DatReaderWriter.DBObjs.Palette;
using RenderSurface = DatReaderWriter.DBObjs.RenderSurface;
using StringTable = DatReaderWriter.DBObjs.StringTable;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "InstalledDat")]
public sealed class CharacterManagementLiveDatTests
{
    [InstalledDatFact]
    public void EnumTable5_ResolvesAndImportsTheExactRetailScreenAndDialogs()
    {
        string datDirectory = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        using var dats = new DatCollection(datDirectory, DatAccessType.Read);

        const uint expectedLayoutDid = 0x21000004u;
        uint layoutDid = RetailDataIdResolver.Resolve(
            dats,
            CharacterManagementUiController.RootEnum,
            5u);
        Assert.Equal(expectedLayoutDid, layoutDid);
        Console.WriteLine(
            "[LA8-DAT] category=5 enum=0x10000005 -> DID=0x21000004; "
            + "selected-root=0x1000039A");

        ElementInfo rootInfo = Assert.IsType<ElementInfo>(
            LayoutImporter.ImportInfos(
                dats,
                layoutDid,
                CharacterManagementUiController.RootElementId));
        Assert.Equal(800f, rootInfo.Width);
        Assert.Equal(600f, rootInfo.Height);
        Assert.Equal(8, rootInfo.Children.Count);
        Assert.Equal(0x06007576u, rootInfo.StateMedia[""].File);
        ImportedLayout screen = LayoutImporter.Build(
            rootInfo,
            _ => (0u, 0, 0),
            null,
            null,
            new DatStringResolver(dats).Resolve);

        var list = Assert.IsType<UiTemplateListBox>(screen.FindElement(
            CharacterManagementUiController.ListElementId));
        UiTemplateListEntry template = Assert.Single(list.Templates);
        Assert.Equal(expectedLayoutDid, template.TemplateLayoutId);
        Assert.Equal(0x100003A5u, template.TemplateElementId);
        AssertButton(screen, CharacterManagementUiController.CreateElementId,
            "Create Character");
        AssertButton(screen, CharacterManagementUiController.EnterElementId,
            "ENTER");
        AssertButton(screen, CharacterManagementUiController.DeleteElementId,
            "DELETE");
        AssertButton(screen, CharacterManagementUiController.RestoreElementId,
            "RESTORE");
        AssertButton(screen, CharacterManagementUiController.CreditsElementId,
            "CREDITS");
        AssertButton(screen, CharacterManagementUiController.ExitElementId,
            "EXIT");
        Assert.IsType<UiText>(screen.FindElement(
            CharacterManagementUiController.WorldTextElementId));
        Assert.DoesNotContain(
            Descendants(screen.Root),
            static element => element is UiViewport);

        ElementInfo rowInfo = Assert.IsType<ElementInfo>(
            LayoutImporter.ImportInfos(dats, layoutDid, template.TemplateElementId));
        Assert.Equal(1u, rowInfo.Type);
        Assert.Equal(160f, rowInfo.Width);
        Assert.Equal(16f, rowInfo.Height);
        Assert.Equal(0x40000009u, rowInfo.FontDid);
        Assert.Equal(
        [
            UiButtonStateMachine.Normal,
            UiButtonStateMachine.NormalRollover,
            UiButtonStateMachine.NormalPressed,
            UiButtonStateMachine.Highlight,
            UiButtonStateMachine.HighlightRollover,
            uint.MaxValue,
        ],
            rowInfo.States.Keys.Order().ToArray());

        Assert.Equal(HJustify.Left, rowInfo.HJustify);
        Assert.DoesNotContain(rowInfo.Children, static child => child.Type == 12u);
        ImportedLayout? builtRowLayout = LayoutImporter.Import(
            dats, template.TemplateLayoutId, template.TemplateElementId,
            _ => (0u, 0, 0), null, null);
        var builtRow = Assert.IsType<UiButton>(builtRowLayout!.Root);
        Assert.Equal(UiButton.LabelAlignment.Left, builtRow.LabelAlign);

        uint dialogDid = RetailDataIdResolver.Resolve(dats, 2u, 5u);
        Assert.Equal(0x2100003Cu, dialogDid);
        ImportedLayout message = BuildSelected(dats, dialogDid, 0x24u);
        Assert.IsType<UiDialogRoot>(message.Root);
        Assert.IsType<UiText>(message.FindElement(0x3Eu));
        Assert.IsType<UiButton>(message.FindElement(0x26u));
        ImportedLayout delete = BuildSelected(dats, dialogDid, 0x2Cu);
        Assert.IsType<UiDialogRoot>(delete.Root);
        Assert.IsType<UiField>(delete.FindElement(0x2Cu));
        Assert.IsType<UiButton>(delete.FindElement(0x2Eu));
        Assert.IsType<UiButton>(delete.FindElement(0x2Fu));

        var strings = new DatStringResolver(dats);
        const uint table = 0x23000002u;
        Assert.Equal("DELETE", Resolve(strings, table,
            "ID_CharacterManagement_DeleteCharacterResponse"));
        Assert.Equal("Please Wait", Resolve(strings, table,
            "ID_CharacterManagement_PleaseWait"));
        Assert.Equal("Entering World", Resolve(strings, table,
            "ID_Character_EnteringWorld"));
        Assert.Equal("Are you sure you want to leave?\n", Resolve(strings, table,
            "ID_CharacterManagement_ConfirmExit"));
        string confirmation = Assert.IsType<string>(strings.ResolveTemplate(
            table,
            "ID_CharacterManagement_DeleteCharacterConfirmation",
            new Dictionary<uint, string>
            {
                [DatStringResolver.PlayerVariable] = "Test Character",
            }));
        Assert.Contains("Test Character", confirmation);
        Assert.Contains("'DELETE'", confirmation);

        StringTable stringTable = Assert.IsType<StringTable>(dats.Get<StringTable>(table));
        var deleteEntry = stringTable.Strings[
            DatStringResolver.ComputeHash(
                "ID_CharacterManagement_DeleteCharacterConfirmation")];
        Assert.Equal([DatStringResolver.PlayerVariable], deleteEntry.Variables);
    }

    [InstalledDatFact]
    public void EveryDeclaredMediaId_ResolvesToADecodableTexture()
    {
        string datDirectory = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        using var dats = new DatCollection(datDirectory, DatAccessType.Read);

        uint layoutDid = RetailDataIdResolver.Resolve(
            dats,
            CharacterManagementUiController.RootEnum,
            5u);
        ElementInfo rootInfo = Assert.IsType<ElementInfo>(
            LayoutImporter.ImportInfos(
                dats,
                layoutDid,
                CharacterManagementUiController.RootElementId));

        var ids = new SortedDictionary<uint, string>();
        CollectMediaIds(rootInfo, "root", ids);

        // Also walk the listbox's row template — it is imported separately by
        // AddItemFromTemplateList/TemplateResolver, not as a root descendant.
        ElementInfo? listInfo = FindById(rootInfo, CharacterManagementUiController.ListElementId);
        Assert.NotNull(listInfo);
        Assert.NotEmpty(listInfo!.TemplateList);
        foreach (var entry in listInfo.TemplateList)
        {
            ElementInfo? rowInfo = LayoutImporter.ImportInfos(
                dats, entry.TemplateLayoutId, entry.TemplateElementId);
            Assert.NotNull(rowInfo);
            CollectMediaIds(rowInfo!, "row-template", ids);
        }

        Console.WriteLine($"[LA8-DIAG] layout=0x{layoutDid:X8} distinct media ids={ids.Count}");
        Assert.NotEmpty(ids);
        Assert.Contains(0x06007576u, ids.Keys);

        var unresolved = new List<string>();
        foreach (var (id, where) in ids)
        {
            bool found = dats.Portal.TryGet<RenderSurface>(id, out RenderSurface? rs)
                || dats.HighRes.TryGet<RenderSurface>(id, out rs);
            if (!found)
            {
                Console.WriteLine($"[LA8-DIAG] 0x{id:X8} ({where}): NOT FOUND in Portal or HighRes");
                unresolved.Add($"0x{id:X8} ({where}): missing RenderSurface");
                continue;
            }

            Palette? palette = rs!.DefaultPaletteId != 0
                ? dats.Get<Palette>(rs.DefaultPaletteId)
                : null;
            DecodedTexture decoded = SurfaceDecoder.DecodeRenderSurface(rs, palette);
            bool magenta = decoded.Width == 1 && decoded.Height == 1
                && decoded.Rgba8 is [0xFF, 0x00, 0xFF, 0xFF];
            Console.WriteLine(
                $"[LA8-DIAG] 0x{id:X8} ({where}): format={rs.Format} "
                + $"{rs.Width}x{rs.Height} defaultPalette=0x{rs.DefaultPaletteId:X8} "
                + $"paletteLoaded={(palette is not null)} decoded={decoded.Width}x{decoded.Height} "
                + $"magenta={magenta}");
            if (magenta)
                unresolved.Add(
                    $"0x{id:X8} ({where}): format={rs.Format} defaultPalette=0x{rs.DefaultPaletteId:X8} "
                    + $"paletteLoaded={(palette is not null)}");
        }

        Assert.True(
            unresolved.Count == 0,
            "Media ids that resolved to the 1x1 magenta placeholder:\n"
            + string.Join('\n', unresolved));
    }

    [InstalledDatFact]
    public void RootAuthorsNoEdgeAnchors_RetailNeverResizesItSelf()
    {
        string datDirectory = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        using var dats = new DatCollection(datDirectory, DatAccessType.Read);

        uint layoutDid = RetailDataIdResolver.Resolve(
            dats,
            CharacterManagementUiController.RootEnum,
            5u);
        ElementInfo rootInfo = Assert.IsType<ElementInfo>(
            LayoutImporter.ImportInfos(
                dats,
                layoutDid,
                CharacterManagementUiController.RootElementId));

        Assert.Equal(0u, rootInfo.Left);
        Assert.Equal(0u, rootInfo.Top);
        Assert.Equal(0u, rootInfo.Right);
        Assert.Equal(0u, rootInfo.Bottom);
        Assert.Equal(3u, rootInfo.Type);
    }

    private static ElementInfo? FindById(ElementInfo info, uint id)
    {
        if (info.Id == id) return info;
        foreach (ElementInfo child in info.Children)
        {
            ElementInfo? found = FindById(child, id);
            if (found is not null) return found;
        }
        return null;
    }

    private static void CollectMediaIds(ElementInfo info, string where, SortedDictionary<uint, string> ids)
    {
        foreach (var (stateName, media) in info.StateMedia)
        {
            if (media.File == 0) continue;
            string label = $"{where} elem=0x{info.Id:X8} type={info.Type} state='{stateName}'";
            if (!ids.ContainsKey(media.File))
                ids[media.File] = label;
            else
                ids[media.File] += " | " + label;
        }
        foreach (ElementInfo child in info.Children)
            CollectMediaIds(child, where, ids);
    }

    private static ImportedLayout BuildSelected(
        IDatReaderWriter dats,
        uint layoutDid,
        uint rootId)
    {
        ElementInfo info = Assert.IsType<ElementInfo>(
            LayoutImporter.ImportInfos(dats, layoutDid, rootId));
        return LayoutImporter.Build(
            info,
            _ => (0u, 0, 0),
            null,
            null,
            new DatStringResolver(dats).Resolve);
    }

    private static string Resolve(
        DatStringResolver strings,
        uint table,
        string key) => Assert.IsType<string>(strings.Resolve(
            table,
            DatStringResolver.ComputeHash(key)));

    private static void AssertButton(
        ImportedLayout layout,
        uint elementId,
        string label) => Assert.Equal(
            label,
            Assert.IsType<UiButton>(layout.FindElement(elementId)).Label);

    private static IEnumerable<UiElement> Descendants(UiElement root)
    {
        yield return root;
        foreach (UiElement child in root.Children)
            foreach (UiElement descendant in Descendants(child))
                yield return descendant;
    }
}

internal sealed class InstalledDatFactAttribute : FactAttribute
{
    public InstalledDatFactAttribute()
    {
        bool requested = Environment.GetEnvironmentVariable(
                "ACDREAM_RUN_INSTALLED_DAT_TESTS") == "1"
            || Environment.GetEnvironmentVariable("ACDREAM_PROBE_LIVE_MOUNT") == "1";
        if (!requested)
        {
            Skip = "Installed-DAT lane not requested; set "
                + "ACDREAM_RUN_INSTALLED_DAT_TESTS=1. The legacy "
                + "ACDREAM_PROBE_LIVE_MOUNT=1 switch is also accepted.";
            return;
        }

        string datDirectory = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        if (!File.Exists(Path.Combine(datDirectory, "client_portal.dat")))
            Skip = $"Installed client_portal.dat is required at '{datDirectory}'.";
    }
}
