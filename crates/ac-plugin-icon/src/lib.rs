//! A plugin's `icon.png`: the rules a file must meet (checked by the
//! launcher when it installs a plugin and by the client before it shows
//! one) and a PNG decoder to 8-bit RGBA. Every colour type, bit depth and
//! the Adam7 interlace the PNG specification allows are read; colour
//! profiles, gamma and the chunk checksums are ignored, as a 64 x 64 tile
//! does not need them. Malformed input is an error or `None`, never a
//! panic, and the image data is never inflated past the size its header
//! declares.
//!
//! Its own crate so the launcher checks and shows icons without linking the
//! plugin UI; `ac-plugin-ui` re-exports it as `icon`.

use std::path::Path;
use std::sync::Arc;

/// A plugin's `icon.png`, decoded: the image its dock slots show ahead
/// of a render surface or initials.
#[derive(Clone, PartialEq, Eq)]
pub struct PluginIcon {
    /// Width in pixels.
    pub width: u32,
    /// Height in pixels.
    pub height: u32,
    /// Four bytes per pixel, red first, top row first.
    pub rgba: Arc<Vec<u8>>,
}

impl std::fmt::Debug for PluginIcon {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PluginIcon")
            .field("width", &self.width)
            .field("height", &self.height)
            .field("bytes", &self.rgba.len())
            .finish()
    }
}

/// The icon's file name in a plugin's folder.
pub const ICON_FILE_NAME: &str = "icon.png";

/// The icon's exact size in pixels.
pub const ICON_EXTENT: u32 = 64;

/// The icon's largest file size.
pub const ICON_MAXIMUM_BYTES: usize = 64 * 1024;

const PNG_SIGNATURE: [u8; 8] = [137, 80, 78, 71, 13, 10, 26, 10];

/// The largest image the decoder reads, pixels on a side.
const MAXIMUM_DECODE_EXTENT: u32 = 4096;

/// Why an icon file breaks the rules.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum IconError {
    /// Larger than [`ICON_MAXIMUM_BYTES`].
    #[error("The plugin icon is larger than {ICON_MAXIMUM_BYTES} bytes.")]
    TooLarge,
    /// No PNG signature.
    #[error("The plugin icon is not a PNG file.")]
    NotPng,
    /// The first chunk is not a 13-byte `IHDR`, or the header is outside
    /// the PNG specification.
    #[error("The plugin icon's PNG header is malformed.")]
    MalformedHeader,
    /// Not [`ICON_EXTENT`] pixels square.
    #[error("The plugin icon must be exactly {ICON_EXTENT}x{ICON_EXTENT} pixels.")]
    WrongSize,
    /// A chunk runs past the end of the file.
    #[error("The plugin icon's PNG data is truncated.")]
    Truncated,
    /// A chunk length above `i32::MAX`.
    #[error("The plugin icon's PNG chunk length is invalid.")]
    ChunkLength,
    /// The file ends without an `IEND` chunk.
    #[error("The plugin icon has no IEND chunk.")]
    NoEnd,
    /// An `acTL` chunk: an animated PNG.
    #[error("The plugin icon must not be animated.")]
    Animated,
    /// Bytes after `IEND`.
    #[error("The plugin icon has data after its IEND chunk.")]
    TrailingData,
    /// The image data inflates past the header's size.
    #[error("The plugin icon's image data is larger than its declared size.")]
    DataTooLarge,
    /// The image data does not inflate.
    #[error("The plugin icon's image data is invalid.")]
    DataInvalid,
}

