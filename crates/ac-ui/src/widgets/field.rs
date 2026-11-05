//! The text entry field: caret, selection, clipboard, a history of sent
//! lines, a one-line view that scrolls sideways to the caret, or wrapped
//! lines that scroll down.
//!
//! Clipboard traffic goes through outputs: copy and cut emit
//! [`UiOutput::SetClipboard`]; Ctrl+V emits [`UiOutput::PasteRequest`] and
//! the client answers with [`UiRoot::paste`]. Key repeat comes from the
//! window (a held key arrives as repeated key-downs), as it does for retail.

use std::sync::Arc;

use crate::draw::{Color, DEFAULT_OUTLINE_COLOR, RenderContext, TextureRef};
use crate::event::{Key, Modifiers, UiEvent, kind};
use crate::font::UiFont;
use crate::plugin::Palette;
use crate::plugin::style;
use crate::root::{ElementMessage, RecallRequest, UiOutput, UiRoot};
use crate::text::Scrollable;
use crate::tree::{Element, ElementId, Widget};

/// Decides whether a typed or pasted character is accepted.
pub type CharFilter = Box<dyn Fn(char) -> bool>;

/// Offered the whole text when a space is typed at its end; a replacement
/// rewrites the line (typed abbreviations such as `/r `).
pub type TextReplacer = Box<dyn FnMut(&str) -> Option<String>>;

/// How many sent lines the field remembers.
pub const HISTORY_CAPACITY: usize = 100;

/// Fallback glyph width when the field has no font (wrapping only).
const FALLBACK_GLYPH_WIDTH: f32 = 8.0;

/// Line height when the field has no font.
const FALLBACK_LINE_HEIGHT: f32 = 14.0;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct WrappedLine {
    start: usize,
    length: usize,
}

/// A text entry field.
pub struct Field {
    /// Dat element id (0 for synthesized fields).
    pub element_id: u32,
    /// The font (nothing draws without one).
    pub font: Option<Arc<UiFont>>,
    /// Text colour.
    pub text_color: Color,
    /// Outlined glyphs.
    pub outline: bool,
    /// Outline colour.
    pub outline_color: Color,
    /// Fill when no image is drawn.
    pub background_color: Color,
    /// Selected-span highlight.
    pub selection_color: Color,
    /// Inset on every side, pixels.
    pub padding: f32,
    /// Most characters the field holds.
    pub max_characters: usize,
    /// One line (Enter submits) or several (Enter breaks the line).
    pub one_line: bool,
    /// Mouse and Shift selection, Ctrl+A/C/X.
    pub selectable: bool,
    /// Short text centred.
    pub centered: bool,
    /// Short text right-aligned.
    pub right_aligned: bool,
    /// Submitting empties the field.
    pub clear_on_submit: bool,
    /// Submitted lines go into the field's history.
    pub record_history: bool,
    /// Accepts or refuses characters.
    pub character_filter: Option<CharFilter>,
    /// Gaining focus selects everything (typing replaces the value).
    pub select_all_on_focus: bool,
    /// The vertical scroll model of a multi-line field.
    pub scroll: Scrollable,
    /// Keep keyboard focus after Enter submits.
    pub stay_focused_after_submit: bool,
    /// Losing keyboard focus sends [`ElementMessage::FocusLost`] with the
    /// text (a plugin field's `onblur`).
    pub reports_focus_loss: bool,
    /// Something receives the submitted line (a plugin field's
    /// `onsubmit`): the focus loss an Enter submit with text causes is then
    /// not reported, as that text was just handed over.
    pub has_submit_handler: bool,
    /// The unfocused image.
    pub background_sprite: u32,
    /// The plugin theme the box draws in (none: a flat fill).
    pub theme_palette: Option<Palette>,
    /// The focused left rail image.
    pub focus_rail_left_sprite: u32,
    /// Its width, pixels.
    pub focus_rail_left_width: f32,
    /// The focused right rail image.
    pub focus_rail_right_sprite: u32,
    /// Its width, pixels.
    pub focus_rail_right_width: f32,
    /// The focused image.
    pub focus_field_sprite: u32,
    /// Typed-abbreviation expansion.
    pub text_replacer: Option<TextReplacer>,
    /// The lines sent before live with an owner elsewhere (OpenAC
    /// `UiField.SharedHistory`: the chat entry every front end shares). Up
    /// and Down ask it for a line ([`ElementMessage::Recall`]) and an edit
    /// that ends the walk tells it so; the line it recalls comes back
    /// through [`Self::show_text`].
    pub shared_history: bool,

