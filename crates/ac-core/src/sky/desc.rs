//! The region's sky description turned into keyframes and sky objects, and
//! the choice of which kind of day (day group) a calendar day gets.
//!
//! `ac-core` reads no dat files. The caller copies the region's sky
//! description field for field into the `*Source` types below (the dat's
//! colours as red, green, blue bytes; ids as plain numbers) and may supply a
//! [`SkyDatLookup`] for the two optional model lookups.

use glam::Vec3;

use super::keyframe::{FogMode, SkyKeyframe, SkyStateProvider};
use crate::world::{DAYS_IN_A_MONTH, DerethCalendar, MONTHS_IN_A_YEAR};

/// The region file holding Dereth's sky.
pub const REGION_DAT_ID: u32 = 0x1300_0000;

/// Sky object property bit: drawn after the scene (in front of it).
pub const SKY_OBJECT_POST_SCENE: u32 = 0x01;
/// Sky object property bit: a weather layer (rain, snow, clouds).
pub const SKY_OBJECT_WEATHER: u32 = 0x04;

const GFX_OBJ_TYPE: u32 = 0x0100_0000;
const SETUP_TYPE: u32 = 0x0200_0000;
const TYPE_MASK: u32 = 0xFF00_0000;

/// The region's sky, as stored.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct SkyDescSource {
    /// Sky update interval.
    pub tick_size: f64,
    /// Lighting update interval.
    pub light_tick_size: f64,
    /// Kinds of day.
    pub day_groups: Vec<DayGroupSource>,
}

/// One kind of day, as stored.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct DayGroupSource {
    /// Relative chance of this kind of day.
    pub chance_of_occur: f32,
    /// Name ("Sunny", "Cloudy", ...).
    pub day_name: String,
    /// Sky objects.
    pub sky_objects: Vec<SkyObjectSource>,
    /// Lighting keyframes.
    pub sky_time: Vec<SkyTimeSource>,
}

/// One sky object, as stored.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct SkyObjectSource {
    /// Day fraction it appears.
    pub begin_time: f32,
    /// Day fraction it disappears.
    pub end_time: f32,
    /// Angle at the start, degrees.
    pub begin_angle: f32,
    /// Angle at the end, degrees.
    pub end_angle: f32,
    /// Texture scroll speed, u.
    pub tex_velocity_x: f32,
    /// Texture scroll speed, v.
    pub tex_velocity_y: f32,
    /// Model id (0 = none).
    pub default_gfx_object_id: u32,
    /// Physics script id (0 = none).
    pub default_pes_object_id: u32,
    /// Property bits.
    pub properties: u32,
}

/// One lighting keyframe, as stored.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct SkyTimeSource {
    /// Day fraction it starts at.
    pub begin: f32,
    /// Sunlight brightness.
    pub dir_bright: f32,
    /// Sun heading, degrees.
    pub dir_heading: f32,
    /// Sun pitch, degrees.
    pub dir_pitch: f32,
    /// Sunlight colour, red, green, blue bytes.
    pub dir_color: [u8; 3],
    /// Ambient brightness.
    pub amb_bright: f32,
    /// Ambient colour, red, green, blue bytes.
    pub amb_color: [u8; 3],
    /// Fog start, metres.
    pub min_world_fog: f32,
    /// Fog end, metres.
    pub max_world_fog: f32,
    /// Fog colour, red, green, blue bytes.
    pub world_fog_color: [u8; 3],
    /// Fog mode value (see [`FogMode::from_dat`]).
    pub world_fog: u32,
    /// Sky object overrides at this time.
    pub sky_obj_replace: Vec<SkyObjectReplaceSource>,
}

/// One sky object override, as stored (percent values unscaled).
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct SkyObjectReplaceSource {
    /// Index of the sky object in its day group.
    pub object_index: u32,
    /// Replacement model id (0 keeps the model).
    pub gfx_obj_id: u32,
    /// Rotation, degrees.
    pub rotate: f32,
    /// Transparency, percent.
    pub transparent: f32,
    /// Luminosity, percent.
    pub luminosity: f32,
    /// Maximum brightness, percent.
    pub max_bright: f32,
}

