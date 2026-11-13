//! The plugin image formats (OpenAC `IPluginImages.FromStream`: "PNG,
//! JPEG, BMP, TGA or GIF", and WebP). Files are built here byte by byte from
//! the formats' specifications, each with the pixels it must give; the WebP
//! files are made by libwebp's tools from images described with their tests.

use super::*;

const RED: [u8; 4] = [255, 0, 0, 255];
const GREEN: [u8; 4] = [0, 255, 0, 255];
const BLUE: [u8; 4] = [0, 0, 255, 255];
const WHITE: [u8; 4] = [255, 255, 255, 255];
const CLEAR: [u8; 4] = [0, 0, 0, 0];

fn pixel(image: &Decoded, x: usize, y: usize) -> [u8; 4] {
    let at = (y * image.width as usize + x) * 4;
    image.rgba[at..at + 4].try_into().unwrap()
}

// ── BMP ─────────────────────────────────────────────────────────────────

/// A bitmap with an info header of `header_size` (40 or more), `bits` per
/// pixel, `compression`, `extra` after the header (masks, palette), and
/// `rows` as stored (bottom first unless `height` is negative).
fn bmp(
    width: i32,
    height: i32,
    bits: u16,
    compression: u32,
    header_size: u32,
    extra: &[u8],
    rows: &[u8],
) -> Vec<u8> {
    let offset = 14 + header_size + extra.len() as u32;
    let mut v = b"BM".to_vec();
    v.extend_from_slice(&(offset + rows.len() as u32).to_le_bytes());
    v.extend_from_slice(&[0; 4]);
    v.extend_from_slice(&offset.to_le_bytes());
    v.extend_from_slice(&header_size.to_le_bytes());
    v.extend_from_slice(&width.to_le_bytes());
    v.extend_from_slice(&height.to_le_bytes());
    v.extend_from_slice(&1u16.to_le_bytes());
    v.extend_from_slice(&bits.to_le_bytes());
    v.extend_from_slice(&compression.to_le_bytes());
    v.extend_from_slice(&[0; 20]);
    v.resize(14 + header_size as usize, 0);
    v.extend_from_slice(extra);
    v.extend_from_slice(rows);
    v
}

#[test]
fn a_24_bit_bitmap_reads_bottom_up_bgr_rows_with_their_padding() {
    // 2 x 2: bottom row blue, green; top row red, white. Rows pad to 8.
    let rows = [
        255, 0, 0, 0, 255, 0, 0, 0, // bottom: blue, green, padding
        0, 0, 255, 255, 255, 255, 0, 0, // top: red, white, padding
    ];
    let file = bmp(2, 2, 24, 0, 40, &[], &rows);
    assert_eq!(detect(&file), Some(ImageFormat::Bmp));
    assert_eq!(dimensions(&file), Some((2, 2)));
    let image = decode(&file).unwrap();
    assert_eq!(
        [
            pixel(&image, 0, 0),
            pixel(&image, 1, 0),
            pixel(&image, 0, 1),
            pixel(&image, 1, 1)
        ],
        [RED, WHITE, BLUE, GREEN]
    );
}

#[test]
fn a_negative_height_is_a_top_down_bitmap() {
    let rows = [0, 0, 255, 0, 255, 0, 0, 0];
    let image = decode(&bmp(1, -2, 24, 0, 40, &[], &rows)).unwrap();
    assert_eq!((image.width, image.height), (1, 2));
    assert_eq!([pixel(&image, 0, 0), pixel(&image, 0, 1)], [RED, BLUE]);
}

#[test]
fn palette_depths_index_their_bgr_palette() {
    let palette = [0, 0, 255, 0, 255, 0, 0, 0]; // red, blue
    // 1 bit: pixels 0,1,1 from the high bit.
    let one = decode(&bmp(3, 1, 1, 0, 40, &palette, &[0b0110_0000, 0, 0, 0])).unwrap();
    assert_eq!(
        [pixel(&one, 0, 0), pixel(&one, 1, 0), pixel(&one, 2, 0)],
        [RED, BLUE, BLUE]
    );
    // 4 bits: pixels 1, 0.
    let four = decode(&bmp(2, 1, 4, 0, 40, &palette, &[0x10, 0, 0, 0])).unwrap();
    assert_eq!([pixel(&four, 0, 0), pixel(&four, 1, 0)], [BLUE, RED]);
    // 8 bits.
    let eight = decode(&bmp(2, 1, 8, 0, 40, &palette, &[1, 0, 0, 0])).unwrap();
    assert_eq!([pixel(&eight, 0, 0), pixel(&eight, 1, 0)], [BLUE, RED]);
}

#[test]
fn sixteen_bits_without_masks_are_5_5_5_with_their_bits_repeated() {
    // 0x7C00 red, 0x0001 the smallest blue.
    let rows = [0x00, 0x7C, 0x01, 0x00];
    let image = decode(&bmp(2, 1, 16, 0, 40, &[], &rows)).unwrap();
    assert_eq!(pixel(&image, 0, 0), RED);
    assert_eq!(pixel(&image, 1, 0), [0, 0, 8, 255]);
}