    editable: bool,
    text: Vec<char>,
    caret: usize,
    sel_anchor: Option<usize>,
    history: Vec<String>,
    history_index: Option<usize>,
    /// Requests for the shared history not yet sent.
    recall_requests: Vec<RecallRequest>,
    /// A shared walk was asked for and not stopped since.
    shared_recall_started: bool,
    focused: bool,
    selecting: bool,
    preserve_focus_selection_on_mouse_down: bool,
    scroll_x: f32,
    wrapped: Vec<WrappedLine>,
    wrapped_line_height: f32,
    text_version: u64,
    wrapped_version: Option<u64>,
    wrapped_width: f32,
    suppress_next_newline_char: bool,
    last_bound_text: Option<String>,
    /// An Enter submit that handed the text over (or cleared it) is under
    /// way, including the focus loss that ends it.
    losing_focus_to_submit: bool,
}

impl std::fmt::Debug for Field {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Field")
            .field("element_id", &self.element_id)
            .field("text", &self.text())
            .field("caret", &self.caret)
            .field("focused", &self.focused)
            .finish_non_exhaustive()
    }
}

impl Default for Field {
    fn default() -> Self {
        Self {
            element_id: 0,
            font: None,
            text_color: Color::WHITE,
            outline: false,
            outline_color: DEFAULT_OUTLINE_COLOR,
            background_color: Color::TRANSPARENT,
            selection_color: Color::rgba(0.25, 0.45, 0.85, 0.5),
            padding: 4.0,
            max_characters: 0xFFFF,
            one_line: true,
            selectable: false,
            centered: false,
            right_aligned: false,
            clear_on_submit: true,
            record_history: true,
            character_filter: None,
            select_all_on_focus: false,
            scroll: Scrollable::default(),
            stay_focused_after_submit: false,
            reports_focus_loss: false,
            has_submit_handler: false,
            background_sprite: 0,
            theme_palette: None,
            focus_rail_left_sprite: 0,
            focus_rail_left_width: 1.0,
            focus_rail_right_sprite: 0,
            focus_rail_right_width: 1.0,
            focus_field_sprite: 0,
            text_replacer: None,
            shared_history: false,
            editable: true,
            text: Vec::new(),
            caret: 0,
            sel_anchor: None,
            history: Vec::new(),
            history_index: None,
            recall_requests: Vec::new(),
            shared_recall_started: false,
            focused: false,
            selecting: false,
            preserve_focus_selection_on_mouse_down: false,
            scroll_x: 0.0,
            wrapped: Vec::new(),
            wrapped_line_height: FALLBACK_LINE_HEIGHT,
            text_version: 0,
            wrapped_version: None,
            wrapped_width: 0.0,
            suppress_next_newline_char: false,
            last_bound_text: None,
            losing_focus_to_submit: false,
        }
    }
}

impl Field {
    /// The element flags a field starts with: it takes focus and typed
    /// characters, and a drag inside it selects instead of moving the
    /// window.
    pub fn configure(element: &mut Element) {
        element.accepts_focus = true;
        element.is_edit_control = true;
        element.captures_pointer_drag = true;
        element.click_through = false;
    }

    // ── State ───────────────────────────────────────────────────────────

    /// The text.
    #[must_use]
    pub fn text(&self) -> String {
        self.text.iter().collect()
    }

    /// The caret, a character index.
    #[must_use]
    pub fn caret_pos(&self) -> usize {
        self.caret
    }

    /// True while the field has keyboard focus.
    #[must_use]
    pub fn is_focused(&self) -> bool {
        self.focused
    }

    /// True when the text can be changed.
    #[must_use]
    pub fn editable(&self) -> bool {
        self.editable
    }

    /// Lines in the history.
    #[must_use]
    pub fn history_count(&self) -> usize {
        self.history.len()
    }

    /// Make the field editable (it takes focus and typing) or read-only
    /// (it gives up focus).
    pub fn set_editable(&mut self, ui: &mut UiRoot, me: ElementId, value: bool) {
        if self.editable == value {
            return;
        }
        self.editable = value;
        if let Some(e) = ui.element_mut(me) {
            e.accepts_focus = value;
            e.is_edit_control = value;
        }
        if !value && self.focused {
            self.give_up_focus(ui, me);
        }
    }

    /// [`Field::set_editable`] for a field not yet in a tree.
    pub fn set_editable_detached(&mut self, element: &mut Element, value: bool) {
        self.editable = value;
        element.accepts_focus = value;
        element.is_edit_control = value;
    }

    fn set_chars(&mut self, chars: Vec<char>) {
        if self.text != chars {
            self.text = chars;
            self.text_version += 1;
        }
    }

    // ── Editing ─────────────────────────────────────────────────────────

