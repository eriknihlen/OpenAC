//! Weather kind, the server's coloured-fog override, the lightning flash and
//! the fog the scene is drawn with.
//!
//! The weather kind follows the active day group's name ("Cloudy" and the
//! like read as overcast). Before any day group drives it, a seeded roll per
//! game day picks one. Fog itself always comes from the sky keyframe; the
//! weather kind is informational.

use glam::Vec3;

use super::DotNetRandom;
use super::keyframe::{FogMode, SkyKeyframe};

/// Kinds of weather.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum WeatherKind {
    /// Clear sky.
    #[default]
    Clear = 0,
    /// Clouded over.
    Overcast = 1,
    /// Rain.
    Rain = 2,
    /// Snow.
    Snow = 3,
    /// Thunderstorm.
    Storm = 4,
}

impl WeatherKind {
    /// The kind's name.
    #[must_use]
    pub fn name(self) -> &'static str {
        match self {
            WeatherKind::Clear => "Clear",
            WeatherKind::Overcast => "Overcast",
            WeatherKind::Rain => "Rain",
            WeatherKind::Snow => "Snow",
            WeatherKind::Storm => "Storm",
        }
    }
}

/// The server's fog override (an admin environment change, values
/// 0..=6). It stays until the server sends another.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum EnvironOverride {
    /// No override: the sky's fog.
    #[default]
    None = 0x00,
    /// Red fog.
    RedFog = 0x01,
    /// Blue fog.
    BlueFog = 0x02,
    /// White fog.
    WhiteFog = 0x03,
    /// Green fog.
    GreenFog = 0x04,
    /// Black fog.
    BlackFog = 0x05,
    /// A second, slightly red, black fog.
    BlackFog2 = 0x06,
}

impl EnvironOverride {
    /// The override for a wire value, if it is one (0..=6).
    #[must_use]
    pub fn from_raw(value: u32) -> Option<Self> {
        Some(match value {
            0x00 => EnvironOverride::None,
            0x01 => EnvironOverride::RedFog,
            0x02 => EnvironOverride::BlueFog,
            0x03 => EnvironOverride::WhiteFog,
            0x04 => EnvironOverride::GreenFog,
            0x05 => EnvironOverride::BlackFog,
            0x06 => EnvironOverride::BlackFog2,
            _ => return None,
        })
    }

    /// The override's name.
    #[must_use]
    pub fn name(self) -> &'static str {
        match self {
            EnvironOverride::None => "None",
            EnvironOverride::RedFog => "RedFog",
            EnvironOverride::BlueFog => "BlueFog",
            EnvironOverride::WhiteFog => "WhiteFog",
            EnvironOverride::GreenFog => "GreenFog",
            EnvironOverride::BlackFog => "BlackFog",
            EnvironOverride::BlackFog2 => "BlackFog2",
        }
    }

    /// The fog colour the override paints.
    #[must_use]
    pub fn fog_color(self) -> Vec3 {
        match self {
            EnvironOverride::RedFog => Vec3::new(0.60, 0.05, 0.05),
            EnvironOverride::BlueFog => Vec3::new(0.08, 0.15, 0.60),
            EnvironOverride::WhiteFog => Vec3::new(0.90, 0.90, 0.92),
            EnvironOverride::GreenFog => Vec3::new(0.08, 0.55, 0.12),
            EnvironOverride::BlackFog => Vec3::new(0.02, 0.02, 0.02),
            EnvironOverride::BlackFog2 => Vec3::new(0.04, 0.01, 0.01),
            EnvironOverride::None => Vec3::ONE,
        }
    }
}

/// What the scene's fog and flash are this frame.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct AtmosphereSnapshot {
    /// Weather kind (informational).
    pub kind: WeatherKind,
    /// Weather intensity, 0..1 (always 1).
    pub intensity: f32,
    /// Fog colour.
    pub fog_color: Vec3,
    /// Fog start, metres.
    pub fog_start: f32,
    /// Fog end, metres.
    pub fog_end: f32,
    /// Fog mode.
    pub fog_mode: FogMode,
    /// Lightning flash, 0..1.
    pub lightning_flash: f32,
    /// The server's fog override.
    pub environ_override: EnvironOverride,
}

