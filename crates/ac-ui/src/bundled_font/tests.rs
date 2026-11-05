//! Ported from OpenAC `tests/AcDream.App.Tests/UI/BundledUiFontTests.cs`
//! (`EmbeddedFontBakesWithoutSystemFonts`); the Regular goldens in
//! `tests/data/bundled_font/` come from OpenAC's own bake, and the SemiBold
//! ones from the same baker.

use super::*;

const RECORDS_16: &str = include_str!("../../tests/data/bundled_font/records_16.txt");
const RECORDS_32: &str = include_str!("../../tests/data/bundled_font/records_32.txt");
const COVERAGE_16: &str = include_str!("../../tests/data/bundled_font/coverage_16.txt");
const SEMIBOLD_16: &str = include_str!("../../tests/data/bundled_font/records_semibold_16.txt");
const SEMIBOLD_32: &str = include_str!("../../tests/data/bundled_font/records_semibold_32.txt");

fn numbers(line: &str) -> Vec<i64> {
    line.split_whitespace()
        .map(|w| w.parse().unwrap())
        .collect()
}

/// A pixel's coverage.
fn alpha(b: &BakedFont, x: u32, y: u32) -> u8 {
    b.coverage[(y * b.width + x) as usize]
}

#[test]
fn embedded_font_bakes_without_system_fonts() {
    for size in [12.0, 16.0, 24.0, 32.0] {
        let atlas = bake(size, Weight::Regular).unwrap();
        let font = atlas.create_font(TextureRef::Runtime(1));
        assert!(atlas.coverage.iter().any(|&a| a > 0 && a < 255), "{size}");
        for c in "BuffProfile_Banes åäö Ω Ж —".chars() {
            assert!(atlas.glyphs.iter().any(|g| g.unicode == c as u16), "{c}");
            let glyph = font.glyph(c).unwrap();
            assert!(crate::font::glyph_advance(glyph) > 0.0, "{c} at {size}");
            assert!(u32::from(glyph.offset_x) + u32::from(glyph.width) <= atlas.width);
            assert!(u32::from(glyph.offset_y) + u32::from(glyph.height) <= atlas.height);
        }
        assert!(font.measure_width("WWW") > font.measure_width("iii"));
        assert_eq!(font.measure_width("?"), font.measure_width("漢"));
        assert!(font.glyph('\n').is_none());
    }
}

#[test]
fn glyph_records_and_line_metrics_are_openacs() {
    for (size, weight, golden) in [
        (16.0, Weight::Regular, RECORDS_16),
        (32.0, Weight::Regular, RECORDS_32),
        (16.0, Weight::SemiBold, SEMIBOLD_16),
        (32.0, Weight::SemiBold, SEMIBOLD_32),
    ] {
        let atlas = bake(size, weight).unwrap();
        let lines = golden
            .lines()
            .skip_while(|l| !l.starts_with("# pixel_height"));
        let mut lines = lines.peekable();
        let header: Vec<&str> = lines.next().unwrap().split_whitespace().collect();
        assert_eq!(
            header[4].parse::<f32>().unwrap(),
            atlas.line_height,
            "{size}"
        );
        assert_eq!(header[6].parse::<f32>().unwrap(), atlas.ascent, "{size}");
        let expected: Vec<FontCharDesc> = lines
            .map(|l| {
                let (code, rest) = l.split_once(' ').unwrap();
                let n = numbers(rest);
                FontCharDesc {
                    unicode: u16::from_str_radix(code, 16).unwrap(),
                    offset_x: n[0] as u16,
                    offset_y: n[1] as u16,
                    width: n[2] as u8,
                    height: n[3] as u8,
                    horizontal_offset_before: n[4] as i8,
                    horizontal_offset_after: n[5] as i8,
                    vertical_offset_before: n[6] as i8,
                }
            })
            .collect();
        assert_eq!(expected.len(), 1063, "{size}");
        assert_eq!(atlas.glyphs, expected, "{size}");
    }
}

/// The pixels inside each glyph box come from another rasteriser than
/// OpenAC's: close, not equal. Per glyph, the average
/// difference stays small, the total ink is within a tenth, and a pixel
/// fully inside the outline there is mostly inside here.
#[test]
fn coverage_is_openacs_within_the_rasteriser_difference() {
    let atlas = bake(16.0, Weight::Regular).unwrap();
    let mut glyphs = 0;
    for line in COVERAGE_16.lines().filter(|l| !l.starts_with('#')) {
        let (code, rest) = line.split_once(' ').unwrap();
        let n = numbers(rest);
        let (w, h) = (n[0] as u32, n[1] as u32);
        let expected = &n[2..];
        let c = char::from_u32(u32::from_str_radix(code, 16).unwrap()).unwrap();
        let g = atlas.glyphs.iter().find(|g| g.unicode == c as u16).unwrap();
        assert_eq!((u32::from(g.width), u32::from(g.height)), (w, h), "{c}");
        let (mut diff, mut ink_there, mut ink_here) = (0i64, 0i64, 0i64);
        for y in 0..h {
            for x in 0..w {
                let there = expected[(y * w + x) as usize];
                let here = i64::from(alpha(
                    &atlas,
                    u32::from(g.offset_x) + x,
                    u32::from(g.offset_y) + y,
                ));
                diff += (there - here).abs();
                ink_there += there;
                ink_here += here;
                if there == 255 {
                    assert!(here >= 192, "{c} at {x},{y}: {here}");
                }
            }
        }
        let mean = diff as f64 / f64::from(w * h);
        let ink = ink_here as f64 / ink_there as f64;
        assert!(mean <= 16.0, "{c}: mean difference {mean}");
        assert!((0.9..=1.1).contains(&ink), "{c}: ink ratio {ink}");
        glyphs += 1;
    }
    assert_eq!(glyphs, 22);
}

