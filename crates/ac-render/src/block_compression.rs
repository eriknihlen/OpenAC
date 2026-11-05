//! Block-compressed textures (BC1, BC2, BC3; the dats call them DXT1, DXT3,
//! DXT5) kept compressed on the GPU, and the mip chain they need.
//!
//! A GPU cannot draw into a block-compressed image, so it cannot shrink one
//! into its smaller mip levels the way it does an RGBA image. The chain is
//! built here instead: level 0 is decoded, halved with a 2 x 2 box filter
//! level after level, and each level is encoded again in the source's own
//! format. The encoder is deliberately simple (the colour box's corners as
//! endpoints, nearest palette entry per texel) and deterministic: the same
//! bytes in give the same bytes out.
//!
//! A block is 4 x 4 texels. BC1 stores two 5:6:5 colours and a 2-bit index
//! per texel (8 bytes); BC2 adds a 4-bit alpha per texel and BC3 two 8-bit
//! alpha endpoints with a 3-bit index per texel (16 bytes each).

use ac_dat::objects::PixelFormat;

/// Edge of a block in texels.
pub const BLOCK_EXTENT: u32 = 4;

/// A block-compressed format.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub enum BcFormat {
    /// DXT1: colour with an optional 1-bit cut-out, 8 bytes a block.
    Bc1,
    /// DXT3: colour and an explicit 4-bit alpha, 16 bytes a block.
    Bc2,
    /// DXT5: colour and an interpolated alpha, 16 bytes a block.
    Bc3,
}

impl BcFormat {
    /// The format a dat pixel format is stored in, when it is block
    /// compressed.
    #[must_use]
    pub fn from_pixel_format(format: PixelFormat) -> Option<Self> {
        match format {
            PixelFormat::DXT1 => Some(Self::Bc1),
            PixelFormat::DXT3 => Some(Self::Bc2),
            PixelFormat::DXT5 => Some(Self::Bc3),
            _ => None,
        }
    }

    /// Bytes one block occupies.
    #[must_use]
    pub fn block_size(self) -> usize {
        match self {
            Self::Bc1 => 8,
            Self::Bc2 | Self::Bc3 => 16,
        }
    }

    /// The GPU format the blocks upload as, unchanged.
    #[must_use]
    pub fn wgpu_format(self) -> wgpu::TextureFormat {
        match self {
            Self::Bc1 => wgpu::TextureFormat::Bc1RgbaUnorm,
            Self::Bc2 => wgpu::TextureFormat::Bc2RgbaUnorm,
            Self::Bc3 => wgpu::TextureFormat::Bc3RgbaUnorm,
        }
    }
}

/// Blocks along one side: whole blocks, at least one.
#[must_use]
pub fn block_count(extent: u32) -> u32 {
    extent.div_ceil(BLOCK_EXTENT).max(1)
}

/// Bytes one level of `width` x `height` texels occupies (an edge block
/// partly outside the image is still stored in full).
#[must_use]
pub fn level_size_bytes(format: BcFormat, width: u32, height: u32) -> usize {
    block_count(width) as usize * block_count(height) as usize * format.block_size()
}

/// Levels in a full chain: halving (floored at one texel) until 1 x 1.
#[must_use]
pub fn full_mip_level_count(mut width: u32, mut height: u32) -> u32 {
    let mut levels = 1;
    while width > 1 || height > 1 {
        width = (width / 2).max(1);
        height = (height / 2).max(1);
        levels += 1;
    }
    levels
}

/// The size of mip level `level`, each side floored at one texel.
#[must_use]
pub fn level_extent(mut width: u32, mut height: u32, level: u32) -> (u32, u32) {
    for _ in 0..level {
        width = (width / 2).max(1);
        height = (height / 2).max(1);
    }
    (width, height)
}

/// Why a level could not be decoded or encoded.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum CodecError {
    /// A side is zero.
    #[error("a level has no size ({width}x{height})")]
    ZeroSize {
        /// Width in texels.
        width: u32,
        /// Height in texels.
        height: u32,
    },
    /// Fewer bytes than the level needs.
    #[error("a {width}x{height} level needs {needed} bytes; {len} were supplied")]
    TooShort {
        /// Width in texels.
        width: u32,
        /// Height in texels.
        height: u32,
        /// Bytes needed.
        needed: usize,
        /// Bytes supplied.
        len: usize,
    },
    /// The stored level could not be decoded.
    #[error(transparent)]
    Decode(#[from] ac_dat::texture::TextureError),
}

fn check_size(width: u32, height: u32) -> Result<(), CodecError> {
    if width == 0 || height == 0 {
        return Err(CodecError::ZeroSize { width, height });
    }
    Ok(())
}

// ── decode ──────────────────────────────────────────────────────────────

/// Decode one level to `width * height` RGBA texels.
pub fn decode_level(
    format: BcFormat,
    blocks: &[u8],
    width: u32,
    height: u32,
) -> Result<Vec<u8>, CodecError> {
    check_size(width, height)?;
    let mut rgba = vec![0u8; width as usize * height as usize * 4];
    decode_level_into(format, blocks, width, height, &mut rgba)?;
    Ok(rgba)
}