/// The optional model lookups the sky loader makes. A failed or missing
/// lookup answers `None`; the loader then uses zero and carries on.
pub trait SkyDatLookup {
    /// The sort centre of model `gfx_obj_id` (a `0x01xxxxxx` id).
    fn gfx_obj_sort_center(&self, gfx_obj_id: u32) -> Option<Vec3>;
    /// The default physics script of setup `setup_id` (a `0x02xxxxxx` id).
    fn setup_default_script(&self, setup_id: u32) -> Option<u32>;
}

/// A sky object ready for drawing.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct SkyObjectData {
    /// Day fraction it appears.
    pub begin_time: f32,
    /// Day fraction it disappears; before `begin_time` means it stays up
    /// across midnight.
    pub end_time: f32,
    /// Angle at the start of its window, degrees.
    pub begin_angle: f32,
    /// Angle at the end of its window, degrees.
    pub end_angle: f32,
    /// Texture scroll speed, u per second.
    pub tex_velocity_x: f32,
    /// Texture scroll speed, v per second.
    pub tex_velocity_y: f32,
    /// Model id (0 = none).
    pub gfx_obj_id: u32,
    /// Physics script id (0 = none).
    pub pes_object_id: u32,
    /// Property bits.
    pub properties: u32,
    /// Default script of a setup model, else 0.
    pub default_script_id: u32,
    /// Sort centre of a single model, else zero.
    pub authored_sort_center: Vec3,
}

impl SkyObjectData {
    /// A weather layer.
    #[must_use]
    pub fn is_weather(&self) -> bool {
        self.properties & SKY_OBJECT_WEATHER != 0
    }

    /// Drawn after the scene.
    #[must_use]
    pub fn is_post_scene(&self) -> bool {
        self.properties & SKY_OBJECT_POST_SCENE != 0
    }

    /// Whether the object is up at day fraction `t`. Equal begin and end
    /// mean always.
    #[must_use]
    pub fn is_visible(&self, t: f32) -> bool {
        if self.begin_time == self.end_time {
            return true;
        }
        if self.begin_time < self.end_time {
            return t >= self.begin_time && t <= self.end_time;
        }
        t >= self.begin_time || t <= self.end_time
    }

    /// How far through its window the object is at `t`, 0..=1.
    #[must_use]
    pub fn angle_progress(&self, t: f32) -> f32 {
        if self.begin_time == self.end_time {
            return 0.0;
        }
        let progress = if self.begin_time < self.end_time {
            let duration = self.end_time - self.begin_time;
            (t - self.begin_time) / duration
        } else {
            let duration = (1.0 - self.begin_time) + self.end_time;
            if t >= self.begin_time {
                (t - self.begin_time) / duration
            } else {
                (t + (1.0 - self.begin_time)) / duration
            }
        };
        progress.clamp(0.0, 1.0)
    }

    /// The object's angle at `t`, degrees: begin to end angle across its
    /// window.
    #[must_use]
    pub fn current_angle(&self, t: f32) -> f32 {
        if self.begin_time == self.end_time {
            return self.begin_angle;
        }
        self.begin_angle + (self.end_angle - self.begin_angle) * self.angle_progress(t)
    }
}

/// A sky object override ready for drawing; the percent values are scaled
/// to 0..1.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct SkyObjectReplaceData {
    /// Index of the sky object in its day group.
    pub object_index: u32,
    /// Replacement model id (0 keeps the model).
    pub gfx_obj_id: u32,
    /// Rotation, degrees.
    pub rotate: f32,
    /// Transparency, 0..1.
    pub transparent: f32,
    /// Luminosity, 0..1.
    pub luminosity: f32,
    /// Maximum brightness, 0..1.
    pub max_bright: f32,
    /// Sort centre of the replacement model, else zero.
    pub authored_sort_center: Vec3,
}

/// One keyframe with its sky object overrides.
#[derive(Debug, Clone, PartialEq)]
pub struct DatSkyKeyframeData {
    /// Lighting and fog.
    pub keyframe: SkyKeyframe,
    /// Sky object overrides at this time.
    pub replaces: Vec<SkyObjectReplaceData>,
}

/// One kind of day, ready for use.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct DayGroupData {
    /// Relative chance (informational; the day choice is a hash).
    pub chance_of_occur: f32,
    /// Name.
    pub name: String,
    /// Sky objects.
    pub sky_objects: Vec<SkyObjectData>,
    /// Keyframes in stored order.
    pub sky_times: Vec<DatSkyKeyframeData>,
}

