//! Truevision TGA: colour-mapped (types 1 and 9), true-colour (2 and 10)
//! and grey (3 and 11), raw or run-length; 8-bit indices or grey, 16-bit
//! grey with alpha, 15 and 16-bit 5-5-5 colour (the attribute bit is not
//! read as alpha: writers set it inconsistently), 24-bit BGR and 32-bit
//! BGRA; colour maps of 8, 15, 16, 24 or 32-bit entries. Rows run up from
//! the bottom unless the descriptor's top-left origin bit is set; the
//! right-to-left bit is not read. An index outside the map takes its first
//! entry. The format has no signature, so a file is a TGA only when its
//! header makes sense.

use super::{CodecError, Decoded, Reader, Result, check_size};

const FORMAT: &str = "TGA";

struct Header {
    id_length: u8,
    map_type: u8,
    image_type: u8,
    map_first: u16,
    map_length: u16,
    map_bits: u8,
    width: u16,
    height: u16,
    bits: u8,
    descriptor: u8,
}

fn header(bytes: &[u8]) -> Result<Header> {
    let mut r = Reader::new(bytes, FORMAT);
    let h = Header {
        id_length: r.u8()?,
        map_type: r.u8()?,
        image_type: r.u8()?,
        map_first: r.u16()?,
        map_length: r.u16()?,
        map_bits: r.u8()?,
        width: {
            r.skip(4)?; // x and y origin
            r.u16()?
        },
        height: r.u16()?,
        bits: r.u8()?,
        descriptor: r.u8()?,
    };
    let sensible = match h.map_type {
        1 => {
            matches!(h.image_type, 1 | 9)
                && h.map_length > 0
                && matches!(h.map_bits, 8 | 15 | 16 | 24 | 32)
                && matches!(h.bits, 8 | 16)
        }
        0 => match h.image_type {
            2 | 10 => matches!(h.bits, 15 | 16 | 24 | 32),
            3 | 11 => matches!(h.bits, 8 | 16),
            _ => false,
        },
        _ => false,
    };
    if !sensible || h.width == 0 || h.height == 0 {
        return Err(CodecError::Corrupt(FORMAT, "the header makes no sense"));
    }
    Ok(h)
}

pub(super) fn is_tga(bytes: &[u8]) -> bool {
    header(bytes).is_ok()
}

pub(super) fn dimensions(bytes: &[u8]) -> Option<(u32, u32)> {
    header(bytes)
        .ok()
        .map(|h| (u32::from(h.width), u32::from(h.height)))
}

/// A 5-5-5 colour, each field scaled to 0..=255.
fn rgb16(v: u16) -> [u8; 4] {
    let scale = |field: u16| ((u32::from(field & 31) * 255) / 31) as u8;
    [scale(v >> 10), scale(v >> 5), scale(v), 255]
}

/// One stored value of `bits` bits (as bytes: `bits.div_ceil(8)`) as a
/// colour: `grey` for an 8 or 16-bit grey image.
fn color(b: &[u8], bits: u8, grey: bool) -> [u8; 4] {
    match (bits, grey) {
        (8, _) => [b[0], b[0], b[0], 255],
        (16, true) => [b[0], b[0], b[0], b[1]],
        (15 | 16, false) => rgb16(u16::from_le_bytes([b[0], b[1]])),
        (24, _) => [b[2], b[1], b[0], 255],
        _ => [b[2], b[1], b[0], b[3]],
    }
}

pub(super) fn decode(bytes: &[u8]) -> Result<Decoded> {
    let h = header(bytes)?;
    let width = usize::from(h.width);
    let height = usize::from(h.height);
    check_size(u32::from(h.width), u32::from(h.height))?;
    let mut r = Reader::new(bytes, FORMAT);
    r.skip(18 + usize::from(h.id_length))?;

    let palette: Vec<[u8; 4]> = if h.map_type == 1 {
        let entry = usize::from(h.map_bits).div_ceil(8);
        r.take(entry * usize::from(h.map_length))?
            .chunks_exact(entry)
            .map(|e| color(e, h.map_bits, false))
            .collect()
    } else {
        Vec::new()
    };
    let indexed = h.map_type == 1;
    let grey = matches!(h.image_type, 3 | 11);
    let size = usize::from(h.bits).div_ceil(8);
    let read = |raw: &[u8]| -> [u8; 4] {
        if indexed {
            let index = if size == 1 {
                usize::from(raw[0])
            } else {
                usize::from(u16::from_le_bytes([raw[0], raw[1]]))
            };
            let entry = index
                .checked_sub(usize::from(h.map_first))
                .filter(|e| *e < palette.len())
                .unwrap_or(0);
            palette[entry]
        } else {
            color(raw, h.bits, grey)
        }
    };

    let count = width * height;
    let mut pixels: Vec<[u8; 4]> = Vec::with_capacity(count);
    if h.image_type >= 9 {
        while pixels.len() < count {
            let packet = r.u8()?;
            let run = usize::from(packet & 0x7F) + 1;
            let room = count - pixels.len();
            if packet & 0x80 != 0 {
                let value = read(r.take(size)?);
                pixels.extend(std::iter::repeat_n(value, run.min(room)));
            } else {
                for _ in 0..run {
                    let value = read(r.take(size)?);
                    if pixels.len() < count {
                        pixels.push(value);
                    }
                }
            }
        }
    } else {
        for raw in r.take(count * size)?.chunks_exact(size) {
            pixels.push(read(raw));
        }
    }

    let top_down = h.descriptor & 0x20 != 0;
    let mut rgba = vec![0u8; count * 4];
    for y in 0..height {
        let source = if top_down { y } else { height - 1 - y };
        for x in 0..width {
            let at = (y * width + x) * 4;
            rgba[at..at + 4].copy_from_slice(&pixels[source * width + x]);
        }
    }
    Ok(Decoded {
        width: u32::from(h.width),
        height: u32::from(h.height),
        rgba,
    })
}