    /// Type one character at the caret (replacing a selection). A space
    /// typed at the end offers the line to the text replacer.
    pub fn insert_char(&mut self, c: char) {
        if !self.editable {
            return;
        }
        let c = if !self.one_line && (c == '\r' || c == '\n') {
            '\n'
        } else if (c as u32) < 0x20 || c == '\u{7F}' {
            return;
        } else {
            c
        };
        if let Some(filter) = &self.character_filter
            && !filter(c)
        {
            return;
        }
        self.delete_selection();
        if self.text.len() >= self.max_characters {
            return;
        }
        let mut chars = self.text.clone();
        chars.insert(self.caret, c);
        self.set_chars(chars);
        self.caret += 1;
        self.reset_recall();

        if c == ' ' && self.caret == self.text.len() {
            let current = self.text();
            if let Some(replace) = self.text_replacer.as_mut()
                && let Some(replacement) = replace(&current)
            {
                self.set_text(&replacement);
            }
        }
    }

    /// Delete the selection, else the character before the caret.
    pub fn backspace(&mut self) {
        if !self.editable || self.delete_selection() || self.caret == 0 {
            return;
        }
        let mut chars = self.text.clone();
        chars.remove(self.caret - 1);
        self.set_chars(chars);
        self.caret -= 1;
    }

    /// Delete the selection, else the character after the caret.
    pub fn delete_forward(&mut self) {
        if !self.editable || self.delete_selection() || self.caret >= self.text.len() {
            return;
        }
        let mut chars = self.text.clone();
        chars.remove(self.caret);
        self.set_chars(chars);
    }

    fn move_caret_to(&mut self, target: usize, shift: bool) {
        let target = target.min(self.text.len());
        if shift {
            self.sel_anchor.get_or_insert(self.caret);
        } else {
            self.sel_anchor = None;
        }
        self.caret = target;
        self.reset_recall();
    }

    /// Move the caret by `delta` characters (dropping any selection).
    pub fn move_caret(&mut self, delta: isize) {
        self.move_caret_by(delta, false);
    }

    fn move_caret_by(&mut self, delta: isize, shift: bool) {
        let target = self.caret.saturating_add_signed(delta);
        self.move_caret_to(target, shift);
    }

    // ── Selection ───────────────────────────────────────────────────────

    fn sel_span(&self) -> (usize, usize) {
        match self.sel_anchor {
            Some(a) if a != self.caret => (a.min(self.caret), a.max(self.caret)),
            _ => (self.caret, self.caret),
        }
    }

    fn has_selection(&self) -> bool {
        self.sel_anchor.is_some_and(|a| a != self.caret)
    }

    /// The selected text ("" without a selection).
    #[must_use]
    pub fn selected_text(&self) -> String {
        let (lo, hi) = self.sel_span();
        self.text[lo..hi].iter().collect()
    }

    fn delete_selection(&mut self) -> bool {
        if !self.has_selection() {
            self.sel_anchor = None;
            return false;
        }
        let (lo, hi) = self.sel_span();
        let mut chars = self.text.clone();
        chars.drain(lo..hi);
        self.set_chars(chars);
        self.caret = lo;
        self.sel_anchor = None;
        true
    }

    /// Select everything.
    pub fn select_all_text(&mut self) {
        if self.text.is_empty() {
            self.sel_anchor = None;
            return;
        }
        self.sel_anchor = Some(0);
        self.caret = self.text.len();
    }

    /// Replace the text (cut to the maximum), caret at the end, no
    /// selection, recall stopped.
    pub fn set_text(&mut self, text: &str) {
        let chars: Vec<char> = text.chars().take(self.max_characters).collect();
        self.set_chars(chars);
        self.caret = self.text.len();
        self.sel_anchor = None;
        self.reset_recall();
    }

    /// Show text that came from elsewhere (a recalled or composed line)
    /// without stopping a walk through the history.
    pub fn show_text(&mut self, line: &str) {
        self.set_chars(line.chars().collect());
        self.caret = self.text.len();
        self.sel_anchor = None;
    }

    /// Follow a value the owner keeps: while nobody is typing in the field,
    /// a value that changed behind it is shown.
    pub fn follow_bound_text(&mut self, bound: &str) {
        if self.focused || self.last_bound_text.as_deref() == Some(bound) {
            return;
        }
        self.last_bound_text = Some(bound.to_owned());
        if self.text() != bound {
            self.set_text(bound);
        }
    }

    fn cut_selection(&mut self, ui: &mut UiRoot) {
        if !self.editable || !self.has_selection() {
            return;
        }
        self.copy_selection(ui);
        self.delete_selection();
        self.reset_recall();
    }