/// One world's weather state.
#[derive(Debug, Clone)]
pub struct WeatherSystem {
    kind: WeatherKind,
    flash_level: f32,
    flash_age: f32,
    environ_override: EnvironOverride,
    rolled_day_index: i32,
    externally_driven: bool,
}

impl Default for WeatherSystem {
    fn default() -> Self {
        Self::new()
    }
}

impl WeatherSystem {
    /// Seconds a weather change takes (kept for reference; changes are
    /// immediate).
    pub const TRANSITION_SECONDS: f32 = 10.0;

    /// Flash decay rate, per second (time constant 0.2 s).
    const FLASH_DECAY: f32 = 1.0 / 0.200;
    /// The flash holds full brightness this long, seconds.
    const FLASH_PEAK_HOLD_S: f32 = 0.05;
    /// "Never rolled" marker for the rolled day.
    const NOT_ROLLED: i32 = i32::MIN;
    /// "Forced; never roll again" marker for the rolled day.
    const FORCED: i32 = i32::MAX;

    /// Clear weather, no override, no flash.
    #[must_use]
    pub fn new() -> Self {
        Self {
            kind: WeatherKind::Clear,
            flash_level: 0.0,
            flash_age: 0.0,
            environ_override: EnvironOverride::None,
            rolled_day_index: Self::NOT_ROLLED,
            externally_driven: false,
        }
    }

    /// The current weather kind.
    #[must_use]
    pub fn kind(&self) -> WeatherKind {
        self.kind
    }

    /// The server's last fog override (kept between messages).
    #[must_use]
    pub fn environ_override(&self) -> EnvironOverride {
        self.environ_override
    }

    /// Install the server's fog override.
    pub fn set_environ_override(&mut self, value: EnvironOverride) {
        self.environ_override = value;
    }

    /// Force a kind (debug command); the daily roll stops for good.
    pub fn force_weather(&mut self, kind: WeatherKind) {
        self.kind = kind;
        self.rolled_day_index = Self::FORCED;
    }

    /// Follow the active day group: its name decides the kind, and the
    /// daily roll stops.
    pub fn set_kind_from_day_group_name(&mut self, day_group_name: &str) {
        self.externally_driven = true;
        let mapped = map_day_group_name_to_kind(day_group_name);
        if mapped != self.kind {
            self.kind = mapped;
        }
    }

    /// Advance by `dt_seconds` on game day `day_index`: roll the day's kind
    /// when the day changed (unless a day group or a forced kind drives
    /// it), and decay the flash.
    pub fn tick(&mut self, day_index: i32, dt_seconds: f32) {
        if !self.externally_driven
            && day_index != self.rolled_day_index
            && self.rolled_day_index != Self::FORCED
        {
            self.rolled_day_index = day_index;
            let new_kind = roll_kind(day_index);
            if new_kind != self.kind {
                self.kind = new_kind;
            }
        }

        if self.flash_level > 0.0 {
            self.flash_age += dt_seconds;
            if self.flash_age < Self::FLASH_PEAK_HOLD_S {
                self.flash_level = 1.0;
            } else {
                self.flash_level =
                    (-(self.flash_age - Self::FLASH_PEAK_HOLD_S) * Self::FLASH_DECAY).exp();
            }
            if self.flash_level < 1e-3 {
                self.flash_level = 0.0;
            }
        }
    }

    /// Start a lightning flash at full brightness.
    pub fn trigger_flash(&mut self) {
        self.flash_level = 1.0;
        self.flash_age = 0.0;
    }

