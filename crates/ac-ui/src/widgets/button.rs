//! The retail button: the normal/rollover/pressed/highlight/ghosted state
//! machine, toggles, hot-click repeat, faces made of one or several
//! stateful children, a label and a value caption.

use std::sync::Arc;

use ac_dat::objects::DIRECT_STATE_ID;

use crate::draw::{Color, DEFAULT_OUTLINE_COLOR, RenderContext};
use crate::element_info::{ElementInfo, StateInfo};
use crate::event::{DragAcceptance, ItemDragPayload, UiEvent, kind};
use crate::font::UiFont;
use crate::geometry::PixelRect;
use crate::layout_policy::LayoutPolicy;
use crate::media;
use crate::root::{ElementMessage, UiRoot};
use crate::states::{self, ButtonVisualInput};
use crate::tree::{Element, ElementId, Widget};
use crate::widgets::dat_element::{active_state_id, apply_state_visibility};

/// Where a label sits in its box.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum LabelAlign {
    /// Centred.
    #[default]
    Center,
    /// Left, after the left offset.
    Left,
    /// Right.
    Right,
}

/// Which button messages someone listens for. A message nobody listens for
/// lets the event bubble on, as the client's unset handlers do.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ButtonListeners {
    /// [`ElementMessage::Clicked`].
    pub click: bool,
    /// [`ElementMessage::DoubleClicked`].
    pub double_click: bool,
    /// [`ElementMessage::RightClicked`].
    pub right_click: bool,
    /// [`ElementMessage::ItemDropped`].
    pub item_drop: bool,
}

/// Decides how a dragged item hovering the button would be received.
pub type DragOverPolicy = Box<dyn FnMut(&ItemDragPayload) -> DragAcceptance>;

#[derive(Debug, Clone)]
struct FaceSegment {
    info: Arc<ElementInfo>,
    original: PixelRect,
    layout: Option<LayoutPolicy>,
}

impl FaceSegment {
    fn new(info: Arc<ElementInfo>) -> Self {
        let original = PixelRect::from_position_and_size(
            info.x as i32,
            info.y as i32,
            info.width as i32,
            info.height as i32,
        );
        let layout = info.original_parent_size.and_then(|(pw, ph)| {
            LayoutPolicy::new(
                info.left,
                info.top,
                info.right,
                info.bottom,
                original,
                PixelRect::from_position_and_size(0, 0, pw as i32, ph as i32),
            )
            .ok()
        });
        Self {
            info,
            original,
            layout,
        }
    }

    fn rect(&self, parent_w: f32, parent_h: f32) -> PixelRect {
        match self.layout {
            Some(l) => l.apply(
                self.original,
                PixelRect::from_position_and_size(0, 0, parent_w as i32, parent_h as i32),
            ),
            None => self.original,
        }
    }
}

/// A retail button.
pub struct Button {
    info: Arc<ElementInfo>,
    media_info: Arc<ElementInfo>,
    face_segments: Vec<FaceSegment>,
    segment_media_states: Vec<String>,
    face_media_state: String,
    last_media_commit_state: Option<String>,
    has_custom_selection_pair: bool,
    state_label_colors: Option<Vec<(u32, Color)>>,
    state_label_outlines: Option<Vec<(u32, bool)>>,
    pressed: bool,
    pointer_over: bool,
    selected: bool,
    enabled: bool,
    hot_clicking: bool,
    suppress_next_click: bool,
    pointer: (i32, i32),
    next_hot_click_time: Option<f64>,
    active_state: String,
    active_state_started_at: f64,
    item_drag_acceptance: DragAcceptance,
    block_label_cache: Option<(BlockKey, Vec<PlacedLine>)>,