#[test]
fn thirty_two_bits_without_masks_carry_alpha_unless_all_of_it_is_zero() {
    let with_alpha = decode(&bmp(1, 1, 32, 0, 40, &[], &[255, 0, 0, 128])).unwrap();
    assert_eq!(pixel(&with_alpha, 0, 0), [0, 0, 255, 128]);
    let zero = decode(&bmp(2, 1, 32, 0, 40, &[], &[0, 0, 255, 0, 0, 255, 0, 0])).unwrap();
    assert_eq!([pixel(&zero, 0, 0), pixel(&zero, 1, 0)], [RED, GREEN]);
}

#[test]
fn bitfields_take_the_files_masks() {
    // 5-6-5 in an info header: the masks follow it.
    let masks = [
        0x00, 0xF8, 0, 0, //
        0xE0, 0x07, 0, 0, //
        0x1F, 0x00, 0, 0,
    ];
    let rows = [0xE0, 0x07, 0x1F, 0x00]; // green, blue
    let image = decode(&bmp(2, 1, 16, 3, 40, &masks, &rows)).unwrap();
    assert_eq!([pixel(&image, 0, 0), pixel(&image, 1, 0)], [GREEN, BLUE]);
    // A V5 header's alpha mask is read.
    let mut header_masks = Vec::new();
    for mask in [0x00FF_0000u32, 0x0000_FF00, 0x0000_00FF, 0xFF00_0000] {
        header_masks.extend_from_slice(&mask.to_le_bytes());
    }
    let mut file = bmp(1, 1, 32, 3, 124, &[], &[0, 0, 255, 64]);
    file[54..70].copy_from_slice(&header_masks);
    assert_eq!(pixel(&decode(&file).unwrap(), 0, 0), [255, 0, 0, 64]);
}

#[test]
fn malformed_bitmaps_are_errors() {
    // Run-length data is not read.
    let rle = bmp(1, 1, 8, 1, 40, &[0; 4], &[0, 0, 0, 0]);
    assert!(matches!(decode(&rle), Err(CodecError::Unsupported(..))));
    // Rows short of the height.
    let short = bmp(4, 4, 24, 0, 40, &[], &[0; 16]);
    assert_eq!(decode(&short), Err(CodecError::Truncated("BMP")));
    // A palette depth with no room for a palette.
    let no_palette = bmp(1, 1, 8, 0, 40, &[], &[0; 4]);
    assert!(matches!(decode(&no_palette), Err(CodecError::Corrupt(..))));
    // Cut anywhere in the header.
    let whole = bmp(1, 1, 24, 0, 40, &[], &[0; 4]);
    for end in 0..54 {
        assert!(decode(&whole[..end]).is_err(), "cut at {end}");
    }
}

// ── TGA ─────────────────────────────────────────────────────────────────

fn tga(
    image_type: u8,
    bits: u8,
    descriptor: u8,
    map: Option<(u16, u8, &[u8])>,
    data: &[u8],
) -> Vec<u8> {
    let (map_type, map_first, map_length, map_bits, map_data) = match map {
        Some((first, entry_bits, entries)) => {
            let size = usize::from(entry_bits).div_ceil(8);
            (1, first, (entries.len() / size) as u16, entry_bits, entries)
        }
        None => (0, 0, 0, 0, &[][..]),
    };
    let mut v = vec![2, map_type, image_type];
    v.extend_from_slice(&map_first.to_le_bytes());
    v.extend_from_slice(&map_length.to_le_bytes());
    v.push(map_bits);
    v.extend_from_slice(&[0; 4]);
    v.extend_from_slice(&2u16.to_le_bytes());
    v.extend_from_slice(&2u16.to_le_bytes());
    v.push(bits);
    v.push(descriptor);
    v.extend_from_slice(b"id");
    v.extend_from_slice(map_data);
    v.extend_from_slice(data);
    v
}

#[test]
fn a_true_colour_tga_runs_up_from_the_bottom_unless_its_origin_is_the_top() {
    // BGRA: bottom row red, green; top row blue, half-clear white.
    let data = [
        0, 0, 255, 255, 0, 255, 0, 255, //
        255, 0, 0, 255, 255, 255, 255, 128,
    ];
    let file = tga(2, 32, 8, None, &data);
    assert_eq!(detect(&file), Some(ImageFormat::Tga));
    assert_eq!(dimensions(&file), Some((2, 2)));
    let up = decode(&file).unwrap();
    assert_eq!(
        [
            pixel(&up, 0, 0),
            pixel(&up, 1, 0),
            pixel(&up, 0, 1),
            pixel(&up, 1, 1)
        ],
        [BLUE, [255, 255, 255, 128], RED, GREEN]
    );
    let down = decode(&tga(2, 32, 8 | 0x20, None, &data)).unwrap();
    assert_eq!(
        [pixel(&down, 0, 0), pixel(&down, 1, 1)],
        [RED, [255, 255, 255, 128]]
    );
}

