//! The text element: one line or many, justified, scrolled, optionally
//! selectable (drag to select, Ctrl+C to copy) and clickable per character.

use std::sync::Arc;

use ac_dat::objects::DIRECT_STATE_ID;

use crate::draw::{Color, DEFAULT_OUTLINE_COLOR, RenderContext};
use crate::element_info::{ElementInfo, color_of};
use crate::event::{Key, Modifiers, UiEvent, kind};
use crate::font::UiFont;
use crate::root::{ElementMessage, UiOutput, UiRoot};
use crate::states;
use crate::text::{
    self, Line, LineKey, Pos, Scrollable, TextRun, VJustify, char_len, char_slice, content_base_y,
    content_offset_x, layout_runs, line_intersects_viewport, runs_match_line, v_offset, wrap_words,
};
use crate::tree::{Element, ElementId, Widget};

/// Decides whether a click on a character is handled (chat name tags).
pub type CharClick = Box<dyn FnMut(Pos) -> bool>;

/// What a text element shows.
#[derive(Debug, Clone, PartialEq)]
pub enum TextContent {
    /// Nothing.
    Empty,
    /// These lines, oldest first.
    Lines(Vec<Line>),
    /// Authored text, word-wrapped to the element's width in the default
    /// colour (re-wrapped when the width, font or colour changes).
    Wrapped(String),
    /// One authored line in the default colour.
    Single(String),
}

#[derive(Debug, Clone, Copy, PartialEq)]
struct Anchored {
    source: i64,
    offset: usize,
}

/// A text element.
pub struct Text {
    /// Dat element id (0 for synthesized text).
    pub element_id: u32,
    content: TextContent,
    wrapped_cache: Option<(f32, u32, Color, Vec<Line>)>,
    /// Identities of the shown lines (anchors selections to their text).
    pub line_keys: Vec<LineKey>,
    /// Coloured runs of a one-line text.
    pub runs: Option<Vec<TextRun>>,
    /// Coloured runs per line (drawn only when they spell the line).
    pub line_runs: Option<Vec<Vec<TextRun>>>,
    /// The font (nothing draws without one).
    pub font: Option<Arc<UiFont>>,
    /// Colour of authored text.
    pub default_color: Color,
    /// Colour of tagged text.
    pub tag_color: Option<Color>,
    /// The authored colour palette (property 0x1B).
    pub font_color_palette: Vec<Color>,
    /// Fill behind the text.
    pub background_color: Color,
    /// Outlined glyphs.
    pub outline: bool,
    /// Outline colour.
    pub outline_color: Color,
    /// The state image drawn under the text (0: none).
    pub background_sprite: u32,
    /// Selection highlight colour.
    pub selection_color: Color,
    /// Inset on every side, pixels.
    pub padding: f32,
    /// Left margin, pixels.
    pub margin_left: f32,
    /// Right margin, pixels.
    pub margin_right: f32,
    /// Top margin, pixels.
    pub margin_top: f32,
    /// Bottom margin, pixels.
    pub margin_bottom: f32,
    /// Single-line layout.
    pub one_line: bool,
    selectable: bool,
    /// Horizontally centred.
    pub centered: bool,
    /// Right-aligned.
    pub right_aligned: bool,
    /// Vertical justification.
    pub vertical_justify: VJustify,
    /// Honour the vertical justification for synthesized text too.
    pub honor_vertical_justification: bool,
    /// The scroll model (also read by a linked scrollbar).
    pub scroll: Scrollable,
    /// Keep the view at the end as lines arrive (lines set by code, such as
    /// a log; the element's own authored string always starts at its first
    /// line).
    pub preserve_end_on_layout: bool,
    /// The wheel scrolls even when not selectable.
    pub wheel_scroll_enabled: bool,
    /// Clicks are reported as [`ElementMessage::Clicked`].
    pub emits_clicks: bool,
    /// Character clicks go here first.
    pub on_char_click: Option<CharClick>,
    /// A click reports the character under it as
    /// [`ElementMessage::TextClicked`] (chat name tags); the click goes on
    /// bubbling.
    pub emits_char_clicks: bool,

