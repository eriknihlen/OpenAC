//! Text layout: word wrap, justification, caret and selection arithmetic,
//! and the vertical scroll model. Pure functions; the text widget uses them.
//!
//! Columns count characters (`char`s). The client counts UTF-16 units; the
//! two agree for every character a dat font has a glyph for.

use crate::draw::Color;

/// Horizontal justification.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum HJustify {
    /// Flush left.
    #[default]
    Left,
    /// Centred.
    Center,
    /// Flush right.
    Right,
}

/// Vertical justification.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum VJustify {
    /// Flush top.
    #[default]
    Top,
    /// Centred.
    Center,
    /// Flush bottom.
    Bottom,
}

/// One shown line and its colour.
#[derive(Debug, Clone, PartialEq)]
pub struct Line {
    /// Text.
    pub text: String,
    /// Colour.
    pub color: Color,
}

impl Line {
    /// A line.
    #[must_use]
    pub fn new(text: impl Into<String>, color: Color) -> Self {
        Self {
            text: text.into(),
            color,
        }
    }
}

/// A coloured piece of one line.
#[derive(Debug, Clone, PartialEq)]
pub struct TextRun {
    /// Text.
    pub text: String,
    /// Colour.
    pub color: Color,
}

impl TextRun {
    /// A run.
    #[must_use]
    pub fn new(text: impl Into<String>, color: Color) -> Self {
        Self {
            text: text.into(),
            color,
        }
    }
}

/// A line and column (character index) in the shown lines.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default, PartialOrd, Ord)]
pub struct Pos {
    /// Line index.
    pub line: usize,
    /// Character index within the line.
    pub col: usize,
}

impl Pos {
    /// A position.
    #[must_use]
    pub const fn new(line: usize, col: usize) -> Self {
        Self { line, col }
    }
}

/// Stable identity of a shown line: the source item that produced it and
/// the character offset in that item's text where the line begins.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct LineKey {
    /// Source item id.
    pub source: i64,
    /// Offset of the line's first character in the source text.
    pub start: usize,
}

/// Number of characters in `text`.
#[must_use]
pub fn char_len(text: &str) -> usize {
    text.chars().count()
}

/// The characters `from..to` of `text` (character indices, clamped).
#[must_use]
pub fn char_slice(text: &str, from: usize, to: usize) -> &str {
    let start = text.char_indices().nth(from).map_or(text.len(), |(i, _)| i);
    let end = text.char_indices().nth(to).map_or(text.len(), |(i, _)| i);
    if end <= start { "" } else { &text[start..end] }
}

/// Place runs left to right from `start_x`; empty runs are dropped but
/// still advance nothing.
#[must_use]
pub fn layout_runs(
    runs: &[TextRun],
    start_x: f32,
    measure: impl Fn(&str) -> f32,
) -> Vec<(String, f32, Color)> {
    let mut placed = Vec::with_capacity(runs.len());
    let mut pen = start_x;
    for run in runs {
        if !run.text.is_empty() {
            placed.push((run.text.clone(), pen, run.color));
        }
        pen += measure(&run.text);
    }
    placed
}

/// True when the runs, joined in order, are exactly `line`.
#[must_use]
pub fn runs_match_line(runs: &[TextRun], line: &str) -> bool {
    let mut rest = line;
    for run in runs {
        match rest.strip_prefix(run.text.as_str()) {
            Some(r) => rest = r,
            None => return false,
        }
    }
    rest.is_empty()
}

/// Word-wrap `text` to `maximum_width` measured pixels. Explicit newlines
/// start new lines (`\r` is dropped); words split on single spaces; a word
/// wider than the line is split at character boundaries.
///
/// # Panics
/// When `maximum_width` is not positive (a caller bug).
#[must_use]
pub fn wrap_words(text: &str, measure: impl Fn(&str) -> f32, maximum_width: f32) -> Vec<String> {
    assert!(maximum_width > 0.0, "wrap width must be positive");
    let mut result = Vec::new();
    let cleaned = text.replace('\r', "");
    for paragraph in cleaned.split('\n') {
        if paragraph.is_empty() {
            result.push(String::new());
            continue;
        }
        let mut line = String::new();
        for word in paragraph.split(' ').filter(|w| !w.is_empty()) {
            let candidate = if line.is_empty() {
                word.to_owned()
            } else {
                format!("{line} {word}")
            };
            if measure(&candidate) <= maximum_width {
                line = candidate;
                continue;
            }
            if !line.is_empty() && measure(word) <= maximum_width {
                result.push(std::mem::take(&mut line));
                line.push_str(word);
                continue;
            }
            for (i, c) in word.chars().enumerate() {
                let mut prefix = if i == 0 && !line.is_empty() { " " } else { "" };
                if !line.is_empty() {
                    let probe = format!("{line}{prefix}{c}");
                    if measure(&probe) > maximum_width {
                        result.push(std::mem::take(&mut line));
                        prefix = "";
                    }
                }
                line.push_str(prefix);
                line.push(c);
            }
        }
        result.push(line);
    }
    result
}