#[test]
fn run_length_packets_repeat_or_copy_and_may_cross_rows() {
    // 24-bit: a run of three red, then one raw blue.
    let data = [0x82, 0, 0, 255, 0x00, 255, 0, 0];
    let image = decode(&tga(10, 24, 0x20, None, &data)).unwrap();
    assert_eq!(
        [
            pixel(&image, 0, 0),
            pixel(&image, 1, 0),
            pixel(&image, 0, 1),
            pixel(&image, 1, 1)
        ],
        [RED, RED, RED, BLUE]
    );
}

#[test]
fn colour_maps_grey_and_5_5_5_pixels() {
    // A map of two 24-bit entries starting at index 4.
    let map = [0, 255, 0, 255, 0, 0]; // green, blue
    let image = decode(&tga(1, 8, 0x20, Some((4, 24, &map)), &[4, 5, 9, 4])).unwrap();
    assert_eq!(
        [
            pixel(&image, 0, 0),
            pixel(&image, 1, 0),
            pixel(&image, 0, 1)
        ],
        [GREEN, BLUE, GREEN],
        "an index outside the map takes its first entry"
    );
    let grey = decode(&tga(3, 8, 0x20, None, &[0, 128, 255, 7])).unwrap();
    assert_eq!(pixel(&grey, 1, 0), [128, 128, 128, 255]);
    let grey_alpha = decode(&tga(3, 16, 0x20, None, &[9, 0, 0, 0, 0, 0, 0, 0])).unwrap();
    assert_eq!(pixel(&grey_alpha, 0, 0), [9, 9, 9, 0]);
    // 5-5-5 with the attribute bit set: still opaque; 1 of 31 is 8.
    let rgb = decode(&tga(
        2,
        16,
        0x20,
        None,
        &[0x00, 0xFC, 0x01, 0x00, 0, 0, 0, 0],
    ))
    .unwrap();
    assert_eq!(
        [pixel(&rgb, 0, 0), pixel(&rgb, 1, 0)],
        [RED, [0, 0, 8, 255]]
    );
}

#[test]
fn a_tga_cut_short_or_with_a_senseless_header_is_an_error() {
    let file = tga(2, 24, 0, None, &[0; 12]);
    for end in 0..file.len() {
        assert!(decode(&file[..end]).is_err(), "cut at {end}");
    }
    assert_eq!(detect(&tga(7, 24, 0, None, &[0; 12])), None);
    assert_eq!(detect(&tga(2, 12, 0, None, &[0; 12])), None);
    assert_eq!(decode(&[1, 2, 3, 4]), Err(CodecError::UnknownFormat));
}

// ── GIF ─────────────────────────────────────────────────────────────────

/// The GIF variant of LZW, written as a reference encoder: codes grow to
/// 12 bits, a clear code restarts a full table.
fn lzw_encode(minimum: u8, indices: &[u8]) -> Vec<u8> {
    use std::collections::HashMap;
    let clear = 1u32 << minimum;
    let mut out = Vec::new();
    let (mut bits, mut held) = (0u64, 0u32);
    let mut put = |code: u32, size: u32, out: &mut Vec<u8>| {
        bits |= u64::from(code) << held;
        held += size;
        while held >= 8 {
            out.push(bits as u8);
            bits >>= 8;
            held -= 8;
        }
    };
    let mut table: HashMap<(u32, u8), u32> = HashMap::new();
    let mut size = u32::from(minimum) + 1;
    let mut next = clear + 2;
    put(clear, size, &mut out);
    let mut current = u32::from(indices[0]);
    for &index in &indices[1..] {
        if let Some(&code) = table.get(&(current, index)) {
            current = code;
            continue;
        }
        put(current, size, &mut out);
        if next < 4096 {
            table.insert((current, index), next);
            next += 1;
            if next > (1 << size) && size < 12 {
                size += 1;
            }
        } else {
            put(clear, size, &mut out);
            table.clear();
            size = u32::from(minimum) + 1;
            next = clear + 2;
        }
        current = u32::from(index);
    }
    put(current, size, &mut out);
    put(clear + 1, size, &mut out);
    put(0, 7, &mut out);
    out
}

fn sub_blocks(data: &[u8]) -> Vec<u8> {
    let mut v = Vec::new();
    for chunk in data.chunks(255) {
        v.push(chunk.len() as u8);
        v.extend_from_slice(chunk);
    }
    v.push(0);
    v
}