/// One chunk's length, type and where its data starts, after checking it
/// lies inside `bytes`.
fn chunk_header(bytes: &[u8], offset: usize) -> Result<(usize, [u8; 4], usize), IconError> {
    let header = bytes
        .get(offset..offset.checked_add(8).ok_or(IconError::Truncated)?)
        .ok_or(IconError::Truncated)?;
    let declared = u32::from_be_bytes([header[0], header[1], header[2], header[3]]);
    if declared > i32::MAX as u32 {
        return Err(IconError::ChunkLength);
    }
    let length = declared as usize;
    let data_offset = offset + 8;
    let end = data_offset
        .checked_add(length)
        .and_then(|end| end.checked_add(4))
        .ok_or(IconError::Truncated)?;
    if end > bytes.len() {
        return Err(IconError::Truncated);
    }
    let kind = [header[4], header[5], header[6], header[7]];
    Ok((length, kind, data_offset))
}

/// Whether the PNG specification allows `bit_depth` for `color_type`.
fn is_allowed_depth(color_type: u8, bit_depth: u8) -> bool {
    match color_type {
        0 => matches!(bit_depth, 1 | 2 | 4 | 8 | 16),
        3 => matches!(bit_depth, 1 | 2 | 4 | 8),
        2 | 4 | 6 => matches!(bit_depth, 8 | 16),
        _ => false,
    }
}

/// The seven Adam7 passes: first column, first row, column step, row step.
const ADAM7: [(u64, u64, u64, u64); 7] = [
    (0, 0, 8, 8),
    (4, 0, 8, 8),
    (0, 4, 4, 8),
    (2, 0, 4, 4),
    (0, 2, 2, 4),
    (1, 0, 2, 2),
    (0, 1, 1, 2),
];

fn channels(color_type: u8) -> u64 {
    match color_type {
        0 | 3 => 1,
        2 => 3,
        4 => 2,
        _ => 4,
    }
}

fn scanline_bytes(width: u64, bits_per_pixel: u64) -> u64 {
    1 + (width * bits_per_pixel).div_ceil(8)
}

/// The size of the filtered image data a header declares: each row (of
/// each interlace pass) with its filter byte.
fn expected_raw_size(width: u32, height: u32, color_type: u8, bit_depth: u8, interlace: u8) -> u64 {
    let bits = channels(color_type) * u64::from(bit_depth);
    let (width, height) = (u64::from(width), u64::from(height));
    if interlace == 0 {
        return scanline_bytes(width, bits) * height;
    }
    ADAM7
        .iter()
        .map(|&(x_start, y_start, x_step, y_step)| {
            let pass_width = if width > x_start {
                (width - x_start).div_ceil(x_step)
            } else {
                0
            };
            let pass_height = if height > y_start {
                (height - y_start).div_ceil(y_step)
            } else {
                0
            };
            if pass_width > 0 && pass_height > 0 {
                scanline_bytes(pass_width, bits) * pass_height
            } else {
                0
            }
        })
        .sum()
}

/// Inflates zlib `data`, refusing output past `expected` bytes.
fn inflate(data: &[u8], expected: u64) -> Result<Vec<u8>, IconError> {
    let limit = usize::try_from(expected.saturating_add(1)).unwrap_or(usize::MAX);
    match miniz_oxide::inflate::decompress_to_vec_zlib_with_limit(data, limit) {
        Ok(output) if output.len() as u64 > expected => Err(IconError::DataTooLarge),
        Ok(output) => Ok(output),
        Err(error) if error.status == miniz_oxide::inflate::TINFLStatus::HasMoreOutput => {
            Err(IconError::DataTooLarge)
        }
        Err(_) => Err(IconError::DataInvalid),
    }
}