    /// Which messages are listened for.
    pub listeners: ButtonListeners,
    /// Judges dragged items hovering the button.
    pub on_item_drag_over: Option<DragOverPolicy>,
    /// Overlay while an accepted drag hovers (0: none).
    pub item_drag_accept_sprite: u32,
    /// Overlay while a refused drag hovers (0: none).
    pub item_drag_reject_sprite: u32,
    /// Label text.
    pub label: Option<String>,
    /// Label font.
    pub label_font: Option<Arc<UiFont>>,
    /// Label colour.
    pub label_color: Color,
    /// Tooltip text set at run time.
    pub tooltip_text: Option<String>,
    /// Outlined label.
    pub outline: bool,
    /// Label outline colour.
    pub outline_color: Color,
    /// Face rectangle left.
    pub face_left: f32,
    /// Face rectangle top.
    pub face_top: f32,
    /// Face width (0: the element's).
    pub face_width: f32,
    /// Face height (0: the element's).
    pub face_height: f32,
    /// Draw this image instead of the state's.
    pub face_file_override: Option<u32>,
    /// Draw this runtime image (stretched, untinted) instead of the face:
    /// a colour swatch baked from its spot art.
    pub color_key_face: Option<crate::draw::TextureRef>,
    /// Multiplied into the face.
    pub tint: Color,
    /// Left inset of a left-aligned label.
    pub label_offset_x: f32,
    /// Label alignment.
    pub label_align: LabelAlign,
    /// Label on the bottom edge of its box.
    pub label_at_bottom: bool,
    /// Label box (default: the whole button).
    pub label_box: Option<(f32, f32, f32, f32)>,
    /// Value caption.
    pub value_label: Option<String>,
    /// Value font.
    pub value_font: Option<Arc<UiFont>>,
    /// Value colour.
    pub value_color: Color,
    /// Value box.
    pub value_box: Option<(f32, f32, f32, f32)>,
    /// Value alignment.
    pub value_align: LabelAlign,
    /// A click flips `selected` (property 0x0B).
    pub toggle_behavior: bool,
    /// Hover shows the rollover state (property 0x13).
    pub rollover_enabled: bool,
    /// Holding repeats the click (property 0x0F).
    pub hot_click_enabled: bool,
    /// Seconds before the first repeat (property 0x10).
    pub hot_click_initial_delay: f32,
    /// Seconds between repeats (property 0x11).
    pub hot_click_repeat_interval: f32,
    /// A toggle press does not flip `selected` itself (a controller owns
    /// it).
    pub suppress_self_toggle: bool,
    /// The controller sets the visual state; input does not.
    pub controller_owns_visual_state: bool,
    /// The object the button stands for (the cursor's "found" form); 0 for
    /// none.
    pub represented_object: u32,
}

impl std::fmt::Debug for Button {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Button")
            .field("id", &self.info.id)
            .field("active_state", &self.active_state)
            .field("selected", &self.selected)
            .field("label", &self.label)
            .finish_non_exhaustive()
    }
}

type BlockKey = (String, u32, [u32; 4], LabelAlign, u32, bool);

/// A placed label line: text, x, y.
pub type PlacedLine = (String, f32, f32);