/// A GIF89a of `width` x `height` whose one frame covers `frame` (left,
/// top, width, height) with `indices`, over a four-colour global table
/// (red, green, blue, white).
fn gif(
    width: u16,
    height: u16,
    frame: (u16, u16, u16, u16),
    transparent: Option<u8>,
    interlaced: bool,
    indices: &[u8],
) -> Vec<u8> {
    let mut v = b"GIF89a".to_vec();
    v.extend_from_slice(&width.to_le_bytes());
    v.extend_from_slice(&height.to_le_bytes());
    v.extend_from_slice(&[0x81, 0, 0]);
    v.extend_from_slice(&[255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255]);
    // A comment extension, skipped.
    v.extend_from_slice(&[0x21, 0xFE]);
    v.extend_from_slice(&sub_blocks(b"made by hand"));
    if let Some(index) = transparent {
        v.extend_from_slice(&[0x21, 0xF9, 4, 1, 0, 0, index, 0]);
    }
    v.push(0x2C);
    for value in [frame.0, frame.1, frame.2, frame.3] {
        v.extend_from_slice(&value.to_le_bytes());
    }
    v.push(if interlaced { 0x40 } else { 0 });
    v.push(2);
    v.extend_from_slice(&sub_blocks(&lzw_encode(2, indices)));
    v.push(0x3B);
    v
}

#[test]
fn the_classic_one_pixel_transparent_gif_is_one_clear_pixel() {
    // The widely used 43-byte spacer: index 0 transparent.
    let file = [
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0xFF, 0xFF,
        0xFF, 0x00, 0x00, 0x00, 0x21, 0xF9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x00,
        0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x02, 0x44, 0x01, 0x00, 0x3B,
    ];
    assert_eq!(detect(&file), Some(ImageFormat::Gif));
    let image = decode(&file).unwrap();
    assert_eq!((image.width, image.height), (1, 1));
    assert_eq!(pixel(&image, 0, 0), CLEAR);
}

#[test]
fn a_gif_frame_lands_on_a_clear_canvas_with_its_transparent_index_left_clear() {
    let file = gif(3, 2, (1, 0, 2, 2), Some(3), false, &[0, 1, 2, 3]);
    assert_eq!(dimensions(&file), Some((3, 2)));
    let image = decode(&file).unwrap();
    assert_eq!(
        [
            pixel(&image, 0, 0),
            pixel(&image, 1, 0),
            pixel(&image, 2, 0),
            pixel(&image, 1, 1),
            pixel(&image, 2, 1)
        ],
        [CLEAR, RED, GREEN, BLUE, CLEAR]
    );
}

#[test]
fn interlaced_rows_come_in_four_passes() {
    // Eight one-pixel rows; stored order 0, 4, 2, 6, 1, 3, 5, 7.
    let by_row = [0u8, 1, 2, 3, 0, 1, 2, 3];
    let stored: Vec<u8> = [0, 4, 2, 6, 1, 3, 5, 7]
        .iter()
        .map(|&r| by_row[r])
        .collect();
    let image = decode(&gif(1, 8, (0, 0, 1, 8), None, true, &stored)).unwrap();
    let colors = [RED, GREEN, BLUE, WHITE];
    for (row, &index) in by_row.iter().enumerate() {
        assert_eq!(
            pixel(&image, 0, row),
            colors[usize::from(index)],
            "row {row}"
        );
    }
}

#[test]
fn long_lzw_data_grows_its_codes_to_twelve_bits_and_clears() {
    // 200 x 100 of a pattern that fills the code table several times.
    let (width, height) = (200u16, 100u16);
    let indices: Vec<u8> = (0..u32::from(width) * u32::from(height))
        .map(|i| ((i * 7 + i / 13 + (i / 200) * 3) % 4) as u8)
        .collect();
    let image = decode(&gif(
        width,
        height,
        (0, 0, width, height),
        None,
        false,
        &indices,
    ))
    .unwrap();
    let colors = [RED, GREEN, BLUE, WHITE];
    for (i, &index) in indices.iter().enumerate() {
        let (x, y) = (i % usize::from(width), i / usize::from(width));
        assert_eq!(pixel(&image, x, y), colors[usize::from(index)], "pixel {i}");
    }
}

#[test]
fn malformed_gifs_are_errors_never_panics() {
    // A frame past the canvas.
    let past = gif(2, 2, (1, 1, 2, 2), None, false, &[0, 1, 2, 3]);
    assert!(matches!(decode(&past), Err(CodecError::Corrupt(..))));
    // No image before the trailer.
    let mut empty = gif(1, 1, (0, 0, 1, 1), None, false, &[0]);
    let descriptor = empty.iter().rposition(|&b| b == 0x2C).unwrap();
    empty.truncate(descriptor);
    empty.push(0x3B);
    assert!(decode(&empty).is_err());
    // Cut anywhere: an error or, once the pixels are under way, a frame
    // left partly clear.
    let whole = gif(4, 4, (0, 0, 4, 4), None, false, &[1; 16]);
    for end in 0..whole.len() {
        let _ = decode(&whole[..end]);
    }
    // Every byte flipped in turn.
    for at in 0..whole.len() {
        let mut bent = whole.clone();
        bent[at] ^= 0xA5;
        let _ = decode(&bent);
    }
}

// ── PNG and JPEG ────────────────────────────────────────────────────────

