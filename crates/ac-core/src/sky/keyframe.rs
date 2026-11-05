//! Sky lighting keyframes and the walk between them.
//!
//! A day group lists keyframes, each starting at a fraction of the day
//! (0 = midnight, 0.25 = dawn, 0.5 = noon, 0.75 = dusk). Between two
//! keyframes every value is blended linearly; the sun heading takes the
//! shorter way round the compass. The sun is described by a heading (compass
//! degrees, 0 = north, 90 = east) and a pitch (degrees above the horizon).

use glam::Vec3;

/// How distance fog thickens, as the sky data names it (0..=3).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum FogMode {
    /// No distance fog.
    Off = 0,
    /// Fog grows linearly from the start to the end distance.
    #[default]
    Linear = 1,
    /// Exponential fog.
    Exp = 2,
    /// Squared exponential fog.
    Exp2 = 3,
}

impl FogMode {
    /// The mode for the sky data's fog value: 1, 2 and 3 name a mode, any
    /// other value turns fog off.
    #[must_use]
    pub fn from_dat(value: u32) -> Self {
        match value {
            1 => FogMode::Linear,
            2 => FogMode::Exp,
            3 => FogMode::Exp2,
            _ => FogMode::Off,
        }
    }
}

/// Sun, ambient light and fog at one moment of the day.
///
/// Colours are linear RGB in 0..=1 and are the raw keyframe colours: the
/// brightness multipliers are applied by [`SkyKeyframe::sun_color`] and
/// [`SkyKeyframe::ambient_color`], not stored in them.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SkyKeyframe {
    /// Day fraction (0..1) this keyframe starts at.
    pub begin: f32,
    /// Sun compass heading, degrees.
    pub sun_heading_deg: f32,
    /// Sun elevation above the horizon, degrees (-90 below .. +90 overhead).
    pub sun_pitch_deg: f32,
    /// Sunlight colour.
    pub dir_color: Vec3,
    /// Sunlight brightness multiplier.
    pub dir_bright: f32,
    /// Ambient colour.
    pub amb_color: Vec3,
    /// Ambient brightness multiplier.
    pub amb_bright: f32,
    /// Fog colour.
    pub fog_color: Vec3,
    /// Kept for the built-in keyframes; the sky data has none (0) and fog
    /// uses the start and end distances.
    pub fog_density: f32,
    /// Distance where fog starts, metres.
    pub fog_start: f32,
    /// Distance where fog is complete, metres.
    pub fog_end: f32,
    /// Fog mode.
    pub fog_mode: FogMode,
}

impl SkyKeyframe {
    /// Fog start used when a keyframe gives none, metres.
    pub const DEFAULT_FOG_START: f32 = 80.0;
    /// Fog end used when a keyframe gives none, metres.
    pub const DEFAULT_FOG_END: f32 = 350.0;

    /// The sunlight colour: the keyframe colour times the length of the sun
    /// vector, which equals the sun brightness.
    #[must_use]
    pub fn sun_color(&self) -> Vec3 {
        self.dir_color * retail_sun_vector(self).length()
    }

    /// The ambient colour: the keyframe colour times the ambient brightness
    /// plus a fifth of the sun brightness, so daylight also lifts the
    /// shadows.
    #[must_use]
    pub fn ambient_color(&self) -> Vec3 {
        self.amb_color * (self.amb_bright + 0.2 * retail_sun_vector(self).length())
    }
}

/// The sun vector: pointing from the ground towards the sun, with length
/// equal to the sun brightness. x is east, y is north, z is up:
/// `bright × (cos(pitch)·sin(heading), cos(pitch)·cos(heading), sin(pitch))`.
#[must_use]
pub fn retail_sun_vector(keyframe: &SkyKeyframe) -> Vec3 {
    let h = keyframe.sun_heading_deg * (std::f32::consts::PI / 180.0);
    let p = keyframe.sun_pitch_deg * (std::f32::consts::PI / 180.0);
    let cos_p = p.cos();
    let sin_p = p.sin();
    let b = keyframe.dir_bright;
    Vec3::new(b * cos_p * h.sin(), b * cos_p * h.cos(), b * sin_p)
}

