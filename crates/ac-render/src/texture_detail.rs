//! The texture-detail options' effect on texture size, fixed when a window's
//! world starts. The two stored options run 0..4, 0 the highest detail:
//! 0 and 1 keep the source size, 2 halves it, 3 quarters it, 4 takes an
//! eighth. Object ("environment") textures shift by that step; landscape
//! textures use a wider table, so their lowest step is a sixteenth. A
//! reduced side never goes below 8 pixels unless the source is smaller.
//! Terrain alpha maps, the interface, particles and the sky keep their size.

use ac_dat::texture::DecodedTexture;
use ac_host::settings::DisplaySettings;

/// How far a texture is reduced from its source size.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ImageScale {
    /// The source size.
    #[default]
    Full = 0,
    /// Half.
    Half = 1,
    /// A quarter.
    Quarter = 2,
    /// An eighth.
    Eighth = 3,
}

/// The smallest side a reduced texture keeps, when the source has it.
pub const MINIMUM_SIZE: u32 = 8;

/// Indexed by [`ImageScale`]; one entry wider than the enum, as the
/// source table is.
const LANDSCAPE_SHIFT_TABLE: [u32; 5] = [0, 1, 2, 4, 8];

impl ImageScale {
    /// The scale a stored texture-detail option selects.
    #[must_use]
    pub fn from_option(detail: i32) -> Self {
        match detail {
            i32::MIN..=1 => ImageScale::Full,
            2 => ImageScale::Half,
            3 => ImageScale::Quarter,
            _ => ImageScale::Eighth,
        }
    }

    /// Bits an environment (object, building, creature) texture side
    /// shifts right by.
    #[must_use]
    pub fn environment_shift(self) -> u32 {
        self as u32
    }

    /// Bits a landscape texture side shifts right by.
    #[must_use]
    pub fn landscape_shift(self) -> u32 {
        LANDSCAPE_SHIFT_TABLE[self as usize]
    }
}

/// One texture side after `shift`: never below [`MINIMUM_SIZE`] unless the
/// source side is smaller, never below 1. `size` 0 stays 0.
#[must_use]
pub fn reduce(size: u32, shift: u32) -> u32 {
    if shift == 0 || size == 0 {
        return size;
    }
    let mut reduced = size.checked_shr(shift).unwrap_or(0);
    if reduced < MINIMUM_SIZE {
        reduced = MINIMUM_SIZE.min(size);
    }
    reduced.max(1)
}

/// The texture detail a world runs with.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct WorldTextureDetail {
    /// Terrain textures.
    pub landscape: ImageScale,
    /// Object, building and creature textures.
    pub environment: ImageScale,
}

impl WorldTextureDetail {
    /// Full detail everywhere.
    pub const FULL: Self = Self {
        landscape: ImageScale::Full,
        environment: ImageScale::Full,
    };

    /// The detail of `display` (already effective: Potato Mode applied).
    #[must_use]
    pub fn from_display(display: &DisplaySettings) -> Self {
        Self {
            landscape: ImageScale::from_option(display.landscape_texture_detail),
            environment: ImageScale::from_option(display.environment_texture_detail),
        }
    }

    /// Whether anything is reduced.
    #[must_use]
    pub fn reduces_anything(&self) -> bool {
        *self != Self::FULL
    }

    /// One side of a landscape texture at this detail.
    #[must_use]
    pub fn landscape_size(&self, size: u32) -> u32 {
        reduce(size, self.landscape.landscape_shift())
    }

    /// One side of an environment texture at this detail.
    #[must_use]
    pub fn environment_size(&self, size: u32) -> u32 {
        reduce(size, self.environment.environment_shift())
    }

    /// A decoded texture at the environment scale, box-averaged; `None`
    /// when nothing shrinks.
    #[must_use]
    pub fn reduce_environment(&self, decoded: &DecodedTexture) -> Option<DecodedTexture> {
        if self.environment == ImageScale::Full {
            return None;
        }
        let width = self.environment_size(decoded.width);
        let height = self.environment_size(decoded.height);
        if width == decoded.width && height == decoded.height {
            return None;
        }
        Some(DecodedTexture {
            width,
            height,
            rgba: downsample_box(
                &decoded.rgba,
                decoded.width,
                decoded.height,
                4,
                width,
                height,
            ),
        })
    }
}