/// Checks a plugin icon's PNG container: at most 64 KiB, the signature, a
/// 13-byte `IHDR` first, exactly 64 x 64 pixels, a colour type and depth the
/// PNG specification allows, no animation, `IEND` last with nothing after
/// it, and image data that inflates to no more than the header's size.
/// Pixel data is not decoded.
///
/// # Errors
/// The first rule the file breaks.
pub fn validate_icon(bytes: &[u8]) -> Result<(), IconError> {
    if bytes.len() > ICON_MAXIMUM_BYTES {
        return Err(IconError::TooLarge);
    }
    if bytes.len() < PNG_SIGNATURE.len() || bytes[..8] != PNG_SIGNATURE {
        return Err(IconError::NotPng);
    }
    let (length, kind, data_offset) = chunk_header(bytes, 8)?;
    if &kind != b"IHDR" || length != 13 {
        return Err(IconError::MalformedHeader);
    }
    let ihdr = &bytes[data_offset..data_offset + 13];
    let width = u32::from_be_bytes([ihdr[0], ihdr[1], ihdr[2], ihdr[3]]);
    let height = u32::from_be_bytes([ihdr[4], ihdr[5], ihdr[6], ihdr[7]]);
    let (bit_depth, color_type, interlace) = (ihdr[8], ihdr[9], ihdr[12]);
    if width != ICON_EXTENT || height != ICON_EXTENT {
        return Err(IconError::WrongSize);
    }
    if !is_allowed_depth(color_type, bit_depth) || ihdr[10] != 0 || ihdr[11] != 0 || interlace > 1 {
        return Err(IconError::MalformedHeader);
    }
    let mut offset = data_offset + length + 4;
    let mut idat = Vec::new();
    loop {
        if offset >= bytes.len() {
            return Err(IconError::NoEnd);
        }
        let (length, kind, data_offset) = chunk_header(bytes, offset)?;
        if &kind == b"acTL" {
            return Err(IconError::Animated);
        }
        if &kind == b"IDAT" {
            idat.extend_from_slice(&bytes[data_offset..data_offset + length]);
        }
        offset = data_offset + length + 4;
        if &kind == b"IEND" {
            break;
        }
    }
    if offset != bytes.len() {
        return Err(IconError::TrailingData);
    }
    let expected = expected_raw_size(width, height, color_type, bit_depth, interlace);
    inflate(&idat, expected).map(|_| ())
}

/// Checks `bytes` against the icon rules and decodes them; `None` for an
/// icon the rules refuse or that does not decode, or that is not 64 x 64
/// once decoded.
#[must_use]
pub fn decode_icon(bytes: &[u8]) -> Option<PluginIcon> {
    validate_icon(bytes).ok()?;
    decode_png(bytes).filter(|icon| icon.width == ICON_EXTENT && icon.height == ICON_EXTENT)
}

/// The `icon.png` in plugin folder `directory`, decoded: `None` when there
/// is none, or it is larger than the rules allow, breaks them, or does not
/// decode, so the dock falls back to the plugin's other icon instead of
/// failing the plugin.
#[must_use]
pub fn load_icon(directory: &Path) -> Option<PluginIcon> {
    let path = directory.join(ICON_FILE_NAME);
    let metadata = std::fs::metadata(&path).ok()?;
    if !metadata.is_file() || metadata.len() > ICON_MAXIMUM_BYTES as u64 {
        return None;
    }
    decode_icon(&std::fs::read(&path).ok()?)
}

struct Header {
    width: u32,
    height: u32,
    bit_depth: u8,
    color_type: u8,
    interlace: u8,
}

impl Header {
    fn channels(&self) -> usize {
        channels(self.color_type) as usize
    }

    fn bits_per_pixel(&self) -> usize {
        self.channels() * usize::from(self.bit_depth)
    }
}

