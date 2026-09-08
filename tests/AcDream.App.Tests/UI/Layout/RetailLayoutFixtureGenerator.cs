using System.Runtime.CompilerServices;
using System.Text.Json;
using AcDream.App.UI.Layout;
using DatReaderWriter;
using DatReaderWriter.Options;
using EnumIDMap = DatReaderWriter.DBObjs.EnumIDMap;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "Manual")]
[Trait("ManualTask", "FixtureGeneration")]
public sealed class RetailLayoutFixtureGenerator
{
    private static readonly (uint Id, string FileName)[] Layouts =
    {
        (ChatWindowController.LayoutId, "chat_2100006f.json"),
        (FloatingChatWindowController.LayoutId, "chat_floaty_2100005b.json"),
        (0x21000016u, "toolbar_21000016.json"),
        (0x2100001Du, "link_status_2100001D.json"),
        (0x21000020u, "vitae_21000020.json"),
        (0x21000023u, "inventory_21000023.json"),
        (0x21000024u, "paperdoll_21000024.json"),
        (0x2100002Eu, "character_2100002E.json"),
        (SpellbookWindowController.LayoutId, "spellbook_21000034.json"),
        (0x2100006Cu, "vitals_2100006C.json"),
        (IndicatorBarController.LayoutId, "indicators_21000071.json"),
        (0x21000072u, "powerbar_21000072.json"),
        (0x21000073u, "combat_21000073.json"),
        (0x21000074u, "radar_21000074.json"),
        (0x2100002Bu, "options_2100002B.json"),
        (0x2100002Au, "options_gameplay_2100002A.json"),
        (0x21000028u, "options_character_21000028.json"),
        (0x2100005Cu, "options_chat_2100005C.json"),
        (0x21000029u, "options_config_21000029.json"),
        (0x21000009u, "keyboard_config_21000009.json"),
    };