    dat_info: Option<Arc<ElementInfo>>,
    active_retail_state: u32,
    active_state_name: String,
    draw_text_after_children: bool,
    honor_dat_vertical_justification: bool,
    authored_state_strings: Vec<(u32, String)>,

    last_lines: Vec<Line>,
    last_line_height: f32,
    last_base_y: f32,
    sel_anchor: Option<Pos>,
    sel_caret: Option<Pos>,
    selecting: bool,
    anchored_start: Option<Anchored>,
    anchored_caret: Option<Anchored>,
}

impl std::fmt::Debug for Text {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Text")
            .field("element_id", &self.element_id)
            .field("content", &self.content)
            .field("active_state", &self.active_state_name)
            .finish_non_exhaustive()
    }
}

impl Default for Text {
    fn default() -> Self {
        Self {
            element_id: 0,
            content: TextContent::Empty,
            wrapped_cache: None,
            line_keys: Vec::new(),
            runs: None,
            line_runs: None,
            font: None,
            default_color: Color::WHITE,
            tag_color: None,
            font_color_palette: Vec::new(),
            background_color: Color::TRANSPARENT,
            outline: false,
            outline_color: DEFAULT_OUTLINE_COLOR,
            background_sprite: 0,
            selection_color: Color::rgba(0.25, 0.45, 0.85, 0.5),
            padding: 0.0,
            margin_left: 0.0,
            margin_right: 0.0,
            margin_top: 0.0,
            margin_bottom: 0.0,
            one_line: false,
            selectable: false,
            centered: false,
            right_aligned: false,
            vertical_justify: VJustify::Center,
            honor_vertical_justification: false,
            scroll: Scrollable::default(),
            preserve_end_on_layout: true,
            wheel_scroll_enabled: false,
            emits_clicks: false,
            on_char_click: None,
            emits_char_clicks: false,
            dat_info: None,
            active_retail_state: DIRECT_STATE_ID,
            active_state_name: String::new(),
            draw_text_after_children: false,
            honor_dat_vertical_justification: false,
            authored_state_strings: Vec::new(),
            last_lines: Vec::new(),
            last_line_height: 16.0,
            last_base_y: 0.0,
            sel_anchor: None,
            sel_caret: None,
            selecting: false,
            anchored_start: None,
            anchored_caret: None,
        }
    }
}

impl Text {
    /// The element flags a text element starts with: display text is
    /// click-through.
    pub fn configure(element: &mut Element) {
        element.click_through = true;
        element.accepts_focus = false;
        element.is_edit_control = false;
        element.captures_pointer_drag = false;
    }

    /// Replace what the element shows.
    pub fn set_content(&mut self, content: TextContent) {
        self.content = content;
        self.wrapped_cache = None;
    }

    /// Show an authored string (the element's own text property) as the
    /// element lays it out: word-wrapped to its width between the margins
    /// unless the element is one line. An authored line break always
    /// starts a new line.
    pub fn set_authored(&mut self, text: String) {
        if !self.one_line || text.contains('\n') {
            self.set_content(TextContent::Wrapped(text));
        } else {
            // In the default colour as it is when drawn, so a state's font
            // colour reaches the authored line.
            self.set_content(TextContent::Single(text));
        }
    }

    /// Show these lines.
    pub fn set_lines(&mut self, lines: Vec<Line>) {
        self.set_content(TextContent::Lines(lines));
    }

    /// What the element shows.
    #[must_use]
    pub fn content(&self) -> &TextContent {
        &self.content
    }

    /// The content's lines (authored text as one unwrapped line).
    #[must_use]
    pub fn content_lines(&self) -> Vec<Line> {
        match &self.content {
            TextContent::Empty => Vec::new(),
            TextContent::Lines(lines) => lines.clone(),
            TextContent::Single(t) | TextContent::Wrapped(t) => {
                vec![Line::new(t.clone(), self.default_color)]
            }
        }
    }

    /// True when the text can be selected.
    #[must_use]
    pub fn selectable(&self) -> bool {
        self.selectable
    }

    /// Make the text selectable (it then takes the pointer, focus and its
    /// own drags) or not (dropping any selection).
    pub fn set_selectable(&mut self, element: &mut Element, value: bool) {
        if self.selectable == value {
            return;
        }
        self.selectable = value;
        element.click_through = !value;
        element.accepts_focus = value;
        element.is_edit_control = value;
        element.captures_pointer_drag = value;
        if !value {
            self.clear_selection();
        }
    }

