//! Loudness and left-right placement of a sound: two numbers per voice,
//! worked out once when the sound starts and fixed for its life.
//!
//! The game does not do 3-D audio. A sound's level falls with the square of
//! its distance past 5 m and is quantised to whole decibels with a −50 dB
//! floor below which the sound is not played at all; its pan is a level
//! difference between the two speakers of up to 15 dB, from the sideways
//! part of its bearing. Height only matters through distance.

use glam::Vec3;

/// Distance below which gain is flat at the authored volume, metres.
pub const VOL_MIN_DISTANCE: f32 = 5.0;
/// [`VOL_MIN_DISTANCE`] squared.
pub const VOL_MIN_DISTANCE_SQ: f32 = 25.0;
/// The quietest level a sound is played at, decibels.
pub const VOL_MIN_DECIBELS: i32 = -50;
/// Pan at a source due east of a north-facing listener is `-15·sin(-90°)`.
pub const PAN_SCALE: f32 = -15.0;
/// Below this whole number of metres a sound is not panned.
pub const PAN_DEADZONE_METRES: i32 = 5;

const DEGREES_TO_RADIANS: f32 = 0.017_453_292;

/// What one new voice is given: whether to play at all, its level in whole
/// decibels (0 is full) and its pan in whole decibels (positive is right).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct VoiceMix {
    /// False when the sound is below the floor and gets no voice.
    pub play: bool,
    /// Level, `VOL_MIN_DECIBELS..=0`.
    pub decibels: i32,
    /// Pan, `-15..=15`.
    pub pan: i32,
}

/// The level of a sound `distance` metres away. Gain is the authored volume
/// inside 5 m and `25·volume/d²` beyond, clamped to 1 **before** the one
/// multiply by `master`; then `ceil(20·log10 g)`. `None` (do not play) when
/// that is below −50 dB or the gain is not positive.
#[must_use]
pub fn attenuation(distance: f32, volume: f32, master: f32) -> Option<i32> {
    let mut g = if distance < VOL_MIN_DISTANCE {
        volume
    } else {
        (VOL_MIN_DISTANCE_SQ * volume) / (distance * distance)
    };
    if g > 1.0 {
        g = 1.0;
    }
    g *= master;
    if g <= 0.0 || g.is_nan() {
        return None;
    }
    let decibels = (20.0 * g.log10()).ceil() as i32;
    (decibels >= VOL_MIN_DECIBELS).then_some(decibels)
}

/// Compass heading from `from` to `to` in degrees: north (+y) is 0, east
/// (+x) is 90, in `[0, 360)`. Height is ignored.
#[must_use]
pub fn compass_heading_degrees(from: Vec3, to: Vec3) -> f32 {
    let dx = to.x - from.x;
    let dy = to.y - from.y;
    let mut heading = 450.0 - dy.atan2(dx) * (180.0 / std::f32::consts::PI);
    heading %= 360.0;
    if heading < 0.0 {
        heading += 360.0;
    }
    heading
}

/// Fold an angle into `(-180, 180]` the way the game does: one remainder,
/// then one subtraction above 180. Large negative inputs stay below −180.
#[must_use]
pub fn normalize_signed_degrees(degrees: f32) -> f32 {
    let mut delta = degrees % 360.0;
    if delta > 180.0 || delta.is_nan() {
        delta -= 360.0;
    }
    delta
}

/// Pan in whole decibels: `(int)(−15·sin Δ)` truncated toward zero, where Δ
/// is the bearing from source to listener minus the listener's heading; 0
/// when `(int)distance < 5` or panning is off.
#[must_use]
pub fn pan(
    bearing_source_to_listener: f32,
    listener_heading_degrees: f32,
    distance: f32,
    panning_enabled: bool,
) -> i32 {
    if !panning_enabled || (distance as i32).abs() < PAN_DEADZONE_METRES {
        return 0;
    }
    let delta = normalize_signed_degrees(bearing_source_to_listener - listener_heading_degrees);
    let pan = ((delta * DEGREES_TO_RADIANS).sin() * PAN_SCALE) as i32;
    pan.clamp(PAN_SCALE as i32, -PAN_SCALE as i32)
}

/// Level and pan of a sound at `source` for a listener at `listener` facing
/// `listener_heading_degrees` (compass).
#[must_use]
pub fn mix(
    listener: Vec3,
    listener_heading_degrees: f32,
    source: Vec3,
    volume: f32,
    master: f32,
    panning_enabled: bool,
) -> VoiceMix {
    let distance = listener.distance(source);
    let bearing = compass_heading_degrees(source, listener);
    let pan = pan(bearing, listener_heading_degrees, distance, panning_enabled);
    match attenuation(distance, volume, master) {
        Some(decibels) => VoiceMix {
            play: true,
            decibels,
            pan,
        },
        None => VoiceMix {
            play: false,
            decibels: VOL_MIN_DECIBELS,
            pan,
        },
    }
}

/// Decibels to a linear amplitude factor.
#[must_use]
pub fn linear_gain(decibels: i32) -> f32 {
    10f32.powf(decibels as f32 / 20.0)
}