#[test]
fn png_is_the_icon_reader_with_its_header_size_read_alone() {
    let rgba: Vec<u8> = [RED, GREEN, BLUE, WHITE].concat();
    let file = ac_bits::png::encode_rgba(2, 2, &rgba);
    assert_eq!(detect(&file), Some(ImageFormat::Png));
    assert_eq!(dimensions(&file), Some((2, 2)));
    let image = decode(&file).unwrap();
    assert_eq!(image.rgba, rgba);
    // The size is read even when the pixel data is missing.
    assert_eq!(dimensions(&file[..33]), Some((2, 2)));
    assert!(decode(&file[..33]).is_err());
}

/// One 8 x 8 grey block whose DC difference is +7 at quantiser 1 (the
/// JPEG decoder's own test image, `ac_dat::texture::jpeg`).
fn grey_jpeg() -> Vec<u8> {
    let mut v = vec![0xFF, 0xD8];
    v.extend_from_slice(&[0xFF, 0xDB, 0x00, 0x43, 0x00]);
    v.extend_from_slice(&[1; 64]);
    v.extend_from_slice(&[0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x08, 0x00, 0x08]);
    v.extend_from_slice(&[0x01, 0x01, 0x11, 0x00]);
    v.extend_from_slice(&[0xFF, 0xC4, 0x00, 0x14, 0x00, 1]);
    v.extend_from_slice(&[0; 15]);
    v.push(3);
    v.extend_from_slice(&[0xFF, 0xC4, 0x00, 0x14, 0x10, 1]);
    v.extend_from_slice(&[0; 15]);
    v.push(0);
    v.extend_from_slice(&[0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]);
    v.push(0b0111_0111);
    v.extend_from_slice(&[0xFF, 0xD9]);
    v
}

#[test]
fn jpeg_is_the_dat_texture_decoder_with_its_frame_size_read_alone() {
    let file = grey_jpeg();
    assert_eq!(detect(&file), Some(ImageFormat::Jpeg));
    assert_eq!(dimensions(&file), Some((8, 8)));
    let image = decode(&file).unwrap();
    let reference = ac_dat::texture::jpeg::decode(&file).unwrap();
    assert_eq!((image.width, image.height), (8, 8));
    assert_eq!(image.rgba, reference.rgba);
    assert!(matches!(decode(&file[..40]), Err(CodecError::Jpeg(_))));
}

// ── WebP ────────────────────────────────────────────────────────────────
//
// Made with libwebp 1.6's `cwebp` and `img2webp` from PNGs whose pixels the
// tests below spell out: `cwebp -lossless -exact` (lossless), `cwebp -q 90`
// (lossy, lossy_alpha) and `img2webp -lossy -q 90` (animated, two frames).

const WEBP_LOSSLESS: &[u8] = include_bytes!("../../tests/data/webp/lossless.webp");
const WEBP_LOSSY: &[u8] = include_bytes!("../../tests/data/webp/lossy.webp");
const WEBP_LOSSY_ALPHA: &[u8] = include_bytes!("../../tests/data/webp/lossy_alpha.webp");
const WEBP_ANIMATED: &[u8] = include_bytes!("../../tests/data/webp/animated.webp");

/// The lossless file's source: 7 x 5, no two pixels alike, alpha included.
fn gradient_pixel(x: u8, y: u8) -> [u8; 4] {
    [x * 36, y * 60, 255 - x * 20 - y * 10, 255 - (x + y * 7) * 7]
}

/// Whether `actual` is within `tolerance` of `expected` in every channel
/// (lossy compression moves colours a little).
fn near(actual: [u8; 4], expected: [u8; 4], tolerance: u8) -> bool {
    actual
        .iter()
        .zip(expected)
        .all(|(a, e)| a.abs_diff(e) <= tolerance)
}

#[test]
fn lossless_webp_gives_its_source_pixel_for_pixel() {
    assert_eq!(detect(WEBP_LOSSLESS), Some(ImageFormat::Webp));
    assert_eq!(&WEBP_LOSSLESS[12..16], b"VP8L");
    assert_eq!(dimensions(WEBP_LOSSLESS), Some((7, 5)));
    let image = decode(WEBP_LOSSLESS).unwrap();
    assert_eq!((image.width, image.height), (7, 5));
    let source: Vec<u8> = (0..5)
        .flat_map(|y| (0..7).flat_map(move |x| gradient_pixel(x, y)))
        .collect();
    assert_eq!(image.rgba, source);
}

#[test]
fn lossy_webp_is_opaque_and_close_to_its_source() {
    // 16 x 8: red left half, blue right half.
    assert_eq!(detect(WEBP_LOSSY), Some(ImageFormat::Webp));
    assert_eq!(&WEBP_LOSSY[12..16], b"VP8 ");
    assert_eq!(dimensions(WEBP_LOSSY), Some((16, 8)));
    let image = decode(WEBP_LOSSY).unwrap();
    assert_eq!((image.width, image.height), (16, 8));
    assert_eq!(image.rgba.len(), 16 * 8 * 4);
    assert!(image.rgba.chunks_exact(4).all(|p| p[3] == 255));
    for y in 0..8 {
        assert!(
            near(pixel(&image, 2, y), RED, 24),
            "{:?}",
            pixel(&image, 2, y)
        );
        assert!(
            near(pixel(&image, 13, y), BLUE, 24),
            "{:?}",
            pixel(&image, 13, y)
        );
    }
}

