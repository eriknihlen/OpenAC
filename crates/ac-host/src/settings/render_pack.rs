//! The renderer's enhancement selection in the display settings, and the
//! built-in atmospheric options it selects.
//!
//! The settings file keeps OpenAC's `renderPack` object (`packId`,
//! `packVersion`, `presetId`, `settingOverrides`) so a settings file moves
//! between the two clients. There are no installable packs here: the only
//! enhancement is the renderer's own atmospheric rendering (OpenAC's
//! built-in "Atmospheric Rendering" pack), selected by its pack id with a
//! quality preset, and tuned by the user's setting overrides. A setting's
//! value is the user's override when it is valid, else the preset's, else
//! the declared default.

use crate::json::{Member, Node, Value};

use super::DisplaySettings;

/// The plain renderer (no enhancement).
pub const RETAIL_PACK_ID: &str = "retail";
/// The plain renderer's only preset.
pub const RETAIL_PRESET_ID: &str = "off";
/// The atmospheric rendering's id (OpenAC's built-in pack id).
pub const ATMOSPHERIC_PACK_ID: &str = "acdream.atmospheric";
/// The atmospheric rendering's version as the settings file records it.
pub const ATMOSPHERIC_PACK_VERSION: &str = "1.0.0";

/// Which enhancement the renderer runs, with its preset and the user's
/// setting values (OpenAC `RenderPackSelectionSettings`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RenderPackSelection {
    /// The pack id (`retail`: none).
    pub pack_id: String,
    /// The pack version the selection was made with.
    pub pack_version: Option<String>,
    /// The quality preset id.
    pub preset_id: String,
    /// The user's values by setting id, in the order set.
    pub setting_overrides: Vec<(String, String)>,
}

impl Default for RenderPackSelection {
    fn default() -> Self {
        Self::retail()
    }
}

impl RenderPackSelection {
    /// The plain renderer.
    #[must_use]
    pub fn retail() -> Self {
        Self {
            pack_id: RETAIL_PACK_ID.to_owned(),
            pack_version: None,
            preset_id: RETAIL_PRESET_ID.to_owned(),
            setting_overrides: Vec::new(),
        }
    }

    /// The atmospheric rendering at `preset_id`, without user values.
    #[must_use]
    pub fn atmospheric(preset_id: &str) -> Self {
        Self {
            pack_id: ATMOSPHERIC_PACK_ID.to_owned(),
            pack_version: Some(ATMOSPHERIC_PACK_VERSION.to_owned()),
            preset_id: preset_id.to_owned(),
            setting_overrides: Vec::new(),
        }
    }

    /// The plain renderer is selected (ids compare ignoring case).
    #[must_use]
    pub fn is_retail(&self) -> bool {
        self.pack_id.eq_ignore_ascii_case(RETAIL_PACK_ID)
    }

    /// The atmospheric rendering is selected.
    #[must_use]
    pub fn is_atmospheric(&self) -> bool {
        self.pack_id.eq_ignore_ascii_case(ATMOSPHERIC_PACK_ID)
    }

    /// The user's value for setting `id` (ignoring case), if any.
    #[must_use]
    pub fn user_value(&self, id: &str) -> Option<&str> {
        self.setting_overrides
            .iter()
            .find(|(k, _)| k == id)
            .or_else(|| {
                self.setting_overrides
                    .iter()
                    .find(|(k, _)| k.eq_ignore_ascii_case(id))
            })
            .map(|(_, v)| v.as_str())
    }

    /// Set the user's value for `id` (replacing any under that id).
    pub fn set_user_value(&mut self, id: &str, value: &str) {
        self.setting_overrides
            .retain(|(k, _)| !k.eq_ignore_ascii_case(id));
        self.setting_overrides
            .push((id.to_owned(), value.to_owned()));
    }

