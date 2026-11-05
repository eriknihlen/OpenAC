//! Volumetric light shafts: sunlight seen in the air between the camera and
//! each pixel, dark where the shadow maps say the sun is blocked. Each
//! pixel walks its view ray in steps, asks the shadow map at each step
//! whether the sun reaches it, and lights the air by the share that is lit.

use ac_core::sky::WeatherKind;

use super::policy::{VOLUMETRIC_SHAFT_ELEVATION_RESPONSE, day_group_multiplier, volumetric_shaft};
use super::shadow::ShadowPreset;

/// The shafts' quality.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct VolumetricShaftQuality {
    /// The preset.
    pub preset: ShadowPreset,
    /// On without a user value (Low: off).
    pub enabled_by_default: bool,
    /// The shaft picture's size against the window.
    pub resolution_scale: f32,
    /// Steps along each ray.
    pub ray_march_steps: u32,
}

impl VolumetricShaftQuality {
    /// A preset's quality (OpenAC `VolumetricShaftQuality.For`).
    #[must_use]
    pub fn for_preset(preset: ShadowPreset) -> Self {
        let (enabled, scale, steps) = match preset {
            ShadowPreset::Low => (false, 0.25, 24),
            ShadowPreset::Medium => (true, 0.25, 40),
            ShadowPreset::High => (true, 0.50, 56),
        };
        Self {
            preset,
            enabled_by_default: enabled,
            resolution_scale: scale,
            ray_march_steps: steps,
        }
    }

    /// The shaft picture's size for a `width` x `height` window.
    #[must_use]
    pub fn target_size(&self, width: u32, height: u32) -> (u32, u32) {
        let side = |v: u32| ((v as f32 * self.resolution_scale).ceil() as u32).max(1);
        (side(width), side(height))
    }
}

/// Why no shafts are drawn this frame.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ShaftGateReason {
    /// They are.
    Rendered,
    /// Their strength is 0.
    DisabledByPreset,
    /// No shadow map this frame.
    NoCurrentDirectionalShadow,
    /// The player is inside.
    Indoor,
    /// The sun is not on the screen.
    SunOffScreen,
    /// Weather, elevation and day group take all of it.
    AtmosphereSuppressed,
}

/// What the shafts need from the frame.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ShaftFrame {
    /// The shadow maps were drawn this frame.
    pub shadow_current: bool,
    /// The player is outdoors.
    pub is_outdoor: bool,
    /// The sun is on the screen.
    pub sun_on_screen: bool,
    /// The sun's elevation, degrees.
    pub sun_elevation_degrees: f32,
    /// The keyframe's sun brightness.
    pub sun_brightness: f32,
    /// The active day group's index.
    pub active_day_group: i32,
    /// The weather.
    pub weather: WeatherKind,
    /// Its intensity.
    pub weather_intensity: f32,
}

/// The gate (OpenAC `VolumetricShaftRenderer.Gate`).
#[must_use]
pub fn shaft_gate(declared_strength: f32, frame: &ShaftFrame) -> ShaftGateReason {
    if declared_strength <= 0.0 {
        ShaftGateReason::DisabledByPreset
    } else if !frame.shadow_current {
        ShaftGateReason::NoCurrentDirectionalShadow
    } else if !frame.is_outdoor {
        ShaftGateReason::Indoor
    } else if !frame.sun_on_screen {
        ShaftGateReason::SunOffScreen
    } else {
        ShaftGateReason::Rendered
    }
}

