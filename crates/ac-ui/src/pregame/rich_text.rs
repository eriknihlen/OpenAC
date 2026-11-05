//! Authored description text built from coloured segments: each segment
//! word-wrapped to the text's inner width, its lines in the segment's
//! colour.

use crate::draw::Color;
use crate::root::UiRoot;
use crate::text::{Line, wrap_words};
use crate::tree::ElementId;
use crate::widgets::Text;

/// One coloured piece (an absent or empty text adds nothing).
pub type Segment<'a> = (Option<&'a str>, Color);

/// The lines `segments` make in text element `text`: the width less the
/// padding and margins on both sides (at least 1), the text's font
/// measuring (8 pixels a character without one).
#[must_use]
pub fn compose(ui: &UiRoot, text: ElementId, segments: &[Segment<'_>]) -> Vec<Line> {
    let width = ui.element(text).map_or(0.0, |e| e.width);
    let Some(t) = ui.widget::<Text>(text) else {
        return Vec::new();
    };
    let maximum = (width - (t.padding + t.margin_left) - (t.padding + t.margin_right)).max(1.0);
    let font = t.font.clone();
    let mut lines = Vec::new();
    for (segment, color) in segments {
        let Some(segment) = segment.filter(|s| !s.is_empty()) else {
            continue;
        };
        let wrapped = match &font {
            Some(f) => wrap_words(segment, |s| f.measure_width(s), maximum),
            None => wrap_words(segment, |s| s.chars().count() as f32 * 8.0, maximum),
        };
        lines.extend(wrapped.into_iter().map(|l| Line::new(l, *color)));
    }
    lines
}

/// Colour `index` of the text's authored font palette, else `fallback`.
#[must_use]
pub fn palette_color(ui: &UiRoot, text: ElementId, index: usize, fallback: Color) -> Color {
    ui.widget::<Text>(text)
        .and_then(|t| t.font_color_palette.get(index).copied())
        .unwrap_or(fallback)
}

/// Show `content` as the text's one line in its default colour.
pub fn set_line(ui: &mut UiRoot, text: ElementId, content: &str) {
    if let Some(t) = ui.widget_mut::<Text>(text) {
        let color = t.default_color;
        t.set_lines(vec![Line::new(content, color)]);
    }
}

/// Show `lines` in the text.
pub fn set_lines(ui: &mut UiRoot, text: ElementId, lines: Vec<Line>) {
    if let Some(t) = ui.widget_mut::<Text>(text) {
        t.set_lines(lines);
    }
}

/// The text's lines joined with spaces (tests and probes).
#[must_use]
pub fn joined(ui: &UiRoot, text: ElementId) -> String {
    ui.widget::<Text>(text).map_or_else(String::new, |t| {
        t.content_lines()
            .iter()
            .map(|l| l.text.clone())
            .collect::<Vec<_>>()
            .join(" ")
    })
}
