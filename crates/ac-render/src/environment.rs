//! A window's world environment: the region's sky and weather driven by
//! the session's game clock, giving each frame its sky keyframe (sun,
//! ambient and fog colours), the atmosphere (fog range, override, flash)
//! and the day group whose sky objects are drawn.
//!
//! The clock is the session's (ticks at the last server sync plus elapsed
//! seconds, one tick per second); a window without a session runs from the
//! offline sync point (noon). A launch option can pin the time of day; the
//! debug cycle overrides it until the next server sync.

use std::time::Instant;

use ac_core::sky::{
    AtmosphereSnapshot, DayGroupData, EnvironOverride, EnvironmentEffect, LoadedSkyDesc,
    SkyKeyframe, WorldEnvironment, WorldEnvironmentDefinition,
};
use ac_core::world::{DAYS_IN_A_MONTH, MONTHS_IN_A_YEAR, MonthName};

/// Startup choices for the sky (launch options).
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct SkyOptions {
    /// Always this time of day, 0..1 (`OPENAC_WORLD_TIME`).
    pub pinned_day_fraction: Option<f32>,
    /// Always this day group (`OPENAC_DAY_GROUP`).
    pub forced_day_group: Option<usize>,
}

impl SkyOptions {
    /// Read the options through `var`: a day fraction in `0 <= f < 1` and
    /// a non-negative day group index; anything else is ignored.
    pub fn from_vars(var: impl Fn(&str) -> Option<String>) -> Self {
        let pinned_day_fraction = var("OPENAC_WORLD_TIME")
            .and_then(|v| v.trim().parse::<f32>().ok())
            .filter(|f| (0.0..1.0).contains(f));
        let forced_day_group = var("OPENAC_DAY_GROUP").and_then(|v| v.trim().parse::<usize>().ok());
        Self {
            pinned_day_fraction,
            forced_day_group,
        }
    }
}

/// What a frame is lit and fogged with.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct EnvironmentFrame {
    /// Time of day, 0..1 (0 and 1 are midnight).
    pub day_fraction: f32,
    /// The sky keyframe at that time.
    pub keyframe: SkyKeyframe,
    /// Fog and flash.
    pub atmosphere: AtmosphereSnapshot,
    /// The server's fog override is on (sky objects then hide and fog).
    pub environ_override: bool,
}

/// The window's environment. See the module documentation.
#[derive(Debug)]
pub struct WindowEnvironment {
    env: WorldEnvironment,
    sky: LoadedSkyDesc,
    /// The server tick count last applied.
    synced: Option<f64>,
    /// Ticks and instant of the offline clock (used before any sync).
    offline: (f64, Instant),
    last_frame: Option<Instant>,
    started: Instant,
}

impl WindowEnvironment {
    /// The environment for the region's sky and calendar origin. `None`
    /// when the sky definition is unusable (the window then keeps the
    /// built-in lighting).
    #[must_use]
    pub fn new(sky: LoadedSkyDesc, zero_time_of_year: f64, options: SkyOptions) -> Option<Self> {
        let definition = WorldEnvironmentDefinition::from_loaded_sky(
            Some(&sky),
            Some(zero_time_of_year),
            options.forced_day_group,
        )
        .ok()?;
        let (mut env, _) = WorldEnvironment::new(definition, 0.0).ok()?;
        env.set_pinned_day_fraction(options.pinned_day_fraction);
        let now = Instant::now();
        let mut this = Self {
            env,
            sky,
            synced: None,
            offline: (WorldEnvironment::OFFLINE_SYNC_TICKS, now),
            last_frame: None,
            started: now,
        };
        // The offline sync point picks the first day group.
        let _ = this
            .env
            .synchronize_from_server(WorldEnvironment::OFFLINE_SYNC_TICKS);
        Some(this)
    }

    /// The clock at `now`: the session's when it has synced, else the
    /// offline clock.
    fn ticks(&self, session_ticks: Option<f64>, now: Instant) -> f64 {
        session_ticks.unwrap_or_else(|| {
            let (ticks, at) = self.offline;
            ticks + now.saturating_duration_since(at).as_secs_f64()
        })
    }

    /// The in-game date and hour at `now` (the map's date line).
    #[must_use]
    pub fn calendar_date(
        &self,
        session_ticks: Option<f64>,
        now: Instant,
    ) -> Option<ac_core::world::CalendarDate> {
        let ticks = self.ticks(session_ticks, now);
        self.env.calendar().to_calendar(ticks).ok()
    }