    fn copy_selection(&self, ui: &mut UiRoot) {
        let s = self.selected_text();
        if !s.is_empty() {
            ui.emit(UiOutput::SetClipboard(s));
        }
    }

    /// Insert clipboard text at the caret: line breaks kept in a
    /// multi-line field (CR LF as one), control characters and refused
    /// characters dropped, cut to the room left.
    pub fn paste_text(&mut self, clip: &str) {
        if !self.editable || clip.is_empty() {
            return;
        }
        let mut accepted = Vec::new();
        let mut previous_cr = false;
        for ch in clip.chars() {
            if !self.one_line && (ch == '\r' || ch == '\n') {
                if ch == '\n' && previous_cr {
                    previous_cr = false;
                    continue;
                }
                accepted.push('\n');
                previous_cr = ch == '\r';
                continue;
            }
            previous_cr = false;
            if (ch as u32) >= 0x20
                && ch != '\u{7F}'
                && self.character_filter.as_ref().is_none_or(|f| f(ch))
            {
                accepted.push(ch);
            }
        }
        if accepted.is_empty() {
            return;
        }
        self.delete_selection();
        let room = self.max_characters.saturating_sub(self.text.len());
        if room == 0 {
            return;
        }
        accepted.truncate(room);
        let mut chars = self.text.clone();
        let n = accepted.len();
        chars.splice(self.caret..self.caret, accepted);
        self.set_chars(chars);
        self.caret += n;
        self.reset_recall();
    }

    /// Hand the field an event while it lives inside another widget (a
    /// menu's search band, OpenAC `UiMenu._searchField`): `owner` stands in
    /// for the field's own element (its focus, its paste requests) and
    /// `width` is the field's width. The owner reads
    /// [`Self::text_version`] to see the text change.
    pub(crate) fn handle_detached(
        &mut self,
        ui: &mut UiRoot,
        owner: ElementId,
        e: &UiEvent,
        width: f32,
    ) -> bool {
        self.handle(ui, owner, e, width)
    }

    /// Counts changes of the text.
    #[must_use]
    pub(crate) fn text_version(&self) -> u64 {
        self.text_version
    }

    /// Draw the field `w` by `h` at the context's origin (a field inside
    /// another widget).
    pub(crate) fn draw_detached(&mut self, w: f32, h: f32, rc: &mut RenderContext<'_>) {
        self.draw_background(w, h, rc);
        if self.one_line {
            self.draw_one_line(w, h, rc);
        } else {
            self.draw_multi_line(w, h, rc);
        }
    }

    /// The clipboard text the field asked for; tells the owner the text
    /// changed.
    pub fn paste(&mut self, ui: &mut UiRoot, me: ElementId, clip: &str) {
        let before = self.text_version;
        self.paste_text(clip);
        for request in self.take_recall_requests() {
            ui.send_message(me, ElementMessage::Recall(request));
        }
        if self.text_version != before {
            ui.send_message(me, ElementMessage::TextChanged(self.text()));
        }
    }

    // ── Submit and history ──────────────────────────────────────────────

    /// Submit the line: a line with something in it is returned (and
    /// remembered); the field empties either way when it clears on submit.
    pub fn submit(&mut self) -> Option<String> {
        if !self.editable {
            return None;
        }
        let t = self.text();
        if t.trim().is_empty() {
            if self.clear_on_submit {
                self.clear();
            }
            return None;
        }
        if self.record_history {
            self.push_history(t.clone());
        }
        if self.clear_on_submit {
            self.clear();
        }
        Some(t)
    }

    fn clear(&mut self) {
        self.set_chars(Vec::new());
        self.caret = 0;
        self.sel_anchor = None;
        self.reset_recall();
    }

    fn reset_recall(&mut self) {
        self.history_index = None;
        if self.shared_history && self.shared_recall_started {
            self.shared_recall_started = false;
            self.recall_requests.push(RecallRequest::Stop);
        }
    }

    fn request_recall(&mut self, request: RecallRequest) {
        self.shared_recall_started = true;
        self.recall_requests.push(request);
    }

    /// The requests for the shared history made since the last call (the
    /// field sends them itself while handling an event; an owner that
    /// changed the text directly takes them here).
    pub fn take_recall_requests(&mut self) -> Vec<RecallRequest> {
        std::mem::take(&mut self.recall_requests)
    }

    fn push_history(&mut self, t: String) {
        self.history.push(t);
        if self.history.len() > HISTORY_CAPACITY {
            self.history.remove(0);
        }
        self.history_index = None;
    }