/// Decode one level into the first `width * height` RGBA texels of
/// `rgba`; every one of them is written.
pub fn decode_level_into(
    format: BcFormat,
    blocks: &[u8],
    width: u32,
    height: u32,
    rgba: &mut [u8],
) -> Result<(), CodecError> {
    check_size(width, height)?;
    let size = format.block_size();
    let (bx_count, by_count) = (block_count(width) as usize, block_count(height) as usize);
    let needed = bx_count * by_count * size;
    if blocks.len() < needed {
        return Err(CodecError::TooShort {
            width,
            height,
            needed,
            len: blocks.len(),
        });
    }
    let (w, h) = (width as usize, height as usize);
    if rgba.len() < w * h * 4 {
        return Err(CodecError::TooShort {
            width,
            height,
            needed: w * h * 4,
            len: rgba.len(),
        });
    }
    let mut texels = [0u8; 64];
    for by in 0..by_count {
        for bx in 0..bx_count {
            let at = (by * bx_count + bx) * size;
            decode_block(format, &blocks[at..at + size], &mut texels);
            for y in 0..4 {
                let ty = by * 4 + y;
                if ty >= h {
                    break;
                }
                for x in 0..4 {
                    let tx = bx * 4 + x;
                    if tx >= w {
                        break;
                    }
                    let s = (y * 4 + x) * 4;
                    let d = (ty * w + tx) * 4;
                    rgba[d..d + 4].copy_from_slice(&texels[s..s + 4]);
                }
            }
        }
    }
    Ok(())
}

/// Decode one block into 16 RGBA texels, row by row. `block` holds at
/// least one block of `format`.
pub fn decode_block(format: BcFormat, block: &[u8], rgba: &mut [u8; 64]) {
    let colour_offset = if format == BcFormat::Bc1 { 0 } else { 8 };
    decode_colour_block(
        &block[colour_offset..colour_offset + 8],
        format == BcFormat::Bc1,
        rgba,
    );
    match format {
        BcFormat::Bc1 => {}
        BcFormat::Bc2 => decode_explicit_alpha(&block[..8], rgba),
        BcFormat::Bc3 => decode_interpolated_alpha(&block[..8], rgba),
    }
}

/// The four colours of a block's palette from its two endpoints; in the
/// three-colour mode the fourth is transparent black.
fn colour_palette(c0: u16, c1: u16, three_colour: bool) -> [[u8; 4]; 4] {
    let a = unpack_565(c0);
    let b = unpack_565(c1);
    let mut palette = [a, b, [0; 4], [0; 4]];
    if three_colour {
        for c in 0..3 {
            palette[2][c] = ((u16::from(a[c]) + u16::from(b[c])) / 2) as u8;
        }
        palette[2][3] = 255;
    } else {
        for c in 0..3 {
            palette[2][c] = ((2 * u16::from(a[c]) + u16::from(b[c])) / 3) as u8;
            palette[3][c] = ((u16::from(a[c]) + 2 * u16::from(b[c])) / 3) as u8;
        }
        palette[2][3] = 255;
        palette[3][3] = 255;
    }
    palette
}

fn decode_colour_block(block: &[u8], allow_transparent_mode: bool, rgba: &mut [u8; 64]) {
    let c0 = u16::from_le_bytes([block[0], block[1]]);
    let c1 = u16::from_le_bytes([block[2], block[3]]);
    let indices = u32::from_le_bytes([block[4], block[5], block[6], block[7]]);
    let palette = colour_palette(c0, c1, allow_transparent_mode && c0 <= c1);
    for texel in 0..16 {
        let index = ((indices >> (texel * 2)) & 3) as usize;
        rgba[texel * 4..texel * 4 + 4].copy_from_slice(&palette[index]);
    }
}

fn decode_explicit_alpha(alpha_block: &[u8], rgba: &mut [u8; 64]) {
    for texel in 0..16 {
        let nibble = (alpha_block[texel / 2] >> ((texel % 2) * 4)) & 0xF;
        // The 4-bit alpha widened to 8 bits.
        rgba[texel * 4 + 3] = ((u16::from(nibble) * 255) / 15) as u8;
    }
}

fn decode_interpolated_alpha(alpha_block: &[u8], rgba: &mut [u8; 64]) {
    let (a0, a1) = (u16::from(alpha_block[0]), u16::from(alpha_block[1]));
    let mut palette = [0u8; 8];
    palette[0] = a0 as u8;
    palette[1] = a1 as u8;
    if a0 > a1 {
        for i in 1..=6u16 {
            palette[usize::from(i) + 1] = (((7 - i) * a0 + i * a1) / 7) as u8;
        }
    } else {
        for i in 1..=4u16 {
            palette[usize::from(i) + 1] = (((5 - i) * a0 + i * a1) / 5) as u8;
        }
        palette[6] = 0;
        palette[7] = 255;
    }
    let mut bits = 0u64;
    for i in 0..6 {
        bits |= u64::from(alpha_block[2 + i]) << (i * 8);
    }
    for texel in 0..16 {
        rgba[texel * 4 + 3] = palette[((bits >> (texel * 3)) & 7) as usize];
    }
}

