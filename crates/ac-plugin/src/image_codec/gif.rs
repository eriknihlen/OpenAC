//! GIF 87a and 89a: the first frame, on a canvas of the logical screen's
//! size that starts fully transparent; the frame's transparent index (from
//! its graphic control extension) leaves the canvas as it is. The frame's
//! own colour table, else the global one; interlaced frames; LZW codes of
//! up to 12 bits. A frame that reaches past the canvas is corrupt; image
//! data that ends early leaves the rest of the frame transparent.

use super::{CodecError, Decoded, Reader, Result, check_size};

const FORMAT: &str = "GIF";

pub(super) fn is_gif(bytes: &[u8]) -> bool {
    bytes.starts_with(b"GIF87a") || bytes.starts_with(b"GIF89a")
}

pub(super) fn dimensions(bytes: &[u8]) -> Option<(u32, u32)> {
    let b = bytes.get(6..10)?;
    Some((
        u32::from(u16::from_le_bytes([b[0], b[1]])),
        u32::from(u16::from_le_bytes([b[2], b[3]])),
    ))
}

fn color_table(r: &mut Reader<'_>, flags: u8) -> Result<Vec<[u8; 4]>> {
    let entries = 2usize << (flags & 7);
    Ok(r.take(entries * 3)?
        .chunks_exact(3)
        .map(|c| [c[0], c[1], c[2], 255])
        .collect())
}

/// The data sub-blocks that follow, joined.
fn sub_blocks(r: &mut Reader<'_>) -> Result<Vec<u8>> {
    let mut out = Vec::new();
    loop {
        let length = usize::from(r.u8()?);
        if length == 0 {
            return Ok(out);
        }
        out.extend_from_slice(r.take(length)?);
    }
}

pub(super) fn decode(bytes: &[u8]) -> Result<Decoded> {
    let mut r = Reader::new(bytes, FORMAT);
    if !is_gif(r.take(6)?) {
        return Err(CodecError::Corrupt(FORMAT, "no GIF signature"));
    }
    let width = r.u16()?;
    let height = r.u16()?;
    let flags = r.u8()?;
    r.skip(2)?; // background index, aspect ratio
    if width == 0 || height == 0 {
        return Err(CodecError::Corrupt(FORMAT, "no pixels"));
    }
    check_size(u32::from(width), u32::from(height))?;
    let global = if flags & 0x80 != 0 {
        color_table(&mut r, flags)?
    } else {
        Vec::new()
    };
    let mut transparent: Option<u8> = None;
    loop {
        match r.u8()? {
            0x21 => {
                let label = r.u8()?;
                let body = sub_blocks(&mut r)?;
                // Graphic control: packed flags, delay, transparent index.
                if label == 0xF9 && body.len() >= 4 {
                    transparent = (body[0] & 1 != 0).then_some(body[3]);
                }
            }
            0x2C => return frame(&mut r, width, height, &global, transparent),
            0x3B => return Err(CodecError::Corrupt(FORMAT, "no image")),
            _ => return Err(CodecError::Corrupt(FORMAT, "unknown block")),
        }
    }
}

