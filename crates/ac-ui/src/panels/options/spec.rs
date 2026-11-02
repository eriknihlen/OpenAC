//! The rows of the Character, Chat and Configuration pages, in the order
//! the game lists them, with the string keys they caption themselves from,
//! their defaults and what each edits.

use ac_runtime::character::option_id as o;

use super::keys::{AudioField, CameraField, ChatField, DisplayField, MixerField, OptionKey};

/// One character option row: the option, the name its caption key is
/// built from (`ID_PlayerOption_<name>`), and whether OpenAC only stores it
/// (grey caption).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct CharacterRow {
    /// The option id.
    pub id: u32,
    /// The retail name.
    pub retail_name: &'static str,
    /// Stored only (no consumer).
    pub store_only: bool,
}

/// One header group of the Character page.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct CharacterGroup {
    /// The header's string key.
    pub header_key: &'static str,
    /// The rows, in order.
    pub rows: &'static [CharacterRow],
}

const fn live(id: u32, retail_name: &'static str) -> CharacterRow {
    CharacterRow {
        id,
        retail_name,
        store_only: false,
    }
}

const fn stored(id: u32, retail_name: &'static str) -> CharacterRow {
    CharacterRow {
        id,
        retail_name,
        store_only: true,
    }
}

/// The Character page: six groups, 50 rows.
pub const CHARACTER_GROUPS: [CharacterGroup; 6] = [
    CharacterGroup {
        header_key: "ID_CharacterOption_UIBehavior_Section",
        rows: &[
            live(o::VIEW_COMBAT_TARGET, "ViewCombatTarget"),
            live(o::SALVAGE_MULTIPLE, "SalvageMultiple"),
            live(o::MAIN_PACK_PREFERRED, "MainPackPreferred"),
        ],
    },
    CharacterGroup {
        header_key: "ID_CharacterOption_UIDisplay_Section",
        rows: &[
            live(o::VIVID_TARGETING_INDICATOR, "VividTargetingIndicator"),
            live(o::SHOW_TOOLTIPS, "ShowTooltips"),
            live(o::COORDINATES_ON_RADAR, "CoordinatesOnRadar"),
            live(o::SIDE_BY_SIDE_VITALS, "SideBySideVitals"),
            live(o::SPELL_DURATION, "SpellDuration"),
            live(o::DISABLE_MOST_WEATHER_EFFECTS, "DisableMostWeatherEffects"),
            live(o::DISABLE_DISTANCE_FOG, "DisableDistanceFog"),
            live(o::PERSISTENT_AT_DAY, "PersistentAtDay"),
            stored(
                o::DISABLE_HOUSE_RESTRICTION_EFFECTS,
                "DisableHouseRestrictionEffects",
            ),
            live(o::USE_CRAFT_SUCCESS_DIALOG, "UseCraftSuccessDialog"),
            live(o::CONFIRM_VOLATILE_RARE_USE, "ConfirmVolatileRareUse"),
            live(o::DISPLAY_TIME_STAMPS, "DisplayTimeStamps"),
            live(o::FILTER_LANGUAGE, "FilterLanguage"),
            live(o::SHOW_HELM, "ShowHelm"),
            live(o::SHOW_CLOAK, "ShowCloak"),
        ],
    },
    CharacterGroup {
        header_key: "ID_CharacterOption_Grouping_Section",
        rows: &[
            live(o::IGNORE_ALLEGIANCE_REQUESTS, "IgnoreAllegianceRequests"),
            live(o::IGNORE_FELLOWSHIP_REQUESTS, "IgnoreFellowshipRequests"),
            stored(
                o::DISPLAY_ALLEGIANCE_LOGON_NOTIFICATIONS,
                "DisplayAllegianceLogonNotifications",
            ),
            live(o::FELLOWSHIP_SHARE_XP, "FellowshipShareXP"),
            live(o::FELLOWSHIP_SHARE_LOOT, "FellowshipShareLoot"),
            live(
                o::FELLOWSHIP_AUTO_ACCEPT_REQUESTS,
                "FellowshipAutoAcceptRequests",
            ),
        ],
    },
    CharacterGroup {
        header_key: "ID_CharacterOption_OtherPlayers_Section",
        rows: &[
            live(o::ACCEPT_LOOT_PERMITS, "AcceptLootPermits"),
            stored(o::USE_DECEPTION, "UseDeception"),
            live(o::ALLOW_GIVE, "AllowGive"),
            live(o::IGNORE_TRADE_REQUESTS, "IgnoreTradeRequests"),
            live(
                o::DRAG_ITEM_ON_PLAYER_OPENS_SECURE_TRADE,
                "DragItemOnPlayerOpensSecureTrade",
            ),
            stored(o::DISPLAY_DATE_OF_BIRTH, "DisplayDateOfBirth"),
            stored(o::DISPLAY_AGE, "DisplayAge"),
            // The server hides the chess rank from appraisals when off.
            live(o::DISPLAY_CHESS_RANK, "DisplayChessRank"),
            stored(o::DISPLAY_FISHING_SKILL, "DisplayFishingSkill"),
            stored(o::DISPLAY_NUMBER_DEATHS, "DisplayNumberDeaths"),
            stored(
                o::DISPLAY_NUMBER_CHARACTER_TITLES,
                "DisplayNumberCharacterTitles",
            ),
        ],
    },
    CharacterGroup {
        header_key: "ID_CharacterOption_CharacterBehavior_Section",
        rows: &[
            live(o::TOGGLE_RUN, "ToggleRun"),
            stored(o::ADVANCED_COMBAT_UI, "AdvancedCombatUI"),
            live(o::AUTO_TARGET, "AutoTarget"),
            live(o::AUTO_REPEAT_ATTACK, "AutoRepeatAttack"),
            live(o::USE_CHARGE_ATTACK, "UseChargeAttack"),
            live(o::LEAD_MISSILE_TARGETS, "LeadMissileTargets"),
            live(o::USE_FAST_MISSILES, "UseFastMissiles"),
        ],
    },
    CharacterGroup {
        header_key: "ID_CharacterOption_Chat_Section",
        rows: &[
            live(o::STAY_IN_CHAT_MODE, "StayInChatMode"),
            live(o::LISTEN_TO_ALLEGIANCE_CHAT, "HearAllegianceChat"),
            live(o::LISTEN_TO_GENERAL_CHAT, "HearGeneralChat"),
            live(o::LISTEN_TO_TRADE_CHAT, "HearTradeChat"),
            live(o::LISTEN_TO_LFG_CHAT, "HearLFGChat"),
            live(o::LISTEN_TO_ROLEPLAY_CHAT, "HearRoleplayChat"),
            live(o::LISTEN_TO_SOCIETY_CHAT, "HearSocietyChat"),
            stored(o::HEAR_PK_DEATH_MESSAGES, "HearPKDeaths"),
        ],
    },
];

