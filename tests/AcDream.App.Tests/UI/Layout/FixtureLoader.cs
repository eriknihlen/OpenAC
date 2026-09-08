using System.IO;
using System.Text.Json;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public static class FixtureLoader
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        IncludeFields = true,
    };

    public static ImportedLayout LoadVitals()
    {
        var root = LoadVitalsInfos();
        return LayoutImporter.Build(root, _ => (0u, 0, 0), null);
    }

    public static AcDream.App.UI.Layout.ElementInfo LoadVitalsInfos()
        => LoadInfos("vitals_2100006C.json");

    public static ImportedLayout LoadChat()
        => LayoutImporter.Build(LoadChatInfos(), _ => (0u, 0, 0), null);

    public static AcDream.App.UI.Layout.ElementInfo LoadChatInfos()
        => LoadInfos("chat_2100006f.json");

    public static ImportedLayout LoadFloatyChat()
        => LayoutImporter.Build(LoadFloatyChatInfos(), _ => (0u, 0, 0), null);

    public static AcDream.App.UI.Layout.ElementInfo LoadFloatyChatInfos()
        => LoadInfos("chat_floaty_2100005b.json");

    public static ImportedLayout LoadRadar()
        => LayoutImporter.Build(LoadRadarInfos(), _ => (0u, 0, 0), null);

    public static AcDream.App.UI.Layout.ElementInfo LoadRadarInfos()
        => LoadInfos("radar_21000074.json");

    public static ImportedLayout LoadToolbar()
        => LayoutImporter.Build(LoadToolbarInfos(), _ => (0u, 0, 0), null);

    public static ElementInfo LoadToolbarInfos()
        => LoadInfos("toolbar_21000016.json");

    public static ImportedLayout LoadInventory()
        => LayoutImporter.Build(LoadInventoryInfos(), _ => (0u, 0, 0), null);

    public static ElementInfo LoadInventoryInfos()
        => LoadInfos("inventory_21000023.json");

    public static ImportedLayout LoadPaperdoll()
        => LayoutImporter.Build(LoadPaperdollInfos(), _ => (0u, 0, 0), null);

    public static ElementInfo LoadPaperdollInfos()
        => LoadInfos("paperdoll_21000024.json");

    public static ImportedLayout LoadCharacter()
        => LayoutImporter.Build(LoadCharacterInfos(), _ => (0u, 0, 0), null);

    public static ElementInfo LoadCharacterInfos()
        => LoadInfos("character_2100002E.json");

    public static ImportedLayout LoadCombat()
        => LayoutImporter.Build(LoadCombatInfos(), _ => (0u, 0, 0), null);

    public static ElementInfo LoadCombatInfos()
        => LoadInfos("combat_21000073.json");

    public static ImportedLayout LoadSpellbook()
        => LayoutImporter.Build(LoadSpellbookInfos(), _ => (0u, 0, 0), null);

    public static ElementInfo LoadSpellbookInfos()
        => LoadInfos("spellbook_21000034.json");

    public static ElementInfo LoadComponentCategoryTemplateInfos()
        => LoadInfos("component_category_21000033_10000466.json");

    public static ElementInfo LoadComponentRowTemplateInfos()
        => LoadInfos("component_row_21000033_10000467.json");

    public static ImportedLayout LoadExamination()
        => LayoutImporter.Build(LoadExaminationInfos(), _ => (0u, 0, 0), null);

    public static ElementInfo LoadExaminationInfos()
        => LoadInfos("examine_2100006B_100005F2.json");

    public static ElementInfo LoadExaminationRowTemplateInfos()
        => LoadInfos("examine_row_2100006B_10000166.json");

    public static ElementInfo LoadExaminationComponentTemplateInfos()
        => LoadInfos("examine_component_2100006B_1000032E.json");

    public static ImportedLayout LoadPowerbar()
        => LayoutImporter.Build(LoadPowerbarInfos(), _ => (0u, 0, 0), null);

    public static ElementInfo LoadPowerbarInfos()
        => LoadInfos("powerbar_21000072.json");

    public static ImportedLayout LoadConfirmationDialog()
        => LayoutImporter.Build(LoadConfirmationDialogInfos(), _ => (0u, 0, 0), null);

    public static ElementInfo LoadConfirmationDialogInfos()
        => LoadInfos("dialogs_2100003C.json");

    public static ImportedLayout LoadFpsDisplay()
        => LayoutImporter.Build(LoadFpsDisplayInfos(), _ => (0u, 0, 0), null);

    public static ElementInfo LoadFpsDisplayInfos()
        => LoadInfos("smartbox_fps_2100000F.json");

    public static ImportedLayout LoadPositiveEffects()
        => LayoutImporter.Build(LoadPositiveEffectsInfos(), _ => (0u, 0, 0), null);

    public static ElementInfo LoadPositiveEffectsInfos()
        => LoadInfos("effects_positive_2100001B.json");

    public static ImportedLayout LoadNegativeEffects()
        => LayoutImporter.Build(LoadNegativeEffectsInfos(), _ => (0u, 0, 0), null);

    public static ElementInfo LoadNegativeEffectsInfos()
        => LoadInfos("effects_negative_2100001B.json");

    public static ElementInfo LoadEffectRowTemplateInfos()
        => LoadInfos("effects_row_2100001B_10000128.json");

    public static ImportedLayout LoadCharacterInformation()
        => LayoutImporter.Build(
            LoadCharacterInformationInfos(), _ => (0u, 0, 0), null);

    public static ElementInfo LoadCharacterInformationInfos()
        => LoadInfos("character_info_2100006E_10000183.json");

    public static ImportedLayout LoadIndicators()
        => LayoutImporter.Build(LoadIndicatorsInfos(), _ => (0u, 0, 0), null);

    public static ElementInfo LoadIndicatorsInfos()
        => LoadInfos("indicators_21000071.json");

    public static ImportedLayout LoadLinkStatus()
        => LayoutImporter.Build(LoadLinkStatusInfos(), _ => (0u, 0, 0), null);

    public static ElementInfo LoadLinkStatusInfos()
        => LoadInfos("link_status_2100001D.json");

    public static ImportedLayout LoadVitae()
        => LayoutImporter.Build(LoadVitaeInfos(), _ => (0u, 0, 0), null);

    public static ElementInfo LoadVitaeInfos()
        => LoadInfos("vitae_21000020.json");

    public static ImportedLayout LoadMiniGame()
        => LayoutImporter.Build(LoadMiniGameInfos(), _ => (0u, 0, 0), null);

    public static ElementInfo LoadMiniGameInfos()
        => LoadInfos("mini_game_2100001E.json");

    public static ImportedLayout LoadVendor()
        => LayoutImporter.Build(LoadVendorInfos(), _ => (0u, 0, 0), null);

    public static ElementInfo LoadVendorInfos()
        => LoadInfos("vendor_21000012_100000B7.json");

    public static ImportedLayout LoadOptionsPanel()
        => LayoutImporter.Build(LoadOptionsPanelInfos(), _ => (0u, 0, 0), null);

    public static ElementInfo LoadOptionsPanelInfos()
        => LoadInfos("options_2100002B.json");

    public static ImportedLayout LoadOptionsGameplay()
        => LayoutImporter.Build(LoadOptionsGameplayInfos(), _ => (0u, 0, 0), null);

    public static ElementInfo LoadOptionsGameplayInfos()
        => LoadInfos("options_gameplay_2100002A.json");

    public static ImportedLayout LoadOptionsCharacter()
        => LayoutImporter.Build(LoadOptionsCharacterInfos(), _ => (0u, 0, 0), null);

    public static ElementInfo LoadOptionsCharacterInfos()
        => LoadInfos("options_character_21000028.json");

    public static ImportedLayout LoadOptionsChat()
        => LayoutImporter.Build(LoadOptionsChatInfos(), _ => (0u, 0, 0), null);

    public static ElementInfo LoadOptionsChatInfos()
        => LoadInfos("options_chat_2100005C.json");

    public static ImportedLayout LoadOptionsConfig()
        => LayoutImporter.Build(LoadOptionsConfigInfos(), _ => (0u, 0, 0), null);

    public static ElementInfo LoadOptionsConfigInfos()
        => LoadInfos("options_config_21000029.json");

    public static ImportedLayout LoadOptionsPanelHost()
        => LayoutImporter.Build(LoadOptionsPanelHostInfos(), _ => (0u, 0, 0), null);

    public static ElementInfo LoadOptionsPanelHostInfos()
        => LoadInfos("options_panel_2100006E_1000018D.json");

    public static ImportedLayout LoadSocialPanelHost()
        => LayoutImporter.Build(LoadSocialPanelHostInfos(), _ => (0u, 0, 0), null);

    public static ElementInfo LoadSocialPanelHostInfos()
        => LoadInfos("social_panel_2100006E_1000018F.json");

    public static ImportedLayout LoadMapHouseHost()
        => LayoutImporter.Build(LoadMapHouseHostInfos(), _ => (0u, 0, 0), null);

    public static ElementInfo LoadMapHouseHostInfos()
        => LoadInfos("map_house_2100006E_1000018C.json");

    public static ImportedLayout LoadKeyboardConfig()
        => LayoutImporter.Build(LoadKeyboardConfigInfos(), _ => (0u, 0, 0), null);

    public static ElementInfo LoadKeyboardConfigInfos()
        => LoadInfos("keyboard_config_21000009.json");

    // ── Shared loader ────────────────────────────────────────────────────────

    private static AcDream.App.UI.Layout.ElementInfo LoadInfos(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "UI", "Layout", "fixtures", fileName);
        if (!File.Exists(path)) throw new FileNotFoundException($"fixture not found at: {path}");
        var bytes = File.ReadAllBytes(path);
        // Strip UTF-8 BOM (EF BB BF) if present so JsonSerializer.Deserialize<T>(ReadOnlySpan<byte>)
        // does not reject the first byte.
        ReadOnlySpan<byte> span = bytes;
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
            span = span[3..];
        return JsonSerializer.Deserialize<AcDream.App.UI.Layout.ElementInfo>(span, _opts)
            ?? throw new InvalidOperationException($"fixture deserialized to null: {path}");
    }
}
