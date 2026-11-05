//! Windows bitmaps: the core (12-byte), info (40) and V3 to V5 (56, 108,
//! 124) headers; 1, 4 and 8 bits through a palette, 16 and 32 through
//! channel masks (the 5-5-5 and 8-8-8-8 defaults, or the file's own with
//! `BI_BITFIELDS`), and 24-bit BGR; bottom-up or top-down rows. Run-length
//! and embedded JPEG or PNG data are not read. A 32-bit file without masks
//! whose alpha bytes are all zero is opaque (writers leave the byte unused).

use super::{CodecError, Decoded, Reader, Result, check_size};

const FORMAT: &str = "BMP";

/// The header sizes read here.
const HEADER_SIZES: [u32; 5] = [12, 40, 56, 108, 124];

/// `BI_RGB`, `BI_RLE8`, `BI_RLE4`, `BI_BITFIELDS`.
const BI_RGB: u32 = 0;
const BI_BITFIELDS: u32 = 3;

pub(super) fn is_bmp(bytes: &[u8]) -> bool {
    bytes.len() >= 18
        && bytes.starts_with(b"BM")
        && HEADER_SIZES.contains(&u32::from_le_bytes([
            bytes[14], bytes[15], bytes[16], bytes[17],
        ]))
}

struct Header {
    width: u32,
    height: u32,
    top_down: bool,
    bits: u16,
    compression: u32,
    offset: u32,
    header_size: u32,
    colors_used: u32,
    /// Red, green, blue, alpha.
    masks: [u32; 4],
    /// Whether all-zero alpha means opaque (32 bits without masks).
    zero_alpha_is_opaque: bool,
}

fn header(bytes: &[u8]) -> Result<Header> {
    let mut r = Reader::new(bytes, FORMAT);
    if r.take(2)? != b"BM" {
        return Err(CodecError::Corrupt(FORMAT, "no BM signature"));
    }
    r.skip(8)?; // file size, two reserved words
    let offset = r.u32()?;
    let header_size = r.u32()?;
    if !HEADER_SIZES.contains(&header_size) {
        return Err(CodecError::Unsupported(FORMAT, "header"));
    }
    let (width, height, planes, bits, compression, colors_used);
    let mut file_masks = [0u32; 4];
    if header_size == 12 {
        width = i32::from(r.u16()?);
        height = i32::from(r.u16()?);
        planes = r.u16()?;
        bits = r.u16()?;
        compression = BI_RGB;
        colors_used = 0;
    } else {
        width = r.i32()?;
        height = r.i32()?;
        planes = r.u16()?;
        bits = r.u16()?;
        compression = r.u32()?;
        r.skip(12)?; // image size, horizontal and vertical resolution
        colors_used = r.u32()?;
        r.skip(4)?; // important colours
        if header_size == 40 {
            if compression == BI_BITFIELDS {
                file_masks = [r.u32()?, r.u32()?, r.u32()?, 0];
            }
        } else {
            file_masks = [r.u32()?, r.u32()?, r.u32()?, r.u32()?];
        }
    }
    if planes != 1 {
        return Err(CodecError::Corrupt(FORMAT, "planes is not 1"));
    }
    if width <= 0 || height == 0 || height == i32::MIN {
        return Err(CodecError::Corrupt(FORMAT, "no pixels"));
    }
    match compression {
        BI_RGB | BI_BITFIELDS => {}
        1 | 2 => return Err(CodecError::Unsupported(FORMAT, "run-length data")),
        _ => return Err(CodecError::Unsupported(FORMAT, "compression")),
    }
    if !matches!(bits, 1 | 4 | 8 | 16 | 24 | 32) {
        return Err(CodecError::Unsupported(FORMAT, "bit depth"));
    }
    let (masks, zero_alpha_is_opaque) = if compression == BI_BITFIELDS {
        if !matches!(bits, 16 | 32) {
            return Err(CodecError::Corrupt(FORMAT, "masks on a depth without them"));
        }
        if file_masks[..3].contains(&0) {
            return Err(CodecError::Corrupt(FORMAT, "a channel mask is empty"));
        }
        (file_masks, false)
    } else {
        match bits {
            16 => ([0x7C00, 0x03E0, 0x001F, 0], false),
            32 => ([0x00FF_0000, 0x0000_FF00, 0x0000_00FF, 0xFF00_0000], true),
            _ => ([0; 4], false),
        }
    };
    Ok(Header {
        width: width.unsigned_abs(),
        height: height.unsigned_abs(),
        top_down: height < 0,
        bits,
        compression,
        offset,
        header_size,
        colors_used,
        masks,
        zero_alpha_is_opaque,
    })
}