    [Fact]
    public void RegenerateAllRetailFixtures_WhenExplicitlyRequested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("ACDREAM_REGENERATE_UI_FIXTURES"),
                "1",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Manual fixture generation was not requested; set "
                    + "ACDREAM_REGENERATE_UI_FIXTURES=1 and ACDREAM_DAT_DIR, "
                    + "then run the Manual lane explicitly.");
        }

        var datDir = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var localStrings = new DatStringResolver(dats);
        foreach ((string key, string expected) in new[]
        {
            ("ID_SpellComp_Category_Scarabs", "SCARABS"),
            ("ID_SpellComp_Category_Herbs", "HERBS"),
            ("ID_SpellComp_Category_Gems", "POWDERED GEMS"),
            ("ID_SpellComp_Category_Alchemical", "ALCHEMICAL SUBSTANCES"),
            ("ID_SpellComp_Category_Talismans", "TALISMANS"),
            ("ID_SpellComp_Category_Tapers", "TAPERS"),
            ("ID_SpellComp_Category_Peas", "PEAS"),
        })
        {
            Assert.Equal(expected, localStrings.Resolve(
                0x23000001u, DatStringResolver.ComputeHash(key)));
        }
        Assert.Equal(
            "SELECT A SPELL",
            localStrings.Resolve(
                0x23000001u,
                DatStringResolver.ComputeHash("ID_Effects_Info_SelectASpell")));

        uint masterDid = (uint)dats.Portal.Header.MasterMapId;
        Assert.True(dats.Portal.TryGet<EnumIDMap>(masterDid, out var master));
        Assert.True(master!.ClientEnumToID.TryGetValue(5u, out uint uiMapDid));
        Assert.True(dats.Portal.TryGet<EnumIDMap>(uiMapDid, out var uiMap));
        Assert.True(uiMap!.ClientEnumToID.TryGetValue(2u, out uint dialogsLayoutDid));
        var dialogs = LayoutImporter.ImportInfos(
            dats,
            dialogsLayoutDid,
            RetailDialogFactory.RootElementId(RetailDialogType.Confirmation));
        Assert.NotNull(dialogs);
        var dialogsJson = JsonSerializer.Serialize(dialogs, new JsonSerializerOptions
        {
            IncludeFields = true,
            WriteIndented = true,
        });
        File.WriteAllText(
            Path.Combine(FixtureDirectory(), $"dialogs_{dialogsLayoutDid:X8}.json"),
            dialogsJson);

        const uint smartboxLayoutDid = 0x2100000Fu;
        var fps = LayoutImporter.ImportInfos(
            dats,
            smartboxLayoutDid,
            0x10000047u);
        Assert.NotNull(fps);
        Assert.Equal(0x4000001Au, fps!.FontDid);
        var fpsStrings = new DatStringResolver(dats);
        Assert.Equal("FPS: ", fpsStrings.Resolve(0x23000001u, 0x0DCFFF73u, 0));
        Assert.Equal(@"\nDEG: ", fpsStrings.Resolve(0x23000001u, 0x0DCFFF73u, 1));
        var fpsJson = JsonSerializer.Serialize(fps, new JsonSerializerOptions
        {
            IncludeFields = true,
            WriteIndented = true,
        });
        File.WriteAllText(
            Path.Combine(FixtureDirectory(), $"smartbox_fps_{smartboxLayoutDid:X8}.json"),
            fpsJson);

        foreach ((uint rootId, string fileName) in new[]
        {
            (EffectsUiController.PositiveRootId, "effects_positive_2100001B.json"),
            (EffectsUiController.NegativeRootId, "effects_negative_2100001B.json"),
        })
        {
            ElementInfo? effects = LayoutImporter.ImportInfos(
                dats, EffectsUiController.LayoutId, rootId);
            Assert.NotNull(effects);
            var effectsJson = JsonSerializer.Serialize(effects, new JsonSerializerOptions
            {
                IncludeFields = true,
                WriteIndented = true,
            });
            File.WriteAllText(Path.Combine(FixtureDirectory(), fileName), effectsJson);
        }

        foreach ((uint layoutId, uint rootId, string fileName) in new[]
        {
            (EffectsUiController.LayoutId, EffectsUiController.RowTemplateId,
                "effects_row_2100001B_10000128.json"),
            (CharacterController.LayoutId, CharacterController.RootId,
                "character_info_2100006E_10000183.json"),
            (0x2100006Bu, 0x100005F2u, "examine_2100006B_100005F2.json"),
            (AppraisalUiController.LayoutId, CreatureAppraisalRowTemplateFactory.TemplateId,
                "examine_row_2100006B_10000166.json"),
            (AppraisalUiController.LayoutId, SpellExamineComponentTemplateFactory.TemplateId,
                "examine_component_2100006B_1000032E.json"),
            (VendorUiController.LayoutId, VendorUiController.RootId,
                "vendor_21000012_100000B7.json"),
            (OptionsPanelController.HostLayoutId, OptionsPanelController.SlotElementId,
                "options_panel_2100006E_1000018D.json"),
            (SocialPanelController.HostLayoutId, SocialPanelController.SlotElementId,
                "social_panel_2100006E_1000018F.json"),
            (MapHousePanelController.HostLayoutId, MapHousePanelController.SlotElementId,
                "map_house_2100006E_1000018C.json"),
        })
        {
            ElementInfo? panelPart = LayoutImporter.ImportInfos(dats, layoutId, rootId);
            Assert.NotNull(panelPart);
            File.WriteAllText(
                Path.Combine(FixtureDirectory(), fileName),
                JsonSerializer.Serialize(panelPart, new JsonSerializerOptions
                {
                    IncludeFields = true,
                    WriteIndented = true,
                }));
        }

        CreatureDisplayNameResolver creatureNames =
            CreatureDisplayNameResolver.Load(dats);
        Assert.Equal("Ghost", creatureNames.Resolve(77));

        ElementInfo? miniGame = LayoutImporter.ImportInfos(
            dats, MiniGameUiController.LayoutId, MiniGameUiController.RootId);
        Assert.NotNull(miniGame);
        File.WriteAllText(
            Path.Combine(FixtureDirectory(), "mini_game_2100001E.json"),
            JsonSerializer.Serialize(miniGame, new JsonSerializerOptions
            {
                IncludeFields = true,
                WriteIndented = true,
            }));

        foreach ((uint rootId, string fileName) in new[]
        {
            (0x10000466u,
                "component_category_21000033_10000466.json"),
            (0x10000467u,
                "component_row_21000033_10000467.json"),
        })
        {
            ElementInfo? componentTemplate = LayoutImporter.ImportInfos(
                dats, 0x21000033u, rootId);
            Assert.NotNull(componentTemplate);
            var componentJson = JsonSerializer.Serialize(componentTemplate,
                new JsonSerializerOptions
                {
                    IncludeFields = true,
                    WriteIndented = true,
                });
            File.WriteAllText(Path.Combine(FixtureDirectory(), fileName), componentJson);
        }

        foreach (var (layoutId, fileName) in Layouts)
        {
            var info = LayoutImporter.ImportInfos(dats, layoutId);
            Assert.NotNull(info);
            if (layoutId == CombatUiController.LayoutId)
            {
                var labels = CombatUiLabels.Resolve(info, new DatStringResolver(dats));
                Assert.Equal("Speed", labels.Speed);
                Assert.Equal("Power", labels.Power);
                Assert.Equal("Repeat Attacks", labels.RepeatAttacks);
                Assert.Equal("Auto Target", labels.AutoTarget);
                Assert.Equal("Keep in View", labels.KeepInView);
                Assert.Equal("High", labels.High);
                Assert.Equal("Medium", labels.Medium);
                Assert.Equal("Low", labels.Low);
                var strings = new DatStringResolver(dats);
                uint[] tabIds =
                {
                    0x100000A3u, 0x100000A4u, 0x100000A5u, 0x100000A6u,
                    0x100000A7u, 0x100000A8u, 0x100000A9u, 0x100005C2u,
                };
                string[] expected = ["I", "II", "III", "IV", "V", "VI", "VII", "VIII"];
                for (int i = 0; i < tabIds.Length; i++)
                {
                    uint id = tabIds[i];
                    ElementInfo tab = FindInfo(info, id)!;
                    Assert.NotNull(tab);
                    Assert.True(tab.TryGetEffectiveProperty(0x17u, out UiPropertyValue label));
                    Assert.Equal(expected[i], strings.Resolve(label.StringInfoValue));
                }
            }
            var json = JsonSerializer.Serialize(info, new JsonSerializerOptions
            {
                IncludeFields = true,
                WriteIndented = true,
            });
            File.WriteAllText(Path.Combine(FixtureDirectory(), fileName), json);
        }

    }

    private static string FixtureDirectory([CallerFilePath] string thisFile = "")
        => Path.Combine(Path.GetDirectoryName(thisFile)!, "fixtures");

    private static ElementInfo? FindInfo(ElementInfo root, uint id)
    {
        if (root.Id == id) return root;
        foreach (ElementInfo child in root.Children)
            if (FindInfo(child, id) is { } match) return match;
        return null;
    }
}