impl Button {
    /// A button for `info`. `media` is the stateful face child (when the
    /// button's own states carry no images); `segments` are several face
    /// children drawn together. `now` is the UI media clock (the start of
    /// the initial state's animation).
    #[must_use]
    pub fn new(
        info: Arc<ElementInfo>,
        media: Option<Arc<ElementInfo>>,
        segments: Vec<Arc<ElementInfo>>,
        now: f64,
    ) -> Self {
        let media_info = media.clone().unwrap_or_else(|| info.clone());
        let face_segments: Vec<FaceSegment> = segments.into_iter().map(FaceSegment::new).collect();
        let segment_media_states = vec![String::new(); face_segments.len()];
        let mut b = Self {
            face_width: media.as_ref().map_or(info.width, |m| m.width),
            face_height: media.as_ref().map_or(info.height, |m| m.height),
            toggle_behavior: info.effective_bool(0x0B, None) == Some(true),
            rollover_enabled: info.effective_bool(0x13, None) == Some(true),
            hot_click_enabled: info.effective_bool(0x0F, None) == Some(true),
            hot_click_initial_delay: info.effective_float(0x10, None).unwrap_or(0.0),
            hot_click_repeat_interval: info.effective_float(0x11, None).unwrap_or(0.0),
            selected: info.effective_bool(0x0E, None) == Some(true),
            info,
            media_info,
            face_segments,
            segment_media_states,
            face_media_state: String::new(),
            last_media_commit_state: None,
            has_custom_selection_pair: false,
            state_label_colors: None,
            state_label_outlines: None,
            pressed: false,
            pointer_over: false,
            enabled: true,
            hot_clicking: false,
            suppress_next_click: false,
            pointer: (0, 0),
            next_hot_click_time: None,
            active_state: String::new(),
            active_state_started_at: 0.0,
            item_drag_acceptance: DragAcceptance::None,
            block_label_cache: None,
            listeners: ButtonListeners::default(),
            on_item_drag_over: None,
            item_drag_accept_sprite: 0,
            item_drag_reject_sprite: 0,
            label: None,
            label_font: None,
            label_color: Color::WHITE,
            tooltip_text: None,
            outline: false,
            outline_color: DEFAULT_OUTLINE_COLOR,
            face_left: 0.0,
            face_top: 0.0,
            face_file_override: None,
            color_key_face: None,
            tint: Color::WHITE,
            label_offset_x: 3.0,
            label_align: LabelAlign::Center,
            label_at_bottom: false,
            label_box: None,
            value_label: None,
            value_font: None,
            value_color: Color::WHITE,
            value_box: None,
            value_align: LabelAlign::Center,
            suppress_self_toggle: false,
            controller_owns_visual_state: false,
            represented_object: 0,
        };
        b.has_custom_selection_pair = b
            .has_state_media(states::custom_state_name(states::UNSELECTED))
            && b.has_state_media(states::custom_state_name(states::SELECTED));
        let initial = if !b.info.default_state_name.is_empty() {
            b.info.default_state_name.clone()
        } else if b.has_state_media("Normal") {
            "Normal".to_owned()
        } else {
            String::new()
        };
        b.set_active_state(&initial, now);
        if let Some((name, requested)) = b.visual_state_target() {
            b.set_active_state(&name, now);
            b.apply_per_state_label_style(requested);
        }
        b
    }

    /// The element flags a button starts with: it takes the pointer.
    pub fn configure(element: &mut Element) {
        element.click_through = false;
    }

    /// The button's description.
    #[must_use]
    pub fn info(&self) -> &Arc<ElementInfo> {
        &self.info
    }

    /// The drawn state's name.
    #[must_use]
    pub fn active_state(&self) -> &str {
        &self.active_state
    }

    fn set_active_state(&mut self, name: &str, now: f64) {
        if self.active_state != name {
            self.active_state = name.to_owned();
            self.active_state_started_at = now;
        }
    }

    /// True when toggled on.
    #[must_use]
    pub fn selected(&self) -> bool {
        self.selected
    }

    /// Toggle on or off, updating the look.
    pub fn set_selected(&mut self, ui: &mut UiRoot, me: ElementId, selected: bool) {
        if self.selected == selected {
            return;
        }
        self.selected = selected;
        self.update_visual_state(ui, me);
    }

    /// The acceptance shown for the drag hovering the button.
    #[must_use]
    pub fn item_drag_acceptance(&self) -> DragAcceptance {
        self.item_drag_acceptance
    }

    /// The id of the active state.
    #[must_use]
    pub fn active_retail_state_id(&self) -> u32 {
        active_state_id(&self.media_info, &self.active_state)
    }

    /// An enum attribute of the button.
    #[must_use]
    pub fn enum_attribute(&self, id: u32) -> Option<u32> {
        match self.info.effective_property(id, None) {
            Some(ac_dat::objects::PropertyValue::Enum(v)) => Some(*v),
            _ => None,
        }
    }