#[test]
fn lossy_webp_with_alpha_keeps_its_alpha_exactly() {
    // 16 x 8 green: the left half opaque, the right half clear. The alpha
    // plane is stored losslessly beside the lossy colour.
    assert_eq!(detect(WEBP_LOSSY_ALPHA), Some(ImageFormat::Webp));
    assert_eq!(&WEBP_LOSSY_ALPHA[12..16], b"VP8X");
    assert_eq!(dimensions(WEBP_LOSSY_ALPHA), Some((16, 8)));
    let image = decode(WEBP_LOSSY_ALPHA).unwrap();
    assert_eq!((image.width, image.height), (16, 8));
    for y in 0..8 {
        for x in 0..16 {
            let alpha = pixel(&image, x, y)[3];
            assert_eq!(alpha, if x < 8 { 255 } else { 0 }, "({x}, {y})");
        }
        assert!(
            near(pixel(&image, 2, y), GREEN, 24),
            "{:?}",
            pixel(&image, 2, y)
        );
    }
}

#[test]
fn an_animated_webp_gives_its_first_frame() {
    // 8 x 8: a red frame, then a blue one.
    assert_eq!(detect(WEBP_ANIMATED), Some(ImageFormat::Webp));
    assert_eq!(dimensions(WEBP_ANIMATED), Some((8, 8)));
    let image = decode(WEBP_ANIMATED).unwrap();
    assert_eq!((image.width, image.height), (8, 8));
    for y in 0..8 {
        for x in 0..8 {
            assert!(
                near(pixel(&image, x, y), RED, 24),
                "{:?}",
                pixel(&image, x, y)
            );
        }
    }
}

#[test]
fn webp_header_sizes_agree_with_the_decoded_images() {
    for file in [WEBP_LOSSLESS, WEBP_LOSSY, WEBP_LOSSY_ALPHA, WEBP_ANIMATED] {
        let image = decode(file).unwrap();
        assert_eq!(dimensions(file), Some((image.width, image.height)));
        // The size is read from the header alone.
        assert_eq!(dimensions(&file[..30]), Some((image.width, image.height)));
    }
}

#[test]
fn a_webp_larger_than_the_budget_is_refused_from_its_header() {
    // An extended header for 8192 x 8192 (64 Mi pixels) and nothing else.
    let mut file = b"RIFF".to_vec();
    file.extend_from_slice(&22u32.to_le_bytes());
    file.extend_from_slice(b"WEBPVP8X");
    file.extend_from_slice(&10u32.to_le_bytes());
    file.extend_from_slice(&[0; 4]);
    file.extend_from_slice(&[0xFF, 0x1F, 0x00, 0xFF, 0x1F, 0x00]);
    assert_eq!(dimensions(&file), Some((8192, 8192)));
    assert_eq!(decode(&file), Err(CodecError::TooLarge));
}

#[test]
fn malformed_webps_are_errors_never_panics() {
    // A RIFF file of another form is not WebP.
    let mut wave = WEBP_LOSSY.to_vec();
    wave[8..12].copy_from_slice(b"WAVE");
    assert_eq!(detect(&wave), None);
    assert_eq!(decode(&wave), Err(CodecError::UnknownFormat));
    // A lossy frame without its start code.
    let mut bent = WEBP_LOSSY.to_vec();
    bent[23] = 0;
    assert_eq!(dimensions(&bent), None);
    assert!(decode(&bent).is_err());
    // Cut short, with the pixels missing.
    let half = &WEBP_LOSSLESS[..WEBP_LOSSLESS.len() / 2];
    assert!(matches!(
        decode(half),
        Err(CodecError::Truncated("WebP") | CodecError::Corrupt("WebP", _))
    ));
    // Cut anywhere: an error, or (a lossy frame missing only the end of its
    // last partition reads it as zeros) the image at its declared size.
    // Every byte flipped in turn: never a panic.
    for file in [WEBP_LOSSLESS, WEBP_LOSSY, WEBP_LOSSY_ALPHA, WEBP_ANIMATED] {
        let size = dimensions(file).unwrap();
        for end in 0..file.len() {
            if let Ok(image) = decode(&file[..end]) {
                assert_eq!((image.width, image.height), size, "{end} of {}", file.len());
                assert_eq!(image.rgba.len(), (size.0 * size.1 * 4) as usize);
            }
        }
        for at in 0..file.len() {
            let mut bent = file.to_vec();
            bent[at] ^= 0xA5;
            let _ = decode(&bent);
        }
    }
}