/// Rows on the Character page.
#[must_use]
pub fn character_row_count() -> usize {
    CHARACTER_GROUPS.iter().map(|g| g.rows.len()).sum()
}

/// The list box's row templates (Configuration page numbering; the
/// Character page uses the first three, the Chat page 0, 1, 3, 6 and 8).
pub mod template {
    /// A section header.
    pub const HEADER: usize = 0;
    /// The separator under a section.
    pub const SEPARATOR: usize = 1;
    /// A checkbox.
    pub const TOGGLE: usize = 2;
    /// A slider without range captions.
    pub const SIMPLE_SLIDER: usize = 3;
    /// A drop-down menu.
    pub const MENU: usize = 4;
    /// A checkbox with a slider.
    pub const TRIO: usize = 5;
    /// A slider with range captions.
    pub const RANGED_SLIDER: usize = 6;
    /// The chat filter checkbox block.
    pub const BITFIELD: usize = 8;
}

/// A menu choice of an OpenAC-only menu: its stored id, its caption,
/// whether it can be picked, and its tooltip.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ExplicitChoice {
    /// Stored value.
    pub id: &'static str,
    /// Caption.
    pub label: &'static str,
    /// Can be picked.
    pub enabled: bool,
    /// Tooltip.
    pub tooltip: &'static str,
}

/// One Configuration page row.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum ConfigRow {
    /// A section header captioned from the string table.
    Header(&'static str),
    /// The separator under a section.
    Separator,
    /// A checkbox.
    Toggle {
        /// Caption key.
        key: &'static str,
        /// Default.
        default: bool,
        /// What it edits.
        target: OptionKey,
        /// Grey caption.
        store_only: bool,
    },
    /// A slider in the setting's own unit.
    Slider {
        /// Template (simple or ranged).
        template: usize,
        /// Caption key.
        key: &'static str,
        /// Low end.
        min: f32,
        /// High end.
        max: f32,
        /// Default.
        default: f32,
        /// What it edits.
        target: OptionKey,
        /// Grey caption.
        store_only: bool,
        /// Low and high end caption keys.
        range: Option<(&'static str, &'static str)>,
    },
    /// A checkbox with a volume slider.
    Trio {
        /// The checkbox's caption key.
        toggle_key: &'static str,
        /// The slider's tooltip key.
        slider_tooltip_key: &'static str,
        /// Checkbox default.
        toggle_default: bool,
        /// Slider default.
        slider_default: f32,
        /// The switch it edits.
        toggle_target: OptionKey,
        /// The volume it edits.
        slider_target: OptionKey,
    },
    /// A menu of numbered choices captioned from the string table.
    Menu {
        /// Caption key.
        key: &'static str,
        /// The choices' caption keys.
        choices: &'static [&'static str],
        /// The value each choice stores (`None`: its index).
        payloads: Option<&'static [i32]>,
        /// Default.
        default: i32,
        /// What it edits.
        target: OptionKey,
        /// Grey caption.
        store_only: bool,
    },
    /// The resolution menu (its choices come from the monitor).
    Resolution {
        /// Caption key.
        key: &'static str,
    },
    /// An OpenAC-only checkbox with its own caption.
    ExplicitToggle {
        /// Caption.
        label: &'static str,
        /// Default.
        default: bool,
        /// What it edits.
        target: OptionKey,
        /// Tooltip.
        tooltip: &'static str,
        /// Dimmed while the retail mixer is on.
        dimmed_by_retail_mixer: bool,
    },
    /// An OpenAC-only number slider.
    ExplicitSlider {
        /// Caption.
        label: &'static str,
        /// Low end.
        min: f64,
        /// High end.
        max: f64,
        /// Step.
        step: f64,
        /// Whole numbers only.
        integer: bool,
        /// Default.
        default: f64,
        /// What it edits.
        target: OptionKey,
        /// Tooltip.
        tooltip: &'static str,
        /// Dimmed while the retail mixer is on.
        dimmed_by_retail_mixer: bool,
        /// Low end caption (`None`: the number).
        range_low: Option<&'static str>,
        /// High end caption (`None`: the number).
        range_high: Option<&'static str>,
        /// When set, the caption shows the value after the label, with
        /// this after the number ("UI Scale (150%)").
        value_suffix: Option<&'static str>,
    },
    /// An OpenAC-only menu of named choices.
    ExplicitMenu {
        /// Caption.
        label: &'static str,
        /// Choices.
        choices: &'static [ExplicitChoice],
        /// Default.
        default: &'static str,
        /// What it edits.
        target: OptionKey,
    },
    /// An OpenAC-only section header with its own caption.
    ExplicitHeader(&'static str),
}

