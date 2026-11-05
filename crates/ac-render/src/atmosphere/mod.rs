//! The atmospheric rendering's rules, apart from the GPU: what the player's
//! selection resolves to (quality, post settings, shadow maps, shafts,
//! wind), the policy curves, the colour path, automatic quality, the shadow
//! cascades and gate, the shafts' gate and strength, and the wind model.
//!
//! The renderer's GPU side is `renderer::atmosphere`. With the option off
//! (the default) none of this runs and the picture is the game's.

pub mod auto_quality;
pub mod color;
pub mod policy;
pub mod shadow;
pub mod volumetric;
pub mod wind;

use ac_core::sky::{AtmosphereSnapshot, SkyKeyframe, WeatherKind, sun_direction};
use ac_host::settings::RenderPackSelection;
use ac_host::settings::render_pack::{
    self as rp, AtmosphericPreset, QualitySemantic, preset_by_id, resolved_number, setting_id,
};
use glam::{Mat4, Vec2, Vec3};

use auto_quality::QualityLevel;
use shadow::{ShadowPreset, ShadowQuality};
use volumetric::VolumetricShaftQuality;
use wind::WindSettings;

/// The post-process settings (OpenAC `AtmosphericPostProcessSettings`).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PostSettings {
    /// Bloom strength.
    pub bloom_strength: f32,
    /// How much of the filmic curve (0: plain clamp).
    pub filmic_strength: f32,
    /// Exposure.
    pub exposure: f32,
    /// Colour saturation.
    pub saturation: f32,
    /// Colour contrast.
    pub contrast: f32,
    /// Vignette strength.
    pub vignette_strength: f32,
    /// Sun-ray strength.
    pub sun_ray_strength: f32,
}

/// Automatic quality's range.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct AutoRange {
    /// The level it starts at.
    pub initial: QualityLevel,
    /// The highest it may go.
    pub maximum: QualityLevel,
}

/// What a selection runs with.
#[derive(Debug, Clone, PartialEq)]
pub struct AtmosphereConfig {
    /// The preset in effect (Auto runs a level's).
    pub preset: &'static AtmosphericPreset,
    /// The post settings.
    pub post: PostSettings,
    /// The shadow strength setting.
    pub shadow_strength: f32,
    /// The shadow maps' quality.
    pub shadow: ShadowQuality,
    /// The shafts' quality.
    pub volumetric: VolumetricShaftQuality,
    /// The shafts' strength setting (0: none).
    pub volumetric_strength: f32,
    /// The wind.
    pub wind: WindSettings,
    /// Automatic quality, when on.
    pub automatic: Option<AutoRange>,
    /// The user's values (for the level automatic quality moves to).
    pub selection: RenderPackSelection,
}

fn shadow_preset_of(preset: &AtmosphericPreset) -> ShadowPreset {
    match preset.semantic {
        QualitySemantic::Low => ShadowPreset::Low,
        QualitySemantic::High => ShadowPreset::High,
        _ => ShadowPreset::Medium,
    }
}

impl AtmosphereConfig {
    /// What `selection` runs: `Ok(None)` for the plain renderer (or a pack
    /// this renderer does not have), an error when the user's values are
    /// invalid (OpenAC then falls back to the plain renderer).
    ///
    /// # Errors
    ///
    /// A sentence naming the bad user value.
    pub fn resolve(selection: &RenderPackSelection) -> Result<Option<Self>, String> {
        if !selection.is_atmospheric() {
            return Ok(None);
        }
        rp::validate_user_overrides(selection)?;
        let Some(selected) = preset_by_id(&selection.preset_id) else {
            return Err(format!(
                "Render pack '{}' has no quality preset '{}'.",
                selection.pack_id, selection.preset_id
            ));
        };
        // Automatic quality: the Auto preset, or the setting on another.
        let automatic_setting =
            resolved_number(setting_id::AUTOMATIC_QUALITY, selected, selection, 0.0) > 0.5;
        let (preset, automatic) = match QualityLevel::of(selected.semantic) {
            None => (
                QualityLevel::Medium.preset(),
                Some(AutoRange {
                    initial: QualityLevel::Medium,
                    maximum: QualityLevel::High,
                }),
            ),
            Some(level) => (
                selected,
                automatic_setting.then_some(AutoRange {
                    initial: level,
                    maximum: QualityLevel::High,
                }),
            ),
        };
        let mut config = Self::for_preset(preset, selection);
        config.automatic = automatic;
        Ok(Some(config))
    }

