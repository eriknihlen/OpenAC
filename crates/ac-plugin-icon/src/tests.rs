//! Ported from OpenAC `tests/AcDream.Core.Tests/Plugins/PluginIconFileTests.cs`
//! (fixtures from `tests/Fixtures/plugin-icons/PngTestData.cs`) and
//! `LauncherPluginIconTests`, with the decoder's own cases and malformed
//! files that must fail without a panic.

use std::path::PathBuf;

use super::*;

fn crc32(bytes: &[u8]) -> u32 {
    let mut crc = 0xFFFF_FFFFu32;
    for &byte in bytes {
        crc ^= u32::from(byte);
        for _ in 0..8 {
            crc = if crc & 1 != 0 {
                (crc >> 1) ^ 0xEDB8_8320
            } else {
                crc >> 1
            };
        }
    }
    !crc
}

fn chunk(kind: &[u8; 4], body: &[u8]) -> Vec<u8> {
    let mut out = (body.len() as u32).to_be_bytes().to_vec();
    out.extend_from_slice(kind);
    out.extend_from_slice(body);
    out.extend_from_slice(&crc32(&out[4..]).to_be_bytes());
    out
}

fn png_with(width: u32, height: u32, header: [u8; 5], raw: &[u8]) -> Vec<u8> {
    let mut ihdr = width.to_be_bytes().to_vec();
    ihdr.extend_from_slice(&height.to_be_bytes());
    ihdr.extend_from_slice(&header);
    let mut out = PNG_SIGNATURE.to_vec();
    out.extend(chunk(b"IHDR", &ihdr));
    out.extend(chunk(
        b"IDAT",
        &miniz_oxide::deflate::compress_to_vec_zlib(raw, 6),
    ));
    out.extend(chunk(b"IEND", &[]));
    out
}

fn png(width: u32, height: u32, color_type: u8, depth: u8, raw: &[u8]) -> Vec<u8> {
    png_with(width, height, [depth, color_type, 0, 0, 0], raw)
}

/// PngTestData.BuildRawScanlines: RGBA rows, filter None, bytes `(x + y) % 256`.
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

/// PngTestData.Valid.
fn valid() -> Vec<u8> {
    png(64, 64, 6, 8, &raw_scanlines(64, 64))
}

/// PngTestData.CorruptPixelData: the first row's filter byte is 250.
fn corrupt_pixel_data() -> Vec<u8> {
    let mut raw = raw_scanlines(64, 64);
    raw[0] = 250;
    png(64, 64, 6, 8, &raw)
}

/// PngTestData.InflateBomb: four million zero bytes behind a 64 x 64 header.
fn inflate_bomb() -> Vec<u8> {
    png(64, 64, 6, 8, &vec![0; 4_000_000])
}

/// PngTestData.AnimatedControl: an `acTL` chunk after `IHDR`.
fn animated() -> Vec<u8> {
    let valid = valid();
    let ihdr_end = 8 + 8 + 13 + 4;
    let mut out = valid[..ihdr_end].to_vec();
    out.extend(chunk(b"acTL", &[0; 8]));
    out.extend_from_slice(&valid[ihdr_end..]);
    out
}

struct TemporaryDirectory(PathBuf);