    /// Recall the line sent before the one shown (the newest first).
    pub fn history_prev(&mut self) {
        if self.shared_history {
            self.request_recall(RecallRequest::Previous);
            return;
        }
        if self.history.is_empty() {
            return;
        }
        let i = match self.history_index {
            None => self.history.len() - 1,
            Some(i) => i.saturating_sub(1),
        };
        self.history_index = Some(i);
        let line = self.history[i].clone();
        self.show_text(&line);
    }

    /// Recall the line sent after the one shown; past the newest, empty.
    pub fn history_next(&mut self) {
        if self.shared_history {
            self.request_recall(RecallRequest::Next);
            return;
        }
        let Some(i) = self.history_index else {
            return;
        };
        let i = i + 1;
        if i >= self.history.len() {
            self.history_index = None;
            self.clear();
            return;
        }
        self.history_index = Some(i);
        let line = self.history[i].clone();
        self.show_text(&line);
    }

    // ── Geometry ────────────────────────────────────────────────────────

    fn measure_to(&self, i: usize) -> f32 {
        if i == 0 {
            return 0.0;
        }
        let s: String = self.text[..i.min(self.text.len())].iter().collect();
        self.font.as_ref().map_or(0.0, |f| f.measure_width(&s))
    }

    fn measure_range(&self, start: usize, length: usize) -> f32 {
        if length == 0 {
            return 0.0;
        }
        let end = (start + length).min(self.text.len());
        let s: String = self.text[start.min(end)..end].iter().collect();
        match &self.font {
            Some(f) => f.measure_width(&s),
            None => s.chars().count() as f32 * FALLBACK_GLYPH_WIDTH,
        }
    }

    fn line_height(&self) -> f32 {
        self.font
            .as_ref()
            .map_or(FALLBACK_LINE_HEIGHT, |f| f.line_height)
    }

    /// The caret's x in the text, pixels.
    #[must_use]
    pub fn caret_pixel_x(&self) -> f32 {
        self.measure_to(self.caret)
    }

    fn text_alignment_offset(&self, width: f32) -> f32 {
        let visible = (width - 2.0 * self.padding).max(1.0);
        let spare = (visible - self.measure_to(self.text.len())).max(0.0);
        if self.right_aligned {
            spare
        } else if self.centered {
            spare * 0.5
        } else {
            0.0
        }
    }

    fn hit_char_x(&self, width: f32, local_x: f32) -> usize {
        let target = local_x - self.padding - self.text_alignment_offset(width) + self.scroll_x;
        if target <= 0.0 {
            return 0;
        }
        let mut best = 0;
        let mut best_dist = f32::MAX;
        for i in 0..=self.text.len() {
            let d = (self.measure_to(i) - target).abs();
            if d < best_dist {
                best_dist = d;
                best = i;
            }
        }
        best
    }

    /// Wrap the text to the width and tell the scroll model how far it
    /// runs (drawing does this too; call it after replacing the text so a
    /// linked scrollbar is right at once).
    pub fn refresh_scroll_extents(&mut self, width: f32, height: f32) {
        let line_height = self.line_height();
        let visible_width = (width - 2.0 * self.padding).max(1.0);
        let visible_height = (height - 2.0 * self.padding).max(1.0);
        let lines = self.build_wrapped_lines(visible_width);
        let count = lines.len();
        self.wrapped = lines;
        self.wrapped_version = Some(self.text_version);
        self.wrapped_width = visible_width;
        self.wrapped_line_height = line_height;
        self.scroll.line_height = (line_height.round_ties_even() as i32).max(1);
        self.scroll.set_extents(
            ((count as f32 * line_height).ceil() as i32).max(1),
            (visible_height.floor() as i32).max(1),
            false,
        );
    }

    fn build_wrapped_lines(&self, maximum_width: f32) -> Vec<WrappedLine> {
        let mut lines = Vec::new();
        let len = self.text.len();
        if len == 0 {
            lines.push(WrappedLine {
                start: 0,
                length: 0,
            });
            return lines;
        }
        let mut start = 0;
        while start < len {
            if self.text[start] == '\n' {
                lines.push(WrappedLine { start, length: 0 });
                start += 1;
                continue;
            }
            let paragraph_end = self.text[start..]
                .iter()
                .position(|c| *c == '\n')
                .map_or(len, |p| start + p);
            let mut end = start;
            let mut last_whitespace_end: Option<usize> = None;
            while end < paragraph_end {
                let candidate = end + 1;
                if self.measure_range(start, candidate - start) > maximum_width && end > start {
                    break;
                }
                end = candidate;
                if self.text[end - 1].is_whitespace() {
                    last_whitespace_end = Some(end);
                }
            }
            if end < paragraph_end
                && let Some(w) = last_whitespace_end
                && w > start
            {
                end = w;
            }
            if end == start {
                end += 1;
            }
            lines.push(WrappedLine {
                start,
                length: end - start,
            });
            start = end;
            if start == paragraph_end && start < len {
                start += 1;
            }
        }
        if self.text.last() == Some(&'\n') {
            lines.push(WrappedLine {
                start: len,
                length: 0,
            });
        }
        lines
    }