impl DayGroupData {
    /// This day's sky, or `None` when it has no keyframes.
    #[must_use]
    pub fn sky(&self) -> Option<SkyStateProvider> {
        SkyStateProvider::new(self.sky_times.iter().map(|s| s.keyframe).collect()).ok()
    }
}

/// The region's sky, ready for use.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct LoadedSkyDesc {
    /// Sky update interval.
    pub tick_size: f64,
    /// Lighting update interval.
    pub light_tick_size: f64,
    /// Kinds of day, in stored order.
    pub day_groups: Vec<DayGroupData>,
}

impl LoadedSkyDesc {
    /// The day group index for a calendar day (see
    /// [`select_day_group_index`]).
    #[must_use]
    pub fn select_day_group_index(
        &self,
        absolute_year: i32,
        days_per_year: i32,
        day_of_year: i32,
        forced_index: Option<usize>,
    ) -> usize {
        select_day_group_index(
            self.day_groups.len(),
            absolute_year,
            days_per_year,
            day_of_year,
            forced_index,
        )
    }

    /// The day group for the day `server_ticks` falls in, by `calendar`.
    /// The reference client uses the default-origin calendar here.
    ///
    /// # Errors
    /// A NaN or infinite tick count.
    pub fn active_day_group(
        &self,
        calendar: &DerethCalendar,
        server_ticks: f64,
        forced_index: Option<usize>,
    ) -> Result<Option<&DayGroupData>, crate::world::CalendarError> {
        let year = calendar.absolute_year(server_ticks)?;
        let day = calendar.day_of_year(server_ticks)?;
        let index = self.select_day_group_index(year, DAYS_PER_YEAR, day, forced_index);
        Ok(self.day_groups.get(index))
    }

    /// The day group of year 0, day 0.
    #[must_use]
    pub fn default_day_group(&self, forced_index: Option<usize>) -> Option<&DayGroupData> {
        let index = self.select_day_group_index(0, DAYS_PER_YEAR, 0, forced_index);
        self.day_groups.get(index)
    }

    /// The sky of the default day group; the built-in sky when there is no
    /// group or it has no keyframes.
    #[must_use]
    pub fn build_default_provider(&self, forced_index: Option<usize>) -> SkyStateProvider {
        self.default_day_group(forced_index)
            .and_then(DayGroupData::sky)
            .unwrap_or_else(SkyStateProvider::default_sky)
    }

    /// The sky for the day `server_ticks` falls in; the built-in sky when
    /// there is no group or it has no keyframes.
    ///
    /// # Errors
    /// A NaN or infinite tick count.
    pub fn build_provider_for_day(
        &self,
        calendar: &DerethCalendar,
        server_ticks: f64,
        forced_index: Option<usize>,
    ) -> Result<SkyStateProvider, crate::world::CalendarError> {
        Ok(self
            .active_day_group(calendar, server_ticks, forced_index)?
            .and_then(DayGroupData::sky)
            .unwrap_or_else(SkyStateProvider::default_sky))
    }
}

/// Days in a game year.
pub const DAYS_PER_YEAR: i32 = DAYS_IN_A_MONTH * MONTHS_IN_A_YEAR;

/// Which day group (kind of weather) a calendar day gets.
///
/// A valid `forced_index` wins. Otherwise the day number
/// `year × days_per_year + day` is hashed (a multiply and add on 32 bits),
/// the hash is read as a fraction of 2³², and that fraction of the group
/// count is the index. Every client computes the same day for the same
/// date, so all players see the same weather.
#[must_use]
pub fn select_day_group_index(
    day_group_count: usize,
    absolute_year: i32,
    days_per_year: i32,
    day_of_year: i32,
    forced_index: Option<usize>,
) -> usize {
    if day_group_count == 0 {
        return 0;
    }
    if let Some(forced) = forced_index
        && forced < day_group_count
    {
        return forced;
    }
    if day_group_count == 1 {
        return 0;
    }

    let seed = absolute_year
        .wrapping_mul(days_per_year)
        .wrapping_add(day_of_year);
    let mixed = (seed as u32)
        .wrapping_mul(0x6A42_FDB2)
        .wrapping_add(0x8ABE_1652);
    // Computed in double precision: every step is exact, as in the
    // original's extended-precision arithmetic.
    let fraction = f64::from(mixed) * (1.0 / 4_294_967_296.0);
    let index = (fraction * day_group_count as f64).floor();
    if index < 0.0 || index >= day_group_count as f64 {
        0
    } else {
        index as usize
    }
}