    /// Follow the element's dat states: the state image under the text, the
    /// state's font colour and visibility, per-state authored strings.
    pub fn configure_dat_state(&mut self, info: Arc<ElementInfo>) {
        self.honor_dat_vertical_justification = true;
        self.draw_text_after_children = info.states.iter().any(|(_, s)| s.pass_to_children);
        self.active_retail_state = info.effective_default_state_id();
        self.dat_info = Some(info);
        self.apply_dat_state(None, self.active_retail_state);
    }

    /// The per-state authored strings (state id → text).
    pub fn set_authored_state_strings(&mut self, strings: Vec<(u32, String)>) {
        self.authored_state_strings = strings;
    }

    /// True when the text draws over its children (a state is passed to
    /// them).
    #[must_use]
    pub fn draws_text_after_children(&self) -> bool {
        self.draw_text_after_children
    }

    fn apply_dat_state(&mut self, ui: Option<(&mut UiRoot, ElementId)>, state_id: u32) -> bool {
        let Some(info) = self.dat_info.clone() else {
            return false;
        };
        let state = info.state(state_id);
        let name = if state_id == DIRECT_STATE_ID {
            if state.is_none() && !info.state_media.contains_key("") {
                return false;
            }
            String::new()
        } else if let Some(s) = state {
            s.name.clone()
        } else {
            let n = states::standard_state_name(state_id);
            if n.is_empty() || !info.state_media.contains_key(n) {
                return false;
            }
            n.to_owned()
        };
        self.active_retail_state = state_id;
        self.active_state_name = name.clone();
        self.background_sprite = info.media_or_default(&name).map_or(0, |m| m.file);
        if let Some(c) = info
            .effective_property(0x1B, Some(state_id))
            .and_then(color_of)
        {
            self.default_color = c;
        }
        if let Some((text, _)) = self
            .authored_state_strings
            .iter()
            .find(|(k, _)| *k == state_id)
            .map(|(_, t)| (t.clone(), ()))
        {
            self.set_authored(text);
        }
        if let Some((ui, me)) = ui {
            if let Some(s) = state
                && let Some(ac_dat::objects::PropertyValue::Bool(invisible)) =
                    s.properties.get(0x3B).map(|p| &p.value)
            {
                ui.set_visible(me, !invisible);
            }
            if state.is_some_and(|s| s.pass_to_children) {
                for child in ui.children(me) {
                    ui.set_retail_state(child, state_id);
                }
            }
        }
        true
    }

    /// The lines as of the last draw.
    #[must_use]
    pub fn shown_lines(&self) -> &[Line] {
        &self.last_lines
    }

    /// The lines the content gives at the element's current width.
    fn current_lines(&mut self, width: f32) -> Vec<Line> {
        match &self.content {
            TextContent::Empty => Vec::new(),
            TextContent::Lines(lines) => lines.clone(),
            TextContent::Single(text) => vec![Line::new(text.clone(), self.default_color)],
            TextContent::Wrapped(text) => {
                let font_id = self.font.as_ref().map_or(0, |f| f.id);
                if let Some((w, f, c, lines)) = &self.wrapped_cache
                    && *w == width
                    && *f == font_id
                    && *c == self.default_color
                {
                    return lines.clone();
                }
                let max = (width
                    - (self.padding + self.margin_left)
                    - (self.padding + self.margin_right))
                    .max(1.0);
                let lines: Vec<Line> = match self.font.as_deref() {
                    Some(font) => wrap_words(text, |s| font.measure_width(s), max),
                    None => wrap_words(text, |s| char_len(s) as f32 * 8.0, max),
                }
                .into_iter()
                .map(|l| Line::new(l, self.default_color))
                .collect();
                self.wrapped_cache = Some((width, font_id, self.default_color, lines.clone()));
                lines
            }
        }
    }

    /// Poll the content and put the selection back on its own text.
    pub fn refresh_lines(&mut self, width: f32) -> &[Line] {
        self.last_lines = self.current_lines(width);
        self.reanchor_selection();
        &self.last_lines
    }

