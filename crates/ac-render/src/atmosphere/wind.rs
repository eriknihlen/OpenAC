//! Foliage wind: trees and bushes of the landscape's scenery sway.
//!
//! A tree's leaves are its clip-mapped (cut-out) sub-meshes and its trunk
//! the solid sub-meshes of a model that also has leaves; a rock (solid,
//! no leaves) does not move. The whole tree leans downwind and back, more
//! the higher a point is (by the square of its height in the canopy, so the
//! base never moves); leaves also swing and flutter. Gusts come and go on
//! two slow waves, and the wind's strength follows the weather.

use ac_core::sky::{WeatherKind, WeatherSystem};
use glam::{Vec2, Vec3, Vec4};

use super::policy::{FOLIAGE_WIND_BY_WEATHER, ease_toward_target, foliage_wind};

/// Leaves (a clip-mapped sub-mesh of procedural scenery).
pub const CUTOUT_FOLIAGE_FLAG: u32 = 0x2;
/// A trunk (a solid sub-mesh of procedural scenery that has leaves).
pub const TRUNK_FLAG: u32 = 0x4;

/// What a sub-mesh does in the wind (OpenAC `FoliageWindClassification.Classify`):
/// only procedural scenery moves; its clip-mapped sub-meshes are leaves,
/// its solid ones a trunk when the model has leaves somewhere.
#[must_use]
pub fn classify(
    is_procedural_scenery: bool,
    excluded: bool,
    clip_map: bool,
    opaque: bool,
    entity_has_cutout_subset: bool,
) -> u32 {
    if !is_procedural_scenery || excluded {
        0
    } else if clip_map {
        CUTOUT_FOLIAGE_FLAG
    } else if opaque && entity_has_cutout_subset {
        TRUNK_FLAG
    } else {
        0
    }
}

/// A full turn as the shader writes it (4 digits, not `TAU`): the flutter
/// direction keeps its exact values.
#[allow(clippy::approx_constant)]
const FLUTTER_TURN: f32 = 6.2832;

fn fract(x: f32) -> f32 {
    x - x.floor()
}

/// Where a world point of a swaying model moves to (OpenAC
/// `FoliageWindModel.Displace`, the shader's twin). `clock_wind` is
/// (seconds, mean, gust, direction radians), `amplitude` (lean, branch,
/// flutter, canopy height), metres.
#[must_use]
pub fn displace(
    world: Vec3,
    instance_origin: Vec3,
    flags: u32,
    clock_wind: Vec4,
    amplitude: Vec4,
) -> Vec3 {
    if flags & (CUTOUT_FOLIAGE_FLAG | TRUNK_FLAG) == 0 {
        return world;
    }
    let (t, mean, gust, angle) = (clock_wind.x, clock_wind.y, clock_wind.z, clock_wind.w);
    let dir = Vec2::new(angle.cos(), angle.sin());
    let perp = Vec2::new(-dir.y, dir.x);
    let h = ((world.z - instance_origin.z) / amplitude.w.max(0.5)).clamp(0.0, 1.0);
    let k = h * h;
    let ph = instance_origin.truncate().dot(Vec2::new(0.137, 0.291));
    let g = 0.5 + 0.5 * (0.05 * t + ph).sin() + 0.25 * (0.13 * t + 1.7 * ph).sin();
    let s = mean + gust * g;
    let lean = k * amplitude.x * s * (0.8 + 0.2 * (0.35 * t + ph).sin());
    let mut d = dir * lean;
    if flags & CUTOUT_FOLIAGE_FLAG != 0 {
        let branch = k * amplitude.y * s * (1.1 * t + ph + 2.0 * h).sin();
        let relative = world.truncate() - instance_origin.truncate();
        let vh = fract(relative.dot(Vec2::new(12.9898, 78.233)).sin() * 43758.547);
        let flutter = h * amplitude.z * s * (6.0 * t + 7.0 * vh).sin();
        d += dir * branch
            + perp * 0.35 * branch
            + Vec2::new((FLUTTER_TURN * vh).cos(), (FLUTTER_TURN * vh).sin()) * flutter;
    }
    let mut p = world;
    p.x += d.x;
    p.y += d.y;
    // Bending shortens the height instead of stretching the model.
    p.z -= 0.5 * d.dot(d) / (h * amplitude.w).max(0.5);
    p
}