    /// Keep only the user values the atmospheric settings declare and
    /// accept, under their declared ids (OpenAC `SanitizeOverrides`).
    #[must_use]
    pub fn sanitized_overrides(&self) -> Vec<(String, String)> {
        self.setting_overrides
            .iter()
            .filter_map(|(id, value)| {
                let setting = setting_by_id(id)?;
                setting
                    .encode(value)
                    .map(|_| (setting.id.to_owned(), value.clone()))
            })
            .collect()
    }

    /// Read OpenAC's `renderPack` object; `None` when it is not one (no
    /// pack or preset id, a non-string override), which means the plain
    /// renderer (OpenAC `SettingsStore.ReadRenderPackSelection`).
    #[must_use]
    pub fn from_node(node: &Node) -> Option<Self> {
        let Value::Object(members) = &node.value else {
            return None;
        };
        let text = |name: &str| match super::member(members, name).map(|n| &n.value) {
            Some(Value::String(s)) => Some(s.clone()),
            _ => None,
        };
        let pack_id = text("packId").unwrap_or_default();
        let preset_id = text("presetId").unwrap_or_default();
        if pack_id.trim().is_empty() || preset_id.trim().is_empty() {
            return None;
        }
        let mut setting_overrides = Vec::new();
        match super::member(members, "settingOverrides").map(|n| &n.value) {
            None => {}
            Some(Value::Object(overrides)) => {
                for m in overrides {
                    let Value::String(v) = &m.value.value else {
                        return None;
                    };
                    setting_overrides.push((m.name.clone(), v.clone()));
                }
            }
            Some(_) => return None,
        }
        Some(Self {
            pack_id,
            pack_version: text("packVersion"),
            preset_id,
            setting_overrides,
        })
    }

    /// The `renderPack` object as written (OpenAC `BuildRenderPackObject`:
    /// keys in ordinal order, overrides sorted by id).
    #[must_use]
    pub fn to_node(&self) -> Node {
        let mut overrides: Vec<(String, String)> = self.setting_overrides.clone();
        overrides.sort_by(|a, b| a.0.cmp(&b.0));
        overrides.dedup_by(|a, b| a.0 == b.0);
        let overrides = overrides
            .into_iter()
            .map(|(k, v)| Member::made(&k, Value::String(v)))
            .collect();
        Node::made(Value::Object(vec![
            Member::made("packId", Value::String(self.pack_id.clone())),
            Member::made(
                "packVersion",
                self.pack_version.clone().map_or(Value::Null, Value::String),
            ),
            Member::made("presetId", Value::String(self.preset_id.clone())),
            Member::made("settingOverrides", Value::Object(overrides)),
        ]))
    }
}

impl DisplaySettings {
    /// The renderer's enhancement selection (the plain renderer when the
    /// file names none or names it badly).
    #[must_use]
    pub fn render_pack_selection(&self) -> RenderPackSelection {
        self.render_pack
            .as_ref()
            .and_then(RenderPackSelection::from_node)
            .unwrap_or_default()
    }

    /// Select `selection`; the plain renderer without user values is
    /// stored as no selection (the file then writes OpenAC's retail
    /// object).
    pub fn set_render_pack_selection(&mut self, selection: &RenderPackSelection) {
        let plain = selection.is_retail()
            && selection.preset_id == RETAIL_PRESET_ID
            && selection.setting_overrides.is_empty();
        self.render_pack = if plain {
            None
        } else {
            Some(selection.to_node())
        };
    }
}

/// What kind of value a setting holds.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SettingKind {
    /// `true` or `false`.
    Boolean,
    /// A whole number.
    Integer,
    /// A number.
    Float,
    /// One of the listed texts.
    Choice,
}

/// One atmospheric setting (OpenAC `RenderSettingDeclaration`).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SettingDeclaration {
    /// Stable id (the settings file's key).
    pub id: &'static str,
    /// The options window's caption.
    pub display_name: &'static str,
    /// Its kind.
    pub kind: SettingKind,
    /// Its value without preset or user value.
    pub default: &'static str,
    /// Low end (numbers).
    pub minimum: Option<f64>,
    /// High end (numbers).
    pub maximum: Option<f64>,
    /// The step values sit on, counted from the low end (numbers).
    pub step: Option<f64>,
    /// The choices (choice settings).
    pub choices: &'static [&'static str],
}