#[test]
fn the_atlas_is_one_coverage_byte_per_pixel() {
    let atlas = bake(DEFAULT_PIXEL_HEIGHT, Weight::Regular).unwrap();
    assert_eq!((atlas.width, atlas.height), (512, 1536));
    assert_eq!(atlas.coverage.len(), 512 * 1536);
    // The one-pixel margin the packer keeps at the page's top and left.
    for page in 0..3 {
        assert!((0..512).all(|x| alpha(&atlas, x, page * 512) == 0));
        assert!((0..512).all(|y| alpha(&atlas, 0, page * 512 + y) == 0));
    }
    let font = atlas.create_font(TextureRef::Runtime(7));
    assert_eq!(font.foreground.texture, TextureRef::Runtime(7));
    assert!(font.background.is_none());
    assert_eq!((font.line_height, font.baseline_offset), (16.0, 13.0));
    assert_eq!((font.border_x, font.border_y), (0, 0));
}

#[test]
fn sizes_outside_8_to_32_pixels_are_refused() {
    for size in [7.9, 32.1, 0.0, -16.0, f32::NAN, f32::INFINITY] {
        assert!(
            matches!(
                bake(size, Weight::Regular),
                Err(BundledFontError::SizeOutOfRange(_))
            ),
            "{size}"
        );
    }
    assert!(bake(8.0, Weight::Regular).is_ok());
}

/// The packer's edge cases on a small page: a range that runs out of room
/// keeps empty records from the glyph that did not fit on (a partial bake,
/// which OpenAC's baker call takes for success); a first glyph that does
/// not fit fails the bake.
#[test]
fn a_full_page_keeps_empty_records_and_a_first_glyph_that_does_not_fit_fails() {
    let face = Face::parse(NOTO_SANS_REGULAR, 0).unwrap();
    let baker = Baker::new(NOTO_SANS_REGULAR, &face, 16.0).unwrap();
    let mut page = vec![0u8; 40 * 40];
    let baked = baker.bake_range(&mut page, 40, u32::from('A'), 26).unwrap();
    let fitted = baked.iter().take_while(|g| g.x1 > g.x0).count();
    assert!(fitted > 0 && fitted < 26, "{fitted}");
    assert!(baked[fitted..].iter().all(|g| *g == BakedChar::default()));
    // Rows: one pixel of margin, one pixel between glyphs, the next row
    // under the tallest glyph of the row before.
    assert_eq!((baked[0].x0, baked[0].y0), (1, 1));
    assert_eq!(baked[1].x0, baked[0].x1 + 1);
    let mut tiny = vec![0u8; 8 * 8];
    assert_eq!(
        baker.bake_range(&mut tiny, 8, u32::from('W'), 1),
        Err(BundledFontError::DoesNotFit)
    );
}

#[test]
fn semibold_bakes_and_differs_from_regular() {
    let regular = bake(16.0, Weight::Regular).unwrap();
    let bold = bake(16.0, Weight::SemiBold).unwrap();
    assert!(bold.glyphs.iter().any(|g| g.unicode == u16::from(b'W')));
    assert_ne!(regular.coverage, bold.coverage);
    // Heavier strokes lay down more ink.
    let ink = |b: &BakedFont| b.coverage.iter().map(|&a| u64::from(a)).sum::<u64>();
    assert!(ink(&bold) > ink(&regular));
}

/// A font's id is its cache key, so two weights of a size must differ.
#[test]
fn each_weight_and_size_has_its_own_font_id() {
    let id = |size, weight| {
        bake(size, weight)
            .unwrap()
            .create_font(TextureRef::Runtime(1))
            .id
    };
    let ids = [
        id(16.0, Weight::Regular),
        id(16.0, Weight::SemiBold),
        id(32.0, Weight::Regular),
        id(32.0, Weight::SemiBold),
    ];
    let unique: std::collections::HashSet<u32> = ids.into_iter().collect();
    assert_eq!(unique.len(), 4, "{ids:x?}");
}

#[test]
fn a_sharper_bake_is_made_at_the_display_density_where_it_fits() {
    for (pixel_height, density, expected) in [
        (16.0, 1.0, 1.0),
        (16.0, 1.25, 1.25),
        (16.0, 1.5, 1.5),
        (16.0, 2.0, 2.0),
        // 48 pixels is past the largest bake.
        (16.0, 3.0, 2.0),
        // 32 / 12, down to a quarter step.
        (12.0, 3.0, 2.5),
        (16.0, 1.6, 1.5),
        (16.0, 0.5, 0.5),
        (16.0, f32::NAN, 1.0),
    ] {
        assert_eq!(
            sharp_scale_for(pixel_height, density),
            expected,
            "{pixel_height} at {density}"
        );
    }
}

/// The largest sharper bake, in both weights, has every glyph of the font's
/// own bake that has an image, so no text mixes the two.
#[test]
fn the_sharper_bake_has_an_image_for_every_glyph_of_the_usual_size() {
    for weight in [Weight::Regular, Weight::SemiBold] {
        let own = bake(DEFAULT_PIXEL_HEIGHT, weight).unwrap();
        let sharp = bake(MAXIMUM_PIXEL_HEIGHT, weight).unwrap();
        let missing: Vec<u16> = own
            .glyphs
            .iter()
            .filter(|g| g.width > 0 && g.height > 0)
            .filter(|g| {
                !sharp
                    .glyphs
                    .iter()
                    .any(|f| f.unicode == g.unicode && f.width > 0 && f.height > 0)
            })
            .map(|g| g.unicode)
            .collect();
        assert!(missing.is_empty(), "{weight:?}: {missing:x?}");
    }
}