    /// Per-state label colours and outlines (from the label's authored
    /// states), applied as the requested state changes.
    pub fn set_per_state_label_style(
        &mut self,
        colors: Option<Vec<(u32, Color)>>,
        outlines: Option<Vec<(u32, bool)>>,
    ) {
        self.state_label_colors = colors;
        self.state_label_outlines = outlines;
        let requested = self.compute_requested_state();
        self.apply_per_state_label_style(requested);
    }

    fn apply_per_state_label_style(&mut self, requested: u32) {
        if let Some(c) = self
            .state_label_colors
            .as_ref()
            .and_then(|m| m.iter().find(|(k, _)| *k == requested))
        {
            self.label_color = c.1;
        }
        if let Some(o) = self
            .state_label_outlines
            .as_ref()
            .and_then(|m| m.iter().find(|(k, _)| *k == requested))
        {
            self.outline = o.1;
        }
    }

    /// Number of face segments.
    #[must_use]
    pub fn face_segment_count(&self) -> usize {
        self.face_segments.len()
    }

    /// The face segment rectangles in a button of the given size.
    #[must_use]
    pub fn face_segment_rects(&self, width: f32, height: f32) -> Vec<PixelRect> {
        self.face_segments
            .iter()
            .map(|s| s.rect(width, height))
            .collect()
    }

    fn has_state_media(&self, name: &str) -> bool {
        if self.face_segments.is_empty() {
            self.media_info.state_media.contains_key(name)
        } else {
            self.face_segments
                .iter()
                .any(|s| s.info.state_media.contains_key(name))
        }
    }

    fn find_state(&self, id: u32) -> Option<&StateInfo> {
        if self.face_segments.is_empty() {
            self.media_info.state(id)
        } else {
            self.face_segments.iter().find_map(|s| s.info.state(id))
        }
    }

    fn compute_requested_state(&self) -> u32 {
        if self.has_custom_selection_pair {
            if self.selected {
                states::SELECTED
            } else {
                states::UNSELECTED
            }
        } else {
            states::requested_button_state(ButtonVisualInput {
                disabled: !self.enabled,
                selected: self.selected,
                rollover_enabled: self.rollover_enabled,
                pressed: self.pressed,
                pointer_over: self.pointer_over,
            })
        }
    }

    /// The state the inputs ask for and its drawn name; none when the
    /// controller owns the look or the element has no such state.
    fn visual_state_target(&self) -> Option<(String, u32)> {
        if self.controller_owns_visual_state {
            return None;
        }
        let requested = self.compute_requested_state();
        if self.has_custom_selection_pair {
            return Some((states::custom_state_name(requested).to_owned(), requested));
        }
        let requested_name = states::button_state_name(requested);
        let authored = self.info.state(requested).map(|s| s.name.clone());
        if authored.is_none() && !self.has_state_media(requested_name) {
            return None;
        }
        let name = match authored {
            Some(n) if !n.is_empty() => n,
            _ => requested_name.to_owned(),
        };
        Some((name, requested))
    }

    /// Enable or disable the button from inside one of its own handlers.
    /// The widget is out of the tree while it runs, so the root's
    /// enabled-changed notification cannot reach it: run it here, or a
    /// ghosted button keeps the look it had.
    fn set_own_enabled(&mut self, ui: &mut UiRoot, me: ElementId, enabled: bool) {
        if ui.element(me).is_none_or(|e| e.enabled() == enabled) {
            return;
        }
        ui.set_enabled(me, enabled);
        self.on_enabled_changed(ui, me);
    }

    fn update_visual_state(&mut self, ui: &mut UiRoot, me: ElementId) {
        self.enabled = ui.element(me).is_none_or(|e| e.enabled());
        if let Some((name, requested)) = self.visual_state_target() {
            self.set_active_state(&name, ui.media_seconds());
            self.apply_per_state_label_style(requested);
            self.cascade(ui, me, requested);
        }
    }

    fn cascade(&self, ui: &mut UiRoot, me: ElementId, state: u32) {
        if !ui.has_children(me) {
            return;
        }
        if !self.find_state(state).is_some_and(|s| s.pass_to_children) {
            return;
        }
        for child in ui.children(me) {
            ui.set_retail_state(child, state);
        }
    }