/// The unit vector from the ground towards the sun; straight up when the
/// sun has no brightness.
#[must_use]
pub fn sun_direction(keyframe: &SkyKeyframe) -> Vec3 {
    let v = retail_sun_vector(keyframe);
    let len = v.length();
    if len > 1e-6 { v / len } else { Vec3::Z }
}

/// Blend two headings (degrees) by `u` the short way round: from 350 to 10
/// walks 20 degrees forward through 0, not 340 back.
#[must_use]
pub fn shortest_angle_lerp(a_deg: f32, b_deg: f32, u: f32) -> f32 {
    let mut delta = b_deg - a_deg;
    while delta > 180.0 {
        delta -= 360.0;
    }
    while delta < -180.0 {
        delta += 360.0;
    }
    a_deg + delta * u
}

/// A day's keyframes, sorted by start time, and the blend between them.
#[derive(Debug, Clone, PartialEq)]
pub struct SkyStateProvider {
    keyframes: Vec<SkyKeyframe>,
}

/// A sky needs at least one keyframe.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct NoKeyframes;

impl std::fmt::Display for NoKeyframes {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("at least one sky keyframe is required")
    }
}

impl std::error::Error for NoKeyframes {}

impl SkyStateProvider {
    /// A sky from `keyframes` in any order; they are sorted by start time.
    ///
    /// # Errors
    /// [`NoKeyframes`] when the list is empty.
    pub fn new(mut keyframes: Vec<SkyKeyframe>) -> Result<Self, NoKeyframes> {
        if keyframes.is_empty() {
            return Err(NoKeyframes);
        }
        keyframes.sort_by(|a, b| compare_like_dotnet(a.begin, b.begin));
        Ok(Self { keyframes })
    }

    /// The built-in sky used before the region's sky is loaded: midnight,
    /// dawn, noon and dusk.
    #[must_use]
    pub fn default_sky() -> Self {
        let kf = |begin: f32,
                  heading: f32,
                  pitch: f32,
                  dir: [f32; 3],
                  amb: [f32; 3],
                  fog: [f32; 3],
                  density: f32,
                  start: f32,
                  end: f32| SkyKeyframe {
            begin,
            sun_heading_deg: heading,
            sun_pitch_deg: pitch,
            dir_color: Vec3::from(dir),
            dir_bright: 1.0,
            amb_color: Vec3::from(amb),
            amb_bright: 1.0,
            fog_color: Vec3::from(fog),
            fog_density: density,
            fog_start: start,
            fog_end: end,
            fog_mode: FogMode::Linear,
        };
        Self {
            keyframes: vec![
                // Midnight: sun below the northern horizon, deep blue.
                kf(
                    0.0,
                    0.0,
                    -30.0,
                    [0.02, 0.02, 0.08],
                    [0.05, 0.05, 0.12],
                    [0.02, 0.02, 0.05],
                    0.004,
                    30.0,
                    180.0,
                ),
                // Dawn: sun rising in the east, warm.
                kf(
                    0.25,
                    90.0,
                    0.0,
                    [1.0, 0.7, 0.4],
                    [0.4, 0.35, 0.3],
                    [0.8, 0.55, 0.4],
                    0.002,
                    60.0,
                    260.0,
                ),
                // Noon: sun high in the south, near white.
                kf(
                    0.5,
                    180.0,
                    70.0,
                    [1.0, 0.98, 0.95],
                    [0.5, 0.5, 0.55],
                    [0.7, 0.75, 0.85],
                    0.0008,
                    120.0,
                    500.0,
                ),
                // Dusk: sun setting in the west, red.
                kf(
                    0.75,
                    270.0,
                    0.0,
                    [0.95, 0.4, 0.25],
                    [0.35, 0.25, 0.25],
                    [0.85, 0.45, 0.35],
                    0.002,
                    60.0,
                    260.0,
                ),
            ],
        }
    }