/// Average the source pixels covering each destination pixel, per channel
/// (rounding half up). The destination is no larger than the source.
#[must_use]
pub fn downsample_box(
    source: &[u8],
    width: u32,
    height: u32,
    channels: usize,
    dst_width: u32,
    dst_height: u32,
) -> Vec<u8> {
    debug_assert!(dst_width <= width && dst_height <= height);
    if dst_width == width && dst_height == height {
        return source.to_vec();
    }
    let (w, dw, dh) = (width as usize, dst_width as usize, dst_height as usize);
    let h = height as usize;
    let mut out = vec![0u8; dw * dh * channels];
    let mut sums = vec![0u32; channels];
    for dy in 0..dh {
        let y0 = dy * h / dh;
        let y1 = ((dy + 1) * h / dh).max(y0 + 1);
        for dx in 0..dw {
            let x0 = dx * w / dw;
            let x1 = ((dx + 1) * w / dw).max(x0 + 1);
            sums.fill(0);
            for y in y0..y1 {
                for x in x0..x1 {
                    let i = (y * w + x) * channels;
                    for (c, sum) in sums.iter_mut().enumerate() {
                        *sum += u32::from(source[i + c]);
                    }
                }
            }
            let n = ((y1 - y0) * (x1 - x0)) as u32;
            let o = (dy * dw + dx) * channels;
            for (c, sum) in sums.iter().enumerate() {
                out[o + c] = ((sum + n / 2) / n) as u8;
            }
        }
    }
    out
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Textures/TextureDetailScaleTests.cs`
    //! (with `TexturePixelsTests`) and
    //! `tests/AcDream.App.Tests/Rendering/Wb/WorldTextureDetailTests.cs` (the
    //! decoded-texture and uncompressed cases; the compressed case is in
    //! `surface_image::tests`).

    use super::*;
    use crate::quality::effective_display;

    #[test]
    fn the_stored_value_maps_to_a_scale_and_0_and_1_keep_full_size() {
        for (detail, expected) in [
            (-1, ImageScale::Full),
            (0, ImageScale::Full),
            (1, ImageScale::Full),
            (2, ImageScale::Half),
            (3, ImageScale::Quarter),
            (4, ImageScale::Eighth),
            (9, ImageScale::Eighth),
        ] {
            assert_eq!(ImageScale::from_option(detail), expected, "{detail}");
        }
    }

    #[test]
    fn environment_shifts_by_the_scale_and_landscape_by_the_wider_table() {
        for (scale, environment, landscape) in [
            (ImageScale::Full, 0, 0),
            (ImageScale::Half, 1, 1),
            (ImageScale::Quarter, 2, 2),
            (ImageScale::Eighth, 3, 4),
        ] {
            assert_eq!(scale.environment_shift(), environment);
            assert_eq!(scale.landscape_shift(), landscape);
        }
    }

    #[test]
    fn reduce_shifts_and_keeps_the_floor() {
        for (size, shift, expected) in [
            (512, 0, 512),
            (512, 3, 64),
            (512, 4, 32),
            (32, 3, 8),
            (64, 3, 8),
            (4, 3, 4),
            (12, 1, 8),
            (1, 8, 1),
        ] {
            assert_eq!(reduce(size, shift), expected, "{size} >> {shift}");
        }
    }

    #[test]
    fn the_box_filter_averages_each_box_per_channel() {
        let mut checker = [0u8; 16];
        for y in 0..4 {
            for x in 0..4 {
                checker[y * 4 + x] = if (x + y) % 2 == 0 { 0 } else { 200 };
            }
        }
        assert_eq!(downsample_box(&checker, 4, 4, 1, 2, 2), [100; 4]);
        let rows = [
            255, 0, 0, 255, 255, 0, 0, 255, 0, 0, 255, 255, 0, 0, 255, 255,
        ];
        assert_eq!(downsample_box(&rows, 2, 2, 4, 1, 1), [128, 0, 128, 255]);
        let flat = [77u8; 12 * 3];
        let out = downsample_box(&flat, 12, 1, 3, 8, 1);
        assert_eq!(out.len(), 8 * 3);
        assert!(out.iter().all(|&v| v == 77));
        let same = [1u8, 2, 3, 4];
        assert_eq!(downsample_box(&same, 2, 2, 1, 2, 2), same);
    }

    #[test]
    fn the_defaults_keep_full_detail_and_potato_takes_the_lowest() {
        let defaults = DisplaySettings::default();
        assert_eq!(
            WorldTextureDetail::from_display(&defaults),
            WorldTextureDetail::FULL
        );
        assert!(!WorldTextureDetail::FULL.reduces_anything());
        let potato = WorldTextureDetail::from_display(&effective_display(&DisplaySettings {
            potato_mode: true,
            ..defaults
        }));
        assert_eq!(potato.landscape, ImageScale::Eighth);
        assert_eq!(potato.environment, ImageScale::Eighth);
        assert!(potato.reduces_anything());
        assert_eq!(potato.environment_size(512), 64);
        assert_eq!(potato.landscape_size(512), 32);
    }

    #[test]
    fn a_decoded_texture_shrinks_at_the_environment_scale_and_tiny_ones_stay() {
        let detail = WorldTextureDetail {
            landscape: ImageScale::Full,
            environment: ImageScale::Eighth,
        };
        let decoded = DecodedTexture {
            width: 128,
            height: 64,
            rgba: vec![33; 128 * 64 * 4],
        };
        let reduced = detail.reduce_environment(&decoded).unwrap();
        assert_eq!((reduced.width, reduced.height), (16, 8));
        assert_eq!(reduced.rgba.len(), 16 * 8 * 4);
        assert!(reduced.rgba.iter().all(|&v| v == 33));
        let magenta = DecodedTexture {
            width: 1,
            height: 1,
            rgba: vec![255, 0, 255, 255],
        };
        assert!(detail.reduce_environment(&magenta).is_none());
        assert!(
            WorldTextureDetail::FULL
                .reduce_environment(&decoded)
                .is_none()
        );
        let quarter = WorldTextureDetail {
            landscape: ImageScale::Full,
            environment: ImageScale::Quarter,
        };
        let layer = DecodedTexture {
            width: 64,
            height: 32,
            rgba: vec![90; 64 * 32 * 4],
        };
        let r = quarter.reduce_environment(&layer).unwrap();
        assert_eq!((r.width, r.height), (16, 8));
        assert!(r.rgba.iter().all(|&v| v == 90));
        let eight = DecodedTexture {
            width: 8,
            height: 8,
            rgba: vec![0; 8 * 8 * 4],
        };
        assert!(detail.reduce_environment(&eight).is_none(), "the floor");
    }
}