    fn sync_media_states(&mut self) {
        if self.last_media_commit_state.as_deref() == Some(self.active_state.as_str()) {
            return;
        }
        let committed = self.active_retail_state_id();
        if self.face_segments.is_empty() {
            self.face_media_state = next_media_state(
                &self.media_info,
                committed,
                &self.active_state,
                &self.face_media_state,
            );
        } else {
            for i in 0..self.face_segments.len() {
                self.segment_media_states[i] = next_media_state(
                    &self.face_segments[i].info,
                    committed,
                    &self.active_state,
                    &self.segment_media_states[i],
                );
            }
        }
        self.last_media_commit_state = Some(self.active_state.clone());
    }

    fn animated_file(&self, info: &ElementInfo, media_state: &str, now: f64) -> (u32, Option<u32>) {
        let Some((_, state)) = info.state_named(media_state) else {
            return (active_file(info, media_state), None);
        };
        if !media::is_animated(&state.media_steps) {
            return (active_file(info, media_state), None);
        }
        let (file, transition) = media::sample(
            &state.media_steps,
            (now - self.active_state_started_at) as f32,
        );
        let file = if file != 0 {
            file
        } else {
            active_file(info, media_state)
        };
        (file, transition)
    }

    fn draw_block_label(
        &mut self,
        rc: &mut RenderContext<'_>,
        text: &str,
        font: &UiFont,
        color: Color,
        bx: (f32, f32, f32, f32),
    ) {
        let key: BlockKey = (
            text.to_owned(),
            font.id,
            [
                bx.0.to_bits(),
                bx.1.to_bits(),
                bx.2.to_bits(),
                bx.3.to_bits(),
            ],
            self.label_align,
            self.label_offset_x.to_bits(),
            self.label_at_bottom,
        );
        let lines = match &self.block_label_cache {
            Some((k, lines)) if *k == key => lines.clone(),
            _ => {
                let lines = wrap_block_lines(
                    text,
                    |s| font.measure_width(s),
                    font.line_height,
                    bx,
                    self.label_align,
                    self.label_offset_x,
                    self.label_at_bottom,
                );
                self.block_label_cache = Some((key, lines.clone()));
                lines
            }
        };
        let clip = lines.len() > 1;
        if clip {
            rc.push_clip(bx.0, bx.1, bx.2, bx.3);
        }
        for (line, x, y) in &lines {
            rc.string_dat(font, line, *x, *y, color, self.outline, self.outline_color);
        }
        if clip {
            rc.pop_clip();
        }
    }

    fn draw_face(&self, rc: &mut RenderContext<'_>, file: u32, rect: PixelRect) {
        if file == 0 || rect.width() <= 0 || rect.height() <= 0 {
            return;
        }
        rc.surface(
            file,
            rect.x0 as f32,
            rect.y0 as f32,
            rect.width() as f32,
            rect.height() as f32,
            self.tint,
        );
    }
}

fn active_file(info: &ElementInfo, media_state: &str) -> u32 {
    info.state_media.get(media_state).map_or(0, |m| m.file)
}

fn has_image_media(info: &ElementInfo, state: &StateInfo, name: &str) -> bool {
    state.image_media_count != 0 || info.state_media.contains_key(name)
}

fn next_media_state(
    info: &ElementInfo,
    committed: u32,
    committed_name: &str,
    current: &str,
) -> String {
    if let Some(state) = info.state(committed) {
        return if has_image_media(info, state, committed_name) {
            committed_name.to_owned()
        } else {
            current.to_owned()
        };
    }
    if info.state_media.contains_key(committed_name) {
        return committed_name.to_owned();
    }
    if let Some(base) = info.state(DIRECT_STATE_ID) {
        return if has_image_media(info, base, "") {
            String::new()
        } else {
            current.to_owned()
        };
    }
    if info.state_media.contains_key("") {
        String::new()
    } else {
        current.to_owned()
    }
}