const TEXTURE_DETAIL_CHOICES: &[&str] = &[
    "ID_Graphics_Value_VeryHigh",
    "ID_Graphics_Value_High",
    "ID_Graphics_Value_Medium",
    "ID_Graphics_Value_Low",
    "ID_Graphics_Value_VeryLow",
];

const TEXTURE_FILTERING_CHOICES: &[&str] = &[
    "ID_Graphics_TextureFiltering_Bilinear",
    "ID_Graphics_TextureFiltering_Trilinear",
    "ID_Graphics_TextureFiltering_Sharp",
    "ID_Graphics_TextureFiltering_Anisotropic",
];

const LANDSCAPE_DRAW_DISTANCE_CHOICES: &[&str] = &[
    "ID_Graphics_Value_VeryLow",
    "ID_Graphics_Value_Low",
    "ID_Graphics_Value_Medium",
    "ID_Graphics_Value_High",
    "ID_Graphics_Value_VeryHigh",
    "ID_Graphics_Value_Extreme",
];

/// The landscape draw distance each choice stores (landblocks).
pub const LANDSCAPE_DRAW_DISTANCE_VALUES: &[i32] = &[3, 5, 8, 11, 15, 25];

const CHAT_FONT_FACE_CHOICES: &[&str] = &[
    "ID_UI_Value_Arial",
    "ID_UI_Value_CourierNew",
    "ID_UI_Value_PalatinoLinotype",
    "ID_UI_Value_Tahoma",
    "ID_UI_Value_TimesNewRoman",
];

const CHAT_FONT_SIZE_CHOICES: &[&str] = &[
    "ID_UI_Value_Tiny",
    "ID_UI_Value_Small",
    "ID_UI_Value_Medium",
    "ID_UI_Value_Large",
    "ID_UI_Value_XLarge",
];

/// The graphics profiles (OpenAC's quality presets).
pub const GRAPHICS_PROFILE_CHOICES: &[ExplicitChoice] = &[
    ExplicitChoice {
        id: "Low",
        label: "Low",
        enabled: true,
        tooltip: "Full detail within 2 landblocks, no anti-aliasing, 4x texture filtering. Landscape range is the draw distance below.",
    },
    ExplicitChoice {
        id: "Medium",
        label: "Medium",
        enabled: true,
        tooltip: "Full detail within 3 landblocks, 2x anti-aliasing, 8x texture filtering. Landscape range is the draw distance below.",
    },
    ExplicitChoice {
        id: "High",
        label: "High",
        enabled: true,
        tooltip: "Full detail within 4 landblocks, 4x anti-aliasing, 16x texture filtering. Landscape range is the draw distance below.",
    },
    ExplicitChoice {
        id: "Ultra",
        label: "Ultra",
        enabled: true,
        tooltip: "Full detail within 5 landblocks, 4x anti-aliasing, 16x texture filtering. Landscape range is the draw distance below.",
    },
];

const fn toggle(
    key: &'static str,
    default: bool,
    target: OptionKey,
    store_only: bool,
) -> ConfigRow {
    ConfigRow::Toggle {
        key,
        default,
        target,
        store_only,
    }
}

#[allow(clippy::too_many_arguments)]
const fn slider(
    template: usize,
    key: &'static str,
    min: f32,
    max: f32,
    default: f32,
    target: OptionKey,
    store_only: bool,
    range: Option<(&'static str, &'static str)>,
) -> ConfigRow {
    ConfigRow::Slider {
        template,
        key,
        min,
        max,
        default,
        target,
        store_only,
        range,
    }
}

const fn menu(
    key: &'static str,
    choices: &'static [&'static str],
    payloads: Option<&'static [i32]>,
    default: i32,
    target: OptionKey,
    store_only: bool,
) -> ConfigRow {
    ConfigRow::Menu {
        key,
        choices,
        payloads,
        default,
        target,
        store_only,
    }
}

const fn trio(
    toggle_key: &'static str,
    slider_tooltip_key: &'static str,
    toggle_target: OptionKey,
    slider_target: OptionKey,
) -> ConfigRow {
    ConfigRow::Trio {
        toggle_key,
        slider_tooltip_key,
        toggle_default: true,
        slider_default: 1.0,
        toggle_target,
        slider_target,
    }
}

/// The interface-scale row's caption.
pub const UI_SCALE_LABEL: &str = "UI Scale";

const fn display(field: DisplayField) -> OptionKey {
    OptionKey::Display(field)
}

const fn audio(field: AudioField) -> OptionKey {
    OptionKey::Audio(field)
}

const fn camera(field: CameraField) -> OptionKey {
    OptionKey::Camera(field)
}

const fn mixer(field: MixerField) -> OptionKey {
    OptionKey::Mixer(field)
}

/// The caption of the modern mouse turning option.
pub const MODERN_MOUSE_TURNING_LABEL: &str = "Modern Mouse Turning";
/// The caption of its both-buttons-run option.
pub const BOTH_MOUSE_BUTTONS_RUN_FORWARD_LABEL: &str = "Both Mouse Buttons Run Forward";

/// A row the settings make unavailable now: Use Mouse Turning while modern
/// mouse turning is on (modern turning replaces it), and Both Mouse
/// Buttons Run Forward while it is off (it needs it). Shown grey; a click
/// changes nothing.
#[must_use]
pub fn unavailable(key: OptionKey, settings: &ac_host::settings::PlayerSettings) -> bool {
    match key {
        OptionKey::Camera(CameraField::UseMouseTurning) => settings.camera.modern_mouse_turning,
        OptionKey::Camera(CameraField::BothMouseButtonsRunForward) => {
            !settings.camera.modern_mouse_turning
        }
        _ => false,
    }
}