    /// Advance to `now`. `synced` is the session's tick count at its last
    /// sync (a new value re-checks the day group and ends the debug time
    /// override) and `session_ticks` its clock now. `disable_distance_fog`
    /// turns the fog off: the player's "disable distance fog" character
    /// option, or the overhead map view.
    pub fn frame(
        &mut self,
        synced: Option<f64>,
        session_ticks: Option<f64>,
        now: Instant,
        disable_distance_fog: bool,
    ) -> EnvironmentFrame {
        if let Some(ticks) = synced
            && self.synced != Some(ticks)
        {
            self.synced = Some(ticks);
            let _ = self.env.synchronize_from_server(ticks);
        }
        let ticks = self.ticks(session_ticks, now);
        let dt = self
            .last_frame
            .replace(now)
            .map_or(0.0, |t| now.saturating_duration_since(t).as_secs_f32());
        if let Ok(date) = self.env.calendar().to_calendar(ticks) {
            let month = MonthName::ALL
                .iter()
                .position(|m| *m == date.month)
                .unwrap_or(0) as i32;
            let day_index = date.year * (DAYS_IN_A_MONTH * MONTHS_IN_A_YEAR)
                + month * DAYS_IN_A_MONTH
                + date.day
                - 1;
            self.env.weather_mut().tick(day_index, dt);
        }
        let day_fraction = self.env.day_fraction(ticks).unwrap_or(0.5) as f32;
        let keyframe = self.env.sky().interpolate(day_fraction);
        let atmosphere = self.env.weather().snapshot(&keyframe, disable_distance_fog);
        EnvironmentFrame {
            day_fraction,
            keyframe,
            atmosphere,
            environ_override: self.env.weather().environ_override() != EnvironOverride::None,
        }
    }

    /// The sky keyframe at `day_fraction` (0..1) of the current sky.
    #[must_use]
    pub fn sky_at_day_fraction(&self, day_fraction: f32) -> SkyKeyframe {
        self.env.sky().interpolate(day_fraction)
    }

    /// The keyframe the landscape is lit with for `frame`: with persistent
    /// daylight (`/day`) the sky at noon, else the frame's own; the sky
    /// and its clock are not changed.
    #[must_use]
    pub fn landscape_keyframe(
        &self,
        frame: &EnvironmentFrame,
        persistent_daylight: bool,
    ) -> SkyKeyframe {
        if persistent_daylight {
            self.sky_at_day_fraction(0.5)
        } else {
            frame.keyframe
        }
    }

    /// The day group whose sky objects are up.
    #[must_use]
    pub fn day_group(&self) -> Option<&DayGroupData> {
        self.sky.day_groups.get(self.env.active_day_group_index()?)
    }

    /// The index of the day group whose sky objects are up.
    #[must_use]
    pub fn day_group_index(&self) -> Option<usize> {
        self.env.active_day_group_index()
    }

    /// Every day group (to prepare their models).
    #[must_use]
    pub fn day_groups(&self) -> &[DayGroupData] {
        &self.sky.day_groups
    }

    /// Seconds since the environment started (sky texture scroll).
    #[must_use]
    pub fn seconds(&self, now: Instant) -> f32 {
        now.saturating_duration_since(self.started).as_secs_f32()
    }

    /// The server's environment change.
    pub fn apply_admin_environs(&mut self, change_type: u32) -> EnvironmentEffect {
        self.env.apply_admin_environs(change_type)
    }

    /// Debug: step the time of day through midnight, dawn, noon, dusk and
    /// off. The message to show.
    pub fn cycle_time_of_day(&mut self) -> String {
        self.env.cycle_time_of_day()
    }