    fn find_caret_line(lines: &[WrappedLine], caret: usize) -> usize {
        for (i, line) in lines.iter().enumerate() {
            let end = line.start + line.length;
            if caret < end || (caret == end && i == lines.len() - 1) {
                return i;
            }
            if caret == end && i + 1 < lines.len() && lines[i + 1].start > caret {
                return i;
            }
        }
        lines.len().saturating_sub(1)
    }

    /// Re-wrap when the text changed since the last wrap.
    pub fn ensure_wrapped_lines_current(&mut self, width: f32) {
        if self.wrapped_version == Some(self.text_version) && !self.wrapped.is_empty() {
            return;
        }
        let w = if self.wrapped_width > 0.0 {
            self.wrapped_width
        } else {
            (width - 2.0 * self.padding).max(1.0)
        };
        self.wrapped = self.build_wrapped_lines(w);
        self.wrapped_version = Some(self.text_version);
    }

    fn hit_char(&mut self, width: f32, local_x: f32, local_y: f32) -> usize {
        if self.one_line {
            return self.hit_char_x(width, local_x);
        }
        self.ensure_wrapped_lines_current(width);
        if self.wrapped.is_empty() {
            return self.text.len();
        }
        let line_index = (((local_y - self.padding + self.scroll.scroll_y() as f32)
            / self.wrapped_line_height.max(1.0))
        .floor()
        .max(0.0) as usize)
            .min(self.wrapped.len() - 1);
        let line = self.wrapped[line_index];
        let target = (local_x - self.padding).max(0.0);
        let mut best = 0;
        let mut best_distance = f32::MAX;
        for col in 0..=line.length {
            let distance = (self.measure_range(line.start, col) - target).abs();
            if distance >= best_distance {
                continue;
            }
            best_distance = distance;
            best = col;
        }
        line.start + best
    }

    // ── Drawing ─────────────────────────────────────────────────────────

    fn draw_background(&self, w: f32, h: f32, rc: &mut RenderContext<'_>) {
        let mut lit = self.focused && self.focus_field_sprite != 0;
        if lit {
            if rc.sprite_size(self.focus_field_sprite).is_some() {
                rc.sprite(
                    TextureRef::Surface(self.focus_field_sprite),
                    0.0,
                    0.0,
                    w,
                    h,
                    (0.0, 0.0, 1.0, 1.0),
                    Color::WHITE,
                );
            } else {
                lit = false;
            }
        }
        if self.focused {
            if rc.sprite_size(self.focus_rail_left_sprite).is_some() {
                rc.sprite(
                    TextureRef::Surface(self.focus_rail_left_sprite),
                    0.0,
                    0.0,
                    self.focus_rail_left_width,
                    h,
                    (0.0, 0.0, 1.0, 1.0),
                    Color::WHITE,
                );
            }
            if rc.sprite_size(self.focus_rail_right_sprite).is_some() {
                rc.sprite(
                    TextureRef::Surface(self.focus_rail_right_sprite),
                    w - self.focus_rail_right_width,
                    0.0,
                    self.focus_rail_right_width,
                    h,
                    (0.0, 0.0, 1.0, 1.0),
                    Color::WHITE,
                );
            }
        }
        if !lit && rc.sprite_size(self.background_sprite).is_some() {
            rc.surface(self.background_sprite, 0.0, 0.0, w, h, Color::WHITE);
            lit = true;
        }
        if !lit {
            match &self.theme_palette {
                Some(p) => style::field(rc, p, w, h, self.background_color, self.focused),
                None => rc.fill(0.0, 0.0, w, h, self.background_color),
            }
        }
    }