fn pack_565(r: i32, g: i32, b: i32) -> u16 {
    let r5 = (r.clamp(0, 255) * 31 + 127) / 255;
    let g6 = (g.clamp(0, 255) * 63 + 127) / 255;
    let b5 = (b.clamp(0, 255) * 31 + 127) / 255;
    ((r5 << 11) | (g6 << 5) | b5) as u16
}

fn unpack_565(packed: u16) -> [u8; 4] {
    let r = ((packed >> 11) & 0x1F) as u8;
    let g = ((packed >> 5) & 0x3F) as u8;
    let b = (packed & 0x1F) as u8;
    [
        (r << 3) | (r >> 2),
        (g << 2) | (g >> 4),
        (b << 3) | (b >> 2),
        255,
    ]
}

// ── encode ──────────────────────────────────────────────────────────────

/// Encode `width * height` RGBA texels as one level of `format`.
pub fn encode_level(
    format: BcFormat,
    rgba: &[u8],
    width: u32,
    height: u32,
) -> Result<Vec<u8>, CodecError> {
    check_size(width, height)?;
    let needed = width as usize * height as usize * 4;
    if rgba.len() < needed {
        return Err(CodecError::TooShort {
            width,
            height,
            needed,
            len: rgba.len(),
        });
    }
    let size = format.block_size();
    let (bx_count, by_count) = (block_count(width), block_count(height));
    let mut output = vec![0u8; bx_count as usize * by_count as usize * size];
    let mut texels = [0u8; 64];
    for by in 0..by_count {
        for bx in 0..bx_count {
            gather_block(rgba, width, height, bx, by, &mut texels);
            let at = (by * bx_count + bx) as usize * size;
            encode_block(format, &texels, &mut output[at..at + size]);
        }
    }
    Ok(output)
}

/// The 16 texels of block (`block_x`, `block_y`); texels past an edge
/// repeat the edge.
fn gather_block(
    rgba: &[u8],
    width: u32,
    height: u32,
    block_x: u32,
    block_y: u32,
    texels: &mut [u8; 64],
) {
    for y in 0..4 {
        let sy = (block_y * 4 + y).min(height - 1) as usize;
        for x in 0..4 {
            let sx = (block_x * 4 + x).min(width - 1) as usize;
            let s = (sy * width as usize + sx) * 4;
            let d = ((y * 4 + x) * 4) as usize;
            texels[d..d + 4].copy_from_slice(&rgba[s..s + 4]);
        }
    }
}

/// Encode 16 RGBA texels (row by row) as one block of `format` into
/// `block` (one block's bytes).
pub fn encode_block(format: BcFormat, rgba: &[u8; 64], block: &mut [u8]) {
    block.fill(0);
    match format {
        BcFormat::Bc1 => encode_colour_block(rgba, true, &mut block[..8]),
        BcFormat::Bc2 => {
            encode_explicit_alpha(rgba, &mut block[..8]);
            encode_colour_block(rgba, false, &mut block[8..16]);
        }
        BcFormat::Bc3 => {
            encode_interpolated_alpha(rgba, &mut block[..8]);
            encode_colour_block(rgba, false, &mut block[8..16]);
        }
    }
}

fn encode_colour_block(rgba: &[u8; 64], allow_transparent_mode: bool, block: &mut [u8]) {
    let needs_transparency =
        allow_transparent_mode && (0..16).any(|texel| rgba[texel * 4 + 3] < 128);
    let ((min_r, min_g, min_b), (max_r, max_g, max_b)) =
        find_colour_extremes(rgba, needs_transparency);
    let mut c0 = pack_565(max_r, max_g, max_b);
    let mut c1 = pack_565(min_r, min_g, min_b);
    if needs_transparency {
        if c0 > c1 {
            std::mem::swap(&mut c0, &mut c1);
        }
    } else if c0 <= c1 {
        if c0 == c1 {
            if c1 == 0 {
                c0 = 1;
            } else {
                c1 -= 1;
            }
        } else {
            std::mem::swap(&mut c0, &mut c1);
        }
    }

    let palette = colour_palette(c0, c1, needs_transparency);
    let palette_size = if needs_transparency { 3 } else { 4 };
    let mut indices = 0u32;
    for texel in 0..16 {
        let index = if needs_transparency && rgba[texel * 4 + 3] < 128 {
            3
        } else {
            nearest_palette_entry(&rgba[texel * 4..texel * 4 + 3], &palette, palette_size)
        };
        indices |= (index as u32) << (texel * 2);
    }
    block[0..2].copy_from_slice(&c0.to_le_bytes());
    block[2..4].copy_from_slice(&c1.to_le_bytes());
    block[4..8].copy_from_slice(&indices.to_le_bytes());
}

