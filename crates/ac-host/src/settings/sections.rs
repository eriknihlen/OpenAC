//! The settings file's other sections: sound, the mixer, chat, the camera,
//! misc and the per-character section, the records they hold, and the two
//! small rules the options panel applies to them (the chat opacity link and
//! the mouse-turning macro).

use crate::json::{Member, Value};

use super::{
    DisplaySettings, SettingsStore, Unreadable, bool_text, float_text, member, object_member,
    object_text, read_bool, read_float, read_int,
};

/// The stored sound settings. The game has three volume knobs (effects,
/// ambient, interface) and three disable switches; OpenAC adds a master
/// volume on top.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct AudioSettings {
    /// Master volume, 0..1.
    pub master: f32,
    /// Effects volume.
    pub sfx: f32,
    /// Ambient volume.
    pub ambient: f32,
    /// The "sound features" preference (0 stereo, 1 mono).
    pub sound_features: i32,
    /// Effects on.
    pub sfx_enabled: bool,
    /// Ambient sounds on.
    pub ambient_enabled: bool,
    /// Interface sounds on.
    pub interface_enabled: bool,
    /// Interface volume.
    pub interface_volume: f32,
    /// Play sound only while the window has the focus.
    pub play_sound_only_when_active: bool,
}

impl Default for AudioSettings {
    fn default() -> Self {
        Self {
            master: 1.0,
            sfx: 1.0,
            ambient: 1.0,
            sound_features: 0,
            sfx_enabled: true,
            ambient_enabled: true,
            interface_enabled: true,
            interface_volume: 1.0,
            play_sound_only_when_active: true,
        }
    }
}

/// The fewest voices the mixer may be given.
pub const MINIMUM_VOICE_COUNT: i32 = 16;
/// The most voices the mixer may be given.
pub const MAXIMUM_VOICE_COUNT: i32 = 64;
/// "No per-sound cap".
pub const NO_PER_WAVE_CAP: i32 = 0;
/// The most voices one sound may hold.
pub const MAXIMUM_MAX_VOICES_PER_WAVE: i32 = 8;

/// The stored mixer settings: their own section, so the options panel's
/// whole-section sound writes never drop them.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct AudioMixerSettings {
    /// Mix exactly as the game: sixteen voices, no priority, no cap.
    pub retail_mixer: bool,
    /// Voices, 16..=64.
    pub voice_count: i32,
    /// Record the authored priority against a voice.
    pub use_authored_priority: bool,
    /// Voices one sound may hold, 0 (no cap) ..= 8.
    pub max_voices_per_wave: i32,
}

impl Default for AudioMixerSettings {
    fn default() -> Self {
        Self {
            retail_mixer: false,
            voice_count: 32,
            use_authored_priority: true,
            max_voices_per_wave: 4,
        }
    }
}

impl AudioMixerSettings {
    /// The same settings with every value inside its range.
    #[must_use]
    pub fn normalized(self) -> Self {
        Self {
            voice_count: self
                .voice_count
                .clamp(MINIMUM_VOICE_COUNT, MAXIMUM_VOICE_COUNT),
            max_voices_per_wave: self
                .max_voices_per_wave
                .clamp(NO_PER_WAVE_CAP, MAXIMUM_MAX_VOICES_PER_WAVE),
            ..self
        }
    }
}

/// The stored chat settings: what the player hears, the five chat windows'
/// text-type filters, the windows' opacity and the chat font.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ChatSettings {
    /// Hear the General channel.
    pub hear_general_chat: bool,
    /// Hear the Trade channel.
    pub hear_trade_chat: bool,
    /// Hear the LFG channel.
    pub hear_lfg_chat: bool,
    /// Hear the Roleplay channel.
    pub hear_roleplay_chat: bool,
    /// Hear the Society channel.
    pub hear_society_chat: bool,
    /// Appear offline.
    pub appear_offline: bool,
    /// Show timestamps.
    pub show_timestamps: bool,
    /// Filter profanity.
    pub filter_profanity: bool,
    /// Font size, points.
    pub font_size: f32,
    /// Floating chat window 1's filter.
    pub chat_window1_filter: u64,
    /// Floating chat window 2's filter.
    pub chat_window2_filter: u64,
    /// Floating chat window 3's filter.
    pub chat_window3_filter: u64,
    /// Floating chat window 4's filter.
    pub chat_window4_filter: u64,
    /// The main chat window's filter.
    pub chat_window_main_filter: u64,
    /// Opacity of a window without the focus.
    pub default_opacity: f32,
    /// Opacity of the focused window.
    pub active_opacity: f32,
    /// Chat font face index (0 Arial .. 4 Times New Roman).
    pub chat_font_face: i32,
    /// Chat font size index (0 tiny .. 4 extra large).
    pub chat_font_size_index: i32,
}

