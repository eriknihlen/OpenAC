//! Ported from OpenAC `tests/AcDream.UI.Abstractions.Tests/Panels/Settings/`:
//! `SettingsStoreTests.cs` (audio, chat, character, window, misc and the
//! cross-section cases), `WindowPlacementStoreTests.cs`,
//! `ChatOpacityLinkTests.cs`, `ChatSettingsTests.cs`,
//! `CharacterSettingsTests.cs`, `AudioSettingsTests.cs`, plus
//! `tests/AcDream.App.Tests/UI/Layout/MouseTurningSettingsMacroTests.cs` and
//! the defaults block of `ConfigOptionsPageControllerTests.cs`.

use std::path::PathBuf;

use super::super::{DisplaySettings, SettingsStore};
use super::*;
use crate::settings::{UiWindowLayout, UiWindowPlacement, UiWindowPosition};

struct Temp(PathBuf);

impl Temp {
    fn new(name: &str) -> Self {
        let path = std::env::temp_dir().join(format!(
            "openac-settings-sections-{name}-{}-{}.json",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        let _ = std::fs::remove_file(&path);
        Self(path)
    }
    fn store(&self) -> SettingsStore {
        SettingsStore::new(self.0.clone())
    }
    fn write(&self, text: &str) {
        std::fs::write(&self.0, text).unwrap();
    }
    fn raw(&self) -> String {
        std::fs::read_to_string(&self.0).unwrap()
    }
}

impl Drop for Temp {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(&self.0);
    }
}

// -- defaults (ConfigOptionsPageControllerTests, ChatSettingsTests,
//    CharacterSettingsTests, AudioSettingsTests) -------------------------

#[test]
fn audio_defaults_match_the_retail_trio_defaults() {
    let d = AudioSettings::default();
    assert_eq!((d.master, d.sfx, d.ambient), (1.0, 1.0, 1.0));
    assert_eq!(d.sound_features, 0);
    assert!(d.sfx_enabled && d.ambient_enabled && d.interface_enabled);
    assert_eq!(d.interface_volume, 1.0);
    assert!(d.play_sound_only_when_active);
}

#[test]
fn camera_defaults_match_the_retail_config_defaults() {
    let d = CameraTurningSettings::default();
    assert_eq!(d.stiffness, 0.45);
    assert_eq!(d.adjustment_speed, 40.0);
    assert_eq!(d.mouse_look_sensitivity, 0.55);
    assert!(d.align_to_slope);
    assert!(!d.invert_mouse_look_y_axis);
    assert!(!d.use_mouse_turning);
}

#[test]
fn chat_defaults_match_the_retail_config_defaults() {
    let d = ChatSettings::default();
    assert_eq!((d.chat_font_face, d.chat_font_size_index), (2, 1));
    assert!(d.hear_general_chat && d.hear_trade_chat && d.hear_lfg_chat);
    assert!(!d.hear_roleplay_chat && !d.hear_society_chat);
    assert!(!d.appear_offline && d.show_timestamps && d.filter_profanity);
    assert_eq!(d.font_size, 12.0);
}

#[test]
fn character_defaults_and_channels() {
    let d = CharacterSettings::default();
    assert_eq!(d.default_chat_channel, "Local");
    assert!(!d.auto_attack && d.confirm_salvage && d.show_pickup_messages);
    assert_eq!(CharacterSettings::AVAILABLE_CHANNELS[0], "Local");
    assert_eq!(CharacterSettings::AVAILABLE_CHANNELS.len(), 7);
}

// -- audio ----------------------------------------------------------------

#[test]
fn load_audio_returns_defaults_when_the_file_is_missing() {
    let t = Temp::new("audio-missing");
    assert_eq!(t.store().load_audio(), (AudioSettings::default(), None));
}

#[test]
fn save_audio_then_load_audio_round_trips_all_fields() {
    let t = Temp::new("audio-roundtrip");
    let original = AudioSettings {
        master: 0.3,
        sfx: 0.9,
        ambient: 0.6,
        sound_features: 1,
        sfx_enabled: false,
        ambient_enabled: false,
        interface_enabled: false,
        interface_volume: 0.25,
        play_sound_only_when_active: false,
    };
    t.store().save_audio(&original).unwrap();
    assert_eq!(t.store().load_audio(), (original, None));
}

#[test]
fn a_profile_without_the_enabled_keys_loads_with_sound_on() {
    let t = Temp::new("audio-old");
    t.write(r#"{ "version": 3, "audio": { "master": 0.8, "sfx": 0.7, "ambient": 0.6 } }"#);
    let a = t.store().load_audio().0;
    assert_eq!((a.master, a.sfx, a.ambient), (0.8, 0.7, 0.6));
    assert!(a.sfx_enabled && a.ambient_enabled && a.interface_enabled);
    assert!(a.play_sound_only_when_active);
}

#[test]
fn load_audio_falls_back_per_field_when_keys_are_missing() {
    let t = Temp::new("audio-partial");
    t.write(r#"{ "version": 1, "audio": { "master": 0.25 } }"#);
    let a = t.store().load_audio().0;
    assert_eq!(a.master, 0.25);
    assert_eq!(a.sfx, AudioSettings::default().sfx);
    assert_eq!(a.ambient, AudioSettings::default().ambient);
}

#[test]
fn save_audio_keeps_the_display_section_and_the_other_way_round() {
    let t = Temp::new("audio-display");
    let store = t.store();
    store
        .save_display(&DisplaySettings {
            resolution: "2560x1440".into(),
            ..DisplaySettings::default()
        })
        .unwrap();
    store
        .save_audio(&AudioSettings {
            master: 0.4,
            ..AudioSettings::default()
        })
        .unwrap();
    assert_eq!(store.load_display().0.resolution, "2560x1440");
    assert_eq!(store.load_audio().0.master, 0.4);

    let t = Temp::new("display-audio");
    let store = t.store();
    store
        .save_audio(&AudioSettings {
            ambient: 0.1,
            ..AudioSettings::default()
        })
        .unwrap();
    store
        .save_display(&DisplaySettings {
            show_fps: true,
            ..DisplaySettings::default()
        })
        .unwrap();
    assert_eq!(store.load_audio().0.ambient, 0.1);
    assert!(store.load_display().0.show_fps);
}

#[test]
fn saving_another_section_migrates_the_display_section_before_stamping_the_version() {
    let t = Temp::new("migrate-other");
    t.write(
        r#"{ "version": 3, "display": { "landscapeTextureDetail": 4, "environmentTextureDetail": 4, "resolution": "1920x1080" } }"#,
    );
    let store = t.store();
    store
        .save_audio(&AudioSettings {
            master: 0.4,
            ..AudioSettings::default()
        })
        .unwrap();
    let d = store.load_display().0;
    assert_eq!(d.landscape_texture_detail, 0);
    assert_eq!(d.environment_texture_detail, 1);
    assert_eq!(d.resolution, "1920x1080");
    assert!(t.raw().contains("\"version\": 4"));
}

#[test]
fn a_nested_save_also_migrates_the_display_section() {
    let t = Temp::new("migrate-nested");
    t.write(r#"{ "version": 2, "display": { "landscapeTextureDetail": 3 } }"#);
    let store = t.store();
    store
        .save_character("+Acdream", &CharacterSettings::default())
        .unwrap();
    assert_eq!(store.load_display().0.landscape_texture_detail, 0);
}

#[test]
fn a_leftover_gameplay_section_breaks_no_load_and_survives_an_unrelated_save() {
    let t = Temp::new("gameplay");
    t.write(
        r#"{ "version": 2, "display": { "resolution": "1366x768" }, "gameplay": { "lockUI": true, "showHelm": false } }"#,
    );
    let store = t.store();
    assert_eq!(store.load_display().0.resolution, "1366x768");
    assert_eq!(store.load_audio().0, AudioSettings::default());
    assert_eq!(store.load_chat().0, ChatSettings::default());
    store
        .save_display(&DisplaySettings {
            resolution: "1920x1080".into(),
            ..DisplaySettings::default()
        })
        .unwrap();
    let raw = t.raw();
    assert!(raw.contains("\"gameplay\""));
    assert!(raw.contains("\"lockUI\": true"));
    assert!(raw.contains("\"showHelm\": false"));
    assert!(raw.contains("1920x1080"));
}

#[test]
fn a_value_of_the_wrong_type_gives_the_section_defaults_and_a_warning() {
    let t = Temp::new("chat-bad");
    t.write(r#"{ "version": 4, "chat": { "chatFontFace": 2.5 } }"#);
    let (chat, warning) = t.store().load_chat();
    assert_eq!(chat, ChatSettings::default());
    assert!(warning.is_some_and(|w| w.contains("using defaults")));
}

// -- mixer ----------------------------------------------------------------

#[test]
fn the_mixer_round_trips_and_is_normalized_on_load() {
    let t = Temp::new("mixer");
    let store = t.store();
    assert_eq!(store.load_audio_mixer().0, AudioMixerSettings::default());
    let m = AudioMixerSettings {
        retail_mixer: true,
        voice_count: 48,
        use_authored_priority: false,
        max_voices_per_wave: 0,
    };
    store.save_audio_mixer(&m).unwrap();
    assert_eq!(store.load_audio_mixer().0, m);
    t.write(r#"{ "version": 4, "audioMixer": { "voiceCount": 300, "maxVoicesPerWave": -2 } }"#);
    let m = store.load_audio_mixer().0;
    assert_eq!((m.voice_count, m.max_voices_per_wave), (64, 0));
}

// -- chat -----------------------------------------------------------------

#[test]
fn chat_round_trips_every_field() {
    let t = Temp::new("chat");
    let store = t.store();
    assert_eq!(store.load_chat(), (ChatSettings::default(), None));
    let original = ChatSettings {
        hear_general_chat: false,
        hear_trade_chat: false,
        hear_lfg_chat: false,
        hear_roleplay_chat: true,
        hear_society_chat: true,
        appear_offline: true,
        show_timestamps: false,
        filter_profanity: false,
        font_size: 16.0,
        ..ChatSettings::default()
    };
    store.save_chat(&original).unwrap();
    assert_eq!(store.load_chat().0, original);
}

#[test]
fn chat_filters_default_to_the_retail_post_init_values_and_round_trip() {
    let t = Temp::new("chat-filters");
    let store = t.store();
    let d = store.load_chat().0;
    assert_eq!(d.chat_window1_filter, 0x0000_101C);
    assert_eq!(d.chat_window2_filter, 0x0004_0C00);
    assert_eq!(d.chat_window3_filter, 0x0008_0000);
    assert_eq!(d.chat_window4_filter, 0x7800_0000);
    assert_eq!(d.chat_window_main_filter, 0xFBFF_FFFF);
    let original = ChatSettings {
        chat_window1_filter: 0x1,
        chat_window2_filter: 0xFFFF_FFFF,
        // The high dword (Society and the reserved bits).
        chat_window3_filter: 0x8000_0000_0000_0000,
        chat_window4_filter: 0,
        chat_window_main_filter: 0x1,
        ..ChatSettings::default()
    };
    store.save_chat(&original).unwrap();
    assert_eq!(store.load_chat().0, original);
}

#[test]
fn chat_opacity_defaults_opaque_and_round_trips() {
    let t = Temp::new("chat-opacity");
    let store = t.store();
    let d = store.load_chat().0;
    assert_eq!((d.default_opacity, d.active_opacity), (1.0, 1.0));
    let original = ChatSettings {
        default_opacity: 0.2,
        active_opacity: 0.8,
        ..ChatSettings::default()
    };
    store.save_chat(&original).unwrap();
    let loaded = store.load_chat().0;
    assert_eq!((loaded.default_opacity, loaded.active_opacity), (0.2, 0.8));
}

#[test]
fn chat_font_face_and_size_round_trip() {
    let t = Temp::new("chat-font");
    let store = t.store();
    let original = ChatSettings {
        chat_font_face: 4,
        chat_font_size_index: 3,
        ..ChatSettings::default()
    };
    store.save_chat(&original).unwrap();
    let loaded = store.load_chat().0;
    assert_eq!((loaded.chat_font_face, loaded.chat_font_size_index), (4, 3));
}

#[test]
fn the_chat_filter_of_each_window_is_addressed_by_its_number() {
    let mut c = ChatSettings::default();
    assert_eq!(c.filter(0), Some(0xFBFF_FFFF));
    c.set_filter(3, 7);
    assert_eq!(c.filter(3), Some(7));
    assert_eq!(c.filter(5), None);
}

// -- camera ---------------------------------------------------------------

#[test]
fn the_camera_section_round_trips_use_mouse_turning() {
    let t = Temp::new("camera");
    let store = t.store();
    let original = CameraTurningSettings {
        stiffness: 0.6,
        adjustment_speed: 70.0,
        mouse_look_sensitivity: 0.2,
        align_to_slope: false,
        invert_mouse_look_y_axis: true,
        use_mouse_turning: true,
        modern_mouse_turning: false,
        both_mouse_buttons_run_forward: false,
    };
    store.save_camera_turning(&original).unwrap();
    assert_eq!(store.load_camera_turning(), (original, None));
}

#[test]
fn modern_mouse_turning_is_off_by_default_and_round_trips_under_openacs_keys() {
    let d = CameraTurningSettings::default();
    assert!(!d.modern_mouse_turning);
    assert!(!d.both_mouse_buttons_run_forward);

    let t = Temp::new("camera-modern");
    let store = t.store();
    let modern = CameraTurningSettings {
        modern_mouse_turning: true,
        both_mouse_buttons_run_forward: true,
        ..d
    };
    store.save_camera_turning(&modern).unwrap();
    let raw = t.raw();
    assert!(raw.contains("\"modernMouseTurning\": true"), "{raw}");
    assert!(
        raw.contains("\"bothMouseButtonsRunForward\": true"),
        "{raw}"
    );
    assert_eq!(store.load_camera_turning(), (modern, None));

    // A file written before the options existed reads them as off.
    t.write(r#"{ "cameraTurning": { "stiffness": 0.6, "useMouseTurning": true } }"#);
    let (old, error) = store.load_camera_turning();
    assert_eq!(error, None);
    assert!(old.use_mouse_turning);
    assert!(!old.modern_mouse_turning && !old.both_mouse_buttons_run_forward);
}

#[test]
fn the_mouse_turning_macro_leaves_modern_mouse_turning_alone() {
    let current = CameraTurningSettings {
        modern_mouse_turning: true,
        both_mouse_buttons_run_forward: true,
        ..CameraTurningSettings::default()
    };
    let r = mouse_turning_macro(&current, false);
    assert!(r.updated.modern_mouse_turning);
    assert!(r.updated.both_mouse_buttons_run_forward);
}

// -- sections together ------------------------------------------------------

#[test]
fn all_four_sections_coexist_in_one_file() {
    let t = Temp::new("four");
    let store = t.store();
    store
        .save_display(&DisplaySettings {
            resolution: "2560x1440".into(),
            ..DisplaySettings::default()
        })
        .unwrap();
    store
        .save_audio(&AudioSettings {
            master: 0.5,
            ..AudioSettings::default()
        })
        .unwrap();
    store
        .save_chat(&ChatSettings {
            hear_trade_chat: false,
            font_size: 14.0,
            ..ChatSettings::default()
        })
        .unwrap();
    store
        .save_character(
            "+Acdream",
            &CharacterSettings {
                default_chat_channel: "Fellowship".into(),
                ..CharacterSettings::default()
            },
        )
        .unwrap();
    assert_eq!(store.load_display().0.resolution, "2560x1440");
    assert_eq!(store.load_audio().0.master, 0.5);
    assert!(!store.load_chat().0.hear_trade_chat);
    assert_eq!(store.load_chat().0.font_size, 14.0);
    assert_eq!(
        store.load_character("+Acdream").default_chat_channel,
        "Fellowship"
    );
    let (all, warnings) = store.load_player();
    assert!(warnings.is_empty());
    assert_eq!(all.display.resolution, "2560x1440");
    assert_eq!(all.audio.master, 0.5);
}

#[test]
fn an_unreadable_file_is_one_warning_for_every_section() {
    let t = Temp::new("player-corrupt");
    t.write("{ not json");
    let (all, warnings) = t.store().load_player();
    assert_eq!(all, PlayerSettings::default());
    assert_eq!(warnings.len(), 1);
}

// -- character --------------------------------------------------------------

#[test]
fn character_settings_are_per_character() {
    let t = Temp::new("character");
    let store = t.store();
    assert_eq!(
        store.load_character("default"),
        CharacterSettings::default()
    );
    store
        .save_character(
            "Bob",
            &CharacterSettings {
                auto_attack: true,
                ..CharacterSettings::default()
            },
        )
        .unwrap();
    assert_eq!(
        store.load_character("+Acdream"),
        CharacterSettings::default()
    );
    let original = CharacterSettings {
        default_chat_channel: "Allegiance".into(),
        auto_attack: true,
        confirm_salvage: false,
        show_pickup_messages: false,
    };
    store.save_character("+Acdream", &original).unwrap();
    assert_eq!(store.load_character("+Acdream"), original);
    assert!(store.load_character("Bob").auto_attack);
}

#[test]
fn save_character_keeps_the_other_sections() {
    let t = Temp::new("character-keep");
    let store = t.store();
    store
        .save_display(&DisplaySettings {
            resolution: "2560x1440".into(),
            ..DisplaySettings::default()
        })
        .unwrap();
    store
        .save_audio(&AudioSettings {
            master: 0.4,
            ..AudioSettings::default()
        })
        .unwrap();
    store
        .save_character(
            "+Acdream",
            &CharacterSettings {
                auto_attack: true,
                ..CharacterSettings::default()
            },
        )
        .unwrap();
    assert_eq!(store.load_display().0.resolution, "2560x1440");
    assert_eq!(store.load_audio().0.master, 0.4);
    assert!(store.load_character("+Acdream").auto_attack);
    assert!(t.raw().contains("\"version\": 4"));
}

// -- misc -------------------------------------------------------------------

#[test]
fn misc_round_trips_and_falls_back_per_field() {
    let t = Temp::new("misc");
    let store = t.store();
    assert_eq!(store.load_misc(), (MiscSettings::default(), None));
    let original = MiscSettings {
        tooltip_enable: false,
        tooltip_delay_seconds: 1.5,
    };
    store.save_misc(&original).unwrap();
    assert_eq!(store.load_misc().0, original);
    t.write(r#"{ "version": 3, "misc": { "tooltipEnable": false } }"#);
    let m = store.load_misc().0;
    assert!(!m.tooltip_enable);
    assert_eq!(
        m.tooltip_delay_seconds,
        MiscSettings::default().tooltip_delay_seconds
    );
}

#[test]
fn the_plugin_dock_defaults_to_floating_and_persists_beside_the_theme() {
    let t = Temp::new("plugin-dock");
    let store = t.store();
    assert_eq!(store.load_plugin_ui().0.dock, "floating");
    let moss = PluginUiSettings {
        theme: "Moss".into(),
        dock: "right".into(),
        dock_size: "bigger".into(),
        dock_opens: "hover".into(),
        dock_slots: 7,
    };
    store.save_plugin_ui(&moss).unwrap();
    assert_eq!(store.load_plugin_ui().0, moss);
    assert_eq!(store.load_player().0.plugin_ui, moss);
    let brass = PluginUiSettings {
        theme: "Brass".into(),
        ..moss
    };
    store.save_plugin_ui(&brass).unwrap();
    assert_eq!(store.load_plugin_ui().0.dock, "right");
    assert_eq!(store.load_plugin_ui().0.dock_size, "bigger");
    assert_eq!(store.load_plugin_ui().0.dock_opens, "hover");
    assert_eq!(store.load_plugin_ui().0.dock_slots, 7);
}

#[test]
fn a_settings_file_from_before_the_dock_loads_as_floating_and_keeps_its_theme() {
    let t = Temp::new("plugin-dock-old");
    t.write(r#"{"pluginUi":{"theme":"Brass"}}"#);
    let loaded = t.store().load_plugin_ui().0;
    assert_eq!(loaded.theme, "Brass");
    assert_eq!(loaded.dock, "floating");
    assert_eq!(loaded.dock_size, "default");
    assert_eq!(loaded.dock_opens, "click");
    assert_eq!(loaded.dock_slots, 0);
}

#[test]
fn the_plugin_theme_persists_without_replacing_other_settings() {
    // PluginUiThemeTests.ThemePersistsWithoutReplacingOtherSettings.
    let t = Temp::new("plugin-ui");
    t.write(r#"{"other":{"keep":true}}"#);
    let store = t.store();
    assert_eq!(store.load_plugin_ui().0.theme, "Classic");
    store
        .save_plugin_ui(&PluginUiSettings {
            theme: "Brass".into(),
            ..PluginUiSettings::default()
        })
        .unwrap();
    assert_eq!(store.load_plugin_ui().0.theme, "Brass");
    assert_eq!(store.load_player().0.plugin_ui.theme, "Brass");
    let raw = t.raw();
    assert!(
        raw.contains("\"other\"") && raw.contains("\"keep\": true"),
        "{raw}"
    );
}

#[test]
fn save_misc_keeps_the_display_section_and_the_other_way_round() {
    let t = Temp::new("misc-display");
    let store = t.store();
    store
        .save_display(&DisplaySettings {
            resolution: "2560x1440".into(),
            ..DisplaySettings::default()
        })
        .unwrap();
    store
        .save_misc(&MiscSettings {
            tooltip_delay_seconds: 2.0,
            ..MiscSettings::default()
        })
        .unwrap();
    assert_eq!(store.load_display().0.resolution, "2560x1440");
    assert_eq!(store.load_misc().0.tooltip_delay_seconds, 2.0);
    store
        .save_display(&DisplaySettings {
            show_fps: true,
            ..DisplaySettings::default()
        })
        .unwrap();
    assert_eq!(store.load_misc().0.tooltip_delay_seconds, 2.0);
    assert!(store.load_display().0.show_fps);
}

// -- window positions, layouts, placements ------------------------------------

fn layout(
    x: f32,
    y: f32,
    w: f32,
    h: f32,
    visible: bool,
    collapsed: bool,
    maximized: bool,
) -> UiWindowLayout {
    UiWindowLayout {
        x,
        y,
        width: w,
        height: h,
        visible,
        collapsed,
        maximized,
        authored_geometry_revision: 0,
    }
}

#[test]
fn window_positions_round_trip_per_character_and_keep_the_settings() {
    let t = Temp::new("positions");
    let store = t.store();
    store
        .save_display(&DisplaySettings {
            resolution: "2560x1440".into(),
            ..DisplaySettings::default()
        })
        .unwrap();
    let alice = UiWindowPosition { x: 321.5, y: 18.0 };
    let bob = UiWindowPosition { x: 44.0, y: 55.0 };
    store.save_window_position("Alice", "radar", alice).unwrap();
    store.save_window_position("Bob", "radar", bob).unwrap();
    assert_eq!(store.load_window_position("Alice", "radar"), Some(alice));
    assert_eq!(store.load_window_position("Bob", "radar"), Some(bob));
    assert_eq!(store.load_window_position("Alice", "inventory"), None);
    assert_eq!(store.load_display().0.resolution, "2560x1440");
}

#[test]
fn window_layouts_round_trip_per_character_and_resolution() {
    let t = Temp::new("layouts");
    let store = t.store();
    store
        .save_display(&DisplaySettings {
            resolution: "2560x1440".into(),
            ..DisplaySettings::default()
        })
        .unwrap();
    let alice1080 = layout(10.0, 20.0, 500.0, 300.0, true, false, true);
    let alice1440 = layout(30.0, 40.0, 650.0, 420.0, false, true, false);
    let bob1080 = layout(50.0, 60.0, 310.0, 132.0, true, false, false);
    let none = UiWindowLayout::default();
    store
        .save_window_layout("Alice", "1920x1080", "chat", &alice1080)
        .unwrap();
    store
        .save_window_layout("Alice", "2560x1440", "chat", &alice1440)
        .unwrap();
    store
        .save_window_layout("Bob", "1920x1080", "toolbar", &bob1080)
        .unwrap();
    assert_eq!(
        store.load_window_layout("Alice", "1920x1080", "chat", &none),
        Some(alice1080)
    );
    assert_eq!(
        store.load_window_layout("Alice", "2560x1440", "chat", &none),
        Some(alice1440)
    );
    assert_eq!(
        store.load_window_layout("Bob", "1920x1080", "toolbar", &none),
        Some(bob1080)
    );
    assert_eq!(
        store.load_window_layout("Alice", "1920x1080", "inventory", &none),
        None
    );
    assert_eq!(store.load_display().0.resolution, "2560x1440");
}

#[test]
fn window_layouts_migrate_a_legacy_radar_position_over_the_current_defaults() {
    let t = Temp::new("layout-legacy");
    let store = t.store();
    store
        .save_window_position("Alice", "radar", UiWindowPosition { x: 321.5, y: 18.0 })
        .unwrap();
    let current = layout(10.0, 10.0, 160.0, 180.0, true, false, false);
    let migrated = store
        .load_window_layout("Alice", "1920x1080", "radar", &current)
        .unwrap();
    assert_eq!((migrated.x, migrated.y), (321.5, 18.0));
    assert_eq!((migrated.width, migrated.height), (160.0, 180.0));
    assert!(migrated.visible);
}

#[test]
fn window_layouts_fall_back_to_the_nearest_saved_resolution() {
    let t = Temp::new("layout-nearest");
    let store = t.store();
    let hd = layout(100.0, 110.0, 400.0, 250.0, true, false, false);
    let uhd = layout(300.0, 310.0, 600.0, 450.0, true, false, false);
    store
        .save_window_layout("Alice", "1280x720", "chat", &hd)
        .unwrap();
    store
        .save_window_layout("Alice", "3840x2160", "chat", &uhd)
        .unwrap();
    let none = UiWindowLayout::default();
    assert_eq!(
        store.load_window_layout("Alice", "1600x900", "chat", &none),
        Some(hd)
    );
    assert_eq!(
        store.load_window_layout("Alice", "3200x1800", "chat", &none),
        Some(uhd)
    );
}

#[test]
fn named_window_layouts_round_trip_independently() {
    let t = Temp::new("named");
    let store = t.store();
    let hunting = layout(10.0, 20.0, 500.0, 250.0, true, false, true);
    let crafting = layout(30.0, 40.0, 300.0, 400.0, false, true, false);
    let none = UiWindowLayout::default();
    store
        .save_named_window_layout("hunting", "chat", &hunting)
        .unwrap();
    store
        .save_named_window_layout("crafting", "chat", &crafting)
        .unwrap();
    assert_eq!(
        store.load_named_window_layout("hunting", "chat", &none),
        Some(hunting)
    );
    assert_eq!(
        store.load_named_window_layout("crafting", "chat", &none),
        Some(crafting)
    );
    assert_eq!(
        store.load_named_window_layout("missing", "chat", &none),
        None
    );
}

const FALLBACK: UiWindowLayout = UiWindowLayout {
    x: 10.0,
    y: 20.0,
    width: 300.0,
    height: 200.0,
    visible: true,
    collapsed: false,
    maximized: false,
    authored_geometry_revision: 0,
};

#[test]
fn the_latest_placement_wins_over_an_older_exact_resolution_and_keeps_every_field() {
    let t = Temp::new("placement-latest");
    let store = t.store();
    store
        .save_window_layout("alice", "1920x1080", "chat", &FALLBACK)
        .unwrap();
    let latest = UiWindowPlacement {
        layout: UiWindowLayout {
            x: 42.0,
            y: 84.0,
            width: 501.0,
            height: 251.0,
            visible: false,
            collapsed: true,
            maximized: true,
            authored_geometry_revision: 7,
        },
        screen_width: 2560,
        screen_height: 1440,
    };
    store
        .save_window_placement("alice", "chat", &latest)
        .unwrap();
    assert_eq!(
        store.load_window_placement("alice", "1920x1080", "chat", &FALLBACK),
        Some(latest)
    );
    assert_eq!(
        store.load_window_layout("alice", "2560x1440", "chat", &FALLBACK),
        Some(latest.layout)
    );
    assert_eq!(
        store.load_window_layout("alice", "1920x1080", "chat", &FALLBACK),
        Some(FALLBACK)
    );
    assert_eq!(
        store.load_window_placement("bob", "1920x1080", "chat", &FALLBACK),
        None
    );
}

#[test]
fn a_legacy_nearest_layout_keeps_its_source_size_and_the_character_apart() {
    let t = Temp::new("placement-nearest");
    let store = t.store();
    let with_x = |x| UiWindowLayout { x, ..FALLBACK };
    store
        .save_window_layout("alice", "1280x720", "chat", &with_x(90.0))
        .unwrap();
    store
        .save_window_layout("alice", "2560x1440", "chat", &with_x(180.0))
        .unwrap();
    store
        .save_window_layout("bob", "1600x900", "chat", &with_x(999.0))
        .unwrap();
    assert_eq!(
        store.load_window_placement("alice", "1600x900", "chat", &FALLBACK),
        Some(UiWindowPlacement {
            layout: with_x(90.0),
            screen_width: 1280,
            screen_height: 720
        })
    );
}

#[test]
fn a_legacy_position_uses_the_requested_size_and_the_fallback_state() {
    let t = Temp::new("placement-position");
    let store = t.store();
    store
        .save_window_position("alice", "radar", UiWindowPosition { x: 75.0, y: 95.0 })
        .unwrap();
    assert_eq!(
        store.load_window_placement("alice", "1920x1080", "radar", &FALLBACK),
        Some(UiWindowPlacement {
            layout: UiWindowLayout {
                x: 75.0,
                y: 95.0,
                ..FALLBACK
            },
            screen_width: 1920,
            screen_height: 1080
        })
    );
    assert_eq!(
        store.load_window_placement("bob", "1920x1080", "radar", &FALLBACK),
        None
    );
}

#[test]
fn a_malformed_or_invalid_placement_reads_as_none() {
    for text in [
        "{",
        "[]",
        r#"{"windowPlacements":17,"windowLayouts":[]}"#,
        r#"{"windowPlacements":{"alice":{"chat":{"screenWidth":0,"screenHeight":1080,"layout":{}}}}}"#,
    ] {
        let t = Temp::new("placement-bad");
        t.write(text);
        assert_eq!(
            t.store()
                .load_window_placement("alice", "1920x1080", "chat", &FALLBACK),
            None,
            "{text}"
        );
    }
}

#[test]
fn an_invalid_latest_entry_falls_back_to_legacy_and_bad_fields_use_defaults() {
    let t = Temp::new("placement-fields");
    t.write(
        r#"{"windowPlacements":{"alice":{"chat":{"screenWidth":"bad","screenHeight":1080,"layout":{}}}},
             "windowLayouts":{"alice":{"1280x720":{"chat":{"x":"bad","y":42,"visible":"bad"}}}}}"#,
    );
    assert_eq!(
        t.store()
            .load_window_placement("alice", "1920x1080", "chat", &FALLBACK),
        Some(UiWindowPlacement {
            layout: UiWindowLayout {
                y: 42.0,
                ..FALLBACK
            },
            screen_width: 1280,
            screen_height: 720
        })
    );
}

#[test]
fn an_invalid_requested_size_reads_none_and_an_invalid_save_writes_nothing() {
    let t = Temp::new("placement-invalid");
    let store = t.store();
    assert_eq!(
        store.load_window_placement("alice", "bad", "chat", &FALLBACK),
        None
    );
    assert!(
        store
            .save_window_placement(
                "alice",
                "chat",
                &UiWindowPlacement {
                    layout: FALLBACK,
                    screen_width: 0,
                    screen_height: 1080
                }
            )
            .is_err()
    );
    assert!(!t.0.exists());
}

fn placed(x: f32, width: i32, height: i32) -> UiWindowPlacement {
    UiWindowPlacement {
        layout: UiWindowLayout { x, ..FALLBACK },
        screen_width: width,
        screen_height: height,
    }
}

#[test]
fn a_batch_save_writes_the_same_file_as_one_save_per_window() {
    let placements = [
        ("chat", placed(1.0, 1920, 1080)),
        ("radar", placed(2.0, 1280, 720)),
        ("combat", placed(3.0, 1920, 1080)),
    ];
    let one = Temp::new("placement-batch-one");
    one.write(r#"{"display":{"vsync":true},"version":4}"#);
    for (name, placement) in &placements {
        one.store()
            .save_window_placement("alice", name, placement)
            .unwrap();
    }
    let batch = Temp::new("placement-batch-all");
    batch.write(r#"{"display":{"vsync":true},"version":4}"#);
    batch
        .store()
        .save_window_placements("alice", &placements)
        .unwrap();
    assert_eq!(one.raw(), batch.raw());
}

#[test]
fn a_batch_load_matches_one_load_per_window() {
    let t = Temp::new("placement-batch-load");
    let store = t.store();
    store
        .save_window_placement("alice", "chat", &placed(5.0, 2560, 1440))
        .unwrap();
    store
        .save_window_layout(
            "alice",
            "1280x720",
            "radar",
            &UiWindowLayout {
                x: 90.0,
                ..FALLBACK
            },
        )
        .unwrap();
    store
        .save_window_position("alice", "combat", UiWindowPosition { x: 75.0, y: 95.0 })
        .unwrap();
    let windows = [
        ("chat", FALLBACK),
        ("radar", FALLBACK),
        ("combat", FALLBACK),
        ("vitals", FALLBACK),
    ];
    let loaded = store.load_window_placements("alice", "1920x1080", &windows);
    assert_eq!(loaded.len(), 3);
    for (name, fallback) in &windows {
        assert_eq!(
            loaded.get(*name).copied(),
            store.load_window_placement("alice", "1920x1080", name, fallback),
            "{name}"
        );
    }
}

#[test]
fn an_invalid_batch_save_writes_nothing() {
    let t = Temp::new("placement-batch-invalid");
    assert!(
        t.store()
            .save_window_placements(
                "alice",
                &[
                    ("chat", placed(1.0, 1920, 1080)),
                    ("radar", placed(2.0, 1920, 0))
                ]
            )
            .is_err()
    );
    assert!(!t.0.exists());
}

// -- chat opacity link --------------------------------------------------------

#[test]
fn the_default_opacity_drags_the_active_one_up_only() {
    assert_eq!(chat_opacity::set_default(1.0, 0.3), (0.3, 1.0));
    assert_eq!(chat_opacity::set_default(0.4, 0.9), (0.9, 0.9));
    assert_eq!(chat_opacity::set_default(1.0, -1.0).0, 0.0);
    assert_eq!(chat_opacity::set_default(1.0, 2.0).0, 1.0);
}

#[test]
fn the_active_opacity_drags_the_default_one_down_only() {
    assert_eq!(chat_opacity::set_active(0.2, 0.9), (0.2, 0.9));
    assert_eq!(chat_opacity::set_active(0.7, 0.3), (0.3, 0.3));
    assert_eq!(chat_opacity::set_active(0.0, -1.0).1, 0.0);
    assert_eq!(chat_opacity::set_active(0.0, 2.0).1, 1.0);
}

#[test]
fn moving_both_never_leaves_active_below_default() {
    let (mut def, mut active): (f32, f32);
    active = 1.0;
    (def, active) = chat_opacity::set_default(active, 0.9);
    assert!(active >= def);
    (def, active) = chat_opacity::set_active(def, 0.1);
    assert!(active >= def);
    (def, active) = chat_opacity::set_default(active, 0.6);
    assert!(active >= def);
    assert_eq!((def, active), (0.6, 0.6));
}

// -- the mouse-turning macro --------------------------------------------------

#[test]
fn from_the_ordinary_defaults_every_value_changes() {
    let r = mouse_turning_macro(&CameraTurningSettings::default(), false);
    assert_eq!(r.updated, CameraTurningSettings::mouse_turning_target());
    assert!(r.use_mouse_turning_changed);
    assert_eq!(r.chat_lines.len(), 6);
}

#[test]
fn at_the_targets_nothing_changes() {
    let r = mouse_turning_macro(&CameraTurningSettings::mouse_turning_target(), true);
    assert_eq!(r.updated, CameraTurningSettings::mouse_turning_target());
    assert!(!r.use_mouse_turning_changed);
    assert!(r.chat_lines.is_empty());
}

#[test]
fn only_stiffness_differs() {
    let current = CameraTurningSettings {
        stiffness: 0.45,
        ..CameraTurningSettings::mouse_turning_target()
    };
    let r = mouse_turning_macro(&current, true);
    assert_eq!(r.updated.stiffness, 0.95);
    assert_eq!(r.updated.adjustment_speed, 50.0);
    assert!(!r.use_mouse_turning_changed);
    assert_eq!(
        r.chat_lines,
        vec![mouse_turning_text::camera_stiffness_changed(0.45, 0.95)]
    );
    assert_eq!(
        r.chat_lines[0],
        "Camera Stiffness was changed from 0.450000 to the mouse turning default of 0.950000."
    );
}

#[test]
fn only_align_to_slope_differs_and_the_line_is_the_literal() {
    let current = CameraTurningSettings {
        align_to_slope: true,
        ..CameraTurningSettings::mouse_turning_target()
    };
    let r = mouse_turning_macro(&current, true);
    assert!(!r.updated.align_to_slope);
    assert_eq!(
        r.chat_lines,
        vec![mouse_turning_text::ALIGN_TO_SLOPE_CHANGED]
    );
}

#[test]
fn only_the_character_option_differs() {
    let r = mouse_turning_macro(&CameraTurningSettings::mouse_turning_target(), false);
    assert_eq!(r.updated, CameraTurningSettings::mouse_turning_target());
    assert!(r.use_mouse_turning_changed);
    assert_eq!(
        r.chat_lines,
        vec![mouse_turning_text::TURN_TO_FACE_CAMERA_CHANGED]
    );
}

#[test]
fn the_lines_come_in_the_retail_order() {
    let d = CameraTurningSettings::default();
    let t = CameraTurningSettings::mouse_turning_target();
    let r = mouse_turning_macro(&d, false);
    assert_eq!(
        r.chat_lines,
        vec![
            mouse_turning_text::camera_stiffness_changed(d.stiffness, t.stiffness),
            mouse_turning_text::camera_adjustment_changed(d.adjustment_speed, t.adjustment_speed),
            mouse_turning_text::mouse_sensitivity_changed(
                d.mouse_look_sensitivity,
                t.mouse_look_sensitivity
            ),
            mouse_turning_text::ALIGN_TO_SLOPE_CHANGED.to_owned(),
            mouse_turning_text::INVERT_MOUSE_LOOK_AXES_CHANGED.to_owned(),
            mouse_turning_text::TURN_TO_FACE_CAMERA_CHANGED.to_owned(),
        ]
    );
}
