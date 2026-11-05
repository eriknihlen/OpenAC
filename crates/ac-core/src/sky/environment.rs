//! One world's environment: the calendar, which day group is active, the
//! weather, the server's fog override and the debug time-of-day and
//! weather cycles.
//!
//! The game clock is not owned here. Every call that needs the time takes
//! the current tick count from the caller's clock (the server's ticks at the
//! last sync plus elapsed seconds).

use std::fmt;

use super::desc::{DAYS_PER_YEAR, LoadedSkyDesc, select_day_group_index};
use super::keyframe::{SkyKeyframe, SkyStateProvider};
use super::weather::{EnvironOverride, WeatherKind, WeatherSystem};
use crate::world::{CalendarError, DAY_TICKS, DEFAULT_ORIGIN_OFFSET_TICKS, DerethCalendar};

/// One kind of day as the environment needs it.
#[derive(Debug, Clone, PartialEq)]
pub struct DayGroupDefinition {
    /// Name; it decides the weather kind.
    pub name: String,
    /// Relative chance (informational).
    pub chance_of_occur: f32,
    /// Number of sky objects (informational).
    pub sky_object_count: usize,
    /// The day's sky.
    pub sky: SkyStateProvider,
}

/// Everything the environment is built from.
#[derive(Debug, Clone, PartialEq)]
pub struct WorldEnvironmentDefinition {
    origin_offset_ticks: f64,
    source_tick_size: f64,
    light_tick_size: f64,
    day_groups: Vec<DayGroupDefinition>,
    forced_day_group_index: Option<usize>,
}

/// A definition value was NaN or infinite.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EnvironmentDefinitionError {
    /// The calendar origin.
    OriginOffset,
    /// The sky tick size.
    SourceTickSize,
    /// The light tick size.
    LightTickSize,
    /// A loaded day group (by index) has no keyframes.
    DayGroupWithoutKeyframes(usize),
}

impl fmt::Display for EnvironmentDefinitionError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let what = match self {
            EnvironmentDefinitionError::OriginOffset => "origin offset",
            EnvironmentDefinitionError::SourceTickSize => "sky tick size",
            EnvironmentDefinitionError::LightTickSize => "light tick size",
            EnvironmentDefinitionError::DayGroupWithoutKeyframes(index) => {
                return write!(f, "sky day group {index} has no keyframes");
            }
        };
        write!(f, "world environment {what} is not finite")
    }
}

impl std::error::Error for EnvironmentDefinitionError {}

impl WorldEnvironmentDefinition {
    /// A definition. A forced day group index outside the list is dropped.
    ///
    /// # Errors
    /// A NaN or infinite origin or tick size.
    pub fn new(
        origin_offset_ticks: f64,
        source_tick_size: f64,
        light_tick_size: f64,
        day_groups: Vec<DayGroupDefinition>,
        forced_day_group_index: Option<usize>,
    ) -> Result<Self, EnvironmentDefinitionError> {
        if !origin_offset_ticks.is_finite() {
            return Err(EnvironmentDefinitionError::OriginOffset);
        }
        if !source_tick_size.is_finite() {
            return Err(EnvironmentDefinitionError::SourceTickSize);
        }
        if !light_tick_size.is_finite() {
            return Err(EnvironmentDefinitionError::LightTickSize);
        }
        let forced_day_group_index = forced_day_group_index.filter(|&i| i < day_groups.len());
        Ok(Self {
            origin_offset_ticks,
            source_tick_size,
            light_tick_size,
            day_groups,
            forced_day_group_index,
        })
    }

