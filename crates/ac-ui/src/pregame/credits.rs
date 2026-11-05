//! The credits screen (from character selection): the authored credit
//! text scrolls up the right half while a strip of pictures scrolls up
//! the left; any key, press or wheel, or the end of the text, shows the
//! wait for a frame and returns to character selection.

use ac_dat::objects::PropertyValue;

use super::Dialogs;
use crate::draw::{Color, SpriteSizes};
use crate::element_info::ElementInfo;
use crate::event::{UiEvent, kind};
use crate::import::ImportedLayout;
use crate::panels::dialog_factory::DialogData;
use crate::root::{ElementMessage, UiRoot};
use crate::text::{Line, wrap_words};
use crate::tree::{Element, ElementId, Widget};
use crate::widgets::{Panel, Text};

/// The screen's entry in the interface enumeration (category 5).
pub const ROOT_ENUM: u32 = 0x1000_0004;
/// The pictures' root.
pub const PICTURE_ROOT_ID: u32 = 0x1000_0413;
/// The text's root.
pub const TEXT_ROOT_ID: u32 = 0x1000_0410;
/// The scrolling text.
pub const TEXT_AREA_ID: u32 = 0x1000_0411;
/// The element id each picture carries.
pub const DYNAMIC_PICTURE_ID: u32 = 0x1000_0415;
/// The text root's property naming the text area.
pub const TEXT_AREA_PROPERTY: u32 = 0x1000_0002;
/// The text root's property naming the string table.
pub const STRING_TABLE_PROPERTY: u32 = 0x1000_0003;
/// The text root's property giving a section's seconds.
pub const SECTION_SECONDS_PROPERTY: u32 = 0x1000_0004;
/// The picture root's property listing the pictures.
pub const PICTURES_PROPERTY: u32 = 0x1000_0005;

/// The fixed-canvas owner number of this screen.
pub(crate) const CANVAS_OWNER: u64 = 4;

/// What the credits show, read from the text and picture roots.
#[derive(Debug, Clone, PartialEq)]
pub struct CreditsResources {
    /// The credit text's pieces, in order (`ID_Credits1`, `ID_Credits2`,
    /// ...).
    pub text_fragments: Vec<String>,
    /// The pictures, in order (they repeat).
    pub picture_ids: Vec<u32>,
    /// Seconds per section of text.
    pub section_seconds: f32,
    /// The wait shown while returning.
    pub please_wait: String,
}

/// The text root's text-area id, string table and section seconds, and
/// the picture root's pictures; `None` when one is missing or not of its
/// kind.
#[must_use]
pub fn authored_properties(
    text_root: &ElementInfo,
    picture_root: &ElementInfo,
) -> Option<(u32, u32, f32, Vec<u32>)> {
    let id = |info: &ElementInfo, property| match info.effective_property(property, None)? {
        PropertyValue::DataId(v) | PropertyValue::Enum(v) => Some(*v),
        _ => None,
    };
    let text_area = id(text_root, TEXT_AREA_PROPERTY)?;
    let table = id(text_root, STRING_TABLE_PROPERTY)?;
    let seconds = text_root.effective_float(SECTION_SECONDS_PROPERTY, None)?;
    let PropertyValue::Array(pictures) =
        picture_root.effective_property(PICTURES_PROPERTY, None)?
    else {
        return None;
    };
    let pictures: Vec<u32> = pictures
        .iter()
        .filter_map(|p| match p.value {
            PropertyValue::DataId(v) | PropertyValue::Enum(v) => Some(v),
            _ => None,
        })
        .filter(|&v| v != 0)
        .collect();
    Some((text_area, table, seconds, pictures))
}

/// How long the text takes to scroll through: a section's seconds scaled
/// by the distance travelled over the distance of one section (the text
/// split into the fragments plus the closing one).
#[must_use]
pub fn duration_seconds(
    section_seconds: f32,
    field_height: f32,
    text_height: f32,
    fragments: usize,
) -> f64 {
    let terminator = fragments as f64 + 1.0;
    let (field, text) = (f64::from(field_height), f64::from(text_height));
    f64::from(section_seconds) * (field + text) / (field + text / terminator)
}

/// Where the text's top is at `progress` (0..=1): it enters at the
/// bottom of the field and leaves past the top.
#[must_use]
pub fn text_top(field_height: f32, text_height: f32, progress: f32) -> i32 {
    let travel = ((field_height + text_height) * progress).round_ties_even() as i32;
    field_height.round_ties_even() as i32 - travel
}