/// The colour bytes as linear 0..1 values.
#[must_use]
pub fn color_to_vec3(rgb: [u8; 3]) -> Vec3 {
    Vec3::new(
        f32::from(rgb[0]) / 255.0,
        f32::from(rgb[1]) / 255.0,
        f32::from(rgb[2]) / 255.0,
    )
}

/// Turn the stored sky into keyframes and sky objects. `dats` resolves
/// models' sort centres and setups' default scripts; without it both are
/// zero.
#[must_use]
pub fn load_sky_desc(source: &SkyDescSource, dats: Option<&dyn SkyDatLookup>) -> LoadedSkyDesc {
    let day_groups = source
        .day_groups
        .iter()
        .map(|group| DayGroupData {
            chance_of_occur: group.chance_of_occur,
            name: group.day_name.clone(),
            sky_objects: group
                .sky_objects
                .iter()
                .map(|object| convert_sky_object(object, dats))
                .collect(),
            sky_times: group
                .sky_time
                .iter()
                .map(|time| convert_time_of_day(time, dats))
                .collect(),
        })
        .collect();
    LoadedSkyDesc {
        tick_size: source.tick_size,
        light_tick_size: source.light_tick_size,
        day_groups,
    }
}

fn convert_sky_object(s: &SkyObjectSource, dats: Option<&dyn SkyDatLookup>) -> SkyObjectData {
    SkyObjectData {
        begin_time: s.begin_time,
        end_time: s.end_time,
        begin_angle: s.begin_angle,
        end_angle: s.end_angle,
        tex_velocity_x: s.tex_velocity_x,
        tex_velocity_y: s.tex_velocity_y,
        gfx_obj_id: s.default_gfx_object_id,
        pes_object_id: s.default_pes_object_id,
        properties: s.properties,
        default_script_id: resolve_setup_default_script(s.default_gfx_object_id, dats),
        authored_sort_center: resolve_sort_center(s.default_gfx_object_id, dats),
    }
}

fn convert_time_of_day(s: &SkyTimeSource, dats: Option<&dyn SkyDatLookup>) -> DatSkyKeyframeData {
    let replaces = s
        .sky_obj_replace
        .iter()
        .map(|r| SkyObjectReplaceData {
            object_index: r.object_index,
            gfx_obj_id: r.gfx_obj_id,
            rotate: r.rotate,
            transparent: r.transparent / 100.0,
            luminosity: r.luminosity / 100.0,
            max_bright: r.max_bright / 100.0,
            authored_sort_center: resolve_sort_center(r.gfx_obj_id, dats),
        })
        .collect();
    let keyframe = SkyKeyframe {
        begin: s.begin,
        sun_heading_deg: s.dir_heading,
        sun_pitch_deg: s.dir_pitch,
        dir_color: color_to_vec3(s.dir_color),
        dir_bright: s.dir_bright,
        amb_color: color_to_vec3(s.amb_color),
        amb_bright: s.amb_bright,
        fog_color: color_to_vec3(s.world_fog_color),
        fog_density: 0.0,
        fog_start: s.min_world_fog,
        fog_end: s.max_world_fog,
        fog_mode: FogMode::from_dat(s.world_fog),
    };
    DatSkyKeyframeData { keyframe, replaces }
}

fn resolve_setup_default_script(model_id: u32, dats: Option<&dyn SkyDatLookup>) -> u32 {
    match dats {
        Some(dats) if model_id & TYPE_MASK == SETUP_TYPE => {
            dats.setup_default_script(model_id).unwrap_or(0)
        }
        _ => 0,
    }
}