    /// The configuration at `preset` with `selection`'s user values.
    #[must_use]
    pub fn for_preset(preset: &'static AtmosphericPreset, selection: &RenderPackSelection) -> Self {
        let n = |id: &str, fallback: f32| resolved_number(id, preset, selection, fallback);
        let post = PostSettings {
            bloom_strength: n(setting_id::BLOOM_STRENGTH, 0.65),
            filmic_strength: n(setting_id::FILMIC_STRENGTH, 1.0),
            exposure: n(setting_id::EXPOSURE, 1.0),
            saturation: n(setting_id::GRADE_SATURATION, 1.0),
            contrast: n(setting_id::GRADE_CONTRAST, 1.0),
            vignette_strength: n(
                setting_id::VIGNETTE_STRENGTH,
                color::DEFAULT_VIGNETTE_STRENGTH_FALLBACK,
            ),
            sun_ray_strength: n(setting_id::SUN_RAY_STRENGTH, 0.55),
        };
        let base = ShadowQuality::for_preset(shadow_preset_of(preset));
        let reach = n(
            setting_id::SUN_SHADOW_REACH_METRES,
            base.maximum_reach_meters,
        );
        // The filter setting is a choice: its position 0, 1, 2 is 1, 9, 25
        // taps, a radius of 0, 1, 2 texels.
        let taps = n(
            setting_id::SUN_SHADOW_PCF_TAPS,
            base.pcf_radius_texels as f32,
        );
        let shadow = ShadowQuality {
            cascade_count: preset.shadow_cascades as usize,
            map_resolution: preset.shadow_resolution,
            maximum_reach_meters: reach.clamp(1.0, 10_000.0),
            pcf_radius_texels: taps.round().clamp(0.0, 2.0) as u32,
            approximate_depth_map_bytes: u64::from(preset.shadow_cascades)
                * u64::from(preset.shadow_resolution).pow(2)
                * 4,
            ..base
        };
        let mut volumetric = VolumetricShaftQuality::for_preset(shadow_preset_of(preset));
        volumetric.resolution_scale = preset.volumetric_scale.clamp(0.0625, 1.0);
        volumetric.ray_march_steps = n(
            setting_id::VOLUMETRIC_RAY_STEPS,
            volumetric.ray_march_steps as f32,
        )
        .round()
        .clamp(8.0, 64.0) as u32;
        let wind = WindSettings {
            enabled: n(setting_id::WIND_ENABLED, 1.0) > 0.5,
            strength: n(setting_id::WIND_STRENGTH, 1.0),
            direction_degrees: n(setting_id::WIND_DIRECTION_DEGREES, 225.0),
            lean_metres: n(setting_id::WIND_LEAN_METRES, 0.25),
            branch_metres: n(setting_id::WIND_BRANCH_METRES, 0.15),
            flutter_metres: n(setting_id::WIND_FLUTTER_METRES, 0.05),
            canopy_height_metres: n(setting_id::WIND_CANOPY_HEIGHT_METRES, 8.0),
        };
        Self {
            preset,
            post,
            shadow_strength: n(setting_id::SUN_SHADOW_STRENGTH, 0.72),
            shadow,
            volumetric,
            volumetric_strength: n(setting_id::VOLUMETRIC_STRENGTH, 0.35).max(0.0),
            wind,
            automatic: None,
            selection: selection.clone(),
        }
    }

