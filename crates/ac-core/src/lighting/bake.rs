//! Static lights baked into a cell's vertex colours.
//!
//! Each static light adds at most its own colour to a vertex: the light's
//! reach is a hard cutoff, the facing term is "half-Lambert" (a surface
//! still catches some light up to a little past 90 degrees away from it),
//! and the strength falls off with the cube of the distance beyond one
//! metre. The lights are summed from black and the sum is clamped to 1;
//! ambient and sun are added separately.

use glam::Vec3;

use super::source::{LightKind, LightSource};

/// Twice the point-light range constant.
const TWO_LPR: f32 = 1.5;
/// Wrap bias: twice the range constant, minus one.
const WRAP_BIAS: f32 = 0.5;

/// What one point light adds to a vertex at `vtx_world_pos` with normal
/// `vtx_world_normal` (not normalised; its length scales the facing term).
#[must_use]
pub fn point_contribution(
    vtx_world_pos: Vec3,
    vtx_world_normal: Vec3,
    light: &LightSource,
) -> Vec3 {
    // From the vertex to the light, used unnormalised.
    let dx = light.world_position.x - vtx_world_pos.x;
    let dy = light.world_position.y - vtx_world_pos.y;
    let dz = light.world_position.z - vtx_world_pos.z;

    let distsq = dx * dx + dy * dy + dz * dz;
    let dist = distsq.sqrt();
    let falloff_eff = light.range;
    if dist >= falloff_eff || falloff_eff <= 1e-4 {
        return Vec3::ZERO;
    }

    let wrap = (1.0 / TWO_LPR)
        * (vtx_world_normal.x * dx
            + vtx_world_normal.y * dy
            + vtx_world_normal.z * dz
            + WRAP_BIAS * dist);
    if wrap <= 0.0 {
        return Vec3::ZERO;
    }

    let norm = if distsq > 1.0 { distsq * dist } else { dist };
    let scale = (1.0 - dist / falloff_eff) * light.intensity * (wrap / norm);

    Vec3::new(
        min_like_dotnet(scale * light.color_linear.x, light.color_linear.x),
        min_like_dotnet(scale * light.color_linear.y, light.color_linear.y),
        min_like_dotnet(scale * light.color_linear.z, light.color_linear.z),
    )
}

/// The baked colour of a vertex: every lit point or spot light in
/// `reaching` summed, each channel clamped to 0..=1.
pub fn compute_vertex_color<'a>(
    vtx_world_pos: Vec3,
    vtx_world_normal: Vec3,
    reaching: impl IntoIterator<Item = &'a LightSource>,
) -> Vec3 {
    let (mut r, mut g, mut b) = (0.0f32, 0.0f32, 0.0f32);
    for light in reaching {
        if !light.is_lit || light.kind == LightKind::Directional {
            continue;
        }
        let c = point_contribution(vtx_world_pos, vtx_world_normal, light);
        r += c.x;
        g += c.y;
        b += c.z;
    }
    Vec3::new(r.clamp(0.0, 1.0), g.clamp(0.0, 1.0), b.clamp(0.0, 1.0))
}