/// Numbers above this cannot be held exactly in an `f32`.
const EXACT_FLOAT_INTEGER_LIMIT: i64 = 16_777_216;

impl SettingDeclaration {
    /// The value as the renderer reads it, or `None` when it is not one the
    /// setting allows: one or zero for a switch, the number for a number
    /// (inside the bounds, on the step), the position of the choice for a
    /// choice (OpenAC `RenderPackSettingValueCodec.TryEncode`).
    #[must_use]
    pub fn encode(&self, value: &str) -> Option<f32> {
        match self.kind {
            SettingKind::Boolean => {
                // .NET bool.TryParse: "true"/"false" ignoring case and
                // surrounding white space.
                let v = value.trim();
                if v.eq_ignore_ascii_case("true") {
                    Some(1.0)
                } else if v.eq_ignore_ascii_case("false") {
                    Some(0.0)
                } else {
                    None
                }
            }
            SettingKind::Integer => {
                let integer: i64 = value.trim().parse().ok()?;
                if !(-EXACT_FLOAT_INTEGER_LIMIT..=EXACT_FLOAT_INTEGER_LIMIT).contains(&integer) {
                    return None;
                }
                let v = integer as f64;
                (self.within_bounds(v) && self.on_step(v)).then_some(integer as f32)
            }
            SettingKind::Float => {
                let v: f64 = value.trim().parse().ok()?;
                if !v.is_finite() || v < -f64::from(f32::MAX) || v > f64::from(f32::MAX) {
                    return None;
                }
                if !self.within_bounds(v) || !self.on_step(v) {
                    return None;
                }
                let encoded = v as f32;
                encoded.is_finite().then_some(encoded)
            }
            SettingKind::Choice => self
                .choices
                .iter()
                .position(|c| *c == value)
                .map(|i| i as f32),
        }
    }

    fn within_bounds(&self, v: f64) -> bool {
        self.minimum.is_none_or(|m| v >= m) && self.maximum.is_none_or(|m| v <= m)
    }

    fn on_step(&self, v: f64) -> bool {
        let Some(step) = self.step else {
            return true;
        };
        if !step.is_finite() || step <= 0.0 {
            return false;
        }
        let quotient = (v - self.minimum.unwrap_or(0.0)) / step;
        if !quotient.is_finite() {
            return false;
        }
        let tolerance = (quotient.abs() * 1e-7).max(1e-7);
        (quotient - quotient.round()).abs() <= tolerance
    }
}

const fn float(
    id: &'static str,
    display_name: &'static str,
    default: &'static str,
    minimum: f64,
    maximum: f64,
    step: f64,
) -> SettingDeclaration {
    SettingDeclaration {
        id,
        display_name,
        kind: SettingKind::Float,
        default,
        minimum: Some(minimum),
        maximum: Some(maximum),
        step: Some(step),
        choices: &[],
    }
}

const fn integer(
    id: &'static str,
    display_name: &'static str,
    default: &'static str,
    minimum: f64,
    maximum: f64,
    step: f64,
) -> SettingDeclaration {
    SettingDeclaration {
        kind: SettingKind::Integer,
        ..float(id, display_name, default, minimum, maximum, step)
    }
}

const fn boolean(
    id: &'static str,
    display_name: &'static str,
    default: &'static str,
) -> SettingDeclaration {
    SettingDeclaration {
        id,
        display_name,
        kind: SettingKind::Boolean,
        default,
        minimum: None,
        maximum: None,
        step: None,
        choices: &[],
    }
}

