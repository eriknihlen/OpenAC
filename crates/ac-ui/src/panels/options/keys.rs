//! What each option row edits, and reading and writing it in the live
//! values the options window keeps a copy of.

use ac_host::settings::render_pack as rp;
use ac_host::settings::{PlayerSettings, QualityPreset, RenderPackSelection};

use super::model::OptionValue;

/// A sound setting.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[allow(missing_docs)]
pub enum AudioField {
    SoundFeatures,
    SfxEnabled,
    Sfx,
    AmbientEnabled,
    Ambient,
    InterfaceEnabled,
    InterfaceVolume,
    PlaySoundOnlyWhenActive,
}

/// A mixer setting.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[allow(missing_docs)]
pub enum MixerField {
    RetailMixer,
    VoiceCount,
    UseAuthoredPriority,
    MaxVoicesPerWave,
}

/// A camera or mouse-look setting.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[allow(missing_docs)]
pub enum CameraField {
    Stiffness,
    AdjustmentSpeed,
    MouseLookSensitivity,
    AlignToSlope,
    InvertMouseLookYAxis,
    UseMouseTurning,
    ModernMouseTurning,
    BothMouseButtonsRunForward,
}

/// A display setting.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[allow(missing_docs)]
pub enum DisplayField {
    Resolution,
    Fullscreen,
    VSync,
    FieldOfView,
    ScreenBrightness,
    AutomaticDegrades,
    GraphicsPerformance,
    DegradeDistance,
    KeepDistantBuildings,
    Quality,
    PotatoMode,
    UiScale,
    UiOnly,
    UiOnlyWhenUnfocused,
    LandscapeTextureDetail,
    EnvironmentTextureDetail,
    TextureFiltering,
    LandscapeDrawDistance,
    BuildingDetailTextures,
    LandscapeDetailTextures,
    MultiPassAlpha,
}

/// A chat setting.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ChatField {
    /// The chat font face.
    FontFace,
    /// The chat font size.
    FontSize,
    /// Opacity of the windows without the focus.
    DefaultOpacity,
    /// Opacity of the focused window.
    ActiveOpacity,
    /// Chat window `n`'s text-type filter (0 the main window, 1..=4 the
    /// floating ones).
    Filter(usize),
}

/// What an option row edits.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum OptionKey {
    /// A character option (server-saved), by its id.
    Character(u32),
    /// Sound.
    Audio(AudioField),
    /// The mixer.
    Mixer(MixerField),
    /// Camera and mouse look.
    Camera(CameraField),
    /// Display.
    Display(DisplayField),
    /// Chat.
    Chat(ChatField),
    /// The renderer's atmospheric rendering.
    RenderPack(RenderPackField),
}

/// An atmospheric-rendering row: the quality (with "off"), or the setting
/// at an index of `ac_host::settings::render_pack::ATMOSPHERIC_SETTINGS`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum RenderPackField {
    /// `off`, or the preset id.
    Quality,
    /// A setting by its declaration index.
    Setting(usize),
}

/// Which stored section a change touched.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Section {
    /// A character option (the server's).
    Character(u32),
    /// Sound.
    Audio,
    /// The mixer.
    Mixer,
    /// Camera.
    Camera,
    /// Display.
    Display,
    /// Chat.
    Chat,
    /// The plugin appearance (the Appearance window, not a page here).
    PluginUi,
}

/// The values the options window reads its rows from: the client settings
/// and the character's two option words.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct LiveValues {
    /// The client settings.
    pub settings: PlayerSettings,
    /// The character's first options word.
    pub options1: u32,
    /// The second word.
    pub options2: u32,
}

const QUALITY_NAMES: [(&str, QualityPreset); 5] = [
    ("Low", QualityPreset::Low),
    ("Medium", QualityPreset::Medium),
    ("High", QualityPreset::High),
    ("Ultra", QualityPreset::Ultra),
    ("Potato", QualityPreset::Potato),
];

/// A quality preset's stored name.
#[must_use]
pub fn quality_name(q: QualityPreset) -> &'static str {
    QUALITY_NAMES
        .iter()
        .find(|(_, v)| *v == q)
        .map_or("High", |(n, _)| n)
}

fn quality_by_name(name: &str) -> Option<QualityPreset> {
    QUALITY_NAMES
        .iter()
        .find(|(n, _)| n.eq_ignore_ascii_case(name))
        .map(|(_, v)| *v)
}

impl LiveValues {
    /// Character option `id`'s value (`None` for an unknown id).
    #[must_use]
    pub fn character_option(&self, id: u32) -> Option<bool> {
        let entry = ac_runtime::character::option(id)?;
        let word = if entry.in_options1 {
            self.options1
        } else {
            self.options2
        };
        Some(word & entry.mask != 0)
    }