    /// The definition for a loaded region sky. `zero_time_of_year` is the
    /// region calendar's origin (the default origin when absent); tick
    /// sizes default to 1 without a sky.
    ///
    /// # Errors
    /// A NaN or infinite origin or tick size, or a day group without
    /// keyframes.
    pub fn from_loaded_sky(
        loaded: Option<&LoadedSkyDesc>,
        zero_time_of_year: Option<f64>,
        forced_day_group_index: Option<usize>,
    ) -> Result<Self, EnvironmentDefinitionError> {
        let origin = zero_time_of_year.unwrap_or(DEFAULT_ORIGIN_OFFSET_TICKS);
        let mut groups = Vec::new();
        for (index, group) in loaded
            .iter()
            .flat_map(|sky| sky.day_groups.iter())
            .enumerate()
        {
            let sky = group
                .sky()
                .ok_or(EnvironmentDefinitionError::DayGroupWithoutKeyframes(index))?;
            groups.push(DayGroupDefinition {
                name: group.name.clone(),
                chance_of_occur: group.chance_of_occur,
                sky_object_count: group.sky_objects.len(),
                sky,
            });
        }
        Self::new(
            origin,
            loaded.map_or(1.0, |sky| sky.tick_size),
            loaded.map_or(1.0, |sky| sky.light_tick_size),
            groups,
            forced_day_group_index,
        )
    }

    /// The calendar origin, ticks.
    #[must_use]
    pub fn origin_offset_ticks(&self) -> f64 {
        self.origin_offset_ticks
    }

    /// The sky's tick size (an update throttle, not a rate).
    #[must_use]
    pub fn source_tick_size(&self) -> f64 {
        self.source_tick_size
    }

    /// The lighting tick size.
    #[must_use]
    pub fn light_tick_size(&self) -> f64 {
        self.light_tick_size
    }

    /// The kinds of day.
    #[must_use]
    pub fn day_groups(&self) -> &[DayGroupDefinition] {
        &self.day_groups
    }

    /// The forced day group, if any.
    #[must_use]
    pub fn forced_day_group_index(&self) -> Option<usize> {
        self.forced_day_group_index
    }
}

/// What an admin environment change asks for.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EnvironmentEffect {
    /// Values 0..=6: a fog override, now in force.
    FogOverride(EnvironOverride),
    /// A named sound to play.
    SoundCue(EnvironSoundCue),
    /// A value with no known meaning.
    Unknown(u32),
}

/// The admin environment sound cues.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[allow(missing_docs)]
pub enum EnvironSoundCue {
    Roar = 0x65,
    Bell = 0x66,
    Chant1 = 0x67,
    Chant2 = 0x68,
    DarkWhispers1 = 0x69,
    DarkWhispers2 = 0x6A,
    DarkLaugh = 0x6B,
    DarkWind = 0x6C,
    DarkSpeech = 0x6D,
    Drums = 0x6E,
    GhostSpeak = 0x6F,
    Breathing = 0x70,
    Howl = 0x71,
    LostSouls = 0x72,
    Squeal = 0x75,
    Thunder1 = 0x76,
    Thunder2 = 0x77,
    Thunder3 = 0x78,
    Thunder4 = 0x79,
    Thunder5 = 0x7A,
    Thunder6 = 0x7B,
}

impl EnvironSoundCue {
    /// The cue for a wire value, if it names one.
    #[must_use]
    pub fn from_raw(value: u32) -> Option<Self> {
        use EnvironSoundCue as C;
        Some(match value {
            0x65 => C::Roar,
            0x66 => C::Bell,
            0x67 => C::Chant1,
            0x68 => C::Chant2,
            0x69 => C::DarkWhispers1,
            0x6A => C::DarkWhispers2,
            0x6B => C::DarkLaugh,
            0x6C => C::DarkWind,
            0x6D => C::DarkSpeech,
            0x6E => C::Drums,
            0x6F => C::GhostSpeak,
            0x70 => C::Breathing,
            0x71 => C::Howl,
            0x72 => C::LostSouls,
            0x75 => C::Squeal,
            0x76 => C::Thunder1,
            0x77 => C::Thunder2,
            0x78 => C::Thunder3,
            0x79 => C::Thunder4,
            0x7A => C::Thunder5,
            0x7B => C::Thunder6,
            _ => return None,
        })
    }
}