/// The column whose glyph midpoint is past `x`: 0 at or before the start,
/// the length past the end.
#[must_use]
pub fn char_index_at(text: &str, advance_of: impl Fn(char) -> f32, x: f32) -> usize {
    if text.is_empty() || x <= 0.0 {
        return 0;
    }
    let mut cursor = 0.0;
    for (i, c) in text.chars().enumerate() {
        let adv = advance_of(c);
        let mid = cursor + adv * 0.5;
        if x < mid {
            return i;
        }
        cursor += adv;
    }
    char_len(text)
}

/// The two positions, earlier first.
#[must_use]
pub fn order(a: Pos, b: Pos) -> (Pos, Pos) {
    if a <= b { (a, b) } else { (b, a) }
}

/// The text from `start` (inclusive) to `end` (exclusive) across `lines`,
/// lines joined by `\n`.
#[must_use]
pub fn selected_text(lines: &[Line], start: Pos, end: Pos) -> String {
    if lines.is_empty() {
        return String::new();
    }
    let (start, end) = order(start, end);
    let last = lines.len() - 1;
    let sl = start.line.min(last);
    let el = end.line.min(last);
    if sl == el {
        let t = &lines[sl].text;
        let len = char_len(t);
        let (c0, c1) = (start.col.min(len), end.col.min(len));
        return if c1 <= c0 {
            String::new()
        } else {
            char_slice(t, c0, c1).to_owned()
        };
    }
    let mut out = String::new();
    let first = &lines[sl].text;
    out.push_str(char_slice(
        first,
        start.col.min(char_len(first)),
        usize::MAX,
    ));
    for line in &lines[sl + 1..el] {
        out.push('\n');
        out.push_str(&line.text);
    }
    out.push('\n');
    let tail = &lines[el].text;
    out.push_str(char_slice(tail, 0, end.col.min(char_len(tail))));
    out
}

/// Where the character at `offset` of source `source` is shown now: `None`
/// when that source has no shown line. An offset before a line of the
/// source lands at that line's start; past the source's last shown text, at
/// its end.
#[must_use]
pub fn resolve_anchor(keys: &[LineKey], lines: &[Line], source: i64, offset: usize) -> Option<Pos> {
    let count = keys.len().min(lines.len());
    let mut last_of_source = None;
    for i in 0..count {
        if keys[i].source != source {
            continue;
        }
        last_of_source = Some(i);
        let start = keys[i].start;
        if offset < start {
            return Some(Pos::new(i, 0));
        }
        if offset <= start + char_len(&lines[i].text) {
            return Some(Pos::new(i, offset - start));
        }
    }
    last_of_source.map(|i| Pos::new(i, char_len(&lines[i].text)))
}

/// Scroll clamped to `0..=content - view`.
#[must_use]
pub fn clamp_scroll(scroll: f32, content_height: f32, view_height: f32) -> f32 {
    let max = (content_height - view_height).max(0.0);
    if scroll < 0.0 {
        0.0
    } else if scroll > max {
        max
    } else {
        scroll
    }
}

/// Top of a single line in an element of `height` under `vj`. Centring
/// ignores the padding.
#[must_use]
pub fn v_offset(height: f32, line_height: f32, padding: f32, vj: VJustify) -> f32 {
    match vj {
        VJustify::Top => padding,
        VJustify::Bottom => height - line_height - padding,
        VJustify::Center => (height - line_height) * 0.5,
    }
}

/// Top of the first line of a multi-line block between `top` and `bottom`.
/// Content taller than the view (or text that does not honour
/// justification) is pinned to the bottom and moved by the scroll.
#[must_use]
pub fn content_base_y(
    top: f32,
    bottom: f32,
    content_height: f32,
    max_scroll: f32,
    scroll_y: f32,
    justification: VJustify,
    honor_justification: bool,
) -> f32 {
    let view = (bottom - top).max(0.0);
    if !honor_justification || content_height > view {
        return bottom - content_height + (max_scroll - scroll_y);
    }
    match justification {
        VJustify::Top => top,
        VJustify::Bottom => bottom - content_height,
        VJustify::Center => top + (view - content_height) * 0.5,
    }
}

