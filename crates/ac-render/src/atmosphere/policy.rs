//! The atmosphere policy: how strongly the sun rays, shadows, light shafts
//! and wind act for the sun's height, the day group and the weather.
//!
//! Each response is a list of points (sun elevation in degrees, strength)
//! read between neighbours: straight lines for the sun rays, lines in the
//! sine of the elevation for the shadows (so the curve follows how high
//! the sun really stands), an S-curve between points for the light shafts.

use ac_core::sky::WeatherKind;

/// One point of a response: at this sun elevation (degrees above the
/// horizon) the effect has this strength.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ResponsePoint {
    /// Sun elevation, degrees.
    pub elevation_degrees: f64,
    /// Strength there.
    pub multiplier: f64,
}

const fn point(elevation_degrees: f64, multiplier: f64) -> ResponsePoint {
    ResponsePoint {
        elevation_degrees,
        multiplier,
    }
}

/// The sun rays' response: none below 3 degrees under the horizon, full
/// at 4 degrees, three quarters at 22, none from 55 up.
pub const SUN_ELEVATION_RESPONSE: [ResponsePoint; 6] = [
    point(-90.0, 0.0),
    point(-3.0, 0.0),
    point(4.0, 1.0),
    point(22.0, 0.75),
    point(55.0, 0.0),
    point(90.0, 0.0),
];

/// The shadows' response: none up to 1 degree, full from 12.
pub const DIRECTIONAL_SHADOW_ELEVATION_RESPONSE: [ResponsePoint; 4] = [
    point(-90.0, 0.0),
    point(1.0, 0.0),
    point(12.0, 1.0),
    point(90.0, 1.0),
];

/// The light shafts' response: from the horizon up to full at 6 degrees,
/// full to 18, gone at 70.
pub const VOLUMETRIC_SHAFT_ELEVATION_RESPONSE: [ResponsePoint; 6] = [
    point(-90.0, 0.0),
    point(0.0, 0.0),
    point(6.0, 1.0),
    point(18.0, 1.0),
    point(70.0, 0.0),
    point(90.0, 0.0),
];

/// Strength by the active day group's index (other groups: 1). OpenAC
/// keys this on the raw index (a known open question there, kept).
pub const ACTIVE_DAY_GROUP_MULTIPLIERS: [(i32, f64); 3] = [(0, 1.0), (1, 0.35), (2, 0.20)];

/// The foliage wind's mean and gust by weather, by the weather's name.
pub const FOLIAGE_WIND_BY_WEATHER: [(&str, f64, f64); 5] = [
    ("Clear", 0.25, 0.15),
    ("Overcast", 0.60, 0.35),
    ("Rain", 0.85, 0.60),
    ("Snow", 0.35, 0.20),
    ("Storm", 1.00, 0.75),
];

/// The weather's name as the wind table keys it.
#[must_use]
pub fn weather_name(weather: WeatherKind) -> &'static str {
    match weather {
        WeatherKind::Clear => "Clear",
        WeatherKind::Overcast => "Overcast",
        WeatherKind::Rain => "Rain",
        WeatherKind::Snow => "Snow",
        WeatherKind::Storm => "Storm",
    }
}

fn evaluate(
    points: &[ResponsePoint],
    input: f32,
    transform_point: impl Fn(f64) -> f32,
    transform_interpolation: impl Fn(f32) -> f32,
    fallback: f32,
) -> f32 {
    let Some(first) = points.first() else {
        return fallback;
    };
    if input <= transform_point(first.elevation_degrees) {
        return first.multiplier as f32;
    }
    for pair in points.windows(2) {
        let (lower, upper) = (pair[0], pair[1]);
        let upper_input = transform_point(upper.elevation_degrees);
        if input > upper_input {
            continue;
        }
        let lower_input = transform_point(lower.elevation_degrees);
        let span = upper_input - lower_input;
        let t = if span <= 0.0 {
            0.0
        } else {
            ((input - lower_input) / span).clamp(0.0, 1.0)
        };
        let t = transform_interpolation(t);
        return (lower.multiplier + (upper.multiplier - lower.multiplier) * f64::from(t)) as f32;
    }
    points[points.len() - 1].multiplier as f32
}

/// The sun rays' strength at `elevation_degrees` (straight lines between
/// points).
#[must_use]
pub fn ray(points: &[ResponsePoint], elevation_degrees: f32, fallback: f32) -> f32 {
    evaluate(points, elevation_degrees, |v| v as f32, |t| t, fallback)
}