    fn set_character_option(&mut self, id: u32, value: bool) {
        let Some(entry) = ac_runtime::character::option(id) else {
            return;
        };
        let word = if entry.in_options1 {
            &mut self.options1
        } else {
            &mut self.options2
        };
        if value {
            *word |= entry.mask;
        } else {
            *word &= !entry.mask;
        }
    }

    /// The live value of `key`.
    #[must_use]
    pub fn read(&self, key: OptionKey) -> Option<OptionValue> {
        use OptionValue::{Bits, Bool, Float, Int, Text};
        let s = &self.settings;
        Some(match key {
            OptionKey::Character(id) => Bool(self.character_option(id)?),
            OptionKey::Audio(f) => {
                let a = &s.audio;
                match f {
                    AudioField::SoundFeatures => Int(a.sound_features),
                    AudioField::SfxEnabled => Bool(a.sfx_enabled),
                    AudioField::Sfx => Float(a.sfx),
                    AudioField::AmbientEnabled => Bool(a.ambient_enabled),
                    AudioField::Ambient => Float(a.ambient),
                    AudioField::InterfaceEnabled => Bool(a.interface_enabled),
                    AudioField::InterfaceVolume => Float(a.interface_volume),
                    AudioField::PlaySoundOnlyWhenActive => Bool(a.play_sound_only_when_active),
                }
            }
            OptionKey::Mixer(f) => {
                let m = &s.mixer;
                match f {
                    MixerField::RetailMixer => Bool(m.retail_mixer),
                    MixerField::VoiceCount => Float(m.voice_count as f32),
                    MixerField::UseAuthoredPriority => Bool(m.use_authored_priority),
                    MixerField::MaxVoicesPerWave => Float(m.max_voices_per_wave as f32),
                }
            }
            OptionKey::Camera(f) => {
                let c = &s.camera;
                match f {
                    CameraField::Stiffness => Float(c.stiffness),
                    CameraField::AdjustmentSpeed => Float(c.adjustment_speed),
                    CameraField::MouseLookSensitivity => Float(c.mouse_look_sensitivity),
                    CameraField::AlignToSlope => Bool(c.align_to_slope),
                    CameraField::InvertMouseLookYAxis => Bool(c.invert_mouse_look_y_axis),
                    CameraField::UseMouseTurning => Bool(c.use_mouse_turning),
                    CameraField::ModernMouseTurning => Bool(c.modern_mouse_turning),
                    CameraField::BothMouseButtonsRunForward => {
                        Bool(c.both_mouse_buttons_run_forward)
                    }
                }
            }
            OptionKey::Display(f) => {
                let d = &s.display;
                match f {
                    DisplayField::Resolution => Text(d.resolution.clone()),
                    DisplayField::Fullscreen => Bool(d.fullscreen),
                    DisplayField::VSync => Bool(d.vsync),
                    DisplayField::FieldOfView => Float(d.field_of_view),
                    DisplayField::ScreenBrightness => Float(d.screen_brightness),
                    DisplayField::AutomaticDegrades => Bool(d.automatic_degrades),
                    DisplayField::GraphicsPerformance => Float(d.graphics_performance),
                    DisplayField::DegradeDistance => Float(d.degrade_distance),
                    DisplayField::KeepDistantBuildings => Bool(d.keep_distant_buildings),
                    DisplayField::Quality => Text(quality_name(d.quality).to_owned()),
                    DisplayField::PotatoMode => Bool(d.potato_mode),
                    DisplayField::UiScale => Float(d.ui_scale_percent as f32),
                    DisplayField::UiOnly => Bool(d.ui_only),
                    DisplayField::UiOnlyWhenUnfocused => Bool(d.ui_only_when_unfocused),
                    DisplayField::LandscapeTextureDetail => Int(d.landscape_texture_detail),
                    DisplayField::EnvironmentTextureDetail => Int(d.environment_texture_detail),
                    DisplayField::TextureFiltering => Int(d.texture_filtering),
                    DisplayField::LandscapeDrawDistance => Int(d.landscape_draw_distance),
                    DisplayField::BuildingDetailTextures => Bool(d.building_detail_textures),
                    DisplayField::LandscapeDetailTextures => Bool(d.landscape_detail_textures),
                    DisplayField::MultiPassAlpha => Bool(d.multi_pass_alpha),
                }
            }
            OptionKey::Chat(f) => {
                let c = &s.chat;
                match f {
                    ChatField::FontFace => Int(c.chat_font_face),
                    ChatField::FontSize => Int(c.chat_font_size_index),
                    ChatField::DefaultOpacity => Float(c.default_opacity),
                    ChatField::ActiveOpacity => Float(c.active_opacity),
                    ChatField::Filter(w) => Bits(c.filter(w)?),
                }
            }
            OptionKey::RenderPack(f) => read_render_pack(&s.display, f)?,
        })
    }