impl Default for ChatSettings {
    fn default() -> Self {
        Self {
            hear_general_chat: true,
            hear_trade_chat: true,
            hear_lfg_chat: true,
            hear_roleplay_chat: false,
            hear_society_chat: false,
            appear_offline: false,
            show_timestamps: true,
            filter_profanity: true,
            font_size: 12.0,
            chat_window1_filter: 0x0000_101C,
            chat_window2_filter: 0x0004_0C00,
            chat_window3_filter: 0x0008_0000,
            chat_window4_filter: 0x7800_0000,
            chat_window_main_filter: 0xFBFF_FFFF,
            default_opacity: 1.0,
            active_opacity: 1.0,
            chat_font_face: 2,
            chat_font_size_index: 1,
        }
    }
}

impl ChatSettings {
    /// The filter of chat window `window` (0 the main window, 1..=4 the
    /// floating ones).
    #[must_use]
    pub fn filter(&self, window: usize) -> Option<u64> {
        Some(match window {
            0 => self.chat_window_main_filter,
            1 => self.chat_window1_filter,
            2 => self.chat_window2_filter,
            3 => self.chat_window3_filter,
            4 => self.chat_window4_filter,
            _ => return None,
        })
    }

    /// Set the filter of chat window `window` (see [`Self::filter`]).
    pub fn set_filter(&mut self, window: usize, value: u64) {
        match window {
            0 => self.chat_window_main_filter = value,
            1 => self.chat_window1_filter = value,
            2 => self.chat_window2_filter = value,
            3 => self.chat_window3_filter = value,
            4 => self.chat_window4_filter = value,
            _ => {}
        }
    }
}

/// The stored camera and mouse-look settings.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct CameraTurningSettings {
    /// How firmly the camera follows, 0.2857..=1.
    pub stiffness: f32,
    /// How fast the camera adjusts, 5..=80.
    pub adjustment_speed: f32,
    /// Mouse-look sensitivity, 0.01..=1.
    pub mouse_look_sensitivity: f32,
    /// Tilt the camera with the ground.
    pub align_to_slope: bool,
    /// Invert the mouse-look vertical axis.
    pub invert_mouse_look_y_axis: bool,
    /// The configuration page's own "use mouse turning" preference (the
    /// character option of the same name is the server's).
    pub use_mouse_turning: bool,
    /// Modern mouse turning (an OpenAC option, off by default): dragging
    /// with the right button pressed over the world turns the view around
    /// the character, and the character turns to face where it looks.
    pub modern_mouse_turning: bool,
    /// With modern mouse turning on, holding both mouse buttons (pressed
    /// over the world) runs forward (an OpenAC option, off by default).
    pub both_mouse_buttons_run_forward: bool,
}

impl Default for CameraTurningSettings {
    fn default() -> Self {
        Self {
            stiffness: 0.45,
            adjustment_speed: 40.0,
            mouse_look_sensitivity: 0.55,
            align_to_slope: true,
            invert_mouse_look_y_axis: false,
            use_mouse_turning: false,
            modern_mouse_turning: false,
            both_mouse_buttons_run_forward: false,
        }
    }
}

