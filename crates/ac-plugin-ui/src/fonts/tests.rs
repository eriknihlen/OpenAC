//! Baking, kerning, the budgets, and hostile files.

use super::*;
use crate::PluginUi;
use crate::model::{UiChange, UiInput};

mod crafted;

const CFF: &[u8] = include_bytes!("../../tests/data/fonts/NotoSansCffFixture.otf");
const OWNER: &str = "acme.hello";

fn regular() -> Vec<u8> {
    NOTO_SANS_REGULAR.to_vec()
}

#[test]
fn noto_bakes_the_default_characters_into_one_atlas() {
    for size in [8.0, 16.0, 32.0, 64.0] {
        let font = bake(regular(), size, &DEFAULT_RANGES, MAXIMUM_GLYPHS).unwrap();
        assert!(font.glyph('A').is_some(), "{size}");
        assert!(font.glyph('я').is_some(), "{size}");
        assert_eq!(
            font.coverage.len() as u64,
            u64::from(font.atlas_width) * u64::from(font.atlas_height)
        );
        assert!(font.coverage.iter().any(|c| *c > 200), "{size}");
    }
}

#[test]
fn sixteen_pixels_fits_a_small_atlas_and_has_the_font_metrics() {
    let font = bake(regular(), 16.0, &DEFAULT_RANGES, MAXIMUM_GLYPHS).unwrap();
    assert!(font.atlas_width <= 1024 && font.atlas_height <= 1024);
    assert_eq!(font.pixel_size, 16.0);
    assert!(
        font.line_height >= 16.0 && font.line_height <= 18.0,
        "{}",
        font.line_height
    );
    assert!(font.ascent > 8.0 && font.ascent < 16.0, "{}", font.ascent);
}

#[test]
fn a_glyph_lands_in_the_atlas_where_its_coordinates_say() {
    let font = bake(regular(), 32.0, &[(0x41, 0x41)], MAXIMUM_GLYPHS).unwrap();
    let g = *font.glyph('A').unwrap();
    assert!(g.width > 10.0 && g.height > 10.0);
    assert!(g.y < 0.0, "above the baseline");
    let w = font.atlas_width as f32;
    let h = font.atlas_height as f32;
    assert_eq!(((g.u1 - g.u0) * w).round(), g.width);
    assert_eq!(((g.v1 - g.v0) * h).round(), g.height);
    let (x0, y0) = ((g.u0 * w) as usize, (g.v0 * h) as usize);
    let inside = (0..g.height as usize)
        .flat_map(|row| {
            let start = (y0 + row) * font.atlas_width as usize + x0;
            font.coverage[start..start + g.width as usize].iter()
        })
        .filter(|c| **c > 0)
        .count();
    let everywhere = font.coverage.iter().filter(|c| **c > 0).count();
    assert_eq!(inside, everywhere, "nothing outside the glyph's box");
}

#[test]
fn a_custom_range_bakes_only_those_characters() {
    let font = bake(regular(), 16.0, &[(0x41, 0x5A)], MAXIMUM_GLYPHS).unwrap();
    assert_eq!(font.glyph_count(), 26);
    assert_eq!(font.measure_width("a"), 0.0, "no 'a', and no '?' baked");
}

#[test]
fn a_range_the_font_has_nothing_in_is_refused() {
    let why = bake(regular(), 16.0, &[(0xE000, 0xE010)], MAXIMUM_GLYPHS).unwrap_err();
    assert!(why.contains("none of the requested characters"), "{why}");
}

#[test]
fn more_glyphs_than_the_cap_is_refused() {
    let why = bake(regular(), 16.0, &DEFAULT_RANGES, 10).unwrap_err();
    assert!(why.contains("the most a font may prepare is 10"), "{why}");
}

#[test]
fn a_range_that_is_not_one_is_refused() {
    for range in [(0x50, 0x40), (0x20, 0x11_0000)] {
        let why = bake(regular(), 16.0, &[range], MAXIMUM_GLYPHS).unwrap_err();
        assert!(why.contains("is not a range of code points"), "{why}");
    }
}

#[test]
fn scanning_past_the_ceiling_is_refused() {
    let why = bake(regular(), 16.0, &[(0x0, 0x2_0000)], MAXIMUM_GLYPHS).unwrap_err();
    assert!(why.contains("more than 65536 code points"), "{why}");
}