/// Per-speaker factors for a pan, the way the game's sound device applies
/// it: the speaker away from the sound is turned down by `|pan|` decibels
/// and the near one is left at full level. Centre is full level on both.
#[must_use]
pub fn pan_channel_gains(pan: i32) -> (f32, f32) {
    let away = linear_gain(-pan.abs());
    match pan {
        p if p > 0 => (away, 1.0),
        p if p < 0 => (1.0, away),
        _ => (1.0, 1.0),
    }
}

/// How far away a sound of this volume can still be heard, metres.
#[must_use]
pub fn audible_radius(volume: f32, master: f32) -> f32 {
    let scale = volume * master;
    if scale <= 0.0 {
        return 0.0;
    }
    let min_gain = 10f32.powf(-51.0 / 20.0);
    (VOL_MIN_DISTANCE_SQ * scale / min_gain).sqrt()
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Audio/RetailSoundMixerTests.cs`.
    //! Its `StereoPositionFromPan` cases test OpenAL's stand-in for the pan
    //! law and are replaced by `pan_channel_gains` cases (the law itself).

    use super::*;

    fn db(distance: f32, volume: f32, master: f32) -> Option<i32> {
        attenuation(distance, volume, master)
    }

    fn mix0(source: Vec3) -> VoiceMix {
        mix(Vec3::ZERO, 0.0, source, 1.0, 1.0, true)
    }

    fn close(a: f32, b: f32, places: i32) -> bool {
        (a - b).abs() <= 0.5 * 10f32.powi(-places)
    }

    #[test]
    fn attenuation_matches_retail_curve() {
        for (d, expected) in [
            (0.0, 0),
            (2.0, 0),
            (4.99, 0),
            (5.0, 0),
            (10.0, -12),
            (20.0, -24),
            (30.0, -31),
            (50.0, -40),
            (90.0, -50),
            (94.0, -50),
        ] {
            assert_eq!(db(d, 1.0, 1.0), Some(expected), "distance {d}");
        }
    }

    #[test]
    fn attenuation_beyond_cutoff_does_not_play() {
        for d in [95.0, 120.0, 1000.0] {
            assert_eq!(db(d, 1.0, 1.0), None);
        }
    }

    #[test]
    fn attenuation_is_inverse_square_not_inverse_first_power() {
        assert_eq!(
            db(10.0, 1.0, 1.0).unwrap() - db(20.0, 1.0, 1.0).unwrap(),
            12
        );
    }

    #[test]
    fn attenuation_clamps_above_unity() {
        assert_eq!(db(1.0, 10.0, 1.0), Some(0));
    }

    #[test]
    fn attenuation_clamps_before_the_master_multiply_not_after() {
        assert_eq!(db(1.0, 10.0, 0.5), Some(-6));
    }

    #[test]
    fn attenuation_high_volume_extends_audible_radius() {
        assert_eq!(db(200.0, 1.0, 1.0), None);
        assert_eq!(db(200.0, 10.0, 1.0), Some(-44));
    }

    #[test]
    fn attenuation_non_positive_master_does_not_play() {
        assert_eq!(db(1.0, 1.0, 0.0), None);
        assert_eq!(db(1.0, 1.0, -1.0), None);
    }

    #[test]
    fn attenuation_master_is_applied_exactly_once() {
        assert_eq!(
            db(10.0, 1.0, 0.5).unwrap() - db(10.0, 1.0, 1.0).unwrap(),
            -6
        );
    }

    #[test]
    fn audible_radius_matches_decoded_radii() {
        for (scale, expected) in [(1.0, 94.2), (0.5, 66.6), (0.1, 29.8)] {
            assert!(close(audible_radius(scale, 1.0), expected, 1), "{scale}");
        }
    }

    #[test]
    fn audible_radius_agrees_with_the_live_predicate() {
        let mut volume = 0.1f32;
        while volume <= 3.0 {
            let radius = audible_radius(volume, 1.0);
            assert!(db(radius - 0.5, volume, 1.0).is_some());
            assert!(db(radius + 0.5, volume, 1.0).is_none());
            volume += 0.1;
        }
    }

    #[test]
    fn decibels_are_whole_numbers_quantised_by_ceil() {
        let mut seen = std::collections::HashSet::new();
        let mut d = 5.0f32;
        while d < 94.0 {
            seen.insert(db(d, 1.0, 1.0).unwrap_or(VOL_MIN_DECIBELS));
            d += 0.05;
        }
        assert!((40..=51).contains(&seen.len()), "{}", seen.len());
    }

    #[test]
    fn linear_gain_round_trips_the_decibel_scale() {
        assert!(close(linear_gain(0), 1.0, 5));
        assert!(close(linear_gain(-6), 0.5, 2));
        assert!(close(linear_gain(-12), 0.25, 2));
        assert!(close(linear_gain(-50), 0.00316, 5));
    }

    #[test]
    fn compass_heading_uses_retail_convention() {
        for (dx, dy, expected) in [
            (0.0, 1.0, 0.0),
            (1.0, 0.0, 90.0),
            (0.0, -1.0, 180.0),
            (-1.0, 0.0, 270.0),
        ] {
            let h = compass_heading_degrees(Vec3::ZERO, Vec3::new(dx, dy, 0.0));
            assert!(close(h, expected, 2), "{dx},{dy}: {h}");
        }
    }

    #[test]
    fn normalize_signed_maps_into_retails_window() {
        for (input, expected) in [
            (0.0, 0.0),
            (180.0, 180.0),
            (181.0, -179.0),
            (270.0, -90.0),
            (359.0, -1.0),
            (-90.0, -90.0),
        ] {
            assert!(
                close(normalize_signed_degrees(input), expected, 3),
                "{input}"
            );
        }
    }

    #[test]
    fn pan_source_due_east_of_north_facing_listener_is_full_right() {
        assert_eq!(mix0(Vec3::new(10.0, 0.0, 0.0)).pan, 15);
    }

    #[test]
    fn pan_source_due_west_of_north_facing_listener_is_full_left() {
        assert_eq!(mix0(Vec3::new(-10.0, 0.0, 0.0)).pan, -15);
    }

    #[test]
    fn pan_has_no_front_back_distinction() {
        assert_eq!(mix0(Vec3::new(0.0, 10.0, 0.0)).pan, 0);
        assert_eq!(mix0(Vec3::new(0.0, -10.0, 0.0)).pan, 0);
    }

    #[test]
    fn pan_rotates_with_listener_heading() {
        let m = mix(Vec3::ZERO, 90.0, Vec3::new(10.0, 0.0, 0.0), 1.0, 1.0, true);
        assert_eq!(m.pan, 0);
    }

    #[test]
    fn pan_deadzone_is_an_integer_metre_test() {
        for (d, expected) in [(1.0, 0), (4.9, 0), (5.0, 15)] {
            assert_eq!(mix0(Vec3::new(d, 0.0, 0.0)).pan, expected, "{d}");
        }
    }

    #[test]
    fn pan_elevation_never_contributes() {
        let level = mix0(Vec3::new(10.0, 0.0, 0.0));
        let high = mix0(Vec3::new(10.0, 0.0, 40.0));
        assert_eq!(level.pan, high.pan);
        assert_ne!(level.decibels, high.decibels);
    }

    #[test]
    fn pan_purely_vertical_offset_inherits_retails_atan2_degeneracy() {
        let m = mix0(Vec3::new(0.0, 0.0, 10.0));
        assert_eq!(m.pan, -15);
        assert_eq!(m.decibels, -12);
    }

    #[test]
    fn pan_disabled_by_preference_is_always_centre() {
        let m = mix(Vec3::ZERO, 0.0, Vec3::new(10.0, 0.0, 0.0), 1.0, 1.0, false);
        assert_eq!(m.pan, 0);
    }

    #[test]
    fn pan_stays_within_fifteen_decibels() {
        for deg in 0..360 {
            let rad = deg as f32 * std::f32::consts::PI / 180.0;
            let m = mix0(Vec3::new(rad.sin() * 20.0, rad.cos() * 20.0, 0.0));
            assert!((-15..=15).contains(&m.pan));
        }
    }

    #[test]
    fn mix_beyond_cutoff_reports_do_not_play() {
        assert!(!mix0(Vec3::new(0.0, 200.0, 0.0)).play);
    }

    #[test]
    fn pan_truncates_toward_zero_not_floor() {
        for (bearing, expected) in [(64.158f32, 13), (-64.158, -13)] {
            let rad = bearing * std::f32::consts::PI / 180.0;
            let m = mix0(Vec3::new(rad.sin() * 20.0, rad.cos() * 20.0, 0.0));
            assert_eq!(m.pan, expected, "{bearing}");
        }
    }

    #[test]
    fn normalize_signed_leaves_large_negatives_alone_as_retail_does() {
        assert!(close(normalize_signed_degrees(-270.0), -270.0, 3));
        let a = (90.0f32 * std::f32::consts::PI / 180.0).sin();
        let b = (normalize_signed_degrees(-270.0) * std::f32::consts::PI / 180.0).sin();
        assert!(close(a, b, 3));
    }

    #[test]
    fn pan_law_turns_down_only_the_far_speaker() {
        assert_eq!(pan_channel_gains(0), (1.0, 1.0));
        let (l, r) = pan_channel_gains(15);
        assert_eq!(r, 1.0);
        assert!(close(20.0 * l.log10(), -15.0, 3));
        let (l, r) = pan_channel_gains(-6);
        assert_eq!(l, 1.0);
        assert!(close(20.0 * r.log10(), -6.0, 3));
    }

    #[test]
    fn pan_law_gives_the_requested_level_difference_at_every_pan() {
        for pan in -15..=15 {
            let (l, r) = pan_channel_gains(pan);
            assert!(close(20.0 * (r / l).log10(), pan as f32, 3), "{pan}");
        }
    }
}