fn resolve_sort_center(model_id: u32, dats: Option<&dyn SkyDatLookup>) -> Vec3 {
    match dats {
        Some(dats) if model_id & TYPE_MASK == GFX_OBJ_TYPE => {
            dats.gfx_obj_sort_center(model_id).unwrap_or(Vec3::ZERO)
        }
        _ => Vec3::ZERO,
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `SkyDescLoaderTests` (the region is built by hand, as there),
    //! plus day-group values printed by OpenAC's `SkyDayGroupSelector`.
    use super::*;

    #[test]
    fn day_group_selector_matches_the_retail_golden_values() {
        for (count, year, day, expected) in [
            (20, 10, 0, 16),
            (20, 10, 83, 5),
            (20, 116, 83, 18),
            (20, 127, 359, 4),
            // Printed by OpenAC.
            (20, 0, 0, 10),
            (7, 3, 200, 5),
            (20, -3, 15, 9),
        ] {
            assert_eq!(
                select_day_group_index(count, year, 360, day, None),
                expected
            );
        }
    }

    #[test]
    fn day_group_selector_is_exact_where_single_precision_is_not() {
        // OpenAC's single-precision arithmetic gives 17 here (the only
        // difference found for years -50..3000 and 2..31 groups); the exact
        // computation gives 16.
        assert_eq!(select_day_group_index(20, 2048, 360, 110, None), 16);
    }

    #[test]
    fn day_group_selector_forced_index_is_an_explicit_input() {
        assert_eq!(select_day_group_index(20, 10, 360, 0, Some(7)), 7);
        assert_eq!(
            select_day_group_index(20, 10, 360, 0, Some(20)),
            16,
            "out of range is ignored"
        );
        assert_eq!(select_day_group_index(0, 10, 360, 0, None), 0);
        assert_eq!(select_day_group_index(1, 10, 360, 0, None), 0);
    }

    fn make_region(dir_bright: f32, red: u8) -> SkyDescSource {
        SkyDescSource {
            tick_size: 1.0,
            light_tick_size: 2.0,
            day_groups: vec![DayGroupSource {
                chance_of_occur: 1.0,
                day_name: String::new(),
                sky_objects: Vec::new(),
                sky_time: vec![SkyTimeSource {
                    begin: 0.5,
                    dir_bright,
                    dir_heading: 180.0,
                    dir_pitch: 70.0,
                    dir_color: [red, 0, 0],
                    amb_bright: 0.4,
                    amb_color: [100, 100, 100],
                    min_world_fog: 120.0,
                    max_world_fog: 400.0,
                    world_fog_color: [50, 50, 50],
                    world_fog: 1,
                    sky_obj_replace: Vec::new(),
                }],
            }],
        }
    }

    #[test]
    fn converts_fog_fields() {
        let loaded = load_sky_desc(&make_region(1.5, 200), None);
        assert_eq!(loaded.tick_size, 1.0);
        assert_eq!(loaded.day_groups.len(), 1);
        assert_eq!(loaded.day_groups[0].sky_times.len(), 1);
        let kf = loaded.day_groups[0].sky_times[0].keyframe;
        assert_eq!(kf.fog_start, 120.0);
        assert_eq!(kf.fog_end, 400.0);
        assert_eq!(kf.fog_mode, FogMode::Linear);
    }

    #[test]
    fn captures_the_sky_object_script_id() {
        let mut region = make_region(1.0, 255);
        region.day_groups[0].sky_objects.push(SkyObjectSource {
            begin_time: 0.0,
            end_time: 1.0,
            default_gfx_object_id: 0x0100_4C44,
            default_pes_object_id: 0x3300_042C,
            properties: 0x05,
            ..SkyObjectSource::default()
        });
        let loaded = load_sky_desc(&region, None);
        let obj = &loaded.day_groups[0].sky_objects[0];
        assert_eq!(obj.gfx_obj_id, 0x0100_4C44);
        assert_eq!(obj.pes_object_id, 0x3300_042C);
        assert!(obj.is_post_scene());
        assert!(obj.is_weather());
        assert_eq!(obj.authored_sort_center, Vec3::ZERO);
    }

    struct FakeDats;

    impl SkyDatLookup for FakeDats {
        fn gfx_obj_sort_center(&self, id: u32) -> Option<Vec3> {
            match id {
                0x0100_1348 => Some(Vec3::new(1050.0, 0.0, 0.0)),
                0x0100_1F6A => Some(Vec3::new(2066.82, 552.99, 0.0)),
                _ => None,
            }
        }
        fn setup_default_script(&self, id: u32) -> Option<u32> {
            (id == 0x0200_0001).then_some(0x3300_0007)
        }
    }

    struct FailingDats;

    impl SkyDatLookup for FailingDats {
        fn gfx_obj_sort_center(&self, _: u32) -> Option<Vec3> {
            None
        }
        fn setup_default_script(&self, _: u32) -> Option<u32> {
            None
        }
    }

    #[test]
    fn with_dats_carries_default_and_replacement_sort_centres() {
        let mut region = make_region(1.0, 255);
        region.day_groups[0].sky_objects.push(SkyObjectSource {
            default_gfx_object_id: 0x0100_1348,
            ..SkyObjectSource::default()
        });
        region.day_groups[0].sky_objects.push(SkyObjectSource {
            default_gfx_object_id: 0x0200_0001,
            ..SkyObjectSource::default()
        });
        region.day_groups[0].sky_time[0]
            .sky_obj_replace
            .push(SkyObjectReplaceSource {
                object_index: 0,
                gfx_obj_id: 0x0100_1F6A,
                transparent: 100.0,
                luminosity: 50.0,
                max_bright: 25.0,
                ..SkyObjectReplaceSource::default()
            });

        let loaded = load_sky_desc(&region, Some(&FakeDats));

        let group = &loaded.day_groups[0];
        assert_eq!(
            group.sky_objects[0].authored_sort_center,
            Vec3::new(1050.0, 0.0, 0.0)
        );
        assert_eq!(group.sky_objects[0].default_script_id, 0);
        assert_eq!(group.sky_objects[1].default_script_id, 0x3300_0007);
        assert_eq!(group.sky_objects[1].authored_sort_center, Vec3::ZERO);
        let replace = &group.sky_times[0].replaces[0];
        assert_eq!(
            replace.authored_sort_center,
            Vec3::new(2066.82, 552.99, 0.0)
        );
        assert_eq!(
            (replace.transparent, replace.luminosity, replace.max_bright),
            (1.0, 0.5, 0.25)
        );
    }

    #[test]
    fn a_failed_optional_lookup_cannot_fail_sky_loading() {
        let mut region = make_region(1.0, 255);
        region.day_groups[0].sky_objects.push(SkyObjectSource {
            default_gfx_object_id: 0x0100_1348,
            ..SkyObjectSource::default()
        });
        let loaded = load_sky_desc(&region, Some(&FailingDats));
        assert_eq!(
            loaded.day_groups[0].sky_objects[0].authored_sort_center,
            Vec3::ZERO
        );
    }

    #[test]
    fn sun_color_uses_the_sun_vector_length() {
        let loaded = load_sky_desc(&make_region(1.5, 200), None);
        let kf = loaded.day_groups[0].sky_times[0].keyframe;
        assert!((1.17..=1.18).contains(&kf.sun_color().x));
    }

    #[test]
    fn the_default_provider_interpolates_dat_keyframes() {
        let loaded = load_sky_desc(&make_region(1.0, 255), None);
        let s = loaded.build_default_provider(None).interpolate(0.1);
        assert!((0.99..=1.01).contains(&s.sun_color().x));
    }

    #[test]
    fn no_day_groups_falls_back_to_the_built_in_sky() {
        let loaded = LoadedSkyDesc::default();
        assert_eq!(
            loaded.build_default_provider(None),
            SkyStateProvider::default_sky()
        );
        let provider = loaded
            .build_provider_for_day(&DerethCalendar::default(), 0.0, None)
            .unwrap();
        assert_eq!(provider, SkyStateProvider::default_sky());
    }

    #[test]
    fn sky_object_visibility_wraps_midnight() {
        let obj = SkyObjectData {
            begin_time: 0.9,
            end_time: 0.1,
            ..SkyObjectData::default()
        };
        assert!(obj.is_visible(0.95));
        assert!(obj.is_visible(0.05));
        assert!(!obj.is_visible(0.5));
    }

    #[test]
    fn sky_object_angle_lerps_across_its_window() {
        let obj = SkyObjectData {
            begin_time: 0.25,
            end_time: 0.75,
            begin_angle: 0.0,
            end_angle: 180.0,
            ..SkyObjectData::default()
        };
        assert!((obj.current_angle(0.5) - 90.0).abs() < 0.01);
        assert!(obj.current_angle(0.25).abs() < 0.01);
    }

    #[test]
    fn fog_mode_values() {
        assert_eq!(FogMode::from_dat(0), FogMode::Off);
        assert_eq!(FogMode::from_dat(1), FogMode::Linear);
        assert_eq!(FogMode::from_dat(2), FogMode::Exp);
        assert_eq!(FogMode::from_dat(3), FogMode::Exp2);
        assert_eq!(FogMode::from_dat(4), FogMode::Off);
    }
}