impl CameraTurningSettings {
    /// The mouse-turning macro's targets: stiffness 0.95, adjustment 50,
    /// sensitivity 0.7, align to slope off, inverted vertical axis on.
    #[must_use]
    pub fn mouse_turning_target() -> Self {
        Self {
            stiffness: 0.95,
            adjustment_speed: 50.0,
            mouse_look_sensitivity: 0.7,
            align_to_slope: false,
            invert_mouse_look_y_axis: true,
            use_mouse_turning: false,
            modern_mouse_turning: false,
            both_mouse_buttons_run_forward: false,
        }
    }
}

/// The stored tooltip preferences.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct MiscSettings {
    /// Show tooltips.
    pub tooltip_enable: bool,
    /// Seconds before a tooltip shows.
    pub tooltip_delay_seconds: f32,
}

impl Default for MiscSettings {
    fn default() -> Self {
        Self {
            tooltip_enable: true,
            tooltip_delay_seconds: 0.25,
        }
    }
}

/// One character's client-side settings (`character.<name>`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CharacterSettings {
    /// The chat channel a line goes to by default.
    pub default_chat_channel: String,
    /// Attack again automatically.
    pub auto_attack: bool,
    /// Ask before salvaging a valuable item.
    pub confirm_salvage: bool,
    /// Show a message for each pickup.
    pub show_pickup_messages: bool,
}

impl Default for CharacterSettings {
    fn default() -> Self {
        Self {
            default_chat_channel: "Local".into(),
            auto_attack: false,
            confirm_salvage: true,
            show_pickup_messages: true,
        }
    }
}

impl CharacterSettings {
    /// The channels the default chat channel may name.
    pub const AVAILABLE_CHANNELS: [&'static str; 7] = [
        "Local",
        "Allegiance",
        "Fellowship",
        "General",
        "Trade",
        "LFG",
        "Roleplay",
    ];
}

/// Every client-side section a window applies, read together at startup.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct PlayerSettings {
    /// Display.
    pub display: DisplaySettings,
    /// Sound.
    pub audio: AudioSettings,
    /// The mixer.
    pub mixer: AudioMixerSettings,
    /// Chat.
    pub chat: ChatSettings,
    /// Camera and mouse look.
    pub camera: CameraTurningSettings,
    /// Tooltips.
    pub misc: MiscSettings,
    /// The plugin appearance.
    pub plugin_ui: PluginUiSettings,
}

/// The appearance plugin windows that opt in take (OpenAC
/// `SettingsStore.LoadPluginUiTheme`: section `pluginUi`, member `theme`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginUiSettings {
    /// The theme's name as saved (`Classic`, `Moss`, `Brass`); the reader
    /// of the name decides what an unknown one means.
    pub theme: String,
    /// Where the plugin dock sits, as saved (`floating`,
    /// `floating-horizontal`, `left`, `right`, `top`, `bottom`); the reader of
    /// the name decides what an unknown one means.
    pub dock: String,
    /// The plugin dock's size, as saved (`smaller`, `default`, `bigger`); the
    /// reader of the name decides what an unknown one means.
    pub dock_size: String,
    /// How the plugin dock opens, as saved (`click`, `hover`); the reader of the
    /// name decides what an unknown one means.
    pub dock_opens: String,
    /// How many windows the plugin dock shows at most before the rest scroll;
    /// 0 or less for as many as the screen has room for.
    pub dock_slots: i32,
}

impl Default for PluginUiSettings {
    fn default() -> Self {
        Self {
            theme: "Classic".to_owned(),
            dock: "floating".to_owned(),
            dock_size: "default".to_owned(),
            dock_opens: "click".to_owned(),
            dock_slots: 0,
        }
    }
}

fn read_plugin_ui(m: &[Member]) -> Result<PluginUiSettings, Unreadable> {
    Ok(PluginUiSettings {
        theme: crate::settings::read_string(m, "theme", "Classic"),
        dock: crate::settings::read_string(m, "dock", "floating"),
        dock_size: crate::settings::read_string(m, "dockSize", "default"),
        dock_opens: crate::settings::read_string(m, "dockOpens", "click"),
        dock_slots: crate::settings::read_int(m, "dockSlots", 0)?,
    })
}