#[test]
fn a_size_that_is_no_size_is_refused() {
    for size in [0.0, -1.0, f32::NAN, f32::INFINITY] {
        assert!(bake(regular(), size, &DEFAULT_RANGES, MAXIMUM_GLYPHS).is_err());
    }
}

#[test]
fn an_opentype_cff_font_bakes() {
    let font = bake(CFF.to_vec(), 32.0, &DEFAULT_RANGES, MAXIMUM_GLYPHS).unwrap();
    assert_eq!(font.glyph_count(), 5, "A V T o ?");
    let a = font.glyph('A').unwrap();
    assert!(a.width > 10.0 && a.height > 10.0 && a.advance > 10.0);
    assert!(font.coverage.iter().any(|c| *c > 200), "it drew");
}

#[test]
fn kerning_pulls_a_v_together_in_truetype_and_cff() {
    for (kind, file) in [("ttf", regular()), ("cff", CFF.to_vec())] {
        let font = bake(file, 32.0, &[(0x20, 0x7E)], MAXIMUM_GLYPHS).unwrap();
        let (a, v) = (
            font.glyph('A').unwrap().advance,
            font.glyph('V').unwrap().advance,
        );
        let kerned = font.measure_width("AV");
        assert!(kerned < a + v - 0.5, "{kind}: {kerned} of {}", a + v);
    }
}

#[test]
fn a_missing_character_is_a_question_mark_when_one_was_baked_and_nothing_otherwise() {
    let with = bake(regular(), 16.0, &[(0x3F, 0x41)], MAXIMUM_GLYPHS).unwrap();
    assert_eq!(with.measure_width("z"), with.measure_width("?"));
    let without = bake(regular(), 16.0, &[(0x41, 0x41)], MAXIMUM_GLYPHS).unwrap();
    assert_eq!(without.measure_width("z"), 0.0);
    assert_eq!(
        with.measure_width("\n"),
        0.0,
        "control characters have none"
    );
}

#[test]
fn a_character_past_the_basic_plane_is_one_character() {
    // The plane the bundled font lacks: a '?' once, not twice.
    let font = bake(regular(), 16.0, &[(0x3F, 0x3F)], MAXIMUM_GLYPHS).unwrap();
    assert_eq!(font.measure_width("😀"), font.measure_width("?"));
}

fn sfnt(version: &[u8; 4], tables: &[(&[u8; 4], u32, u32)], extra: usize) -> Vec<u8> {
    let mut out = version.to_vec();
    out.extend_from_slice(&(tables.len() as u16).to_be_bytes());
    out.extend_from_slice(&[0; 6]);
    for (tag, offset, length) in tables {
        out.extend_from_slice(*tag);
        out.extend_from_slice(&[0; 4]);
        out.extend_from_slice(&offset.to_be_bytes());
        out.extend_from_slice(&length.to_be_bytes());
    }
    out.resize(out.len() + extra, 0);
    out
}

#[test]
fn bytes_that_are_not_a_font_are_refused_without_a_panic() {
    let full = [
        (b"cmap", 12u32, 0u32),
        (b"head", 12, 0),
        (b"hhea", 12, 0),
        (b"hmtx", 12, 0),
    ];
    for (bytes, expected) in [
        (Vec::new(), "not a TrueType"),
        (vec![0; 4096], "not a TrueType"),
        (b"ttcf0000000000000000".to_vec(), "collections"),
        (sfnt(&[0, 1, 0, 0], &[], 16), "not a TrueType"),
        (
            sfnt(&[0, 1, 0, 0], &[(b"cmap", 0, 0xFFFF_FFFF)], 16),
            "not a TrueType",
        ),
        (
            sfnt(&[0, 1, 0, 0], &[(b"head", 12, 0)], 16),
            "missing its 'cmap'",
        ),
        (sfnt(&[0, 1, 0, 0], &full, 16), "missing its outlines"),
    ] {
        let why = bake(bytes, 16.0, &DEFAULT_RANGES, MAXIMUM_GLYPHS).unwrap_err();
        assert!(why.contains(expected), "{why}, wanted {expected}");
    }
}

