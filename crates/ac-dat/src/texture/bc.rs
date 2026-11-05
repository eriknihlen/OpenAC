//! Block-compressed textures (DXT1, DXT3, DXT5; also called BC1, BC2,
//! BC3).
//!
//! Each 4×4 pixel block stores two 16-bit colours and a 2-bit index per
//! pixel that picks one of four colours on the line between them. DXT3 adds
//! a 4-bit alpha per pixel; DXT5 adds two 8-bit alpha endpoints and a 3-bit
//! index per pixel. In-between colours are computed in floating point and
//! truncated, which decides the exact low bits.

use super::TextureError;

/// Which block layout.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(super) enum Kind {
    Dxt1,
    Dxt3,
    Dxt5,
}

impl Kind {
    fn block_size(self) -> usize {
        match self {
            Self::Dxt1 => 8,
            Self::Dxt3 | Self::Dxt5 => 16,
        }
    }
}

/// `((den - num) * a + num * b + correction) / den`, divided in `f32` and
/// truncated.
fn interpolate(a: u8, b: u8, num: i32, den: i32, correction: i32) -> u8 {
    let n = (den - num) * i32::from(a) + num * i32::from(b) + correction;
    (n as f32 / den as f32) as i32 as u8
}

/// A 5:6:5 colour widened to 8 bits per channel by repeating the top bits.
fn rgb565(c: u16) -> [u8; 3] {
    let r = ((c & 0xF800) >> 11) as u8;
    let g = ((c & 0x07E0) >> 5) as u8;
    let b = (c & 0x1F) as u8;
    [
        (r << 3) | (r >> 2),
        (g << 2) | (g >> 4),
        (b << 3) | (b >> 2),
    ]
}

fn lerp_rgb(a: [u8; 3], b: [u8; 3], num: i32, den: i32) -> [u8; 3] {
    [
        interpolate(a[0], b[0], num, den, 0),
        interpolate(a[1], b[1], num, den, 0),
        interpolate(a[2], b[2], num, den, 0),
    ]
}

/// The colour part of a block: 16 RGBA pixels with alpha 255.
fn color_block(block: &[u8], three_color_mode: bool) -> [[u8; 4]; 16] {
    let c0 = u16::from_le_bytes([block[0], block[1]]);
    let c1 = u16::from_le_bytes([block[2], block[3]]);
    let indices = u32::from_le_bytes([block[4], block[5], block[6], block[7]]);
    let (a, b) = (rgb565(c0), rgb565(c1));
    let palette = if three_color_mode && c0 <= c1 {
        [a, b, lerp_rgb(a, b, 1, 2), [0, 0, 0]]
    } else {
        [a, b, lerp_rgb(a, b, 1, 3), lerp_rgb(a, b, 2, 3)]
    };
    let mut out = [[0u8; 4]; 16];
    for (i, px) in out.iter_mut().enumerate() {
        let [r, g, b] = palette[((indices >> (i * 2)) & 3) as usize];
        *px = [r, g, b, 255];
    }
    out
}

/// Eight alpha levels from two endpoints and 3-bit indices (DXT5).
fn interpolated_alpha(block: &[u8]) -> [u8; 16] {
    let bits = u64::from_le_bytes(block[..8].try_into().unwrap_or([0; 8]));
    let (e0, e1) = (block[0], block[1]);
    let levels = if e0 <= e1 {
        let f = |n| interpolate(e0, e1, n, 5, 2);
        [e0, e1, f(1), f(2), f(3), f(4), 0, 255]
    } else {
        let s = |n| interpolate(e0, e1, n, 7, 3);
        [e0, e1, s(1), s(2), s(3), s(4), s(5), s(6)]
    };
    let mut out = [0u8; 16];
    for (i, a) in out.iter_mut().enumerate() {
        *a = levels[((bits >> (16 + 3 * i)) & 7) as usize];
    }
    out
}