/// The air's density and the shafts' strength this frame: the setting
/// scaled by the weather, the elevation curve, the sun's brightness and the
/// day group (OpenAC `VolumetricShaftRenderer.Parameters`).
#[must_use]
pub fn shaft_parameters(declared_strength: f32, frame: &ShaftFrame) -> (f32, f32) {
    let weather_target = match frame.weather {
        WeatherKind::Clear => 1.0,
        WeatherKind::Overcast => 0.18,
        WeatherKind::Rain => 0.10,
        WeatherKind::Snow => 0.16,
        WeatherKind::Storm => 0.06,
    };
    let weather = 1.0 + (weather_target - 1.0) * frame.weather_intensity.clamp(0.0, 1.0);
    let elevation = volumetric_shaft(
        &VOLUMETRIC_SHAFT_ELEVATION_RESPONSE,
        frame.sun_elevation_degrees,
        0.0,
    );
    let energy = frame.sun_brightness.clamp(0.0, 4.0);
    let day_group = day_group_multiplier(frame.active_day_group).clamp(0.0, 4.0);
    let strength = (declared_strength * weather * elevation * energy * day_group).clamp(0.0, 1.0);
    (0.035 * strength, strength)
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.App.Tests/Rendering/VolumetricShaftQualityTests.cs`
    //! (the quality table) and `Packs/VolumetricShaftRendererTests.cs` (the
    //! gate and the strength).
    use super::*;

    #[test]
    fn weak_hardware_defaults_shafts_off() {
        let low = VolumetricShaftQuality::for_preset(ShadowPreset::Low);
        let medium = VolumetricShaftQuality::for_preset(ShadowPreset::Medium);
        let high = VolumetricShaftQuality::for_preset(ShadowPreset::High);
        assert!(!low.enabled_by_default);
        assert_eq!(medium.resolution_scale, 0.25);
        assert_eq!(high.resolution_scale, 0.50);
        assert!(low.ray_march_steps < medium.ray_march_steps);
        assert!(medium.ray_march_steps < high.ray_march_steps);
        // Medium writes a quarter-size picture: 800 x 600 gives 200 x 150.
        assert_eq!(medium.target_size(800, 600), (200, 150));
    }

    fn frame() -> ShaftFrame {
        // OpenAC's test inputs: sun at 12 degrees on screen, brightness 1,
        // clear, day group 0, outdoors.
        ShaftFrame {
            shadow_current: true,
            is_outdoor: true,
            sun_on_screen: true,
            sun_elevation_degrees: 12.0,
            sun_brightness: 1.0,
            active_day_group: 0,
            weather: WeatherKind::Clear,
            weather_intensity: 0.0,
        }
    }

    #[test]
    fn a_stale_shadow_indoors_or_sun_off_screen_draws_nothing() {
        assert_eq!(shaft_gate(0.35, &frame()), ShaftGateReason::Rendered);
        assert_eq!(shaft_gate(0.0, &frame()), ShaftGateReason::DisabledByPreset);
        let stale = ShaftFrame {
            shadow_current: false,
            ..frame()
        };
        assert_eq!(
            shaft_gate(0.35, &stale),
            ShaftGateReason::NoCurrentDirectionalShadow
        );
        let indoor = ShaftFrame {
            is_outdoor: false,
            ..frame()
        };
        assert_eq!(shaft_gate(0.35, &indoor), ShaftGateReason::Indoor);
        let off = ShaftFrame {
            sun_on_screen: false,
            ..frame()
        };
        assert_eq!(shaft_gate(0.35, &off), ShaftGateReason::SunOffScreen);
    }

    #[test]
    fn weather_and_elevation_scale_the_strength() {
        let clear_low_sun = shaft_parameters(0.35, &frame()).1;
        let overcast = shaft_parameters(
            0.35,
            &ShaftFrame {
                weather: WeatherKind::Overcast,
                weather_intensity: 1.0,
                ..frame()
            },
        )
        .1;
        let noon = shaft_parameters(
            0.35,
            &ShaftFrame {
                sun_elevation_degrees: 70.0,
                ..frame()
            },
        )
        .1;
        assert!(clear_low_sun > overcast);
        assert!(clear_low_sun > noon);
        assert!(overcast > 0.0);
        // At 12 degrees the curve is full: the strength is the setting.
        assert!((clear_low_sun - 0.35).abs() < 1e-6);
        assert!((shaft_parameters(0.35, &frame()).0 - 0.035 * 0.35).abs() < 1e-7);
    }

    #[test]
    fn the_day_group_multiplier_scales_the_strength() {
        let zero = shaft_parameters(0.35, &frame()).1;
        let one = shaft_parameters(
            0.35,
            &ShaftFrame {
                active_day_group: 1,
                ..frame()
            },
        )
        .1;
        assert!((one - zero * 0.35).abs() < 1e-5);
    }
}