/// A file whose line metrics make every glyph enormous is refused before
/// anything is allocated for it or its outlines are read: a glyph this size
/// would take gigabytes.
#[test]
fn a_glyph_claiming_to_be_huge_is_refused_before_it_is_drawn() {
    let mut bytes = CFF.to_vec();
    let tables = usize::from(u16::from_be_bytes([bytes[4], bytes[5]]));
    let record = (0..tables)
        .map(|t| 12 + 16 * t)
        .find(|r| &bytes[*r..*r + 4] == b"hhea")
        .unwrap();
    let hhea = u32::from_be_bytes(bytes[record + 8..record + 12].try_into().unwrap()) as usize;
    // Ascender 1, descender 0: the size asked for spans one font unit.
    bytes[hhea + 4..hhea + 6].copy_from_slice(&1i16.to_be_bytes());
    bytes[hhea + 6..hhea + 8].copy_from_slice(&0i16.to_be_bytes());
    let why = bake(bytes, 64.0, &DEFAULT_RANGES, MAXIMUM_GLYPHS).unwrap_err();
    assert!(why.contains("line metrics are not usable"), "{why}");
}

/// A small generator, so a failure repeats.
struct Random(u64);

impl Random {
    fn next(&mut self) -> u64 {
        self.0 ^= self.0 << 13;
        self.0 ^= self.0 >> 7;
        self.0 ^= self.0 << 17;
        self.0
    }
}

/// Damaged files either bake or are refused; none may panic out, and what
/// does bake still lays text out. Bytes of the table directory and of the
/// tables are overwritten, and the file is cut short.
#[test]
fn damaged_font_files_never_panic_out() {
    let mut random = Random(0x0DEC_AF00_C0FF_EE01);
    for round in 0..300 {
        let mut bytes = CFF.to_vec();
        for _ in 0..1 + random.next() % 8 {
            let at = (random.next() as usize) % bytes.len();
            bytes[at] = random.next() as u8;
        }
        if round % 5 == 0 {
            bytes.truncate((random.next() as usize) % bytes.len());
        }
        if let Ok(font) = bake(bytes, 16.0, &DEFAULT_RANGES, MAXIMUM_GLYPHS) {
            let _ = font.measure_width("AVTo?");
        }
    }
}

fn ui() -> PluginUi {
    let mut ui = PluginUi::new();
    ui.set_window_attached(true);
    ui
}

fn take_added(ui: &mut PluginUi) -> Vec<crate::fonts::FontView> {
    ui.take_changes()
        .into_iter()
        .filter_map(|c| match c {
            UiChange::FontAdded(view) => Some(*view),
            _ => None,
        })
        .collect()
}

#[test]
fn asking_twice_holds_twice_and_handles_are_not_reused() {
    let mut ui = ui();
    let first = ui.font_bundled(OWNER, 16.0, FontWeight::Regular);
    assert_ne!(first.handle, 0);
    assert_eq!(ui.font_bundled(OWNER, 16.0, FontWeight::Regular), first);
    assert_eq!(ui.font_count(OWNER), 1);
    assert!(ui.font_release(OWNER, first.handle));
    assert_eq!(ui.font_count(OWNER), 1, "one hold left");
    assert!(ui.font_release(OWNER, first.handle));
    assert_eq!(ui.font_count(OWNER), 0);
    assert!(!ui.font_release(OWNER, first.handle), "already gone");
    assert!(
        ui.take_changes()
            .iter()
            .any(|c| matches!(c, UiChange::FontRemoved { handle, .. } if *handle == first.handle))
    );
    let next = ui.font_bundled(OWNER, 16.0, FontWeight::Regular);
    assert!(next.handle > first.handle);
}

#[test]
fn a_size_is_prepared_to_the_nearest_quarter_pixel() {
    let mut ui = ui();
    let font = ui.font_bundled(OWNER, 16.1, FontWeight::Regular);
    assert_eq!(font.pixel_size, 16.0);
    assert_eq!(ui.font_bundled(OWNER, 16.0, FontWeight::Regular), font);
    let other = ui.font_bundled(OWNER, 16.25, FontWeight::Regular);
    assert_eq!(other.pixel_size, 16.25);
    assert_ne!(other.handle, font.handle);
}

#[test]
fn a_size_outside_the_limits_is_refused_and_reported_once() {
    let mut ui = ui();
    for size in [5.9, 64.5, f32::NAN] {
        assert_eq!(
            ui.font_bundled(OWNER, size, FontWeight::Regular),
            FontInfo::default()
        );
    }
    assert_eq!(ui.font_bundled(OWNER, 5.9, FontWeight::Regular).handle, 0);
    assert_eq!(ui.take_reports().len(), 3);
    assert_ne!(ui.font_bundled(OWNER, 6.0, FontWeight::Regular).handle, 0);
    assert_ne!(ui.font_bundled(OWNER, 64.0, FontWeight::SemiBold).handle, 0);
}