/// A 64-bit number member, by its written digits; anything else (or out
/// of range) falls back.
fn read_u64(members: &[Member], name: &str, fallback: u64) -> u64 {
    match member(members, name).map(|n| &n.value) {
        Some(Value::Number(raw)) => raw.parse::<u64>().unwrap_or(fallback),
        _ => fallback,
    }
}

fn read_audio(a: &[Member]) -> Result<AudioSettings, Unreadable> {
    let d = AudioSettings::default();
    Ok(AudioSettings {
        master: read_float(a, "master", d.master)?,
        sfx: read_float(a, "sfx", d.sfx)?,
        ambient: read_float(a, "ambient", d.ambient)?,
        sound_features: read_int(a, "soundFeatures", d.sound_features)?,
        sfx_enabled: read_bool(a, "sfxEnabled", d.sfx_enabled),
        ambient_enabled: read_bool(a, "ambientEnabled", d.ambient_enabled),
        interface_enabled: read_bool(a, "interfaceEnabled", d.interface_enabled),
        interface_volume: read_float(a, "interfaceVolume", d.interface_volume)?,
        play_sound_only_when_active: read_bool(
            a,
            "playSoundOnlyWhenActive",
            d.play_sound_only_when_active,
        ),
    })
}

fn read_mixer(m: &[Member]) -> Result<AudioMixerSettings, Unreadable> {
    let d = AudioMixerSettings::default();
    Ok(AudioMixerSettings {
        retail_mixer: read_bool(m, "retailMixer", d.retail_mixer),
        voice_count: read_int(m, "voiceCount", d.voice_count)?,
        use_authored_priority: read_bool(m, "useAuthoredPriority", d.use_authored_priority),
        max_voices_per_wave: read_int(m, "maxVoicesPerWave", d.max_voices_per_wave)?,
    }
    .normalized())
}

fn read_chat(c: &[Member]) -> Result<ChatSettings, Unreadable> {
    let d = ChatSettings::default();
    Ok(ChatSettings {
        hear_general_chat: read_bool(c, "hearGeneralChat", d.hear_general_chat),
        hear_trade_chat: read_bool(c, "hearTradeChat", d.hear_trade_chat),
        hear_lfg_chat: read_bool(c, "hearLFGChat", d.hear_lfg_chat),
        hear_roleplay_chat: read_bool(c, "hearRoleplayChat", d.hear_roleplay_chat),
        hear_society_chat: read_bool(c, "hearSocietyChat", d.hear_society_chat),
        appear_offline: read_bool(c, "appearOffline", d.appear_offline),
        show_timestamps: read_bool(c, "showTimestamps", d.show_timestamps),
        filter_profanity: read_bool(c, "filterProfanity", d.filter_profanity),
        font_size: read_float(c, "fontSize", d.font_size)?,
        chat_window1_filter: read_u64(c, "chatWindow1Filter", d.chat_window1_filter),
        chat_window2_filter: read_u64(c, "chatWindow2Filter", d.chat_window2_filter),
        chat_window3_filter: read_u64(c, "chatWindow3Filter", d.chat_window3_filter),
        chat_window4_filter: read_u64(c, "chatWindow4Filter", d.chat_window4_filter),
        chat_window_main_filter: read_u64(c, "chatWindowMainFilter", d.chat_window_main_filter),
        default_opacity: read_float(c, "defaultOpacity", d.default_opacity)?,
        active_opacity: read_float(c, "activeOpacity", d.active_opacity)?,
        chat_font_face: read_int(c, "chatFontFace", d.chat_font_face)?,
        chat_font_size_index: read_int(c, "chatFontSizeIndex", d.chat_font_size_index)?,
    })
}

