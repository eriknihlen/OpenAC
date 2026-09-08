using System.IO;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.UI.Abstractions.Tests.Panels.Settings;

public sealed class SettingsStoreTests : System.IDisposable
{
    private readonly string _tempPath;

    public SettingsStoreTests()
    {
        _tempPath = Path.Combine(
            Path.GetTempPath(),
            $"acdream-settings-test-{System.Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }

    [Fact]
    public void LoadDisplay_returns_defaults_when_file_is_missing()
    {
        var store = new SettingsStore(_tempPath);
        var loaded = store.LoadDisplay();
        Assert.Equal(DisplaySettings.Default, loaded);
    }

    [Fact]
    public void SaveDisplay_then_LoadDisplay_round_trips_all_fields()
    {
        var store = new SettingsStore(_tempPath);
        var original = new DisplaySettings(
            Resolution: "2560x1440",
            Fullscreen: true,
            VSync: false,
            FieldOfView: 100f,
            Gamma: 1.4f,
            ShowFps: true,
            Quality: AcDream.UI.Abstractions.Settings.QualityPreset.Ultra,
            ParticleRange: ParticleRange.Extended)
        {
            RenderPack = new RenderPackSelectionSettings(
                "acdream.atmospheric",
                "1.0.0",
                "high")
            {
                SettingOverrides = new RenderPackSettingOverrides(
                    new Dictionary<string, string>
                    {
                        ["exposure"] = "1.25",
                        ["sun-rays"] = "true",
                    }),
            },
        };

        store.SaveDisplay(original);
        var loaded = store.LoadDisplay();

        Assert.Equal(original, loaded);
        Assert.Equal("1.25", loaded.RenderPack.SettingOverrides["exposure"]);
        Assert.Equal("true", loaded.RenderPack.SettingOverrides["sun-rays"]);
    }

    [Fact]
    public void LoadDisplay_falls_back_to_defaults_when_file_is_corrupt()
    {
        File.WriteAllText(_tempPath, "{ this is not valid json");
        var store = new SettingsStore(_tempPath);

        var loaded = store.LoadDisplay();

        Assert.Equal(DisplaySettings.Default, loaded);
    }

    [Fact]
    public void LoadDisplay_falls_back_per_field_when_keys_missing()
    {
        // Partial file — only resolution set; everything else should
        // pick up DisplaySettings.Default values.
        File.WriteAllText(_tempPath, """
            {
              "version": 1,
              "display": { "resolution": "1366x768" }
            }
            """);
        var store = new SettingsStore(_tempPath);

        var loaded = store.LoadDisplay();

        Assert.Equal("1366x768", loaded.Resolution);
        Assert.Equal(DisplaySettings.Default.Fullscreen, loaded.Fullscreen);
        Assert.Equal(DisplaySettings.Default.VSync, loaded.VSync);
        Assert.Equal(DisplaySettings.Default.FieldOfView, loaded.FieldOfView);
        Assert.Equal(ParticleRange.Extended, loaded.ParticleRange);
        Assert.Equal(RenderPackSelectionSettings.Retail, loaded.RenderPack);
        Assert.Empty(loaded.RenderPack.SettingOverrides);
    }

    [Fact]
    public void LoadDisplay_upgrades_pre_pack_file_to_retail_without_a_write()
    {
        File.WriteAllText(_tempPath, """
            {
              "version": 3,
              "display": {
                "resolution": "1920x1080",
                "fullscreen": false
              }
            }
            """);

        DisplaySettings loaded = new SettingsStore(_tempPath).LoadDisplay();

        Assert.Equal("1920x1080", loaded.Resolution);
        Assert.Equal(RenderPackSelectionSettings.Retail, loaded.RenderPack);
    }


    [Theory]
    [InlineData(60f, 90f)]
    [InlineData(45f, 75.5f)]
    [InlineData(120f, 160f)]
    public void LoadDisplay_migrates_pre_v3_fieldOfView(float stored, float expected)
    {
        File.WriteAllText(_tempPath, $$"""
            {
              "version": 2,
              "display": { "fieldOfView": {{stored}} }
            }
            """);
        var store = new SettingsStore(_tempPath);

        Assert.Equal(expected, store.LoadDisplay().FieldOfView, precision: 1);
    }

    [Fact]
    public void LoadDisplay_does_not_migrate_a_v3_fieldOfView()
    {
        // A post-migration 60 is a deliberate gameFOV — it must survive.
        File.WriteAllText(_tempPath, """
            {
              "version": 3,
              "display": { "fieldOfView": 60 }
            }
            """);
        var store = new SettingsStore(_tempPath);

        Assert.Equal(60f, store.LoadDisplay().FieldOfView);
    }

    [Fact]
    public void LoadDisplay_absent_fieldOfView_needs_no_migration()
    {
        File.WriteAllText(_tempPath, """
            {
              "version": 2,
              "display": { "resolution": "1920x1080" }
            }
            """);
        var store = new SettingsStore(_tempPath);

        Assert.Equal(90f, store.LoadDisplay().FieldOfView);
    }

    [Fact]
    public void SaveDisplay_stamps_v3_so_the_migration_runs_once()
    {
        File.WriteAllText(_tempPath, """
            {
              "version": 2,
              "display": { "fieldOfView": 60 }
            }
            """);
        var store = new SettingsStore(_tempPath);

        DisplaySettings migrated = store.LoadDisplay();   // 60 → 90
        store.SaveDisplay(migrated);                      // stamps version 3

        // A second load must NOT re-migrate the already-migrated 90.
        Assert.Equal(90f, store.LoadDisplay().FieldOfView);
    }

    [Fact]
    public void LoadDisplay_invalid_particle_range_falls_back_to_default()
    {
        File.WriteAllText(_tempPath, """
            {
              "version": 2,
              "display": { "particleRange": "Unlimited" }
            }
            """);

        var loaded = new SettingsStore(_tempPath).LoadDisplay();

        Assert.Equal(ParticleRange.Extended, loaded.ParticleRange);
    }

    [Fact]
    public void LoadDisplay_numeric_undefined_particle_range_falls_back_to_default()
    {
        File.WriteAllText(_tempPath, """
            {
              "version": 2,
              "display": { "particleRange": "999" }
            }
            """);

        var loaded = new SettingsStore(_tempPath).LoadDisplay();

        Assert.Equal(ParticleRange.Extended, loaded.ParticleRange);
    }

    [Fact]
    public void SaveDisplay_preserves_unknown_top_level_keys()
    {
        File.WriteAllText(_tempPath, """
            {
              "version": 1,
              "display": { "resolution": "1280x720" },
              "audio": { "master": 0.5, "music": 0.7 }
            }
            """);
        var store = new SettingsStore(_tempPath);

        store.SaveDisplay(DisplaySettings.Default with { Resolution = "1920x1080" });

        var raw = File.ReadAllText(_tempPath);
        Assert.Contains("\"audio\"", raw);
        Assert.Contains("\"master\"", raw);
        Assert.Contains("0.5", raw);
        // And the new display value did get written.
        Assert.Contains("1920x1080", raw);
    }

    // -- Audio section round-trip ----------------------------------------

    [Fact]
    public void LoadAudio_returns_defaults_when_file_is_missing()
    {
        var store = new SettingsStore(_tempPath);
        Assert.Equal(AudioSettings.Default, store.LoadAudio());
    }

    [Fact]
    public void SaveAudio_then_LoadAudio_round_trips_all_fields()
    {
        var store = new SettingsStore(_tempPath);
        var original = new AudioSettings(Master: 0.3f, Sfx: 0.9f, Ambient: 0.6f);

        store.SaveAudio(original);
        var loaded = store.LoadAudio();

        Assert.Equal(original, loaded);
    }

    [Fact]
    public void LoadAudio_falls_back_per_field_when_keys_missing()
    {
        File.WriteAllText(_tempPath, """
            {
              "version": 1,
              "audio": { "master": 0.25 }
            }
            """);
        var store = new SettingsStore(_tempPath);

        var loaded = store.LoadAudio();

        Assert.Equal(0.25f, loaded.Master);
        Assert.Equal(AudioSettings.Default.Sfx, loaded.Sfx);
        Assert.Equal(AudioSettings.Default.Ambient, loaded.Ambient);
    }

    [Fact]
    public void SaveAudio_preserves_display_section()
    {
        // Save display first, then audio — display values must survive.
        var store = new SettingsStore(_tempPath);
        store.SaveDisplay(DisplaySettings.Default with { Resolution = "2560x1440" });
        store.SaveAudio(AudioSettings.Default with { Master = 0.4f });

        Assert.Equal("2560x1440", store.LoadDisplay().Resolution);
        Assert.Equal(0.4f, store.LoadAudio().Master);
    }

    [Fact]
    public void SaveDisplay_after_SaveAudio_preserves_audio_section()
    {
        // Reverse order — audio must survive a subsequent display save.
        var store = new SettingsStore(_tempPath);
        store.SaveAudio(AudioSettings.Default with { Ambient = 0.1f });
        store.SaveDisplay(DisplaySettings.Default with { ShowFps = true });

        Assert.Equal(0.1f, store.LoadAudio().Ambient);
        Assert.True(store.LoadDisplay().ShowFps);
    }


    [Fact]
    public void LeftoverGameplaySection_FromAnOlderSettingsJson_DoesNotBreakOtherLoads()
    {
        File.WriteAllText(_tempPath, """
            {
              "version": 2,
              "display": { "resolution": "1366x768" },
              "gameplay": { "lockUI": true, "toggleRun": false }
            }
            """);
        var store = new SettingsStore(_tempPath);

        Assert.Equal("1366x768", store.LoadDisplay().Resolution);
        Assert.Equal(AudioSettings.Default, store.LoadAudio());
        Assert.Equal(ChatSettings.Default, store.LoadChat());
    }

    [Fact]
    public void LeftoverGameplaySection_SurvivesAnUnrelatedSave()
    {
        File.WriteAllText(_tempPath, """
            {
              "version": 2,
              "gameplay": { "lockUI": true, "showHelm": false }
            }
            """);
        var store = new SettingsStore(_tempPath);

        store.SaveDisplay(DisplaySettings.Default with { Resolution = "1920x1080" });

        var raw = File.ReadAllText(_tempPath);
        Assert.Contains("\"gameplay\"", raw);
        Assert.Contains("\"lockUI\": true", raw);
        Assert.Contains("\"showHelm\": false", raw);
        Assert.Contains("1920x1080", raw);
    }


    [Fact]
    public void LoadChat_returns_defaults_when_file_is_missing()
    {
        var store = new SettingsStore(_tempPath);
        Assert.Equal(ChatSettings.Default, store.LoadChat());
    }

    [Fact]
    public void SaveChat_then_LoadChat_round_trips_all_fields()
    {
        var store = new SettingsStore(_tempPath);
        var original = new ChatSettings(
            HearGeneralChat:  false,
            HearTradeChat:    false,
            HearLFGChat:      false,
            HearRoleplayChat: true,
            HearSocietyChat:  true,
            AppearOffline:    true,
            ShowTimestamps:   false,
            FilterProfanity:  false,
            FontSize:         16f);

        store.SaveChat(original);
        Assert.Equal(original, store.LoadChat());
    }


    [Fact]
    public void LoadChat_returns_retail_PostInit_filter_defaults_when_file_is_missing()
    {
        var store = new SettingsStore(_tempPath);
        ChatSettings loaded = store.LoadChat();

        Assert.Equal(0x0000101Cu, loaded.ChatWindow1Filter);
        Assert.Equal(0x00040C00u, loaded.ChatWindow2Filter);
        Assert.Equal(0x00080000u, loaded.ChatWindow3Filter);
        Assert.Equal(0x78000000u, loaded.ChatWindow4Filter);
    }

    [Fact]
    public void SaveChat_then_LoadChat_round_trips_floating_window_filters()
    {
        var store = new SettingsStore(_tempPath);
        var original = ChatSettings.Default with
        {
            ChatWindow1Filter = 0x1u,
            ChatWindow2Filter = 0xFFFFFFFFu,
            ChatWindow3Filter = 0x8000000000000000u, // exercises the high dword (Society/reserved bits)
            ChatWindow4Filter = 0ul,
        };

        store.SaveChat(original);
        ChatSettings loaded = store.LoadChat();

        Assert.Equal(original.ChatWindow1Filter, loaded.ChatWindow1Filter);
        Assert.Equal(original.ChatWindow2Filter, loaded.ChatWindow2Filter);
        Assert.Equal(original.ChatWindow3Filter, loaded.ChatWindow3Filter);
        Assert.Equal(original.ChatWindow4Filter, loaded.ChatWindow4Filter);
    }


    [Fact]
    public void LoadChat_returns_retail_main_window_filter_default_when_file_is_missing()
    {
        var store = new SettingsStore(_tempPath);
        ChatSettings loaded = store.LoadChat();

        Assert.Equal(0xFBFFFFFFu, loaded.ChatWindowMainFilter);
    }

    [Fact]
    public void SaveChat_then_LoadChat_round_trips_main_window_filter()
    {
        var store = new SettingsStore(_tempPath);
        var original = ChatSettings.Default with { ChatWindowMainFilter = 0x1ul };

        store.SaveChat(original);
        ChatSettings loaded = store.LoadChat();

        Assert.Equal(original.ChatWindowMainFilter, loaded.ChatWindowMainFilter);
    }


    [Fact]
    public void LoadChat_returns_acdream_opaque_opacity_defaults_when_file_is_missing()
    {
        var store = new SettingsStore(_tempPath);
        ChatSettings loaded = store.LoadChat();

        Assert.Equal(1.0f, loaded.DefaultOpacity);
        Assert.Equal(1.0f, loaded.ActiveOpacity);
    }

    [Fact]
    public void SaveChat_then_LoadChat_round_trips_opacity()
    {
        var store = new SettingsStore(_tempPath);
        var original = ChatSettings.Default with
        {
            DefaultOpacity = 0.2f,
            ActiveOpacity = 0.8f,
        };

        store.SaveChat(original);
        ChatSettings loaded = store.LoadChat();

        Assert.Equal(original.DefaultOpacity, loaded.DefaultOpacity);
        Assert.Equal(original.ActiveOpacity, loaded.ActiveOpacity);
    }

    [Fact]
    public void All_three_sections_coexist_in_one_settings_json()
    {
        var store = new SettingsStore(_tempPath);
        store.SaveDisplay(DisplaySettings.Default with { Resolution = "2560x1440" });
        store.SaveAudio(AudioSettings.Default with { Master = 0.5f });
        store.SaveChat(ChatSettings.Default with { HearTradeChat = false, FontSize = 14f });

        Assert.Equal("2560x1440", store.LoadDisplay().Resolution);
        Assert.Equal(0.5f, store.LoadAudio().Master);
        Assert.False(store.LoadChat().HearTradeChat);
        Assert.Equal(14f, store.LoadChat().FontSize);
    }


    [Fact]
    public void LoadCharacter_returns_defaults_when_file_is_missing()
    {
        var store = new SettingsStore(_tempPath);
        Assert.Equal(CharacterSettings.Default, store.LoadCharacter("default"));
    }

    [Fact]
    public void LoadCharacter_returns_defaults_when_toonKey_not_in_file()
    {
        // File exists with a different toon's data; asking for "+Acdream"
        // returns defaults rather than the other toon's data.
        var store = new SettingsStore(_tempPath);
        store.SaveCharacter("Bob", CharacterSettings.Default with { AutoAttack = true });

        var loaded = store.LoadCharacter("+Acdream");
        Assert.Equal(CharacterSettings.Default, loaded);
    }

    [Fact]
    public void SaveCharacter_then_LoadCharacter_round_trips_all_fields()
    {
        var store = new SettingsStore(_tempPath);
        var original = new CharacterSettings(
            DefaultChatChannel: "Allegiance",
            AutoAttack:         true,
            ConfirmSalvage:     false,
            ShowPickupMessages: false);

        store.SaveCharacter("+Acdream", original);
        Assert.Equal(original, store.LoadCharacter("+Acdream"));
    }

    [Fact]
    public void SaveCharacter_preserves_other_toons_within_character_section()
    {
        var store = new SettingsStore(_tempPath);
        var alice = CharacterSettings.Default with { DefaultChatChannel = "Allegiance" };
        var bob   = CharacterSettings.Default with { DefaultChatChannel = "Fellowship", AutoAttack = true };

        store.SaveCharacter("Alice", alice);
        store.SaveCharacter("Bob", bob);

        Assert.Equal(alice, store.LoadCharacter("Alice"));
        Assert.Equal(bob,   store.LoadCharacter("Bob"));
    }

    [Fact]
    public void SaveCharacter_preserves_other_top_level_sections()
    {
        // Display/audio survive when SaveCharacter writes its nested map.
        var store = new SettingsStore(_tempPath);
        store.SaveDisplay(DisplaySettings.Default with { Resolution = "2560x1440" });
        store.SaveAudio(AudioSettings.Default with { Master = 0.4f });
        store.SaveCharacter("+Acdream", CharacterSettings.Default with { AutoAttack = true });

        Assert.Equal("2560x1440", store.LoadDisplay().Resolution);
        Assert.Equal(0.4f, store.LoadAudio().Master);
        Assert.True(store.LoadCharacter("+Acdream").AutoAttack);
    }

    [Fact]
    public void All_four_sections_coexist_in_one_settings_json()
    {
        var store = new SettingsStore(_tempPath);
        store.SaveDisplay(DisplaySettings.Default with { Resolution = "2560x1440" });
        store.SaveAudio(AudioSettings.Default with { Master = 0.5f });
        store.SaveChat(ChatSettings.Default with { HearTradeChat = false });
        store.SaveCharacter("+Acdream",
            CharacterSettings.Default with { DefaultChatChannel = "Fellowship" });

        Assert.Equal("2560x1440", store.LoadDisplay().Resolution);
        Assert.Equal(0.5f, store.LoadAudio().Master);
        Assert.False(store.LoadChat().HearTradeChat);
        Assert.Equal("Fellowship", store.LoadCharacter("+Acdream").DefaultChatChannel);
    }

    [Fact]
    public void WindowPositions_RoundTripPerCharacterAndPreserveSettings()
    {
        var store = new SettingsStore(_tempPath);
        store.SaveDisplay(DisplaySettings.Default with { Resolution = "2560x1440" });
        store.SaveWindowPosition("Alice", "radar", new UiWindowPosition(321.5f, 18f));
        store.SaveWindowPosition("Bob", "radar", new UiWindowPosition(44f, 55f));

        Assert.Equal(new UiWindowPosition(321.5f, 18f),
            store.LoadWindowPosition("Alice", "radar"));
        Assert.Equal(new UiWindowPosition(44f, 55f),
            store.LoadWindowPosition("Bob", "radar"));
        Assert.Null(store.LoadWindowPosition("Alice", "inventory"));
        Assert.Equal("2560x1440", store.LoadDisplay().Resolution);
    }

    [Fact]
    public void WindowLayouts_RoundTripPerCharacterResolutionAndPreserveSettings()
    {
        var store = new SettingsStore(_tempPath);
        store.SaveDisplay(DisplaySettings.Default with { Resolution = "2560x1440" });
        var alice1080 = new UiWindowLayout(10f, 20f, 500f, 300f, true, false, true);
        var alice1440 = new UiWindowLayout(30f, 40f, 650f, 420f, false, true, false);
        var bob1080 = new UiWindowLayout(50f, 60f, 310f, 132f, true, false, false);

        store.SaveWindowLayout("Alice", "1920x1080", "chat", alice1080);
        store.SaveWindowLayout("Alice", "2560x1440", "chat", alice1440);
        store.SaveWindowLayout("Bob", "1920x1080", "toolbar", bob1080);

        Assert.Equal(alice1080, store.LoadWindowLayout("Alice", "1920x1080", "chat", default));
        Assert.Equal(alice1440, store.LoadWindowLayout("Alice", "2560x1440", "chat", default));
        Assert.Equal(bob1080, store.LoadWindowLayout("Bob", "1920x1080", "toolbar", default));
        Assert.Null(store.LoadWindowLayout("Alice", "1920x1080", "inventory", default));
        Assert.Equal("2560x1440", store.LoadDisplay().Resolution);
    }

    [Fact]
    public void WindowLayouts_MigrateLegacyRadarPositionOverCurrentDefaults()
    {
        var store = new SettingsStore(_tempPath);
        store.SaveWindowPosition("Alice", "radar", new UiWindowPosition(321.5f, 18f));
        var current = new UiWindowLayout(10f, 10f, 160f, 180f, true, false, false);

        UiWindowLayout migrated = Assert.IsType<UiWindowLayout>(
            store.LoadWindowLayout("Alice", "1920x1080", "radar", current));

        Assert.Equal(321.5f, migrated.X);
        Assert.Equal(18f, migrated.Y);
        Assert.Equal(current.Width, migrated.Width);
        Assert.Equal(current.Height, migrated.Height);
        Assert.Equal(current.Visible, migrated.Visible);
    }

    [Fact]
    public void WindowLayouts_FallBackToNearestSavedResolution()
    {
        var store = new SettingsStore(_tempPath);
        var hd = new UiWindowLayout(100f, 110f, 400f, 250f, true, false, false);
        var uhd = new UiWindowLayout(300f, 310f, 600f, 450f, true, false, false);
        store.SaveWindowLayout("Alice", "1280x720", "chat", hd);
        store.SaveWindowLayout("Alice", "3840x2160", "chat", uhd);

        Assert.Equal(hd, store.LoadWindowLayout("Alice", "1600x900", "chat", default));
        Assert.Equal(uhd, store.LoadWindowLayout("Alice", "3200x1800", "chat", default));
    }

    [Fact]
    public void NamedWindowLayouts_RoundTripIndependentlyOfCharacterAndResolution()
    {
        var store = new SettingsStore(_tempPath);
        var hunting = new UiWindowLayout(10f, 20f, 500f, 250f, true, false, true);
        var crafting = new UiWindowLayout(30f, 40f, 300f, 400f, false, true, false);

        store.SaveNamedWindowLayout("hunting", "chat", hunting);
        store.SaveNamedWindowLayout("crafting", "chat", crafting);

        Assert.Equal(hunting, store.LoadNamedWindowLayout("hunting", "chat", default));
        Assert.Equal(crafting, store.LoadNamedWindowLayout("crafting", "chat", default));
        Assert.Null(store.LoadNamedWindowLayout("missing", "chat", default));
    }


    [Fact]
    public void LoadMisc_returns_defaults_when_file_is_missing()
    {
        var store = new SettingsStore(_tempPath);
        Assert.Equal(MiscSettings.Default, store.LoadMisc());
    }

    [Fact]
    public void SaveMisc_then_LoadMisc_round_trips_all_fields()
    {
        var store = new SettingsStore(_tempPath);
        var original = new MiscSettings(TooltipEnable: false, TooltipDelaySeconds: 1.5f);

        store.SaveMisc(original);
        var loaded = store.LoadMisc();

        Assert.Equal(original, loaded);
    }

    [Fact]
    public void LoadMisc_falls_back_per_field_when_keys_missing()
    {
        File.WriteAllText(_tempPath, """
            {
              "version": 3,
              "misc": { "tooltipEnable": false }
            }
            """);
        var store = new SettingsStore(_tempPath);

        var loaded = store.LoadMisc();

        Assert.False(loaded.TooltipEnable);
        Assert.Equal(MiscSettings.Default.TooltipDelaySeconds, loaded.TooltipDelaySeconds);
    }

    [Fact]
    public void SaveMisc_preserves_display_section_and_vice_versa()
    {
        var store = new SettingsStore(_tempPath);
        store.SaveDisplay(DisplaySettings.Default with { Resolution = "2560x1440" });
        store.SaveMisc(MiscSettings.Default with { TooltipDelaySeconds = 2f });

        Assert.Equal("2560x1440", store.LoadDisplay().Resolution);
        Assert.Equal(2f, store.LoadMisc().TooltipDelaySeconds);

        store.SaveDisplay(DisplaySettings.Default with { ShowFps = true });
        Assert.Equal(2f, store.LoadMisc().TooltipDelaySeconds);
        Assert.True(store.LoadDisplay().ShowFps);
    }
}