type Rgb = (i32, i32, i32);

/// The per-channel minimum and maximum over the block's texels (skipping
/// cut-out texels when asked); black when every texel is skipped.
fn find_colour_extremes(rgba: &[u8; 64], ignore_transparent_texels: bool) -> (Rgb, Rgb) {
    let (mut min, mut max) = ((255, 255, 255), (0, 0, 0));
    let mut any = false;
    for texel in 0..16 {
        if ignore_transparent_texels && rgba[texel * 4 + 3] < 128 {
            continue;
        }
        any = true;
        let r = i32::from(rgba[texel * 4]);
        let g = i32::from(rgba[texel * 4 + 1]);
        let b = i32::from(rgba[texel * 4 + 2]);
        min = (min.0.min(r), min.1.min(g), min.2.min(b));
        max = (max.0.max(r), max.1.max(g), max.2.max(b));
    }
    if any {
        (min, max)
    } else {
        ((0, 0, 0), (0, 0, 0))
    }
}

/// The palette entry nearest `colour` (squared RGB distance; the first of
/// equals wins).
fn nearest_palette_entry(colour: &[u8], palette: &[[u8; 4]; 4], palette_size: usize) -> usize {
    let mut best = 0;
    let mut best_distance = i32::MAX;
    for (entry, p) in palette.iter().enumerate().take(palette_size) {
        let dr = i32::from(colour[0]) - i32::from(p[0]);
        let dg = i32::from(colour[1]) - i32::from(p[1]);
        let db = i32::from(colour[2]) - i32::from(p[2]);
        let distance = dr * dr + dg * dg + db * db;
        if distance < best_distance {
            best_distance = distance;
            best = entry;
        }
    }
    best
}

fn encode_explicit_alpha(rgba: &[u8; 64], alpha_block: &mut [u8]) {
    for texel in 0..16 {
        let nibble = ((u16::from(rgba[texel * 4 + 3]) * 15 + 127) / 255) as u8;
        let index = texel / 2;
        if texel % 2 == 0 {
            alpha_block[index] = (alpha_block[index] & 0xF0) | nibble;
        } else {
            alpha_block[index] = (alpha_block[index] & 0x0F) | (nibble << 4);
        }
    }
}

fn encode_interpolated_alpha(rgba: &[u8; 64], alpha_block: &mut [u8]) {
    let mut min = 255u8;
    let mut max = 0u8;
    for texel in 0..16 {
        let a = rgba[texel * 4 + 3];
        min = min.min(a);
        max = max.max(a);
    }
    let (mut a0, mut a1) = (max, min);
    if a0 == a1 {
        if a1 > 0 {
            a1 -= 1;
        } else {
            a0 = 1;
        }
    }
    // Always the eight-level ramp: a0 > a1 here.
    let mut palette = [0u8; 8];
    palette[0] = a0;
    palette[1] = a1;
    for i in 1..=6u16 {
        palette[usize::from(i) + 1] = (((7 - i) * u16::from(a0) + i * u16::from(a1)) / 7) as u8;
    }
    let mut bits = 0u64;
    for texel in 0..16 {
        let a = i32::from(rgba[texel * 4 + 3]);
        let mut best = 0u64;
        let mut best_distance = i32::MAX;
        for (entry, &p) in palette.iter().enumerate() {
            let distance = (a - i32::from(p)).abs();
            if distance < best_distance {
                best_distance = distance;
                best = entry as u64;
            }
        }
        bits |= best << (texel * 3);
    }
    alpha_block[0] = a0;
    alpha_block[1] = a1;
    for i in 0..6 {
        alpha_block[2 + i] = (bits >> (i * 8)) as u8;
    }
}

// ── the mip chain ───────────────────────────────────────────────────────

/// One level below level 0.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MipLevel {
    /// Its index (1 is the first below level 0).
    pub mip_level: u32,
    /// Width in texels.
    pub width: u32,
    /// Height in texels.
    pub height: u32,
    /// Its bytes: blocks for a compressed chain, RGBA texels otherwise.
    pub data: Vec<u8>,
}

/// Levels 1.. of a compressed level 0 (`mip_level_count` levels in all,
/// so none for 1): level 0 is decoded, not re-encoded.
pub fn build_compressed(
    format: BcFormat,
    level0: &[u8],
    width: u32,
    height: u32,
    mip_level_count: u32,
) -> Result<Vec<MipLevel>, CodecError> {
    if mip_level_count <= 1 {
        return Ok(Vec::new());
    }
    let rgba = decode_level(format, level0, width, height)?;
    build_from_rgba(Some(format), &rgba, width, height, mip_level_count)
}