fn read_camera(c: &[Member]) -> Result<CameraTurningSettings, Unreadable> {
    let d = CameraTurningSettings::default();
    Ok(CameraTurningSettings {
        stiffness: read_float(c, "stiffness", d.stiffness)?,
        adjustment_speed: read_float(c, "adjustmentSpeed", d.adjustment_speed)?,
        mouse_look_sensitivity: read_float(c, "mouseLookSensitivity", d.mouse_look_sensitivity)?,
        align_to_slope: read_bool(c, "alignToSlope", d.align_to_slope),
        invert_mouse_look_y_axis: read_bool(c, "invertMouseLookYAxis", d.invert_mouse_look_y_axis),
        use_mouse_turning: read_bool(c, "useMouseTurning", d.use_mouse_turning),
        modern_mouse_turning: read_bool(c, "modernMouseTurning", d.modern_mouse_turning),
        both_mouse_buttons_run_forward: read_bool(
            c,
            "bothMouseButtonsRunForward",
            d.both_mouse_buttons_run_forward,
        ),
    })
}

fn read_misc(m: &[Member]) -> Result<MiscSettings, Unreadable> {
    let d = MiscSettings::default();
    Ok(MiscSettings {
        tooltip_enable: read_bool(m, "tooltipEnable", d.tooltip_enable),
        tooltip_delay_seconds: read_float(m, "tooltipDelaySeconds", d.tooltip_delay_seconds)?,
    })
}

/// A field present with the wrong type spoils the whole record (the game
/// reads typed values and falls back to the defaults on any failure).
fn read_character(c: &[Member]) -> Result<CharacterSettings, Unreadable> {
    let d = CharacterSettings::default();
    let boolean = |name: &str, fallback: bool| match member(c, name).map(|n| &n.value) {
        None | Some(Value::Null) => Ok(fallback),
        Some(Value::Bool(b)) => Ok(*b),
        Some(_) => Err(Unreadable),
    };
    let default_chat_channel = match member(c, "defaultChatChannel").map(|n| &n.value) {
        None | Some(Value::Null) => d.default_chat_channel,
        Some(Value::String(s)) => s.clone(),
        Some(_) => return Err(Unreadable),
    };
    Ok(CharacterSettings {
        default_chat_channel,
        auto_attack: boolean("autoAttack", d.auto_attack)?,
        confirm_salvage: boolean("confirmSalvage", d.confirm_salvage)?,
        show_pickup_messages: boolean("showPickupMessages", d.show_pickup_messages)?,
    })
}

impl SettingsStore {
    /// Read section `name` with `read`: the defaults when it is missing,
    /// the defaults and a warning when the file or a value cannot be read.
    fn load_with<T: Default>(
        &self,
        name: &str,
        read: impl FnOnce(&[Member]) -> Result<T, Unreadable>,
    ) -> (T, Option<String>) {
        match self.section(name) {
            Ok(None) => (T::default(), None),
            Ok(Some(members)) => match read(&members) {
                Ok(value) => (value, None),
                Err(Unreadable) => (
                    T::default(),
                    Some(self.failed(&format!("a {name} value has the wrong type"))),
                ),
            },
            Err(why) => (T::default(), Some(self.failed(&why))),
        }
    }

    /// The sound settings (see [`SettingsStore::load_display`] for the
    /// fallbacks).
    #[must_use]
    pub fn load_audio(&self) -> (AudioSettings, Option<String>) {
        self.load_with("audio", read_audio)
    }

    /// Write the sound section.
    pub fn save_audio(&self, a: &AudioSettings) -> std::io::Result<()> {
        let fields = [
            ("ambient", float_text(a.ambient)),
            ("ambientEnabled", bool_text(a.ambient_enabled)),
            ("interfaceEnabled", bool_text(a.interface_enabled)),
            ("interfaceVolume", float_text(a.interface_volume)),
            ("master", float_text(a.master)),
            (
                "playSoundOnlyWhenActive",
                bool_text(a.play_sound_only_when_active),
            ),
            ("sfx", float_text(a.sfx)),
            ("sfxEnabled", bool_text(a.sfx_enabled)),
            ("soundFeatures", a.sound_features.to_string()),
        ];
        self.save_section("audio", &object_text(&fields))
    }

    /// The mixer settings, normalized.
    #[must_use]
    pub fn load_audio_mixer(&self) -> (AudioMixerSettings, Option<String>) {
        self.load_with("audioMixer", read_mixer)
    }

