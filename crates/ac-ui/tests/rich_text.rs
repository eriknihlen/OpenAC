//! Authored rich text: coloured segments word-wrapped into lines.
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/DatRichTextTests.cs`
//! (no font: 8 pixels a character, as OpenAC's fallback measure).

mod common;

use ac_ui::draw::Color;
use ac_ui::pregame::rich_text::{Segment, compose, palette_color};
use ac_ui::root::UiRoot;
use ac_ui::tree::ElementId;
use ac_ui::widgets::Text;
use common::{at, root};

const WHITE: Color = Color::WHITE;
const GREEN: Color = Color::rgba(0.0, 1.0, 0.0, 1.0);

fn target(width: f32, edit: impl FnOnce(&mut Text)) -> (UiRoot, ElementId) {
    let mut ui = root(1200.0, 800.0);
    let mut text = Text::default();
    edit(&mut text);
    let parent = ui.root();
    let id = ui.add(parent, at(0.0, 0.0, width, 200.0), text);
    (ui, id)
}

fn texts(ui: &UiRoot, id: ElementId, segments: &[Segment<'_>]) -> Vec<String> {
    compose(ui, id, segments)
        .into_iter()
        .map(|l| l.text)
        .collect()
}

#[test]
fn real_newlines_split_and_literal_pairs_stay_verbatim() {
    let (ui, id) = target(1000.0, |_| {});
    assert_eq!(
        texts(&ui, id, &[(Some("line one\nliteral \\n stays"), WHITE)]),
        ["line one", "literal \\n stays"]
    );
}

#[test]
fn a_long_segment_wraps_to_the_width() {
    let (ui, id) = target(80.0, |_| {});
    let lines = texts(
        &ui,
        id,
        &[(Some("one two three four five six seven eight"), WHITE)],
    );
    assert!(lines.len() > 1);
    for line in &lines {
        assert!(
            line.chars().count() as f32 * 8.0 <= 80.0,
            "{line} overflowed"
        );
    }
}

#[test]
fn each_segment_keeps_its_own_colour() {
    let (ui, id) = target(1000.0, |_| {});
    let lines = compose(
        &ui,
        id,
        &[(Some("Header:"), GREEN), (Some("Body text."), WHITE)],
    );
    assert_eq!(lines.len(), 2);
    assert_eq!(lines[0].color, GREEN);
    assert_eq!(lines[1].color, WHITE);
}

#[test]
fn absent_or_empty_segments_are_skipped() {
    let (ui, id) = target(1000.0, |_| {});
    assert_eq!(
        texts(
            &ui,
            id,
            &[(None, WHITE), (Some(""), WHITE), (Some("real text"), WHITE)]
        ),
        ["real text"]
    );
}

#[test]
fn no_separator_goes_between_segments() {
    let (ui, id) = target(1000.0, |_| {});
    assert_eq!(
        texts(&ui, id, &[(Some("first"), WHITE), (Some("second"), WHITE)]),
        ["first", "second"]
    );
}

#[test]
fn the_wrap_width_is_less_the_margins() {
    let margins = |t: &mut Text| {
        t.margin_left = 10.0;
        t.margin_right = 10.0;
    };
    let (ui, id) = target(100.0, margins);
    assert_eq!(texts(&ui, id, &[(Some("aaaaaaaaaa"), WHITE)]).len(), 1);
    let (ui, id) = target(100.0, margins);
    assert!(
        texts(&ui, id, &[(Some("aaaaaaaaaaa"), WHITE)]).len() > 1,
        "an 88 px word in an 80 px content width wraps"
    );
}

#[test]
fn the_palette_colour_is_the_authored_entry() {
    let (ui, id) = target(100.0, |t| t.font_color_palette = vec![WHITE, GREEN]);
    assert_eq!(palette_color(&ui, id, 0, GREEN), WHITE);
    assert_eq!(palette_color(&ui, id, 1, WHITE), GREEN);
}

#[test]
fn the_palette_colour_falls_back_past_the_palette() {
    let (ui, id) = target(100.0, |_| {});
    assert_eq!(palette_color(&ui, id, 1, GREEN), GREEN);
}