/// Setting ids the renderer reads.
pub mod setting_id {
    #![allow(missing_docs)]
    pub const BLOOM_STRENGTH: &str = "bloom-strength";
    pub const FILMIC_STRENGTH: &str = "filmic-strength";
    pub const EXPOSURE: &str = "exposure";
    pub const GRADE_SATURATION: &str = "grade-saturation";
    pub const GRADE_CONTRAST: &str = "grade-contrast";
    pub const VIGNETTE_STRENGTH: &str = "vignette-strength";
    pub const SUN_RAY_STRENGTH: &str = "sun-ray-strength";
    pub const SUN_SHADOW_STRENGTH: &str = "sun-shadow-strength";
    pub const SUN_SHADOW_REACH_METRES: &str = "sun-shadow-reach-metres";
    pub const SUN_SHADOW_PCF_TAPS: &str = "sun-shadow-pcf-taps";
    pub const VOLUMETRIC_STRENGTH: &str = "volumetric-strength";
    pub const VOLUMETRIC_RAY_STEPS: &str = "volumetric-ray-steps";
    pub const AUTOMATIC_QUALITY: &str = "automatic-quality";
    pub const WIND_ENABLED: &str = "wind-enabled";
    pub const WIND_STRENGTH: &str = "wind-strength";
    pub const WIND_DIRECTION_DEGREES: &str = "wind-direction-degrees";
    pub const WIND_LEAN_METRES: &str = "wind-lean-metres";
    pub const WIND_BRANCH_METRES: &str = "wind-branch-metres";
    pub const WIND_FLUTTER_METRES: &str = "wind-flutter-metres";
    pub const WIND_CANOPY_HEIGHT_METRES: &str = "wind-canopy-height-metres";
}

/// The atmospheric settings in OpenAC's declaration order, with its
/// captions, defaults, bounds and steps (OpenAC
/// `BuiltInAtmosphericRenderPack.Settings`).
pub const ATMOSPHERIC_SETTINGS: [SettingDeclaration; 20] = {
    use setting_id as s;
    [
        float(s::BLOOM_STRENGTH, "Bloom strength", "0.65", 0.0, 2.0, 0.05),
        float(
            s::FILMIC_STRENGTH,
            "Filmic tonemap strength",
            "1",
            0.0,
            1.0,
            0.05,
        ),
        float(s::EXPOSURE, "Exposure", "0.8", 0.25, 4.0, 0.05),
        float(
            s::GRADE_SATURATION,
            "Colour saturation",
            "1",
            0.0,
            2.0,
            0.05,
        ),
        float(s::GRADE_CONTRAST, "Colour contrast", "1", 0.5, 2.0, 0.05),
        float(
            s::VIGNETTE_STRENGTH,
            "Vignette strength",
            "0.245",
            0.0,
            1.0,
            0.005,
        ),
        float(
            s::SUN_RAY_STRENGTH,
            "Sun-ray strength",
            "0.55",
            0.0,
            2.0,
            0.05,
        ),
        float(
            s::SUN_SHADOW_STRENGTH,
            "Directional-shadow strength",
            "0.72",
            0.0,
            1.0,
            0.02,
        ),
        integer(
            s::SUN_SHADOW_REACH_METRES,
            "Directional-shadow reach (metres)",
            "240",
            16.0,
            240.0,
            1.0,
        ),
        SettingDeclaration {
            id: s::SUN_SHADOW_PCF_TAPS,
            display_name: "Directional-shadow filter taps",
            kind: SettingKind::Choice,
            default: "9",
            minimum: None,
            maximum: None,
            step: None,
            choices: &["1", "9", "25"],
        },
        float(
            s::VOLUMETRIC_STRENGTH,
            "Volumetric-shaft strength",
            "0.35",
            0.0,
            1.0,
            0.01,
        ),
        integer(
            s::VOLUMETRIC_RAY_STEPS,
            "Volumetric ray-march steps",
            "40",
            8.0,
            64.0,
            8.0,
        ),
        boolean(s::AUTOMATIC_QUALITY, "Automatic quality", "false"),
        boolean(s::WIND_ENABLED, "Foliage wind", "true"),
        float(
            s::WIND_STRENGTH,
            "Foliage wind strength",
            "1",
            0.0,
            2.0,
            0.05,
        ),
        float(
            s::WIND_DIRECTION_DEGREES,
            "Foliage wind direction (degrees)",
            "225",
            0.0,
            360.0,
            5.0,
        ),
        float(
            s::WIND_LEAN_METRES,
            "Foliage lean amplitude (metres)",
            "0.25",
            0.0,
            1.0,
            0.01,
        ),
        float(
            s::WIND_BRANCH_METRES,
            "Foliage branch-swing amplitude (metres)",
            "0.15",
            0.0,
            1.0,
            0.01,
        ),
        float(
            s::WIND_FLUTTER_METRES,
            "Foliage flutter amplitude (metres)",
            "0.05",
            0.0,
            0.5,
            0.005,
        ),
        float(
            s::WIND_CANOPY_HEIGHT_METRES,
            "Foliage canopy height (metres)",
            "8",
            2.0,
            30.0,
            0.5,
        ),
    ]
};