    /// Write `value` to `key`; the section it touched, `None` when the value
    /// does not fit the key (nothing changed).
    pub fn write(&mut self, key: OptionKey, value: &OptionValue) -> Option<Section> {
        use OptionValue::{Bits, Bool, Float, Int, Text};
        let s = &mut self.settings;
        match (key, value) {
            (OptionKey::Character(id), Bool(v)) => {
                ac_runtime::character::option(id)?;
                self.set_character_option(id, *v);
                Some(Section::Character(id))
            }
            (OptionKey::Audio(f), v) => {
                let a = &mut s.audio;
                match (f, v) {
                    (AudioField::SoundFeatures, Int(x)) => a.sound_features = *x,
                    (AudioField::SfxEnabled, Bool(x)) => a.sfx_enabled = *x,
                    (AudioField::Sfx, Float(x)) => a.sfx = *x,
                    (AudioField::AmbientEnabled, Bool(x)) => a.ambient_enabled = *x,
                    (AudioField::Ambient, Float(x)) => a.ambient = *x,
                    (AudioField::InterfaceEnabled, Bool(x)) => a.interface_enabled = *x,
                    (AudioField::InterfaceVolume, Float(x)) => a.interface_volume = *x,
                    (AudioField::PlaySoundOnlyWhenActive, Bool(x)) => {
                        a.play_sound_only_when_active = *x;
                    }
                    _ => return None,
                }
                Some(Section::Audio)
            }
            (OptionKey::Mixer(f), v) => {
                let m = &mut s.mixer;
                match (f, v) {
                    (MixerField::RetailMixer, Bool(x)) => m.retail_mixer = *x,
                    (MixerField::VoiceCount, Float(x)) => m.voice_count = x.round() as i32,
                    (MixerField::UseAuthoredPriority, Bool(x)) => m.use_authored_priority = *x,
                    (MixerField::MaxVoicesPerWave, Float(x)) => {
                        m.max_voices_per_wave = x.round() as i32;
                    }
                    _ => return None,
                }
                Some(Section::Mixer)
            }
            (OptionKey::Camera(f), v) => {
                let c = &mut s.camera;
                match (f, v) {
                    (CameraField::Stiffness, Float(x)) => c.stiffness = *x,
                    (CameraField::AdjustmentSpeed, Float(x)) => c.adjustment_speed = *x,
                    (CameraField::MouseLookSensitivity, Float(x)) => {
                        c.mouse_look_sensitivity = *x;
                    }
                    (CameraField::AlignToSlope, Bool(x)) => c.align_to_slope = *x,
                    (CameraField::InvertMouseLookYAxis, Bool(x)) => {
                        c.invert_mouse_look_y_axis = *x;
                    }
                    (CameraField::UseMouseTurning, Bool(x)) => c.use_mouse_turning = *x,
                    (CameraField::ModernMouseTurning, Bool(x)) => c.modern_mouse_turning = *x,
                    (CameraField::BothMouseButtonsRunForward, Bool(x)) => {
                        c.both_mouse_buttons_run_forward = *x;
                    }
                    _ => return None,
                }
                Some(Section::Camera)
            }
            (OptionKey::Display(f), v) => {
                let d = &mut s.display;
                match (f, v) {
                    (DisplayField::Resolution, Text(x)) => d.resolution = x.clone(),
                    (DisplayField::Fullscreen, Bool(x)) => d.fullscreen = *x,
                    (DisplayField::VSync, Bool(x)) => d.vsync = *x,
                    (DisplayField::FieldOfView, Float(x)) => d.field_of_view = *x,
                    (DisplayField::ScreenBrightness, Float(x)) => d.screen_brightness = *x,
                    (DisplayField::AutomaticDegrades, Bool(x)) => d.automatic_degrades = *x,
                    (DisplayField::GraphicsPerformance, Float(x)) => d.graphics_performance = *x,
                    (DisplayField::DegradeDistance, Float(x)) => d.degrade_distance = *x,
                    (DisplayField::KeepDistantBuildings, Bool(x)) => {
                        d.keep_distant_buildings = *x;
                    }
                    (DisplayField::Quality, Text(x)) => d.quality = quality_by_name(x)?,
                    (DisplayField::PotatoMode, Bool(x)) => d.potato_mode = *x,
                    (DisplayField::UiScale, Float(x)) => {
                        d.ui_scale_percent = ac_host::settings::ui_scale_percent(x.round() as i32);
                    }
                    (DisplayField::UiOnly, Bool(x)) => d.ui_only = *x,
                    (DisplayField::UiOnlyWhenUnfocused, Bool(x)) => d.ui_only_when_unfocused = *x,
                    (DisplayField::LandscapeTextureDetail, Int(x)) => {
                        d.landscape_texture_detail = *x;
                    }
                    (DisplayField::EnvironmentTextureDetail, Int(x)) => {
                        d.environment_texture_detail = *x;
                    }
                    (DisplayField::TextureFiltering, Int(x)) => d.texture_filtering = *x,
                    (DisplayField::LandscapeDrawDistance, Int(x)) => {
                        d.landscape_draw_distance = *x;
                    }
                    (DisplayField::BuildingDetailTextures, Bool(x)) => {
                        d.building_detail_textures = *x;
                    }
                    (DisplayField::LandscapeDetailTextures, Bool(x)) => {
                        d.landscape_detail_textures = *x;
                    }
                    (DisplayField::MultiPassAlpha, Bool(x)) => d.multi_pass_alpha = *x,
                    _ => return None,
                }
                Some(Section::Display)
            }
            (OptionKey::Chat(f), v) => {
                let c = &mut s.chat;
                match (f, v) {
                    (ChatField::FontFace, Int(x)) => c.chat_font_face = *x,
                    (ChatField::FontSize, Int(x)) => c.chat_font_size_index = *x,
                    (ChatField::DefaultOpacity, Float(x)) => c.default_opacity = *x,
                    (ChatField::ActiveOpacity, Float(x)) => c.active_opacity = *x,
                    (ChatField::Filter(w), Bits(x)) => {
                        c.filter(w)?;
                        c.set_filter(w, *x);
                    }
                    _ => return None,
                }
                Some(Section::Chat)
            }
            (OptionKey::RenderPack(f), v) => {
                write_render_pack(&mut s.display, f, v)?;
                Some(Section::Display)
            }
            _ => None,
        }
    }
}