const fn explicit_toggle(
    label: &'static str,
    default: bool,
    target: OptionKey,
    tooltip: &'static str,
) -> ConfigRow {
    ConfigRow::ExplicitToggle {
        label,
        default,
        target,
        tooltip,
        dimmed_by_retail_mixer: false,
    }
}

/// The Configuration page in order: sound (with OpenAC's four mixer rows),
/// camera, graphics (with OpenAC's five), texture, input and interface,
/// each closed by a separator. `defaults` is the display defaults.
#[must_use]
pub fn config_rows() -> Vec<ConfigRow> {
    use ConfigRow::{Header, Separator};
    let def = ac_host::settings::DisplaySettings::default();
    let mix = ac_host::settings::AudioMixerSettings::default();
    vec![
        Header("ID_Sound_SoundSection"),
        menu(
            "ID_Sound_SoundFeatures",
            &["ID_Sound_Stereo", "ID_Sound_Mono"],
            None,
            0,
            audio(AudioField::SoundFeatures),
            true,
        ),
        trio(
            "ID_Sound_DisableSound",
            "ID_Sound_EffectVolume",
            audio(AudioField::SfxEnabled),
            audio(AudioField::Sfx),
        ),
        trio(
            "ID_Sound_DisableAmbientSound",
            "ID_Sound_AmbientVolume",
            audio(AudioField::AmbientEnabled),
            audio(AudioField::Ambient),
        ),
        trio(
            "ID_Sound_DisableInterfaceSound",
            "ID_Sound_InterfaceVolume",
            audio(AudioField::InterfaceEnabled),
            audio(AudioField::InterfaceVolume),
        ),
        toggle(
            "ID_Sound_NoFocusNoSound",
            true,
            audio(AudioField::PlaySoundOnlyWhenActive),
            false,
        ),
        ConfigRow::ExplicitToggle {
            label: "Retail Mixer",
            default: mix.retail_mixer,
            target: mixer(MixerField::RetailMixer),
            tooltip: "Mix exactly like the original client: 16 voices, no priority, no per-sound cap. Overrides the three settings below.",
            dimmed_by_retail_mixer: false,
        },
        ConfigRow::ExplicitSlider {
            label: "Voices",
            min: f64::from(ac_host::settings::MINIMUM_VOICE_COUNT),
            max: f64::from(ac_host::settings::MAXIMUM_VOICE_COUNT),
            step: 1.0,
            integer: true,
            default: f64::from(mix.voice_count),
            target: mixer(MixerField::VoiceCount),
            tooltip: "How many sounds can play at once. The original client used 16.",
            dimmed_by_retail_mixer: true,
            range_low: None,
            range_high: None,
            value_suffix: None,
        },
        ConfigRow::ExplicitToggle {
            label: "Priority",
            default: mix.use_authored_priority,
            target: mixer(MixerField::UseAuthoredPriority),
            tooltip: "An important sound (a hit, a spell, an interface cue) may take the voice of a quieter one such as a footstep when all voices are busy.",
            dimmed_by_retail_mixer: true,
        },
        ConfigRow::ExplicitSlider {
            label: "Voices Per Sound",
            min: f64::from(ac_host::settings::NO_PER_WAVE_CAP),
            max: f64::from(ac_host::settings::MAXIMUM_MAX_VOICES_PER_WAVE),
            step: 1.0,
            integer: true,
            default: f64::from(mix.max_voices_per_wave),
            target: mixer(MixerField::MaxVoicesPerWave),
            tooltip: "How many copies of the same sound may play at once; a further copy replaces the oldest.",
            dimmed_by_retail_mixer: true,
            range_low: Some("Off"),
            range_high: Some("8"),
            value_suffix: None,
        },
        Separator,
        Header("ID_Camera_CameraSection"),
        slider(
            template::RANGED_SLIDER,
            "ID_Camera_Stiffness",
            0.285_714_3,
            1.0,
            0.45,
            camera(CameraField::Stiffness),
            false,
            Some(("ID_Graphics_Value_Soft", "ID_Graphics_Value_Hard")),
        ),
        slider(
            template::RANGED_SLIDER,
            "ID_Camera_AdjustmentSpeed",
            5.0,
            80.0,
            40.0,
            camera(CameraField::AdjustmentSpeed),
            false,
            Some(("ID_Graphics_Value_Slow", "ID_Graphics_Value_Fast")),
        ),
        slider(
            template::RANGED_SLIDER,
            "ID_Graphics_FieldOfView",
            10.0,
            160.0,
            90.0,
            display(DisplayField::FieldOfView),
            false,
            Some(("ID_Graphics_Value_Narrow", "ID_Graphics_Value_Wide")),
        ),
        toggle(
            "ID_Camera_AlignToSlope",
            true,
            camera(CameraField::AlignToSlope),
            false,
        ),
        Separator,
        Header("ID_Graphics_GraphicsSection"),
        ConfigRow::Resolution {
            key: "ID_Rendering_DisplayResolution",
        },
        toggle(
            "ID_Rendering_FullScreen",
            true,
            display(DisplayField::Fullscreen),
            false,
        ),
        toggle(
            "ID_Rendering_SyncToDisplayRefresh",
            false,
            display(DisplayField::VSync),
            false,
        ),
        slider(
            template::RANGED_SLIDER,
            "ID_Graphics_ScreenBrightness",
            -1.0,
            1.0,
            0.0,
            display(DisplayField::ScreenBrightness),
            true,
            Some(("ID_Graphics_Value_Dark", "ID_Graphics_Value_Bright")),
        ),
        toggle(
            "ID_Graphics_AdaptiveDegrade",
            false,
            display(DisplayField::AutomaticDegrades),
            false,
        ),
        slider(
            template::RANGED_SLIDER,
            "ID_Graphics_AdaptiveDegradeBias",
            -1.0,
            1.0,
            0.0,
            display(DisplayField::GraphicsPerformance),
            false,
            Some(("ID_Graphics_Value_Speed", "ID_Graphics_Value_Detail")),
        ),
        slider(
            template::RANGED_SLIDER,
            "ID_Graphics_DegradeDistance",
            0.0,
            100.0,
            50.0,
            display(DisplayField::DegradeDistance),
            false,
            Some(("ID_Graphics_Value_Close", "ID_Graphics_Value_Far")),
        ),
        explicit_toggle(
            "Keep Distant Buildings",
            def.keep_distant_buildings,
            display(DisplayField::KeepDistantBuildings),
            "A building whose detail levels end in \"draw nothing\" falls back to its simplest mesh instead of disappearing. We draw objects much further out than those levels were made for, so without this a distant building can vanish while the fences and stairs around it stay.",
        ),
        ConfigRow::ExplicitMenu {
            label: "Graphics Profile",
            choices: GRAPHICS_PROFILE_CHOICES,
            default: "High",
            target: display(DisplayField::Quality),
        },
        explicit_toggle(
            "Potato Mode",
            def.potato_mode,
            display(DisplayField::PotatoMode),
            "Everything at its cheapest, for running many clients on one machine: full detail only in the nearest landblocks, landscape draw distance 3, no anti-aliasing, the lowest texture detail, plain texture filtering, no building detail textures, the plain render pack and retail particle range. Your other settings are kept and come back when this is off. It applies at once: the world's textures are made again at the new detail and replace the old ones over the next moments.",
        ),
        ConfigRow::ExplicitSlider {
            label: UI_SCALE_LABEL,
            min: f64::from(ac_host::settings::UI_SCALE_MIN_PERCENT),
            max: f64::from(ac_host::settings::UI_SCALE_MAX_PERCENT),
            step: f64::from(ac_host::settings::UI_SCALE_STEP_PERCENT),
            integer: true,
            default: f64::from(def.ui_scale_percent),
            target: display(DisplayField::UiScale),
            tooltip: "How large the in-game windows, text and controls are drawn, all together; the world is not scaled. It applies when you let go of the slider. The login and character screens always fill the window.",
            dimmed_by_retail_mixer: false,
            range_low: Some("50%"),
            range_high: Some("300%"),
            value_suffix: Some("%"),
        },
        explicit_toggle(
            "UI Only",
            def.ui_only,
            display(DisplayField::UiOnly),
            "Stop drawing the world and keep only the landblocks around you loaded; the panels, chat, radar and plugins keep working. For a client nobody is watching. Off again brings the world back as it streams in.",
        ),
        explicit_toggle(
            "UI Only in Background",
            def.ui_only_when_unfocused,
            display(DisplayField::UiOnlyWhenUnfocused),
            "Switch to UI Only whenever this window is not the active one, and back when it is: the client you are looking at draws the world, the others do not.",
        ),
        Separator,
        Header("ID_Graphics_TextureSection"),
        menu(
            "ID_Graphics_LandscapeTextureDetail",
            TEXTURE_DETAIL_CHOICES,
            None,
            def.landscape_texture_detail,
            display(DisplayField::LandscapeTextureDetail),
            false,
        ),
        menu(
            "ID_Graphics_EnvironmentTextureDetail",
            TEXTURE_DETAIL_CHOICES,
            None,
            def.environment_texture_detail,
            display(DisplayField::EnvironmentTextureDetail),
            false,
        ),
        menu(
            "ID_Graphics_TextureFiltering",
            TEXTURE_FILTERING_CHOICES,
            None,
            1,
            display(DisplayField::TextureFiltering),
            true,
        ),
        menu(
            "ID_Graphics_LandscapeDrawDistance",
            LANDSCAPE_DRAW_DISTANCE_CHOICES,
            Some(LANDSCAPE_DRAW_DISTANCE_VALUES),
            8,
            display(DisplayField::LandscapeDrawDistance),
            false,
        ),
        toggle(
            "ID_Graphics_BuildingDetailTextures",
            true,
            display(DisplayField::BuildingDetailTextures),
            false,
        ),
        // The game's strings keep this row's caption and help, though the
        // game itself never offered it.
        toggle(
            "ID_Graphics_LandscapeDetailTextures",
            def.landscape_detail_textures,
            display(DisplayField::LandscapeDetailTextures),
            false,
        ),
        toggle(
            "ID_Graphics_MultiPassAlpha",
            false,
            display(DisplayField::MultiPassAlpha),
            true,
        ),
        Separator,
        Header("ID_Input_InputSection"),
        explicit_toggle(
            MODERN_MOUSE_TURNING_LABEL,
            false,
            camera(CameraField::ModernMouseTurning),
            "Hold the right mouse button over the world and drag: the view turns around your character, and your character turns to face it at its normal turn speed. The turn keys still turn your character. Replaces Use Mouse Turning while on.",
        ),
        slider(
            template::SIMPLE_SLIDER,
            "ID_Input_MouseLookSensitivity",
            // The game stores 0.00999999978, the f32 nearest 0.01.
            0.01,
            1.0,
            0.55,
            camera(CameraField::MouseLookSensitivity),
            false,
            None,
        ),
        toggle(
            "ID_Input_InvertMouseLookYAxis",
            false,
            camera(CameraField::InvertMouseLookYAxis),
            false,
        ),
        toggle(
            "ID_Input_UseMouseTurning",
            false,
            camera(CameraField::UseMouseTurning),
            true,
        ),
        explicit_toggle(
            BOTH_MOUSE_BUTTONS_RUN_FORWARD_LABEL,
            false,
            camera(CameraField::BothMouseButtonsRunForward),
            "With Modern Mouse Turning on: hold both mouse buttons, pressed over the world, to run forward. Letting go of either stops; the back-up key cancels it.",
        ),
        Separator,
        Header("ID_UI_UISection"),
        menu(
            "ID_UI_ChatFontFace",
            CHAT_FONT_FACE_CHOICES,
            None,
            2,
            OptionKey::Chat(ChatField::FontFace),
            false,
        ),
        menu(
            "ID_UI_ChatFontSize",
            CHAT_FONT_SIZE_CHOICES,
            None,
            1,
            OptionKey::Chat(ChatField::FontSize),
            false,
        ),
        Separator,
    ]
    .into_iter()
    .chain(atmospheric_rows())
    .collect()
}