    /// Number of keyframes.
    #[must_use]
    pub fn keyframe_count(&self) -> usize {
        self.keyframes.len()
    }

    /// The keyframes, sorted by start time.
    #[must_use]
    pub fn keyframes(&self) -> &[SkyKeyframe] {
        &self.keyframes
    }

    /// The sky at day fraction `t` (wrapped into 0..1): the last keyframe
    /// starting at or before `t` blended towards the next one (the first
    /// one after the last). The result's `begin` is the wrapped `t`; its fog
    /// mode is the earlier keyframe's.
    #[must_use]
    pub fn interpolate(&self, t: f32) -> SkyKeyframe {
        let t = (f64::from(t) - f64::from(t).floor()) as f32;

        let mut k1_index = self.keyframes.len() - 1;
        for (i, k) in self.keyframes.iter().enumerate() {
            if k.begin <= t {
                k1_index = i;
            } else {
                break;
            }
        }
        let k2_index = (k1_index + 1) % self.keyframes.len();
        let k1 = &self.keyframes[k1_index];
        let k2 = &self.keyframes[k2_index];

        let k1_begin = k1.begin;
        let mut k2_begin = k2.begin;
        if k2_begin <= k1_begin {
            k2_begin += 1.0;
        }
        let mut t_wrapped = t;
        if t_wrapped < k1_begin {
            t_wrapped += 1.0;
        }

        let span = f32_max_like_dotnet(1e-6, k2_begin - k1_begin);
        let u = ((t_wrapped - k1_begin) / span).clamp(0.0, 1.0);

        SkyKeyframe {
            begin: t,
            sun_heading_deg: shortest_angle_lerp(k1.sun_heading_deg, k2.sun_heading_deg, u),
            sun_pitch_deg: lerp(k1.sun_pitch_deg, k2.sun_pitch_deg, u),
            dir_color: lerp3(k1.dir_color, k2.dir_color, u),
            dir_bright: lerp(k1.dir_bright, k2.dir_bright, u),
            amb_color: lerp3(k1.amb_color, k2.amb_color, u),
            amb_bright: lerp(k1.amb_bright, k2.amb_bright, u),
            fog_color: lerp3(k1.fog_color, k2.fog_color, u),
            fog_density: lerp(k1.fog_density, k2.fog_density, u),
            fog_start: lerp(k1.fog_start, k2.fog_start, u),
            fog_end: lerp(k1.fog_end, k2.fog_end, u),
            fog_mode: k1.fog_mode,
        }
    }
}

fn lerp(a: f32, b: f32, u: f32) -> f32 {
    a + (b - a) * u
}

/// Component-wise blend in the form the reference client's vector type
/// computes on hardware with fused multiply-add: `a·(1−u) + b·u` with the
/// first product and the sum rounded once.
fn lerp3(a: Vec3, b: Vec3, u: f32) -> Vec3 {
    let one = |a: f32, b: f32| a.mul_add(1.0 - u, b * u);
    Vec3::new(one(a.x, b.x), one(a.y, b.y), one(a.z, b.z))
}

/// The larger value; a NaN in either argument gives NaN.
fn f32_max_like_dotnet(a: f32, b: f32) -> f32 {
    if a.is_nan() || b.is_nan() {
        f32::NAN
    } else if a > b {
        a
    } else {
        b
    }
}