/// Left of a one-line text of `line_width`: the game justifies it inside
/// the box less both text margins and starts it at the left margin (a
/// left-justified line also after the padding; a right-justified one ends
/// the padding short of the right margin). With no margins this is
/// `padding`, the centre, or `width - line_width - padding`. Nothing is
/// clamped: a line wider than the box runs past it and is clipped there.
#[must_use]
pub fn one_line_offset_x(
    element_width: f32,
    padding: f32,
    margin_left: f32,
    margin_right: f32,
    line_width: f32,
    centered: bool,
    right_aligned: bool,
) -> f32 {
    let inner = element_width - margin_left - margin_right;
    if centered {
        margin_left + (inner - line_width) * 0.5
    } else if right_aligned {
        margin_left + inner - line_width - padding
    } else {
        margin_left + padding
    }
}

/// Left of a line of `line_width` inside the element's padding and margins.
#[must_use]
pub fn content_offset_x(
    element_width: f32,
    padding: f32,
    margin_left: f32,
    margin_right: f32,
    line_width: f32,
    centered: bool,
    right_aligned: bool,
) -> f32 {
    let left = padding + margin_left;
    let right = element_width - padding - margin_right;
    if centered {
        left.max(left + (right - left - line_width) * 0.5)
    } else if right_aligned {
        left.max(right - line_width)
    } else {
        left
    }
}

/// True when any part of the line is inside the viewport rows.
#[must_use]
pub fn line_intersects_viewport(line_top: f32, line_height: f32, top: f32, bottom: f32) -> bool {
    line_height > 0.0 && line_top < bottom && line_top + line_height > top
}

/// Vertical scroll state of a text or list: content and view heights in
/// whole pixels, the scroll offset, and the line step.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Scrollable {
    /// Height of everything, pixels.
    pub content_height: i32,
    /// Height shown, pixels.
    pub view_height: i32,
    /// One line, pixels.
    pub line_height: i32,
    scroll_y: i32,
}

impl Default for Scrollable {
    fn default() -> Self {
        Self {
            content_height: 0,
            view_height: 0,
            line_height: 16,
            scroll_y: 0,
        }
    }
}

impl Scrollable {
    /// Scroll offset from the top, pixels.
    #[must_use]
    pub fn scroll_y(&self) -> i32 {
        self.scroll_y
    }

    /// Largest scroll offset.
    #[must_use]
    pub fn max_scroll(&self) -> i32 {
        (self.content_height - self.view_height).max(0)
    }

    /// True when the content is taller than the view.
    #[must_use]
    pub fn has_overflow(&self) -> bool {
        self.content_height > self.view_height
    }

    /// True when scrolled to the end.
    #[must_use]
    pub fn at_end(&self) -> bool {
        self.scroll_y >= self.max_scroll()
    }

    /// New heights; with `preserve_end` a view that was at the end stays
    /// there (new lines scroll in), otherwise the offset is re-clamped.
    pub fn set_extents(&mut self, content_height: i32, view_height: i32, preserve_end: bool) {
        let was_at_end = self.at_end();
        self.content_height = content_height.max(0);
        self.view_height = view_height.max(0);
        if preserve_end && was_at_end {
            self.scroll_to_end();
        } else {
            self.set_scroll_y(self.scroll_y);
        }
    }

    /// Scroll to `y`, clamped.
    pub fn set_scroll_y(&mut self, y: i32) {
        self.scroll_y = y.clamp(0, self.max_scroll());
    }

    /// Scroll to the end.
    pub fn scroll_to_end(&mut self) {
        self.scroll_y = self.max_scroll();
    }

    /// The view's share of the content, 0..=1 (1 when empty).
    #[must_use]
    pub fn thumb_ratio(&self) -> f32 {
        if self.content_height <= 0 {
            1.0
        } else {
            (self.view_height as f32 / self.content_height as f32).min(1.0)
        }
    }

    /// The offset as a share of the maximum, 0..=1.
    #[must_use]
    pub fn position_ratio(&self) -> f32 {
        let max = self.max_scroll();
        if max <= 0 {
            0.0
        } else {
            self.scroll_y as f32 / max as f32
        }
    }