/// Lines of a multi-line label (split on `\n`) placed in a box: centred
/// vertically (or on the bottom edge), each left-aligned after the offset
/// or centred.
#[must_use]
pub fn wrap_block_lines(
    text: &str,
    measure: impl Fn(&str) -> f32,
    line_height: f32,
    (bx, by, bw, bh): (f32, f32, f32, f32),
    align: LabelAlign,
    left_offset: f32,
    at_bottom: bool,
) -> Vec<(String, f32, f32)> {
    let lines: Vec<&str> = text.split('\n').collect();
    let total = lines.len() as f32 * line_height;
    let start_y = if at_bottom {
        by + bh - total
    } else {
        by + (bh - total) * 0.5
    };
    lines
        .iter()
        .enumerate()
        .map(|(i, line)| {
            let x = if align == LabelAlign::Left {
                bx + left_offset
            } else {
                bx + (bw - measure(line)) * 0.5
            };
            ((*line).to_owned(), x, start_y + i as f32 * line_height)
        })
        .collect()
}

impl Widget for Button {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        if ui.dispatch_to_pointer_region(me, e) {
            return true;
        }
        let enabled = ui.element(me).is_none_or(|el| el.enabled());
        let contains = |ui: &UiRoot, x: i32, y: i32| {
            ui.element(me).is_some_and(|el| {
                x >= 0 && y >= 0 && (x as f32) < el.width && (y as f32) < el.height
            })
        };
        match e.kind {
            kind::HOVER_ENTER => {
                self.pointer_over = true;
                self.update_visual_state(ui, me);
                true
            }
            kind::HOVER_LEAVE => {
                self.pointer_over = false;
                self.update_visual_state(ui, me);
                true
            }
            kind::MOUSE_DOWN => {
                self.pointer = (e.data1, e.data2);
                self.pointer_over = contains(ui, e.data1, e.data2);
                self.pressed = true;
                self.update_visual_state(ui, me);
                if enabled {
                    ui.send_message(me, ElementMessage::Pressed);
                }
                if self.hot_click_enabled && enabled {
                    ui.send_message(
                        me,
                        ElementMessage::Clicked {
                            x: self.pointer.0,
                            y: self.pointer.1,
                        },
                    );
                    self.hot_clicking = true;
                    self.next_hot_click_time = None;
                }
                true
            }
            kind::MOUSE_MOVE if self.pressed => {
                self.pointer = (e.data1, e.data2);
                self.pointer_over = contains(ui, e.data1, e.data2);
                self.update_visual_state(ui, me);
                true
            }
            kind::MOUSE_UP => {
                self.pointer = (e.data1, e.data2);
                self.pointer_over = contains(ui, e.data1, e.data2);
                self.suppress_next_click = self.hot_clicking && self.pointer_over;
                self.hot_clicking = false;
                self.next_hot_click_time = None;
                if self.pressed
                    && self.pointer_over
                    && enabled
                    && self.toggle_behavior
                    && !self.suppress_self_toggle
                {
                    self.selected = !self.selected;
                }
                if self.pressed && enabled {
                    ui.send_message(me, ElementMessage::Released);
                }
                self.pressed = false;
                self.update_visual_state(ui, me);
                true
            }
            kind::CLICK => {
                if !enabled {
                    return true;
                }
                if self.suppress_next_click {
                    self.suppress_next_click = false;
                    return true;
                }
                if self.listeners.click {
                    ui.send_message(
                        me,
                        ElementMessage::Clicked {
                            x: e.data1,
                            y: e.data2,
                        },
                    );
                }
                self.listeners.click
            }
            kind::DOUBLE_CLICK => {
                if !self.listeners.double_click {
                    return false;
                }
                if enabled {
                    ui.send_message(me, ElementMessage::DoubleClicked);
                }
                true
            }
            kind::RIGHT_CLICK => {
                if !self.listeners.right_click {
                    return false;
                }
                if enabled {
                    ui.send_message(me, ElementMessage::RightClicked);
                }
                true
            }
            kind::DRAG_ENTER => {
                let item = e.payload.as_ref().and_then(|p| p.get::<ItemDragPayload>());
                self.item_drag_acceptance = match (item, self.on_item_drag_over.as_mut()) {
                    (Some(item), Some(policy)) => policy(item),
                    _ => DragAcceptance::None,
                };
                self.on_item_drag_over.is_some()
            }
            kind::DRAG_OVER => {
                self.item_drag_acceptance = DragAcceptance::None;
                self.on_item_drag_over.is_some()
            }
            kind::DROP_RELEASED => {
                self.item_drag_acceptance = DragAcceptance::None;
                if self.listeners.item_drop
                    && let Some(item) = e.payload.as_ref().and_then(|p| p.get::<ItemDragPayload>())
                {
                    ui.send_message(me, ElementMessage::ItemDropped(item.clone()));
                }
                self.listeners.item_drop
            }
            _ => false,
        }
    }

    fn handles_click(&self, _element: &Element) -> bool {
        true
    }

    fn consumes_dat_children(&self) -> bool {
        true
    }

    fn child_import(&self) -> crate::widgets::ChildImport {
        crate::widgets::ChildImport::Buttons
    }

    fn tooltip_text(&self, _element: &Element) -> Option<String> {
        self.tooltip_text
            .as_ref()
            .filter(|t| !t.trim().is_empty())
            .cloned()
    }

    fn on_enabled_changed(&mut self, ui: &mut UiRoot, me: ElementId) {
        if !ui.element(me).is_none_or(|e| e.enabled()) {
            self.pressed = false;
            self.hot_clicking = false;
            self.next_hot_click_time = None;
        }
        self.update_visual_state(ui, me);
    }

    fn global_time(&mut self, ui: &mut UiRoot, me: ElementId, now: f64) {
        if !self.hot_clicking || !self.hot_click_enabled {
            return;
        }
        let next = *self
            .next_hot_click_time
            .get_or_insert(now + f64::from(self.hot_click_initial_delay));
        if !self.pointer_over && now >= next {
            self.next_hot_click_time = Some(now);
            return;
        }
        if self.pointer_over && now >= next {
            ui.send_message(
                me,
                ElementMessage::Clicked {
                    x: self.pointer.0,
                    y: self.pointer.1,
                },
            );
            self.next_hot_click_time = Some(next + f64::from(self.hot_click_repeat_interval));
        }
    }

    fn active_cursor_state_name(&self) -> &str {
        &self.active_state
    }

    fn represented_object(&self) -> u32 {
        self.represented_object
    }

    fn retail_state(&self) -> Option<u32> {
        Some(self.active_retail_state_id())
    }

    fn set_retail_state(&mut self, ui: &mut UiRoot, me: ElementId, state: u32) -> bool {
        let info = self.info.clone();
        let now = ui.media_seconds();
        if self.toggle_behavior && (state == states::NORMAL || state == states::HIGHLIGHT) {
            self.set_selected(ui, me, state == states::HIGHLIGHT);
            apply_state_visibility(ui, me, &info, state);
            return true;
        }
        if state == states::GHOSTED {
            self.set_own_enabled(ui, me, false);
            apply_state_visibility(ui, me, &info, state);
            return true;
        }
        self.set_own_enabled(ui, me, true);
        if state == DIRECT_STATE_ID {
            if !self.has_state_media("") {
                return false;
            }
            self.set_active_state("", now);
            apply_state_visibility(ui, me, &info, state);
            self.cascade(ui, me, state);
            return true;
        }
        if let Some(name) = self.find_state(state).map(|s| s.name.clone()) {
            self.set_active_state(&name, now);
            apply_state_visibility(ui, me, &info, state);
            self.cascade(ui, me, state);
            return true;
        }
        let name = states::standard_state_name(state);
        if !name.is_empty() && self.has_state_media(name) {
            self.set_active_state(name, now);
            apply_state_visibility(ui, me, &info, state);
            self.cascade(ui, me, state);
            return true;
        }
        false
    }

    fn draw(&mut self, ui: &mut UiRoot, me: ElementId, rc: &mut RenderContext<'_>) {
        let Some((w, h)) = ui.element(me).map(|e| (e.width, e.height)) else {
            return;
        };
        self.sync_media_states();
        let now = rc.media_seconds();
        let mut hand_off = None;
        if !self.face_segments.is_empty() {
            for i in 0..self.face_segments.len() {
                let info = self.face_segments[i].info.clone();
                let (frame, transition) =
                    self.animated_file(&info, &self.segment_media_states[i], now);
                let rect = self.face_segments[i].rect(w, h);
                self.draw_face(rc, frame, rect);
                hand_off = hand_off.or(transition);
            }
        } else if let Some(texture) = self.color_key_face {
            let fw = if self.face_width > 0.0 {
                self.face_width
            } else {
                w
            };
            let fh = if self.face_height > 0.0 {
                self.face_height
            } else {
                h
            };
            rc.sprite(
                texture,
                self.face_left,
                self.face_top,
                fw,
                fh,
                (0.0, 0.0, 1.0, 1.0),
                Color::WHITE,
            );
        } else {
            let file = match self.face_file_override {
                Some(f) => f,
                None => {
                    let info = self.media_info.clone();
                    let (file, transition) = self.animated_file(&info, &self.face_media_state, now);
                    hand_off = transition;
                    file
                }
            };
            if file != 0 {
                let fw = if self.face_width > 0.0 {
                    self.face_width
                } else {
                    w
                };
                let fh = if self.face_height > 0.0 {
                    self.face_height
                } else {
                    h
                };
                rc.surface(file, self.face_left, self.face_top, fw, fh, self.tint);
            }
        }
        if let Some(state) = hand_off {
            self.set_retail_state(ui, me, state);
        }

        if let (Some(label), Some(font)) = (self.label.clone(), self.label_font.clone())
            && !label.is_empty()
        {
            let (bx, by, mut bw, bh) = self.label_box.unwrap_or((0.0, 0.0, w, h));
            if let Some((vx, _, _, _)) = self.value_box
                && vx > bx
            {
                bw = bw.min(vx - bx);
            }
            let color = self.label_color;
            self.draw_block_label(rc, &label, &font, color, (bx, by, bw, bh));
        }

        if let (Some(value), Some(font)) = (self.value_label.as_deref(), self.value_font.as_deref())
            && !value.is_empty()
        {
            let (bx, by, bw, bh) = self.value_box.unwrap_or((0.0, 0.0, w, h));
            let vw = font.measure_width(value);
            let vx = match self.value_align {
                LabelAlign::Left => bx + self.label_offset_x,
                LabelAlign::Right => bx + bw - vw,
                LabelAlign::Center => bx + (bw - vw) * 0.5,
            };
            let vy = by + (bh - font.line_height) * 0.5;
            rc.string_dat(
                font,
                value,
                vx,
                vy,
                self.value_color,
                self.outline,
                self.outline_color,
            );
        }

        let drag_sprite = match self.item_drag_acceptance {
            DragAcceptance::Accept => self.item_drag_accept_sprite,
            DragAcceptance::Reject => self.item_drag_reject_sprite,
            DragAcceptance::None => 0,
        };
        if drag_sprite != 0 && rc.sprite_size(drag_sprite).is_some() {
            rc.sprite(
                crate::draw::TextureRef::Surface(drag_sprite),
                0.0,
                0.0,
                w,
                h,
                (0.0, 0.0, 1.0, 1.0),
                self.tint,
            );
        }
    }
}

impl Button {
    /// Bring the look in line with the current inputs (after construction
    /// or a property change).
    pub fn refresh(&mut self, ui: &mut UiRoot, me: ElementId) {
        self.update_visual_state(ui, me);
    }
}
