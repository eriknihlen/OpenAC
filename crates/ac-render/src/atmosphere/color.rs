//! The filmic colour path, on the CPU (the post shader's twin, for tests).
//!
//! The world is drawn as the game draws it, in display values; the post
//! decodes them to light (gamma 2.2), adds bloom, rays and shafts, scales by
//! the exposure, compresses highlights with the ACES fit (the "filmic"
//! curve), grades saturation and contrast around mid grey, darkens the
//! corners, and encodes back to display values.

use glam::Vec3;

/// The display gamma the decode and encode assume.
pub const DISPLAY_GAMMA: f32 = 2.2;
/// Mid grey in light values.
pub const LINEAR_MID_GREY: f32 = 0.18;
/// Where bloom starts, in light values.
pub const BLOOM_THRESHOLD_LINEAR: f32 = 1.0;
/// The soft knee below the threshold, in light values.
pub const BLOOM_KNEE_LINEAR: f32 = 0.73;
/// The vignette strength when none is set (the shipped default).
pub const DEFAULT_VIGNETTE_STRENGTH_FALLBACK: f32 = 0.245;

const REC709_LUMA: Vec3 = Vec3::new(0.2126, 0.7152, 0.0722);

fn pow(v: Vec3, e: f32) -> Vec3 {
    Vec3::new(v.x.powf(e), v.y.powf(e), v.z.powf(e))
}

/// Display value to light.
#[must_use]
pub fn decode(c: Vec3) -> Vec3 {
    pow(c.max(Vec3::ZERO), DISPLAY_GAMMA)
}

/// Light to display value.
#[must_use]
pub fn encode(c: Vec3) -> Vec3 {
    pow(c.max(Vec3::ZERO), 1.0 / DISPLAY_GAMMA)
}

/// The ACES filmic fit: highlights roll off instead of clipping.
#[must_use]
pub fn aces_fitted(v: Vec3) -> Vec3 {
    let (a, b, c, d, e) = (2.51, 0.03, 2.43, 0.59, 0.14);
    let numerator = v * (a * v + Vec3::splat(b));
    let denominator = v * (c * v + Vec3::splat(d)) + Vec3::splat(e);
    (numerator / denominator).clamp(Vec3::ZERO, Vec3::ONE)
}

/// Saturation (towards the grey of the same brightness) and contrast
/// (around mid grey).
#[must_use]
pub fn grade(color: Vec3, saturation: f32, contrast: f32) -> Vec3 {
    let luminance = color.dot(REC709_LUMA);
    let color = Vec3::splat(luminance).lerp(color, saturation);
    (color - Vec3::splat(LINEAR_MID_GREY)) * contrast + Vec3::splat(LINEAR_MID_GREY)
}

/// The whole tone curve for one light value.
#[must_use]
pub fn filmic(
    hdr: Vec3,
    exposure: f32,
    filmic_strength: f32,
    saturation: f32,
    contrast: f32,
    vignette_factor: f32,
) -> Vec3 {
    let exposed = (hdr * exposure).max(Vec3::ZERO);
    let linear_clamped = exposed.clamp(Vec3::ZERO, Vec3::ONE);
    let color = linear_clamped.lerp(aces_fitted(exposed), filmic_strength.clamp(0.0, 1.0));
    let color = grade(color, saturation, contrast) * vignette_factor;
    color.clamp(Vec3::ZERO, Vec3::ONE)
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.App.Tests/Rendering/Packs/AtmosphericColorPipelineTests.cs`.
    use super::*;
    use ac_host::settings::render_pack::setting_by_id;

    const EIGHT_BIT_HALF_STEP: f32 = 1.0 / 510.0;

    #[test]
    fn neutral_settings_reproduce_the_plain_pixel_within_half_an_eight_bit_step() {
        for step in 0..=255 {
            let c = step as f32 / 255.0;
            let r = encode(filmic(decode(Vec3::splat(c)), 1.0, 0.0, 1.0, 1.0, 1.0));
            for ch in [r.x, r.y, r.z] {
                assert!((ch - c).abs() < EIGHT_BIT_HALF_STEP, "c={c}: {ch}");
            }
        }
    }

    #[test]
    fn decode_and_encode_round_trip_within_float_precision() {
        for step in 0..=255 {
            let c = step as f32 / 255.0;
            let p = Vec3::splat(c);
            assert!((encode(decode(p)).x - c).abs() < 1e-6);
            assert!((decode(encode(p)).x - c).abs() < 1e-6);
        }
    }

    #[test]
    fn filmic_output_is_monotonic_in_exposure() {
        let hdr = decode(Vec3::splat(0.5));
        let mut previous = -1.0f32;
        let mut exposure = 0.1f32;
        while exposure <= 3.0 {
            let r = filmic(hdr, exposure, 1.0, 1.0, 1.0, 1.0);
            assert!(r.x >= previous - EIGHT_BIT_HALF_STEP, "{exposure}");
            previous = r.x;
            exposure += 0.1;
        }
    }

    #[test]
    fn the_accepted_exposure_reproduces_the_owner_gated_midtone() {
        let at = |v: f32| encode(filmic(decode(Vec3::splat(v)), 0.80, 1.0, 1.0, 1.0, 1.0)).x;
        assert!((at(0.46) - 0.50).abs() <= 0.02);
        assert!((at(0.9) - 0.85).abs() <= 0.02);
        assert!((at(0.1) - 0.05).abs() <= 0.02);
    }

    #[test]
    fn the_bloom_knee_is_the_decoded_gamma_space_range() {
        let lower = 0.55f32.powf(DISPLAY_GAMMA);
        assert!((lower - 0.27).abs() <= 0.01);
        let knee = BLOOM_THRESHOLD_LINEAR - lower;
        assert!((knee - BLOOM_KNEE_LINEAR).abs() < 0.005);
        assert_eq!(BLOOM_THRESHOLD_LINEAR, 1.0);
    }

    #[test]
    fn the_vignette_default_darkens_corners_by_twelve_percent() {
        let declared: f32 = setting_by_id("vignette-strength")
            .unwrap()
            .default
            .parse()
            .unwrap();
        assert!((declared - 0.245).abs() < 1e-3);
        assert_eq!(declared, DEFAULT_VIGNETTE_STRENGTH_FALLBACK);
        let shown = encode(Vec3::splat(1.0 - declared)).x;
        assert!((shown - 0.88).abs() <= 0.005);
        let exposure: f32 = setting_by_id("exposure").unwrap().default.parse().unwrap();
        assert!((exposure - 0.80).abs() < 1e-3);
    }
}