    fn clear_selection(&mut self) {
        self.selecting = false;
        self.sel_anchor = None;
        self.sel_caret = None;
        self.anchored_start = None;
        self.anchored_caret = None;
    }

    fn anchored_at(&self, p: Pos) -> Option<Anchored> {
        if self.line_keys.is_empty() {
            return None;
        }
        let key = self.line_keys[p.line.min(self.line_keys.len() - 1)];
        Some(Anchored {
            source: key.source,
            offset: key.start + p.col,
        })
    }

    fn reanchor_selection(&mut self) {
        let (Some(start), Some(caret)) = (self.anchored_start, self.anchored_caret) else {
            return;
        };
        let a = text::resolve_anchor(
            &self.line_keys,
            &self.last_lines,
            start.source,
            start.offset,
        );
        let c = text::resolve_anchor(
            &self.line_keys,
            &self.last_lines,
            caret.source,
            caret.offset,
        );
        match (a, c) {
            (Some(a), Some(c)) => {
                self.sel_anchor = Some(a);
                self.sel_caret = Some(c);
            }
            _ => self.clear_selection(),
        }
    }

    fn ordered_selection(&self) -> Option<(Pos, Pos)> {
        let (a, c) = (self.sel_anchor?, self.sel_caret?);
        let (s, e) = text::order(a, c);
        (s != e).then_some((s, e))
    }

    /// The selected text ("" without a selection).
    #[must_use]
    pub fn selected_text(&self) -> String {
        match self.ordered_selection() {
            Some((s, e)) => text::selected_text(&self.last_lines, s, e),
            None => String::new(),
        }
    }

    /// Select every shown line.
    pub fn select_all(&mut self) {
        if self.last_lines.is_empty() {
            self.clear_selection();
            return;
        }
        let last = self.last_lines.len() - 1;
        let anchor = Pos::new(0, 0);
        let caret = Pos::new(last, char_len(&self.last_lines[last].text));
        self.sel_anchor = Some(anchor);
        self.sel_caret = Some(caret);
        self.anchored_start = self.anchored_at(anchor);
        self.anchored_caret = self.anchored_at(caret);
    }

    /// The current selection, earlier end first.
    #[must_use]
    pub fn selection(&self) -> Option<(Pos, Pos)> {
        self.ordered_selection()
    }

    fn horizontal_offset(&self, width: f32, line: &str) -> f32 {
        let lw = self.font.as_ref().map_or(0.0, |f| f.measure_width(line));
        content_offset_x(
            width,
            self.padding,
            self.margin_left,
            self.margin_right,
            lw,
            self.centered,
            self.right_aligned,
        )
    }

    /// The line and column under element-local (`x`, `y`), from the last
    /// drawn layout.
    #[must_use]
    pub fn hit_char(&self, width: f32, x: f32, y: f32) -> Pos {
        if self.last_lines.is_empty() {
            return Pos::new(0, 0);
        }
        let lh = if self.last_line_height <= 0.0 {
            16.0
        } else {
            self.last_line_height
        };
        let line = (((y - self.last_base_y) / lh).floor().max(0.0) as usize)
            .min(self.last_lines.len() - 1);
        let text = &self.last_lines[line].text;
        let line_x = self.horizontal_offset(width, text);
        let col = match self.font.as_deref() {
            Some(f) => text::char_index_at(text, |c| f.advance(c), x - line_x),
            None => 0,
        };
        Pos::new(line, col)
    }