    /// The same configuration at automatic quality's `level`.
    #[must_use]
    pub fn at_level(&self, level: QualityLevel) -> Self {
        let mut c = Self::for_preset(level.preset(), &self.selection);
        c.automatic = self.automatic;
        c
    }
}

/// What the atmospheric passes need from the frame (OpenAC
/// `AtmosphericFrameInputs`).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct FrameInputs {
    /// The sun's place on the screen (0..1, y down), or (-1, -1).
    pub sun_screen_uv: Vec2,
    /// The sun is on the screen.
    pub sun_on_screen: bool,
    /// The keyframe's sun elevation, degrees.
    pub sun_elevation_degrees: f32,
    /// The keyframe's sun colour.
    pub sun_color: Vec3,
    /// From the ground toward the sun.
    pub sun_direction: Vec3,
    /// The keyframe's sun brightness.
    pub sun_brightness: f32,
    /// Clip space back to the drawing frame.
    pub inverse_view_projection: Mat4,
    /// The active day group's index (-1: none).
    pub active_day_group: i32,
    /// The weather.
    pub weather: WeatherKind,
    /// Its intensity.
    pub weather_intensity: f32,
    /// Seconds since the last frame.
    pub delta_seconds: f32,
    /// The window's size.
    pub viewport: (u32, u32),
    /// The player is outdoors.
    pub is_outdoor: bool,
}

impl FrameInputs {
    /// The inputs for a frame seen from `eye` through `view_projection`
    /// (OpenAC `AtmosphericFrameInputState.Publish`): the sun's screen
    /// place from a point 10 km toward it.
    #[must_use]
    #[allow(clippy::too_many_arguments)]
    pub fn publish(
        keyframe: &SkyKeyframe,
        atmosphere: &AtmosphereSnapshot,
        eye: Vec3,
        view_projection: Mat4,
        active_day_group: i32,
        delta_seconds: f32,
        viewport: (u32, u32),
        is_outdoor: bool,
    ) -> Self {
        let direction = sun_direction(keyframe);
        let clip = view_projection * (eye + direction * 10_000.0).extend(1.0);
        let finite =
            clip.x.is_finite() && clip.y.is_finite() && clip.w.is_finite() && clip.w > 1e-5;
        let uv = if finite {
            Vec2::new(clip.x / clip.w * 0.5 + 0.5, 0.5 - clip.y / clip.w * 0.5)
        } else {
            Vec2::splat(-1.0)
        };
        let on_screen = finite && (0.0..=1.0).contains(&uv.x) && (0.0..=1.0).contains(&uv.y);
        let inverse = if view_projection.determinant() != 0.0 {
            view_projection.inverse()
        } else {
            Mat4::IDENTITY
        };
        Self {
            sun_screen_uv: uv,
            sun_on_screen: on_screen,
            sun_elevation_degrees: keyframe.sun_pitch_deg,
            sun_color: keyframe.sun_color(),
            sun_direction: direction,
            sun_brightness: keyframe.dir_bright,
            inverse_view_projection: inverse,
            active_day_group,
            weather: atmosphere.kind,
            weather_intensity: atmosphere.intensity.clamp(0.0, 1.0),
            delta_seconds,
            viewport,
            is_outdoor,
        }
    }