/// Orders floats as the reference client's sort does: NaN first and equal
/// to itself, then ascending.
pub(crate) fn compare_like_dotnet(a: f32, b: f32) -> std::cmp::Ordering {
    use std::cmp::Ordering;
    match (a.is_nan(), b.is_nan()) {
        (true, true) => Ordering::Equal,
        (true, false) => Ordering::Less,
        (false, true) => Ordering::Greater,
        (false, false) => a.partial_cmp(&b).unwrap_or(Ordering::Equal),
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `SkyStateTests`, plus exact values printed by OpenAC's
    //! `SkyStateProvider` (marked "OpenAC").
    use super::*;

    fn kf(heading: f32, pitch: f32, dir: Vec3, dir_bright: f32, amb_bright: f32) -> SkyKeyframe {
        SkyKeyframe {
            begin: 0.5,
            sun_heading_deg: heading,
            sun_pitch_deg: pitch,
            dir_color: dir,
            dir_bright,
            amb_color: Vec3::ONE,
            amb_bright,
            fog_color: Vec3::ONE,
            fog_density: 0.0,
            fog_start: SkyKeyframe::DEFAULT_FOG_START,
            fog_end: SkyKeyframe::DEFAULT_FOG_END,
            fog_mode: FogMode::Linear,
        }
    }

    fn bits(v: Vec3) -> [u32; 3] {
        v.to_array().map(f32::to_bits)
    }

    #[test]
    fn default_has_four_keyframes() {
        assert_eq!(SkyStateProvider::default_sky().keyframe_count(), 4);
    }

    #[test]
    fn at_an_exact_keyframe_returns_its_data() {
        let noon = SkyStateProvider::default_sky().interpolate(0.5);
        assert!((0.9..=1.1).contains(&noon.sun_color().x));
        assert!((0.9..=1.1).contains(&noon.sun_color().y));
    }

    #[test]
    fn between_keyframes_lerps_the_raw_inputs() {
        let sky = SkyStateProvider::default_sky();
        let (dawn, noon, mid) = (
            sky.interpolate(0.25),
            sky.interpolate(0.5),
            sky.interpolate(0.375),
        );
        let low = dawn.dir_color.y.min(noon.dir_color.y);
        let high = dawn.dir_color.y.max(noon.dir_color.y);
        assert!((low..=high).contains(&mid.dir_color.y));
    }

    #[test]
    fn sun_vector_at_zenith_has_the_brightness_as_length() {
        let v = retail_sun_vector(&kf(0.0, 90.0, Vec3::ONE, 1.5, 0.3));
        assert!((1.49..=1.51).contains(&v.length()));
    }

    #[test]
    fn sun_vector_length_always_equals_the_brightness() {
        let horizon = kf(0.0, 0.0, Vec3::ONE, 2.0, 1.0);
        assert!((1.99..=2.01).contains(&retail_sun_vector(&horizon).length()));

        let dawn = kf(90.0, 0.9, Vec3::ONE, 0.224, 0.40);
        let v = retail_sun_vector(&dawn);
        assert!((0.223..=0.225).contains(&v.x));
        assert!((-0.001..=0.001).contains(&v.y));
        assert!((0.003..=0.004).contains(&v.z));
        assert!((0.223..=0.225).contains(&v.length()));
    }

    #[test]
    fn sun_color_uses_the_sun_vector_length() {
        let c = kf(0.0, 90.0, Vec3::splat(0.5), 2.0, 0.3).sun_color();
        for v in c.to_array() {
            assert!((0.99..=1.01).contains(&v));
        }
    }

    #[test]
    fn ambient_is_boosted_by_a_fifth_of_the_sun_vector_length() {
        // (1,1,1) × (0.4 + 0.2 × 1) = 0.6.
        let c = kf(0.0, 0.0, Vec3::ONE, 1.0, 0.4).ambient_color();
        assert!((0.59..=0.61).contains(&c.x));
    }

    #[test]
    fn wraps_across_midnight() {
        let k = SkyStateProvider::default_sky().interpolate(0.01);
        assert!(!k.sun_color().x.is_nan());
        assert!(!k.ambient_color().x.is_nan());
    }

    #[test]
    fn sun_direction_is_a_unit_vector() {
        let d = sun_direction(&kf(180.0, 70.0, Vec3::ONE, 1.0, 1.0));
        assert!((0.99..=1.01).contains(&d.length()));
    }

    #[test]
    fn shortest_angle_lerp_crosses_north() {
        // OpenAC: 350 -> 10 at 0.5 gives 360; 10 -> 350 at 0.25 gives 5.
        assert_eq!(shortest_angle_lerp(350.0, 10.0, 0.5), 360.0);
        assert_eq!(shortest_angle_lerp(10.0, 350.0, 0.25), 5.0);
    }

    #[test]
    fn interpolation_matches_openac_bit_for_bit() {
        let sky = SkyStateProvider::default_sky();

        // OpenAC Interpolate(0.1f): 40 % from midnight to dawn.
        let k = sky.interpolate(0.1);
        assert_eq!(k.begin.to_bits(), 0x3DCC_CCCD);
        assert_eq!(k.sun_heading_deg, 36.0);
        assert_eq!(k.sun_pitch_deg, -18.0);
        assert_eq!(bits(k.dir_color), [0x3ED2_F1AA, 0x3E95_8106, 0x3E54_FDF5]);
        assert_eq!(bits(k.amb_color), [0x3E42_8F5D, 0x3E2E_147B, 0x3E44_9BA6]);
        assert_eq!(bits(k.fog_color), [0x3EA9_FBE8, 0x3E6D_9169, 0x3E42_8F5D]);
        assert_eq!(k.fog_density.to_bits(), 0x3B51_B718);
        assert_eq!((k.fog_start, k.fog_end), (42.0, 212.0));
        assert_eq!(k.fog_mode, FogMode::Linear);

        // OpenAC Interpolate(0.6f): 40 % from noon to dusk.
        let k = sky.interpolate(0.6);
        assert_eq!(k.sun_heading_deg, 216.0);
        assert_eq!(k.sun_pitch_deg.to_bits(), 0x4227_FFFE);
        assert_eq!(bits(k.dir_color), [0x3F7A_E148, 0x3F3F_7CED, 0x3F2B_851D]);
        assert_eq!(bits(k.amb_color), [0x3EE1_47AE, 0x3ECC_CCCC, 0x3EDC_28F5]);
        assert_eq!(k.fog_start.to_bits(), 0x42BF_FFFF);
        assert_eq!(k.fog_end.to_bits(), 0x43C9_FFFF);

        // OpenAC Interpolate(0.9f): between dusk and the next midnight.
        let k = sky.interpolate(0.9);
        assert_eq!(k.sun_heading_deg, 324.0);
        assert_eq!(k.sun_pitch_deg.to_bits(), 0xC18F_FFFE);
        assert_eq!(bits(k.dir_color), [0x3EC8_B43C, 0x3E30_20C7, 0x3E17_8D51]);
        assert_eq!(k.fog_start.to_bits(), 0x4228_0001);

        // OpenAC Interpolate(1.3f) and (-0.2f): wrapped into the day first.
        let k = sky.interpolate(1.3);
        assert_eq!(k.begin.to_bits(), 0x3E99_9998);
        assert_eq!(k.sun_heading_deg.to_bits(), 0x42D7_FFFE);
        assert_eq!(bits(k.dir_color), [0x3F80_0000, 0x3F41_8936, 0x3F02_8F5A]);
        let k = sky.interpolate(-0.2);
        assert_eq!(k.begin.to_bits(), 0x3F4C_CCCD);
        assert_eq!(k.sun_heading_deg, 288.0);
        assert_eq!(bits(k.dir_color), [0x3F43_9580, 0x3EA5_E354, 0x3E5D_2F1A]);
    }

    #[test]
    fn derived_colours_match_openac() {
        let sky = SkyStateProvider::default_sky();
        let close = |a: Vec3, b: [f32; 3]| (a - Vec3::from(b)).abs().max_element() < 2e-6;

        // OpenAC: SunColor, AmbientColor and the sun vector at 0.375 and 0.75.
        let k = sky.interpolate(0.375);
        assert!(close(k.sun_color(), [1.0, 0.84000003, 0.675]));
        assert!(close(k.ambient_color(), [0.54, 0.51000005, 0.51000005]));
        assert!(close(
            retail_sun_vector(&k),
            [0.579228, -0.579228, 0.57357645]
        ));
        let k = sky.interpolate(0.75);
        assert!(close(k.ambient_color(), [0.42000002, 0.3, 0.3]));
        assert!(close(sun_direction(&k), [-1.0, 1.1924881e-8, 0.0]));
    }
}