/// A day group change made by [`WorldEnvironment::refresh_day_group`], for
/// the caller to log.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DayGroupChange {
    /// Calendar year.
    pub absolute_year: i32,
    /// Day of the year, 0-based.
    pub day_of_year: i32,
    /// The new active day group.
    pub index: usize,
    /// The weather kind it set.
    pub weather: WeatherKind,
}

/// The environment's observable state.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct WorldEnvironmentSnapshot {
    /// Bumped on every change.
    pub revision: u64,
    /// Active day group, once one was chosen.
    pub active_day_group_index: Option<usize>,
    /// Game day number (`year × 360 + day`) of the active day group.
    pub active_day_index: Option<i64>,
    /// Weather kind.
    pub weather: WeatherKind,
    /// Server fog override.
    pub environ_override: EnvironOverride,
}

/// One world's environment.
#[derive(Debug, Clone)]
pub struct WorldEnvironment {
    definition: WorldEnvironmentDefinition,
    calendar: DerethCalendar,
    sky: SkyStateProvider,
    weather: WeatherSystem,
    active_day_group_index: Option<usize>,
    active_day_index: Option<i64>,
    debug_day_fraction: Option<f32>,
    pinned_day_fraction: Option<f32>,
    time_debug_step: u32,
    weather_debug_step: usize,
    revision: u64,
}

impl WorldEnvironment {
    /// Where the caller's clock should be synced right after construction,
    /// before the server's first time sync: noon on the default origin.
    pub const OFFLINE_SYNC_TICKS: f64 = DAY_TICKS / 16.0;

    const DEBUG_WEATHER_KINDS: [WeatherKind; 5] = [
        WeatherKind::Clear,
        WeatherKind::Overcast,
        WeatherKind::Rain,
        WeatherKind::Snow,
        WeatherKind::Storm,
    ];

    /// The environment for `definition`, choosing the day group for
    /// `now_ticks` (the caller's clock before its offline sync; 0 for a
    /// clock never synced). The caller then syncs its clock to
    /// [`Self::OFFLINE_SYNC_TICKS`].
    ///
    /// # Errors
    /// A negative origin.
    pub fn new(
        definition: WorldEnvironmentDefinition,
        now_ticks: f64,
    ) -> Result<(Self, Option<DayGroupChange>), CalendarError> {
        let calendar = DerethCalendar::new(definition.origin_offset_ticks)?;
        let mut env = Self {
            definition,
            calendar,
            sky: SkyStateProvider::default_sky(),
            weather: WeatherSystem::new(),
            active_day_group_index: None,
            active_day_index: None,
            debug_day_fraction: None,
            pinned_day_fraction: None,
            time_debug_step: 0,
            weather_debug_step: 0,
            revision: 1,
        };
        let change = if env.definition.day_groups.is_empty() {
            None
        } else {
            env.refresh_day_group(now_ticks)?
        };
        // The caller's offline sync clears any debug time.
        env.revision += 1;
        Ok((env, change))
    }

    /// The definition.
    #[must_use]
    pub fn definition(&self) -> &WorldEnvironmentDefinition {
        &self.definition
    }

    /// This world's calendar.
    #[must_use]
    pub fn calendar(&self) -> &DerethCalendar {
        &self.calendar
    }

    /// The weather.
    #[must_use]
    pub fn weather(&self) -> &WeatherSystem {
        &self.weather
    }

    /// The weather, for ticking and the flash.
    pub fn weather_mut(&mut self) -> &mut WeatherSystem {
        &mut self.weather
    }

    /// The active day's sky (the built-in sky until a day group is chosen).
    #[must_use]
    pub fn sky(&self) -> &SkyStateProvider {
        &self.sky
    }

    /// The active day group index.
    #[must_use]
    pub fn active_day_group_index(&self) -> Option<usize> {
        self.active_day_group_index
    }