    /// Debug: step the weather. The message to show.
    pub fn cycle_weather(&mut self) -> String {
        self.env.cycle_weather()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;

    #[test]
    fn the_sky_options_accept_only_valid_values() {
        let vars: HashMap<&str, &str> =
            HashMap::from([("OPENAC_WORLD_TIME", "0.25"), ("OPENAC_DAY_GROUP", "3")]);
        let o = SkyOptions::from_vars(|k| vars.get(k).map(|v| (*v).to_owned()));
        assert_eq!(o.pinned_day_fraction, Some(0.25));
        assert_eq!(o.forced_day_group, Some(3));
        for bad in ["1", "-0.1", "noon", ""] {
            let o = SkyOptions::from_vars(|k| (k == "OPENAC_WORLD_TIME").then(|| bad.to_owned()));
            assert_eq!(o.pinned_day_fraction, None, "{bad}");
        }
        assert_eq!(SkyOptions::from_vars(|_| None), SkyOptions::default());
    }

    fn sky() -> LoadedSkyDesc {
        use ac_core::sky::{DatSkyKeyframeData, SkyStateProvider};
        let keyframes = SkyStateProvider::default_sky().keyframes().to_vec();
        LoadedSkyDesc {
            tick_size: 1.0,
            light_tick_size: 1.0,
            day_groups: vec![DayGroupData {
                name: "Sunny".into(),
                sky_times: keyframes
                    .into_iter()
                    .map(|keyframe| DatSkyKeyframeData {
                        keyframe,
                        replaces: Vec::new(),
                    })
                    .collect(),
                ..DayGroupData::default()
            }],
        }
    }

    #[test]
    fn a_pinned_time_wins_and_a_sync_moves_the_clock() {
        let options = SkyOptions {
            pinned_day_fraction: Some(0.75),
            forced_day_group: None,
        };
        let mut env = WindowEnvironment::new(sky(), 3600.0, options).unwrap();
        let now = Instant::now();
        assert_eq!(env.frame(None, None, now, false).day_fraction, 0.75);
        assert_eq!(env.day_group().map(|g| g.name.as_str()), Some("Sunny"));

        let mut env = WindowEnvironment::new(sky(), 3600.0, SkyOptions::default()).unwrap();
        let a = env
            .frame(Some(100_000.0), Some(100_000.0), now, false)
            .day_fraction;
        let b = env
            .frame(Some(100_000.0), Some(100_000.0 + 7620.0 / 4.0), now, false)
            .day_fraction;
        assert!(((b - a).rem_euclid(1.0) - 0.25).abs() < 1e-4, "{a} {b}");
    }

    #[test]
    fn persistent_daylight_lights_the_landscape_at_noon_without_moving_the_clock() {
        // OpenAC WorldRenderFrameBuilderTests
        // PersistentAtDay_UsesNoonLandscapeLightingWithoutChangingTheSkyClock.
        let options = SkyOptions {
            pinned_day_fraction: Some(0.0),
            forced_day_group: None,
        };
        let mut env = WindowEnvironment::new(sky(), 3600.0, options).unwrap();
        let frame = env.frame(None, None, Instant::now(), false);
        let midnight = frame.keyframe;
        let noon = env.sky_at_day_fraction(0.5);
        assert_ne!(midnight.ambient_color(), noon.ambient_color());
        let lit = env.landscape_keyframe(&frame, true);
        assert_eq!(lit.ambient_color(), noon.ambient_color());
        assert_eq!(lit.sun_color(), noon.sun_color());
        assert_eq!(frame.day_fraction, 0.0);
        let unlit = env.landscape_keyframe(&frame, false);
        assert_eq!(unlit, midnight);
    }

    #[test]
    fn the_distance_fog_option_turns_the_fog_off() {
        // OpenAC WeatherSystemTests DisableDistanceFogSource_True_ForcesFogModeOff
        // and _False_LeavesFogModeAlone, through the window's frame (GameWindow
        // binds the source to the DisableDistanceFog character option).
        let options = SkyOptions {
            pinned_day_fraction: Some(0.5),
            forced_day_group: None,
        };
        let mut env = WindowEnvironment::new(sky(), 3600.0, options).unwrap();
        let now = Instant::now();
        let fogged = env.frame(None, None, now, false);
        assert_ne!(fogged.atmosphere.fog_mode, ac_core::sky::FogMode::Off);
        assert_eq!(fogged.atmosphere.fog_mode, fogged.keyframe.fog_mode);
        let clear = env.frame(None, None, now, true);
        assert_eq!(clear.atmosphere.fog_mode, ac_core::sky::FogMode::Off);
        assert_eq!(clear.atmosphere.fog_color, fogged.atmosphere.fog_color);
    }

    #[test]
    fn the_debug_cycle_overrides_until_the_next_sync() {
        let mut env = WindowEnvironment::new(sky(), 3600.0, SkyOptions::default()).unwrap();
        let now = Instant::now();
        env.frame(Some(50_000.0), Some(50_000.0), now, false);
        env.cycle_time_of_day();
        assert_eq!(
            env.frame(Some(50_000.0), Some(50_000.0), now, false)
                .day_fraction,
            0.0
        );
        env.cycle_time_of_day();
        assert_eq!(
            env.frame(Some(50_000.0), Some(50_000.0), now, false)
                .day_fraction,
            0.25
        );
        let synced = env
            .frame(Some(60_000.0), Some(60_000.0), now, false)
            .day_fraction;
        assert_ne!(synced, 0.25);
    }
}