/// The wind settings (OpenAC `FoliageWindSettings`).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct WindSettings {
    /// Wind on.
    pub enabled: bool,
    /// Multiplies the weather's wind.
    pub strength: f32,
    /// Where it blows to, degrees from east toward north.
    pub direction_degrees: f32,
    /// Lean at the canopy top, metres.
    pub lean_metres: f32,
    /// Branch swing, metres.
    pub branch_metres: f32,
    /// Leaf flutter, metres.
    pub flutter_metres: f32,
    /// Canopy height, metres.
    pub canopy_height_metres: f32,
}

/// The wind's state across frames: its clock and the eased mean and gust
/// (OpenAC `AtmosphericPostProcessGraph.ResolveFoliageWind`).
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct WindState {
    started: bool,
    clock_seconds: f32,
    last_advance_seconds: f32,
    mean: f32,
    gust: f32,
}

impl WindState {
    /// Advance to `clock_seconds` under `weather` (once per frame) and give
    /// the shader's two vectors: (seconds, mean, gust, direction radians)
    /// and (lean, branch, flutter, canopy height). Outdoors only; off and
    /// indoors the mean and gust are zero.
    pub fn advance(
        &mut self,
        settings: &WindSettings,
        clock_seconds: f32,
        weather: WeatherKind,
        is_outdoor: bool,
    ) -> (Vec4, Vec4) {
        let (mean, gust) = foliage_wind(Some(&FOLIAGE_WIND_BY_WEATHER), weather);
        let (target_mean, target_gust) = (mean * settings.strength, gust * settings.strength);
        if self.started {
            let dt = (clock_seconds - self.last_advance_seconds).clamp(0.0, 1.0);
            self.mean = ease_toward_target(
                self.mean,
                target_mean,
                dt,
                WeatherSystem::TRANSITION_SECONDS,
            );
            self.gust = ease_toward_target(
                self.gust,
                target_gust,
                dt,
                WeatherSystem::TRANSITION_SECONDS,
            );
        } else {
            self.mean = target_mean;
            self.gust = target_gust;
            self.started = true;
        }
        self.clock_seconds = clock_seconds;
        self.last_advance_seconds = clock_seconds;
        self.current(settings, is_outdoor)
    }