pub(super) fn dimensions(bytes: &[u8]) -> Option<(u32, u32)> {
    header(bytes).ok().map(|h| (h.width, h.height))
}

/// A field of `mask` widened (or cut) to eight bits: a short field's bits
/// repeat, so its largest value is 255.
fn channel(pixel: u32, mask: u32) -> u8 {
    if mask == 0 {
        return 0;
    }
    let shift = mask.trailing_zeros();
    let width = 32 - (mask >> shift).leading_zeros();
    let value = (pixel & mask) >> shift;
    if width >= 8 {
        return (value >> (width - 8)) as u8;
    }
    let mut out = 0u32;
    let mut filled = 0;
    while filled < 8 {
        out = (out << width) | value;
        filled += width;
    }
    (out >> (filled - 8)) as u8
}

pub(super) fn decode(bytes: &[u8]) -> Result<Decoded> {
    let h = header(bytes)?;
    check_size(h.width, h.height)?;
    let width = h.width as usize;
    let height = h.height as usize;

    let palette_start = 14
        + h.header_size as usize
        + if h.header_size == 40 && h.compression == BI_BITFIELDS {
            12
        } else {
            0
        };
    let offset = h.offset as usize;
    if offset < palette_start || offset > bytes.len() {
        return Err(CodecError::Corrupt(FORMAT, "bad pixel data offset"));
    }
    let mut palette: Vec<[u8; 4]> = Vec::new();
    if h.bits <= 8 {
        let entry = if h.header_size == 12 { 3 } else { 4 };
        let most = 1usize << h.bits;
        let declared = if h.colors_used == 0 || h.colors_used as usize > most {
            most
        } else {
            h.colors_used as usize
        };
        // The palette is what fits between the header and the pixels.
        let count = declared.min((offset - palette_start) / entry);
        if count == 0 {
            return Err(CodecError::Corrupt(FORMAT, "no palette"));
        }
        palette = bytes[palette_start..palette_start + count * entry]
            .chunks_exact(entry)
            .map(|e| [e[2], e[1], e[0], 255])
            .collect();
    }

    let row_bytes = (width * usize::from(h.bits)).div_ceil(32) * 4;
    let data = &bytes[offset..];
    if data.len() < row_bytes * height {
        return Err(CodecError::Truncated(FORMAT));
    }
    let mut rgba = vec![0u8; width * height * 4];
    let mut any_alpha = false;
    for y in 0..height {
        let source = if h.top_down { y } else { height - 1 - y };
        let row = &data[source * row_bytes..(source + 1) * row_bytes];
        let out = &mut rgba[y * width * 4..(y + 1) * width * 4];
        for (x, px) in out.chunks_exact_mut(4).enumerate() {
            let color = match h.bits {
                1 | 4 | 8 => {
                    let bit = x * usize::from(h.bits);
                    let byte = row[bit / 8];
                    let shift = 8 - usize::from(h.bits) - bit % 8;
                    let index = (byte >> shift) & ((1u16 << h.bits) - 1) as u8;
                    palette
                        .get(usize::from(index))
                        .copied()
                        .unwrap_or([0, 0, 0, 255])
                }
                24 => [row[x * 3 + 2], row[x * 3 + 1], row[x * 3], 255],
                16 | 32 => {
                    let pixel = if h.bits == 16 {
                        u32::from(u16::from_le_bytes([row[x * 2], row[x * 2 + 1]]))
                    } else {
                        u32::from_le_bytes([
                            row[x * 4],
                            row[x * 4 + 1],
                            row[x * 4 + 2],
                            row[x * 4 + 3],
                        ])
                    };
                    let alpha = if h.masks[3] == 0 {
                        255
                    } else {
                        channel(pixel, h.masks[3])
                    };
                    any_alpha |= alpha != 0;
                    [
                        channel(pixel, h.masks[0]),
                        channel(pixel, h.masks[1]),
                        channel(pixel, h.masks[2]),
                        alpha,
                    ]
                }
                // The header refuses every other depth.
                _ => [0; 4],
            };
            px.copy_from_slice(&color);
        }
    }
    if h.zero_alpha_is_opaque && !any_alpha {
        for px in rgba.chunks_exact_mut(4) {
            px[3] = 255;
        }
    }
    Ok(Decoded {
        width: h.width,
        height: h.height,
        rgba,
    })
}