    /// The observable state.
    #[must_use]
    pub fn snapshot(&self) -> WorldEnvironmentSnapshot {
        WorldEnvironmentSnapshot {
            revision: self.revision,
            active_day_group_index: self.active_day_group_index,
            active_day_index: self.active_day_index,
            weather: self.weather.kind(),
            environ_override: self.weather.environ_override(),
        }
    }

    /// Pin the time of day (a launch option); it beats the debug override
    /// and the clock.
    pub fn set_pinned_day_fraction(&mut self, value: Option<f32>) {
        self.pinned_day_fraction = value;
    }

    /// The day fraction at `now_ticks`: the pinned value, else the debug
    /// override, else the calendar's.
    ///
    /// # Errors
    /// A NaN or infinite tick count (when neither override applies).
    pub fn day_fraction(&self, now_ticks: f64) -> Result<f64, CalendarError> {
        if let Some(pinned) = self.pinned_day_fraction {
            return Ok(f64::from(pinned));
        }
        if let Some(debug) = self.debug_day_fraction {
            return Ok(f64::from(debug));
        }
        self.calendar.day_fraction(now_ticks)
    }

    /// The sky at `now_ticks`.
    ///
    /// # Errors
    /// As [`Self::day_fraction`].
    pub fn current_sky(&self, now_ticks: f64) -> Result<SkyKeyframe, CalendarError> {
        Ok(self.sky.interpolate(self.day_fraction(now_ticks)? as f32))
    }

    /// The sky at a given day fraction (the "persistent daylight" option
    /// asks for noon).
    #[must_use]
    pub fn sky_at_day_fraction(&self, day_fraction: f32) -> SkyKeyframe {
        self.sky.interpolate(day_fraction)
    }

    /// The server synced the clock to `ticks`: the debug time override
    /// ends and the day group is re-checked.
    ///
    /// # Errors
    /// A NaN or infinite tick count.
    pub fn synchronize_from_server(
        &mut self,
        ticks: f64,
    ) -> Result<Option<DayGroupChange>, CalendarError> {
        self.debug_day_fraction = None;
        self.revision += 1;
        if self.definition.day_groups.is_empty() {
            return Ok(None);
        }
        self.refresh_day_group(ticks)
    }

    /// Re-check which day group the day of `now_ticks` gets. Switches the
    /// sky and weather when the day or the group changed.
    ///
    /// # Errors
    /// A NaN or infinite tick count.
    pub fn refresh_day_group(
        &mut self,
        now_ticks: f64,
    ) -> Result<Option<DayGroupChange>, CalendarError> {
        if self.definition.day_groups.is_empty() {
            return Ok(None);
        }
        let absolute_year = self.calendar.absolute_year(now_ticks)?;
        let day_of_year = self.calendar.day_of_year(now_ticks)?;
        let day_index =
            i64::from(absolute_year) * i64::from(DAYS_PER_YEAR) + i64::from(day_of_year);
        let index = select_day_group_index(
            self.definition.day_groups.len(),
            absolute_year,
            DAYS_PER_YEAR,
            day_of_year,
            self.definition.forced_day_group_index,
        );
        if Some(day_index) == self.active_day_index && Some(index) == self.active_day_group_index {
            return Ok(None);
        }
        self.active_day_index = Some(day_index);
        self.active_day_group_index = Some(index);
        let group = &self.definition.day_groups[index];
        self.sky = group.sky.clone();
        self.weather.set_kind_from_day_group_name(&group.name);
        self.revision += 1;
        Ok(Some(DayGroupChange {
            absolute_year,
            day_of_year,
            index,
            weather: self.weather.kind(),
        }))
    }