    fn draw_text(&mut self, width: f32, height: f32, rc: &mut RenderContext<'_>) {
        let Some(font) = self.font.clone() else {
            return;
        };
        if self.one_line {
            if let Some(runs) = self.runs.clone() {
                self.draw_single_line_runs(&font, width, height, &runs, rc);
                return;
            }
            let lines = self.current_lines(width);
            let Some(line0) = lines.first() else {
                return;
            };
            let x = text::one_line_offset_x(
                width,
                self.padding,
                self.margin_left,
                self.margin_right,
                font.measure_width(&line0.text),
                self.centered,
                self.right_aligned,
            );
            let y = v_offset(
                height,
                font.line_height,
                self.padding,
                self.vertical_justify,
            );
            rc.string_dat(
                &font,
                &line0.text,
                x,
                y,
                line0.color,
                self.outline,
                self.outline_color,
            );
            return;
        }

        self.refresh_lines(width);
        let lines = self.last_lines.clone();
        self.last_line_height = font.line_height;
        if lines.is_empty() {
            return;
        }
        let lh = font.line_height;
        let top = self.padding + self.margin_top;
        let bottom = height - self.padding - self.margin_bottom;
        let content_h = lines.len() as f32 * lh;
        self.scroll.line_height = lh.round_ties_even() as i32;
        // A text element keeps its scroll offset (zero at first) as its text
        // is laid out, so an authored string too tall for its box shows its
        // start; only a panel streaming lines in follows the end.
        let authored = matches!(
            self.content,
            TextContent::Single(_) | TextContent::Wrapped(_)
        );
        self.scroll.set_extents(
            content_h.ceil() as i32,
            (bottom - top).floor() as i32,
            self.preserve_end_on_layout && !authored,
        );
        let base_y = content_base_y(
            top,
            bottom,
            content_h,
            self.scroll.max_scroll() as f32,
            self.scroll.scroll_y() as f32,
            self.vertical_justify,
            self.honor_dat_vertical_justification || self.honor_vertical_justification,
        );
        self.last_base_y = base_y;
        let selection = self.ordered_selection();
        let mut placed: Vec<(String, f32, f32, Color)> = Vec::new();
        for (i, line) in lines.iter().enumerate() {
            let y = base_y + i as f32 * lh;
            if !line_intersects_viewport(y, lh, top, bottom) {
                continue;
            }
            let line_x = self.horizontal_offset(width, &line.text);
            if let Some((s, e)) = selection
                && i >= s.line
                && i <= e.line
            {
                let len = char_len(&line.text);
                let c0 = if i == s.line { s.col } else { 0 }.min(len);
                let c1 = if i == e.line { e.col } else { len }.min(len);
                if c1 > c0 {
                    let hx = line_x + font.measure_width(char_slice(&line.text, 0, c0));
                    let hw = font.measure_width(char_slice(&line.text, c0, c1));
                    rc.fill(hx, y, hw, lh, self.selection_color);
                }
            }
            let runs = self
                .line_runs
                .as_ref()
                .and_then(|r| r.get(i))
                .filter(|r| !r.is_empty() && runs_match_line(r, &line.text));
            match runs {
                Some(runs) => {
                    for (t, x, c) in layout_runs(runs, line_x, |s| font.measure_width(s)) {
                        placed.push((t, x, y, c));
                    }
                }
                None => placed.push((line.text.clone(), line_x, y, line.color)),
            }
        }
        if self.outline {
            for (t, x, y, _) in &placed {
                rc.string_dat_pass(&font, t, *x, *y, self.outline_color, true);
            }
        }
        for (t, x, y, c) in &placed {
            rc.string_dat_pass(&font, t, *x, *y, *c, false);
        }
    }

    fn draw_single_line_runs(
        &self,
        font: &UiFont,
        width: f32,
        height: f32,
        runs: &[TextRun],
        rc: &mut RenderContext<'_>,
    ) {
        if runs.is_empty() {
            return;
        }
        let total: f32 = runs.iter().map(|r| font.measure_width(&r.text)).sum();
        // The runs keep OpenAC's floor at the padding; inside it they sit
        // as a plain one-line text does (within the text margins).
        let x = text::one_line_offset_x(
            width,
            self.padding,
            self.margin_left,
            self.margin_right,
            total,
            self.centered,
            self.right_aligned,
        );
        let x = if self.centered || self.right_aligned {
            self.padding.max(x)
        } else {
            x
        };
        let y = v_offset(
            height,
            font.line_height,
            self.padding,
            self.vertical_justify,
        );
        let placed = layout_runs(runs, x, |s| font.measure_width(s));
        if self.outline {
            for (t, px, _) in &placed {
                rc.string_dat_pass(font, t, *px, y, self.outline_color, true);
            }
        }
        for (t, px, c) in &placed {
            rc.string_dat_pass(font, t, *px, y, *c, false);
        }
    }
}