/// The setting declared under `id` (ignoring case).
#[must_use]
pub fn setting_by_id(id: &str) -> Option<&'static SettingDeclaration> {
    ATMOSPHERIC_SETTINGS
        .iter()
        .find(|s| s.id.eq_ignore_ascii_case(id))
}

/// What a quality preset is for.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum QualitySemantic {
    /// Weak hardware.
    Low,
    /// The default.
    Medium,
    /// Strong hardware.
    High,
    /// Chosen and changed by the frame cost.
    Automatic,
}

/// One quality preset of the atmospheric rendering (OpenAC
/// `RenderQualityPreset` as the built-in pack declares it).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct AtmosphericPreset {
    /// Stable id.
    pub id: &'static str,
    /// Caption.
    pub display_name: &'static str,
    /// What it is for.
    pub semantic: QualitySemantic,
    /// Side of each shadow map, pixels.
    pub shadow_resolution: u32,
    /// Shadow cascades (map layers).
    pub shadow_cascades: u32,
    /// Bloom targets' size against the window.
    pub post_scale: f32,
    /// Sun mask and sun-ray targets' size against the window.
    pub ray_scale: f32,
    /// The light-shaft target's size against the window.
    pub volumetric_scale: f32,
    /// The pack's video memory ceiling at 1920 x 1080, bytes.
    pub max_resident_bytes: u64,
    /// Added GPU time, median and 99th percentile, milliseconds.
    pub gpu_p50_ms: f64,
    /// See `gpu_p50_ms`.
    pub gpu_p99_ms: f64,
    /// Added CPU time, median and 99th percentile, milliseconds.
    pub cpu_p50_ms: f64,
    /// See `cpu_p50_ms`.
    pub cpu_p99_ms: f64,
    /// OpenAC renders this preset's cascades in one multiview pass.
    pub multiview_cascades: bool,
    /// The preset's values for settings (before the user's).
    pub setting_overrides: &'static [(&'static str, &'static str)],
}

const MIB: u64 = 1024 * 1024;