    /// Write the mixer section.
    pub fn save_audio_mixer(&self, m: &AudioMixerSettings) -> std::io::Result<()> {
        let fields = [
            ("maxVoicesPerWave", m.max_voices_per_wave.to_string()),
            ("retailMixer", bool_text(m.retail_mixer)),
            ("useAuthoredPriority", bool_text(m.use_authored_priority)),
            ("voiceCount", m.voice_count.to_string()),
        ];
        self.save_section("audioMixer", &object_text(&fields))
    }

    /// The chat settings.
    #[must_use]
    pub fn load_chat(&self) -> (ChatSettings, Option<String>) {
        self.load_with("chat", read_chat)
    }

    /// Write the chat section.
    pub fn save_chat(&self, c: &ChatSettings) -> std::io::Result<()> {
        let fields = [
            ("activeOpacity", float_text(c.active_opacity)),
            ("appearOffline", bool_text(c.appear_offline)),
            ("chatFontFace", c.chat_font_face.to_string()),
            ("chatFontSizeIndex", c.chat_font_size_index.to_string()),
            ("chatWindow1Filter", c.chat_window1_filter.to_string()),
            ("chatWindow2Filter", c.chat_window2_filter.to_string()),
            ("chatWindow3Filter", c.chat_window3_filter.to_string()),
            ("chatWindow4Filter", c.chat_window4_filter.to_string()),
            (
                "chatWindowMainFilter",
                c.chat_window_main_filter.to_string(),
            ),
            ("defaultOpacity", float_text(c.default_opacity)),
            ("filterProfanity", bool_text(c.filter_profanity)),
            ("fontSize", float_text(c.font_size)),
            ("hearGeneralChat", bool_text(c.hear_general_chat)),
            ("hearLFGChat", bool_text(c.hear_lfg_chat)),
            ("hearRoleplayChat", bool_text(c.hear_roleplay_chat)),
            ("hearSocietyChat", bool_text(c.hear_society_chat)),
            ("hearTradeChat", bool_text(c.hear_trade_chat)),
            ("showTimestamps", bool_text(c.show_timestamps)),
        ];
        self.save_section("chat", &object_text(&fields))
    }

    /// The camera and mouse-look settings.
    #[must_use]
    pub fn load_camera_turning(&self) -> (CameraTurningSettings, Option<String>) {
        self.load_with("cameraTurning", read_camera)
    }

    /// Write the camera section.
    pub fn save_camera_turning(&self, c: &CameraTurningSettings) -> std::io::Result<()> {
        let fields = [
            ("adjustmentSpeed", float_text(c.adjustment_speed)),
            ("alignToSlope", bool_text(c.align_to_slope)),
            (
                "bothMouseButtonsRunForward",
                bool_text(c.both_mouse_buttons_run_forward),
            ),
            (
                "invertMouseLookYAxis",
                bool_text(c.invert_mouse_look_y_axis),
            ),
            ("modernMouseTurning", bool_text(c.modern_mouse_turning)),
            ("mouseLookSensitivity", float_text(c.mouse_look_sensitivity)),
            ("stiffness", float_text(c.stiffness)),
            ("useMouseTurning", bool_text(c.use_mouse_turning)),
        ];
        self.save_section("cameraTurning", &object_text(&fields))
    }

    /// The tooltip preferences.
    #[must_use]
    pub fn load_misc(&self) -> (MiscSettings, Option<String>) {
        self.load_with("misc", read_misc)
    }

    /// Write the tooltip preferences.
    pub fn save_misc(&self, m: &MiscSettings) -> std::io::Result<()> {
        let fields = [
            ("tooltipDelaySeconds", float_text(m.tooltip_delay_seconds)),
            ("tooltipEnable", bool_text(m.tooltip_enable)),
        ];
        self.save_section("misc", &object_text(&fields))
    }

    /// The plugin appearance (`Classic` when the section is absent or
    /// unreadable).
    #[must_use]
    pub fn load_plugin_ui(&self) -> (PluginUiSettings, Option<String>) {
        self.load_with("pluginUi", read_plugin_ui)
    }