/// The screen's input catcher: any key, press or wheel asks to return.
#[derive(Debug, Default)]
pub struct CreditsActionSurface;

impl Widget for CreditsActionSurface {
    fn on_event(&mut self, ui: &mut UiRoot, me: ElementId, e: &UiEvent) -> bool {
        let live = ui.element(me).is_some_and(|el| el.visible && el.enabled());
        if !live {
            return false;
        }
        match e.kind {
            kind::KEY_DOWN
            | kind::MOUSE_DOWN
            | kind::RIGHT_DOWN
            | kind::MIDDLE_DOWN
            | kind::SCROLL => {
                ui.send_message(me, ElementMessage::Pressed);
                true
            }
            kind::KEY_UP
            | kind::MOUSE_UP
            | kind::RIGHT_UP
            | kind::MIDDLE_UP
            | kind::CLICK
            | kind::RIGHT_CLICK => true,
            _ => false,
        }
    }

    fn handles_click(&self, _element: &Element) -> bool {
        true
    }
}

/// The credits screen. See the module documentation.
#[derive(Debug)]
pub struct CreditsScreen {
    picture_root: ElementId,
    text_root: ElementId,
    text_area: ElementId,
    surface: ElementId,
    resources: CreditsResources,
    canvas: (f32, f32),
    pictures: Vec<ElementId>,
    text_height: f32,
    start: f64,
    duration: f64,
    last_progress: f32,
    next_picture: usize,
    tick_sequence: u64,
    return_at_tick: u64,
    wait: u32,
    active: bool,
    return_pending: bool,
}

impl CreditsScreen {
    /// Bind the imported picture root (0x10000413) and text root
    /// (0x10000410) and add them, hidden. `None` (nothing mounted) when the
    /// roots or the text area are not the authored ones, or there is no
    /// text, no picture or no positive section time.
    pub fn bind(
        ui: &mut UiRoot,
        pictures: &ImportedLayout,
        text: &ImportedLayout,
        resources: CreditsResources,
    ) -> Option<Self> {
        let picture_root = pictures.root;
        let text_root = text.root;
        if ui.element(picture_root)?.dat_element_id != PICTURE_ROOT_ID
            || ui.element(text_root)?.dat_element_id != TEXT_ROOT_ID
            || resources.text_fragments.is_empty()
            || resources.picture_ids.is_empty()
            || !resources.section_seconds.is_finite()
            || resources.section_seconds <= 0.0
        {
            return None;
        }
        let text_area = text
            .find(TEXT_AREA_ID)
            .filter(|t| ui.widget::<Text>(*t).is_some())?;
        // The screen moves and sizes the text itself.
        if let Some(e) = ui.element_mut(text_area) {
            e.layout_policy = None;
            e.anchors = crate::geometry::AnchorEdges::NONE;
        }
        let edges = |ui: &UiRoot, id| {
            ui.element(id)
                .map_or((0.0, 0.0), |e| (e.left + e.width, e.top + e.height))
        };
        let (pw, ph) = edges(ui, picture_root);
        let (tw, th) = edges(ui, text_root);
        let (w, h) = (pw.max(tw), ph.max(th));
        let canvas = (
            if w > 0.0 { w } else { 800.0 },
            if h > 0.0 { h } else { 600.0 },
        );
        let screen = ui.root();
        for root in [picture_root, text_root] {
            if let Some(e) = ui.element_mut(root) {
                e.visible = false;
            }
            ui.add_child(screen, root);
        }
        let mut surface = Element::at(0.0, 0.0, canvas.0, canvas.1);
        surface.visible = false;
        surface.accepts_focus = true;
        surface.click_through = false;
        let surface = ui.add(screen, surface, CreditsActionSurface);
        Some(Self {
            picture_root,
            text_root,
            text_area,
            surface,
            resources,
            canvas,
            pictures: Vec::new(),
            text_height: 0.0,
            start: 0.0,
            duration: 0.0,
            last_progress: 0.0,
            next_picture: 0,
            tick_sequence: 0,
            return_at_tick: u64::MAX,
            wait: 0,
            active: false,
            return_pending: false,
        })
    }

    /// Whether the credits show.
    #[must_use]
    pub fn is_active(&self) -> bool {
        self.active
    }

    /// The input catcher.
    #[must_use]
    pub fn surface(&self) -> ElementId {
        self.surface
    }

    /// The scrolling text.
    #[must_use]
    pub fn text_area(&self) -> ElementId {
        self.text_area
    }