/// The presets in OpenAC's order: Low, Medium, High, Auto (OpenAC
/// `BuiltInAtmosphericRenderPack.QualityPresets`).
pub const ATMOSPHERIC_PRESETS: [AtmosphericPreset; 4] = {
    use setting_id as s;
    [
        AtmosphericPreset {
            id: "low",
            display_name: "Low",
            semantic: QualitySemantic::Low,
            shadow_resolution: 768,
            shadow_cascades: 2,
            post_scale: 0.25,
            ray_scale: 0.25,
            volumetric_scale: 0.25,
            max_resident_bytes: 64 * MIB,
            gpu_p50_ms: 2.0,
            gpu_p99_ms: 3.0,
            cpu_p50_ms: 0.15,
            cpu_p99_ms: 0.50,
            multiview_cascades: true,
            setting_overrides: &[
                (s::AUTOMATIC_QUALITY, "false"),
                (s::VOLUMETRIC_STRENGTH, "0"),
                (s::VOLUMETRIC_RAY_STEPS, "24"),
                (s::SUN_SHADOW_STRENGTH, "0.72"),
                (s::SUN_SHADOW_REACH_METRES, "72"),
                (s::SUN_SHADOW_PCF_TAPS, "1"),
                (s::SUN_RAY_STRENGTH, "0.4"),
                (s::WIND_FLUTTER_METRES, "0"),
            ],
        },
        AtmosphericPreset {
            id: "medium",
            display_name: "Medium",
            semantic: QualitySemantic::Medium,
            shadow_resolution: 1536,
            shadow_cascades: 3,
            post_scale: 0.5,
            ray_scale: 0.5,
            volumetric_scale: 0.25,
            max_resident_bytes: 128 * MIB,
            gpu_p50_ms: 3.25,
            gpu_p99_ms: 4.50,
            cpu_p50_ms: 0.25,
            cpu_p99_ms: 0.75,
            multiview_cascades: false,
            setting_overrides: &[
                (s::AUTOMATIC_QUALITY, "false"),
                (s::VOLUMETRIC_STRENGTH, "0.35"),
                (s::VOLUMETRIC_RAY_STEPS, "40"),
                (s::SUN_SHADOW_STRENGTH, "0.72"),
                (s::SUN_SHADOW_REACH_METRES, "144"),
                (s::SUN_SHADOW_PCF_TAPS, "9"),
                (s::SUN_RAY_STRENGTH, "0.55"),
                (s::WIND_FLUTTER_METRES, "0.05"),
            ],
        },
        AtmosphericPreset {
            id: "high",
            display_name: "High",
            semantic: QualitySemantic::High,
            shadow_resolution: 2048,
            shadow_cascades: 4,
            post_scale: 0.5,
            ray_scale: 0.5,
            volumetric_scale: 0.5,
            max_resident_bytes: 256 * MIB,
            gpu_p50_ms: 4.50,
            gpu_p99_ms: 6.00,
            cpu_p50_ms: 0.35,
            cpu_p99_ms: 1.00,
            multiview_cascades: false,
            setting_overrides: &[
                (s::AUTOMATIC_QUALITY, "false"),
                (s::VOLUMETRIC_STRENGTH, "0.35"),
                (s::VOLUMETRIC_RAY_STEPS, "56"),
                (s::SUN_SHADOW_STRENGTH, "0.72"),
                (s::SUN_SHADOW_REACH_METRES, "240"),
                (s::SUN_SHADOW_PCF_TAPS, "25"),
                (s::SUN_RAY_STRENGTH, "0.55"),
                (s::WIND_FLUTTER_METRES, "0.05"),
            ],
        },
        AtmosphericPreset {
            id: "auto",
            display_name: "Auto",
            semantic: QualitySemantic::Automatic,
            shadow_resolution: 1536,
            shadow_cascades: 3,
            post_scale: 0.5,
            ray_scale: 0.5,
            volumetric_scale: 0.25,
            max_resident_bytes: 128 * MIB,
            gpu_p50_ms: 3.25,
            gpu_p99_ms: 4.50,
            cpu_p50_ms: 0.25,
            cpu_p99_ms: 0.75,
            multiview_cascades: false,
            setting_overrides: &[
                (s::AUTOMATIC_QUALITY, "true"),
                (s::VOLUMETRIC_STRENGTH, "0.35"),
                (s::VOLUMETRIC_RAY_STEPS, "40"),
                (s::SUN_SHADOW_STRENGTH, "0.72"),
                (s::SUN_SHADOW_REACH_METRES, "144"),
                (s::SUN_SHADOW_PCF_TAPS, "9"),
                (s::SUN_RAY_STRENGTH, "0.55"),
            ],
        },
    ]
};

/// The preset with `id` (ignoring case).
#[must_use]
pub fn preset_by_id(id: &str) -> Option<&'static AtmosphericPreset> {
    ATMOSPHERIC_PRESETS
        .iter()
        .find(|p| p.id.eq_ignore_ascii_case(id))
}