/// Decodes a PNG of any size up to 4096 pixels on a side to RGBA; `None`
/// when it is not a PNG, its header is outside the specification, or its
/// data is short, corrupt or longer than the header declares.
#[must_use]
pub fn decode_png(bytes: &[u8]) -> Option<PluginIcon> {
    if bytes.len() < 8 || bytes[..8] != PNG_SIGNATURE {
        return None;
    }
    let mut offset = 8;
    let mut header: Option<Header> = None;
    let mut palette: Vec<[u8; 3]> = Vec::new();
    let mut transparency: Vec<u8> = Vec::new();
    let mut data = Vec::new();
    while offset + 8 <= bytes.len() {
        let (length, kind, start) = chunk_header(bytes, offset).ok()?;
        let body = &bytes[start..start + length];
        match &kind {
            b"IHDR" if body.len() == 13 => {
                if body[10] != 0 || body[11] != 0 || body[12] > 1 {
                    return None;
                }
                header = Some(Header {
                    width: u32::from_be_bytes([body[0], body[1], body[2], body[3]]),
                    height: u32::from_be_bytes([body[4], body[5], body[6], body[7]]),
                    bit_depth: body[8],
                    color_type: body[9],
                    interlace: body[12],
                });
            }
            b"PLTE" => {
                palette = body
                    .chunks_exact(3)
                    .map(|entry| [entry[0], entry[1], entry[2]])
                    .collect();
            }
            b"tRNS" => transparency = body.to_vec(),
            b"IDAT" => data.extend_from_slice(body),
            b"IEND" => break,
            _ => {}
        }
        offset = start + length + 4;
    }
    let header = header?;
    if header.width == 0
        || header.height == 0
        || header.width > MAXIMUM_DECODE_EXTENT
        || header.height > MAXIMUM_DECODE_EXTENT
        || !is_allowed_depth(header.color_type, header.bit_depth)
    {
        return None;
    }
    let expected = expected_raw_size(
        header.width,
        header.height,
        header.color_type,
        header.bit_depth,
        header.interlace,
    );
    let raw = inflate(&data, expected).ok()?;
    let width = header.width as usize;
    let height = header.height as usize;
    let mut samples = vec![0u16; width * height * header.channels()];
    if header.interlace == 0 {
        unfilter_into(&header, &raw, 0, (0, 0, 1, 1), width, height, &mut samples)?;
    } else {
        let mut consumed = 0;
        for (x0, y0, dx, dy) in ADAM7 {
            let pass = (x0 as usize, y0 as usize, dx as usize, dy as usize);
            let pass_width = if width > pass.0 {
                (width - pass.0).div_ceil(pass.2)
            } else {
                0
            };
            let pass_height = if height > pass.1 {
                (height - pass.1).div_ceil(pass.3)
            } else {
                0
            };
            if pass_width == 0 || pass_height == 0 {
                continue;
            }
            consumed = unfilter_into(
                &header,
                &raw,
                consumed,
                pass,
                pass_width,
                pass_height,
                &mut samples,
            )?;
        }
    }
    Some(PluginIcon {
        width: header.width,
        height: header.height,
        rgba: Arc::new(to_rgba(&header, &samples, &palette, &transparency)?),
    })
}

/// Unfilters one image (or interlace pass) starting at `start` in `raw`,
/// writing its samples into place; returns where the next pass starts.
fn unfilter_into(
    header: &Header,
    raw: &[u8],
    start: usize,
    (x0, y0, dx, dy): (usize, usize, usize, usize),
    pass_width: usize,
    pass_height: usize,
    samples: &mut [u16],
) -> Option<usize> {
    let bits = header.bits_per_pixel();
    let stride = (pass_width * bits).div_ceil(8);
    let pixel_bytes = bits.div_ceil(8).max(1);
    let mut previous = vec![0u8; stride];
    let mut current = vec![0u8; stride];
    let mut position = start;
    let channels = header.channels();
    let width = header.width as usize;
    for row in 0..pass_height {
        let filter = *raw.get(position)?;
        let line = raw.get(position + 1..position + 1 + stride)?;
        position += 1 + stride;
        for index in 0..stride {
            let left = if index >= pixel_bytes {
                current[index - pixel_bytes]
            } else {
                0
            };
            let up = previous[index];
            let up_left = if index >= pixel_bytes {
                previous[index - pixel_bytes]
            } else {
                0
            };
            let predictor = match filter {
                0 => 0,
                1 => left,
                2 => up,
                3 => ((u16::from(left) + u16::from(up)) / 2) as u8,
                4 => paeth(left, up, up_left),
                _ => return None,
            };
            current[index] = line[index].wrapping_add(predictor);
        }
        let y = y0 + row * dy;
        for column in 0..pass_width {
            let x = x0 + column * dx;
            for channel in 0..channels {
                let sample_index = column * channels + channel;
                let value = read_sample(&current, sample_index, header.bit_depth);
                *samples.get_mut((y * width + x) * channels + channel)? = value;
            }
        }
        std::mem::swap(&mut previous, &mut current);
    }
    Some(position)
}