    /// The pictures on the strip, top first.
    #[must_use]
    pub fn pictures(&self) -> &[ElementId] {
        &self.pictures
    }

    /// How long the text takes, seconds.
    #[must_use]
    pub fn duration(&self) -> f64 {
        self.duration
    }

    /// Start the credits at `now` (seconds).
    pub fn activate(
        &mut self,
        ui: &mut UiRoot,
        dialogs: &mut Dialogs<'_>,
        now: f64,
        sprites: &dyn SpriteSizes,
    ) -> bool {
        if self.active {
            return false;
        }
        self.reset_run(ui, dialogs, now);
        self.active = true;
        for e in [self.picture_root, self.text_root, self.surface] {
            ui.set_visible(e, true);
        }
        let _ = ui.declare_fixed_canvas(CANVAS_OWNER, self.canvas);
        ui.bring_to_front(self.picture_root);
        ui.bring_to_front(self.text_root);
        ui.bring_to_front(self.surface);
        ui.set_keyboard_focus(Some(self.surface));
        self.tick(ui, dialogs, now, sprites)
    }

    /// One frame at `now`; true when the credits ended and character
    /// selection shows again.
    pub fn tick(
        &mut self,
        ui: &mut UiRoot,
        dialogs: &mut Dialogs<'_>,
        now: f64,
        sprites: &dyn SpriteSizes,
    ) -> bool {
        if !self.active {
            return false;
        }
        self.tick_sequence += 1;
        if self.return_pending {
            if self.tick_sequence >= self.return_at_tick {
                self.deactivate(ui, dialogs);
                return true;
            }
            return false;
        }
        let elapsed = (now - self.start).max(0.0);
        let progress = if self.duration <= 0.0 {
            1.0
        } else {
            ((elapsed / self.duration) as f32).clamp(0.0, 1.0)
        };
        let progress = progress.max(self.last_progress);
        self.last_progress = progress;
        let field = ui.element(self.text_root).map_or(0.0, |e| e.height);
        let old_top = ui
            .element(self.text_area)
            .map_or(0, |e| e.top.round_ties_even() as i32);
        let new_top = text_top(field, self.text_height, progress);
        if let Some(e) = ui.element_mut(self.text_area) {
            e.left = 0.0;
            e.top = new_top as f32;
        }
        self.scroll_pictures(ui, old_top - new_top, sprites);
        if progress >= 1.0 {
            self.begin_return(ui, dialogs);
        }
        false
    }