/// The atmospheric rendering's quality: off (the game's own picture) or a
/// preset, with OpenAC's per-preset cost estimate as the tooltip.
pub const ATMOSPHERIC_QUALITY_CHOICES: &[ExplicitChoice] = &[
    ExplicitChoice {
        id: "off",
        label: "Off",
        enabled: true,
        tooltip: "The game's own picture, unchanged.",
    },
    ExplicitChoice {
        id: "low",
        label: "Low",
        enabled: true,
        tooltip: "Filmic HDR atmosphere, moving sun-and-moon shadows from terrain, trees, buildings, players, and monsters, plus optional volumetric shafts.\nEstimated ceiling — GPU p50/p99 ≤ 2/3 ms; render CPU p50/p99 ≤ 0.15/0.5 ms; VRAM ≤ 64 MiB.",
    },
    ExplicitChoice {
        id: "medium",
        label: "Medium",
        enabled: true,
        tooltip: "Filmic HDR atmosphere, moving sun-and-moon shadows from terrain, trees, buildings, players, and monsters, plus optional volumetric shafts.\nEstimated ceiling — GPU p50/p99 ≤ 3.25/4.5 ms; render CPU p50/p99 ≤ 0.25/0.75 ms; VRAM ≤ 128 MiB.",
    },
    ExplicitChoice {
        id: "high",
        label: "High",
        enabled: true,
        tooltip: "Filmic HDR atmosphere, moving sun-and-moon shadows from terrain, trees, buildings, players, and monsters, plus optional volumetric shafts.\nEstimated ceiling — GPU p50/p99 ≤ 4.5/6 ms; render CPU p50/p99 ≤ 0.35/1 ms; VRAM ≤ 256 MiB.",
    },
    ExplicitChoice {
        id: "auto",
        label: "Auto",
        enabled: true,
        tooltip: "Filmic HDR atmosphere, moving sun-and-moon shadows from terrain, trees, buildings, players, and monsters, plus optional volumetric shafts.\nEstimated ceiling — GPU p50/p99 ≤ 3.25/4.5 ms; render CPU p50/p99 ≤ 0.25/0.75 ms; VRAM ≤ 128 MiB.",
    },
];