#[test]
fn the_weights_are_different_fonts_and_the_bundled_bakes_are_shared() {
    let mut ui = ui();
    let regular = ui.font_bundled(OWNER, 20.0, FontWeight::Regular);
    let semibold = ui.font_bundled(OWNER, 20.0, FontWeight::SemiBold);
    assert_ne!(regular.handle, semibold.handle);
    assert!(
        ui.font_measure(OWNER, semibold.handle, "Golem").0
            > ui.font_measure(OWNER, regular.handle, "Golem").0
    );
    let added = take_added(&mut ui);
    let other = ui.font_bundled("acme.other", 20.0, FontWeight::Regular);
    let again = take_added(&mut ui);
    assert_ne!(other.handle, 0);
    assert!(Arc::ptr_eq(&added[0].font, &again[0].font), "one bake");
    assert_eq!(ui.font_owned_bytes(OWNER), 0, "shared, so it costs nothing");
}

#[test]
fn past_the_count_a_font_is_refused_and_one_let_go_makes_room() {
    let mut ui = ui();
    let handles: Vec<u32> = (0..MAXIMUM_COUNT)
        .map(|i| {
            ui.font_bundled(OWNER, 8.0 + i as f32, FontWeight::Regular)
                .handle
        })
        .collect();
    assert!(handles.iter().all(|h| *h != 0));
    assert_eq!(ui.font_bundled(OWNER, 40.0, FontWeight::Regular).handle, 0);
    let reports = ui.take_reports();
    assert!(
        reports[0].1.contains("already holds 16 fonts"),
        "{reports:?}"
    );
    assert!(ui.font_release(OWNER, handles[0]));
    assert_ne!(ui.font_bundled(OWNER, 40.0, FontWeight::Regular).handle, 0);
}

#[test]
fn a_plugins_own_font_counts_its_file_and_atlas_against_the_budget() {
    let mut ui = ui();
    let font = ui.font_load(OWNER, "fonts/a.otf", CFF, 16.0, &[]);
    assert_ne!(font.handle, 0);
    assert_eq!(ui.font_owned_bytes(OWNER), CFF.len() as u64 + 256 * 256);
    // A repeat is the same font, and its bytes are not looked at.
    assert_eq!(ui.font_load(OWNER, "fonts/a.otf", b"junk", 16.0, &[]), font);
    assert_eq!(ui.font_owned_bytes(OWNER), CFF.len() as u64 + 256 * 256);
    assert!(ui.font_release(OWNER, font.handle));
    assert!(ui.font_release(OWNER, font.handle));
    assert_eq!(ui.font_owned_bytes(OWNER), 0);
}

#[test]
fn own_fonts_past_the_budget_are_refused() {
    let mut ui = ui();
    // Regular is over half a megabyte; a 64 px default set takes a 2048 atlas.
    let mut made = 0;
    let mut refused = None;
    for i in 0..MAXIMUM_COUNT {
        let font = ui.font_load(OWNER, &format!("f{i}"), NOTO_SANS_REGULAR, 64.0, &[]);
        if font.handle == 0 {
            refused = Some(i);
            break;
        }
        made += 1;
    }
    assert!(made >= 1, "the first fits");
    assert!(refused.is_some(), "the budget ends it");
    assert!(ui.font_owned_bytes(OWNER) <= MAXIMUM_BYTES);
    let reports = ui.take_reports();
    assert!(reports[0].1.contains("the budget is"), "{reports:?}");
}

#[test]
fn a_file_over_the_limit_is_refused_before_it_is_read() {
    let mut ui = ui();
    let bytes = vec![0u8; MAXIMUM_FILE_BYTES + 1];
    assert_eq!(ui.font_load(OWNER, "big", &bytes, 16.0, &[]).handle, 0);
    let reports = ui.take_reports();
    assert!(reports[0].1.contains("larger than"), "{reports:?}");
}

#[test]
fn a_file_that_is_no_font_is_refused_and_reported_once() {
    let mut ui = ui();
    for _ in 0..3 {
        assert_eq!(
            ui.font_load(OWNER, "bad", b"not a font", 16.0, &[]).handle,
            0
        );
    }
    assert_eq!(ui.take_reports().len(), 1);
    assert_eq!(ui.font_count(OWNER), 0);
    assert!(ui.font_owned_bytes(OWNER) == 0);
}