/// The shadows' strength at the light's elevation sine (lines in sine
/// space).
#[must_use]
pub fn directional_shadow_from_sin(
    points: &[ResponsePoint],
    light_elevation_sin: f32,
    fallback: f32,
) -> f32 {
    evaluate(
        points,
        light_elevation_sin.clamp(-1.0, 1.0),
        |degrees| (degrees as f32 * (std::f32::consts::PI / 180.0)).sin(),
        |t| t,
        fallback,
    )
}

/// The shadows' strength at `elevation_degrees`.
#[must_use]
pub fn directional_shadow(points: &[ResponsePoint], elevation_degrees: f32, fallback: f32) -> f32 {
    directional_shadow_from_sin(
        points,
        (elevation_degrees * (std::f32::consts::PI / 180.0)).sin(),
        fallback,
    )
}

/// The light shafts' strength at `elevation_degrees` (smooth steps between
/// points).
#[must_use]
pub fn volumetric_shaft(points: &[ResponsePoint], elevation_degrees: f32, fallback: f32) -> f32 {
    evaluate(
        points,
        elevation_degrees,
        |v| v as f32,
        |t| t * t * (3.0 - 2.0 * t),
        fallback,
    )
}

/// The wind's mean and gust for `weather` from `table` (matched by exact
/// name), else its Clear row, else none.
#[must_use]
pub fn foliage_wind(table: Option<&[(&str, f64, f64)]>, weather: WeatherKind) -> (f32, f32) {
    let Some(table) = table else {
        return (0.0, 0.0);
    };
    let name = weather_name(weather);
    let mut clear = None;
    for &(kind, mean, gust) in table {
        if kind == name {
            return (mean as f32, gust as f32);
        }
        if clear.is_none() && kind == "Clear" {
            clear = Some((mean as f32, gust as f32));
        }
    }
    clear.unwrap_or((0.0, 0.0))
}

/// Move `current` toward `target` by the share of `transition_seconds`
/// that `delta_seconds` is (all of it at once without a transition).
#[must_use]
pub fn ease_toward_target(
    current: f32,
    target: f32,
    delta_seconds: f32,
    transition_seconds: f32,
) -> f32 {
    let rate = if transition_seconds <= 0.0 {
        1.0
    } else {
        (delta_seconds / transition_seconds).clamp(0.0, 1.0)
    };
    current + (target - current) * rate
}

/// The active day group's strength (1 for a group the table does not
/// list).
#[must_use]
pub fn day_group_multiplier(active_day_group: i32) -> f32 {
    ACTIVE_DAY_GROUP_MULTIPLIERS
        .iter()
        .find(|(group, _)| *group == active_day_group)
        .map_or(1.0, |(_, m)| *m as f32)
}