/// A chunk: its name, payload size, payload and the pad to an even length.
fn webp_chunk(name: &[u8; 4], payload: &[u8]) -> Vec<u8> {
    let mut v = name.to_vec();
    v.extend_from_slice(&(payload.len() as u32).to_le_bytes());
    v.extend_from_slice(payload);
    if payload.len() % 2 == 1 {
        v.push(0);
    }
    v
}

/// A WebP file of these chunks.
fn webp_file(chunks: &[Vec<u8>]) -> Vec<u8> {
    let body: Vec<u8> = chunks.concat();
    let mut v = b"RIFF".to_vec();
    v.extend_from_slice(&(4 + body.len() as u32).to_le_bytes());
    v.extend_from_slice(b"WEBP");
    v.extend_from_slice(&body);
    v
}

/// The payload of the first chunk named `name` in a fixture.
fn webp_payload<'a>(file: &'a [u8], name: &[u8; 4]) -> &'a [u8] {
    let mut at = 12;
    loop {
        let size = u32::from_le_bytes(file[at + 4..at + 8].try_into().unwrap()) as usize;
        if &file[at..at + 4] == name {
            return &file[at + 8..at + 8 + size];
        }
        at += 8 + size + (size & 1);
    }
}

fn le24(value: u32) -> [u8; 3] {
    let b = value.to_le_bytes();
    [b[0], b[1], b[2]]
}

/// An extended header: `flags`, then the canvas size.
fn vp8x(flags: u8, width: u32, height: u32) -> Vec<u8> {
    let mut payload = vec![flags, 0, 0, 0];
    payload.extend_from_slice(&le24(width - 1));
    payload.extend_from_slice(&le24(height - 1));
    webp_chunk(b"VP8X", &payload)
}

/// An animation of one frame at the canvas's corner, declared `width` x
/// `height`, holding `inner` chunks.
fn one_frame_animation(canvas: (u32, u32), width: u32, height: u32, inner: &[Vec<u8>]) -> Vec<u8> {
    // Alpha (0x10) and animation (0x02).
    let header = vp8x(0x12, canvas.0, canvas.1);
    let anim = webp_chunk(b"ANIM", &[0; 6]);
    let mut frame = Vec::new();
    frame.extend_from_slice(&le24(0));
    frame.extend_from_slice(&le24(0));
    frame.extend_from_slice(&le24(width - 1));
    frame.extend_from_slice(&le24(height - 1));
    frame.extend_from_slice(&le24(100));
    // Not blended onto the (clear) canvas, so its pixels come out as they
    // are; blending rounds a partly clear pixel's colour.
    frame.push(0x02);
    frame.extend_from_slice(&inner.concat());
    webp_file(&[header, anim, webp_chunk(b"ANMF", &frame)])
}

/// An uncompressed alpha chunk (a header byte of 0, then one byte a
/// pixel): opaque in the left half, clear in the right.
fn raw_alpha(width: u32, height: u32) -> Vec<u8> {
    let mut payload = vec![0];
    for _ in 0..height {
        payload.extend((0..width).map(|x| if x < width / 2 { 255 } else { 0 }));
    }
    webp_chunk(b"ALPH", &payload)
}

#[test]
fn an_animation_frame_with_alpha_decodes_at_its_declared_size_only() {
    // lossy_alpha's 16 x 8 colour, with its alpha, as an animation's frame.
    let colour = webp_chunk(b"VP8 ", webp_payload(WEBP_LOSSY_ALPHA, b"VP8 "));
    let file = one_frame_animation((16, 8), 16, 8, &[raw_alpha(16, 8), colour.clone()]);
    let image = decode(&file).unwrap();
    assert_eq!((image.width, image.height), (16, 8));
    assert_eq!(pixel(&image, 2, 3)[3], 255);
    assert_eq!(pixel(&image, 13, 3)[3], 0);
    // The frame and its alpha declared 8 x 8 while its colour is 16 x 8:
    // the decoder would write the larger frame into the smaller buffer.
    let file = one_frame_animation((16, 8), 8, 8, &[raw_alpha(8, 8), colour]);
    assert!(matches!(decode(&file), Err(CodecError::Corrupt("WebP", _))));
}

#[test]
fn a_bitstream_larger_than_its_canvas_is_refused_before_decoding() {
    let colour = webp_payload(WEBP_LOSSY, b"VP8 ");
    // A 2 x 2 canvas holding the 16 x 8 frame.
    let file = webp_file(&[vp8x(0, 2, 2), webp_chunk(b"VP8 ", colour)]);
    assert_eq!(dimensions(&file), Some((2, 2)));
    assert!(matches!(decode(&file), Err(CodecError::Corrupt("WebP", _))));
    // The same frame's header saying 16383 x 16383: refused as too large,
    // before the decoder allocates its planes.
    let mut huge = colour.to_vec();
    huge[6..10].copy_from_slice(&[0xFF, 0x3F, 0xFF, 0x3F]);
    let file = webp_file(&[vp8x(0, 2, 2), webp_chunk(b"VP8 ", &huge)]);
    assert_eq!(decode(&file), Err(CodecError::TooLarge));
    // And inside an animation's frame.
    let file = one_frame_animation((2, 2), 2, 2, &[webp_chunk(b"VP8 ", &huge)]);
    assert_eq!(decode(&file), Err(CodecError::TooLarge));
}