    fn draw_one_line(&mut self, w: f32, h: f32, rc: &mut RenderContext<'_>) {
        let lh = self.line_height();
        let ty = (h - lh) * 0.5;
        let visible_w = (w - 2.0 * self.padding).max(1.0);
        let caret_x = self.measure_to(self.caret);
        let full_w = self.measure_to(self.text.len());
        if caret_x - self.scroll_x > visible_w {
            self.scroll_x = caret_x - visible_w;
        }
        if caret_x < self.scroll_x {
            self.scroll_x = caret_x;
        }
        self.scroll_x = self.scroll_x.clamp(0.0, (full_w - visible_w).max(0.0));
        let align_x = self.text_alignment_offset(w);

        let mut start = 0;
        while start < self.text.len() && self.measure_to(start + 1) <= self.scroll_x {
            start += 1;
        }
        let mut end = start;
        while end < self.text.len() && self.measure_to(end + 1) - self.scroll_x <= visible_w {
            end += 1;
        }

        if self.has_selection() {
            let (lo, hi) = self.sel_span();
            let h0 = (align_x + self.measure_to(lo) - self.scroll_x).max(0.0);
            let h1 = (align_x + self.measure_to(hi) - self.scroll_x).min(visible_w);
            if h1 > h0 {
                rc.fill(self.padding + h0, ty, h1 - h0, lh, self.selection_color);
            }
        }
        if end > start
            && let Some(font) = self.font.clone()
        {
            let vis: String = self.text[start..end].iter().collect();
            let vx = self.padding + align_x + (self.measure_to(start) - self.scroll_x);
            rc.string_dat(
                &font,
                &vis,
                vx,
                ty,
                self.text_color,
                self.outline,
                self.outline_color,
            );
        }
        if self.focused {
            let cx = self.padding + align_x + (caret_x - self.scroll_x);
            if cx >= self.padding - 1.0 && cx <= w - self.padding + 1.0 {
                rc.fill(cx, ty, 1.0, lh, self.text_color);
            }
        }
    }

    fn draw_multi_line(&mut self, w: f32, h: f32, rc: &mut RenderContext<'_>) {
        self.refresh_scroll_extents(w, h);
        let lh = self.wrapped_line_height;
        let visible_height = (h - 2.0 * self.padding).max(1.0);
        let lines = self.wrapped.clone();
        let caret_line = Self::find_caret_line(&lines, self.caret);
        if self.focused {
            let caret_top = caret_line as f32 * lh;
            let caret_bottom = caret_top + lh;
            if caret_top < self.scroll.scroll_y() as f32 {
                self.scroll.set_scroll_y(caret_top.floor() as i32);
            } else if caret_bottom > self.scroll.scroll_y() as f32 + visible_height {
                self.scroll
                    .set_scroll_y((caret_bottom - visible_height).ceil() as i32);
            }
        }
        let (sel_lo, sel_hi) = self.sel_span();
        let scroll_y = self.scroll.scroll_y() as f32;
        for (i, line) in lines.iter().enumerate() {
            let y = self.padding + i as f32 * lh - scroll_y;
            if y + lh <= self.padding || y >= h - self.padding {
                continue;
            }
            let line_end = line.start + line.length;
            let hl_lo = sel_lo.max(line.start);
            let hl_hi = sel_hi.min(line_end);
            if self.has_selection() && hl_hi > hl_lo {
                let x0 = self.padding + self.measure_range(line.start, hl_lo - line.start);
                let x1 = self.padding + self.measure_range(line.start, hl_hi - line.start);
                rc.fill(x0, y, (x1 - x0).max(0.0), lh, self.selection_color);
            }
            if let Some(font) = self.font.clone() {
                let text: String = self.text[line.start..line_end].iter().collect();
                rc.string_dat(
                    &font,
                    &text,
                    self.padding,
                    y,
                    self.text_color,
                    self.outline,
                    self.outline_color,
                );
            }
        }
        if self.focused && !lines.is_empty() {
            let line = lines[caret_line];
            let col = self.caret.saturating_sub(line.start).min(line.length);
            let x = self.padding + self.measure_range(line.start, col);
            let y = self.padding + caret_line as f32 * lh - scroll_y;
            rc.fill(x, y, 1.0, lh, self.text_color);
        }
    }
}

impl Widget for Field {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        let width = ui.element(me).map_or(0.0, |el| el.width);
        let before = self.text_version;
        let handled = self.handle(ui, me, e, width);
        for request in self.take_recall_requests() {
            ui.send_message(me, ElementMessage::Recall(request));
        }
        if self.text_version != before {
            ui.send_message(me, ElementMessage::TextChanged(self.text()));
        }
        handled
    }

    fn consumes_dat_children(&self) -> bool {
        true
    }

    fn handles_click(&self, _element: &Element) -> bool {
        true
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let Some((w, h)) = ui.element(me).map(|e| (e.width, e.height)) else {
            return;
        };
        self.draw_background(w, h, rc);
        if self.one_line {
            self.draw_one_line(w, h, rc);
        } else {
            self.draw_multi_line(w, h, rc);
        }
    }

    fn scroll_model(&mut self) -> Option<&mut Scrollable> {
        Some(&mut self.scroll)
    }
}