/// Levels 1.. of an RGBA level 0: each halves the one before with a 2 x 2
/// box filter, and is encoded in `format` (kept RGBA when `None`).
///
/// An RGBA level is itself the level returned, and the next is filtered
/// from it. A compressed level is encoded from a filtered working image;
/// two working images, each the size of level 1, take turns (one is
/// filtered into the other), so the chain allocates only the levels it
/// returns and those two.
pub fn build_from_rgba(
    format: Option<BcFormat>,
    rgba: &[u8],
    width: u32,
    height: u32,
    mip_level_count: u32,
) -> Result<Vec<MipLevel>, CodecError> {
    check_size(width, height)?;
    let mut levels: Vec<MipLevel> = Vec::with_capacity(mip_level_count.saturating_sub(1) as usize);
    if mip_level_count <= 1 {
        return Ok(levels);
    }
    let (mut w, mut h) = (width, height);
    let Some(format) = format else {
        for level in 1..mip_level_count {
            let above = levels.last().map_or(rgba, |l| l.data.as_slice());
            let (data, nw, nh) = downsample(above, w, h)?;
            levels.push(MipLevel {
                mip_level: level,
                width: nw,
                height: nh,
                data,
            });
            (w, h) = (nw, nh);
        }
        return Ok(levels);
    };
    let first = (width / 2).max(1) as usize * (height / 2).max(1) as usize * 4;
    let (mut target, mut above) = (vec![0u8; first], vec![0u8; first]);
    for level in 1..mip_level_count {
        let source = if level == 1 { rgba } else { above.as_slice() };
        let (nw, nh) = downsample_into(source, w, h, &mut target)?;
        let next = &target[..nw as usize * nh as usize * 4];
        levels.push(MipLevel {
            mip_level: level,
            width: nw,
            height: nh,
            data: encode_level(format, next, nw, nh)?,
        });
        std::mem::swap(&mut target, &mut above);
        (w, h) = (nw, nh);
    }
    Ok(levels)
}

/// Halve an RGBA image (each side floored at one texel): every texel is
/// the rounded mean of the 2 x 2 texels above it, alpha averaged like the
/// colours; at an odd edge the last row or column is read twice.
pub fn downsample(rgba: &[u8], width: u32, height: u32) -> Result<(Vec<u8>, u32, u32), CodecError> {
    check_size(width, height)?;
    let (nw, nh) = ((width / 2).max(1), (height / 2).max(1));
    let mut output = vec![0u8; nw as usize * nh as usize * 4];
    downsample_into(rgba, width, height, &mut output)?;
    Ok((output, nw, nh))
}