impl TemporaryDirectory {
    fn new(tag: &str) -> Self {
        let path =
            std::env::temp_dir().join(format!("openac-plugin-icon-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&path);
        std::fs::create_dir_all(&path).unwrap();
        Self(path)
    }

    fn with_icon(tag: &str, bytes: &[u8]) -> Self {
        let directory = Self::new(tag);
        std::fs::write(directory.0.join(ICON_FILE_NAME), bytes).unwrap();
        directory
    }
}

impl Drop for TemporaryDirectory {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

#[test]
fn loads_a_valid_icon() {
    // LoadsAValidIcon
    let directory = TemporaryDirectory::with_icon("valid", &valid());
    let icon = load_icon(&directory.0).unwrap();
    assert_eq!((icon.width, icon.height), (64, 64));
    assert_eq!(icon.rgba.len(), 64 * 64 * 4);
    // The second pixel of the first row: bytes 4..8 of the row, (x + y) % 256.
    assert_eq!(&icon.rgba[4..8], &[4, 5, 6, 7]);
}

#[test]
fn fails_with_no_icon_file() {
    // FailsWithNoIconFile
    let directory = TemporaryDirectory::new("none");
    assert!(load_icon(&directory.0).is_none());
}

#[test]
fn fails_when_over_maximum_bytes() {
    // FailsWhenOverMaximumBytes
    let mut bytes = valid();
    bytes.resize((ICON_MAXIMUM_BYTES + 1).max(bytes.len() + 1), 0);
    let directory = TemporaryDirectory::with_icon("large", &bytes);
    assert!(load_icon(&directory.0).is_none());
}

#[test]
fn fails_with_the_wrong_dimensions() {
    // FailsWithTheWrongDimensions
    let directory =
        TemporaryDirectory::with_icon("small", &png(32, 32, 6, 8, &raw_scanlines(32, 32)));
    assert!(load_icon(&directory.0).is_none());
}

#[test]
fn fails_when_animated() {
    // FailsWhenAnimated
    let directory = TemporaryDirectory::with_icon("animated", &animated());
    assert!(load_icon(&directory.0).is_none());
}

#[test]
fn fails_with_bytes_after_iend() {
    // FailsWithBytesAfterIend
    let mut bytes = valid();
    bytes.push(0);
    let directory = TemporaryDirectory::with_icon("trailing", &bytes);
    assert!(load_icon(&directory.0).is_none());
}

#[test]
fn fails_with_corrupt_pixel_data_that_passes_the_header_rules() {
    // FailsWithCorruptPixelDataThatPassesTheHeaderRules
    let bytes = corrupt_pixel_data();
    validate_icon(&bytes).unwrap();
    let directory = TemporaryDirectory::with_icon("corrupt", &bytes);
    assert!(load_icon(&directory.0).is_none());
}

#[test]
fn refuses_an_inflate_bomb() {
    // RefusesAnInflateBombWithoutAllocatingItsExpandedSize: the inflate
    // stops one byte past the declared size, so it is refused rather than
    // expanded.
    let bytes = inflate_bomb();
    assert_eq!(validate_icon(&bytes), Err(IconError::DataTooLarge));
    assert!(decode_png(&bytes).is_none());
    let directory = TemporaryDirectory::with_icon("bomb", &bytes);
    assert!(load_icon(&directory.0).is_none());
}

#[test]
fn interlaced_rgba_size_matches_the_seven_passes() {
    // OpenAC LauncherPluginIconTests: 264+264+520+1040+2064+4128+8224 = 16504.
    assert_eq!(expected_raw_size(64, 64, 6, 8, 1), 16_504);
    assert_eq!(expected_raw_size(64, 64, 6, 8, 0), 16_448);
}

#[test]
fn decodes_rgba_with_every_filter() {
    // Two rows of two pixels: rows filtered None and Sub, Up, Average, Paeth all give the
    // same image back.
    for filter in 0u8..=4 {
        let rows: [[u8; 8]; 2] = [[10, 20, 30, 255, 40, 50, 60, 128], [1, 2, 3, 4, 5, 6, 7, 8]];
        let mut raw = Vec::new();
        let mut previous = [0u8; 8];
        for row in rows {
            raw.push(filter);
            let mut encoded = [0u8; 8];
            for index in 0..8 {
                let left = if index >= 4 { row[index - 4] } else { 0 };
                let up = previous[index];
                let up_left = if index >= 4 { previous[index - 4] } else { 0 };
                let predictor = match filter {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => ((u16::from(left) + u16::from(up)) / 2) as u8,
                    _ => paeth(left, up, up_left),
                };
                encoded[index] = row[index].wrapping_sub(predictor);
            }
            raw.extend_from_slice(&encoded);
            previous = row;
        }
        let icon = decode_png(&png(2, 2, 6, 8, &raw)).unwrap();
        assert_eq!(
            *icon.rgba,
            vec![10, 20, 30, 255, 40, 50, 60, 128, 1, 2, 3, 4, 5, 6, 7, 8],
            "filter {filter}"
        );
    }
}

#[test]
fn decodes_low_bit_gray_a_palette_and_a_validated_icon() {
    // One row of four 2-bit gray pixels: 0, 1, 2, 3 -> 0, 85, 170, 255.
    let icon = decode_png(&png(4, 1, 0, 2, &[0, 0b0001_1011])).unwrap();
    assert_eq!(
        *icon.rgba,
        vec![
            0, 0, 0, 255, 85, 85, 85, 255, 170, 170, 170, 255, 255, 255, 255, 255
        ]
    );
    // Two 8-bit palette pixels, the second entry half transparent.
    let mut bytes = PNG_SIGNATURE.to_vec();
    let mut ihdr = 2u32.to_be_bytes().to_vec();
    ihdr.extend_from_slice(&1u32.to_be_bytes());
    ihdr.extend_from_slice(&[8, 3, 0, 0, 0]);
    bytes.extend(chunk(b"IHDR", &ihdr));
    bytes.extend(chunk(b"PLTE", &[1, 2, 3, 4, 5, 6]));
    bytes.extend(chunk(b"tRNS", &[255, 128]));
    bytes.extend(chunk(
        b"IDAT",
        &miniz_oxide::deflate::compress_to_vec_zlib(&[0, 0, 1], 6),
    ));
    bytes.extend(chunk(b"IEND", &[]));
    assert_eq!(
        *decode_png(&bytes).unwrap().rgba,
        vec![1, 2, 3, 255, 4, 5, 6, 128]
    );
    let mut raw = Vec::new();
    for _ in 0..64 {
        raw.push(0);
        raw.extend(std::iter::repeat_n([200u8, 100, 50, 255], 64).flatten());
    }
    let icon = decode_icon(&png(64, 64, 6, 8, &raw)).unwrap();
    assert_eq!((icon.width, icon.height), (64, 64));
    assert_eq!(&icon.rgba[..4], &[200, 100, 50, 255]);
    assert!(
        decode_icon(&png(2, 2, 6, 8, &[0; 18])).is_none(),
        "not 64 x 64"
    );
}

#[test]
fn malformed_files_are_refused_without_a_panic() {
    // Headers outside the specification (bit depth 3, colour type 5, bit
    // depth 0 or 32), a palette index past the palette, short data, no
    // header and truncated chunks: none decodes, none panics.
    for header in [
        [3, 0, 0, 0, 0],
        [8, 5, 0, 0, 0],
        [0, 6, 0, 0, 0],
        [32, 6, 0, 0, 0],
        [16, 3, 0, 0, 0],
        [8, 6, 1, 0, 0],
        [8, 6, 0, 1, 0],
        [8, 6, 0, 0, 2],
    ] {
        assert!(
            decode_png(&png_with(4, 4, header, &[0; 64])).is_none(),
            "{header:?}"
        );
    }
    // Palette pixels with no palette.
    assert!(decode_png(&png(2, 1, 3, 8, &[0, 0, 1])).is_none());
    // One row short.
    assert!(decode_png(&png(2, 2, 6, 8, &[0; 9])).is_none());
    // Not zlib.
    let mut bytes = PNG_SIGNATURE.to_vec();
    let mut ihdr = 1u32.to_be_bytes().to_vec();
    ihdr.extend_from_slice(&1u32.to_be_bytes());
    ihdr.extend_from_slice(&[8, 6, 0, 0, 0]);
    bytes.extend(chunk(b"IHDR", &ihdr));
    bytes.extend(chunk(b"IDAT", &[1, 2, 3, 4]));
    bytes.extend(chunk(b"IEND", &[]));
    assert!(decode_png(&bytes).is_none());
    assert!(decode_png(&[]).is_none());
    assert!(decode_png(&PNG_SIGNATURE).is_none());
    // Every prefix of a valid file, and every single-byte corruption of its
    // header and first data bytes.
    let valid = valid();
    for length in 0..valid.len() {
        let _ = decode_png(&valid[..length]);
        let _ = validate_icon(&valid[..length]);
    }
    for index in 0..valid.len().min(96) {
        for value in [0u8, 1, 3, 0x7F, 0x80, 0xFF] {
            let mut bytes = valid.clone();
            bytes[index] = value;
            let _ = decode_png(&bytes);
            let _ = decode_icon(&bytes);
        }
    }
}

#[test]
fn the_rules_name_what_is_wrong() {
    // LauncherPluginIconTests: the launcher's messages.
    assert_eq!(validate_icon(&valid()), Ok(()));
    assert_eq!(
        validate_icon(&png(32, 32, 6, 8, &raw_scanlines(32, 32))),
        Err(IconError::WrongSize)
    );
    assert_eq!(validate_icon(&animated()), Err(IconError::Animated));
    assert_eq!(
        IconError::WrongSize.to_string(),
        "The plugin icon must be exactly 64x64 pixels."
    );
    assert_eq!(
        IconError::TooLarge.to_string(),
        "The plugin icon is larger than 65536 bytes."
    );
}