#[test]
fn a_font_measures_only_for_its_plugin_and_not_after_it_goes() {
    let mut ui = ui();
    let font = ui.font_bundled(OWNER, 16.0, FontWeight::Regular);
    let (w, h) = ui.font_measure(OWNER, font.handle, "Golem");
    assert!(w > 0.0);
    assert_eq!(h, font.line_height);
    assert_eq!(ui.font_measure(OWNER, font.handle, ""), (0.0, 0.0));
    assert_eq!(
        ui.font_measure("acme.other", font.handle, "Golem"),
        (0.0, 0.0)
    );
    assert_eq!(ui.font_measure(OWNER, 99, "Golem"), (0.0, 0.0));
    ui.font_release(OWNER, font.handle);
    assert_eq!(ui.font_measure(OWNER, font.handle, "Golem"), (0.0, 0.0));
}

#[test]
fn fonts_go_with_their_plugin_and_handles_are_not_reused_after_a_reload() {
    let mut ui = ui();
    let font = ui.font_bundled(OWNER, 16.0, FontWeight::Regular);
    ui.take_changes();
    ui.release(OWNER);
    assert_eq!(ui.font_count(OWNER), 0);
    assert!(
        ui.take_changes()
            .iter()
            .any(|c| matches!(c, UiChange::FontRemoved { handle, .. } if *handle == font.handle))
    );
    let next = ui.font_bundled(OWNER, 16.0, FontWeight::Regular);
    assert!(next.handle > font.handle);
}

#[test]
fn a_window_that_attaches_gets_the_fonts_held() {
    let mut ui = ui();
    let font = ui.font_bundled(OWNER, 16.0, FontWeight::Regular);
    let snapshot = ui.snapshot();
    assert!(snapshot.iter().any(
        |c| matches!(c, UiChange::FontAdded(v) if v.handle == font.handle && v.owner == OWNER)
    ));
}

#[test]
fn a_sharper_bake_follows_the_display_and_changes_no_measurement() {
    let mut ui = ui();
    let font = ui.font_load(OWNER, "a", CFF, 16.0, &[]);
    let before = ui.font_measure(OWNER, font.handle, "AVTo");
    assert!(take_added(&mut ui)[0].sharp.is_none(), "one pixel a unit");
    ui.input(UiInput::DisplayDensity(2.0));
    let added = take_added(&mut ui);
    let sharp = added[0].sharp.as_ref().unwrap();
    assert_eq!(sharp.scale, 2.0);
    assert_eq!(sharp.font.pixel_size, 32.0);
    assert!(sharp.font.glyph('A').unwrap().height > added[0].font.glyph('A').unwrap().height);
    assert_eq!(ui.font_measure(OWNER, font.handle, "AVTo"), before);
    // The same density again costs nothing.
    ui.input(UiInput::DisplayDensity(2.0));
    assert!(take_added(&mut ui).is_empty());
    // A font asked for now gets one at once.
    ui.font_bundled(OWNER, 16.0, FontWeight::Regular);
    let new = take_added(&mut ui);
    assert_eq!(new.last().unwrap().sharp.as_ref().unwrap().scale, 2.0);
    // Back on a standard display the bakes go.
    ui.input(UiInput::DisplayDensity(1.0));
    assert!(take_added(&mut ui).iter().all(|v| v.sharp.is_none()));
}

#[test]
fn a_density_is_rounded_down_to_a_quarter_step() {
    assert_eq!(sharp_scale(1.6), 1.5);
    assert_eq!(sharp_scale(2.0), 2.0);
    assert_eq!(sharp_scale(0.5), 1.0);
    assert_eq!(sharp_scale(f32::NAN), 1.0);
}

#[test]
fn a_sharper_bake_that_does_not_fit_steps_down_or_is_left_out_and_reported() {
    let mut ui = ui();
    // A 64 px default set takes most of a 2048 atlas at 1x and cannot fit at 2x.
    let font = ui.font_bundled(OWNER, 64.0, FontWeight::Regular);
    assert_ne!(font.handle, 0);
    ui.take_changes();
    ui.input(UiInput::DisplayDensity(4.0));
    let added = take_added(&mut ui);
    let drawn = added[0].sharp.as_ref().map_or(1.0, |s| s.scale);
    assert!(drawn < 4.0, "{drawn}");
    let reports = ui.take_reports();
    assert_eq!(reports.len(), 1, "{reports:?}");
    assert!(reports[0].1.contains("less sharp"), "{reports:?}");
}