/// The shadow filter's choices (OpenAC's "1", "9", "25" taps).
const PCF_TAP_CHOICES: &[ExplicitChoice] = &[
    ExplicitChoice {
        id: "1",
        label: "1",
        enabled: true,
        tooltip: "",
    },
    ExplicitChoice {
        id: "9",
        label: "9",
        enabled: true,
        tooltip: "",
    },
    ExplicitChoice {
        id: "25",
        label: "25",
        enabled: true,
        tooltip: "",
    },
];

/// OpenAC's "Graphics Enhancements" section at the end of the page: the
/// quality menu, then a row per atmospheric setting with OpenAC's captions
/// and declared defaults (OpenAC `ConfigOptionsPageController.BindRenderPackSection`).
fn atmospheric_rows() -> Vec<ConfigRow> {
    use crate::panels::options::keys::RenderPackField;
    use ac_host::settings::render_pack::{ATMOSPHERIC_SETTINGS, SettingKind};
    let mut rows = vec![
        ConfigRow::ExplicitHeader("Graphics Enhancements"),
        ConfigRow::ExplicitMenu {
            label: "Atmospheric rendering",
            choices: ATMOSPHERIC_QUALITY_CHOICES,
            default: "off",
            target: OptionKey::RenderPack(RenderPackField::Quality),
        },
    ];
    for (index, s) in ATMOSPHERIC_SETTINGS.iter().enumerate() {
        let target = OptionKey::RenderPack(RenderPackField::Setting(index));
        rows.push(match s.kind {
            SettingKind::Boolean => ConfigRow::ExplicitToggle {
                label: s.display_name,
                default: s.encode(s.default).is_some_and(|v| v > 0.5),
                target,
                tooltip: "",
                dimmed_by_retail_mixer: false,
            },
            SettingKind::Integer | SettingKind::Float => ConfigRow::ExplicitSlider {
                label: s.display_name,
                min: s.minimum.unwrap_or(0.0),
                max: s.maximum.unwrap_or(1.0),
                step: s.step.unwrap_or(0.0),
                integer: s.kind == SettingKind::Integer,
                default: s.encode(s.default).map_or(0.0, f64::from),
                target,
                tooltip: "",
                dimmed_by_retail_mixer: false,
                range_low: None,
                range_high: None,
                value_suffix: None,
            },
            SettingKind::Choice => ConfigRow::ExplicitMenu {
                label: s.display_name,
                choices: PCF_TAP_CHOICES,
                default: s.default,
                target,
            },
        });
    }
    rows.push(ConfigRow::Separator);
    rows
}