/// How much of the sun's effect the weather leaves, blended in by the
/// weather's intensity.
#[must_use]
pub fn weather_policy(weather: WeatherKind, intensity: f32) -> f32 {
    let target = match weather {
        WeatherKind::Clear => 1.0,
        WeatherKind::Overcast => 0.18,
        WeatherKind::Rain => 0.10,
        WeatherKind::Snow => 0.16,
        WeatherKind::Storm => 0.06,
    };
    1.0 + (target - 1.0) * intensity.clamp(0.0, 1.0)
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.App.Tests/Rendering/Packs/RenderPackAtmospherePolicyEvaluationTests.cs`.
    use super::*;

    const FIVE_KIND_TABLE: [(&str, f64, f64); 5] = FOLIAGE_WIND_BY_WEATHER;

    #[test]
    fn foliage_wind_resolves_each_of_the_five_declared_weather_kinds() {
        for (kind, mean, gust) in [
            (WeatherKind::Clear, 0.25, 0.15),
            (WeatherKind::Overcast, 0.60, 0.35),
            (WeatherKind::Rain, 0.85, 0.60),
            (WeatherKind::Snow, 0.35, 0.20),
            (WeatherKind::Storm, 1.00, 0.75),
        ] {
            assert_eq!(foliage_wind(Some(&FIVE_KIND_TABLE), kind), (mean, gust));
        }
    }

    #[test]
    fn foliage_wind_falls_back_to_the_clear_row_for_an_unlisted_kind() {
        let table = [("Clear", 0.25, 0.15), ("Storm", 1.00, 0.75)];
        assert_eq!(
            foliage_wind(Some(&table), WeatherKind::Overcast),
            (0.25, 0.15)
        );
    }

    #[test]
    fn foliage_wind_returns_zero_when_neither_the_kind_nor_clear_is_declared() {
        let table = [("Storm", 1.00, 0.75)];
        assert_eq!(
            foliage_wind(Some(&table), WeatherKind::Overcast),
            (0.0, 0.0)
        );
        assert_eq!(foliage_wind(None, WeatherKind::Clear), (0.0, 0.0));
    }

    #[test]
    fn foliage_wind_matches_by_exact_name_not_substring_or_case() {
        let table = [
            ("Rainy", 0.99, 0.99),
            ("rain", 0.99, 0.99),
            ("Clear", 0.10, 0.05),
        ];
        assert_eq!(foliage_wind(Some(&table), WeatherKind::Rain), (0.10, 0.05));
    }

    #[test]
    fn ease_toward_target_does_not_move_with_zero_delta() {
        assert_eq!(ease_toward_target(0.25, 0.85, 0.0, 10.0), 0.25);
    }

    #[test]
    fn ease_toward_target_converges_without_overshoot_or_discontinuity() {
        let (target, mut current) = (0.85f32, 0.25f32);
        let mut previous = current;
        for i in 0..4000 {
            current = ease_toward_target(current, target, 0.1, 10.0);
            assert!(current >= previous - 1e-6, "tick {i}");
            assert!(current <= target + 1e-6, "tick {i}");
            previous = current;
        }
        assert!((current - target).abs() < 0.01);
    }

    #[test]
    fn ease_toward_target_snaps_without_a_transition_and_clamps_a_long_delta() {
        assert_eq!(ease_toward_target(0.0, 1.0, 0.001, 0.0), 1.0);
        assert_eq!(ease_toward_target(0.0, 1.0, 1000.0, 10.0), 1.0);
    }

    /// OpenAC `AtmosphericPostProcessGraphTests.BuiltInShadowStrengthUsesTheDescriptorCurveExactlyOnce`:
    /// the shadow curve is linear in the sine between 1 and 12 degrees.
    #[test]
    fn the_shadow_curve_is_linear_in_the_sine_of_the_elevation() {
        let elevation = 6.0f32;
        let rad = |d: f32| d * std::f32::consts::PI / 180.0;
        let expected = (rad(elevation).sin() - rad(1.0).sin()) / (rad(12.0).sin() - rad(1.0).sin());
        let got = directional_shadow(&DIRECTIONAL_SHADOW_ELEVATION_RESPONSE, elevation, 0.0);
        assert!((got - expected).abs() < 1e-5, "{got} {expected}");
        assert_eq!(
            directional_shadow(&DIRECTIONAL_SHADOW_ELEVATION_RESPONSE, 0.5, 0.0),
            0.0
        );
        assert_eq!(
            directional_shadow(&DIRECTIONAL_SHADOW_ELEVATION_RESPONSE, 40.0, 0.0),
            1.0
        );
    }

    /// OpenAC `AtmosphericPostProcessGraphTests.AuthoredElevationDayGroupAndVisibilityGateSunEffects`
    /// (the elevation, day-group and weather terms of the sun policy).
    #[test]
    fn the_sun_terms_follow_elevation_day_group_and_weather() {
        assert!((ray(&SUN_ELEVATION_RESPONSE, 4.0, 1.0) - 1.0).abs() < 1e-3);
        assert_eq!(ray(&SUN_ELEVATION_RESPONSE, 55.0, 1.0), 0.0);
        assert!((day_group_multiplier(1) - 0.35).abs() < 1e-6);
        assert_eq!(day_group_multiplier(7), 1.0);
        assert!((weather_policy(WeatherKind::Overcast, 1.0) - 0.18).abs() < 1e-6);
        assert_eq!(weather_policy(WeatherKind::Overcast, 0.0), 1.0);
    }

    #[test]
    fn the_shaft_curve_eases_between_its_points() {
        let shaft = |e| volumetric_shaft(&VOLUMETRIC_SHAFT_ELEVATION_RESPONSE, e, 0.0);
        assert_eq!(shaft(-5.0), 0.0);
        assert_eq!(shaft(3.0), 0.5);
        assert_eq!(shaft(12.0), 1.0);
        assert!(shaft(1.0) < 1.0 / 6.0, "smooth start");
        assert_eq!(shaft(80.0), 0.0);
    }
}