/// [`downsample`] into the first texels of `output`, which must hold the
/// halved image; every texel of it is written. Returns its size.
pub fn downsample_into(
    rgba: &[u8],
    width: u32,
    height: u32,
    output: &mut [u8],
) -> Result<(u32, u32), CodecError> {
    check_size(width, height)?;
    let needed = width as usize * height as usize * 4;
    if rgba.len() < needed {
        return Err(CodecError::TooShort {
            width,
            height,
            needed,
            len: rgba.len(),
        });
    }
    let (nw, nh) = ((width / 2).max(1), (height / 2).max(1));
    if output.len() < nw as usize * nh as usize * 4 {
        return Err(CodecError::TooShort {
            width: nw,
            height: nh,
            needed: nw as usize * nh as usize * 4,
            len: output.len(),
        });
    }
    let (w, h) = (width as usize, height as usize);
    for y in 0..nh as usize {
        let y0 = (y * 2).min(h - 1);
        let y1 = (y * 2 + 1).min(h - 1);
        for x in 0..nw as usize {
            let x0 = (x * 2).min(w - 1);
            let x1 = (x * 2 + 1).min(w - 1);
            let a = (y0 * w + x0) * 4;
            let b = (y0 * w + x1) * 4;
            let c = (y1 * w + x0) * 4;
            let d = (y1 * w + x1) * 4;
            let o = (y * nw as usize + x) * 4;
            for ch in 0..4 {
                let sum = u32::from(rgba[a + ch])
                    + u32::from(rgba[b + ch])
                    + u32::from(rgba[c + ch])
                    + u32::from(rgba[d + ch]);
                output[o + ch] = ((sum + 2) / 4) as u8;
            }
        }
    }
    Ok((nw, nh))
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.App.Tests/Rendering/Gpu/Vk/BlockCompressionTests.cs`
    //! (the codec, chain and filter tests; the texture-slot tests belong to
    //! OpenAC's descriptor table, which wgpu bind groups replace).
    use super::*;

    fn solid_rgba(width: u32, height: u32, r: u8, g: u8, b: u8, a: u8) -> Vec<u8> {
        [r, g, b, a].repeat((width * height) as usize)
    }

    /// A deterministic byte stream (xorshift), standing in for the C#
    /// tests' seeded `Random`: those tests check reproducibility, not
    /// particular bytes.
    fn noise(len: usize, mut seed: u64) -> Vec<u8> {
        (0..len)
            .map(|_| {
                seed ^= seed << 13;
                seed ^= seed >> 7;
                seed ^= seed << 17;
                seed as u8
            })
            .collect()
    }

    const ALL: [BcFormat; 3] = [BcFormat::Bc1, BcFormat::Bc2, BcFormat::Bc3];

    // ── sizes ────────────────────────────────────────────────────────

    #[test]
    fn block_sizes_match_the_dxt_formats() {
        assert_eq!(BcFormat::Bc1.block_size(), 8);
        assert_eq!(BcFormat::Bc2.block_size(), 16);
        assert_eq!(BcFormat::Bc3.block_size(), 16);
    }

    #[test]
    fn level_size_rounds_up_to_whole_blocks() {
        // A 5x5 BC1 level still needs 2x2 blocks; a level smaller than one
        // block is still one block.
        assert_eq!(level_size_bytes(BcFormat::Bc1, 5, 5), 4 * 8);
        assert_eq!(level_size_bytes(BcFormat::Bc1, 1, 1), 8);
    }

    #[test]
    fn full_mip_chain_reaches_one_by_one() {
        assert_eq!(full_mip_level_count(256, 256), 9);
        assert_eq!(full_mip_level_count(1, 1), 1);
        assert_eq!(full_mip_level_count(256, 4), 9);
        // The renderer's own count agrees.
        assert_eq!(crate::assets::mip_level_count(256, 4), 9);
    }

    #[test]
    fn level_extent_floors_at_one_texel() {
        assert_eq!(level_extent(256, 64, 2), (64, 16));
        assert_eq!(level_extent(4, 4, 9), (1, 1));
    }

    #[test]
    fn dat_formats_map_to_their_block_formats() {
        assert_eq!(
            BcFormat::from_pixel_format(PixelFormat::DXT1),
            Some(BcFormat::Bc1)
        );
        assert_eq!(
            BcFormat::from_pixel_format(PixelFormat::DXT3),
            Some(BcFormat::Bc2)
        );
        assert_eq!(
            BcFormat::from_pixel_format(PixelFormat::DXT5),
            Some(BcFormat::Bc3)
        );
        assert_eq!(BcFormat::from_pixel_format(PixelFormat::P8), None);
        assert_eq!(
            BcFormat::Bc1.wgpu_format(),
            wgpu::TextureFormat::Bc1RgbaUnorm
        );
    }

    // ── round trips ──────────────────────────────────────────────────

    #[test]
    fn solid_colour_survives_the_round_trip_exactly_in_every_format() {
        // Exactly representable in 5:6:5, so "exact" is fair to demand.
        let source = solid_rgba(8, 8, 0x00, 0x00, 0xFF, 0xFF);
        for format in ALL {
            let encoded = encode_level(format, &source, 8, 8).unwrap();
            let decoded = decode_level(format, &encoded, 8, 8).unwrap();
            assert_eq!(decoded, source, "{format:?}");
        }
    }

    #[test]
    fn bc3_preserves_a_solid_alpha_exactly() {
        let source = solid_rgba(4, 4, 0xFF, 0xFF, 0xFF, 0x42);
        let encoded = encode_level(BcFormat::Bc3, &source, 4, 4).unwrap();
        let decoded = decode_level(BcFormat::Bc3, &encoded, 4, 4).unwrap();
        for texel in 0..16 {
            assert_eq!(decoded[texel * 4 + 3], 0x42);
        }
    }

    #[test]
    fn bc1_keeps_cut_out_texels_fully_transparent() {
        let mut source = solid_rgba(4, 4, 0xFF, 0xFF, 0xFF, 0xFF);
        for texel in (0..16).step_by(2) {
            source[texel * 4 + 3] = 0;
        }
        let encoded = encode_level(BcFormat::Bc1, &source, 4, 4).unwrap();
        let decoded = decode_level(BcFormat::Bc1, &encoded, 4, 4).unwrap();
        for texel in 0..16 {
            let expected = if texel % 2 == 0 { 0 } else { 255 };
            assert_eq!(decoded[texel * 4 + 3], expected, "texel {texel}");
        }
    }

    #[test]
    fn bc1_uses_three_colour_mode_only_when_the_block_has_cut_out_texels() {
        let opaque = solid_rgba(4, 4, 0x20, 0x40, 0x60, 0xFF);
        let mut with_holes = opaque.clone();
        with_holes[3] = 0;
        let o = encode_level(BcFormat::Bc1, &opaque, 4, 4).unwrap();
        let h = encode_level(BcFormat::Bc1, &with_holes, 4, 4).unwrap();
        let endpoints = |b: &[u8]| {
            (
                u16::from_le_bytes([b[0], b[1]]),
                u16::from_le_bytes([b[2], b[3]]),
            )
        };
        let (oc0, oc1) = endpoints(&o);
        let (hc0, hc1) = endpoints(&h);
        assert!(oc0 > oc1, "an opaque block uses the four-colour mode");
        assert!(
            hc0 <= hc1,
            "a block with a cut-out texel uses three colours"
        );
    }

    #[test]
    fn gradient_round_trip_stays_close_rather_than_collapsing() {
        let mut source = vec![0u8; 64];
        for texel in 0..16 {
            source[texel * 4] = ((texel % 4) * 80) as u8;
            source[texel * 4 + 3] = 255;
        }
        let encoded = encode_level(BcFormat::Bc1, &source, 4, 4).unwrap();
        let decoded = decode_level(BcFormat::Bc1, &encoded, 4, 4).unwrap();
        let mut distinct = std::collections::HashSet::new();
        for texel in 0..16 {
            distinct.insert(decoded[texel * 4]);
            let error = (i32::from(decoded[texel * 4]) - i32::from(source[texel * 4])).abs();
            assert!(error <= 12, "texel {texel}: error {error}");
        }
        assert_eq!(distinct.len(), 4);
    }

    #[test]
    fn decoding_follows_the_block_layout() {
        // c0 = white > c1 = black: indices 0, 1, 2, 3 on the first row;
        // the in-between colours are (2a + b) / 3 and (a + 2b) / 3.
        let mut block = Vec::new();
        block.extend_from_slice(&0xFFFFu16.to_le_bytes());
        block.extend_from_slice(&0x0000u16.to_le_bytes());
        block.extend_from_slice(&0b11_10_01_00u32.to_le_bytes());
        let out = decode_level(BcFormat::Bc1, &block, 4, 4).unwrap();
        assert_eq!(
            &out[0..16],
            &[
                255, 255, 255, 255, 0, 0, 0, 255, 170, 170, 170, 255, 85, 85, 85, 255
            ]
        );
        // c0 <= c1: three colours and transparent black.
        let mut block = Vec::new();
        block.extend_from_slice(&0x0000u16.to_le_bytes());
        block.extend_from_slice(&0xFFFFu16.to_le_bytes());
        block.extend_from_slice(&0b11_10_01_00u32.to_le_bytes());
        let out = decode_level(BcFormat::Bc1, &block, 4, 4).unwrap();
        assert_eq!(&out[8..16], &[127, 127, 127, 255, 0, 0, 0, 0]);
    }

    #[test]
    fn short_data_and_zero_sizes_are_errors() {
        assert!(matches!(
            decode_level(BcFormat::Bc1, &[0; 8], 8, 4),
            Err(CodecError::TooShort { needed: 16, .. })
        ));
        assert!(matches!(
            encode_level(BcFormat::Bc1, &[0; 4], 0, 1),
            Err(CodecError::ZeroSize { .. })
        ));
    }

    // ── determinism ──────────────────────────────────────────────────

    #[test]
    fn encoding_the_same_bytes_twice_produces_the_same_bytes() {
        let source = noise(16 * 16 * 4, 20_260_728);
        let first = encode_level(BcFormat::Bc3, &source, 16, 16).unwrap();
        let second = encode_level(BcFormat::Bc3, &source, 16, 16).unwrap();
        assert_eq!(first, second);
    }

    #[test]
    fn the_whole_mip_chain_is_reproducible() {
        let source = noise(32 * 32 * 4, 1_189_998_819);
        let first = build_from_rgba(Some(BcFormat::Bc1), &source, 32, 32, 6).unwrap();
        let second = build_from_rgba(Some(BcFormat::Bc1), &source, 32, 32, 6).unwrap();
        assert_eq!(first, second);
    }

    // ── the chain ────────────────────────────────────────────────────

    /// The chain built in two working images is the chain of a fresh
    /// image per level (each level filtered from the one above, then
    /// encoded), for every format and for sides that reach one texel at
    /// different levels, where a working image still holds the larger
    /// level's texels past the smaller one's end.
    #[test]
    fn the_chain_in_working_images_is_the_chain_of_fresh_images() {
        for (w, h) in [(64, 64), (64, 8), (8, 64), (20, 12)] {
            let source = noise(w as usize * h as usize * 4, 0x5eed + u64::from(w * h));
            let count = full_mip_level_count(w, h);
            for format in [BcFormat::Bc1, BcFormat::Bc2, BcFormat::Bc3] {
                let mut expected = Vec::new();
                let (mut above, mut lw, mut lh) = (source.clone(), w, h);
                for level in 1..count {
                    let (next, nw, nh) = downsample(&above, lw, lh).unwrap();
                    expected.push(MipLevel {
                        mip_level: level,
                        width: nw,
                        height: nh,
                        data: encode_level(format, &next, nw, nh).unwrap(),
                    });
                    (above, lw, lh) = (next, nw, nh);
                }
                let built = build_from_rgba(Some(format), &source, w, h, count).unwrap();
                assert_eq!(built, expected, "{w}x{h} {format:?}");
            }
            let mut rgba_levels = Vec::new();
            let (mut above, mut lw, mut lh) = (source.clone(), w, h);
            for _ in 1..count {
                let (next, nw, nh) = downsample(&above, lw, lh).unwrap();
                rgba_levels.push(next.clone());
                (above, lw, lh) = (next, nw, nh);
            }
            let built: Vec<Vec<u8>> = build_from_rgba(None, &source, w, h, count)
                .unwrap()
                .into_iter()
                .map(|l| l.data)
                .collect();
            assert_eq!(built, rgba_levels, "{w}x{h} rgba");
        }
    }

    #[test]
    fn decoding_into_a_larger_image_writes_the_level_at_its_start() {
        let source = noise(8 * 8 * 4, 77);
        let blocks = encode_level(BcFormat::Bc3, &source, 8, 8).unwrap();
        let mut into = vec![0xAB; 8 * 8 * 4 + 16];
        decode_level_into(BcFormat::Bc3, &blocks, 8, 8, &mut into).unwrap();
        assert_eq!(
            into[..256],
            decode_level(BcFormat::Bc3, &blocks, 8, 8).unwrap()[..]
        );
        assert_eq!(into[256..], [0xAB; 16]);
        assert!(decode_level_into(BcFormat::Bc3, &blocks, 8, 8, &mut [0; 255]).is_err());
    }

    #[test]
    fn chain_halves_each_level_and_stops_at_one_by_one() {
        let source = solid_rgba(16, 16, 0x00, 0xFF, 0x00, 0xFF);
        let levels = build_from_rgba(Some(BcFormat::Bc1), &source, 16, 16, 5).unwrap();
        let index: Vec<u32> = levels.iter().map(|l| l.mip_level).collect();
        let widths: Vec<u32> = levels.iter().map(|l| l.width).collect();
        let heights: Vec<u32> = levels.iter().map(|l| l.height).collect();
        assert_eq!(index, [1, 2, 3, 4]);
        assert_eq!(widths, [8, 4, 2, 1]);
        assert_eq!(heights, [8, 4, 2, 1]);
        // Even a 1x1 level occupies one whole block.
        assert_eq!(levels[3].data.len(), 8);
    }

    #[test]
    fn chain_of_a_uniform_image_is_that_colour_at_every_level() {
        let source = solid_rgba(16, 16, 0x00, 0x00, 0xFF, 0xFF);
        for level in build_from_rgba(Some(BcFormat::Bc1), &source, 16, 16, 5).unwrap() {
            let decoded =
                decode_level(BcFormat::Bc1, &level.data, level.width, level.height).unwrap();
            for texel in decoded.chunks_exact(4) {
                assert_eq!(&texel[..3], &[0x00, 0x00, 0xFF]);
            }
        }
    }

    #[test]
    fn chain_starts_from_a_compressed_level_zero_without_re_encoding_it() {
        let source = solid_rgba(8, 8, 0xFF, 0x00, 0x00, 0xFF);
        let level0 = encode_level(BcFormat::Bc1, &source, 8, 8).unwrap();
        let levels = build_compressed(BcFormat::Bc1, &level0, 8, 8, 4).unwrap();
        let index: Vec<u32> = levels.iter().map(|l| l.mip_level).collect();
        assert_eq!(index, [1, 2, 3]);
    }

    #[test]
    fn chain_of_a_single_level_image_is_empty() {
        let source = solid_rgba(4, 4, 1, 2, 3, 255);
        assert!(
            build_from_rgba(Some(BcFormat::Bc1), &source, 4, 4, 1)
                .unwrap()
                .is_empty()
        );
        assert!(
            build_compressed(BcFormat::Bc1, &[0; 8], 4, 4, 1)
                .unwrap()
                .is_empty()
        );
    }

    #[test]
    fn an_uncompressed_chain_keeps_rgba_levels() {
        let source = solid_rgba(4, 4, 9, 8, 7, 6);
        let levels = build_from_rgba(None, &source, 4, 4, 3).unwrap();
        assert_eq!(levels[0].data, solid_rgba(2, 2, 9, 8, 7, 6));
        assert_eq!(levels[1].data, [9, 8, 7, 6]);
    }

    // ── the filter ───────────────────────────────────────────────────

    #[test]
    fn downsample_averages_two_by_two_blocks_with_round_half_up() {
        // Two texels of 0 and two of 1 average to 0.5, which rounds to 1.
        let source = [0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0];
        let (output, w, h) = downsample(&source, 2, 2).unwrap();
        assert_eq!((w, h), (1, 1));
        assert_eq!(output, [1, 1, 1, 1]);
    }

    #[test]
    fn downsample_clamps_at_odd_extents_rather_than_reading_out_of_bounds() {
        let source = solid_rgba(3, 3, 10, 20, 30, 255);
        let (output, w, h) = downsample(&source, 3, 3).unwrap();
        assert_eq!((w, h), (1, 1));
        assert_eq!(output, [10, 20, 30, 255]);
    }

    #[test]
    fn downsample_averages_alpha_without_premultiplying() {
        let source = [
            255, 255, 255, 255, 255, 255, 255, 0, 255, 255, 255, 255, 255, 255, 255, 0,
        ];
        let (output, _, _) = downsample(&source, 2, 2).unwrap();
        assert_eq!(output, [255, 255, 255, 128]);
    }
}
