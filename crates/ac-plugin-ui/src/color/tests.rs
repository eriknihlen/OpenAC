//! Ported from OpenAC `tests/AcDream.App.Tests/UI/MarkupColorValueTests.cs`
//! and `MarkupColorSupportTests.cs` (the ramps and the recent colours).

use super::*;
use crate::control_state::MemoryState;

#[test]
fn format_writes_upper_case_hex() {
    // Format_WritesUpperCaseHex
    assert_eq!(format(0x80FF_0000, true), "#80FF0000");
    assert_eq!(format(0x80FF_0000, false), "#FF0000");
    assert_eq!(format(0x00AB_CDEF, true), "#00ABCDEF");
}

#[test]
fn parse_accepts_six_or_eight_digits() {
    // Parse_AcceptsSixOrEightDigits, Parse_SixDigitsTakeTheGivenAlpha
    for (text, expected) in [
        ("#FF0000", 0xFFFF_0000),
        ("ff0000", 0xFFFF_0000),
        ("  #80ff0000 ", 0x80FF_0000),
        ("80FF0000", 0x80FF_0000),
    ] {
        assert_eq!(parse(text, 0xFF), Some(expected), "{text}");
    }
    assert_eq!(parse("#123456", 0x40), Some(0x4012_3456));
}

#[test]
fn parse_rejects_everything_else() {
    // Parse_RejectsEverythingElse
    for text in [
        "",
        "#",
        "#12345",
        "#1234567",
        "#123456789",
        "#GG0000",
        "##FF0000",
    ] {
        assert_eq!(parse(text, 0xFF), None, "{text}");
    }
}

#[test]
fn parse_strict_takes_only_hash_and_eight_digits() {
    // ParseStrict_AcceptsOnlyHashAndEightDigits, ParseStrict_RejectsOtherForms
    assert_eq!(parse_strict("#80ff0000"), Some(0x80FF_0000));
    assert_eq!(parse_strict("#FFFFFFFF"), Some(0xFFFF_FFFF));
    for text in ["FF000000", "#FF0000", " #FF000000", ""] {
        assert_eq!(parse_strict(text), None, "{text}");
    }
}

fn close(a: f32, b: f32, tolerance: f32) -> bool {
    (a - b).abs() <= tolerance
}

#[test]
fn to_hsv_of_known_colours() {
    // ToHsv_KnownColours
    for (argb, h, s, v) in [
        (0xFFFF_0000, 0.0, 1.0, 1.0),
        (0xFF00_FFFF, 180.0, 1.0, 1.0),
        (0xFF80_8080, 0.0, 0.0, 0.502),
        (0xFF80_4040, 0.0, 0.5, 0.502),
        (0xFF00_0000, 0.0, 0.0, 0.0),
    ] {
        let (gh, gs, gv) = to_hsv(argb);
        assert!(close(gh, h, 0.05), "{argb:08X} h {gh}");
        assert!(close(gs, s, 0.005), "{argb:08X} s {gs}");
        assert!(close(gv, v, 0.005), "{argb:08X} v {gv}");
    }
}

#[test]
fn from_hsv_of_known_colours() {
    // FromHsv_KnownColours, FromHsv_KeepsTheGivenAlpha
    for (h, s, v, expected) in [
        (0.0, 1.0, 1.0, 0xFFFF_0000),
        (180.0, 1.0, 1.0, 0xFF00_FFFF),
        (0.0, 0.5, 0.5, 0xFF80_4040),
        (360.0, 1.0, 1.0, 0xFFFF_0000),
        (120.0, 1.0, 1.0, 0xFF00_FF00),
        (240.0, 1.0, 1.0, 0xFF00_00FF),
    ] {
        assert_eq!(from_hsv(h, s, v, 0xFF), expected, "{h} {s} {v}");
    }
    assert_eq!(from_hsv(0.0, 1.0, 1.0, 0x40), 0x40FF_0000);
}

#[test]
fn hsv_round_trips_a_sample_of_byte_colours() {
    // Hsv_RoundTripsEveryByteColourInASample
    for i in 0u32..4096 {
        let r = (i * 37) & 0xFF;
        let g = (i * 101 + 13) & 0xFF;
        let b = (i * 211 + 7) & 0xFF;
        let argb = 0xFF00_0000 | r << 16 | g << 8 | b;
        let (h, s, v) = to_hsv(argb);
        assert_eq!(from_hsv(h, s, v, 0xFF), argb, "{argb:08X}");
    }
}

fn at(rgba: &[u8], w: usize, x: usize, y: usize, channel: usize) -> u8 {
    rgba[(y * w + x) * 4 + channel]
}