fn paeth(a: u8, b: u8, c: u8) -> u8 {
    let p = i16::from(a) + i16::from(b) - i16::from(c);
    let pa = (p - i16::from(a)).abs();
    let pb = (p - i16::from(b)).abs();
    let pc = (p - i16::from(c)).abs();
    if pa <= pb && pa <= pc {
        a
    } else if pb <= pc {
        b
    } else {
        c
    }
}

/// Sample `index` of an unfiltered row; `bit_depth` is one the
/// specification allows (checked before any row is read).
fn read_sample(line: &[u8], index: usize, bit_depth: u8) -> u16 {
    match bit_depth {
        16 => u16::from_be_bytes([line[index * 2], line[index * 2 + 1]]),
        8 => u16::from(line[index]),
        depth => {
            let depth = usize::from(depth);
            let bit = index * depth;
            let byte = line[bit / 8];
            let shift = 8 - depth - (bit % 8);
            u16::from((byte >> shift) & ((1u8 << depth) - 1))
        }
    }
}

/// A sample scaled to 8 bits.
fn to_8(value: u16, bit_depth: u8) -> u8 {
    match bit_depth {
        16 => (value >> 8) as u8,
        8 => value as u8,
        depth => {
            let maximum = (1u16 << depth) - 1;
            ((u32::from(value) * 255 + u32::from(maximum) / 2) / u32::from(maximum)) as u8
        }
    }
}

fn to_rgba(
    header: &Header,
    samples: &[u16],
    palette: &[[u8; 3]],
    transparency: &[u8],
) -> Option<Vec<u8>> {
    let depth = header.bit_depth;
    let pixels = (header.width * header.height) as usize;
    let mut out = Vec::with_capacity(pixels * 4);
    let key = |index: usize| -> Option<u16> {
        transparency
            .get(index * 2..index * 2 + 2)
            .map(|pair| u16::from_be_bytes([pair[0], pair[1]]))
    };
    for pixel in 0..pixels {
        match header.color_type {
            0 => {
                let gray = samples[pixel];
                let level = to_8(gray, depth);
                let alpha = if key(0) == Some(gray) { 0 } else { 255 };
                out.extend_from_slice(&[level, level, level, alpha]);
            }
            2 => {
                let rgb = &samples[pixel * 3..pixel * 3 + 3];
                let alpha =
                    if key(0) == Some(rgb[0]) && key(1) == Some(rgb[1]) && key(2) == Some(rgb[2]) {
                        0
                    } else {
                        255
                    };
                out.extend_from_slice(&[
                    to_8(rgb[0], depth),
                    to_8(rgb[1], depth),
                    to_8(rgb[2], depth),
                    alpha,
                ]);
            }
            3 => {
                let index = usize::from(samples[pixel]);
                let color = palette.get(index)?;
                let alpha = transparency.get(index).copied().unwrap_or(255);
                out.extend_from_slice(&[color[0], color[1], color[2], alpha]);
            }
            4 => {
                let level = to_8(samples[pixel * 2], depth);
                let alpha = to_8(samples[pixel * 2 + 1], depth);
                out.extend_from_slice(&[level, level, level, alpha]);
            }
            6 => {
                let rgba = &samples[pixel * 4..pixel * 4 + 4];
                out.extend(rgba.iter().map(|&value| to_8(value, depth)));
            }
            _ => return None,
        }
    }
    Some(out)
}

#[cfg(test)]
mod tests;