    /// The fog and flash for keyframe `kf`: the keyframe's fog, its colour
    /// replaced by the override's when there is one, and fog off when the
    /// player turned distance fog off.
    #[must_use]
    pub fn snapshot(&self, kf: &SkyKeyframe, disable_distance_fog: bool) -> AtmosphereSnapshot {
        let mut fog_color = kf.fog_color;
        let mut fog_mode = kf.fog_mode;
        if self.environ_override != EnvironOverride::None {
            fog_color = self.environ_override.fog_color();
        }
        if disable_distance_fog {
            fog_mode = FogMode::Off;
        }
        AtmosphereSnapshot {
            kind: self.kind,
            intensity: 1.0,
            fog_color,
            fog_start: kf.fog_start,
            fog_end: kf.fog_end,
            fog_mode,
            lightning_flash: self.flash_level,
            environ_override: self.environ_override,
        }
    }
}

/// Day group names that mention storm, snow, rain, cloud, overcast, dark or
/// fog (any case) are overcast; every other name is clear.
#[must_use]
pub fn map_day_group_name_to_kind(name: &str) -> WeatherKind {
    if name.trim().is_empty() {
        return WeatherKind::Clear;
    }
    let lc = name.to_lowercase();
    let overcast = ["storm", "snow", "rain", "cloud", "overcast", "dark", "fog"];
    if overcast.iter().any(|word| lc.contains(word)) {
        WeatherKind::Overcast
    } else {
        WeatherKind::Clear
    }
}