/// The quality menu's value: `off` unless the atmospheric rendering is
/// selected, then its preset (an unknown one reads as the first, as OpenAC
/// normalizes it).
fn quality_choice(selection: &RenderPackSelection) -> &'static str {
    if !selection.is_atmospheric() {
        return rp::RETAIL_PRESET_ID;
    }
    rp::preset_by_id(&selection.preset_id).map_or(rp::ATMOSPHERIC_PRESETS[0].id, |p| p.id)
}

fn read_render_pack(
    display: &ac_host::settings::DisplaySettings,
    field: RenderPackField,
) -> Option<OptionValue> {
    let selection = display.render_pack_selection();
    match field {
        RenderPackField::Quality => Some(OptionValue::Text(quality_choice(&selection).to_owned())),
        RenderPackField::Setting(index) => {
            let setting = rp::ATMOSPHERIC_SETTINGS.get(index)?;
            // Off, the rows show what the first preset would run with.
            let preset =
                rp::preset_by_id(quality_choice(&selection)).unwrap_or(&rp::ATMOSPHERIC_PRESETS[0]);
            let value = rp::resolve_setting(setting, preset, &selection);
            Some(match setting.kind {
                rp::SettingKind::Boolean => OptionValue::Bool(setting.encode(value)? > 0.5),
                rp::SettingKind::Integer | rp::SettingKind::Float => {
                    OptionValue::Float(setting.encode(value)?)
                }
                rp::SettingKind::Choice => OptionValue::Text(value.to_owned()),
            })
        }
    }
}