#[test]
fn webp_headers_are_read_inside_their_chunks_and_checked() {
    // A lossless header with a version other than 0.
    let mut versioned = WEBP_LOSSLESS.to_vec();
    versioned[24] |= 0x20;
    assert_eq!(dimensions(&versioned), None);
    assert!(matches!(
        decode(&versioned),
        Err(CodecError::Corrupt("WebP", _))
    ));
    // A lossy chunk declaring fewer bytes than its header needs.
    let colour = webp_payload(WEBP_LOSSY, b"VP8 ");
    let mut short = webp_file(&[webp_chunk(b"VP8 ", &colour[..6])]);
    short.extend_from_slice(&colour[6..]);
    assert_eq!(dimensions(&short), None);
    assert!(decode(&short).is_err());
    // A lossy frame 0 pixels wide.
    let mut empty = colour.to_vec();
    empty[6..8].copy_from_slice(&[0, 0]);
    let file = webp_file(&[webp_chunk(b"VP8 ", &empty)]);
    assert_eq!(dimensions(&file), None);
    assert!(matches!(decode(&file), Err(CodecError::Corrupt("WebP", _))));
    // A lossless image 16384 x 1, the most its header can say, which the
    // decoder would read as 0 wide.
    let bits: u32 = 0x3FFF;
    let mut header = vec![0x2F];
    header.extend_from_slice(&bits.to_le_bytes());
    let file = webp_file(&[webp_chunk(b"VP8L", &header)]);
    assert_eq!(dimensions(&file), Some((16384, 1)));
    assert_eq!(
        decode(&file),
        Err(CodecError::Unsupported(
            "WebP",
            "images 16384 or more pixels across"
        ))
    );
}

#[test]
fn a_webp_is_read_to_its_riff_end_with_its_chunks_as_the_decoder_reads_them() {
    // Bytes after the RIFF end are not read; a RIFF end past the file's is
    // a short file.
    let mut trailed = WEBP_LOSSLESS.to_vec();
    trailed.extend_from_slice(b"VP8X");
    assert_eq!(decode(&trailed), decode(WEBP_LOSSLESS));
    let mut long = WEBP_LOSSLESS.to_vec();
    long[4] += 2;
    assert_eq!(decode(&long), Err(CodecError::Truncated("WebP")));
    // A lossy frame whose first partition runs past its chunk.
    let mut colour = webp_payload(WEBP_LOSSY, b"VP8 ").to_vec();
    colour[0] |= 0xE0;
    colour[1] = 0xFF;
    colour[2] = 0xFF;
    let file = webp_file(&[webp_chunk(b"VP8 ", &colour)]);
    assert_eq!(dimensions(&file), Some((16, 8)));
    assert_eq!(decode(&file), Err(CodecError::Truncated("WebP")));
    // Alpha followed by lossless colour, which the decoder would read as
    // lossy.
    let lossless = webp_chunk(b"VP8L", webp_payload(WEBP_LOSSLESS, b"VP8L"));
    let file = one_frame_animation((7, 5), 7, 5, &[raw_alpha(7, 5), lossless.clone()]);
    assert!(matches!(decode(&file), Err(CodecError::Corrupt("WebP", _))));
    // Without alpha, lossless colour is a frame.
    let file = one_frame_animation((7, 5), 7, 5, &[lossless]);
    assert_eq!(
        decode(&file).unwrap().rgba,
        decode(WEBP_LOSSLESS).unwrap().rgba
    );
}

#[test]
fn a_still_webp_with_a_stray_animation_frame_allocates_for_its_canvas_only() {
    // A still extended image may carry an animation frame chunk; the
    // decoder then reads a lossless bitstream inside it in place of the
    // image's lossy one, at the canvas's size. One of another size (here
    // 16383 x 16383 in a 16 x 8 canvas) is refused, not allocated for.
    let colour = webp_chunk(b"VP8 ", webp_payload(WEBP_LOSSY, b"VP8 "));
    let bits: u32 = 0x3FFE | (0x3FFE << 14);
    let mut huge = vec![0x2F];
    huge.extend_from_slice(&bits.to_le_bytes());
    huge.extend_from_slice(&[0; 11]);
    let mut frame = vec![0; 16];
    frame.extend_from_slice(&webp_chunk(b"VP8L", &huge));
    let still = webp_file(&[vp8x(0, 16, 8), colour.clone()]);
    assert_eq!(
        decode(&still).unwrap().rgba,
        decode(WEBP_LOSSY).unwrap().rgba
    );
    let file = webp_file(&[vp8x(0, 16, 8), colour, webp_chunk(b"ANMF", &frame)]);
    assert_eq!(dimensions(&file), Some((16, 8)));
    assert!(matches!(decode(&file), Err(CodecError::Corrupt("WebP", _))));
}