fn frame(
    r: &mut Reader<'_>,
    width: u16,
    height: u16,
    global: &[[u8; 4]],
    transparent: Option<u8>,
) -> Result<Decoded> {
    let left = usize::from(r.u16()?);
    let top = usize::from(r.u16()?);
    let frame_width = usize::from(r.u16()?);
    let frame_height = usize::from(r.u16()?);
    let flags = r.u8()?;
    let (width, height) = (usize::from(width), usize::from(height));
    if left + frame_width > width || top + frame_height > height {
        return Err(CodecError::Corrupt(
            FORMAT,
            "the frame reaches past the canvas",
        ));
    }
    let local = if flags & 0x80 != 0 {
        Some(color_table(r, flags)?)
    } else {
        None
    };
    let palette = local.as_deref().unwrap_or(global);
    if palette.is_empty() {
        return Err(CodecError::Corrupt(FORMAT, "no colour table"));
    }
    let minimum_code = r.u8()?;
    let data = sub_blocks(r)?;
    let indices = lzw(minimum_code, &data, frame_width * frame_height)?;

    // Interlaced rows come in four passes: every 8th from 0, every 8th from
    // 4, every 4th from 2, every 2nd from 1.
    let rows: Vec<usize> = if flags & 0x40 != 0 {
        [(0, 8), (4, 8), (2, 4), (1, 2)]
            .into_iter()
            .flat_map(|(start, step)| (start..frame_height).step_by(step))
            .collect()
    } else {
        (0..frame_height).collect()
    };
    let mut rgba = vec![0u8; width * height * 4];
    if frame_width > 0 {
        for (row, line) in rows.iter().zip(indices.chunks(frame_width)) {
            for (column, &index) in line.iter().enumerate() {
                if Some(index) == transparent {
                    continue;
                }
                if let Some(color) = palette.get(usize::from(index)) {
                    let at = ((top + row) * width + left + column) * 4;
                    rgba[at..at + 4].copy_from_slice(color);
                }
            }
        }
    }
    Ok(Decoded {
        width: width as u32,
        height: height as u32,
        rgba,
    })
}

/// The GIF variant of LZW: least significant bit first, codes growing from
/// `minimum + 1` bits to 12, a clear code and an end code. Stops at
/// `count` indices or where the data or the end code stops.
fn lzw(minimum: u8, data: &[u8], count: usize) -> Result<Vec<u8>> {
    if !(1..=11).contains(&minimum) {
        return Err(CodecError::Corrupt(FORMAT, "bad LZW code size"));
    }
    const MAXIMUM_CODES: usize = 4096;
    let clear = 1usize << minimum;
    let end = clear + 1;
    let mut prefix = vec![0u16; MAXIMUM_CODES];
    let mut suffix = vec![0u8; MAXIMUM_CODES];
    let mut first = vec![0u8; MAXIMUM_CODES];
    for code in 0..clear {
        suffix[code] = code as u8;
        first[code] = code as u8;
    }
    let mut out = Vec::with_capacity(count);
    let mut stack: Vec<u8> = Vec::with_capacity(MAXIMUM_CODES);
    let mut size = u32::from(minimum) + 1;
    let mut next = clear + 2;
    let mut previous: Option<usize> = None;
    let mut bits: u32 = 0;
    let mut held: u32 = 0;
    let mut bytes = data.iter();
    while out.len() < count {
        while held < size {
            let Some(&byte) = bytes.next() else {
                return Ok(out);
            };
            bits |= u32::from(byte) << held;
            held += 8;
        }
        let code = (bits & ((1 << size) - 1)) as usize;
        bits >>= size;
        held -= size;
        if code == clear {
            size = u32::from(minimum) + 1;
            next = clear + 2;
            previous = None;
            continue;
        }
        if code == end {
            break;
        }
        let Some(prior) = previous else {
            if code >= clear {
                return Err(CodecError::Corrupt(FORMAT, "bad first LZW code"));
            }
            out.push(code as u8);
            previous = Some(code);
            continue;
        };
        let lead = if code < next {
            first[code]
        } else if code == next {
            first[prior]
        } else {
            return Err(CodecError::Corrupt(FORMAT, "bad LZW code"));
        };
        if next < MAXIMUM_CODES {
            prefix[next] = prior as u16;
            suffix[next] = lead;
            first[next] = first[prior];
            next += 1;
            if next == 1 << size && size < 12 {
                size += 1;
            }
        }
        // The string of `code`, last byte first.
        stack.clear();
        let mut at = code;
        while at >= clear {
            stack.push(suffix[at]);
            at = usize::from(prefix[at]);
        }
        stack.push(suffix[at]);
        out.extend(stack.iter().rev().take(count - out.len()));
        previous = Some(code);
    }
    Ok(out)
}