/// OpenAC's two menus (pack, then preset) as one: `off` selects the plain
/// renderer; a preset selects the atmospheric rendering at it, keeping the
/// user's valid values when it was already selected. A setting row stores
/// the user's value, only while the atmospheric rendering is selected.
fn write_render_pack(
    display: &mut ac_host::settings::DisplaySettings,
    field: RenderPackField,
    value: &OptionValue,
) -> Option<()> {
    let mut selection = display.render_pack_selection();
    match (field, value) {
        (RenderPackField::Quality, OptionValue::Text(id)) => {
            if id == rp::RETAIL_PRESET_ID {
                selection = RenderPackSelection::retail();
            } else {
                let preset = rp::preset_by_id(id)?;
                let kept = if selection.is_atmospheric() {
                    selection.sanitized_overrides()
                } else {
                    Vec::new()
                };
                selection = RenderPackSelection::atmospheric(preset.id);
                selection.setting_overrides = kept;
            }
        }
        (RenderPackField::Setting(index), v) => {
            if !selection.is_atmospheric() {
                return None;
            }
            let setting = rp::ATMOSPHERIC_SETTINGS.get(index)?;
            let text = match (setting.kind, v) {
                (rp::SettingKind::Boolean, OptionValue::Bool(b)) => b.to_string(),
                (rp::SettingKind::Integer, OptionValue::Float(x)) => {
                    format!("{}", x.round() as i64)
                }
                (rp::SettingKind::Float, OptionValue::Float(x)) => format!("{x}"),
                (rp::SettingKind::Choice, OptionValue::Text(t)) => t.clone(),
                _ => return None,
            };
            setting.encode(&text)?;
            selection.setting_overrides = selection.sanitized_overrides();
            selection.set_user_value(setting.id, &text);
        }
        _ => return None,
    }
    display.set_render_pack_selection(&selection);
    Some(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_runtime::character::option_id;

    #[test]
    fn every_key_reads_back_what_was_written() {
        let mut live = LiveValues::default();
        let cases = [
            (OptionKey::Audio(AudioField::Sfx), OptionValue::Float(0.25)),
            (
                OptionKey::Mixer(MixerField::VoiceCount),
                OptionValue::Float(48.0),
            ),
            (
                OptionKey::Display(DisplayField::Quality),
                OptionValue::Text("Ultra".into()),
            ),
            (
                OptionKey::Display(DisplayField::VSync),
                OptionValue::Bool(false),
            ),
            (
                OptionKey::Chat(ChatField::Filter(2)),
                OptionValue::Bits(0x1_0000_0000),
            ),
            (
                OptionKey::Character(option_id::DISABLE_MOST_WEATHER_EFFECTS),
                OptionValue::Bool(true),
            ),
        ];
        for (key, value) in cases {
            assert!(live.write(key, &value).is_some(), "{key:?}");
            assert_eq!(live.read(key), Some(value), "{key:?}");
        }
        assert_eq!(live.options1, 0x0001_0000);
    }

    /// OpenAC `ConfigOptionsPageController.BindRenderPackSection`: picking
    /// the pack selects its first preset without user values; a preset
    /// change keeps the valid user values; a setting row stores the user's
    /// value; the plain renderer drops them.
    #[test]
    fn the_atmospheric_rows_select_presets_and_store_user_values() {
        let mut live = LiveValues::default();
        let quality = OptionKey::RenderPack(RenderPackField::Quality);
        let exposure = rp::ATMOSPHERIC_SETTINGS
            .iter()
            .position(|s| s.id == "exposure")
            .unwrap();
        let exposure = OptionKey::RenderPack(RenderPackField::Setting(exposure));
        assert_eq!(live.read(quality), Some(OptionValue::Text("off".into())));
        // Off, a setting row changes nothing.
        assert_eq!(live.write(exposure, &OptionValue::Float(1.0)), None);
        assert_eq!(
            live.write(quality, &OptionValue::Text("medium".into())),
            Some(Section::Display)
        );
        assert_eq!(live.read(exposure), Some(OptionValue::Float(0.8)));
        assert_eq!(
            live.write(exposure, &OptionValue::Float(1.25)),
            Some(Section::Display)
        );
        assert_eq!(live.read(exposure), Some(OptionValue::Float(1.25)));
        // Off the 0.05 step: refused.
        assert_eq!(live.write(exposure, &OptionValue::Float(1.27)), None);
        live.write(quality, &OptionValue::Text("high".into()));
        let selection = live.settings.display.render_pack_selection();
        assert_eq!(selection.preset_id, "high");
        assert_eq!(selection.user_value("exposure"), Some("1.25"));
        live.write(quality, &OptionValue::Text("off".into()));
        assert_eq!(live.settings.display.render_pack, None);
        live.write(quality, &OptionValue::Text("low".into()));
        assert_eq!(live.read(exposure), Some(OptionValue::Float(0.8)));
        assert_eq!(
            live.write(quality, &OptionValue::Text("ultra".into())),
            None
        );
    }

    #[test]
    fn a_value_of_the_wrong_kind_changes_nothing() {
        let mut live = LiveValues::default();
        assert_eq!(
            live.write(OptionKey::Audio(AudioField::Sfx), &OptionValue::Bool(true)),
            None
        );
        assert_eq!(live, LiveValues::default());
        assert_eq!(live.read(OptionKey::Character(0x99)), None);
    }
}