/// One chat filter row: its mask and caption key (table 0x2300000D).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct FilterRow {
    /// The text types it covers.
    pub mask: u64,
    /// Caption key (`<key>_Desc` is its tooltip).
    pub label_key: &'static str,
}

/// The filter rows, in the game's order (the first, Gameplay, is not
/// offered for the main window).
pub const FILTER_ROWS: [FilterRow; 13] = [
    FilterRow {
        mask: 0x0000_0000_8391_2021,
        label_key: "ID_ChatOption_TextFilter_Gameplay",
    },
    FilterRow {
        mask: 0x0000_0000_0060_0040,
        label_key: "ID_ChatOption_TextFilter_Combat",
    },
    FilterRow {
        mask: 0x0000_0000_0002_0080,
        label_key: "ID_ChatOption_TextFilter_Magic",
    },
    FilterRow {
        mask: 0x0000_0000_0000_1004,
        label_key: "ID_ChatOption_TextFilter_AreaSpeech",
    },
    FilterRow {
        mask: 0x0000_0000_0000_0018,
        label_key: "ID_ChatOption_TextFilter_Tells",
    },
    FilterRow {
        mask: 0x0000_0000_0004_0C00,
        label_key: "ID_ChatOption_TextFilter_Allegience",
    },
    FilterRow {
        mask: 0x0000_0000_0008_0000,
        label_key: "ID_ChatOption_TextFilter_Fellowship",
    },
    FilterRow {
        mask: 0x0000_0000_0800_0000,
        label_key: "ID_ChatOption_TextFilter_General",
    },
    FilterRow {
        mask: 0x0000_0000_1000_0000,
        label_key: "ID_ChatOption_TextFilter_Trade",
    },
    FilterRow {
        mask: 0x0000_0000_2000_0000,
        label_key: "ID_ChatOption_TextFilter_LFG",
    },
    FilterRow {
        mask: 0x0000_0000_4000_0000,
        label_key: "ID_ChatOption_TextFilter_Roleplay",
    },
    FilterRow {
        mask: 0x0000_0001_0000_0000,
        label_key: "ID_ChatOption_TextFilter_Society",
    },
    FilterRow {
        mask: 0x0000_0000_0400_0000,
        label_key: "ID_ChatOption_TextFilter_Error",
    },
];

/// One chat window's filter block.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct FilterBlock {
    /// The game's window number.
    pub retail_window_id: i32,
    /// Our window number (0 main, 1..=4 floating).
    pub window: usize,
    /// Header key.
    pub header_key: &'static str,
    /// The window's default filter.
    pub default_filter: u64,
    /// Whether the Gameplay row is offered.
    pub includes_gameplay_row: bool,
}