/// The preset for `semantic` (Low, Medium, High or Auto).
#[must_use]
pub fn preset_for(semantic: QualitySemantic) -> &'static AtmosphericPreset {
    ATMOSPHERIC_PRESETS
        .iter()
        .find(|p| p.semantic == semantic)
        .unwrap_or(&ATMOSPHERIC_PRESETS[1])
}

/// `setting`'s value under `preset` and the user's `selection`: the user's
/// value when the setting accepts it, else the preset's, else the default
/// (OpenAC `RenderPackSettingResolution.Resolve` with the options window's
/// validity check).
#[must_use]
pub fn resolve_setting<'a>(
    setting: &SettingDeclaration,
    preset: &'a AtmosphericPreset,
    selection: &'a RenderPackSelection,
) -> &'a str {
    if let Some(user) = selection.user_value(setting.id)
        && setting.encode(user).is_some()
    {
        return user;
    }
    preset
        .setting_overrides
        .iter()
        .find(|(id, _)| id.eq_ignore_ascii_case(setting.id))
        .map_or(setting.default, |(_, v)| v)
}

/// Why the user's values cannot run: an id the atmospheric settings do not
/// declare, or a value its setting refuses (OpenAC
/// `RenderPackSettingResolution.ValidateUserOverrides`; OpenAC then falls
/// back to the plain renderer).
///
/// # Errors
///
/// The first offending value, as a sentence for the log.
pub fn validate_user_overrides(selection: &RenderPackSelection) -> Result<(), String> {
    for (id, value) in &selection.setting_overrides {
        let Some(setting) = setting_by_id(id) else {
            return Err(format!(
                "Render pack '{}' has a user override for unknown setting '{id}'.",
                selection.pack_id
            ));
        };
        if setting.encode(value).is_none() {
            return Err(format!(
                "Render pack '{}' user override '{id}' has invalid {:?} value '{value}'.",
                selection.pack_id, setting.kind
            ));
        }
    }
    Ok(())
}