impl Field {
    /// The edit ends: its state is finished first, then the loss is
    /// reported once (a second notice of the same loss is ignored).
    fn lose_focus(&mut self, ui: &mut UiRoot, me: ElementId) {
        let was_focused = std::mem::replace(&mut self.focused, false);
        self.reset_recall();
        self.sel_anchor = None;
        self.selecting = false;
        self.preserve_focus_selection_on_mouse_down = false;
        if was_focused && self.reports_focus_loss && !self.losing_focus_to_submit {
            ui.send_message(me, ElementMessage::FocusLost(self.text()));
        }
    }

    /// Let go of the keyboard from inside the field's own key handling: the
    /// root's notice cannot reach the field while it is handling a key, so
    /// the loss is taken here.
    fn give_up_focus(&mut self, ui: &mut UiRoot, me: ElementId) {
        ui.set_keyboard_focus(None);
        if ui.keyboard_focus() != Some(me) {
            self.lose_focus(ui, me);
        }
    }

    fn handle(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent, width: f32) -> bool {
        match e.kind {
            kind::FOCUS_GAINED => {
                self.focused = true;
                if self.select_all_on_focus {
                    self.select_all_text();
                    self.preserve_focus_selection_on_mouse_down = true;
                }
                true
            }
            kind::FOCUS_LOST => {
                self.lose_focus(ui, me);
                true
            }
            kind::CHAR => {
                let value = char::from_u32(e.data0 as u32).unwrap_or('\0');
                if self.suppress_next_newline_char && (value == '\r' || value == '\n') {
                    self.suppress_next_newline_char = false;
                    return true;
                }
                self.suppress_next_newline_char = false;
                self.insert_char(value);
                true
            }
            kind::MOUSE_DOWN => {
                if self.preserve_focus_selection_on_mouse_down {
                    self.preserve_focus_selection_on_mouse_down = false;
                    self.selecting = false;
                    return true;
                }
                self.caret = self.hit_char(width, e.data1 as f32, e.data2 as f32);
                self.sel_anchor = self.selectable.then_some(self.caret);
                self.selecting = self.selectable;
                true
            }
            kind::MOUSE_MOVE => {
                if self.selectable && self.selecting {
                    self.caret = self.hit_char(width, e.data1 as f32, e.data2 as f32);
                }
                true
            }
            kind::MOUSE_UP => {
                self.selecting = false;
                true
            }
            kind::KEY_UP => true,
            kind::KEY_DOWN => self.key_down(ui, me, e.key, e.modifiers),
            kind::SCROLL => {
                if self.one_line {
                    return false;
                }
                self.scroll.scroll_by_lines(-e.data0.signum());
                true
            }
            kind::CLICK => {
                if self.editable {
                    return false;
                }
                ui.send_message(me, ElementMessage::ReadOnlyClicked);
                true
            }
            _ => false,
        }
    }

    fn key_down(&mut self, ui: &mut UiRoot, me: ElementId, key: Key, modifiers: Modifiers) -> bool {
        if !self.editable {
            return true;
        }
        if modifiers.contains(Modifiers::CTRL) {
            match key {
                Key::A if self.selectable => self.select_all_text(),
                Key::C if self.selectable => self.copy_selection(ui),
                Key::X if self.selectable => self.cut_selection(ui),
                Key::V => ui.emit(UiOutput::PasteRequest(me)),
                _ => {}
            }
            return true;
        }
        let shift = self.selectable && modifiers.contains(Modifiers::SHIFT);
        match key {
            Key::Escape => {
                // Stepping away from the entry keeps what was typed.
                self.give_up_focus(ui, me);
                true
            }
            Key::Enter | Key::KeypadEnter => {
                if !self.one_line {
                    self.insert_char('\n');
                    self.suppress_next_newline_char = true;
                    return true;
                }
                // The focus loss that ends a submit which handed the text
                // over (or emptied the field) is not a focus loss to report.
                self.losing_focus_to_submit = self.clear_on_submit
                    || (self.has_submit_handler && !self.text().trim().is_empty());
                if let Some(line) = self.submit() {
                    ui.send_message(me, ElementMessage::Submitted(line));
                }
                if !self.stay_focused_after_submit {
                    self.give_up_focus(ui, me);
                }
                self.losing_focus_to_submit = false;
                true
            }
            Key::Backspace => {
                self.backspace();
                true
            }
            Key::Delete => {
                self.delete_forward();
                true
            }
            Key::Left => {
                self.move_caret_by(-1, shift);
                true
            }
            Key::Right => {
                self.move_caret_by(1, shift);
                true
            }
            Key::Home => {
                self.move_caret_to(0, shift);
                true
            }
            Key::End => {
                self.move_caret_to(self.text.len(), shift);
                true
            }
            Key::Up => {
                self.history_prev();
                true
            }
            Key::Down => {
                self.history_next();
                true
            }
            _ => false,
        }
    }
}