/// The five blocks, main window first.
pub const FILTER_BLOCKS: [FilterBlock; 5] = [
    FilterBlock {
        retail_window_id: 8,
        window: 0,
        header_key: "ID_ChatOption_MainChatWindow_Section",
        default_filter: ac_core::chat::MAIN_WINDOW_DEFAULT_FILTER,
        includes_gameplay_row: false,
    },
    FilterBlock {
        retail_window_id: 2,
        window: 1,
        header_key: "ID_ChatOption_FloatyChatWindow1_Section",
        default_filter: ac_core::chat::FLOATY1_DEFAULT_FILTER,
        includes_gameplay_row: true,
    },
    FilterBlock {
        retail_window_id: 3,
        window: 2,
        header_key: "ID_ChatOption_FloatyChatWindow2_Section",
        default_filter: ac_core::chat::FLOATY2_DEFAULT_FILTER,
        includes_gameplay_row: true,
    },
    FilterBlock {
        retail_window_id: 4,
        window: 3,
        header_key: "ID_ChatOption_FloatyChatWindow3_Section",
        default_filter: ac_core::chat::FLOATY3_DEFAULT_FILTER,
        includes_gameplay_row: true,
    },
    FilterBlock {
        retail_window_id: 5,
        window: 4,
        header_key: "ID_ChatOption_FloatyChatWindow4_Section",
        default_filter: ac_core::chat::FLOATY4_DEFAULT_FILTER,
        includes_gameplay_row: true,
    },
];

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `CharacterOptionsPageControllerTests.cs`,
    //! `ConfigOptionsPageControllerTests.cs` and
    //! `ChatOptionsPageControllerTests.cs` (the table cases).
    use super::*;

    #[test]
    fn six_character_groups_in_order_with_their_row_counts() {
        let headers: Vec<_> = CHARACTER_GROUPS.iter().map(|g| g.header_key).collect();
        assert_eq!(
            headers,
            [
                "ID_CharacterOption_UIBehavior_Section",
                "ID_CharacterOption_UIDisplay_Section",
                "ID_CharacterOption_Grouping_Section",
                "ID_CharacterOption_OtherPlayers_Section",
                "ID_CharacterOption_CharacterBehavior_Section",
                "ID_CharacterOption_Chat_Section",
            ]
        );
        let counts: Vec<_> = CHARACTER_GROUPS.iter().map(|g| g.rows.len()).collect();
        assert_eq!(counts, [3, 15, 6, 11, 7, 8]);
        assert_eq!(character_row_count(), 50);
    }

    #[test]
    fn every_character_row_is_in_the_option_table_once() {
        let ids: Vec<u32> = CHARACTER_GROUPS
            .iter()
            .flat_map(|g| g.rows.iter().map(|r| r.id))
            .collect();
        for id in &ids {
            assert!(ac_runtime::character::option(*id).is_some(), "{id:#x}");
        }
        let mut sorted = ids.clone();
        sorted.sort_unstable();
        sorted.dedup();
        assert_eq!(sorted.len(), ids.len());
        // Every option but the three the page leaves out has a row.
        for entry in ac_runtime::character::OPTIONS {
            let excluded = [o::LOCK_UI, o::USE_MOUSE_TURNING, o::APPEAR_OFFLINE];
            assert_eq!(
                ids.contains(&entry.id),
                !excluded.contains(&entry.id),
                "{}",
                entry.name
            );
        }
        let chat = CHARACTER_GROUPS[5].rows;
        assert_eq!(chat.last().unwrap().id, o::HEAR_PK_DEATH_MESSAGES);
    }

    #[test]
    fn the_store_only_rows_are_openacs() {
        let stored: Vec<&str> = CHARACTER_GROUPS
            .iter()
            .flat_map(|g| g.rows.iter())
            .filter(|r| r.store_only)
            .map(|r| r.retail_name)
            .collect();
        assert_eq!(
            stored,
            [
                "DisableHouseRestrictionEffects",
                "DisplayAllegianceLogonNotifications",
                "UseDeception",
                "DisplayDateOfBirth",
                "DisplayAge",
                "DisplayFishingSkill",
                "DisplayNumberDeaths",
                "DisplayNumberCharacterTitles",
                "AdvancedCombatUI",
                "HearPKDeaths",
            ]
        );
        // OpenAC CharacterOptionsPageControllerTests: 10 of the 50 rows
        // only store their value; the 40 others are live.
        let rows = CHARACTER_GROUPS.iter().map(|g| g.rows.len()).sum::<usize>();
        assert_eq!((stored.len(), rows - stored.len()), (10, 40));
    }

    fn option_rows(rows: &[ConfigRow]) -> usize {
        rows.iter()
            .map(|r| match r {
                ConfigRow::Header(_) | ConfigRow::ExplicitHeader(_) | ConfigRow::Separator => 0,
                ConfigRow::Trio { .. } => 2,
                _ => 1,
            })
            .sum()
    }

    #[test]
    fn the_configuration_page_has_40_option_rows_in_49_list_items() {
        let all = config_rows();
        // The Graphics Enhancements section follows the game's rows.
        let enhancements = all
            .iter()
            .position(|r| matches!(r, ConfigRow::ExplicitHeader(_)))
            .expect("the enhancements section");
        let rows = &all[..enhancements];
        // The 39, the UI scale row, the landscape detail textures row (the
        // game has its caption but never offered it), and OpenAC's two
        // modern mouse turning rows, last of the explicit toggles (they sit
        // in the input section).
        let modern: Vec<&str> = rows
            .iter()
            .filter_map(|r| match r {
                ConfigRow::ExplicitToggle { label, .. } => Some(*label),
                _ => None,
            })
            .collect();
        assert_eq!(
            modern[modern.len() - 2..],
            [
                MODERN_MOUSE_TURNING_LABEL,
                BOTH_MOUSE_BUTTONS_RUN_FORWARD_LABEL
            ]
        );
        assert_eq!(option_rows(rows), 39 + 1 + 1 + 2);
        assert_eq!(rows.len(), 48 + 1 + 1 + 2);
        let headers = rows
            .iter()
            .filter(|r| matches!(r, ConfigRow::Header(_)))
            .count();
        let separators = rows
            .iter()
            .filter(|r| matches!(r, ConfigRow::Separator))
            .count();
        assert_eq!((headers, separators), (6, 6));
        // A header, the quality menu, the 20 settings, a separator.
        assert_eq!(option_rows(&all[enhancements..]), 21);
        assert_eq!(all.len() - enhancements, 23);
    }

    #[test]
    fn the_retail_defaults_are_the_literals() {
        // ConfigOptionsPageControllerTests.EveryRow_DefaultValue_MatchesRetailLiteral.
        for row in config_rows() {
            match row {
                ConfigRow::Toggle {
                    key: "ID_Rendering_SyncToDisplayRefresh",
                    default,
                    ..
                } => assert!(!default),
                ConfigRow::Toggle {
                    key: "ID_Rendering_FullScreen",
                    default,
                    ..
                } => assert!(default),
                ConfigRow::Slider {
                    key: "ID_Camera_Stiffness",
                    min,
                    default,
                    ..
                } => assert_eq!((min, default), (0.285_714_3, 0.45)),
                ConfigRow::Slider {
                    key: "ID_Input_MouseLookSensitivity",
                    min,
                    default,
                    ..
                } => assert_eq!((min, default), (0.01, 0.55)),
                ConfigRow::Menu {
                    key: "ID_Graphics_LandscapeDrawDistance",
                    default,
                    payloads,
                    ..
                } => {
                    assert_eq!(default, 8);
                    assert_eq!(payloads, Some(&[3, 5, 8, 11, 15, 25][..]));
                }
                ConfigRow::Menu {
                    key: "ID_UI_ChatFontFace",
                    default,
                    ..
                } => assert_eq!(default, 2),
                _ => {}
            }
        }
    }

    #[test]
    fn the_filter_tables_are_the_games() {
        assert_eq!(FILTER_ROWS.len(), 13);
        for (i, a) in FILTER_ROWS.iter().enumerate() {
            assert_ne!(a.mask, 0);
            for b in &FILTER_ROWS[i + 1..] {
                assert_ne!(a.mask, b.mask);
            }
        }
        let ids: Vec<i32> = FILTER_BLOCKS.iter().map(|b| b.retail_window_id).collect();
        assert_eq!(ids, [8, 2, 3, 4, 5]);
        assert_eq!(FILTER_BLOCKS[0].default_filter, 0xFBFF_FFFF);
        assert!(!FILTER_BLOCKS[0].includes_gameplay_row);
        assert!(FILTER_BLOCKS[1..].iter().all(|b| b.includes_gameplay_row));
    }
}