    /// The vectors as last advanced.
    #[must_use]
    pub fn current(&self, settings: &WindSettings, is_outdoor: bool) -> (Vec4, Vec4) {
        let gate = if settings.enabled && is_outdoor {
            1.0
        } else {
            0.0
        };
        (
            Vec4::new(
                self.clock_seconds,
                self.mean * gate,
                self.gust * gate,
                settings.direction_degrees.to_radians(),
            ),
            Vec4::new(
                settings.lean_metres,
                settings.branch_metres,
                settings.flutter_metres,
                settings.canopy_height_metres,
            ),
        )
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.App.Tests/Rendering/Packs/FoliageWindModelTests.cs`,
    //! `Wb/FoliageWindClassificationTests.cs` and the wind cases of
    //! `Packs/AtmosphericPostProcessGraphTests.cs`.
    use super::*;

    const WORLD: Vec3 = Vec3::new(12.0, -7.0, 5.0);
    const ORIGIN: Vec3 = Vec3::new(10.0, -8.0, 1.0);
    const AMPLITUDE: Vec4 = Vec4::new(0.25, 0.15, 0.05, 8.0);
    const CALM: Vec4 = Vec4::new(3.5, 0.0, 0.0, 3.9);

    fn near(a: Vec3, b: Vec3, tolerance: f32) -> bool {
        (a - b).length() <= tolerance
    }

    #[test]
    fn no_flags_is_identity_whatever_the_wind() {
        assert_eq!(
            displace(WORLD, ORIGIN, 0, Vec4::new(3.5, 1.0, 1.0, 3.9), AMPLITUDE),
            WORLD
        );
    }

    #[test]
    fn calm_wind_and_the_base_vertex_never_move() {
        for flags in [CUTOUT_FOLIAGE_FLAG, TRUNK_FLAG] {
            assert!(near(
                displace(WORLD, ORIGIN, flags, CALM, AMPLITUDE),
                WORLD,
                1e-4
            ));
            let base = Vec3::new(ORIGIN.x + 3.0, ORIGIN.y, ORIGIN.z);
            let windy = Vec4::new(11.0, 1.0, 1.0, 2.1);
            assert!(near(
                displace(base, ORIGIN, flags, windy, AMPLITUDE),
                base,
                1e-4
            ));
        }
    }

    #[test]
    fn the_canopy_top_moves_no_more_than_the_amplitudes() {
        let bound = AMPLITUDE.x + 1.35 * AMPLITUDE.y + AMPLITUDE.z + 1e-4;
        let mut t = 0.0f32;
        while t < 40.0 {
            let mut xy = -5.0f32;
            while xy <= 5.0 {
                let top = Vec3::new(ORIGIN.x + xy, ORIGIN.y - xy, ORIGIN.z + AMPLITUDE.w);
                let r = displace(
                    top,
                    ORIGIN,
                    CUTOUT_FOLIAGE_FLAG,
                    Vec4::new(t, 1.0, 0.0, 0.0),
                    AMPLITUDE,
                );
                assert!((r - top).truncate().length() <= bound, "t={t} xy={xy}");
                xy += 4.3;
            }
            t += 3.7;
        }
    }

    #[test]
    fn the_height_never_increases() {
        // A small LCG stands in for the test's seeded .NET Random.
        let mut seed = 1337u32;
        let mut next = || {
            seed = seed.wrapping_mul(1_664_525).wrapping_add(1_013_904_223);
            (seed >> 8) as f32 / (1u32 << 24) as f32
        };
        for i in 0..200 {
            let world = Vec3::new(
                next() * 40.0 - 20.0,
                next() * 40.0 - 20.0,
                next() * AMPLITUDE.w,
            );
            let wind = Vec4::new(
                next() * 1000.0,
                next(),
                next(),
                next() * std::f32::consts::TAU,
            );
            let flags = if next() < 0.5 {
                CUTOUT_FOLIAGE_FLAG
            } else {
                TRUNK_FLAG
            };
            let r = displace(world, ORIGIN, flags, wind, AMPLITUDE);
            assert!(r.z <= world.z + 1e-5, "iteration {i}");
        }
    }

    #[test]
    fn the_trunk_ignores_the_point_hash_and_the_leaves_do_not() {
        let z = ORIGIN.z + 0.5 * AMPLITUDE.w;
        let wind = Vec4::new(7.25, 0.8, 0.6, 1.1);
        let moved = |flags, x: f32, y: f32| {
            let p = Vec3::new(x, y, z);
            displace(p, ORIGIN, flags, wind, AMPLITUDE) - p
        };
        let reference = moved(TRUNK_FLAG, ORIGIN.x, ORIGIN.y);
        assert!(near(
            reference,
            moved(TRUNK_FLAG, ORIGIN.x + 500.0, ORIGIN.y - 300.0),
            1e-4
        ));
        assert!(near(
            reference,
            moved(TRUNK_FLAG, ORIGIN.x - 17.3, ORIGIN.y + 91.0),
            1e-4
        ));
        assert_ne!(
            moved(CUTOUT_FOLIAGE_FLAG, ORIGIN.x, ORIGIN.y),
            moved(CUTOUT_FOLIAGE_FLAG, ORIGIN.x + 500.0, ORIGIN.y - 300.0)
        );
    }

    #[test]
    fn the_flutter_hash_is_relative_to_the_tree() {
        let local = Vec3::new(3.7, -2.1, 4.0);
        let far_origin = ORIGIN + Vec3::new(0.291, -0.137, 0.0) * 50_000.0;
        let wind = Vec4::new(19.5, 0.8, 0.6, 2.3);
        let a = displace(ORIGIN + local, ORIGIN, CUTOUT_FOLIAGE_FLAG, wind, AMPLITUDE)
            - (ORIGIN + local);
        let b = displace(
            far_origin + local,
            far_origin,
            CUTOUT_FOLIAGE_FLAG,
            wind,
            AMPLITUDE,
        ) - (far_origin + local);
        assert!(near(a, b, 1e-3));
    }

    #[test]
    fn storm_wind_moves_mid_height_leaves_by_more_than_a_centimetre() {
        let mid = Vec3::new(ORIGIN.x + 2.4, ORIGIN.y - 1.1, ORIGIN.z + 0.5 * AMPLITUDE.w);
        let mut t = 0.0f32;
        while t < 30.0 {
            let r = displace(
                mid,
                ORIGIN,
                CUTOUT_FOLIAGE_FLAG,
                Vec4::new(t, 1.0, 0.75, 0.0),
                AMPLITUDE,
            );
            assert!((r - mid).length() > 0.01, "t={t}");
            t += 2.9;
        }
    }

    #[test]
    fn only_procedural_scenery_sways_by_its_material() {
        assert_eq!(
            classify(true, false, true, false, false),
            CUTOUT_FOLIAGE_FLAG
        );
        assert_eq!(classify(true, false, false, true, true), TRUNK_FLAG);
        assert_eq!(classify(true, false, false, true, false), 0, "a rock");
        assert_eq!(classify(false, false, true, false, true), 0);
        assert_eq!(classify(true, true, true, false, true), 0, "excluded");
        assert_eq!(classify(true, false, false, false, true), 0, "blended");
    }

    fn settings() -> WindSettings {
        WindSettings {
            enabled: true,
            strength: 1.0,
            direction_degrees: 225.0,
            lean_metres: 0.25,
            branch_metres: 0.15,
            flutter_metres: 0.05,
            canopy_height_metres: 8.0,
        }
    }

    #[test]
    fn the_first_advance_snaps_to_the_weather_and_then_eases() {
        let mut w = WindState::default();
        let (clock, amp) = w.advance(&settings(), 0.0, WeatherKind::Clear, true);
        assert_eq!((clock.y, clock.z), (0.25, 0.15));
        assert_eq!(amp, Vec4::new(0.25, 0.15, 0.05, 8.0));
        assert!((clock.w - 225f32.to_radians()).abs() < 1e-5);
        let (clock, _) = w.advance(&settings(), 1.0, WeatherKind::Storm, true);
        let rate = 1.0 / 10.0;
        assert!((clock.y - (0.25 + (1.00 - 0.25) * rate)).abs() < 1e-5);
        assert!((clock.z - (0.15 + (0.75 - 0.15) * rate)).abs() < 1e-5);
        let mut storm = WindState::default();
        let (clock, _) = storm.advance(&settings(), 12.0, WeatherKind::Storm, true);
        assert_eq!((clock.y, clock.z), (1.0, 0.75));
    }

    #[test]
    fn indoors_or_off_the_wind_is_exactly_zero() {
        let mut w = WindState::default();
        let (clock, _) = w.advance(&settings(), 12.0, WeatherKind::Storm, false);
        assert_eq!((clock.y, clock.z), (0.0, 0.0));
        let off = WindSettings {
            enabled: false,
            ..settings()
        };
        let (clock, _) = WindState::default().advance(&off, 12.0, WeatherKind::Storm, true);
        assert_eq!((clock.y, clock.z), (0.0, 0.0));
    }
}