    /// Write the plugin appearance (the other sections are kept).
    pub fn save_plugin_ui(&self, p: &PluginUiSettings) -> std::io::Result<()> {
        let mut theme = String::new();
        crate::json::write_string(&mut theme, &p.theme);
        let mut dock = String::new();
        crate::json::write_string(&mut dock, &p.dock);
        let mut dock_size = String::new();
        crate::json::write_string(&mut dock_size, &p.dock_size);
        let mut dock_opens = String::new();
        crate::json::write_string(&mut dock_opens, &p.dock_opens);
        self.save_section(
            "pluginUi",
            &object_text(&[
                ("theme", theme),
                ("dock", dock),
                ("dockSize", dock_size),
                ("dockOpens", dock_opens),
                ("dockSlots", p.dock_slots.to_string()),
            ]),
        )
    }

    /// Every client-side section at once, with the warnings of those that
    /// could not be read.
    #[must_use]
    pub fn load_player(&self) -> (PlayerSettings, Vec<String>) {
        let mut warnings = Vec::new();
        let mut take = |w: Option<String>| warnings.extend(w);
        let (display, w) = self.load_display();
        take(w);
        let (audio, w) = self.load_audio();
        take(w);
        let (mixer, w) = self.load_audio_mixer();
        take(w);
        let (chat, w) = self.load_chat();
        take(w);
        let (camera, w) = self.load_camera_turning();
        take(w);
        let (misc, w) = self.load_misc();
        take(w);
        let (plugin_ui, w) = self.load_plugin_ui();
        take(w);
        // One unreadable file is one warning, not seven.
        warnings.dedup();
        (
            PlayerSettings {
                display,
                audio,
                mixer,
                chat,
                camera,
                misc,
                plugin_ui,
            },
            warnings,
        )
    }

    /// Character `toon`'s settings (the defaults when the file or the
    /// character has none, or a value has the wrong type).
    #[must_use]
    pub fn load_character(&self, toon: &str) -> CharacterSettings {
        let Ok(Some(root)) = self.read_root() else {
            return CharacterSettings::default();
        };
        let toon = member(&root, "character")
            .and_then(|n| match &n.value {
                Value::Object(m) => member(m, toon),
                _ => None,
            })
            .and_then(|n| match &n.value {
                Value::Object(m) => Some(m),
                _ => None,
            });
        toon.and_then(|m| read_character(m).ok())
            .unwrap_or_default()
    }

    /// Write character `toon`'s settings, keeping every other character and
    /// section.
    pub fn save_character(&self, toon: &str, s: &CharacterSettings) -> std::io::Result<()> {
        let mut root = self.load_mutable_root()?;
        let characters = object_member(&mut root, "character");
        let record = vec![
            Member::made("autoAttack", Value::Bool(s.auto_attack)),
            Member::made("confirmSalvage", Value::Bool(s.confirm_salvage)),
            Member::made(
                "defaultChatChannel",
                Value::String(s.default_chat_channel.clone()),
            ),
            Member::made("showPickupMessages", Value::Bool(s.show_pickup_messages)),
        ];
        super::set_member(characters, toon, Value::Object(record));
        self.write_mutable_root(root)
    }
}

/// The chat windows' two opacity sliders are linked: the focused window is
/// never more transparent than the others.
pub mod chat_opacity {
    /// The unfocused opacity set to `new_default` (clamped to 0..=1): the
    /// active one rises to it when below. Returns (default, active).
    #[must_use]
    pub fn set_default(current_active: f32, new_default: f32) -> (f32, f32) {
        let new_default = new_default.clamp(0.0, 1.0);
        let active = if current_active < new_default {
            new_default
        } else {
            current_active
        };
        (new_default, active)
    }

    /// The focused opacity set to `new_active` (clamped to 0..=1): the
    /// default one falls to it when above. Returns (default, active).
    #[must_use]
    pub fn set_active(current_default: f32, new_active: f32) -> (f32, f32) {
        let new_active = new_active.clamp(0.0, 1.0);
        let default = if current_default > new_active {
            new_active
        } else {
            current_default
        };
        (default, new_active)
    }
}