    /// The sun rays' and bloom's sun term (OpenAC
    /// `AtmosphericPostProcessGraph.EvaluateSunPolicy`): none indoors or
    /// with the sun off the screen.
    #[must_use]
    pub fn sun_policy(&self) -> f32 {
        if !self.is_outdoor || !self.sun_on_screen {
            return 0.0;
        }
        (policy::ray(
            &policy::SUN_ELEVATION_RESPONSE,
            self.sun_elevation_degrees,
            1.0,
        ) * policy::day_group_multiplier(self.active_day_group)
            * policy::weather_policy(self.weather, self.weather_intensity))
        .clamp(0.0, 4.0)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_core::sky::{EnvironOverride, FogMode};

    #[test]
    fn the_plain_renderer_and_unknown_packs_run_nothing() {
        assert_eq!(
            AtmosphereConfig::resolve(&RenderPackSelection::retail()),
            Ok(None)
        );
        let other = RenderPackSelection {
            pack_id: "shiny".into(),
            ..RenderPackSelection::atmospheric("low")
        };
        assert_eq!(AtmosphereConfig::resolve(&other), Ok(None));
    }

    /// OpenAC `AtmosphericPostProcessGraphTests.SemanticPresetResourcesAndSettingsDriveShadowAndVolumetricQuality`
    /// (the settings half; the resource half is the presets' table).
    #[test]
    fn presets_and_user_values_drive_shadow_and_shaft_quality() {
        let mut selection = RenderPackSelection::atmospheric("medium");
        selection.set_user_value("sun-shadow-reach-metres", "96");
        selection.set_user_value("sun-shadow-pcf-taps", "25");
        selection.set_user_value("volumetric-ray-steps", "64");
        let c = AtmosphereConfig::resolve(&selection).unwrap().unwrap();
        assert_eq!(c.shadow.cascade_count, 3);
        assert_eq!(c.shadow.map_resolution, 1536);
        assert_eq!(c.shadow.maximum_reach_meters, 96.0);
        assert_eq!(c.shadow.pcf_radius_texels, 2);
        assert_eq!(c.volumetric.ray_march_steps, 64);
        assert_eq!(c.volumetric.resolution_scale, 0.25);
        assert_eq!(c.automatic, None);
        let low = AtmosphereConfig::resolve(&RenderPackSelection::atmospheric("low"))
            .unwrap()
            .unwrap();
        assert_eq!(
            (low.shadow.cascade_count, low.shadow.map_resolution),
            (2, 768)
        );
        assert_eq!(low.shadow.maximum_reach_meters, 72.0);
        assert_eq!(low.shadow.pcf_radius_texels, 0);
        assert_eq!(low.volumetric_strength, 0.0, "Low leaves the shafts off");
        assert_eq!(low.wind.flutter_metres, 0.0);
        assert_eq!(low.post.sun_ray_strength, 0.4);
        assert_eq!(low.post.exposure, 0.8);
    }

    /// OpenAC `RenderPackController.PlanActivation` with
    /// `TryResolveAutomaticRange`: Auto runs Medium and may climb to High;
    /// the setting on another preset starts there.
    #[test]
    fn auto_runs_medium_up_to_high() {
        let auto = AtmosphereConfig::resolve(&RenderPackSelection::atmospheric("auto"))
            .unwrap()
            .unwrap();
        assert_eq!(auto.preset.id, "medium");
        assert_eq!(
            auto.automatic,
            Some(AutoRange {
                initial: QualityLevel::Medium,
                maximum: QualityLevel::High
            })
        );
        let mut low = RenderPackSelection::atmospheric("low");
        low.set_user_value("automatic-quality", "true");
        let c = AtmosphereConfig::resolve(&low).unwrap().unwrap();
        assert_eq!(c.preset.id, "low");
        assert_eq!(c.automatic.unwrap().initial, QualityLevel::Low);
        assert_eq!(c.at_level(QualityLevel::High).shadow.cascade_count, 4);
    }

    /// OpenAC `RenderPackSettingResolution.ValidateUserOverrides`: a bad
    /// user value refuses the whole selection.
    #[test]
    fn a_bad_user_value_refuses_the_selection() {
        let mut s = RenderPackSelection::atmospheric("high");
        s.set_user_value("exposure", "9");
        assert!(AtmosphereConfig::resolve(&s).is_err());
        let mut s = RenderPackSelection::atmospheric("high");
        s.set_user_value("sparkle", "1");
        assert!(AtmosphereConfig::resolve(&s).is_err());
    }

    fn atmosphere(kind: WeatherKind, intensity: f32) -> AtmosphereSnapshot {
        AtmosphereSnapshot {
            kind,
            intensity,
            fog_color: Vec3::splat(0.4),
            fog_start: 80.0,
            fog_end: 350.0,
            fog_mode: FogMode::Linear,
            lightning_flash: 0.0,
            environ_override: EnvironOverride::None,
        }
    }

    /// OpenAC `AtmosphericPostProcessGraphTests.AuthoredElevationDayGroupAndVisibilityGateSunEffects`.
    #[test]
    fn the_sun_policy_follows_elevation_day_group_visibility_and_weather() {
        let dawn = FrameInputs {
            sun_screen_uv: Vec2::new(0.5, 0.4),
            sun_on_screen: true,
            sun_elevation_degrees: 4.0,
            sun_color: Vec3::ONE,
            sun_direction: Vec3::Z,
            sun_brightness: 1.0,
            inverse_view_projection: Mat4::IDENTITY,
            active_day_group: 0,
            weather: WeatherKind::Clear,
            weather_intensity: 0.0,
            delta_seconds: 1.0 / 60.0,
            viewport: (1280, 720),
            is_outdoor: true,
        };
        assert!((dawn.sun_policy() - 1.0).abs() < 1e-3);
        let dusk = FrameInputs {
            active_day_group: 1,
            ..dawn
        };
        assert!((dusk.sun_policy() - 0.35).abs() < 1e-3);
        let noon = FrameInputs {
            sun_elevation_degrees: 55.0,
            ..dawn
        };
        assert_eq!(noon.sun_policy(), 0.0);
        let behind = FrameInputs {
            sun_on_screen: false,
            ..dawn
        };
        assert_eq!(behind.sun_policy(), 0.0);
        let overcast = FrameInputs {
            weather: WeatherKind::Overcast,
            weather_intensity: 1.0,
            ..dawn
        };
        assert!((overcast.sun_policy() - 0.18).abs() < 1e-3);
        let indoor = FrameInputs {
            is_outdoor: false,
            ..dawn
        };
        assert_eq!(indoor.sun_policy(), 0.0);
        let _ = atmosphere(WeatherKind::Clear, 1.0);
    }

    #[test]
    fn the_sun_lands_on_the_screen_where_the_camera_looks() {
        let keyframe = SkyKeyframe {
            begin: 0.0,
            sun_heading_deg: 0.0,
            sun_pitch_deg: 10.0,
            dir_color: Vec3::ONE,
            dir_bright: 1.0,
            amb_color: Vec3::splat(0.2),
            amb_bright: 0.4,
            fog_color: Vec3::splat(0.4),
            fog_density: 0.0,
            fog_start: 80.0,
            fog_end: 350.0,
            fog_mode: FogMode::Linear,
        };
        let eye = Vec3::new(10.0, 20.0, 5.0);
        let toward = sun_direction(&keyframe);
        let view = glam::camera::rh::view::look_at_mat4(eye, eye + toward, Vec3::Z);
        let projection =
            glam::camera::rh::proj::directx::perspective(1.0, 16.0 / 9.0, 0.1, 50_000.0);
        let inputs = FrameInputs::publish(
            &keyframe,
            &atmosphere(WeatherKind::Clear, 1.0),
            eye,
            projection * view,
            0,
            0.016,
            (1280, 720),
            true,
        );
        assert!(inputs.sun_on_screen);
        assert!((inputs.sun_screen_uv - Vec2::splat(0.5)).length() < 1e-3);
        let away = glam::camera::rh::view::look_at_mat4(eye, eye - toward, Vec3::Z);
        let behind = FrameInputs::publish(
            &keyframe,
            &atmosphere(WeatherKind::Clear, 1.0),
            eye,
            projection * away,
            0,
            0.016,
            (1280, 720),
            true,
        );
        assert!(!behind.sun_on_screen);
    }
}