/// The smaller value; a NaN in either argument gives NaN.
fn min_like_dotnet(a: f32, b: f32) -> f32 {
    if a.is_nan() || b.is_nan() {
        f32::NAN
    } else if a < b {
        a
    } else {
        b
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `LightBakeTests` and `LightBakeConformanceTests`; the exact
    //! values marked "OpenAC" were printed by OpenAC's `LightBake`.
    use super::*;

    fn torch(pos: Vec3, intensity: f32, range: f32) -> LightSource {
        LightSource {
            kind: LightKind::Point,
            world_position: pos,
            color_linear: Vec3::ONE,
            intensity,
            range,
            is_lit: true,
            ..LightSource::default()
        }
    }

    fn near(a: f32, b: f32, eps: f32) -> bool {
        (a - b).abs() <= eps
    }

    #[test]
    fn near_torch_facing_it_saturates_to_color() {
        let c = point_contribution(
            Vec3::ZERO,
            Vec3::Z,
            &torch(Vec3::new(0.0, 0.0, 2.0), 100.0, 10.0),
        );
        assert_eq!(c, Vec3::ONE);
    }

    #[test]
    fn far_torch_falls_off_smoothly() {
        // 8 m above, range 10: scale = (1 - 0.8) * 100 * (8 / 512) = 0.3125.
        let c = point_contribution(
            Vec3::ZERO,
            Vec3::Z,
            &torch(Vec3::new(0.0, 0.0, 8.0), 100.0, 10.0),
        );
        assert!(near(c.x, 0.3125, 1e-4) && near(c.y, 0.3125, 1e-4) && near(c.z, 0.3125, 1e-4));
    }

    #[test]
    fn out_of_range_contributes_nothing() {
        let c = point_contribution(
            Vec3::ZERO,
            Vec3::Z,
            &torch(Vec3::new(0.0, 0.0, 11.0), 100.0, 10.0),
        );
        assert_eq!(c, Vec3::ZERO);
    }

    #[test]
    fn facing_away_beyond_wrap_contributes_nothing() {
        let c = point_contribution(
            Vec3::ZERO,
            -Vec3::Z,
            &torch(Vec3::new(0.0, 0.0, 2.0), 100.0, 10.0),
        );
        assert_eq!(c, Vec3::ZERO);
    }

    #[test]
    fn half_lambert_wrap_lights_a_surface_angled_past_90_degrees() {
        let t = 100.0f64.to_radians();
        let n = Vec3::new(t.sin() as f32, 0.0, t.cos() as f32);
        let c = point_contribution(Vec3::ZERO, n, &torch(Vec3::new(0.0, 0.0, 2.0), 100.0, 10.0));
        assert!(c.x > 0.0);
    }

    #[test]
    fn vertex_color_sums_lights_and_clamps_to_one() {
        let l = torch(Vec3::new(0.0, 0.0, 2.0), 100.0, 10.0);
        assert_eq!(
            compute_vertex_color(Vec3::ZERO, Vec3::Z, [&l, &l]),
            Vec3::ONE
        );
    }

    #[test]
    fn vertex_color_skips_directional_and_unlit() {
        let sun = LightSource {
            kind: LightKind::Directional,
            ..torch(Vec3::new(0.0, 0.0, 2.0), 100.0, 10.0)
        };
        let off = LightSource {
            is_lit: false,
            ..torch(Vec3::new(0.0, 0.0, 2.0), 100.0, 10.0)
        };
        assert_eq!(
            compute_vertex_color(Vec3::ZERO, Vec3::Z, [&sun, &off]),
            Vec3::ZERO
        );
    }

    fn orange_torch(pos: Vec3) -> LightSource {
        LightSource {
            kind: LightKind::Point,
            world_position: pos,
            color_linear: Vec3::new(1.0, 0.588, 0.314),
            intensity: 100.0,
            range: 4.0 * 1.3,
            is_lit: true,
            ..LightSource::default()
        }
    }

    #[test]
    fn a_single_orange_torch_is_warm_and_bounded_never_white() {
        for dist in [1.0, 2.0, 3.0, 4.0, 5.0] {
            let c = compute_vertex_color(
                Vec3::ZERO,
                Vec3::X,
                [&orange_torch(Vec3::new(dist, 0.0, 0.0))],
            );
            for v in [c.x, c.y, c.z] {
                assert!((0.0..=1.0).contains(&v));
            }
            if c.x > 0.0 {
                assert!(c.x >= c.y && c.y >= c.z, "{c} at d={dist}");
            }
        }
    }

    #[test]
    fn beyond_range_contributes_nothing() {
        let c = compute_vertex_color(
            Vec3::ZERO,
            Vec3::X,
            [&orange_torch(Vec3::new(100.0, 0.0, 0.0))],
        );
        assert_eq!(c, Vec3::ZERO);
    }

    #[test]
    fn many_overlapping_intense_torches_still_clamp_to_one() {
        let lights: Vec<LightSource> = (0..8)
            .map(|i| LightSource {
                kind: LightKind::Point,
                world_position: Vec3::new(1.5, 0.1 * i as f32, 0.0),
                color_linear: Vec3::new(0.98, 0.95, 0.9),
                intensity: 100.0,
                range: 5.2,
                is_lit: true,
                ..LightSource::default()
            })
            .collect();
        let c = compute_vertex_color(Vec3::ZERO, Vec3::X, &lights);
        for v in [c.x, c.y, c.z] {
            assert!((0.0..=1.0).contains(&v));
        }
    }

    #[test]
    fn matches_openac_values() {
        // OpenAC: a 100-intensity orange torch at (1.5, 0.7, 0.3), range 5.2,
        // is capped at its own colour for these vertices, and gives exactly
        // nothing where the normal faces away.
        let mut t = orange_torch(Vec3::new(1.5, 0.7, 0.3));
        t.range = 5.2;
        let capped = Vec3::new(1.0, 0.588, 0.314);
        assert_eq!(point_contribution(Vec3::ZERO, Vec3::X, &t), capped);
        assert_eq!(
            point_contribution(Vec3::new(0.0, 2.0, 0.0), Vec3::Y, &t),
            Vec3::ZERO
        );
        assert_eq!(
            point_contribution(Vec3::new(-1.0, 0.0, 0.0), Vec3::new(0.6, 0.8, 0.0), &t),
            capped
        );
        assert_eq!(
            point_contribution(Vec3::new(0.0, 0.0, -2.0), Vec3::new(0.0, 0.0, 0.5), &t),
            capped
        );

        // OpenAC: a dim cream light 3 m away, bit-exact, and two of them summed.
        let dim = LightSource {
            kind: LightKind::Point,
            world_position: Vec3::new(3.0, 0.0, 0.0),
            color_linear: Vec3::new(0.98, 0.843, 0.612),
            intensity: 1.2,
            range: 6.5,
            is_lit: true,
            ..LightSource::default()
        };
        let one = point_contribution(Vec3::ZERO, Vec3::X, &dim);
        assert_eq!(
            one.to_array().map(f32::to_bits),
            [0x3D90_185F, 0x3D77_E70E, 0x3D33_F8D2]
        );
        let two = compute_vertex_color(Vec3::ZERO, Vec3::X, [&dim, &dim]);
        assert_eq!(
            two.to_array().map(f32::to_bits),
            [0x3E10_185F, 0x3DF7_E70E, 0x3DB3_F8D2]
        );
    }
}