/// The seeded roll for a game day: 60 % clear, 20 % overcast, 12 % rain,
/// 5 % snow, 3 % storm.
fn roll_kind(day_index: i32) -> WeatherKind {
    let seed = (day_index as u32).wrapping_mul(0x9E37_79B1) as i32;
    let r = DotNetRandom::new(seed).next_double();
    if r < 0.60 {
        WeatherKind::Clear
    } else if r < 0.80 {
        WeatherKind::Overcast
    } else if r < 0.92 {
        WeatherKind::Rain
    } else if r < 0.97 {
        WeatherKind::Snow
    } else {
        WeatherKind::Storm
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `WeatherSystemTests`, plus roll and flash values printed by
    //! OpenAC's `WeatherSystem` (marked "OpenAC").
    use super::*;
    use crate::sky::SkyStateProvider;

    fn noon() -> SkyKeyframe {
        SkyStateProvider::default_sky().interpolate(0.5)
    }

    #[test]
    fn the_roll_matches_openac_day_by_day() {
        // OpenAC: kinds for days 0..40 and the clear count over 1000 days.
        let expected = [
            1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 2, 0, 1, 0, 0, 2, 0, 2, 0, 4, 2, 0, 1, 0, 3, 3, 0, 1, 0,
            2, 0, 0, 1, 0, 0, 0, 0, 1, 0, 1,
        ];
        let mut sys = WeatherSystem::new();
        let mut clear = 0;
        for d in 0..1000 {
            sys.tick(d, 0.0);
            if d < 40 {
                assert_eq!(sys.kind() as i32, expected[d as usize], "day {d}");
            }
            if sys.kind() == WeatherKind::Clear {
                clear += 1;
            }
        }
        assert_eq!(clear, 599);

        let mut sys = WeatherSystem::new();
        let mut kinds = Vec::new();
        for d in [-1, -5, i32::MAX - 1, 123_456, -123_456] {
            sys.tick(d, 0.0);
            kinds.push(sys.kind() as i32);
        }
        assert_eq!(kinds, [0, 1, 0, 0, 0]);
    }

    #[test]
    fn the_roll_is_deterministic_and_mostly_clear() {
        let (mut a, mut b) = (WeatherSystem::new(), WeatherSystem::new());
        for d in 0..100 {
            a.tick(d, 100.0);
            b.tick(d, 100.0);
            assert_eq!(a.kind(), b.kind());
        }
    }

    #[test]
    fn snapshot_always_passes_the_keyframe_fog_whatever_the_kind() {
        let kf = noon();
        for kind in [
            WeatherKind::Clear,
            WeatherKind::Overcast,
            WeatherKind::Rain,
            WeatherKind::Snow,
            WeatherKind::Storm,
        ] {
            let mut sys = WeatherSystem::new();
            sys.force_weather(kind);
            sys.tick(1, 100.0);
            let snap = sys.snapshot(&kf, false);
            assert_eq!(snap.kind, kind);
            assert_eq!(snap.fog_start, kf.fog_start);
            assert_eq!(snap.fog_end, kf.fog_end);
            assert_eq!(snap.fog_color, kf.fog_color);
        }
    }

    #[test]
    fn disabled_distance_fog_turns_fog_off_only() {
        let kf = noon();
        let sys = WeatherSystem::new();
        assert_eq!(sys.snapshot(&kf, false).fog_mode, kf.fog_mode);
        assert_ne!(kf.fog_mode, FogMode::Off);
        let snap = sys.snapshot(&kf, true);
        assert_eq!(snap.fog_mode, FogMode::Off);
        assert_eq!(snap.fog_start, kf.fog_start);
        assert_eq!(snap.fog_end, kf.fog_end);
    }

    #[test]
    fn an_environ_override_tints_the_fog() {
        let mut sys = WeatherSystem::new();
        sys.set_environ_override(EnvironOverride::RedFog);
        let snap = sys.snapshot(&noon(), false);
        assert_eq!(snap.environ_override, EnvironOverride::RedFog);
        assert!(snap.fog_color.x > snap.fog_color.y);
        assert!(snap.fog_color.x > snap.fog_color.z);
    }

    #[test]
    fn the_flash_decays_over_time() {
        let kf = noon();
        let mut sys = WeatherSystem::new();
        sys.trigger_flash();
        assert!(sys.snapshot(&kf, false).lightning_flash > 0.9);
        sys.tick(0, 1.0);
        assert!(sys.snapshot(&kf, false).lightning_flash < 0.1);
    }

    #[test]
    fn the_flash_curve_matches_openac() {
        // OpenAC: held at 1 for 0.05 s, then e^(-(age - 0.05) / 0.2), cut to
        // 0 below 0.001.
        let kf = noon();
        let mut sys = WeatherSystem::new();
        sys.trigger_flash();
        let expected = [
            1.0,
            0.951_229_45,
            0.576_949_8,
            0.212_247_98,
            0.017_422_37,
            0.0,
        ];
        for (dt, want) in [0.03, 0.03, 0.1, 0.2, 0.5, 1.0].into_iter().zip(expected) {
            sys.tick(0, dt);
            let got = sys.snapshot(&kf, false).lightning_flash;
            assert!((got - want).abs() < 1e-6, "{got} vs {want}");
        }
    }

    #[test]
    fn day_group_names_map_to_kinds() {
        for (name, expected) in [
            ("Sunny", WeatherKind::Clear),
            ("SUNNY", WeatherKind::Clear),
            ("Clear", WeatherKind::Clear),
            ("", WeatherKind::Clear),
            ("Cloudy", WeatherKind::Overcast),
            ("Overcast", WeatherKind::Overcast),
            ("Dark skies", WeatherKind::Overcast),
            ("Fog", WeatherKind::Overcast),
            ("Rainy", WeatherKind::Overcast),
            ("heavy rain", WeatherKind::Overcast),
            ("Snowy", WeatherKind::Overcast),
            ("Blizzard", WeatherKind::Clear),
            ("Stormy", WeatherKind::Overcast),
            ("Thunderstorm", WeatherKind::Overcast),
        ] {
            let mut sys = WeatherSystem::new();
            sys.set_kind_from_day_group_name(name);
            sys.tick(0, 100.0);
            assert_eq!(sys.kind(), expected, "{name}");
        }
    }

    #[test]
    fn a_day_group_name_stops_the_roll() {
        let mut sys = WeatherSystem::new();
        sys.set_kind_from_day_group_name("Sunny");
        sys.tick(0, 100.0);
        assert_eq!(sys.kind(), WeatherKind::Clear);
        for d in 1..50 {
            sys.tick(d, 100.0);
            assert_eq!(sys.kind(), WeatherKind::Clear);
        }
    }
}
