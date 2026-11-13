//! WebP: a RIFF file holding a lossy (`VP8 `), lossless (`VP8L`) or
//! extended (`VP8X`: alpha, animation, metadata) image. The size is read
//! here from the first chunk's header; the pixels come from the `image-webp`
//! decoder. An animation gives its first frame, as a GIF does, on a canvas
//! of its declared size. The chunks are walked here before the decoder
//! runs, so a frame whose bitstream is larger than declared, which the
//! decoder would allocate for (or, in an animation with alpha, index past
//! the end of its buffer and panic on), is refused first.

use std::io::Cursor;

use image_webp::{DecodingError, WebPDecoder};

use super::{CodecError, Decoded, Result, check_size};

const FORMAT: &str = "WebP";

/// Whether `bytes` start as a WebP file: a RIFF container of form `WEBP`.
pub(super) fn is_webp(bytes: &[u8]) -> bool {
    bytes.starts_with(b"RIFF") && bytes.get(8..12) == Some(b"WEBP")
}

/// The image's size from its first chunk's header: the frame header of a
/// lossy image, the header of a lossless one, or the canvas of an extended
/// one. `None` when that header is short, malformed or not one of these.
pub(super) fn dimensions(bytes: &[u8]) -> Option<(u32, u32)> {
    // The first chunk as far as the file and its declared size both reach:
    // the header needs only its first bytes.
    let name: &[u8; 4] = bytes.get(12..16)?.try_into().ok()?;
    let size = u32::from_le_bytes(bytes.get(16..20)?.try_into().ok()?) as usize;
    let payload = &bytes[20..bytes.len().min(20usize.saturating_add(size))];
    match name {
        b"VP8X" => canvas(payload),
        _ => bitstream_size(name, payload)?.ok(),
    }
}

/// A chunk's name and payload, from `at` to the end of `bytes`; an error
/// once a chunk's header or payload runs past the end.
fn chunks(bytes: &[u8], mut at: usize) -> impl Iterator<Item = Result<(&[u8; 4], &[u8])>> {
    std::iter::from_fn(move || {
        if at >= bytes.len() {
            return None;
        }
        let chunk = bytes
            .get(at..at + 8)
            .and_then(|header| {
                let name: &[u8; 4] = header[..4].try_into().ok()?;
                let size = u32::from_le_bytes(header[4..8].try_into().ok()?) as usize;
                let payload = bytes.get(at + 8..(at + 8).checked_add(size)?)?;
                // Payloads are padded to an even length.
                at += 8 + size + (size & 1);
                Some((name, payload))
            })
            .ok_or(CodecError::Truncated(FORMAT));
        if chunk.is_err() {
            at = bytes.len();
        }
        Some(chunk)
    })
}

/// An extended header's canvas: flags and three reserved bytes, then width
/// - 1 and height - 1 in 24 bits each.
fn canvas(payload: &[u8]) -> Option<(u32, u32)> {
    if payload.len() < 10 {
        return None;
    }
    Some((le24(payload, 4)? + 1, le24(payload, 7)? + 1))
}