    /// The player pressed something on the screen: return.
    pub fn on_message(
        &mut self,
        ui: &mut UiRoot,
        dialogs: &mut Dialogs<'_>,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        if element != self.surface {
            return false;
        }
        if matches!(message, ElementMessage::Pressed) {
            self.begin_return(ui, dialogs);
        }
        true
    }

    fn reset_run(&mut self, ui: &mut UiRoot, dialogs: &mut Dialogs<'_>, now: f64) {
        dialogs.close(ui, &mut self.wait);
        self.clear_pictures(ui);
        self.return_pending = false;
        self.return_at_tick = u64::MAX;
        self.last_progress = 0.0;
        self.next_picture = 0;
        let field = ui
            .element(self.text_root)
            .map_or((0.0, 0.0), |e| (e.width, e.height));
        if let Some(e) = ui.element_mut(self.text_area) {
            e.width = field.0;
        }
        let all: String = self.resources.text_fragments.concat();
        let Some(t) = ui.widget_mut::<Text>(self.text_area) else {
            return;
        };
        let maximum =
            (field.0 - (t.padding + t.margin_left) - (t.padding + t.margin_right)).max(1.0);
        let mut wrapped = match &t.font {
            Some(f) => wrap_words(&all, |s| f.measure_width(s), maximum),
            None => wrap_words(&all, |s| s.chars().count() as f32 * 8.0, maximum),
        };
        if wrapped.is_empty() {
            wrapped.push(String::new());
        }
        let line_height = t.font.as_ref().map_or(16.0, |f| f.line_height);
        let color = t.default_color;
        let count = wrapped.len();
        t.set_lines(wrapped.into_iter().map(|l| Line::new(l, color)).collect());
        self.text_height = line_height.max(line_height * count as f32);
        if let Some(e) = ui.element_mut(self.text_area) {
            e.left = 0.0;
            e.top = field.1;
            e.height = self.text_height;
        }
        self.duration = duration_seconds(
            self.resources.section_seconds,
            field.1,
            self.text_height,
            self.resources.text_fragments.len(),
        );
        self.start = now;
    }

    fn scroll_pictures(&mut self, ui: &mut UiRoot, delta: i32, sprites: &dyn SpriteSizes) {
        if delta != 0 {
            for &p in &self.pictures {
                if let Some(e) = ui.element_mut(p) {
                    e.top -= delta as f32;
                }
            }
        }
        if let Some(&first) = self.pictures.first()
            && ui.element(first).is_some_and(|e| e.top + e.height < 0.0)
        {
            self.pictures.remove(0);
            ui.destroy(first);
        }
        if self.pictures.is_empty() {
            self.add_picture(ui, sprites);
        }
        let strip = ui.element(self.picture_root).map_or(0.0, |e| e.height);
        if let Some(&last) = self.pictures.last()
            && ui.element(last).is_some_and(|e| e.top < strip)
        {
            self.add_picture(ui, sprites);
        }
    }

    fn add_picture(&mut self, ui: &mut UiRoot, sprites: &dyn SpriteSizes) {
        let ids = &self.resources.picture_ids;
        if ids.is_empty() {
            return;
        }
        let id = ids[self.next_picture];
        self.next_picture = (self.next_picture + 1) % ids.len();
        let Some((w, h)) = sprites.sprite_size(id).filter(|(w, h)| *w > 0 && *h > 0) else {
            return;
        };
        let top = match self.pictures.last().and_then(|&p| ui.element(p)) {
            Some(last) => last.top + last.height + 1.0,
            None => ui.element(self.picture_root).map_or(0.0, |e| e.height) + 1.0,
        };
        let mut e = Element::at(0.0, top, w as f32, h as f32);
        e.dat_element_id = DYNAMIC_PICTURE_ID;
        e.click_through = true;
        // The strip moves its pictures itself.
        let mut e = e.with_anchors(crate::geometry::AnchorEdges::NONE);
        e.layout_policy = None;
        let picture = ui.add(
            self.picture_root,
            e,
            Panel {
                background_color: Color::TRANSPARENT,
                border_color: Color::TRANSPARENT,
                border_thickness: 0.0,
                background_sprite: id,
            },
        );
        self.pictures.push(picture);
    }

    fn begin_return(&mut self, ui: &mut UiRoot, dialogs: &mut Dialogs<'_>) {
        if !self.active || self.return_pending {
            return;
        }
        self.return_pending = true;
        let please_wait = self.resources.please_wait.clone();
        self.wait = dialogs.make(ui, DialogData::wait(&please_wait));
        self.return_at_tick = self.tick_sequence + 2;
    }

    fn deactivate(&mut self, ui: &mut UiRoot, dialogs: &mut Dialogs<'_>) {
        self.return_pending = false;
        self.return_at_tick = u64::MAX;
        self.active = false;
        for e in [self.picture_root, self.text_root, self.surface] {
            ui.set_visible(e, false);
        }
        if ui.keyboard_focus() == Some(self.surface) {
            ui.set_keyboard_focus(None);
        }
        ui.revoke_fixed_canvas(CANVAS_OWNER);
        dialogs.close(ui, &mut self.wait);
        self.clear_pictures(ui);
    }

    fn clear_pictures(&mut self, ui: &mut UiRoot) {
        for p in self.pictures.drain(..) {
            ui.destroy(p);
        }
    }

    /// Take the screen down (the window goes); true when it was showing
    /// (character selection shows again).
    pub fn dispose(&mut self, ui: &mut UiRoot, dialogs: &mut Dialogs<'_>) -> bool {
        let was_active = self.active;
        self.deactivate(ui, dialogs);
        for e in [self.picture_root, self.text_root, self.surface] {
            ui.destroy(e);
        }
        was_active
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_text_runs_a_section_per_fragment_and_scrolls_half_way_in_half_the_time() {
        // OpenAC `CreditsUiControllerTests.
        // Activate_UsesAuthoredCanvas_TextTiming_AndCyclicPictureStrip`:
        // two fragments, three 16-pixel lines, a 600-pixel field.
        let d = duration_seconds(20.0, 600.0, 48.0, 2);
        assert!((d - 20.0 * (600.0 + 48.0) / (600.0 + 48.0 / 3.0)).abs() < 1e-9);
        assert_eq!(text_top(600.0, 48.0, 0.0), 600);
        assert_eq!(text_top(600.0, 48.0, 0.5), 276);
        assert_eq!(text_top(600.0, 48.0, 1.0), -48);
    }
}