/// The lines the mouse-turning macro writes to chat.
pub mod mouse_turning_text {
    /// Stiffness changed from `from` to `to`.
    #[must_use]
    pub fn camera_stiffness_changed(from: f32, to: f32) -> String {
        format!(
            "Camera Stiffness was changed from {from:.6} to the mouse turning default of {to:.6}."
        )
    }

    /// Adjustment speed changed.
    #[must_use]
    pub fn camera_adjustment_changed(from: f32, to: f32) -> String {
        format!(
            "Camera Adjustment was changed from {from:.6} to the mouse turning default of {to:.6}."
        )
    }

    /// Mouse sensitivity changed.
    #[must_use]
    pub fn mouse_sensitivity_changed(from: f32, to: f32) -> String {
        format!(
            "Mouse Sensitivity was changed from {from:.6} to the mouse turning default of {to:.6}."
        )
    }

    /// Align to slope turned off.
    pub const ALIGN_TO_SLOPE_CHANGED: &str =
        "Align To Slope was changed from TRUE to the mouse turning default of FALSE.";

    /// Inverted mouse look turned on.
    pub const INVERT_MOUSE_LOOK_AXES_CHANGED: &str =
        "Invert Mouselook Axes was changed from FALSE to the mouse turning default of TRUE.";

    /// Turn to face the camera turned on.
    pub const TURN_TO_FACE_CAMERA_CHANGED: &str =
        "Turn to Face Camera was changed from FALSE to the mouse turning default of TRUE.";
}

/// What the "use mouse turning settings" button does.
#[derive(Debug, Clone, PartialEq)]
pub struct MouseTurningMacro {
    /// The camera settings afterwards.
    pub updated: CameraTurningSettings,
    /// The "use mouse turning" character option must be turned on.
    pub use_mouse_turning_changed: bool,
    /// One chat line per change, in the game's order.
    pub chat_lines: Vec<String>,
}

/// The "use mouse turning settings" button: each camera value not at its
/// mouse-turning target is set to it, with a line saying so, and the
/// character option is turned on if it is off.
#[must_use]
pub fn mouse_turning_macro(
    current: &CameraTurningSettings,
    use_mouse_turning_current: bool,
) -> MouseTurningMacro {
    use mouse_turning_text as text;
    let target = CameraTurningSettings::mouse_turning_target();
    let mut lines = Vec::new();
    let mut updated = *current;
    if current.stiffness != target.stiffness {
        lines.push(text::camera_stiffness_changed(
            current.stiffness,
            target.stiffness,
        ));
        updated.stiffness = target.stiffness;
    }
    if current.adjustment_speed != target.adjustment_speed {
        lines.push(text::camera_adjustment_changed(
            current.adjustment_speed,
            target.adjustment_speed,
        ));
        updated.adjustment_speed = target.adjustment_speed;
    }
    if current.mouse_look_sensitivity != target.mouse_look_sensitivity {
        lines.push(text::mouse_sensitivity_changed(
            current.mouse_look_sensitivity,
            target.mouse_look_sensitivity,
        ));
        updated.mouse_look_sensitivity = target.mouse_look_sensitivity;
    }
    if current.align_to_slope != target.align_to_slope {
        lines.push(text::ALIGN_TO_SLOPE_CHANGED.to_owned());
        updated.align_to_slope = target.align_to_slope;
    }
    if current.invert_mouse_look_y_axis != target.invert_mouse_look_y_axis {
        lines.push(text::INVERT_MOUSE_LOOK_AXES_CHANGED.to_owned());
        updated.invert_mouse_look_y_axis = target.invert_mouse_look_y_axis;
    }
    let use_mouse_turning_changed = !use_mouse_turning_current;
    if use_mouse_turning_changed {
        lines.push(text::TURN_TO_FACE_CAMERA_CHANGED.to_owned());
    }
    // OpenAC builds the result record without its own preference, which
    // therefore comes back at its default.
    updated.use_mouse_turning = false;
    MouseTurningMacro {
        updated,
        use_mouse_turning_changed,
        chat_lines: lines,
    }
}

#[cfg(test)]
mod tests;