impl Widget for Text {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        let width = ui.element(me).map_or(0.0, |el| el.width);
        if e.kind == kind::CLICK && self.on_char_click.is_some() {
            let pos = self.hit_char(width, e.data1 as f32, e.data2 as f32);
            if let Some(click) = self.on_char_click.as_mut()
                && click(pos)
            {
                return true;
            }
        }
        if e.kind == kind::CLICK && self.emits_char_clicks && e.target == Some(me) {
            let pos = self.hit_char(width, e.data1 as f32, e.data2 as f32);
            ui.send_message(me, ElementMessage::TextClicked(pos));
        }
        if e.kind == kind::CLICK && self.emits_clicks {
            ui.send_message(
                me,
                ElementMessage::Clicked {
                    x: e.data1,
                    y: e.data2,
                },
            );
            return true;
        }
        match e.kind {
            kind::SCROLL => {
                if !self.selectable && !self.wheel_scroll_enabled {
                    return false;
                }
                // Wheel up reveals older lines: toward the top.
                self.scroll.scroll_by_lines(-e.data0);
                true
            }
            kind::MOUSE_DOWN => {
                if !self.selectable {
                    return false;
                }
                let p = self.hit_char(width, e.data1 as f32, e.data2 as f32);
                self.sel_anchor = Some(p);
                self.sel_caret = Some(p);
                self.anchored_start = self.anchored_at(p);
                self.anchored_caret = self.anchored_start;
                self.selecting = true;
                true
            }
            kind::MOUSE_MOVE => {
                if !self.selectable || !self.selecting {
                    return false;
                }
                let p = self.hit_char(width, e.data1 as f32, e.data2 as f32);
                self.sel_caret = Some(p);
                self.anchored_caret = self.anchored_at(p);
                true
            }
            kind::MOUSE_UP => {
                if !self.selectable {
                    return false;
                }
                self.selecting = false;
                true
            }
            kind::KEY_DOWN => {
                if !self.selectable {
                    return false;
                }
                let ctrl = e.modifiers.contains(Modifiers::CTRL);
                if ctrl && e.key == Key::C {
                    let selected = self.selected_text();
                    if !selected.is_empty() {
                        ui.emit(UiOutput::SetClipboard(selected));
                    }
                    return true;
                }
                if ctrl && e.key == Key::A {
                    self.select_all();
                    return true;
                }
                false
            }
            _ => false,
        }
    }

    fn handles_click(&self, element: &Element) -> bool {
        self.emits_clicks
            || self.on_char_click.is_some()
            || self.emits_char_clicks
            || self.wheel_scroll_enabled
            || element
                .pointer_region
                .as_ref()
                .is_some_and(|r| r.handler.handles_click())
    }

    fn consumes_dat_children(&self) -> bool {
        match &self.dat_info {
            None => true,
            Some(info) => !info.states.iter().any(|(_, s)| s.pass_to_children),
        }
    }

    fn child_import(&self) -> crate::widgets::ChildImport {
        if self.consumes_dat_children() {
            crate::widgets::ChildImport::MediaBearing
        } else {
            crate::widgets::ChildImport::All
        }
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let Some((w, h)) = ui.element(me).map(|e| (e.width, e.height)) else {
            return;
        };
        if self.background_sprite != 0 {
            rc.surface(self.background_sprite, 0.0, 0.0, w, h, Color::WHITE);
        }
        rc.fill(0.0, 0.0, w, h, self.background_color);
        if !self.draw_text_after_children {
            self.draw_text(w, h, rc);
        }
    }

    fn draw_after_children(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        if self.draw_text_after_children
            && let Some((w, h)) = ui.element(me).map(|e| (e.width, e.height))
        {
            self.draw_text(w, h, rc);
        }
    }

    fn active_cursor_state_name(&self) -> &str {
        &self.active_state_name
    }

    fn retail_state(&self) -> Option<u32> {
        self.dat_info.as_ref().map(|_| self.active_retail_state)
    }

    fn set_retail_state(&mut self, ui: &mut UiRoot, me: ElementId, state: u32) -> bool {
        self.apply_dat_state(Some((ui, me)), state)
    }

    fn scroll_model(&mut self) -> Option<&mut Scrollable> {
        Some(&mut self.scroll)
    }
}