/// Decode a block-compressed image to RGBA. With `clip_map`, pure black
/// pixels become fully transparent.
pub(super) fn decode(
    data: &[u8],
    width: usize,
    height: usize,
    kind: Kind,
    clip_map: bool,
) -> Result<Vec<u8>, TextureError> {
    let size = kind.block_size();
    if !data.len().is_multiple_of(size) {
        return Err(TextureError::PartialBlock {
            len: data.len(),
            block: size,
        });
    }
    let blocks_wide = width.div_ceil(4);
    let blocks_high = height.div_ceil(4);
    let needed = blocks_wide
        .checked_mul(blocks_high)
        .and_then(|n| n.checked_mul(size))
        .ok_or(TextureError::TooLarge { width, height })?;
    if data.len() < needed {
        return Err(TextureError::DataTooShort {
            needed,
            len: data.len(),
        });
    }
    let mut out = vec![0u8; width * height * 4];
    for by in 0..blocks_high {
        for bx in 0..blocks_wide {
            let at = (by * blocks_wide + bx) * size;
            let block = &data[at..at + size];
            let pixels = match kind {
                Kind::Dxt1 => color_block(block, true),
                Kind::Dxt3 => {
                    let mut p = color_block(&block[8..], false);
                    let alpha = u64::from_le_bytes(block[..8].try_into().unwrap_or([0; 8]));
                    for (i, px) in p.iter_mut().enumerate() {
                        px[3] = ((alpha >> (4 * i)) & 15) as u8 * 17;
                    }
                    p
                }
                Kind::Dxt5 => {
                    let mut p = color_block(&block[8..], false);
                    let alpha = interpolated_alpha(&block[..8]);
                    for (px, a) in p.iter_mut().zip(alpha) {
                        px[3] = a;
                    }
                    p
                }
            };
            for (i, px) in pixels.iter().enumerate() {
                let (x, y) = (bx * 4 + i % 4, by * 4 + i / 4);
                if x >= width || y >= height {
                    continue;
                }
                let o = (y * width + x) * 4;
                out[o..o + 4].copy_from_slice(px);
                if clip_map && px[..3] == [0, 0, 0] {
                    out[o + 3] = 0;
                }
            }
        }
    }
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn block1(c0: u16, c1: u16, indices: u32) -> Vec<u8> {
        let mut b = Vec::new();
        b.extend_from_slice(&c0.to_le_bytes());
        b.extend_from_slice(&c1.to_le_bytes());
        b.extend_from_slice(&indices.to_le_bytes());
        b
    }

    fn px(out: &[u8], i: usize) -> [u8; 4] {
        out[i * 4..i * 4 + 4].try_into().unwrap()
    }

    #[test]
    fn widening_repeats_the_top_bits() {
        assert_eq!(rgb565(0xFFFF), [255, 255, 255]);
        assert_eq!(rgb565(0x0000), [0, 0, 0]);
        // r = 10 -> 82, g = 33 -> 134, b = 5 -> 41.
        assert_eq!(rgb565((10 << 11) | (33 << 5) | 5), [82, 134, 41]);
    }

    #[test]
    fn four_colour_block() {
        // c0 = white > c1 = black: indices 0,1,2,3 on the first row.
        let data = block1(0xFFFF, 0x0000, 0b11_10_01_00);
        let out = decode(&data, 4, 4, Kind::Dxt1, false).unwrap();
        assert_eq!(px(&out, 0), [255, 255, 255, 255]);
        assert_eq!(px(&out, 1), [0, 0, 0, 255]);
        // (2*255 + 0) / 3 = 170, (255 + 0) / 3 = 85.
        assert_eq!(px(&out, 2), [170, 170, 170, 255]);
        assert_eq!(px(&out, 3), [85, 85, 85, 255]);
        // Index 0 everywhere else.
        assert_eq!(px(&out, 15), [255, 255, 255, 255]);
    }

    #[test]
    fn three_colour_block_has_opaque_black() {
        let data = block1(0x0000, 0xFFFF, 0b11_10_01_00);
        let out = decode(&data, 4, 4, Kind::Dxt1, false).unwrap();
        // (0 + 255) / 2 = 127.
        assert_eq!(px(&out, 2), [127, 127, 127, 255]);
        assert_eq!(px(&out, 3), [0, 0, 0, 255]);
        let clipped = decode(&data, 4, 4, Kind::Dxt1, true).unwrap();
        assert_eq!(px(&clipped, 3), [0, 0, 0, 0]);
        assert_eq!(px(&clipped, 0), [0, 0, 0, 0]);
        assert_eq!(px(&clipped, 1), [255, 255, 255, 255]);
    }

    #[test]
    fn dxt3_alpha_is_four_bits_times_17() {
        let mut data = 0xFEDC_BA98_7654_3210u64.to_le_bytes().to_vec();
        data.extend(block1(0x0000, 0xFFFF, 0));
        let out = decode(&data, 4, 4, Kind::Dxt3, false).unwrap();
        for i in 0..16 {
            assert_eq!(out[i * 4 + 3], i as u8 * 17);
        }
        // The colour part never uses the three-colour mode.
        assert_eq!(px(&out, 0)[..3], [0, 0, 0]);
    }

    #[test]
    fn dxt5_alpha_levels() {
        // e0 = 200 > e1 = 100: seven-step ramp. Pixel i uses index i % 8.
        let mut bits = 0u64;
        for i in 0..16 {
            bits |= ((i % 8) as u64) << (16 + 3 * i);
        }
        let mut data = (bits | 200 | (100 << 8)).to_le_bytes().to_vec();
        data.extend(block1(0xFFFF, 0, 0));
        let out = decode(&data, 4, 4, Kind::Dxt5, false).unwrap();
        // (6*200 + 1*100 + 3) / 7 = 186, ..., (1*200 + 6*100 + 3) / 7 = 114.
        let alpha: Vec<u8> = (0..8).map(|i| out[i * 4 + 3]).collect();
        assert_eq!(alpha, [200, 100, 186, 171, 157, 143, 129, 114]);

        // e0 = 100 <= e1 = 200: five-step ramp plus 0 and 255.
        let mut data = (bits | 100 | (200 << 8)).to_le_bytes().to_vec();
        data.extend(block1(0xFFFF, 0, 0));
        let out = decode(&data, 4, 4, Kind::Dxt5, false).unwrap();
        let alpha: Vec<u8> = (0..8).map(|i| out[i * 4 + 3]).collect();
        // (4*100 + 200 + 2) / 5 = 120, ..., (100 + 4*200 + 2) / 5 = 180.
        assert_eq!(alpha, [100, 200, 120, 140, 160, 180, 0, 255]);
    }

    #[test]
    fn partial_blocks_are_cropped() {
        // A 6×2 image needs 2×1 blocks.
        let mut data = block1(0xFFFF, 0xFFFF, 0);
        data.extend(block1(0x0000, 0x0000, 0));
        let out = decode(&data, 6, 2, Kind::Dxt1, false).unwrap();
        assert_eq!(out.len(), 6 * 2 * 4);
        assert_eq!(px(&out, 3), [255, 255, 255, 255]);
        assert_eq!(px(&out, 4), [0, 0, 0, 255]);
        assert_eq!(px(&out, 6 + 5), [0, 0, 0, 255]);
    }

    #[test]
    fn short_or_ragged_data_is_an_error() {
        let data = block1(0, 0, 0);
        assert!(matches!(
            decode(&data, 8, 4, Kind::Dxt1, false),
            Err(TextureError::DataTooShort { needed: 16, len: 8 })
        ));
        assert!(matches!(
            decode(&data[..7], 4, 4, Kind::Dxt1, false),
            Err(TextureError::PartialBlock { len: 7, block: 8 })
        ));
        assert!(matches!(
            decode(&data, 4, 4, Kind::Dxt5, false),
            Err(TextureError::PartialBlock { len: 8, block: 16 })
        ));
    }
}
