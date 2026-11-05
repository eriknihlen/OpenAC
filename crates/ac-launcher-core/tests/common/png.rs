//! Real 64x64 PNGs and every header-rule refusal (OpenAC
//! `tests/Fixtures/plugin-icons/PngTestData.cs`).
#![allow(dead_code)]

const SIGNATURE: [u8; 8] = [137, 80, 78, 71, 13, 10, 26, 10];
const IHDR_CHUNK_END: usize = 8 + 8 + 13 + 4;
pub const EXTENT: u32 = 64;

fn crc32(bytes: &[u8]) -> u32 {
    ac_launcher_core::updates::zip::crc32(bytes)
}

fn chunk(kind: &str, data: &[u8]) -> Vec<u8> {
    let mut out = Vec::with_capacity(12 + data.len());
    out.extend_from_slice(&(data.len() as u32).to_be_bytes());
    out.extend_from_slice(kind.as_bytes());
    out.extend_from_slice(data);
    let crc = crc32(&out[4..]);
    out.extend_from_slice(&crc.to_be_bytes());
    out
}

fn raw_scanlines(width: u32, height: u32) -> Vec<u8> {
    let stride = 1 + width as usize * 4;
    let mut raw = vec![0u8; stride * height as usize];
    for y in 0..height as usize {
        for x in 0..width as usize * 4 {
            raw[y * stride + 1 + x] = ((x + y) % 256) as u8;
        }
    }
    raw
}

fn build(width: u32, height: u32, raw: Option<Vec<u8>>, header: Option<[u8; 5]>) -> Vec<u8> {
    let raw = raw.unwrap_or_else(|| raw_scanlines(width, height));
    let idat = miniz_oxide::deflate::compress_to_vec_zlib(&raw, 6);
    let mut ihdr = [0u8; 13];
    ihdr[..4].copy_from_slice(&width.to_be_bytes());
    ihdr[4..8].copy_from_slice(&height.to_be_bytes());
    ihdr[8] = 8;
    ihdr[9] = 6;
    if let Some(h) = header {
        ihdr[8..13].copy_from_slice(&h);
    }
    let mut out = SIGNATURE.to_vec();
    out.extend(chunk("IHDR", &ihdr));
    out.extend(chunk("IDAT", &idat));
    out.extend(chunk("IEND", &[]));
    out
}

pub fn valid() -> Vec<u8> {
    build(EXTENT, EXTENT, None, None)
}

pub fn over_maximum_bytes() -> Vec<u8> {
    let mut result = valid();
    result.resize((64 * 1024 + 1).max(result.len() + 1), 0);
    result
}

pub fn no_signature() -> Vec<u8> {
    let mut bytes = valid();
    bytes[0] = 0;
    bytes
}

pub fn first_chunk_wrong_type() -> Vec<u8> {
    let mut bytes = valid();
    bytes[12..16].copy_from_slice(b"IHDX");
    bytes
}

pub fn first_chunk_wrong_length() -> Vec<u8> {
    let mut bytes = valid();
    bytes[8..12].copy_from_slice(&12u32.to_be_bytes());
    bytes
}

pub fn wrong_dimensions() -> Vec<u8> {
    build(32, 32, None, None)
}

fn insert_after_ihdr(insert: &[u8]) -> Vec<u8> {
    let valid = valid();
    let mut result = valid[..IHDR_CHUNK_END].to_vec();
    result.extend_from_slice(insert);
    result.extend_from_slice(&valid[IHDR_CHUNK_END..]);
    result
}

pub fn chunk_length_overflow() -> Vec<u8> {
    let mut header = 0x8000_0000u32.to_be_bytes().to_vec();
    header.extend_from_slice(b"abcd");
    insert_after_ihdr(&header)
}

pub fn chunk_runs_past_end() -> Vec<u8> {
    let mut header = 100_000u32.to_be_bytes().to_vec();
    header.extend_from_slice(b"abcd");
    insert_after_ihdr(&header)
}

pub fn animated_control() -> Vec<u8> {
    insert_after_ihdr(&chunk("acTL", &[0; 8]))
}

pub fn missing_iend() -> Vec<u8> {
    let bytes = valid();
    bytes[..bytes.len() - 12].to_vec()
}

pub fn trailing_bytes_after_iend() -> Vec<u8> {
    let mut bytes = valid();
    bytes.push(0);
    bytes
}

pub fn inflate_bomb() -> Vec<u8> {
    build(EXTENT, EXTENT, Some(vec![0; 4_000_000]), None)
}

/// A 64x64 PNG with these header fields and a zero-filled pixel stream.
pub fn with_header(
    bit_depth: u8,
    color_type: u8,
    raw_length: usize,
    compression: u8,
    filter: u8,
    interlace: u8,
) -> Vec<u8> {
    build(
        EXTENT,
        EXTENT,
        Some(vec![0; raw_length]),
        Some([bit_depth, color_type, compression, filter, interlace]),
    )
}