/// `id`'s value as the renderer reads it (see [`resolve_setting`] and
/// [`SettingDeclaration::encode`]); `fallback` for an unknown id or a
/// value the setting refuses.
#[must_use]
pub fn resolved_number(
    id: &str,
    preset: &AtmosphericPreset,
    selection: &RenderPackSelection,
    fallback: f32,
) -> f32 {
    let Some(setting) = setting_by_id(id) else {
        return fallback;
    };
    setting
        .encode(resolve_setting(setting, preset, selection))
        .filter(|v| v.is_finite())
        .unwrap_or(fallback)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_declared_defaults_are_values_their_settings_accept() {
        for s in &ATMOSPHERIC_SETTINGS {
            assert!(s.encode(s.default).is_some(), "{}", s.id);
        }
        for p in &ATMOSPHERIC_PRESETS {
            for (id, value) in p.setting_overrides {
                let s = setting_by_id(id).expect("declared");
                assert!(s.encode(value).is_some(), "{} {id}={value}", p.id);
            }
        }
    }

    /// OpenAC `RenderPackSettingValueCodec.TryEncode`.
    #[test]
    fn values_are_checked_against_kind_bounds_and_step() {
        let exposure = setting_by_id("exposure").unwrap();
        assert_eq!(exposure.encode("0.8"), Some(0.8));
        assert_eq!(exposure.encode("0.25"), Some(0.25));
        assert_eq!(exposure.encode("0.2"), None, "below the low end");
        assert_eq!(exposure.encode("0.81"), None, "off the 0.05 step");
        assert_eq!(exposure.encode("abc"), None);
        let steps = setting_by_id("volumetric-ray-steps").unwrap();
        assert_eq!(steps.encode("40"), Some(40.0));
        assert_eq!(steps.encode("44"), None, "off the 8 step from 8");
        assert_eq!(steps.encode("40.0"), None, "an integer is written whole");
        let taps = setting_by_id("sun-shadow-pcf-taps").unwrap();
        assert_eq!(
            taps.encode("25"),
            Some(2.0),
            "a choice encodes its position"
        );
        assert_eq!(taps.encode("4"), None);
        let wind = setting_by_id("wind-enabled").unwrap();
        assert_eq!(wind.encode("True"), Some(1.0));
        assert_eq!(wind.encode("false"), Some(0.0));
        assert_eq!(wind.encode("yes"), None);
    }

    /// OpenAC `RenderPackSettingResolution.Resolve`: the user's value, else
    /// the preset's, else the default.
    #[test]
    fn a_setting_resolves_user_then_preset_then_default() {
        let low = preset_by_id("low").unwrap();
        let mut selection = RenderPackSelection::atmospheric("low");
        let flutter = setting_by_id("wind-flutter-metres").unwrap();
        let bloom = setting_by_id("bloom-strength").unwrap();
        assert_eq!(resolve_setting(flutter, low, &selection), "0");
        assert_eq!(resolve_setting(bloom, low, &selection), "0.65");
        selection.set_user_value("Bloom-Strength", "1.2");
        assert_eq!(resolve_setting(bloom, low, &selection), "1.2");
        // A value the setting refuses falls through to the preset.
        selection.set_user_value("wind-flutter-metres", "0.7");
        assert_eq!(resolve_setting(flutter, low, &selection), "0");
        assert_eq!(
            resolved_number("sun-shadow-reach-metres", low, &selection, 0.0),
            72.0
        );
    }

    #[test]
    fn the_selection_round_trips_through_the_settings_object() {
        let mut selection = RenderPackSelection::atmospheric("high");
        selection.set_user_value("exposure", "1.1");
        selection.set_user_value("bloom-strength", "0.4");
        let back = RenderPackSelection::from_node(&selection.to_node()).unwrap();
        assert_eq!(back.pack_id, ATMOSPHERIC_PACK_ID);
        assert_eq!(back.pack_version.as_deref(), Some("1.0.0"));
        assert_eq!(back.preset_id, "high");
        // Written sorted by id.
        assert_eq!(
            back.setting_overrides,
            vec![
                ("bloom-strength".to_owned(), "0.4".to_owned()),
                ("exposure".to_owned(), "1.1".to_owned()),
            ]
        );
        let mut display = DisplaySettings::default();
        assert!(display.render_pack_selection().is_retail());
        display.set_render_pack_selection(&selection);
        assert_eq!(display.render_pack_selection().preset_id, "high");
        display.set_render_pack_selection(&RenderPackSelection::retail());
        assert_eq!(display.render_pack, None);
    }

    /// OpenAC `SettingsStore.ReadRenderPackSelection`: a missing id or
    /// preset, or a non-string override, falls back to the plain renderer.
    #[test]
    fn a_malformed_object_means_the_plain_renderer() {
        let read = |text: &str| {
            let node = crate::json::parse(text.as_bytes()).unwrap();
            RenderPackSelection::from_node(&node)
        };
        assert!(read(r#"{ "packId": "acdream.atmospheric" }"#).is_none());
        assert!(read(r#"{ "packId": " ", "presetId": "low" }"#).is_none());
        assert!(
            read(r#"{ "packId": "x", "presetId": "low", "settingOverrides": { "a": 1 } }"#)
                .is_none()
        );
        assert!(read(r#"{ "packId": "x", "presetId": "low", "settingOverrides": [] }"#).is_none());
        let ok = read(r#"{ "packId": "x", "presetId": "low", "packVersion": 3 }"#).unwrap();
        assert_eq!(ok.pack_version, None);
    }

    #[test]
    fn sanitizing_keeps_only_declared_valid_values_under_their_ids() {
        let mut selection = RenderPackSelection::atmospheric("medium");
        selection.set_user_value("EXPOSURE", "1.5");
        selection.set_user_value("unknown", "1");
        selection.set_user_value("bloom-strength", "9");
        assert_eq!(
            selection.sanitized_overrides(),
            vec![("exposure".to_owned(), "1.5".to_owned())]
        );
    }
}