    /// Apply an admin environment change: 0..=6 install a fog override
    /// (kept until the next one); other values are sound cues and leave
    /// the fog alone.
    pub fn apply_admin_environs(&mut self, change_type: u32) -> EnvironmentEffect {
        if let Some(value) = EnvironOverride::from_raw(change_type) {
            self.weather.set_environ_override(value);
            self.revision += 1;
            return EnvironmentEffect::FogOverride(value);
        }
        match EnvironSoundCue::from_raw(change_type) {
            Some(cue) => EnvironmentEffect::SoundCue(cue),
            None => EnvironmentEffect::Unknown(change_type),
        }
    }

    /// Debug command: step the time override through midnight, dawn, noon,
    /// dusk and off. Returns the message to show.
    pub fn cycle_time_of_day(&mut self) -> String {
        self.time_debug_step = (self.time_debug_step + 1) % 5;
        let selection = match self.time_debug_step {
            1 => Some(0.0f32),
            2 => Some(0.25),
            3 => Some(0.5),
            4 => Some(0.75),
            _ => None,
        };
        self.revision += 1;
        self.debug_day_fraction = selection;
        match selection {
            Some(value) => format!("Time override = {value:.2}"),
            None => "Time override cleared".to_owned(),
        }
    }

    /// Debug command: step the forced weather through clear, overcast,
    /// rain, snow and storm. Returns the message to show.
    pub fn cycle_weather(&mut self) -> String {
        self.weather_debug_step = (self.weather_debug_step + 1) % Self::DEBUG_WEATHER_KINDS.len();
        let kind = Self::DEBUG_WEATHER_KINDS[self.weather_debug_step];
        self.weather.force_weather(kind);
        self.revision += 1;
        format!("Weather = {}", kind.name())
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `RuntimeWorldEnvironmentStateTests` and
    //! `WorldEnvironmentControllerTests`. The clock is the test's: ticks
    //! are passed in. One-shot initialisation needs no test here: an
    //! environment is built once from its definition.
    use glam::Vec3;

    use super::*;
    use crate::sky::{DatSkyKeyframeData, DayGroupData, FogMode};

    fn sky() -> SkyStateProvider {
        SkyStateProvider::new(vec![SkyKeyframe {
            begin: 0.0,
            sun_heading_deg: 0.0,
            sun_pitch_deg: 90.0,
            dir_color: Vec3::ONE,
            dir_bright: 1.0,
            amb_color: Vec3::ONE,
            amb_bright: 1.0,
            fog_color: Vec3::ZERO,
            fog_density: 0.0,
            fog_start: SkyKeyframe::DEFAULT_FOG_START,
            fog_end: SkyKeyframe::DEFAULT_FOG_END,
            fog_mode: FogMode::Linear,
        }])
        .unwrap()
    }

    fn group(name: &str) -> DayGroupDefinition {
        DayGroupDefinition {
            name: name.to_owned(),
            chance_of_occur: 100.0,
            sky_object_count: 1,
            sky: sky(),
        }
    }

    fn definition(
        origin: f64,
        names: &[&str],
        forced: Option<usize>,
    ) -> WorldEnvironmentDefinition {
        WorldEnvironmentDefinition::new(
            origin,
            0.8,
            0.2,
            names.iter().map(|n| group(n)).collect(),
            forced,
        )
        .unwrap()
    }

    fn initialized(origin: f64, names: &[&str]) -> WorldEnvironment {
        WorldEnvironment::new(definition(origin, names, None), 0.0)
            .unwrap()
            .0
    }

    #[test]
    fn construction_picks_the_day_group_and_the_offline_sync_is_noon() {
        let (env, change) = WorldEnvironment::new(
            definition(DEFAULT_ORIGIN_OFFSET_TICKS, &["Sunny"], None),
            0.0,
        )
        .unwrap();
        assert_eq!(env.active_day_group_index(), Some(0));
        assert_eq!(change.map(|c| c.index), Some(0));
        assert_eq!(env.weather().kind(), WeatherKind::Clear);
        let noon = env
            .day_fraction(WorldEnvironment::OFFLINE_SYNC_TICKS)
            .unwrap();
        assert!((0.499..=0.501).contains(&noon));
        assert_eq!(env.definition().source_tick_size(), 0.8);
    }

    #[test]
    fn two_worlds_keep_their_own_calendar_weather_and_debug_state() {
        let mut first = initialized(3600.0, &["Sunny"]);
        let mut second = initialized(DEFAULT_ORIGIN_OFFSET_TICKS, &["Cloudy"]);
        first.synchronize_from_server(0.0).unwrap();
        second.synchronize_from_server(0.0).unwrap();
        first.apply_admin_environs(0x01);
        first.cycle_time_of_day();

        assert_eq!(first.calendar().origin_offset_ticks(), 3600.0);
        assert_eq!(
            second.calendar().origin_offset_ticks(),
            DEFAULT_ORIGIN_OFFSET_TICKS
        );
        assert_eq!(first.day_fraction(0.0).unwrap(), 0.0);
        assert!((second.day_fraction(0.0).unwrap() - 7.0 / 16.0).abs() < 0.001);
        assert_eq!(first.weather().environ_override(), EnvironOverride::RedFog);
        assert_eq!(second.weather().environ_override(), EnvironOverride::None);
        assert_eq!(first.weather().kind(), WeatherKind::Clear);
        assert_eq!(second.weather().kind(), WeatherKind::Overcast);
    }

    #[test]
    fn a_server_sync_clears_the_debug_time() {
        let mut env = initialized(DEFAULT_ORIGIN_OFFSET_TICKS, &["Sunny"]);
        env.cycle_time_of_day();
        assert_eq!(env.day_fraction(12.0).unwrap(), 0.0);
        env.synchronize_from_server(0.0).unwrap();
        // Twelve seconds later the caller's clock reads 12 ticks.
        assert_ne!(env.day_fraction(12.0).unwrap(), 0.0);
    }

    #[test]
    fn admin_environs_fog_values_are_the_world_state() {
        for (raw, expected) in [
            (0x00, EnvironOverride::None),
            (0x01, EnvironOverride::RedFog),
            (0x06, EnvironOverride::BlackFog2),
        ] {
            let mut env = initialized(DEFAULT_ORIGIN_OFFSET_TICKS, &["Sunny"]);
            assert_eq!(
                env.apply_admin_environs(raw),
                EnvironmentEffect::FogOverride(expected)
            );
            assert_eq!(env.snapshot().environ_override, expected);
        }
    }

    #[test]
    fn an_admin_environs_sound_leaves_the_fog_alone() {
        let mut env = initialized(DEFAULT_ORIGIN_OFFSET_TICKS, &["Sunny"]);
        env.apply_admin_environs(0x04);
        assert_eq!(
            env.apply_admin_environs(0x78),
            EnvironmentEffect::SoundCue(EnvironSoundCue::Thunder3)
        );
        assert_eq!(env.weather().environ_override(), EnvironOverride::GreenFog);
        assert_eq!(
            env.apply_admin_environs(0x73),
            EnvironmentEffect::Unknown(0x73)
        );
    }

    #[test]
    fn a_forced_day_group_belongs_to_the_definition() {
        let env = WorldEnvironment::new(
            definition(DEFAULT_ORIGIN_OFFSET_TICKS, &["Sunny", "Cloudy"], Some(1)),
            0.0,
        )
        .unwrap()
        .0;
        assert_eq!(env.active_day_group_index(), Some(1));
        assert_eq!(env.weather().kind(), WeatherKind::Overcast);
        let dropped = definition(DEFAULT_ORIGIN_OFFSET_TICKS, &["Sunny"], Some(5));
        assert_eq!(dropped.forced_day_group_index(), None);
    }

    #[test]
    fn a_sync_within_the_same_day_does_not_reselect() {
        let mut env = initialized(DEFAULT_ORIGIN_OFFSET_TICKS, &["Cloudy"]);
        assert!(env.synchronize_from_server(100_000.0).unwrap().is_some());
        assert!(env.synchronize_from_server(100_000.0).unwrap().is_none());
        assert_eq!(env.weather().kind(), WeatherKind::Overcast);
    }

    fn loaded(name: &str) -> LoadedSkyDesc {
        LoadedSkyDesc {
            tick_size: 0.8,
            light_tick_size: 0.2,
            day_groups: vec![DayGroupData {
                chance_of_occur: 100.0,
                name: name.to_owned(),
                sky_objects: Vec::new(),
                sky_times: vec![DatSkyKeyframeData {
                    keyframe: sky().keyframes()[0],
                    replaces: Vec::new(),
                }],
            }],
        }
    }

    #[test]
    fn a_loaded_sky_without_game_time_uses_the_default_origin() {
        let with_origin =
            WorldEnvironmentDefinition::from_loaded_sky(Some(&loaded("Sunny")), Some(3600.0), None)
                .unwrap();
        let without =
            WorldEnvironmentDefinition::from_loaded_sky(Some(&loaded("Sunny")), None, None)
                .unwrap();
        assert_eq!(with_origin.origin_offset_ticks(), 3600.0);
        assert_eq!(without.origin_offset_ticks(), DEFAULT_ORIGIN_OFFSET_TICKS);
        let env = WorldEnvironment::new(without, 0.0).unwrap().0;
        let noon = env
            .day_fraction(WorldEnvironment::OFFLINE_SYNC_TICKS)
            .unwrap();
        assert!((0.499..=0.501).contains(&noon));
        assert_eq!(env.weather().kind(), WeatherKind::Clear);

        let none = WorldEnvironmentDefinition::from_loaded_sky(None, None, None).unwrap();
        assert_eq!(
            (none.source_tick_size(), none.light_tick_size()),
            (1.0, 1.0)
        );
        assert!(none.day_groups().is_empty());
    }

    #[test]
    fn a_loaded_day_group_without_keyframes_is_refused() {
        let mut sky = loaded("Sunny");
        sky.day_groups[0].sky_times.clear();
        assert_eq!(
            WorldEnvironmentDefinition::from_loaded_sky(Some(&sky), None, None),
            Err(EnvironmentDefinitionError::DayGroupWithoutKeyframes(0))
        );
    }

    #[test]
    fn cycle_time_of_day_keeps_the_five_step_sequence() {
        let mut env = initialized(DEFAULT_ORIGIN_OFFSET_TICKS, &["Sunny"]);
        for (message, fraction) in [
            ("Time override = 0.00", 0.0),
            ("Time override = 0.25", 0.25),
            ("Time override = 0.50", 0.5),
            ("Time override = 0.75", 0.75),
        ] {
            assert_eq!(env.cycle_time_of_day(), message);
            assert_eq!(env.day_fraction(0.0).unwrap(), fraction);
        }
        assert_eq!(env.cycle_time_of_day(), "Time override cleared");
    }

    #[test]
    fn cycle_weather_keeps_the_order() {
        let mut env = initialized(DEFAULT_ORIGIN_OFFSET_TICKS, &["Sunny"]);
        for (message, kind) in [
            ("Weather = Overcast", WeatherKind::Overcast),
            ("Weather = Rain", WeatherKind::Rain),
            ("Weather = Snow", WeatherKind::Snow),
            ("Weather = Storm", WeatherKind::Storm),
            ("Weather = Clear", WeatherKind::Clear),
        ] {
            assert_eq!(env.cycle_weather(), message);
            assert_eq!(env.weather().kind(), kind);
        }
    }

    #[test]
    fn a_pinned_day_fraction_beats_the_debug_time_and_the_clock() {
        let mut env = initialized(DEFAULT_ORIGIN_OFFSET_TICKS, &["Sunny"]);
        env.cycle_time_of_day();
        env.set_pinned_day_fraction(Some(0.3));
        assert!((env.day_fraction(0.0).unwrap() - 0.3).abs() < 1e-6);
    }
}