/// A lossy or lossless bitstream's size from its header; `None` when the
/// chunk is not a bitstream.
fn bitstream_size(name: &[u8; 4], payload: &[u8]) -> Option<Result<(u32, u32)>> {
    let corrupt = |why| Some(Err(CodecError::Corrupt(FORMAT, why)));
    match name {
        b"VP8 " => {
            // A three-byte frame tag (a key frame has bit 0 clear), the start
            // code, then 14-bit width and height with a 2-bit scale above each.
            if payload.len() < 10 {
                return Some(Err(CodecError::Truncated(FORMAT)));
            }
            if payload[0] & 1 != 0 {
                return Some(Err(CodecError::Unsupported(
                    FORMAT,
                    "inter-predicted frames",
                )));
            }
            if payload[3..6] != [0x9D, 0x01, 0x2A] {
                return corrupt("a lossy frame has no start code");
            }
            let width = u32::from(u16::from_le_bytes([payload[6], payload[7]])) & 0x3FFF;
            let height = u32::from(u16::from_le_bytes([payload[8], payload[9]])) & 0x3FFF;
            if width == 0 || height == 0 {
                return corrupt("the image has no pixels");
            }
            Some(Ok((width, height)))
        }
        b"VP8L" => {
            // The signature byte, then width - 1 and height - 1 in 14 bits
            // each, an alpha hint and a 3-bit version that must be 0.
            if payload.len() < 5 {
                return Some(Err(CodecError::Truncated(FORMAT)));
            }
            let bits = u32::from_le_bytes([payload[1], payload[2], payload[3], payload[4]]);
            if payload[0] != 0x2F || bits >> 29 != 0 {
                return corrupt("a lossless image has a bad signature");
            }
            Some(Ok(((bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1)))
        }
        _ => None,
    }
}

/// A bitstream's size as [`bitstream_size`] reads it, once its whole chunk
/// is in hand: a lossy frame's first partition (its length is the frame
/// tag's top 19 bits) must also fit in the chunk, as the decoder sizes its
/// buffer for it before reading it.
fn checked_bitstream(name: &[u8; 4], payload: &[u8]) -> Option<Result<(u32, u32)>> {
    let size = bitstream_size(name, payload)?;
    if name == b"VP8 " && size.is_ok() {
        let tag = u32::from_le_bytes([payload[0], payload[1], payload[2], 0]);
        if 10 + (tag >> 5) as usize > payload.len() {
            return Some(Err(CodecError::Truncated(FORMAT)));
        }
    }
    Some(size)
}

/// Checks, before the decoder runs, every size it will allocate for: the
/// declared image, and the size in the header of each bitstream it decodes
/// (an extended image's frame, an animation's first frame), which must be
/// the size declared for it. The decoder sizes its planes by the
/// bitstream's header, so a large frame in a small canvas would otherwise
/// be allocated before it was found not to fit.
fn check_layout(bytes: &[u8]) -> Result<(u32, u32)> {
    let mismatch = CodecError::Corrupt(FORMAT, "a frame's size is not the size declared for it");
    let mut top = chunks(bytes, 12);
    let (name, payload) = top.next().ok_or(CodecError::Truncated(FORMAT))??;
    if let Some(size) = checked_bitstream(name, payload) {
        let (width, height) = size?;
        check_size(width, height)?;
        return supported(width, height);
    }
    if name != b"VP8X" {
        return Err(CodecError::Corrupt(
            FORMAT,
            "the first chunk is not an image",
        ));
    }
    let (width, height) = canvas(payload).ok_or(CodecError::Truncated(FORMAT))?;
    check_size(width, height)?;
    let animated = payload[0] & 0x02 != 0;
    for chunk in top {
        let (name, payload) = chunk?;
        if animated && name == b"ANMF" {
            // Only the first frame is decoded: its place and size (24 bits
            // each), duration and flags, then an optional alpha chunk and
            // its bitstream.
            if payload.len() < 16 {
                return Err(CodecError::Truncated(FORMAT));
            }
            let frame = (
                le24(payload, 6).unwrap_or(0) + 1,
                le24(payload, 9).unwrap_or(0) + 1,
            );
            let mut inner = chunks(payload, 16);
            let (mut name, mut data) = inner.next().ok_or(CodecError::Truncated(FORMAT))??;
            if name == b"ALPH" {
                // The decoder reads what follows alpha as lossy colour.
                (name, data) = inner.next().ok_or(CodecError::Truncated(FORMAT))??;
                if name != b"VP8 " {
                    return Err(CodecError::Corrupt(
                        FORMAT,
                        "alpha is not followed by lossy colour",
                    ));
                }
            }
            let size = checked_bitstream(name, data).ok_or(mismatch.clone())??;
            check_size(size.0, size.1)?;
            if size != frame {
                return Err(mismatch);
            }
            supported(size.0, size.1)?;
            return Ok((width, height));
        }
        if !animated && let Some(size) = checked_bitstream(name, payload) {
            let size = size?;
            check_size(size.0, size.1)?;
            if size != (width, height) {
                return Err(mismatch);
            }
        }
    }
    supported(width, height)
}

/// The decoder reads a lossless image 16384 pixels across as 0 across;
/// nothing that wide fits the pixel budget unless it is a thin strip.
fn supported(width: u32, height: u32) -> Result<(u32, u32)> {
    if width >= 1 << 14 || height >= 1 << 14 {
        return Err(CodecError::Unsupported(
            FORMAT,
            "images 16384 or more pixels across",
        ));
    }
    Ok((width, height))
}

/// Decodes a WebP file to RGBA. Every size the decoder allocates for is
/// checked first ([`check_layout`]). The file ends where its RIFF header
/// says; bytes after that are not read.
pub(super) fn decode(bytes: &[u8]) -> Result<Decoded> {
    let riff_size = u32::from_le_bytes(
        bytes
            .get(4..8)
            .and_then(|b| b.try_into().ok())
            .ok_or(CodecError::Truncated(FORMAT))?,
    ) as usize;
    if riff_size < 4 {
        return Err(CodecError::Corrupt(FORMAT, "the RIFF size is too small"));
    }
    let bytes = 8usize
        .checked_add(riff_size)
        .and_then(|end| bytes.get(..end))
        .ok_or(CodecError::Truncated(FORMAT))?;
    let declared = check_layout(bytes)?;
    let mut decoder = WebPDecoder::new(Cursor::new(bytes)).map_err(codec_error)?;
    let (width, height) = decoder.dimensions();
    if (width, height) != declared {
        return Err(CodecError::Corrupt(FORMAT, "the image's size is unclear"));
    }
    let size = decoder.output_buffer_size().ok_or(CodecError::TooLarge)?;
    let mut pixels = vec![0; size];
    decoder.read_image(&mut pixels).map_err(codec_error)?;
    let rgba = if decoder.has_alpha() {
        pixels
    } else {
        pixels
            .chunks_exact(3)
            .flat_map(|rgb| [rgb[0], rgb[1], rgb[2], 255])
            .collect()
    };
    Ok(Decoded {
        width,
        height,
        rgba,
    })
}

/// The decoder's refusal as this module's: a short file is truncated, a
/// size past the platform's is too large, a lossy frame that is not a key
/// frame (which a still image never holds) is unsupported, and anything
/// else is corrupt.
fn codec_error(error: DecodingError) -> CodecError {
    match error {
        DecodingError::IoError(error) if error.kind() == std::io::ErrorKind::UnexpectedEof => {
            CodecError::Truncated(FORMAT)
        }
        DecodingError::ImageTooLarge => CodecError::TooLarge,
        DecodingError::UnsupportedFeature(_) => {
            CodecError::Unsupported(FORMAT, "inter-predicted frames")
        }
        _ => CodecError::Corrupt(FORMAT, "the image does not decode"),
    }
}

fn le24(bytes: &[u8], at: usize) -> Option<u32> {
    let b = bytes.get(at..at + 3)?;
    Some(u32::from_le_bytes([b[0], b[1], b[2], 0]))
}