#[test]
fn ramps_fade_as_the_square_and_strips_need() {
    // WhiteToClear_FadesLeftToRight, ClearToBlack_DarkensTopToBottom,
    // Hue_RunsFromRedThroughCyan, AlphaRamp_IsOpaqueAtTheTop
    let p = ramps::white_to_clear(128, 4);
    assert_eq!(p.len(), 128 * 4 * 4);
    assert_eq!(at(&p, 128, 0, 2, 3), 255);
    assert_eq!(at(&p, 128, 127, 2, 3), 0);
    assert_eq!(at(&p, 128, 64, 0, 0), 255);
    let p = ramps::clear_to_black(4, 128);
    assert_eq!(at(&p, 4, 1, 0, 3), 0);
    assert_eq!(at(&p, 4, 1, 127, 3), 255);
    assert_eq!(at(&p, 4, 1, 64, 0), 0);
    let p = ramps::hue(16, 128);
    assert_eq!(
        (
            at(&p, 16, 0, 0, 0),
            at(&p, 16, 0, 0, 1),
            at(&p, 16, 0, 0, 3)
        ),
        (255, 0, 255)
    );
    assert!(at(&p, 16, 8, 64, 0) <= 2);
    assert!(at(&p, 16, 8, 64, 1) >= 253);
    assert!(at(&p, 16, 8, 64, 2) >= 253);
    let p = ramps::alpha_ramp(16, 128);
    assert_eq!(at(&p, 16, 3, 0, 3), 255);
    assert_eq!(at(&p, 16, 3, 127, 3), 0);
    assert_eq!(at(&p, 16, 3, 127, 1), 255);
}

#[test]
fn recent_is_newest_first_without_repeats_and_at_most_eight() {
    // Recent_NewestFirstNoRepeatsAtMostEight
    let mut recent = RecentColors::default();
    for i in 1..=10 {
        recent.add(0xFF00_0000 | i, None);
    }
    recent.add(0xFF00_0005, None);
    assert_eq!(
        recent.colors(None),
        [
            0xFF00_0005,
            0xFF00_000A,
            0xFF00_0009,
            0xFF00_0008,
            0xFF00_0007,
            0xFF00_0006,
            0xFF00_0004,
            0xFF00_0003
        ]
    );
}

fn stored(state: &MemoryState) -> Option<&String> {
    state.values.get(&("color".to_owned(), "recent".to_owned()))
}

#[test]
fn recent_stores_comma_text_in_the_shared_state() {
    // Recent_StoresCommaTextInTheSharedState
    let mut state = MemoryState::default();
    let mut recent = RecentColors::default();
    recent.add(0x80FF_0000, Some(&mut state));
    recent.add(0xFF00_FF00, Some(&mut state));
    assert_eq!(
        stored(&state).map(String::as_str),
        Some("#FF00FF00,#80FF0000")
    );
}

#[test]
fn recent_reads_the_stored_list_and_skips_junk() {
    // Recent_ReadsTheStoredListAndSkipsJunk, Recent_KeepsAtMostEightFromStoredText
    let mut state = MemoryState::default();
    state.set("color", "recent", "#FF0000FF,junk,#ff0000ff,#12,#80112233");
    let mut recent = RecentColors::default();
    assert_eq!(recent.colors(Some(&mut state)), [0xFF00_00FF, 0x8011_2233]);
    let many: Vec<String> = (1..=12).map(|i| format!("#FF0000{i:02X}")).collect();
    state.set("color", "recent", &many.join(","));
    assert_eq!(
        RecentColors::default().colors(Some(&mut state)).len(),
        RecentColors::CAPACITY
    );
}

#[test]
fn recent_rereads_after_a_character_switch() {
    // Recent_RereadsAfterACharacterSwitch
    let mut state = MemoryState::default();
    let mut recent = RecentColors::default();
    recent.add(0xFF11_1111, Some(&mut state));
    state.set("color", "recent", "#FF222222");
    state.generation += 1;
    assert_eq!(recent.colors(Some(&mut state)), [0xFF22_2222]);
    recent.add(0xFF33_3333, Some(&mut state));
    assert_eq!(
        stored(&state).map(String::as_str),
        Some("#FF333333,#FF222222")
    );
}

#[test]
fn two_lists_over_one_state_share_their_picks() {
    // Recent_TwoListsOverOneStateShareTheirPicks,
    // Recent_AddWithoutReadingFirstKeepsTheOtherListsPicks
    let mut state = MemoryState::default();
    let mut a = RecentColors::default();
    let mut b = RecentColors::default();
    assert!(a.colors(Some(&mut state)).is_empty());
    assert!(b.colors(Some(&mut state)).is_empty());
    a.add(0xFF11_1111, Some(&mut state));
    assert_eq!(b.colors(Some(&mut state)), [0xFF11_1111]);
    b.add(0xFF22_2222, Some(&mut state));
    assert_eq!(a.colors(Some(&mut state)), [0xFF22_2222, 0xFF11_1111]);
    assert_eq!(
        stored(&state).map(String::as_str),
        Some("#FF222222,#FF111111")
    );
}