    /// Scroll to a share of the maximum (rounded to a pixel).
    pub fn set_position_ratio(&mut self, ratio: f32) {
        let y = (ratio.clamp(0.0, 1.0) * self.max_scroll() as f32).round_ties_even() as i32;
        self.set_scroll_y(y);
    }

    /// Scroll by whole lines (positive scrolls toward the end).
    pub fn scroll_by_lines(&mut self, lines: i32) {
        self.set_scroll_y(self.scroll_y + lines * self.line_height);
    }

    /// Scroll by whole views.
    pub fn scroll_by_page(&mut self, pages: i32) {
        self.set_scroll_y(self.scroll_y + pages * self.view_height);
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.App.Tests/UI/UiTextTests.cs`,
    //! `UiTextDatFontTests.cs` and `UiScrollableTests.cs`.

    use super::*;

    fn run(t: &str) -> TextRun {
        TextRun::new(t, Color::WHITE)
    }

    fn lines(texts: &[&str]) -> Vec<Line> {
        texts.iter().map(|t| Line::new(*t, Color::WHITE)).collect()
    }

    fn len10(t: &str) -> f32 {
        char_len(t) as f32 * 10.0
    }

    #[test]
    fn layout_runs_advances_the_pen() {
        let green = Color::rgba(0.0, 0.698, 0.0, 1.0);
        let placed = layout_runs(&[TextRun::new("Dww", green), run(" tells you")], 4.0, len10);
        assert_eq!(placed.len(), 2);
        assert_eq!((placed[0].1, placed[1].1), (4.0, 34.0));
        assert_eq!((placed[0].2, placed[1].2), (green, Color::WHITE));
    }

    #[test]
    fn layout_runs_drops_empty_runs_without_shifting() {
        let placed = layout_runs(&[run(""), run("ab"), run(""), run("cd")], 0.0, len10);
        assert_eq!(placed.len(), 2);
        assert_eq!((placed[0].1, placed[1].1), (0.0, 20.0));
    }

    #[test]
    fn runs_match_line_accepts_only_an_exact_split() {
        assert!(runs_match_line(
            &[run("Dww"), run(" tells you, \"hi\"")],
            "Dww tells you, \"hi\""
        ));
        assert!(!runs_match_line(
            &[run(" tells you, \"hi\""), run("Dww")],
            "Dww tells you, \"hi\""
        ));
        let line = "Dww tells you";
        assert!(!runs_match_line(&[run("Dww")], line));
        assert!(!runs_match_line(&[run(line), run("!")], line));
        assert!(!runs_match_line(&[run("Dwx"), run(" tells you")], line));
        assert!(!runs_match_line(&[], line));
        assert!(runs_match_line(&[], ""));
        assert!(runs_match_line(&[run("")], ""));
        assert!(runs_match_line(&[run("ab"), run(""), run("cd")], "abcd"));
    }

    #[test]
    fn wrap_words_uses_measured_width_and_keeps_paragraphs() {
        assert_eq!(
            wrap_words("one two three\nfour", len10, 75.0),
            ["one two", "three", "four"]
        );
        let unit = |t: &str| char_len(t) as f32;
        assert_eq!(
            wrap_words("one two three\nfour", unit, 7.0),
            ["one two", "three", "four"]
        );
    }

    #[test]
    fn wrap_words_splits_an_over_wide_word_at_characters() {
        let unit = |t: &str| char_len(t) as f32;
        assert_eq!(wrap_words("go abcdef", unit, 5.0), ["go ab", "cdef"]);
    }

    #[test]
    fn clamp_scroll_pins_and_caps() {
        assert_eq!(clamp_scroll(50.0, 80.0, 200.0), 0.0);
        assert_eq!(clamp_scroll(0.0, 80.0, 200.0), 0.0);
        assert_eq!(clamp_scroll(1000.0, 500.0, 200.0), 300.0);
        assert_eq!(clamp_scroll(120.0, 500.0, 200.0), 120.0);
        assert_eq!(clamp_scroll(-50.0, 500.0, 200.0), 0.0);
    }

    #[test]
    fn char_index_at_snaps_to_glyph_midpoints() {
        let mono = |_| 10.0;
        assert_eq!(char_index_at("hello", mono, 0.0), 0);
        assert_eq!(char_index_at("hello", mono, -5.0), 0);
        assert_eq!(char_index_at("hello", mono, 4.0), 0);
        assert_eq!(char_index_at("hello", mono, 6.0), 1);
        assert_eq!(char_index_at("hello", mono, 14.0), 1);
        assert_eq!(char_index_at("hello", mono, 16.0), 2);
        assert_eq!(char_index_at("hello", mono, 1000.0), 5);
        assert_eq!(char_index_at("", mono, 50.0), 0);
    }

    #[test]
    fn char_index_at_uses_the_dat_glyph_advance() {
        // Every glyph 2 before + 6 wide + 2 after = 10.
        let g = crate::font::test_fonts::glyph('x', (0, 0), (6, 12), 2, 2, 0);
        assert_eq!(crate::font::glyph_advance(&g), 10.0);
        let adv = |_| crate::font::glyph_advance(&g);
        assert_eq!(char_index_at("abc", adv, 4.0), 0);
        assert_eq!(char_index_at("abc", adv, 12.0), 1);
        assert_eq!(char_index_at("abc", adv, 100.0), 3);
    }

    fn scrollable(content: i32, view: i32) -> Scrollable {
        Scrollable {
            content_height: content,
            view_height: view,
            ..Scrollable::default()
        }
    }

    #[test]
    fn scroll_is_clamped_to_the_content() {
        let mut s = scrollable(300, 100);
        s.set_scroll_y(500);
        assert_eq!(s.scroll_y(), 200);
        s.set_scroll_y(-50);
        assert_eq!(s.scroll_y(), 0);
        let mut s = scrollable(80, 100);
        s.set_scroll_y(40);
        assert_eq!(s.scroll_y(), 0);
        assert!(!s.has_overflow());
    }

    #[test]
    fn thumb_and_position_ratios() {
        assert_eq!(scrollable(400, 100).thumb_ratio(), 0.25);
        assert_eq!(scrollable(50, 100).thumb_ratio(), 1.0);
        let mut s = scrollable(300, 100);
        s.set_scroll_y(100);
        assert_eq!(s.position_ratio(), 0.5);
        s.set_scroll_y(200);
        assert_eq!(s.position_ratio(), 1.0);
        let mut s = scrollable(300, 100);
        s.set_position_ratio(0.5);
        assert_eq!(s.scroll_y(), 100);
    }

    #[test]
    fn scroll_by_lines_and_pages() {
        let mut s = scrollable(1000, 100);
        s.scroll_by_lines(-2);
        assert_eq!(s.scroll_y(), 0);
        s.set_scroll_y(50);
        s.scroll_by_lines(2);
        assert_eq!(s.scroll_y(), 82);
        s.set_scroll_y(200);
        s.scroll_by_page(1);
        assert_eq!(s.scroll_y(), 300);
    }

    #[test]
    fn set_extents_preserves_the_end_but_keeps_user_scrollback() {
        let mut s = Scrollable::default();
        s.set_extents(400, 100, true);
        assert_eq!(s.scroll_y(), 300);
        s.scroll_by_lines(-1);
        assert_eq!(s.scroll_y(), 284);
        s.set_extents(400, 100, true);
        assert_eq!(s.scroll_y(), 284);
        s.scroll_to_end();
        s.set_extents(500, 100, true);
        assert_eq!(s.scroll_y(), 400);
    }

    #[test]
    fn selected_text_assembles_spans() {
        let l = lines(&["hello world"]);
        assert_eq!(selected_text(&l, Pos::new(0, 6), Pos::new(0, 11)), "world");
        assert_eq!(selected_text(&l, Pos::new(0, 11), Pos::new(0, 6)), "world");
        assert_eq!(
            selected_text(&lines(&["hello"]), Pos::new(0, 3), Pos::new(0, 3)),
            ""
        );
        let l = lines(&["first line", "second line", "third line"]);
        assert_eq!(
            selected_text(&l, Pos::new(0, 6), Pos::new(2, 5)),
            "line\nsecond line\nthird"
        );
        let l = lines(&["alpha", "bravo"]);
        assert_eq!(
            selected_text(&l, Pos::new(0, 2), Pos::new(1, 3)),
            "pha\nbra"
        );
        assert_eq!(
            selected_text(&l, Pos::new(1, 3), Pos::new(0, 2)),
            "pha\nbra"
        );
        assert_eq!(selected_text(&[], Pos::new(0, 0), Pos::new(0, 0)), "");
    }

    #[test]
    fn order_sorts_by_line_then_column() {
        assert_eq!(
            order(Pos::new(2, 1), Pos::new(0, 5)),
            (Pos::new(0, 5), Pos::new(2, 1))
        );
        assert_eq!(
            order(Pos::new(1, 8), Pos::new(1, 2)),
            (Pos::new(1, 2), Pos::new(1, 8))
        );
    }

    #[test]
    fn v_offset_by_justification() {
        assert_eq!(v_offset(55.0, 12.0, 0.0, VJustify::Center), 21.5);
        assert_eq!(v_offset(55.0, 12.0, 0.0, VJustify::Top), 0.0);
        assert_eq!(v_offset(55.0, 12.0, 4.0, VJustify::Top), 4.0);
        assert_eq!(v_offset(55.0, 12.0, 2.0, VJustify::Bottom), 41.0);
        assert_eq!(
            v_offset(40.0, 12.0, 4.0, VJustify::Center),
            v_offset(40.0, 12.0, 0.0, VJustify::Center)
        );
    }

    #[test]
    fn content_base_y_follows_retail_justification_when_it_fits() {
        for (vj, expected) in [
            (VJustify::Top, 0.0),
            (VJustify::Center, 4.5),
            (VJustify::Bottom, 9.0),
        ] {
            assert_eq!(
                content_base_y(0.0, 25.0, 16.0, 0.0, 0.0, vj, true),
                expected
            );
        }
        assert_eq!(
            content_base_y(0.0, 25.0, 64.0, 39.0, 12.0, VJustify::Center, true),
            -12.0
        );
        assert_eq!(
            content_base_y(0.0, 25.0, 16.0, 0.0, 0.0, VJustify::Center, false),
            9.0
        );
    }

    #[test]
    fn one_line_offset_x_justifies_inside_the_text_margins() {
        // The game's text justification: the left
        // margin plus the offset inside the box less both margins. With no
        // margins it is OpenAC's one-line placement.
        assert_eq!(
            one_line_offset_x(90.0, 0.0, 0.0, 0.0, 95.0, false, false),
            0.0
        );
        assert_eq!(
            one_line_offset_x(100.0, 0.0, 0.0, 0.0, 40.0, true, false),
            30.0
        );
        assert_eq!(
            one_line_offset_x(100.0, 2.0, 0.0, 0.0, 40.0, false, true),
            58.0
        );
        // The journal page list's title column: 90 wide, margins 6 and 6;
        // a title wider than the column starts at 6 and is clipped at 90.
        assert_eq!(
            one_line_offset_x(90.0, 0.0, 6.0, 6.0, 95.0, false, false),
            6.0
        );
        assert_eq!(
            one_line_offset_x(100.0, 0.0, 6.0, 2.0, 40.0, true, false),
            32.0
        );
        assert_eq!(
            one_line_offset_x(100.0, 0.0, 6.0, 2.0, 40.0, false, true),
            58.0
        );
    }

    #[test]
    fn content_offset_x_honours_padding_and_margins() {
        assert_eq!(
            content_offset_x(200.0, 4.0, 0.0, 0.0, 30.0, false, false),
            4.0
        );
        assert_eq!(
            content_offset_x(200.0, 4.0, 0.0, 0.0, 30.0, true, false),
            85.0
        );
        assert_eq!(
            content_offset_x(200.0, 4.0, 0.0, 0.0, 30.0, false, true),
            166.0
        );
        assert_eq!(
            content_offset_x(265.0, 0.0, 9.0, 26.0, 100.0, false, false),
            9.0
        );
        assert_eq!(
            content_offset_x(265.0, 0.0, 9.0, 26.0, 50.0, false, true),
            189.0
        );
    }

    #[test]
    fn line_intersects_viewport_keeps_partial_lines() {
        assert!(line_intersects_viewport(0.0, 16.0, 0.0, 15.0));
        assert!(line_intersects_viewport(-1.0, 16.0, 0.0, 15.0));
        assert!(!line_intersects_viewport(-16.0, 16.0, 0.0, 15.0));
        assert!(!line_intersects_viewport(15.0, 16.0, 0.0, 15.0));
    }

    #[test]
    fn char_slices_count_characters_not_bytes() {
        assert_eq!(char_slice("\u{e9}t\u{e9}", 1, 3), "t\u{e9}");
        assert_eq!(char_slice("abc", 2, 99), "c");
        assert_eq!(char_slice("abc", 3, 1), "");
    }
}
